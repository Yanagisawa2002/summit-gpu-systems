[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectRoot,
    [ValidateSet('Install', 'Uninstall')]
    [string]$Mode = 'Install',
    [string]$Commit,
    [string]$RepositoryUrl =
        'https://github.com/Yanagisawa2002/summit-gpu-systems.git',
    [string]$LocalRepositoryRoot,
    [switch]$IncludeDiagnostics,
    [switch]$IncludeLabs,
    [string]$ReceiptPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Sha256Text {
    param([Parameter(Mandatory = $true)][string]$Text)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
        return [Convert]::ToHexString($algorithm.ComputeHash($bytes))
    }
    finally {
        $algorithm.Dispose()
    }
}

function Get-PackageValue {
    param(
        [Parameter(Mandatory = $true)][string]$PackageName,
        [Parameter(Mandatory = $true)][string]$CommitValue
    )
    if ($script:UseLocalPackages) {
        $path = Join-Path `
            $script:ResolvedLocalRepository "Packages\$PackageName"
        if (-not (Test-Path -LiteralPath $path -PathType Container)) {
            throw "Missing local package: $path"
        }
        return 'file:' + [IO.Path]::GetFullPath($path).Replace('\', '/')
    }
    return $RepositoryUrl + '?path=/Packages/' +
        $PackageName + '#' + $CommitValue
}

$resolvedProject = (Resolve-Path -LiteralPath $ProjectRoot).Path
$manifestPath = Join-Path $resolvedProject 'Packages\manifest.json'
$projectVersionPath = Join-Path `
    $resolvedProject 'ProjectSettings\ProjectVersion.txt'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Missing Unity package manifest: $manifestPath"
}
if (-not (Test-Path -LiteralPath $projectVersionPath -PathType Leaf)) {
    throw "Missing Unity ProjectVersion.txt: $projectVersionPath"
}

$versionLine = Select-String -LiteralPath $projectVersionPath `
    -Pattern '^m_EditorVersion: (?<full>(?<version>\d+)\.(?<minor>\d+)\.[^\s]+)$' |
    Select-Object -First 1
if ($null -eq $versionLine) {
    throw 'Unable to parse the Unity editor version.'
}
$major = [int]$versionLine.Matches[0].Groups['version'].Value
$minor = [int]$versionLine.Matches[0].Groups['minor'].Value
if ($major -lt 6000 -or ($major -eq 6000 -and $minor -lt 5)) {
    throw 'GPU Systems Toolkit requires Unity 6000.5 or newer.'
}

$stablePackages = @(
    'com.summit.gpu-primitives',
    'com.summit.gpu-direct-binning',
    'com.summit.gpu-adaptive-binning',
    'com.summit.gpu-driven-instances',
    'com.summit.gpu-autotuning',
    'com.yanagisawa.gpu-systems-toolkit'
)
$diagnosticPackages = @('com.summit.gpu-timestamps')
$labPackages = @(
    'com.summit.gpu-sensor-pipeline',
    'com.summit.gpu-residency-manager',
    'com.summit.gpu-deadline-scheduler'
)
$ownedPackages = @(
    $stablePackages + $diagnosticPackages + $labPackages)

$script:UseLocalPackages =
    -not [string]::IsNullOrWhiteSpace($LocalRepositoryRoot)
$script:ResolvedLocalRepository = $null
if ($script:UseLocalPackages) {
    $script:ResolvedLocalRepository = (
        Resolve-Path -LiteralPath $LocalRepositoryRoot).Path
}
elseif ($Mode -eq 'Install') {
    if ([string]::IsNullOrWhiteSpace($Commit) -or
        $Commit -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'Git installation requires a full 40-character Commit.'
    }
    if ($RepositoryUrl.Contains('?') -or $RepositoryUrl.Contains('#')) {
        throw 'RepositoryUrl must not contain a query or fragment.'
    }
}
if ([string]::IsNullOrWhiteSpace($Commit)) {
    $Commit = 'local-uncommitted'
}

$beforeText = Get-Content -Raw -LiteralPath $manifestPath
$manifest = $beforeText | ConvertFrom-Json -AsHashtable
$dependencies = $manifest['dependencies']
if ($null -eq $dependencies) {
    throw 'Unity package manifest has no dependencies object.'
}

$changedPackages = [Collections.Generic.List[string]]::new()
if ($Mode -eq 'Install') {
    $requestedPackages = [Collections.Generic.List[string]]::new()
    foreach ($name in $stablePackages) {
        $requestedPackages.Add($name)
    }
    if ($IncludeDiagnostics) {
        foreach ($name in $diagnosticPackages) {
            $requestedPackages.Add($name)
        }
    }
    if ($IncludeLabs) {
        foreach ($name in $labPackages) {
            $requestedPackages.Add($name)
        }
    }
    foreach ($name in $requestedPackages) {
        $value = Get-PackageValue $name $Commit.ToLowerInvariant()
        if (-not $dependencies.ContainsKey($name) -or
            [string]$dependencies[$name] -cne $value) {
            $dependencies[$name] = $value
            $changedPackages.Add($name)
        }
    }
}
else {
    foreach ($name in $ownedPackages) {
        if ($dependencies.ContainsKey($name)) {
            $dependencies.Remove($name)
            $changedPackages.Add($name)
        }
    }
}

$orderedDependencies = [ordered]@{}
foreach ($name in @($dependencies.Keys | Sort-Object)) {
    $orderedDependencies[$name] = $dependencies[$name]
}
$manifest['dependencies'] = $orderedDependencies
$afterText = ($manifest | ConvertTo-Json -Depth 12) + [Environment]::NewLine
$temporaryManifest = $manifestPath + '.partial'
Set-Content -LiteralPath $temporaryManifest -Value $afterText `
    -Encoding utf8NoBOM -NoNewline
Move-Item -LiteralPath $temporaryManifest -Destination $manifestPath -Force

if ([string]::IsNullOrWhiteSpace($ReceiptPath)) {
    $ReceiptPath = Join-Path `
        $resolvedProject 'GpuSystemsToolkitInstallReceipt.json'
}
$resolvedReceipt = [IO.Path]::GetFullPath($ReceiptPath)
$receiptDirectory = Split-Path -Parent $resolvedReceipt
New-Item -ItemType Directory -Force -Path $receiptDirectory | Out-Null
$receipt = [pscustomobject][ordered]@{
    schemaVersion = 1
    suite = 'gpu-systems-toolkit.upm-install'
    accepted = $true
    mode = $Mode.ToLowerInvariant()
    projectRoot = $resolvedProject
    unityVersion = $versionLine.Matches[0].Groups['full'].Value
    packageSource = if ($Mode -eq 'Uninstall') {
        'none'
    } elseif ($script:UseLocalPackages) {
        'local'
    } else {
        'git'
    }
    commit = if ($Mode -eq 'Uninstall') {
        'not-applicable'
    } else {
        $Commit.ToLowerInvariant()
    }
    repository = if ($Mode -eq 'Uninstall') {
        'none'
    } elseif ($script:UseLocalPackages) {
        $script:ResolvedLocalRepository
    } else {
        $RepositoryUrl
    }
    includeDiagnostics = [bool]$IncludeDiagnostics
    includeLabs = [bool]$IncludeLabs
    changedPackageCount = $changedPackages.Count
    changedPackages = [string[]]$changedPackages.ToArray()
    manifestBeforeSha256 = Get-Sha256Text $beforeText
    manifestAfterSha256 = Get-Sha256Text $afterText
    sealedTransportEligible =
        (-not $script:UseLocalPackages) -and $Mode -eq 'Install'
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    evidenceBoundary =
        'This receipt proves deterministic manifest mutation only. Unity ' +
        'package resolution and compilation require a separate Editor log.'
}
$receipt | ConvertTo-Json -Depth 6 |
    Set-Content -LiteralPath $resolvedReceipt -Encoding utf8NoBOM
$receipt | ConvertTo-Json -Depth 6
