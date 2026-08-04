[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DataRoot,

    [ValidateRange(0, 16)]
    [int]$DeviceIndex = 0,

    [ValidateSet(1, 4, 6)]
    [int]$CameraCount = 1,

    [ValidateRange(5, 300)]
    [int]$WarmupSeconds = 60,

    [ValidateRange(120, 3600)]
    [int]$SampleFrames = 900,

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
        "Reports\GpuClusterCull\ab-$timestamp-device-$DeviceIndex-cameras-$CameraCount")
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

for ($round = 1; $round -le $Repeats; $round++) {
    foreach ($variant in @(
        @{ Name = 'off'; Pixels = 0 },
        @{ Name = '1px'; Pixels = 1 }
    )) {
        $stem = "device-$DeviceIndex-cameras-$CameraCount-$($variant.Name)-r$round"
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
            '-nycgis-bfp2-screen-cull-pixels', [string]$variant.Pixels,
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

        Write-Host (
            "{0}: GPU {1}/{2}/{3} ms, frame {4}/{5}/{6} ms, FPS {7}, packs {8}/{9}" -f
            $stem,
            $report.gpuFrameMsAverage,
            $report.gpuFrameMsP95,
            $report.gpuFrameMsP99,
            $report.frameMsAverage,
            $report.frameMsP95,
            $report.frameMsP99,
            $report.fpsAverage,
            $report.bfp2ResidentPacks,
            $report.bfp2TotalPacks)
    }
}

Write-Host "Completed paired GPU cluster-culling A/B runs: $outputRoot"
