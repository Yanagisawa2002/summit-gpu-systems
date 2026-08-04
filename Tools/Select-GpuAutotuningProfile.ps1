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
    $waveP99Regression = -1.0 * (Improvement $baseline.p99 $wave.p99)
    $selectedVariant = if (
        $waveImprovement -ge 1.0 -and $waveP99Regression -le 2.0) {
        'wave-ops'
    } else {
        'portable'
    }
    $selected = $candidateStats[$selectedVariant]

    $gpuAverageImprovements = [System.Collections.Generic.List[double]]::new()
    $gpuP99Improvements = [System.Collections.Generic.List[double]]::new()
    $absoluteReductions = [System.Collections.Generic.List[double]]::new()
    $positiveWins = 0
    for ($round = $CalibrationRounds + 1; $round -le $roundCount; $round++) {
        $portable = @($blocks | Where-Object {
            $_.operation -eq $operation -and
            $_.variant -eq 'portable' -and
            [int]$_.round -eq $round })
        $chosen = @($blocks | Where-Object {
            $_.operation -eq $operation -and
            $_.variant -eq $selectedVariant -and
            [int]$_.round -eq $round })
        if ($portable.Count -ne 1 -or $chosen.Count -ne 1) {
            throw "$operation evaluation round $round is incomplete."
        }
        $baselineAverage = Number $portable[0].gpuRegionAverageMs 'baseline average'
        $selectedAverage = Number $chosen[0].gpuRegionAverageMs 'selected average'
        $baselineP99 = Number $portable[0].gpuRegionP99Ms 'baseline p99'
        $selectedP99 = Number $chosen[0].gpuRegionP99Ms 'selected p99'
        $averageImprovement = Improvement $baselineAverage $selectedAverage
        $p99Improvement = Improvement $baselineP99 $selectedP99
        if ($averageImprovement -gt 0.0) { $positiveWins++ }
        $gpuAverageImprovements.Add($averageImprovement)
        $gpuP99Improvements.Add($p99Improvement)
        $absoluteReductions.Add($baselineAverage - $selectedAverage)
        $evaluationRows.Add([pscustomobject]@{
            workloadId = $operation
            evaluationRound = $round
            baselineBackend = 'portable'
            selectedBackend = $selectedVariant
            baselineGpuAverageMs = $baselineAverage
            selectedGpuAverageMs = $selectedAverage
            gpuAverageImprovementPercent = $averageImprovement
            baselineGpuP99Ms = $baselineP99
            selectedGpuP99Ms = $selectedP99
            gpuP99ImprovementPercent = $p99Improvement
        })
    }

    $profileRows.Add([pscustomobject]@{
        workloadId = $operation
        baselineBackend = 'Portable'
        selectedBackend = if ($selectedVariant -eq 'wave-ops') {
            'WaveOps'
        } else { 'Portable' }
        accepted = $true
        calibrationSamplesPerCandidate = [int]$selected.samples
        baselineMedianMs = [double]$baseline.median
        selectedMedianMs = [double]$selected.median
        baselineP99Ms = [double]$baseline.p99
        selectedP99Ms = [double]$selected.p99
        calibrationImprovementPercent =
            Improvement $baseline.median $selected.median
        evaluationPairs = $gpuAverageImprovements.Count
        evaluationPositiveWins = $positiveWins
        evaluationGpuAverageImprovementMedianPercent =
            Median ([double[]]$gpuAverageImprovements)
        evaluationGpuP99ImprovementMedianPercent =
            Median ([double[]]$gpuP99Improvements)
        evaluationGpuAverageAbsoluteReductionMedianMs =
            Median ([double[]]$absoluteReductions)
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
        if ([int]$row.evaluationPositiveWins -lt 3) {
            $violations.Add("$($row.workloadId): fewer than 3/4 average wins.")
        }
        if ([double]$row.evaluationGpuAverageImprovementMedianPercent -lt 3.0) {
            $violations.Add("$($row.workloadId): median average improvement below 3%.")
        }
        if ([double]$row.evaluationGpuAverageAbsoluteReductionMedianMs -lt 0.005) {
            $violations.Add("$($row.workloadId): absolute reduction below 0.005 ms.")
        }
        if ([double]$row.evaluationGpuP99ImprovementMedianPercent -lt -5.0) {
            $violations.Add("$($row.workloadId): median P99 regression exceeds 5%.")
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
$lines.Add('| Workload | Selected | Calibration improvement | Evaluation GPU avg | Evaluation GPU P99 | Wins |')
$lines.Add('|---|---|---:|---:|---:|---:|')
foreach ($row in $profileRows) {
    $lines.Add(('| {0} | {1} | {2:F2}% | {3:F2}% | {4:F2}% | {5}/{6} |' -f
        $row.workloadId,
        $row.selectedBackend,
        $row.calibrationImprovementPercent,
        $row.evaluationGpuAverageImprovementMedianPercent,
        $row.evaluationGpuP99ImprovementMedianPercent,
        $row.evaluationPositiveWins,
        $row.evaluationPairs))
}
$lines.Add('')
$lines.Add('Calibration and evaluation use disjoint rounds. A mismatched or missing device profile falls back to capability-based Auto selection.')
$lines | Set-Content (Join-Path $root 'AUTOTUNING_SUMMARY.md')
Write-Output "Generated GPU autotuning profile: $root"
