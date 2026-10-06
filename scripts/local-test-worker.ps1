param([Parameter(Mandatory)][string]$RequestPath)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
Import-Module (Join-Path $PSScriptRoot 'test-console.psm1') -Force
$request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
$nativeReturned = $false
$exitCode = 1
$console = [IO.StreamWriter]::new($request.Console, $false, [Text.UTF8Encoding]::new($false))
try {
    $arguments = @('test', $request.Project, '--configuration', $request.Configuration, '--no-build', '--no-restore', '--nologo', '-nodeReuse:false',
        '--filter', $request.Filter, '--results-directory', $request.Directory, '--logger', ('trx;LogFileName=' + $request.Name + '.trx'), '--logger', 'console;verbosity=normal')
    if ($request.StopOnFailure) { $arguments += @('--', 'xUnit.StopOnFail=true') }
    $count = 0
    & dotnet @arguments 2>&1 | ForEach-Object {
        $console.WriteLine([string]$_)
        $console.Flush()
        $case = Get-TestConsoleCase ([string]$_)
        if ($null -ne $case) {
            $count++
            Write-Output "TEST_PROGRESS worker=$($request.Name) completed=$count outcome=$($case.Outcome) method=$($case.Method)"
        }
    }
    $exitCode = $LASTEXITCODE
    $nativeReturned = $true
}
finally {
    $console.Dispose()
    [IO.File]::WriteAllText($request.Completion, (ConvertTo-Json @{ Returned = $nativeReturned; ExitCode = $exitCode } -Compress))
}
exit $exitCode
