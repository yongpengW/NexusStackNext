# Run in a fresh generated solution with Docker available. Never reads env/test.dev.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$project = 'nsn-deployment-' + [Guid]::NewGuid().ToString('N')
$diagnostic = Join-Path ([IO.Path]::GetTempPath()) "$project.log"
$services = @('platform', 'costing', 'pricing', 'gateway')
$ownedFiles = @($services | ForEach-Object { Join-Path $repoRoot "env/$_.container.dev" })
$override = Join-Path $repoRoot 'env/container.verify.dev'
$routes = Join-Path $repoRoot 'env/container.routes.dev'
$ownedFiles += @($override, $routes)
foreach ($path in $ownedFiles) {
    if (Test-Path -LiteralPath $path) { throw 'Container verification requires a fresh generated directory without container configuration.' }
}
$compose = @('compose', '--project-name', $project, '-f', (Join-Path $repoRoot 'deployment/compose.yaml'), '-f', $override)
function Invoke-Docker([string[]]$Arguments) {
    $output = & docker @Arguments 2>&1
    $code = $LASTEXITCODE
    [IO.File]::AppendAllText($diagnostic, ($output -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    if ($code -ne 0) {
        # Build has no environment files in its context; compiler diagnostics are safe to show.
        if ($Arguments -contains 'build') { Write-Host ($output -join "`n") }
        throw "Container CLI failed ($($Arguments -join ' ')); private diagnostics: $diagnostic"
    }
    return ($output -join "`n")
}
function Write-Environment([string]$Service, [hashtable]$Settings) {
    $lines = @($Settings.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
    [IO.File]::WriteAllLines((Join-Path $repoRoot "env/$Service.container.dev"), $lines, [Text.UTF8Encoding]::new($false))
}
function Request([string]$Path, [int]$Status, [string]$Method = 'GET', $Body = $null, [hashtable]$Headers = @{}) {
    $parameters = @{ Uri = "$script:baseAddress$Path"; Method = $Method; Headers = $Headers; SkipHttpErrorCheck = $true; TimeoutSec = 5 }
    if ($null -ne $Body) { $parameters.Body = $Body | ConvertTo-Json -Compress; $parameters.ContentType = 'application/json' }
    $response = Invoke-WebRequest @parameters
    if ([int]$response.StatusCode -ne $Status) { throw "Unexpected HTTP $([int]$response.StatusCode) at $Path; expected $Status." }
    if ($response.Content) { return ($response.Content | ConvertFrom-Json) }
}
$started = $false
$savedPort = $env:NSN_GATEWAY_PORT
try {
    $env:NSN_GATEWAY_PORT = '0'
    $key = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    foreach ($service in $services) {
        $connection = [Data.Common.DbConnectionStringBuilder]::new()
        $connection['Host'] = 'database'
        $connection['Database'] = "nsn_$service"
        $connection['Username'] = 'postgres'
        $settings = @{
            Jwt__SigningKey = $key
            OperationJournal__Storage__Provider = 'Postgres'
            ConnectionStrings__OperationJournal = $connection.ConnectionString
        }
        if ($service -eq 'platform') {
            foreach ($context in @('Identity', 'Platform', 'Files', 'Auditing', 'Scheduling')) { $settings["ConnectionStrings__$context"] = $connection.ConnectionString }
            $settings['Identity__Root__UserName'] = 'container-root'
            $settings['Identity__Root__Password'] = $password
        }
        elseif ($service -eq 'costing') { $settings['ConnectionStrings__Costing'] = $connection.ConnectionString }
        elseif ($service -eq 'pricing') { $settings['ConnectionStrings__Pricing'] = $connection.ConnectionString }
        Write-Environment $service $settings
    }
    # Private to this generated project: no host port and no persistent database volume.
    $databaseCompose = @'
services:
  database:
    image: postgres:18
    environment:
      POSTGRES_HOST_AUTH_METHOD: trust
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres"]
      interval: 2s
      timeout: 2s
      retries: 30
'@
    [IO.File]::WriteAllText($override, $databaseCompose, [Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'prepare-container-routes.ps1')
    $started = $true
    Write-Output 'Building four explicit host images from the generated solution.'
    $null = Invoke-Docker ($compose + @('build'))
    $null = Invoke-Docker ($compose + @('up', '-d', '--wait', '--wait-timeout', '90', 'database'))
    foreach ($service in $services) {
        $null = Invoke-Docker ($compose + @('exec', '-T', 'database', 'createdb', '-U', 'postgres', "nsn_$service"))
    }
    foreach ($command in @('migrate-identity', 'migrate-platform', 'migrate-files', 'migrate-auditing', 'migrate-scheduling', 'migrate-operation-journal')) {
        $null = Invoke-Docker ($compose + @('run', '--rm', '--no-deps', 'platform', $command))
    }
    foreach ($service in @('costing', 'pricing')) {
        $null = Invoke-Docker ($compose + @('run', '--rm', '--no-deps', $service, "migrate-$service"))
        $null = Invoke-Docker ($compose + @('run', '--rm', '--no-deps', $service, 'migrate-operation-journal'))
    }
    $null = Invoke-Docker ($compose + @('run', '--rm', '--no-deps', 'gateway', 'migrate-operation-journal'))
    $null = Invoke-Docker ($compose + @('up', '-d', '--no-build'))
    foreach ($service in @('database') + $services) {
        $id = Invoke-Docker ($compose + @('ps', '-q', $service))
        if ([string]::IsNullOrWhiteSpace($id)) { throw "Missing running container: $service" }
        $container = @(Invoke-Docker @('inspect', $id) | ConvertFrom-Json)[0]
        $bindings = @($container.HostConfig.PortBindings.PSObject.Properties | Where-Object { @($_.Value).Count -gt 0 })
        if ($container.HostConfig.NetworkMode -eq 'host') { throw "Host networking bypasses the edge: $service" }
        if ($service -eq 'gateway') {
            if ($bindings.Count -ne 1 -or $bindings[0].Name -ne '8080/tcp') { throw 'Gateway must publish exactly its HTTP entry.' }
            $port = @($container.NetworkSettings.Ports.'8080/tcp')[0]
            if ($port.HostIp -ne '127.0.0.1') { throw 'Verification gateway must bind loopback.' }
            $script:baseAddress = "http://127.0.0.1:$($port.HostPort)"
        }
        elseif ($bindings.Count -ne 0) { throw "Non-edge container published a host port: $service" }
        if ($service -ne 'database') {
            $uid = Invoke-Docker @('exec', $id, 'id', '-u')
            if ($uid -eq '0') { throw "Application container runs as root: $service" }
        }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest "$script:baseAddress/health/ready" -SkipHttpErrorCheck -TimeoutSec 3
            if ([int]$response.StatusCode -eq 200) { $ready = $true; break }
        }
        catch [System.Net.Http.HttpRequestException] { }
        catch [System.Threading.Tasks.TaskCanceledException] { }
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) { throw 'Generated container gateway did not become ready.' }
    $null = Request '/api/identity' 200
    $null = Request '/api/pricing/tasks' 401
    $null = Request '/api/costing/batches' 401
    $login = Request '/api/identity/login' 200 'POST' @{ userName = 'container-root'; password = $password }
    if ([string]::IsNullOrWhiteSpace($login.data.accessToken)) { throw 'Login returned no access token.' }
    $headers = @{ Authorization = "Bearer $($login.data.accessToken)" }
    $null = Request '/api/identity/session/v1' 200 -Headers $headers
    $null = Request '/api/costing/batches' 200 -Headers $headers
    $itemId = [Guid]::NewGuid().ToString()
    $null = Request '/api/pricing/cost' 202 'POST' @{ requestId = [Guid]::NewGuid().ToString(); itemId = $itemId; expectedVersion = '0'; cost = 80; feeRate = 0.2 } $headers
    $quoteReady = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while ([DateTime]::UtcNow -lt $deadline) {
        $response = Invoke-WebRequest "$script:baseAddress/api/pricing/items/$itemId" -Headers $headers -SkipHttpErrorCheck -TimeoutSec 5
        if ([int]$response.StatusCode -eq 200) {
            $quote = $response.Content | ConvertFrom-Json
            if ($quote.data.cost -ne 80 -or $quote.data.feeRate -ne 0.2) { throw 'Unexpected persisted pricing result.' }
            $quoteReady = $true
            break
        }
        if ([int]$response.StatusCode -ne 404) { throw 'Unexpected pricing result while waiting for the worker.' }
        Start-Sleep -Milliseconds 200
    }
    if (-not $quoteReady) { throw 'Pricing worker did not finish the accepted task.' }
    Write-Output 'PASS: generated images, eleven migration commands, gateway readiness/login/business work, non-root execution and edge-only host binding.'
}
finally {
    if ($started) { $null = Invoke-Docker ($compose + @('down', '--volumes', '--remove-orphans')) }
    foreach ($path in $ownedFiles) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force } }
    $env:NSN_GATEWAY_PORT = $savedPort
}
