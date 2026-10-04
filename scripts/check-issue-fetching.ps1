# Exercise the real tracker CLI with a disposable gh adapter; never contacts GitHub.
[CmdletBinding()]
param([string[]]$Modes)

$ErrorActionPreference = 'Stop'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('nsn-issue-probes-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
$oldPath = $env:PATH
$oldMode = $env:NSN_ISSUE_PROBE_MODE
Import-Module (Join-Path $PSScriptRoot 'probe-process-identity.psm1') -Force
function Stop-OwnedPipeFixture([switch]$RequireRunning) {
    $marker = Join-Path $scratch 'held-pipes'
    if (Test-Path -LiteralPath $marker) {
        $identity = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($identity.Pid -le 0 -or [string]::IsNullOrWhiteSpace($identity.StartStamp) -or $identity.Route -notmatch '/labels\?') { throw 'Pipe probe has no valid target process identity.' }
        $owned = Get-Process -Id $identity.Pid -ErrorAction SilentlyContinue
        $isOriginal = $owned -and -not $owned.HasExited -and (Get-ProbeProcessStartStamp $identity.Pid) -ceq $identity.StartStamp
        if ($RequireRunning -and -not $isOriginal) { throw 'Pipe probe has no surviving owned child at CLI exit.' }
        if ($RequireRunning -and $IsLinux) {
            Write-Output ('PASS: Linux kernel process identity; UTC stamp differs=' + ($owned.StartTime.ToUniversalTime().Ticks -ne $identity.StartedUtcTicks))
        }
        if ($isOriginal) {
            $owned.Kill($true)
            if (-not $owned.WaitForExit(5000)) { throw 'Owned pipe fixture did not exit.' }
        }
    } elseif ($RequireRunning) { throw 'Pipe probe never reached its final labels request.' }
}
$adapter = @'
#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'probe-process-identity.psm1') -Force
$route = $args[1]
if ($env:NSN_ISSUE_PROBE_MODE -eq 'accumulated-budget') { Start-Sleep -Milliseconds 600 }
$timeoutRoute = switch ($env:NSN_ISSUE_PROBE_MODE) {
    'request-timeout-parent' { '/sub_issues\?' }
    'request-timeout-dependency' { '/issues/2/dependencies/blocked_by\?' }
    'request-timeout-labels' { '/labels\?' }
    default { $null }
}
if ($timeoutRoute -and $route -match $timeoutRoute) {
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'timed-process'), (ConvertTo-Json -Compress @{ Pid = $PID; StartStamp = Get-ProbeProcessStartStamp $PID; Route = $route }))
    Start-Sleep -Seconds 15
}
if ($route -match '/issues\?') {
    switch ($env:NSN_ISSUE_PROBE_MODE) {
        { $_ -in 'request-timeout', 'total-timeout' } {
            [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'timed-process'), (ConvertTo-Json -Compress @{ Pid = $PID; StartStamp = Get-ProbeProcessStartStamp $PID; Route = $route }))
            Start-Sleep -Seconds 10
        }
        'persistent-service' {
            $marker = Join-Path $PSScriptRoot 'service-attempts'
            $attempt = if (Test-Path -LiteralPath $marker) { 1 + [int](Get-Content -LiteralPath $marker) } else { 1 }
            [IO.File]::WriteAllText($marker, [string]$attempt)
            if ($attempt -le 3) {
                [Console]::Error.WriteLine('gh: NSN_SYNTHETIC_SECRET unavailable (HTTP 503)')
                exit 1
            }
        }
        { $_ -in 'permanent-service', 'stdout-service' } {
            $marker = Join-Path $PSScriptRoot $env:NSN_ISSUE_PROBE_MODE
            if (-not (Test-Path -LiteralPath $marker)) {
                [IO.File]::WriteAllText($marker, 'failure must not retry')
                if ($_ -eq 'permanent-service') { [Console]::Error.WriteLine('gh: NSN_SYNTHETIC_SECRET forbidden (HTTP 403)') }
                else { 'gh: NSN_SYNTHETIC_SECRET fake diagnostic on stdout (HTTP 503)' }
                exit 1
            }
        }
    }
}
if ($env:NSN_ISSUE_PROBE_MODE -eq 'invalid-list' -and $route -match '/issues\?') {
    'NSN_SYNTHETIC_SECRET invalid-json'
    exit 0
}
$transientRoute = switch ($env:NSN_ISSUE_PROBE_MODE) {
    'transient-list' { '/issues\?' }
    'transient-502' { '/issues\?' }
    'transient-504' { '/issues\?' }
    'transient-parent' { '/sub_issues\?' }
    'transient-dependency' { '/issues/2/dependencies/blocked_by\?' }
    'transient-labels' { '/labels\?' }
    default { $null }
}
if ($transientRoute -and $route -match $transientRoute) {
    $identity = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($env:NSN_ISSUE_PROBE_MODE + $route)))
    $marker = Join-Path $PSScriptRoot $identity
    if (-not (Test-Path -LiteralPath $marker)) {
        [IO.File]::WriteAllText($marker, 'first failure')
        $status = switch ($env:NSN_ISSUE_PROBE_MODE) { 'transient-502' { 502 }; 'transient-504' { 504 }; default { 503 } }
        [Console]::Error.WriteLine("gh: NSN_SYNTHETIC_SECRET service unavailable (HTTP $status)")
        exit 1
    }
}
switch -Regex ($route) {
    '/issues\?' { $items = '[{"number":1,"title":"Map","labels":[{"name":"wayfinder:map"}]},{"number":2,"title":"Task","labels":[{"name":"wayfinder:task"}]}]' }
    '/sub_issues\?' { $items = '[{"number":2}]' }
    '/dependencies/blocked_by' {
        if ($env:NSN_ISSUE_PROBE_MODE -eq 'failure' -and $route -like '*/2/*') { exit 1 }
        if ($env:NSN_ISSUE_PROBE_MODE -eq 'invalid' -and $route -like '*/2/*') { 'not-json'; exit 0 }
        if ($env:NSN_ISSUE_PROBE_MODE -eq 'empty' -and $route -like '*/2/*') { exit 0 }
        if ($env:NSN_ISSUE_PROBE_MODE -eq 'null' -and $route -like '*/2/*') { 'null'; exit 0 }
        if ($env:NSN_ISSUE_PROBE_MODE -eq 'zero-pages' -and $route -like '*/2/*') { '[]'; exit 0 }
        if ($env:NSN_ISSUE_PROBE_MODE -eq 'pagination' -and $args -notcontains '--paginate') { exit 1 }
        $items = if ($route -like '*/2/*') {
            if ($env:NSN_ISSUE_PROBE_MODE -eq 'missing') { '[{"number":99}]' } else { '[{"number":1}]' }
        } else { '[]' }
    }
    '/labels\?' { $items = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'labels.json') -Raw }
    default { exit 1 }
}
if ($env:NSN_ISSUE_PROBE_MODE -eq 'held-pipes-labels' -and $route -match '/labels\?') {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command pwsh).Source
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    foreach ($argument in @('-NoProfile', '-Command', 'Start-Sleep -Seconds 15')) { $start.ArgumentList.Add($argument) }
    # Inherit the adapter's output handles; its exit alone must not bypass the read budget.
    $child = [Diagnostics.Process]::Start($start)
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'held-pipes'), (ConvertTo-Json -Compress @{ Pid = $child.Id; StartedUtcTicks = $child.StartTime.ToUniversalTime().Ticks; StartStamp = Get-ProbeProcessStartStamp $child.Id; Route = $route }))
    $child.Dispose()
}
# Real gh emits independent JSON pages unless --slurp wraps the pages in an array.
# All records are on page two, including the invalid blocker in the missing probe.
if ($env:NSN_ISSUE_PROBE_MODE -eq 'malformed-slurp') { $items; exit 0 }
if ($args -contains '--slurp') { '[[],' + $items + ']' } else { "[]`n$items" }
exit 0
'@
try {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'probe-process-identity.psm1') -Destination (Join-Path $scratch 'probe-process-identity.psm1')
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
    $knownModes = @('success', 'failure', 'invalid', 'empty', 'null', 'zero-pages', 'missing', 'pagination', 'transient-list', 'transient-parent', 'transient-dependency', 'transient-labels', 'transient-502', 'transient-504', 'invalid-list', 'malformed-slurp', 'persistent-service', 'permanent-service', 'stdout-service', 'request-timeout', 'request-timeout-parent', 'request-timeout-dependency', 'request-timeout-labels', 'total-timeout', 'accumulated-budget', 'held-pipes-labels')
    if ($Modes.Count -eq 0) { $Modes = $knownModes }
    foreach ($mode in $Modes) {
        if ($mode -notin $knownModes) { throw 'Unknown tracker probe; refusing an empty or misleading check.' }
        $env:NSN_ISSUE_PROBE_MODE = $mode
        $options = switch ($mode) {
            { $_ -like 'request-timeout*' -or $_ -eq 'held-pipes-labels' } { @('-RequestTimeoutSeconds', '2', '-TotalTimeoutSeconds', '30') }
            'total-timeout' { @('-RequestTimeoutSeconds', '20', '-TotalTimeoutSeconds', '2') }
            'accumulated-budget' { @('-RequestTimeoutSeconds', '20', '-TotalTimeoutSeconds', '2') }
            default { @() }
        }
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $timedProcess = Join-Path $scratch 'timed-process'
        if (Test-Path -LiteralPath $timedProcess) { Remove-Item -LiteralPath $timedProcess }
        # Observe the CLI process itself; PowerShell's native pipeline on Windows also waits for descendants.
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = (Get-Command pwsh).Source
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in (@('-NoProfile', '-File', (Join-Path $PSScriptRoot 'check-issues.ps1'), '-Repository', 'fixture/repo') + $options)) { $start.ArgumentList.Add($argument) }
        $cli = [Diagnostics.Process]::Start($start)
        try {
            $stdout = $cli.StandardOutput.ReadToEndAsync()
            $stderr = $cli.StandardError.ReadToEndAsync()
            if (-not $cli.WaitForExit(30000)) { $cli.Kill($true); throw 'Tracker probe CLI did not finish.' }
            $timer.Stop()
            # The fixture may inherit CLI handles too. Close only its owned child after observing CLI exit.
            if ($mode -eq 'held-pipes-labels') { Stop-OwnedPipeFixture -RequireRunning }
            $streams = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdout, $stderr))
            if (-not $streams.Wait(2000)) { throw 'Tracker probe CLI output did not finish.' }
            $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
            $exitCode = $cli.ExitCode
        }
        finally { $cli.Dispose() }
        $expected = $mode -in @('success', 'pagination', 'transient-list', 'transient-parent', 'transient-dependency', 'transient-labels', 'transient-502', 'transient-504')
        if (($exitCode -eq 0) -ne $expected) { throw "Tracker CLI unexpected exit for $mode; expected success=$expected" }
        if (($output -join "`n") -match 'NSN_SYNTHETIC_SECRET') { throw "Tracker CLI exposed raw adapter output for $mode" }
        if ($mode -eq 'persistent-service' -and [int](Get-Content -LiteralPath (Join-Path $scratch 'service-attempts')) -ne 3) { throw 'Tracker CLI exceeded its bounded service recovery attempts.' }
        if ($mode -eq 'accumulated-budget' -and $timer.Elapsed.TotalSeconds -gt 6) { throw 'Tracker CLI renewed the overall budget between requests.' }
        if ($mode -like 'request-timeout*' -or $mode -eq 'total-timeout') {
            $ceiling = if ($mode -in 'request-timeout', 'total-timeout') { 6 } else { 10 }
            if ($timer.Elapsed.TotalSeconds -gt $ceiling) { throw "Tracker CLI exceeded the time budget for $mode" }
            if (-not (Test-Path -LiteralPath $timedProcess)) { throw "Timeout probe never reached its target request: $mode" }
            $identity = Get-Content -LiteralPath $timedProcess -Raw | ConvertFrom-Json
            $target = switch ($mode) {
                'request-timeout-parent' { '/sub_issues\?' }
                'request-timeout-dependency' { '/issues/2/dependencies/blocked_by\?' }
                'request-timeout-labels' { '/labels\?' }
                default { '/issues\?' }
            }
            if ($identity.Pid -le 0 -or [string]::IsNullOrWhiteSpace($identity.StartStamp) -or $identity.Route -notmatch $target) { throw 'Timeout probe has no valid target process identity.' }
            $original = Get-Process -Id $identity.Pid -ErrorAction SilentlyContinue
            if ($original -and -not $original.HasExited -and (Get-ProbeProcessStartStamp $identity.Pid) -ceq $identity.StartStamp) { throw 'Timed-out adapter is still running.' }
        }
        if ($mode -eq 'held-pipes-labels') {
            $marker = Join-Path $scratch 'held-pipes'
            if (-not (Test-Path -LiteralPath $marker)) { throw 'Pipe probe never reached its final labels request.' }
            $identity = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
            if ($identity.Pid -le 0 -or [string]::IsNullOrWhiteSpace($identity.StartStamp) -or $identity.Route -notmatch '/labels\?') { throw 'Pipe probe has no valid target process identity.' }
            if ($timer.Elapsed.TotalSeconds -gt 10) { throw 'Tracker CLI waited beyond the budget for inherited output pipes.' }
        }
        Write-Output "PASS: tracker CLI $mode"
    }
}
finally {
    $env:PATH = $oldPath
    $env:NSN_ISSUE_PROBE_MODE = $oldMode
    Stop-OwnedPipeFixture
    $resolved = [IO.Path]::GetFullPath($scratch)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^nsn-issue-probes-[a-f0-9]{32}$') { throw 'Unexpected cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
exit 0
