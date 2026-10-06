$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path $PSScriptRoot -Parent
$lab = Join-Path ([IO.Path]::GetTempPath()) ('nsn-local-concurrency-' + [guid]::NewGuid().ToString('N'))
$scripts = Join-Path $lab 'scripts'
$project = Join-Path $lab 'tests/HostIntegration.Tests'
$laterProject = Join-Path $lab 'tests/ZZLater.Tests'
foreach ($path in @($scripts, $project, $laterProject, (Join-Path $lab 'env'))) { [void][IO.Directory]::CreateDirectory($path) }
foreach ($name in @('run-tests.ps1', 'test-ownership.psm1', 'test-console.psm1', 'local-test-worker.ps1', 'local-test-concurrency.psm1', 'ci-test-support.psm1', 'local-test-weights.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $scripts $name)
}
Copy-Item -LiteralPath (Join-Path $sourceRoot 'global.json') -Destination (Join-Path $lab 'global.json')
[IO.File]::WriteAllText((Join-Path $lab 'env/test.dev'), 'NEXUSSTACK_TEST_POSTGRES=Host=probe.invalid;Database=probe')
[IO.File]::WriteAllText((Join-Path $lab 'NexusStackNext.slnx'), '<Solution><Project Path="tests/HostIntegration.Tests/HostIntegration.Tests.csproj" /><Project Path="tests/ZZLater.Tests/ZZLater.Tests.csproj" /></Solution>')
$versions = [xml](Get-Content -LiteralPath (Join-Path $sourceRoot 'Directory.Packages.props') -Raw)
$packages = foreach ($name in @('Microsoft.NET.Test.Sdk', 'xunit', 'xunit.runner.visualstudio')) {
    $node = @($versions.Project.ItemGroup.PackageVersion | Where-Object Include -CEQ $name)
    if ($node.Count -ne 1) { throw 'Package manifest entry missing.' }
    '<PackageReference Include="' + $name + '" Version="' + $node[0].Version + '" />'
}
$csproj = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup>' + ($packages -join '') + '</ItemGroup></Project>'
[IO.File]::WriteAllText((Join-Path $project 'HostIntegration.Tests.csproj'), $csproj)
[IO.File]::WriteAllText((Join-Path $laterProject 'ZZLater.Tests.csproj'), $csproj)
[IO.File]::WriteAllText((Join-Path $laterProject 'Later.cs'), 'using Xunit; public sealed class Later { [Fact] public void Runs() { var root = Environment.GetEnvironmentVariable("NSN_CONCURRENCY_EVIDENCE")!; File.WriteAllText(Path.Combine(root, "later-project"), "executed"); if (Environment.GetEnvironmentVariable("NSN_SIGNAL_AFTER_PASS") == "true") { var reports = Directory.GetDirectories(root, "nsn-test-results-*"); Assert.Single(reports); File.WriteAllText(Path.Combine(reports[0], "load-stop"), "controlled pressure after return"); } } }')
Copy-Item -LiteralPath (Join-Path $sourceRoot 'tests/HostIntegration.Tests/JourneyDatabaseOperation.cs') -Destination $project
$classes = @(0..3 | ForEach-Object { 'NexusStackNext.HostIntegration.Tests.Lane' + $_ })
[IO.File]::WriteAllText((Join-Path $scripts 'local-test-classes.json'), (ConvertTo-Json -InputObject $classes))
$source = @'
using Xunit;
[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace NexusStackNext.HostIntegration.Tests;
[CollectionDefinition("owned")]
public sealed class OwnedCollection : ICollectionFixture<OwnedFixture> { }
public sealed class OwnedFixture : IAsyncLifetime
{
 public async Task InitializeAsync()
 {
  File.WriteAllText(Probe.PathFor("ready"), System.Text.Json.JsonSerializer.Serialize(new { Pid = Environment.ProcessId, StartedUtcTicks = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks }));
  using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
  var expected = int.Parse(Environment.GetEnvironmentVariable("NSN_EXPECTED_LANES")!);
  while (Directory.GetFiles(Probe.Root, "ready-*").Length < expected) { await Task.Delay(25, deadline.Token); }
  while (!File.Exists(Path.Combine(Probe.Root, "release"))) { await Task.Delay(25, deadline.Token); }
  File.WriteAllText(Probe.PathFor("business-start"), DateTimeOffset.UtcNow.Ticks.ToString());
  await Task.Delay(200, deadline.Token);
 }
 public async Task DisposeAsync()
 {
  await using var cleanup = await JourneyDatabaseOperation.EnterAsync();
  await Task.Yield();
  File.WriteAllText(Probe.PathFor("business-end"), DateTimeOffset.UtcNow.Ticks.ToString());
  File.WriteAllText(Probe.PathFor("disposed"), "disposed");
 }
}
public static class Probe
{
 public static string Root => Environment.GetEnvironmentVariable("NSN_CONCURRENCY_EVIDENCE")!;
 public static string PathFor(string kind) => Path.Combine(Root, kind + "-" + (Environment.GetEnvironmentVariable("NSN_LOCAL_WORKER") ?? "serial"));
 public static async Task ExecuteAsync(string owner, string name)
 {
  await using var lease = Environment.GetEnvironmentVariable("NSN_BYPASS_DDL") == "true" ? null : await JourneyDatabaseOperation.EnterAsync(preparation: true);
  var path = Path.Combine(Root, "ddl-" + owner + "-" + name);
  await File.WriteAllTextAsync(path + "-start", DateTimeOffset.UtcNow.Ticks.ToString());
  await Task.Delay(150);
  await File.WriteAllTextAsync(path + "-end", DateTimeOffset.UtcNow.Ticks.ToString());
  Assert.False(Environment.GetEnvironmentVariable("NSN_CONTROLLED_FAILURE") == "true" && owner == "0" && name == "A");
 }
}
public sealed class Exclusive
{
 [Fact] public async Task RequiresIndependentExecution()
 {
  var expected = int.Parse(Environment.GetEnvironmentVariable("NSN_EXPECTED_LANES")!);
  if (Environment.GetEnvironmentVariable("NSN_LOCAL_WORKER") is not null) { Assert.Equal(expected, Directory.GetFiles(Probe.Root, "disposed-*").Length); }
  File.WriteAllText(Path.Combine(Probe.Root, "exclusive"), "executed");
  if (Environment.GetEnvironmentVariable("NEXUSSTACK_TEST_DDL_GUARD") is not null)
  {
   await using (var held = await JourneyDatabaseOperation.EnterAsync())
   {
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JourneyDatabaseOperation.EnterAsync(cancellationToken: cancel.Token));
   }
   await using var released = await JourneyDatabaseOperation.EnterAsync();
   Assert.NotNull(released);
  }
 }
}
'@
foreach ($index in 0..3) {
    $second = if ($index -eq 0) { '[Theory][InlineData(1)][InlineData(2)] public Task B_Isolated(int row) => Probe.ExecuteAsync("0", "B" + row);' } else { "[Fact] public Task B_Isolated() => Probe.ExecuteAsync(`"$index`", `"B`");" }
    $source += "`n[Collection(`"owned`")] public sealed class Lane$index { [Fact] public Task A_Isolated() => Probe.ExecuteAsync(`"$index`", `"A`"); $second }"
}
[IO.File]::WriteAllText((Join-Path $project 'Cases.cs'), $source)
dotnet build (Join-Path $lab 'NexusStackNext.slnx') -c Release --nologo *> (Join-Path $lab 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Service-free concurrency probe build failed; private evidence retained.' }

function Start-Probe([string]$Case, [int]$Concurrency, [bool]$Fail, [bool]$Bypass, [switch]$Ci) {
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $arguments = @('-NoProfile', '-File', (Join-Path $scripts 'run-tests.ps1'), '-NoBuild', '-Configuration', 'Release', '-Concurrency', [string]$Concurrency)
    if ([IO.Path]::GetFileName($Case) -ne 'pressure') { $arguments += '-StopOnFailure' }
    if ([IO.Path]::GetFileName($Case) -eq 'filter') { $arguments += @('-Filter', 'FullyQualifiedName~NexusStackNext.HostIntegration.Tests.Lane0.|FullyQualifiedName~NexusStackNext.HostIntegration.Tests.Lane1.') }
    if ($Ci) { $arguments += @('-CiShard', '1', '-ReportDirectory', (Join-Path $Case 'reports')) }
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    foreach ($name in @('TEMP', 'TMP', 'TMPDIR')) { $start.Environment[$name] = $Case }
    $start.Environment['NSN_CONCURRENCY_EVIDENCE'] = $Case
    $start.Environment['NSN_EXPECTED_LANES'] = $(if ([IO.Path]::GetFileName($Case) -eq 'filter') { '2' } else { [string]$Concurrency })
    $start.Environment['NSN_CONTROLLED_FAILURE'] = $Fail.ToString().ToLowerInvariant()
    $start.Environment['NSN_BYPASS_DDL'] = $Bypass.ToString().ToLowerInvariant()
    $start.Environment['NSN_SIGNAL_AFTER_PASS'] = ([IO.Path]::GetFileName($Case) -eq 'returned-pressure').ToString().ToLowerInvariant()
    $start.Environment['GITHUB_ACTIONS'] = 'false'
    $process = [Diagnostics.Process]::Start($start)
    [pscustomobject]@{ Process = $process; Output = $process.StandardOutput.ReadToEndAsync(); Error = $process.StandardError.ReadToEndAsync() }
}
function Complete-Probe($Running, [string]$Case) {
    try {
        if (-not $Running.Process.WaitForExit(60000)) { throw 'Service-free concurrency probe exceeded its bound.' }
        $output = $Running.Output.GetAwaiter().GetResult()
        $errorOutput = $Running.Error.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $Case 'runner.stdout'), $output)
        [IO.File]::WriteAllText((Join-Path $Case 'runner.stderr'), $errorOutput)
        [pscustomobject]@{ ExitCode = $Running.Process.ExitCode; Output = $output; Error = $errorOutput }
    }
    finally {
        if (-not $Running.Process.HasExited) { $Running.Process.Kill($true); $Running.Process.WaitForExit() }
        $Running.Process.Dispose()
    }
}
function Get-Peak([string]$Case, [string]$Pattern) {
    $events = @()
    foreach ($file in @(Get-ChildItem -LiteralPath $Case -File -Filter $Pattern)) {
        $events += [pscustomobject]@{ Ticks = [long][IO.File]::ReadAllText($file.FullName); Delta = $(if ($file.Name -like '*-start' -or $file.Name -like 'business-start-*') { 1 } else { -1 }) }
    }
    if ($events.Count -eq 0 -or $events.Count % 2) { throw 'Resource timing evidence missing.' }
    $active = 0
    $peak = 0
    foreach ($event in ($events | Sort-Object Ticks, Delta)) { $active += $event.Delta; $peak = [math]::Max($active, $peak) }
    if ($active -ne 0) { throw 'Resource timing evidence unbalanced.' }
    return $peak
}
foreach ($mode in @('four', 'two', 'one', 'returned-pressure', 'filter', 'failure', 'pressure', 'interruption', 'bypass', 'ci')) {
    $case = Join-Path $lab $mode
    [void][IO.Directory]::CreateDirectory($case)
    $concurrency = switch ($mode) { 'two' { 2 } { $_ -in @('one', 'returned-pressure') } { 1 } default { 4 } }
    $running = Start-Probe $case $concurrency ($mode -eq 'failure') ($mode -eq 'bypass') -Ci:($mode -eq 'ci')
    if ($mode -in @('four', 'pressure', 'interruption')) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        while (@(Get-ChildItem -LiteralPath $case -File -Filter 'ready-*').Count -lt 4 -and -not $running.Process.HasExited) {
            if ($watch.Elapsed.TotalSeconds -gt 40) { throw 'Four native workers did not become ready.' }
            Start-Sleep -Milliseconds 50
        }
        if ($mode -eq 'interruption') { $running.Process.Kill(); $running.Process.WaitForExit() }
        if ($mode -ne 'pressure') {
            $second = Start-Probe $case 4 $false $false
            $rejection = Complete-Probe $second $case
            if ($rejection.ExitCode -eq 0 -or ($rejection.Output + $rejection.Error) -notmatch 'TEST_OWNERSHIP_REJECTED' -or $rejection.Output -match 'LOCAL_TEST_PLAN') { throw 'Second workload bypassed ownership.' }
        }
        if ($mode -eq 'pressure') {
            $directory = @(Get-ChildItem -LiteralPath $case -Directory -Filter 'nsn-test-results-*')
            if ($directory.Count -ne 1) { throw 'Pressure probe cannot locate its owned workload.' }
            [IO.File]::WriteAllText((Join-Path $directory[0].FullName 'load-stop'), 'controlled pressure')
        }
    }
    [IO.File]::WriteAllText((Join-Path $case 'release'), 'release')
    $result = Complete-Probe $running $case
    if ($mode -eq 'interruption') {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $identities = @(@(Get-ChildItem -LiteralPath $case -File -Filter 'ready-*') + @(Get-ChildItem -LiteralPath $case -File -Recurse -Filter '*.process.json') | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
        if ($identities.Count -ne 8) { throw 'Interrupted probe must identify four native hosts and four workers.' }
        do {
            $alive = @($identities | Where-Object { $process = Get-Process -Id $_.Pid -ErrorAction SilentlyContinue; $null -ne $process -and $process.StartTime.ToUniversalTime().Ticks -eq $_.StartedUtcTicks })
            if ($watch.Elapsed.TotalSeconds -gt 45) { throw 'Interrupted service-free children did not drain.' }
            if ($alive.Count) { Start-Sleep -Milliseconds 100 }
        } while ($alive.Count)
        $owner = Get-Content -LiteralPath (Join-Path $case 'nexusstack-run-tests.lock') -Raw | ConvertFrom-Json
        if ($owner.State -cne 'active') { throw 'Interrupted owner discarded uncertain completion.' }
        Write-Host 'PASS: interrupted controller leaves active recovery rejection; all identified service-free children drained.'
        continue
    }
    if ($mode -eq 'ci') {
        if ($result.ExitCode -eq 0 -or ($result.Output + $result.Error) -notmatch 'CI shards do not allow local Concurrency' -or $result.Output -match '已注入|LOCAL_TEST_PLAN' -or (Test-Path -LiteralPath (Join-Path $case 'nexusstack-run-tests.lock'))) { throw 'CI concurrency was not rejected before configuration and load.' }
        Write-Host 'PASS: CI refuses local concurrency before configuration and load.'
        continue
    }
    $owner = Get-Content -LiteralPath (Join-Path $case 'nexusstack-run-tests.lock') -Raw | ConvertFrom-Json
    if ($owner.State -cne 'idle') { throw 'Normally returned workers did not release ownership.' }
    $expectedLanes = $(if ($mode -eq 'filter') { 2 } else { $concurrency })
    if (@(Get-ChildItem -LiteralPath $case -File -Filter 'disposed-*').Count -ne $expectedLanes) { throw 'Async fixture cleanup missing.' }
    $businessPeak = Get-Peak $case 'business-*'
    if ($mode -eq 'pressure') {
        if ($result.ExitCode -eq 0 -or (Test-Path -LiteralPath (Join-Path $case 'exclusive')) -or (Test-Path -LiteralPath (Join-Path $case 'later-project')) -or @(Get-ChildItem -LiteralPath $case -File -Filter 'ddl-*').Count -or $businessPeak -ne 4) { throw 'Pressure stop did not refuse preparation and subsequent projects while allowing cleanup.' }
        Write-Host 'PASS: pressure signal refuses new preparation, drains four lanes, preserves cleanup and idle ownership.'
        continue
    }
    $ddlPeak = Get-Peak $case 'ddl-*'
    if ($businessPeak -ne $expectedLanes) { throw 'Expected bounded parallel business work did not overlap.' }
    if ($mode -eq 'bypass') {
        if ($ddlPeak -le 1) { throw 'Negative resource guard probe failed to observe a violation.' }
        Write-Host 'PASS: negative control observes concurrent heavy operations when the lease is bypassed.'
        continue
    }
    if ($ddlPeak -ne 1) { throw 'Heavy operations overlapped.' }
    if ($mode -eq 'returned-pressure') {
        if ($result.ExitCode -eq 0 -or $result.Output -match '全部 2 个工程通过' -or -not (Test-Path -LiteralPath (Join-Path $case 'later-project'))) { throw 'Pressure after a passing project was reported as a complete passing run.' }
        Write-Host 'PASS: pressure after a passing native return still makes the workload incomplete and nonzero.'
        continue
    }
    if ($mode -eq 'failure') {
        if ($result.ExitCode -eq 0 -or (Test-Path -LiteralPath (Join-Path $case 'exclusive')) -or (Test-Path -LiteralPath (Join-Path $case 'later-project'))) { throw 'Failed lanes started subsequent exclusive work or reported success.' }
        Write-Host 'PASS: native failure, all in-flight cleanup, no subsequent exclusive load, idle ownership.'
        continue
    }
    if ($result.ExitCode -ne 0) { throw 'Healthy concurrency probe failed; private evidence retained.' }
    if ($mode -ne 'filter' -and -not (Test-Path -LiteralPath (Join-Path $case 'later-project'))) { throw 'Healthy run did not exercise the subsequent project.' }
    if ($concurrency -gt 1 -and $result.Output -notmatch 'TEST_PROGRESS worker=HostIntegration.Tests.lane-') { throw 'Actual native parallel workers did not emit safe case progress.' }
    $report = @(Get-ChildItem -LiteralPath $case -File -Recurse -Filter 'HostIntegration.Tests.trx')
    if ($report.Count -ne 1) { throw 'Expected one complete merged report.' }
    Import-Module (Join-Path $scripts 'ci-test-support.psm1') -Force
    $rows = @(Get-TestReportResults $report[0].FullName 'HostIntegration.Tests')
    $expectedCases = $(if ($mode -eq 'filter') { 5 } else { 10 })
    if ($rows.Count -ne $expectedCases -or @($rows | Where-Object Outcome -CNE 'Passed').Count -or @($rows.Id | Select-Object -Unique).Count -ne $expectedCases) { throw 'Healthy inventory disagrees with selected unique passes.' }
    if (@($rows | Where-Object Method -CEQ 'NexusStackNext.HostIntegration.Tests.Lane0.B_Isolated').Count -ne 2) { throw 'Theory rows missing from selected inventory.' }
    if ($concurrency -gt 1) {
        $theoryReports = @(Get-ChildItem -LiteralPath $report[0].DirectoryName -File -Filter 'HostIntegration.Tests.lane-*.trx' | Where-Object {
            @(Get-TestReportResults $_.FullName 'HostIntegration.Tests' | Where-Object Method -CEQ 'NexusStackNext.HostIntegration.Tests.Lane0.B_Isolated').Count -gt 0
        })
        if ($theoryReports.Count -ne 1) { throw 'Theory rows split across workers.' }
    }
    Write-Host "PASS: $expectedLanes native lane(s), heavy-operation peak 1, $expectedCases selected unique passes, async cleanup, idle ownership."
}
$root = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not [IO.Path]::GetFullPath($lab).StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Probe cleanup outside owned temporary root.' }
Remove-Item -LiteralPath $lab -Recurse -Force
