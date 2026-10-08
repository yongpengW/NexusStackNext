# Public runner probes use external CLI adapters; no service or private configuration is contacted.
$ErrorActionPreference = 'Stop'
$lab = Join-Path ([IO.Path]::GetTempPath()) ('nsn-ci-runner-probes-' + [guid]::NewGuid().ToString('N'))
$fixture = Join-Path $lab 'repo'
$adapter = Join-Path $lab 'adapter'
$temporary = Join-Path $lab 'temporary'
$reports = Join-Path $lab 'reports'
foreach ($directory in @((Join-Path $fixture 'scripts'), $adapter, $temporary, $reports)) {
    [void][IO.Directory]::CreateDirectory($directory)
}
foreach ($name in @('run-tests.ps1', 'test-ownership.psm1', 'test-console.psm1', 'ci-test-tools.ps1',
    'ci-test-support.psm1', 'ci-test-weights.json', 'ci-test-partitions.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $fixture "scripts/$name")
}
foreach ($name in @('HostIntegration.Tests', 'Unit.Tests')) {
    $directory = Join-Path $fixture "tests/$name"
    [void][IO.Directory]::CreateDirectory($directory)
    [IO.File]::WriteAllText((Join-Path $directory "$name.csproj"), '<Project />')
}
$declaration = Get-Content -LiteralPath (Join-Path $fixture 'scripts/ci-test-partitions.json') -Raw | ConvertFrom-Json -AsHashtable
$cases = @($declaration.Values | ForEach-Object { $_.Keys } | Sort-Object | ForEach-Object {
    @{ Project = 'HostIntegration.Tests'; DisplayName = $_ }
})
foreach ($method in @('Example.AlphaTests.First', 'Example.AlphaTests.Second', 'Example.BetaTests.First', 'Example.GammaTests.First')) {
    $cases += @{ Project = 'HostIntegration.Tests'; DisplayName = $method }
}
$cases += @{ Project = 'HostIntegration.Tests'; DisplayName = 'Example.AlphaTests.Theory(value: "DO_NOT_PUBLISH_ARGUMENT")' }
$cases += @{ Project = 'HostIntegration.Tests'; DisplayName = 'Example.AlphaTests.Theory(value: "another")' }
$cases += @{ Project = 'Unit.Tests'; DisplayName = 'Example.UnitTests.First' }
$casePath = Join-Path $lab 'cases.json'
[IO.File]::WriteAllText($casePath, (ConvertTo-Json -InputObject $cases -Depth 5), [Text.UTF8Encoding]::new($false))

function Write-Adapter([string]$Name, [string]$Source) {
    $script = Join-Path $adapter "$Name-adapter.ps1"
    [IO.File]::WriteAllText($script, $Source, [Text.UTF8Encoding]::new($false))
    if ($IsWindows) {
        $escaped = $script.Replace("'", "''")
        [IO.File]::WriteAllText((Join-Path $adapter "$Name.ps1"), "& '$escaped' @args`nexit `$LASTEXITCODE`n")
    } else {
        $quote = "'`"'`"'"
        $shell = (Get-Command pwsh).Source.Replace("'", $quote)
        $escaped = $script.Replace("'", $quote)
        [IO.File]::WriteAllText((Join-Path $adapter $Name), "#!/bin/sh`nexec '$shell' -NoProfile -File '$escaped' `"`$@`"`n")
        & chmod +x (Join-Path $adapter $Name)
        if ($LASTEXITCODE -ne 0) { throw 'Could not enable the isolated CLI adapter.' }
    }
}
Write-Adapter 'docker' @'
if ($args[0] -cne 'inspect') { exit 1 }
$ports = @{}
foreach ($port in @('5432', '6379', '5672')) { $ports[$port + '/tcp'] = @(@{ HostIp = '127.0.0.1'; HostPort = $port }) }
@{ running = $true; health = 'healthy'; ports = $ports } | ConvertTo-Json -Depth 5 -Compress
exit 0
'@
Write-Adapter 'dotnet' @'
$ErrorActionPreference = 'Stop'
if ($args[0] -cne 'test') { throw 'Unexpected adapter command.' }
$project = [IO.Path]::GetFileNameWithoutExtension($args[1])
$cases = @(Get-Content $env:NSN_CI_PROBE_CASES -Raw | ConvertFrom-Json | Where-Object Project -CEQ $project)
if ('--list-tests' -in $args) {
    foreach ($case in $cases) { '    ' + $case.DisplayName }
    exit 0
}
$filterIndex = [array]::IndexOf($args, '--filter')
if ($filterIndex -ge 0) {
    $methods = @($args[$filterIndex + 1] -split '\|' | ForEach-Object {
        if (-not $_.StartsWith('FullyQualifiedName=', [StringComparison]::Ordinal)) { throw 'Unexpected method filter.' }
        $_.Substring('FullyQualifiedName='.Length)
    })
    $cases = @($cases | Where-Object { ($_.DisplayName -split '\(')[0] -cin $methods })
}
if ($cases.Count -eq 0) { throw 'Adapter received an empty selection.' }
$directoryIndex = [array]::IndexOf($args, '--results-directory')
$path = Join-Path $args[$directoryIndex + 1] ($project + '.trx')
$rows = $cases | ForEach-Object {
    '<UnitTestResult testName="' + [Security.SecurityElement]::Escape($_.DisplayName) + '" outcome="Passed" duration="00:00:00.1000000" />'
}
$count = $cases.Count
[IO.File]::WriteAllText($path, '<TestRun><Results>' + ($rows -join '') + '</Results><ResultSummary><Counters total="' + $count + '" executed="' + $count + '" passed="' + $count + '" failed="0" notExecuted="0" /></ResultSummary></TestRun>')
exit 0
'@

foreach ($shard in 0..3) {
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', (Join-Path $fixture 'scripts/run-tests.ps1'),
        '-NoBuild', '-CiShard', [string]$shard, '-ReportDirectory', $reports)) { $start.ArgumentList.Add($argument) }
    foreach ($key in @('TEMP', 'TMP', 'TMPDIR')) { $start.Environment[$key] = $temporary }
    $start.Environment['PATH'] = $adapter + [IO.Path]::PathSeparator + $env:PATH
    $start.Environment['NSN_CI_PROBE_CASES'] = $casePath
    $start.Environment['GITHUB_ACTIONS'] = 'true'
    $start.Environment['RUNNER_ENVIRONMENT'] = 'github-hosted'
    $start.Environment['GITHUB_SHA'] = '1' * 40
    $start.Environment['GITHUB_RUN_ID'] = '10'
    $start.Environment['GITHUB_RUN_ATTEMPT'] = '1'
    $connection = [Data.Common.DbConnectionStringBuilder]::new()
    foreach ($pair in @(@('Host', '127.0.0.1'), @('Port', '5432'), @('Database', 'postgres'), @('Username', 'postgres'))) {
        $connection[$pair[0]] = $pair[1]
    }
    $start.Environment['NEXUSSTACK_TEST_POSTGRES'] = $connection.ConnectionString
    $start.Environment['NEXUSSTACK_TEST_REDIS'] = '127.0.0.1:6379'
    $start.Environment['NEXUSSTACK_TEST_RABBITMQ'] = @{ HostName = '127.0.0.1'; Port = 5672; UserName = 'nsn-ci'; Password = 'probe-only'; VirtualHost = '/' } | ConvertTo-Json -Compress
    foreach ($key in @('NSN_CI_POSTGRES_CONTAINER', 'NSN_CI_REDIS_CONTAINER', 'NSN_CI_RABBIT_CONTAINER')) { $start.Environment[$key] = 'a' * 12 }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill($true)
            throw 'CI runner probe exceeded its deadline.'
        }
        $drain = [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($stdout, $stderr))
        if (-not $drain.Wait(5000)) { throw 'CI runner probe output did not close within its deadline.' }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $lab "shard-$shard.private.log"), $output, [Text.UTF8Encoding]::new($false))
        if ($process.ExitCode -ne 0 -or $output.Contains('DO_NOT_PUBLISH_ARGUMENT')) { throw 'CI runner probe failed or exposed arguments.' }
    }
    finally { $process.Dispose() }
    $report = Get-Content -LiteralPath (Join-Path $reports "shard-$shard.json") -Raw | ConvertFrom-Json
    $expected = [Collections.Generic.HashSet[string]]::new([string[]]@($report.Plan | Where-Object Shard -EQ $shard | Select-Object -ExpandProperty Id), [StringComparer]::Ordinal)
    $actual = [Collections.Generic.HashSet[string]]::new([string[]]$report.Results.Id, [StringComparer]::Ordinal)
    if ($report.Results.Count -ne $expected.Count -or -not $actual.SetEquals($expected)) { throw 'CI runner executed cases outside its assigned shard.' }
    $owner = Get-Content -LiteralPath (Join-Path $temporary 'nexusstack-run-tests.lock') -Raw | ConvertFrom-Json
    if ($owner.State -cne 'idle') { throw 'CI runner probe did not release workload ownership.' }
}
& pwsh -NoProfile -File (Join-Path $fixture 'scripts/ci-test-tools.ps1') -Action Verify -InputPath $reports -Revision ('1' * 40) -RunId '10' -Attempt '1'
if ($LASTEXITCODE -ne 0) { throw 'Public runner reports did not pass the complete four-shard gate.' }
Write-Output 'PASS: all four public CI runners execute exactly their assigned cases and complete the unified report gate.'

$resolved = [IO.Path]::GetFullPath($lab)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($resolved) -notmatch '^nsn-ci-runner-probes-[a-f0-9]{32}$') { throw 'Unexpected CI probe cleanup target.' }
Remove-Item -LiteralPath $resolved -Recurse -Force
exit 0
