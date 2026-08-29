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
Set-StrictMode -Version Latest

function Invoke-GitText {
    param(
        [Parameter(Mandatory = $true)][string]$WorkingTree,
        [Parameter(Mandatory = $true)][string[]]$GitArguments,
        [switch]$AllowFailure
    )
    $output = @(& git -C $WorkingTree @GitArguments 2>&1)
    $exitCode = $LASTEXITCODE
    if (-not $AllowFailure -and $exitCode -ne 0) {
        throw (
            "Git command failed ($exitCode): git -C '$WorkingTree' " +
            ($GitArguments -join ' ') + "`n" + ($output -join "`n"))
    }
    return [ordered]@{
        exitCode = $exitCode
        lines = [string[]]$output
        text = ($output -join "`n").Trim()
    }
}

function Get-GitSnapshot {
    param([Parameter(Mandatory = $true)][string]$WorkingTree)
    $rootResult = Invoke-GitText `
        -WorkingTree $WorkingTree `
        -GitArguments @('rev-parse', '--show-toplevel')
    $gitRoot = [IO.Path]::GetFullPath($rootResult.text)
    $head = (Invoke-GitText `
        -WorkingTree $gitRoot `
        -GitArguments @('rev-parse', 'HEAD')).text
    $branchResult = Invoke-GitText `
        -WorkingTree $gitRoot `
        -GitArguments @('symbolic-ref', '--quiet', '--short', 'HEAD') `
        -AllowFailure
    $statusResult = Invoke-GitText `
        -WorkingTree $gitRoot `
        -GitArguments @('status', '--porcelain=v1', '--untracked-files=all')
    $statusLines = [string[]]@(
        $statusResult.lines | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_)
        })
    return [ordered]@{
        root = $gitRoot
        head = $head
        branch = if ($branchResult.exitCode -eq 0) {
            $branchResult.text
        }
        else {
            'HEAD'
        }
        dirty = $statusLines.Count -ne 0
        statusLines = $statusLines
    }
}

function Get-ProjectUnityVersion {
    param([Parameter(Mandatory = $true)][string]$ResolvedProjectPath)
    $projectVersionPath = Join-Path $ResolvedProjectPath (
        'ProjectSettings\ProjectVersion.txt')
    if (-not (Test-Path -LiteralPath $projectVersionPath -PathType Leaf)) {
        throw "Unity ProjectVersion.txt is missing: $projectVersionPath"
    }
    $versionLine = Get-Content -LiteralPath $projectVersionPath |
        Where-Object { $_ -like 'm_EditorVersion:*' } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace([string]$versionLine)) {
        throw "Project Unity version is missing: $projectVersionPath"
    }
    return [ordered]@{
        path = [IO.Path]::GetFullPath($projectVersionPath)
        version = (($versionLine -split ':', 2)[1]).Trim()
        sha256 = (Get-FileHash `
            -LiteralPath $projectVersionPath `
            -Algorithm SHA256).Hash
    }
}

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
$ProjectPath = [System.IO.Path]::GetFullPath($ProjectPath)
$ResultsPath = [System.IO.Path]::GetFullPath($ResultsPath)
$UnityEditorPath = [System.IO.Path]::GetFullPath($UnityEditorPath)

$gitStart = Get-GitSnapshot -WorkingTree $ProjectPath
if ([bool]$gitStart.dirty) {
    throw "Unity EditMode receipt requires a clean worktree:`n" +
        (@($gitStart.statusLines) -join "`n")
}
$projectVersion = Get-ProjectUnityVersion -ResolvedProjectPath $ProjectPath
$unityVersionInfo = (Get-Item -LiteralPath $UnityEditorPath).VersionInfo
$unityProductVersion =
    (([string]$unityVersionInfo.ProductVersion -split '_', 2)[0]).Trim()
if ([string]::IsNullOrWhiteSpace($unityProductVersion)) {
    throw "Unity executable has no ProductVersion: $UnityEditorPath"
}
if ($unityProductVersion -cne [string]$projectVersion.version) {
    throw (
        "Unity executable version '$unityProductVersion' does not match " +
        "ProjectVersion '$($projectVersion.version)'.")
}

$resultsDirectory = Split-Path -Parent $ResultsPath
New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
$logPath = Join-Path $resultsDirectory 'editmode-unity.log'
$receiptPath = $ResultsPath + '.receipt.json'
foreach ($stalePath in @($ResultsPath, $receiptPath)) {
    if (Test-Path -LiteralPath $stalePath -PathType Leaf) {
        Remove-Item -LiteralPath $stalePath -Force
    }
}
$startedUtc = (Get-Date).ToUniversalTime().ToString('o')
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
$inconclusive = [int]$testRun.inconclusive
if ([string]$testRun.result -cne 'Passed' -or
    $total -le 0 -or
    $failed -ne 0 -or
    $inconclusive -ne 0 -or
    ($passed + $skipped) -ne $total) {
    throw (
        "Unity EditMode result is incomplete: result=$($testRun.result), " +
        "passed=$passed, failed=$failed, skipped=$skipped, " +
        "inconclusive=$inconclusive, total=$total. See $ResultsPath")
}

$gitFinal = Get-GitSnapshot -WorkingTree $ProjectPath
if ([string]$gitFinal.head -cne [string]$gitStart.head -or
    [string]$gitFinal.root -cne [string]$gitStart.root -or
    [bool]$gitFinal.dirty) {
    throw "Git state changed during Unity EditMode tests:`n" +
        (@($gitFinal.statusLines) -join "`n")
}
$resultsSha256 =
    (Get-FileHash -LiteralPath $ResultsPath -Algorithm SHA256).Hash
$receipt = [ordered]@{
    schemaVersion = 1
    receiptType = 'summit.unity-editmode-test'
    projectPath = $ProjectPath
    projectVersionPath = [string]$projectVersion.path
    projectVersionSha256 = [string]$projectVersion.sha256
    projectUnityVersion = [string]$projectVersion.version
    unityExecutablePath = $UnityEditorPath
    unityExecutableSha256 =
        (Get-FileHash -LiteralPath $UnityEditorPath -Algorithm SHA256).Hash
    unityProductVersion = $unityProductVersion
    resultsPath = $ResultsPath
    resultsSha256 = $resultsSha256
    logPath = [IO.Path]::GetFullPath($logPath)
    testPlatform = 'EditMode'
    testFilter = $TestFilter
    useGraphics = [bool]$UseGraphics
    forceDirect3D12 = [bool]$ForceDirect3D12
    exitCode = [int]$unityProcess.ExitCode
    result = [string]$testRun.result
    total = $total
    passed = $passed
    failed = $failed
    skipped = $skipped
    inconclusive = $inconclusive
    gitCommit = [string]$gitStart.head
    gitRoot = [string]$gitStart.root
    gitStart = $gitStart
    gitFinal = $gitFinal
    gitStateStable = $true
    startedUtc = $startedUtc
    completedUtc = (Get-Date).ToUniversalTime().ToString('o')
}
$receipt | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $receiptPath -Encoding utf8

Write-Host "Unity EditMode tests passed: $passed/$total; skipped: $skipped."
Write-Host "Results: $ResultsPath"
Write-Host "Receipt: $receiptPath"
Write-Host "Log: $logPath"
