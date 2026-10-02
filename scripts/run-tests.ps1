<#
.SYNOPSIS
    跑全部测试，并把 env/test.dev 里的环境变量注入。

.DESCRIPTION
    集成测试需要真实 PostgreSQL（本机没有容器运行时，见 ADR-0005）。
    连接串从 env/test.dev 读——那个文件不进仓库、不进模板。

    缺 NEXUSSTACK_TEST_POSTGRES 时本脚本**直接失败**：
    集成测试在缺配置时会自己跳过（这样新克隆的仓库 `dotnet test` 不会红），
    但**项目自己的脚本上不该悄悄跳过一整层测试**——那与"一个不会失败的检查"是同一件事。

.EXAMPLE
    pwsh -File scripts/run-tests.ps1 -Init    # 首次：生成 env/test.dev
    pwsh -File scripts/run-tests.ps1          # 之后：跑全部测试
    pwsh -File scripts/run-tests.ps1 -Filter 'FullyQualifiedName~Integration'
#>
param(
    [switch] $Init,

    [string] $Filter,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent
$envFile = Join-Path $repoRoot 'env\test.dev'
$solution = Join-Path $repoRoot 'NexusStackNext.slnx'

# ---------- -Init：生成骨架 ----------
if ($Init) {
    if (Test-Path $envFile) {
        Write-Host "$envFile 已存在，不覆盖。" -ForegroundColor Yellow
        exit 0
    }

    # 骨架放在**脚本里**而不是一个 env/*.example 文件：
    # 文件形式会让"填了值的那个"和"没填值的那个"长得一模一样，
    # 而长得一样正是把密钥提交进仓库的常见成因。
    $skeleton = @'
# 测试环境变量 —— 本机开发用
#
# 这个文件**不会**被提交、也**不会**进模板（见 env/README.md）。
#
# 集成测试需要一个**真实可用的 PostgreSQL**。本机没有容器运行时
# （Docker / Podman / WSL 均未安装），所以 Testcontainers 不可用——
# 指向你能连上的那个库即可，远端也行。
#
# 测试使用独立 schema；Identity 宿主旅程会建临时数据库，用完 DROP。
# 测试账号须有建库、删库权限；断线验证只操作它自己创建的临时库。
# 但请确保这个连接串指向的是**开发/测试库**，不要指向生产。

# ---- 必填 ----
NEXUSSTACK_TEST_POSTGRES=Host=;Port=5432;Database=nexusstack_platform;Username=;Password=
'@

    Set-Content -Path $envFile -Value $skeleton -Encoding UTF8

    Write-Host "已生成 $envFile" -ForegroundColor Green
    Write-Host ''
    Write-Host '接下来：把 NEXUSSTACK_TEST_POSTGRES 填成一个可用的 PostgreSQL 连接串。'
    Write-Host '（可以直接抄配置中心里那个 PostgreSQL 的值，库名建议用开发库而不是生产库。）'
    Write-Host ''
    Write-Host '然后运行：pwsh -File scripts/run-tests.ps1'
    exit 0
}

# ---------- 注入环境变量 ----------
if (-not (Test-Path $envFile) -and [string]::IsNullOrWhiteSpace($env:NEXUSSTACK_TEST_POSTGRES)) {
    Write-Host "找不到 $envFile" -ForegroundColor Red
    Write-Host ''
    Write-Host '先生成它：pwsh -File scripts/run-tests.ps1 -Init' -ForegroundColor Yellow
    exit 1
}

foreach ($line in $(if (Test-Path $envFile) { Get-Content $envFile -Encoding UTF8 } else { @() })) {
    $trimmed = $line.Trim()
    if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }

    $parts = $trimmed -split '=', 2
    if ($parts.Count -ne 2) { continue }

    $key = $parts[0].Trim()
    [Environment]::SetEnvironmentVariable($key, $parts[1], 'Process')
    Write-Host "  已注入 $key"
}

# ---------- 缺连接串就失败 ----------
if ([string]::IsNullOrWhiteSpace($env:NEXUSSTACK_TEST_POSTGRES)) {
    Write-Host ''
    Write-Host "$envFile 里没有 NEXUSSTACK_TEST_POSTGRES —— 集成测试会被跳过。" -ForegroundColor Red
    Write-Host '这与"一个不会失败的检查"是同一件事，所以这里直接失败。' -ForegroundColor Red
    Write-Host '把 PostgreSQL 连接串填进去再跑。' -ForegroundColor Yellow
    exit 1
}

# ---------- 跑之前先看清要压哪台机器 ----------
#
# **这一条是 2026-09-30 那次事故里最缺的一环。**
# 那台 PostgreSQL **不只我们在用**——AgileConfig（配置中心）也用它。
# 我用 13 个并行工程把它压垮之后，AgileConfig 连不上库直接崩，
# 容器的重启策略把它无限拉起，崩溃循环把磁盘读吃满，最后 SSH 都卡死。
#
# 一个测试脚本不该有能力拖垮别的服务。做不到隔离，至少要做到**让人看见**。
$target = '<未知>'
if ($env:NEXUSSTACK_TEST_POSTGRES -match 'Host=([^;]+)') { $target = $Matches[1] }
Write-Host ''
Write-Host "目标数据库：$target" -ForegroundColor Yellow
Write-Host '  注意：这台库可能同时被配置中心等别的服务使用。' -ForegroundColor Yellow
Write-Host '  测试会逐个项目**串行**跑；如果你还要跑第二份，先等这一份结束。' -ForegroundColor Yellow

# ---------- 全局互斥：同时只允许一份全量在跑 ----------
$lockFile = Join-Path ([System.IO.Path]::GetTempPath()) 'nexusstack-run-tests.lock'
if (Test-Path $lockFile) {
    $age = (Get-Date) - (Get-Item $lockFile).LastWriteTime
    if ($age.TotalMinutes -lt 120) {
        Write-Host ''
        Write-Host "已经有一份全量测试在跑（锁文件 $lockFile，$([math]::Round($age.TotalMinutes,1)) 分钟前建立）。" -ForegroundColor Red
        Write-Host '同时跑两份会把那台共享数据库压得更狠——这正是上一次事故的成因。' -ForegroundColor Red
        Write-Host '确认没有在跑的话，删掉锁文件再试。' -ForegroundColor Yellow
        exit 1
    }
    Write-Host "  （发现过期的锁文件，$([math]::Round($age.TotalMinutes,0)) 分钟前的，忽略）" -ForegroundColor DarkGray
}
Set-Content -Path $lockFile -Value $PID -Encoding UTF8
try {

# ---------- 关掉 MSBuild 的节点复用 ----------
#
# **必须是环境变量，不能是命令行参数。**
# 2026-09-30 实测：`dotnet build … -nodeReuse:false` **不管用**——父进程收下了这个参数，
# 但它派生出来的工作节点命令行里仍然写着 `/nodeReuse:true`，于是构建完留下 5 个常驻进程。
# 它们不连数据库，但会在机器上白占 CPU 与内存，而且**看起来就像"测试还在跑"**。
#
# 这个变量是给**派生进程**看的，所以子节点会照着做。实测：设上它之后残留节点为 0。
$env:MSBUILDDISABLENODEREUSE = '1'

# ---------- 先构建 ----------
Write-Host ''
Write-Host '构建…' -ForegroundColor Cyan
& dotnet build $solution --configuration $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host '构建失败，测试不跑。' -ForegroundColor Red
    exit $LASTEXITCODE
}

# ---------- 逐个项目跑，**一次只跑一个** ----------
#
# **为什么是循环而不是 `dotnet test <解决方案>`。**
#
# 加了 `-m:1` 也不行——2026-09-30 实测过一次：`dotnet test <slnx> -m:1` 仍然同时起
# 十三个测试进程（十三个 `dotnet` 全在同一分钟启动）。`-m:1` 限制的是 MSBuild 的
# 构建并行度，而不是测试宿主的启动并行度。**想真正串行，只能自己循环。**
#
# 为什么必须串行：本仓十几个测试工程**都要连同一台 PostgreSQL**，
# 并行时每个都在建 schema、跑迁移、删 schema。那会打满服务端的磁盘读——
# 2026-09-30 现场：读 1800 IOPS / 107 MBps、读延迟 70 ms，全量跑一个多小时没结束，
# 而 `pg_catalog` 被 DDL 撑大，自动清理一直在读目录表。
#
# 串行**不一定更慢**：并行时每个工程都在等同一台库，总吞吐由那台库决定，
# 并行只是把争抢变成了等待。而且这里会打印每个工程的耗时——
# "哪一类慢"正是上一次排查时最缺的信息（票据 66）。
$testProjects = Get-ChildItem -Recurse -File (Join-Path $repoRoot 'tests') -Filter '*.csproj' |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    Where-Object {
        # `TestSupport` 与 `IntegrationSupport` 是**库**不是测试工程（它们显式写着
        # `<IsTestProject>false</IsTestProject>`）。`dotnet test` 对它们会立刻成功退出，
        # 因此无害——但每次都白跑两遍、还占着输出里的两行，那会让人以为自己在看测试结果。
        -not ([System.IO.File]::ReadAllText($_.FullName) -match '<IsTestProject>\s*false\s*</IsTestProject>')
    } |
    Sort-Object FullName

if ($testProjects.Count -eq 0) {
    Write-Host '一个测试工程都没找到——这不正常。' -ForegroundColor Red
    exit 1
}

$results = @()
foreach ($project in $testProjects) {
    $name = $project.BaseName
    Write-Host ''
    Write-Host "▶ $name" -ForegroundColor Cyan

    $dotnetArgs = @('test', $project.FullName, '--configuration', $Configuration, '--nologo', '--no-build', '-nodeReuse:false')
    if ($Filter) {
        $dotnetArgs += @('--filter', $Filter)
    }

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $output = & dotnet @dotnetArgs 2>&1
    $watch.Stop()

    $output | Where-Object { $_ -match '已通过!|失败!|通过!|Passed!|Failed!' } | ForEach-Object {
        Write-Host "  $_"
    }

    $results += [pscustomobject]@{
        Project  = $name
        ExitCode = $LASTEXITCODE
        Seconds  = [math]::Round($watch.Elapsed.TotalSeconds, 1)
    }
}

# ---------- 汇总 ----------
Write-Host ''
Write-Host '按工程耗时（从慢到快）：' -ForegroundColor Cyan
$results | Sort-Object Seconds -Descending | ForEach-Object {
    $colour = if ($_.ExitCode -ne 0) { 'Red' } else { 'Gray' }
    Write-Host ("  {0,-58} {1,8:N1}s" -f $_.Project, $_.Seconds) -ForegroundColor $colour
}

$failed = @($results | Where-Object { $_.ExitCode -ne 0 })
Write-Host ''
if ($failed.Count -gt 0) {
    Write-Host ("失败的工程 " + $failed.Count + " 个：" + (($failed | ForEach-Object { $_.Project }) -join '、')) -ForegroundColor Red
    exit 1
}

Write-Host ("全部 " + $results.Count + " 个工程通过，合计 " + [math]::Round(($results | Measure-Object -Property Seconds -Sum).Sum, 1) + " 秒。") -ForegroundColor Green
exit 0

}
finally {
    # **锁一定要释放**，包括构建失败、测试失败、以及被 Ctrl+C 打断这些路径。
    # `exit` 在 PowerShell 里会触发 finally，所以上面那些 exit 不会把锁留下。
    Remove-Item $lockFile -Force -ErrorAction SilentlyContinue
}
