Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-TestIdentity([string]$Project, [string]$DisplayName) {
    $match = [regex]::Match($DisplayName, '^(?<method>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*){2,})(?:\(|$)')
    if (-not $match.Success) { throw 'Unsupported test identity; refusing incomplete discovery.' }
    [pscustomobject]@{
        Project = $Project
        Method = $match.Groups['method'].Value
        Id = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Project + ':' + $DisplayName)))
    }
}

function Assert-TestInventory([object[]]$Inventory) {
    if ($Inventory.Count -eq 0) { throw 'Empty test inventory.' }
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($test in $Inventory) {
        if ($test.Project -notmatch '^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*$' -or
            $test.Method -notmatch '^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*){2,}$' -or
            $test.Id -cnotmatch '^[a-f0-9]{64}$' -or -not $ids.Add($test.Id)) { throw 'Invalid or duplicate test identity.' }
    }
}

function Get-DiscoveredTests([string]$ProjectPath, [string]$Configuration) {
    $project = [IO.Path]::GetFileNameWithoutExtension($ProjectPath)
    $output = & dotnet test $ProjectPath --configuration $Configuration --no-build --no-restore --list-tests --nologo 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Discovery failed for $project; raw output withheld." }
    $inventory = @($output | Where-Object { [string]$_ -match '^    \S' } | ForEach-Object { Get-TestIdentity $project ([string]$_).Trim() })
    Assert-TestInventory $inventory
    return $inventory
}

function Get-TestReportResults([string]$ReportPath, [string]$Project) {
    try {
        [xml]$report = Get-Content -LiteralPath $ReportPath -Raw
        $rows = @($report.SelectNodes('//*[local-name()="UnitTestResult"]'))
        if ($rows.Count -eq 0) { throw 'Empty TRX.' }
        foreach ($row in $rows) {
            $identity = Get-TestIdentity $Project $row.testName
            [pscustomobject]@{
                Project = $identity.Project; Method = $identity.Method; Id = $identity.Id
                Outcome = [string]$row.outcome
                Seconds = [timespan]::Parse($row.duration, [Globalization.CultureInfo]::InvariantCulture).TotalSeconds
            }
        }
    }
    catch { throw "Unusable test report for $Project; raw report withheld." }
}

function New-CiTestPlan([object[]]$Inventory) {
    Assert-TestInventory $Inventory
    $weights = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ci-test-weights.json') -Raw | ConvertFrom-Json -AsHashtable
    $hostTests = @($Inventory | Where-Object Project -EQ 'HostIntegration.Tests')
    $classes = @($hostTests | Group-Object { $_.Method.Substring(0, $_.Method.LastIndexOf('.')) } | ForEach-Object {
        [pscustomobject]@{ Name = $_.Name; Tests = $_.Group; Weight = $(if ($weights.ContainsKey($_.Name)) { [double]$weights[$_.Name] } else { $_.Count * 5.0 }) }
    } | Sort-Object @{ Expression = 'Weight'; Descending = $true }, Name -Culture en-US -CaseSensitive)
    if ($classes.Count -lt 3 -or $hostTests.Count -eq $Inventory.Count) { throw 'Expected three host groups and at least one other project.' }
    $loads = @(0.0, 0.0, 0.0)
    $assignments = @{}
    foreach ($class in $classes) {
        $index = 0
        for ($candidate = 1; $candidate -lt 3; $candidate++) { if ($loads[$candidate] -lt $loads[$index]) { $index = $candidate } }
        $loads[$index] += $class.Weight
        foreach ($test in $class.Tests) { $assignments[$test.Id] = $index + 1 }
    }
    foreach ($test in ($Inventory | Sort-Object Id -Culture en-US -CaseSensitive)) {
        [pscustomobject]@{ Project = $test.Project; Method = $test.Method; Id = $test.Id; Shard = $(if ($assignments.ContainsKey($test.Id)) { $assignments[$test.Id] } else { 0 }) }
    }
}

function Assert-CiTestReports([object[]]$Reports, [string]$Revision, [string]$RunId, [string]$Attempt) {
    if ($Reports.Count -ne 4 -or $Revision -notmatch '^[a-f0-9]{40}$' -or $RunId -notmatch '^\d+$' -or $Attempt -notmatch '^\d+$') { throw 'Expected four reports for one run.' }
    $plan = @(New-CiTestPlan @($Reports[0].Plan))
    $canonical = ConvertTo-Json -InputObject $plan -Depth 10 -Compress
    $shards = [Collections.Generic.HashSet[int]]::new()
    $all = @()
    foreach ($report in $Reports) {
        if ($report.Shard -notin 0..3 -or -not $shards.Add($report.Shard) -or
            $report.Revision -cne $Revision -or $report.RunId -cne $RunId -or $report.Attempt -cne $Attempt) { throw 'Unexpected, duplicate or stale shard.' }
        if ((ConvertTo-Json -InputObject @($report.Plan) -Depth 10 -Compress) -cne $canonical) { throw 'Discovery or partition disagreement.' }
        $expected = @{}
        foreach ($test in @($plan | Where-Object Shard -EQ $report.Shard)) { $expected[$test.Id] = $test }
        if ($expected.Count -eq 0) { throw 'Empty shard.' }
        foreach ($result in @($report.Results)) {
            if (-not $expected.ContainsKey($result.Id)) { throw 'Unexpected or duplicate test result.' }
            $test = $expected[$result.Id]
            if ($result.Project -cne $test.Project -or $result.Method -cne $test.Method -or $result.Outcome -cne 'Passed' -or
                [double]$result.Seconds -lt 0 -or -not [double]::IsFinite([double]$result.Seconds)) { throw 'Failed, skipped or inconsistent test result.' }
            $expected.Remove($result.Id)
            $all += $result
        }
        if ($expected.Count -ne 0) { throw 'Missing tests.' }
    }
    Write-Output "Verified $($all.Count) tests across four isolated shards: zero missing, duplicate, failed or skipped."
    $all | Group-Object { $_.Project + ':' + $_.Method.Substring(0, $_.Method.LastIndexOf('.')) } | ForEach-Object {
        [pscustomobject]@{ Class = $_.Name; Seconds = [math]::Round(($_.Group.Seconds | Measure-Object -Sum).Sum, 1) }
    } | Sort-Object Seconds -Descending | Select-Object -First 15 | ForEach-Object { Write-Output ("  {0:N1}s {1}" -f $_.Seconds, $_.Class) }
}

function Assert-CiIsolation([string]$RepoRoot) {
    $stage = 'runner'
    try {
        if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted') { throw 'Requires hosted CI.' }
        $stage = 'private-config'
        if (Test-Path -LiteralPath (Join-Path $RepoRoot 'env/test.dev')) { throw 'Private configuration forbidden.' }
        $stage = 'postgres'
        $connection = [Data.Common.DbConnectionStringBuilder]::new()
        # PowerShell adapts this dictionary: property assignment creates a key instead of invoking the setter.
        $connection.set_ConnectionString($env:NEXUSSTACK_TEST_POSTGRES)
        if ($connection.Count -ne 4 -or $connection['Host'] -cne '127.0.0.1' -or [string]$connection['Port'] -cne '5432' -or
            $connection['Database'] -cne 'postgres' -or $connection['Username'] -cne 'postgres') { throw 'Dependency configuration rejected.' }
        $stage = 'redis'
        if ($env:NEXUSSTACK_TEST_REDIS -cne '127.0.0.1:6379') { throw 'Cache configuration rejected.' }
        $stage = 'rabbitmq'
        $broker = $env:NEXUSSTACK_TEST_RABBITMQ | ConvertFrom-Json
        if ($broker.HostName -cne '127.0.0.1' -or $broker.Port -ne 5672 -or $broker.VirtualHost -cne '/' -or
            $broker.UserName -cne 'nsn-ci' -or [string]::IsNullOrWhiteSpace($broker.Password)) { throw 'Broker configuration rejected.' }
        $stage = 'containers'
        $containers = @(
            @{ Id = $env:NSN_CI_POSTGRES_CONTAINER; Port = '5432' },
            @{ Id = $env:NSN_CI_REDIS_CONTAINER; Port = '6379' },
            @{ Id = $env:NSN_CI_RABBIT_CONTAINER; Port = '5672' }
        )
        foreach ($container in $containers) {
            if ($container.Id -notmatch '^[a-f0-9]{12,64}$') { throw 'Missing runner container identity.' }
            $output = & docker inspect --format '{"running":{{json .State.Running}},"health":{{json .State.Health.Status}},"ports":{{json .NetworkSettings.Ports}}}' $container.Id 2>&1
            if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect runner container.' }
            $state = ($output -join "`n") | ConvertFrom-Json -AsHashtable
            $bindings = @($state.ports[$container.Port + '/tcp'])
            if (-not $state.running -or $state.health -cne 'healthy' -or $bindings.Count -ne 1 -or
                $bindings[0].HostIp -cne '127.0.0.1' -or $bindings[0].HostPort -cne $container.Port) { throw 'Unhealthy or unisolated dependency.' }
            foreach ($binding in @($state.ports.Values | ForEach-Object { $_ } | Where-Object { $null -ne $_ })) {
                if ($binding.HostIp -cne '127.0.0.1') { throw 'Dependency exposed beyond loopback.' }
            }
        }
    }
    catch { throw "CI_ISOLATION_REJECTED stage=$stage; isolated hosted runner and healthy loopback dependencies required." }
}

Export-ModuleMember -Function Get-TestIdentity, Assert-TestInventory, Get-DiscoveredTests, Get-TestReportResults, New-CiTestPlan, Assert-CiTestReports, Assert-CiIsolation
