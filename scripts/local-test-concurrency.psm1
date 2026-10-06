Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ci-test-support.psm1') -Force

function New-LocalHostPlan([object[]]$Inventory, [int]$Concurrency) {
    Assert-TestInventory $Inventory
    $classes = @(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'local-test-classes.json') -Raw | ConvertFrom-Json)
    if ($classes.Count -eq 0 -or @($classes | Select-Object -Unique).Count -ne $classes.Count) { throw 'Empty or duplicate local class declaration.' }
    $weights = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'local-test-weights.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($weights -isnot [Collections.IDictionary] -or $weights.Count -eq 0) { throw 'Local weight declaration is empty or invalid.' }
    foreach ($value in $weights.Values) {
        if ($value -isnot [ValueType] -or $value -is [bool] -or -not [double]::IsFinite([double]$value) -or [double]$value -le 0) { throw 'Local weight must be a finite positive number.' }
    }
    $loads = @(0.0) * $Concurrency
    $plan = @()
    $groups = @($Inventory | Group-Object { $_.Method.Substring(0, $_.Method.LastIndexOf('.')) })
    foreach ($class in $classes) {
        if ($class -cnotmatch '^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+$' -or @($groups | Where-Object Name -CEQ $class).Count -ne 1) { throw 'Declared parallel class missing from discovery.' }
    }
    $parallel = @($groups | Where-Object Name -CIn $classes | Sort-Object @{ Expression = { if ($weights.Contains($_.Name)) { [double]$weights[$_.Name] } else { $_.Count * 5.0 } }; Descending = $true }, Name -Culture en-US -CaseSensitive)
    foreach ($group in $parallel) {
        $lane = 0
        for ($index = 1; $index -lt $Concurrency; $index++) { if ($loads[$index] -lt $loads[$lane]) { $lane = $index } }
        $loads[$lane] += $(if ($weights.Contains($group.Name)) { [double]$weights[$group.Name] } else { $group.Count * 5.0 })
        foreach ($test in $group.Group) { $plan += [pscustomobject]@{ Method = $test.Method; Id = $test.Id; Lane = $lane } }
    }
    foreach ($group in @($groups | Where-Object Name -CNotIn $classes)) {
        foreach ($test in $group.Group) { $plan += [pscustomobject]@{ Method = $test.Method; Id = $test.Id; Lane = -1 } }
    }
    if ($plan.Count -ne $Inventory.Count) { throw 'Local partition lost discovery identities.' }
    return $plan
}

function Invoke-LocalBatch([object[]]$Jobs, [string]$Directory, [string]$Guard, [ref]$WorkloadReturned) {
    $running = @()
    try {
        foreach ($job in $Jobs) {
            $requestPath = Join-Path $Directory ($job.Name + '.request.json')
            [IO.File]::WriteAllText($requestPath, (ConvertTo-Json $job -Compress))
            $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
            $start.UseShellExecute = $false
            $start.CreateNoWindow = $true
            $start.RedirectStandardOutput = $true
            $start.RedirectStandardError = $true
            $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
            $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
            foreach ($argument in @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'local-test-worker.ps1'), '-RequestPath', $requestPath)) { $start.ArgumentList.Add($argument) }
            $start.Environment['NEXUSSTACK_TEST_DDL_GUARD'] = $Guard
            $start.Environment['NEXUSSTACK_TEST_LOAD_STOP'] = (Join-Path $Directory 'load-stop')
            $start.Environment['NSN_LOCAL_WORKER'] = $job.Name
            $WorkloadReturned.Value = $false
            $process = [Diagnostics.Process]::Start($start)
            $running += [pscustomobject]@{ Process = $process; Request = $job; Line = $process.StandardOutput.ReadLineAsync(); Errors = $process.StandardError.ReadToEndAsync() }
            [IO.File]::WriteAllText((Join-Path $Directory ($job.Name + '.process.json')), (ConvertTo-Json @{ Pid = $process.Id; StartedUtcTicks = $process.StartTime.ToUniversalTime().Ticks } -Compress))
        }
        do {
            foreach ($worker in $running) {
                while ($null -ne $worker.Line -and $worker.Line.IsCompleted) {
                    $line = $worker.Line.GetAwaiter().GetResult()
                    if ($null -eq $line) { $worker.Line = $null; break }
                    if ($line -cmatch '^TEST_PROGRESS worker=[A-Za-z0-9.-]+ completed=\d+ outcome=(Passed|Failed|Skipped) method=NexusStackNext\.[A-Za-z0-9_.]+$') { Write-Host $line }
                    $worker.Line = $worker.Process.StandardOutput.ReadLineAsync()
                }
            }
            if (@($running | Where-Object { -not $_.Process.HasExited -or $null -ne $_.Line -or -not $_.Errors.IsCompleted }).Count -ne 0) { Start-Sleep -Milliseconds 100 }
        } while (@($running | Where-Object { -not $_.Process.HasExited -or $null -ne $_.Line -or -not $_.Errors.IsCompleted }).Count -ne 0)
        $allReturned = $true
        $failed = $false
        foreach ($worker in $running) {
            $errors = $worker.Errors.GetAwaiter().GetResult()
            [IO.File]::WriteAllText((Join-Path $Directory ($worker.Request.Name + '.worker.stderr')), $errors)
            if (-not (Test-Path -LiteralPath $worker.Request.Completion)) { $allReturned = $false; continue }
            try {
                $completion = Get-Content -LiteralPath $worker.Request.Completion -Raw | ConvertFrom-Json
                if ($completion.Returned -isnot [bool] -or -not $completion.Returned) { $allReturned = $false }
                if ($completion.ExitCode -ne 0 -or $worker.Process.ExitCode -ne 0) { $failed = $true }
            }
            catch { $allReturned = $false }
        }
        $WorkloadReturned.Value = $allReturned
        if (-not $allReturned) { throw 'LOCAL_WORKER_RETURN_UNPROVEN: keep workload ownership active; inspect private evidence.' }
        return (-not $failed)
    }
    finally { foreach ($worker in $running) { $worker.Process.Dispose() } }
}

function Merge-LocalTestReports([string[]]$Paths, [object[]]$Expected, [string]$Destination) {
    $results = @($Paths | ForEach-Object { Get-TestReportResults $_ 'HostIntegration.Tests' })
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($row in $results) { if (-not $ids.Add($row.Id)) { throw 'Duplicate local test result.' } }
    if (-not $ids.SetEquals([string[]]@($Expected.Id))) { throw 'Local results disagree with selected discovery; incomplete runs are not passing runs.' }
    $reports = @($Paths | ForEach-Object { [xml](Get-Content -LiteralPath $_ -Raw) })
    [xml]$merged = $reports[0].OuterXml
    foreach ($name in @('Results', 'TestDefinitions', 'TestEntries')) {
        $target = $merged.SelectSingleNode('/*/*[local-name()="' + $name + '"]')
        $target.RemoveAll()
        foreach ($report in $reports) {
            foreach ($node in $report.SelectNodes('/*/*[local-name()="' + $name + '"]/*')) { [void]$target.AppendChild($merged.ImportNode($node, $true)) }
        }
    }
    $counters = $merged.SelectSingleNode('//*[local-name()="Counters"]')
    foreach ($attribute in @($counters.Attributes)) {
        $name = $attribute.Name
        $sum = 0
        foreach ($report in $reports) { $sum += [int]$report.SelectSingleNode('//*[local-name()="Counters"]').GetAttribute($name) }
        $counters.SetAttribute($name, [string]$sum)
    }
    $summary = $merged.SelectSingleNode('//*[local-name()="ResultSummary"]')
    $summary.SetAttribute('outcome', $(if (@($results | Where-Object Outcome -CNE 'Passed').Count) { 'Failed' } else { 'Completed' }))
    $times = $merged.SelectSingleNode('/*/*[local-name()="Times"]')
    foreach ($attribute in @('start', 'creation', 'queuing')) {
        $time = @($reports | ForEach-Object { [datetimeoffset]::Parse($_.SelectSingleNode('/*/*[local-name()="Times"]').GetAttribute($attribute)) } | Sort-Object)[0]
        $times.SetAttribute($attribute, $time.ToString('o'))
    }
    $finish = @($reports | ForEach-Object { [datetimeoffset]::Parse($_.SelectSingleNode('/*/*[local-name()="Times"]').GetAttribute('finish')) } | Sort-Object)[-1]
    $times.SetAttribute('finish', $finish.ToString('o'))
    $merged.Save($Destination)
    return (@($results | Where-Object Outcome -CNE 'Passed').Count -eq 0)
}

function Invoke-LocalHostTests([string]$Project, [string]$Configuration, [int]$Concurrency, [string]$Filter, [bool]$StopOnFailure, [string]$Directory, [ref]$WorkloadReturned) {
    $WorkloadReturned.Value = $false
    $inventory = @(Get-DiscoveredTests $Project $Configuration)
    $WorkloadReturned.Value = $true
    $plan = @(New-LocalHostPlan $inventory $Concurrency)
    if ($Filter) {
        $terms = @($Filter.Split('|') | ForEach-Object {
            $match = [regex]::Match($_, '^FullyQualifiedName(?<operator>[~=])(?<value>[A-Za-z0-9_.]+)$')
            if (-not $match.Success) { throw 'Local parallel mode supports FullyQualifiedName substring/exact terms joined by |; use Concurrency 1 for other filters.' }
            [pscustomobject]@{ Operator = $match.Groups['operator'].Value; Value = $match.Groups['value'].Value }
        })
        $plan = @($plan | Where-Object {
            $method = $_.Method
            @($terms | Where-Object { if ($_.Operator -eq '=') { $method -ceq $_.Value } else { $method.Contains($_.Value, [StringComparison]::Ordinal) } }).Count -gt 0
        })
    }
    if ($plan.Count -eq 0) { throw 'Empty local selected inventory.' }
    $guard = Join-Path $Directory 'database-operations.lock'
    [IO.File]::WriteAllText($guard, 'One expensive database operation at a time.')
    $jobs = @($plan | Group-Object Lane | Sort-Object { [int]$_.Name } | ForEach-Object {
        $name = 'HostIntegration.Tests.lane-' + $(if ($_.Name -eq '-1') { 'exclusive' } else { $_.Name })
        $classFilter = (@($_.Group.Method | ForEach-Object { $_.Substring(0, $_.LastIndexOf('.')) } | Sort-Object -Unique) | ForEach-Object { 'FullyQualifiedName~' + $_ + '.' }) -join '|'
        [pscustomobject]@{
            Name = $name; Project = $Project; Configuration = $Configuration; StopOnFailure = $StopOnFailure
            Filter = $(if ($Filter) { '(' + $classFilter + ')&(' + $Filter + ')' } else { $classFilter })
            Directory = $Directory; Console = (Join-Path $Directory ($name + '.console.log')); Completion = (Join-Path $Directory ($name + '.completion.json'))
            Exclusive = ($_.Name -eq '-1')
        }
    })
    Write-Host "LOCAL_TEST_PLAN concurrency=$Concurrency parallel=$(@($plan | Where-Object Lane -GE 0).Count) exclusive=$(@($plan | Where-Object Lane -EQ -1).Count)"
    $parallel = @($jobs | Where-Object { -not $_.Exclusive })
    $exclusive = @($jobs | Where-Object Exclusive)
    $success = $true
    if ($parallel.Count) { $success = Invoke-LocalBatch $parallel $Directory $guard $WorkloadReturned }
    if (Test-Path -LiteralPath (Join-Path $Directory 'load-stop')) { Write-Host 'LOCAL_RESOURCE_STOP: in-flight lanes returned; subsequent workload was not started.'; return 1 }
    if ($StopOnFailure -and -not $success) { Write-Host 'STOP_ON_FAILURE: in-flight lanes returned; exclusive local work was not started.'; return 1 }
    if ($exclusive.Count) { $success = (Invoke-LocalBatch $exclusive $Directory $guard $WorkloadReturned) -and $success }
    $paths = @($jobs | ForEach-Object { Join-Path $Directory ($_.Name + '.trx') })
    $complete = Merge-LocalTestReports $paths $plan (Join-Path $Directory 'HostIntegration.Tests.trx')
    return $(if ($success -and $complete) { 0 } else { 1 })
}

Export-ModuleMember -Function Invoke-LocalHostTests
