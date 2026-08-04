[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SummaryPath,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

$resolvedSummary = (Resolve-Path -LiteralPath $SummaryPath).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Split-Path -Parent $resolvedSummary
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path (Get-Location) $OutputDirectory
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

function Convert-ToDouble {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Value,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $parsed = 0.0
    if (-not [double]::TryParse(
            [string]$Value,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) {
        throw "Metric '$Name' is not numeric: '$Value'."
    }
    return $parsed
}

function Get-Median {
    param([Parameter(Mandatory = $true)][double[]]$Values)

    if ($Values.Count -eq 0) {
        throw 'Median requires at least one value.'
    }
    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return [double]$sorted[$middle]
    }
    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function Get-ImprovementPercent {
    param(
        [Parameter(Mandatory = $true)][double]$Baseline,
        [Parameter(Mandatory = $true)][double]$Candidate
    )

    if ([Math]::Abs($Baseline) -lt 1.0e-12) {
        return 0.0
    }
    return ($Baseline - $Candidate) / $Baseline * 100.0
}

$rows = @(Import-Csv -LiteralPath $resolvedSummary)
if ($rows.Count -eq 0) {
    throw "Summary is empty: $resolvedSummary"
}

$devices = @($rows.device | Sort-Object -Unique)
if ($devices.Count -ne 1) {
    throw "Expected one GPU device, observed: $($devices -join ', ')"
}

$signatures = @(
    $rows |
        ForEach-Object {
            "$($_.bfp2TotalPacks)|$($_.bfp2ResidentPacks)|" +
            "$($_.bfp2ResidentGpuBytes)|$($_.orthophotoBasePages)|" +
            "$($_.orthophotoLod0Pages)"
        } |
        Sort-Object -Unique
)
if ($signatures.Count -ne 1) {
    throw "Production residency signatures differ: $($signatures -join ', ')"
}

$roundIds = @($rows.round | Sort-Object {[int]$_} -Unique)
$paired = [System.Collections.Generic.List[object]]::new()
foreach ($roundId in $roundIds) {
    $roundRows = @($rows | Where-Object { $_.round -eq $roundId })
    $off = @($roundRows | Where-Object { $_.mode -eq 'off' })
    $brute = @($roundRows | Where-Object { $_.mode -eq 'gpu-spatial-brute' })
    $index = @($roundRows | Where-Object { $_.mode -eq 'gpu-spatial-index' })
    if ($off.Count -ne 1 -or $brute.Count -ne 1 -or $index.Count -ne 1) {
        throw "Round $roundId does not contain exactly one off, brute, and index row."
    }

    foreach ($row in @($brute[0], $index[0])) {
        foreach ($field in @(
            'spatialSortedOrderViolations',
            'spatialCellRangeViolations',
            'spatialQueryMismatchCount',
            'spatialBoundsViolations',
            'measurementReadbackBytesPerFrame')) {
            if ([int64]$row.$field -ne 0) {
                throw "Round $roundId mode $($row.mode) failed '$field': $($row.$field)."
            }
        }
    }
    if ($brute[0].spatialResultHash -ne $index[0].spatialResultHash) {
        throw "Round $roundId result hashes differ."
    }

    $bruteGpuAverage = Convert-ToDouble $brute[0].gpuAverageMs 'gpuAverageMs'
    $indexGpuAverage = Convert-ToDouble $index[0].gpuAverageMs 'gpuAverageMs'
    $bruteGpuP99 = Convert-ToDouble $brute[0].gpuP99Ms 'gpuP99Ms'
    $indexGpuP99 = Convert-ToDouble $index[0].gpuP99Ms 'gpuP99Ms'
    $bruteFrameAverage = Convert-ToDouble $brute[0].frameAverageMs 'frameAverageMs'
    $indexFrameAverage = Convert-ToDouble $index[0].frameAverageMs 'frameAverageMs'
    $bruteFrameP99 = Convert-ToDouble $brute[0].frameP99Ms 'frameP99Ms'
    $indexFrameP99 = Convert-ToDouble $index[0].frameP99Ms 'frameP99Ms'
    $bruteUpdates = Convert-ToDouble $brute[0].spatialUpdatesPerSecond 'spatialUpdatesPerSecond'
    $indexUpdates = Convert-ToDouble $index[0].spatialUpdatesPerSecond 'spatialUpdatesPerSecond'
    $offGpuAverage = Convert-ToDouble $off[0].gpuAverageMs 'off.gpuAverageMs'

    $paired.Add([pscustomobject]@{
        round = [int]$roundId
        order = $brute[0].order
        offGpuAverageMs = $offGpuAverage
        bruteGpuAverageMs = $bruteGpuAverage
        indexGpuAverageMs = $indexGpuAverage
        gpuAverageImprovementPercent =
            Get-ImprovementPercent $bruteGpuAverage $indexGpuAverage
        bruteGpuP99Ms = $bruteGpuP99
        indexGpuP99Ms = $indexGpuP99
        gpuP99ImprovementPercent =
            Get-ImprovementPercent $bruteGpuP99 $indexGpuP99
        bruteFrameAverageMs = $bruteFrameAverage
        indexFrameAverageMs = $indexFrameAverage
        frameAverageImprovementPercent =
            Get-ImprovementPercent $bruteFrameAverage $indexFrameAverage
        bruteFrameP99Ms = $bruteFrameP99
        indexFrameP99Ms = $indexFrameP99
        frameP99ImprovementPercent =
            Get-ImprovementPercent $bruteFrameP99 $indexFrameP99
        bruteUpdatesPerSecond = $bruteUpdates
        indexUpdatesPerSecond = $indexUpdates
        updateRateChangePercent =
            ($indexUpdates - $bruteUpdates) / $bruteUpdates * 100.0
        candidateReductionPercent =
            Convert-ToDouble $index[0].spatialCandidateReductionPercent 'candidateReductionPercent'
        resultHash = $index[0].spatialResultHash
    })
}

$pairedPath = Join-Path $outputRoot 'paired-deltas.csv'
$paired | Export-Csv -LiteralPath $pairedPath -NoTypeInformation -Encoding utf8

$offGpuValues = @(
    $rows |
        Where-Object { $_.mode -eq 'off' } |
        ForEach-Object { Convert-ToDouble $_.gpuAverageMs 'off.gpuAverageMs' }
)
$gpuAverageImprovements =
    [double[]]@($paired | ForEach-Object { $_.gpuAverageImprovementPercent })
$gpuP99Improvements =
    [double[]]@($paired | ForEach-Object { $_.gpuP99ImprovementPercent })
$frameAverageImprovements =
    [double[]]@($paired | ForEach-Object { $_.frameAverageImprovementPercent })
$frameP99Improvements =
    [double[]]@($paired | ForEach-Object { $_.frameP99ImprovementPercent })
$updateRateChanges =
    [double[]]@($paired | ForEach-Object { $_.updateRateChangePercent })

$offMedian = Get-Median ([double[]]$offGpuValues)
$offDriftPercent =
    (($offGpuValues | Measure-Object -Maximum).Maximum -
     ($offGpuValues | Measure-Object -Minimum).Minimum) /
    $offMedian * 100.0
$allGpuAveragePositive =
    @($gpuAverageImprovements | Where-Object { $_ -le 0.0 }).Count -eq 0
$allGpuP99Positive =
    @($gpuP99Improvements | Where-Object { $_ -le 0.0 }).Count -eq 0

$performanceDirection = if ($allGpuAveragePositive -and $allGpuP99Positive) {
    'reproducible-index-improvement'
}
else {
    'no-reproducible-index-improvement'
}
$interpretation = if ($allGpuAveragePositive -and $allGpuP99Positive) {
    'Paired direction is reproducible; exact effect size has low confidence until GPU timestamp or same-process interleaving is added.'
}
else {
    'Correctness passed, but paired whole-frame results do not support an index performance improvement at this query count.'
}

$analysisLines = @(
    "GPU spatial-index A/B quality summary",
    "source=$resolvedSummary",
    "device=$($devices[0])",
    "rounds=$($roundIds.Count)",
    "productionSignature=$($signatures[0])",
    "pointCount=$($rows[0].spatialPointCount)",
    "queryCount=$($rows | Where-Object { $_.mode -eq 'gpu-spatial-index' } | Select-Object -First 1 -ExpandProperty spatialQueryCount)",
    "allGpuAveragePairsImproved=$([int]$allGpuAveragePositive)",
    "allGpuP99PairsImproved=$([int]$allGpuP99Positive)",
    ("gpuAverageImprovementMedianPercent={0:F3}" -f (Get-Median $gpuAverageImprovements)),
    ("gpuAverageImprovementMinimumPercent={0:F3}" -f (($gpuAverageImprovements | Measure-Object -Minimum).Minimum)),
    ("gpuAverageImprovementMaximumPercent={0:F3}" -f (($gpuAverageImprovements | Measure-Object -Maximum).Maximum)),
    ("gpuP99ImprovementMedianPercent={0:F3}" -f (Get-Median $gpuP99Improvements)),
    ("gpuP99ImprovementMinimumPercent={0:F3}" -f (($gpuP99Improvements | Measure-Object -Minimum).Minimum)),
    ("gpuP99ImprovementMaximumPercent={0:F3}" -f (($gpuP99Improvements | Measure-Object -Maximum).Maximum)),
    ("frameAverageImprovementMedianPercent={0:F3}" -f (Get-Median $frameAverageImprovements)),
    ("frameP99ImprovementMedianPercent={0:F3}" -f (Get-Median $frameP99Improvements)),
    ("updateRateChangeMedianPercent={0:F3}" -f (Get-Median $updateRateChanges)),
    ("offGpuAverageDriftPercent={0:F3}" -f $offDriftPercent),
    "performanceDirection=$performanceDirection",
    "qualityGate=passed-with-high-cross-process-baseline-drift",
    "interpretation=$interpretation"
)
$analysisPath = Join-Path $outputRoot 'quality-summary.txt'
$analysisLines | Set-Content -LiteralPath $analysisPath -Encoding utf8

$analysisLines
"pairedDeltas=$pairedPath"
"qualitySummary=$analysisPath"
