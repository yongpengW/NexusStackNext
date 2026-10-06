$SourceRoot = Split-Path $PSScriptRoot -Parent
$ErrorActionPreference='Stop'
$lab=Join-Path ([IO.Path]::GetTempPath()) ('nsn-test-stop-'+[guid]::NewGuid().ToString('N'))
if(Test-Path -LiteralPath $lab){throw 'Probe directory unexpectedly already exists'}

function Invoke-RunnerProbe([string[]]$Arguments, [hashtable]$Environment, [string]$LogPrefix, [int]$DeadlineSeconds = 30) {
 $start=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
 $start.UseShellExecute=$false
 $start.RedirectStandardOutput=$true
 $start.RedirectStandardError=$true
 $start.CreateNoWindow=$true
 foreach($argument in $Arguments){$start.ArgumentList.Add($argument)}
 foreach($key in $Environment.Keys){$start.Environment[$key]=$Environment[$key]}
 $process=[Diagnostics.Process]::Start($start)
 try {
  $stdout=$process.StandardOutput.ReadToEndAsync()
  $stderr=$process.StandardError.ReadToEndAsync()
  if(-not $process.WaitForExit($DeadlineSeconds*1000)){throw 'Service-free probe did not finish within its bound'}
  $output=$stdout.GetAwaiter().GetResult()
  $errorOutput=$stderr.GetAwaiter().GetResult()
  [IO.File]::WriteAllText("$LogPrefix.stdout",$output)
  [IO.File]::WriteAllText("$LogPrefix.stderr",$errorOutput)
  return [pscustomobject]@{ExitCode=$process.ExitCode;Output=$output;Error=$errorOutput}
 }
 finally {
  if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()}
  $process.Dispose()
 }
}
$scripts=Join-Path $lab 'scripts'
$project=Join-Path $lab 'tests/Probe.Tests'
$laterProject=Join-Path $lab 'tests/ZZLater.Tests'
$temporary=Join-Path $lab 'private-temp'
foreach($path in @($scripts,$project,$laterProject,$temporary,(Join-Path $lab 'env'))){[IO.Directory]::CreateDirectory($path)|Out-Null}
foreach($name in @('run-tests.ps1','test-ownership.psm1','test-console.psm1')){Copy-Item -LiteralPath (Join-Path $SourceRoot "scripts/$name") -Destination (Join-Path $scripts $name)}
Copy-Item -LiteralPath (Join-Path $SourceRoot 'global.json') -Destination (Join-Path $lab 'global.json')
[IO.File]::WriteAllText((Join-Path $lab 'env/test.dev'),'NEXUSSTACK_TEST_POSTGRES=Host=probe.invalid;Database=probe',[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $lab 'NexusStackNext.slnx'),'<Solution><Project Path="tests/Probe.Tests/Probe.Tests.csproj" /><Project Path="tests/ZZLater.Tests/ZZLater.Tests.csproj" /></Solution>')
$manifest=[xml](Get-Content -LiteralPath (Join-Path $SourceRoot 'Directory.Packages.props') -Raw)
$packages=foreach($name in @('Microsoft.NET.Test.Sdk','xunit','xunit.runner.visualstudio')){
 $node=@($manifest.Project.ItemGroup.PackageVersion|Where-Object Include -CEQ $name)
 if($node.Count -ne 1 -or [string]::IsNullOrWhiteSpace($node[0].Version)){throw 'Missing package version'}
 '<PackageReference Include="'+$name+'" Version="'+$node[0].Version+'" />'
}
$csproj='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup>'+($packages -join '')+'</ItemGroup></Project>'
[IO.File]::WriteAllText((Join-Path $project 'Probe.Tests.csproj'),$csproj)
[IO.File]::WriteAllText((Join-Path $laterProject 'ZZLater.Tests.csproj'),$csproj)
[IO.File]::WriteAllText((Join-Path $laterProject 'Later.cs'),@'
using Xunit;
namespace NexusStackNext.Probe.Tests;
public sealed class LaterProject
{
 [Fact] public void LaterProjectRuns() => File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("NSN_STOP_PATH")!,"events"),"next-project"+Environment.NewLine);
}
'@)
$source=@'
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;
[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace NexusStackNext.Probe.Tests;
[CollectionDefinition("owned")]
public sealed class OwnedCollection : ICollectionFixture<OwnedFixture> { }
[Collection("owned")]
[TestCaseOrderer("NexusStackNext.Probe.Tests.Alphabetical", "Probe.Tests")]
public sealed class StopCases
{
 [Fact] public void A_BeforeFailure() => Record("before");
 [Fact] public void B_ControlledFailure() { Record("middle"); Assert.False(Environment.GetEnvironmentVariable("NSN_STOP_FAIL") == "true"); }
 [Fact] public void C_LaterWork() => Record("later");
 private static void Record(string text) => File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("NSN_STOP_PATH")!,"events"),text+Environment.NewLine);
}
public sealed class OwnedFixture : IAsyncLifetime
{
 public Task InitializeAsync() => Task.CompletedTask;
 public async Task DisposeAsync() { await Task.Yield(); File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("NSN_STOP_PATH")!,"events"),"disposed"+Environment.NewLine); }
}
public sealed class Alphabetical : ITestCaseOrderer
{
 public IEnumerable<T> OrderTestCases<T>(IEnumerable<T> cases) where T:ITestCase => cases.OrderBy(test=>test.TestMethod.Method.Name,StringComparer.Ordinal);
}
'@
[IO.File]::WriteAllText((Join-Path $project 'StopCases.cs'),$source)
dotnet build (Join-Path $lab 'NexusStackNext.slnx') -c Release --nologo *> (Join-Path $lab 'build.log')
if($LASTEXITCODE -ne 0){throw 'Isolated service-free probe build failed'}
foreach($mode in @('fail','pass')){
 $case=Join-Path $temporary $mode
 [IO.Directory]::CreateDirectory($case)|Out-Null
 $arguments=@('-NoProfile','-File',(Join-Path $scripts 'run-tests.ps1'),'-Configuration','Release','-NoBuild','-StopOnFailure')
 $environment=@{TEMP=$case;TMP=$case;TMPDIR=$case;NSN_STOP_PATH=$case;NSN_STOP_FAIL=$(if($mode -eq 'fail'){'true'}else{'false'});GITHUB_ACTIONS='false'}
 $result=Invoke-RunnerProbe $arguments $environment (Join-Path $lab $mode)
 $reports=@(Get-ChildItem -LiteralPath $case -Recurse -File -Filter '*.trx')
 $expectedProjects=$(if($mode -eq 'fail'){1}else{2})
 if($reports.Count -ne $expectedProjects){throw 'Unexpected later-project execution or incomplete healthy run'}
 $primaryReport=@($reports|Where-Object Name -CEQ 'Probe.Tests.trx')
 if($primaryReport.Count -ne 1){throw 'Missing or ambiguous native TRX'}
 [xml]$report=Get-Content -LiteralPath $primaryReport[0].FullName -Raw
 $c=$report.TestRun.ResultSummary.Counters
 $events=@(Get-Content -LiteralPath (Join-Path $case 'events'))
 $ownership=Get-Content -LiteralPath (Join-Path $case 'nexusstack-run-tests.lock') -Raw|ConvertFrom-Json
 $expectedCount=$(if($mode -eq 'fail'){2}else{3})
 $expectedFailures=$(if($mode -eq 'fail'){1}else{0})
 $expectedEvents=$(if($mode -eq 'fail'){'before,middle,disposed'}else{'before,middle,later,disposed,next-project'})
 if([int]$c.executed -ne $expectedCount -or [int]$c.failed -ne $expectedFailures -or [int]$c.passed -ne ($expectedCount-$expectedFailures) -or $ownership.State -cne 'idle' -or ($events -join ',') -cne $expectedEvents){throw 'Stop behavior, complete green execution, or owned cleanup failed'}
 if(($mode -eq 'fail' -and $result.ExitCode -eq 0) -or ($mode -eq 'pass' -and $result.ExitCode -ne 0)){throw 'Native verdict incorrectly propagated'}
 Write-Output "PASS: $mode completed=$expectedCount failed=$expectedFailures owned async fixture disposed; ownership idle."
}
$arguments=@('-NoProfile','-File',(Join-Path $scripts 'run-tests.ps1'),'-NoBuild','-StopOnFailure','-CiShard','0')
$rejected=Invoke-RunnerProbe $arguments @{} (Join-Path $lab 'ci-rejection') 10
if($rejected.ExitCode -eq 0 -or -not $rejected.Error.Contains('CI shards do not allow StopOnFailure') -or $rejected.Output.Contains('已注入')){throw 'CI accepted incomplete-run option or read private config before rejection'}
Write-Output 'PASS: CI rejects failure-stop before private configuration or test load.'
# Remove only this successfully completed, exclusively generated service-free fixture.
$temporaryRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
$resolved=[IO.Path]::GetFullPath($lab)
if(-not $resolved.StartsWith($temporaryRoot,[StringComparison]::Ordinal)){throw 'Unsafe probe cleanup target'}
Remove-Item -LiteralPath $resolved -Recurse -Force
