[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$FixtureRoot,
    [ValidateSet('Local', 'Git')]
    [string]$PackageSource = 'Local',
    [string]$PackageRepositoryRoot,
    [string]$PackageCommit,
    [string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

function Get-GitText {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )
    $output = @(& git -C $Root @Arguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }
    return ($output -join "`n").Trim()
}

$repositoryRoot = (Resolve-Path -LiteralPath (
    Join-Path $PSScriptRoot '..\..\..')).Path
$resolvedFixture = (Resolve-Path -LiteralPath $FixtureRoot).Path
$lockValidator = Join-Path $PSScriptRoot 'Test-UpstreamBenchmarkLock.ps1'
$overlaySource = Join-Path (
    Split-Path -Parent $PSScriptRoot) 'Overlay\Assets\ExternalBrgBenchmark'
$overlayTarget = Join-Path $resolvedFixture 'Assets\ExternalBrgBenchmark'
$manifestPath = Join-Path $resolvedFixture 'Packages\manifest.json'
$projectVersionPath = Join-Path `
    $resolvedFixture 'ProjectSettings\ProjectVersion.txt'

if (-not (Test-Path -LiteralPath $overlaySource -PathType Container)) {
    throw "Missing adapter overlay: $overlaySource"
}
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Missing fixture package manifest: $manifestPath"
}
if (-not (Select-String -LiteralPath $projectVersionPath `
        -Pattern '^m_EditorVersion: 6000\.5\.2f1$' -Quiet)) {
    throw 'The fixture must be migrated to exactly Unity 6000.5.2f1 first.'
}

$preValidation = & $lockValidator -FixtureRoot $resolvedFixture |
    ConvertFrom-Json
if (-not [bool]$preValidation.accepted) {
    throw 'Pinned upstream validation failed before overlay preparation.'
}

if ([string]::IsNullOrWhiteSpace($PackageRepositoryRoot)) {
    $PackageRepositoryRoot = $repositoryRoot
}
$resolvedPackageRepository = (
    Resolve-Path -LiteralPath $PackageRepositoryRoot).Path
$actualPackageCommit = Get-GitText $resolvedPackageRepository `
    @('rev-parse', 'HEAD')
if ([string]::IsNullOrWhiteSpace($PackageCommit)) {
    $PackageCommit = $actualPackageCommit
}
if ($PackageCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'PackageCommit must be a full 40-character Git commit.'
}
if ($PackageSource -eq 'Local' -and
    $actualPackageCommit -cne $PackageCommit.ToLowerInvariant()) {
    throw "Local package checkout is $actualPackageCommit, not $PackageCommit."
}

New-Item -ItemType Directory -Force -Path $overlayTarget | Out-Null
Copy-Item -Path (Join-Path $overlaySource '*') `
    -Destination $overlayTarget -Recurse -Force

$manifest = Get-Content -Raw -LiteralPath $manifestPath |
    ConvertFrom-Json -AsHashtable
$dependencies = $manifest['dependencies']
if ($null -eq $dependencies) {
    throw 'Fixture manifest has no dependencies object.'
}

$packageNames = @(
    'com.summit.gpu-autotuning',
    'com.summit.gpu-primitives',
    'com.summit.gpu-direct-binning',
    'com.summit.gpu-driven-instances',
    'com.summit.gpu-timestamps'
)
foreach ($packageName in $packageNames) {
    if ($PackageSource -eq 'Local') {
        $packagePath = Join-Path `
            $resolvedPackageRepository "Packages\$packageName"
        if (-not (Test-Path -LiteralPath $packagePath -PathType Container)) {
            throw "Missing local package: $packagePath"
        }
        $dependencies[$packageName] = 'file:' + (
            [IO.Path]::GetFullPath($packagePath).Replace('\', '/'))
    }
    else {
        $dependencies[$packageName] =
            'https://github.com/Yanagisawa2002/' +
            'summit-gpu-systems.git?path=/Packages/' +
            "$packageName#$PackageCommit"
    }
}

$orderedDependencies = [ordered]@{}
foreach ($name in @($dependencies.Keys | Sort-Object)) {
    $orderedDependencies[$name] = $dependencies[$name]
}
$manifest['dependencies'] = $orderedDependencies
$manifestJson = $manifest | ConvertTo-Json -Depth 12
Set-Content -LiteralPath $manifestPath -Value $manifestJson `
    -Encoding utf8NoBOM

$postValidation = & $lockValidator -FixtureRoot $resolvedFixture |
    ConvertFrom-Json
if (-not [bool]$postValidation.accepted) {
    throw 'Pinned upstream validation failed after overlay preparation.'
}

$overlayFiles = Get-ChildItem -LiteralPath $overlaySource -Recurse -File |
    Sort-Object FullName
$overlayReceipts = foreach ($file in $overlayFiles) {
    $relative = [IO.Path]::GetRelativePath(
        $overlaySource,
        $file.FullName).Replace('\', '/')
    [pscustomobject][ordered]@{
        path = $relative
        bytes = $file.Length
        sha256 = Get-FileSha256 $file.FullName
    }
}

if ([string]::IsNullOrWhiteSpace($ReceiptPath)) {
    $ReceiptPath = Join-Path `
        $resolvedFixture 'ExternalBrgBenchmarkIntegrationReceipt.json'
}
$resolvedReceipt = [IO.Path]::GetFullPath($ReceiptPath)
$receiptDirectory = Split-Path -Parent $resolvedReceipt
New-Item -ItemType Directory -Force -Path $receiptDirectory | Out-Null

$receipt = [pscustomobject][ordered]@{
    schemaVersion = 1
    suite = 'gpu-systems.external-brg-shooter.integration'
    accepted = $true
    sealedEvidenceEligible = $PackageSource -eq 'Git'
    upstreamCommit = [string]$postValidation.upstreamCommit
    fixtureHead = [string]$postValidation.fixtureHead
    targetEditor = '6000.5.2f1'
    packageSource = $PackageSource.ToLowerInvariant()
    packageCommit = $PackageCommit.ToLowerInvariant()
    packageRepository = if ($PackageSource -eq 'Local') {
        $resolvedPackageRepository
    } else {
        'https://github.com/Yanagisawa2002/summit-gpu-systems.git'
    }
    manifestSha256 = Get-FileSha256 $manifestPath
    overlayFileCount = @($overlayReceipts).Count
    overlayFiles = @($overlayReceipts)
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    evidenceBoundary = if ($PackageSource -eq 'Local') {
        'Development-only local package transport; not sealed performance evidence.'
    } else {
        'Commit-pinned Git UPM transport; formal runner must still seal Player and raw evidence.'
    }
}
$receipt | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $resolvedReceipt -Encoding utf8NoBOM
$receipt | ConvertTo-Json -Depth 8
