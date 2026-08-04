[CmdletBinding()]
param(
    [string]$DataRoot = $env:NYCGIS_DATA_ROOT,

    [ValidateRange(0, 16)]
    [int]$DeviceIndex = 0,

    [ValidateRange(1, 4)]
    [int]$CameraCount = 4,

    [ValidateRange(0, 300)]
    [int]$SceneWarmupSeconds = 60,

    [ValidateRange(0, 600)]
    [int]$WorkloadWarmupFrames = 120,

    [ValidateRange(60, 3600)]
    [int]$SampleFrames = 900,

    [ValidateRange(1, 6)]
    [int]$Rounds = 3,

    [ValidateRange(65536, 1048576)]
    [int]$SensorElementCount = 1048576,

    [ValidateRange(256, 2048)]
    [int]$ResidencyPointsPerPage = 2048,

    [int]$Seed = 1731,

    [string]$UnityPath,

    [string]$OutputDirectory,

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$playerPath = Join-Path $projectRoot (
    'Builds\Validation\FullCityWeather\NYCGISFullCityWeatherQA.exe')

function Resolve-UnityEditor {
    param([string]$RequestedPath)
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = (Resolve-Path -LiteralPath $RequestedPath).Path
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Unity Editor is missing: $resolved"
        }
        return $resolved
    }
    $versionLine = Get-Content -LiteralPath (
        (Join-Path $projectRoot 'ProjectSettings\ProjectVersion.txt')) |
        Select-Object -First 1
    $version = ($versionLine -split ':', 2)[1].Trim()
    $candidate = Join-Path 'C:\Program Files\Unity\Hub\Editor' (
        "$version\Editor\Unity.exe")
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Unity $version was not found at $candidate"
    }
    return $candidate
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $projectRoot (
        "Reports\GpuStressShowcase\local-$stamp")
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

if (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
    $resolvedDataRoot = (Resolve-Path -LiteralPath $DataRoot).Path
    if (-not (Test-Path -LiteralPath $resolvedDataRoot -PathType Container)) {
        throw "NYC GIS data root is missing: $resolvedDataRoot"
    }
    $env:NYCGIS_DATA_ROOT = $resolvedDataRoot
}

if (-not $SkipBuild) {
    $resolvedUnity = Resolve-UnityEditor -RequestedPath $UnityPath
    $buildLog = Join-Path $outputRoot 'build.log'
    $buildArgs = @(
        '-batchmode',
        '-projectPath', "`"$projectRoot`"",
        '-executeMethod', 'GpuStressShowcaseBuild.BuildBatch',
        '-logFile', "`"$buildLog`""
    )
    $build = Start-Process -FilePath $resolvedUnity `
        -ArgumentList $buildArgs `
        -WorkingDirectory $projectRoot `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($build.ExitCode -ne 0) {
        throw "Showcase Player build failed with exit code $($build.ExitCode). See $buildLog"
    }
}

if (-not (Test-Path -LiteralPath $playerPath -PathType Leaf)) {
    throw "Showcase Player is missing: $playerPath"
}

$rows = [System.Collections.Generic.List[object]]::new()
for ($round = 1; $round -le $Rounds; $round++) {
    $variants = if (($round % 2) -eq 1) {
        @('baseline', 'optimized')
    }
    else {
        @('optimized', 'baseline')
    }
    foreach ($variant in $variants) {
        $stem = "$variant-r$round"
        $reportPath = Join-Path $outputRoot "$stem.json"
        $screenshotPath = Join-Path $outputRoot "$stem.png"
        $logPath = Join-Path $outputRoot "$stem.log"
        $arguments = @(
            '-force-d3d12',
            '-force-device-index', [string]$DeviceIndex,
            '-screen-fullscreen', '0',
            '-screen-width', '1600',
            '-screen-height', '900',
            '-gpu-stress-showcase',
            '-gpu-stress-auto-exit',
            '-gpu-stress-variant', $variant,
            '-gpu-stress-cameras', [string]$CameraCount,
            '-gpu-stress-scene-warmup-seconds', [string]$SceneWarmupSeconds,
            '-gpu-stress-workload-warmup-frames', [string]$WorkloadWarmupFrames,
            '-gpu-stress-sample-frames', [string]$SampleFrames,
            '-gpu-stress-sensor-elements', [string]$SensorElementCount,
            '-gpu-stress-points-per-page', [string]$ResidencyPointsPerPage,
            '-gpu-stress-seed', [string]$Seed,
            '-gpu-stress-report', "`"$reportPath`"",
            '-gpu-stress-screenshot', "`"$screenshotPath`"",
            '-logFile', "`"$logPath`""
        )
        $process = Start-Process -FilePath $playerPath `
            -ArgumentList $arguments `
            -WorkingDirectory $projectRoot `
            -WindowStyle Minimized `
            -Wait `
            -PassThru
        if ($process.ExitCode -ne 0) {
            throw "$stem failed with Player exit code $($process.ExitCode). See $logPath"
        }
        if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
            throw "$stem did not produce $reportPath"
        }
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        if (-not $report.qualityPassed) {
            throw "$stem failed output validation: $($report.qualityMessage)"
        }
        if ($report.variant -ne $variant) {
            throw "$stem reported variant '$($report.variant)'."
        }
        if ($variant -eq 'optimized' -and
            $report.rendererAlgorithm -ne 'WaveTile32') {
            throw "$stem did not activate WaveTile32; got $($report.rendererAlgorithm)."
        }
        $rows.Add([pscustomobject]@{
            Round = $round
            Variant = $variant
            FrameAverageMs = [double]$report.frameAverageMs
            FrameP99Ms = [double]$report.frameP99Ms
            GpuAverageMs = [double]$report.gpuAverageMs
            GpuP99Ms = [double]$report.gpuP99Ms
            FpsAverage = [double]$report.fpsAverage
            LongFrame33Rate = [double]$report.longFrame33Rate
            SensorUploadBytes = [long]$report.sensorLogicalUploadBytes
            ResidencyUploadBytes = [long]$report.residencyLogicalUploadBytes
            CriticalP99Ms = [double]$report.criticalLatencyP99Ms
            OutputHash = [string]$report.compositeOutputHash
            Report = $reportPath
        })
    }
}

$rows | Export-Csv -LiteralPath (Join-Path $outputRoot 'runs.csv') `
    -NoTypeInformation
$summaryScript = Join-Path $PSScriptRoot 'Summarize-GpuStressShowcaseAB.ps1'
& powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File $summaryScript `
    -InputDirectory $outputRoot
if ($LASTEXITCODE -ne 0) {
    throw "Showcase summary failed with exit code $LASTEXITCODE"
}
Write-Host "GPU stress A/B complete: $outputRoot"
