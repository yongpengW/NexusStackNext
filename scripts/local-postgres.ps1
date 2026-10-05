# Windows portable PostgreSQL lifecycle; credentials are never command-line arguments.
[CmdletBinding()]
param(
    [ValidateSet('Start', 'Status', 'Stop', 'Test')][string]$Action = 'Test',
    [string]$RuntimeDirectory,
    [string]$StateDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'NexusStackNext/TestPostgres'),
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$Filter,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
if ($Action -in @('Start', 'Test')) {
    Write-Output 'LOCAL_POSTGRES_DISABLED: use scripts/run-tests.ps1 with the shared test configuration.'
    exit 1
}
if (-not $IsWindows) { Write-Output 'LOCAL_POSTGRES_UNSUPPORTED: the portable EDB lifecycle currently requires Windows.'; exit 1 }
try {
    Import-Module (Join-Path $PSScriptRoot 'local-postgres.psm1') -Force
    $result = Invoke-LocalPostgres -Action $Action -RuntimeDirectory $RuntimeDirectory -StateDirectory $StateDirectory -Configuration $Configuration -Filter $Filter -NoBuild:$NoBuild
    $result
}
catch {
    $stage = [regex]::Match($_.Exception.Message, '^LOCAL_POSTGRES_REJECTED stage=[a-z-]+$').Value
    if (-not $stage) { $stage = 'LOCAL_POSTGRES_REJECTED stage=uncertain-state' }
    Write-Output $stage
    exit 1
}
