# 写路径与调度触发的端到端验证（第 30 轮 review）。
#
# 两条路此前都只有单元级证据：
#   1. Platform 的设置**写入**（`PUT /api/platform/settings/{key}`）
#   2. Scheduling 的任务**被真的触发**（`SchedulingWorker` 每 10 秒一拍）
#
# 用到就清理：两个进程、临时日志。

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $env:TEMP "nexusstack-writepaths"
Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$env:MSBUILDDISABLENODEREUSE = '1'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Identity__Storage__Provider = 'Memory'
$env:Jwt__SigningKey = 'writepaths-check-signing-key-long-enough-hs256'
$env:Jwt__Issuer = 'nexusstack'
$env:Jwt__Audience = 'nexusstack'

Write-Host '启动平台宿主（5191）…' -ForegroundColor Cyan
$platform = Start-Process -FilePath 'dotnet' `
    -ArgumentList @('run', '--project', (Join-Path $repoRoot 'src\Hosts\NexusStackNext.PlatformHost'), '--no-build', '--urls', 'http://127.0.0.1:5191') `
    -RedirectStandardOutput (Join-Path $outDir 'platform.log') -RedirectStandardError (Join-Path $outDir 'platform.err.log') `
    -NoNewWindow -PassThru
Start-Sleep -Seconds 7

Write-Host '启动网关（5190）…' -ForegroundColor Cyan
$gateway = Start-Process -FilePath 'dotnet' `
    -ArgumentList @('run', '--project', (Join-Path $repoRoot 'src\Gateway\NexusStackNext.Gateway'), '--no-build', '--urls', 'http://127.0.0.1:5190') `
    -RedirectStandardOutput (Join-Path $outDir 'gateway.log') -RedirectStandardError (Join-Path $outDir 'gateway.err.log') `
    -NoNewWindow -PassThru
Start-Sleep -Seconds 9

function Call([string]$method, [string]$url, [string]$body, [string]$token) {
    $a = @('-s', '-o', '-', '-w', "`n__STATUS__%{http_code}", '-X', $method, '--max-time', '25')
    if ($body) { $a += @('-H', 'Content-Type: application/json', '-d', $body) }
    if ($token) { $a += @('-H', "Authorization: Bearer $token") }
    $a += $url

    $raw = & curl.exe @a 2>&1 | Out-String
    $status = if ($raw -match '__STATUS__(\d+)') { $Matches[1] } else { '???' }
    return @{ Status = $status; Body = ($raw -replace "`n__STATUS__\d+\s*$", '').Trim() }
}

try {
    $results = [System.Collections.Generic.List[string]]::new()

    # ---------- 拿一个令牌 ----------
    $username = 'wpaths' + (Get-Random -Maximum 99999)
    $null = Call 'POST' 'http://127.0.0.1:5190/api/identity/users' `
        ("{""UserName"":""$username"",""Password"":""Write-Paths-123456""}") $null
    $login = Call 'POST' 'http://127.0.0.1:5190/api/identity/login' `
        ("{""UserName"":""$username"",""Password"":""Write-Paths-123456""}") $null

    $token = if ($login.Body -match '"accessToken"\s*:\s*"([^"]+)"') { $Matches[1] } else { $null }
    $results.Add("0. 登录拿到令牌        → $($login.Status)")

    if ($token) {
        # ---------- Platform：读（公开）与写（需要令牌）----------
        $key = 'journey.key'
        $read = Call 'GET' "http://127.0.0.1:5190/api/platform/settings/$key" $null $null
        $results.Add("1. 经网关读设置        → $($read.Status)（公开）")

        $write = Call 'PUT' "http://127.0.0.1:5190/api/platform/settings/$key" `
            '{"Value":"hello","Description":"journey"}' $token
        $results.Add("2. 经网关写设置        → $($write.Status)（期望 204）")

        $directWrite = Call 'PUT' 'http://127.0.0.1:5191/api/platform/settings/direct.key' `
            '{"Value":"direct","Description":"bypass"}' $null
        $results.Add("   直连后端写（对照）  → $($directWrite.Status)")

        $readBack = Call 'GET' "http://127.0.0.1:5190/api/platform/settings/$key" $null $null
        $results.Add("3. 写后读回            → $($readBack.Status)  $($readBack.Body.Substring(0,[Math]::Min(70,$readBack.Body.Length)))")

        # ---------- Scheduling：定义任务 → 等一个节拍 → 看它有没有被触发 ----------
        $code = 'journey-' + (Get-Random -Maximum 9999)
        $define = Call 'POST' 'http://127.0.0.1:5190/api/scheduling/tasks/' `
            ("{""Code"":""$code"",""IntervalSeconds"":30}") $token
        $results.Add("4. 定义调度任务        → $($define.Status)")

        if ($define.Status -in @('200', '201')) {
            Write-Host '   等一个 10 秒的调度节拍…' -ForegroundColor DarkGray
            Start-Sleep -Seconds 14

            $list = Call 'GET' 'http://127.0.0.1:5190/api/scheduling/tasks/' $null $token
            # **判据是 `lastRunAt`，不是 `executionCount`。**
            # 第一版找的是后者——而响应的形状里根本没有那个字段，
            # 于是"没找到"被读成了"没触发"，而日志里明明写着"触发 1 个"。
            # 又是一个"错误的断言读起来像真的失败"。
            $triggered = $list.Body -match '"lastRunAt"\s*:\s*"[^"]+"'
            $results.Add("5. 一个节拍后查任务    → $($list.Status)  已被触发=$triggered")
            if (-not $triggered) {
                $results.Add("     响应片段：$($list.Body.Substring(0,[Math]::Min(220,$list.Body.Length)))")
            }
        }
    }
    else {
        $results.Add('   拿不到令牌，后续跳过')
    }

    Write-Host ''
    $results | ForEach-Object { Write-Host "  $_" }

    Write-Host ''
    Write-Host '调度器日志：' -ForegroundColor DarkGray
    Get-Content (Join-Path $outDir 'platform.log') -ErrorAction SilentlyContinue |
        Select-String -Pattern '触发|调度|Scheduling' | Select-Object -Last 5 | ForEach-Object { Write-Host "  $($_.Line.Trim())" -ForegroundColor DarkGray }
}
finally {
    Write-Host ''
    Write-Host '清理…' -ForegroundColor DarkGray
    foreach ($proc in @($gateway, $platform)) {
        if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
    }
    # 同 verify-user-journey.ps1：**只收我们自己起的宿主**，不按 `dotnet` 收
    # （那会连带杀掉机器上所有 .NET 进程，而越界不会有任何提示）。
    Get-Process -Name NexusStackNext.PlatformHost, NexusStackNext.Gateway -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Host "  日志留在 $outDir" -ForegroundColor DarkGray
}
