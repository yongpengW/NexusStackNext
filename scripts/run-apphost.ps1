# 本地编排：读进连接信息，然后拉起 AppHost。
#
# **它只做一件事**：把 env/*.dev 里的连接信息变成环境变量，交给 AppHost。
# 那些文件不进仓库，所以连接信息不会出现在任何提交里。
#
# 用法：
#   ./scripts/run-apphost.ps1
#
# 只想跑单个服务的话**不需要这个脚本** —— 那是 Aspire 之外的路径，见 README 的"两个进程"一节。

[CmdletBinding()]
param(
    # 用哪个环境文件；默认 platform.dev。
    [string]$EnvFile = 'platform.dev'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$envPath = Join-Path $repoRoot "env\$EnvFile"

if (-not (Test-Path $envPath)) {
    Write-Host ''
    Write-Host "找不到 $envPath。" -ForegroundColor Red
    Write-Host '先把连接信息填进 env/ 下的文件（见 env/README.md）。' -ForegroundColor Yellow
    Write-Host '那些文件已 gitignore，也不会进模板。' -ForegroundColor Yellow
    exit 1
}

# ---------- 把 *.dev 读成环境变量 ----------
#
# 用**第一个等号**切分：值本身可能含 `=`（base64 口令、JSON 里的等号），
# 而 `-split '='` 会把它切成三段以上。
$count = 0
foreach ($line in (Get-Content $envPath -Encoding UTF8)) {
    $trimmed = $line.Trim()
    if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }

    $separator = $trimmed.IndexOf('=')
    if ($separator -le 0) { continue }

    $key = $trimmed.Substring(0, $separator).Trim()
    $value = $trimmed.Substring($separator + 1)

    # 双下划线是配置层级：AgileConfig__AppId -> AgileConfig:AppId
    [Environment]::SetEnvironmentVariable($key, $value, 'Process')
    $count++
}

Write-Host "  已注入 $count 个变量（来自 env/$EnvFile）" -ForegroundColor DarkGray

# ---------- AppHost 需要的那几个，从已注入的配置里取 ----------
#
# AppHost 不直接读 AgileConfig（它是编排者，不是应用），所以这里把
# "这个环境用哪套中间件"翻译成它认识的四个变量。
$postgres = $env:NEXUSSTACK_TEST_POSTGRES
if ([string]::IsNullOrWhiteSpace($postgres)) {
    # 没有测试库连接串时，退回 master 凭据里的那一条（如果被注入过）。
    $postgres = $env:ConnectionStrings__PostgreSQL
}

if (-not [string]::IsNullOrWhiteSpace($postgres)) {
    [Environment]::SetEnvironmentVariable('NEXUSSTACK_DB', $postgres, 'Process')
}

# Redis / RabbitMQ / Seq 的地址同样从已注入的配置里推导。
# **推导不出来就留空** —— AppHost 会点名说缺哪个，而不是拿着空串往下走。
if ($env:Redis__Configuration) {
    [Environment]::SetEnvironmentVariable('NEXUSSTACK_REDIS', $env:Redis__Configuration, 'Process')
}

if ($env:RabbitMQ__HostName) {
    [Environment]::SetEnvironmentVariable(
        'NEXUSSTACK_RABBITMQ',
        "Host=$($env:RabbitMQ__HostName);Port=$($env:RabbitMQ__Port)",
        'Process')
}

if ($env:Serilog__WriteTo__0__Args__serverUrl) {
    [Environment]::SetEnvironmentVariable('NEXUSSTACK_SEQ', $env:Serilog__WriteTo__0__Args__serverUrl, 'Process')
}

# ---------- 起 AppHost ----------
Write-Host ''
Write-Host '启动 AppHost（本地编排两个进程：平台宿主与网关）…' -ForegroundColor Cyan
Write-Host '  Aspire 面板会在同一进程的终端里给出地址。' -ForegroundColor DarkGray

# 关掉 MSBuild 的节点复用：它会留下常驻进程，而"看起来还在跑"与"真的在跑"分不开。
$env:MSBUILDDISABLENODEREUSE = '1'

& dotnet run --project (Join-Path $repoRoot 'aspire\NexusStackNext.AppHost\NexusStackNext.AppHost.csproj') @args
exit $LASTEXITCODE
