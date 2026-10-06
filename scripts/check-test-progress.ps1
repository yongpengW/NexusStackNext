# Exercise the actual runner while an external CLI workload remains blocked.
$ErrorActionPreference = 'Stop'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('nsn-test-progress-' + [Guid]::NewGuid().ToString('N'))
$fixture = Join-Path $scratch 'repo'
$adapter = Join-Path $scratch 'adapter'
$temporary = Join-Path $scratch 'temporary'
foreach ($directory in @((Join-Path $fixture 'scripts'), (Join-Path $fixture 'tests/Probe.Tests'), $adapter, $temporary)) {
    [void][IO.Directory]::CreateDirectory($directory)
}
foreach ($name in @('run-tests.ps1', 'test-ownership.psm1', 'test-console.psm1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $fixture "scripts/$name")
}
[IO.File]::WriteAllText((Join-Path $fixture 'tests/Probe.Tests/Probe.Tests.csproj'), '<Project />')
$fake = @'
$ErrorActionPreference = 'Stop'
$outcome = if ($env:NSN_PROGRESS_MODE.StartsWith('fail')) { 'Failed' } else { 'Passed' }
$label = if ($env:NSN_PROGRESS_MODE.EndsWith('zh')) {
    if ($outcome -eq 'Passed') { '已通过' } else { '失败' }
} else { $outcome }
Write-Output "  $label NexusStackNext.Probe.Tests.Journey(argument: private-argument-sentinel) [3 ms]"
Write-Output 'Passed! private-argument-sentinel'
$watch = [Diagnostics.Stopwatch]::StartNew()
while (-not (Test-Path -LiteralPath $env:NSN_PROGRESS_RELEASE)) {
    if ($watch.Elapsed.TotalSeconds -gt 12) { exit 9 }
    Start-Sleep -Milliseconds 25
}


if ($outcome -eq 'Failed') {
    Write-Output 'Failed! - Failed: 1, Passed: 0, Skipped: 0, Total: 1'
    exit 7
}
Write-Output 'Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1'
exit 0
'@
$fakePath = Join-Path $adapter 'fake-dotnet.ps1'
[IO.File]::WriteAllText($fakePath, $fake, [Text.UTF8Encoding]::new($false))
if ($IsWindows) {
    [IO.File]::WriteAllText((Join-Path $adapter 'dotnet.cmd'), "@echo off`r`n`"$((Get-Command pwsh).Source)`" -NoProfile -File `"$fakePath`" %*`r`n", [Text.UTF8Encoding]::new($false))
} else {
    $quote = "'`"'`"'"
    $shell = (Get-Command pwsh).Source.Replace("'", $quote)
    $script = $fakePath.Replace("'", $quote)
    [IO.File]::WriteAllText((Join-Path $adapter 'dotnet'), "#!/bin/sh`nexec '$shell' -NoProfile -File '$script' `"`$@`"`n", [Text.UTF8Encoding]::new($false))
    & chmod +x (Join-Path $adapter 'dotnet')
    if ($LASTEXITCODE -ne 0) { throw 'Could not enable the external adapter.' }
}
$release = Join-Path $scratch 'release'
foreach ($mode in @('pass', 'pass-zh', 'fail', 'fail-zh')) {
    if (Test-Path -LiteralPath $release) { Remove-Item -LiteralPath $release }
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', (Join-Path $fixture 'scripts/run-tests.ps1'), '-NoBuild', '-Project', 'Probe.Tests')) {
        $start.ArgumentList.Add($argument)
    }
    foreach ($key in @('TEMP', 'TMP', 'TMPDIR')) { $start.Environment[$key] = $temporary }
    $start.Environment['PATH'] = $adapter + [IO.Path]::PathSeparator + $env:PATH
    $start.Environment['NEXUSSTACK_TEST_POSTGRES'] = 'Host=progress-probe.invalid'
    foreach ($key in @('NEXUSSTACK_TEST_REDIS', 'NEXUSSTACK_TEST_RABBITMQ')) { [void]$start.Environment.Remove($key) }
    $start.Environment['NSN_PROGRESS_MODE'] = $mode
    $start.Environment['NSN_PROGRESS_RELEASE'] = $release
    $start.Environment['GITHUB_ACTIONS'] = 'false'
    $previousReports = @(Get-ChildItem -LiteralPath $temporary -Directory -Filter 'nsn-test-results-*' | Select-Object -ExpandProperty FullName)
    $process = [Diagnostics.Process]::Start($start)
    $errors = $process.StandardError.ReadToEndAsync()
    $lines = [Collections.Generic.List[string]]::new()
    try {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $observed = $false
        while ($watch.Elapsed.TotalSeconds -lt 5 -and -not $observed) {
            $read = $process.StandardOutput.ReadLineAsync()
            $remaining = [Math]::Max(1, [int](5000 - $watch.Elapsed.TotalMilliseconds))
            if (-not $read.Wait($remaining)) { break }
            $line = $read.GetAwaiter().GetResult()
            if ($null -eq $line) { break }
            $lines.Add($line)
            $observed = $line.Contains('TEST_PROGRESS')
        }
        if (-not $observed -or $process.HasExited) { throw 'No actual case progress arrived while the workload was running.' }
        $expected = if ($mode.StartsWith('fail')) { 'Failed' } else { 'Passed' }
        $progress = $lines[$lines.Count - 1]
        if (-not $progress.Contains("outcome=$expected") -or -not $progress.Contains('project=Probe.Tests') -or -not $progress.Contains('completed=1') -or
            -not $progress.Contains('method=NexusStackNext.Probe.Tests.Journey') -or $progress.Contains('private-argument-sentinel')) {
            throw 'Live progress omitted its result or exposed case parameters.'
        }
        $privateReady = $false
        $privateWatch = [Diagnostics.Stopwatch]::StartNew()
        while ($privateWatch.Elapsed.TotalSeconds -lt 2.5 -and -not $privateReady) {
            $newReports = @(Get-ChildItem -LiteralPath $temporary -Directory -Filter 'nsn-test-results-*' | Where-Object FullName -NotIn $previousReports)
            if ($newReports.Count -gt 1) { throw 'Ambiguous live private diagnostics.' }
            if ($newReports.Count -eq 1) {
                $privatePath = Join-Path $newReports[0].FullName 'Probe.Tests.console.log'
                if (Test-Path -LiteralPath $privatePath) {
                    $privateStream = [IO.File]::Open($privatePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                    $privateReader = [IO.StreamReader]::new($privateStream)
                    try { $privateReady = $privateReader.ReadToEnd().Contains('private-argument-sentinel') }
                    finally { $privateReader.Dispose() }
                }
            }
            if (-not $privateReady) { Start-Sleep -Milliseconds 25 }
        }
        if (-not $privateReady -or $process.HasExited) { throw 'Private diagnostic evidence was not available before the workload finished.' }
        [IO.File]::WriteAllText($release, 'release')
        $tail = $process.StandardOutput.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) { throw 'The released runner did not finish.' }
        $output = ($lines -join "`n") + $tail.GetAwaiter().GetResult() + $errors.GetAwaiter().GetResult()
        if ($output.Contains('private-argument-sentinel') -or ($expected -eq 'Passed' -and $process.ExitCode -ne 0) -or
            ($expected -eq 'Failed' -and $process.ExitCode -eq 0)) { throw 'Runner result propagation or parameter redaction failed.' }
        $guard = Get-Content -LiteralPath (Join-Path $temporary 'nexusstack-run-tests.lock') -Raw | ConvertFrom-Json
        if ($guard.State -ne 'idle') { throw 'Returned external workload did not release ownership.' }
        Write-Output "PASS: $mode progress and private evidence arrive before completion, with parameters withheld and exit/ownership preserved"
    }
    finally {
        # Release only our own external fixture; it has a bounded wait even if a reader failed.
        [IO.File]::WriteAllText($release, 'release')
        [void]$process.WaitForExit(15000)
        $process.Dispose()
    }
}

# Exact project selection must fail before starting a workload when the name does not exist.
$start.ArgumentList.Clear()
foreach ($argument in @('-NoProfile', '-File', (Join-Path $fixture 'scripts/run-tests.ps1'), '-NoBuild', '-Project', 'Missing.Tests')) {
    $start.ArgumentList.Add($argument)
}
$process = [Diagnostics.Process]::Start($start)
try {
    $output = $process.StandardOutput.ReadToEndAsync()
    $errors = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(10000)) { throw 'Invalid project selection did not return.' }
    $text = $output.GetAwaiter().GetResult() + $errors.GetAwaiter().GetResult()
    if ($process.ExitCode -eq 0 -or -not $text.Contains('TEST_PROJECT_NOT_FOUND') -or $text.Contains('TEST_PROGRESS')) {
        throw 'Unknown project selection started a workload or silently passed.'
    }
    Write-Output 'PASS: unknown project rejected before test workload'
}
finally { $process.Dispose() }

# Real xUnit verifies early failure stops later work and still disposes the owned fixture.
& (Join-Path $PSScriptRoot 'check-test-stop.ps1')
& (Join-Path $PSScriptRoot 'check-local-test-concurrency.ps1')
