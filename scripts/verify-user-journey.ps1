# 端到端用户旅程（真实 HTTP，第 27 轮 review）。
#
# 注册 → 登录 → 经网关调用受保护端点 → 撤销 → 同一个访问令牌立即失效。
#
# **为什么必须走真 HTTP。** 现有测试用 WebApplicationFactory 直接打平台宿主，
# 于是**跳过了网关的路由策略**。网关的 `requireAuthentication` 是在边缘判定的，
# 而"边缘是唯一入口"是部署不变量——所以只有真的从 5190 打进去，
# 才知道这条路对用户是不是通的。
#
# 用完清理：两个进程、临时日志。

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'api-response.ps1')
$repoRoot = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $env:TEMP "nexusstack-journey"
Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$env:MSBUILDDISABLENODEREUSE = '1'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Identity__Storage__Provider = 'Memory'
$env:Platform__Storage__Provider = 'Memory'
$env:Jwt__SigningKey = 'journey-check-signing-key-long-enough-for-hs256'
$env:Jwt__Issuer = 'nexusstack'
$env:Jwt__Audience = 'nexusstack'

# **根账号只在这条旅程里注入**（票据 71 的决定：宿主启动时按配置播种）。
# 走进程环境变量而不是改 `env/platform.dev`：那三个文件里是活凭据，
# 而这条脚本只是要一个"能从零把授权链建起来"的宿主。
$env:Identity__Root__UserName = 'journey-root'
$env:Identity__Root__Password = 'journey-root-password-1234567890'

$platformLog = Join-Path $outDir 'platform.log'
$gatewayLog = Join-Path $outDir 'gateway.log'

Write-Host '启动平台宿主（5191）…' -ForegroundColor Cyan
$platform = Start-Process -FilePath 'dotnet' `
    -ArgumentList @('run', '--project', (Join-Path $repoRoot 'src\Hosts\NexusStackNext.PlatformHost'), '--no-build', '--urls', 'http://127.0.0.1:5191') `
    -RedirectStandardOutput $platformLog -RedirectStandardError (Join-Path $outDir 'platform.err.log') `
    -NoNewWindow -PassThru
Start-Sleep -Seconds 7

Write-Host '启动网关（5190）…' -ForegroundColor Cyan
$gateway = Start-Process -FilePath 'dotnet' `
    -ArgumentList @('run', '--project', (Join-Path $repoRoot 'src\Gateway\NexusStackNext.Gateway'), '--no-build', '--urls', 'http://127.0.0.1:5190') `
    -RedirectStandardOutput $gatewayLog -RedirectStandardError (Join-Path $outDir 'gateway.err.log') `
    -NoNewWindow -PassThru
Start-Sleep -Seconds 9

function Call([string]$method, [string]$url, [string]$body, [string]$token) {
    $args = @('-s', '-o', '-', '-w', "`n__STATUS__%{http_code}", '-X', $method, '--max-time', '20')
    if ($body) { $args += @('-H', 'Content-Type: application/json', '-d', $body) }
    if ($token) { $args += @('-H', "Authorization: Bearer $token") }
    $args += $url

    $raw = & curl.exe @args 2>&1 | Out-String
    $status = if ($raw -match '__STATUS__(\d+)') { $Matches[1] } else { '???' }
    $payload = ($raw -replace "`n__STATUS__\d+\s*$", '').Trim()
    return ConvertFrom-ApiResponse -Status $status -Body $payload
}

try {
    $results = [System.Collections.Generic.List[string]]::new()
    $chainStatus = '跳过（拿不到令牌或用户标识）'

    # ---------- 1. 经网关注册（产品决定：自注册是匿名端点）----------
    $username = 'journey' + (Get-Random -Maximum 99999)
    $register = Call 'POST' 'http://127.0.0.1:5190/api/identity/users' `
        ("{""UserName"":""$username"",""Password"":""Journey-Password-123""}") $null
    $results.Add("1. 经网关注册          → $($register.Status)")

    # ---------- 2. 经网关登录 ----------
    $login = Call 'POST' 'http://127.0.0.1:5190/api/identity/login' `
        ("{""UserName"":""$username"",""Password"":""Journey-Password-123""}") $null
    $results.Add("2. 经网关登录          → $($login.Status)")

    # 网关不让过时，直接从后端试一次——用来区分"网关挡的"与"后端本来就坏"
    $direct = Call 'POST' 'http://127.0.0.1:5191/api/identity/login' `
        ("{""UserName"":""$username"",""Password"":""Journey-Password-123""}") $null
    $results.Add("   直连后端登录        → $($direct.Status)（对照）")

    # ---------- 3. 带着令牌经网关访问受保护端点 ----------
    $token = $login.Data.accessToken
    if (-not $token) { $token = $direct.Data.accessToken }

    if ($token) {
        $withToken = Call 'GET' 'http://127.0.0.1:5190/api/identity/users/1/permissions' $null $token
        $results.Add("3. 带令牌经网关        → $($withToken.Status)")

        $without = Call 'GET' 'http://127.0.0.1:5190/api/identity/users/1/permissions' $null $null
        $results.Add("4. 不带令牌经网关      → $($without.Status)（期望 401）")

        # ---------- 5a-5e. Files：上传 → 元数据 → 下载 → 删除（字节往返）----------
        #
        # **比的是哈希，不是状态码。** 一个"上传成功、下载回来却是别的内容"的实现，
        # 状态码全绿而用户拿到的是错的文件——那正是本轮 review 想抓的那类失效。
        $payload = Join-Path $outDir 'upload.bin'
        $downloaded = Join-Path $outDir 'download.bin'
        $bytes = New-Object byte[] 4096
        (New-Object Random 42).NextBytes($bytes)
        [System.IO.File]::WriteAllBytes($payload, $bytes)
        $sourceHash = (Get-FileHash $payload -Algorithm SHA256).Hash

        $uploadRaw = & curl.exe -s -o - -w "`n__STATUS__%{http_code}" -X POST `
            -H "Authorization: Bearer $token" `
            -H 'Content-Type: application/octet-stream' `
            --data-binary "@$payload" `
            'http://127.0.0.1:5190/api/files?name=journey.bin' 2>&1 | Out-String
        $uploadStatus = if ($uploadRaw -match '__STATUS__(\d+)') { $Matches[1] } else { '???' }
        $results.Add("5a. 经网关上传文件      → $uploadStatus")

        $uploadedEnvelope = (($uploadRaw -replace "`n__STATUS__\d+\s*$", '').Trim()) | ConvertFrom-Json
        $fileId = if ($uploadedEnvelope.success -eq $true) { $uploadedEnvelope.data.fileId } else { $null }

        if ($fileId) {
            $meta = Call 'GET' "http://127.0.0.1:5190/api/files/$fileId/metadata" $null $token
            $results.Add("5b. 取元数据            → $($meta.Status)")

            $dlStatus = & curl.exe -s -o $downloaded -w '%{http_code}' --max-time 20 `
                -H "Authorization: Bearer $token" `
                "http://127.0.0.1:5190/api/files/$fileId" 2>&1 | Out-String
            $downloadedHash = if (Test-Path $downloaded) { (Get-FileHash $downloaded -Algorithm SHA256).Hash } else { '' }
            $sameBytes = $downloadedHash -eq $sourceHash
            $results.Add("5c. 下载文件            → $($dlStatus.Trim())  字节一致=$sameBytes")

            $del = Call 'DELETE' "http://127.0.0.1:5190/api/files/$fileId" $null $token
            $results.Add("5d. 删除文件            → $($del.Status)")

            $gone = Call 'GET' "http://127.0.0.1:5190/api/files/$fileId/metadata" $null $token
            $results.Add("5e. 删后再取元数据      → $($gone.Status)（期望 404）")
        }
        else {
            $results.Add('5a. 上传没拿到 fileId，Files 那几步跳过')
            $results.Add("     响应：$($uploadRaw.Substring(0,[Math]::Min(160,$uploadRaw.Length)))")
        }

        # ---------- 6. 授权链：从零到"普通用户调通一个受保护端点"（票据 67 的验收）----------
        #
        # 这一段与 `HostIntegration.Tests` 的旅程测试**刻意重复**，但走的是不同的路：
        # 那一条直打宿主（`WebApplicationFactory` 跳过网关的路由策略），
        # 这一条从 5190 打进来——而"`POST /api/identity/menus` 经不经得过边缘"
        # 只有这一段能回答。按 `AGENTS.md` 的两条防线：静的那条每次构建都跑，动的那条才是事实。
        #
        # 顺序刻意是"先被拒、再授权、再调通"：少了中间那次被拒，
        # "角色变了、权限缓存失效了吗"就验不出来（票据 67 的第 4 处断链）。
        $userId = $register.Data.userId

        $rootLogin = Call 'POST' 'http://127.0.0.1:5190/api/identity/login' `
            ("{""UserName"":""$($env:Identity__Root__UserName)"",""Password"":""$($env:Identity__Root__Password)""}") $null
        $results.Add("6a. 根账号经网关登录    → $($rootLogin.Status)（期望 200，靠启动播种）")

        $rootToken = $rootLogin.Data.accessToken

        $chainStatus = '跳过（拿不到根账号令牌或用户标识）'

        if ($rootToken -and $userId) {
            $menu = Call 'POST' 'http://127.0.0.1:5190/api/identity/menus' `
                '{"Title":"后台导航","SortOrder":1,"ParentMenuId":null}' $rootToken
            $menuId = $menu.Data.menuId
            $results.Add("6b. 建菜单（根账号）    → $($menu.Status)（期望 201）menuId=$menuId")

            $role = Call 'POST' 'http://127.0.0.1:5190/api/identity/roles' `
                '{"Code":"journey-back-office","Name":"旅程后台"}' $rootToken
            $roleId = $role.Data.roleId
            $results.Add("6c. 建角色（根账号）    → $($role.Status)（期望 201）roleId=$roleId")

            if ($menuId -and $roleId) {
                $grant = Call 'POST' "http://127.0.0.1:5190/api/identity/roles/$roleId/menus/$menuId" '{}' $rootToken
                $results.Add("6d. 菜单授给角色        → $($grant.Status)（期望 204）")

                $resource = Call 'POST' 'http://127.0.0.1:5190/api/identity/api-resources' `
                    ("{""Path"":""/api/identity/users/{userId}/permissions"",""Method"":""GET"",""MenuId"":$menuId}") $rootToken
                $results.Add("6e. 登记 api-resource   → $($resource.Status)（期望 201）")

                $beforeGrant = Call 'GET' "http://127.0.0.1:5190/api/identity/users/$userId/permissions" $null $token
                $results.Add("6f. 授权前调受保护端点  → $($beforeGrant.Status)（期望 403，且这一次会把空权限写进缓存）")

                $assign = Call 'POST' "http://127.0.0.1:5190/api/identity/users/$userId/roles/$roleId" '{}' $rootToken
                $results.Add("6g. 把角色给普通用户    → $($assign.Status)（期望 204）")

                $afterGrant = Call 'GET' "http://127.0.0.1:5190/api/identity/users/$userId/permissions" $null $token
                $results.Add("6h. 授权后再调同一个端点 → $($afterGrant.Status)（**期望 200**：票据 67 的验收）")

                $escalation = Call 'POST' 'http://127.0.0.1:5190/api/identity/menus' `
                    '{"Title":"我自己加的","SortOrder":9,"ParentMenuId":null}' $token
                $results.Add("6i. 普通用户想建菜单    → $($escalation.Status)（期望 403）")

                $chainStatus = if ($afterGrant.Status -eq '200' -and $escalation.Status -eq '403') { '通过' } else { "**不通过**（6h=$($afterGrant.Status) 6i=$($escalation.Status)）" }
            }
        }

        $logout = Call 'POST' 'http://127.0.0.1:5190/api/identity/logout' '{}' $token
        $results.Add("7. 经网关登出          → $($logout.Status)")

        # ---------- 8. 同一个访问令牌应当立刻失效 ----------
        $after = Call 'GET' 'http://127.0.0.1:5190/api/identity/users/1/permissions' $null $token
        $results.Add("8. 登出后同一个令牌    → $($after.Status)（期望 401）")
    }
    else {
        $results.Add('3-6. 拿不到 accessToken，后续步骤跳过')
    }

    Write-Host ''
    $results | ForEach-Object { Write-Host "  $_" }

    Write-Host ''
    if ($chainStatus -eq '通过') {
        Write-Host '授权链经网关走通了：从零 → 建菜单 → 挂 api-resource → 建角色授权 → 普通用户调通（票据 67 的验收）。' -ForegroundColor Green
    }
    else {
        Write-Host "**授权链经网关没有走通**：$chainStatus" -ForegroundColor Red
        Write-Host '这一步走不通意味着：经边缘没有任何人能拿到权限（而后端测试可能是全绿的）。' -ForegroundColor Red
    }

    Write-Host ''
    if ($login.Status -eq '200') {
        Write-Host '经网关的登录链路是通的。' -ForegroundColor Green
    }
    else {
        Write-Host "**经网关登录返回 $($login.Status)** —— 而直连后端是 $($direct.Status)。" -ForegroundColor Red
        Write-Host '边缘是唯一入口（部署不变量），所以这等于"用户登不进来"。' -ForegroundColor Red
        Write-Host ''
        Write-Host '网关侧的错误：' -ForegroundColor DarkGray
        Get-Content $gatewayLog -Tail 6 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
    }
}
finally {
    Write-Host ''
    Write-Host '清理…' -ForegroundColor DarkGray
    foreach ($proc in @($gateway, $platform)) {
        if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    }
    # **只收我们自己起的宿主。**
    #
    # 原来这里写的是 `Get-Process -Name dotnet, testhost | Stop-Process -Force`——
    # 那会杀掉机器上**所有** .NET 进程：Visual Studio、别的服务、别人正在跑的构建。
    # 一个验证脚本不该有那个权力，而且这种越界**不会有任何提示**。
    # `dotnet run` 会派生真正的宿主进程，所以按它自己的进程名收尾。
    Get-Process -Name NexusStackNext.PlatformHost, NexusStackNext.Gateway -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Host "  日志留在 $outDir" -ForegroundColor DarkGray
}
