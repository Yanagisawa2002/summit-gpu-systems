[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$SelectionReceiptPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$modulePath = Join-Path $PSScriptRoot 'GpuDrivenInstancePolicyBenchmark.psm1'
Import-Module -Name $modulePath -Force

function Assert-HexLength {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][int]$Length,
        [Parameter(Mandatory = $true)][string]$Name
    )
    if ($Value -notmatch ('^[0-9a-fA-F]{' + $Length + '}$')) {
        throw "$Name must be exactly $Length hexadecimal characters."
    }
}

function Get-EnumValue {
    param(
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Value
    )
    $tables = @{
        Upload = @{ None = 0; Dirty = 1; Full = 2 }
        Output = @{ CulledTail = 0; VisibleOnly = 1 }
        Culling = @{ Flat = 0; Hierarchy = 1 }
        Backend = @{ Portable = 1; WaveOps = 2 }
    }
    if (-not $tables.ContainsKey($Kind) -or
        -not $tables[$Kind].ContainsKey($Value)) {
        throw "Unsupported $Kind enum value '$Value'."
    }
    return [int]$tables[$Kind][$Value]
}

function Get-UniqueInteger {
    param(
        [Parameter(Mandatory = $true)][object[]]$Rows,
        [Parameter(Mandatory = $true)][string]$Property,
        [Parameter(Mandatory = $true)][string]$Context
    )
    $values = @($Rows | ForEach-Object {
        $propertyValue = $_.PSObject.Properties[$Property]
        if ($null -eq $propertyValue) {
            throw "$Context is missing '$Property'."
        }
        [int64]$propertyValue.Value
    } | Select-Object -Unique)
    if ($values.Count -ne 1 -or
        $values[0] -lt [int]::MinValue -or
        $values[0] -gt [int]::MaxValue) {
        throw "$Context does not have one Int32 '$Property' value."
    }
    return [int]$values[0]
}

function Get-UniqueString {
    param(
        [Parameter(Mandatory = $true)][object[]]$Rows,
        [Parameter(Mandatory = $true)][string]$Property,
        [Parameter(Mandatory = $true)][string]$Context
    )
    $values = @($Rows | ForEach-Object {
        $propertyValue = $_.PSObject.Properties[$Property]
        if ($null -eq $propertyValue) {
            throw "$Context is missing '$Property'."
        }
        [string]$propertyValue.Value
    } | Select-Object -Unique)
    if ($values.Count -ne 1 -or
        [string]::IsNullOrWhiteSpace([string]$values[0])) {
        throw "$Context does not have one non-empty '$Property' value."
    }
    return [string]$values[0]
}

function Get-MeasuredDecision {
    param(
        [Parameter(Mandatory = $true)]$Evidence,
        [Parameter(Mandatory = $true)][string]$CaseId
    )
    $rows = @($Evidence.raw | Where-Object {
        [string]$_.caseId -ceq $CaseId
    })
    if ($rows.Count -eq 0) {
        throw "No measured decision rows exist for '$CaseId'."
    }
    if (@($rows | Where-Object {
            [uint32]$_.decisionFlags -ne 0 -or
            -not (ConvertTo-PolicyBoolean $_.decisionAccepted) -or
            -not (ConvertTo-PolicyBoolean $_.decisionStableExpected)
        }).Count -ne 0) {
        throw "Measured decision '$CaseId' contains fallback or instability."
    }
    return [pscustomobject][ordered]@{
        uploadMode = Get-UniqueString $rows 'decisionUploadMode' $CaseId
        outputMode = Get-UniqueString $rows 'decisionOutputMode' $CaseId
        cullingMode = Get-UniqueString $rows 'decisionCullingMode' $CaseId
        primitiveBackend = Get-UniqueString `
            $rows 'decisionPrimitiveBackend' $CaseId
    }
}

function Assert-MeasuredDecisionEqual {
    param(
        [Parameter(Mandatory = $true)]$Left,
        [Parameter(Mandatory = $true)]$Right,
        [Parameter(Mandatory = $true)][string]$Context
    )
    foreach ($property in @(
            'uploadMode', 'outputMode', 'cullingMode',
            'primitiveBackend')) {
        if ([string]$Left.PSObject.Properties[$property].Value -cne
            [string]$Right.PSObject.Properties[$property].Value) {
            throw "$Context measured decision differs at '$property'."
        }
    }
}

function Assert-CandidateKindContract {
    param(
        [Parameter(Mandatory = $true)]$Baseline,
        [Parameter(Mandatory = $true)]$Candidate,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Upload', 'Hierarchy')][string]$CandidateKind,
        [Parameter(Mandatory = $true)][string]$Context
    )
    if ($CandidateKind -ceq 'Upload') {
        if ([string]$Baseline.uploadMode -ceq
                [string]$Candidate.uploadMode -or
            [string]$Baseline.outputMode -cne
                [string]$Candidate.outputMode -or
            [string]$Baseline.cullingMode -cne
                [string]$Candidate.cullingMode -or
            [string]$Baseline.primitiveBackend -cne
                [string]$Candidate.primitiveBackend) {
            throw "$Context Upload candidate must change only uploadMode."
        }
        return
    }
    if ([string]$Baseline.cullingMode -cne 'Flat' -or
        [string]$Candidate.cullingMode -cne 'Hierarchy' -or
        [string]$Baseline.uploadMode -cne [string]$Candidate.uploadMode -or
        [string]$Baseline.outputMode -cne [string]$Candidate.outputMode -or
        [string]$Baseline.primitiveBackend -cne
            [string]$Candidate.primitiveBackend) {
        throw "$Context Hierarchy candidate must change only cullingMode " +
            'from Flat to Hierarchy.'
    }
}

function Get-ExactSelectorMetrics {
    param(
        [Parameter(Mandatory = $true)]$Evidence,
        [Parameter(Mandatory = $true)][string]$CandidateCaseId
    )
    $rows = @($Evidence.raw | Where-Object {
        [string]$_.caseId -ceq $CandidateCaseId
    })
    if ($rows.Count -eq 0) {
        throw "No candidate rows exist for '$CandidateCaseId'."
    }
    foreach ($required in @(
            'plannedUploadCallCount', 'plannedUploadedRecordCount',
            'changedInstanceCount')) {
        if ($null -eq $rows[0].PSObject.Properties[$required]) {
            throw "Candidate evidence is missing selector fact '$required'."
        }
    }
    $active = Get-UniqueInteger $rows 'instanceCount' $CandidateCaseId
    $views = Get-UniqueInteger $rows 'viewCount' $CandidateCaseId
    $changed = Get-UniqueInteger $rows 'changedInstanceCount' $CandidateCaseId
    $plannedUploaded = Get-UniqueInteger `
        $rows 'plannedUploadedRecordCount' $CandidateCaseId
    $uploadCalls = Get-UniqueInteger `
        $rows 'plannedUploadCallCount' $CandidateCaseId
    $visiblePairs = [int64]$Evidence.config.visiblePairCount
    $totalPairs = [int64]$active * [int64]$views
    if ($active -le 0 -or $views -le 0 -or
        $changed -lt 0 -or $changed -gt $active -or
        $visiblePairs -lt 0 -or $visiblePairs -gt $totalPairs) {
        throw "$CandidateCaseId has invalid selector counts."
    }
    $dirtyBp = [int](([int64]$changed * 10000L) / $active)
    $visibleBp = [int](($visiblePairs * 10000L) / $totalPairs)
    $amplificationBp = if ($changed -eq 0) {
        # Exact parity with GpuDrivenInstancePolicySelector: amplification is
        # zero whenever there are no dirty instances, including a forced Full
        # upload used by hierarchy calibration cells.
        0
    }
    else {
        [int](([int64]$plannedUploaded * 10000L) / $changed)
    }
    return [pscustomobject][ordered]@{
        activeInstanceCount = $active
        dirtyBasisPoints = $dirtyBp
        uploadCallCount = $uploadCalls
        uploadAmplificationBasisPoints = $amplificationBp
        visibleBasisPoints = $visibleBp
        hierarchyCandidateBasisPoints =
            [int]$Evidence.config.hierarchyCandidateBasisPoints
        clusterCount = [int]$Evidence.config.clusterCount
        viewCount = $views
    }
}

function New-ObservedRuleRange {
    param(
        [Parameter(Mandatory = $true)]$CalibrationMetrics,
        [Parameter(Mandatory = $true)]$HoldoutMetrics,
        [switch]$ExitRange
    )
    if ($CalibrationMetrics.activeInstanceCount -ne
            $HoldoutMetrics.activeInstanceCount -or
        $CalibrationMetrics.viewCount -ne $HoldoutMetrics.viewCount) {
        throw 'Active instance and view counts changed between policy seeds.'
    }
    $Metrics = $HoldoutMetrics
    $activeMargin = [Math]::Max(
        1,
        [int][Math]::Ceiling($Metrics.activeInstanceCount * 0.02))
    $dirtyMinimum = [Math]::Min(
        $CalibrationMetrics.dirtyBasisPoints,
        $HoldoutMetrics.dirtyBasisPoints)
    $dirtyMaximum = [Math]::Max(
        $CalibrationMetrics.dirtyBasisPoints,
        $HoldoutMetrics.dirtyBasisPoints)
    $callMinimum = [Math]::Min(
        $CalibrationMetrics.uploadCallCount,
        $HoldoutMetrics.uploadCallCount)
    $callMaximum = [Math]::Max(
        $CalibrationMetrics.uploadCallCount,
        $HoldoutMetrics.uploadCallCount)
    $amplificationMinimum = [Math]::Min(
        $CalibrationMetrics.uploadAmplificationBasisPoints,
        $HoldoutMetrics.uploadAmplificationBasisPoints)
    $amplificationMaximum = [Math]::Max(
        $CalibrationMetrics.uploadAmplificationBasisPoints,
        $HoldoutMetrics.uploadAmplificationBasisPoints)
    $visibleMinimum = [Math]::Min(
        $CalibrationMetrics.visibleBasisPoints,
        $HoldoutMetrics.visibleBasisPoints)
    $visibleMaximum = [Math]::Max(
        $CalibrationMetrics.visibleBasisPoints,
        $HoldoutMetrics.visibleBasisPoints)
    $candidateMinimum = [Math]::Min(
        $CalibrationMetrics.hierarchyCandidateBasisPoints,
        $HoldoutMetrics.hierarchyCandidateBasisPoints)
    $candidateMaximum = [Math]::Max(
        $CalibrationMetrics.hierarchyCandidateBasisPoints,
        $HoldoutMetrics.hierarchyCandidateBasisPoints)
    $clusterMinimum = [Math]::Min(
        $CalibrationMetrics.clusterCount,
        $HoldoutMetrics.clusterCount)
    $clusterMaximum = [Math]::Max(
        $CalibrationMetrics.clusterCount,
        $HoldoutMetrics.clusterCount)
    return [ordered]@{
        minActiveInstanceCount = if ($ExitRange) {
            [Math]::Max(0, $Metrics.activeInstanceCount - $activeMargin)
        } else { $Metrics.activeInstanceCount }
        maxActiveInstanceCount = if ($ExitRange) {
            [Math]::Min(
                [int]::MaxValue,
                [int64]$Metrics.activeInstanceCount + $activeMargin)
        } else { $Metrics.activeInstanceCount }
        minDirtyBasisPoints = if ($ExitRange) {
            [Math]::Max(0, $dirtyMinimum - 50)
        } else { [Math]::Max(0, $dirtyMinimum - 1) }
        maxDirtyBasisPoints = if ($ExitRange) {
            [Math]::Min(10000, $dirtyMaximum + 50)
        } else { [Math]::Min(10000, $dirtyMaximum + 1) }
        minUploadCallCount = if ($ExitRange) {
            [Math]::Max(0, $callMinimum - 1)
        } else { $callMinimum }
        maxUploadCallCount = if ($ExitRange) {
            [Math]::Min([int]::MaxValue, $callMaximum + 1)
        } else { $callMaximum }
        minUploadAmplificationBasisPoints = if ($ExitRange) {
            [Math]::Max(
                0,
                $amplificationMinimum - 100)
        } else { [Math]::Max(0, $amplificationMinimum - 10) }
        maxUploadAmplificationBasisPoints = if ($ExitRange) {
            [Math]::Min(
                [int]::MaxValue,
                [int64]$amplificationMaximum + 100)
        } else {
            [Math]::Min(
                [int]::MaxValue,
                [int64]$amplificationMaximum + 10)
        }
        minVisibleBasisPoints = if ($ExitRange) {
            [Math]::Max(0, $visibleMinimum - 100)
        } else { [Math]::Max(0, $visibleMinimum - 1) }
        maxVisibleBasisPoints = if ($ExitRange) {
            [Math]::Min(10000, $visibleMaximum + 100)
        } else { [Math]::Min(10000, $visibleMaximum + 1) }
        minHierarchyCandidateBasisPoints = if ($ExitRange) {
            [Math]::Max(
                0,
                $candidateMinimum - 100)
        } else { [Math]::Max(0, $candidateMinimum - 25) }
        maxHierarchyCandidateBasisPoints = if ($ExitRange) {
            [Math]::Min(
                10000,
                $candidateMaximum + 100)
        } else { [Math]::Min(10000, $candidateMaximum + 25) }
        minClusterCount = if ($ExitRange) {
            [Math]::Max(0, $clusterMinimum - 1)
        } else { $clusterMinimum }
        maxClusterCount = if ($ExitRange) {
            [Math]::Min([int]::MaxValue, $clusterMaximum + 1)
        } else { $clusterMaximum }
        minViewCount = $Metrics.viewCount
        maxViewCount = $Metrics.viewCount
    }
}

function Assert-EquivalentEnvironment {
    param(
        [Parameter(Mandatory = $true)]$Left,
        [Parameter(Mandatory = $true)]$Right,
        [Parameter(Mandatory = $true)][string]$Context
    )
    foreach ($property in @(
            'operatingSystem', 'processorType', 'graphicsDeviceName',
            'graphicsDeviceVendor', 'graphicsDeviceVendorId',
            'graphicsDeviceId', 'graphicsDeviceType',
            'graphicsDeviceVersion', 'graphicsShaderLevel',
            'deviceFingerprint', 'unityVersion')) {
        if ([string]$Left.device.PSObject.Properties[$property].Value -cne
            [string]$Right.device.PSObject.Properties[$property].Value) {
            throw "$Context environment differs at '$property'."
        }
    }
}

$resolvedManifest = [IO.Path]::GetFullPath($ManifestPath)
if (-not (Test-Path -LiteralPath $resolvedManifest -PathType Leaf)) {
    throw "Policy selection manifest is missing: $resolvedManifest"
}
$manifest = Get-Content -LiteralPath $resolvedManifest -Raw |
    ConvertFrom-Json
if ([int]$manifest.schemaVersion -ne 1 -or
    [string]$manifest.suite -cne 'summit.gpu-driven-instance-policy-selection' -or
    @($manifest.cells).Count -eq 0) {
    throw 'Policy selection manifest has an unsupported schema or no cells.'
}
Assert-HexLength ([string]$manifest.sourceCommit) 40 'sourceCommit'
foreach ($name in @(
        'sourceSnapshotSha256', 'pipelineContractFingerprint',
        'shaderContractFingerprint', 'measurementContractFingerprint')) {
    Assert-HexLength ([string]$manifest.$name) 64 $name
}
if ([string]$manifest.unityVersion -cne '6000.5.2f1') {
    throw 'Profile selection requires the frozen Unity 6000.5.2f1 contract.'
}
$seeds = @(
    [int]$manifest.calibrationSeed,
    [int]$manifest.holdoutSeed,
    [int]$manifest.replaySeed)
if (@($seeds | Select-Object -Unique).Count -ne 3) {
    throw 'Calibration, holdout, and replay seeds must be distinct.'
}

$cellReceipts = [Collections.Generic.List[object]]::new()
$rules = [Collections.Generic.List[object]]::new()
$firstHoldout = $null
$ruleIds = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
foreach ($cell in @($manifest.cells)) {
    if ([string]::IsNullOrWhiteSpace([string]$cell.ruleId) -or
        -not $ruleIds.Add([string]$cell.ruleId)) {
        throw 'Every policy cell must have a unique non-empty ruleId.'
    }
    if ([string]$cell.candidateKind -cnotin @('Upload', 'Hierarchy')) {
        throw "Cell '$($cell.ruleId)' has an invalid candidateKind."
    }
    $calibration = Assert-PolicyBenchmarkEvidence `
        -Directory ([string]$cell.calibrationDirectory) `
        -ExpectedSampleFrames ([int]$cell.sampleFrames) `
        -ExpectedSeed ([int]$manifest.calibrationSeed) `
        -ExpectedBuildCommit ([string]$manifest.sourceCommit) `
        -ExpectedUnityVersion ([string]$manifest.unityVersion) `
        -ExpectedPipelineFingerprint (
            [string]$manifest.pipelineContractFingerprint) `
        -ExpectedShaderFingerprint (
            [string]$manifest.shaderContractFingerprint) `
        -ExpectedMeasurementFingerprint (
            [string]$manifest.measurementContractFingerprint) `
        -ExpectedCalibrationProtocol (
            [string]$manifest.calibrationProtocol) `
        -ExpectedLeftCaseId ([string]$cell.baselineCaseId) `
        -ExpectedRightCaseId ([string]$cell.candidateCaseId)
    $holdout = Assert-PolicyBenchmarkEvidence `
        -Directory ([string]$cell.holdoutDirectory) `
        -ExpectedSampleFrames ([int]$cell.sampleFrames) `
        -ExpectedSeed ([int]$manifest.holdoutSeed) `
        -ExpectedBuildCommit ([string]$manifest.sourceCommit) `
        -ExpectedUnityVersion ([string]$manifest.unityVersion) `
        -ExpectedPipelineFingerprint (
            [string]$manifest.pipelineContractFingerprint) `
        -ExpectedShaderFingerprint (
            [string]$manifest.shaderContractFingerprint) `
        -ExpectedMeasurementFingerprint (
            [string]$manifest.measurementContractFingerprint) `
        -ExpectedCalibrationProtocol (
            [string]$manifest.calibrationProtocol) `
        -ExpectedLeftCaseId ([string]$cell.baselineCaseId) `
        -ExpectedRightCaseId ([string]$cell.candidateCaseId)
    Assert-EquivalentEnvironment $calibration $holdout $cell.ruleId
    if ([int]$calibration.config.instanceCount -ne
            [int]$holdout.config.instanceCount -or
        [int]$calibration.config.viewCount -ne
            [int]$holdout.config.viewCount -or
        [int]$calibration.config.visibilityBasisPoints -ne
            [int]$holdout.config.visibilityBasisPoints -or
        [int]$calibration.config.dirtyBasisPoints -ne
            [int]$holdout.config.dirtyBasisPoints -or
        [string]$calibration.config.requiredOutputMode -cne
            [string]$holdout.config.requiredOutputMode) {
        throw "Cell '$($cell.ruleId)' changed workload between seeds."
    }
    if ($null -eq $firstHoldout) {
        $firstHoldout = $holdout
    }
    else {
        Assert-EquivalentEnvironment $firstHoldout $holdout $cell.ruleId
    }

    $calibrationGate = Test-PolicyCandidateGate `
        $calibration ([string]$cell.baselineCaseId) `
        ([string]$cell.candidateCaseId) ([string]$cell.candidateKind)
    $holdoutGate = Test-PolicyCandidateGate `
        $holdout ([string]$cell.baselineCaseId) `
        ([string]$cell.candidateCaseId) ([string]$cell.candidateKind)
    $candidateAccepted = [bool]$calibrationGate.accepted -and
        [bool]$holdoutGate.accepted
    $calibrationCandidateDecision = Get-MeasuredDecision `
        $calibration ([string]$cell.candidateCaseId)
    $holdoutCandidateDecision = Get-MeasuredDecision `
        $holdout ([string]$cell.candidateCaseId)
    $calibrationBaselineDecision = Get-MeasuredDecision `
        $calibration ([string]$cell.baselineCaseId)
    $holdoutBaselineDecision = Get-MeasuredDecision `
        $holdout ([string]$cell.baselineCaseId)
    Assert-MeasuredDecisionEqual $calibrationCandidateDecision `
        $holdoutCandidateDecision "$($cell.ruleId) candidate"
    Assert-MeasuredDecisionEqual $calibrationBaselineDecision `
        $holdoutBaselineDecision "$($cell.ruleId) baseline"
    Assert-CandidateKindContract `
        $holdoutBaselineDecision `
        $holdoutCandidateDecision `
        ([string]$cell.candidateKind) `
        ([string]$cell.ruleId)
    if ([string]$holdoutBaselineDecision.uploadMode -cne 'Full' -or
        [string]$holdoutBaselineDecision.cullingMode -cne 'Flat' -or
        [string]$holdoutBaselineDecision.primitiveBackend -cne 'Portable' -or
        [string]$holdoutBaselineDecision.outputMode -cne
            [string]$holdout.config.requiredOutputMode) {
        throw "Cell '$($cell.ruleId)' measured baseline is not the frozen " +
            'Full + caller output + Flat + Portable safety policy.'
    }
    if ([string]$holdoutCandidateDecision.uploadMode -cne
            [string]$cell.candidateUploadMode -or
        [string]$holdoutCandidateDecision.cullingMode -cne
            [string]$cell.candidateCullingMode -or
        [string]$holdoutCandidateDecision.outputMode -cne
            [string]$holdout.config.requiredOutputMode -or
        [string]$holdoutCandidateDecision.primitiveBackend -cne
            'Portable') {
        throw "Cell '$($cell.ruleId)' manifest candidate decision does not " +
            'match the unique measured raw decision.'
    }
    $selectedDecision = if ($candidateAccepted) {
        $holdoutCandidateDecision
    }
    else {
        $holdoutBaselineDecision
    }
    $selectedUpload = [string]$selectedDecision.uploadMode
    $selectedCulling = [string]$selectedDecision.cullingMode
    $selectedBackend = [string]$selectedDecision.primitiveBackend
    $selectedOutput = [string]$selectedDecision.outputMode
    $calibrationMetrics = Get-ExactSelectorMetrics $calibration `
        ([string]$cell.candidateCaseId)
    $holdoutMetrics = Get-ExactSelectorMetrics $holdout `
        ([string]$cell.candidateCaseId)
    $evidenceId = Get-PolicyTextSha256 -Text (
        $calibration.evidenceSha256 + "`n" +
        $holdout.evidenceSha256 + "`n" +
        [string]$cell.ruleId + "`n" +
        $candidateAccepted + "`n" +
        $selectedUpload + "`n" + $selectedCulling)
    $rules.Add([ordered]@{
        ruleId = [string]$cell.ruleId
        holdoutAccepted = $true
        holdoutEvidenceId = $evidenceId
        holdoutSampleCount = @($holdout.raw | Where-Object {
            [string]$_.caseId -ceq [string]$cell.candidateCaseId
        }).Count
        requiredOutputMode = Get-EnumValue Output $selectedOutput
        uploadMode = Get-EnumValue Upload $selectedUpload
        cullingMode = Get-EnumValue Culling $selectedCulling
        primitiveBackend = Get-EnumValue Backend $selectedBackend
        requiredConsecutiveFrames = 2
        enter = New-ObservedRuleRange $calibrationMetrics $holdoutMetrics
        exit = New-ObservedRuleRange `
            $calibrationMetrics $holdoutMetrics -ExitRange
    })
    $cellReceipts.Add([pscustomobject][ordered]@{
        ruleId = [string]$cell.ruleId
        candidateKind = [string]$cell.candidateKind
        baselineCaseId = [string]$cell.baselineCaseId
        candidateCaseId = [string]$cell.candidateCaseId
        calibrationEvidenceSha256 = $calibration.evidenceSha256
        holdoutEvidenceSha256 = $holdout.evidenceSha256
        calibrationGate = $calibrationGate
        holdoutGate = $holdoutGate
        candidateAccepted = $candidateAccepted
        calibrationCandidateDecision = $calibrationCandidateDecision
        holdoutCandidateDecision = $holdoutCandidateDecision
        calibrationBaselineDecision = $calibrationBaselineDecision
        holdoutBaselineDecision = $holdoutBaselineDecision
        selectedDecisionSource = if ($candidateAccepted) {
            'measured-candidate'
        } else { 'measured-baseline' }
        selectedUploadMode = $selectedUpload
        selectedOutputMode = $selectedOutput
        selectedCullingMode = $selectedCulling
        selectedPrimitiveBackend = $selectedBackend
        calibrationSelectorMetrics = $calibrationMetrics
        holdoutSelectorMetrics = $holdoutMetrics
        holdoutEvidenceId = $evidenceId
    })
}

$environmentDevice = $firstHoldout.device
$evidenceSetBuilder = [Text.StringBuilder]::new()
foreach ($receipt in $cellReceipts) {
    [void]$evidenceSetBuilder.Append($receipt.ruleId)
    [void]$evidenceSetBuilder.Append('=')
    [void]$evidenceSetBuilder.Append($receipt.holdoutEvidenceId)
    [void]$evidenceSetBuilder.Append("`n")
}
$holdoutEvidenceSetId = Get-PolicyTextSha256 $evidenceSetBuilder.ToString()
$profile = [ordered]@{
    schemaVersion = 1
    policyContractVersion = 2
    profileRevision = 1
    generatedUtc = (Get-Date).ToUniversalTime().ToString('O')
    sourceCommit = ([string]$manifest.sourceCommit).ToLowerInvariant()
    calibrationProtocol = [string]$manifest.calibrationProtocol
    measurementContractFingerprint =
        ([string]$manifest.measurementContractFingerprint).ToUpperInvariant()
    holdoutEvidenceSetId = $holdoutEvidenceSetId
    holdoutAccepted = $true
    unityVersion = [string]$manifest.unityVersion
    autotuningPackageVersion = '0.3.0'
    gpuDrivenInstancesPackageVersion = '0.4.0'
    processorType = [string]$environmentDevice.processorType
    operatingSystem = [string]$environmentDevice.operatingSystem
    pipelineContractFingerprint =
        [string]$manifest.pipelineContractFingerprint
    shaderContractFingerprint = [string]$manifest.shaderContractFingerprint
    device = [ordered]@{
        schemaVersion = 1
        vendorId = [int]$environmentDevice.graphicsDeviceVendorId
        deviceId = [int]$environmentDevice.graphicsDeviceId
        vendor = [string]$environmentDevice.graphicsDeviceVendor
        deviceName = [string]$environmentDevice.graphicsDeviceName
        graphicsApi = [string]$environmentDevice.graphicsDeviceType
        graphicsVersion = [string]$environmentDevice.graphicsDeviceVersion
        shaderLevel = [int]$environmentDevice.graphicsShaderLevel
    }
    rules = [object[]]$rules.ToArray()
}

$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutput
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$profileJson = $profile | ConvertTo-Json -Depth 20
$temporaryPath = $resolvedOutput + '.tmp'
[IO.File]::WriteAllText(
    $temporaryPath,
    $profileJson + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporaryPath -Destination $resolvedOutput -Force
$profileSha256 = (Get-FileHash -LiteralPath $resolvedOutput `
    -Algorithm SHA256).Hash

if ([string]::IsNullOrWhiteSpace($SelectionReceiptPath)) {
    $SelectionReceiptPath = $resolvedOutput + '.selection.json'
}
$resolvedReceipt = [IO.Path]::GetFullPath($SelectionReceiptPath)
$receiptDirectory = Split-Path -Parent $resolvedReceipt
New-Item -ItemType Directory -Path $receiptDirectory -Force | Out-Null
$selectionReceipt = [ordered]@{
    schemaVersion = 1
    suite = 'summit.gpu-driven-instance-policy-selection'
    optimizedAxes = [string[]]@('Upload', 'Culling')
    outputContractRole = 'caller-semantic-match-constraint'
    primitiveBackendRole =
        'portable-compatibility-field;compose-pr1-resolver'
    generatedUtc = (Get-Date).ToUniversalTime().ToString('O')
    sourceCommit = [string]$manifest.sourceCommit
    manifestPath = $resolvedManifest
    manifestSha256 = (Get-FileHash -LiteralPath $resolvedManifest `
        -Algorithm SHA256).Hash
    profilePath = $resolvedOutput
    profileSha256 = $profileSha256
    holdoutEvidenceSetId = $holdoutEvidenceSetId
    calibrationSeed = [int]$manifest.calibrationSeed
    holdoutSeed = [int]$manifest.holdoutSeed
    replaySeed = [int]$manifest.replaySeed
    allRulesHoldoutAccepted = $true
    candidateRejectionSelectsMeasuredBaseline = $true
    cells = [object[]]$cellReceipts.ToArray()
}
[IO.File]::WriteAllText(
    $resolvedReceipt,
    ($selectionReceipt | ConvertTo-Json -Depth 30) +
        [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))

Write-Output ([pscustomobject][ordered]@{
    profilePath = $resolvedOutput
    profileSha256 = $profileSha256
    selectionReceiptPath = $resolvedReceipt
    holdoutEvidenceSetId = $holdoutEvidenceSetId
    ruleCount = $rules.Count
})
