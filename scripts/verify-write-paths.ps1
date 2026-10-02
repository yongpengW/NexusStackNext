# 写路径与调度触发的端到端验证（第 30 轮 review）。
#
# 两条路此前都只有单元级证据：
#   1. Platform 的设置**写入**（`PUT /api/platform/settings/{key}`）
#   2. Scheduling 的任务**被真的触发**（`SchedulingWorker` 每 10 秒一拍）
#
# 用到就清理：两个进程、临时日志。

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'api-response.ps1')
$repoRoot = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $env:TEMP ("nexusstack-writepaths-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$env:MSBUILDDISABLENODEREUSE = '1'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Identity__Storage__Provider = 'Memory'
$env:Platform__Storage__Provider = 'Memory'
$env:Files__Storage__Provider = 'Memory'
$env:Auditing__Storage__Provider = 'Memory'
$env:Scheduling__Storage__Provider = 'Memory'
$env:Jwt__SigningKey = 'writepaths-check-signing-key-long-enough-hs256'
$env:Jwt__Issuer = 'nexusstack'
$env:Jwt__Audience = 'nexusstack'
$env:AgileConfig__AppId = ''
$env:RabbitMQ__HostName = ''
$env:Identity__Root__UserName = 'writepaths-root'
$env:Identity__Root__Password = 'Write-Paths-123456'

foreach ($port in @(5190, 5191)) {
    $reservation = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
    try { $reservation.Start() } finally { $reservation.Stop() }
}
$platform = $null
$gateway = $null
try {
Write-Host '启动平台宿主（5191）…' -ForegroundColor Cyan
$platform = Start-Process -FilePath 'dotnet' `
    -ArgumentList @('run', '--project', (Join-Path $repoRoot 'src\Hosts\NexusStackNext.PlatformHost'), '--no-build', '--urls', 'http://127.0.0.1:5191') `
    -RedirectStandardOutput (Join-Path $outDir 'platform.log') -RedirectStandardError (Join-Path $outDir 'platform.err.log') `
    -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 7

Write-Host '启动网关（5190）…' -ForegroundColor Cyan
$gateway = Start-Process -FilePath 'dotnet' `
    -ArgumentList @('run', '--project', (Join-Path $repoRoot 'src\Gateway\NexusStackNext.Gateway'), '--no-build', '--urls', 'http://127.0.0.1:5190') `
    -RedirectStandardOutput (Join-Path $outDir 'gateway.log') -RedirectStandardError (Join-Path $outDir 'gateway.err.log') `
    -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 9

function Call([string]$method, [string]$url, [string]$body, [string]$token) {
    # 令牌与请求口令只留在当前进程，不进入 curl 的命令参数。
    $parameters = @{ Method = $method; Uri = $url; TimeoutSec = 25; SkipHttpErrorCheck = $true }
    if ($body) { $parameters.Body = $body; $parameters.ContentType = 'application/json' }
    if ($token) { $parameters.Headers = @{ Authorization = "Bearer $token" } }
    $response = Invoke-WebRequest @parameters
    return ConvertFrom-ApiResponse -Status ([string][int]$response.StatusCode) -Body $response.Content
}

    $results = [System.Collections.Generic.List[string]]::new()

    # ---------- 拿一个令牌 ----------
    $username = $env:Identity__Root__UserName
    $login = Call 'POST' 'http://127.0.0.1:5190/api/identity/login' `
        ("{""UserName"":""$username"",""Password"":""Write-Paths-123456""}") $null

    $token = $login.Data.accessToken
    $results.Add("0. 登录拿到令牌        → $($login.Status)")

    if ($token) {
        # ---------- Platform：匿名拒绝，根管理员读写 ----------
        $key = 'journey.key'
        $read = Call 'GET' "http://127.0.0.1:5190/api/platform/settings/$key" $null $null
        $results.Add("1. 匿名经网关读设置    → $($read.Status)（期望 401）")
        if ($read.Status -ne '401') { throw '匿名设置读取没有被拒绝。' }

        $write = Call 'PUT' "http://127.0.0.1:5190/api/platform/settings/$key" `
            '{"Value":"hello","Description":"journey"}' $token
        $results.Add("2. 经网关写设置        → $($write.Status)（期望 204）")
        if ($write.Status -ne '204') { throw '授权设置写入失败。' }

        $directWrite = Call 'PUT' 'http://127.0.0.1:5191/api/platform/settings/direct.key' `
            '{"Value":"direct","Description":"bypass"}' $null
        $results.Add("   直连后端写（对照）  → $($directWrite.Status)")

        $readBack = Call 'GET' "http://127.0.0.1:5190/api/platform/settings/$key" $null $token
        if ($readBack.Status -ne '200' -or $readBack.Data.value -ne 'hello') { throw '设置写后读回不一致。' }
        $results.Add("3. 写后读回            → $($readBack.Status)  $($readBack.Body.Substring(0,[Math]::Min(70,$readBack.Body.Length)))")

        # ---------- Scheduling：定义任务 → 等一个节拍 → 看它有没有被触发 ----------
        $code = 'journey-' + (Get-Random -Maximum 9999)
        $definition = @{ Code = $code; IntervalSeconds = 30; TargetKind = 'costing.recalculate'; TargetId = [Guid]::NewGuid() } | ConvertTo-Json -Compress
        $define = Call 'POST' 'http://127.0.0.1:5190/api/scheduling/tasks/' `
            $definition $token
        $results.Add("4. 定义调度任务        → $($define.Status)")
        if ($define.Status -ne '201') { throw '定义调度任务失败，不能继续报告触发验证通过。' }

        if ($define.Status -in @('200', '201')) {
            Write-Host '   等一个 10 秒的调度节拍…' -ForegroundColor DarkGray
            Start-Sleep -Seconds 14

            $list = Call 'GET' 'http://127.0.0.1:5190/api/scheduling/tasks/' $null $token
            # **判据是 `lastRunAt`，不是 `executionCount`。**
            # 第一版找的是后者——而响应的形状里根本没有那个字段，
            # 于是"没找到"被读成了"没触发"，而日志里明明写着"触发 1 个"。
            # 又是一个"错误的断言读起来像真的失败"。
            $triggered = @($list.Data | Where-Object { $_.code -eq $code -and $_.lastRunAt }).Count -eq 1
            $results.Add("5. 一个节拍后查任务    → $($list.Status)  已被触发=$triggered")
            if (-not $triggered) {
                $results.Add("     响应片段：$($list.Body.Substring(0,[Math]::Min(220,$list.Body.Length)))")
            }
        }
    }
    else {
        throw '拿不到测试管理员令牌，验证失败。'
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
        if ($proc -and -not $proc.HasExited) {
            $proc.Kill($true)
            $proc.WaitForExit()
        }
    }
    # 只终止这次启动的两个进程树，不按进程名清理其他开发会话。
    Write-Host "  日志留在 $outDir" -ForegroundColor DarkGray
}
