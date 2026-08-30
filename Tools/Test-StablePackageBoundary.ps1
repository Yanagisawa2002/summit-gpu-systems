[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$expected = [ordered]@{
    'com.summit.gpu-primitives' = '0.1.0'
    'com.summit.gpu-direct-binning' = '0.2.0'
    'com.summit.gpu-adaptive-binning' = '0.3.0'
    'com.summit.gpu-driven-instances' = '0.4.0'
    'com.summit.gpu-autotuning' = '0.4.0'
}
$metaName = 'com.yanagisawa.gpu-systems-toolkit'
$metaPath = Join-Path $repository "Packages\$metaName\package.json"
$installerPath = Join-Path $repository 'Tools\Install-GpuSystemsToolkit.ps1'
$failures = [Collections.Generic.List[string]]::new()

if (-not (Test-Path -LiteralPath $metaPath -PathType Leaf)) {
    $failures.Add("Missing stable meta-package: $metaPath")
}
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    $failures.Add("Missing commit-pinned installer: $installerPath")
}

$checkedRuntimeFiles = 0
foreach ($entry in $expected.GetEnumerator()) {
    $packagePath = Join-Path $repository "Packages\$($entry.Key)"
    $manifestPath = Join-Path $packagePath 'package.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        $failures.Add("Missing stable package manifest: $manifestPath")
        continue
    }
    $manifest = Get-Content -Raw -LiteralPath $manifestPath |
        ConvertFrom-Json
    if ([string]$manifest.name -cne $entry.Key -or
        [string]$manifest.version -cne $entry.Value) {
        $failures.Add(
            "Stable package identity mismatch: $($entry.Key)@$($entry.Value)")
    }
    if ([string]$manifest.displayName -match 'SUMMIT' -or
        [string]$manifest.author.name -match '^SUMMIT$') {
        $failures.Add(
            "Stable package metadata still exposes project branding: $($entry.Key)")
    }

    $runtimePath = Join-Path $packagePath 'Runtime'
    if (Test-Path -LiteralPath $runtimePath -PathType Container) {
        $runtimeFiles = Get-ChildItem -LiteralPath $runtimePath -Recurse -File |
            Where-Object Extension -in '.cs', '.compute', '.hlsl', '.shader'
        $checkedRuntimeFiles += @($runtimeFiles).Count
        $violations = $runtimeFiles | Select-String -Pattern `
            'NYCGIS|Bfp2|FishNet|FullCityWeather|Assets/NYC|SUMMIT scene'
        foreach ($violation in @($violations)) {
            $failures.Add(
                "Project-specific runtime dependency: " +
                "$($violation.Path):$($violation.LineNumber)")
        }
    }
}

if (Test-Path -LiteralPath $metaPath -PathType Leaf) {
    $meta = Get-Content -Raw -LiteralPath $metaPath | ConvertFrom-Json
    if ([string]$meta.name -cne $metaName -or
        [string]$meta.version -cne '0.1.0') {
        $failures.Add('Stable meta-package identity/version mismatch.')
    }
    $actualDependencies = @{}
    foreach ($property in @($meta.dependencies.psobject.Properties)) {
        $actualDependencies[$property.Name] = [string]$property.Value
    }
    foreach ($entry in $expected.GetEnumerator()) {
        if (-not $actualDependencies.ContainsKey($entry.Key) -or
            $actualDependencies[$entry.Key] -cne $entry.Value) {
            $failures.Add(
                "Meta-package dependency mismatch: $($entry.Key)@$($entry.Value)")
        }
    }
    foreach ($forbidden in @(
            'com.summit.gpu-timestamps',
            'com.summit.gpu-sensor-pipeline',
            'com.summit.gpu-residency-manager',
            'com.summit.gpu-deadline-scheduler')) {
        if ($actualDependencies.ContainsKey($forbidden)) {
            $failures.Add(
                "Optional diagnostics/lab leaked into stable default: $forbidden")
        }
    }
    if ($actualDependencies.Count -ne $expected.Count) {
        $failures.Add(
            "Meta-package dependency count mismatch: " +
            "$($actualDependencies.Count), expected $($expected.Count)")
    }
    $samples = @($meta.samples)
    if ($samples.Count -ne 1 -or
        [string]$samples[0].displayName -cne 'Policy Quick Start' -or
        [string]$samples[0].path -cne 'Samples~/Policy Quick Start') {
        $failures.Add('Stable meta-package sample contract is missing or invalid.')
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "Stable package boundary validation failed ($($failures.Count))."
}

Write-Output 'Stable package boundary validated.'
Write-Output "Stable components: $($expected.Count)"
Write-Output "Runtime shader/C# files checked: $checkedRuntimeFiles"
Write-Output 'Diagnostics and Sensor/Residency/Scheduler labs are opt-in.'
