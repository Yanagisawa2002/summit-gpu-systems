[CmdletBinding()]
param(
    [string]$UnityPath =
        'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe',
    [string]$OutputDirectory,
    [string]$PlayerPath,
    [ValidateRange(0, 16)]
    [int]$DeviceIndex = 0,
    [ValidateSet('SingleScenario', 'FormalMatrix')]
    [string]$Workflow = 'SingleScenario',
    [ValidateRange(0, 1800)]
    [int]$WarmupFrames = 30,
    [ValidateRange(30, 7200)]
    [int]$SampleFrames = 60,
    [ValidateRange(5, 240)]
    [int]$PlayerTimeoutMinutes = 90,
    [ValidateRange(5, 180)]
    [int]$EditModeTimeoutMinutes = 60,
    # Deprecated and never accepted as a formal receipt. The runner always
    # creates a commit-bound XML receipt with the frozen Unity executable.
    [string]$EditModeResultsPath,
    [ValidateRange(1, 16777216)]
    [int]$InstanceCount = 100000,
    [ValidateRange(1, 32)]
    [int]$ViewCount = 1,
    [ValidateSet(500, 2500, 7500, 10000)]
    [int]$VisibilityBasisPoints = 2500,
    [ValidateRange(0, 10000)]
    [int]$DirtyBasisPoints = 1000,
    [int]$Seed = 20260830,
    [ValidateSet(
        'safe-baseline',
        'full-flat', 'full-hierarchy', 'dirty-flat', 'dirty-hierarchy',
        'none-flat', 'none-hierarchy', 'forced-selected', 'actual-auto')]
    [string]$LeftCase = 'full-flat',
    [ValidateSet(
        'safe-baseline',
        'full-flat', 'full-hierarchy', 'dirty-flat', 'dirty-hierarchy',
        'none-flat', 'none-hierarchy', 'forced-selected', 'actual-auto')]
    [string]$RightCase = 'dirty-flat',
    [ValidateSet('visible-only', 'culled-tail')]
    [string]$RequiredOutput = 'visible-only',
    [string]$ProfilePath,
    [switch]$Resume,
    [switch]$RecoverInterrupted
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$expectedUnityVersion = '6000.5.2f1'
$calibrationSeed = 20260830
$holdoutSeed = 20260831
$replaySeed = 20260833
$calibrationProtocol =
    'gpu-driven-policy-upload-culling-v2-holdout-v1-replay-v2-checkpoint-v1'
$protocolAmendmentReason =
    'two-measured-axes-plus-atomic-resumable-phase-evidence'
$formalSampleFrames = 900
$formalWarmupFrames = 60
$playerWindowContract = 'visible-windowed-swapchain-v1'
$suite = 'summit.gpu-driven-instance-policy-runner'
$measurementContract = @'
summit.gpu-driven-instance-policy.measurement.v1
schedule=ABBA;BAAB
blocks=8
playerWindow=visible-windowed-swapchain-v1;WindowStyle-Hidden-forbidden
frameTimingLatency=4
gpuFrameUnavailableLiteral=unavailable
gpuFrameBlockValidCoverage>=95%
gpuFramePairedComparisonCoverage>=90%
otherTimedMetricCoverage=100%
nativeTimestampAbi=2
nativeTimestampCapabilities=0x1f
timedAllocationBytes=0
timedMeasurementReadbackBytes=0
timedSlotWaitFrames=0
validationPhases=warmup-left,warmup-right,final-left,final-right
engineIndirectArgumentsExact=1
renderTargetNonBlackHash=1
validationLifecycle=single-pending-owner;timeout-fail-closed;dispose-requires-none
selectorIterations=100000
selectorAllocatedBytes=0
selectorUnstableDecisions=0
candidateGate=mean>=2%;wins>=55%;primaryP95<=5%;primaryP99<=10%
replayGate=decision-exact;selector-mean<=0.01ms;selector-p99<=0.05ms;isolated-iterations=100000;isolated-allocated=0;isolated-unstable=0
endToEndReplayGate=accepted:candidate-gate;rejected:full-flat-portable+no-material-p99-regression
'@

$projectRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent $PSScriptRoot))
$provenanceModulePath = Join-Path $PSScriptRoot 'GpuBenchmarkProvenance.psm1'
$checkpointModulePath = Join-Path $PSScriptRoot 'GpuBenchmarkCheckpoint.psm1'
$policyModulePath =
    Join-Path $PSScriptRoot 'GpuDrivenInstancePolicyBenchmark.psm1'
$selectorScriptPath =
    Join-Path $PSScriptRoot 'Select-GpuDrivenInstancePolicyProfile.ps1'
$runnerTestPath = Join-Path $PSScriptRoot (
    'Tests\Test-GpuDrivenInstancePolicyBenchmarkProvenance.ps1')
$checkpointTestPath = Join-Path $PSScriptRoot (
    'Tests\Test-GpuBenchmarkCheckpoint.ps1')
foreach ($path in @(
        $provenanceModulePath,
        $checkpointModulePath,
        $policyModulePath,
        $selectorScriptPath,
        $runnerTestPath,
        $checkpointTestPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required policy benchmark tool is missing: $path"
    }
}
Import-Module -Name $provenanceModulePath -Force
Import-Module -Name $checkpointModulePath -Force
Import-Module -Name $policyModulePath -Force

function Quote-PolicyProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Value)
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Write-Utf8Json {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$Depth = 20
    )
    [void](Write-GpuBenchmarkAtomicJson `
        -Value $Value `
        -Path $Path `
        -Depth $Depth)
}

function Wait-PolicyProcess {
    param(
        [Parameter(Mandatory = $true)][Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)][int]$TimeoutMinutes
    )
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes($TimeoutMinutes)
    while (-not $Process.HasExited) {
        $remaining = $deadline - [DateTimeOffset]::UtcNow
        if ($remaining -le [TimeSpan]::Zero) {
            return $false
        }
        $waitMilliseconds = [int][Math]::Max(
            1.0,
            [Math]::Min(30000.0, $remaining.TotalMilliseconds))
        if ($Process.WaitForExit($waitMilliseconds)) {
            return $true
        }
    }
    return $true
}

function Get-CanonicalCaseId {
    param([Parameter(Mandatory = $true)][string]$Case)
    switch ($Case) {
        'safe-baseline' { return 'gpu-driven-policy/safe-baseline' }
        'full-flat' { return 'gpu-driven-policy/calibration/full-flat' }
        'full-hierarchy' {
            return 'gpu-driven-policy/calibration/full-hierarchy'
        }
        'dirty-flat' { return 'gpu-driven-policy/calibration/dirty-flat' }
        'dirty-hierarchy' {
            return 'gpu-driven-policy/calibration/dirty-hierarchy'
        }
        'none-flat' { return 'gpu-driven-policy/calibration/none-flat' }
        'none-hierarchy' {
            return 'gpu-driven-policy/calibration/none-hierarchy'
        }
        'forced-selected' { return 'gpu-driven-policy/forced-selected' }
        'actual-auto' { return 'gpu-driven-policy/actual-auto' }
        default { throw "Unsupported policy case '$Case'." }
    }
}

function Get-PolicySourceFiles {
    $roots = @(
        'Assets\GpuDrivenInstanceBenchmark',
        'Packages\com.summit.gpu-autotuning',
        'Packages\com.summit.gpu-driven-instances',
        'Packages\com.summit.gpu-direct-binning',
        'Packages\com.summit.gpu-primitives',
        'Packages\com.summit.gpu-timestamps') | ForEach-Object {
            Join-Path $projectRoot $_
        }
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    foreach ($root in $roots) {
        if (-not (Test-Path -LiteralPath $root -PathType Container)) {
            throw "Policy source root is missing: $root"
        }
        foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File |
                Where-Object {
                    $_.Extension -in @(
                        '.cs', '.compute', '.shader', '.asmdef', '.dll',
                        '.cpp', '.h', '.json')
                }) {
            $files.Add($file)
        }
    }
    foreach ($path in @(
            $PSCommandPath,
            $checkpointModulePath,
            $policyModulePath,
            $selectorScriptPath,
            $runnerTestPath,
            $checkpointTestPath,
            $provenanceModulePath,
            (Join-Path $projectRoot 'ProjectSettings\ProjectVersion.txt'),
            (Join-Path $projectRoot 'Packages\manifest.json'),
            (Join-Path $projectRoot 'Packages\packages-lock.json'))) {
        $files.Add((Get-Item -LiteralPath $path))
    }
    return [IO.FileInfo[]]$files.ToArray()
}

function Get-PipelineContractFiles {
    $files = @()
    foreach ($relative in @(
            'Packages\com.summit.gpu-driven-instances\Runtime',
            'Packages\com.summit.gpu-direct-binning\Runtime',
            'Packages\com.summit.gpu-primitives\Runtime')) {
        $files += Get-ChildItem -LiteralPath (Join-Path $projectRoot $relative) `
            -Recurse -File | Where-Object {
                $_.Extension -in @('.cs', '.compute', '.asmdef')
            }
    }
    return [IO.FileInfo[]]$files
}

function Get-ShaderContractFiles {
    $files = @()
    foreach ($relative in @(
            'Assets\GpuDrivenInstanceBenchmark\Runtime',
            'Packages\com.summit.gpu-driven-instances\Runtime',
            'Packages\com.summit.gpu-direct-binning\Runtime',
            'Packages\com.summit.gpu-primitives\Runtime')) {
        $files += Get-ChildItem -LiteralPath (Join-Path $projectRoot $relative) `
            -Recurse -File | Where-Object {
                $_.Extension -in @('.compute', '.shader')
            }
    }
    return [IO.FileInfo[]]$files
}

function New-FormalPolicyCells {
    $cells = [Collections.Generic.List[object]]::new()
    foreach ($dirty in @(0, 100, 1000, 10000)) {
        $candidate = if ($dirty -eq 0) { 'none-flat' } else { 'dirty-flat' }
        $upload = if ($dirty -eq 0) { 'None' } else { 'Dirty' }
        $cells.Add([pscustomobject][ordered]@{
            ruleId = "upload-n100000-v1-visible2500-dirty$dirty"
            candidateKind = 'Upload'
            instanceCount = 100000
            viewCount = 1
            visibilityBasisPoints = 2500
            dirtyBasisPoints = $dirty
            requiredOutput = 'visible-only'
            baselineCase = 'full-flat'
            candidateCase = $candidate
            candidateUploadMode = $upload
            candidateCullingMode = 'Flat'
        })
    }
    foreach ($visible in @(500, 2500, 7500, 10000)) {
        $cells.Add([pscustomobject][ordered]@{
            ruleId = "hierarchy-n1048576-v4-visible$visible-dirty0"
            candidateKind = 'Hierarchy'
            instanceCount = 1048576
            viewCount = 4
            visibilityBasisPoints = $visible
            dirtyBasisPoints = 0
            requiredOutput = 'visible-only'
            baselineCase = 'full-flat'
            candidateCase = 'full-hierarchy'
            candidateUploadMode = 'Full'
            candidateCullingMode = 'Hierarchy'
        })
    }
    return [object[]]$cells.ToArray()
}

function New-PolicyPhaseSpecification {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)][string]$Phase,
        [Parameter(Mandatory = $true)][int]$RunSeed,
        [Parameter(Mandatory = $true)][string]$RunLeftCase,
        [Parameter(Mandatory = $true)][string]$RunRightCase
    )

    return [pscustomobject][ordered]@{
        schemaVersion = 1
        phaseId = $Phase + '/' + [string]$Scenario.ruleId
        phase = $Phase
        ruleId = [string]$Scenario.ruleId
        seed = $RunSeed
        instanceCount = [int]$Scenario.instanceCount
        viewCount = [int]$Scenario.viewCount
        visibilityBasisPoints = [int]$Scenario.visibilityBasisPoints
        dirtyBasisPoints = [int]$Scenario.dirtyBasisPoints
        requiredOutput = [string]$Scenario.requiredOutput
        leftCaseId = Get-CanonicalCaseId $RunLeftCase
        rightCaseId = Get-CanonicalCaseId $RunRightCase
        requiresAcceptedProfile =
            $RunLeftCase -in @('forced-selected', 'actual-auto') -or
            $RunRightCase -in @('forced-selected', 'actual-auto')
    }
}

function Get-PolicyPhaseReceiptPath {
    param([Parameter(Mandatory = $true)][string]$PhaseId)

    $safeName = $PhaseId -replace '[^A-Za-z0-9_.-]', '--'
    $identity = Get-GpuBenchmarkObjectSha256 $PhaseId
    return Join-Path $script:phaseReceiptsRoot (
        $safeName + '--' + $identity.Substring(0, 16) + '.json')
}

function Get-PolicyEvidenceFileSetReceipt {
    param([Parameter(Mandatory = $true)][string]$Directory)

    return Get-GpuBenchmarkFileSetReceipt `
        -Root $Directory `
        -RelativePaths @(
            'run-summary.txt',
            'config.json',
            'device.json',
            'raw-frames.csv',
            'block-summary.csv',
            'validation.csv',
            'selector-overhead.csv')
}

function Get-PolicyEvidenceAssertionParameters {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)]$PhaseSpecification
    )

    $parameters = @{
        Directory = $Directory
        ExpectedSampleFrames = $SampleFrames
        ExpectedSeed = [int]$PhaseSpecification.seed
        ExpectedBuildCommit = $gitCommit
        ExpectedUnityVersion = $expectedUnityVersion
        ExpectedPipelineFingerprint = $pipelineContractFingerprint
        ExpectedShaderFingerprint = $shaderContractFingerprint
        ExpectedMeasurementFingerprint = $measurementContractFingerprint
        ExpectedCalibrationProtocol = $calibrationProtocol
        ExpectedLeftCaseId = [string]$PhaseSpecification.leftCaseId
        ExpectedRightCaseId = [string]$PhaseSpecification.rightCaseId
    }
    if ([bool]$PhaseSpecification.requiresAcceptedProfile) {
        $parameters['RequireAcceptedProfile'] = $true
    }
    return $parameters
}

function Update-PolicyRunnerProgress {
    if ($null -eq $script:runnerConfig -or
        [string]::IsNullOrWhiteSpace($script:runnerConfigPath)) {
        return
    }
    $script:runnerConfig['playerRuns'] =
        [object[]]$script:playerRuns.ToArray()
    $script:runnerConfig['checkpoint']['executedPhaseCount'] =
        $script:executedPhaseCount
    $script:runnerConfig['checkpoint']['resumedPhaseCount'] =
        $script:resumedPhaseCount
    $script:runnerConfig['checkpoint']['lastProgressUtc'] =
        (Get-Date).ToUniversalTime().ToString('O')
    Write-Utf8Json $script:runnerConfig $script:runnerConfigPath 40
}

function Read-CompletedPolicyPhase {
    param(
        [Parameter(Mandatory = $true)]$PhaseSpecification,
        [Parameter(Mandatory = $true)][string]$RunDirectory,
        [Parameter(Mandatory = $true)][string]$ReceiptPath,
        [string]$RunProfilePath
    )

    $receipt = Read-GpuBenchmarkSealedJson `
        -Path $ReceiptPath `
        -ExpectedSuite 'summit.gpu-benchmark-phase' `
        -ExpectedSchemaVersion 1
    $specificationSha256 =
        Get-GpuBenchmarkObjectSha256 $PhaseSpecification
    $expectedProfileSha256 = if (
        [bool]$PhaseSpecification.requiresAcceptedProfile) {
        (Get-FileHash -LiteralPath $RunProfilePath -Algorithm SHA256).Hash
    }
    else { '' }
    $resolvedEvidenceDirectory = [IO.Path]::GetFullPath($RunDirectory)
    $expectedScenarioId = [string]$PhaseSpecification.phase + '-' +
        [string]$PhaseSpecification.ruleId + '-seed' +
        [string]$PhaseSpecification.seed
    if (-not (Test-PolicySha256Equal `
            -Left ([string]$receipt.runContractFingerprint) `
            -Right $script:runContractFingerprint) -or
        [string]$receipt.phaseId -cne
            [string]$PhaseSpecification.phaseId -or
        [string]$receipt.scenarioId -cne $expectedScenarioId -or
        [int]$receipt.exitCode -ne 0 -or
        [string]::IsNullOrWhiteSpace([string]$receipt.startedUtc) -or
        [string]::IsNullOrWhiteSpace([string]$receipt.finishedUtc) -or
        -not (Test-PolicySha256Equal `
            -Left ([string]$receipt.phaseSpecificationSha256) `
            -Right $specificationSha256) -or
        -not (Test-PolicySha256Equal `
            -Left ([string]$receipt.playerPayloadSha256) `
            -Right ([string]$initialPayload.sha256)) -or
        -not [string]::Equals(
            [IO.Path]::GetFullPath([string]$receipt.evidenceDirectory),
            $resolvedEvidenceDirectory,
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$receipt.profileSha256 -cne $expectedProfileSha256) {
        throw "Phase receipt is not bound to the current run: $ReceiptPath"
    }
    $null = Assert-GpuBenchmarkFileSetReceipt `
        -Root $resolvedEvidenceDirectory `
        -Receipt $receipt.evidenceFileSet
    $assertionParameters = Get-PolicyEvidenceAssertionParameters `
        $resolvedEvidenceDirectory $PhaseSpecification
    $evidence = Assert-PolicyBenchmarkEvidence @assertionParameters
    if (-not (Test-PolicySha256Equal `
            -Left ([string]$receipt.evidenceSha256) `
            -Right ([string]$evidence.evidenceSha256))) {
        throw "Phase evidence identity changed after sealing: $ReceiptPath"
    }
    $currentPayload =
        Get-GpuBenchmarkPlayerPayload -PlayerPath $resolvedPlayerPath
    if ([string]$currentPayload.sha256 -cne [string]$initialPayload.sha256) {
        throw "Frozen Player payload changed before resuming '$($receipt.phaseId)'."
    }
    $script:playerRuns.Add([pscustomobject][ordered]@{
        scenarioId = [string]$receipt.scenarioId
        phaseId = [string]$receipt.phaseId
        phase = [string]$PhaseSpecification.phase
        seed = [int]$PhaseSpecification.seed
        leftCase = [string]$PhaseSpecification.leftCaseId
        rightCase = [string]$PhaseSpecification.rightCaseId
        startedUtc = [string]$receipt.startedUtc
        finishedUtc = [string]$receipt.finishedUtc
        exitCode = 0
        evidenceDirectory = $resolvedEvidenceDirectory
        evidenceSha256 = [string]$evidence.evidenceSha256
        playerPayloadSha256 = [string]$currentPayload.sha256
        phaseReceiptPath = [IO.Path]::GetFullPath($ReceiptPath)
        phaseReceiptSha256 = [string]$receipt.recordSha256
        resumed = $true
    })
    $script:resumedPhaseCount++
    Update-PolicyRunnerProgress
    return $evidence
}

function Invoke-PolicyPlayer {
    param(
        [Parameter(Mandatory = $true)]$Scenario,
        [Parameter(Mandatory = $true)][string]$Phase,
        [Parameter(Mandatory = $true)][int]$RunSeed,
        [Parameter(Mandatory = $true)][string]$RunLeftCase,
        [Parameter(Mandatory = $true)][string]$RunRightCase,
        [string]$RunProfilePath
    )

    $preRunGit = Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
    if ($preRunGit.head -ine $gitCommit -or [bool]$preRunGit.dirty) {
        throw 'Git state changed before a policy Player run.'
    }
    $preRunSourceSha256 =
        Get-PolicyCombinedSha256 (Get-PolicySourceFiles) $projectRoot
    if ($preRunSourceSha256 -cne $sourceSnapshotSha256) {
        throw 'Policy benchmark source changed before a Player run.'
    }
    $preRunPayload =
        Get-GpuBenchmarkPlayerPayload -PlayerPath $resolvedPlayerPath
    if ([string]$preRunPayload.sha256 -cne
        [string]$initialPayload.sha256) {
        throw 'Player payload changed before a policy Player run.'
    }

    $phaseSpecification = New-PolicyPhaseSpecification `
        $Scenario $Phase $RunSeed $RunLeftCase $RunRightCase
    $scenarioId = $Phase + '-' + [string]$Scenario.ruleId +
        '-seed' + [string]$RunSeed
    $runDirectory = Join-Path $outputRoot (
        (Join-Path $Phase ([string]$Scenario.ruleId)))
    $phaseReceiptPath = Get-PolicyPhaseReceiptPath `
        ([string]$phaseSpecification.phaseId)
    $requireProfile = [bool]$phaseSpecification.requiresAcceptedProfile
    if ($requireProfile) {
        if (-not (Test-Path -LiteralPath $RunProfilePath -PathType Leaf)) {
            throw "Policy profile is missing: $RunProfilePath"
        }
        if (-not [string]::IsNullOrWhiteSpace($script:frozenProfileSha256) -and
            -not (Test-PolicySha256Equal `
                -Left (Get-FileHash -LiteralPath $RunProfilePath `
                    -Algorithm SHA256).Hash `
                -Right $script:frozenProfileSha256)) {
            throw 'Frozen policy profile changed before replay.'
        }
    }
    if (Test-Path -LiteralPath $phaseReceiptPath -PathType Leaf) {
        if (-not $Resume) {
            throw "Fresh workflow found an unexpected phase receipt: $phaseReceiptPath"
        }
        return Read-CompletedPolicyPhase `
            $phaseSpecification $runDirectory $phaseReceiptPath $RunProfilePath
    }
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
    if ($null -ne (Get-ChildItem -LiteralPath $runDirectory -Force |
            Select-Object -First 1)) {
        if (-not ($Resume -and $RecoverInterrupted)) {
            throw "Unsealed phase directory requires -Resume -RecoverInterrupted: $runDirectory"
        }
        $interruptedRoot = Join-Path $script:checkpointRoot `
            'interruptions\phases'
        New-Item -ItemType Directory -Path $interruptedRoot -Force | Out-Null
        $archivePath = Join-Path $interruptedRoot (
            (($phaseSpecification.phaseId -replace '[^A-Za-z0-9_.-]', '--')) +
            '--' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
        Move-Item `
            -LiteralPath $runDirectory `
            -Destination $archivePath `
            -ErrorAction Stop
        New-Item -ItemType Directory -Path $runDirectory | Out-Null
    }
    $playerLog = Join-Path $runDirectory 'player.log'
    $arguments = @(
        '-force-d3d12',
        '-force-device-index', [string]$DeviceIndex,
        '-screen-fullscreen', '0',
        '-screen-width', '640',
        '-screen-height', '360',
        '-gpu-driven-instance-policy-benchmark',
        '-gpu-driven-instance-policy-output',
            (Quote-PolicyProcessArgument $runDirectory),
        '-gpu-driven-instance-policy-scenario-id',
            (Quote-PolicyProcessArgument $scenarioId),
        '-gpu-driven-instance-policy-instance-count',
            [string]$Scenario.instanceCount,
        '-gpu-driven-instance-policy-view-count',
            [string]$Scenario.viewCount,
        '-gpu-driven-instance-policy-visibility-bps',
            [string]$Scenario.visibilityBasisPoints,
        '-gpu-driven-instance-policy-dirty-bps',
            [string]$Scenario.dirtyBasisPoints,
        '-gpu-driven-instance-policy-seed', [string]$RunSeed,
        '-gpu-driven-instance-policy-warmup-frames', [string]$WarmupFrames,
        '-gpu-driven-instance-policy-sample-frames', [string]$SampleFrames,
        '-gpu-driven-instance-policy-timeout-seconds', '120',
        '-gpu-driven-instance-policy-left-case',
            (Quote-PolicyProcessArgument $RunLeftCase),
        '-gpu-driven-instance-policy-right-case',
            (Quote-PolicyProcessArgument $RunRightCase),
        '-gpu-driven-instance-policy-required-output',
            (Quote-PolicyProcessArgument ([string]$Scenario.requiredOutput)),
        '-gpu-driven-instance-policy-pipeline-fingerprint',
            (Quote-PolicyProcessArgument $pipelineContractFingerprint),
        '-gpu-driven-instance-policy-shader-fingerprint',
            (Quote-PolicyProcessArgument $shaderContractFingerprint),
        '-gpu-driven-instance-policy-calibration-protocol',
            (Quote-PolicyProcessArgument $calibrationProtocol),
        '-gpu-driven-instance-policy-measurement-contract-fingerprint',
            (Quote-PolicyProcessArgument $measurementContractFingerprint),
        '-gpu-driven-instance-policy-build-commit',
            (Quote-PolicyProcessArgument $gitCommit),
        '-logFile', (Quote-PolicyProcessArgument $playerLog))
    if (-not [string]::IsNullOrWhiteSpace($RunProfilePath)) {
        if (-not [string]::IsNullOrWhiteSpace($script:frozenProfileSha256) -and
            -not (Test-PolicySha256Equal `
                -Left (Get-FileHash -LiteralPath $RunProfilePath `
                    -Algorithm SHA256).Hash `
                -Right $script:frozenProfileSha256)) {
            throw 'Frozen policy profile changed before replay.'
        }
        $arguments += @(
            '-gpu-driven-instance-policy-profile-path',
            (Quote-PolicyProcessArgument (
                [IO.Path]::GetFullPath($RunProfilePath))))
    }

    $startedUtc = (Get-Date).ToUniversalTime().ToString('O')
    # Keep the benchmark Player windowed and visible. On D3D12, launching the
    # same frozen Player with WindowStyle Hidden can preserve a valid
    # FrameTimingManager record while suppressing its whole-frame GPU value.
    $process = Start-Process `
        -FilePath $resolvedPlayerPath `
        -ArgumentList $arguments `
        -WorkingDirectory $playerPayloadRoot `
        -PassThru
    $completed = Wait-PolicyProcess `
        -Process $process `
        -TimeoutMinutes $PlayerTimeoutMinutes
    if (-not $completed) {
        $process.Kill()
        $process.WaitForExit()
        throw "$scenarioId timed out after $PlayerTimeoutMinutes minutes."
    }
    if ($process.ExitCode -ne 0) {
        throw "$scenarioId failed with exit code $($process.ExitCode); see $playerLog"
    }
    $receiptParameters = Get-PolicyEvidenceAssertionParameters `
        $runDirectory $phaseSpecification
    $evidence = Assert-PolicyBenchmarkEvidence @receiptParameters
    if ($requireProfile) {
        $expectedProfileSha = (Get-FileHash -LiteralPath $RunProfilePath `
            -Algorithm SHA256).Hash
        if (-not (Test-PolicySha256Equal `
                -Left ([string]$evidence.config.profileSha256) `
                -Right $expectedProfileSha)) {
            throw "$scenarioId loaded a different profile payload."
        }
    }
    $currentPayload =
        Get-GpuBenchmarkPlayerPayload -PlayerPath $resolvedPlayerPath
    if ([string]$currentPayload.sha256 -cne [string]$initialPayload.sha256) {
        throw "$scenarioId changed the frozen Player payload."
    }
    $finishedUtc = (Get-Date).ToUniversalTime().ToString('O')
    $phaseReceipt = Write-GpuBenchmarkSealedJson `
        -Value ([ordered]@{
            schemaVersion = 1
            suite = 'summit.gpu-benchmark-phase'
            runContractFingerprint = $script:runContractFingerprint
            phaseSpecificationSha256 =
                Get-GpuBenchmarkObjectSha256 $phaseSpecification
            phaseId = [string]$phaseSpecification.phaseId
            scenarioId = $scenarioId
            startedUtc = $startedUtc
            finishedUtc = $finishedUtc
            exitCode = 0
            evidenceDirectory = [IO.Path]::GetFullPath($runDirectory)
            evidenceSha256 = [string]$evidence.evidenceSha256
            evidenceFileSet = Get-PolicyEvidenceFileSetReceipt $runDirectory
            playerPayloadSha256 = [string]$currentPayload.sha256
            profileSha256 = if ($requireProfile) {
                (Get-FileHash -LiteralPath $RunProfilePath `
                    -Algorithm SHA256).Hash
            }
            else { '' }
        }) `
        -Path $phaseReceiptPath `
        -CreateNew
    $playerRuns.Add([pscustomobject][ordered]@{
        scenarioId = $scenarioId
        phaseId = [string]$phaseSpecification.phaseId
        phase = $Phase
        seed = $RunSeed
        leftCase = Get-CanonicalCaseId $RunLeftCase
        rightCase = Get-CanonicalCaseId $RunRightCase
        startedUtc = $startedUtc
        finishedUtc = $finishedUtc
        exitCode = $process.ExitCode
        evidenceDirectory = $runDirectory
        evidenceSha256 = $evidence.evidenceSha256
        playerPayloadSha256 = $currentPayload.sha256
        phaseReceiptPath = [IO.Path]::GetFullPath($phaseReceiptPath)
        phaseReceiptSha256 = [string]$phaseReceipt.recordSha256
        resumed = $false
    })
    $script:executedPhaseCount++
    Update-PolicyRunnerProgress
    return $evidence
}

$gitStart = Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
if ([bool]$gitStart.dirty) {
    throw "Benchmark requires a clean worktree:`n" +
        (@($gitStart.statusLines) -join "`n")
}
if ([string]$gitStart.branch -ceq 'HEAD') {
    throw 'Benchmark requires a named Git branch.'
}
$gitCommit = [string]$gitStart.head
if ($gitCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'Benchmark requires a full 40-hex build commit.'
}

$projectVersionPath =
    Join-Path $projectRoot 'ProjectSettings\ProjectVersion.txt'
$versionLine = Get-Content -LiteralPath $projectVersionPath |
    Where-Object { $_ -like 'm_EditorVersion:*' } |
    Select-Object -First 1
$projectUnityVersion = ($versionLine -split ':', 2)[1].Trim()
if ($projectUnityVersion -cne $expectedUnityVersion) {
    throw "Policy benchmark requires Unity $expectedUnityVersion exactly."
}
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw "Unity editor is missing: $UnityPath"
}
$resolvedUnityPath = [IO.Path]::GetFullPath($UnityPath)
if ($resolvedUnityPath -notmatch
    '[\\/]6000\.5\.2f1[\\/]Editor[\\/]Unity\.exe$') {
    throw 'UnityPath is not the frozen 6000.5.2f1 editor executable.'
}

if ($Workflow -ceq 'FormalMatrix') {
    $violations = [Collections.Generic.List[string]]::new()
    if ($DeviceIndex -ne 0) { $violations.Add('DeviceIndex must be 0.') }
    if ($WarmupFrames -ne $formalWarmupFrames) {
        $violations.Add("WarmupFrames must be $formalWarmupFrames.")
    }
    if ($SampleFrames -ne $formalSampleFrames) {
        $violations.Add("SampleFrames must be $formalSampleFrames.")
    }
    if ($violations.Count -ne 0) {
        throw "Formal matrix contract rejected:`n$($violations -join "`n")"
    }
}
elseif ($SampleFrames -lt $formalSampleFrames) {
    Write-Warning (
        'Short SingleScenario runs are diagnostics only. Sample cardinality ' +
        'does not guarantee Unity GPU-frame availability; a run is usable as ' +
        'performance evidence only when every existing coverage gate passes.')
}

$expectedTestIdentities = @(
    'Summit.GpuDrivenInstance.Benchmark.Tests.Editor.dll',
    'Summit.GpuDrivenInstance.Benchmark.Tests.GpuDrivenInstancePolicyBenchmarkAdapterTests',
    'Summit.GpuDrivenInstance.Benchmark.Tests.GpuDrivenInstancePolicyBenchmarkAdapterTests.MeasuredOrdinalsArePairLocalAndDisjointFromWarmup',
    'Summit.GpuDrivenInstance.Benchmark.Tests.GpuDrivenInstancePolicyBenchmarkAdapterTests.PairedInputEvidenceRequiresAllThreeExactFields',
    'Summit.GpuDrivenInstance.Benchmark.Tests.GpuDrivenInstancePolicyBenchmarkAdapterTests.EngineIndirectEvidenceRequiresExactCopiedWords',
    'Summit.GpuDrivenInstance.Benchmark.Tests.GpuDrivenInstancePolicyBenchmarkAdapterTests.RenderTargetEvidenceRejectsBlackAndHashesNonBlackRgba',
    'Summit.GpuDrivenInstance.Benchmark.Tests.GpuDrivenInstancePolicyBenchmarkAdapterTests.AdapterPresentationResourcesRemainDiscoverable',
    'Summit.GpuDrivenInstance.Benchmark.Tests.GpuDrivenInstancePolicyInputGeneratorTests',
    'Summit.GpuAutotuning.Tests.Editor.dll',
    'Summit.GpuAutotuning.Tests.GpuDrivenInstancePolicyProfileTests',
    'Summit.GpuAutotuning.Tests.GpuDrivenInstancePolicySelectorTests',
    'Summit.GpuAutotuning.Tests.GpuDrivenInstancePolicyCompositionTests',
    'Summit.GpuAutotuning.Tests.GpuDrivenInstancePolicyCompositionTests.OutputMismatchFailsClosedInsteadOfBecomingTunable',
    'Summit.GpuAutotuning.Tests.GpuDrivenInstancePolicySelectorTests.WarmSelectPathAllocatesZeroBytesAcrossOneHundredThousandCalls',
    'Summit.GpuAutotuning.Tests.GpuDrivenInstancePolicyProfileStoreTests',
    'Summit.GpuDrivenInstances.Tests.Editor.dll',
    'Summit.GpuDrivenInstances.Tests.GpuInstanceStateUploaderTests',
    'Summit.GpuDrivenInstances.Tests.GpuInstanceStateUploaderTests.PlannedDirtyUploadRecordsExactlyTheInspectedPlan',
    'Summit.GpuDrivenInstances.Tests.GpuInstanceStateUploaderTests.DisposedPlannedSourceCannotBeReplacedWithSameRevision',
    'Summit.GpuDrivenInstances.Tests.GpuInstanceStateUploaderTests.PlanGenerationExhaustionIsPermanentFailClosed',
    'Summit.GpuDrivenInstances.Tests.GpuInstanceStateUploaderTests.WarmPlannedRecordingDoesNotAllocateManagedMemory')

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ($RecoverInterrupted -and -not $Resume) {
    throw '-RecoverInterrupted is valid only together with -Resume.'
}
if ($Resume -and [string]::IsNullOrWhiteSpace($OutputDirectory)) {
    throw '-Resume requires the exact existing -OutputDirectory.'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot (
        "Reports\GpuDrivenInstancePolicyBenchmark\$Workflow-$stamp")
}
elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ($Resume) {
    if (-not (Test-Path -LiteralPath $outputRoot -PathType Container)) {
        throw "Resume output directory is missing: $outputRoot"
    }
}
elseif (Test-Path -LiteralPath $outputRoot) {
    if ($null -ne (Get-ChildItem -LiteralPath $outputRoot -Force |
            Select-Object -First 1)) {
        throw 'Benchmark output directory must be new or empty.'
    }
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$script:checkpointRoot = Join-Path $outputRoot 'checkpoint'
$script:phaseReceiptsRoot = Join-Path $script:checkpointRoot 'phases'
$runContractPath = Join-Path $script:checkpointRoot 'run-contract.json'
$existingRunContract = if ($Resume) {
    Read-GpuBenchmarkSealedJson `
        -Path $runContractPath `
        -ExpectedSuite 'summit.gpu-driven-instance-policy-run-contract' `
        -ExpectedSchemaVersion 1
}
else { $null }

if ([string]::IsNullOrWhiteSpace($PlayerPath)) {
    $PlayerPath = if ($Resume) {
        [string]$existingRunContract.playerPath
    }
    else {
        Join-Path $projectRoot (
            "Builds\GpuDrivenInstancePolicyBenchmark\$stamp\" +
            'GpuDrivenInstancePolicyBenchmark.exe')
    }
}
elseif (-not [IO.Path]::IsPathRooted($PlayerPath)) {
    $PlayerPath = Join-Path $projectRoot $PlayerPath
}
$resolvedPlayerPath = [IO.Path]::GetFullPath($PlayerPath)
$playerPayloadRoot = Split-Path -Parent $resolvedPlayerPath
if ($outputRoot.Equals(
        $playerPayloadRoot,
        [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot.StartsWith(
        $playerPayloadRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Report output may not be inside the Player payload.'
}
if (-not $Resume -and (Test-Path -LiteralPath $playerPayloadRoot)) {
    if ($null -ne (Get-ChildItem -LiteralPath $playerPayloadRoot -Force |
            Select-Object -First 1)) {
        throw 'Fresh Player payload directory must be new or empty.'
    }
}
if ($Resume) {
    if (-not (Test-Path -LiteralPath $playerPayloadRoot -PathType Container)) {
        throw "Resume Player payload directory is missing: $playerPayloadRoot"
    }
}
else {
    New-Item -ItemType Directory -Path $playerPayloadRoot -Force | Out-Null
}

$sourceFiles = Get-PolicySourceFiles
$sourceSnapshotSha256 =
    Get-PolicyCombinedSha256 $sourceFiles $projectRoot
$pipelineContractFingerprint = Get-PolicyCombinedSha256 `
    (Get-PipelineContractFiles) $projectRoot
$shaderContractFingerprint = Get-PolicyCombinedSha256 `
    (Get-ShaderContractFiles) $projectRoot
$measurementContractFingerprint = Get-PolicyTextSha256 $measurementContract

$formalCells = if ($Workflow -ceq 'FormalMatrix') {
    New-FormalPolicyCells
}
else { [object[]]@() }
$singleCell = [pscustomobject][ordered]@{
    ruleId = 'single-scenario'
    instanceCount = $InstanceCount
    viewCount = $ViewCount
    visibilityBasisPoints = $VisibilityBasisPoints
    dirtyBasisPoints = $DirtyBasisPoints
    requiredOutput = $RequiredOutput
}
$expectedPhaseSpecifications = [Collections.Generic.List[object]]::new()
if ($Workflow -ceq 'FormalMatrix') {
    foreach ($cell in $formalCells) {
        $expectedPhaseSpecifications.Add((New-PolicyPhaseSpecification `
            $cell 'calibration' $calibrationSeed `
            $cell.baselineCase $cell.candidateCase))
        $expectedPhaseSpecifications.Add((New-PolicyPhaseSpecification `
            $cell 'holdout' $holdoutSeed `
            $cell.baselineCase $cell.candidateCase))
    }
    foreach ($cell in $formalCells) {
        $expectedPhaseSpecifications.Add((New-PolicyPhaseSpecification `
            $cell 'replay' $replaySeed 'forced-selected' 'actual-auto'))
        $expectedPhaseSpecifications.Add((New-PolicyPhaseSpecification `
            $cell 'replay-end-to-end' $replaySeed `
            'full-flat' 'actual-auto'))
    }
}
else {
    $expectedPhaseSpecifications.Add((New-PolicyPhaseSpecification `
        $singleCell 'single' $Seed $LeftCase $RightCase))
}
$resolvedInputProfilePath = if (
    [string]::IsNullOrWhiteSpace($ProfilePath)) { '' }
else { [IO.Path]::GetFullPath($ProfilePath) }
$inputProfileSha256 = if (
    [string]::IsNullOrWhiteSpace($resolvedInputProfilePath)) { '' }
else {
    if (-not (Test-Path -LiteralPath $resolvedInputProfilePath -PathType Leaf)) {
        throw "Input policy profile is missing: $resolvedInputProfilePath"
    }
    (Get-FileHash -LiteralPath $resolvedInputProfilePath -Algorithm SHA256).Hash
}
$runContract = [ordered]@{
    schemaVersion = 1
    suite = 'summit.gpu-driven-instance-policy-run-contract'
    workflow = $Workflow
    projectRoot = $projectRoot
    outputDirectory = $outputRoot
    playerPath = $resolvedPlayerPath
    unityPath = $resolvedUnityPath
    unityVersion = $expectedUnityVersion
    graphicsApi = 'Direct3D12'
    playerWindowContract = $playerWindowContract
    gitCommit = $gitCommit
    gitBranch = [string]$gitStart.branch
    sourceSnapshotSha256 = $sourceSnapshotSha256
    pipelineContractFingerprint = $pipelineContractFingerprint
    shaderContractFingerprint = $shaderContractFingerprint
    measurementContractFingerprint = $measurementContractFingerprint
    calibrationProtocol = $calibrationProtocol
    protocolAmendmentReason = $protocolAmendmentReason
    deviceIndex = $DeviceIndex
    warmupFrames = $WarmupFrames
    sampleFrames = $SampleFrames
    playerTimeoutMinutes = $PlayerTimeoutMinutes
    editModeTimeoutMinutes = $EditModeTimeoutMinutes
    calibrationSeed = $calibrationSeed
    holdoutSeed = $holdoutSeed
    replaySeed = $replaySeed
    optimizedAxes = [string[]]@('Upload', 'Culling')
    semanticConstraints = [ordered]@{
        output = 'caller-required;not-calibrated'
        primitiveBackend =
            'portable-in-this-matrix;compose-with-primitive-autotuner'
    }
    formalCells = [object[]]$formalCells
    singleScenario = $singleCell
    singleSeed = $Seed
    singleLeftCase = $LeftCase
    singleRightCase = $RightCase
    inputProfilePath = $resolvedInputProfilePath
    inputProfileSha256 = $inputProfileSha256
    expectedPhases = [object[]]$expectedPhaseSpecifications.ToArray()
}
$expectedRunContractFingerprint =
    Get-GpuBenchmarkObjectSha256 $runContract
if ($Resume) {
    if (-not (Test-PolicySha256Equal `
            -Left ([string]$existingRunContract.recordSha256) `
            -Right $expectedRunContractFingerprint)) {
        throw 'Resume parameters, source, commit, paths, or protocol do not match the immutable run contract.'
    }
    $script:runContractFingerprint =
        [string]$existingRunContract.recordSha256
}
else {
    $sealedRunContract = Write-GpuBenchmarkSealedJson `
        -Value $runContract `
        -Path $runContractPath `
        -CreateNew
    $script:runContractFingerprint =
        [string]$sealedRunContract.recordSha256
}
New-Item -ItemType Directory -Path $script:phaseReceiptsRoot -Force |
    Out-Null
$runLockPath = Join-Path $script:checkpointRoot 'run.lock.json'
$runLockHandle = Enter-GpuBenchmarkRunLock `
    -Path $runLockPath `
    -RunContractFingerprint $script:runContractFingerprint `
    -RecoverInterrupted:$RecoverInterrupted

try {

$preflightRoot = Join-Path $outputRoot 'preflight'
New-Item -ItemType Directory -Path $preflightRoot -Force | Out-Null
$generatedEditModeResultsPath =
    Join-Path $preflightRoot 'editmode-results.xml'
$editModeLogPath = Join-Path $preflightRoot 'unity-editmode.log'
if (-not $Resume) {
if (Test-Path -LiteralPath $generatedEditModeResultsPath) {
    throw 'Commit-bound EditMode XML path was not fresh.'
}
$editModeArguments = @(
    '-batchmode',
    '-force-d3d12',
    '-projectPath', (Quote-PolicyProcessArgument $projectRoot),
    '-runTests',
    '-testPlatform', 'EditMode',
    '-testResults',
        (Quote-PolicyProcessArgument $generatedEditModeResultsPath),
    '-logFile', (Quote-PolicyProcessArgument $editModeLogPath))
$editModeProcess = Start-Process `
    -FilePath $resolvedUnityPath `
    -ArgumentList $editModeArguments `
    -WorkingDirectory $projectRoot `
    -WindowStyle Hidden `
    -PassThru
$editModeCompleted = Wait-PolicyProcess `
    -Process $editModeProcess `
    -TimeoutMinutes $EditModeTimeoutMinutes
if (-not $editModeCompleted) {
    $editModeProcess.Kill()
    $editModeProcess.WaitForExit()
    throw "Commit-bound EditMode run timed out after " +
        "$EditModeTimeoutMinutes minutes."
}
if ($editModeProcess.ExitCode -ne 0) {
    throw "Commit-bound EditMode run failed with exit code " +
        "$($editModeProcess.ExitCode); see $editModeLogPath"
}
$editModeReceipt = Get-PolicyNUnitReceipt `
    -Path $generatedEditModeResultsPath `
    -ExpectedIdentities $expectedTestIdentities
$postTestGit = Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
if ($postTestGit.head -ine $gitCommit -or [bool]$postTestGit.dirty) {
    throw 'Git state changed during commit-bound EditMode tests.'
}
$postTestSourceSha256 =
    Get-PolicyCombinedSha256 (Get-PolicySourceFiles) $projectRoot
if ($postTestSourceSha256 -cne $sourceSnapshotSha256) {
    throw 'Policy source hash changed during commit-bound EditMode tests.'
}
$editModeProvenance = [ordered]@{
    schemaVersion = 1
    generatedByRunner = $true
    externalReceiptAccepted = $false
    deprecatedExternalPath = $EditModeResultsPath
    sourceCommit = $gitCommit
    sourceSnapshotSha256 = $sourceSnapshotSha256
    unityVersion = $expectedUnityVersion
    unityPath = $resolvedUnityPath
    graphicsApiArgument = '-force-d3d12'
    resultPath = $generatedEditModeResultsPath
    resultSha256 = $editModeReceipt.sha256
    receipt = $editModeReceipt
    gitAfter = $postTestGit
    sourceSnapshotSha256After = $postTestSourceSha256
}
[void](Assert-PolicyPreflightBinding `
    -Provenance ([pscustomobject]$editModeProvenance) `
    -ExpectedCommit $gitCommit `
    -ExpectedSourceSha256 $sourceSnapshotSha256 `
    -ExpectedXmlSha256 $editModeReceipt.sha256 `
    -ExpectedUnityVersion $expectedUnityVersion)
Write-Utf8Json $editModeProvenance (
    Join-Path $preflightRoot 'editmode-provenance.json') 30
}
else {
    $editModeProvenancePath =
        Join-Path $preflightRoot 'editmode-provenance.json'
    if (-not (Test-Path -LiteralPath $generatedEditModeResultsPath `
            -PathType Leaf) -or
        -not (Test-Path -LiteralPath $editModeProvenancePath -PathType Leaf)) {
        throw 'Resume requires the completed commit-bound EditMode receipt.'
    }
    $editModeReceipt = Get-PolicyNUnitReceipt `
        -Path $generatedEditModeResultsPath `
        -ExpectedIdentities $expectedTestIdentities
    $editModeProvenance = Get-Content `
        -LiteralPath $editModeProvenancePath `
        -Raw | ConvertFrom-Json -DateKind String
    if (-not [string]::Equals(
            [IO.Path]::GetFullPath([string]$editModeProvenance.resultPath),
            [IO.Path]::GetFullPath($generatedEditModeResultsPath),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Resume EditMode provenance points to another XML receipt.'
    }
    [void](Assert-PolicyPreflightBinding `
        -Provenance $editModeProvenance `
        -ExpectedCommit $gitCommit `
        -ExpectedSourceSha256 $sourceSnapshotSha256 `
        -ExpectedXmlSha256 $editModeReceipt.sha256 `
        -ExpectedUnityVersion $expectedUnityVersion)
    $postTestGit = Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
    if ($postTestGit.head -ine $gitCommit -or [bool]$postTestGit.dirty) {
        throw 'Git state changed before resuming commit-bound evidence.'
    }
    $postTestSourceSha256 =
        Get-PolicyCombinedSha256 (Get-PolicySourceFiles) $projectRoot
    if ($postTestSourceSha256 -cne $sourceSnapshotSha256) {
        throw 'Policy source hash changed before resume.'
    }
}

$script:playerRuns = [Collections.Generic.List[object]]::new()
$script:executedPhaseCount = 0
$script:resumedPhaseCount = 0
$script:frozenProfileSha256 = ''
$previousRunnerConfig = if ($Resume -and
    (Test-Path -LiteralPath (Join-Path $outputRoot 'runner-config.json') `
        -PathType Leaf)) {
    Get-Content -LiteralPath (Join-Path $outputRoot 'runner-config.json') -Raw |
        ConvertFrom-Json -DateKind String
}
else { $null }
$startedUtc = if ($null -ne $previousRunnerConfig -and
    -not [string]::IsNullOrWhiteSpace(
        [string]$previousRunnerConfig.startedUtc)) {
    [string]$previousRunnerConfig.startedUtc
}
else { (Get-Date).ToUniversalTime().ToString('O') }
$previousResumeInvocationCount = if ($null -ne $previousRunnerConfig -and
    $null -ne $previousRunnerConfig.PSObject.Properties[
        'resumeInvocationCount']) {
    [int]$previousRunnerConfig.resumeInvocationCount
}
else { 0 }
$script:runnerConfig = [ordered]@{
    schemaVersion = 2
    suite = $suite
    workflow = $Workflow
    startedUtc = $startedUtc
    finalizedUtc = ''
    projectRoot = $projectRoot
    unityPath = $resolvedUnityPath
    unityVersion = $projectUnityVersion
    gitStart = $gitStart
    gitCommit = $gitCommit
    gitBranch = [string]$gitStart.branch
    sourceSnapshotSha256 = $sourceSnapshotSha256
    pipelineContractFingerprint = $pipelineContractFingerprint
    shaderContractFingerprint = $shaderContractFingerprint
    measurementContractFingerprint = $measurementContractFingerprint
    calibrationProtocol = $calibrationProtocol
    protocolAmendmentReason = $protocolAmendmentReason
    calibrationSeed = $calibrationSeed
    holdoutSeed = $holdoutSeed
    replaySeed = $replaySeed
    seedsArePairwiseDistinct = $true
    editModeResults = $editModeReceipt
    editModeProvenance = $editModeProvenance
    externalEditModeResultsPathIgnored = $EditModeResultsPath
    outputDirectory = $outputRoot
    playerPath = $resolvedPlayerPath
    deviceIndex = $DeviceIndex
    warmupFrames = $WarmupFrames
    sampleFrames = $SampleFrames
    freshPlayerBuild = $true
    playerBuildCreatedByRun = $true
    currentInvocationBuiltPlayer = -not $Resume
    resumed = [bool]$Resume
    resumeInvocationCount = $previousResumeInvocationCount + [int][bool]$Resume
    sourceHashesStableAcrossBuild = $false
    playerPayload = $null
    playerPayloadStableThroughRun = $false
    playerRuns = @()
    evidenceValid = $false
    formalContractSatisfied = $false
    checkpoint = [ordered]@{
        schemaVersion = 1
        runContractPath = $runContractPath
        runContractFingerprint = $script:runContractFingerprint
        setupReceiptPath = Join-Path $script:checkpointRoot 'setup.json'
        phaseReceiptsDirectory = $script:phaseReceiptsRoot
        expectedPhaseCount = $expectedPhaseSpecifications.Count
        executedPhaseCount = 0
        resumedPhaseCount = 0
        recoveryRequested = [bool]$RecoverInterrupted
        recoveredLockPath = [string]$runLockHandle.RecoveredLockPath
        lastProgressUtc = ''
    }
}
$script:runnerConfigPath = Join-Path $outputRoot 'runner-config.json'
Write-Utf8Json $runnerConfig $runnerConfigPath 30

$playerPayloadManifestPath =
    Join-Path $outputRoot 'player-payload-manifest.json'
$setupReceiptPath = Join-Path $script:checkpointRoot 'setup.json'
if (-not $Resume) {
$buildLog = Join-Path $outputRoot 'unity-build.log'
$buildArguments = @(
    '-batchmode',
    '-nographics',
    '-quit',
    '-projectPath', (Quote-PolicyProcessArgument $projectRoot),
    '-executeMethod',
        'GpuDrivenInstancePolicyBenchmarkBuild.PerformBuild',
    '-gpu-driven-instance-policy-player-path',
        (Quote-PolicyProcessArgument $resolvedPlayerPath),
    '-logFile', (Quote-PolicyProcessArgument $buildLog))
$build = Start-Process `
    -FilePath $resolvedUnityPath `
    -ArgumentList $buildArguments `
    -WorkingDirectory $projectRoot `
    -WindowStyle Hidden `
    -Wait `
    -PassThru
if ($build.ExitCode -ne 0) {
    throw "Fresh Unity Player build failed; see $buildLog"
}
$buildSummaryPath = Join-Path $playerPayloadRoot 'policy-build-summary.txt'
if (-not (Test-Path -LiteralPath $resolvedPlayerPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $buildSummaryPath -PathType Leaf) -or
    -not (Select-String -LiteralPath $buildSummaryPath `
        -SimpleMatch 'result=Succeeded' -Quiet)) {
    throw 'Fresh Player build receipt is missing or unsuccessful.'
}
$postBuildGit = Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
if ($postBuildGit.head -ine $gitCommit -or [bool]$postBuildGit.dirty) {
    throw 'Git state changed during the fresh Player build.'
}
$postBuildSourceSha256 =
    Get-PolicyCombinedSha256 (Get-PolicySourceFiles) $projectRoot
if ($postBuildSourceSha256 -cne $sourceSnapshotSha256) {
    throw 'Policy benchmark source hashes changed during Player build.'
}
$initialPayload =
    Get-GpuBenchmarkPlayerPayload -PlayerPath $resolvedPlayerPath
$runnerConfig['sourceHashesStableAcrossBuild'] = $true
$runnerConfig['playerPayload'] = $initialPayload
$playerPayloadRecord = Write-GpuBenchmarkSealedJson `
    -Value $initialPayload `
    -Path $playerPayloadManifestPath `
    -CreateNew
$setupReceipt = Write-GpuBenchmarkSealedJson `
    -Value ([ordered]@{
        schemaVersion = 1
        suite = 'summit.gpu-driven-instance-policy-setup'
        runContractFingerprint = $script:runContractFingerprint
        sourceSnapshotSha256 = $sourceSnapshotSha256
        editModeResultSha256 = [string]$editModeReceipt.sha256
        preflightFileSet = Get-GpuBenchmarkFileSetReceipt `
            -Root $preflightRoot `
            -RelativePaths @(
                'editmode-results.xml',
                'editmode-provenance.json')
        playerPayloadRecordSha256 =
            [string]$playerPayloadRecord.recordSha256
        playerPayloadSha256 = [string]$initialPayload.sha256
        completedUtc = (Get-Date).ToUniversalTime().ToString('O')
    }) `
    -Path $setupReceiptPath `
    -CreateNew
}
else {
    $playerPayloadRecord = Read-GpuBenchmarkSealedJson `
        -Path $playerPayloadManifestPath `
        -ExpectedSchemaVersion 1
    $setupReceipt = Read-GpuBenchmarkSealedJson `
        -Path $setupReceiptPath `
        -ExpectedSuite 'summit.gpu-driven-instance-policy-setup' `
        -ExpectedSchemaVersion 1
    $initialPayload =
        Get-GpuBenchmarkPlayerPayload -PlayerPath $resolvedPlayerPath
    $currentPayloadRecordSha256 =
        Get-GpuBenchmarkObjectSha256 $initialPayload
    if (-not (Test-PolicySha256Equal `
            -Left ([string]$playerPayloadRecord.recordSha256) `
            -Right $currentPayloadRecordSha256) -or
        -not (Test-PolicySha256Equal `
            -Left ([string]$setupReceipt.runContractFingerprint) `
            -Right $script:runContractFingerprint) -or
        -not (Test-PolicySha256Equal `
            -Left ([string]$setupReceipt.sourceSnapshotSha256) `
            -Right $sourceSnapshotSha256) -or
        -not (Test-PolicySha256Equal `
            -Left ([string]$setupReceipt.editModeResultSha256) `
            -Right ([string]$editModeReceipt.sha256)) -or
        -not (Test-PolicySha256Equal `
            -Left ([string]$setupReceipt.playerPayloadRecordSha256) `
            -Right ([string]$playerPayloadRecord.recordSha256)) -or
        -not (Test-PolicySha256Equal `
            -Left ([string]$setupReceipt.playerPayloadSha256) `
            -Right ([string]$initialPayload.sha256))) {
        throw 'Resume setup receipt, source, preflight, or Player payload changed.'
    }
    $null = Assert-GpuBenchmarkFileSetReceipt `
        -Root $preflightRoot `
        -Receipt $setupReceipt.preflightFileSet
    $runnerConfig['sourceHashesStableAcrossBuild'] = $true
    $runnerConfig['playerPayload'] = $initialPayload
}
Write-Utf8Json $runnerConfig $runnerConfigPath 30

if ($Workflow -ceq 'SingleScenario') {
    $singleEvidence = Invoke-PolicyPlayer `
        -Scenario $singleCell `
        -Phase 'single' `
        -RunSeed $Seed `
        -RunLeftCase $LeftCase `
        -RunRightCase $RightCase `
        -RunProfilePath $resolvedInputProfilePath
    Write-Utf8Json ([ordered]@{
        schemaVersion = 1
        evidenceDirectory = $singleEvidence.directory
        evidenceSha256 = $singleEvidence.evidenceSha256
        leftCase = [string]$singleEvidence.config.leftCase
        rightCase = [string]$singleEvidence.config.rightCase
        rawFrameCount = @($singleEvidence.raw).Count
        gpuFrameReadyRows = [int]$singleEvidence.summary.gpuFrameReadyRows
        gpuFrameUnavailableRows =
            [int]$singleEvidence.summary.gpuFrameUnavailableRows
    }) (Join-Path $outputRoot 'single-scenario-receipt.json') 12
}
else {
    $cells = $formalCells
    $manifestCells = [Collections.Generic.List[object]]::new()
    foreach ($cell in $cells) {
        $calibrationEvidence = Invoke-PolicyPlayer `
            -Scenario $cell `
            -Phase 'calibration' `
            -RunSeed $calibrationSeed `
            -RunLeftCase $cell.baselineCase `
            -RunRightCase $cell.candidateCase
        $holdoutEvidence = Invoke-PolicyPlayer `
            -Scenario $cell `
            -Phase 'holdout' `
            -RunSeed $holdoutSeed `
            -RunLeftCase $cell.baselineCase `
            -RunRightCase $cell.candidateCase
        $manifestCells.Add([pscustomobject][ordered]@{
            ruleId = $cell.ruleId
            candidateKind = $cell.candidateKind
            sampleFrames = $SampleFrames
            baselineCaseId = Get-CanonicalCaseId $cell.baselineCase
            candidateCaseId = Get-CanonicalCaseId $cell.candidateCase
            candidateUploadMode = $cell.candidateUploadMode
            candidateCullingMode = $cell.candidateCullingMode
            calibrationDirectory = $calibrationEvidence.directory
            holdoutDirectory = $holdoutEvidence.directory
        })
    }
    $selectionManifest = [ordered]@{
        schemaVersion = 1
        suite = 'summit.gpu-driven-instance-policy-selection'
        sourceCommit = $gitCommit
        sourceSnapshotSha256 = $sourceSnapshotSha256
        unityVersion = $expectedUnityVersion
        pipelineContractFingerprint = $pipelineContractFingerprint
        shaderContractFingerprint = $shaderContractFingerprint
        measurementContractFingerprint = $measurementContractFingerprint
        calibrationProtocol = $calibrationProtocol
        protocolAmendmentReason = $protocolAmendmentReason
        calibrationSeed = $calibrationSeed
        holdoutSeed = $holdoutSeed
        replaySeed = $replaySeed
        replayContracts = [ordered]@{
            selectorEquivalence = [ordered]@{
                leftCaseId = 'gpu-driven-policy/forced-selected'
                rightCaseId = 'gpu-driven-policy/actual-auto'
                gate =
                    'decision-exact;selector-mean<=0.01ms;selector-p99<=0.05ms;isolated-iterations=100000;isolated-allocated=0;isolated-unstable=0'
            }
            endToEnd = [ordered]@{
                leftCaseId = 'gpu-driven-policy/calibration/full-flat'
                rightCaseId = 'gpu-driven-policy/actual-auto'
                acceptedRuleGate =
                    'mean>=2%;wins>=55%;p95<=5%;p99<=10%;material-tail'
                rejectedRuleGate =
                    'full-flat-portable;no-material-p99-regression'
            }
        }
        cells = [object[]]$manifestCells.ToArray()
    }
    $selectionManifestPath =
        Join-Path $outputRoot 'policy-selection-manifest.json'
    $generatedProfilePath =
        Join-Path $outputRoot 'gpu-driven-instance-policy.json'
    $selectionReceiptPath =
        Join-Path $outputRoot 'policy-selection-receipt.json'
    $selectionCheckpointPath =
        Join-Path $script:checkpointRoot 'selection.json'
    $selectionManifestObjectSha256 =
        Get-GpuBenchmarkObjectSha256 $selectionManifest
    if ($Resume -and
        (Test-Path -LiteralPath $selectionCheckpointPath -PathType Leaf)) {
        $selectionCheckpoint = Read-GpuBenchmarkSealedJson `
            -Path $selectionCheckpointPath `
            -ExpectedSuite 'summit.gpu-driven-instance-policy-selection-stage' `
            -ExpectedSchemaVersion 1
        if (-not (Test-PolicySha256Equal `
                -Left ([string]$selectionCheckpoint.runContractFingerprint) `
                -Right $script:runContractFingerprint) -or
            -not (Test-PolicySha256Equal `
                -Left ([string]$selectionCheckpoint.
                    selectionManifestObjectSha256) `
                -Right $selectionManifestObjectSha256)) {
            throw 'Selection checkpoint does not match current sealed evidence.'
        }
        $null = Assert-GpuBenchmarkFileSetReceipt `
            -Root $outputRoot `
            -Receipt $selectionCheckpoint.fileSet
        $selectionReceipt =
            Get-Content -LiteralPath $selectionReceiptPath -Raw |
                ConvertFrom-Json -DateKind String
        $profileResult = [pscustomobject][ordered]@{
            profilePath = $generatedProfilePath
            profileSha256 = [string]$selectionCheckpoint.profileSha256
            selectionReceiptPath = $selectionReceiptPath
            holdoutEvidenceSetId =
                [string]$selectionReceipt.holdoutEvidenceSetId
            ruleCount = [int]$selectionCheckpoint.ruleCount
        }
    }
    else {
        $selectionOutputs = @(
            $selectionManifestPath,
            $generatedProfilePath,
            $selectionReceiptPath) | Where-Object {
                Test-Path -LiteralPath $_
            }
        if ($selectionOutputs.Count -ne 0) {
            if (-not ($Resume -and $RecoverInterrupted)) {
                throw 'Unsealed selection outputs require -Resume -RecoverInterrupted.'
            }
            $selectionArchive = Join-Path $script:checkpointRoot (
                'interruptions\selection-' +
                (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff'))
            New-Item -ItemType Directory -Path $selectionArchive -Force |
                Out-Null
            foreach ($path in $selectionOutputs) {
                Move-Item `
                    -LiteralPath $path `
                    -Destination (Join-Path $selectionArchive (
                        Split-Path -Leaf $path)) `
                    -ErrorAction Stop
            }
        }
        Write-Utf8Json $selectionManifest $selectionManifestPath 20
        $profileResult = & $selectorScriptPath `
            -ManifestPath $selectionManifestPath `
            -OutputPath $generatedProfilePath `
            -SelectionReceiptPath $selectionReceiptPath
        $selectionReceipt =
            Get-Content -LiteralPath $selectionReceiptPath -Raw |
                ConvertFrom-Json -DateKind String
        $selectionCheckpoint = Write-GpuBenchmarkSealedJson `
            -Value ([ordered]@{
                schemaVersion = 1
                suite =
                    'summit.gpu-driven-instance-policy-selection-stage'
                runContractFingerprint = $script:runContractFingerprint
                selectionManifestObjectSha256 =
                    $selectionManifestObjectSha256
                profileSha256 = [string]$profileResult.profileSha256
                ruleCount = [int]$profileResult.ruleCount
                fileSet = Get-GpuBenchmarkFileSetReceipt `
                    -Root $outputRoot `
                    -RelativePaths @(
                        'policy-selection-manifest.json',
                        'gpu-driven-instance-policy.json',
                        'policy-selection-receipt.json')
                completedUtc = (Get-Date).ToUniversalTime().ToString('O')
            }) `
            -Path $selectionCheckpointPath `
            -CreateNew
    }
    if ([int]$selectionReceipt.cells.Count -ne $cells.Count -or
        -not [bool]$selectionReceipt.allRulesHoldoutAccepted -or
        -not [bool]$selectionReceipt.
            candidateRejectionSelectsMeasuredBaseline) {
        throw 'Policy selection receipt is incomplete.'
    }
    $script:frozenProfileSha256 =
        (Get-FileHash -LiteralPath $generatedProfilePath `
            -Algorithm SHA256).Hash
    if (-not (Test-PolicySha256Equal `
            -Left $script:frozenProfileSha256 `
            -Right ([string]$profileResult.profileSha256)) -or
        -not (Test-PolicySha256Equal `
            -Left ([string]$selectionReceipt.profileSha256) `
            -Right $script:frozenProfileSha256) -or
        -not (Test-PolicySha256Equal `
            -Left ([string]$selectionReceipt.manifestSha256) `
            -Right (Get-FileHash -LiteralPath $selectionManifestPath `
                -Algorithm SHA256).Hash)) {
        throw 'Generated policy profile hash does not match its receipt.'
    }

    $replayReceipts = [Collections.Generic.List[object]]::new()
    $endToEndReplayReceipts = [Collections.Generic.List[object]]::new()
    foreach ($cell in $cells) {
        $selectionCell = @($selectionReceipt.cells | Where-Object {
            [string]$_.ruleId -ceq [string]$cell.ruleId
        })
        if ($selectionCell.Count -ne 1) {
            throw "Replay '$($cell.ruleId)' has no unique selected rule."
        }
        $evidence = Invoke-PolicyPlayer `
            -Scenario $cell `
            -Phase 'replay' `
            -RunSeed $replaySeed `
            -RunLeftCase 'forced-selected' `
            -RunRightCase 'actual-auto' `
            -RunProfilePath $generatedProfilePath
        $replay = Test-PolicyReplayEquivalence $evidence
        $autoRows = @($evidence.raw | Where-Object {
            [string]$_.caseId -ceq 'gpu-driven-policy/actual-auto'
        })
        $actualRuleIds = @($autoRows |
            Select-Object -ExpandProperty decisionRuleId -Unique)
        if ($actualRuleIds.Count -ne 1 -or
            [string]$actualRuleIds[0] -cne [string]$cell.ruleId -or
            @($autoRows | Where-Object {
                [string]$_.decisionUploadMode -cne
                    [string]$selectionCell[0].selectedUploadMode -or
                [string]$_.decisionCullingMode -cne
                    [string]$selectionCell[0].selectedCullingMode -or
                [string]$_.decisionPrimitiveBackend -cne 'Portable' -or
                [uint32]$_.decisionFlags -ne 0
            }).Count -ne 0) {
            throw "ActualAuto did not execute frozen rule '$($cell.ruleId)'."
        }
        if (-not [bool]$replay.accepted) {
            throw "Replay '$($cell.ruleId)' failed exact decision or selector-overhead gates."
        }
        $replayReceipts.Add([pscustomobject][ordered]@{
            ruleId = $cell.ruleId
            evidenceDirectory = $evidence.directory
            evidenceSha256 = $evidence.evidenceSha256
            requiredGate = $replay.requiredGate
            decisionMismatchCount = $replay.decisionMismatchCount
            selectorOverheadThresholds = $replay.selectorOverheadThresholds
            selectorOverheadFailures = $replay.selectorOverheadFailures
            selectorCpuMs = $replay.selectorCpuMs
            isolatedSelectorOverhead = $replay.isolatedSelectorOverhead
            observedDiagnosticTailRegressions =
                $replay.observedDiagnosticTailRegressions
            comparisons = $replay.comparisons
            accepted = $replay.accepted
        })

        $endToEndEvidence = Invoke-PolicyPlayer `
            -Scenario $cell `
            -Phase 'replay-end-to-end' `
            -RunSeed $replaySeed `
            -RunLeftCase 'full-flat' `
            -RunRightCase 'actual-auto' `
            -RunProfilePath $generatedProfilePath
        $endToEndReplay = Test-PolicyEndToEndReplay `
            -Evidence $endToEndEvidence `
            -CandidateKind ([string]$cell.candidateKind) `
            -CandidateAccepted ([bool]$selectionCell[0].candidateAccepted) `
            -ExpectedRuleId ([string]$cell.ruleId) `
            -ExpectedUploadMode (
                [string]$selectionCell[0].selectedUploadMode) `
            -ExpectedOutputMode (
                [string]$selectionCell[0].selectedOutputMode) `
            -ExpectedCullingMode (
                [string]$selectionCell[0].selectedCullingMode) `
            -ExpectedPrimitiveBackend (
                [string]$selectionCell[0].selectedPrimitiveBackend)
        if (-not [bool]$endToEndReplay.accepted) {
            throw "End-to-end replay '$($cell.ruleId)' failed its " +
                "accepted/rejected performance and correctness gate."
        }
        $endToEndReplayReceipts.Add([pscustomobject][ordered]@{
            ruleId = $cell.ruleId
            candidateKind = $cell.candidateKind
            candidateAccepted = [bool]$selectionCell[0].candidateAccepted
            evidenceDirectory = $endToEndEvidence.directory
            evidenceSha256 = $endToEndEvidence.evidenceSha256
            decisionMismatchCount =
                $endToEndReplay.decisionMismatchCount
            safeRejectedDecision =
                $endToEndReplay.safeRejectedDecision
            requiredGate = $endToEndReplay.requiredGate
            materialTailFailures =
                $endToEndReplay.materialTailFailures
            performanceGate = $endToEndReplay.performanceGate
            accepted = $endToEndReplay.accepted
        })
    }
    $formalReceipt = [ordered]@{
        schemaVersion = 2
        suite = 'summit.gpu-driven-instance-policy-formal-v2'
        optimizedAxes = [string[]]@('Upload', 'Culling')
        outputContractRole = 'caller-semantic-match-constraint'
        primitiveBackendRole =
            'portable-in-this-matrix;compose-pr1-resolver'
        sourceCommit = $gitCommit
        sourceSnapshotSha256 = $sourceSnapshotSha256
        unityVersion = $expectedUnityVersion
        measurementContractFingerprint = $measurementContractFingerprint
        calibrationProtocol = $calibrationProtocol
        calibrationSeed = $calibrationSeed
        holdoutSeed = $holdoutSeed
        replaySeed = $replaySeed
        protocolAmendmentReason = $protocolAmendmentReason
        seedsArePairwiseDistinct = $true
        sampleFramesPerBlock = $SampleFrames
        blocksPerRun = 8
        calibrationRunCount = $cells.Count
        holdoutRunCount = $cells.Count
        selectorEquivalenceReplayRunCount = $cells.Count
        endToEndReplayRunCount = $cells.Count
        replayRunCount = 2 * $cells.Count
        totalRunCount = 4 * $cells.Count
        expectedRawFrameCount =
            4 * $cells.Count * 8 * $SampleFrames
        gpuFrameCoverageContract = [ordered]@{
            unavailableLiteral = 'unavailable'
            minimumPerBlockValidPercent = 95.0
            minimumPairedComparisonPercent = 90.0
            otherTimedMetricsRequireCompleteCoverage = $true
        }
        profilePath = $generatedProfilePath
        profileSha256 = $profileResult.profileSha256
        selectionReceiptPath = $selectionReceiptPath
        candidateFailureRetainsEvidenceAndSelectsMeasuredBaseline = $true
        selectorEquivalenceReplayAllAccepted =
            @($replayReceipts | Where-Object {
            -not [bool]$_.accepted
        }).Count -eq 0
        endToEndReplayAllAccepted =
            @($endToEndReplayReceipts | Where-Object {
                -not [bool]$_.accepted
            }).Count -eq 0
        replayAllAccepted =
            @($replayReceipts | Where-Object {
                -not [bool]$_.accepted
            }).Count -eq 0 -and
            @($endToEndReplayReceipts | Where-Object {
                -not [bool]$_.accepted
            }).Count -eq 0
        replay = [object[]]$replayReceipts.ToArray()
        endToEndReplay = [object[]]$endToEndReplayReceipts.ToArray()
    }
    Write-Utf8Json $formalReceipt (
        Join-Path $outputRoot 'formal-matrix-receipt.json') 40
    if (-not (Test-PolicySha256Equal `
            -Left (Get-FileHash -LiteralPath $generatedProfilePath `
                -Algorithm SHA256).Hash `
            -Right $script:frozenProfileSha256)) {
        throw 'Frozen policy profile changed during replay.'
    }

    $summaryRows = foreach ($cellReceipt in @($selectionReceipt.cells)) {
        $endToEndCell = @($endToEndReplayReceipts | Where-Object {
            [string]$_.ruleId -ceq [string]$cellReceipt.ruleId
        })
        if ($endToEndCell.Count -ne 1) {
            throw "Formal summary has no unique end-to-end replay for '$($cellReceipt.ruleId)'."
        }
        $endToEndPrimaryMetric =
            [string]$endToEndCell[0].performanceGate.primaryMetric
        $endToEndPrimary = $endToEndCell[0].performanceGate.comparisons.
            PSObject.Properties[$endToEndPrimaryMetric].Value
        [pscustomobject][ordered]@{
            ruleId = [string]$cellReceipt.ruleId
            candidateKind = [string]$cellReceipt.candidateKind
            candidateAccepted = [bool]$cellReceipt.candidateAccepted
            selectedUploadMode = [string]$cellReceipt.selectedUploadMode
            selectedCullingMode = [string]$cellReceipt.selectedCullingMode
            selectedPrimitiveBackend =
                [string]$cellReceipt.selectedPrimitiveBackend
            calibrationAccepted =
                [bool]$cellReceipt.calibrationGate.accepted
            holdoutAccepted = [bool]$cellReceipt.holdoutGate.accepted
            holdoutPrimaryMetric =
                [string]$cellReceipt.holdoutGate.primaryMetric
            holdoutMeanImprovementPercent =
                [double]$cellReceipt.holdoutGate.comparisons.
                    PSObject.Properties[
                        [string]$cellReceipt.holdoutGate.primaryMetric].
                    Value.meanImprovementPercent
            holdoutPositiveWinPercent =
                [double]$cellReceipt.holdoutGate.comparisons.
                    PSObject.Properties[
                        [string]$cellReceipt.holdoutGate.primaryMetric].
                    Value.positiveWinPercent
            holdoutP95RegressionPercent =
                [double]$cellReceipt.holdoutGate.comparisons.
                    PSObject.Properties[
                        [string]$cellReceipt.holdoutGate.primaryMetric].
                    Value.p95RegressionPercent
            holdoutP99RegressionPercent =
                [double]$cellReceipt.holdoutGate.comparisons.
                    PSObject.Properties[
                        [string]$cellReceipt.holdoutGate.primaryMetric].
                    Value.p99RegressionPercent
            holdoutPairedDeltaMeanMs =
                [double]$cellReceipt.holdoutGate.comparisons.
                    PSObject.Properties[
                        [string]$cellReceipt.holdoutGate.primaryMetric].
                    Value.pairedDelta.mean
            holdoutPairedDeltaP95Ms =
                [double]$cellReceipt.holdoutGate.comparisons.
                    PSObject.Properties[
                        [string]$cellReceipt.holdoutGate.primaryMetric].
                    Value.pairedDelta.p95
            holdoutPairedDeltaP99Ms =
                [double]$cellReceipt.holdoutGate.comparisons.
                    PSObject.Properties[
                        [string]$cellReceipt.holdoutGate.primaryMetric].
                    Value.pairedDelta.p99
            holdoutPairedImprovementMeanPercent =
                [double]$cellReceipt.holdoutGate.comparisons.
                    PSObject.Properties[
                        [string]$cellReceipt.holdoutGate.primaryMetric].
                    Value.pairedImprovementPercent.mean
            holdoutPairedImprovementP95Percent =
                [double]$cellReceipt.holdoutGate.comparisons.
                    PSObject.Properties[
                        [string]$cellReceipt.holdoutGate.primaryMetric].
                    Value.pairedImprovementPercent.p95
            holdoutPairedImprovementP99Percent =
                [double]$cellReceipt.holdoutGate.comparisons.
                    PSObject.Properties[
                        [string]$cellReceipt.holdoutGate.primaryMetric].
                    Value.pairedImprovementPercent.p99
            endToEndAccepted = [bool]$endToEndCell[0].accepted
            endToEndRequiredGate =
                [string]$endToEndCell[0].requiredGate
            endToEndDecisionMismatchCount =
                [int]$endToEndCell[0].decisionMismatchCount
            endToEndMaterialTailFailureCount =
                @($endToEndCell[0].materialTailFailures).Count
            endToEndSafeRejectedDecision =
                [bool]$endToEndCell[0].safeRejectedDecision
            endToEndCandidatePerformanceGateAccepted =
                [bool]$endToEndCell[0].performanceGate.accepted
            endToEndPrimaryMetric = $endToEndPrimaryMetric
            endToEndMeanImprovementPercent =
                [double]$endToEndPrimary.meanImprovementPercent
            endToEndPositiveWinPercent =
                [double]$endToEndPrimary.positiveWinPercent
            endToEndP95RegressionPercent =
                [double]$endToEndPrimary.p95RegressionPercent
            endToEndP99RegressionPercent =
                [double]$endToEndPrimary.p99RegressionPercent
        }
    }
    $summaryRows | Export-Csv -LiteralPath (
        Join-Path $outputRoot 'formal-matrix-summary.csv') `
        -NoTypeInformation -Encoding utf8
    $report = [Collections.Generic.List[string]]::new()
    $report.Add('# GPU-driven instance automatic policy formal report')
    $report.Add('')
    $report.Add(('- Build commit: `{0}`' -f $gitCommit))
    $report.Add(('- Unity: `{0}`; graphics API: D3D12' -f
        $expectedUnityVersion))
    $report.Add(('- Seeds: calibration `{0}`, holdout `{1}`, replay `{2}`' -f
        $calibrationSeed, $holdoutSeed, $replaySeed))
    $report.Add('- Scope: this matrix calibrates upload and culling only. Output is a caller semantic constraint; primitive backend stays Portable here and composes with the independent PR1 resolver.')
    $report.Add("- Formal runs: $($cells.Count * 4) total ($($cells.Count) calibration, $($cells.Count) holdout, $($cells.Count) selector-equivalence replay, $($cells.Count) end-to-end replay).")
    $report.Add("- Raw measured rows: $($cells.Count * 4 * 8 * $SampleFrames)")
    $report.Add('- Candidate failure policy: retain evidence and reuse the measured baseline decision (formal baseline is Full + Flat + Portable).')
    $report.Add('- Metric availability: CPU/native/submission metrics require 100% coverage; GPU frame time uses literal `unavailable`, at least 95% valid rows per block, and at least 90% jointly valid paired comparisons.')
    $report.Add('- Selector-equivalence replay: ActualAuto decision equals ForcedSelected per row; selector mean must be <= 0.01 ms and selector P99 <= 0.05 ms; the isolated selector must complete 100,000 iterations with zero allocation and zero unstable decisions. Full performance-tail comparisons are retained as diagnostics; the independent end-to-end replay owns their acceptance.')
    $report.Add('- End-to-end replay: Full+Flat versus ActualAuto on the replay seed. Accepted candidates repeat the full mean/wins/P95/P99 gate; rejected candidates remain Full+Flat+Portable with no material P99 regression.')
    $report.Add('')
    $report.Add('| Rule | Candidate | Selected upload | Selected culling | Holdout mean | E2E gate | E2E mean | E2E wins |')
    $report.Add('|---|---:|---|---|---:|---:|---:|---:|')
    foreach ($row in $summaryRows) {
        $report.Add(('| {0} | {1} | {2} | {3} | {4:F2}% | {5} | {6:F2}% | {7:F2}% |' -f
            $row.ruleId,
            $row.candidateAccepted,
            $row.selectedUploadMode,
            $row.selectedCullingMode,
            $row.holdoutMeanImprovementPercent,
            $row.endToEndAccepted,
            $row.endToEndMeanImprovementPercent,
            $row.endToEndPositiveWinPercent))
    }
    [IO.File]::WriteAllLines(
        (Join-Path $outputRoot 'BENCHMARK_REPORT.md'),
        $report,
        [Text.UTF8Encoding]::new($false))
}

$expectedPhaseIds = [string[]]@(
    $expectedPhaseSpecifications | ForEach-Object {
        [string]$_.phaseId
    })
$completedPhaseRecords = Assert-GpuBenchmarkPhaseCoverage `
    -ReceiptsDirectory $script:phaseReceiptsRoot `
    -ExpectedPhaseIds $expectedPhaseIds `
    -RunContractFingerprint $script:runContractFingerprint
$phaseReceiptIdentities = [object[]]@(
    $completedPhaseRecords | Sort-Object phaseId | ForEach-Object {
        [pscustomobject][ordered]@{
            phaseId = [string]$_.phaseId
            recordSha256 = [string]$_.recordSha256
            evidenceSha256 = [string]$_.evidenceSha256
        }
    })
$phaseReceiptSetSha256 =
    Get-GpuBenchmarkObjectSha256 $phaseReceiptIdentities

$finalPayload =
    Get-GpuBenchmarkPlayerPayload -PlayerPath $resolvedPlayerPath
if ([string]$finalPayload.sha256 -cne [string]$initialPayload.sha256) {
    throw 'Player payload changed during the policy workflow.'
}
$gitFinal = Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
if ($gitFinal.head -ine $gitCommit -or [bool]$gitFinal.dirty) {
    throw 'Git state changed during the policy workflow.'
}
$finalSourceSha256 =
    Get-PolicyCombinedSha256 (Get-PolicySourceFiles) $projectRoot
if ($finalSourceSha256 -cne $sourceSnapshotSha256) {
    throw 'Policy benchmark source changed during the workflow.'
}
$runnerConfig['playerRuns'] = [object[]]$playerRuns.ToArray()
$runnerConfig['playerPayloadStableThroughRun'] = $true
$runnerConfig['sourceHashesStableThroughRun'] = $true
$runnerConfig['gitFinal'] = $gitFinal
$runnerConfig['checkpoint']['phaseCoverageAccepted'] = $true
$runnerConfig['checkpoint']['phaseReceiptSetSha256'] =
    $phaseReceiptSetSha256
$runnerConfig['checkpoint']['completedPhaseCount'] =
    $completedPhaseRecords.Count
$runnerConfig['evidenceValid'] = $true
$runnerConfig['formalContractSatisfied'] =
    $Workflow -ceq 'FormalMatrix'
$runnerConfig['finalizedUtc'] = (Get-Date).ToUniversalTime().ToString('O')
Write-Utf8Json $runnerConfig $runnerConfigPath 40

$hashFiles = Get-ChildItem -LiteralPath $outputRoot -Recurse -File |
    Where-Object {
        $_.Name -cne 'SHA256SUMS' -and
        -not [string]::Equals(
            $_.FullName,
            $runLockPath,
            [StringComparison]::OrdinalIgnoreCase)
    } |
    Sort-Object FullName
$hashLines = foreach ($file in $hashFiles) {
    $relative = $file.FullName.Substring($outputRoot.Length).
        TrimStart([char[]]@('\', '/')).Replace('\', '/')
    (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash +
        '  ' + $relative
}
[void](Write-GpuBenchmarkAtomicText `
    -Path (Join-Path $outputRoot 'SHA256SUMS') `
    -Text (($hashLines -join [Environment]::NewLine) +
        [Environment]::NewLine))

Write-Host "GPU-driven instance policy workflow passed: $outputRoot"
}
finally {
    Exit-GpuBenchmarkRunLock $runLockHandle
}
