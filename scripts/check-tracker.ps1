#!/usr/bin/env pwsh
#
# 跟踪器与规范的**一致性检查**。
#
# 存在的理由与票据 28 同源：一句关于"我们做了什么"的声明，如果不受检查，
# 就会在没人注意的时候变成谎话。跟踪器正是这种东西——它读起来像结论。
#
# 检查项：
#   1. 每张票据都有 Status / Type / Labels / Blocked by
#   2. Status 是五个规范角色之一
#   3. Blocked by 引用的票据真实存在
#   4. 已 resolved 的票据，不得仍被未解决的票据阻塞（自相矛盾）
#   5. map.md 的票据索引与 issues/ 下的文件一一对应
#   6. 每个上下文都有 CONTEXT.md、至少一份 docs/adr/、以及一个宿主
#   7. CONTEXT-MAP.md 提到每一个上下文
#   8. 每个 ADR 目录的编号连续，无跳号

param(
    # 要检查的 effort（`.scratch/` 下的目录名）。不给就**自动发现**。
    [string]$Effort
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path $PSScriptRoot -Parent

# ---------- 找出要检查哪个 effort ----------
#
# **这里原本硬编码了 `.scratch/nexusstack-next`。** 那对本仓库是对的，
# 但这个脚本**跟着模板一起分发出去了**，而别人的 effort 会叫别的名字——
# 于是它对新使用者永远失败，且失败信息只说"路径不存在"，
# 不告诉他"换个 effort 名字就行"。第 18 轮的模板端到端验证发现的。
$scratchRoot = Join-Path $repoRoot '.scratch'

if (-not $Effort) {
    $candidates = @()
    if (Test-Path $scratchRoot) {
        $candidates = @(Get-ChildItem $scratchRoot -Directory -ErrorAction SilentlyContinue)
    }

    if ($candidates.Count -eq 1) {
        $Effort = $candidates[0].Name
    }
    elseif ($candidates.Count -gt 1) {
        Write-Host ''
        Write-Host '`.scratch/` 下有多个 effort，说不清要检查哪一个：' -ForegroundColor Yellow
        $candidates | ForEach-Object { Write-Host "  $($_.Name)" }
        Write-Host ''
        Write-Host '指定一个：pwsh -File scripts/check-tracker.ps1 -Effort <名字>' -ForegroundColor Yellow
        Write-Host ''
        exit 1
    }
    else {
        $Effort = 'nexusstack-next'
    }
}

$scratch = Join-Path $scratchRoot $Effort
$issuesDir = Join-Path $scratch 'issues'
$mapPath = Join-Path $scratch 'map.md'
$servicesDir = Join-Path $repoRoot 'src/Services'

$problems = [System.Collections.Generic.List[string]]::new()

function Add-Problem([string]$category, [string]$detail) {
    $problems.Add("[$category] $detail")
}

# ---------- 0：目录不存在时立刻说清楚，而不是抛原生路径错误 ----------
# 抛原生错误读起来像"脚本坏了"；而实际情况是"检查没有对象可查"。
# 这两件事必须能区分开——见 review/06 记录的失败。
$requiredPaths = @(
    @{ Path = $issuesDir;   What = '票据目录' },
    @{ Path = $servicesDir; What = '上下文目录' },
    @{ Path = $mapPath;     What = '地图文件' }
)
$missing = @($requiredPaths | Where-Object { -not (Test-Path $_.Path) })

if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host '跟踪器检查无法执行——以下路径不存在：' -ForegroundColor Red
    $missing | ForEach-Object { Write-Host ("  [{0}] {1}" -f $_.What, $_.Path) -ForegroundColor Red }
    Write-Host '检查没有对象可查时不得报告通过。' -ForegroundColor Red
    Write-Host ''
    exit 1
}

# ---------- 1~4：票据规范与依赖 ----------
$canonicalStatuses = @('needs-triage', 'needs-info', 'ready-for-agent', 'ready-for-human', 'wontfix', 'claimed', 'resolved')

$tickets = @{}
Get-ChildItem -File $issuesDir -Filter '*.md' | ForEach-Object {
    $id = ($_.Name -split '-')[0]
    $text = Get-Content $_.FullName -Encoding UTF8 -Raw

    $status = if ($text -match '(?m)^Status:\s*(\S+)') { $Matches[1] } else { $null }
    $type = if ($text -match '(?m)^Type:\s*(\S+)') { $Matches[1] } else { $null }
    $labels = if ($text -match '(?m)^Labels:\s*(.+)$') { $Matches[1].Trim() } else { $null }
    $blockedBy = if ($text -match '(?m)^Blocked by:\s*(.+)$') { $Matches[1].Trim() } else { $null }

    if (-not $status) { Add-Problem '票据字段' "$($_.Name) 缺少 Status" }
    if (-not $type) { Add-Problem '票据字段' "$($_.Name) 缺少 Type" }
    if (-not $labels) { Add-Problem '票据字段' "$($_.Name) 缺少 Labels" }
    if (-not $blockedBy) { Add-Problem '票据字段' "$($_.Name) 缺少 Blocked by" }

    if ($status -and $canonicalStatuses -notcontains $status) {
        Add-Problem '票据状态' "$($_.Name) 的 Status '$status' 不是五个规范角色之一"
    }

    $tickets[$id] = [pscustomobject]@{
        File      = $_.Name
        Status    = $status
        BlockedBy = $blockedBy
    }
}

# 刻意**不**比对地图索引与票据文件的标题。
#
# 试过一次，报出 17 处"不一致"——但读下来那不是漂移，而是索引本来就用简称：
# 票据 04 的文件标题是「BuildingBlocks.Infrastructure：消息基座」，
# 而索引写「消息基座（Outbox/Inbox/拓扑/失败语义/ID 生成）」——后者对读者更有用。
#
# 索引是**注解**，不是副本。逐字比对会逼着索引退化成抄写，
# 而"假装检查了一个检查不了的东西"本身就是本项目一直在清除的那类声明。
#
# 索引真正会出的问题是结构性的：漏了一张票、或者指着一张不存在的票——
# 那两条在下面第 5 节里查。标题对不对，是人读的时候一眼能看出来的那种问题。

foreach ($id in $tickets.Keys) {
    $t = $tickets[$id]
    if (-not $t.BlockedBy -or $t.BlockedBy -eq '无' -or $t.BlockedBy -eq '-') { continue }

    foreach ($ref in ($t.BlockedBy -split '[,、\s]+' | Where-Object { $_ -match '^\d+$' })) {
        if (-not $tickets.ContainsKey($ref)) {
            Add-Problem '依赖引用' "票据 $id 依赖 $ref，但没有这张票"
            continue
        }

        if ($t.Status -eq 'resolved' -and $tickets[$ref].Status -ne 'resolved') {
            Add-Problem '依赖矛盾' "票据 $id 已 resolved，却仍被未解决的 $ref 阻塞"
        }
    }
}

# ---------- 5：map.md 索引与票据文件一一对应 ----------
if (-not (Test-Path $mapPath)) {
    Add-Problem '检查自身' "找不到 $mapPath ——索引无从核对。"
}

$mapText = if (Test-Path $mapPath) { Get-Content $mapPath -Encoding UTF8 -Raw } else { '' }
$indexed = [System.Collections.Generic.HashSet[string]]::new()
foreach ($line in ($mapText -split "`n")) {
    if ($line -match '^\|\s*(\d{2})\s*\|') { [void]$indexed.Add($Matches[1]) }
}

foreach ($id in $tickets.Keys) {
    if (-not $indexed.Contains($id)) { Add-Problem '地图索引' "票据 $id 存在，但 map.md 的票据索引里没有它" }
}
foreach ($id in $indexed) {
    if (-not $tickets.ContainsKey($id)) { Add-Problem '地图索引' "map.md 索引里有票据 $id，但没有对应的文件" }
}

# ---------- 5b：地图的状态列必须与票据的 Status 一致 ----------
#
# 地图是**给人看的一行摘要**，所以它的状态列允许写中文（"待开工"、"**needs-info**：等你设环境变量"）。
# 但"票据说做完了、地图说没做"（以及反过来）是纯粹的**漂移**，没有任何理由，
# 而它会让只读地图的人得出错误结论——本仓就出现过一次（票据 49）。
#
# 只断言 resolved 这一条**双向**一致：其余写法保持自由，
# 因为地图本来就是摘要，不是字段的副本。查得太死会逼着人把摘要写成字段。
foreach ($line in ($mapText -split "`n")) {
    $parts = @($line.Trim().Trim('|') -split '\|')
    if ($parts.Count -ne 4) { continue }

    $id = $parts[0].Trim()
    if ($id -notmatch '^\d{2}$') { continue }
    if (-not $tickets.ContainsKey($id)) { continue }

    $cell = $parts[3].Trim()
    $mapSaysResolved = $cell -match 'resolved'
    $ticketIsResolved = $tickets[$id].Status -eq 'resolved'

    if ($mapSaysResolved -and -not $ticketIsResolved) {
        Add-Problem '地图状态' "map.md 说票据 $id 已 resolved，但票据里写的是 '$($tickets[$id].Status)'"
    }
    elseif ($ticketIsResolved -and -not $mapSaysResolved) {
        Add-Problem '地图状态' "票据 $id 已是 resolved，但 map.md 的状态列写的是 '$cell'"
    }
}

# ---------- 6~7：上下文规范完整性 ----------
$contexts = Get-ChildItem -Directory $servicesDir | Select-Object -ExpandProperty Name

foreach ($context in $contexts) {
    $dir = Join-Path $servicesDir $context

    if (-not (Test-Path (Join-Path $dir 'CONTEXT.md'))) {
        Add-Problem '上下文规范' "$context 缺少 CONTEXT.md"
    }

    $adrs = @(Get-ChildItem -Recurse -File $dir -Filter '*.md' -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -match '\\adr\\' })
    if ($adrs.Count -eq 0) {
        Add-Problem '上下文规范' "$context 没有 docs/adr/ ——上下文级决定无处安放"
    }

    # 每个上下文都要有自己的 **HTTP 面**（模块）。
    #
    # 注意它**不等于**"可独立部署的服务"：当前五个上下文都是平台能力，
    # 由 `src/Hosts/NexusStackNext.PlatformHost` **一个**宿主组装（ADR-0013）。
    #
    # 这条规则原先找的是 `*.Api`，并把它称作"宿主项目、可独立部署的服务"——
    # 票据 51 把那些项目合并成类库之后，那两个说法就都不成立了，而规则一直没跟着改，
    # 于是它一直静默地查着一个早已不存在的形状。票据 52 改名 `.Endpoints` 时才暴露出来。
    $endpointsProject = @(Get-ChildItem -Recurse -File $dir -Filter '*.Endpoints.csproj' -ErrorAction SilentlyContinue)
    if ($endpointsProject.Count -eq 0) {
        Add-Problem '上下文规范' "$context 没有 HTTP 面项目（*.Endpoints）——它的端点无处安放"
    }
}

$contextMap = Get-Content (Join-Path $repoRoot 'CONTEXT-MAP.md') -Encoding UTF8 -Raw
foreach ($context in $contexts) {
    if ($contextMap -notmatch [regex]::Escape($context)) {
        Add-Problem '词表地图' "CONTEXT-MAP.md 没有提到上下文 $context"
    }
}

# ---------- 8：ADR 编号连续 ----------
$adrDirs = @(Get-ChildItem -Recurse -Directory $repoRoot -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -eq 'adr' -and $_.FullName -notmatch '\\(bin|obj)\\' })

foreach ($dir in $adrDirs) {
    $numbers = @(Get-ChildItem -File $dir.FullName -Filter '*.md' |
                 ForEach-Object { if ($_.Name -match '^(\d{4})-') { [int]$Matches[1] } } |
                 Sort-Object -Unique)

    if ($numbers.Count -eq 0) { continue }

    $expected = 1..($numbers.Count)
    if (-not (@(Compare-Object $numbers $expected).Count -eq 0)) {
        Add-Problem 'ADR 编号' "$($dir.FullName.Substring($repoRoot.Length + 1)) 编号不连续：$($numbers -join ', ')"
    }
}

# ---------- 0：先确认检查本身有对象可查 ----------
# 检查了**零个**对象与"检查通过"必须能区分开。
# 这条守卫来自一次真实的错误：一个文件枚举失败却零输出的循环，
# 被读成了"没有问题"（见 review/06）。
if ($tickets.Count -eq 0) {
    Add-Problem '检查自身' "在 $issuesDir 下一张票据都没找到——检查等于没跑，不能当作通过。"
}
if (@($contexts).Count -eq 0) {
    Add-Problem '检查自身' "在 $servicesDir 下一个上下文都没找到——检查等于没跑。"
}

# ---------- 9：解决方案文件里指向的文件必须真实存在 ----------
#
# **<File> 条目不会被 SDK 校验。** 实测：指向一个不存在的文件时，
# `dotnet sln list` 与 `dotnet build` 都一声不吭、退出码 0。
# （<Project> 条目会，在还原阶段报 MSB3202——所以漏项目是响亮的，漏文件是安静的。）
#
# 也就是说 `Solution Items` 分组烂掉了没有任何东西会响。这里补上。
$solutionPath = Join-Path $repoRoot 'NexusStackNext.slnx'
if (Test-Path $solutionPath) {
    $slnxText = Get-Content $solutionPath -Encoding UTF8 -Raw
    $fileEntries = [regex]::Matches($slnxText, '<File\s+Path="([^"]+)"\s*/>')

    if ($fileEntries.Count -eq 0) {
        Add-Problem '检查自身' "$solutionPath 里没有任何 <File> 条目——要么被删光了，要么检查写错了。"
    }

    foreach ($entry in $fileEntries) {
        $relative = $entry.Groups[1].Value
        if (-not (Test-Path (Join-Path $repoRoot $relative))) {
            Add-Problem '解决方案条目' ('NexusStackNext.slnx 里的 <File Path="' + $relative + '" /> 指向的文件不存在')
        }
    }
}

# ---------- 10：解决方案里的项目必须与磁盘上的 csproj 双向一致 ----------
#
# 这条盯的是 `.slnx` 的**安静失败**，而它有两种形态（都已实测）：
#
#   1. **`<Folder>` 里再套 `<Folder>`** —— 被套住的项目会被**静默忽略**：
#      实测 37 → 35，`dotnet sln list` 不报错，`dotnet build` 也照样成功。
#      少两个项目，没有任何东西会响。票据 01 当年遇到的正是这个。
#   2. `<Folder Name>` 少首尾斜杠 —— 整份文件加载失败（`MSB4025`，响亮，反而不可怕）。
#
# 双向比对一次把两者都盖住，而且不需要知道失败原因是什么。
$solutionForProjects = Join-Path $repoRoot 'NexusStackNext.slnx'
if (Test-Path $solutionForProjects) {
    $listed = @(
        dotnet sln $solutionForProjects list 2>&1 |
            Select-Object -Skip 2 |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ } |
            ForEach-Object { $_.Replace('\', '/') } |
            Sort-Object
    )

    $onDisk = @(
        Get-ChildItem -Recurse -File $repoRoot -Filter *.csproj |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
            ForEach-Object { $_.FullName.Substring($repoRoot.Length + 1).Replace('\', '/') } |
            Sort-Object
    )

    $inSolutionOnly = @(Compare-Object $onDisk $listed | Where-Object { $_.SideIndicator -eq '=>' } | ForEach-Object { $_.InputObject })
    $onDiskOnly = @(Compare-Object $onDisk $listed | Where-Object { $_.SideIndicator -eq '<=' } | ForEach-Object { $_.InputObject })

    foreach ($missingProject in $onDiskOnly) {
        Add-Problem '解决方案项目' "磁盘上有 $missingProject，但 NexusStackNext.slnx 里没有它（嵌套 <Folder> 会静默丢项目）"
    }

    foreach ($extraProject in $inSolutionOnly) {
        Add-Problem '解决方案项目' "NexusStackNext.slnx 里有 $extraProject，但磁盘上没有这个文件"
    }
}

# ---------- 结论 ----------
if ($problems.Count -gt 0) {
    Write-Host ''
    Write-Host "跟踪器检查发现 $($problems.Count) 个问题：" -ForegroundColor Red
    $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host ''
    exit 1
}

Write-Host ("跟踪器干净：{0} 张票据、{1} 个上下文、{2} 个 ADR 目录，全部一致。" -f `
    $tickets.Count, $contexts.Count, $adrDirs.Count) -ForegroundColor Green
exit 0
