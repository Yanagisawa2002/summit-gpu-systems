[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolsRoot = Split-Path -Parent $PSScriptRoot
$runnerPath =
    Join-Path $toolsRoot 'Run-GpuDrivenInstanceMacrobenchmark.ps1'
if (-not (Test-Path -LiteralPath $runnerPath -PathType Leaf)) {
    throw "Runner is missing: $runnerPath"
}
$runner = Get-Content -LiteralPath $runnerPath -Raw

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
        @('Get-GpuBenchmarkGitSnapshot', 'Git snapshot'),
        @('Benchmark requires a clean worktree', 'Clean-tree gate'),
        @('sourceHashesStableAcrossBuild', 'Source stability receipt'),
        @('Test-GpuDrivenInstanceMacrobenchmarkProvenance.ps1',
            'Runner-test source provenance'),
        @('Get-GpuBenchmarkPlayerPayload', 'Payload manifest'),
        @('playerPayloadStableThroughRun', 'Payload stability receipt'),
        @('GpuDrivenInstanceMacrobenchmarkBuild.PerformBuild',
            'Dedicated Player build entrypoint'),
        @('-gpu-driven-instance-macro-player-path',
            'Dedicated Player build output argument'),
        @('-gpu-driven-instance-macrobenchmark', 'Player enable flag'),
        @('-force-d3d12', 'D3D12 launch contract'),
        @("graphicsDeviceType -cne 'Direct3D12'",
            'D3D12 device receipt gate'),
        @('nativeTimestampAbiVersion -ne 2',
            'Native timestamp ABI gate'),
        @('-gpu-driven-instance-macro-require-complete-gpu-timings',
            'Complete native timing requirement'),
        @('-gpu-driven-instance-macro-require-complete-frame-timings',
            'Complete frame timing requirement'),
        @('nativeTimestampStatus -cne ''ready''',
            'Per-sample native timing gate'),
        @('frameTimingValid -ne 1', 'Per-sample frame timing gate'),
        @('frameTimingCaptureLatencyFrames -ne 4',
            'Fixed four-frame alignment gate'),
        @('measurementReadbackBytes -ne 0',
            'Zero measurement readback gate'),
        @('timedAllocationFree -ne 1', 'Timed allocation summary gate'),
        @('mainThreadAllocationRows -ne 0',
            'Timed allocation row gate'),
        @('mainThreadAllocatedBytes -ne 0',
            'Timed allocation byte gate'),
        @('completionFencesComplete -ne 1',
            'Completion-fence summary gate'),
        @('engineSubmissionWindowValid -eq 1',
            'Optional engine submission-window selection'),
        @("return 'unavailable'",
            'Unavailable optional evidence contract'),
        @('explicitBufferUploadBytes', 'Explicit buffer-upload evidence'),
        @('engineInstancePayloadBytes', 'Engine instance-payload evidence'),
        @('$validation.Count -ne 4',
            'Validation cardinality gate'),
        @('validationReadbackRetries',
            'Validation retry receipt'),
        @('attemptCount -gt 3',
            'Bounded validation retry gate'),
        @('Validation retry receipt mismatch',
            'Validation retry reconciliation'),
        @('CPU/GPU validation parity failed', 'Image/hash parity gate'),
        @('processIds.Count -ne 1', 'Same-PID gate'),
        @('control-pre;ABBA;BAAB;control-post',
            'Counterbalanced schedule contract'),
        @('cpu-burst-engine-native', 'Engine-native CPU identity'),
        @('gpu-visible-only-engine-indirect',
            'GPU indirect identity'),
        @('smoke-n10000-v1-g1-visible25', 'Frozen smoke cell'),
        @('primary-n10000-v1-g1-visible25',
            'Formal 10K single-view cell'),
        @('primary-n100000-v1-g1-visible25',
            'Formal 100K single-view cell'),
        @('primary-n10000-v4-g8-visible25',
            'Formal 10K multi-view cell'),
        @('primary-n100000-v4-g8-visible25',
            'Formal 100K multi-view cell'),
        @('sampleFrames = 900', 'Formal sample count'),
        @('cpuSubmissionP95MinimumImprovementPercent = 20.0',
            'CPU relative threshold'),
        @('cpuSubmissionP95MinimumImprovementMilliseconds = 0.20',
            'CPU absolute threshold'),
        @('requiredCpuSubmissionCells = 3',
            'Three-of-four CPU threshold'),
        @('nativeGpuRegionP99MaximumRegressionPercent = 5.0',
            'Native GPU region-tail guard'),
        @('cpuFrameBaselineP95Ms', 'CPU frame P95 evidence'),
        @('cpuFrameBaselineP99Ms', 'CPU frame P99 evidence'),
        @('cpuMainThreadBaselineP95Ms', 'Main-thread P95 evidence'),
        @('cpuRenderThreadBaselineP95Ms', 'Render-thread P95 evidence'),
        @('cpuRenderThreadBaselineValidSamples',
            'Optional render-thread sample count'),
        @('renderSubmissionWindowBaselineP95Ms',
            'Engine submission-window P95 evidence'),
        @('gpuFrameBaselineP99Ms', 'GPU frame P99 evidence'),
        @('gpuFrameBaselineValidSamples',
            'Optional GPU frame sample count'),
        @('nativeGpuRegionP99GatePassed',
            'Native GPU region-tail gate'),
        @('Formal macrobenchmark performance gate failed closed',
            'Formal fail-closed decision'),
        @('raw-frames.csv', 'Raw frame evidence'),
        @('block-summary.csv', 'Block evidence'),
        @('nativeGpuValidSamples -ne $SampleFrames',
            'Per-block native timing cardinality'),
        @('frameTimingValidSamples -ne $SampleFrames',
            'Per-block frame timing cardinality'),
        @('fencePassed -ne 1', 'Per-block completion fence gate'),
        @('validation.csv', 'Validation evidence'),
        @('matrix.csv', 'Matrix receipt'),
        @('matrix-summary.csv', 'Matrix metrics'),
        @('BENCHMARK_REPORT.md', 'Human-readable report'),
        @('$rowTemplate -f', 'Report-row format application'),
        @('NativeGPU={13}', 'Native GPU gate report label'),
        @('formalContractSatisfied', 'Formal completion receipt'))) {
    Assert-Contains $runner $requirement[0] $requirement[1]
}

Assert-NotContains `
    -Text $runner `
    -Needle 'MetricValues $baselineRows uploadBytes' `
    -Label 'Legacy upload summary'

$playerArgumentsStart = $runner.IndexOf(
    '$arguments = @(',
    [StringComparison]::Ordinal)
$playerArgumentsEnd = $runner.IndexOf(
    '$runSummaryPath =',
    $playerArgumentsStart,
    [StringComparison]::Ordinal)
if ($playerArgumentsStart -lt 0 -or $playerArgumentsEnd -le $playerArgumentsStart) {
    throw 'Player launch block could not be isolated.'
}
$playerLaunch = $runner.Substring(
    $playerArgumentsStart,
    $playerArgumentsEnd - $playerArgumentsStart)
Assert-NotContains $playerLaunch "'-batchmode'" 'Player launch'
Assert-NotContains $playerLaunch '-WindowStyle Hidden' 'Player launch'
Assert-Contains $playerLaunch "'-screen-fullscreen', '0'" `
    'Windowed Player launch'

$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $runnerPath,
    [ref]$tokens,
    [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw "Runner has $($parseErrors.Count) PowerShell parse error(s)."
}

$requiredFunctions = @(
    'Number',
    'MetricValues',
    'Percentile',
    'OptionalPercentile',
    'ImprovementPercent',
    'RegressionPercent',
    'Test-StringSequence',
    'Assert-MacroSchedule')
$functionAsts = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $requiredFunctions -contains $node.Name
}, $true))
if ($functionAsts.Count -ne $requiredFunctions.Count) {
    throw 'Runner analysis helpers are incomplete.'
}
$harnessText = ($functionAsts |
    Sort-Object { [Array]::IndexOf($requiredFunctions, $_.Name) } |
    ForEach-Object { $_.Extent.Text }) -join "`n`n"
$harnessText += @'

$cpuVariant = 'cpu-burst-engine-native'
$gpuVariant = 'gpu-visible-only-engine-indirect'

$metricRows = @(
    [pscustomobject]@{ value = '1.25' },
    [pscustomobject]@{ value = '2.75' })
$metricValues = [double[]]@(
    MetricValues -Rows $metricRows -Property value -RequirePositive)
if ($metricValues.Count -ne 2 -or
    [Math]::Abs($metricValues[0] - 1.25) -gt 0.0000001 -or
    [Math]::Abs($metricValues[1] - 2.75) -gt 0.0000001) {
    throw 'Metric parsing helper returned unexpected values.'
}

$median = Percentile ([double[]]@(1.0, 2.0, 3.0, 4.0)) 0.50
if ([Math]::Abs($median - 2.5) -gt 0.0000001) {
    throw "Percentile helper returned $median; expected 2.5."
}
$optionalMissing = OptionalPercentile ([double[]]@()) 0.95
if ($optionalMissing -cne 'unavailable') {
    throw "Optional percentile returned '$optionalMissing'; expected unavailable."
}
$optionalPresent = OptionalPercentile ([double[]]@(1.0, 2.0, 3.0)) 0.50
if ([Math]::Abs([double]$optionalPresent - 2.0) -gt 0.0000001) {
    throw "Optional percentile returned '$optionalPresent'; expected 2."
}
$improvement = ImprovementPercent 10.0 8.0
if ([Math]::Abs($improvement - 20.0) -gt 0.0000001) {
    throw "Improvement helper returned $improvement; expected 20."
}
$regression = RegressionPercent 10.0 10.5
if ([Math]::Abs($regression - 5.0) -gt 0.0000001) {
    throw "Regression helper returned $regression; expected 5."
}
if (-not (Test-StringSequence @('a', 'b') @('a', 'b')) -or
    (Test-StringSequence @('a', 'b') @('b', 'a'))) {
    throw 'String-sequence helper does not enforce ordinal ordering.'
}

$variants = @(
    'empty-render-frame',
    $cpuVariant, $gpuVariant, $gpuVariant, $cpuVariant,
    $gpuVariant, $cpuVariant, $cpuVariant, $gpuVariant,
    'empty-render-frame')
$types = @(
    'control-pre',
    'measurement', 'measurement', 'measurement', 'measurement',
    'measurement', 'measurement', 'measurement', 'measurement',
    'control-post')
$rows = [Collections.Generic.List[object]]::new()
for ($block = 1; $block -le 10; $block++) {
    foreach ($sample in 0..1) {
        $rows.Add([pscustomobject]@{
            blockIndex = $block
            blockType = $types[$block - 1]
            variant = $variants[$block - 1]
            processId = 42
            sampleIndex = $sample
        })
    }
}
Assert-MacroSchedule `
    -Raw ([object[]]$rows.ToArray()) `
    -SamplesPerBlock 2 `
    -ExpectedProcessId 42 `
    -ScenarioId 'unit-valid'

$rows[3].processId = 43
$samePidRejected = $false
try {
    Assert-MacroSchedule `
        -Raw ([object[]]$rows.ToArray()) `
        -SamplesPerBlock 2 `
        -ExpectedProcessId 42 `
        -ScenarioId 'unit-invalid'
}
catch {
    $samePidRejected = $true
}
if (-not $samePidRejected) {
    throw 'Schedule helper accepted mixed process IDs.'
}
'analysis-helper-unit-tests=passed'
'@
$harness = [scriptblock]::Create($harnessText)
$harnessResult = @(& $harness)
if ($harnessResult -notcontains 'analysis-helper-unit-tests=passed') {
    throw 'Runner analysis helper unit tests did not complete.'
}

Write-Host (
    'GPU-driven instance macrobenchmark provenance and analysis contract ' +
    'validated.')
