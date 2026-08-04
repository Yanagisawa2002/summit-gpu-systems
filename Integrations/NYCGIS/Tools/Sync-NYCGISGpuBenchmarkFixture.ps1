[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $true)]
    [string]$FixtureRoot,

    [string]$ProjectRoot = "",

    [string]$ExpectedSceneSha256 = "B1E650A08F43F8A47C470CB1EB4796FCF6926209BE36DBFED7E5E5FB2CA6FB5E",

    [switch]$VerifyOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Join-Path $PSScriptRoot ".."
}

function Resolve-ExistingDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$LiteralPath,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    $resolved = Resolve-Path -LiteralPath $LiteralPath -ErrorAction Stop
    if (-not (Test-Path -LiteralPath $resolved.Path -PathType Container)) {
        throw "$Label is not a directory: $($resolved.Path)"
    }

    return $resolved.Path
}

$fixtureRootResolved = Resolve-ExistingDirectory -LiteralPath $FixtureRoot -Label "Fixture root"
$projectRootResolved = Resolve-ExistingDirectory -LiteralPath $ProjectRoot -Label "Project root"

$projectAssets = Join-Path $projectRootResolved "Assets"
$projectSettings = Join-Path $projectRootResolved "ProjectSettings"
if (-not (Test-Path -LiteralPath $projectAssets -PathType Container) -or
    -not (Test-Path -LiteralPath $projectSettings -PathType Container)) {
    throw "Project root is not a Unity project: $projectRootResolved"
}

$sourceScene = Join-Path $fixtureRootResolved "Assets\Scenes\NYCGISDemoFull.unity"
$sourceGenerated = Join-Path $fixtureRootResolved "Assets\Generated"
if (-not (Test-Path -LiteralPath $sourceScene -PathType Leaf)) {
    throw "Fixture scene is missing: $sourceScene"
}
if (-not (Test-Path -LiteralPath $sourceGenerated -PathType Container)) {
    throw "Fixture generated-assets directory is missing: $sourceGenerated"
}

$sourceSceneHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourceScene).Hash
if ($sourceSceneHash -ne $ExpectedSceneSha256) {
    throw "Fixture scene hash mismatch. Expected $ExpectedSceneSha256, observed $sourceSceneHash."
}

$targetScene = Join-Path $projectRootResolved "Assets\Scenes\NYCGISDemoFull.unity"
$targetGenerated = Join-Path $projectRootResolved "Assets\Generated"

if (-not $VerifyOnly) {
    if ($PSCmdlet.ShouldProcess($targetScene, "Copy verified production benchmark scene")) {
        $targetSceneDirectory = Split-Path -Parent $targetScene
        New-Item -ItemType Directory -Path $targetSceneDirectory -Force | Out-Null
        Copy-Item -LiteralPath $sourceScene -Destination $targetScene -Force
    }

    if ($PSCmdlet.ShouldProcess($targetGenerated, "Copy generated production benchmark assets")) {
        New-Item -ItemType Directory -Path $targetGenerated -Force | Out-Null
        $sourceFiles = Get-ChildItem -LiteralPath $sourceGenerated -Recurse -File
        foreach ($sourceFile in $sourceFiles) {
            $relativePath = $sourceFile.FullName.Substring($sourceGenerated.Length).TrimStart("\")
            $targetFile = Join-Path $targetGenerated $relativePath
            $targetDirectory = Split-Path -Parent $targetFile
            New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
            Copy-Item -LiteralPath $sourceFile.FullName -Destination $targetFile -Force
        }
    }
}

if (-not (Test-Path -LiteralPath $targetScene -PathType Leaf)) {
    throw "Target scene is missing after synchronization: $targetScene"
}

$targetSceneHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $targetScene).Hash
if ($targetSceneHash -ne $ExpectedSceneSha256) {
    throw "Target scene hash mismatch. Expected $ExpectedSceneSha256, observed $targetSceneHash."
}

$fixtureGeneratedFiles = Get-ChildItem -LiteralPath $sourceGenerated -Recurse -File
$missingGeneratedFiles = @()
foreach ($sourceFile in $fixtureGeneratedFiles) {
    $relativePath = $sourceFile.FullName.Substring($sourceGenerated.Length).TrimStart("\")
    $targetFile = Join-Path $targetGenerated $relativePath
    if (-not (Test-Path -LiteralPath $targetFile -PathType Leaf)) {
        $missingGeneratedFiles += $relativePath
    }
}
if ($missingGeneratedFiles.Count -gt 0) {
    throw "Target is missing generated fixture files: $($missingGeneratedFiles -join ', ')"
}

[pscustomobject]@{
    FixtureRoot = $fixtureRootResolved
    ProjectRoot = $projectRootResolved
    SceneSha256 = $targetSceneHash
    GeneratedFileCount = $fixtureGeneratedFiles.Count
    Verified = $true
}
