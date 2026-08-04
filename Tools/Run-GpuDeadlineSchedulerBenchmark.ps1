[CmdletBinding()]
param(
    [string]$UnityPath =
        'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe',
    [string]$OutputDirectory,
    [string]$PlayerPath,
    [ValidateSet('smoke', 'discovery', 'formal')]
    [string]$MatrixPreset = 'discovery',
    [ValidateRange(0, 16)]
    [int]$DeviceIndex = 0,
    [ValidateRange(5, 120)]
    [int]$PlayerTimeoutMinutes = 60,
    [switch]$SkipBuild,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Quote-Argument([string]$Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Wait-ProcessBounded(
    [System.Diagnostics.Process]$Process,
    [int]$TimeoutMilliseconds,
    [string]$Description) {
    if (-not $Process.WaitForExit($TimeoutMilliseconds)) {
        $Process.Kill()
        $Process.WaitForExit()
        throw "$Description timed out."
    }
    if ($Process.ExitCode -ne 0) {
        throw "$Description failed with exit code $($Process.ExitCode)."
    }
}

$projectRoot = [System.IO.Path]::GetFullPath(
    (Split-Path -Parent $PSScriptRoot))
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw "Unity editor is missing: $UnityPath"
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot (
        'Reports\GpuDeadlineScheduler\' +
        [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

if ([string]::IsNullOrWhiteSpace($PlayerPath)) {
    $PlayerPath = Join-Path $projectRoot (
        'Builds\GpuDeadlineSchedulerBenchmark\' +
        'GpuDeadlineSchedulerBenchmark.exe')
}
$resolvedPlayer = [System.IO.Path]::GetFullPath($PlayerPath)
$buildCommit = (& git -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to resolve the benchmark commit.'
}

$common = [ordered]@{
    superRounds = 4
    warmupSamples = 20
    sampleCount = 240
    cooldownFrames = 5
    calibrationSamples = 12
}
switch ($MatrixPreset) {
    'smoke' {
        $common.superRounds = 1
        $common.warmupSamples = 2
        $common.sampleCount = 64
        $common.cooldownFrames = 0
        $scenarios = @(
            [ordered]@{
                id = 'deadline-smoke-w4096-p8192'
                workItems = 4096
                criticalIterations = 16
                normalIterations = 24
                backgroundIterations = 48
                pressureItems = 8192
                pressureIterations = 32
                criticalDeadlineUs = 2500
                normalDeadlineUs = 6000
                backgroundDeadlineUs = 18000
            })
    }
    'discovery' {
        $scenarios = @(
            [ordered]@{
                id = 'deadline-balanced-w131072-p262144'
                workItems = 131072
                criticalIterations = 256
                normalIterations = 512
                backgroundIterations = 1024
                pressureItems = 262144
                pressureIterations = 512
                criticalDeadlineUs = 4000
                normalDeadlineUs = 8000
                backgroundDeadlineUs = 25000
            },
            [ordered]@{
                id = 'deadline-saturated-w262144-p524288'
                workItems = 262144
                criticalIterations = 1024
                normalIterations = 2048
                backgroundIterations = 4096
                pressureItems = 524288
                pressureIterations = 2048
                criticalDeadlineUs = 8000
                normalDeadlineUs = 16000
                backgroundDeadlineUs = 50000
            })
    }
    'formal' {
        $common.warmupSamples = 30
        $common.sampleCount = 900
        $scenarios = @(
            [ordered]@{
                id = 'deadline-balanced-w131072-p262144'
                workItems = 131072
                criticalIterations = 256
                normalIterations = 512
                backgroundIterations = 1024
                pressureItems = 262144
                pressureIterations = 512
                criticalDeadlineUs = 4000
                normalDeadlineUs = 8000
                backgroundDeadlineUs = 25000
            },
            [ordered]@{
                id = 'deadline-saturated-w262144-p524288'
                workItems = 262144
                criticalIterations = 1024
                normalIterations = 2048
                backgroundIterations = 4096
                pressureItems = 524288
                pressureIterations = 2048
                criticalDeadlineUs = 8000
                normalDeadlineUs = 16000
                backgroundDeadlineUs = 50000
            })
    }
}

$runnerConfig = [ordered]@{
    suite = 'summit.gpu-deadline-scheduler'
    matrixPreset = $MatrixPreset
    gitCommit = $buildCommit
    deviceIndex = $DeviceIndex
    common = $common
    scenarios = $scenarios
}
$runnerConfig | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath (Join-Path $outputRoot 'runner-config.json')

if (-not $SkipTests) {
    $testResults = Join-Path $outputRoot 'editmode-results.xml'
    $testLog = Join-Path $outputRoot 'editmode.log'
    $testArguments = @(
        '-batchmode',
        '-force-d3d12',
        '-force-device-index', [string]$DeviceIndex,
        '-projectPath', (Quote-Argument $projectRoot),
        '-runTests',
        '-testPlatform', 'EditMode',
        '-testResults', (Quote-Argument $testResults),
        '-logFile', (Quote-Argument $testLog))
    $testProcess = Start-Process -FilePath $UnityPath `
        -ArgumentList $testArguments `
        -WorkingDirectory $projectRoot `
        -WindowStyle Hidden `
        -PassThru
    Wait-ProcessBounded $testProcess (30 * 60 * 1000) 'EditMode tests'
    [xml]$testXml = Get-Content -LiteralPath $testResults
    if ([string]$testXml.'test-run'.result -cne 'Passed' -or
        [int]$testXml.'test-run'.failed -ne 0) {
        throw 'EditMode acceptance tests did not pass.'
    }
}

if (-not $SkipBuild) {
    $buildLog = Join-Path $outputRoot 'unity-build.log'
    $buildArguments = @(
        '-batchmode',
        '-quit',
        '-force-d3d12',
        '-force-device-index', [string]$DeviceIndex,
        '-projectPath', (Quote-Argument $projectRoot),
        '-executeMethod', 'GpuDeadlineSchedulerBenchmarkBuild.PerformBuild',
        '-gpu-deadline-player-path', (Quote-Argument $resolvedPlayer),
        '-logFile', (Quote-Argument $buildLog))
    $buildProcess = Start-Process -FilePath $UnityPath `
        -ArgumentList $buildArguments `
        -WorkingDirectory $projectRoot `
        -WindowStyle Hidden `
        -PassThru
    Wait-ProcessBounded $buildProcess (30 * 60 * 1000) 'Unity Player build'
}
if (-not (Test-Path -LiteralPath $resolvedPlayer -PathType Leaf)) {
    throw "Benchmark Player is missing: $resolvedPlayer"
}

foreach ($scenario in $scenarios) {
    $scenarioRoot = Join-Path $outputRoot $scenario.id
    New-Item -ItemType Directory -Force -Path $scenarioRoot | Out-Null
    $playerLog = Join-Path $scenarioRoot 'player.log'
    $arguments = @(
        '-force-d3d12',
        '-force-device-index', [string]$DeviceIndex,
        '-screen-fullscreen', '0',
        '-screen-width', '640',
        '-screen-height', '360',
        '-gpu-deadline-scheduler-benchmark',
        '-gpu-deadline-report-dir', (Quote-Argument $scenarioRoot),
        '-gpu-deadline-scenario-id', $scenario.id,
        '-gpu-deadline-build-commit', $buildCommit,
        '-gpu-deadline-super-rounds', [string]$common.superRounds,
        '-gpu-deadline-warmup-samples', [string]$common.warmupSamples,
        '-gpu-deadline-sample-count', [string]$common.sampleCount,
        '-gpu-deadline-cooldown-frames', [string]$common.cooldownFrames,
        '-gpu-deadline-work-items', [string]$scenario.workItems,
        '-gpu-deadline-critical-iterations',
            [string]$scenario.criticalIterations,
        '-gpu-deadline-normal-iterations',
            [string]$scenario.normalIterations,
        '-gpu-deadline-background-iterations',
            [string]$scenario.backgroundIterations,
        '-gpu-deadline-pressure-items', [string]$scenario.pressureItems,
        '-gpu-deadline-pressure-iterations',
            [string]$scenario.pressureIterations,
        '-gpu-deadline-critical-deadline-us',
            [string]$scenario.criticalDeadlineUs,
        '-gpu-deadline-normal-deadline-us',
            [string]$scenario.normalDeadlineUs,
        '-gpu-deadline-background-deadline-us',
            [string]$scenario.backgroundDeadlineUs,
        '-gpu-deadline-calibration-samples',
            [string]$common.calibrationSamples,
        '-gpu-deadline-timeout-seconds', '60',
        '-logFile', (Quote-Argument $playerLog))
    Write-Output "Running $($scenario.id)"
    $player = Start-Process -FilePath $resolvedPlayer `
        -ArgumentList $arguments `
        -WorkingDirectory $projectRoot `
        -WindowStyle Hidden `
        -PassThru
    Wait-ProcessBounded `
        $player `
        ($PlayerTimeoutMinutes * 60 * 1000) `
        "Player scenario $($scenario.id)"
    $summaryPath = Join-Path $scenarioRoot 'run-summary.txt'
    if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
        throw "Scenario summary is missing: $summaryPath"
    }
    $summary = @{}
    foreach ($line in Get-Content -LiteralPath $summaryPath) {
        $parts = $line -split '=', 2
        if ($parts.Count -eq 2) { $summary[$parts[0]] = $parts[1] }
    }
    if ($summary.passed -ne '1') {
        throw "Scenario $($scenario.id) failed: $($summary.status)"
    }
}

& (Join-Path $PSScriptRoot 'Summarize-GpuDeadlineSchedulerBenchmark.ps1') `
    -ReportDirectory $outputRoot
if ($LASTEXITCODE -ne 0) {
    throw 'Deadline scheduler summary validation failed.'
}
Write-Output "Completed GPU deadline scheduler benchmark: $outputRoot"
