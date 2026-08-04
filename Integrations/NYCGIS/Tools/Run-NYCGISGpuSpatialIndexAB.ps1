[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DataRoot,

    [ValidateRange(0, 16)]
    [int]$DeviceIndex = 0,

    [ValidateSet(1, 4, 6)]
    [int]$CameraCount = 1,

    [ValidateRange(160, 3840)]
    [int]$SensorWidth = 1280,

    [ValidateRange(90, 2160)]
    [int]$SensorHeight = 720,

    [ValidateRange(1, 240)]
    [int]$SensorRateHz = 30,

    [ValidateRange(16, 4096)]
    [int]$SpatialQueryCount = 256,

    [ValidateRange(5, 300)]
    [int]$WarmupSeconds = 60,

    [ValidateRange(120, 3600)]
    [int]$SampleFrames = 900,

    [ValidateRange(1, 9)]
    [int]$Repeats = 3,

    [bool]$RequireExpectedProductionSignature = $true,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

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
        "Reports\GpuSpatialIndex\ab-$timestamp-device-$DeviceIndex-" +
        "$($SensorWidth)x$($SensorHeight)-$($SensorRateHz)hz-" +
        "q$SpatialQueryCount")
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

function Get-RunOrder {
    param([Parameter(Mandatory = $true)][int]$Round)

    $orders = @(
        @('off', 'gpu-spatial-brute', 'gpu-spatial-index'),
        @('gpu-spatial-brute', 'gpu-spatial-index', 'off'),
        @('gpu-spatial-index', 'off', 'gpu-spatial-brute')
    )
    return $orders[($Round - 1) % $orders.Count]
}

$expectedSignature = '227|73|0|1932028912|66|180'
$observedSignature = $null
$summary = [System.Collections.Generic.List[object]]::new()
for ($round = 1; $round -le $Repeats; $round++) {
    foreach ($variant in (Get-RunOrder -Round $round)) {
        $stem = "device-$DeviceIndex-cameras-$CameraCount-$variant-r$round"
        $reportPath = Join-Path $outputRoot "$stem.txt"
        $logPath = Join-Path $outputRoot "$stem.log"
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
            '-nycgis-bfp2-screen-cull-pixels', '1',
            '-nycgis-sensor-mode', $variant,
            '-nycgis-sensor-width', [string]$SensorWidth,
            '-nycgis-sensor-height', [string]$SensorHeight,
            '-nycgis-sensor-rate-hz', [string]$SensorRateHz,
            '-nycgis-spatial-query-count', [string]$SpatialQueryCount,
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
        if ([int]$report.sensorCorrectnessPassed -ne 1) {
            throw "$stem failed the spatial workload correctness gate. See $reportPath"
        }
        if ([int]$report.bfp2LoadingPacks -ne 0) {
            throw "$stem still had $($report.bfp2LoadingPacks) BFP2 packs loading after warm-up."
        }

        $signature = (
            "$($report.bfp2TotalPacks)|$($report.bfp2ResidentPacks)|" +
            "$($report.bfp2LoadingPacks)|$($report.bfp2ResidentGpuBytes)|" +
            "$($report.orthophotoBasePages)|$($report.orthophotoLod0Pages)")
        if ($null -eq $observedSignature) {
            $observedSignature = $signature
        }
        elseif ($signature -ne $observedSignature) {
            throw "$stem residency signature '$signature' differs from '$observedSignature'."
        }
        if ($RequireExpectedProductionSignature -and
            $signature -ne $expectedSignature) {
            throw "$stem production signature '$signature' differs from expected '$expectedSignature'."
        }

        $summary.Add([pscustomobject]@{
            round = $round
            order = [string]::Join('>', (Get-RunOrder -Round $round))
            mode = $variant
            device = $report.graphicsDeviceName
            frameAverageMs = $report.frameMsAverage
            frameP95Ms = $report.frameMsP95
            frameP99Ms = $report.frameMsP99
            gpuAverageMs = $report.gpuFrameMsAverage
            gpuP95Ms = $report.gpuFrameMsP95
            gpuP99Ms = $report.gpuFrameMsP99
            mainAverageMs = $report.mainThreadMsAverage
            mainP99Ms = $report.mainThreadMsP99
            spatialAlgorithm = $report.sensorSpatialAlgorithm
            spatialPointCount = $report.sensorPointCount
            spatialQueryCount = $report.sensorSpatialQueryCount
            spatialGridResolution = $report.sensorSpatialGridResolution
            spatialRadixPasses = $report.sensorSpatialRadixPasses
            spatialResidentBytes = $report.sensorSpatialResidentBytes
            spatialCandidateTestsPerUpdate = $report.sensorSpatialCandidateTestsPerUpdate
            spatialCandidateReductionPercent = $report.sensorSpatialCandidateReductionPercent
            spatialUpdatesPerSecond = $report.sensorSpatialUpdatesPerSecond
            spatialQueriesPerSecond = $report.sensorSpatialQueriesPerSecond
            spatialHostAverageMs = $report.sensorHostWorkMsAverage
            spatialHostP99Ms = $report.sensorHostWorkMsP99
            spatialSortedOrderViolations = $report.sensorSpatialSortedOrderViolations
            spatialCellRangeViolations = $report.sensorSpatialCellRangeViolations
            spatialQueryMismatchCount = $report.sensorSpatialQueryMismatchCount
            spatialBoundsViolations = $report.sensorSpatialBoundsViolations
            spatialResultHash = $report.sensorSpatialResultHash
            measurementReadbackBytesPerFrame = $report.sensorMeasurementReadbackBytesPerFrame
            bfp2TotalPacks = $report.bfp2TotalPacks
            bfp2ResidentPacks = $report.bfp2ResidentPacks
            bfp2LoadingPacks = $report.bfp2LoadingPacks
            bfp2ResidentGpuBytes = $report.bfp2ResidentGpuBytes
            orthophotoBasePages = $report.orthophotoBasePages
            orthophotoLod0Pages = $report.orthophotoLod0Pages
        })

        Write-Host (
            ("{0}: frame {1}/{2}/{3} ms, GPU {4}/{5}/{6} ms, " +
             "spatial {7} updates/s, {8} queries/s, host P99 {9} ms, " +
             "candidate reduction {10}%, signature {11}") -f
            $stem,
            $report.frameMsAverage,
            $report.frameMsP95,
            $report.frameMsP99,
            $report.gpuFrameMsAverage,
            $report.gpuFrameMsP95,
            $report.gpuFrameMsP99,
            $report.sensorSpatialUpdatesPerSecond,
            $report.sensorSpatialQueriesPerSecond,
            $report.sensorHostWorkMsP99,
            $report.sensorSpatialCandidateReductionPercent,
            $signature)
    }
}

$summaryPath = Join-Path $outputRoot 'summary.csv'
$summary | Export-Csv -LiteralPath $summaryPath -NoTypeInformation -Encoding utf8
Write-Host "Completed GPU spatial-index A/B runs: $outputRoot"
Write-Host "Summary: $summaryPath"
