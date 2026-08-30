[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolsRoot = Split-Path -Parent $PSScriptRoot
$modulePath = Join-Path $toolsRoot 'GpuBenchmarkCheckpoint.psm1'
if (-not (Test-Path -LiteralPath $modulePath -PathType Leaf)) {
    throw "Checkpoint module is missing: $modulePath"
}
$tokens = $null
$errors = $null
[void][Management.Automation.Language.Parser]::ParseFile(
    $modulePath,
    [ref]$tokens,
    [ref]$errors)
if ($errors.Count -ne 0) {
    throw 'Checkpoint module has PowerShell parse errors: ' +
        (@($errors | ForEach-Object Message) -join '; ')
}
Import-Module -Name $modulePath -Force

function Assert-Throws {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $failed = $false
    try {
        & $Action
    }
    catch {
        $failed = $true
    }
    if (-not $failed) {
        throw "$Label did not fail closed."
    }
}

$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).
    TrimEnd('\', '/')
$temporaryRoot = Join-Path $temporaryBase (
    'summit-gpu-checkpoint-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $left = [ordered]@{
        schemaVersion = 1
        suite = 'checkpoint-test'
        nested = [ordered]@{ z = 2; a = 1 }
        values = @('x', 3, $true)
    }
    $right = [ordered]@{
        values = @('x', 3, $true)
        nested = [ordered]@{ a = 1; z = 2 }
        suite = 'checkpoint-test'
        schemaVersion = 1
    }
    if ((Get-GpuBenchmarkObjectSha256 $left) -cne
        (Get-GpuBenchmarkObjectSha256 $right)) {
        throw 'Canonical object hashing depends on property insertion order.'
    }

    $sealedPath = Join-Path $temporaryRoot 'sealed.json'
    $sealed = Write-GpuBenchmarkSealedJson `
        -Value $left `
        -Path $sealedPath `
        -CreateNew
    $read = Read-GpuBenchmarkSealedJson `
        -Path $sealedPath `
        -ExpectedSuite 'checkpoint-test' `
        -ExpectedSchemaVersion 1
    if ([string]$read.recordSha256 -cne [string]$sealed.recordSha256) {
        throw 'Sealed record did not round-trip its identity.'
    }
    Assert-Throws {
        $null = Write-GpuBenchmarkSealedJson $left $sealedPath -CreateNew
    } 'Create-new sealed record overwrite'
    $tampered = Get-Content -LiteralPath $sealedPath -Raw | ConvertFrom-Json
    $tampered.nested.a = 99
    [IO.File]::WriteAllText(
        $sealedPath,
        ($tampered | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))
    Assert-Throws {
        $null = Read-GpuBenchmarkSealedJson $sealedPath
    } 'Tampered sealed record'

    $evidenceRoot = Join-Path $temporaryRoot 'evidence'
    New-Item -ItemType Directory -Path $evidenceRoot | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $evidenceRoot 'a.txt'),
        'alpha',
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $evidenceRoot 'b.txt'),
        'beta',
        [Text.UTF8Encoding]::new($false))
    $fileSet = Get-GpuBenchmarkFileSetReceipt `
        -Root $evidenceRoot `
        -RelativePaths @('b.txt', 'a.txt')
    $null = Assert-GpuBenchmarkFileSetReceipt $evidenceRoot $fileSet
    [IO.File]::AppendAllText(
        (Join-Path $evidenceRoot 'a.txt'),
        '-tampered',
        [Text.UTF8Encoding]::new($false))
    Assert-Throws {
        $null = Assert-GpuBenchmarkFileSetReceipt $evidenceRoot $fileSet
    } 'Tampered file set'

    $contractFingerprint = 'A1' * 32
    $phaseRoot = Join-Path $temporaryRoot 'phases'
    New-Item -ItemType Directory -Path $phaseRoot | Out-Null
    foreach ($phaseId in @('calibration/cell-a', 'holdout/cell-a')) {
        $name = $phaseId.Replace('/', '--') + '.json'
        $null = Write-GpuBenchmarkSealedJson `
            -Value ([ordered]@{
                schemaVersion = 1
                suite = 'summit.gpu-benchmark-phase'
                runContractFingerprint = $contractFingerprint
                phaseId = $phaseId
            }) `
            -Path (Join-Path $phaseRoot $name) `
            -CreateNew
    }
    $coverage = Assert-GpuBenchmarkPhaseCoverage `
        -ReceiptsDirectory $phaseRoot `
        -ExpectedPhaseIds @('calibration/cell-a', 'holdout/cell-a') `
        -RunContractFingerprint $contractFingerprint
    if ($coverage.Count -ne 2) {
        throw 'Exact phase coverage did not return two receipts.'
    }
    Assert-Throws {
        $null = Assert-GpuBenchmarkPhaseCoverage `
            $phaseRoot @('calibration/cell-a') $contractFingerprint
    } 'Unexpected phase receipt'
    $null = Write-GpuBenchmarkSealedJson `
        -Value ([ordered]@{
            schemaVersion = 1
            suite = 'summit.gpu-benchmark-phase'
            runContractFingerprint = $contractFingerprint
            phaseId = 'calibration/cell-a'
        }) `
        -Path (Join-Path $phaseRoot 'duplicate.json') `
        -CreateNew
    Assert-Throws {
        $null = Assert-GpuBenchmarkPhaseCoverage `
            $phaseRoot @('calibration/cell-a', 'holdout/cell-a') `
            $contractFingerprint
    } 'Duplicate phase receipt'
    [IO.File]::WriteAllText(
        (Join-Path $phaseRoot '.torn.tmp'),
        'partial',
        [Text.UTF8Encoding]::new($false))
    Assert-Throws {
        $null = Assert-GpuBenchmarkPhaseCoverage `
            $phaseRoot @('calibration/cell-a', 'holdout/cell-a') `
            $contractFingerprint
    } 'Unsealed phase receipt temporary file'

    $lockPath = Join-Path $temporaryRoot 'checkpoint\run.lock.json'
    $lock = Enter-GpuBenchmarkRunLock $lockPath $contractFingerprint
    try {
        Assert-Throws {
            $null = Enter-GpuBenchmarkRunLock $lockPath $contractFingerprint
        } 'Concurrent run lock'
        Assert-Throws {
            $null = Enter-GpuBenchmarkRunLock `
                $lockPath $contractFingerprint -RecoverInterrupted
        } 'Recovery of active run lock'
    }
    finally {
        Exit-GpuBenchmarkRunLock $lock
    }
    if (Test-Path -LiteralPath $lockPath) {
        throw 'Normal lock release left a lock file behind.'
    }

    $stale = [ordered]@{
        schemaVersion = 1
        suite = 'summit.gpu-benchmark-run-lock'
        runContractFingerprint = $contractFingerprint
        processId = [int]::MaxValue
        processStartUtc = '2000-01-01T00:00:00.0000000Z'
        machineName = [Environment]::MachineName
        acquiredUtc = '2000-01-01T00:00:00.0000000Z'
    }
    $null = Write-GpuBenchmarkSealedJson $stale $lockPath -CreateNew
    $recovered = Enter-GpuBenchmarkRunLock `
        $lockPath $contractFingerprint -RecoverInterrupted
    try {
        if ([string]::IsNullOrWhiteSpace($recovered.RecoveredLockPath) -or
            -not (Test-Path -LiteralPath $recovered.RecoveredLockPath)) {
            throw 'Interrupted lock was not archived before recovery.'
        }
    }
    finally {
        Exit-GpuBenchmarkRunLock $recovered
    }
}
finally {
    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    if (-not $resolvedTemporaryRoot.StartsWith(
            $temporaryBase + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedTemporaryRoot) -notlike
            'summit-gpu-checkpoint-test-*') {
        throw "Refusing to remove unexpected test path: $resolvedTemporaryRoot"
    }
    Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
}

Write-Host 'GPU benchmark checkpoint contract tests passed.'
