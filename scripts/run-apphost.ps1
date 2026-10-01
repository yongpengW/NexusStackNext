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
    [string]$EnvFile = 'platform.dev',

    # **其余参数原样转给 `dotnet run`。**
    #
    # 原来这里直接用 `@args`——而在带 `[CmdletBinding()]` 的脚本里 `$args` **是空的**
    # （高级函数的未绑定参数会被拒绝，而不是落进 `$args`）。于是"转发参数"这句话
    # 写在那里却从不生效，而它读起来像生效了。
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Remaining
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
function Import-EnvFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        # 只补**还没有**的键：辅助文件不该覆盖主文件里的同名值。
        [switch]$OnlyIfUnset
    )

    $loaded = 0

    foreach ($line in (Get-Content $Path -Encoding UTF8)) {
        $trimmed = $line.Trim()
        if ($trimmed -eq '' -or $trimmed.StartsWith('#')) { continue }

        $separator = $trimmed.IndexOf('=')
        if ($separator -le 0) { continue }

        $key = $trimmed.Substring(0, $separator).Trim()
        $value = $trimmed.Substring($separator + 1)

        if ($OnlyIfUnset -and -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($key, 'Process'))) {
            continue
        }

        # 双下划线是配置层级：AgileConfig__AppId -> AgileConfig:AppId
        [Environment]::SetEnvironmentVariable($key, $value, 'Process')
        $loaded++
    }

    return $loaded
}

$count = Import-EnvFile -Path $envPath
Write-Host "  已注入 $count 个变量（来自 env/$EnvFile）" -ForegroundColor DarkGray

# 测试环境文件可补充已有本地中间件配置；测试数据库不能作为实际宿主的隐式默认值。
$auxPath = Join-Path $repoRoot 'env\test.dev'
if ((Test-Path $auxPath) -and ($auxPath -ne $envPath)) {
    $aux = Import-EnvFile -Path $auxPath -OnlyIfUnset
    if ($aux -gt 0) { Write-Host "  另从 env/test.dev 补了 $aux 个键（不覆盖主文件）" -ForegroundColor DarkGray }
}

# ---------- AppHost 需要的那几个，从已注入的配置里取 ----------
#
# AppHost 不直接读 AgileConfig（它是编排者，不是应用），所以这里把
# "这个环境用哪套中间件"翻译成它认识的四个变量。
if ([string]::IsNullOrWhiteSpace($env:NEXUSSTACK_DB) -and
    -not [string]::IsNullOrWhiteSpace($env:ConnectionStrings__Identity)) {
    [Environment]::SetEnvironmentVariable('NEXUSSTACK_DB', $env:ConnectionStrings__Identity, 'Process')
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

# ---------- 三处必须绕开的坑（第 17 轮逐个定位出来的）----------
#
# **它们此前只写在票据 14 的注释里**，于是下一个人跑这个脚本会再撞一遍同样四堵墙。
# 三处都**不动你的机器配置**（尤其是证书存储）：
#
# 1. Aspire 的面板默认要 HTTPS，而开发证书**未受信任**时它起不来
#    （`Unable to configure HTTPS endpoint`）。这个开关让它走明文 HTTP —— 本地面板够用，
#    而**不替你把证书装进受信任的根存储**（那是机器级的安全改动，得你点头）。
if (-not $env:ASPIRE_ALLOW_UNSECURED_TRANSPORT) {
    [Environment]::SetEnvironmentVariable('ASPIRE_ALLOW_UNSECURED_TRANSPORT', 'true', 'Process')
}

# 2. Aspire CLI 要在 %USERPROFILE%\.aspire\cli\bch 落东西；**目录不存在时它报"拒绝访问"**
#    而不是"目录不存在"——那句诊断会把人往权限的方向带。先建出来。
$aspireCliDir = Join-Path $env:USERPROFILE '.aspire\cli\bch'
if (-not (Test-Path $aspireCliDir)) {
    New-Item -ItemType Directory -Force -Path $aspireCliDir | Out-Null
    Write-Host "  建出 Aspire CLI 目录：$aspireCliDir" -ForegroundColor DarkGray
}

# 3. Aspire 默认**顺便**往 Windows 事件日志写一份，而写不进去时它抛未处理异常、
#    **把整个进程带走**（`Cannot open log for source '.NET Runtime'`）。
#    一条附带日志失败杀掉主程序——这一条把它关掉。
[Environment]::SetEnvironmentVariable('Logging__EventLog__LogLevel__Default', 'None', 'Process')

# ---------- 起 AppHost ----------
Write-Host ''
Write-Host '启动 AppHost（本地编排两个进程：平台宿主与网关）…' -ForegroundColor Cyan
Write-Host '  Aspire 面板会在同一进程的终端里给出地址。' -ForegroundColor DarkGray

# 关掉 MSBuild 的节点复用：它会留下常驻进程，而"看起来还在跑"与"真的在跑"分不开。
$env:MSBUILDDISABLENODEREUSE = '1'

& dotnet run --project (Join-Path $repoRoot 'aspire\NexusStackNext.AppHost\NexusStackNext.AppHost.csproj') @Remaining
exit $LASTEXITCODE
