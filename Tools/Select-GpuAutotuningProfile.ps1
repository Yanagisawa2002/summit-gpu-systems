[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReportDirectory,
    [ValidateRange(1, 6)]
    [int]$CalibrationRounds = 2,
    [switch]$FormalAcceptance
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [System.IO.Path]::GetFullPath($ReportDirectory)
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$minimumAverageImprovementPercent = 3.0
$minimumAbsoluteReductionMs = 0.005
$minimumPositiveWinFraction = 0.75
$minimumP99ImprovementPercent = -5.0
$calibrationMinimumP99ImprovementPercent = -2.0

function Number($value, [string]$label) {
    try {
        return [double]::Parse(
            [string]$value,
            [System.Globalization.NumberStyles]::Float,
            $culture)
    }
    catch {
        throw "Unable to parse $label='$value'."
    }
}
function Average([double[]]$values) {
    if ($values.Count -eq 0) { throw 'Cannot average an empty sample set.' }
    return ($values | Measure-Object -Average).Average
}
function Percentile([double[]]$values, [double]$fraction) {
    if ($values.Count -eq 0) { throw 'Cannot percentile an empty sample set.' }
    [double[]]$sorted = @($values | Sort-Object)
    $index = [Math]::Max(
        0,
        [int][Math]::Ceiling($sorted.Count * $fraction) - 1)
    return $sorted[$index]
}
function Median([double[]]$values) { return Percentile $values 0.5 }
function Improvement([double]$baseline, [double]$candidate) {
    if ($baseline -le 0.0) { return 0.0 }
    return (($baseline - $candidate) / $baseline) * 100.0
}
function Require-File([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required evidence file is missing: $path"
    }
    return $path
}

$runner = Get-Content -Raw -LiteralPath (
    Require-File (Join-Path $root 'runner-config.json')) | ConvertFrom-Json
$device = Get-Content -Raw -LiteralPath (
    Require-File (Join-Path $root 'device.json')) | ConvertFrom-Json
$blocks = @(Import-Csv -LiteralPath (
    Require-File (Join-Path $root 'block-summary.csv')))
$raw = @(Import-Csv -LiteralPath (
    Require-File (Join-Path $root 'raw-frames.csv')))
$validation = @(Import-Csv -LiteralPath (
    Require-File (Join-Path $root 'validation.csv')))
$runSummary = @{}
foreach ($line in Get-Content -LiteralPath (
        Require-File (Join-Path $root 'run-summary.txt'))) {
    $parts = $line -split '=', 2
    if ($parts.Count -eq 2) { $runSummary[$parts[0]] = $parts[1] }
}
if ($runSummary.passed -ne '1' -or
    [int]$runSummary.validationFailures -ne 0 -or
    [int64]$runSummary.measurementReadbackBytes -ne 0) {
    throw 'Primitive source benchmark did not pass its correctness/timing gates.'
}
if (@($validation | Where-Object { [int]$_.passed -ne 1 }).Count -ne 0) {
    throw 'Candidate correctness validation contains failures.'
}

$operations = @(
    $blocks |
        Where-Object { $_.operation -ne 'control' } |
        Select-Object -ExpandProperty operation -Unique |
        Sort-Object)
if ($operations.Count -eq 0) { throw 'No candidate workloads were found.' }
[int]$roundCount = [int]$runner.rounds
if ($CalibrationRounds -ge $roundCount) {
    throw 'CalibrationRounds must leave at least one independent evaluation round.'
}

$profileRows = [System.Collections.Generic.List[object]]::new()
$evaluationRows = [System.Collections.Generic.List[object]]::new()
foreach ($operation in $operations) {
    $operationValidation = @(
        $validation | Where-Object { $_.operation -eq $operation })
    foreach ($phase in @('warmup', 'final')) {
        $phaseRows = @($operationValidation | Where-Object { $_.phase -eq $phase })
        $hashes = @($phaseRows | Select-Object -ExpandProperty resultHash -Unique)
        if ($phaseRows.Count -ne 2 -or $hashes.Count -ne 1) {
            throw "$operation $phase validation does not prove equivalent outputs."
        }
    }

    $candidateStats = @{}
    foreach ($variant in @('portable', 'wave-ops')) {
        $samples = @(
            $raw | Where-Object {
                $_.operation -eq $operation -and
                $_.variant -eq $variant -and
                [int]$_.round -le $CalibrationRounds -and
                [int]$_.gpuRegionTimingValid -eq 1
            } | ForEach-Object {
                Number $_.nativeTimestampElapsedMs (
                    "$operation/$variant calibration GPU ms")
            })
        if ($samples.Count -eq 0) {
            throw "Missing calibration samples for $operation/$variant."
        }
        $candidateStats[$variant] = [pscustomobject]@{
            samples = $samples.Count
            median = Median ([double[]]$samples)
            p99 = Percentile ([double[]]$samples) 0.99
        }
    }

    $baseline = $candidateStats['portable']
    $wave = $candidateStats['wave-ops']
    $waveImprovement = Improvement $baseline.median $wave.median
    $waveAbsoluteReduction = $baseline.median - $wave.median
    $waveP99Improvement = Improvement $baseline.p99 $wave.p99
    $calibrationMeetsUpgradeGate =
        $waveImprovement -ge $minimumAverageImprovementPercent -and
        $waveAbsoluteReduction -ge $minimumAbsoluteReductionMs -and
        $waveP99Improvement -ge $calibrationMinimumP99ImprovementPercent
    $selectedVariant = if ($calibrationMeetsUpgradeGate) {
        'wave-ops'
    } else {
        'portable'
    }
    $selected = $candidateStats[$selectedVariant]

    $gpuAverageImprovements = [System.Collections.Generic.List[double]]::new()
    $gpuP99Improvements = [System.Collections.Generic.List[double]]::new()
    $absoluteReductions = [System.Collections.Generic.List[double]]::new()
    $positiveWins = 0
    $candidateGpuAverageImprovements =
        [System.Collections.Generic.List[double]]::new()
    $candidateGpuP99Improvements =
        [System.Collections.Generic.List[double]]::new()
    $candidateAbsoluteReductions =
        [System.Collections.Generic.List[double]]::new()
    $candidatePositiveWins = 0
    for ($round = $CalibrationRounds + 1; $round -le $roundCount; $round++) {
        $portable = @($blocks | Where-Object {
            $_.operation -eq $operation -and
            $_.variant -eq 'portable' -and
            [int]$_.round -eq $round })
        $waveCandidate = @($blocks | Where-Object {
            $_.operation -eq $operation -and
            $_.variant -eq 'wave-ops' -and
            [int]$_.round -eq $round })
        if ($portable.Count -ne 1 -or $waveCandidate.Count -ne 1) {
            throw "$operation evaluation round $round is incomplete."
        }
        $baselineAverage = Number $portable[0].gpuRegionAverageMs 'baseline average'
        $waveAverage = Number `
            $waveCandidate[0].gpuRegionAverageMs `
            'wave candidate average'
        $baselineP99 = Number $portable[0].gpuRegionP99Ms 'baseline p99'
        $waveP99 = Number $waveCandidate[0].gpuRegionP99Ms 'wave candidate p99'
        $selectedAverage = if ($selectedVariant -eq 'wave-ops') {
            $waveAverage
        } else {
            $baselineAverage
        }
        $selectedP99 = if ($selectedVariant -eq 'wave-ops') {
            $waveP99
        } else {
            $baselineP99
        }
        $averageImprovement = Improvement $baselineAverage $selectedAverage
        $p99Improvement = Improvement $baselineP99 $selectedP99
        $candidateAverageImprovement =
            Improvement $baselineAverage $waveAverage
        $candidateP99Improvement = Improvement $baselineP99 $waveP99
        if ($averageImprovement -gt 0.0) { $positiveWins++ }
        if ($candidateAverageImprovement -gt 0.0) {
            $candidatePositiveWins++
        }
        $gpuAverageImprovements.Add($averageImprovement)
        $gpuP99Improvements.Add($p99Improvement)
        $absoluteReductions.Add($baselineAverage - $selectedAverage)
        $candidateGpuAverageImprovements.Add($candidateAverageImprovement)
        $candidateGpuP99Improvements.Add($candidateP99Improvement)
        $candidateAbsoluteReductions.Add($baselineAverage - $waveAverage)
        $evaluationRows.Add([pscustomobject]@{
            workloadId = $operation
            evaluationRound = $round
            baselineBackend = 'portable'
            candidateBackend = 'wave-ops'
            selectedBackend = $selectedVariant
            baselineGpuAverageMs = $baselineAverage
            candidateGpuAverageMs = $waveAverage
            selectedGpuAverageMs = $selectedAverage
            candidateGpuAverageImprovementPercent =
                $candidateAverageImprovement
            gpuAverageImprovementPercent = $averageImprovement
            baselineGpuP99Ms = $baselineP99
            candidateGpuP99Ms = $waveP99
            selectedGpuP99Ms = $selectedP99
            candidateGpuP99ImprovementPercent = $candidateP99Improvement
            gpuP99ImprovementPercent = $p99Improvement
        })
    }

    $requiredPositiveWins = [int][Math]::Ceiling(
        $candidateGpuAverageImprovements.Count *
        $minimumPositiveWinFraction)
    $candidateAverageMedian =
        Median ([double[]]$candidateGpuAverageImprovements)
    $candidateP99Median =
        Median ([double[]]$candidateGpuP99Improvements)
    $candidateAbsoluteMedian =
        Median ([double[]]$candidateAbsoluteReductions)
    $candidateMeetsUpgradeGate =
        $candidatePositiveWins -ge $requiredPositiveWins -and
        $candidateAverageMedian -ge $minimumAverageImprovementPercent -and
        $candidateAbsoluteMedian -ge $minimumAbsoluteReductionMs -and
        $candidateP99Median -ge $minimumP99ImprovementPercent
    $selectionConfirmed =
        ($selectedVariant -eq 'wave-ops' -and $candidateMeetsUpgradeGate) -or
        ($selectedVariant -eq 'portable' -and -not $candidateMeetsUpgradeGate)

    $profileRows.Add([pscustomobject]@{
        workloadId = $operation
        baselineBackend = 'Portable'
        selectedBackend = if ($selectedVariant -eq 'wave-ops') {
            'WaveOps'
        } else { 'Portable' }
        accepted = $selectionConfirmed
        calibrationSamplesPerCandidate = [int]$selected.samples
        baselineMedianMs = [double]$baseline.median
        selectedMedianMs = [double]$selected.median
        baselineP99Ms = [double]$baseline.p99
        selectedP99Ms = [double]$selected.p99
        calibrationImprovementPercent = $waveImprovement
        calibrationAbsoluteReductionMs = $waveAbsoluteReduction
        calibrationP99ImprovementPercent = $waveP99Improvement
        calibrationMeetsUpgradeGate = $calibrationMeetsUpgradeGate
        evaluationPairs = $gpuAverageImprovements.Count
        evaluationPositiveWins = $positiveWins
        evaluationGpuAverageImprovementMedianPercent =
            Median ([double[]]$gpuAverageImprovements)
        evaluationGpuP99ImprovementMedianPercent =
            Median ([double[]]$gpuP99Improvements)
        evaluationGpuAverageAbsoluteReductionMedianMs =
            Median ([double[]]$absoluteReductions)
        candidateEvaluationRequiredPositiveWins = $requiredPositiveWins
        candidateEvaluationPositiveWins = $candidatePositiveWins
        candidateEvaluationGpuAverageImprovementMedianPercent =
            $candidateAverageMedian
        candidateEvaluationGpuP99ImprovementMedianPercent =
            $candidateP99Median
        candidateEvaluationGpuAverageAbsoluteReductionMedianMs =
            $candidateAbsoluteMedian
        candidateMeetsUpgradeGate = $candidateMeetsUpgradeGate
        selectionConfirmed = $selectionConfirmed
    })
}

$fingerprint = [ordered]@{
    schemaVersion = 1
    vendorId = [int]$device.graphicsDeviceVendorId
    deviceId = [int]$device.graphicsDeviceId
    vendor = [string]$device.graphicsDeviceVendor
    deviceName = [string]$device.graphicsDeviceName
    graphicsApi = [string]$device.graphicsDeviceType
    graphicsVersion = [string]$device.graphicsDeviceVersion
    shaderLevel = [int]$device.graphicsShaderLevel
}
$profile = [ordered]@{
    schemaVersion = 1
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    sourceCommit = [string]$runner.gitCommit
    calibrationProtocol =
        "rounds-1-$CalibrationRounds-calibration;rounds-$($CalibrationRounds + 1)-$roundCount-evaluation"
    device = $fingerprint
    workloads = @($profileRows | ForEach-Object {
        [ordered]@{
            workloadId = $_.workloadId
            baselineBackend = $_.baselineBackend
            selectedBackend = $_.selectedBackend
            accepted = $_.accepted
            calibrationSamplesPerCandidate = $_.calibrationSamplesPerCandidate
            baselineMedianMs = $_.baselineMedianMs
            selectedMedianMs = $_.selectedMedianMs
            baselineP99Ms = $_.baselineP99Ms
            selectedP99Ms = $_.selectedP99Ms
            calibrationImprovementPercent = $_.calibrationImprovementPercent
            calibrationAbsoluteReductionMs = $_.calibrationAbsoluteReductionMs
            calibrationP99ImprovementPercent =
                $_.calibrationP99ImprovementPercent
            calibrationMeetsUpgradeGate = $_.calibrationMeetsUpgradeGate
        }
    })
}
$profile | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (
    Join-Path $root 'autotune-profile.json')
$profileRows | Export-Csv -LiteralPath (
    Join-Path $root 'autotune-workload-summary.csv') -NoTypeInformation
$evaluationRows | Export-Csv -LiteralPath (
    Join-Path $root 'autotune-evaluation.csv') -NoTypeInformation

$acceptance = 'not-formal'
if ($FormalAcceptance) {
    $violations = [System.Collections.Generic.List[string]]::new()
    if ($profileRows.Count -ne 3) {
        $violations.Add("Expected 3 workloads; got $($profileRows.Count).")
    }
    foreach ($row in $profileRows) {
        if ([int]$row.evaluationPairs -ne 4) {
            $violations.Add("$($row.workloadId): expected 4 evaluation pairs.")
        }
        if (-not [bool]$row.selectionConfirmed) {
            $violations.Add(
                "$($row.workloadId): independent evaluation did not confirm " +
                "$($row.selectedBackend) selected during calibration.")
        }
    }
    if ($violations.Count -ne 0) {
        $violations | Set-Content (Join-Path $root 'autotune-acceptance-violations.txt')
        throw "Formal autotuning acceptance rejected:`n$($violations -join "`n")"
    }
    $acceptance = 'accepted'
}
$acceptance | Set-Content (Join-Path $root 'autotune-acceptance-status.txt')

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# Cross-vendor GPU autotuning')
$lines.Add('')
$lines.Add("Acceptance: **$acceptance**")
$lines.Add('')
$lines.Add("Device: **$($device.graphicsDeviceName)** / $($device.graphicsDeviceType)")
$lines.Add('')
$lines.Add('| Workload | Selected | Calibration WaveOps delta | Evaluation WaveOps avg | Evaluation WaveOps P99 | WaveOps wins | Confirmed |')
$lines.Add('|---|---|---:|---:|---:|---:|---:|')
foreach ($row in $profileRows) {
    $lines.Add(('| {0} | {1} | {2:F2}% | {3:F2}% | {4:F2}% | {5}/{6} | {7} |' -f
        $row.workloadId,
        $row.selectedBackend,
        $row.calibrationImprovementPercent,
        $row.candidateEvaluationGpuAverageImprovementMedianPercent,
        $row.candidateEvaluationGpuP99ImprovementMedianPercent,
        $row.candidateEvaluationPositiveWins,
        $row.evaluationPairs,
        [int][bool]$row.selectionConfirmed))
}
$lines.Add('')
$lines.Add('Calibration and evaluation use disjoint rounds. WaveOps is selected only when it clears the improvement and P99 gates; otherwise the validated profile keeps the portable backend. A mismatched or missing device profile falls back to capability-based Auto selection.')
$lines | Set-Content (Join-Path $root 'AUTOTUNING_SUMMARY.md')
Write-Output "Generated GPU autotuning profile: $root"
