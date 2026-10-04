Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The open handle is the atomic guard. The persistent state closes the orphan-process gap.
function Enter-TestOwnership {
    $path = Join-Path ([IO.Path]::GetTempPath()) 'nexusstack-run-tests.lock'
    $created = $false
    try {
        try {
            $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $created = $true
        }
        catch [IO.IOException] { $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    }
    catch { throw 'TEST_OWNERSHIP_REJECTED: another owner holds the guard, or protection is unavailable.' }
    try {
        if (-not $created -and $stream.Length -eq 0) { throw 'Existing empty guard has no completion proof.' }
        if ($stream.Length -gt 0) {
            if ($stream.Length -gt 4096) { throw 'Invalid guard state.' }
            $bytes = [byte[]]::new([int]$stream.Length)
            $stream.ReadExactly($bytes)
            $state = [Text.UTF8Encoding]::new($false, $true).GetString($bytes) | ConvertFrom-Json -NoEnumerate -ErrorAction Stop
            if ($null -eq $state -or $state.GetType() -ne [System.Management.Automation.PSCustomObject] -or $state.Version -isnot [long] -or $state.Version -ne 1 -or $state.State -isnot [string] -or $state.State -ne 'idle') { throw 'Prior workload termination is unproven.' }
        }
        $owner = [ordered]@{ Version = 1; State = 'active'; Pid = $PID; StartedUtcTicks = [Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().Ticks }
        Set-TestOwnershipState $stream $owner
        return $stream
    }
    catch {
        $stream.Dispose()
        throw 'TEST_OWNERSHIP_REJECTED: prior workload termination is unproven; inspect recovery instructions.'
    }
}

function Set-TestOwnershipState([IO.FileStream]$Stream, $State) {
    $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $State -Compress))
    $Stream.Position = 0
    $Stream.SetLength(0)
    $Stream.Write($bytes)
    $Stream.Flush($true)
}

function Exit-TestOwnership([IO.FileStream]$Stream, [bool]$WorkloadReturned) {
    try {
        if ($WorkloadReturned) { Set-TestOwnershipState $Stream ([ordered]@{ Version = 1; State = 'idle' }) }
    }
    finally { $Stream.Dispose() }
}

Export-ModuleMember -Function Enter-TestOwnership, Exit-TestOwnership
