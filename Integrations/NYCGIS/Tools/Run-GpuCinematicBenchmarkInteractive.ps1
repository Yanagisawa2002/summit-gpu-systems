[CmdletBinding()]
param(
    [ValidateSet('baseline', 'optimized')][string]$Variant = 'optimized',
    [string]$DataRoot = $env:NYCGIS_DATA_ROOT,
    [ValidateRange(1, 4)][int]$CameraCount = 1,
    [ValidateRange(60, 3600)][int]$SampleFrames = 1800,
    [ValidateRange(1, 60)][int]$WorkloadIssueIntervalFrames = 12,
    [ValidateRange(1280, 7680)][int]$Width = 1920,
    [ValidateRange(720, 4320)][int]$Height = 1080,
    [string]$UnityPath,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$playerPath = Join-Path $projectRoot 'Builds\Validation\FullCityWeather\NYCGISFullCityWeatherQA.exe'

function Resolve-UnityEditor {
    if (-not [string]::IsNullOrWhiteSpace($UnityPath)) {
        return (Resolve-Path -LiteralPath $UnityPath).Path
    }
    $versionLine = Get-Content -LiteralPath (Join-Path $projectRoot 'ProjectSettings\ProjectVersion.txt') | Select-Object -First 1
    $version = ($versionLine -split ':', 2)[1].Trim()
    $candidate = Join-Path 'C:\Program Files\Unity\Hub\Editor' "$version\Editor\Unity.exe"
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Unity $version is missing: $candidate"
    }
    return $candidate
}

if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $env:USERPROFILE 'Downloads\NYCGISData'
}
if ($CameraCount -ne 1 -and $CameraCount -ne 4) {
    throw 'The cinematic benchmark requires one or four cameras.'
}
$env:NYCGIS_DATA_ROOT = (Resolve-Path -LiteralPath $DataRoot).Path

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputRoot = Join-Path $projectRoot "Reports\GpuCinematicBenchmark\interactive-$Variant-$stamp"
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

if (-not $SkipBuild) {
    $buildLog = Join-Path $outputRoot 'build.log'
    $buildArgs = @(
        '-batchmode',
        '-projectPath', $projectRoot,
        '-executeMethod', 'GpuStressShowcaseBuild.BuildBatch',
        '-logFile', $buildLog
    )
    $buildOptions = @{
        FilePath = (Resolve-UnityEditor)
        ArgumentList = $buildArgs
        WorkingDirectory = $projectRoot
        WindowStyle = 'Hidden'
        Wait = $true
        PassThru = $true
    }
    $build = Start-Process @buildOptions
    if ($build.ExitCode -ne 0) {
        throw "Cinematic showcase build failed. See $buildLog"
    }
}
if (-not (Test-Path -LiteralPath $playerPath -PathType Leaf)) {
    throw "Cinematic showcase Player is missing: $playerPath"
}
$reportPath = Join-Path $outputRoot "$Variant.json"
$screenshotPath = Join-Path $outputRoot "$Variant.png"
$logPath = Join-Path $outputRoot "$Variant.log"
$arguments = @(
    '-force-d3d12',
    '-screen-fullscreen', '0',
    '-screen-width', [string]$Width,
    '-screen-height', [string]$Height,
    '-gpu-stress-showcase',
    '-gpu-stress-cinematic-fullscreen',
    '-gpu-stress-output-width', [string]$Width,
    '-gpu-stress-output-height', [string]$Height,
    '-gpu-stress-cinematic',
    '-gpu-stress-variant', $Variant,
    '-gpu-stress-cameras', [string]$CameraCount,
    '-gpu-stress-cinematic-duration-seconds', '45',
    '-gpu-stress-cinematic-time-of-day', '17.1',
    '-gpu-stress-scene-warmup-seconds', '60',
    '-gpu-stress-workload-warmup-frames', '120',
    '-gpu-stress-workload-issue-interval-frames',
        [string]$WorkloadIssueIntervalFrames,
    '-gpu-stress-sample-frames', [string]$SampleFrames,
    '-gpu-stress-sensor-elements', '1048576',
    '-gpu-stress-points-per-page', '2048',
    '-gpu-stress-report', $reportPath,
    '-gpu-stress-screenshot', $screenshotPath,
    '-logFile', $logPath
)
$runOptions = @{
    FilePath = $playerPath
    ArgumentList = $arguments
    WorkingDirectory = $projectRoot
    PassThru = $true
}
$process = Start-Process @runOptions
Write-Host "Started $Variant cinematic showcase (PID $($process.Id))."
Write-Host "Close the Player when finished. Output: $outputRoot"
