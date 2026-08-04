[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DataRoot,

    [ValidateRange(0, 16)]
    [int]$DeviceIndex = 0,

    [ValidateSet(1, 4, 6)]
    [int]$CameraCount = 1,

    [ValidateRange(0.0, 16.0)]
    [double]$ScreenCullPixels = 1.0,

    [ValidateRange(5, 300)]
    [int]$WarmupSeconds = 60,

    [ValidateRange(120, 3600)]
    [int]$SampleFrames = 900,

    [ValidateRange(1, 10)]
    [int]$Repeats = 3,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$projectRoot = Split-Path -Parent $PSScriptRoot
$playerPath = Join-Path $projectRoot 'Builds\Validation\FullCityWeather\NYCGISFullCityWeatherQA.exe'
if (-not (Test-Path -LiteralPath $playerPath -PathType Leaf)) {
    throw "Benchmark Player is missing: $playerPath"
}

$resolvedDataRoot = (Resolve-Path -LiteralPath $DataRoot).Path
if (-not (Test-Path -LiteralPath $resolvedDataRoot -PathType Container)) {
    throw "NYC GIS data root is missing: $resolvedDataRoot"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $projectRoot (
        "Reports\GpuWaveCompaction\ab-$timestamp-device-$DeviceIndex-cameras-$CameraCount")
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}

$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$env:NYCGIS_DATA_ROOT = $resolvedDataRoot

function Read-NYCGISReport {
    param([Parameter(Mandatory = $true)][string]$Path)

    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        $parts = $line -split '=', 2
        if ($parts.Count -eq 2) {
            $values[$parts[0]] = $parts[1]
        }
    }
    return $values
}

function Convert-ToDouble {
    param([Parameter(Mandatory = $true)][string]$Value)
    return [double]::Parse($Value, [System.Globalization.NumberStyles]::Float, $invariant)
}

function Get-Median {
    param([Parameter(Mandatory = $true)][double[]]$Values)
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) {
        return [double]::NaN
    }
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return $sorted[$middle]
    }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2.0
}

$variantDefinitions = @(
    [pscustomobject]@{ Name = 'scalar-aos'; Expected = 'ScalarAoS' },
    [pscustomobject]@{ Name = 'scalar-compact'; Expected = 'ScalarCompact' },
    [pscustomobject]@{ Name = 'wave-compact'; Expected = 'WaveCompact' }
)
$rows = [System.Collections.Generic.List[object]]::new()

for ($round = 1; $round -le $Repeats; $round++) {
    switch (($round - 1) % 6) {
        0 { $roundVariants = @($variantDefinitions[0], $variantDefinitions[1], $variantDefinitions[2]) }
        1 { $roundVariants = @($variantDefinitions[1], $variantDefinitions[2], $variantDefinitions[0]) }
        2 { $roundVariants = @($variantDefinitions[2], $variantDefinitions[0], $variantDefinitions[1]) }
        3 { $roundVariants = @($variantDefinitions[2], $variantDefinitions[1], $variantDefinitions[0]) }
        4 { $roundVariants = @($variantDefinitions[1], $variantDefinitions[0], $variantDefinitions[2]) }
        5 { $roundVariants = @($variantDefinitions[0], $variantDefinitions[2], $variantDefinitions[1]) }
    }

    foreach ($variant in $roundVariants) {
        $stem = "device-$DeviceIndex-cameras-$CameraCount-$($variant.Name)-r$round"
        $reportPath = Join-Path $outputRoot "$stem.txt"
        $logPath = Join-Path $outputRoot "$stem.log"
        $screenshotPath = Join-Path $outputRoot "$stem.png"
        $arguments = @(
            '-force-d3d12',
            '-force-device-index', [string]$DeviceIndex,
            '-screen-fullscreen', '0',
            '-screen-width', '1280',
            '-screen-height', '720',
            '-nycgis-weather-perf',
            '-nycgis-weather-cameras', [string]$CameraCount,
            '-nycgis-weather-warmup-seconds', [string]$WarmupSeconds,
            '-nycgis-weather-sample-frames', [string]$SampleFrames,
            '-nycgis-bfp2-screen-cull-pixels',
            $ScreenCullPixels.ToString($invariant),
            '-nycgis-bfp2-cull-algorithm', $variant.Name,
            '-nycgis-weather-screenshot', "`"$screenshotPath`"",
            '-nycgis-weather-report', "`"$reportPath`"",
            '-logFile', "`"$logPath`""
        )

        $process = Start-Process `
            -FilePath $playerPath `
            -ArgumentList $arguments `
            -WorkingDirectory $projectRoot `
            -WindowStyle Minimized `
            -Wait `
            -PassThru
        if ($process.ExitCode -ne 0) {
            throw "$stem failed with Player exit code $($process.ExitCode). See $logPath"
        }
        if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
            throw "$stem did not write a report. See $logPath"
        }

        $report = Read-NYCGISReport -Path $reportPath
        if ($report.status -ne 'completed') {
            throw "$stem did not complete successfully. See $reportPath"
        }
        if ([int]$report.gpuFrameTimingValidSamples -lt $SampleFrames) {
            throw "$stem has incomplete GPU frame timing samples. Run the Player minimized, not hidden."
        }
        if ([int]$report.bfp2TotalPacks -le 0 -or [int]$report.bfp2ResidentPacks -le 0) {
            throw "$stem did not load the production BFP2 dataset. Check NYCGIS_DATA_ROOT."
        }
        if ($report.bfp2ActiveCullAlgorithm -ne $variant.Expected) {
            throw "$stem requested $($variant.Expected) but ran $($report.bfp2ActiveCullAlgorithm)."
        }
        if ($report.bfp2CullValidationValid -ne '1') {
            throw "$stem failed synchronized GPU cull-output validation."
        }
        if ($report.screenshotSaved -ne '1' -or
            -not (Test-Path -LiteralPath $screenshotPath -PathType Leaf)) {
            throw "$stem did not save its visual-validation screenshot."
        }

        $row = [pscustomobject]@{
            Round = $round
            Variant = $variant.Name
            ActiveAlgorithm = $report.bfp2ActiveCullAlgorithm
            GpuMsAverage = Convert-ToDouble $report.gpuFrameMsAverage
            GpuMsP95 = Convert-ToDouble $report.gpuFrameMsP95
            GpuMsP99 = Convert-ToDouble $report.gpuFrameMsP99
            FrameMsAverage = Convert-ToDouble $report.frameMsAverage
            FrameMsP95 = Convert-ToDouble $report.frameMsP95
            FrameMsP99 = Convert-ToDouble $report.frameMsP99
            FpsAverage = Convert-ToDouble $report.fpsAverage
            DrawCallsAverage = Convert-ToDouble $report.drawCallsAverage
            ResidentGpuBytes = [long]$report.bfp2ResidentGpuBytes
            CompactLayoutBytes = [long]$report.bfp2CompactClusterLayoutBytes
            ResidentPacks = [int]$report.bfp2ResidentPacks
            TotalPacks = [int]$report.bfp2TotalPacks
            ValidationVisibleClusters = [long]$report.bfp2ValidationVisibleClusters
            ValidationVisibleIndices = [long]$report.bfp2ValidationVisibleIndices
            ValidationCulledClusters = [long]$report.bfp2ValidationCulledClusters
            ValidationOverflowClusters = [long]$report.bfp2ValidationOverflowClusters
            ValidationDrawIndices = [long]$report.bfp2ValidationDrawIndices
            ValidationPerPackHash = $report.bfp2ValidationPerPackHash
            Screenshot = $screenshotPath
            Report = $reportPath
        }
        $rows.Add($row)

        Write-Host (
            "{0}: GPU {1:F3}/{2:F3}/{3:F3} ms, frame {4:F3}/{5:F3}/{6:F3} ms, FPS {7:F2}" -f
            $stem,
            $row.GpuMsAverage,
            $row.GpuMsP95,
            $row.GpuMsP99,
            $row.FrameMsAverage,
            $row.FrameMsP95,
            $row.FrameMsP99,
            $row.FpsAverage)
    }
}

$referenceValidationHash =
    ($rows | Where-Object Variant -eq 'scalar-aos' | Select-Object -First 1).ValidationPerPackHash
foreach ($row in $rows) {
    if ($row.ValidationPerPackHash -ne $referenceValidationHash) {
        throw (
            "GPU cull-output mismatch: $($row.Variant) round $($row.Round) hash " +
            "$($row.ValidationPerPackHash), expected $referenceValidationHash.")
    }
}

$runCsvPath = Join-Path $outputRoot 'runs.csv'
$rows | Export-Csv -LiteralPath $runCsvPath -NoTypeInformation -Encoding utf8

$baselineGpu = Get-Median @($rows | Where-Object Variant -eq 'scalar-aos' | ForEach-Object GpuMsAverage)
$baselineGpuP99 = Get-Median @($rows | Where-Object Variant -eq 'scalar-aos' | ForEach-Object GpuMsP99)
$baselineFrame = Get-Median @($rows | Where-Object Variant -eq 'scalar-aos' | ForEach-Object FrameMsAverage)
$baselineFrameP99 = Get-Median @($rows | Where-Object Variant -eq 'scalar-aos' | ForEach-Object FrameMsP99)
$aggregateRows = foreach ($variant in $variantDefinitions) {
    $variantRows = @($rows | Where-Object Variant -eq $variant.Name)
    $gpuMedian = Get-Median @($variantRows | ForEach-Object GpuMsAverage)
    $gpuP99Median = Get-Median @($variantRows | ForEach-Object GpuMsP99)
    $frameMedian = Get-Median @($variantRows | ForEach-Object FrameMsAverage)
    $frameP99Median = Get-Median @($variantRows | ForEach-Object FrameMsP99)
    [pscustomobject]@{
        Variant = $variant.Name
        Repeats = $variantRows.Count
        GpuMsAverageMedian = $gpuMedian
        GpuMsP99Median = $gpuP99Median
        GpuAverageDeltaPercent = 100.0 * ($gpuMedian - $baselineGpu) / $baselineGpu
        GpuP99DeltaPercent = 100.0 * ($gpuP99Median - $baselineGpuP99) / $baselineGpuP99
        FrameMsAverageMedian = $frameMedian
        FrameMsP99Median = $frameP99Median
        FrameAverageDeltaPercent = 100.0 * ($frameMedian - $baselineFrame) / $baselineFrame
        FrameP99DeltaPercent = 100.0 * ($frameP99Median - $baselineFrameP99) / $baselineFrameP99
        FpsAverageMedian = Get-Median @($variantRows | ForEach-Object FpsAverage)
    }
}

$aggregateCsvPath = Join-Path $outputRoot 'aggregate.csv'
$aggregateRows | Export-Csv -LiteralPath $aggregateCsvPath -NoTypeInformation -Encoding utf8
$summaryPath = Join-Path $outputRoot 'SUMMARY.md'
$summary = [System.Collections.Generic.List[string]]::new()
$summary.Add('# GPU Cluster Layout and Wave Compaction A/B')
$summary.Add('')
$summary.Add("Device index: $DeviceIndex; cameras: $CameraCount; screen cull: $ScreenCullPixels px; repeats: $Repeats.")
$summary.Add("Cull-output validation hash: $referenceValidationHash (identical across every run).")
$summary.Add('')
$summary.Add('| Variant | GPU avg (ms) | GPU avg delta | GPU P99 (ms) | GPU P99 delta | Frame avg (ms) | Frame avg delta | Frame P99 (ms) | Frame P99 delta | FPS |')
$summary.Add('|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|')
foreach ($row in $aggregateRows) {
    $summary.Add((
        '| {0} | {1:F3} | {2:+0.00;-0.00;0.00}% | {3:F3} | {4:+0.00;-0.00;0.00}% | {5:F3} | {6:+0.00;-0.00;0.00}% | {7:F3} | {8:+0.00;-0.00;0.00}% | {9:F2} |' -f
        $row.Variant,
        $row.GpuMsAverageMedian,
        $row.GpuAverageDeltaPercent,
        $row.GpuMsP99Median,
        $row.GpuP99DeltaPercent,
        $row.FrameMsAverageMedian,
        $row.FrameAverageDeltaPercent,
        $row.FrameMsP99Median,
        $row.FrameP99DeltaPercent,
        $row.FpsAverageMedian))
}
$summary.Add('')
$summary.Add('Negative deltas are improvements. GPU profiler markers are disabled in timed runs.')
[System.IO.File]::WriteAllLines($summaryPath, $summary)

Write-Host "Completed counterbalanced GPU layout/wave A/B runs: $outputRoot"
