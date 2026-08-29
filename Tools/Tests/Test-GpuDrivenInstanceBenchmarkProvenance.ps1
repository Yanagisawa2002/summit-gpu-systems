[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolsRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Split-Path -Parent $toolsRoot
$runnerPath =
    Join-Path $toolsRoot 'Run-GpuDrivenInstanceBenchmark.ps1'
$editModeRunnerPath =
    Join-Path $toolsRoot 'Run-UnityEditModeTests.ps1'
$controllerPath = Join-Path $projectRoot (
    'Assets\GpuDrivenInstanceBenchmark\Runtime\' +
    'GpuDrivenInstanceBenchmarkController.cs')
$hierarchicalGeneratorPath = Join-Path $projectRoot (
    'Assets\GpuDrivenInstanceBenchmark\Runtime\' +
    'GpuDrivenInstanceHierarchicalInputGenerator.cs')
if (-not (Test-Path -LiteralPath $runnerPath -PathType Leaf)) {
    throw "Runner is missing: $runnerPath"
}
if (-not (Test-Path -LiteralPath $editModeRunnerPath -PathType Leaf)) {
    throw "EditMode runner is missing: $editModeRunnerPath"
}
foreach ($path in @($controllerPath, $hierarchicalGeneratorPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Benchmark evidence source is missing: $path"
    }
}
$runner = Get-Content -LiteralPath $runnerPath -Raw
$editModeRunner = Get-Content -LiteralPath $editModeRunnerPath -Raw
$controller = Get-Content -LiteralPath $controllerPath -Raw
$hierarchicalGenerator =
    Get-Content -LiteralPath $hierarchicalGeneratorPath -Raw

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Label
    )
    if (-not $Text.Contains($Needle)) {
        throw "$Label is missing required token: $Needle"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Label
    )
    if ($Text.Contains($Needle)) {
        throw "$Label contains forbidden token: $Needle"
    }
}

foreach ($requirement in @(
        @('$ErrorActionPreference = ''Stop''', 'Fail-fast errors'),
        @('Set-StrictMode -Version Latest', 'Strict mode'),
        @("[ValidateSet('filtered-binning', 'hierarchical-culling')]",
            'Explicit benchmark mode'),
        @('benchmarkMode = $BenchmarkMode',
            'Mode frozen into runner receipt'),
        @('-SkipBuild is retained for command-line compatibility',
            'All-mode fresh-build fail-fast'),
        @('gpu-driven-hierarchical-culling-v1',
            'Named hierarchical formal contract'),
        @('Summit.GpuDrivenInstance.Benchmark.Tests.GpuDrivenInstanceHierarchicalInputGeneratorTests',
            'Exact hierarchical benchmark fixture identity'),
        @('Summit.GpuDrivenInstances.Tests.GpuDrivenInstanceHierarchicalPipelineIntegrationTests',
            'Exact hierarchical pipeline fixture identity'),
        @('Summit.GpuDirectBinning.Tests.GpuDirectSpatialBinnerPrecountedIntegrationTests',
            'Exact precounted binner fixture identity'),
        @('-gpu-driven-instance-benchmark-mode',
            'Player mode attribution'),
        @('Get-GpuBenchmarkGitSnapshot', 'Git snapshot'),
        @('Benchmark requires a clean worktree', 'Clean-tree gate'),
        @('sourceHashesStableAcrossBuild', 'Source stability receipt'),
        @('Get-VerifiedEditModeReceipt',
            'Formal EditMode sidecar verification'),
        @('editmode-results.xml.receipt.json',
            'Retained EditMode receipt'),
        @('unityExecutableSha256', 'Unity executable receipt binding'),
        @('projectVersionSha256', 'ProjectVersion receipt binding'),
        @('formal EditMode tests did not use the Direct3D 12 graphics path',
            'Formal D3D12 EditMode receipt gate'),
        @("@('testFilter', [string]`$receipt.testFilter, '')",
            'Formal full-suite empty-filter gate'),
        @('$config.unityVersion -cne $projectUnityVersion',
            'Built Player Unity version gate'),
        @('Get-GpuBenchmarkPlayerPayload', 'Payload manifest'),
        @('playerPayloadStableThroughRun', 'Payload stability receipt'),
        @('-gpu-driven-instance-require-complete-gpu-timings',
            'Complete native timing requirement'),
        @('nativeTimestampStatus -cne ''ready''',
            'Per-sample native timestamp gate'),
        @('validation.Count -ne 4', 'Pre/post validation cardinality'),
        @('runtimeShaderSha256', 'Classification shader provenance'),
        @('binningShaderSha256', 'Binning shader provenance'),
        @('runtimeApiSha256', 'Runtime API provenance'),
        @('culled-tail-portable', 'Baseline identity'),
        @('visible-only-discard-key-portable', 'Optimized identity'),
        @('flat-visible-only-portable', 'Hierarchical baseline identity'),
        @('hierarchical-visible-only-portable',
            'Hierarchical optimized identity'),
        @('visible5-n1048576-v4', 'Five-percent cell'),
        @('visible25-n1048576-v4', 'Twenty-five-percent cell'),
        @('visible75-n1048576-v4', 'Seventy-five-percent cell'),
        @('visible100-n1048576-v4', 'Full-visibility control cell'),
        @('seeded-coprime-permutation-v1',
            'Dispersed visibility layout gate'),
        @('spatial-clustered-multiview-64-v2',
            'Clustered visibility layout gate'),
        @('GpuDrivenInstanceHierarchicalInputGeneratorTests',
            'Hierarchical benchmark test receipt'),
        @('GpuInstanceClusterBuilderTests',
            'Cluster builder test receipt'),
        @('GpuDrivenInstanceHierarchicalPipelineIntegrationTests',
            'Hierarchical pipeline test receipt'),
        @('GpuDirectSpatialBinnerPrecountedIntegrationTests',
            'Precounted binner test receipt'),
        @('$config.benchmarkMode -cne $BenchmarkMode',
            'Built Player mode receipt gate'),
        @('$config.clusterCount -ne $expectedClusterCount',
            'Cluster count accounting gate'),
        @('$config.clusterBytes -ne $expectedClusterBytes',
            'Cluster byte accounting gate'),
        @('expectedCoarseVisibleClusterViewCount',
            'Exact coarse statistic gate'),
        @('expectedCandidateInstanceViewCount',
            'Exact candidate statistic gate'),
        @('expectedHierarchicalVisiblePairCount',
            'Exact visible-pair statistic gate'),
        @('candidateReductionPercent',
            'Candidate reduction reporting'),
        @('Flat/hierarchical oracle equivalence failed',
            'Shared CPU oracle gate'),
        @('mainThreadAllocationRows',
            'Zero-allocation row count gate'),
        @('mainThreadAllocatedBytes',
            'Zero-allocation byte gate'),
        @('$missingRawFields = @(',
            'Strict-mode raw-field array normalization'),
        @('$missingBlockFields = @(',
            'Strict-mode block-field array normalization'),
        @('Allocation evidence:',
            'Zero-allocation report evidence'),
        @('[int64]$_.measurementReadbackBytes -ne 0',
            'Zero timed readback gate'),
        @('[int]$_.fencePassed -ne 1', 'Per-block fence gate'),
        @('sampleFrames = 900', 'Formal sample count'),
        @('same-process paired ABBA/BAAB', 'Paired protocol report'),
        @('pairedMedianSpeedupPercent', 'Paired effect estimate'),
        @('nativeGpuP95NonRegression',
            'Native GPU P95 non-regression gate'),
        @('frameP99Within5Percent',
            'Frame P99 five-percent guardrail'),
        @('enqueueP99Within5Percent',
            'Enqueue P99 five-percent guardrail'),
        @('guardrails in these measured cells:',
            'Enumerated filtered material cells'),
        @('formalContractSatisfied', 'Formal completion receipt'))) {
    Assert-Contains $runner $requirement[0] $requirement[1]
}

Assert-NotContains `
    -Text $runner `
    -Needle "'Summit.GpuDrivenInstance.Benchmark.Tests.' +" `
    -Label 'Hierarchical fixture identity array'
Assert-NotContains `
    -Text $runner `
    -Needle 'if (-not $SkipBuild)' `
    -Label 'Fresh-build runner'
Assert-NotContains `
    -Text $runner `
    -Needle 'through the measured' `
    -Label 'Filtered material-cell report'

foreach ($requirement in @(
        @('Set-StrictMode -Version Latest', 'EditMode strict mode'),
        @("@('status', '--porcelain=v1', '--untracked-files=all')",
            'Ignored-output-safe clean-tree snapshot'),
        @('Unity EditMode receipt requires a clean worktree',
            'Pre-test clean-tree gate'),
        @('Git state changed during Unity EditMode tests',
            'Post-test clean-tree gate'),
        @("receiptType = 'summit.unity-editmode-test'",
            'Named receipt schema'),
        @('projectUnityVersion =', 'Project version receipt'),
        @('unityExecutablePath =', 'Unity executable receipt'),
        @('unityExecutableSha256 =', 'Unity executable hash receipt'),
        @('resultsSha256 =', 'NUnit XML hash receipt'),
        @('gitCommit = [string]$gitStart.head', 'Tested commit receipt'),
        @('gitStateStable = $true', 'Stable Git state receipt'),
        @('$ResultsPath + ''.receipt.json''', 'Receipt sidecar naming'))) {
    Assert-Contains $editModeRunner $requirement[0] $requirement[1]
}

foreach ($requirement in @(
        @('spatial-clustered-multiview-64-v2',
            'Runtime hierarchical layout identity'),
        @('targetVisiblePairCount',
            'Exact multi-view visible-pair target'))) {
    Assert-Contains `
        $hierarchicalGenerator `
        $requirement[0] `
        $requirement[1]
}
foreach ($requirement in @(
        @('unityVersion = Application.unityVersion',
            'Actual Player Unity version'),
        @('expectedCoarseVisibleClusterViewCount',
            'Runtime expected coarse statistic'),
        @('expectedCandidateInstanceViewCount',
            'Runtime expected candidate statistic'),
        @('expectedHierarchicalVisiblePairCount',
            'Runtime expected visible statistic'),
        @('mainThreadAllocatedBytes',
            'Runtime raw allocation field'),
        @('mainThreadAllocationRows',
            'Runtime aggregate allocation field'))) {
    Assert-Contains $controller $requirement[0] $requirement[1]
}

$tokens = $null
$parseErrors = $null
[Management.Automation.Language.Parser]::ParseFile(
    $runnerPath,
    [ref]$tokens,
    [ref]$parseErrors) | Out-Null
if ($parseErrors.Count -ne 0) {
    throw "Runner has $($parseErrors.Count) PowerShell parse error(s)."
}

$tokens = $null
$parseErrors = $null
[Management.Automation.Language.Parser]::ParseFile(
    $editModeRunnerPath,
    [ref]$tokens,
    [ref]$parseErrors) | Out-Null
if ($parseErrors.Count -ne 0) {
    throw (
        "EditMode runner has $($parseErrors.Count) PowerShell parse error(s).")
}

$skipBuildRejected = $false
try {
    & $runnerPath -SkipBuild 2>$null
}
catch {
    $skipBuildRejected =
        $_.Exception.Message.Contains(
            '-SkipBuild is retained for command-line compatibility')
}
if (-not $skipBuildRejected) {
    throw '-SkipBuild did not fail before benchmark discovery/build work.'
}

Write-Host 'GPU-driven instance benchmark provenance contract validated.'
