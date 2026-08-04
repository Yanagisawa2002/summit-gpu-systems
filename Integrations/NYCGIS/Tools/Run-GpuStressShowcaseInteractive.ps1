[CmdletBinding()]
param(
    [ValidateSet('baseline', 'optimized')]
    [string]$Variant = 'baseline',

    [string]$DataRoot = $env:NYCGIS_DATA_ROOT,

    [ValidateRange(1, 4)]
    [int]$CameraCount = 4,

    [ValidateRange(60, 3600)]
    [int]$SampleFrames = 1800,

    [string]$UnityPath,

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$playerPath = Join-Path $projectRoot (
    'Builds\Validation\FullCityWeather\NYCGISFullCityWeatherQA.exe')

function Resolve-UnityEditor {
    if (-not [string]::IsNullOrWhiteSpace($UnityPath)) {
        return (Resolve-Path -LiteralPath $UnityPath).Path
    }
    $versionLine = Get-Content -LiteralPath (
        (Join-Path $projectRoot 'ProjectSettings\ProjectVersion.txt')) |
        Select-Object -First 1
    $version = ($versionLine -split ':', 2)[1].Trim()
    $candidate = Join-Path 'C:\Program Files\Unity\Hub\Editor' (
        "$version\Editor\Unity.exe")
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Unity $version is missing: $candidate"
    }
    return $candidate
}

if (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
    $env:NYCGIS_DATA_ROOT = (Resolve-Path -LiteralPath $DataRoot).Path
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputRoot = Join-Path $projectRoot (
    "Reports\GpuStressShowcase\interactive-$Variant-$stamp")
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

if (-not $SkipBuild) {
    $buildLog = Join-Path $outputRoot 'build.log'
    $build = Start-Process -FilePath (Resolve-UnityEditor) `
        -ArgumentList @(
            '-batchmode',
            '-projectPath', "`"$projectRoot`"",
            '-executeMethod', 'GpuStressShowcaseBuild.BuildBatch',
            '-logFile', "`"$buildLog`"") `
        -WorkingDirectory $projectRoot `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($build.ExitCode -ne 0) {
        throw "Showcase build failed. See $buildLog"
    }
}
if (-not (Test-Path -LiteralPath $playerPath -PathType Leaf)) {
    throw "Showcase Player is missing: $playerPath"
}

$reportPath = Join-Path $outputRoot "$Variant.json"
$logPath = Join-Path $outputRoot "$Variant.log"
$arguments = @(
    '-force-d3d12',
    '-screen-fullscreen', '0',
    '-screen-width', '1600',
    '-screen-height', '900',
    '-gpu-stress-showcase',
    '-gpu-stress-variant', $Variant,
    '-gpu-stress-cameras', [string]$CameraCount,
    '-gpu-stress-scene-warmup-seconds', '60',
    '-gpu-stress-workload-warmup-frames', '120',
    '-gpu-stress-sample-frames', [string]$SampleFrames,
    '-gpu-stress-sensor-elements', '1048576',
    '-gpu-stress-points-per-page', '2048',
    '-gpu-stress-report', "`"$reportPath`"",
    '-logFile', "`"$logPath`""
)
$process = Start-Process -FilePath $playerPath `
    -ArgumentList $arguments `
    -WorkingDirectory $projectRoot `
    -PassThru
Write-Host "Started $Variant visual showcase (PID $($process.Id))."
Write-Host "Close the Player when finished. Output: $outputRoot"
