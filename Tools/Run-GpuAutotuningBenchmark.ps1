[CmdletBinding()]
param(
    [string]$UnityPath =
        'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe',
    [string]$OutputDirectory,
    [ValidateSet('smoke', 'discovery', 'formal')]
    [string]$MatrixPreset = 'discovery',
    [ValidateRange(0, 16)]
    [int]$DeviceIndex = 0,
    [switch]$SkipBuild,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root (
        'Reports\GpuAutotuning\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
}
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$player = Join-Path $root 'Builds\GpuAutotuning\GpuAutotuningBenchmark.exe'

$rounds = 4
$calibrationRounds = 1
$warmupFrames = 30
$sampleFrames = 240
$elementCount = 1048576
switch ($MatrixPreset) {
    'smoke' {
        $rounds = 3
        $calibrationRounds = 1
        $warmupFrames = 5
        $sampleFrames = 60
        $elementCount = 65536
    }
    'formal' {
        $rounds = 6
        $calibrationRounds = 2
        $warmupFrames = 60
        $sampleFrames = 900
    }
}

if ($MatrixPreset -eq 'formal') {
    $branch = (& git -C $root branch --show-current).Trim()
    if ([string]::IsNullOrWhiteSpace($branch)) {
        throw 'Formal autotuning requires a named Git branch.'
    }
    if (@(& git -C $root status --porcelain).Count -ne 0) {
        throw 'Formal autotuning requires a clean Git worktree.'
    }
    if ($SkipBuild -or $SkipTests) {
        throw 'Formal autotuning forbids SkipBuild and SkipTests.'
    }
}

function Run-EditModeTests {
    $testLog = Join-Path $output 'editmode.log'
    $testArgs = @(
        '-batchmode', '-force-d3d12',
        '-force-device-index', [string]$DeviceIndex,
        '-projectPath', ('"' + $root + '"'),
        '-runTests', '-testPlatform', 'EditMode',
        '-testResults', ('"' + $editModeResults + '"'),
        '-logFile', ('"' + $testLog + '"'))
    $test = Start-Process -FilePath $UnityPath -ArgumentList $testArgs `
        -WorkingDirectory $root -WindowStyle Hidden -PassThru
    if (-not $test.WaitForExit(30 * 60 * 1000)) {
        $test.Kill(); throw 'EditMode tests timed out.'
    }
    if ($test.ExitCode -ne 0) { throw 'EditMode tests failed.' }
    [xml]$testXml = Get-Content -LiteralPath $editModeResults
    if ([string]$testXml.'test-run'.result -cne 'Passed' -or
        [int]$testXml.'test-run'.failed -ne 0) {
        throw 'EditMode NUnit result was not fully passed.'
    }
}

$editModeResults = Join-Path $output 'editmode-results.xml'
if (-not $SkipTests -and $MatrixPreset -ne 'formal') {
    Run-EditModeTests
}

$primitiveArgs = @{
    UnityPath = $UnityPath
    OutputDirectory = $output
    PlayerPath = $player
    DeviceIndex = $DeviceIndex
    Rounds = $rounds
    WarmupFrames = $warmupFrames
    SampleFrames = $sampleFrames
    CooldownFrames = 5
    ElementCount = $elementCount
    Seed = 20260806
    DispatchesPerFrame = 1
    Operations = 'exclusive-scan,stable-compaction,radix-sort-32'
    Backends = 'portable,wave-ops'
    ValidationTimeoutSeconds = 60
    PlayerTimeoutMinutes = 120
    SkipSummary = $true
}
if ($SkipBuild) { $primitiveArgs.SkipBuild = $true }
& (Join-Path $PSScriptRoot 'Run-GpuPrimitiveBenchmark.ps1') @primitiveArgs
if ($LASTEXITCODE -ne 0) { throw 'GPU primitive candidate benchmark failed.' }
if (-not $SkipTests -and $MatrixPreset -eq 'formal') {
    Run-EditModeTests
}

$selectorArgs = @{
    ReportDirectory = $output
    CalibrationRounds = $calibrationRounds
}
if ($MatrixPreset -eq 'formal') { $selectorArgs.FormalAcceptance = $true }
& (Join-Path $PSScriptRoot 'Select-GpuAutotuningProfile.ps1') @selectorArgs
if ($LASTEXITCODE -ne 0) { throw 'GPU autotuning profile selection failed.' }

$measuredDevice = Get-Content -LiteralPath (
    Join-Path $output 'device.json') -Raw | ConvertFrom-Json

[ordered]@{
    suite = 'summit.gpu-cross-vendor-autotuning'
    matrixPreset = $MatrixPreset
    deviceIndex = $DeviceIndex
    rounds = $rounds
    calibrationRounds = $calibrationRounds
    evaluationRounds = $rounds - $calibrationRounds
    warmupFrames = $warmupFrames
    sampleFrames = $sampleFrames
    elementCount = $elementCount
    operations = $primitiveArgs.Operations
    backends = $primitiveArgs.Backends
    hardwareValidationCompleted = $true
    measuredDevice = [ordered]@{
        vendorId = [int]$measuredDevice.graphicsDeviceVendorId
        deviceId = [int]$measuredDevice.graphicsDeviceId
        vendor = [string]$measuredDevice.graphicsDeviceVendor
        name = [string]$measuredDevice.graphicsDeviceName
        graphicsApi = [string]$measuredDevice.graphicsDeviceType
    }
    amdValidated = [int]$measuredDevice.graphicsDeviceVendorId -eq 0x1002
    nvidiaValidated = [int]$measuredDevice.graphicsDeviceVendorId -eq 0x10DE
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (
    Join-Path $output 'autotune-runner-config.json')
Write-Output "Completed GPU autotuning benchmark: $output"
