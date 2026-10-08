# Public partition CLI: known journeys, no database or private configuration.
$ErrorActionPreference = 'Stop'
$tool = Join-Path $PSScriptRoot 'ci-test-tools.ps1'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('nsn-ci-partition-probes-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
$methods = @(
    'NexusStackNext.HostIntegration.Tests.FactRecoveryBrokerJourneyTests.Costing_StoppedFactsRecoverThroughRealBroker_AndSurviveBothProcessRestarts'
    'NexusStackNext.HostIntegration.Tests.FactRecoveryBrokerJourneyTests.Files_StoppedFactsRecoverThroughRealBroker_AndSurviveBothProcessRestarts'
    'NexusStackNext.HostIntegration.Tests.FactRecoveryBrokerJourneyTests.FourMemorySources_RecoverBothFactKinds_AndCentralEvidenceSurvivesProducerDisposalAndCentralRestart'
    'NexusStackNext.HostIntegration.Tests.FactRecoveryBrokerJourneyTests.Identity_StoppedFactsRecoverThroughRealBroker_AndSurviveBothProcessRestarts'
    'NexusStackNext.HostIntegration.Tests.FactRecoveryBrokerJourneyTests.Platform_StoppedFactsRecoverThroughRealBroker_AndSurviveBothProcessRestarts'
    'NexusStackNext.HostIntegration.Tests.FactRecoveryBrokerJourneyTests.Pricing_StoppedFactsRecoverThroughRealBroker_AndSurviveBothProcessRestarts'
    'NexusStackNext.HostIntegration.Tests.FactRecoveryBrokerJourneyTests.Scheduling_StoppedFactsRecoverThroughRealBroker_AndSurviveBothProcessRestarts'
    'NexusStackNext.HostIntegration.Tests.FilesPersistenceJourneyTests.AcceptedDeletion_ResumesAfterStorageRecoveryAndProcessRestart'
    'NexusStackNext.HostIntegration.Tests.FilesPersistenceJourneyTests.ConcurrentDeletes_BothObserveDurableDeletion'
    'NexusStackNext.HostIntegration.Tests.FilesPersistenceJourneyTests.FileAudit_SoftDeletionPreservesCreationAndRecordsTheDeletingActor'
    'NexusStackNext.HostIntegration.Tests.FilesPersistenceJourneyTests.FilesDatabaseOutage_ChangesReadiness_WithoutStoppingIdentity_AndRecovers'
    'NexusStackNext.HostIntegration.Tests.FilesPersistenceJourneyTests.FirstCleanupAttempt_IsNotStarvedByDueFailingRetries'
    'NexusStackNext.HostIntegration.Tests.FilesPersistenceJourneyTests.ProcessDiesDuringCommit_RecoveryKeepsBytesForAnyCommittedMetadata'
    'NexusStackNext.HostIntegration.Tests.FilesPersistenceJourneyTests.Recovery_PreservesLiveUpload_AndRemovesInterruptedUploadAfterRestart'
    'NexusStackNext.HostIntegration.Tests.FilesPersistenceJourneyTests.RejectedMetadataCommit_LeavesNoPublishedFile_AndRecoversOrphanBytes'
    'NexusStackNext.HostIntegration.Tests.FilesPersistenceJourneyTests.ReplacedStorageDirectory_DoesNotFalselyConfirmDeletion'
    'NexusStackNext.HostIntegration.Tests.FilesPersistenceJourneyTests.UnmigratedFilesDatabase_RefusesStartup_UntilIndependentMigrationRuns'
    'NexusStackNext.HostIntegration.Tests.FilesPersistenceJourneyTests.UnremovableOrphan_DoesNotBlockOtherOrphanRecovery'
    'NexusStackNext.HostIntegration.Tests.FilesPersistenceJourneyTests.UnwritableOrphanProtection_DoesNotBlockOtherOrphanRecovery'
    'NexusStackNext.HostIntegration.Tests.FilesPersistenceJourneyTests.UploadedPrivateFile_RemainsOwnedAndDownloadable_AfterProcessRestart'
    'NexusStackNext.HostIntegration.Tests.ScheduledCostBusinessJourneyTests.CostingCrashBeforeReceiptCommit_RollsBackTaskAndInbox_AndRedeliveryRecovers'
    'NexusStackNext.HostIntegration.Tests.ScheduledCostBusinessJourneyTests.GatewayCalendar_SurvivesCostingOutageAndCreatorLogout_ThenCompletesThroughPricing'
    'NexusStackNext.HostIntegration.Tests.ScheduledCostBusinessJourneyTests.GatewaySchedule_SurvivesCostingOutageAndCreatorLogout_ThenCompletesWithoutInventingBusinessVersions'
)
$inventory = @($methods + @('Example.AlphaTests.First', 'Example.AlphaTests.Second', 'Example.BetaTests.First', 'Example.GammaTests.First') | ForEach-Object {
    @{ Project = 'HostIntegration.Tests'; Method = $_; Id = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($_))) }
})
$inventory += @{ Project = 'Unit.Tests'; Method = 'Example.UnitTests.First'; Id = ('e' * 64) }
$inputPath = Join-Path $scratch 'inventory.json'
$outputPath = Join-Path $scratch 'plan.json'
[IO.File]::WriteAllText($inputPath, (ConvertTo-Json -InputObject $inventory -Depth 10), [Text.UTF8Encoding]::new($false))
& pwsh -NoProfile -File $tool -Action Plan -InputPath $inputPath -OutputPath $outputPath
if ($LASTEXITCODE -ne 0) { throw 'Partition probe planning failed.' }
$plan = @(Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json)
if (@($plan | Where-Object Project -CEQ 'HostIntegration.Tests' | Select-Object -ExpandProperty Shard -Unique).Count -ne 4 -or
    @($plan | Where-Object Project -CNE 'HostIntegration.Tests' | Where-Object Shard -NE 0).Count -ne 0) {
    throw 'All four isolated shards must share host journeys while other projects remain on shard zero.'
}
foreach ($class in @('FactRecoveryBrokerJourneyTests', 'FilesPersistenceJourneyTests', 'ScheduledCostBusinessJourneyTests')) {
    if (@($plan | Where-Object Method -Like "*.$class.*" | Select-Object -ExpandProperty Shard -Unique).Count -lt 2) {
        throw "Large journey class still blocks one shard: $class"
    }
}
Write-Output 'PASS: declared large journey classes split across host shards.'

function Invoke-Plan([string]$ToolPath, [bool]$Success) {
    $output = & pwsh -NoProfile -File $ToolPath -Action Plan -InputPath $inputPath -OutputPath $outputPath 2>&1
    if (($LASTEXITCODE -eq 0) -ne $Success) { throw 'Unexpected partition CLI result.' }
    if (-not $Success -and ($output -join "`n") -notmatch 'CI test plan or report verification failed') { throw 'Partition rejection must remain redacted.' }
}
function Write-Inventory([object[]]$Items) {
    [IO.File]::WriteAllText($inputPath, (ConvertTo-Json -InputObject $Items -Depth 10), [Text.UTF8Encoding]::new($false))
}
$originalPlan = ConvertTo-Json -InputObject $plan -Depth 10 -Compress
Write-Inventory @($inventory | Sort-Object Id -Descending)
Invoke-Plan $tool $true
if ((ConvertTo-Json -InputObject @(Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json) -Depth 10 -Compress) -cne $originalPlan) { throw 'Partition depends on input order.' }
if ($plan.Count -ne 28 -or @($plan.Id | Select-Object -Unique).Count -ne 28 -or @($plan | Where-Object Project -CNE 'HostIntegration.Tests' | Where-Object Shard -EQ 0).Count -ne 1 -or
    @($plan | Where-Object Method -Like 'Example.AlphaTests.*' | Select-Object -ExpandProperty Shard -Unique).Count -ne 1) { throw 'Method partition lost a case or split an undeclared class.' }

# The same method represents three Theory cases, including future parameters.
$theoryMethod = $methods[0]
$expanded = $inventory + @(
    @{ Project = 'HostIntegration.Tests'; Method = $theoryMethod; Id = ('1' * 64) },
    @{ Project = 'HostIntegration.Tests'; Method = $theoryMethod; Id = ('2' * 64) },
    @{ Project = 'HostIntegration.Tests'; Method = 'NexusStackNext.HostIntegration.Tests.FilesPersistenceJourneyTests.FutureJourney'; Id = ('3' * 64) }
)
Write-Inventory $expanded
Invoke-Plan $tool $true
$expandedPlan = @(Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json)
if ($expandedPlan.Count -ne 31 -or @($expandedPlan | Where-Object Method -CEQ $theoryMethod).Count -ne 3 -or
    @($expandedPlan | Where-Object Method -CEQ $theoryMethod | Select-Object -ExpandProperty Shard -Unique).Count -ne 1 -or
    @($expandedPlan | Where-Object Id -CEQ ('3' * 64) | Where-Object Shard -In 0, 1, 2, 3).Count -ne 1) { throw 'Theory parameters split or a new method disappeared.' }
Write-Output 'PASS: deterministic complete plan, undeclared classes intact, Theory cases together, future method included.'

# Mutate a disposable copy of the public CLI; the repository policy is never changed by a probe.
foreach ($name in @('ci-test-tools.ps1', 'ci-test-support.psm1', 'ci-test-weights.json', 'ci-test-partitions.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $scratch $name)
}
$copyTool = Join-Path $scratch 'ci-test-tools.ps1'
$policyPath = Join-Path $scratch 'ci-test-partitions.json'
$originalPolicy = Get-Content -LiteralPath $policyPath -Raw
Write-Inventory $inventory
Invoke-Plan $copyTool $true
foreach ($mutation in @('empty', 'invalid-root', 'empty-class', 'unknown-class', 'unknown-method', 'foreign-method', 'zero', 'negative', 'string', 'boolean')) {
    $policy = $originalPolicy | ConvertFrom-Json -AsHashtable
    $class = 'NexusStackNext.HostIntegration.Tests.FilesPersistenceJourneyTests'
    $method = 'NexusStackNext.HostIntegration.Tests.FilesPersistenceJourneyTests.AcceptedDeletion_ResumesAfterStorageRecoveryAndProcessRestart'
    switch ($mutation) {
        'empty' { $policy = @{} }
        'invalid-root' { $policy = @('invalid') }
        'empty-class' { $policy[$class] = @{} }
        'unknown-class' { $policy['Example.MissingTests'] = @{ 'Example.MissingTests.First' = 1.0 } }
        'unknown-method' { $policy[$class]["$class.MissingJourney"] = 1.0 }
        'foreign-method' { $policy[$class]['Example.OtherTests.First'] = 1.0 }
        'zero' { $policy[$class][$method] = 0 }
        'negative' { $policy[$class][$method] = -1 }
        'string' { $policy[$class][$method] = '1' }
        'boolean' { $policy[$class][$method] = $true }
    }
    [IO.File]::WriteAllText($policyPath, (ConvertTo-Json -InputObject $policy -Depth 10), [Text.UTF8Encoding]::new($false))
    Invoke-Plan $copyTool $false
}
[IO.File]::WriteAllText($policyPath, $originalPolicy, [Text.UTF8Encoding]::new($false))
Invoke-Plan $copyTool $true
Write-Output 'PASS: invalid, stale or empty declarations rejected; restored policy accepted.'

$weightPath = Join-Path $scratch 'ci-test-weights.json'
$originalWeights = Get-Content -LiteralPath $weightPath -Raw
foreach ($mutation in @('empty', 'invalid-root', 'invalid-key', 'zero', 'negative', 'string', 'boolean')) {
    $weights = $originalWeights | ConvertFrom-Json -AsHashtable
    switch ($mutation) {
        'empty' { $weights = @{} }
        'invalid-root' { $weights = @('invalid') }
        'invalid-key' { $weights['invalid key'] = 1.0 }
        'zero' { $weights['Unit.Tests'] = 0 }
        'negative' { $weights['Unit.Tests'] = -1 }
        'string' { $weights['Unit.Tests'] = '1' }
        'boolean' { $weights['Unit.Tests'] = $true }
    }
    [IO.File]::WriteAllText($weightPath, (ConvertTo-Json -InputObject $weights -Depth 10), [Text.UTF8Encoding]::new($false))
    Invoke-Plan $copyTool $false
}
$weights = $originalWeights | ConvertFrom-Json -AsHashtable
$weights['Unit.Tests'] = 1000000.0
[IO.File]::WriteAllText($weightPath, (ConvertTo-Json -InputObject $weights -Depth 10), [Text.UTF8Encoding]::new($false))
Invoke-Plan $copyTool $true
$reservedPlan = @(Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json)
if (@($reservedPlan | Where-Object Project -CEQ 'HostIntegration.Tests' | Where-Object Shard -EQ 0).Count -ne 0 -or
    @($reservedPlan | Where-Object Project -CNE 'HostIntegration.Tests' | Where-Object Shard -EQ 0).Count -ne 1) {
    throw 'Planner ignored the measured cost of other projects on shard zero.'
}
[IO.File]::WriteAllText($weightPath, $originalWeights, [Text.UTF8Encoding]::new($false))
Invoke-Plan $copyTool $true
if ((ConvertTo-Json -InputObject @(Get-Content -LiteralPath $outputPath -Raw | ConvertFrom-Json) -Depth 10 -Compress) -cne $originalPlan) {
    throw 'Restored weights did not restore the original deterministic plan.'
}
Write-Output 'PASS: invalid weights rejected, other project cost reserved, restored weights recover the plan.'

$resolvedScratch = [IO.Path]::GetFullPath($scratch)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedScratch.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($resolvedScratch) -notmatch '^nsn-ci-partition-probes-[a-f0-9]{32}$') { throw 'Unexpected partition probe cleanup target.' }
Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
exit 0
