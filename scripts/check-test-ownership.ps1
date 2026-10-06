# Exercise the real runner CLI with an external dotnet adapter; no real services are contacted.
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'probe-process-identity.psm1') -Force
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('nsn-test-ownership-probe-' + [Guid]::NewGuid().ToString('N'))
$fixture = Join-Path $scratch 'repo'
$temporary = Join-Path $scratch 'temporary'
$adapter = Join-Path $scratch 'adapter'
foreach ($directory in @((Join-Path $fixture 'scripts'), (Join-Path $fixture 'tests/Probe.Tests'), $temporary, $adapter)) {
    [void][IO.Directory]::CreateDirectory($directory)
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'run-tests.ps1') -Destination (Join-Path $fixture 'scripts/run-tests.ps1')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'test-console.psm1') -Destination (Join-Path $fixture 'scripts/test-console.psm1')
$module = Join-Path $PSScriptRoot 'test-ownership.psm1'
if (Test-Path -LiteralPath $module) { Copy-Item -LiteralPath $module -Destination (Join-Path $fixture 'scripts/test-ownership.psm1') }
[IO.File]::WriteAllText((Join-Path $fixture 'tests/Probe.Tests/Probe.Tests.csproj'), '<Project />')
$fake = @'
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'probe-process-identity.psm1') -Force
$entry = Join-Path $env:NSN_OWNERSHIP_PROBE ([Guid]::NewGuid().ToString('N') + '.entry')
[IO.File]::WriteAllText($entry, (@{ Pid = $PID; StartStamp = Get-ProbeProcessStartStamp $PID } | ConvertTo-Json -Compress))
if ($env:NSN_OWNERSHIP_MODE -eq 'hold') {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath (Join-Path $env:NSN_OWNERSHIP_PROBE 'release'))) {
        if ($watch.Elapsed.TotalSeconds -gt 20) { exit 3 }
        Start-Sleep -Milliseconds 50
    }
}
Write-Output 'Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1'
if ($env:NSN_OWNERSHIP_MODE -eq 'fail') { exit 1 }
exit 0
'@
$fakePath = Join-Path $adapter 'fake-dotnet.ps1'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'probe-process-identity.psm1') -Destination (Join-Path $adapter 'probe-process-identity.psm1')
[IO.File]::WriteAllText($fakePath, $fake, [Text.UTF8Encoding]::new($false))
if ($IsWindows) {
    [IO.File]::WriteAllText((Join-Path $adapter 'dotnet.cmd'), "@echo off`r`n`"$((Get-Command pwsh).Source)`" -NoProfile -File `"$fakePath`" %*`r`n", [Text.UTF8Encoding]::new($false))
} else {
    # Paths come from our own temp directory; single quotes are escaped for the shell adapter.
    $quote = "'`"'`"'"
    $pwsh = (Get-Command pwsh).Source.Replace("'", $quote)
    $script = $fakePath.Replace("'", $quote)
    [IO.File]::WriteAllText((Join-Path $adapter 'dotnet'), "#!/bin/sh`nexec '$pwsh' -NoProfile -File '$script' `"`$@`"`n", [Text.UTF8Encoding]::new($false))
    & chmod +x (Join-Path $adapter 'dotnet')
    if ($LASTEXITCODE -ne 0) { throw 'Could not enable the isolated adapter.' }
}
function Start-Runner([string]$Mode, [string]$Root = $fixture, [bool]$Build = $false) {
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', (Join-Path $Root 'scripts/run-tests.ps1'))) { $start.ArgumentList.Add($argument) }
    if (-not $Build) { $start.ArgumentList.Add('-NoBuild') }
    foreach ($key in @('TEMP', 'TMP', 'TMPDIR')) { $start.Environment[$key] = $temporary }
    $start.Environment['PATH'] = $adapter + [IO.Path]::PathSeparator + $env:PATH
    $start.Environment['NEXUSSTACK_TEST_POSTGRES'] = 'Host=ownership-probe.invalid'
    foreach ($key in @('NEXUSSTACK_TEST_REDIS', 'NEXUSSTACK_TEST_RABBITMQ')) { [void]$start.Environment.Remove($key) }
    $start.Environment['GITHUB_ACTIONS'] = 'false'
    $start.Environment['NSN_OWNERSHIP_PROBE'] = $scratch
    $start.Environment['NSN_OWNERSHIP_MODE'] = $Mode
    $process = [Diagnostics.Process]::Start($start)
    return @{ Process = $process; Output = $process.StandardOutput.ReadToEndAsync(); Error = $process.StandardError.ReadToEndAsync() }
}
function Complete-Runner($Runner) {
    if (-not $Runner.Process.WaitForExit(10000)) { throw 'Isolated runner exceeded its deadline.' }
    $output = $Runner.Output.GetAwaiter().GetResult() + $Runner.Error.GetAwaiter().GetResult()
    return @{ Exit = $Runner.Process.ExitCode; Output = $output }
}
$owner = $null
$contender = $null
try {
    $owner = Start-Runner 'hold'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (@(Get-ChildItem -LiteralPath $scratch -Filter '*.entry').Count -eq 0) {
        if ($owner.Process.HasExited -or $watch.Elapsed.TotalSeconds -gt 10) { throw 'The positive-control workload never started.' }
        Start-Sleep -Milliseconds 50
    }
    $external = Get-Content -LiteralPath @(Get-ChildItem -LiteralPath $scratch -Filter '*.entry')[0].FullName -Raw | ConvertFrom-Json
    if ($external.Pid -eq $owner.Process.Id) { throw 'The adapter must be an external workload, not in-process code.' }
    $guard = Join-Path $temporary 'nexusstack-run-tests.lock'
    if (-not (Test-Path -LiteralPath $guard)) { throw 'The runner has no ownership protection object.' }
    [IO.File]::SetLastWriteTimeUtc($guard, [DateTime]::UtcNow.AddHours(-3))
    $secondRoot = Join-Path $scratch 'second-repo'
    Copy-Item -LiteralPath $fixture -Destination $secondRoot -Recurse
    $contender = Start-Runner 'pass' $secondRoot
    $result = Complete-Runner $contender
    if ($result.Exit -eq 0 -or @(Get-ChildItem -LiteralPath $scratch -Filter '*.entry').Count -ne 1) {
        throw 'A second workload entered while the old protection still had a live owner.'
    }
    [IO.File]::WriteAllText((Join-Path $scratch 'release'), 'release')
    if ((Complete-Runner $owner).Exit -ne 0) { throw 'The positive-control owner did not finish successfully.' }
    Write-Output 'PASS: an old active guard rejects a second real runner CLI workload'
    $owner.Process.Dispose()
    $owner = Start-Runner 'pass'
    if ((Complete-Runner $owner).Exit -ne 0) { throw 'A completed owner prevented a later run.' }
    Write-Output 'PASS: success releases ownership for the next workload'
    foreach ($build in @($false, $true)) {
        $owner.Process.Dispose()
        $owner = Start-Runner 'fail' $fixture $build
        if ((Complete-Runner $owner).Exit -eq 0) { throw 'The fixture did not exercise a failing external command.' }
        $owner.Process.Dispose()
        $owner = Start-Runner 'pass'
        if ((Complete-Runner $owner).Exit -ne 0) { throw 'A normally returned failure prevented safe recovery.' }
    }
    Write-Output 'PASS: test and build failure release ownership after the external command returns'
    Remove-Item -LiteralPath (Join-Path $scratch 'release')
    $before = @(Get-ChildItem -LiteralPath $scratch -Filter '*.entry').Count
    $owner.Process.Dispose()
    $contender.Process.Dispose()
    $owner = Start-Runner 'hold'
    $contender = Start-Runner 'hold' $secondRoot
    $watch.Restart()
    while (@(Get-ChildItem -LiteralPath $scratch -Filter '*.entry').Count -eq $before -or
        (-not $owner.Process.HasExited -and -not $contender.Process.HasExited)) {
        if ($watch.Elapsed.TotalSeconds -gt 10) { break }
        Start-Sleep -Milliseconds 50
    }
    [IO.File]::WriteAllText((Join-Path $scratch 'release'), 'release')
    $first = Complete-Runner $owner
    $second = Complete-Runner $contender
    if (@($first, $second | Where-Object Exit -EQ 0).Count -ne 1 -or
        @(Get-ChildItem -LiteralPath $scratch -Filter '*.entry').Count -ne $before + 1) {
        throw 'Simultaneous runners did not admit exactly one external workload.'
    }
    Write-Output 'PASS: simultaneous runners in two workspaces admit exactly one external workload'
    $before = @(Get-ChildItem -LiteralPath $scratch -Filter '*.entry').Count
    foreach ($badState in @('[{"Version":1,"State":"idle"}]', '[{"Version":1,"State":"idle"},{"Version":1,"State":"idle"}]',
        '', '{', '17', '{"Version":2,"State":"idle"}', '{"Version":[1],"State":"idle"}', '{"Version":1,"State":["idle"]}',
        ('{"Version":1,"State":"active","Pid":' + $PID + ',"StartedUtcTicks":0}'),
        '{"Version":1,"State":"active","Pid":2147483647,"StartedUtcTicks":0}')) {
        [IO.File]::WriteAllText($guard, $badState)
        $contender.Process.Dispose()
        $contender = Start-Runner 'pass'
        $result = Complete-Runner $contender
        if ($result.Exit -eq 0 -or @(Get-ChildItem -LiteralPath $scratch -Filter '*.entry').Count -ne $before) {
            throw 'Malformed or uncertain ownership state admitted a workload.'
        }
    }
    Write-Output 'PASS: malformed, legacy and uncertain PID identities fail closed before workload starts'
    # This reset touches only the deliberately malformed object in our private fixture.
    [IO.File]::WriteAllText($guard, '{"Version":1,"State":"idle"}')
    Remove-Item -LiteralPath (Join-Path $scratch 'release')
    $previous = @(Get-ChildItem -LiteralPath $scratch -Filter '*.entry' | Select-Object -ExpandProperty FullName)
    $owner.Process.Dispose()
    $owner = Start-Runner 'hold'
    $watch.Restart()
    do {
        $newEntries = @(Get-ChildItem -LiteralPath $scratch -Filter '*.entry' | Where-Object FullName -NotIn $previous)
        if ($owner.Process.HasExited -or $watch.Elapsed.TotalSeconds -gt 10) { throw 'The orphan positive-control workload never started.' }
        if ($newEntries.Count -eq 0) { Start-Sleep -Milliseconds 50 }
    } while ($newEntries.Count -eq 0)
    if ($newEntries.Count -ne 1) { throw 'Unexpected orphan fixture workload count.' }
    $external = Get-Content -LiteralPath $newEntries[0].FullName -Raw | ConvertFrom-Json
    $owner.Process.Kill() # Only our own fixture owner, deliberately leaving its external workload alive.
    if (-not $owner.Process.WaitForExit(5000)) { throw 'The fixture owner did not terminate.' }
    $orphan = Get-Process -Id $external.Pid -ErrorAction Stop
    if ($orphan.HasExited -or (Get-ProbeProcessStartStamp $external.Pid) -cne $external.StartStamp) { throw 'The fixture workload identity changed.' }
    $before = @(Get-ChildItem -LiteralPath $scratch -Filter '*.entry').Count
    $contender.Process.Dispose()
    $contender = Start-Runner 'pass' $secondRoot
    $result = Complete-Runner $contender
    if ($result.Exit -eq 0 -or @(Get-ChildItem -LiteralPath $scratch -Filter '*.entry').Count -ne $before) {
        throw 'An orphaned external workload lost its protection.'
    }
    $orphan.Kill($true)
    if (-not $orphan.WaitForExit(5000)) { throw 'The isolated orphan did not terminate.' }
    $orphan.Dispose()
    $contender.Process.Dispose()
    $contender = Start-Runner 'pass'
    if ((Complete-Runner $contender).Exit -eq 0) { throw 'An abandoned marker was reclaimed without proof of normal completion.' }
    Write-Output 'PASS: lost owners fail closed while external workload lives and after uncertain termination'
}
finally {
    foreach ($runner in @($owner, $contender)) {
        if ($null -eq $runner) { continue }
        if (-not $runner.Process.HasExited) { $runner.Process.Kill($true); [void]$runner.Process.WaitForExit(5000) }
        $runner.Process.Dispose()
    }
    # On probe failure, reclaim only our adapter processes with the exact recorded start identity.
    foreach ($entry in @(Get-ChildItem -LiteralPath $scratch -Filter '*.entry')) {
        $identity = Get-Content -LiteralPath $entry.FullName -Raw | ConvertFrom-Json
        $process = Get-Process -Id $identity.Pid -ErrorAction SilentlyContinue
        if ($null -ne $process -and -not $process.HasExited -and (Get-ProbeProcessStartStamp $identity.Pid) -ceq $identity.StartStamp) {
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
            $process.Dispose()
        }
    }
}
