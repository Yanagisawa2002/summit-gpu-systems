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
    [switch]$CompareLruPolicies,
    [ValidateSet(384, 4096, 32768)][int]$PhysicalSlots = 384,
    [ValidateSet(4096, 16384, 65536)][int]$VirtualPages = 4096,
    [switch]$SkipBuild,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
if ($PhysicalSlots -gt $VirtualPages) { throw 'PhysicalSlots must not exceed VirtualPages.' }
Set-StrictMode -Version Latest

function Quote-Argument([string]$Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Wait-Bounded(
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

$root = [System.IO.Path]::GetFullPath(
    (Split-Path -Parent $PSScriptRoot))
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw "Unity editor is missing: $UnityPath"
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root (
        'Reports\GpuResidencyManager\' +
        [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
}
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
if ([string]::IsNullOrWhiteSpace($PlayerPath)) {
    $PlayerPath = Join-Path $root (
        'Builds\GpuResidencyBenchmark\GpuResidencyBenchmark.exe')
}
$player = [System.IO.Path]::GetFullPath($PlayerPath)
$commit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve git commit.' }

$common = [ordered]@{
    superRounds = 4
    warmupFrames = 30
    sampleFrames = 240
    cooldownFrames = 5
    virtualPages = $VirtualPages
    physicalSlots = $PhysicalSlots
}
switch ($MatrixPreset) {
    'smoke' {
        $common.superRounds = 1
        $common.warmupFrames = 2
        $common.sampleFrames = 64
        $common.cooldownFrames = 0
        $scenarios = @([ordered]@{
            id = 'residency-smoke-p256'
            pointsPerPage = 256
            seed = 20260804
        })
    }
    'discovery' {
        $scenarios = @(
            [ordered]@{
                id = 'residency-p1024'
                pointsPerPage = 1024
                seed = 20260804
            },
            [ordered]@{
                id = 'residency-p2048'
                pointsPerPage = 2048
                seed = 20260805
            })
    }
    'formal' {
        $common.warmupFrames = 60
        $common.sampleFrames = 900
        $scenarios = @(
            [ordered]@{
                id = 'residency-p1024'
                pointsPerPage = 1024
                seed = 20260804
            },
            [ordered]@{
                id = 'residency-p2048'
                pointsPerPage = 2048
                seed = 20260805
            })
    }
}

$formalAcceptance = [ordered]@{
    requiredScenarioCount = 2
    requiredPairsPerScenario = 8
    minimumGpuAverageImprovementPercent = 10.0
    minimumGpuP99ImprovementPercent = 0.0
    minimumCpuPreparationImprovementPercent = 10.0
    minimumUploadAverageReductionPercent = 85.0
    requireAllPairedGpuWins = $true
}

[ordered]@{
    suite = 'summit.gpu-residency-manager'
    matrixPreset = $MatrixPreset
    gitCommit = $commit
    deviceIndex = $DeviceIndex
    common = $common
    scenarios = $scenarios
    formalAcceptance = $formalAcceptance
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (
    Join-Path $output 'runner-config.json')

if (-not $SkipTests) {
    $testResults = Join-Path $output 'editmode-results.xml'
    $testLog = Join-Path $output 'editmode.log'
    $args = @(
        '-batchmode', '-force-d3d12',
        '-force-device-index', [string]$DeviceIndex,
        '-projectPath', (Quote-Argument $root),
        '-runTests', '-testPlatform', 'EditMode',
        '-testResults', (Quote-Argument $testResults),
        '-logFile', (Quote-Argument $testLog))
    $process = Start-Process -FilePath $UnityPath -ArgumentList $args `
        -WorkingDirectory $root -WindowStyle Hidden -PassThru
    Wait-Bounded $process (30 * 60 * 1000) 'EditMode tests'
    [xml]$xml = Get-Content -LiteralPath $testResults
    if ([int]$xml.'test-run'.passed -le 0 -or [int]$xml.'test-run'.failed -ne 0) {
        throw 'EditMode tests failed.'
    }
    # NUnit reports Skipped:Ignored for the complete suite when optional explicit
    # wave variants and the separately invoked index benchmark use Assert.Ignore.
    # Preserve the full suite and fail on every undeclared skip or other outcome.
    $testCases = @($xml.SelectNodes('//test-case'))
    if ($testCases.Count -ne [int]$xml.'test-run'.total) { throw 'Incomplete EditMode test evidence.' }
    foreach ($testCase in $testCases) {
        if ($testCase.result -ceq 'Passed') { continue }
        $explicitWave = $testCase.fullname -cmatch '^Summit\.GpuPrimitives\.Tests\.GpuPrimitiveCandidateTests\.CandidateMatchesIndependentOracles\("primitives-v1-wave(32|64)-t128-e4-r4"\)$'
        $indexOptIn = $testCase.fullname -ceq 'Summit.GpuSensorIndex.Benchmark.Tests.GpuSensorIndexComparison.Compare'
        $reason = [string]$testCase.reason.message.InnerText
        if ($testCase.result -cne 'Skipped' -or
            -not (($explicitWave -and $reason.StartsWith('Explicit wave candidate unavailable:')) -or
                  ($indexOptIn -and $reason.StartsWith('Use the dedicated comparison runner')))) {
            throw "Unexpected EditMode outcome: $($testCase.fullname): $($testCase.result)"
        }
    }
}

if (-not $SkipBuild) {
    $buildLog = Join-Path $output 'unity-build.log'
    $args = @(
        '-batchmode', '-quit', '-force-d3d12',
        '-force-device-index', [string]$DeviceIndex,
        '-projectPath', (Quote-Argument $root),
        '-executeMethod', 'GpuResidencyBenchmarkBuild.PerformBuild',
        '-gpu-residency-player-path', (Quote-Argument $player),
        '-logFile', (Quote-Argument $buildLog))
    $process = Start-Process -FilePath $UnityPath -ArgumentList $args `
        -WorkingDirectory $root -WindowStyle Hidden -PassThru
    Wait-Bounded $process (30 * 60 * 1000) 'Unity Player build'
}
if (-not (Test-Path -LiteralPath $player -PathType Leaf)) {
    throw "Benchmark Player is missing: $player"
}

foreach ($scenario in $scenarios) {
    $scenarioRoot = Join-Path $output $scenario.id
    New-Item -ItemType Directory -Force -Path $scenarioRoot | Out-Null
    $playerLog = Join-Path $scenarioRoot 'player.log'
    $args = @(
        '-force-d3d12', '-force-device-index', [string]$DeviceIndex,
        '-screen-fullscreen', '0', '-screen-width', '640',
        '-screen-height', '360',
        '-gpu-residency-benchmark',
        '-gpu-residency-report-dir', (Quote-Argument $scenarioRoot),
        '-gpu-residency-scenario-id', $scenario.id,
        '-gpu-residency-build-commit', $commit,
        '-gpu-residency-super-rounds', [string]$common.superRounds,
        '-gpu-residency-warmup-frames', [string]$common.warmupFrames,
        '-gpu-residency-sample-frames', [string]$common.sampleFrames,
        '-gpu-residency-cooldown-frames', [string]$common.cooldownFrames,
        '-gpu-residency-virtual-pages', [string]$common.virtualPages,
        '-gpu-residency-physical-slots', [string]$common.physicalSlots,
        '-gpu-residency-points-per-page', [string]$scenario.pointsPerPage,
        '-gpu-residency-seed', [string]$scenario.seed,
        '-gpu-residency-timeout-seconds', '60',
        '-logFile', (Quote-Argument $playerLog))
    if ($CompareLruPolicies) { $args += '-gpu-residency-compare-lru' }
    Write-Output "Running $($scenario.id)"
    $process = Start-Process -FilePath $player -ArgumentList $args `
        -WorkingDirectory $root -WindowStyle Hidden -PassThru
    Wait-Bounded $process ($PlayerTimeoutMinutes * 60 * 1000) `
        "Player scenario $($scenario.id)"
    $summary = @{}
    foreach ($line in Get-Content -LiteralPath (
            Join-Path $scenarioRoot 'run-summary.txt')) {
        $parts = $line -split '=', 2
        if ($parts.Count -eq 2) { $summary[$parts[0]] = $parts[1] }
    }
    if ($summary.passed -ne '1') {
        throw "Scenario $($scenario.id) failed: $($summary.status)"
    }
}

if (-not $CompareLruPolicies) {
& (Join-Path $PSScriptRoot 'Summarize-GpuResidencyBenchmark.ps1') `
    -ReportDirectory $output
if ($LASTEXITCODE -ne 0) { throw 'Residency summary failed.' }
} else { & (Join-Path $PSScriptRoot 'Summarize-ResidencyLruComparison.ps1') -ReportDirectory $output }
Write-Output "Completed GPU residency benchmark: $output"
