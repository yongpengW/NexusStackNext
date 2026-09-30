# GitHub 后端的跟踪器检查（ADR-0016）。
#
# 为什么单独一个脚本：`check-tracker.ps1` 里那些"读 .scratch/ 下的票与地图"的组，
# 对象在切换后**搬到了 GitHub**。本仓的纪律是"失去对象的检查不得静默通过"，
# 所以那些组必须**改写或退役**——而不是留着当空壳。
#
# 这个脚本就是改写后的那一半：**后端换成 GitHub 之后，票据形状由这些检查守着。**
# 它与 `check-tracker.ps1` 的分工是"跟着后端声明走"：谁的后端谁检查，
# 声明与检查不匹配时，两边都会说话（见各自的输出）。
#
# 用法：
#   pwsh -File scripts/check-issues.ps1
#
# 依赖：`gh` 已登录（CI 里由 workflows 的 GH_TOKEN 提供），以及仓库可达。
# 想离线跑？**它会明确失败**，而不是假装通过——这是有意的。

[CmdletBinding()]
param(
    # 检查哪个仓库；默认从 git remote 推。CI 里可用 GITHUB_REPOSITORY 覆盖。
    [string]$Repository
)

$ErrorActionPreference = 'Stop'

function Add-Problem([string]$area, [string]$message) {
    $script:problems += [pscustomobject]@{ Area = $area; Message = $message }
}

$problems = @()

# ---------- 0：先确认检查本身有对象可查 ----------
#
# 这一组检查的对象是"GitHub 上的 issue 板"。板子空着（一个 issue 都没有）时，
# "没有违反"与"什么都没查到"长得一样——所以先把这个歧义掐掉。
$gh = (Get-Command gh -ErrorAction SilentlyContinue)
if (-not $gh) {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'GitHub CLI\gh.exe')
    ) | Where-Object { $_ -and (Test-Path $_) }
    if ($candidates.Count -eq 0) {
        Write-Host '找不到 gh——GitHub 后端的检查没有它就没有对象可查。' -ForegroundColor Red
        exit 1
    }
    $ghPath = $candidates[0]
} else {
    $ghPath = $gh.Source
}

if (-not $Repository) {
    if ($env:GITHUB_REPOSITORY) {
        $Repository = $env:GITHUB_REPOSITORY
    } else {
        $remote = (git remote get-url origin 2>$null)
        if ($remote -match 'github\.com[:/](?<owner>[^/]+)/(?<repo>[^/.]+)') {
            $Repository = "$($Matches['owner'])/$($Matches['repo'])"
        }
    }
}

if (-not $Repository) {
    Write-Host '推不出仓库名（git remote 里没有 github.com，也没设 GITHUB_REPOSITORY）。' -ForegroundColor Red
    exit 1
}

Write-Host "GitHub 后端检查：$Repository" -ForegroundColor DarkGray

# 一次取全（open + closed），后面的检查都在这一份快照上做，避免多次请求之间互相不一致。
# **--paginate**：板子超过 30 张票时不会静默截断。
$json = & $ghPath api "repos/$Repository/issues?state=all&per_page=100" --paginate 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "查 issue 失败：$(($json -join ' ') -replace '\s+', ' ')" -ForegroundColor Red
    Write-Host '（离线、未登录、或令牌权限不足，都会走到这里——检查不通过，而不是跳过。）' -ForegroundColor Yellow
    exit 1
}

$issues = @(($json -join "`n") | ConvertFrom-Json | Where-Object { -not $_.pull_request })

if ($issues.Count -eq 0) {
    Add-Problem '检查自身' 'issue 板上一张票都没有——这一组检查没有对象，不能当作通过'
} else {
    Write-Host "  读到 $($issues.Count) 张票" -ForegroundColor DarkGray

    # ---------- 1：地图 issue（wayfinder:map）必须存在且唯一 ----------
    $maps = @($issues | Where-Object { @($_.labels | ForEach-Object { $_.name }) -contains 'wayfinder:map' })

    if ($maps.Count -eq 0) {
        Add-Problem '地图' '没有带 wayfinder:map 标签的 issue——wayfinder 的地图没有落点'
    } elseif ($maps.Count -gt 1) {
        Add-Problem '地图' "有 $($maps.Count) 个 wayfinder:map（#$(@($maps | ForEach-Object { $_.number }) -join ', #')）——地图应当是唯一的"
    } else {
        $map = $maps[0]
        Write-Host "  地图：#$($map.number) $($map.title)" -ForegroundColor DarkGray

        # ---------- 2：该 effort 的票据应当挂在地图下（原生父子关系） ----------
        #
        # 只查"带 wayfinder:task 标签的票"：地图下可能还有别的类型（research / prototype / grilling）。
        $tasks = @($issues | Where-Object { @($_.labels | ForEach-Object { $_.name }) -contains 'wayfinder:task' })
        $subIds = @()
        $subJson = & $ghPath api "repos/$Repository/issues/$($map.number)/sub_issues?per_page=100" --paginate 2>&1
        if ($LASTEXITCODE -eq 0) { $subIds = @(($subJson -join "`n") | ConvertFrom-Json | ForEach-Object { $_.id }) }

        foreach ($t in $tasks) {
            if ($subIds -notcontains $t.id) {
                Add-Problem '父子关系' "#$($t.number) $($t.title) 带 wayfinder:task，却没挂在地图 #$($map.number) 下面"
            }
        }
    }

    # ---------- 3：阻塞边必须指向真实存在的票 ----------
    #
    # 原生依赖是这次切换换来的两样东西之一（另一样是原生评论）。它坏掉的方式很安静：
    # 指向一张已删除/不存在的票时，图上那半条边会消失。
    $checked = 0
    foreach ($issue in ($issues | Where-Object { -not $_.pull_request })) {
        $depJson = & $ghPath api "repos/$Repository/issues/$($issue.number)/dependencies/blocked_by" 2>&1
        if ($LASTEXITCODE -ne 0) { continue }
        $blockers = @(($depJson -join "`n") | ConvertFrom-Json)
        foreach ($b in $blockers) {
            $checked++
            if ($issues.number -notcontains $b.number) {
                Add-Problem '阻塞图' "#$($issue.number) 被 #$($b.number) 阻塞，而那张票不在板子上"
            }
        }
    }
    Write-Host "  阻塞边：$checked 条" -ForegroundColor DarkGray

    # ---------- 4：标签必须按调色盘存在（颜色也要对） ----------
    #
    # `docs/agents/label-colors.json` 是仓库侧的契约；切换后它才对得上东西——
    # 此前 markdown 后端下 label 只是个字符串，没有颜色可谈。
    $palettePath = Join-Path (Split-Path $PSScriptRoot -Parent) 'docs/agents/label-colors.json'
    if (-not (Test-Path $palettePath)) {
        Add-Problem '检查自身' "找不到 $palettePath —— 调色盘契约不在了"
    } else {
        $palette = (Get-Content $palettePath -Raw -Encoding UTF8) | ConvertFrom-Json
        $labelJson = & $ghPath api "repos/$Repository/labels?per_page=100" --paginate 2>&1
        $live = @{}
        if ($LASTEXITCODE -eq 0) {
            foreach ($l in (($labelJson -join "`n") | ConvertFrom-Json)) { $live[$l.name] = $l.color }
        } else {
            Add-Problem '检查自身' '查标签失败——这一组没有对象可查'
        }

        $missing = 0
        $wrongColor = 0
        foreach ($p in $palette.PSObject.Properties) {
            $want = $p.Value.TrimStart('#').ToLowerInvariant()
            if (-not $live.ContainsKey($p.Name)) { $missing++; Add-Problem '标签' "调色盘里的 $($p.Name) 在仓库里不存在" ; continue }
            if ($live[$p.Name].ToLowerInvariant() -ne $want) {
                $wrongColor++
                Add-Problem '标签' "$($p.Name) 的颜色是 #$($live[$p.Name])，调色盘要求 #$want"
            }
        }
        Write-Host "  标签：$($palette.PSObject.Properties.Count) 个契约项（缺 $missing / 颜色不符 $wrongColor）" -ForegroundColor DarkGray
    }
}

# ---------- 结论 ----------
Write-Host ''

if ($problems.Count -eq 0) {
    Write-Host 'GitHub 后端的票据检查干净。' -ForegroundColor Green
    exit 0
}

Write-Host "GitHub 后端的票据检查发现 $($problems.Count) 个问题：" -ForegroundColor Red
foreach ($p in $problems) {
    Write-Host "  [$($p.Area)] $($p.Message)"
}
exit 1
