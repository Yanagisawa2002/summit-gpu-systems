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

    [ValidateSet('Dense', 'Bev', 'BevResident')]
    [string]$Workload = 'Dense',

    [ValidateRange(5, 300)]
    [int]$WarmupSeconds = 30,

    [ValidateRange(120, 3600)]
    [int]$SampleFrames = 600,

    [ValidateRange(1, 10)]
    [int]$Repeats = 3,

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
        "Reports\GpuSensorData\ab-$timestamp-device-$DeviceIndex-" +
        "$($Workload.ToLowerInvariant())-$($SensorWidth)x$($SensorHeight)-$($SensorRateHz)hz")
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

$variants = if ($Workload -eq 'BevResident') {
    @('off', 'cpu-bev-cost', 'gpu-bev-resident')
}
elseif ($Workload -eq 'Bev') {
    @('off', 'cpu-bev', 'gpu-bev')
}
else {
    @('off', 'cpu', 'gpu')
}
$summary = [System.Collections.Generic.List[object]]::new()
for ($round = 1; $round -le $Repeats; $round++) {
    foreach ($variant in $variants) {
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
        if ([int]$report.sensorCorrectnessPassed -ne 1) {
            throw "$stem failed the sensor workload correctness check. See $reportPath"
        }

        $summary.Add([pscustomobject]@{
            round = $round
            mode = $variant
            device = $report.graphicsDeviceName
            sensorProduct = $report.sensorProduct
            frameAverageMs = $report.frameMsAverage
            frameP95Ms = $report.frameMsP95
            frameP99Ms = $report.frameMsP99
            gpuAverageMs = $report.gpuFrameMsAverage
            gpuP95Ms = $report.gpuFrameMsP95
            gpuP99Ms = $report.gpuFrameMsP99
            mainAverageMs = $report.mainThreadMsAverage
            mainP99Ms = $report.mainThreadMsP99
            sensorCompletedFrames = $report.sensorCompletedFrames
            sensorDroppedFrames = $report.sensorDroppedFrames
            sensorMPointsPerSecond = $report.sensorMillionPointsPerSecond
            sensorOutputGiBPerSecond = $report.sensorOutputGiBPerSecond
            sensorHostWorkAverageMs = $report.sensorHostWorkMsAverage
            sensorHostWorkP99Ms = $report.sensorHostWorkMsP99
            sensorLatencyAverageMs = $report.sensorCompletionLatencyMsAverage
            sensorLatencyP99Ms = $report.sensorCompletionLatencyMsP99
            sensorMaximumPointError = $report.sensorMaximumPointError
            sensorMeasurementReadbackBytesPerFrame =
                $report.sensorMeasurementReadbackBytesPerFrame
            sensorGpuResidentProduct = $report.sensorGpuResidentProduct
            sensorCompletionSemantics = $report.sensorCompletionSemantics
        })

        Write-Host (
            ("{0}: frame {1}/{2}/{3} ms, GPU {4}/{5}/{6} ms, sensor {7} Mpts/s, " +
             "host P99 {8} ms, latency P99 {9} ms, drops {10}") -f
             $stem,
            $report.frameMsAverage,
            $report.frameMsP95,
            $report.frameMsP99,
            $report.gpuFrameMsAverage,
            $report.gpuFrameMsP95,
            $report.gpuFrameMsP99,
            $report.sensorMillionPointsPerSecond,
            $report.sensorHostWorkMsP99,
            $report.sensorCompletionLatencyMsP99,
            $report.sensorDroppedFrames)
    }
}

$summaryPath = Join-Path $outputRoot 'summary.csv'
$summary | Export-Csv -LiteralPath $summaryPath -NoTypeInformation -Encoding utf8
Write-Host "Completed GPU sensor-data A/B runs: $outputRoot"
Write-Host "Summary: $summaryPath"
