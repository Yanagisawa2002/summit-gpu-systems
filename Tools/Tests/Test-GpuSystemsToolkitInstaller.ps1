[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolsRoot = Split-Path -Parent $PSScriptRoot
$installer = Join-Path $toolsRoot 'Install-GpuSystemsToolkit.ps1'
$script:assertionCount = 0
$commit = '0123456789abcdef0123456789abcdef01234567'
$repository = 'https://github.com/example/gpu-systems-toolkit.git'

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

function New-TestProject {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [string]$UnityVersion = '6000.5.2f1'
    )
    New-Item -ItemType Directory -Force -Path (
        Join-Path $Root 'Assets') | Out-Null
    New-Item -ItemType Directory -Force -Path (
        Join-Path $Root 'Packages') | Out-Null
    New-Item -ItemType Directory -Force -Path (
        Join-Path $Root 'ProjectSettings') | Out-Null
    [ordered]@{
        dependencies = [ordered]@{
            'com.unity.test-framework' = '1.7.0'
        }
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (
        Join-Path $Root 'Packages\manifest.json') -Encoding utf8NoBOM
    @(
        "m_EditorVersion: $UnityVersion",
        'm_EditorVersionWithRevision: 6000.5.2f1 (fixture)'
    ) | Set-Content -LiteralPath (
        Join-Path $Root 'ProjectSettings\ProjectVersion.txt') `
        -Encoding utf8NoBOM
}

function Read-ManifestDependencies {
    param([Parameter(Mandatory = $true)][string]$Root)
    return (Get-Content -Raw -LiteralPath (
        Join-Path $Root 'Packages\manifest.json') |
        ConvertFrom-Json -AsHashtable)['dependencies']
}

$testRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'gpu-systems-toolkit-installer-' + [guid]::NewGuid().ToString('N'))
$projectRoot = Join-Path $testRoot 'supported'
$oldProjectRoot = Join-Path $testRoot 'unsupported'

try {
    New-TestProject -Root $projectRoot
    $installReceipt = Join-Path $projectRoot 'install-receipt.json'
    $null = & $installer `
        -ProjectRoot $projectRoot `
        -Mode Install `
        -Commit $commit `
        -RepositoryUrl $repository `
        -ReceiptPath $installReceipt
    $receipt = Get-Content -Raw -LiteralPath $installReceipt |
        ConvertFrom-Json
    $dependencies = Read-ManifestDependencies $projectRoot
    $stablePackages = @(
        'com.summit.gpu-primitives',
        'com.summit.gpu-direct-binning',
        'com.summit.gpu-adaptive-binning',
        'com.summit.gpu-driven-instances',
        'com.summit.gpu-autotuning',
        'com.yanagisawa.gpu-systems-toolkit'
    )
    foreach ($name in $stablePackages) {
        $expectedUrl = $repository + '?path=/Packages/' + $name + '#' + $commit
        Assert-True ($dependencies[$name] -ceq $expectedUrl) (
            "Stable package is commit-pinned: $name")
    }
    Assert-True ([int]$receipt.changedPackageCount -eq 6) (
        'Initial stable install records six changed packages.')
    Assert-True ([bool]$receipt.sealedTransportEligible) (
        'Commit-pinned Git install is transport-eligible.')
    Assert-True (-not $dependencies.ContainsKey(
            'com.summit.gpu-timestamps')) (
        'Diagnostics are excluded from the stable default.')
    Assert-True (-not $dependencies.ContainsKey(
            'com.summit.gpu-sensor-pipeline')) (
        'Labs are excluded from the stable default.')

    $manifestPath = Join-Path $projectRoot 'Packages\manifest.json'
    $hashBeforeRepeat = (Get-FileHash -LiteralPath $manifestPath).Hash
    $repeatReceipt = Join-Path $projectRoot 'repeat-receipt.json'
    $null = & $installer `
        -ProjectRoot $projectRoot `
        -Mode Install `
        -Commit $commit `
        -RepositoryUrl $repository `
        -ReceiptPath $repeatReceipt
    $repeat = Get-Content -Raw -LiteralPath $repeatReceipt | ConvertFrom-Json
    $hashAfterRepeat = (Get-FileHash -LiteralPath $manifestPath).Hash
    Assert-True ([int]$repeat.changedPackageCount -eq 0) (
        'Repeating the same install is semantically idempotent.')
    Assert-True ($hashBeforeRepeat -ceq $hashAfterRepeat) (
        'Repeating the same install is byte-for-byte idempotent.')
    Assert-True (
        [string]$repeat.manifestBeforeSha256 -ceq
            [string]$repeat.manifestAfterSha256) (
        'Idempotent receipt records matching manifest hashes.')

    $optionalReceipt = Join-Path $projectRoot 'optional-receipt.json'
    $null = & $installer `
        -ProjectRoot $projectRoot `
        -Mode Install `
        -Commit $commit `
        -RepositoryUrl $repository `
        -IncludeDiagnostics `
        -IncludeLabs `
        -ReceiptPath $optionalReceipt
    $optional = Get-Content -Raw -LiteralPath $optionalReceipt |
        ConvertFrom-Json
    $dependencies = Read-ManifestDependencies $projectRoot
    Assert-True ([int]$optional.changedPackageCount -eq 4) (
        'Diagnostics and three labs are installed only when requested.')
    Assert-True ($dependencies.ContainsKey('com.summit.gpu-timestamps')) (
        'Diagnostics opt-in is installed.')
    Assert-True ($dependencies.ContainsKey(
            'com.summit.gpu-deadline-scheduler')) (
        'Lab opt-in is installed.')

    $uninstallReceipt = Join-Path $projectRoot 'uninstall-receipt.json'
    $null = & $installer `
        -ProjectRoot $projectRoot `
        -Mode Uninstall `
        -ReceiptPath $uninstallReceipt
    $uninstall = Get-Content -Raw -LiteralPath $uninstallReceipt |
        ConvertFrom-Json
    $dependencies = Read-ManifestDependencies $projectRoot
    Assert-True ([int]$uninstall.changedPackageCount -eq 10) (
        'Uninstall removes every toolkit-owned stable and optional package.')
    Assert-True ($dependencies.Count -eq 1 -and
        $dependencies['com.unity.test-framework'] -ceq '1.7.0') (
        'Uninstall preserves unrelated project dependencies.')
    Assert-True ([string]$uninstall.packageSource -ceq 'none' -and
        [string]$uninstall.commit -ceq 'not-applicable' -and
        [string]$uninstall.repository -ceq 'none') (
        'Uninstall receipt does not imply a package source or commit.')
    Assert-True (-not [bool]$uninstall.sealedTransportEligible) (
        'Uninstall is never marked as sealed transport evidence.')

    New-TestProject -Root $oldProjectRoot -UnityVersion '6000.4.0f1'
    $rejected = $false
    try {
        $null = & $installer `
            -ProjectRoot $oldProjectRoot `
            -Mode Install `
            -Commit $commit `
            -RepositoryUrl $repository
    }
    catch {
        $rejected = $_.Exception.Message -match 'requires Unity 6000.5'
    }
    Assert-True $rejected 'Unity versions older than 6000.5 are rejected.'

    "PASS assertions=$script:assertionCount"
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
