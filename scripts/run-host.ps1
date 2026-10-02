#!/usr/bin/env pwsh
#
# 从 env/<名称>.dev 读环境变量，然后启动对应宿主。
#
# 为什么需要它：两个宿主的 AgileConfig **AppId 不同**，必须在**各自的进程**里设。
# 在一个终端里设一次再跑两个，第二个会读到第一个的 AppId，去拉别人的配置。
#
# 本脚本只把变量设进它自己那个进程（'Process' 作用域），不污染你的终端。
#
# env/*.dev 存的是**真实密钥**：已被 .gitignore 忽略、被模板排除，
# 且 assert-no-credentials.ps1 会断言它没进模板生成物。见 env/README.md。
#
#   pwsh -File scripts/run-host.ps1 platform -Init    # 首次：生成 env/platform.dev
#   pwsh -File scripts/run-host.ps1 platform          # 之后：启动

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('platform', 'gateway')]
    [string] $Name,

    [switch] $Init
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$envFile = Join-Path $repoRoot "env\$Name.dev"

$hosts = @{
    platform = @{
        Project     = 'src/Hosts/NexusStackNext.PlatformHost/NexusStackNext.PlatformHost.csproj'
        Urls        = 'http://127.0.0.1:5191'
        AppId       = 'nexusstack_platform'
        Description = '平台宿主（Identity / Platform / Scheduling / Auditing / Files）'
    }
    gateway  = @{
        Project     = 'src/Gateway/NexusStackNext.Gateway/NexusStackNext.Gateway.csproj'
        Urls        = 'http://127.0.0.1:5190'
        AppId       = 'nexusstack_gateway'
        Description = '网关（YARP 边缘）'
    }
}

$target = $hosts[$Name]

# ---------- -Init：生成骨架 ----------
#
# 骨架放在**脚本里**而不是一个 env/*.example 文件：
# 文件形式会让"填了值的那个"和"没填值的那个"长得一模一样，
# 而长得一样正是把密钥提交进仓库的常见成因。
if ($Init) {
    if (Test-Path $envFile) {
        Write-Host "$envFile 已存在，不覆盖。" -ForegroundColor Yellow
        exit 0
    }

    $skeleton = @"
# $($target.Description) —— 本机开发环境变量
#
# 这个文件**不会**被提交、也**不会**进模板（见 env/README.md）。
# 双下划线表示配置层级：AgileConfig__AppId -> AgileConfig:AppId

# ---- AgileConfig（必填）----
# AppId 与 Secret 在 AgileConfig 后台的应用列表里（密钥点眼睛图标）。
AgileConfig__AppId=$($target.AppId)
AgileConfig__Secret=
AgileConfig__Nodes=http://your-agileconfig:8010

"@

    $skeleton += @"

# ---- 认证签名密钥（**必填**）----
# 平台宿主没配它会**启动即失败**（OptionsValidationException），这是有意的：
# 认证能力的配置不该缺省 succeed。至少 32 字节，别提交进仓库。
# 网关那边不对称：缺它时照常启动，而所有要求认证的路由一律 401（fail-closed）。
Jwt__SigningKey=

# ---- 可选：根账号播种（引导用，不配则跳过）----
# 配了就会在启动时播种一个内置根账号（**存在同名账号则跳过、绝不重置口令**）。
# 它走 IsRoot 旁路、不做权限判定，所以上线后第一件事是轮换口令。
# 它是权限链的第一环：没有它，谁也建不出菜单、授不出权限（见 env/README.md）。
# Identity__Root__UserName=
# Identity__Root__Password=

# ---- 可选：运维可见性与多环境 ----
# 后台「客户端」页面靠这两个把连接显示成人能看懂的东西（不配就是空白）。
# AgileConfig__Name=
# AgileConfig__Tag=
#
# **多环境。** 为空表示拉"无环境"那一份；若配置中心按环境分了配置，那就少了一截，
# 而且**不会报错**。值要与后台里的环境名一致（例如 TEST）。
# AgileConfig__Env=
"@
    if ($Name -eq 'platform') {
        $skeleton += @"

# Identity 与 Platform 默认使用 PostgreSQL；可连接同一物理库，各自拥有 schema。
ConnectionStrings__Identity=
ConnectionStrings__Platform=
# 首次或升级：pwsh -File scripts/migrate-identity.ps1
# 然后执行：pwsh -File scripts/migrate-platform.ps1
# 无库演示请显式使用以下三项（会丢失重启前数据）：
# DOTNET_ENVIRONMENT=Development
# Identity__Storage__Provider=Memory
# Platform__Storage__Provider=Memory
"@
        $skeleton += "`n# 文件存储根目录；留空则用 AppContext.BaseDirectory 下的 file-storage。`n# Files__StorageRoot=`n"
    }
    else {
        $skeleton += "`n# 路由表路径；留空则用应用目录下的 routes.json。`n# Gateway__RouteTablePath=`n"
    }

    [System.IO.File]::WriteAllText($envFile, $skeleton, (New-Object System.Text.UTF8Encoding($false)))

    Write-Host "已生成 $envFile" -ForegroundColor Green
    Write-Host ''
    Write-Host '接下来：把 AgileConfig__Secret 填上（后台应用列表的眼睛图标），'
    Write-Host "并把 AgileConfig__Nodes 换成真实地址（当前是占位符）。"
    Write-Host ''
    Write-Host "然后运行：pwsh -File scripts\run-host.ps1 $Name"
    exit 0
}

# ---------- 正常启动 ----------

if (-not (Test-Path $envFile)) {
    Write-Host "找不到 $envFile" -ForegroundColor Red
    Write-Host ''
    Write-Host "先生成它：pwsh -File scripts\run-host.ps1 $Name -Init" -ForegroundColor Yellow
    exit 1
}

$empty = @()
$loaded = 0

foreach ($line in (Get-Content $envFile -Encoding UTF8)) {
    $trimmed = $line.Trim()

    # 空行与注释跳过。
    if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }

    $separator = $trimmed.IndexOf('=')
    if ($separator -lt 1) { continue }

    $key = $trimmed.Substring(0, $separator).Trim()
    $value = $trimmed.Substring($separator + 1).Trim()

    if (-not $value) { $empty += $key }

    [Environment]::SetEnvironmentVariable($key, $value, 'Process')
    $loaded++
}

Write-Host "启动：$($target.Description)" -ForegroundColor Green
Write-Host "  环境变量来源：env\$Name.dev（$loaded 项，只作用于本进程）"

if ($empty.Count -gt 0) {
    # 空值**不阻止启动**（降级路径是设计的一部分），但要说清楚——
    # 否则"忘了填"会表现成"配置中心没生效"，而那是最难查的一类问题。
    Write-Host "  以下变量是空的：$($empty -join ', ')" -ForegroundColor Yellow
    Write-Host '  可选依赖可不配置；缺少 JWT 密钥或 Identity 数据库配置会阻止平台宿主启动。' -ForegroundColor Yellow
}

Write-Host "  地址：$($target.Urls)"
Write-Host ''

Push-Location $repoRoot
try {
    dotnet run --project $target.Project --urls $target.Urls
}
finally {
    Pop-Location
}
