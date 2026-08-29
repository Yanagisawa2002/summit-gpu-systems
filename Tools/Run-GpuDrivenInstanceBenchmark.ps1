[CmdletBinding()]
param(
    [string]$UnityPath =
        'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe',
    [string]$OutputDirectory,
    [string]$PlayerPath,
    [ValidateRange(0, 16)]
    [int]$DeviceIndex = 0,
    [ValidateSet('single', 'visibility-sweep-v1')]
    [string]$MatrixPreset = 'single',
    [ValidateSet('filtered-binning', 'hierarchical-culling')]
    [string]$BenchmarkMode = 'filtered-binning',
    [string]$ScenarioId = 'custom',
    [ValidateSet('visible5', 'visible25', 'visible75', 'visible100')]
    [string]$Visibility = 'visible25',
    [ValidateRange(1024, 4194240)]
    [int]$InstanceCount = 1048576,
    [ValidateRange(1, 32)]
    [int]$ViewCount = 4,
    [int]$Seed = 20260829,
    [ValidateRange(1, 4)]
    [int]$SuperRounds = 2,
    [ValidateRange(5, 1800)]
    [int]$WarmupFrames = 60,
    [ValidateRange(60, 7200)]
    [int]$SampleFrames = 240,
    [ValidateRange(0, 600)]
    [int]$CooldownFrames = 15,
    [ValidateRange(1, 128)]
    [int]$DispatchesPerFrame = 1,
    [ValidateRange(5, 600)]
    [int]$ValidationTimeoutSeconds = 60,
    [ValidateRange(5, 240)]
    [int]$PlayerTimeoutMinutes = 90,
    [switch]$FormalAcceptanceMode,
    [string]$EditModeResultsPath,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$BenchmarkMode = $BenchmarkMode.ToLowerInvariant()
if ($SkipBuild) {
    throw (
        '-SkipBuild is retained for command-line compatibility, but every ' +
        'GPU-driven instance benchmark run requires a fresh Player build.')
}

$formalContract = [ordered]@{
    contractId = if ($BenchmarkMode -ceq 'hierarchical-culling') {
        'gpu-driven-hierarchical-culling-v1'
    }
    else {
        'gpu-driven-visible-only-v1'
    }
    unityVersion = '6000.5.2f1'
    deviceIndex = 0
    benchmarkMode = if ($BenchmarkMode -ceq 'hierarchical-culling') {
        'hierarchical-culling'
    }
    else {
        'filtered-binning'
    }
    visibilityLayout = if ($BenchmarkMode -ceq 'hierarchical-culling') {
        'spatial-clustered-multiview-64-v2'
    }
    else {
        'seeded-coprime-permutation-v1'
    }
    matrixPreset = 'visibility-sweep-v1'
    instanceCount = 1048576
    viewCount = 4
    superRounds = 2
    warmupFrames = 60
    sampleFrames = 900
    cooldownFrames = 15
    dispatchesPerFrame = 1
}
$formalScenarioPrefix = if ($BenchmarkMode -ceq 'hierarchical-culling') {
    'hierarchical-'
}
else {
    ''
}
$formalScenarios = @(
    [ordered]@{
        scenarioId = $formalScenarioPrefix + 'visible5-n1048576-v4'
        visibility = 'visible5'
        instanceCount = 1048576
        viewCount = 4
        seed = 20260829
    },
    [ordered]@{
        scenarioId = $formalScenarioPrefix + 'visible25-n1048576-v4'
        visibility = 'visible25'
        instanceCount = 1048576
        viewCount = 4
        seed = 20260829
    },
    [ordered]@{
        scenarioId = $formalScenarioPrefix + 'visible75-n1048576-v4'
        visibility = 'visible75'
        instanceCount = 1048576
        viewCount = 4
        seed = 20260829
    },
    [ordered]@{
        scenarioId = $formalScenarioPrefix + 'visible100-n1048576-v4'
        visibility = 'visible100'
        instanceCount = 1048576
        viewCount = 4
        seed = 20260829
    }
)
$hierarchicalMode = $BenchmarkMode -ceq 'hierarchical-culling'
$expectedVisibilityLayout = if ($hierarchicalMode) {
    'spatial-clustered-multiview-64-v2'
}
else {
    'seeded-coprime-permutation-v1'
}
$baselineVariantId = if ($hierarchicalMode) {
    'flat-visible-only-portable'
}
else {
    'culled-tail-portable'
}
$optimizedVariantId = if ($hierarchicalMode) {
    'hierarchical-visible-only-portable'
}
else {
    'visible-only-discard-key-portable'
}
$baselineReportLabel = if ($hierarchicalMode) {
    'Flat visible-only'
}
else {
    'Baseline'
}
$optimizedReportLabel = if ($hierarchicalMode) {
    'Hierarchical visible-only'
}
else {
    'Visible-only'
}

function Quote-ProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Value)
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Get-CombinedSha256 {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo[]]$Files,
        [Parameter(Mandatory = $true)][string]$RelativeTo
    )
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $builder = [System.Text.StringBuilder]::new()
        foreach ($file in @($Files | Sort-Object FullName)) {
            $relative = $file.FullName.Substring($RelativeTo.Length).
                TrimStart([char[]]@('\', '/')).Replace('\', '/')
            $hash = (Get-FileHash `
                -LiteralPath $file.FullName `
                -Algorithm SHA256).Hash
            [void]$builder.Append($relative)
            [void]$builder.Append("`0")
            [void]$builder.Append($hash)
            [void]$builder.Append("`n")
        }
        return ([BitConverter]::ToString(
            $sha.ComputeHash(
                [Text.Encoding]::UTF8.GetBytes($builder.ToString())))).
            Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
}

function Get-NUnitMetadata {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$ExpectedIdentities
    )
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "EditMode results are missing: $resolved"
    }
    [xml]$xml = Get-Content -LiteralPath $resolved -Raw
    $run = $xml.'test-run'
    if ($null -eq $run) {
        throw "EditMode result is not NUnit XML: $resolved"
    }
    $identities = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $nodes = @($xml.SelectNodes("//test-suite[@type='Assembly']")) +
        @($xml.SelectNodes("//test-suite[@type='TestFixture']"))
    foreach ($node in $nodes) {
        [void]$identities.Add(
            $(if ([string]$node.type -ceq 'Assembly') {
                [string]$node.name
            }
            else {
                [string]$node.fullname
            }))
    }
    $missing = @(
        $ExpectedIdentities |
            Where-Object { -not $identities.Contains($_) })
    return [ordered]@{
        path = $resolved
        sha256 =
            (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
        result = [string]$run.result
        total = [int]$run.total
        passed = [int]$run.passed
        failed = [int]$run.failed
        skipped = [int]$run.skipped
        inconclusive = [int]$run.inconclusive
        expectedIdentities = $ExpectedIdentities
        observedIdentities = @($identities | Sort-Object)
        missingIdentities = $missing
    }
}

function Get-UnityProductVersion {
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)
    $versionInfo = (Get-Item -LiteralPath $ExecutablePath).VersionInfo
    $version = (([string]$versionInfo.ProductVersion -split '_', 2)[0]).Trim()
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "Unity executable has no ProductVersion: $ExecutablePath"
    }
    return $version
}

function Get-VerifiedEditModeReceipt {
    param(
        [Parameter(Mandatory = $true)]$TestMetadata,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$ProjectVersionPath,
        [Parameter(Mandatory = $true)][string]$ProjectUnityVersion,
        [Parameter(Mandatory = $true)][string]$UnityPath,
        [Parameter(Mandatory = $true)][string]$UnityProductVersion,
        [Parameter(Mandatory = $true)][string]$GitCommit
    )
    $receiptPath = [string]$TestMetadata.path + '.receipt.json'
    if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
        throw "Formal EditMode receipt is missing: $receiptPath"
    }
    $receipt = Get-Content -LiteralPath $receiptPath -Raw |
        ConvertFrom-Json
    $required = @(
        'schemaVersion',
        'receiptType',
        'projectPath',
        'projectVersionPath',
        'projectVersionSha256',
        'projectUnityVersion',
        'unityExecutablePath',
        'unityExecutableSha256',
        'unityProductVersion',
        'resultsPath',
        'resultsSha256',
        'testPlatform',
        'useGraphics',
        'forceDirect3D12',
        'exitCode',
        'result',
        'total',
        'passed',
        'failed',
        'skipped',
        'inconclusive',
        'gitCommit',
        'gitRoot',
        'gitStart',
        'gitFinal',
        'gitStateStable')
    $missing = @(
        $required | Where-Object {
            $_ -notin @($receipt.PSObject.Properties.Name)
        })
    if ($missing.Count -ne 0) {
        throw "Formal EditMode receipt is incomplete: $($missing -join ', ')"
    }
    foreach ($snapshotName in @('gitStart', 'gitFinal')) {
        $snapshot = $receipt.$snapshotName
        $snapshotMissing = @(
            @('root', 'head', 'branch', 'dirty', 'statusLines') |
                Where-Object {
                    $_ -notin @($snapshot.PSObject.Properties.Name)
                })
        if ($snapshotMissing.Count -ne 0) {
            throw (
                "Formal EditMode receipt $snapshotName is incomplete: " +
                ($snapshotMissing -join ', '))
        }
    }

    $resolvedProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
    $resolvedProjectVersionPath =
        [IO.Path]::GetFullPath($ProjectVersionPath)
    $resolvedUnityPath = [IO.Path]::GetFullPath($UnityPath)
    $resolvedResultsPath = [IO.Path]::GetFullPath([string]$TestMetadata.path)
    $currentUnitySha256 =
        (Get-FileHash -LiteralPath $resolvedUnityPath -Algorithm SHA256).Hash
    $currentProjectVersionSha256 =
        (Get-FileHash `
            -LiteralPath $resolvedProjectVersionPath `
            -Algorithm SHA256).Hash
    $violations = [Collections.Generic.List[string]]::new()
    if ([int]$receipt.schemaVersion -ne 1) {
        $violations.Add("schemaVersion=$($receipt.schemaVersion)")
    }
    if ([string]$receipt.receiptType -cne 'summit.unity-editmode-test') {
        $violations.Add("receiptType='$($receipt.receiptType)'")
    }
    foreach ($pathEntry in @(
            @('projectPath', [string]$receipt.projectPath,
                $resolvedProjectRoot),
            @('gitRoot', [string]$receipt.gitRoot,
                $resolvedProjectRoot),
            @('projectVersionPath', [string]$receipt.projectVersionPath,
                $resolvedProjectVersionPath),
            @('unityExecutablePath', [string]$receipt.unityExecutablePath,
                $resolvedUnityPath),
            @('resultsPath', [string]$receipt.resultsPath,
                $resolvedResultsPath))) {
        if ([IO.Path]::GetFullPath($pathEntry[1]) -ine $pathEntry[2]) {
            $violations.Add(
                "$($pathEntry[0])='$($pathEntry[1])'; expected '$($pathEntry[2])'")
        }
    }
    foreach ($valueEntry in @(
            @('gitCommit', [string]$receipt.gitCommit, $GitCommit),
            @('gitStart.head', [string]$receipt.gitStart.head, $GitCommit),
            @('gitFinal.head', [string]$receipt.gitFinal.head, $GitCommit),
            @('gitStart.root',
                [IO.Path]::GetFullPath([string]$receipt.gitStart.root),
                $resolvedProjectRoot),
            @('gitFinal.root',
                [IO.Path]::GetFullPath([string]$receipt.gitFinal.root),
                $resolvedProjectRoot),
            @('resultsSha256', [string]$receipt.resultsSha256,
                [string]$TestMetadata.sha256),
            @('projectVersionSha256',
                [string]$receipt.projectVersionSha256,
                $currentProjectVersionSha256),
            @('unityExecutableSha256',
                [string]$receipt.unityExecutableSha256,
                $currentUnitySha256),
            @('projectUnityVersion',
                [string]$receipt.projectUnityVersion,
                $ProjectUnityVersion),
            @('unityProductVersion',
                [string]$receipt.unityProductVersion,
                $UnityProductVersion),
            @('testPlatform', [string]$receipt.testPlatform, 'EditMode'),
            @('result', [string]$receipt.result,
                [string]$TestMetadata.result))) {
        if ([string]$valueEntry[1] -cne [string]$valueEntry[2]) {
            $violations.Add(
                "$($valueEntry[0])='$($valueEntry[1])'; " +
                "expected '$($valueEntry[2])'")
        }
    }
    foreach ($countEntry in @(
            @('total', [int]$receipt.total, [int]$TestMetadata.total),
            @('passed', [int]$receipt.passed, [int]$TestMetadata.passed),
            @('failed', [int]$receipt.failed, [int]$TestMetadata.failed),
            @('skipped', [int]$receipt.skipped, [int]$TestMetadata.skipped),
            @('inconclusive', [int]$receipt.inconclusive,
                [int]$TestMetadata.inconclusive))) {
        if ([int]$countEntry[1] -ne [int]$countEntry[2]) {
            $violations.Add(
                "$($countEntry[0])=$($countEntry[1]); expected $($countEntry[2])")
        }
    }
    if ([int]$receipt.exitCode -ne 0) {
        $violations.Add("exitCode=$($receipt.exitCode)")
    }
    if (-not [bool]$receipt.useGraphics -or
        -not [bool]$receipt.forceDirect3D12) {
        $violations.Add(
            'formal EditMode tests did not use the Direct3D 12 graphics path')
    }
    if (-not [bool]$receipt.gitStateStable -or
        [bool]$receipt.gitStart.dirty -or
        [bool]$receipt.gitFinal.dirty -or
        @($receipt.gitStart.statusLines).Count -ne 0 -or
        @($receipt.gitFinal.statusLines).Count -ne 0) {
        $violations.Add('receipt Git state is not clean and stable')
    }
    if ($violations.Count -ne 0) {
        throw (
            "Formal EditMode receipt rejected:`n" +
            ($violations -join "`n"))
    }
    return [ordered]@{
        path = [IO.Path]::GetFullPath($receiptPath)
        sha256 =
            (Get-FileHash -LiteralPath $receiptPath -Algorithm SHA256).Hash
        receipt = $receipt
        verified = $true
    }
}

function Read-KeyValueFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    $result = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        $parts = $line -split '=', 2
        if ($parts.Count -eq 2) {
            $result[$parts[0]] = $parts[1]
        }
    }
    return $result
}

function Number {
    param([Parameter(Mandatory = $true)]$Value)
    return [double]::Parse(
        [string]$Value,
        [Globalization.CultureInfo]::InvariantCulture)
}

function Is-FiniteNumber {
    param([Parameter(Mandatory = $true)]$Value)
    $number = Number $Value
    return -not [double]::IsNaN($number) -and
        -not [double]::IsInfinity($number)
}

function Mean {
    param([Parameter(Mandatory = $true)][double[]]$Values)
    if ($Values.Count -eq 0) { throw 'Cannot average an empty array.' }
    return [double](($Values | Measure-Object -Average).Average)
}

function Percentile {
    param(
        [Parameter(Mandatory = $true)][double[]]$Values,
        [Parameter(Mandatory = $true)][double]$Probability
    )
    if ($Values.Count -eq 0) { throw 'Cannot rank an empty array.' }
    $sorted = [double[]]@($Values | Sort-Object)
    $rank = ($sorted.Count - 1) * $Probability
    $low = [int][Math]::Floor($rank)
    $high = [int][Math]::Ceiling($rank)
    if ($low -eq $high) { return $sorted[$low] }
    return $sorted[$low] +
        ($sorted[$high] - $sorted[$low]) * ($rank - $low)
}

function ImprovementPercent {
    param([double]$Baseline, [double]$Optimized)
    if ($Baseline -le 0.0) { return [double]::NaN }
    return 100.0 * ($Baseline - $Optimized) / $Baseline
}

if ([int64]$InstanceCount * [int64]$ViewCount -gt 16776960L) {
    throw 'InstanceCount * ViewCount exceeds the GPU primitive capacity.'
}
if ($FormalAcceptanceMode) {
    $violations = [Collections.Generic.List[string]]::new()
    foreach ($entry in @(
            @('DeviceIndex', $DeviceIndex, $formalContract.deviceIndex),
            @('InstanceCount', $InstanceCount,
                $formalContract.instanceCount),
            @('ViewCount', $ViewCount, $formalContract.viewCount),
            @('SuperRounds', $SuperRounds, $formalContract.superRounds),
            @('WarmupFrames', $WarmupFrames,
                $formalContract.warmupFrames),
            @('SampleFrames', $SampleFrames,
                $formalContract.sampleFrames),
            @('CooldownFrames', $CooldownFrames,
                $formalContract.cooldownFrames),
            @('DispatchesPerFrame', $DispatchesPerFrame,
                $formalContract.dispatchesPerFrame))) {
        if ([int64]$entry[1] -ne [int64]$entry[2]) {
            $violations.Add(
                "$($entry[0])=$($entry[1]); expected $($entry[2])")
        }
    }
    if ($MatrixPreset -cne $formalContract.matrixPreset) {
        $violations.Add(
            "MatrixPreset='$MatrixPreset'; expected " +
            "'$($formalContract.matrixPreset)'")
    }
    if ($BenchmarkMode -cne $formalContract.benchmarkMode) {
        $violations.Add(
            "BenchmarkMode='$BenchmarkMode'; expected " +
            "'$($formalContract.benchmarkMode)'")
    }
    if ([string]::IsNullOrWhiteSpace($EditModeResultsPath)) {
        $violations.Add('EditModeResultsPath is required.')
    }
    if ($violations.Count -ne 0) {
        throw "Formal acceptance contract rejected:`n$($violations -join "`n")"
    }
}
elseif ($MatrixPreset -cne 'single') {
    throw "Non-formal runs require MatrixPreset='single'."
}

$projectRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent $PSScriptRoot))
$provenanceModulePath =
    Join-Path $PSScriptRoot 'GpuBenchmarkProvenance.psm1'
Import-Module -Name $provenanceModulePath -Force
$gitStart = Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
if ([bool]$gitStart.dirty) {
    throw "Benchmark requires a clean worktree:`n" +
        (@($gitStart.statusLines) -join "`n")
}
if ([string]$gitStart.branch -ceq 'HEAD') {
    throw 'Benchmark requires a named Git branch.'
}
$gitCommit = [string]$gitStart.head

$projectVersionPath =
    Join-Path $projectRoot 'ProjectSettings\ProjectVersion.txt'
$versionLine = Get-Content -LiteralPath $projectVersionPath |
    Where-Object { $_ -like 'm_EditorVersion:*' } |
    Select-Object -First 1
$projectUnityVersion = ($versionLine -split ':', 2)[1].Trim()
if ($FormalAcceptanceMode -and
    $projectUnityVersion -cne $formalContract.unityVersion) {
    throw "Formal benchmark requires Unity $($formalContract.unityVersion)."
}
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw "Unity editor is missing: $UnityPath"
}
$resolvedUnityPath = [IO.Path]::GetFullPath($UnityPath)
$unityProductVersion =
    Get-UnityProductVersion -ExecutablePath $resolvedUnityPath
if ($unityProductVersion -cne $projectUnityVersion) {
    throw (
        "Unity executable version '$unityProductVersion' does not match " +
        "ProjectVersion '$projectUnityVersion'.")
}
if ($FormalAcceptanceMode -and
    $unityProductVersion -cne $formalContract.unityVersion) {
    throw (
        "Formal benchmark requires editor $($formalContract.unityVersion); " +
        "found '$unityProductVersion'.")
}

$expectedTestIdentities = @(
    'Summit.GpuDrivenInstance.Benchmark.Tests.Editor.dll',
    'Summit.GpuDrivenInstance.Benchmark.Tests.GpuDrivenInstanceBenchmarkCpuOracleTests',
    'Summit.GpuDrivenInstance.Benchmark.Tests.GpuDrivenInstanceBenchmarkScheduleTests',
    'Summit.GpuDrivenInstance.Benchmark.Tests.GpuDrivenInstanceInputGeneratorTests',
    'Summit.GpuDrivenInstances.Tests.Editor.dll',
    'Summit.GpuDrivenInstances.Tests.GpuDrivenInstancePipelineContractTests',
    'Summit.GpuDrivenInstances.Tests.GpuDrivenInstancePipelineIntegrationTests',
    'Summit.GpuDirectBinning.Tests.Editor.dll',
    'Summit.GpuDirectBinning.Tests.GpuDirectSpatialBinnerContractTests',
    'Summit.GpuDirectBinning.Tests.GpuDirectSpatialBinnerIntegrationTests'
)
if ($hierarchicalMode) {
    $expectedTestIdentities += @(
        'Summit.GpuDrivenInstance.Benchmark.Tests.' +
            'GpuDrivenInstanceHierarchicalInputGeneratorTests',
        'Summit.GpuDrivenInstances.Tests.GpuInstanceClusterBuilderTests',
        'Summit.GpuDrivenInstances.Tests.' +
            'GpuDrivenInstanceHierarchicalPipelineIntegrationTests',
        'Summit.GpuDirectBinning.Tests.' +
            'GpuDirectSpatialBinnerPrecountedIntegrationTests'
    )
}
$testMetadata = $null
$testReceiptMetadata = $null
if (-not [string]::IsNullOrWhiteSpace($EditModeResultsPath)) {
    $testMetadata = Get-NUnitMetadata `
        -Path $EditModeResultsPath `
        -ExpectedIdentities $expectedTestIdentities
}
if ($FormalAcceptanceMode -and
    ($null -eq $testMetadata -or
        $testMetadata.result -cne 'Passed' -or
        $testMetadata.total -le 0 -or
        $testMetadata.passed -ne $testMetadata.total -or
        $testMetadata.failed -ne 0 -or
        $testMetadata.skipped -ne 0 -or
        $testMetadata.inconclusive -ne 0 -or
        @($testMetadata.missingIdentities).Count -ne 0)) {
    $missing = if ($null -eq $testMetadata) {
        'metadata unavailable'
    }
    else { @($testMetadata.missingIdentities) -join ', ' }
    throw "Formal benchmark requires complete focused tests; missing: $missing"
}
if ($FormalAcceptanceMode) {
    $testReceiptMetadata = Get-VerifiedEditModeReceipt `
        -TestMetadata $testMetadata `
        -ProjectRoot $projectRoot `
        -ProjectVersionPath $projectVersionPath `
        -ProjectUnityVersion $projectUnityVersion `
        -UnityPath $resolvedUnityPath `
        -UnityProductVersion $unityProductVersion `
        -GitCommit $gitCommit
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $projectRoot (
        "Reports\GpuDrivenInstance\$MatrixPreset-$stamp")
}
elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ($FormalAcceptanceMode -and (Test-Path -LiteralPath $outputRoot)) {
    if ($null -ne (
            Get-ChildItem -LiteralPath $outputRoot -Force |
                Select-Object -First 1)) {
        throw 'Formal report directory must be new or empty.'
    }
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($PlayerPath)) {
    $PlayerPath = Join-Path $projectRoot (
        'Builds\GpuDrivenInstanceBenchmark\' +
        'GpuDrivenInstanceBenchmark.exe')
}
elseif (-not [IO.Path]::IsPathRooted($PlayerPath)) {
    $PlayerPath = Join-Path $projectRoot $PlayerPath
}
$resolvedPlayerPath = [IO.Path]::GetFullPath($PlayerPath)
if ($outputRoot.StartsWith(
        (Split-Path -Parent $resolvedPlayerPath) +
            [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Report output may not be inside the Player payload.'
}

$benchmarkRoot =
    Join-Path $projectRoot 'Assets\GpuDrivenInstanceBenchmark'
$instanceRuntime =
    Join-Path $projectRoot 'Packages\com.summit.gpu-driven-instances\Runtime'
$binningRuntime =
    Join-Path $projectRoot 'Packages\com.summit.gpu-direct-binning\Runtime'
$primitiveRuntime =
    Join-Path $projectRoot 'Packages\com.summit.gpu-primitives\Runtime'
$timestampRuntime =
    Join-Path $projectRoot 'Packages\com.summit.gpu-timestamps\Runtime'
$runtimeShaderPath = Join-Path $instanceRuntime (
    'Resources\GpuDrivenInstances\GpuDrivenInstances.compute')
$binningShaderPath = Join-Path $binningRuntime (
    'Resources\GpuDirectBinning\GpuDirectBinning.compute')
$timestampDllPath = Join-Path $timestampRuntime (
    'Plugins\x86_64\SummitGpuTimestamps.dll')
$buildScriptPath = Join-Path $benchmarkRoot (
    'Editor\GpuDrivenInstanceBenchmarkBuild.cs')
foreach ($path in @(
        $runtimeShaderPath,
        $binningShaderPath,
        $timestampDllPath,
        $buildScriptPath,
        $provenanceModulePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required benchmark input is missing: $path"
    }
}

$runtimeApiFiles = [IO.FileInfo[]]@(
    Get-ChildItem -LiteralPath $instanceRuntime -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.asmdef') }
    Get-ChildItem -LiteralPath $binningRuntime -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.asmdef') }
    Get-ChildItem -LiteralPath $primitiveRuntime -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.asmdef') }
)
$sourceFiles = [IO.FileInfo[]]@(
    Get-ChildItem -LiteralPath $benchmarkRoot -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.compute', '.asmdef') }
    Get-ChildItem -LiteralPath $instanceRuntime -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.compute', '.asmdef') }
    Get-ChildItem -LiteralPath $binningRuntime -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.compute', '.asmdef') }
    Get-ChildItem -LiteralPath $primitiveRuntime -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.compute', '.asmdef') }
    Get-ChildItem -LiteralPath $timestampRuntime -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.asmdef') }
    Get-Item -LiteralPath $timestampDllPath
    Get-Item -LiteralPath $PSCommandPath
    Get-Item -LiteralPath $provenanceModulePath
    Get-Item -LiteralPath $projectVersionPath
    Get-Item -LiteralPath (Join-Path $projectRoot 'Packages\manifest.json')
    Get-Item -LiteralPath (Join-Path $projectRoot 'Packages\packages-lock.json')
)
$sourceSnapshotSha256 =
    Get-CombinedSha256 -Files $sourceFiles -RelativeTo $projectRoot
$runtimeShaderSha256 =
    (Get-FileHash -LiteralPath $runtimeShaderPath -Algorithm SHA256).Hash
$binningShaderSha256 =
    (Get-FileHash -LiteralPath $binningShaderPath -Algorithm SHA256).Hash
$runtimeApiSha256 =
    Get-CombinedSha256 -Files $runtimeApiFiles -RelativeTo $projectRoot

$scenarios = if ($FormalAcceptanceMode) {
    $formalScenarios
}
else {
    @([ordered]@{
        scenarioId = $ScenarioId
        visibility = $Visibility
        instanceCount = $InstanceCount
        viewCount = $ViewCount
        seed = $Seed
    })
}

$videoControllers = @()
$videoControllerError = ''
try {
    $videoControllers = @(
        Get-CimInstance -ClassName Win32_VideoController |
            ForEach-Object {
                [ordered]@{
                    name = [string]$_.Name
                    driverVersion = [string]$_.DriverVersion
                    pnpDeviceId = [string]$_.PNPDeviceID
                }
            })
}
catch {
    $videoControllerError = $_.Exception.Message
}

$runnerConfig = [ordered]@{
    schemaVersion = 1
    suite = 'summit.gpu-driven-instance'
    formalAcceptanceMode = [bool]$FormalAcceptanceMode
    formalContract = $formalContract
    benchmarkMode = $BenchmarkMode
    visibilityLayout = $expectedVisibilityLayout
    baselineVariant = $baselineVariantId
    optimizedVariant = $optimizedVariantId
    matrixPreset = $MatrixPreset
    scenarios = $scenarios
    editModeResults = $testMetadata
    editModeReceipt = $testReceiptMetadata
    projectRoot = $projectRoot
    projectUnityVersion = $projectUnityVersion
    unityPath = $resolvedUnityPath
    unityProductVersion = $unityProductVersion
    playerPath = $resolvedPlayerPath
    outputDirectory = $outputRoot
    deviceIndex = $DeviceIndex
    superRounds = $SuperRounds
    warmupFrames = $WarmupFrames
    sampleFrames = $SampleFrames
    cooldownFrames = $CooldownFrames
    dispatchesPerFrame = $DispatchesPerFrame
    validationTimeoutSeconds = $ValidationTimeoutSeconds
    gitCommit = $gitCommit
    gitBranch = [string]$gitStart.branch
    gitStart = $gitStart
    sourceSnapshotSha256 = $sourceSnapshotSha256
    runtimeShaderSha256 = $runtimeShaderSha256
    binningShaderSha256 = $binningShaderSha256
    runtimeApiSha256 = $runtimeApiSha256
    timestampNativeDllSha256 =
        (Get-FileHash -LiteralPath $timestampDllPath -Algorithm SHA256).Hash
    windowsVideoControllers = $videoControllers
    windowsVideoControllerInventoryError = $videoControllerError
    playerPayload = $null
    playerPayloadStableThroughRun = $false
    sourceHashesStableAcrossBuild = $false
    playerRuns = @()
    gitFinal = $null
    formalContractSatisfied = $false
    runnerConfigFinalized = $false
    startedUtc = (Get-Date).ToUniversalTime().ToString('o')
    finalizedUtc = ''
}
$runnerConfigPath = Join-Path $outputRoot 'runner-config.json'
$runnerConfig | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath $runnerConfigPath -Encoding utf8
if ($null -ne $testMetadata) {
    Copy-Item -LiteralPath $testMetadata.path `
        -Destination (Join-Path $outputRoot 'editmode-results.xml') -Force
}
if ($null -ne $testReceiptMetadata) {
    Copy-Item -LiteralPath $testReceiptMetadata.path `
        -Destination (
            Join-Path $outputRoot 'editmode-results.xml.receipt.json') -Force
}

$buildLog = Join-Path $outputRoot 'unity-build.log'
New-Item -ItemType Directory -Force `
    -Path (Split-Path -Parent $resolvedPlayerPath) | Out-Null
$buildArguments = @(
    '-batchmode',
    '-nographics',
    '-quit',
    '-projectPath', (Quote-ProcessArgument $projectRoot),
    '-executeMethod', 'GpuDrivenInstanceBenchmarkBuild.PerformBuild',
    '-gpu-driven-instance-player-path',
        (Quote-ProcessArgument $resolvedPlayerPath),
    '-logFile', (Quote-ProcessArgument $buildLog)
)
$build = Start-Process `
    -FilePath $UnityPath `
    -ArgumentList $buildArguments `
    -WorkingDirectory $projectRoot `
    -WindowStyle Hidden `
    -Wait `
    -PassThru
if ($build.ExitCode -ne 0) {
    throw "Unity Player build failed; see $buildLog"
}
if (-not (Test-Path -LiteralPath $resolvedPlayerPath -PathType Leaf)) {
    throw "Benchmark Player is missing: $resolvedPlayerPath"
}
$postBuildGit = Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
if ($postBuildGit.head -ine $gitCommit -or [bool]$postBuildGit.dirty) {
    throw 'Git state changed during the Player build.'
}
$postBuildSourceHash =
    Get-CombinedSha256 -Files $sourceFiles -RelativeTo $projectRoot
$sourceStable = $postBuildSourceHash -ceq $sourceSnapshotSha256
if (-not $sourceStable) {
    throw 'Benchmark source hashes changed during the Player build.'
}
$initialPayload =
    Get-GpuBenchmarkPlayerPayload -PlayerPath $resolvedPlayerPath
$runnerConfig['sourceHashesStableAcrossBuild'] = $sourceStable
$runnerConfig['playerPayload'] = $initialPayload
$initialPayload | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath (
        Join-Path $outputRoot 'player-payload-manifest.json') -Encoding utf8

$matrixRows = [Collections.Generic.List[object]]::new()
$summaryRows = [Collections.Generic.List[object]]::new()
$playerRuns = [Collections.Generic.List[object]]::new()
foreach ($scenario in $scenarios) {
    if ([int64]$scenario.instanceCount * [int64]$scenario.viewCount -gt
        16776960L) {
        throw "Scenario '$($scenario.scenarioId)' exceeds capacity."
    }
    $scenarioRoot = Join-Path $outputRoot $scenario.scenarioId
    New-Item -ItemType Directory -Path $scenarioRoot -Force | Out-Null
    $playerLog = Join-Path $scenarioRoot 'player.log'
    $arguments = @(
        '-batchmode',
        '-force-d3d12',
        '-force-device-index', [string]$DeviceIndex,
        '-screen-fullscreen', '0',
        '-screen-width', '640',
        '-screen-height', '360',
        '-gpu-driven-instance-benchmark',
        '-gpu-driven-instance-benchmark-mode',
            (Quote-ProcessArgument $BenchmarkMode),
        '-gpu-driven-instance-report-dir',
            (Quote-ProcessArgument $scenarioRoot),
        '-gpu-driven-instance-scenario-id',
            (Quote-ProcessArgument $scenario.scenarioId),
        '-gpu-driven-instance-visibility',
            (Quote-ProcessArgument $scenario.visibility),
        '-gpu-driven-instance-super-rounds', [string]$SuperRounds,
        '-gpu-driven-instance-warmup-frames', [string]$WarmupFrames,
        '-gpu-driven-instance-sample-frames', [string]$SampleFrames,
        '-gpu-driven-instance-cooldown-frames', [string]$CooldownFrames,
        '-gpu-driven-instance-instance-count',
            [string]$scenario.instanceCount,
        '-gpu-driven-instance-view-count', [string]$scenario.viewCount,
        '-gpu-driven-instance-seed', [string]$scenario.seed,
        '-gpu-driven-instance-dispatches-per-frame',
            [string]$DispatchesPerFrame,
        '-gpu-driven-instance-validation-timeout-seconds',
            [string]$ValidationTimeoutSeconds,
        '-gpu-driven-instance-require-complete-gpu-timings', '1',
        '-gpu-driven-instance-build-commit', $gitCommit,
        '-gpu-driven-instance-runtime-shader-sha256',
            $runtimeShaderSha256,
        '-gpu-driven-instance-reference-shader-sha256',
            $binningShaderSha256,
        '-gpu-driven-instance-runtime-api-sha256',
            $runtimeApiSha256,
        '-logFile', (Quote-ProcessArgument $playerLog)
    )
    Write-Host (
        "Running '$($scenario.scenarioId)': " +
        "$($scenario.visibility), N=$($scenario.instanceCount), " +
        "views=$($scenario.viewCount)")
    $player = Start-Process `
        -FilePath $resolvedPlayerPath `
        -ArgumentList $arguments `
        -WorkingDirectory $projectRoot `
        -WindowStyle Hidden `
        -PassThru
    if (-not $player.WaitForExit($PlayerTimeoutMinutes * 60 * 1000)) {
        $player.Kill()
        $player.WaitForExit()
        throw "Scenario '$($scenario.scenarioId)' timed out."
    }
    if ($player.ExitCode -ne 0) {
        throw "Scenario '$($scenario.scenarioId)' failed; see $playerLog"
    }

    $runSummaryPath = Join-Path $scenarioRoot 'run-summary.txt'
    $configPath = Join-Path $scenarioRoot 'config.json'
    $rawPath = Join-Path $scenarioRoot 'raw-frames.csv'
    $blockPath = Join-Path $scenarioRoot 'block-summary.csv'
    $validationPath = Join-Path $scenarioRoot 'validation.csv'
    foreach ($path in @(
            $runSummaryPath, $configPath, $rawPath, $blockPath,
            $validationPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Scenario output is incomplete: $path"
        }
    }
    $run = Read-KeyValueFile -Path $runSummaryPath
    if (-not $run.ContainsKey('mainThreadAllocationRows') -or
        -not $run.ContainsKey('mainThreadAllocatedBytes') -or
        [int]$run.passed -ne 1 -or $run.status -cne 'completed' -or
        [string]$run.benchmarkMode -cne $BenchmarkMode -or
        [string]$run.visibilityLayout -cne $expectedVisibilityLayout -or
        [int]$run.validationFailures -ne 0 -or
        [int]$run.gpuRegionTimingComplete -ne 1 -or
        [int]$run.nativeTimestampPendingRows -ne 0 -or
        [int]$run.mainThreadAllocationRows -ne 0 -or
        [int64]$run.mainThreadAllocatedBytes -ne 0L) {
        throw "Scenario quality gate failed: $($scenario.scenarioId)"
    }
    $config = Get-Content -LiteralPath $configPath -Raw |
        ConvertFrom-Json
    $expectedClusterCount = if ($hierarchicalMode) {
        [int][Math]::Ceiling([double]$scenario.instanceCount / 64.0)
    }
    else {
        0
    }
    $expectedClusterBytes = [int64]$expectedClusterCount * 32L
    $expectedHierarchyStatisticsBytes = if ($hierarchicalMode) {
        12L
    }
    else {
        0L
    }
    $expectedCoarseVisibleClusterViewCount = 0L
    $expectedCandidateInstanceViewCount = 0L
    $expectedHierarchicalVisiblePairCount = 0L
    if ($hierarchicalMode) {
        $expectedStatisticsFields = @(
            'expectedCoarseVisibleClusterViewCount',
            'expectedCandidateInstanceViewCount',
            'expectedHierarchicalVisiblePairCount')
        $missingStatisticsFields = @(
            $expectedStatisticsFields | Where-Object {
                $_ -notin @($config.PSObject.Properties.Name)
            })
        if ($missingStatisticsFields.Count -ne 0) {
            throw (
                "Scenario expected hierarchy statistics are missing: " +
                ($missingStatisticsFields -join ', '))
        }
        $expectedCoarseVisibleClusterViewCount =
            [int64]$config.expectedCoarseVisibleClusterViewCount
        $expectedCandidateInstanceViewCount =
            [int64]$config.expectedCandidateInstanceViewCount
        $expectedHierarchicalVisiblePairCount =
            [int64]$config.expectedHierarchicalVisiblePairCount
        $visiblePairNumerator =
            [int64]$scenario.instanceCount *
            [int64]$scenario.viewCount *
            [int]([string]$scenario.visibility).Substring(7)
        $oracleVisiblePairCount =
            [int64][Math]::Floor([double]$visiblePairNumerator / 100.0)
        $flatCandidatePairCount =
            [int64]$scenario.instanceCount * [int64]$scenario.viewCount
        if ($expectedCoarseVisibleClusterViewCount -lt 0L -or
            $expectedCoarseVisibleClusterViewCount -gt
                ([int64]$expectedClusterCount * [int64]$scenario.viewCount) -or
            $expectedCandidateInstanceViewCount -lt
                $expectedCoarseVisibleClusterViewCount -or
            $expectedCandidateInstanceViewCount -gt
                ($expectedCoarseVisibleClusterViewCount * 64L) -or
            $expectedCandidateInstanceViewCount -lt
                $expectedHierarchicalVisiblePairCount -or
            $expectedCandidateInstanceViewCount -gt $flatCandidatePairCount -or
            $expectedHierarchicalVisiblePairCount -ne
                $oracleVisiblePairCount) {
            throw (
                "Scenario expected hierarchy statistics are invalid: " +
                $scenario.scenarioId)
        }
    }
    if ([string]$config.buildCommit -cne $gitCommit -or
        [string]$config.unityVersion -cne $projectUnityVersion -or
        [string]$config.benchmarkMode -cne $BenchmarkMode -or
        [string]$config.visibilityLayout -cne
            $expectedVisibilityLayout -or
        [string]$config.baselineId -cne ($baselineVariantId + '-v1') -or
        [string]$config.directId -cne ($optimizedVariantId + '-v1') -or
        [int]$config.clusterCount -ne $expectedClusterCount -or
        [int64]$config.clusterBytes -ne $expectedClusterBytes -or
        [int64]$config.hierarchyStatisticsBytes -ne
            $expectedHierarchyStatisticsBytes -or
        [int]$config.instancesPerCluster -ne $(if ($hierarchicalMode) {
            64
        }
        else {
            0
        }) -or
        [string]::IsNullOrWhiteSpace(
            [string]$config.statisticsSemantics) -or
        [string]$config.runtimeShaderSha256 -cne
            $runtimeShaderSha256 -or
        [string]$config.referenceShaderSha256 -cne
            $binningShaderSha256 -or
        [string]$config.runtimeApiSha256 -cne $runtimeApiSha256) {
        throw "Scenario provenance mismatch: $($scenario.scenarioId)"
    }
    $validation = @(Import-Csv -LiteralPath $validationPath)
    if ($validation.Count -ne 4 -or
        @($validation | Where-Object { [int]$_.passed -ne 1 }).Count -ne 0) {
        throw "Scenario validation failed: $($scenario.scenarioId)"
    }
    $baselineValidation = @($validation | Where-Object {
        $_.variant -ceq $baselineVariantId
    })
    $optimizedValidation = @($validation | Where-Object {
        $_.variant -ceq $optimizedVariantId
    })
    $baselineValidationPhases = @(
        $baselineValidation | Select-Object -ExpandProperty phase -Unique)
    $optimizedValidationPhases = @(
        $optimizedValidation | Select-Object -ExpandProperty phase -Unique)
    if ($baselineValidation.Count -ne 2 -or
        $optimizedValidation.Count -ne 2 -or
        $baselineValidationPhases.Count -ne 2 -or
        $optimizedValidationPhases.Count -ne 2 -or
        @($baselineValidationPhases | Where-Object {
            $_ -notin @('warmup', 'final')
        }).Count -ne 0 -or
        @($optimizedValidationPhases | Where-Object {
            $_ -notin @('warmup', 'final')
        }).Count -ne 0) {
        throw "Scenario validation identities differ: $($scenario.scenarioId)"
    }
    $validatedCoarseVisibleClusterViewCount = 0L
    $validatedCandidateInstanceViewCount = 0L
    $validatedHierarchicalVisiblePairCount = 0L
    if ($hierarchicalMode) {
        $validationHashes = @(
            $validation |
                Select-Object -ExpandProperty resultHash -Unique)
        if ($validationHashes.Count -ne 1 -or
            @($validation | Where-Object {
                $_.message -notlike '*canonical membership*'
            }).Count -ne 0 -or
            @($optimizedValidation | Where-Object {
                [int64]$_.coarseVisibleClusterViewCount -ne
                    $expectedCoarseVisibleClusterViewCount -or
                [int64]$_.candidateInstanceViewCount -ne
                    $expectedCandidateInstanceViewCount -or
                [int64]$_.hierarchicalVisiblePairCount -ne
                    $expectedHierarchicalVisiblePairCount
            }).Count -ne 0) {
            throw (
                "Flat/hierarchical oracle equivalence failed: " +
                $scenario.scenarioId)
        }
        $validatedCoarseVisibleClusterViewCount =
            [int64]$optimizedValidation[0].coarseVisibleClusterViewCount
        $validatedCandidateInstanceViewCount =
            [int64]$optimizedValidation[0].candidateInstanceViewCount
        $validatedHierarchicalVisiblePairCount =
            [int64]$optimizedValidation[0].hierarchicalVisiblePairCount
    }
    $raw = @(Import-Csv -LiteralPath $rawPath)
    $requiredRawFields = @(
        'nativeTimestampStatus',
        'gpuRegionElapsedMs',
        'frameMs',
        'enqueueCpuMs',
        'mainThreadAllocatedBytes',
        'measurementReadbackBytes',
        'timestampInstrumentationReadbackBytes')
    $missingRawFields = if ($raw.Count -eq 0) {
        $requiredRawFields
    }
    else {
        @($requiredRawFields | Where-Object {
            $_ -notin @($raw[0].PSObject.Properties.Name)
        })
    }
    if ($missingRawFields.Count -ne 0) {
        throw (
            "Scenario raw evidence fields are missing: " +
            ($missingRawFields -join ', '))
    }
    $expectedRows = ($SuperRounds * 4 + 2) * $SampleFrames
    if ($raw.Count -ne $expectedRows -or
        @($raw | Where-Object {
            $_.nativeTimestampStatus -cne 'ready' -or
            [int64]$_.nativeTimestampElapsedTicks -lt 0 -or
            [int64]$_.nativeTimestampFrequency -le 0 -or
            [int64]$_.nativeTimestampEndTicks -lt
                [int64]$_.nativeTimestampBeginTicks -or
            -not (Is-FiniteNumber $_.gpuRegionElapsedMs) -or
            -not (Is-FiniteNumber $_.frameMs) -or
            -not (Is-FiniteNumber $_.enqueueCpuMs) -or
            (Number $_.gpuRegionElapsedMs) -lt 0.0 -or
            ($_.blockType -ceq 'measurement' -and
                ([int64]$_.nativeTimestampElapsedTicks -le 0 -or
                 (Number $_.gpuRegionElapsedMs) -le 0.0)) -or
            (Number $_.frameMs) -le 0.0 -or
            (Number $_.enqueueCpuMs) -lt 0.0 -or
            [int64]$_.mainThreadAllocatedBytes -ne 0L -or
            [int64]$_.measurementReadbackBytes -ne 0 -or
            [int64]$_.timestampInstrumentationReadbackBytes -ne 16
        }).Count -ne 0) {
        throw "Scenario timestamp rows are incomplete: $($scenario.scenarioId)"
    }
    $blocks = @(Import-Csv -LiteralPath $blockPath)
    $requiredBlockFields = @(
        'mainThreadAllocationRows',
        'mainThreadAllocatedBytes')
    $missingBlockFields = if ($blocks.Count -eq 0) {
        $requiredBlockFields
    }
    else {
        @($requiredBlockFields | Where-Object {
            $_ -notin @($blocks[0].PSObject.Properties.Name)
        })
    }
    if ($missingBlockFields.Count -ne 0) {
        throw (
            "Scenario block allocation fields are missing: " +
            ($missingBlockFields -join ', '))
    }
    $expectedBlockCount = $SuperRounds * 4 + 2
    if ($blocks.Count -ne $expectedBlockCount -or
        @($blocks | Where-Object {
            [int]$_.samples -ne $SampleFrames -or
            [int]$_.gpuRegionValidSamples -ne $SampleFrames -or
            [int]$_.fenceSupported -ne 1 -or
            [int]$_.fencePassed -ne 1 -or
            [int]$_.mainThreadAllocationRows -ne 0 -or
            [int64]$_.mainThreadAllocatedBytes -ne 0L -or
            [int64]$_.measurementReadbackBytes -ne 0 -or
            [int64]$_.timestampInstrumentationReadbackBytes -ne
                (16L * $SampleFrames)
        }).Count -ne 0) {
        throw "Scenario block evidence is incomplete: $($scenario.scenarioId)"
    }
    $baselineRows = @($raw | Where-Object {
        $_.variant -ceq $baselineVariantId
    })
    $optimizedRows = @($raw | Where-Object {
        $_.variant -ceq $optimizedVariantId
    })
    $expectedCaseRows = $SuperRounds * 2 * $SampleFrames
    if ($baselineRows.Count -ne $expectedCaseRows -or
        $optimizedRows.Count -ne $expectedCaseRows) {
        throw "Scenario case sample counts differ: $($scenario.scenarioId)"
    }
    $baselineTimes = [double[]]@(
        $baselineRows | ForEach-Object { Number $_.gpuRegionElapsedMs })
    $optimizedTimes = [double[]]@(
        $optimizedRows | ForEach-Object { Number $_.gpuRegionElapsedMs })
    $baselineFrameTimes = [double[]]@(
        $baselineRows | ForEach-Object { Number $_.frameMs })
    $optimizedFrameTimes = [double[]]@(
        $optimizedRows | ForEach-Object { Number $_.frameMs })
    $baselineEnqueueTimes = [double[]]@(
        $baselineRows | ForEach-Object { Number $_.enqueueCpuMs })
    $optimizedEnqueueTimes = [double[]]@(
        $optimizedRows | ForEach-Object { Number $_.enqueueCpuMs })
    $pairSpeedups = [Collections.Generic.List[double]]::new()
    foreach ($pairIndex in 1..($SuperRounds * 2)) {
        $pairBaseline = [double[]]@(
            $baselineRows |
                Where-Object { [int]$_.pairIndex -eq $pairIndex } |
                ForEach-Object { Number $_.gpuRegionElapsedMs })
        $pairOptimized = [double[]]@(
            $optimizedRows |
                Where-Object { [int]$_.pairIndex -eq $pairIndex } |
                ForEach-Object { Number $_.gpuRegionElapsedMs })
        if ($pairBaseline.Count -ne $SampleFrames -or
            $pairOptimized.Count -ne $SampleFrames) {
            throw "Pair $pairIndex is incomplete: $($scenario.scenarioId)"
        }
        $pairSpeedups.Add(
            (ImprovementPercent `
                -Baseline (Mean $pairBaseline) `
                -Optimized (Mean $pairOptimized)))
    }
    $baselineMean = Mean $baselineTimes
    $optimizedMean = Mean $optimizedTimes
    $pairMedian = Percentile ([double[]]$pairSpeedups) 0.50
    $pairMinimum =
        [double](($pairSpeedups | Measure-Object -Minimum).Minimum)
    $pairMaximum =
        [double](($pairSpeedups | Measure-Object -Maximum).Maximum)
    $baselineGpuP95 = Percentile $baselineTimes 0.95
    $optimizedGpuP95 = Percentile $optimizedTimes 0.95
    $gpuP95Speedup =
        ImprovementPercent $baselineGpuP95 $optimizedGpuP95
    $baselineFrameP99 = Percentile $baselineFrameTimes 0.99
    $optimizedFrameP99 = Percentile $optimizedFrameTimes 0.99
    $frameP99Regression =
        -1.0 * (ImprovementPercent $baselineFrameP99 $optimizedFrameP99)
    $baselineEnqueueP99 = Percentile $baselineEnqueueTimes 0.99
    $optimizedEnqueueP99 = Percentile $optimizedEnqueueTimes 0.99
    $enqueueP99Regression =
        -1.0 * (ImprovementPercent `
            $baselineEnqueueP99 `
            $optimizedEnqueueP99)
    $nativeGpuP95NonRegression = $optimizedGpuP95 -le $baselineGpuP95
    $frameP99WithinGuardrail =
        $optimizedFrameP99 -le ($baselineFrameP99 * 1.05)
    $enqueueP99WithinGuardrail =
        $baselineEnqueueP99 -gt 0.0 -and
        $optimizedEnqueueP99 -le ($baselineEnqueueP99 * 1.05)
    $tailGuardrailsPassed =
        $nativeGpuP95NonRegression -and
        $frameP99WithinGuardrail -and
        $enqueueP99WithinGuardrail
    $decision = if ($pairMedian -ge 1.0 -and
        $pairMinimum -gt 0.0 -and
        $tailGuardrailsPassed) {
        'material-improvement'
    }
    elseif ([Math]::Abs($pairMedian) -lt 1.0 -and
        $tailGuardrailsPassed) {
        'parity'
    }
    else {
        'regression-or-unstable'
    }
    $summaryRows.Add([pscustomobject][ordered]@{
        scenarioId = $scenario.scenarioId
        benchmarkMode = $BenchmarkMode
        visibilityLayout = $expectedVisibilityLayout
        baselineVariant = $baselineVariantId
        optimizedVariant = $optimizedVariantId
        visibility = $scenario.visibility
        visibilityPercent =
            [int]([string]$scenario.visibility).Substring(7)
        instanceCount = $scenario.instanceCount
        viewCount = $scenario.viewCount
        clusterCount = $expectedClusterCount
        clusterBytes = $expectedClusterBytes
        expectedCoarseVisibleClusterViewCount =
            $expectedCoarseVisibleClusterViewCount
        expectedCandidateInstanceViewCount =
            $expectedCandidateInstanceViewCount
        expectedHierarchicalVisiblePairCount =
            $expectedHierarchicalVisiblePairCount
        coarseVisibleClusterViewCount =
            $validatedCoarseVisibleClusterViewCount
        candidateInstanceViewCount =
            $validatedCandidateInstanceViewCount
        hierarchicalVisiblePairCount =
            $validatedHierarchicalVisiblePairCount
        hierarchyStatisticValidationRows = if ($hierarchicalMode) {
            2
        }
        else {
            0
        }
        candidateReductionPercent = if ($hierarchicalMode) {
            ImprovementPercent `
                ([double]([int64]$scenario.instanceCount *
                    [int64]$scenario.viewCount)) `
                ([double]$validatedCandidateInstanceViewCount)
        }
        else {
            0.0
        }
        pairCount = $SuperRounds * 2
        samplesPerVariant = $expectedCaseRows
        baselineGpuMeanMs = $baselineMean
        optimizedGpuMeanMs = $optimizedMean
        meanSpeedupPercent =
            ImprovementPercent $baselineMean $optimizedMean
        baselineGpuP50Ms = Percentile $baselineTimes 0.50
        optimizedGpuP50Ms = Percentile $optimizedTimes 0.50
        p50SpeedupPercent = ImprovementPercent `
            (Percentile $baselineTimes 0.50) `
            (Percentile $optimizedTimes 0.50)
        baselineGpuP95Ms = $baselineGpuP95
        optimizedGpuP95Ms = $optimizedGpuP95
        p95SpeedupPercent = $gpuP95Speedup
        baselineFrameP99Ms = $baselineFrameP99
        optimizedFrameP99Ms = $optimizedFrameP99
        frameP99RegressionPercent = $frameP99Regression
        baselineEnqueueP99Ms = $baselineEnqueueP99
        optimizedEnqueueP99Ms = $optimizedEnqueueP99
        enqueueP99RegressionPercent = $enqueueP99Regression
        nativeGpuP95NonRegression = [bool]$nativeGpuP95NonRegression
        frameP99Within5Percent = [bool]$frameP99WithinGuardrail
        enqueueP99Within5Percent = [bool]$enqueueP99WithinGuardrail
        tailGuardrailsPassed = [bool]$tailGuardrailsPassed
        pairedMedianSpeedupPercent = $pairMedian
        pairedMinSpeedupPercent = $pairMinimum
        pairedMaxSpeedupPercent = $pairMaximum
        decision = $decision
        validationRows = $validation.Count
        validationFailures = 0
        nativeTimestampReadyRows = [int]$run.nativeTimestampReadyRows
        mainThreadAllocationRows = [int]$run.mainThreadAllocationRows
        mainThreadAllocatedBytes = [int64]$run.mainThreadAllocatedBytes
        processId = [int]$run.processId
    })
    $matrixRows.Add([pscustomobject][ordered]@{
        scenarioId = $scenario.scenarioId
        benchmarkMode = $BenchmarkMode
        visibilityLayout = $expectedVisibilityLayout
        visibility = $scenario.visibility
        instanceCount = $scenario.instanceCount
        viewCount = $scenario.viewCount
        seed = $scenario.seed
        processId = [int]$run.processId
        status = $run.status
        passed = $run.passed
        rawSampleCount = $run.rawSampleCount
    })
    $playerRuns.Add([ordered]@{
        scenarioId = $scenario.scenarioId
        processId = [int]$run.processId
        exitCode = $player.ExitCode
        reportDirectory = $scenarioRoot
        playerLog = $playerLog
    })
}

$matrixRows | Export-Csv -LiteralPath (
    Join-Path $outputRoot 'matrix.csv') -NoTypeInformation -Encoding utf8
$summaryRows | Export-Csv -LiteralPath (
    Join-Path $outputRoot 'matrix-summary.csv') `
    -NoTypeInformation -Encoding utf8
$totalRawEvidenceRows = [int64](
    ($summaryRows |
        Measure-Object -Property nativeTimestampReadyRows -Sum).Sum)
$totalMainThreadAllocationRows = [int64](
    ($summaryRows |
        Measure-Object -Property mainThreadAllocationRows -Sum).Sum)
$totalMainThreadAllocatedBytes = [int64](
    ($summaryRows |
        Measure-Object -Property mainThreadAllocatedBytes -Sum).Sum)

$reportLines = [Collections.Generic.List[string]]::new()
$reportLines.Add($(if ($hierarchicalMode) {
    '# GPU-driven hierarchical multi-view culling benchmark'
}
else {
    '# GPU-driven visible-only benchmark'
}))
$reportLines.Add('')
$reportLines.Add(
    "Commit: ``$gitCommit``  ")
if ($hierarchicalMode) {
    $reportLines.Add(
        "Mode: ``$BenchmarkMode``; layout: ``$expectedVisibilityLayout``.  ")
}
$reportLines.Add(
    "Protocol: same-process paired ABBA/BAAB, native D3D12 timestamps, " +
    "$SampleFrames samples per block.  ")
$reportLines.Add(
    'Correctness: CPU oracle before and after measurement; no timed readback; ' +
    'zero main-thread allocated bytes in every sampled row and block.')
$reportLines.Add(
    "Allocation evidence: $totalRawEvidenceRows raw rows, " +
    "$totalMainThreadAllocationRows allocation rows, " +
    "$totalMainThreadAllocatedBytes allocated bytes.  ")
$reportLines.Add(
    'Material gate: paired median >= 1% with every pair positive, native GPU ' +
    'P95 non-regression, and no more than 5% regression in frame P99 or ' +
    'enqueue P99.')
$reportLines.Add('')
$reportLines.Add(
    "| Visible | $baselineReportLabel mean (ms) | " +
    "$optimizedReportLabel mean (ms) | " +
    'Mean speedup | Paired median | GPU P95 speedup | Frame P99 regression | ' +
    'Enqueue P99 regression | Pair range | Decision |')
$reportLines.Add(
    '|---:|---:|---:|---:|---:|---:|---:|---:|---:|:---|')
foreach ($row in $summaryRows) {
    $reportLines.Add(
        (('| {0}% | {1:F4} | {2:F4} | {3:F2}% | {4:F2}% | ' +
          '{5:F2}% | {6:F2}% | {7:F2}% | {8:F2}% to {9:F2}% | {10} |') -f
            $row.visibilityPercent,
            $row.baselineGpuMeanMs,
            $row.optimizedGpuMeanMs,
            $row.meanSpeedupPercent,
            $row.pairedMedianSpeedupPercent,
            $row.p95SpeedupPercent,
            $row.frameP99RegressionPercent,
            $row.enqueueP99RegressionPercent,
            $row.pairedMinSpeedupPercent,
            $row.pairedMaxSpeedupPercent,
            $row.decision))
}
$reportLines.Add('')
if ($hierarchicalMode) {
    $reportLines.Add(
        '| Visible | Coarse cluster-view pairs | Candidate instance-view ' +
        'pairs | Visible pairs | Candidate reduction vs flat |')
    $reportLines.Add('|---:|---:|---:|---:|---:|')
    foreach ($row in $summaryRows) {
        $reportLines.Add(
            (('| {0}% | {1} | {2} | {3} | {4:F2}% |') -f
                $row.visibilityPercent,
                $row.coarseVisibleClusterViewCount,
                $row.candidateInstanceViewCount,
                $row.hierarchicalVisiblePairCount,
                $row.candidateReductionPercent))
    }
    $reportLines.Add('')
}
$material = @($summaryRows | Where-Object {
    [string]$_.decision -ceq 'material-improvement'
})
if ($material.Count -eq 0) {
    if ($hierarchicalMode) {
        $reportLines.Add(
            'Decision: do not select hierarchical culling by default; this ' +
            'matrix did not show a material, consistently positive paired ' +
            'result. The explicit hierarchical API remains available.')
    }
    else {
        $reportLines.Add(
            'Decision: retain `CulledTail` as the default; this matrix did not ' +
            'show a material, consistently positive paired result.')
    }
}
else {
    $materialCells = @(
        $material |
            Sort-Object visibilityPercent |
            ForEach-Object { "$($_.visibilityPercent)%" }) -join ', '
    if ($hierarchicalMode) {
        $reportLines.Add(
            "Decision evidence: ``$optimizedReportLabel`` had at least 1% " +
            'paired-median speedup with every pair positive and passed all ' +
            "P95/P99 guardrails in: " +
            "$materialCells. Other cells are parity or regression. The " +
            'public API remains explicit; no runtime policy is inferred ' +
            'outside this matrix.')
    }
    else {
        $reportLines.Add(
            "Decision evidence: `VisibleOnly` had at least 1% paired-median " +
            'speedup with every pair positive and passed all P95/P99 ' +
            "guardrails in these measured cells: $materialCells. Other cells " +
            'are parity or regression. The public API remains explicit; no ' +
            'runtime policy is inferred outside this matrix.')
    }
}
$reportLines | Set-Content -LiteralPath (
    Join-Path $outputRoot 'BENCHMARK_REPORT.md') -Encoding utf8

$finalGit = Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
$finalPayload =
    Get-GpuBenchmarkPlayerPayload -PlayerPath $resolvedPlayerPath
$payloadStable =
    [string]$initialPayload.sha256 -ceq [string]$finalPayload.sha256 -and
    [int]$initialPayload.fileCount -eq [int]$finalPayload.fileCount -and
    [int64]$initialPayload.lengthBytes -eq [int64]$finalPayload.lengthBytes
if ($finalGit.head -ine $gitCommit -or [bool]$finalGit.dirty) {
    throw 'Git state changed during benchmark execution.'
}
if (-not $payloadStable) {
    throw 'Player payload changed during benchmark execution.'
}
$runnerConfig['gitFinal'] = $finalGit
$runnerConfig['playerPayload'] = $finalPayload
$runnerConfig['playerPayloadStableThroughRun'] = $payloadStable
$runnerConfig['playerRuns'] = @($playerRuns)
$runnerConfig['formalContractSatisfied'] = [bool]$FormalAcceptanceMode
$runnerConfig['runnerConfigFinalized'] = $true
$runnerConfig['finalizedUtc'] =
    (Get-Date).ToUniversalTime().ToString('o')
$runnerConfig | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath $runnerConfigPath -Encoding utf8
$finalPayload | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath (
        Join-Path $outputRoot 'player-payload-manifest.json') -Encoding utf8

Write-Host "Completed GPU-driven instance benchmark: $outputRoot"
Write-Host "Summary: $(Join-Path $outputRoot 'matrix-summary.csv')"
