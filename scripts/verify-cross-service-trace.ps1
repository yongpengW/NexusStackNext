# 跨服务关联 ID 的实测（票据 14 验收 4）。
#
# 起两个宿主，**经网关**发一个请求，然后比对两边日志里的 TraceId。
# 同一个 TraceId 出现在两个进程里，就是"跨服务关联"这件事本身。
#
# 用完会清理干净：两个进程、日志文件、临时路由表。

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$outDir = Join-Path $env:TEMP "nexusstack-trace-check"
Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$env:MSBUILDDISABLENODEREUSE = '1'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Jwt__SigningKey = 'trace-check-signing-key-long-enough-for-hs256'
$env:Jwt__Issuer = 'nexusstack'
$env:Jwt__Audience = 'nexusstack'

# 没有 OTLP 端点：ServiceDefaults 会"只是不导出"，而不是崩。这本身也是一次验证。
[Environment]::SetEnvironmentVariable('OTEL_EXPORTER_OTLP_ENDPOINT', $null, 'Process')

$platformLog = Join-Path $outDir 'platform.log'
$gatewayLog = Join-Path $outDir 'gateway.log'

Write-Host '启动平台宿主（5191）…' -ForegroundColor Cyan
$platform = Start-Process -FilePath 'dotnet' `
    -ArgumentList @('run', '--project', (Join-Path $repoRoot 'src\Hosts\NexusStackNext.PlatformHost'), '--no-build', '--urls', 'http://127.0.0.1:5191') `
    -RedirectStandardOutput $platformLog -RedirectStandardError (Join-Path $outDir 'platform.err.log') `
    -NoNewWindow -PassThru

Start-Sleep -Seconds 6

Write-Host '启动网关（5190）…' -ForegroundColor Cyan
$gateway = Start-Process -FilePath 'dotnet' `
    -ArgumentList @('run', '--project', (Join-Path $repoRoot 'src\Gateway\NexusStackNext.Gateway'), '--no-build', '--urls', 'http://127.0.0.1:5190') `
    -RedirectStandardOutput $gatewayLog -RedirectStandardError (Join-Path $outDir 'gateway.err.log') `
    -NoNewWindow -PassThru

Start-Sleep -Seconds 8

try {
    Write-Host ''
    Write-Host '经网关请求 /api/identity …' -ForegroundColor Cyan

    $correlation = [guid]::NewGuid().ToString('N')
    $response = & curl.exe -s -D - -o NUL --max-time 20 `
        -H "X-Correlation-Id: $correlation" `
        "http://127.0.0.1:5190/api/identity" 2>&1

    $status = ($response | Select-String -Pattern '^HTTP/' | Select-Object -Last 1).Line
    Write-Host "  $($status.Trim())" -ForegroundColor Yellow

    $echoed = ($response | Select-String -Pattern 'X-Correlation-Id' | Select-Object -First 1).Line
    if ($echoed) { Write-Host "  $($echoed.Trim())" -ForegroundColor Yellow }

    Start-Sleep -Seconds 3

    # ---------- 比对两个进程里的 TraceId ----------
    Write-Host ''
    Write-Host '平台宿主日志里的 TraceId：' -ForegroundColor Cyan
    $platformTraces = @(Select-String -Path $platformLog -Pattern '\[([0-9a-f]{32})\]' -AllMatches |
        ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    $platformTraces | ForEach-Object { "  $_" }

    Write-Host '网关日志里的 TraceId：' -ForegroundColor Cyan
    $gatewayTraces = @(Select-String -Path $gatewayLog -Pattern '\[([0-9a-f]{32})\]' -AllMatches |
        ForEach-Object { $_.Matches } | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    $gatewayTraces | ForEach-Object { "  $_" }

    $shared = @($platformTraces | Where-Object { $gatewayTraces -contains $_ })

    Write-Host ''
    if ($shared.Count -gt 0) {
        Write-Host "✅ 两个进程共享 $($shared.Count) 个 TraceId —— 跨服务关联成立。" -ForegroundColor Green
        $shared | ForEach-Object { "   $_" }
    }
    else {
        Write-Host '❌ 两个进程没有共享任何 TraceId —— 跨服务关联不成立。' -ForegroundColor Red
        Write-Host '   平台宿主日志尾部：' -ForegroundColor DarkGray
        Get-Content $platformLog -Tail 8 -ErrorAction SilentlyContinue | ForEach-Object { "     $_" }
        Write-Host '   网关日志尾部：' -ForegroundColor DarkGray
        Get-Content $gatewayLog -Tail 8 -ErrorAction SilentlyContinue | ForEach-Object { "     $_" }
    }
}
finally {
    Write-Host ''
    Write-Host '清理…' -ForegroundColor DarkGray
    foreach ($proc in @($gateway, $platform)) {
        if ($proc -and -not $proc.HasExited) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }

    # 同 verify-user-journey.ps1：**只收我们自己起的宿主**，不按 `dotnet` 收
    # （那会连带杀掉机器上所有 .NET 进程，而越界不会有任何提示）。
    Get-Process -Name NexusStackNext.PlatformHost, NexusStackNext.Gateway -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Write-Host "  日志留在 $outDir" -ForegroundColor DarkGray
}
