param(
    [Parameter(Mandatory)][ValidateSet('Plan', 'Verify', 'Isolation', 'Discover', 'Report')][string]$Action,
    [string]$InputPath,
    [string]$OutputPath,
    [string]$Revision,
    [string]$RunId,
    [string]$Attempt,
    [string]$Project,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'ci-test-support.psm1') -Force
try {
    switch ($Action) {
        'Plan' {
            $inventory = @(Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json)
            $plan = @(New-CiTestPlan $inventory)
            [IO.File]::WriteAllText($OutputPath, (ConvertTo-Json -InputObject $plan -Depth 10), [Text.UTF8Encoding]::new($false))
        }
        'Verify' {
            $files = @(Get-ChildItem -LiteralPath $InputPath -File -Recurse)
            if ($files.Count -ne 4 -or @($files | Where-Object Name -NotMatch '^shard-[0-3]\.json$').Count -ne 0) { throw 'Unexpected report files.' }
            $reports = @($files | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
            Assert-CiTestReports $reports $Revision $RunId $Attempt
        }
        'Isolation' { Assert-CiIsolation $InputPath }
        'Discover' {
            $inventory = @(Get-DiscoveredTests $InputPath $Configuration)
            [IO.File]::WriteAllText($OutputPath, (ConvertTo-Json -InputObject $inventory -Depth 10), [Text.UTF8Encoding]::new($false))
        }
        'Report' {
            $results = @(Get-TestReportResults $InputPath $Project)
            [IO.File]::WriteAllText($OutputPath, (ConvertTo-Json -InputObject $results -Depth 10), [Text.UTF8Encoding]::new($false))
        }
        default { throw 'Not implemented.' }
    }
}
catch {
    # Neither malformed configuration nor raw discovery/report data may reach CI logs.
    if ($Action -eq 'Isolation') {
        $stage = [regex]::Match($_.Exception.Message, 'stage=[a-z-]+').Value
        Write-Host "CI_ISOLATION_REJECTED $stage"
    }
    else { Write-Host 'CI test plan or report verification failed. Inspect the private input locally.' }
    exit 1
}
