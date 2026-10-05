Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'test-ownership.psm1') -Force

function Deny-LocalPostgres([string]$Stage) { throw "LOCAL_POSTGRES_REJECTED stage=$Stage" }

function Read-LocalPostgresState($Context) {
    $file = Get-Item -LiteralPath $Context.StateFile -ErrorAction Stop
    if ($file.Length -eq 0 -or $file.Length -gt 16384) { Deny-LocalPostgres 'state' }
    $state = [IO.File]::ReadAllText($Context.StateFile) | ConvertFrom-Json -NoEnumerate
    if ($null -eq $state -or $state.GetType() -ne [System.Management.Automation.PSCustomObject] -or
        $state.Version -isnot [long] -or $state.Version -ne 1 -or $state.State -isnot [string] -or
        $state.State -notin @('stopped', 'running') -or $state.DataDirectory -isnot [string] -or
        $state.DataDirectory -ine $Context.DataDirectory -or $state.RuntimeDirectory -isnot [string] -or
        $state.Role -isnot [string] -or $state.Role -notmatch '^nsn_test_[a-f0-9]{16}$' -or
        $state.Port -isnot [long] -or $state.Port -lt 1 -or $state.Port -gt 65535 -or
        $state.RuntimeVersion -isnot [string] -or $state.RuntimeVersion -notmatch '^18\.\d+$' -or
        $null -eq $state.ArtifactHashes -or $state.ArtifactHashes.GetType() -ne [System.Management.Automation.PSCustomObject]) { Deny-LocalPostgres 'state' }
    return $state
}

function Write-LocalPostgresState($Context, $State) {
    $temporary = Join-Path $Context.Root ('state-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    [IO.File]::WriteAllText($temporary, (ConvertTo-Json -InputObject $State -Depth 5 -Compress), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary, $Context.StateFile, $true)
}

function Get-LocalPostgresArtifactHashes($Context, $Expected = $null) {
    $data = Get-Item -LiteralPath $Context.DataDirectory -Force -ErrorAction Stop
    if (-not $data.PSIsContainer -or ($data.Attributes -band [IO.FileAttributes]::ReparsePoint)) { Deny-LocalPostgres 'cluster-drift' }
    $hashes = [ordered]@{}
    foreach ($relative in @('data/postgresql.conf', 'data/postgresql.auto.conf', 'data/pg_hba.conf', 'password.private', 'pgpass.private', 'connection.private')) {
        $path = Join-Path $Context.Root $relative
        $file = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { Deny-LocalPostgres 'cluster-drift' }
        $hashes[$relative] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($null -ne $Expected -and ($Expected.$relative -isnot [string] -or $Expected.$relative -cne $hashes[$relative])) { Deny-LocalPostgres 'cluster-drift' }
    }
    return $hashes
}

function Invoke-LocalPostgresCommand($Context, [string]$Name, [string[]]$Arguments, [bool]$Connect = $false) {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $Context.RuntimeDirectory "$Name.exe"))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    foreach ($key in @($start.Environment.Keys | Where-Object { $_.StartsWith('PG', [StringComparison]::OrdinalIgnoreCase) })) { [void]$start.Environment.Remove($key) }
    if ($Connect) {
        $start.Environment['PGHOST'] = '127.0.0.1'
        $start.Environment['PGPORT'] = [string]$Context.State.Port
        $start.Environment['PGDATABASE'] = 'postgres'
        $start.Environment['PGUSER'] = $Context.State.Role
        $start.Environment['PGPASSFILE'] = Join-Path $Context.Root 'pgpass.private'
        $start.Environment['PGCONNECT_TIMEOUT'] = '5'
    }
    $Context.CommandReturned = $false
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill() # Only the command we started; an uncertain detached server is not adopted.
            [void]$process.WaitForExit(5000)
            Deny-LocalPostgres 'command-timeout'
        }
        if (-not [Threading.Tasks.Task]::WhenAll([Threading.Tasks.Task[]]@($output, $errorOutput)).Wait(1000)) { Deny-LocalPostgres 'output-timeout' }
        $Context.CommandReturned = $true
        [IO.File]::WriteAllText((Join-Path $Context.Root "$Name-command.private.log"), $output.Result + $errorOutput.Result)
        if ($process.ExitCode -ne 0) { Deny-LocalPostgres 'command' }
        return $output.Result.Trim()
    }
    finally { $process.Dispose() }
}

function Get-LocalPostgresRuntime($Context, [string]$RuntimeDirectory, $Expected = $null) {
    $Context.RuntimeDirectory = [IO.Path]::GetFullPath($RuntimeDirectory)
    $hashes = [ordered]@{}
    foreach ($name in @('postgres', 'initdb', 'pg_ctl', 'psql')) {
        $path = Join-Path $Context.RuntimeDirectory "$name.exe"
        $hashes[$name] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($null -ne $Expected -and ($Expected.Hashes.$name -isnot [string] -or $Expected.Hashes.$name -cne $hashes[$name])) { Deny-LocalPostgres 'runtime-changed' }
    }
    if ($null -ne $Expected) { return $Expected.RuntimeVersion }
    $version = Invoke-LocalPostgresCommand $Context 'postgres' @('--version')
    if ($version -notmatch '^postgres \(PostgreSQL\) (18\.\d+)$') { Deny-LocalPostgres 'runtime-version' }
    $Context.Hashes = $hashes
    return $Matches[1]
}

function Get-LocalPostgresProcess($Context, [bool]$MatchRecorded = $true) {
    $lines = [IO.File]::ReadAllLines((Join-Path $Context.DataDirectory 'postmaster.pid'))
    $processId = 0
    if ($lines.Count -lt 8 -or -not [int]::TryParse($lines[0], [ref]$processId) -or $processId -le 0 -or
        [IO.Path]::GetFullPath($lines[1]) -ine $Context.DataDirectory -or $lines[3] -cne [string]$Context.State.Port -or
        $lines[5] -cne '127.0.0.1' -or $lines[7].Trim() -cne 'ready') { Deny-LocalPostgres 'identity' }
    $process = Get-Process -Id $processId -ErrorAction Stop
    try {
        # Pin the Windows process handle before checking identity and issuing pg_ctl stop.
        [void]$process.SafeHandle
        if ($process.HasExited -or $process.Path -ine (Join-Path $Context.RuntimeDirectory 'postgres.exe') -or
            ($MatchRecorded -and ($Context.State.Pid -isnot [long] -or $Context.State.Pid -ne $processId -or
                $Context.State.StartedUtcTicks -isnot [long] -or $Context.State.StartedUtcTicks -ne $process.StartTime.ToUniversalTime().Ticks))) { Deny-LocalPostgres 'identity' }
        return $process
    }
    catch { $process.Dispose(); throw }
}

function Assert-LocalPostgresReady($Context) {
    $query = "SELECT current_setting('data_directory'), current_setting('port'), current_setting('listen_addresses'), current_setting('fsync'), current_setting('synchronous_commit'), current_setting('full_page_writes'), current_setting('TimeZone'), current_user, current_database();"
    $value = Invoke-LocalPostgresCommand $Context 'psql' @('-X', '-A', '-t', '-F', '|', '-v', 'ON_ERROR_STOP=1', '-c', $query) $true
    $fields = $value.Split('|')
    if ($fields.Count -ne 9 -or [IO.Path]::GetFullPath($fields[0]) -ine $Context.DataDirectory -or
        $fields[1] -cne [string]$Context.State.Port -or $fields[2] -cne '127.0.0.1' -or $fields[3] -cne 'on' -or
        $fields[4] -cne 'on' -or $fields[5] -cne 'on' -or $fields[6] -cne 'UTC' -or
        $fields[7] -cne $Context.State.Role -or $fields[8] -cne 'postgres') { Deny-LocalPostgres 'readiness' }
}

function Invoke-LocalPostgres([string]$Action, [string]$RuntimeDirectory, [string]$StateDirectory, [string]$Configuration, [string]$Filter, [bool]$NoBuild) {
    if ($Action -in @('Start', 'Test')) { throw 'LOCAL_POSTGRES_DISABLED: use the shared test configuration.' }
    if ($Action -notin @('Status', 'Stop')) { Deny-LocalPostgres 'action' }
    $root = [IO.Path]::GetFullPath($StateDirectory)
    $allowed = @([IO.Path]::GetTempPath(), [Environment]::GetFolderPath('LocalApplicationData'))
    if (-not @($allowed | Where-Object { $root.StartsWith([IO.Path]::GetFullPath($_).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) }).Count) { Deny-LocalPostgres 'directory' }
    $ancestor = $root
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { Deny-LocalPostgres 'directory' }
        }
        $parent = [IO.Directory]::GetParent($ancestor)
        $ancestor = if ($null -eq $parent) { $null } else { $parent.FullName }
    }
    if (-not (Test-Path -LiteralPath $root)) { Deny-LocalPostgres 'missing-instance' }
    $item = Get-Item -LiteralPath $root -Force
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl = Get-Acl -LiteralPath $root
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $acl.GetOwner([Security.Principal.SecurityIdentifier]) -ne $sid -or
        -not $acl.AreAccessRulesProtected -or @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | Where-Object { $_.IdentityReference -ne $sid -or $_.AccessControlType -ne 'Allow' }).Count -ne 0) { Deny-LocalPostgres 'directory-permissions' }
    $context = @{ Root = $root; DataDirectory = Join-Path $root 'data'; StateFile = Join-Path $root 'owner.json'; CommandReturned = $true }
    $guard = [IO.File]::Open((Join-Path $root 'operation.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $testOwnership = $null
    try {
        if ($Action -eq 'Stop') { $testOwnership = Enter-TestOwnership }
        $context.State = Read-LocalPostgresState $context
        [void](Get-LocalPostgresArtifactHashes $context $context.State.ArtifactHashes)
        if ($RuntimeDirectory -and [IO.Path]::GetFullPath($RuntimeDirectory) -ine $context.State.RuntimeDirectory) { Deny-LocalPostgres 'runtime-changed' }
        [void](Get-LocalPostgresRuntime $context $context.State.RuntimeDirectory $context.State)
        if ($context.State.State -eq 'running') {
            $process = Get-LocalPostgresProcess $context
            try {
                Assert-LocalPostgresReady $context
                if ($Action -eq 'Stop') {
                    [void](Invoke-LocalPostgresCommand $context 'pg_ctl' @('-D', $context.DataDirectory, '-m', 'fast', '-w', '-t', '30', 'stop'))
                    if (-not $process.WaitForExit(5000) -or (Test-Path -LiteralPath (Join-Path $context.DataDirectory 'postmaster.pid'))) { Deny-LocalPostgres 'uncertain-server' }
                    $context.State.State = 'stopped'
                    Write-LocalPostgresState $context $context.State
                }
            }
            finally { $process.Dispose() }
        } elseif (Test-Path -LiteralPath (Join-Path $context.DataDirectory 'postmaster.pid')) { Deny-LocalPostgres 'uncertain-server' }
        [pscustomobject]@{ State = $context.State.State; Version = $context.State.RuntimeVersion; Port = $context.State.Port; ConnectionFile = Join-Path $root 'connection.private' } | ConvertTo-Json -Compress
    }
    finally {
        if ($null -ne $testOwnership) { Exit-TestOwnership $testOwnership $context.CommandReturned }
        $guard.Dispose()
    }
}

Export-ModuleMember -Function Invoke-LocalPostgres
