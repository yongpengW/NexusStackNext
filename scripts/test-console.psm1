function Get-TestConsoleCase([string]$Line) {
    $match = [regex]::Match($Line, '^\s*(Passed|Failed|Skipped|已通过|通过|失败|已跳过|跳过)\s+(?<method>NexusStackNext\.[A-Za-z0-9_.]+)(?:[\s(]|$)')
    if (-not $match.Success) { return $null }
    $outcome = switch ($match.Groups[1].Value) {
        { $_ -in @('Passed', '已通过', '通过') } { 'Passed' }
        { $_ -in @('Failed', '失败') } { 'Failed' }
        default { 'Skipped' }
    }
    [pscustomobject]@{ Method = $match.Groups['method'].Value; Outcome = $outcome }
}

Export-ModuleMember -Function Get-TestConsoleCase
