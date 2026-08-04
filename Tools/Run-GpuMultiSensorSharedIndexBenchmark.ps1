[CmdletBinding()]
param(
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe',
    [string]$OutputDirectory,
    [ValidateSet('discovery', 'formal', 'single')]
    [string]$MatrixPreset = 'discovery',
    [ValidateRange(0, 16)] [int]$DeviceIndex = 0,
    [ValidateRange(1024, 16776960)] [int]$ElementCount = 262144,
    [ValidateRange(2, 64)] [int]$SensorCount = 4,
    [ValidateRange(1, 1024)] [int]$QueriesPerSensor = 32,
    [int]$Seed = 20260801,
    [ValidateRange(1, 4)] [int]$SuperRounds = 4,
    [ValidateRange(5, 1800)] [int]$WarmupFrames = 60,
    [ValidateRange(64, 7200)] [int]$SampleFrames = 240,
    [ValidateRange(0, 600)] [int]$CooldownFrames = 15,
    [ValidateRange(5, 240)] [int]$PlayerTimeoutMinutes = 90,
    [switch]$SkipBuild,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw "Unity Editor is missing: $UnityPath"
}
$status = @(git -C $projectRoot status --porcelain)
if (-not $AllowDirty -and $status.Count -ne 0) {
    throw "Benchmark requires a clean worktree.`n$($status -join "`n")"
}
$commit = (git -C $projectRoot rev-parse HEAD).Trim()
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $projectRoot "Reports\GpuMultiSensorSharedIndex\$MatrixPreset-$stamp"
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$player = Join-Path $projectRoot 'Builds\GpuMultiSensorSharedIndex\GpuMultiSensorSharedIndex.exe'
$buildLog = Join-Path $outputRoot 'unity-build.log'

if (-not $SkipBuild) {
    $buildArgs = @(
        '-batchmode','-nographics','-quit',
        '-projectPath', $projectRoot,
        '-executeMethod','GpuSensorPipelineBenchmarkBuild.PerformBuild',
        '-gpu-sensor-pipeline-player-path', $player,
        '-logFile', $buildLog)
    $build = Start-Process -FilePath $UnityPath -ArgumentList $buildArgs -WindowStyle Hidden -PassThru -Wait
    if ($build.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $player)) {
        throw "Unity Player build failed. See $buildLog"
    }
}
elseif (-not (Test-Path -LiteralPath $player -PathType Leaf)) {
    throw "Benchmark Player is missing: $player"
}

$scenarios = switch ($MatrixPreset) {
    'formal' {
        @(
            [pscustomobject]@{ id='shared-n262144-s4-q32'; n=262144; sensors=4; q=32; seed=20260801 },
            [pscustomobject]@{ id='shared-n1048576-s4-q64'; n=1048576; sensors=4; q=64; seed=20260802 })
    }
    'discovery' {
        @(
            [pscustomobject]@{ id='shared-n262144-s4-q32'; n=262144; sensors=4; q=32; seed=20260801 },
            [pscustomobject]@{ id='shared-n1048576-s4-q64'; n=1048576; sensors=4; q=64; seed=20260802 })
    }
    default {
        @([pscustomobject]@{ id='shared-custom'; n=$ElementCount; sensors=$SensorCount; q=$QueriesPerSensor; seed=$Seed })
    }
}
if ($MatrixPreset -ceq 'formal') { $SampleFrames = 900 }

$shaderPath = Join-Path $projectRoot 'Packages\com.summit.gpu-sensor-pipeline\Runtime\Resources\GpuSensorPipeline\GpuSensorPipeline.compute'
$apiPath = Join-Path $projectRoot 'Packages\com.summit.gpu-sensor-pipeline\Runtime\GpuSensorPipeline.cs'
$timestampDll = Join-Path (Split-Path -Parent $player) 'GpuMultiSensorSharedIndex_Data\Plugins\x86_64\SummitGpuTimestamps.dll'
$shaderHash = (Get-FileHash -LiteralPath $shaderPath -Algorithm SHA256).Hash
$apiHash = (Get-FileHash -LiteralPath $apiPath -Algorithm SHA256).Hash
$dllHash = (Get-FileHash -LiteralPath $timestampDll -Algorithm SHA256).Hash

foreach ($scenario in $scenarios) {
    $scenarioRoot = Join-Path $outputRoot $scenario.id
    New-Item -ItemType Directory -Path $scenarioRoot -Force | Out-Null
    $playerLog = Join-Path $scenarioRoot 'player.log'
    $arguments = @(
        '-batchmode','-force-d3d12','-force-device-index',[string]$DeviceIndex,
        '-gpu-sensor-pipeline-benchmark',
        '-gpu-sensor-pipeline-mode','multi-sensor-shared-index',
        '-gpu-sensor-pipeline-report-dir',('"' + $scenarioRoot + '"'),
        '-gpu-sensor-pipeline-scenario-id',$scenario.id,
        '-gpu-sensor-pipeline-super-rounds',[string]$SuperRounds,
        '-gpu-sensor-pipeline-warmup-frames',[string]$WarmupFrames,
        '-gpu-sensor-pipeline-sample-frames',[string]$SampleFrames,
        '-gpu-sensor-pipeline-cooldown-frames',[string]$CooldownFrames,
        '-gpu-sensor-pipeline-element-count',[string]$scenario.n,
        '-gpu-sensor-pipeline-bin-count','262144',
        '-gpu-sensor-pipeline-sensor-count',[string]$scenario.sensors,
        '-gpu-sensor-pipeline-queries-per-sensor',[string]$scenario.q,
        '-gpu-sensor-pipeline-seed',[string]$scenario.seed,
        '-gpu-sensor-pipeline-staging-slot-count','4',
        '-gpu-sensor-pipeline-validation-timeout-seconds','120',
        '-gpu-sensor-pipeline-require-complete-gpu-timings','1',
        '-gpu-sensor-pipeline-build-commit',$commit,
        '-gpu-sensor-pipeline-runtime-shader-sha256',$shaderHash,
        '-gpu-sensor-pipeline-runtime-api-sha256',$apiHash,
        '-gpu-sensor-pipeline-native-timestamp-dll-sha256',$dllHash,
        '-logFile',('"' + $playerLog + '"'))
    Write-Host "Running $($scenario.id): N=$($scenario.n), sensors=$($scenario.sensors), queries/sensor=$($scenario.q)"
    $process = Start-Process -FilePath $player -ArgumentList $arguments -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($PlayerTimeoutMinutes * 60 * 1000)) {
        $process.Kill()
        throw "Scenario '$($scenario.id)' timed out."
    }
    if ($process.ExitCode -ne 0) {
        throw "Scenario '$($scenario.id)' failed with exit code $($process.ExitCode). See $playerLog"
    }
}

$runner = [ordered]@{
    suite = 'summit.gpu-multi-sensor-shared-index'
    matrixPreset = $MatrixPreset
    gitCommit = $commit
    deviceIndex = $DeviceIndex
    superRounds = $SuperRounds
    warmupFrames = $WarmupFrames
    sampleFrames = $SampleFrames
    cooldownFrames = $CooldownFrames
    scenarios = @($scenarios)
}
$runner | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputRoot 'runner-config.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'Summarize-GpuMultiSensorSharedIndexBenchmark.ps1') -ReportDirectory $outputRoot
Write-Host "Completed multi-sensor shared-index benchmark: $outputRoot"
