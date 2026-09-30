#!/usr/bin/env pwsh
#
# 跟踪器与规范的**一致性检查**。
#
# 存在的理由与票据 28 同源：一句关于"我们做了什么"的声明，如果不受检查，
# 就会在没人注意的时候变成谎话。跟踪器正是这种东西——它读起来像结论。
#
# 检查项：**23 组**，文件内按段注释分开——
#   1~ 8：票据字段与状态、map 索引一一对应、上下文与 ADR 的结构（本段）
#   9~10：模板与凭据（若有）
#  11~18：MattSkills 规范符合性（Type 取值、Labels 与 Status 自洽、地图五节逐字、
#          决策索引行形式、字段不得写成粗体、Blocked by 形状、后端探测锚点、调色盘 11 键、
#          CONTEXT.md 术语必须有 _Avoid_）
#     19：`spec.md` 的七节与用户故事形式（to-spec 的模板）
#     20：跟踪器的**变体必须被声明**（面板只认 `## Comments`，本仓用了两套历史标题）
#     21：`AGENTS.md` 的**指针必须指向存在的文件**（写作规范的核心规则）
#     22：ADR 的 `status` frontmatter 取值来自封闭集（`ADR-FORMAT`；不要求每份都写）
#     23：`review/` 的编号唯一且连续（这条是被我自己撞号逼出来的——见那段注释）
#     24：已 resolved 的票不得留未打勾的验收框（这条是被两张"结票时漏打勾"的票逼出来的）
#
# ---------- 后端的那个问题：这个脚本现在守什么 ----------
#
# **2026-09-30 起，票据后端是 GitHub Issues**（`docs/agents/issue-tracker.md` 的首行标题就是声明，
# 见 ADR-0016）。所以这个脚本里"读 `.scratch/` 下的票与地图"的那些组，**职责变了**：
#
#   1. 它们守的是**冻结的归档**（`.scratch/nexusstack-next/`）——那一轮 72 张票记录怎么走过来的，
#      形状自洽与否仍然是有意义的性质；
#   2. 它们同时是**markdown 后端的检查**：谁把首行标题切回 `Local Markdown`，这些组当天就该用。
#
# **在线票据的形状由另一个脚本守**：`scripts/check-issues.ps1`（地图唯一、票据挂在地图下、
# 阻塞边指向真实存在的票、标签与调色盘一致）。两个脚本在 CI 的同一个步骤里跑。
#
# 为什么不把那些组删掉：删了就等于"后端切回来时没有人检查"——而本仓的纪律是
# **失去对象的检查必须改写或退役并写明理由**，不是留着当空壳、也不是一删了事。
#
# （这张清单原来只写到第 8 条，而实际早就不是 8 条了——**清单本身也是会被读的声明**，
#   所以它跟代码一起更新。）

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
        Type      = $type
        Labels    = $labels
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

# **报违规之前，先证明自己看得见对象。**
#
# 这条守卫是被 CI 逼出来的：这一段的路径匹配原来写的是 `\\adr\\`（只认 Windows 反斜杠），
# 而 CI 跑在 ubuntu 上——那边路径是 `/`，于是**一个 adr 目录都匹配不上**，
# 于是循环里逐个上下文报"没有 docs/adr/"：**五个冤枉**，而真正的问题是匹配写错了。
#
# 它和 `AGENTS.md` 那条纪律是同一枚硬币的两面：
# 枚举为空时**不得报告通过**，同样也**不得报告违规**——因为那两种报告都不是观察到的结论。
$allContextAdrDirs = @(Get-ChildItem -Recurse -Directory $servicesDir -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq 'adr' -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })

if ($allContextAdrDirs.Count -eq 0) {
    # 先拼进变量再调用：跨行的 `(...)` 在**命令参数位置**不成立（隐式续行只在表达式语法里）。
    # 这个坑我在这个文件里踩了第三次——所以这次把原因写在旁边。
    $adrGuardMessage = 'src/Services 下一个 docs/adr 目录都没扫到——' +
        '这不是"五个上下文都缺 ADR"，而是这条检查的路径匹配没对上（分隔符？大小写？）'
    Add-Problem '检查自身' $adrGuardMessage
}

foreach ($context in $contexts) {
    $dir = Join-Path $servicesDir $context

    if (-not (Test-Path (Join-Path $dir 'CONTEXT.md'))) {
        Add-Problem '上下文规范' "$context 缺少 CONTEXT.md"
    }

    # 分隔符写成 `[\\/]` 而不是 `\\`：**同一段代码在 Windows 与 Linux 上都要能匹配**。
    $adrs = @(Get-ChildItem -Recurse -File $dir -Filter '*.md' -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -match '[\\/]adr[\\/]' })
    if ($adrs.Count -eq 0 -and $allContextAdrDirs.Count -gt 0) {
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
             Where-Object { $_.Name -eq 'adr' -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })

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
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
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

# ---------- 11~18：MattSkills 规范符合性 ----------
#
# 这一节的每一条都对应 deck 里**真的会读它**的那行代码，或技能里**明文**的规则；
# 来源写在每条上面。写清"谁在读它"，下一个人才判断得出这条该不该改。
#
# 为什么它们要进脚本：这些正是本仓反复记录的那类声明——读起来像事实，
# 而**没有任何东西会在它过期时提醒你**。panels 读不到就是静默失效。

$wayfinderTypes = @('research', 'prototype', 'grilling', 'task')
$triageRoles = @('needs-triage', 'needs-info', 'ready-for-agent', 'ready-for-human', 'wontfix')
$ticketLabelCount = 0

foreach ($id in ($tickets.Keys | Sort-Object)) {
    $t = $tickets[$id]
    $ownLabels = @($t.Labels -split '[,，]' | ForEach-Object { $_.Trim() } | Where-Object { $_ })

    # 11：Type 必须是 wayfinder 四类之一。
    # 来源：docs/agents/issue-tracker.md 的 Wayfinding operations。
    # 面板把 Type 存进 customFields 而**不校验**——非法值不报错，只是"这张票是哪一类"静默丢失。
    if ($t.Type -and ($wayfinderTypes -notcontains $t.Type)) {
        Add-Problem '票据类型' "$($t.File) 的 Type '$($t.Type)' 不是 wayfinder 四类之一（$($wayfinderTypes -join ' / ')）"
    }

    # 12：Labels 必须是规范角色，且与 Status 自洽。
    # 来源：docs/agents/triage-labels.md（五个角色）+ triage 的状态机
    # （已完成的票不该还挂在"待处理 / 等回复"里）。
    foreach ($label in $ownLabels) {
        $ticketLabelCount++
        if (-not (($triageRoles -contains $label) -or $label.StartsWith('wayfinder:', [StringComparison]::Ordinal))) {
            Add-Problem '票据标签' "$($t.File) 的 Labels '$label' 不是规范角色（五个 triage 角色或 wayfinder:*）"
        }
        if ($t.Status -eq 'resolved' -and $label -in @('needs-triage', 'needs-info')) {
            Add-Problem '标签自相矛盾' "$($t.File) 已 resolved，却仍挂着 '$label'——完成的票不该留在未完成队列里"
        }
    }
}

if ($ticketLabelCount -eq 0) {
    Add-Problem '检查自身' '一张票的 Labels 都没解析到——这部分检查等于没跑，不能当作通过。'
}

# 13：地图五个区块必须**逐字**存在。
# 来源：面板按 `## Destination` / `## Notes` / `## Decisions so far` /
# `## Not yet specified` / `## Out of scope` 五个名字取值（lib/mapBody.js、shared/parser.js）。
# 改掉一个名字不会报错——那一块在面板里直接变成空的。
$mapSections = @('Destination', 'Notes', 'Decisions so far', 'Not yet specified', 'Out of scope')
foreach ($section in $mapSections) {
    if ($mapText -notmatch "(?m)^##\s+$([regex]::Escape($section))\s*$") {
        Add-Problem '地图区块' "map.md 缺少逐字小节 '## $section'——面板读不到它，那一块会是空的"
    }
}

# 14：Decisions so far 的条目必须是 "- [标题](链接) — 要点"。
# 来源：同一个解析器**只收**以 "- [" 开头的行；别的写法放在那里等于不存在。
# （这里的正则用单引号：双引号里的 `$(...)` 会被 PowerShell 当成子表达式求值。）
$decisionBlockPattern = '(?ms)^##\s+Decisions so far\s*$\r?\n(.*?)(?=^##\s|\z)'
$decisionIndexRows = 0
if ($mapText -match $decisionBlockPattern) {
    foreach ($line in ($Matches[1] -split "`n")) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('<!--')) { continue }
        if ($trimmed -notmatch '^-\s') { continue }

        if ($trimmed -match '^-\s*\[.+?\]\(.+?\)') {
            $decisionIndexRows++
        }
        else {
            Add-Problem '地图索引' "map.md 的 Decisions so far 有一行不是 '- [标题](链接) — 要点' 形式，面板看不见它：$trimmed"
        }
    }
}
if ($decisionIndexRows -eq 0) {
    Add-Problem '检查自身' 'map.md 的 Decisions so far 一条合规索引都没有——检查等于没跑，不能当作通过。'
}

# 15：票据字段必须是**行首裸行**，不能写成粗体。
# 来源：面板的正则是 `^\s*Status\s*[:\uFF1A]`。`**Status:** ready-for-agent` 匹配不到，
# 于是状态、依赖、认领一起静默失效——而 deck 自己 bundled 的 to-tickets 本地模板
# 恰好就是粗体写法，照抄即中招。这条守的是"照模板写反而读不到"。
foreach ($file in (Get-ChildItem -File $issuesDir -Filter '*.md')) {
    $text = Get-Content $file.FullName -Encoding UTF8 -Raw
    foreach ($field in @('Status', 'Type', 'Labels', 'Blocked by')) {
        if ($text -match "(?m)^\s*\*\*\s*$([regex]::Escape($field))\s*[:\uFF1A]") {
            Add-Problem '票据字段形状' "$($file.Name) 把 '$field' 写成了粗体——面板只认行首裸字段行"
        }
    }
}

# 16：Blocked by 的形状统一（`—` 或 `NN, NN`）。
# 来源：本仓历史里 `-` 与 `—` 两种都出现过。面板只按 /#?(\d+)/ 取号，所以这不是面板的问题，
# 是**人读时**"到底阻塞了什么"的问题。
foreach ($id in ($tickets.Keys | Sort-Object)) {
    $raw = $tickets[$id].BlockedBy
    if (-not $raw) { continue }
    if ($raw -match '^\s*(?:—|–|-|无|None)\s*$') { continue }
    if ($raw -match '^\s*\d{2}(?:\s*[,、]\s*\d{2})*\s*$') { continue }
    Add-Problem '票据依赖形状' "$($tickets[$id].File) 的 Blocked by '$raw' 形状不规范（应为 '—' 或 'NN, NN'）"
}

# 17：后端探测锚点 + 调色盘契约。
# 来源：deck 靠 `docs/agents/issue-tracker.md` 的 H1 判定后端（正则 /^#\s*issue\s*tracker\s*:\s*(markdown|local)/im），
# 靠 `docs/agents/label-colors.json` 覆盖内置调色盘（缺键的标签在面板里会回灰）。
$trackerDoc = Join-Path $repoRoot 'docs/agents/issue-tracker.md'
if (-not (Test-Path $trackerDoc)) {
    Add-Problem '规范文件' 'docs/agents/issue-tracker.md 不存在——deck 判定不出这个仓库用哪种 tracker'
}
else {
    $trackerHead = Get-Content $trackerDoc -Encoding UTF8 -TotalCount 1
    if ($trackerHead -notmatch '^#\s*Issue tracker\s*:') {
        Add-Problem '规范文件' "docs/agents/issue-tracker.md 的首行是 '$trackerHead'，不是 '# Issue tracker: …'——面板会因此认不出后端"
    }
}

$palettePath = Join-Path $repoRoot 'docs/agents/label-colors.json'
if (-not (Test-Path $palettePath)) {
    Add-Problem '规范文件' 'docs/agents/label-colors.json 不存在——面板的标签配色会全部回落到灰'
}
else {
    $palette = Get-Content $palettePath -Encoding UTF8 -Raw | ConvertFrom-Json
    $paletteNames = @($palette.PSObject.Properties.Name)
    $requiredColors = @('bug', 'needs-triage', 'needs-info', 'ready-for-agent', 'ready-for-human',
        'wontfix', 'wayfinder:map', 'wayfinder:research', 'wayfinder:prototype',
        'wayfinder:grilling', 'wayfinder:task')
    foreach ($name in $requiredColors) {
        if ($paletteNames -notcontains $name) {
            Add-Problem '标签配色' "docs/agents/label-colors.json 缺 '$name'——该标签在面板里会回灰"
        }
    }
    if ($paletteNames.Count -eq 0) {
        Add-Problem '检查自身' 'docs/agents/label-colors.json 一个键都没有——检查等于没跑。'
    }
}

# 18：CONTEXT.md 的词表格式。
# 来源：CONTEXT-FORMAT.md——每个术语是一条 `**Term**:` 定义，且**必须**配 `_Avoid_:`（表里有反义词
# 才是"有主张"的词表；没有反义词的条目会让同义词悄悄回来）。
# 这里只查格式（可判的那半）；"定义里有没有实现细节"是判断项，写在 docs/agents/domain.md 的约定里。
$contextFiles = @(Get-ChildItem -File (Join-Path $repoRoot 'src/Services') -Recurse -Filter 'CONTEXT.md')
if ($contextFiles.Count -eq 0) {
    Add-Problem '检查自身' '一个上下文 CONTEXT.md 都没找到——词表格式检查等于没跑，不能当作通过。'
}
foreach ($contextFile in $contextFiles) {
    $pendingTerm = $null
    foreach ($line in (Get-Content $contextFile.FullName -Encoding UTF8)) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^\*\*.+?\*\*\s*[:\uFF1A]?\s*$' -or $trimmed -match '^\*\*.+?\*\*\s*[:\uFF1A]\s*\S') {
            if ($pendingTerm) {
                Add-Problem '词表格式' "$($contextFile.Name) 的术语 $pendingTerm 没有 _Avoid_ 行"
            }
            $pendingTerm = $trimmed
            continue
        }
        if ($trimmed.StartsWith('_Avoid_')) { $pendingTerm = $null }
    }
    if ($pendingTerm) {
        Add-Problem '词表格式' "$($contextFile.Name) 的术语 $pendingTerm 没有 _Avoid_ 行"
    }
}

# 19：spec.md 的形状（`to-spec` 的模板）。
#
# 来源：bundled-skills/to-spec/SKILL.md 的 `<spec-template>`——七个**逐字** H2，
# 以及"编号用户故事，形式为 `As an <actor>, I want a <feature>, so that <benefit>`"。
# 这份 spec 的原文逐字保留在附录里，七节是新补的；两者并存是**有意的**（附录是证据）。
$specPath = Join-Path $scratch 'spec.md'
if (-not (Test-Path $specPath)) {
    Add-Problem '规范文件' "spec.md 不存在（$scratch）——to-spec 的产物缺了，这一层没有对象可查"
}
else {
    $specText = Get-Content $specPath -Encoding UTF8 -Raw

    foreach ($heading in @('Problem Statement', 'Solution', 'User Stories', 'Implementation Decisions',
            'Testing Decisions', 'Out of Scope', 'Further Notes')) {
        if ($specText -notmatch "(?m)^##\s+$([regex]::Escape($heading))\s*$") {
            Add-Problem 'spec 形状' "spec.md 缺少逐字小节 '## $heading'（to-spec 的模板要求七节都在）"
        }
    }

    $storyPattern = '^\d+\.\s+As an? .+,\s+I want .+,\s+so that .+$'
    $storyLines = @($specText -split "`n" | ForEach-Object { $_.TrimEnd() } | Where-Object { $_ -match '^\d+\.\s+As\s' })

    if ($storyLines.Count -eq 0) {
        Add-Problem '检查自身' 'spec.md 里一条用户故事都没解析到——检查等于没跑，不能当作通过。'
    }
    else {
        foreach ($story in $storyLines) {
            if ($story -notmatch $storyPattern) {
                Add-Problem '用户故事' "spec.md 的用户故事形式不对（应为 'As an <actor>, I want a <feature>, so that <benefit>'）：$story"
            }
        }
    }
}

# 20：历史对话**住在 `## Comments` 里**——面板唯一认的锚点。
#
# 这一条的前身是"**变体必须被声明**"：36 张票的历史原先用 `## Answer` / `## 第 N 轮：…`
# 两套标题，面板读不到，所以当时的守则退一步只要求"把变体写下来"。
# 2026-09-30 把那 33 张票迁进了文末的 `## Comments`（迁移带逐行等价证明，并用 deck 自己的
# `parseMd` 跑出 **40 条评论**），**变体没有了**，于是这条检查换了对象：
#   它现在守"**别再长回来**"（旧标题一律违规）以及"那份说明还在"（声明义务仍在：
#   将来若又出现某种变体，它必须先被写下来）。
$trackerDocPath = Join-Path $repoRoot 'docs/agents/issue-tracker.md'
if (-not (Test-Path $trackerDocPath)) {
    Add-Problem '规范文件' 'docs/agents/issue-tracker.md 不存在——跟踪器的约定没有落点，变体也就无从声明'
}
else {
    $trackerDoc = Get-Content $trackerDocPath -Encoding UTF8 -Raw

    # **跟着后端声明走**（ADR-0016，2026-09-30）。
    #
    # 这份文件的第一行就是后端声明。后端是 markdown 时，下面那两个锚点（`## Comments`、
    # "面板能读到什么"）就是面板与跟踪器之间唯一的契约；后端切到 GitHub 之后它们**失去了对象**
    # ——但"面板要读得到东西"这条**义务没有消失**，只是换了载体：原生评论、原生依赖、`wayfinder:map`。
    #
    # 这条检查第一次红就是被这次切换逼出来的：契约一翻，它还去找 markdown 的锚点，
    # 于是**它自己**成了"失去对象"的那一个。改写（而不是删掉）才对：义务要留在原处。
    $declaredBackend = if ($trackerDoc -match '(?m)^#\s*issue\s*tracker\s*:\s*github') { 'github' } else { 'markdown' }

    $anchors = if ($declaredBackend -eq 'github') {
        @('Wayfinding operations', 'wayfinder:map', 'blocked_by', 'gh issue')
    } else {
        @('### 三、面板能读到什么', '## Comments', 'Wayfinding operations')
    }

    foreach ($needle in $anchors) {
        if ($trackerDoc -notmatch [regex]::Escape($needle)) {
            # 注意：`(` 跨行的续行在**命令参数位置**是不成立的（隐式续行只在表达式语法里），
            # 所以这里先把消息拼出来。写错过两次，解析器报的是"缺少右括号"。
            $message = "docs/agents/issue-tracker.md（声明为 $declaredBackend）里找不到 '$needle'：" +
                '面板与跟踪器之间的锚点是唯一的契约，必须写在文档里'
            Add-Problem '变体声明' $message
        }
    }
}

$issueDir20 = Join-Path $scratch 'issues'
$issueFiles20 = @(Get-ChildItem $issueDir20 -Filter '*.md' -ErrorAction SilentlyContinue)

if ($issueFiles20.Count -eq 0) {
    Add-Problem '检查自身' 'issues/ 下一张票都没有——第 20 条没有对象，不能当作通过'
}
else {
    $legacyTickets = @()
    $withComments = @()

    foreach ($ticket in $issueFiles20) {
        $ticketText = Get-Content $ticket.FullName -Encoding UTF8 -Raw

        # 旧标题一律违规：历史必须挂在 `## Comments` 之下（`### ` 块），不能自成 H2。
        if ($ticketText -match '(?m)^## (Answer|第 )') { $legacyTickets += $ticket.Name }
        if ($ticketText -match '(?m)^## Comments\s*$') { $withComments += $ticket.Name }
    }

    foreach ($legacy in $legacyTickets) {
        $message = "$legacy 用了 `## Answer` / `## 第 N 轮：…` 这两套旧标题——" +
            '面板只认 `## Comments`，那种写法在面板里等于没有历史'
        Add-Problem '历史位置' $message
    }

    if ($withComments.Count -eq 0) {
        Write-Host '  提示：当前没有票带 `## Comments`——若这是有意的（全新 effort），可以删掉这一段。' -ForegroundColor DarkGray
    }
}

# 21：**指针必须指向存在的东西**（`writing-for-agents` 的核心规则）。
#
# `AGENTS.md` 是给 agent 读的入口，它的价值几乎全在指针上——所以"指针指向的东西还在不在"
# 是最该被守住的一条。指着空气的指针比没有指针更坏：它让人以为那里有东西。
$agentsPath = Join-Path $repoRoot 'AGENTS.md'
if (-not (Test-Path $agentsPath)) {
    Add-Problem '规范文件' 'AGENTS.md 不存在——给 agent 的入口没有了'
}
else {
    $agentsText = Get-Content $agentsPath -Encoding UTF8 -Raw

    # 这份清单是**枚举常量**，不是被扫出来的集合——所以它没有"空枚举"的风险；
    # 风险在另一边：有人删掉一个文件却留下指针。
    $pointers = @(
        'docs/agents/issue-tracker.md',
        'docs/agents/triage-labels.md',
        'docs/agents/domain.md',
        'docs/agents/design-vocabulary.md',
        'docs/agents/coding-standards.md',
        'docs/agents/pr-and-credentials.md'
    )

    foreach ($pointer in $pointers) {
        if (-not (Test-Path (Join-Path $repoRoot $pointer))) {
            Add-Problem '指针' "AGENTS.md 指向 $pointer，而那个文件不存在——指着空气的指针比没有指针更坏"
        }
        elseif ($agentsText -notmatch [regex]::Escape($pointer)) {
            Add-Problem '指针' "AGENTS.md 没有提到 $pointer：文件存在，却没有任何入口指向它"
        }
    }

    # **上面那张表守不住"新加的指针"**——它是枚举常量，而指针随时会新增。
    # 这不是推测：2026-09-30 我加了 `docs/agents/pr-and-credentials.md` 并在 AGENTS.md 里指它，
    # 把那个路径改成不存在的名字，检查**依然全绿**（表里没有它）。
    # 所以再加两条**派生**规则；它们的对象是扫出来的，因此各自带一条空枚举守卫。

    # (1) 反向：`docs/agents/` 下每个 .md 都要有入口。
    $agentsDocsDir = Join-Path $repoRoot 'docs/agents'
    $agentDocs = @(Get-ChildItem $agentsDocsDir -File -Filter '*.md' -ErrorAction SilentlyContinue)
    if ($agentDocs.Count -eq 0) {
        Add-Problem '检查自身' 'docs/agents/ 下一个 .md 都没扫到——第 21 条的反向规则没有对象，不能当作通过'
    }

    foreach ($doc in $agentDocs) {
        $relative = "docs/agents/$($doc.Name)"
        if ($agentsText -notmatch [regex]::Escape($relative)) {
            Add-Problem '指针' "$relative 存在，而 AGENTS.md 没提到它——文件存在，却没有任何入口指向它"
        }
    }

    # (2) 正向：AGENTS.md 里写出来的仓库路径，必须真的存在。
    $mentionedPaths = @([regex]::Matches(
            $agentsText,
            '(?<![\w/.-])((?:docs|scripts|tests|src|aspire)/[A-Za-z0-9_./-]+\.(?:md|ps1|sh|json|cs|csproj|slnx))') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)

    if ($mentionedPaths.Count -eq 0) {
        Add-Problem '检查自身' 'AGENTS.md 里一个仓库文件路径都没扫到——第 21 条的正向规则没有对象，不能当作通过'
    }

    foreach ($mentioned in $mentionedPaths) {
        if (-not (Test-Path (Join-Path $repoRoot $mentioned))) {
            Add-Problem '指针' "AGENTS.md 提到 $mentioned，而那个文件不存在——指着空气的指针比没有指针更坏"
        }
    }
}

# 22：ADR 的 `status` frontmatter **取值必须来自封闭集**（`ADR-FORMAT`）。
#
# 这一条**不要求**每份 ADR 都写 `status`——`ADR-FORMAT` 说它可选
# （"Only include these when they add genuine value. Most ADRs won't need them."）。
# 守的是"**写了就必须合法**"：把 `status: 部分取代` 这种话写进去，
# 机器读不懂，而它读起来像已经被记录了——这正是本仓最反复吃的那类亏。
$adrDir = Join-Path $repoRoot 'docs/adr'
$adrFiles = @(Get-ChildItem $adrDir -Filter '*.md' -ErrorAction SilentlyContinue)

if ($adrFiles.Count -eq 0) {
    Add-Problem '检查自身' 'docs/adr/ 下一份 ADR 都没有——第 22 条没有对象，不能当作通过'
}
else {
    $allowedPrefixes = @('proposed', 'accepted', 'deprecated', 'superseded by ADR-')

    foreach ($adr in $adrFiles) {
        $adrText = Get-Content $adr.FullName -Encoding UTF8 -Raw

        if ($adrText -match '(?ms)^---\s*\r?\n(.*?)\r?\n---\s*$') {
            $frontMatter = $Matches[1]

            if ($frontMatter -match '(?m)^status:\s*(.+)$') {
                $status = $Matches[1].Trim().Trim('"').Trim("'")
                $legal = $false

                foreach ($prefix in $allowedPrefixes) {
                    if ($status.StartsWith($prefix, [System.StringComparison]::Ordinal)) { $legal = $true; break }
                }

                if (-not $legal) {
                    # （同 §20 的注释：`(` 跨行在**命令参数位置**不成立，先把消息拼出来。
                    #   这条注释是第二次写下的——第一次写完之后我又踩了一次。）
                    $message = "$($adr.Name) 的 status 不在封闭集里：'$status'" +
                        '（应为 proposed / accepted / deprecated / superseded by ADR-NNNN）'
                    Add-Problem 'ADR 状态' $message
                }
            }
        }
    }
}

# 23：`review/` 的编号**唯一且连续**（`issue-tracker.md` §四 定了 `NN-<slug>.md` 这条规则）。
#
# 这条是**被我自己的错逼出来的**：符合性矩阵我随手编了 `05`，而 `05-deep-modules.md`
# 已经在那儿了——同一个号两份文件，按编号找东西的人必然找错。
# **没有检查会说话，因为我从没写过这条规则**；而"没写过"与"不需要"看起来一样。
$reviewDir = Join-Path $scratch 'review'

# **只扫 `*.md`**：编号规则（`issue-tracker.md` §四）说的就是 `NN-<slug>.md`，
# 所以把过滤条件写成这条规则本身，而不是靠"这一层恰好没有别的东西"。
#
# 这一层里确实有别的东西：`review/acl-report/`（ACL 诊断技能留下的 `.jsonl`）。
# 它在**子目录**里，而 `-File` 不递归，所以它现在扫不到——
# 但"扫不到"是它的位置恰好如此，不是一条规则。
#
# > **这里原来写的是另一个原因，而那个原因是错的。** 我一度以为它是**隐藏文件**、
# > 于是"别的机器上会误报没有 NN- 前缀"，还把这句写进了注释。
# > 真去反向验证时才发现：它只是住在子目录里，从来不存在跨平台误报。
# > **一个听起来合理的机制，被写下来之后就成了"事实"**——而它没有经过验证。
$reviewFiles = @(Get-ChildItem $reviewDir -File -Filter '*.md' -ErrorAction SilentlyContinue)

if ($reviewFiles.Count -eq 0) {
    Add-Problem '检查自身' 'review/ 下一份评审都没有——第 23 条没有对象，不能当作通过'
}
else {
    $reviewNumbers = @()

    foreach ($review in $reviewFiles) {
        if ($review.Name -match '^(\d{2})-') { $reviewNumbers += [int]$Matches[1] }
        else { Add-Problem '评审编号' "$($review.Name) 没有 NN- 前缀（issue-tracker.md §四 规定形如 NN-<slug>.md）" }
    }

    if ($reviewNumbers.Count -gt 0) {
        $sortedReviews = @($reviewNumbers | Sort-Object)
        $dupeReviews = @($sortedReviews | Group-Object | Where-Object { $_.Count -gt 1 })

        foreach ($dupe in $dupeReviews) {
            $message = "review/ 里编号 $($dupe.Name) 有 $($dupe.Count) 份文件——同一号两份，按编号找必然找错"
            Add-Problem '评审编号' $message
        }

        $missingReviews = @(1..$sortedReviews[-1] | Where-Object { $sortedReviews -notcontains $_ })
        if ($missingReviews.Count -gt 0) {
            Add-Problem '评审编号' "review/ 里跳号：$($missingReviews -join ', ')（编号是索引，跳号让人以为文件丢了）"
        }
    }
}

# 24：**已 resolved 的票不得留未打勾的验收框。**
#
# 这条检查是被两张票逼出来的：`15-test-harness`（5 个框一个没打）与
# `08-identity-application`（差 1 个），两张都置成了 `resolved`，而验收标准一条都没勾。
# 它一直没人看见——直到有人问"还有没有待完成的票据"，按这个形状去筛才浮出来。
#
# 为什么这条规则成立：`resolved` 的意思就是"验收标准成立"。留着未打勾的框只有两种可能——
# 漏打（补上并写明证据），或者**验收标准已被取代**（那就必须写明取代者：
# 15 号票里两条与后来"绝不能并行跑共享库"的决定直接冲突）。两种都不是"不用管"。
$ticketFiles24 = @(Get-ChildItem $issueDir20 -Filter '*.md' -ErrorAction SilentlyContinue)

if ($ticketFiles24.Count -eq 0) {
    Add-Problem '检查自身' 'issues/ 下一张票都没有——第 24 条没有对象，不能当作通过'
}
else {
    foreach ($ticket in $ticketFiles24) {
        $ticketText24 = Get-Content $ticket.FullName -Raw -Encoding UTF8

        $status24 = if ($ticketText24 -match '(?m)^Status:\s*(.+)$') { $Matches[1].Trim() } else { '' }
        if ($status24 -ne 'resolved') { continue }

        $unchecked = ([regex]::Matches($ticketText24, '(?m)^\s*-\s*\[ \]')).Count

        if ($unchecked -gt 0) {
            $message = "$($ticket.Name) 已 resolved，却还留着 $unchecked 个未打勾的验收框——" +
                'resolved 的意思就是"验收成立"：要么补证据打勾，要么写明它被哪张票取代'
            Add-Problem '验收框' $message
        }
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
