Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Only read-only tracker requests belong here. Never emit gh output on failure.
function Invoke-GitHubRead([string]$GhPath, [string]$Route, [long]$Deadline, [int]$RequestTimeoutSeconds) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        $remaining = ($Deadline - [Diagnostics.Stopwatch]::GetTimestamp()) / [Diagnostics.Stopwatch]::Frequency
        if ($remaining -le 0) { throw 'GitHub read budget exhausted.' }
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        if ([IO.Path]::GetExtension($GhPath) -eq '.ps1') {
            $start.FileName = (Get-Command pwsh -ErrorAction Stop).Source
            foreach ($argument in @('-NoProfile', '-File', $GhPath)) { $start.ArgumentList.Add($argument) }
        } else { $start.FileName = $GhPath }
        foreach ($argument in @('api', $Route, '--method', 'GET', '--paginate', '--slurp')) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $start
        try {
            if (-not $process.Start()) { throw 'GitHub read could not start.' }
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            $milliseconds = [int][math]::Max(1, [math]::Floor([math]::Min($remaining, $RequestTimeoutSeconds) * 1000))
            if (-not $process.WaitForExit($milliseconds)) {
                $process.Kill($true)
                if (-not $process.WaitForExit(5000)) { throw 'GitHub read termination failed.' }
                throw 'GitHub read timed out.'
            }
            $output = $stdout.GetAwaiter().GetResult()
            $errorOutput = $stderr.GetAwaiter().GetResult()
            if ($process.ExitCode -eq 0) {
                $document = $null
                try {
                    $document = [Text.Json.JsonDocument]::Parse($output)
                    if ($document.RootElement.ValueKind -ne 'Array' -or $document.RootElement.GetArrayLength() -eq 0) { throw 'Missing pages.' }
                    foreach ($page in $document.RootElement.EnumerateArray()) {
                        if ($page.ValueKind -ne 'Array') { throw 'Invalid page.' }
                        foreach ($item in $page.EnumerateArray()) {
                            if ($item.ValueKind -ne 'Object') { throw 'Invalid record.' }
                        }
                    }
                }
                catch { throw 'GitHub read returned unusable pages; raw response withheld.' }
                finally { if ($null -ne $document) { $document.Dispose() } }
                return $output
            }
            # Match gh's diagnostic, not arbitrary JSON bodies or HTTP text on stdout.
            if ($attempt -eq 3 -or $errorOutput -notmatch '(?m)^gh: [^\r\n]*\(HTTP (502|503|504)\)\s*$') {
                throw 'GitHub read failed.'
            }
        }
        finally { $process.Dispose() }
        $delay = 250 * $attempt
        $remainingMilliseconds = ($Deadline - [Diagnostics.Stopwatch]::GetTimestamp()) / [Diagnostics.Stopwatch]::Frequency * 1000
        if ($remainingMilliseconds -le $delay) { throw 'GitHub read budget exhausted.' }
        Start-Sleep -Milliseconds $delay
    }
}

Export-ModuleMember -Function Invoke-GitHubRead
