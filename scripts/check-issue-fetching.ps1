# Exercise the real tracker CLI with a disposable gh adapter; never contacts GitHub.
$ErrorActionPreference = 'Stop'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('nsn-issue-probes-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
$oldPath = $env:PATH
$oldMode = $env:NSN_ISSUE_PROBE_MODE
$adapter = @'
#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'
$route = $args[1]
switch -Regex ($route) {
    '/issues\?' { $items = '[{"number":1,"title":"Map","labels":[{"name":"wayfinder:map"}]},{"number":2,"title":"Task","labels":[{"name":"wayfinder:task"}]}]' }
    '/sub_issues\?' { $items = '[{"number":2}]' }
    '/dependencies/blocked_by' {
        if ($env:NSN_ISSUE_PROBE_MODE -eq 'failure' -and $route -like '*/2/*') { exit 1 }
        if ($env:NSN_ISSUE_PROBE_MODE -eq 'invalid' -and $route -like '*/2/*') { 'not-json'; exit 0 }
        if ($env:NSN_ISSUE_PROBE_MODE -eq 'empty' -and $route -like '*/2/*') { exit 0 }
        if ($env:NSN_ISSUE_PROBE_MODE -eq 'null' -and $route -like '*/2/*') { 'null'; exit 0 }
        if ($env:NSN_ISSUE_PROBE_MODE -eq 'pagination' -and $args -notcontains '--paginate') { exit 1 }
        $items = if ($route -like '*/2/*') {
            if ($env:NSN_ISSUE_PROBE_MODE -eq 'missing') { '[{"number":99}]' } else { '[{"number":1}]' }
        } else { '[]' }
    }
    '/labels\?' { $items = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'labels.json') -Raw }
    default { exit 1 }
}
# Real gh emits independent JSON pages unless --slurp wraps the pages in an array.
# All records are on page two, including the invalid blocker in the missing probe.
if ($args -contains '--slurp') { '[[],' + $items + ']' } else { "[]`n$items" }
exit 0
'@
try {
    $palette = Get-Content (Join-Path $PSScriptRoot '../docs/agents/label-colors.json') -Raw | ConvertFrom-Json
    $labels = @($palette.PSObject.Properties | ForEach-Object { @{ name = $_.Name; color = $_.Value.TrimStart('#') } })
    [IO.File]::WriteAllText((Join-Path $scratch 'labels.json'), (ConvertTo-Json -InputObject $labels))
    $scriptPath = Join-Path $scratch 'gh.ps1'
    [IO.File]::WriteAllText($scriptPath, $adapter.Replace("`r`n", "`n"))
    $adapterPath = $scriptPath
    if (-not $IsWindows) {
        # pwsh -File requires a .ps1 extension, even when launched through a shebang.
        $adapterPath = Join-Path $scratch 'gh'
        $launcher = @'
#!/bin/sh
exec pwsh -NoProfile -File "$0.ps1" "$@"
'@
        [IO.File]::WriteAllText($adapterPath, $launcher.Replace("`r`n", "`n"))
        & chmod +x $adapterPath
        if ($LASTEXITCODE -ne 0) { throw 'Cannot prepare CLI adapter.' }
    }
    $env:PATH = $scratch + [IO.Path]::PathSeparator + $oldPath
    if ((Get-Command gh).Source -cne $adapterPath) { throw 'Fixture gh was not selected; refusing network access.' }
    foreach ($mode in @('success', 'failure', 'invalid', 'empty', 'null', 'missing', 'pagination')) {
        $env:NSN_ISSUE_PROBE_MODE = $mode
        $output = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'check-issues.ps1') -Repository fixture/repo 2>&1
        $expected = $mode -in @('success', 'pagination')
        if (($LASTEXITCODE -eq 0) -ne $expected) { throw "Tracker CLI unexpected exit for $mode; expected success=$expected" }
        Write-Output "PASS: tracker CLI $mode"
    }
}
finally {
    $env:PATH = $oldPath
    $env:NSN_ISSUE_PROBE_MODE = $oldMode
    $resolved = [IO.Path]::GetFullPath($scratch)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^nsn-issue-probes-[a-f0-9]{32}$') { throw 'Unexpected cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
exit 0
