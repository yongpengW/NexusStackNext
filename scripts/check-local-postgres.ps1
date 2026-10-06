# Verify shared configuration and rejection of retired local entry points through real CLI boundaries.
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
foreach ($name in @('run-tests.ps1', 'test-ownership.psm1', 'test-console.psm1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $fixture "scripts/$name")
}
foreach ($name in @('local-postgres.ps1', 'local-postgres.psm1')) {
    $path = Join-Path $PSScriptRoot $name
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $fixture "scripts/$name") }
}
[IO.File]::WriteAllText((Join-Path $fixture 'tests/Probe.Tests/Probe.Tests.csproj'), '<Project />')
$base = "NEXUSSTACK_TEST_POSTGRES=Host=shared-fixture.invalid`nNEXUSSTACK_TEST_REDIS=redis-fixture.invalid`nNEXUSSTACK_TEST_RABBITMQ=rabbitmq-fixture.invalid`n"
$baseFile = Join-Path $fixture 'env/test.dev'
[IO.File]::WriteAllText($baseFile, $base)
$entry = Join-Path $scratch 'entered.json'
$fake = @'
$ErrorActionPreference = 'Stop'
[IO.File]::WriteAllText($env:NSN_LOCAL_PG_ENTRY, (@{ Postgres = $env:NEXUSSTACK_TEST_POSTGRES; Redis = $env:NEXUSSTACK_TEST_REDIS; RabbitMQ = $env:NEXUSSTACK_TEST_RABBITMQ } | ConvertTo-Json -Compress))
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
function Invoke-Runner([string[]]$Arguments = @(), [bool]$Fail = $false, [string]$ScriptName = 'run-tests.ps1', [bool]$SkipBuild = $true) {
    if (Test-Path -LiteralPath $entry) { Remove-Item -LiteralPath $entry }
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $prefix = @('-NoProfile', '-File', (Join-Path $fixture "scripts/$ScriptName"))
    if ($SkipBuild) { $prefix += '-NoBuild' }
    foreach ($argument in $prefix + $Arguments) { $start.ArgumentList.Add($argument) }
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
if ($positive.Exit -ne 0 -or -not (Test-Path -LiteralPath $entry)) { throw 'The shared-configuration positive control did not enter.' }
$selected = Get-Content -LiteralPath $entry -Raw | ConvertFrom-Json
if ($selected.Postgres -cne 'Host=shared-fixture.invalid' -or $selected.Redis -cne 'redis-fixture.invalid' -or $selected.RabbitMQ -cne 'rabbitmq-fixture.invalid' -or
    [IO.File]::ReadAllText($baseFile) -cne $base) { throw 'Shared configuration or other test settings changed.' }
Write-Output 'PASS: shared configuration is passed unchanged to the workload'
$connectionFile = Join-Path $scratch 'connection.private'
[IO.File]::WriteAllText($connectionFile, 'Host=127.0.0.1;Port=61599;Database=postgres;Username=your-fixture;Password=your-fixture-only-sentinel')
foreach ($value in @($connectionFile, '', (Join-Path $scratch 'missing.private'))) {
    $rejected = Invoke-Runner -Arguments @('-LocalPostgresConnectionFile', $value) -SkipBuild $false
    if ($rejected.Exit -eq 0 -or (Test-Path -LiteralPath $entry) -or
        -not $rejected.Output.Contains('LOCAL_POSTGRES_CONFIGURATION_REJECTED') -or
        $rejected.Output.Contains('fixture-only-sentinel')) { throw 'A retired override entered the workload or exposed a credential.' }
}
Write-Output 'PASS: valid, empty and missing local override files reject before workload entry'
foreach ($mode in @('-CiShard', '-Init')) {
    $arguments = @('-LocalPostgresConnectionFile', $connectionFile, $mode)
    if ($mode -eq '-CiShard') { $arguments += '0' }
    $rejected = Invoke-Runner -Arguments $arguments
    if ($rejected.Exit -eq 0 -or (Test-Path -LiteralPath $entry) -or
        -not $rejected.Output.Contains('LOCAL_POSTGRES_CONFIGURATION_REJECTED')) { throw 'A retired override reached initialization or CI.' }
}
Write-Output 'PASS: retired local overrides cannot enter CI or rewrite configuration'
$moduleProbe = @"
param([switch]`$NoBuild, [string]`$Action, [string]`$StateDirectory)
Import-Module (Join-Path `$PSScriptRoot 'local-postgres.psm1') -Force
Invoke-LocalPostgres -Action `$Action -StateDirectory `$StateDirectory
"@
[IO.File]::WriteAllText((Join-Path $fixture 'scripts/module-probe.ps1'), $moduleProbe)
foreach ($action in @('Start', 'Test')) {
    foreach ($scriptName in @('local-postgres.ps1', 'module-probe.ps1')) {
        $rejected = Invoke-Runner -Arguments @('-Action', $action, '-StateDirectory', '') -ScriptName $scriptName
        if ($rejected.Exit -eq 0 -or (Test-Path -LiteralPath $entry) -or
            -not $rejected.Output.Contains('LOCAL_POSTGRES_DISABLED')) { throw 'A retired local lifecycle entry was accepted or resolved the invalid instance path.' }
    }
}
Write-Output 'PASS: CLI and module Start/Test reject before resolving an invalid instance path'
[IO.File]::WriteAllText($baseFile, 'NEXUSSTACK_TEST_REDIS=redis-fixture.invalid')
$missing = Invoke-Runner
if ($missing.Exit -eq 0 -or (Test-Path -LiteralPath $entry)) { throw 'Missing shared PostgreSQL configuration entered a workload.' }
[IO.File]::WriteAllText($baseFile, $base)
$runnerFailure = Invoke-Runner -Fail $true
if ($runnerFailure.Exit -ne 1 -or -not (Test-Path -LiteralPath $entry)) { throw 'The workload-failure positive control did not execute.' }
Write-Output 'PASS: missing shared configuration fails and workload failures remain nonzero'
if ([IO.File]::ReadAllText($baseFile) -cne $base) { throw 'The shared configuration file was changed.' }
# Local connection parsing and Windows Start/Test lifecycle probes are retired:
# those operations are now forbidden. Status/Stop keep their existing ownership checks.
if ($IsWindows) {
    # A harmless executable supplies a process identity; no PostgreSQL is started.
    $instance = Join-Path $scratch 'identity-instance'
    $runtime = Join-Path $scratch 'identity-runtime'
    $data = Join-Path $instance 'data'
    foreach ($directory in @($instance, $runtime, $data)) { [void][IO.Directory]::CreateDirectory($directory) }
    Set-Acl -LiteralPath $instance -AclObject $security
    $hashes = [ordered]@{}
    foreach ($name in @('postgres', 'initdb', 'pg_ctl', 'psql')) {
        $path = Join-Path $runtime "$name.exe"
        Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32/ping.exe') -Destination $path
        $hashes[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    $artifacts = [ordered]@{}
    foreach ($relative in @('data/postgresql.conf', 'data/postgresql.auto.conf', 'data/pg_hba.conf', 'password.private', 'pgpass.private', 'connection.private')) {
        $path = Join-Path $instance $relative
        [IO.File]::WriteAllText($path, 'identity-fixture')
        $artifacts[$relative] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    $process = Start-Process -FilePath (Join-Path $runtime 'postgres.exe') -ArgumentList @('-t', '127.0.0.1') -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $instance 'process.log') -RedirectStandardError (Join-Path $instance 'process-error.log')
    try {
        $ownerPath = Join-Path $instance 'owner.json'
        $state = [ordered]@{ Version = 1; State = 'running'; DataDirectory = $data; RuntimeDirectory = $runtime; Role = 'nsn_test_0123456789abcdef'; Port = 61599; RuntimeVersion = '18.6'; Hashes = $hashes; ArtifactHashes = $artifacts; Pid = $process.Id; StartedUtcTicks = $process.StartTime.ToUniversalTime().Ticks }
        [IO.File]::WriteAllLines((Join-Path $data 'postmaster.pid'), @([string]$process.Id, $data, '0', '61599', '', '127.0.0.1', '', 'ready'))
        foreach ($field in @('Pid', 'StartedUtcTicks')) {
            $original = $state[$field]
            $state[$field] = 0
            [IO.File]::WriteAllText($ownerPath, (ConvertTo-Json -InputObject $state -Depth 5 -Compress))
            $before = (Get-FileHash -LiteralPath $ownerPath -Algorithm SHA256).Hash
            $refused = Invoke-Runner -ScriptName 'local-postgres.ps1' -Arguments @('-Action', 'Stop', '-StateDirectory', $instance)
            if ($refused.Exit -eq 0 -or -not $refused.Output.Contains('LOCAL_POSTGRES_REJECTED stage=identity') -or $process.HasExited -or
                (Get-FileHash -LiteralPath $ownerPath -Algorithm SHA256).Hash -cne $before -or
                (Test-Path -LiteralPath (Join-Path $instance 'pg_ctl-command.private.log'))) { throw 'Stop accepted a mismatched identity or altered the fixture.' }
            $state[$field] = $original
        }
        Write-Output 'PASS: Stop rejects PID and start-time mismatches without stopping or changing the fixture'
        $state.ArtifactHashes = $null
        [IO.File]::WriteAllText($ownerPath, (ConvertTo-Json -InputObject $state -Depth 5 -Compress))
        $refused = Invoke-Runner -ScriptName 'local-postgres.ps1' -Arguments @('-Action', 'Status', '-StateDirectory', $instance)
        if ($refused.Exit -eq 0 -or -not $refused.Output.Contains('LOCAL_POSTGRES_REJECTED stage=state') -or $process.HasExited) { throw 'Status accepted missing artifact evidence.' }
        Write-Output 'PASS: Status rejects missing artifact evidence'
    }
    finally { if (-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }; $process.Dispose() }
} else {
    foreach ($action in @('Status', 'Stop')) {
        $statePath = Join-Path $scratch 'unsupported-instance'
        $refused = Invoke-Runner -ScriptName 'local-postgres.ps1' -Arguments @('-Action', $action, '-StateDirectory', $statePath)
        if ($refused.Exit -eq 0 -or -not $refused.Output.Contains('LOCAL_POSTGRES_UNSUPPORTED') -or (Test-Path -LiteralPath $statePath)) { throw 'Unsupported management created state or claimed qualification.' }
    }
    Write-Output 'PASS: unsupported OS rejects Status/Stop without creating instance state'
}
$resolvedScratch = [IO.Path]::GetFullPath($scratch)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedScratch.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($resolvedScratch) -notmatch '^nsn-local-postgres-probe-[a-f0-9]{32}$') { throw 'Unexpected probe cleanup target' }
Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
exit 0
