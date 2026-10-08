# Public CLI regression probes; no database, broker or private configuration is used.
$ErrorActionPreference = 'Stop'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('nsn-ci-probes-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
$tool = Join-Path $PSScriptRoot 'ci-test-tools.ps1'
function Invoke-Probe([string[]]$Arguments, [bool]$Success) {
    $output = & pwsh -NoProfile -File $tool @Arguments 2>&1
    if (($LASTEXITCODE -eq 0) -ne $Success) { throw "Unexpected CLI exit for $($Arguments[0]); expected success=$Success" }
}
function Write-Json($Value, [string]$Path) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 15), [Text.UTF8Encoding]::new($false))
}
function Assert-IsolationRejection([string]$Stage) {
    $output = & pwsh -NoProfile -File $tool -Action Isolation -InputPath $scratch 2>&1
    if ($LASTEXITCODE -eq 0 -or ($output -join "`n") -notmatch "CI_ISOLATION_REJECTED stage=$Stage") { throw "Expected rejection at $Stage, before contacting dependencies" }
}
Invoke-Probe @('-Action', 'Prerequisites', '-ShardResult', 'success', '-RepositoryResult', 'success') $true
Write-Output 'PASS: successful test and repository prerequisites admit the final gate'
foreach ($state in @('failure', 'cancelled', 'skipped', '', 'queued', 'SUCCESS')) {
    Invoke-Probe @('-Action', 'Prerequisites', '-ShardResult', $state, '-RepositoryResult', 'success') $false
    Invoke-Probe @('-Action', 'Prerequisites', '-ShardResult', 'success', '-RepositoryResult', $state) $false
}
Invoke-Probe @('-Action', 'Prerequisites', '-ShardResult', 'success') $false
Invoke-Probe @('-Action', 'Prerequisites', '-RepositoryResult', 'success') $false
Write-Output 'PASS: either failed, cancelled, skipped, missing or unknown prerequisite rejects the final gate'
$inventoryPath = Join-Path $scratch 'inventory.json'
$planPath = Join-Path $scratch 'plan.json'
$inventory = @(
    @{ Project = 'HostIntegration.Tests'; Method = 'Example.AlphaTests.First'; Id = ('a' * 64) },
    @{ Project = 'HostIntegration.Tests'; Method = 'Example.AlphaTests.Second'; Id = ('b' * 64) },
    @{ Project = 'HostIntegration.Tests'; Method = 'Example.BetaTests.First'; Id = ('c' * 64) },
    @{ Project = 'HostIntegration.Tests'; Method = 'Example.GammaTests.First'; Id = ('d' * 64) },
    @{ Project = 'Unit.Tests'; Method = 'Example.UnitTests.First'; Id = ('e' * 64) }
)
# These public probes use a complete declared partition inventory as well as the synthetic classes.
$partitionDeclaration = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ci-test-partitions.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($methods in $partitionDeclaration.Values) {
    foreach ($method in $methods.Keys) {
        $inventory += @{ Project = 'HostIntegration.Tests'; Method = $method; Id = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($method))) }
    }
}
Write-Json $inventory $inventoryPath
Invoke-Probe @('-Action', 'Plan', '-InputPath', $inventoryPath, '-OutputPath', $planPath) $true
$plan = @(Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json)
if ($plan.Count -ne $inventory.Count -or @($plan | Where-Object Project -CNE 'HostIntegration.Tests' | Where-Object Shard -EQ 0).Count -ne 1 -or
    @($plan | Where-Object Project -CNE 'HostIntegration.Tests' | Where-Object Shard -NE 0).Count -ne 0) { throw 'Partition lost or misplaced tests' }
if (@($plan | Where-Object Method -Like 'Example.AlphaTests.*' | Select-Object -ExpandProperty Shard -Unique).Count -ne 1) { throw 'A class was split' }
if (@($plan | Select-Object -ExpandProperty Shard -Unique).Count -ne 4) { throw 'Empty partition' }
Write-Output 'PASS: all tests partitioned once, undeclared class kept together'
$reportsPath = Join-Path $scratch 'reports'
[void][IO.Directory]::CreateDirectory($reportsPath)
foreach ($shard in 0..3) {
    $report = @{ Shard = $shard; Revision = ('1' * 40); RunId = '10'; Attempt = '1'; Plan = $plan; Results = @($plan | Where-Object Shard -EQ $shard | ForEach-Object {
        @{ Project = $_.Project; Method = $_.Method; Id = $_.Id; Outcome = 'Passed';
            Seconds = $(if ($_.Method -ceq 'Example.AlphaTests.First') { 12.5 } else { 0.1 }) }
    }) }
    Write-Json $report (Join-Path $reportsPath "shard-$shard.json")
}
$verify = @('-Action', 'Verify', '-InputPath', $reportsPath, '-Revision', ('1' * 40), '-RunId', '10', '-Attempt', '1')
Invoke-Probe $verify $true
$timingOutput = & pwsh -NoProfile -File $tool @verify 2>&1
if ($LASTEXITCODE -ne 0 -or ($timingOutput -join "`n") -notmatch 'Slowest test cases' -or
    ($timingOutput -join "`n") -notmatch 'Example.AlphaTests.First' -or
    ($timingOutput -join "`n") -notmatch 'Shard 0: \d+ tests') { throw 'Missing individual test and shard timing diagnostics' }
Write-Output 'PASS: complete reports accepted'
$reportPath = Join-Path $reportsPath 'shard-1.json'
$original = Get-Content -LiteralPath $reportPath -Raw
foreach ($mutation in @('missing', 'duplicate', 'skipped', 'failed', 'stale', 'old-run', 'old-attempt', 'wrong-shard', 'wrong-plan', 'foreign-test')) {
    $report = $original | ConvertFrom-Json
    switch ($mutation) {
        'missing' { $report.Results = @() }
        'duplicate' { $report.Results = @($report.Results) + @($report.Results[0]) }
        'skipped' { $report.Results[0].Outcome = 'NotExecuted' }
        'failed' { $report.Results[0].Outcome = 'Failed' }
        'stale' { $report.Revision = '2' * 40 }
        'old-run' { $report.RunId = '9' }
        'old-attempt' { $report.Attempt = '2' }
        'wrong-shard' { $report.Shard = 0 }
        'wrong-plan' { $report.Plan[0].Shard = 9 }
        'foreign-test' { $report.Results[0].Id = 'f' * 64 }
    }
    Write-Json $report $reportPath
    Invoke-Probe $verify $false
}
[IO.File]::WriteAllText($reportPath, $original)
Move-Item -LiteralPath $reportPath -Destination (Join-Path $scratch 'saved.json')
Invoke-Probe $verify $false
Move-Item -LiteralPath (Join-Path $scratch 'saved.json') -Destination $reportPath
Invoke-Probe $verify $true
foreach ($badInventory in @(@(), @($inventory[0], $inventory[0]))) {
    Write-Json $badInventory $inventoryPath
    Invoke-Probe @('-Action', 'Plan', '-InputPath', $inventoryPath, '-OutputPath', $planPath) $false
}
Write-Output 'PASS: empty/duplicate discovery and missing/duplicate/skipped/failed/stale reports rejected'

$oldCi = $env:GITHUB_ACTIONS
try {
    $env:GITHUB_ACTIONS = 'false'
    $output = & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'run-tests.ps1') -CiShard 0 2>&1
    if ($LASTEXITCODE -eq 0 -or ($output -join "`n") -notmatch 'CI_ISOLATION_REJECTED') { throw 'Local sharding was not safely rejected before reading private configuration' }
}
finally { $env:GITHUB_ACTIONS = $oldCi }
Write-Output 'PASS: local shard invocation rejected before private configuration is read'
$savedEnvironment = @{}
foreach ($key in @('GITHUB_ACTIONS', 'RUNNER_ENVIRONMENT', 'NEXUSSTACK_TEST_POSTGRES', 'NEXUSSTACK_TEST_REDIS', 'NEXUSSTACK_TEST_RABBITMQ', 'NSN_CI_POSTGRES_CONTAINER')) {
    $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key)
}
try {
    $env:GITHUB_ACTIONS = 'true'
    $env:RUNNER_ENVIRONMENT = 'github-hosted'
    $env:NSN_CI_POSTGRES_CONTAINER = $null
    $connection = [Data.Common.DbConnectionStringBuilder]::new()
    $connection['Host'] = 'example.invalid'
    $connection['Port'] = 5432
    $connection['Database'] = 'postgres'
    $connection['Username'] = 'postgres'
    $env:NEXUSSTACK_TEST_POSTGRES = $connection.ConnectionString
    $env:NEXUSSTACK_TEST_REDIS = '127.0.0.1:6379'
    $env:NEXUSSTACK_TEST_RABBITMQ = @{ HostName = '127.0.0.1'; Port = 5672; UserName = 'nsn-ci'; Password = 'probe-only'; VirtualHost = '/' } | ConvertTo-Json -Compress
    Assert-IsolationRejection 'postgres'
    $connection['Host'] = '127.0.0.1'
    # A dictionary key named Count must not hide provider aliases from the guard.
    $connection['Server'] = 'example.invalid'
    $connection['Count'] = 4
    $env:NEXUSSTACK_TEST_POSTGRES = $connection.get_ConnectionString()
    Assert-IsolationRejection 'postgres'
    [void]$connection.Remove('Server')
    [void]$connection.Remove('Count')
    $env:NEXUSSTACK_TEST_POSTGRES = $connection.ConnectionString
    $env:NEXUSSTACK_TEST_REDIS = 'example.invalid:6379'
    Assert-IsolationRejection 'redis'
    $env:NEXUSSTACK_TEST_REDIS = '127.0.0.1:6379'
    $env:NEXUSSTACK_TEST_RABBITMQ = $env:NEXUSSTACK_TEST_RABBITMQ.Replace('127.0.0.1', 'example.invalid')
    Assert-IsolationRejection 'rabbitmq'
    [void][IO.Directory]::CreateDirectory((Join-Path $scratch 'env'))
    [IO.File]::WriteAllText((Join-Path $scratch 'env/test.dev'), 'PRIVATE_CONFIGURATION_PROBE')
    Assert-IsolationRejection 'private-config'
}
finally { foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key]) } }
Write-Output 'PASS: remote dependency configurations and private configuration rejected'

$trxPath = Join-Path $scratch 'probe.trx'
$safePath = Join-Path $scratch 'safe.json'
[IO.File]::WriteAllText($trxPath, '<TestRun><Results><UnitTestResult testName="Example.AlphaTests.First(value: &quot;DO_NOT_PUBLISH_ARGUMENT&quot;)" outcome="Passed" duration="00:00:01.2500000" /></Results></TestRun>')
Invoke-Probe @('-Action', 'Report', '-InputPath', $trxPath, '-Project', 'Unit.Tests', '-OutputPath', $safePath) $true
$safe = Get-Content -LiteralPath $safePath -Raw
$row = @($safe | ConvertFrom-Json)[0]
if ($safe.Contains('DO_NOT_PUBLISH_ARGUMENT') -or $row.Method -cne 'Example.AlphaTests.First' -or $row.Seconds -ne 1.25 -or $row.Id -notmatch '^[a-f0-9]{64}$') { throw 'Report sanitization failed' }
[IO.File]::WriteAllText($trxPath, '<TestRun><Results /></TestRun>')
Invoke-Probe @('-Action', 'Report', '-InputPath', $trxPath, '-Project', 'Unit.Tests', '-OutputPath', $safePath) $false
Write-Output 'PASS: report arguments redacted and empty TRX rejected'

& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'check-ci-partitions.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Method partition regression probes failed.' }
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'check-ci-runner.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Public CI runner regression probes failed.' }

# Delete only this probe's freshly created, resolved temporary directory.
$resolvedScratch = [IO.Path]::GetFullPath($scratch)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedScratch.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($resolvedScratch) -notmatch '^nsn-ci-probes-[a-f0-9]{32}$') { throw 'Unexpected probe cleanup target' }
Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
# GitHub's pwsh wrapper propagates LASTEXITCODE; expected failing probes must not leak their exit code.
exit 0
