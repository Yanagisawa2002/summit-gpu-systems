[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolsRoot = Split-Path -Parent $PSScriptRoot
$runnerPath =
    Join-Path $toolsRoot 'Run-GpuDrivenInstancePolicyBenchmark.ps1'
$selectorPath =
    Join-Path $toolsRoot 'Select-GpuDrivenInstancePolicyProfile.ps1'
$modulePath =
    Join-Path $toolsRoot 'GpuDrivenInstancePolicyBenchmark.psm1'
$checkpointModulePath =
    Join-Path $toolsRoot 'GpuBenchmarkCheckpoint.psm1'
$checkpointTestPath =
    Join-Path $PSScriptRoot 'Test-GpuBenchmarkCheckpoint.ps1'
foreach ($path in @(
        $runnerPath,
        $selectorPath,
        $modulePath,
        $checkpointModulePath,
        $checkpointTestPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Policy tooling is missing: $path"
    }
}

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

function Assert-Parses {
    param([Parameter(Mandatory = $true)][string]$Path)
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$errors)
    if ($errors.Count -ne 0) {
        $messages = @($errors | ForEach-Object { $_.Message }) -join '; '
        throw "$Path has PowerShell parse errors: $messages"
    }
}

function Assert-Throws {
    param(
        [Parameter(Mandatory = $true)][scriptblock]$Action,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $thrown = $false
    try {
        & $Action
    }
    catch {
        $thrown = $true
    }
    if (-not $thrown) {
        throw "$Label did not fail closed."
    }
}

$runner = Get-Content -LiteralPath $runnerPath -Raw
$selector = Get-Content -LiteralPath $selectorPath -Raw
$module = Get-Content -LiteralPath $modulePath -Raw
$checkpointModule = Get-Content -LiteralPath $checkpointModulePath -Raw
foreach ($path in @(
        $runnerPath,
        $selectorPath,
        $modulePath,
        $checkpointModulePath,
        $checkpointTestPath)) {
    Assert-Parses $path
}

foreach ($requirement in @(
        @('Get-GpuBenchmarkGitSnapshot', 'Clean Git snapshot'),
        @('Benchmark requires a clean worktree', 'Clean-tree gate'),
        @('sourceHashesStableAcrossBuild', 'Source stability receipt'),
        @('Get-GpuBenchmarkPlayerPayload', 'Player manifest'),
        @('playerPayloadStableThroughRun', 'Payload stability receipt'),
        @('GpuDrivenInstancePolicyBenchmarkBuild.PerformBuild',
            'Dedicated fresh build'),
        @('-gpu-driven-instance-policy-player-path', 'Build output argument'),
        @('-gpu-driven-instance-policy-benchmark', 'Player enable argument'),
        @('-force-d3d12', 'D3D12 contract'),
        @("'6000.5.2f1'", 'Exact Unity contract'),
        @('$calibrationSeed = 20260830', 'Calibration seed'),
        @('$holdoutSeed = 20260831', 'Holdout seed'),
        @('$replaySeed = 20260833', 'Unseen amended replay seed'),
        @('gpu-driven-policy-upload-culling-v2-holdout-v1-replay-v2-checkpoint-v1',
            'Scoped resumable replay protocol'),
        @('$formalSampleFrames = 900', 'Formal sample cardinality'),
        @('visible-windowed-swapchain-v1',
            'Visible Player timing contract'),
        @('WindowStyle-Hidden-forbidden',
            'Hidden Player rejection contract'),
        @('Short SingleScenario runs are diagnostics only.',
            'SingleScenario evidence warning'),
        @('does not guarantee Unity GPU-frame availability',
            'No false sample-count availability guarantee'),
        @('[ValidateSet(500, 2500, 7500, 10000)]',
            'Controller-compatible visibility set'),
        @('ABBA;BAAB', 'Counterbalanced schedule'),
        @('selectorIterations=100000', 'Selector microbenchmark'),
        @('engineIndirectArgumentsExact=1',
            'Exact engine indirect-argument validation'),
        @('renderTargetNonBlackHash=1',
            'Non-black render-target validation'),
        @('gpuFrameUnavailableLiteral=unavailable',
            'Explicit unavailable GPU-frame contract'),
        @('gpuFrameBlockValidCoverage>=95%',
            'GPU-frame block coverage contract'),
        @('gpuFramePairedComparisonCoverage>=90%',
            'GPU-frame paired coverage contract'),
        @('otherTimedMetricCoverage=100%',
            'Complete non-GPU-frame metric coverage'),
        @('validationLifecycle=single-pending-owner;timeout-fail-closed;dispose-requires-none',
            'Fail-closed validation lifecycle'),
        @("'-runTests'", 'Self-generated EditMode run'),
        @("'-testPlatform', 'EditMode'", 'EditMode platform'),
        @('generatedByRunner = $true', 'Runner-owned EditMode receipt'),
        @('externalReceiptAccepted = $false',
            'External EditMode receipt exclusion'),
        @('Assert-PolicyPreflightBinding', 'Preflight provenance binding'),
        @('GpuDrivenInstancePolicyBenchmarkAdapterTests.EngineIndirectEvidenceRequiresExactCopiedWords',
            'Policy presentation EditMode identity'),
        @('GpuDrivenInstancePolicySelectorTests.WarmSelectPathAllocatesZeroBytesAcrossOneHundredThousandCalls',
            'Selector allocation EditMode identity'),
        @('GpuInstanceStateUploaderTests.PlannedDirtyUploadRecordsExactlyTheInspectedPlan',
            'Uploader EditMode identity'),
        @('candidateGate=mean>=2%;wins>=55%', 'Cautious candidate gate'),
        @('replayGate=decision-exact;selector-mean<=0.01ms;selector-p99<=0.05ms;isolated-iterations=100000;isolated-allocated=0;isolated-unstable=0',
            'Selector-equivalence overhead gate'),
        @('endToEndReplayGate=accepted:candidate-gate;rejected:full-flat-portable+no-material-p99-regression',
            'End-to-end accepted/rejected replay contract'),
        @('forced-selected', 'Forced selected replay'),
        @('actual-auto', 'Actual auto replay'),
        @('Test-PolicyReplayEquivalence', 'Replay equivalence gate'),
        @('Test-PolicyEndToEndReplay', 'End-to-end replay gate'),
        @("suite = 'summit.gpu-driven-instance-policy-formal-v2'",
            'Self-describing amended formal receipt'),
        @('Test-PolicySha256Equal', 'Case-neutral SHA-256 identity gate'),
        @("-Phase 'replay-end-to-end'", 'Independent end-to-end replay'),
        @('endToEndReplayRunCount', 'End-to-end replay run count'),
        @('expectedRawFrameCount', 'Formal raw-row count'),
        @('decisionMismatchCount', 'Decision mismatch receipt'),
        @('materialTailFailures', 'Tail regression receipt'),
        @('formal-matrix-summary.csv', 'Formal summary'),
        @('formal-matrix-receipt.json', 'Formal receipt'),
        @('BENCHMARK_REPORT.md', 'Human report'),
        @('[switch]$Resume', 'Explicit resume switch'),
        @('[switch]$RecoverInterrupted', 'Explicit interruption recovery'),
        @('run-contract.json', 'Immutable run contract'),
        @('Enter-GpuBenchmarkRunLock', 'Exclusive run lock'),
        @('Write-GpuBenchmarkSealedJson', 'Atomic sealed receipts'),
        @('Read-CompletedPolicyPhase', 'Validated phase reuse'),
        @('Assert-GpuBenchmarkPhaseCoverage',
            'Duplicate and missing phase rejection'),
        @('[Collections.Generic.List[string]]::new()',
            'Zero-safe selection recovery collection'),
        @('$selectionOutputs.Add($candidateSelectionOutput)',
            'Explicit selection recovery discovery'),
        @("optimizedAxes = [string[]]@('Upload', 'Culling')",
            'Measured two-axis scope'),
        @('caller-required;not-calibrated',
            'Output semantic-constraint boundary'),
        @('compose-with-primitive-autotuner',
            'Primitive autotuner composition boundary'),
        @('SHA256SUMS', 'Evidence hashes'))) {
    Assert-Contains $runner $requirement[0] $requirement[1]
}
Assert-NotContains $runner '[switch]$SkipBuild' 'Fresh build runner'
$playerLaunch = [regex]::Match(
    $runner,
    '(?s)\$process = Start-Process.*?\$completed = Wait-PolicyProcess')
if (-not $playerLaunch.Success) {
    throw 'Player launch block could not be identified.'
}
Assert-NotContains `
    $playerLaunch.Value `
    '-WindowStyle' `
    'Visible Player timing launch'

foreach ($requirement in @(
        @('Get-MeasuredDecision', 'Measured decision extraction'),
        @('Assert-MeasuredDecisionEqual', 'Cross-seed decision identity'),
        @('manifest candidate decision does not', 'Manifest cross-check'),
        @("'measured-candidate'", 'Candidate decision receipt'),
        @("'measured-baseline'", 'Rejected candidate baseline receipt'),
        @('candidateRejectionSelectsMeasuredBaseline',
            'Measured baseline fail-safe receipt'),
        @('holdoutEvidenceId', 'Per-rule evidence hash'),
        @('holdoutEvidenceSetId', 'Profile evidence set hash'),
        @('requiredConsecutiveFrames = 2', 'Hysteresis contract'),
        @('sourceCommit', 'Build commit provenance'),
        @("ToString('O')", 'Canonical UTC provenance'))) {
    Assert-Contains $selector $requirement[0] $requirement[1]
}

foreach ($requirement in @(
        @('Assert-PolicySchedule', 'Eight-block schedule gate'),
        @('nativeTimestampElapsedNanoseconds', 'Native timestamp evidence'),
        @('frameTimingCaptureLatencyFrames', 'Frame alignment evidence'),
        @('cpuSubmissionWindowMs', 'CPU submission evidence'),
        @('cpuFrameMs', 'CPU frame-tail evidence'),
        @('gpuFrameMs', 'GPU frame-tail evidence'),
        @('gpuFrameReadyRows', 'GPU frame availability summary'),
        @('gpuFrameUnavailableRows', 'GPU frame unavailable summary'),
        @('GpuFrameBlockMinimumCoveragePercent',
            'GPU frame block coverage gate'),
        @('GpuFramePairedMinimumCoveragePercent',
            'GPU frame paired coverage gate'),
        @('minimumPairCoveragePercent',
            'Paired coverage comparison receipt'),
        @('measurementReadbackBytes', 'Timed readback gate'),
        @('mainThreadAllocatedBytes', 'Timed allocation gate'),
        @('slotWaitFrames', 'Slot wait gate'),
        @('hierarchyStatisticsAvailable', 'Hierarchy statistics gate'),
        @('engineIndirectWordCount', 'Engine indirect evidence schema'),
        @('engineIndirectMismatchCount', 'Engine argument mismatch gate'),
        @('renderTargetNonBlackPixelCount', 'Non-black target gate'),
        @('presentationValidationPassed', 'Presentation receipt gate'),
        @('validationLifecycleDrainStatus',
            'Validation lifecycle summary gate'),
        @('decisionFlags', 'Decision fallback gate'),
        @('positiveWins', 'Paired positive wins'),
        @('pairedDelta', 'Paired delta summary'),
        @('pairedImprovementPercent', 'Paired percent summary'),
        @('Test-PolicyEndToEndReplay', 'End-to-end policy helper'),
        @('Test-PolicySha256Equal', 'SHA-256 identity helper'),
        @('safeRejectedDecision', 'Rejected-rule safety evidence'),
        @('p95RegressionPercent', 'P95 comparison'),
        @('p99RegressionPercent', 'P99 comparison'))) {
    Assert-Contains $module $requirement[0] $requirement[1]
}

foreach ($requirement in @(
        @('FileMode]::CreateNew', 'Exclusive file creation'),
        @('FileShare]::Read', 'Held readable run lock'),
        @('File]::Move($temporary, $resolved, $true)',
            'Atomic overwrite replacement'),
        @('recordSha256', 'Canonical record seal'),
        @('Test-GpuBenchmarkLockOwnerActive', 'PID/start-time lock identity'),
        @('interruptions', 'Recoverable interruption archive'),
        @('duplicate, missing, or unexpected',
            'Exact phase coverage rejection'))) {
    Assert-Contains $checkpointModule $requirement[0] $requirement[1]
}

Import-Module -Name $modulePath -Force

$mixedCaseSha = 'A1b2C3d4' * 8
if (-not (Test-PolicySha256Equal `
        -Left $mixedCaseSha.ToUpperInvariant() `
        -Right $mixedCaseSha.ToLowerInvariant())) {
    throw 'SHA-256 identity rejected case-only hexadecimal differences.'
}
$differentSha = $mixedCaseSha.Substring(0, 63) + '0'
if (Test-PolicySha256Equal -Left $mixedCaseSha -Right $differentSha) {
    throw 'SHA-256 identity accepted a different digest.'
}
foreach ($malformedSha in @('', ('a' * 63), ('g' * 64))) {
    if (Test-PolicySha256Equal -Left $mixedCaseSha -Right $malformedSha) {
        throw 'SHA-256 identity accepted a malformed digest.'
    }
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'summit-policy-provenance-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
try {
    $commit = 'a' * 40
    $pipelineHash = 'b' * 64
    $shaderHash = 'c' * 64
    $measurementHash = 'd' * 64
    $protocol =
        'gpu-driven-policy-upload-culling-v2-holdout-v1-replay-v2-checkpoint-v1'
    $unity = '6000.5.2f1'
    $sampleFrames = 20

    $invalidVisibilityRejectedByBinding = $false
    try {
        $null = & $runnerPath `
            -UnityPath (Join-Path $temporaryRoot 'must-not-launch.exe') `
            -OutputDirectory (
                Join-Path $temporaryRoot 'must-not-create-output') `
            -VisibilityBasisPoints 1234
    }
    catch {
        if ($_.FullyQualifiedErrorId -notmatch
                '^ParameterArgumentValidationError') {
            throw
        }
        $invalidVisibilityRejectedByBinding = $true
    }
    if (-not $invalidVisibilityRejectedByBinding -or
        (Test-Path -LiteralPath (
            Join-Path $temporaryRoot 'must-not-create-output'))) {
        throw 'Unsupported visibility was not rejected before runner work.'
    }

    $syntheticNUnitPath = Join-Path $temporaryRoot 'editmode-results.xml'
    $syntheticNUnit = @'
<?xml version="1.0" encoding="utf-8"?>
<test-run result="Passed" total="2" passed="2" failed="0" skipped="0" inconclusive="0">
  <test-suite type="Assembly" name="Synthetic.Policy.Tests.dll" result="Passed">
    <test-suite type="TestFixture" fullname="Synthetic.Policy.Tests.PolicyFixture" result="Passed">
      <test-case fullname="Synthetic.Policy.Tests.PolicyFixture.PolicyCase" result="Passed" />
      <test-case fullname="Synthetic.Policy.Tests.PolicyFixture.UploaderCase" result="Passed" />
    </test-suite>
  </test-suite>
</test-run>
'@
    [IO.File]::WriteAllText(
        $syntheticNUnitPath,
        $syntheticNUnit,
        [Text.UTF8Encoding]::new($false))
    $expectedSyntheticIdentities = @(
        'Synthetic.Policy.Tests.dll',
        'Synthetic.Policy.Tests.PolicyFixture',
        'Synthetic.Policy.Tests.PolicyFixture.PolicyCase',
        'Synthetic.Policy.Tests.PolicyFixture.UploaderCase')
    $syntheticNUnitReceipt = Get-PolicyNUnitReceipt `
        -Path $syntheticNUnitPath `
        -ExpectedIdentities $expectedSyntheticIdentities
    Assert-Throws {
        $null = Get-PolicyNUnitReceipt `
            -Path $syntheticNUnitPath `
            -ExpectedIdentities @(
                $expectedSyntheticIdentities +
                'Synthetic.Policy.Tests.PolicyFixture.MissingCase')
    } 'Missing EditMode identity'

    $preflightSourceHash = '1' * 64
    $preflight = [pscustomobject][ordered]@{
        schemaVersion = 1
        generatedByRunner = $true
        externalReceiptAccepted = $false
        sourceCommit = $commit
        sourceSnapshotSha256 = $preflightSourceHash
        sourceSnapshotSha256After = $preflightSourceHash
        resultSha256 = $syntheticNUnitReceipt.sha256
        unityVersion = $unity
        graphicsApiArgument = '-force-d3d12'
    }
    $null = Assert-PolicyPreflightBinding `
        -Provenance $preflight `
        -ExpectedCommit $commit `
        -ExpectedSourceSha256 $preflightSourceHash `
        -ExpectedXmlSha256 $syntheticNUnitReceipt.sha256 `
        -ExpectedUnityVersion $unity
    $stalePreflight = $preflight | ConvertTo-Json -Depth 5 |
        ConvertFrom-Json
    $stalePreflight.sourceSnapshotSha256After = '2' * 64
    Assert-Throws {
        $null = Assert-PolicyPreflightBinding `
            -Provenance $stalePreflight `
            -ExpectedCommit $commit `
            -ExpectedSourceSha256 $preflightSourceHash `
            -ExpectedXmlSha256 $syntheticNUnitReceipt.sha256 `
            -ExpectedUnityVersion $unity
    } 'Stale EditMode source binding'

    function Write-SyntheticEvidence {
        param(
            [Parameter(Mandatory = $true)][string]$Directory,
            [Parameter(Mandatory = $true)][int]$Seed,
            [Parameter(Mandatory = $true)][string]$LeftCase,
            [Parameter(Mandatory = $true)][string]$RightCase,
            [switch]$AcceptedProfile,
            [switch]$SlowCandidate,
            [switch]$GpuFrameCoverageBoundary,
            [string]$RuleId = 'synthetic-rule'
        )
        New-Item -ItemType Directory -Path $Directory -Force | Out-Null
        $processId = 42
        $expectedRows = 8 * $sampleFrames
        $gpuFrameUnavailableRows = if ($GpuFrameCoverageBoundary) {
            8
        }
        else { 0 }
        $gpuFrameReadyRows =
            $expectedRows - $gpuFrameUnavailableRows
        $engineIndirectWordCount = 5
        $engineIndirectReadbackBytes = 40
        $renderTargetReadbackBytes = 1048576
        $presentationReadbackBytesPerValidation =
            $engineIndirectReadbackBytes + $renderTargetReadbackBytes
        $validationReadbackBytesPerValidation =
            64 + $presentationReadbackBytesPerValidation
        $presentationReadbackBytes =
            4 * $presentationReadbackBytesPerValidation
        $validationReadbackBytes =
            4 * $validationReadbackBytesPerValidation
        $summary = @(
            'suite=summit.gpu-driven-instance-policy',
            'passed=1',
            'status=passed',
            'processId=42',
            'scheduleContract=ABBA;BAAB',
            'measurementBlockCount=8',
            'expectedMeasurementBlockCount=8',
            "rawFrameCount=$expectedRows",
            "expectedRawFrameCount=$expectedRows",
            "frameTimingReadyRows=$expectedRows",
            "gpuFrameReadyRows=$gpuFrameReadyRows",
            "gpuFrameUnavailableRows=$gpuFrameUnavailableRows",
            "submissionWindowReadyRows=$expectedRows",
            "nativeTimestampReadyRows=$expectedRows",
            "stableDecisionRows=$expectedRows",
            ('timestampInstrumentationReadbackBytes=' +
                (16 * $expectedRows)),
            'decisionIssueCount=0',
            'frameAlignmentIssueCount=0',
            'pairedInputIssueCount=0',
            'firstMeasuredResidentIssueCount=0',
            'validationLifecycleDrainIssueCount=0',
            'validationTimeoutCount=0',
            'validationDrainTimeoutCount=0',
            'presentationValidationTimeoutCount=0',
            'validationLifecycleComplete=1',
            'validationLifecycleDrainStatus=not-required',
            'validationLifecycleDrainIncomplete=0',
            'measuredLogicalOrdinalContract=pair-local-v1',
            'unmeasuredLogicalOrdinalContract=independent-high-bit-v1',
            'mainThreadAllocationRows=0',
            'mainThreadAllocatedBytes=0',
            'timedAllocationFree=1',
            'slotWaitFrames=0',
            'measurementReadbackBytes=0',
            'validationCount=4',
            'expectedValidationCount=4',
            'validationFailures=0',
            "validationReadbackBytes=$validationReadbackBytes",
            'presentationValidationCount=4',
            'expectedPresentationValidationCount=4',
            'presentationValidationFailures=0',
            "presentationValidationReadbackBytes=$presentationReadbackBytes",
            'deterministicRenderTargetHash=RT-HASH',
            'minimumRenderTargetNonBlackPixels=64',
            'completionFencesComplete=1',
            ('profileAccepted=' + $(if ($AcceptedProfile) { '1' } else { '0' })),
            "pipelineContractFingerprint=$pipelineHash",
            "shaderContractFingerprint=$shaderHash",
            "measurementContractFingerprint=$measurementHash",
            "calibrationProtocol=$protocol",
            'selectorOverheadIterations=100000',
            'selectorOverheadAllocatedBytes=0',
            'selectorOverheadUnstableDecisionCount=0',
            'nativeTimestampWarmupPassed=1',
            'nativeTimestampWarmupStatus=ready',
            'nativeTimestampAcquireFailures=0',
            'nativeTimestampResultFailures=0',
            'nativeTimestampTimeouts=0',
            "buildCommit=$commit",
            'buildCommitValid=1',
            'evidenceGatePassed=1')
        [IO.File]::WriteAllLines(
            (Join-Path $Directory 'run-summary.txt'),
            $summary,
            [Text.UTF8Encoding]::new($false))

        $config = [ordered]@{
            schemaVersion = 1
            suite = 'summit.gpu-driven-instance-policy'
            processId = $processId
            unityVersion = $unity
            scenarioId = "synthetic-seed$Seed"
            instanceCount = 100000
            viewCount = 1
            visibilityBasisPoints = 2500
            visiblePairCount = 25000
            dirtyBasisPoints = 1000
            seed = $Seed
            requiredOutputMode = 'VisibleOnly'
            clusterCount = 1563
            hierarchyCandidateBasisPoints = 2600
            sampleFramesPerBlock = $sampleFrames
            measurementBlocks = 8
            scheduleContract = 'ABBA;BAAB'
            frameTimingResultLatencyFrames = 4
            gpuFrameUnavailableLiteral = 'unavailable'
            gpuFrameBlockMinimumCoveragePercent = 95
            gpuFramePairedMinimumCoveragePercent = 90
            otherTimedMetricCoveragePercent = 100
            stateResetOutsideMeasuredWindow = $true
            selectorResetOutsideMeasuredWindow = $true
            caseLocalConvergenceOutsideMeasuredWindow = $true
            measurementReadbackBytesPerFrame = 0
            timestampInstrumentationBytesPerCompletedSample = 16
            selectorOverheadIterations = 100000
            leftCase = $LeftCase
            rightCase = $RightCase
            profileSha256 = if ($AcceptedProfile) { 'e' * 64 } else { 'unavailable' }
            profileAccepted = [bool]$AcceptedProfile
            pipelineContractFingerprint = $pipelineHash
            shaderContractFingerprint = $shaderHash
            calibrationProtocol = $protocol
            measurementContractFingerprint = $measurementHash
            buildCommit = $commit
            nativeTimestampAvailability = 'Available'
            nativeTimestampAbiVersion = 2
            nativeTimestampCapabilityFlags = 31
            nativeTimestampWarmupPassed = $true
        }
        [IO.File]::WriteAllText(
            (Join-Path $Directory 'config.json'),
            ($config | ConvertTo-Json -Depth 10),
            [Text.UTF8Encoding]::new($false))
        $device = [ordered]@{
            operatingSystem = 'Synthetic OS'
            processorType = 'Synthetic CPU'
            graphicsDeviceName = 'Synthetic RTX'
            graphicsDeviceVendor = 'Synthetic NVIDIA'
            graphicsDeviceVendorId = 4318
            graphicsDeviceId = 9860
            graphicsDeviceType = 'Direct3D12'
            graphicsDeviceVersion = 'Direct3D 12.0 synthetic'
            graphicsShaderLevel = 60
            supportsComputeShaders = $true
            supportsGraphicsFence = $true
            supportsAsyncGpuReadback = $true
            frameTimingFeatureEnabled = $true
            deviceFingerprint = 'v1-10DE-2684-direct3d12-sm60'
            unityVersion = $unity
        }
        [IO.File]::WriteAllText(
            (Join-Path $Directory 'device.json'),
            ($device | ConvertTo-Json -Depth 10),
            [Text.UTF8Encoding]::new($false))

        $sides = @('A', 'B', 'B', 'A', 'B', 'A', 'A', 'B')
        $pairOrders = @('AB', 'AB', 'BA', 'BA', 'BA', 'BA', 'AB', 'AB')
        $pairIndices = @(1, 1, 2, 2, 3, 3, 4, 4)
        $rows = [Collections.Generic.List[object]]::new()
        $sourceIndex = 0
        for ($block = 1; $block -le 8; $block++) {
            $side = $sides[$block - 1]
            $caseId = if ($side -ceq 'A') { $LeftCase } else { $RightCase }
            $withinPairPosition = (($block - 1) % 2) + 1
            foreach ($sample in 1..$sampleFrames) {
                $candidate = $side -ceq 'B'
                $candidateMetric = if ($SlowCandidate) { 3.0 } else { 1.0 }
                $metric = if ($candidate) { $candidateMetric } else { 2.0 }
                $gpuFrameValid = -not (
                    $GpuFrameCoverageBoundary -and
                    $sample -eq $withinPairPosition)
                $isDirtyDecision = $caseId -match 'dirty-flat$'
                $isAutoDecision = $caseId -ceq 'gpu-driven-policy/actual-auto'
                $isForcedSelected =
                    $caseId -ceq 'gpu-driven-policy/forced-selected'
                $decisionUpload = if ($isDirtyDecision) {
                    'Dirty'
                }
                elseif ($isAutoDecision -or $isForcedSelected) {
                    'Dirty'
                }
                else { 'Full' }
                $rows.Add([pscustomobject][ordered]@{
                    sourceRowIndex = $sourceIndex
                    processId = $processId
                    scenarioId = $config.scenarioId
                    blockIndex = $block
                    superRound = if ($block -le 4) { 1 } else { 2 }
                    sequencePosition = (($block - 1) % 4) + 1
                    pairIndex = $pairIndices[$block - 1]
                    pairOrder = $pairOrders[$block - 1]
                    withinPairPosition = (($block - 1) % 2) + 1
                    side = $side
                    caseId = $caseId
                    sampleIndex = $sample
                    logicalOrdinal =
                        ($pairIndices[$block - 1] - 1) * $sampleFrames +
                        $sample
                    instanceCount = 100000
                    viewCount = 1
                    visibilityBasisPoints = 2500
                    dirtyBasisPoints = 1000
                    clusterCount = 1563
                    hierarchyCandidateBp = 2600
                    totalCpuMs = $metric
                    selectorCpuMs = if ($isAutoDecision) { 0.01 } else { 0.0 }
                    selectorCpuTicks = if ($isAutoDecision) { 100 } else { 0 }
                    selectorInvoked = if ($isAutoDecision) { 1 } else { 0 }
                    gpuRegionElapsedMs = $metric
                    cpuFrameMs = $metric + 3.0
                    cpuMainThreadFrameMs = $metric + 2.0
                    cpuRenderThreadFrameMs = $metric + 1.0
                    gpuFrameMs = if ($gpuFrameValid) {
                        $metric + 4.0
                    }
                    else { 'unavailable' }
                    cpuSubmissionWindowMs = $metric + 0.5
                    mainThreadAllocatedBytes = 0
                    slotWaitFrames = 0
                    changedInstanceCount = 10000
                    plannedUploadedRecordCount = 10000
                    plannedUploadCallCount = 2
                    updateHash = "UPDATE-$($pairIndices[$block - 1])-$sample"
                    expectedStateHash = "STATE-$($pairIndices[$block - 1])-$sample"
                    decisionUploadMode = $decisionUpload
                    decisionOutputMode = 'VisibleOnly'
                    decisionCullingMode = 'Flat'
                    decisionPrimitiveBackend = 'Portable'
                    decisionProfileRuleIndex = if ($AcceptedProfile) { 0 } else { -1 }
                    decisionRuleId = if ($AcceptedProfile) { $RuleId } else { '' }
                    decisionFlags = 0
                    decisionSource = if ($isAutoDecision) {
                        'ActualAuto'
                    }
                    elseif ($isForcedSelected) { 'ForcedSelected' }
                    else { 'ForcedCalibration' }
                    decisionAccepted = 1
                    decisionStableExpected = 1
                    uploadAmplificationBp = if ($decisionUpload -ceq 'Dirty') {
                        10000
                    } else { 100000 }
                    completionFenceAppended = 1
                    nativeTimestampStatus = 'ready'
                    nativeTimestampElapsedNanoseconds = 1000000
                    frameTimingValid = 1
                    frameTimingCaptureLatencyFrames = 4
                    cpuRenderThreadFrameValid = 1
                    gpuFrameValid = if ($gpuFrameValid) { 1 } else { 0 }
                    submissionWindowValid = 1
                    measurementReadbackBytes = 0
                    timestampInstrumentationReadbackBytes = 16
                })
                $sourceIndex++
            }
        }
        $rows.ToArray() | Export-Csv -LiteralPath (
            Join-Path $Directory 'raw-frames.csv') `
            -NoTypeInformation -Encoding utf8

        $blockRows = for ($block = 1; $block -le 8; $block++) {
            [pscustomobject]@{
                blockIndex = $block
                pairIndex = $pairIndices[$block - 1]
                pairOrder = $pairOrders[$block - 1]
                withinPairPosition = (($block - 1) % 2) + 1
                side = $sides[$block - 1]
                caseId = if ($sides[$block - 1] -ceq 'A') {
                    $LeftCase
                } else { $RightCase }
                sampleCount = $sampleFrames
                timestampReadyRows = $sampleFrames
                frameTimingReadyRows = $sampleFrames
                gpuFrameValidCount = if ($GpuFrameCoverageBoundary) {
                    $sampleFrames - 1
                }
                else { $sampleFrames }
                submissionWindowReadyRows = $sampleFrames
                stableDecisionRows = $sampleFrames
                mainThreadAllocationRows = 0
                mainThreadAllocatedBytes = 0
                slotWaitFrames = 0
                residentStateHashBeforeMeasured = "RESIDENT-$block"
                firstMeasuredExpectedStateHash =
                    "STATE-$($pairIndices[$block - 1])-1"
                firstMeasuredResidentDifferenceRequired = 1
                firstMeasuredStateDiffersFromResident = 1
                completionFencesPassed = 1
            }
        }
        $blockRows | Export-Csv -LiteralPath (
            Join-Path $Directory 'block-summary.csv') `
            -NoTypeInformation -Encoding utf8

        $validationRows = foreach ($phase in @(
                'warmup-left', 'warmup-right', 'final-left', 'final-right')) {
            $caseId = if ($phase -match 'left$') { $LeftCase } else { $RightCase }
            [pscustomobject]@{
                phase = $phase
                caseId = $caseId
                passed = 1
                message = 'Synthetic exact validation passed.'
                readbackBytes = $validationReadbackBytesPerValidation
                expectedOutputHash = 'OUTPUT'
                actualOutputHash = 'OUTPUT'
                expectedStateHash = 'STATE'
                actualStateHash = 'STATE'
                invalidKeyCount = 0
                diagnosticFlags = 0
                hierarchyStatisticsAvailable = 1
                expectedCoarseVisibleClusterViewCount = 0
                expectedCandidateInstanceViewCount = 0
                expectedHierarchicalVisiblePairCount = 0
                coarseVisibleClusterViewCount = 0
                candidateInstanceViewCount = 0
                hierarchicalVisiblePairCount = 0
                clusterCount = 1563
                hierarchyCandidateBp = 2600
                engineIndirectWordCount = $engineIndirectWordCount
                engineIndirectReadbackBytes =
                    $engineIndirectReadbackBytes
                engineIndirectExpectedHash = 'ENGINE-HASH'
                engineIndirectActualHash = 'ENGINE-HASH'
                engineIndirectMismatchCount = 0
                engineIndirectExact = 1
                renderTargetFormat = 'RGBA32'
                renderTargetWidth = 512
                renderTargetHeight = 512
                renderTargetReadbackBytes = $renderTargetReadbackBytes
                renderTargetHash = 'RT-HASH'
                renderTargetBlackReferenceHash = 'BLACK-HASH'
                renderTargetNonBlackPixelCount = 64
                renderTargetHashConsistent = 1
                presentationValidationPassed = 1
                presentationValidationMessage =
                    'Synthetic presentation validation passed.'
            }
        }
        $validationRows | Export-Csv -LiteralPath (
            Join-Path $Directory 'validation.csv') `
            -NoTypeInformation -Encoding utf8
        [pscustomobject]@{
            iterations = 100000
            stopwatchFrequency = 10000000
            meanNs = 100
            p50Ns = 100
            p95Ns = 100
            p99Ns = 100
            allocatedBytes = 0
            unstableDecisionCount = 0
            flags = if ($AcceptedProfile) { 0 } else { 1 }
            profileAccepted = if ($AcceptedProfile) { 1 } else { 0 }
        } | Export-Csv -LiteralPath (
            Join-Path $Directory 'selector-overhead.csv') `
            -NoTypeInformation -Encoding utf8
    }

    function Copy-SyntheticDecisionEvidence {
        param(
            [Parameter(Mandatory = $true)][string]$Source,
            [Parameter(Mandatory = $true)][string]$Destination,
            [Parameter(Mandatory = $true)][string]$CandidateCaseId,
            [Parameter(Mandatory = $true)][string]$UploadMode,
            [Parameter(Mandatory = $true)][string]$CullingMode
        )
        Copy-Item -LiteralPath $Source -Destination $Destination -Recurse
        $rawPath = Join-Path $Destination 'raw-frames.csv'
        $decisionRows = @(Import-Csv -LiteralPath $rawPath)
        foreach ($row in @($decisionRows | Where-Object {
                [string]$_.caseId -ceq $CandidateCaseId
            })) {
            $row.decisionUploadMode = $UploadMode
            $row.decisionCullingMode = $CullingMode
        }
        $decisionRows | Export-Csv -LiteralPath $rawPath `
            -NoTypeInformation -Encoding utf8
    }

    function Copy-SyntheticZeroDirtyHierarchyEvidence {
        param(
            [Parameter(Mandatory = $true)][string]$Source,
            [Parameter(Mandatory = $true)][string]$Destination,
            [Parameter(Mandatory = $true)][string]$CandidateCaseId
        )
        Copy-Item -LiteralPath $Source -Destination $Destination -Recurse
        $configPath = Join-Path $Destination 'config.json'
        $hierarchyConfig = Get-Content -LiteralPath $configPath -Raw |
            ConvertFrom-Json
        $hierarchyConfig.dirtyBasisPoints = 0
        [IO.File]::WriteAllText(
            $configPath,
            ($hierarchyConfig | ConvertTo-Json -Depth 10),
            [Text.UTF8Encoding]::new($false))
        $rawPath = Join-Path $Destination 'raw-frames.csv'
        $decisionRows = @(Import-Csv -LiteralPath $rawPath)
        foreach ($row in $decisionRows) {
            $row.dirtyBasisPoints = 0
            $row.changedInstanceCount = 0
            $row.plannedUploadedRecordCount = 100000
            $row.plannedUploadCallCount = 1
            $row.uploadAmplificationBp = 'unavailable'
            $row.decisionUploadMode = 'Full'
            $row.decisionCullingMode = if (
                [string]$row.caseId -ceq $CandidateCaseId) {
                'Hierarchy'
            } else { 'Flat' }
        }
        $decisionRows | Export-Csv -LiteralPath $rawPath `
            -NoTypeInformation -Encoding utf8
        $blockPath = Join-Path $Destination 'block-summary.csv'
        $blockRows = @(Import-Csv -LiteralPath $blockPath)
        foreach ($block in $blockRows) {
            $block.firstMeasuredResidentDifferenceRequired = 0
            $block.firstMeasuredStateDiffersFromResident = 1
        }
        $blockRows | Export-Csv -LiteralPath $blockPath `
            -NoTypeInformation -Encoding utf8
    }

    $leftCalibration = 'gpu-driven-policy/calibration/full-flat'
    $rightCalibration = 'gpu-driven-policy/calibration/dirty-flat'
    $calibrationDirectory = Join-Path $temporaryRoot 'calibration'
    $holdoutDirectory = Join-Path $temporaryRoot 'holdout'
    Write-SyntheticEvidence $calibrationDirectory 20260830 `
        $leftCalibration $rightCalibration
    Write-SyntheticEvidence $holdoutDirectory 20260831 `
        $leftCalibration $rightCalibration
    $calibrationEvidence = Assert-PolicyBenchmarkEvidence `
        $calibrationDirectory $sampleFrames 20260830 $commit $unity `
        $pipelineHash $shaderHash $measurementHash $protocol `
        $leftCalibration $rightCalibration
    $candidateGate = Test-PolicyCandidateGate `
        $calibrationEvidence $leftCalibration $rightCalibration Upload
    $expectedPairedRows = 4 * $sampleFrames
    if (-not $candidateGate.accepted -or
        $candidateGate.comparisons.totalCpuMs.positiveWins -ne
            $expectedPairedRows -or
        $candidateGate.comparisons.totalCpuMs.pairedDelta.count -ne
            $expectedPairedRows) {
        throw 'Synthetic candidate comparison did not pass exact paired gates.'
    }

    $gpuCoverageDirectory =
        Join-Path $temporaryRoot 'gpu-frame-coverage-boundary'
    Write-SyntheticEvidence $gpuCoverageDirectory 20260830 `
        $leftCalibration $rightCalibration -GpuFrameCoverageBoundary
    $gpuCoverageEvidence = Assert-PolicyBenchmarkEvidence `
        $gpuCoverageDirectory $sampleFrames 20260830 $commit $unity `
        $pipelineHash $shaderHash $measurementHash $protocol `
        $leftCalibration $rightCalibration
    $gpuCoverageComparison = Get-PolicyPairedComparison `
        -Rows $gpuCoverageEvidence.raw `
        -BaselineCaseId $leftCalibration `
        -CandidateCaseId $rightCalibration `
        -Metric gpuFrameMs
    if ($gpuCoverageComparison.totalPairCount -ne $expectedPairedRows -or
        $gpuCoverageComparison.pairCount -ne
            [int](0.9 * $expectedPairedRows) -or
        $gpuCoverageComparison.unavailablePairCount -ne
            [int](0.1 * $expectedPairedRows) -or
        $gpuCoverageComparison.pairCoveragePercent -ne 90.0 -or
        $gpuCoverageComparison.minimumPairCoveragePercent -ne 90.0 -or
        @($gpuCoverageEvidence.raw | Where-Object {
            [int]$_.gpuFrameValid -eq 0 -and
            [string]$_.gpuFrameMs -cne 'unavailable'
        }).Count -ne 0) {
        throw 'GPU-frame boundary evidence did not retain exact availability receipts.'
    }

    $badGpuLiteralDirectory =
        Join-Path $temporaryRoot 'gpu-frame-fabricated-zero'
    Copy-Item -LiteralPath $gpuCoverageDirectory `
        -Destination $badGpuLiteralDirectory -Recurse
    $badGpuLiteralPath =
        Join-Path $badGpuLiteralDirectory 'raw-frames.csv'
    $badGpuLiteralRows = @(Import-Csv -LiteralPath $badGpuLiteralPath)
    $badGpuLiteralRow = @($badGpuLiteralRows | Where-Object {
        [int]$_.gpuFrameValid -eq 0
    })[0]
    $badGpuLiteralRow.gpuFrameMs = 0
    $badGpuLiteralRows | Export-Csv -LiteralPath $badGpuLiteralPath `
        -NoTypeInformation -Encoding utf8
    Assert-Throws {
        $null = Assert-PolicyBenchmarkEvidence `
            $badGpuLiteralDirectory $sampleFrames 20260830 $commit $unity `
            $pipelineHash $shaderHash $measurementHash $protocol `
            $leftCalibration $rightCalibration
    } 'Fabricated zero for unavailable GPU frame'

    $badGpuBlockDirectory =
        Join-Path $temporaryRoot 'gpu-frame-below-block-coverage'
    Copy-Item -LiteralPath $gpuCoverageDirectory `
        -Destination $badGpuBlockDirectory -Recurse
    $badGpuBlockRawPath =
        Join-Path $badGpuBlockDirectory 'raw-frames.csv'
    $badGpuBlockRows = @(Import-Csv -LiteralPath $badGpuBlockRawPath)
    $extraUnavailable = @($badGpuBlockRows | Where-Object {
        [int]$_.blockIndex -eq 1 -and [int]$_.sampleIndex -eq 3
    })[0]
    $extraUnavailable.gpuFrameValid = 0
    $extraUnavailable.gpuFrameMs = 'unavailable'
    $badGpuBlockRows | Export-Csv -LiteralPath $badGpuBlockRawPath `
        -NoTypeInformation -Encoding utf8
    $badGpuBlockSummaryPath =
        Join-Path $badGpuBlockDirectory 'block-summary.csv'
    $badGpuBlockSummaries = @(
        Import-Csv -LiteralPath $badGpuBlockSummaryPath)
    $badGpuBlockSummaries[0].gpuFrameValidCount = $sampleFrames - 2
    $badGpuBlockSummaries | Export-Csv `
        -LiteralPath $badGpuBlockSummaryPath `
        -NoTypeInformation -Encoding utf8
    $badGpuRunSummaryPath =
        Join-Path $badGpuBlockDirectory 'run-summary.txt'
    $badGpuRunSummary = @(Get-Content -LiteralPath $badGpuRunSummaryPath |
        ForEach-Object {
            if ($_ -like 'gpuFrameReadyRows=*') {
                'gpuFrameReadyRows=' + (8 * $sampleFrames - 9)
            }
            elseif ($_ -like 'gpuFrameUnavailableRows=*') {
                'gpuFrameUnavailableRows=9'
            }
            else { $_ }
        })
    [IO.File]::WriteAllLines(
        $badGpuRunSummaryPath,
        $badGpuRunSummary,
        [Text.UTF8Encoding]::new($false))
    Assert-Throws {
        $null = Assert-PolicyBenchmarkEvidence `
            $badGpuBlockDirectory $sampleFrames 20260830 $commit $unity `
            $pipelineHash $shaderHash $measurementHash $protocol `
            $leftCalibration $rightCalibration
    } 'GPU frame below 95 percent block coverage'

    $badGpuPairRows = @($gpuCoverageEvidence.raw | ForEach-Object {
        $_ | Select-Object -Property *
    })
    $extraPairUnavailable = @($badGpuPairRows | Where-Object {
        [string]$_.caseId -ceq $rightCalibration -and
        [int]$_.pairIndex -eq 1 -and [int]$_.sampleIndex -eq 3
    })[0]
    $extraPairUnavailable.gpuFrameValid = 0
    $extraPairUnavailable.gpuFrameMs = 'unavailable'
    Assert-Throws {
        $null = Get-PolicyPairedComparison `
            -Rows $badGpuPairRows `
            -BaselineCaseId $leftCalibration `
            -CandidateCaseId $rightCalibration `
            -Metric gpuFrameMs
    } 'GPU frame below 90 percent paired coverage'

    $badCpuCoverageDirectory =
        Join-Path $temporaryRoot 'cpu-frame-incomplete-coverage'
    Copy-Item -LiteralPath $calibrationDirectory `
        -Destination $badCpuCoverageDirectory -Recurse
    $badCpuRawPath = Join-Path $badCpuCoverageDirectory 'raw-frames.csv'
    $badCpuRows = @(Import-Csv -LiteralPath $badCpuRawPath)
    $badCpuRows[0].cpuFrameMs = 'unavailable'
    $badCpuRows | Export-Csv -LiteralPath $badCpuRawPath `
        -NoTypeInformation -Encoding utf8
    Assert-Throws {
        $null = Assert-PolicyBenchmarkEvidence `
            $badCpuCoverageDirectory $sampleFrames 20260830 $commit $unity `
            $pipelineHash $shaderHash $measurementHash $protocol `
            $leftCalibration $rightCalibration
    } 'Incomplete non-GPU-frame metric coverage'

    $firstBaselineRow = @($calibrationEvidence.raw | Where-Object {
        [string]$_.caseId -ceq $leftCalibration
    })[0]
    $firstCandidateRow = @($calibrationEvidence.raw | Where-Object {
        [string]$_.caseId -ceq $rightCalibration
    })[0]
    $duplicatedPairRows = @($calibrationEvidence.raw) +
        @($firstBaselineRow, $firstCandidateRow)
    Assert-Throws {
        $null = Get-PolicyPairedComparison `
            -Rows $duplicatedPairRows `
            -BaselineCaseId $leftCalibration `
            -CandidateCaseId $rightCalibration `
            -Metric totalCpuMs
    } 'Duplicated paired baseline/candidate rows'
    $corruptParityRows = @($calibrationEvidence.raw | ForEach-Object {
        $_ | Select-Object -Property *
    })
    $corruptCandidateRow = @($corruptParityRows | Where-Object {
        [string]$_.caseId -ceq $rightCalibration
    })[0]
    $corruptCandidateRow.expectedStateHash = 'CORRUPT-STATE'
    Assert-Throws {
        $null = Get-PolicyPairedComparison `
            -Rows $corruptParityRows `
            -BaselineCaseId $leftCalibration `
            -CandidateCaseId $rightCalibration `
            -Metric totalCpuMs
    } 'Corrupt paired logical input'

    $oldSchemaDirectory = Join-Path $temporaryRoot 'old-validation-schema'
    Copy-Item -LiteralPath $calibrationDirectory `
        -Destination $oldSchemaDirectory -Recurse
    $oldValidationPath = Join-Path $oldSchemaDirectory 'validation.csv'
    $oldValidationRows = @(Import-Csv -LiteralPath $oldValidationPath |
        Select-Object -Property * -ExcludeProperty engineIndirectExact)
    $oldValidationRows | Export-Csv -LiteralPath $oldValidationPath `
        -NoTypeInformation -Encoding utf8
    Assert-Throws {
        $null = Assert-PolicyBenchmarkEvidence `
            -Directory $oldSchemaDirectory `
            -ExpectedSampleFrames $sampleFrames `
            -ExpectedSeed 20260830 `
            -ExpectedBuildCommit $commit `
            -ExpectedUnityVersion $unity `
            -ExpectedPipelineFingerprint $pipelineHash `
            -ExpectedShaderFingerprint $shaderHash `
            -ExpectedMeasurementFingerprint $measurementHash `
            -ExpectedCalibrationProtocol $protocol `
            -ExpectedLeftCaseId $leftCalibration `
            -ExpectedRightCaseId $rightCalibration
    } 'Legacy validation schema'

    $replayDirectory = Join-Path $temporaryRoot 'replay'
    $forcedCase = 'gpu-driven-policy/forced-selected'
    $autoCase = 'gpu-driven-policy/actual-auto'
    Write-SyntheticEvidence $replayDirectory 20260833 `
        $forcedCase $autoCase -AcceptedProfile
    $replayEvidence = Assert-PolicyBenchmarkEvidence `
        $replayDirectory $sampleFrames 20260833 $commit $unity `
        $pipelineHash $shaderHash $measurementHash $protocol `
        $forcedCase $autoCase -RequireAcceptedProfile
    $badSelectorClockDirectory =
        Join-Path $temporaryRoot 'bad-selector-clock'
    Copy-Item -LiteralPath $replayDirectory `
        -Destination $badSelectorClockDirectory -Recurse
    $badSelectorClockPath =
        Join-Path $badSelectorClockDirectory 'raw-frames.csv'
    $badSelectorClockRows = @(Import-Csv -LiteralPath $badSelectorClockPath)
    $badSelectorClockRow = @($badSelectorClockRows | Where-Object {
        [string]$_.caseId -ceq $autoCase
    })[0]
    $badSelectorClockRow.selectorCpuTicks =
        [int64]$badSelectorClockRow.selectorCpuTicks + 1L
    $badSelectorClockRows | Export-Csv `
        -LiteralPath $badSelectorClockPath -NoTypeInformation -Encoding utf8
    Assert-Throws {
        Assert-PolicyBenchmarkEvidence `
            $badSelectorClockDirectory $sampleFrames 20260833 `
            $commit $unity $pipelineHash $shaderHash `
            $measurementHash $protocol $forcedCase $autoCase `
            -RequireAcceptedProfile
    } 'Selector tick-to-millisecond cross-check'
    $replay = Test-PolicyReplayEquivalence $replayEvidence
    if (-not $replay.accepted -or $replay.decisionMismatchCount -ne 0 -or
        [double]$replay.selectorCpuMs.forcedSelected.mean -ne 0.0 -or
        [double]$replay.selectorCpuMs.actualAuto.mean -le 0.0 -or
        [double]$replay.selectorOverheadThresholds.maximumMeanMs -ne 0.01 -or
        [double]$replay.selectorOverheadThresholds.maximumP99Ms -ne 0.05 -or
        @($replay.selectorOverheadFailures).Count -ne 0) {
        throw 'Synthetic ActualAuto replay did not equal ForcedSelected.'
    }
    $replayEvidence.raw[0].decisionUploadMode = 'Full'
    $mismatch = Test-PolicyReplayEquivalence $replayEvidence
    if ($mismatch.accepted -or $mismatch.decisionMismatchCount -eq 0) {
        throw 'Replay helper accepted a per-row policy decision mismatch.'
    }
    $replayEvidence.raw[0].decisionUploadMode = 'Dirty'
    foreach ($row in @($replayEvidence.raw | Where-Object {
            [string]$_.caseId -ceq $autoCase
        })) {
        $row.totalCpuMs = 100.0
        $row.cpuFrameMs = 103.0
        $row.cpuMainThreadFrameMs = 102.0
    }
    $diagnosticTail = Test-PolicyReplayEquivalence $replayEvidence
    if (-not $diagnosticTail.accepted -or
        @($diagnosticTail.observedDiagnosticTailRegressions).Count -eq 0) {
        throw 'Replay helper did not retain whole-frame tail diagnostics.'
    }
    $autoSelectorRows = @($replayEvidence.raw | Where-Object {
        [string]$_.caseId -ceq $autoCase
    })
    foreach ($row in $autoSelectorRows) {
        $row.totalCpuMs = 1.0
        $row.cpuFrameMs = 4.0
        $row.cpuMainThreadFrameMs = 3.0
        $row.selectorCpuMs = 0.001
    }
    $autoSelectorRows[0].selectorCpuMs = 0.05
    $autoSelectorRows[1].selectorCpuMs = 0.05
    $selectorBoundary = Test-PolicyReplayEquivalence $replayEvidence
    if (-not $selectorBoundary.accepted) {
        throw 'Replay helper rejected the frozen selector P99 boundary.'
    }
    $autoSelectorRows[0].selectorCpuMs = 0.050000002
    $autoSelectorRows[1].selectorCpuMs = 0.050000002
    $selectorP99Regression = Test-PolicyReplayEquivalence $replayEvidence
    if ($selectorP99Regression.accepted -or
        $selectorP99Regression.selectorOverheadFailures -cnotcontains
            'actual-auto-selector-p99-above-0.05ms') {
        throw 'Replay helper accepted selector P99 above the frozen gate.'
    }
    foreach ($row in $autoSelectorRows) {
        $row.selectorCpuMs = 0.011
    }
    $selectorMeanRegression = Test-PolicyReplayEquivalence $replayEvidence
    if ($selectorMeanRegression.accepted -or
        $selectorMeanRegression.selectorOverheadFailures -cnotcontains
            'actual-auto-selector-mean-above-0.01ms' -or
        $selectorMeanRegression.selectorOverheadFailures -ccontains
            'actual-auto-selector-p99-above-0.05ms') {
        throw 'Replay helper accepted selector mean above the frozen gate.'
    }
    foreach ($row in $autoSelectorRows) {
        $row.selectorCpuMs = 0.001
    }
    $forcedSelectorRows = @($replayEvidence.raw | Where-Object {
        [string]$_.caseId -ceq $forcedCase
    })
    foreach ($row in $forcedSelectorRows) {
        $row.selectorCpuMs = 0.001
    }
    $forcedSelectorRegression = Test-PolicyReplayEquivalence $replayEvidence
    if ($forcedSelectorRegression.accepted -or
        $forcedSelectorRegression.selectorOverheadFailures -cnotcontains
            'forced-selected-selector-time-nonzero') {
        throw 'Replay helper accepted nonzero ForcedSelected selector time.'
    }
    foreach ($row in $forcedSelectorRows) {
        $row.selectorCpuMs = 0.0
    }
    $replayEvidence.selectorOverhead.allocatedBytes = 1
    $isolatedSelectorRegression =
        Test-PolicyReplayEquivalence $replayEvidence
    if ($isolatedSelectorRegression.accepted -or
        $isolatedSelectorRegression.selectorOverheadFailures -cnotcontains
            'isolated-selector-contract-not-satisfied') {
        throw 'Replay helper accepted allocating isolated selector evidence.'
    }
    $replayEvidence.selectorOverhead.allocatedBytes = 0

    $endToEndDirectory = Join-Path $temporaryRoot 'replay-end-to-end'
    Write-SyntheticEvidence $endToEndDirectory 20260833 `
        $leftCalibration $autoCase -AcceptedProfile
    $endToEndEvidence = Assert-PolicyBenchmarkEvidence `
        $endToEndDirectory $sampleFrames 20260833 $commit $unity `
        $pipelineHash $shaderHash $measurementHash $protocol `
        $leftCalibration $autoCase -RequireAcceptedProfile
    $acceptedEndToEnd = Test-PolicyEndToEndReplay `
        -Evidence $endToEndEvidence `
        -CandidateKind Upload `
        -CandidateAccepted $true `
        -ExpectedRuleId synthetic-rule `
        -ExpectedUploadMode Dirty `
        -ExpectedOutputMode VisibleOnly `
        -ExpectedCullingMode Flat
    if (-not $acceptedEndToEnd.accepted -or
        -not $acceptedEndToEnd.performanceGate.accepted -or
        $acceptedEndToEnd.performanceGate.comparisons.totalCpuMs.
            positiveWinPercent -ne 100.0) {
        throw 'Accepted rule did not repeat the full end-to-end gate.'
    }

    $noGainRows = @($endToEndEvidence.raw | ForEach-Object {
        $_ | Select-Object -Property *
    })
    foreach ($row in @($noGainRows | Where-Object {
            [string]$_.caseId -ceq $autoCase
        })) {
        $row.totalCpuMs = 2.0
    }
    $noGainEndToEnd = Test-PolicyEndToEndReplay `
        -Evidence ([pscustomobject]@{ raw = $noGainRows }) `
        -CandidateKind Upload `
        -CandidateAccepted $true `
        -ExpectedRuleId synthetic-rule `
        -ExpectedUploadMode Dirty `
        -ExpectedOutputMode VisibleOnly `
        -ExpectedCullingMode Flat
    if ($noGainEndToEnd.accepted -or
        $noGainEndToEnd.performanceGate.accepted) {
        throw 'Accepted rule bypassed the end-to-end mean/win gate.'
    }

    $safeRejectedRows = @($endToEndEvidence.raw | ForEach-Object {
        $_ | Select-Object -Property *
    })
    foreach ($row in @($safeRejectedRows | Where-Object {
            [string]$_.caseId -ceq $autoCase
        })) {
        $row.decisionUploadMode = 'Full'
    }
    $safeRejectedEvidence = [pscustomobject]@{ raw = $safeRejectedRows }
    $safeRejectedEndToEnd = Test-PolicyEndToEndReplay `
        -Evidence $safeRejectedEvidence `
        -CandidateKind Upload `
        -CandidateAccepted $false `
        -ExpectedRuleId synthetic-rule `
        -ExpectedUploadMode Full `
        -ExpectedOutputMode VisibleOnly `
        -ExpectedCullingMode Flat
    if (-not $safeRejectedEndToEnd.accepted -or
        -not $safeRejectedEndToEnd.safeRejectedDecision) {
        throw 'Measured safe rejected rule did not pass end-to-end replay.'
    }
    $unsafeRejectedEndToEnd = Test-PolicyEndToEndReplay `
        -Evidence $endToEndEvidence `
        -CandidateKind Upload `
        -CandidateAccepted $false `
        -ExpectedRuleId synthetic-rule `
        -ExpectedUploadMode Dirty `
        -ExpectedOutputMode VisibleOnly `
        -ExpectedCullingMode Flat
    if ($unsafeRejectedEndToEnd.accepted -or
        $unsafeRejectedEndToEnd.safeRejectedDecision) {
        throw 'Rejected rule accepted a non-Full automatic decision.'
    }

    $tailRegressionRows = @($safeRejectedRows | ForEach-Object {
        $_ | Select-Object -Property *
    })
    $baselineByKey = @{}
    foreach ($row in @($tailRegressionRows | Where-Object {
            [string]$_.caseId -ceq $leftCalibration
        })) {
        $baselineByKey[([string]$row.pairIndex + ':' +
            [string]$row.sampleIndex)] = $row
    }
    foreach ($row in @($tailRegressionRows | Where-Object {
            [string]$_.caseId -ceq $autoCase
        })) {
        $baseline = $baselineByKey[([string]$row.pairIndex + ':' +
            [string]$row.sampleIndex)]
        foreach ($metric in @(
                'totalCpuMs', 'gpuRegionElapsedMs', 'cpuFrameMs',
                'cpuMainThreadFrameMs', 'cpuRenderThreadFrameMs',
                'gpuFrameMs', 'cpuSubmissionWindowMs')) {
            $row.PSObject.Properties[$metric].Value =
                [double]$baseline.PSObject.Properties[$metric].Value + 1.0
        }
    }
    $tailRegressionEndToEnd = Test-PolicyEndToEndReplay `
        -Evidence ([pscustomobject]@{ raw = $tailRegressionRows }) `
        -CandidateKind Upload `
        -CandidateAccepted $false `
        -ExpectedRuleId synthetic-rule `
        -ExpectedUploadMode Full `
        -ExpectedOutputMode VisibleOnly `
        -ExpectedCullingMode Flat
    if ($tailRegressionEndToEnd.accepted -or
        @($tailRegressionEndToEnd.materialTailFailures).Count -eq 0) {
        throw 'Rejected rule bypassed the material-tail replay gate.'
    }

    $manifest = [ordered]@{
        schemaVersion = 1
        suite = 'summit.gpu-driven-instance-policy-selection'
        sourceCommit = $commit
        sourceSnapshotSha256 = '1' * 64
        unityVersion = $unity
        pipelineContractFingerprint = $pipelineHash
        shaderContractFingerprint = $shaderHash
        measurementContractFingerprint = $measurementHash
        calibrationProtocol = $protocol
        calibrationSeed = 20260830
        holdoutSeed = 20260831
        replaySeed = 20260833
        cells = @([ordered]@{
            ruleId = 'synthetic-rule'
            candidateKind = 'Upload'
            sampleFrames = $sampleFrames
            baselineCaseId = $leftCalibration
            candidateCaseId = $rightCalibration
            candidateUploadMode = 'Dirty'
            candidateCullingMode = 'Flat'
            calibrationDirectory = $calibrationDirectory
            holdoutDirectory = $holdoutDirectory
        })
    }
    $baseManifestJson = $manifest | ConvertTo-Json -Depth 20
    $manifestPath = Join-Path $temporaryRoot 'selection-manifest.json'
    [IO.File]::WriteAllText(
        $manifestPath,
        $baseManifestJson,
        [Text.UTF8Encoding]::new($false))
    $profilePath = Join-Path $temporaryRoot 'profile.json'
    $selectionPath = Join-Path $temporaryRoot 'selection.json'
    [void](& $selectorPath $manifestPath $profilePath $selectionPath)
    $profile = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
    $selectionReceipt = Get-Content -LiteralPath $selectionPath -Raw |
        ConvertFrom-Json
    if ([int]$profile.rules.Count -ne 1 -or
        [int]$profile.rules[0].uploadMode -ne 1 -or
        [int]$profile.rules[0].cullingMode -ne 0 -or
        [int]$profile.rules[0].primitiveBackend -ne 1 -or
        [string]$selectionReceipt.cells[0].selectedDecisionSource -cne
            'measured-candidate' -or
        [string]$selectionReceipt.cells[0].holdoutCandidateDecision.uploadMode `
            -cne 'Dirty' -or
        [string]$profile.sourceCommit -notmatch '^[0-9a-f]{40}$' -or
        [string]$profile.measurementContractFingerprint -notmatch
            '^[0-9A-F]{64}$' -or
        [string]$profile.holdoutEvidenceSetId -notmatch
            '^[0-9A-F]{64}$' -or
        -not [bool]$profile.holdoutAccepted -or
        [string]$profile.unityVersion -cne $unity -or
        [string]$profile.device.graphicsApi -cne 'Direct3D12') {
        throw 'Profile generator did not use the measured candidate decision.'
    }
    foreach ($rangeProperty in @(
            'minActiveInstanceCount', 'maxActiveInstanceCount',
            'minDirtyBasisPoints', 'maxDirtyBasisPoints',
            'minVisibleBasisPoints', 'maxVisibleBasisPoints',
            'minViewCount', 'maxViewCount',
            'minUploadCallCount', 'maxUploadCallCount',
            'minUploadAmplificationBasisPoints',
            'maxUploadAmplificationBasisPoints',
            'minHierarchyCandidateBasisPoints',
            'maxHierarchyCandidateBasisPoints',
            'minClusterCount', 'maxClusterCount')) {
        if ($null -eq $profile.rules[0].enter.PSObject.Properties[
                $rangeProperty] -or
            $null -eq $profile.rules[0].exit.PSObject.Properties[
                $rangeProperty]) {
            throw "Profile rule omitted '$rangeProperty'."
        }
    }
    $profileGeneratedUtc = [DateTimeOffset]$profile.generatedUtc
    if ($profileGeneratedUtc.Offset -ne [TimeSpan]::Zero) {
        throw 'Profile generatedUtc is not canonical UTC provenance.'
    }

    $hierarchyCalibrationDirectory =
        Join-Path $temporaryRoot 'hierarchy-zero-dirty-calibration'
    $hierarchyHoldoutDirectory =
        Join-Path $temporaryRoot 'hierarchy-zero-dirty-holdout'
    Copy-SyntheticZeroDirtyHierarchyEvidence `
        $calibrationDirectory $hierarchyCalibrationDirectory `
        $rightCalibration
    Copy-SyntheticZeroDirtyHierarchyEvidence `
        $holdoutDirectory $hierarchyHoldoutDirectory `
        $rightCalibration
    $hierarchyManifest = $baseManifestJson | ConvertFrom-Json
    $hierarchyManifest.cells[0].candidateKind = 'Hierarchy'
    $hierarchyManifest.cells[0].candidateUploadMode = 'Full'
    $hierarchyManifest.cells[0].candidateCullingMode = 'Hierarchy'
    $hierarchyManifest.cells[0].calibrationDirectory =
        $hierarchyCalibrationDirectory
    $hierarchyManifest.cells[0].holdoutDirectory =
        $hierarchyHoldoutDirectory
    $hierarchyManifestPath =
        Join-Path $temporaryRoot 'hierarchy-zero-dirty-manifest.json'
    [IO.File]::WriteAllText(
        $hierarchyManifestPath,
        ($hierarchyManifest | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))
    $hierarchyProfilePath =
        Join-Path $temporaryRoot 'hierarchy-zero-dirty-profile.json'
    $hierarchySelectionPath =
        Join-Path $temporaryRoot 'hierarchy-zero-dirty-selection.json'
    $null = & $selectorPath `
        $hierarchyManifestPath `
        $hierarchyProfilePath `
        $hierarchySelectionPath
    $hierarchyProfile = Get-Content -LiteralPath $hierarchyProfilePath -Raw |
        ConvertFrom-Json
    if ([int]$hierarchyProfile.rules[0].uploadMode -ne 2 -or
        [int]$hierarchyProfile.rules[0].cullingMode -ne 1 -or
        [int]$hierarchyProfile.rules[0].enter.minUploadAmplificationBasisPoints `
            -ne 0) {
        throw 'Zero-dirty Full+Hierarchy calibration did not remain selectable.'
    }

    $slowDirectory = Join-Path $temporaryRoot 'holdout-slow'
    Write-SyntheticEvidence $slowDirectory 20260831 `
        $leftCalibration $rightCalibration -SlowCandidate
    $slowManifest = $baseManifestJson | ConvertFrom-Json
    $slowManifest.cells[0].holdoutDirectory = $slowDirectory
    $slowManifestPath = Join-Path $temporaryRoot 'slow-manifest.json'
    [IO.File]::WriteAllText(
        $slowManifestPath,
        ($slowManifest | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))
    $safeProfilePath = Join-Path $temporaryRoot 'safe-profile.json'
    $safeSelectionPath = Join-Path $temporaryRoot 'safe-selection.json'
    [void](& $selectorPath `
        $slowManifestPath $safeProfilePath $safeSelectionPath)
    $safeProfile = Get-Content -LiteralPath $safeProfilePath -Raw |
        ConvertFrom-Json
    $safeSelection = Get-Content -LiteralPath $safeSelectionPath -Raw |
        ConvertFrom-Json
    if ([bool]$safeSelection.cells[0].candidateAccepted -or
        [string]$safeSelection.cells[0].selectedDecisionSource -cne
            'measured-baseline' -or
        [int]$safeProfile.rules[0].uploadMode -ne 2 -or
        [int]$safeProfile.rules[0].cullingMode -ne 0 -or
        -not (Test-Path -LiteralPath (
            Join-Path $slowDirectory 'raw-frames.csv'))) {
        throw 'Rejected candidate did not retain evidence and reuse baseline.'
    }

    $badManifest = $baseManifestJson | ConvertFrom-Json
    $badManifest.cells[0].candidateUploadMode = 'Full'
    $badManifestPath = Join-Path $temporaryRoot 'bad-manifest.json'
    [IO.File]::WriteAllText(
        $badManifestPath,
        ($badManifest | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))
    $rejected = $false
    try {
        [void](& $selectorPath `
            $badManifestPath `
            (Join-Path $temporaryRoot 'bad-profile.json') `
            (Join-Path $temporaryRoot 'bad-selection.json'))
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw 'Manifest/raw candidate decision disagreement was not rejected.'
    }

    $kindMismatchManifest = $baseManifestJson | ConvertFrom-Json
    $kindMismatchManifest.cells[0].candidateKind = 'Hierarchy'
    $kindMismatchManifestPath =
        Join-Path $temporaryRoot 'kind-mismatch-manifest.json'
    [IO.File]::WriteAllText(
        $kindMismatchManifestPath,
        ($kindMismatchManifest | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))
    Assert-Throws {
        $null = & $selectorPath `
            $kindMismatchManifestPath `
            (Join-Path $temporaryRoot 'kind-mismatch-profile.json') `
            (Join-Path $temporaryRoot 'kind-mismatch-selection.json')
    } 'Candidate-kind/raw-axis mismatch'

    $sameCalibrationDirectory =
        Join-Path $temporaryRoot 'same-decision-calibration'
    $sameHoldoutDirectory = Join-Path $temporaryRoot 'same-decision-holdout'
    Copy-SyntheticDecisionEvidence `
        $calibrationDirectory $sameCalibrationDirectory `
        $rightCalibration Full Flat
    Copy-SyntheticDecisionEvidence `
        $holdoutDirectory $sameHoldoutDirectory `
        $rightCalibration Full Flat
    $sameDecisionManifest = $baseManifestJson | ConvertFrom-Json
    $sameDecisionManifest.cells[0].calibrationDirectory =
        $sameCalibrationDirectory
    $sameDecisionManifest.cells[0].holdoutDirectory = $sameHoldoutDirectory
    $sameDecisionManifest.cells[0].candidateUploadMode = 'Full'
    $sameDecisionManifestPath =
        Join-Path $temporaryRoot 'same-decision-manifest.json'
    [IO.File]::WriteAllText(
        $sameDecisionManifestPath,
        ($sameDecisionManifest | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))
    Assert-Throws {
        $null = & $selectorPath `
            $sameDecisionManifestPath `
            (Join-Path $temporaryRoot 'same-decision-profile.json') `
            (Join-Path $temporaryRoot 'same-decision-selection.json')
    } 'Same-decision Upload candidate'

    $multiAxisCalibrationDirectory =
        Join-Path $temporaryRoot 'multi-axis-calibration'
    $multiAxisHoldoutDirectory =
        Join-Path $temporaryRoot 'multi-axis-holdout'
    Copy-SyntheticDecisionEvidence `
        $calibrationDirectory $multiAxisCalibrationDirectory `
        $rightCalibration Dirty Hierarchy
    Copy-SyntheticDecisionEvidence `
        $holdoutDirectory $multiAxisHoldoutDirectory `
        $rightCalibration Dirty Hierarchy
    $multiAxisManifest = $baseManifestJson | ConvertFrom-Json
    $multiAxisManifest.cells[0].calibrationDirectory =
        $multiAxisCalibrationDirectory
    $multiAxisManifest.cells[0].holdoutDirectory =
        $multiAxisHoldoutDirectory
    $multiAxisManifest.cells[0].candidateCullingMode = 'Hierarchy'
    $multiAxisManifestPath =
        Join-Path $temporaryRoot 'multi-axis-manifest.json'
    [IO.File]::WriteAllText(
        $multiAxisManifestPath,
        ($multiAxisManifest | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))
    Assert-Throws {
        $null = & $selectorPath `
            $multiAxisManifestPath `
            (Join-Path $temporaryRoot 'multi-axis-profile.json') `
            (Join-Path $temporaryRoot 'multi-axis-selection.json')
    } 'Multi-axis Upload candidate'
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
}

Write-Host (
    'GPU-driven instance policy tooling provenance, synthetic evidence, ' +
    'selection, and replay tests passed.')
