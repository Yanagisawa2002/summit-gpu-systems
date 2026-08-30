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
    [string]$ProfilePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$expectedUnityVersion = '6000.5.2f1'
$calibrationSeed = 20260830
$holdoutSeed = 20260831
$replaySeed = 20260832
$calibrationProtocol = 'gpu-driven-policy-calibration-v1-holdout-v1'
$formalSampleFrames = 900
$formalWarmupFrames = 60
$suite = 'summit.gpu-driven-instance-policy-runner'
$measurementContract = @'
summit.gpu-driven-instance-policy.measurement.v1
schedule=ABBA;BAAB
blocks=8
frameTimingLatency=4
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
replayGate=decision-exact;no-material-p99-regression
endToEndReplayGate=accepted:candidate-gate;rejected:full-flat-portable+no-material-p99-regression
'@

$projectRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent $PSScriptRoot))
$provenanceModulePath = Join-Path $PSScriptRoot 'GpuBenchmarkProvenance.psm1'
$policyModulePath =
    Join-Path $PSScriptRoot 'GpuDrivenInstancePolicyBenchmark.psm1'
$selectorScriptPath =
    Join-Path $PSScriptRoot 'Select-GpuDrivenInstancePolicyProfile.ps1'
$runnerTestPath = Join-Path $PSScriptRoot (
    'Tests\Test-GpuDrivenInstancePolicyBenchmarkProvenance.ps1')
foreach ($path in @(
        $provenanceModulePath,
        $policyModulePath,
        $selectorScriptPath,
        $runnerTestPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required policy benchmark tool is missing: $path"
    }
}
Import-Module -Name $provenanceModulePath -Force
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
    [IO.File]::WriteAllText(
        $Path,
        ($Value | ConvertTo-Json -Depth $Depth) + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
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
            $policyModulePath,
            $selectorScriptPath,
            $runnerTestPath,
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

    $scenarioId = $Phase + '-' + [string]$Scenario.ruleId +
        '-seed' + [string]$RunSeed
    $runDirectory = Join-Path $outputRoot (
        (Join-Path $Phase ([string]$Scenario.ruleId)))
    New-Item -ItemType Directory -Path $runDirectory -Force | Out-Null
    if ($null -ne (Get-ChildItem -LiteralPath $runDirectory -Force |
            Select-Object -First 1)) {
        throw "Run directory is not empty: $runDirectory"
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
        if (-not (Test-Path -LiteralPath $RunProfilePath -PathType Leaf)) {
            throw "Policy profile is missing: $RunProfilePath"
        }
        if (-not [string]::IsNullOrWhiteSpace($script:frozenProfileSha256) -and
            (Get-FileHash -LiteralPath $RunProfilePath -Algorithm SHA256).Hash `
                -cne $script:frozenProfileSha256) {
            throw 'Frozen policy profile changed before replay.'
        }
        $arguments += @(
            '-gpu-driven-instance-policy-profile-path',
            (Quote-PolicyProcessArgument (
                [IO.Path]::GetFullPath($RunProfilePath))))
    }

    $startedUtc = (Get-Date).ToUniversalTime().ToString('O')
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
    $requireProfile = $RunLeftCase -in @('forced-selected', 'actual-auto') -or
        $RunRightCase -in @('forced-selected', 'actual-auto')
    $receiptParameters = @{
        Directory = $runDirectory
        ExpectedSampleFrames = $SampleFrames
        ExpectedSeed = $RunSeed
        ExpectedBuildCommit = $gitCommit
        ExpectedUnityVersion = $expectedUnityVersion
        ExpectedPipelineFingerprint = $pipelineContractFingerprint
        ExpectedShaderFingerprint = $shaderContractFingerprint
        ExpectedMeasurementFingerprint = $measurementContractFingerprint
        ExpectedCalibrationProtocol = $calibrationProtocol
        ExpectedLeftCaseId = Get-CanonicalCaseId $RunLeftCase
        ExpectedRightCaseId = Get-CanonicalCaseId $RunRightCase
    }
    if ($requireProfile) {
        $receiptParameters['RequireAcceptedProfile'] = $true
    }
    $evidence = Assert-PolicyBenchmarkEvidence @receiptParameters
    if ($requireProfile) {
        $expectedProfileSha = (Get-FileHash -LiteralPath $RunProfilePath `
            -Algorithm SHA256).Hash
        if ([string]$evidence.config.profileSha256 -cne
            $expectedProfileSha) {
            throw "$scenarioId loaded a different profile payload."
        }
    }
    $currentPayload =
        Get-GpuBenchmarkPlayerPayload -PlayerPath $resolvedPlayerPath
    if ([string]$currentPayload.sha256 -cne [string]$initialPayload.sha256) {
        throw "$scenarioId changed the frozen Player payload."
    }
    $playerRuns.Add([pscustomobject][ordered]@{
        scenarioId = $scenarioId
        phase = $Phase
        seed = $RunSeed
        leftCase = Get-CanonicalCaseId $RunLeftCase
        rightCase = Get-CanonicalCaseId $RunRightCase
        startedUtc = $startedUtc
        finishedUtc = (Get-Date).ToUniversalTime().ToString('O')
        exitCode = $process.ExitCode
        evidenceDirectory = $runDirectory
        evidenceSha256 = $evidence.evidenceSha256
        playerPayloadSha256 = $currentPayload.sha256
    })
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
    'Summit.GpuAutotuning.Tests.GpuDrivenInstancePolicySelectorTests.WarmSelectPathAllocatesZeroBytesAcrossOneHundredThousandCalls',
    'Summit.GpuAutotuning.Tests.GpuDrivenInstancePolicyProfileStoreTests',
    'Summit.GpuDrivenInstances.Tests.Editor.dll',
    'Summit.GpuDrivenInstances.Tests.GpuInstanceStateUploaderTests',
    'Summit.GpuDrivenInstances.Tests.GpuInstanceStateUploaderTests.PlannedDirtyUploadRecordsExactlyTheInspectedPlan',
    'Summit.GpuDrivenInstances.Tests.GpuInstanceStateUploaderTests.DisposedPlannedSourceCannotBeReplacedWithSameRevision',
    'Summit.GpuDrivenInstances.Tests.GpuInstanceStateUploaderTests.PlanGenerationExhaustionIsPermanentFailClosed',
    'Summit.GpuDrivenInstances.Tests.GpuInstanceStateUploaderTests.WarmPlannedRecordingDoesNotAllocateManagedMemory')

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot (
        "Reports\GpuDrivenInstancePolicyBenchmark\$Workflow-$stamp")
}
elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) {
    if ($null -ne (Get-ChildItem -LiteralPath $outputRoot -Force |
            Select-Object -First 1)) {
        throw 'Benchmark output directory must be new or empty.'
    }
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($PlayerPath)) {
    $PlayerPath = Join-Path $projectRoot (
        "Builds\GpuDrivenInstancePolicyBenchmark\$stamp\" +
        'GpuDrivenInstancePolicyBenchmark.exe')
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
if (Test-Path -LiteralPath $playerPayloadRoot) {
    if ($null -ne (Get-ChildItem -LiteralPath $playerPayloadRoot -Force |
            Select-Object -First 1)) {
        throw 'Fresh Player payload directory must be new or empty.'
    }
}
New-Item -ItemType Directory -Path $playerPayloadRoot -Force | Out-Null

$sourceFiles = Get-PolicySourceFiles
$sourceSnapshotSha256 =
    Get-PolicyCombinedSha256 $sourceFiles $projectRoot
$pipelineContractFingerprint = Get-PolicyCombinedSha256 `
    (Get-PipelineContractFiles) $projectRoot
$shaderContractFingerprint = Get-PolicyCombinedSha256 `
    (Get-ShaderContractFiles) $projectRoot
$measurementContractFingerprint = Get-PolicyTextSha256 $measurementContract

$preflightRoot = Join-Path $outputRoot 'preflight'
New-Item -ItemType Directory -Path $preflightRoot -Force | Out-Null
$generatedEditModeResultsPath =
    Join-Path $preflightRoot 'editmode-results.xml'
$editModeLogPath = Join-Path $preflightRoot 'unity-editmode.log'
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

$playerRuns = [Collections.Generic.List[object]]::new()
$script:frozenProfileSha256 = ''
$runnerConfig = [ordered]@{
    schemaVersion = 1
    suite = $suite
    workflow = $Workflow
    startedUtc = (Get-Date).ToUniversalTime().ToString('O')
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
    sourceHashesStableAcrossBuild = $false
    playerPayload = $null
    playerPayloadStableThroughRun = $false
    playerRuns = @()
    evidenceValid = $false
    formalContractSatisfied = $false
}
$runnerConfigPath = Join-Path $outputRoot 'runner-config.json'
Write-Utf8Json $runnerConfig $runnerConfigPath 30

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
Write-Utf8Json $initialPayload (
    Join-Path $outputRoot 'player-payload-manifest.json') 30
Write-Utf8Json $runnerConfig $runnerConfigPath 30

if ($Workflow -ceq 'SingleScenario') {
    $single = [pscustomobject][ordered]@{
        ruleId = 'single-scenario'
        instanceCount = $InstanceCount
        viewCount = $ViewCount
        visibilityBasisPoints = $VisibilityBasisPoints
        dirtyBasisPoints = $DirtyBasisPoints
        requiredOutput = $RequiredOutput
    }
    $singleEvidence = Invoke-PolicyPlayer `
        -Scenario $single `
        -Phase 'single' `
        -RunSeed $Seed `
        -RunLeftCase $LeftCase `
        -RunRightCase $RightCase `
        -RunProfilePath $ProfilePath
    Write-Utf8Json ([ordered]@{
        schemaVersion = 1
        evidenceDirectory = $singleEvidence.directory
        evidenceSha256 = $singleEvidence.evidenceSha256
        leftCase = [string]$singleEvidence.config.leftCase
        rightCase = [string]$singleEvidence.config.rightCase
        rawFrameCount = @($singleEvidence.raw).Count
    }) (Join-Path $outputRoot 'single-scenario-receipt.json') 12
}
else {
    $cells = New-FormalPolicyCells
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
        calibrationSeed = $calibrationSeed
        holdoutSeed = $holdoutSeed
        replaySeed = $replaySeed
        replayContracts = [ordered]@{
            selectorEquivalence = [ordered]@{
                leftCaseId = 'gpu-driven-policy/forced-selected'
                rightCaseId = 'gpu-driven-policy/actual-auto'
                gate = 'decision-exact;no-material-p99-regression'
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
    Write-Utf8Json $selectionManifest $selectionManifestPath 20
    $generatedProfilePath =
        Join-Path $outputRoot 'gpu-driven-instance-policy.json'
    $selectionReceiptPath =
        Join-Path $outputRoot 'policy-selection-receipt.json'
    $profileResult = & $selectorScriptPath `
        -ManifestPath $selectionManifestPath `
        -OutputPath $generatedProfilePath `
        -SelectionReceiptPath $selectionReceiptPath
    $selectionReceipt = Get-Content -LiteralPath $selectionReceiptPath -Raw |
        ConvertFrom-Json
    if ([int]$selectionReceipt.cells.Count -ne $cells.Count -or
        -not [bool]$selectionReceipt.allRulesHoldoutAccepted -or
        -not [bool]$selectionReceipt.
            candidateRejectionSelectsMeasuredBaseline) {
        throw 'Policy selection receipt is incomplete.'
    }
    $script:frozenProfileSha256 =
        (Get-FileHash -LiteralPath $generatedProfilePath `
            -Algorithm SHA256).Hash
    if ($script:frozenProfileSha256 -cne
        [string]$profileResult.profileSha256) {
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
            throw "Replay '$($cell.ruleId)' failed exact decision or tail gates."
        }
        $replayReceipts.Add([pscustomobject][ordered]@{
            ruleId = $cell.ruleId
            evidenceDirectory = $evidence.directory
            evidenceSha256 = $evidence.evidenceSha256
            decisionMismatchCount = $replay.decisionMismatchCount
            materialTailFailures = $replay.materialTailFailures
            selectorCpuMs = $replay.selectorCpuMs
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
        schemaVersion = 1
        suite = 'summit.gpu-driven-instance-policy-formal-v1'
        sourceCommit = $gitCommit
        unityVersion = $expectedUnityVersion
        calibrationSeed = $calibrationSeed
        holdoutSeed = $holdoutSeed
        replaySeed = $replaySeed
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
    if ((Get-FileHash -LiteralPath $generatedProfilePath `
            -Algorithm SHA256).Hash -cne $script:frozenProfileSha256) {
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
    $report.Add("- Formal runs: $($cells.Count * 4) total ($($cells.Count) calibration, $($cells.Count) holdout, $($cells.Count) selector-equivalence replay, $($cells.Count) end-to-end replay).")
    $report.Add("- Raw measured rows: $($cells.Count * 4 * 8 * $SampleFrames)")
    $report.Add('- Candidate failure policy: retain evidence and reuse the measured baseline decision (formal baseline is Full + Flat + Portable).')
    $report.Add('- Selector-equivalence replay: ActualAuto decision equals ForcedSelected per row; selector overhead is recorded; no material P99 regression.')
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
$runnerConfig['evidenceValid'] = $true
$runnerConfig['formalContractSatisfied'] =
    $Workflow -ceq 'FormalMatrix'
$runnerConfig['finalizedUtc'] = (Get-Date).ToUniversalTime().ToString('O')
Write-Utf8Json $runnerConfig $runnerConfigPath 40

$hashFiles = Get-ChildItem -LiteralPath $outputRoot -Recurse -File |
    Where-Object { $_.Name -cne 'SHA256SUMS' } |
    Sort-Object FullName
$hashLines = foreach ($file in $hashFiles) {
    $relative = $file.FullName.Substring($outputRoot.Length).
        TrimStart([char[]]@('\', '/')).Replace('\', '/')
    (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash +
        '  ' + $relative
}
[IO.File]::WriteAllLines(
    (Join-Path $outputRoot 'SHA256SUMS'),
    $hashLines,
    [Text.UTF8Encoding]::new($false))

Write-Host "GPU-driven instance policy workflow passed: $outputRoot"
