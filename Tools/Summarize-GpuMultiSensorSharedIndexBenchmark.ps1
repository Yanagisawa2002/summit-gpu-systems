[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReportDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$baselineName = 'rebuilt-per-sensor'
$sharedName = 'shared-sensor-index'

function Read-KeyValue([string]$Path) {
    $result = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        $parts = $line -split '=', 2
        if ($parts.Count -eq 2) { $result[$parts[0]] = $parts[1] }
    }
    return $result
}

function Number($Value) {
    return [double]::Parse(
        [string]$Value,
        [System.Globalization.NumberStyles]::Float,
        $invariant)
}

function Median([double[]]$Values) {
    [double[]]$sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { throw 'Median requires values.' }
    $middle = [int]($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2.0
}

function Improvement([double]$Baseline, [double]$Candidate) {
    if ($Baseline -le 0.0) { throw 'Improvement baseline must be positive.' }
    return 100.0 * ($Baseline - $Candidate) / $Baseline
}

$root = [System.IO.Path]::GetFullPath($ReportDirectory)
$scenarioDirectories = @(Get-ChildItem -LiteralPath $root -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'block-summary.csv') } |
    Sort-Object Name)
if ($scenarioDirectories.Count -lt 1) {
    throw "No benchmark scenarios found under $root"
}

$matrix = [System.Collections.Generic.List[object]]::new()
$pairs = [System.Collections.Generic.List[object]]::new()
foreach ($directory in $scenarioDirectories) {
    $config = Get-Content -LiteralPath (Join-Path $directory.FullName 'config.json') -Raw |
        ConvertFrom-Json
    $device = Get-Content -LiteralPath (Join-Path $directory.FullName 'device.json') -Raw |
        ConvertFrom-Json
    $run = Read-KeyValue (Join-Path $directory.FullName 'run-summary.txt')
    $blocks = @(Import-Csv -LiteralPath (Join-Path $directory.FullName 'block-summary.csv'))
    $validation = @(Import-Csv -LiteralPath (Join-Path $directory.FullName 'validation.csv'))

    if ([string]$config.benchmarkMode -cne 'multi-sensor-shared-index' -or
        [int]$config.sensorCount -lt 2 -or
        [int]$config.queryCount -ne
            ([int]$config.sensorCount * [int]$config.queriesPerSensor)) {
        throw "Scenario '$($directory.Name)' has an invalid multi-sensor contract."
    }
    if ($run.passed -cne '1' -or $run.status -cne 'completed') {
        throw "Scenario '$($directory.Name)' did not complete successfully."
    }
    if (@($validation | Where-Object { [int]$_.passed -ne 1 }).Count -ne 0) {
        throw "Scenario '$($directory.Name)' failed digest validation."
    }
    $measured = @($blocks | Where-Object { $_.blockType -ceq 'measurement' })
    $baseline = @($measured | Where-Object { $_.variant -ceq $baselineName })
    $shared = @($measured | Where-Object { $_.variant -ceq $sharedName })
    $expectedVariantBlocks = [int]$config.superRounds * 2
    $expectedPairs = [int]$config.superRounds * 2
    if ($baseline.Count -ne $expectedVariantBlocks -or
        $shared.Count -ne $expectedVariantBlocks) {
        throw "Scenario '$($directory.Name)' lacks balanced blocks per variant."
    }
    foreach ($block in $measured) {
        if ([int]$block.gpuRegionValidSamples -ne [int]$config.sampleFrames -or
            [int]$block.fencePassed -ne 1 -or
            [long]$block.measurementReadbackBytes -ne 0) {
            throw "Scenario '$($directory.Name)' has incomplete timing or fence evidence."
        }
    }

    for ($pairIndex = 1; $pairIndex -le $expectedPairs; $pairIndex++) {
        $a = @($baseline | Where-Object { [int]$_.pairIndex -eq $pairIndex })
        $b = @($shared | Where-Object { [int]$_.pairIndex -eq $pairIndex })
        if ($a.Count -ne 1 -or $b.Count -ne 1) {
            throw "Scenario '$($directory.Name)' pair $pairIndex is incomplete."
        }
        $pairs.Add([pscustomobject]@{
            scenarioId = [string]$config.scenarioId
            pairIndex = $pairIndex
            pairOrder = [string]$a[0].pairOrder
            baselineGpuAverageMs = Number $a[0].gpuRegionAverageMs
            sharedGpuAverageMs = Number $b[0].gpuRegionAverageMs
            gpuAverageImprovementPercent = Improvement (Number $a[0].gpuRegionAverageMs) (Number $b[0].gpuRegionAverageMs)
            baselineGpuP99Ms = Number $a[0].gpuRegionP99Ms
            sharedGpuP99Ms = Number $b[0].gpuRegionP99Ms
            gpuP99ImprovementPercent = Improvement (Number $a[0].gpuRegionP99Ms) (Number $b[0].gpuRegionP99Ms)
            baselineFrameAverageMs = Number $a[0].frameAverageMs
            sharedFrameAverageMs = Number $b[0].frameAverageMs
            frameAverageImprovementPercent = Improvement (Number $a[0].frameAverageMs) (Number $b[0].frameAverageMs)
        })
    }

    [double[]]$baselineGpu = @($baseline | ForEach-Object { Number $_.gpuRegionAverageMs })
    [double[]]$sharedGpu = @($shared | ForEach-Object { Number $_.gpuRegionAverageMs })
    [double[]]$baselineP99 = @($baseline | ForEach-Object { Number $_.gpuRegionP99Ms })
    [double[]]$sharedP99 = @($shared | ForEach-Object { Number $_.gpuRegionP99Ms })
    [double[]]$baselineFrame = @($baseline | ForEach-Object { Number $_.frameAverageMs })
    [double[]]$sharedFrame = @($shared | ForEach-Object { Number $_.frameAverageMs })
    $scenarioPairs = @($pairs | Where-Object { $_.scenarioId -ceq [string]$config.scenarioId })
    $matrix.Add([pscustomobject]@{
        scenarioId = [string]$config.scenarioId
        graphicsDeviceName = [string]$device.graphicsDeviceName
        elementCount = [int]$config.elementCount
        sensorCount = [int]$config.sensorCount
        queriesPerSensor = [int]$config.queriesPerSensor
        baselineIndexBuilds = [int]$config.rebuiltPerSensorIndexBuildCount
        sharedIndexBuilds = [int]$config.sharedSensorIndexBuildCount
        independentSpatialIndexBytes = [long]$config.independentSpatialIndexResidentBytes
        sharedSpatialIndexBytes = [long]$config.sharedSpatialIndexResidentBytes
        spatialIndexBytesSaved = [long]$config.multiSensorSpatialIndexBytesSaved
        baselineGpuAverageMs = Median $baselineGpu
        sharedGpuAverageMs = Median $sharedGpu
        gpuAverageImprovementPercent = Improvement (Median $baselineGpu) (Median $sharedGpu)
        baselineGpuP99Ms = Median $baselineP99
        sharedGpuP99Ms = Median $sharedP99
        gpuP99ImprovementPercent = Improvement (Median $baselineP99) (Median $sharedP99)
        baselineFrameAverageMs = Median $baselineFrame
        sharedFrameAverageMs = Median $sharedFrame
        frameAverageImprovementPercent = Improvement (Median $baselineFrame) (Median $sharedFrame)
        pairedGpuWins = @($scenarioPairs | Where-Object { $_.sharedGpuAverageMs -lt $_.baselineGpuAverageMs }).Count
        pairedFrameWins = @($scenarioPairs | Where-Object { $_.sharedFrameAverageMs -lt $_.baselineFrameAverageMs }).Count
        pairedCount = $expectedPairs
        validationRows = $validation.Count
        validationPassed = 1
    })
}

$pairs | Export-Csv -LiteralPath (Join-Path $root 'pair-summary.csv') -NoTypeInformation -Encoding utf8
$matrix | Export-Csv -LiteralPath (Join-Path $root 'matrix-summary.csv') -NoTypeInformation -Encoding utf8
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# Multi-sensor shared GPU spatial-index A/B')
$lines.Add('')
$lines.Add('| Scenario | GPU avg improvement | GPU P99 improvement | Frame avg improvement | GPU wins | Index bytes saved |')
$lines.Add('|---|---:|---:|---:|---:|---:|')
foreach ($row in $matrix) {
    $lines.Add(('| {0} | {1:F2}% | {2:F2}% | {3:F2}% | {4}/{5} | {6} |' -f
        $row.scenarioId,
        $row.gpuAverageImprovementPercent,
        $row.gpuP99ImprovementPercent,
        $row.frameAverageImprovementPercent,
        $row.pairedGpuWins,
        $row.pairedCount,
        $row.spatialIndexBytesSaved))
}
$lines.Add('')
$lines.Add('All measurement blocks use native DX12 timestamps on the main graphics command list. Validation is outside measurement; workload readback is zero bytes per frame.')
[System.IO.File]::WriteAllLines((Join-Path $root 'SUMMARY.md'), $lines)
Write-Host "Validated multi-sensor shared-index evidence: $root"
