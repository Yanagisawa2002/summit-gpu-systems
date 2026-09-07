[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ReportDirectory, [switch]$Comparison)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$config = Get-Content (Join-Path $ReportDirectory 'config.json') -Raw | ConvertFrom-Json
$runner = Get-Content (Join-Path $ReportDirectory 'runner-config.json') -Raw | ConvertFrom-Json
if ($Comparison) {
    foreach ($name in 'sourceHashesStableAcrossBuild','playerPayloadStableThroughRun','runnerConfigFinalized') {
        if (-not $runner.$name) { throw "Candidate comparison provenance gate failed: $name" }
    }
    if ($runner.sourceSnapshotSha256 -ne $runner.preBuildSourceSnapshotSha256) { throw 'Source hash drift.' }
    foreach ($name in 'gitStart','gitPostBuildAfterRestore','gitFinal') {
        $snapshot = $runner.$name
        if ($snapshot.dirty -or @($snapshot.statusLines).Count -ne 0 -or
            $snapshot.head -ne $runner.gitCommit -or $snapshot.branch -ne $runner.gitBranch) {
            throw "Candidate comparison requires clean, unchanged source identity: $name"
        }
    }
    if ($runner.gitCommit -notmatch '^[a-fA-F0-9]{40}$' -or $runner.gitBranch -eq 'HEAD') { throw 'Missing named commit identity.' }
    if (-not $config.requireCompleteGpuTimings -or $config.rounds -ne 3 -or
        $config.sampleFrames -ne 900 -or $config.localWarmupFrames -ne 60) {
        throw 'Comparison requires three counterbalanced rounds, 60 warmup, 900 samples and complete native GPU timings.'
    }
}
# Reuse the existing independent verifier for every raw token/tag, begin/end
# tick conversion, frequency/generation, fences, zero readback, counterbalance,
# complete round/case/sample matrix, and recomputation of block statistics.
# In non-legacy-formal mode it verifies config.selectedCases (opaque string IDs).
& (Join-Path $PSScriptRoot 'Summarize-GpuPrimitiveBenchmark.ps1') -ReportDirectory $ReportDirectory -RequireNativeIntegrity:($config.nativeTimestampBackendSelected -eq 'native-d3d12-timestamp-query') | Out-Null
& (Join-Path $PSScriptRoot 'Tests/Test-R9700PrimitiveCandidateOutput.ps1') -ReportDirectory $ReportDirectory | Out-Null
$raw = @(Import-Csv (Join-Path $ReportDirectory 'raw-frames.csv'))
$blocks = @(Import-Csv (Join-Path $ReportDirectory 'block-summary.csv'))
$resources = @(Import-Csv (Join-Path $ReportDirectory 'candidate-resources.csv'))
$caps = @(Import-Csv (Join-Path $ReportDirectory 'candidate-capabilities.csv'))
$supported = @($caps | Where-Object supported -eq 'True')
$expected = @('control/empty-command-buffer')
foreach ($op in @('exclusive-scan','stable-compaction','radix-sort-32','reduce-sum')) {
    foreach ($candidate in $supported) { $expected += "$op/$($candidate.candidateId)" }
    if ($op -ne 'reduce-sum') { $expected += "$op/portable"; if ($config.supportsWaveOperations) { $expected += "$op/wave-ops" } }
}
if (@(Compare-Object ($expected | Sort-Object) ($config.selectedCases | Sort-Object)).Count -ne 0) {
    throw 'Incomplete bounded candidate/baseline operation matrix.'
}
function Stats([double[]]$values) {
    if ($values.Count -eq 0 -or @($values | Where-Object { [double]::IsNaN($_) -or [double]::IsInfinity($_) -or $_ -lt 0 }).Count -gt 0) {
        throw 'Missing/invalid metric samples.'
    }
    $sorted = @($values | Sort-Object)
    return @{ Average = ($values | Measure-Object -Average).Average; P99 = $sorted[[Math]::Ceiling($sorted.Count * .99) - 1] }
}
$summary = foreach ($block in $blocks | Where-Object operation -ne 'control') {
    $rows = @($raw | Where-Object { $_.caseId -eq $block.caseId -and $_.round -eq $block.round })
    $gc = Stats ([double[]]@($rows.enqueueGcBytes))
    $resource = @($resources | Where-Object caseId -eq $block.caseId)[0]
    $reference = if ($block.operation -eq 'reduce-sum') { 'primitives-v1-portable-t128-e4-r4' }
        elseif ($config.supportsWaveOperations) { 'wave-ops' } else { 'portable' }
    $baseline = @($blocks | Where-Object { $_.operation -eq $block.operation -and $_.round -eq $block.round -and $_.variant -eq $reference })
    if ($baseline.Count -ne 1) { throw 'Missing same-round comparison reference.' }
    $gpuValid = @($rows | Where-Object gpuRegionTimingValid -ne '1').Count -eq 0
    [pscustomobject]@{
        mode = $(if ($Comparison) { 'comparison' } else { 'smoke-not-for-selection' })
        operation = $block.operation; candidateId = $block.variant; round = $block.round
        samples = $rows.Count; nativeGpuTimingComplete = $gpuValid
        gpuAverageMs = $(if ($gpuValid) { [double]$block.gpuRegionAverageMs } else { $null })
        gpuP99Ms = $(if ($gpuValid) { [double]$block.gpuRegionP99Ms } else { $null })
        enqueueCpuAverageMs = [double]$block.enqueueAverageMs; enqueueCpuP99Ms = [double]$block.enqueueP99Ms
        enqueueGcAverageBytes = $gc.Average; enqueueGcP99Bytes = $gc.P99
        frameAverageMs = [double]$block.frameAverageMs; frameP99Ms = [double]$block.frameP99Ms
        referenceId = $reference
        pairedGpuAverageDeltaMs = $(if ($gpuValid) { [double]$block.gpuRegionAverageMs - [double]$baseline[0].gpuRegionAverageMs } else { $null })
        pairedGpuP99DeltaMs = $(if ($gpuValid) { [double]$block.gpuRegionP99Ms - [double]$baseline[0].gpuRegionP99Ms } else { $null })
        dispatches = $resource.dispatchCount; scratchBytes = $resource.instanceScratchBytes
        candidateScratchBytes = $resource.candidateScratchBytes
    }
}
$summary | Export-Csv (Join-Path $ReportDirectory 'candidate-summary.csv') -NoTypeInformation
Write-Output "Candidate summary verified: $($summary.Count) per-round rows. Mode: $(if ($Comparison) {'comparison'} else {'smoke; no default selection'})."
