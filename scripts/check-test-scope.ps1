# Exercise the public runner with an external CLI adapter and isolated, service-free fixtures.
$ErrorActionPreference = 'Stop'
$lab = Join-Path ([IO.Path]::GetTempPath()) ('nsn-test-scope-' + [guid]::NewGuid().ToString('N'))
$fixture = Join-Path $lab 'repo'
$adapter = Join-Path $lab 'adapter'
$temporary = Join-Path $lab 'temporary'
foreach ($directory in @((Join-Path $fixture 'scripts'), $adapter, $temporary)) {
    [void][IO.Directory]::CreateDirectory($directory)
}
foreach ($name in @('run-tests.ps1', 'test-ownership.psm1', 'test-console.psm1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $fixture "scripts/$name")
}
foreach ($name in @('Chosen.Tests', 'Unrelated.Tests')) {
    $directory = Join-Path $fixture "tests/$name"
    [void][IO.Directory]::CreateDirectory($directory)
    [IO.File]::WriteAllText((Join-Path $directory "$name.csproj"), '<Project />')
}
$entryPath = Join-Path $lab 'entries.txt'
$filterPath = Join-Path $lab 'filter.txt'
$fake = @'
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFileNameWithoutExtension($args[1])
[IO.File]::AppendAllText($env:NSN_SCOPE_ENTRIES, $project + "`n")
$filterIndex = [array]::IndexOf($args, '--filter')
[IO.File]::WriteAllText($env:NSN_SCOPE_FILTER, $(if ($filterIndex -ge 0) { $args[$filterIndex + 1] } else { '' }))
$directoryIndex = [array]::IndexOf($args, '--results-directory')
$path = Join-Path $args[$directoryIndex + 1] ($project + '.trx')
$mode = $env:NSN_SCOPE_MODE
if ($mode -eq 'missing') { exit 0 }
if ($mode -eq 'malformed') { [IO.File]::WriteAllText($path, '<private-argument-sentinel'); exit 0 }
$total = if ($mode -eq 'empty') { 0 } else { 1 }
$outcome = if ($mode -eq 'skipped') { 'NotExecuted' } elseif ($mode -eq 'failed') { 'Failed' } else { 'Passed' }
$passed = if ($outcome -eq 'Passed') { $total } else { 0 }
$failed = if ($mode -eq 'failed') { 1 } else { 0 }
$skipped = if ($mode -eq 'skipped') { 1 } else { 0 }
$declared = if ($mode -eq 'mismatch') { 2 } else { $total }
$result = if ($total) { '<UnitTestResult testName="Probe.Journey(private-argument-sentinel)" outcome="' + $outcome + '" />' } else { '' }
$xml = '<TestRun><Results>' + $result + '</Results><ResultSummary><Counters total="' + $declared + '" executed="' + ($total - $skipped) + '" passed="' + $passed + '" failed="' + $failed + '" notExecuted="' + $skipped + '" /></ResultSummary></TestRun>'
[IO.File]::WriteAllText($path, $xml)
if ($mode -eq 'failed') { exit 7 }
exit 0
'@
$fakePath = Join-Path $adapter 'fake-dotnet.ps1'
[IO.File]::WriteAllText($fakePath, $fake, [Text.UTF8Encoding]::new($false))
if ($IsWindows) {
    [IO.File]::WriteAllText((Join-Path $adapter 'dotnet.cmd'), "@echo off`r`n`"$((Get-Command pwsh).Source)`" -NoProfile -File `"$fakePath`" %*`r`n")
} else {
    $quote = "'`"'`"'"
    $shell = (Get-Command pwsh).Source.Replace("'", $quote)
    $script = $fakePath.Replace("'", $quote)
    [IO.File]::WriteAllText((Join-Path $adapter 'dotnet'), "#!/bin/sh`nexec '$shell' -NoProfile -File '$script' `"`$@`"`n")
    & chmod +x (Join-Path $adapter 'dotnet')
    if ($LASTEXITCODE -ne 0) { throw 'Could not enable the isolated adapter.' }
}
function Invoke-ScopeProbe([string[]]$Arguments, [string]$Mode = 'pass') {
    [IO.File]::WriteAllText($entryPath, '')
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-File', (Join-Path $fixture 'scripts/run-tests.ps1'), '-NoBuild') + $Arguments) {
        $start.ArgumentList.Add($argument)
    }
    foreach ($key in @('TEMP', 'TMP', 'TMPDIR')) { $start.Environment[$key] = $temporary }
    $start.Environment['PATH'] = $adapter + [IO.Path]::PathSeparator + $env:PATH
    $start.Environment['NEXUSSTACK_TEST_POSTGRES'] = 'Host=scope-probe.invalid'
    foreach ($key in @('NEXUSSTACK_TEST_REDIS', 'NEXUSSTACK_TEST_RABBITMQ')) { [void]$start.Environment.Remove($key) }
    $start.Environment['GITHUB_ACTIONS'] = 'false'
    $start.Environment['NSN_SCOPE_ENTRIES'] = $entryPath
    $start.Environment['NSN_SCOPE_FILTER'] = $filterPath
    $start.Environment['NSN_SCOPE_MODE'] = $Mode
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) { throw 'Scope probe exceeded its deadline.' }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        if ($output.Contains('private-argument-sentinel')) { throw 'Scope probe exposed private report content.' }
        $guardPath = Join-Path $temporary 'nexusstack-run-tests.lock'
        if (Test-Path -LiteralPath $guardPath) {
            $guard = Get-Content -LiteralPath $guardPath -Raw | ConvertFrom-Json
            if ($guard.State -cne 'idle') { throw 'Returned scope probe did not release workload ownership.' }
        }
        return [pscustomobject]@{ Exit = $process.ExitCode; Output = $output; Projects = @(Get-Content -LiteralPath $entryPath) }
    }
    finally { $process.Dispose() }
}
$focused = @('-Scope', 'Focused', '-Project', 'Chosen.Tests')
$result = Invoke-ScopeProbe $focused
if ($result.Exit -ne 0 -or $result.Projects.Count -ne 1 -or $result.Projects[0] -cne 'Chosen.Tests' -or
    -not $result.Output.Contains('TEST_SCOPE scope=Focused') -or -not $result.Output.Contains('fullSuite=False')) {
    throw 'Focused validation must run only the selected project and report partial coverage.'
}
Write-Output 'PASS: explicit focused validation selects one project and reports partial coverage'
foreach ($arguments in @(@('-Project', 'Chosen.Tests'), @('-Project', 'Chosen.Tests', '-Filter', 'FullyQualifiedName~Journey'))) {
    $result = Invoke-ScopeProbe $arguments
    if ($result.Exit -ne 0 -or $result.Projects.Count -ne 1 -or -not $result.Output.Contains('TEST_SCOPE scope=Focused')) { throw 'Existing selection must remain focused.' }
    if ('-Filter' -in $arguments -and [IO.File]::ReadAllText($filterPath) -cne 'FullyQualifiedName~Journey') { throw 'The selected filter was not forwarded to the CLI.' }
}
foreach ($mode in @('empty', 'missing', 'malformed', 'mismatch', 'skipped', 'failed')) {
    $result = Invoke-ScopeProbe ($focused + @('-Filter', 'FullyQualifiedName~Journey')) $mode
    if ($result.Exit -eq 0 -or $result.Projects.Count -ne 1) { throw "Focused $mode evidence must fail." }
    Write-Output "PASS: focused $mode evidence refuses success and releases ownership"
}
foreach ($arguments in @(@('-Scope', 'Focused'), @('-Scope', 'Full', '-Project', 'Chosen.Tests'),
    @('-Scope', 'Full', '-Filter', 'FullyQualifiedName~Journey'), @('-Scope', 'Focused', '-Project', 'Missing.Tests'),
    @('-Scope', 'Focused', '-Project', 'Chosen.Tests', '-Filter', ''), @('-Scope', 'Focused', '-Project', 'Chosen.Tests', '-CiShard', '0'))) {
    $result = Invoke-ScopeProbe $arguments
    if ($result.Exit -eq 0 -or $result.Projects.Count -ne 0) { throw 'Invalid scope started workload or passed.' }
}
$result = Invoke-ScopeProbe @()
if ($result.Exit -ne 0 -or $result.Projects.Count -ne 2 -or -not $result.Output.Contains('TEST_SCOPE scope=Full')) { throw 'Default full scope changed.' }
Write-Output 'PASS: invalid scope starts no workload; default full scope retains both projects'
