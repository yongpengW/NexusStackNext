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

function Get-TestContentionEvidence([string]$Output) {
    # Only a closed schema is public. No arbitrary error codes, messages, IDs or payloads.
    $pattern = '^NSN_CONTENTION (recovery busy=(True|False) unavailable=(True|False) succeeded=(True|False)|cleanup busy=(True|False) canceled=(True|False)|pricing phase=(initial|cost_refusal|fee_refusal|missing_task|recovered) status=[1-5][0-9]{2})$'
    foreach ($line in ($Output -split '\r?\n')) {
        if ($line -cmatch $pattern) { $line }
    }
}

Export-ModuleMember -Function Get-TestConsoleCase, Get-TestContentionEvidence
