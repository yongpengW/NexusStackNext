# Verify connection selection through the actual runner and an external dotnet adapter.
param([string]$RuntimeDirectory)
$ErrorActionPreference = 'Stop'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('nsn-local-postgres-probe-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
if ($IsWindows) {
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetOwner($sid)
    $security.SetAccessRuleProtection($true, $false)
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $scratch -AclObject $security
} else { [IO.File]::SetUnixFileMode($scratch, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute) }
$fixture = Join-Path $scratch 'repo'
$adapter = Join-Path $scratch 'adapter'
$temporary = Join-Path $scratch 'temporary'
foreach ($directory in @((Join-Path $fixture 'scripts'), (Join-Path $fixture 'env'), (Join-Path $fixture 'tests/Probe.Tests'), $adapter, $temporary)) {
    [void][IO.Directory]::CreateDirectory($directory)
}
foreach ($name in @('run-tests.ps1', 'test-ownership.psm1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $fixture "scripts/$name")
}
foreach ($name in @('local-postgres.ps1', 'local-postgres.psm1')) {
    $path = Join-Path $PSScriptRoot $name
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $fixture "scripts/$name") }
}
[IO.File]::WriteAllText((Join-Path $fixture 'tests/Probe.Tests/Probe.Tests.csproj'), '<Project />')
$base = "NEXUSSTACK_TEST_POSTGRES=Host=shared-fixture.invalid`nNEXUSSTACK_TEST_REDIS=redis-fixture.invalid`n"
$baseFile = Join-Path $fixture 'env/test.dev'
[IO.File]::WriteAllText($baseFile, $base)
$entry = Join-Path $scratch 'entered.json'
$fake = @'
$ErrorActionPreference = 'Stop'
[IO.File]::WriteAllText($env:NSN_LOCAL_PG_ENTRY, (@{ Postgres = $env:NEXUSSTACK_TEST_POSTGRES; Redis = $env:NEXUSSTACK_TEST_REDIS } | ConvertTo-Json -Compress))
Write-Output 'Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1'
if ($env:NSN_LOCAL_PG_FAIL -eq 'true') { exit 7 }
exit 0
'@
$fakePath = Join-Path $adapter 'fake-dotnet.ps1'
[IO.File]::WriteAllText($fakePath, $fake, [Text.UTF8Encoding]::new($false))
if ($IsWindows) {
    [IO.File]::WriteAllText((Join-Path $adapter 'dotnet.cmd'), "@echo off`r`n`"$((Get-Command pwsh).Source)`" -NoProfile -File `"$fakePath`" %*`r`n")
} else {
    $quote = "'`"'`"'"
    $pwsh = (Get-Command pwsh).Source.Replace("'", $quote)
    $script = $fakePath.Replace("'", $quote)
    [IO.File]::WriteAllText((Join-Path $adapter 'dotnet'), "#!/bin/sh`nexec '$pwsh' -NoProfile -File '$script' `"`$@`"`n")
    & chmod +x (Join-Path $adapter 'dotnet')
    if ($LASTEXITCODE -ne 0) { throw 'Could not enable the isolated adapter.' }
}
function Invoke-Runner([string[]]$Arguments = @(), [bool]$Fail = $false, [string]$ScriptName = 'run-tests.ps1') {
    if (Test-Path -LiteralPath $entry) { Remove-Item -LiteralPath $entry }
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', (Join-Path $fixture "scripts/$ScriptName"), '-NoBuild') + $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($key in @('TEMP', 'TMP', 'TMPDIR')) { $start.Environment[$key] = $temporary }
    $start.Environment['PATH'] = $adapter + [IO.Path]::PathSeparator + $env:PATH
    foreach ($key in @('NEXUSSTACK_TEST_POSTGRES', 'NEXUSSTACK_TEST_REDIS', 'NEXUSSTACK_TEST_RABBITMQ')) { [void]$start.Environment.Remove($key) }
    $start.Environment['GITHUB_ACTIONS'] = 'false'
    $start.Environment['NSN_LOCAL_PG_ENTRY'] = $entry
    $start.Environment['NSN_LOCAL_PG_FAIL'] = [string]$Fail
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) {
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
            throw 'Fixture CLI exceeded its deadline.'
        }
        if (-not [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($output, $errorOutput)).Wait(5000)) { throw 'Fixture output did not drain.' }
        return @{ Exit = $process.ExitCode; Output = $output.Result + $errorOutput.Result }
    }
    finally { $process.Dispose() }
}
$positive = Invoke-Runner
if ($positive.Exit -ne 0 -or -not (Test-Path -LiteralPath $entry)) { throw 'The positive-control workload did not enter.' }
$selected = Get-Content -LiteralPath $entry -Raw | ConvertFrom-Json
if ($selected.Postgres -cne 'Host=shared-fixture.invalid') { throw 'Default selection no longer uses the existing configuration.' }
Write-Output 'PASS: default runner still selects its original test configuration'
$connectionFile = Join-Path $scratch 'connection.private'
function New-FixtureConnection([string]$TargetHost, [string]$TargetPort, [bool]$WithPassword = $true) {
    $settings = [Data.Common.DbConnectionStringBuilder]::new()
    $settings['Host'] = $TargetHost
    $settings['Port'] = $TargetPort
    $settings['Database'] = 'postgres'
    $settings['Username'] = 'nsn_test_fixture'
    if ($WithPassword) { $settings['Password'] = 'fixture-only-sentinel' }
    return $settings.get_ConnectionString()
}
$local = New-FixtureConnection '127.0.0.1' '61599'
[IO.File]::WriteAllText($connectionFile, $local)
$overridden = Invoke-Runner -Arguments @('-LocalPostgresConnectionFile', $connectionFile)
if ($overridden.Exit -ne 0 -or -not (Test-Path -LiteralPath $entry)) { throw 'Explicit local connection did not reach the real runner workload.' }
$selected = Get-Content -LiteralPath $entry -Raw | ConvertFrom-Json
if ($selected.Postgres -cne $local -or $selected.Redis -cne 'redis-fixture.invalid' -or [IO.File]::ReadAllText($baseFile) -cne $base) {
    throw 'Connection override failed to preserve other test settings and the original file.'
}
if ($overridden.Output.Contains('fixture-only-sentinel')) { throw 'Connection credential reached public CLI output.' }
Write-Output 'PASS: private local connection overrides only PostgreSQL and leaves the original file unchanged'
$blank = Invoke-Runner -Arguments @('-LocalPostgresConnectionFile', '')
if ($blank.Exit -eq 0 -or (Test-Path -LiteralPath $entry) -or -not $blank.Output.Contains('LOCAL_POSTGRES_CONFIGURATION_REJECTED')) {
    throw 'An explicitly empty local selection fell back to the shared target.'
}
Write-Output 'PASS: explicit empty local selection refuses shared-target fallback'
foreach ($contents in @('', '{', (New-FixtureConnection 'shared-fixture.invalid' '5432'),
    (New-FixtureConnection '127.0.0.1' '0'), (New-FixtureConnection '127.0.0.1' '5432' $false))) {
    [IO.File]::WriteAllText($connectionFile, $contents)
    $invalid = Invoke-Runner -Arguments @('-LocalPostgresConnectionFile', $connectionFile)
    if ($invalid.Exit -eq 0 -or (Test-Path -LiteralPath $entry) -or -not $invalid.Output.Contains('LOCAL_POSTGRES_CONFIGURATION_REJECTED') -or
        $invalid.Output.Contains('fixture-only-sentinel')) { throw 'Invalid local selection entered a workload or exposed a fixture credential.' }
}
Write-Output 'PASS: invalid, shared-target and credential-incomplete selections fail before workload entry'
foreach ($alias in @('Server', 'Data Source', 'User ID', 'Options')) {
    $settings = [Data.Common.DbConnectionStringBuilder]::new()
    $settings.set_ConnectionString($local)
    $settings[$alias] = 'shared-fixture.invalid'
    [IO.File]::WriteAllText($connectionFile, $settings.get_ConnectionString())
    $invalid = Invoke-Runner -Arguments @('-LocalPostgresConnectionFile', $connectionFile)
    if ($invalid.Exit -eq 0 -or (Test-Path -LiteralPath $entry) -or -not $invalid.Output.Contains('LOCAL_POSTGRES_CONFIGURATION_REJECTED')) {
        throw 'An unvalidated provider alias or option reached the workload through the local connection seam.'
    }
}
Write-Output 'PASS: provider aliases and extra options refuse entry before workload starts'
$settings = [Data.Common.DbConnectionStringBuilder]::new()
$settings.set_ConnectionString($local)
$settings['Server'] = 'shared-fixture.invalid'
$settings['Count'] = 5
$settings['Keys'] = 'Host'
[IO.File]::WriteAllText($connectionFile, $settings.get_ConnectionString())
$invalid = Invoke-Runner -Arguments @('-LocalPostgresConnectionFile', $connectionFile)
if ($invalid.Exit -eq 0 -or (Test-Path -LiteralPath $entry) -or -not $invalid.Output.Contains('LOCAL_POSTGRES_CONFIGURATION_REJECTED')) {
    throw 'Dictionary member aliases hid extra connection keys from the local selection guard.'
}
Write-Output 'PASS: dictionary member aliases cannot hide extra provider keys'
[IO.File]::WriteAllText($connectionFile, $local)
foreach ($mode in @('-CiShard', '-Init')) {
    $arguments = @('-LocalPostgresConnectionFile', $connectionFile, $mode)
    if ($mode -eq '-CiShard') { $arguments += '0' }
    $invalid = Invoke-Runner -Arguments $arguments
    if ($invalid.Exit -eq 0 -or (Test-Path -LiteralPath $entry) -or -not $invalid.Output.Contains('LOCAL_POSTGRES_CONFIGURATION_REJECTED')) {
        throw 'Local connection selection was accepted outside the supported local-run seam.'
    }
}
Write-Output 'PASS: CI shards and skeleton initialization reject local overrides before any workload'
$runnerFailure = Invoke-Runner -Fail $true
if ($runnerFailure.Exit -ne 1 -or -not (Test-Path -LiteralPath $entry)) { throw 'The real runner failure positive control did not execute.' }
Write-Output 'PASS: original runner reports its nonzero workload as the documented failure exit'

if ($IsWindows) {
    $outside = Join-Path ([Environment]::GetFolderPath('ApplicationData')) ('nsn-pg-link-target-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($outside)
    $alias = Join-Path $scratch 'alias'
    [void](New-Item -ItemType Junction -Path $alias -Target $outside)
    try {
        $attempt = Invoke-Runner -ScriptName 'local-postgres.ps1' -Arguments @('-Action', 'Start', '-StateDirectory', (Join-Path $alias 'instance'), '-RuntimeDirectory', (Join-Path $scratch 'missing-runtime'))
        if ($attempt.Exit -eq 0 -or -not $attempt.Output.Contains('LOCAL_POSTGRES_REJECTED stage=directory') -or
            @(Get-ChildItem -LiteralPath $outside -Force).Count -ne 0) { throw 'A path alias wrote managed state outside the allowed physical roots.' }
        Write-Output 'PASS: reparse ancestors refuse initialization before modifying the outside target'
    }
    finally { [IO.Directory]::Delete($alias) } # Only the junction itself; keep the owned target for inspection.
} else {
    foreach ($action in @('Start', 'Status', 'Test', 'Stop')) {
        $statePath = Join-Path $scratch 'unsupported-instance'
        $attempt = Invoke-Runner -ScriptName 'local-postgres.ps1' -Arguments @('-Action', $action, '-StateDirectory', $statePath)
        if ($attempt.Exit -eq 0 -or -not $attempt.Output.Contains('LOCAL_POSTGRES_UNSUPPORTED') -or (Test-Path -LiteralPath $statePath)) { throw 'Unsupported lifecycle mutated state or claimed qualification.' }
    }
    Write-Output 'PASS: unsupported OS refuses all lifecycle actions before state mutation'
}

if ($RuntimeDirectory) {
    if (-not $IsWindows) { throw 'The real EDB lifecycle qualification requires Windows.' }
    $instance = Join-Path $scratch 'instance'
    $failWorkload = $false
    function Invoke-LocalCli([string]$Action) {
        $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('-NoProfile', '-File', (Join-Path $fixture 'scripts/local-postgres.ps1'), '-Action', $Action,
            '-RuntimeDirectory', $RuntimeDirectory, '-StateDirectory', $instance, '-NoBuild')) { $start.ArgumentList.Add($argument) }
        foreach ($key in @('TEMP', 'TMP', 'TMPDIR')) { $start.Environment[$key] = $temporary }
        $start.Environment['PATH'] = $adapter + [IO.Path]::PathSeparator + $env:PATH
        $start.Environment['NSN_LOCAL_PG_ENTRY'] = $entry
        $start.Environment['NSN_LOCAL_PG_FAIL'] = [string]$failWorkload
        $process = [Diagnostics.Process]::Start($start)
        try {
            $output = $process.StandardOutput.ReadToEndAsync()
            $errorOutput = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(90000)) { throw 'Owned PostgreSQL fixture exceeded its deadline; inspect before recovery.' }
            if (-not [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($output, $errorOutput)).Wait(5000)) { throw 'Owned CLI output did not drain.' }
            return @{ Exit = $process.ExitCode; Output = $output.Result + $errorOutput.Result }
        }
        finally { $process.Dispose() }
    }
    $started = Invoke-LocalCli 'Start'
    if ($started.Exit -ne 0) { throw 'The public CLI could not start a private owned PostgreSQL.' }
    try {
        $status = Invoke-LocalCli 'Status'
        if ($status.Exit -ne 0) { throw 'The public CLI could not validate its own running instance.' }
        $reported = $status.Output | ConvertFrom-Json
        if ($reported.State -cne 'running' -or $reported.Version -notmatch '^18\.\d+$' -or $reported.Port -le 0) { throw 'The instance was not proven ready.' }
        $ownerPath = Join-Path $instance 'owner.json'
        $originalOwner = [IO.File]::ReadAllText($ownerPath)
        $originalConnection = [IO.File]::ReadAllText((Join-Path $instance 'connection.private'))
        $initializedAt = (Get-Item -LiteralPath (Join-Path $instance 'initdb-command.private.log')).LastWriteTimeUtc.Ticks
        $reused = Invoke-LocalCli 'Start'
        if ($reused.Exit -ne 0 -or [IO.File]::ReadAllText($ownerPath) -cne $originalOwner -or
            (Get-Item -LiteralPath (Join-Path $instance 'initdb-command.private.log')).LastWriteTimeUtc.Ticks -ne $initializedAt) { throw 'Starting a running owned instance changed its identity or repeated initialization.' }
        Write-Output 'PASS: repeated Start retains the running identity and does not repeat initialization'
        foreach ($field in @('Pid', 'StartedUtcTicks')) {
            $corrupt = $originalOwner | ConvertFrom-Json
            $corrupt.$field = if ($field -eq 'Pid') { $PID } else { 0 }
            [IO.File]::WriteAllText($ownerPath, (ConvertTo-Json -InputObject $corrupt -Depth 5 -Compress))
            try {
                $refused = Invoke-LocalCli 'Stop'
                if ($refused.Exit -eq 0 -or -not $refused.Output.Contains('LOCAL_POSTGRES_REJECTED stage=identity')) { throw 'Wrong PID/start identity permitted instance stop.' }
            }
            finally { [IO.File]::WriteAllText($ownerPath, $originalOwner) }
            $stillRunning = Invoke-LocalCli 'Status'
            if ($stillRunning.Exit -ne 0 -or ($stillRunning.Output | ConvertFrom-Json).State -cne 'running') { throw 'Identity refusal altered the proven instance.' }
        }
        Write-Output 'PASS: PID and start-identity mismatch refuse stop and preserve the proven instance'
        $corrupt = $originalOwner | ConvertFrom-Json
        $corrupt.ArtifactHashes = $null
        [IO.File]::WriteAllText($ownerPath, (ConvertTo-Json -InputObject $corrupt -Depth 5 -Compress))
        try {
            $refused = Invoke-LocalCli 'Status'
            if ($refused.Exit -eq 0 -or -not $refused.Output.Contains('LOCAL_POSTGRES_REJECTED')) { throw 'Missing artifact evidence was treated as valid instance ownership.' }
        }
        finally { [IO.File]::WriteAllText($ownerPath, $originalOwner) }
        Write-Output 'PASS: missing artifact evidence refuses qualification instead of skipping validation'
        if (Test-Path -LiteralPath $entry) { Remove-Item -LiteralPath $entry }
        $tested = Invoke-LocalCli 'Test'
        if ($tested.Exit -ne 0 -or -not (Test-Path -LiteralPath $entry)) { throw 'The tool did not route its real runner CLI to its owned PostgreSQL.' }
        $observed = Get-Content -LiteralPath $entry -Raw | ConvertFrom-Json
        if ($observed.Postgres -cne [IO.File]::ReadAllText((Join-Path $instance 'connection.private')) -or
            $observed.Redis -cne 'redis-fixture.invalid' -or [IO.File]::ReadAllText($baseFile) -cne $base) { throw 'Owned test selection changed or overwrote the original test configuration.' }
        Write-Output 'PASS: Test invokes the original serial runner against the owned instance without replacing its other configuration'
        $failWorkload = $true
        try {
            $failed = Invoke-LocalCli 'Test'
            if ($failed.Exit -ne $runnerFailure.Exit) { throw 'The tool hid the real runner failure exit code.' }
        }
        finally { $failWorkload = $false }
        $stillRunning = Invoke-LocalCli 'Status'
        if ($stillRunning.Exit -ne 0 -or ($stillRunning.Output | ConvertFrom-Json).State -cne 'running') { throw 'A normally returned test failure damaged the reusable instance.' }
        Write-Output 'PASS: Test preserves the original failure exit code and leaves its proven instance reusable'
        $stopped = Invoke-LocalCli 'Stop'
        if ($stopped.Exit -ne 0) { throw 'The public CLI could not stop its own proven instance.' }
        $status = Invoke-LocalCli 'Status'
        if ($status.Exit -ne 0 -or ($status.Output | ConvertFrom-Json).State -cne 'stopped') { throw 'Normal stop was not retained for safe reuse.' }
        $restarted = Invoke-LocalCli 'Start'
        $status = Invoke-LocalCli 'Status'
        if ($restarted.Exit -ne 0 -or $status.Exit -ne 0 -or ($status.Output | ConvertFrom-Json).State -cne 'running' -or
            ($status.Output | ConvertFrom-Json).Port -ne $reported.Port -or
            [IO.File]::ReadAllText((Join-Path $instance 'connection.private')) -cne $originalConnection -or
            (Get-Item -LiteralPath (Join-Path $instance 'initdb-command.private.log')).LastWriteTimeUtc.Ticks -ne $initializedAt) { throw 'Restart did not reuse its retained cluster, port and private connection.' }
        $stopped = Invoke-LocalCli 'Stop'
        if ($stopped.Exit -ne 0) { throw 'The reused instance could not be cleanly stopped.' }
        Write-Output 'PASS: stopped instance restarts with the same private connection and no repeated initialization'
        $configurationPath = Join-Path $instance 'data/postgresql.conf'
        $configuration = [IO.File]::ReadAllText($configurationPath)
        try {
            # Harmless drift exercises the refusal without weakening durability or opening a listener.
            [IO.File]::AppendAllText($configurationPath, "`nlog_min_messages = 'warning'`n")
            $drifted = Invoke-LocalCli 'Start'
            if ($drifted.Exit -eq 0 -or -not $drifted.Output.Contains('LOCAL_POSTGRES_REJECTED stage=cluster-drift') -or
                (Test-Path -LiteralPath (Join-Path $instance 'data/postmaster.pid'))) { throw 'Managed configuration drift admitted a server before validation.' }
        }
        finally { [IO.File]::WriteAllText($configurationPath, $configuration, [Text.UTF8Encoding]::new($false)) }
        Write-Output 'PASS: managed configuration drift is rejected before starting a listener'
        Write-Output 'PASS: real CLI starts, verifies and cleanly stops only its private PostgreSQL'
    }
    finally {
        # A failure remains visible; the tool must prove ownership before any attempted fixture stop.
        $cleanup = Invoke-LocalCli 'Stop'
        if ($cleanup.Exit -ne 0) { Write-Output 'Fixture cleanup refused; retain its state for inspection.' }
    }
} else {
    Write-Output 'INFO: real Windows PostgreSQL lifecycle qualification requires -RuntimeDirectory; portable selection probes above do not prove it.'
}
