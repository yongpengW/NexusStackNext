Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Cross-process UTC conversion on Linux can use different cached boot-clock estimates.
# Use the kernel start counter, keeping PID reuse distinct without rounding wall time.
function Get-ProbeProcessStartStamp([int]$ProcessId) {
    if ($IsLinux) {
        $stat = [IO.File]::ReadAllText("/proc/$ProcessId/stat")
        $end = $stat.LastIndexOf(')')
        if ($end -lt 0) { throw 'Process identity is unavailable.' }
        # Fields after the parenthesized command begin at field 3; kernel start_time is field 22.
        $fields = $stat.Substring($end + 1).Trim() -split '\s+'
        if ($fields.Count -lt 20 -or $fields[19] -notmatch '^\d+$' -or [long]$fields[19] -le 0) { throw 'Process identity is unavailable.' }
        return 'linux:' + $fields[19]
    }
    return 'utc:' + (Get-Process -Id $ProcessId -ErrorAction Stop).StartTime.ToUniversalTime().Ticks
}

Export-ModuleMember -Function Get-ProbeProcessStartStamp
