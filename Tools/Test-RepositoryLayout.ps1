[CmdletBinding()]
param(
    [string]$RepositoryRoot = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [System.IO.Path]::GetFullPath(
        (Split-Path -Parent $PSScriptRoot))
}
$expectedPackages = @(
    'com.summit.gpu-adaptive-binning',
    'com.summit.gpu-autotuning',
    'com.summit.gpu-deadline-scheduler',
    'com.summit.gpu-direct-binning',
    'com.summit.gpu-primitives',
    'com.summit.gpu-residency-manager',
    'com.summit.gpu-sensor-pipeline',
    'com.summit.gpu-timestamps'
)
$failures = [System.Collections.Generic.List[string]]::new()
$manifestPath = Join-Path $RepositoryRoot 'Packages\manifest.json'

if (-not (Test-Path -LiteralPath $manifestPath)) {
    $failures.Add("Missing Unity manifest: $manifestPath")
}
else {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($packageName in $expectedPackages) {
        $packageRoot = Join-Path $RepositoryRoot "Packages\$packageName"
        $packageManifest = Join-Path $packageRoot 'package.json'
        if (-not (Test-Path -LiteralPath $packageManifest)) {
            $failures.Add("Missing package manifest: $packageManifest")
            continue
        }

        $metadata = Get-Content -LiteralPath $packageManifest -Raw |
            ConvertFrom-Json
        if ($metadata.name -ne $packageName) {
            $failures.Add(
                "Package name mismatch at ${packageManifest}: $($metadata.name)")
        }

        if ($null -eq $manifest.dependencies.$packageName) {
            $failures.Add("Root manifest does not embed $packageName")
        }
        if ($packageName -notin $manifest.testables) {
            $failures.Add("Root manifest does not mark $packageName testable")
        }
    }
}

$portableRoots = @(
    (Join-Path $RepositoryRoot 'Packages'),
    (Join-Path $RepositoryRoot 'Assets')
)
$portableFiles = Get-ChildItem -LiteralPath $portableRoots -Recurse -File |
    Where-Object Extension -in '.cs', '.compute', '.hlsl', '.shader'
$forbiddenPattern = 'NYCGIS|Bfp2|FishNet|FullCityWeather'
$violations = $portableFiles | Select-String -Pattern $forbiddenPattern
foreach ($violation in $violations) {
    $failures.Add(
        "Portable boundary violation: $($violation.Path):$($violation.LineNumber)")
}

$integrationRoot = Join-Path $RepositoryRoot 'Integrations\NYCGIS'
if (-not (Test-Path -LiteralPath $integrationRoot)) {
    $failures.Add('Missing isolated NYCGIS integration snapshot.')
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Error $failure -ErrorAction Continue
    }
    throw "Repository validation failed with $($failures.Count) issue(s)."
}

$sourceCount = $portableFiles.Count
Write-Host "Repository layout validated."
Write-Host "Packages: $($expectedPackages.Count)"
Write-Host "Portable shader/C# files checked: $sourceCount"
Write-Host "Project-specific integration is isolated under Integrations/NYCGIS."
