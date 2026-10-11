# Keep the canonical business route policies; replace only deployment destinations.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$source = Join-Path $repoRoot 'src/Gateway/NexusStackNext.Gateway/routes.business.json'
$table = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json
if (@($table.clusters).Count -ne 3) { throw 'Expected the platform, costing and pricing clusters.' }
foreach ($cluster in $table.clusters) {
    $service = switch ($cluster.clusterId) {
        'platform-host' { 'platform' }
        'costing-host' { 'costing' }
        'pricing-host' { 'pricing' }
        default { throw 'Unexpected deployment cluster.' }
    }
    foreach ($destination in $cluster.destinations) { $destination.address = "http://${service}:8080" }
}
$output = Join-Path $repoRoot 'env/container.routes.dev'
[IO.File]::WriteAllText($output, ($table | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
Write-Output 'Container routes prepared from the canonical business route table.'
