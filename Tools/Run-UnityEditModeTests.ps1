[CmdletBinding()]
param(
    [string]$UnityEditorPath =
        'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe',
    [string]$ProjectPath = '',
    [string]$ResultsPath = '',
    [string]$TestFilter = '',
    [switch]$UseGraphics,
    [switch]$ForceDirect3D12
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = [System.IO.Path]::GetFullPath(
        (Split-Path -Parent $PSScriptRoot))
}
if (-not (Test-Path -LiteralPath $UnityEditorPath)) {
    throw "Unity editor was not found: $UnityEditorPath"
}
if (-not (Test-Path -LiteralPath $ProjectPath)) {
    throw "Unity project was not found: $ProjectPath"
}
if ([string]::IsNullOrWhiteSpace($ResultsPath)) {
    $ResultsPath = Join-Path $ProjectPath 'TestResults\editmode-results.xml'
}

$resultsDirectory = Split-Path -Parent $ResultsPath
New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
$logPath = Join-Path $resultsDirectory 'editmode-unity.log'
$arguments = @(
    '-batchmode'
)
if (-not $UseGraphics) {
    $arguments += '-nographics'
}
if ($ForceDirect3D12) {
    if (-not $UseGraphics) {
        throw '-ForceDirect3D12 requires -UseGraphics.'
    }
    $arguments += '-force-d3d12'
}
$arguments += @(
    '-projectPath', $ProjectPath,
    '-runTests',
    '-testPlatform', 'EditMode',
    '-testResults', $ResultsPath,
    '-logFile', $logPath
)
if (-not [string]::IsNullOrWhiteSpace($TestFilter)) {
    $arguments += @('-testFilter', $TestFilter)
}

$unityProcess = Start-Process `
    -FilePath $UnityEditorPath `
    -ArgumentList $arguments `
    -PassThru `
    -Wait `
    -WindowStyle Hidden
if ($unityProcess.ExitCode -ne 0) {
    throw "Unity EditMode tests failed with exit code $($unityProcess.ExitCode). See $logPath"
}
if (-not (Test-Path -LiteralPath $ResultsPath)) {
    throw "Unity exited without writing test results. See $logPath"
}

[xml]$resultDocument = Get-Content -LiteralPath $ResultsPath -Raw
$testRun = $resultDocument.'test-run'
if ($null -eq $testRun) {
    throw "Unity test results do not contain a test-run root: $ResultsPath"
}
$failed = [int]$testRun.failed
$passed = [int]$testRun.passed
$total = [int]$testRun.total
$skipped = [int]$testRun.skipped
if ($failed -gt 0) {
    throw "Unity reported $failed failed EditMode test(s). See $ResultsPath"
}

Write-Host "Unity EditMode tests passed: $passed/$total; skipped: $skipped."
Write-Host "Results: $ResultsPath"
Write-Host "Log: $logPath"
