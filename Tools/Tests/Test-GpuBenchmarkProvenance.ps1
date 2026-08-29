[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolsRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Split-Path -Parent $toolsRoot
Import-Module (Join-Path $toolsRoot 'GpuBenchmarkProvenance.psm1') -Force

$script:assertionCount = 0

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    $script:assertionCount++
    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Message
    )

    $script:assertionCount++
    try {
        & $Action
    }
    catch {
        return
    }
    throw "Assertion failed: expected failure for $Message"
}

function Invoke-TestGit {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $output = @(& git -C $Root @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "Test Git command failed: git $($Arguments -join ' ')"
    }
    return [string[]]$output
}

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    $encoding = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $Value, $encoding)
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'summit-gpu-provenance-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null

try {
    $fixtureRelative = 'fixture.txt'
    $fixturePath = Join-Path $testRoot $fixtureRelative
    Write-Utf8NoBom -Path $fixturePath -Value 'canonical fixture'

    [void](Invoke-TestGit -Root $testRoot -Arguments @('init', '--quiet'))
    [void](Invoke-TestGit -Root $testRoot -Arguments @(
        'config', 'user.email', 'gpu-provenance-test@example.invalid'))
    [void](Invoke-TestGit -Root $testRoot -Arguments @(
        'config', 'user.name', 'GPU Provenance Test'))
    [void](Invoke-TestGit -Root $testRoot -Arguments @(
        'config', 'core.autocrlf', 'false'))
    [void](Invoke-TestGit -Root $testRoot -Arguments @('add', '--all'))
    [void](Invoke-TestGit -Root $testRoot -Arguments @(
        'commit', '--quiet', '-m', 'canonical fixture'))

    $clean = Get-GpuBenchmarkGitSnapshot -ProjectRoot $testRoot
    Assert-True (-not [bool]$clean.dirty) 'new fixture is clean'
    Assert-True ($clean.statusLines.Count -eq 0) 'clean status has no rows'
    $cleanResult = Restore-KnownUnityBenchmarkDrift `
        -ProjectRoot $testRoot `
        -Snapshot $clean `
        -ExpectedHead $clean.head
    Assert-True (-not [bool]$cleanResult.restored) 'clean state needs no restore'
    Assert-True ($cleanResult.restoredPaths.Count -eq 0) 'no paths were restored'
    Assert-True (-not [bool]$cleanResult.afterSnapshot.dirty) 'clean tree stayed clean'

    Write-Utf8NoBom `
        -Path (Join-Path $testRoot 'unexpected.txt') `
        -Value 'untracked'
    $untracked = Get-GpuBenchmarkGitSnapshot -ProjectRoot $testRoot
    Assert-Throws {
        Restore-KnownUnityBenchmarkDrift `
            -ProjectRoot $testRoot `
            -Snapshot $untracked `
            -ExpectedHead $clean.head
    } 'untracked drift'
    Remove-Item -LiteralPath (Join-Path $testRoot 'unexpected.txt')

    Add-Content -LiteralPath $fixturePath -Value '# staged'
    [void](Invoke-TestGit -Root $testRoot -Arguments @(
        'add', '--', $fixtureRelative))
    $staged = Get-GpuBenchmarkGitSnapshot -ProjectRoot $testRoot
    Assert-Throws {
        Restore-KnownUnityBenchmarkDrift `
            -ProjectRoot $testRoot `
            -Snapshot $staged `
            -ExpectedHead $clean.head
    } 'staged drift'
    [void](Invoke-TestGit -Root $testRoot -Arguments @(
        'restore', '--staged', '--worktree', '--', $fixtureRelative))

    $beforeBranchChange = Get-GpuBenchmarkGitSnapshot -ProjectRoot $testRoot
    [void](Invoke-TestGit -Root $testRoot -Arguments @(
        'switch', '--quiet', '-c', 'branch-race'))
    Assert-Throws {
        Restore-KnownUnityBenchmarkDrift `
            -ProjectRoot $testRoot `
            -Snapshot $beforeBranchChange `
            -ExpectedHead $beforeBranchChange.head
    } 'branch changed after snapshot'

    $payloadRoot = Join-Path $testRoot 'Payload'
    $dataRoot = Join-Path $payloadRoot 'Benchmark_Data'
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    $playerPath = Join-Path $payloadRoot 'Benchmark.exe'
    Write-Utf8NoBom -Path $playerPath -Value 'exe'
    Write-Utf8NoBom `
        -Path (Join-Path $payloadRoot 'UnityPlayer.dll') `
        -Value 'dll'
    Write-Utf8NoBom -Path (Join-Path $dataRoot 'data.bin') -Value 'payload'
    Write-Utf8NoBom -Path (Join-Path $dataRoot 'zero.bin') -Value ''
    Write-Utf8NoBom `
        -Path (Join-Path $payloadRoot 'build-summary.txt') `
        -Value 'excluded-one'
    $payload1 = Get-GpuBenchmarkPlayerPayload -PlayerPath $playerPath
    Assert-True ($payload1.fileCount -eq 4) 'zero-byte payload file is included'
    Assert-True ($payload1.dataFileCount -eq 2) 'all data files counted'
    Assert-True ($payload1.dataLengthBytes -gt 0) 'data directory is non-empty'

    Write-Utf8NoBom `
        -Path (Join-Path $payloadRoot 'build-summary.txt') `
        -Value 'excluded-two'
    $payload2 = Get-GpuBenchmarkPlayerPayload -PlayerPath $playerPath
    Assert-True (
        $payload2.sha256 -ceq $payload1.sha256) 'build-summary is excluded'

    Write-Utf8NoBom -Path (Join-Path $dataRoot 'zero.bin') -Value 'x'
    $payload3 = Get-GpuBenchmarkPlayerPayload -PlayerPath $playerPath
    Assert-True (
        $payload3.sha256 -cne $payload1.sha256) 'payload mutation changes hash'

    Remove-Item -LiteralPath (Join-Path $payloadRoot 'UnityPlayer.dll')
    Assert-Throws {
        Get-GpuBenchmarkPlayerPayload -PlayerPath $playerPath
    } 'missing UnityPlayer.dll'

    "PASS assertions=$script:assertionCount"
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
