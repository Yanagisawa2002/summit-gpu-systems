[CmdletBinding()]
param(
    [string]$UnityPath,
    [string]$OutputDirectory,
    [string]$PlayerPath,
    [ValidateRange(0, 16)]
    [int]$DeviceIndex = 0,
    [ValidateSet(
        'single',
        'discovery-amd-r9700-v1',
        'calibration-nvidia-rtx4090-v1',
        'formal-amd-r9700-v1',
        'formal-amd-r9700-contention-v1')]
    [string]$MatrixPreset = 'single',
    [string]$ScenarioId = 'custom',
    [ValidateRange(1024, 16776960)]
    [int]$ElementCount = 1048576,
    [ValidateRange(1, 16776960)]
    [int]$BinCount = 4096,
    [ValidateSet('uniform', 'hotset4', 'hotset16', 'singlebin')]
    [string]$Distribution = 'uniform',
    [int]$Seed = 20260730,
    [ValidateRange(1, 8)]
    [int]$SuperRounds = 4,
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
    [ValidateRange(5, 120)]
    [int]$EditModeTimeoutMinutes = 30,
    [ValidateRange(5, 240)]
    [int]$PlayerTimeoutMinutes = 90,
    [switch]$FormalAcceptanceMode,
    [string]$EditModeResultsPath,
    [switch]$AllowMissingGpuTiming,
    [switch]$SkipBuild,
    [switch]$SkipSummary
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-GeneratorV3SingleBinKey {
    param(
        [Parameter(Mandatory = $true)][int]$Seed,
        [Parameter(Mandatory = $true)][int]$BinCount
    )
    if ($BinCount -le 0) {
        throw 'Generator-v3 exact single-bin key requires positive C.'
    }
    $seedBytes = [BitConverter]::GetBytes([int]$Seed)
    $unsignedSeed = [BitConverter]::ToUInt32($seedBytes, 0)
    return [int]($unsignedSeed % [uint32]$BinCount)
}

function ConvertTo-GpuAdaptiveBinningScenario {
    param(
        [Parameter(Mandatory = $true)][object]$Scenario,
        [Parameter(Mandatory = $true)][string]$MatrixPreset
    )
    $scenarioId = [string]$Scenario.scenarioId
    $elementCount = [int]$Scenario.elementCount
    $binCount = [int]$Scenario.binCount
    $distribution = [string]$Scenario.distribution
    $seed = [int]$Scenario.seed

    $dominantSetCardinality = switch ($distribution) {
        'singlebin' { 1; break }
        'hotset4' { 4; break }
        'hotset16' { 16; break }
        'uniform' { $binCount; break }
        default {
            throw (
                "Unsupported distribution '$distribution' in " +
                "scenario '$scenarioId'.")
        }
    }
    $bracketGroup = if (
        $elementCount -eq 1048576 -and
        $binCount -eq 16 -and
        $distribution -in @(
            'singlebin',
            'hotset4',
            'uniform')) {
        'n1048576-c16-contention'
    }
    else {
        ''
    }
    $expectedExactSingleBinKey =
        if ($distribution -ceq 'singlebin') {
            Get-GeneratorV3SingleBinKey `
                -Seed $seed `
                -BinCount $binCount
        }
        else {
            -1
        }

    $declaredExactSingleBinKey = $null
    $declaredExactSingleBinKeyPresent = $false
    if ($Scenario -is [System.Collections.IDictionary]) {
        $declaredExactSingleBinKeyPresent =
            $Scenario.Contains('exactSingleBinKey')
        if ($declaredExactSingleBinKeyPresent) {
            $declaredExactSingleBinKey =
                [int]$Scenario['exactSingleBinKey']
        }
    }
    else {
        $declaredProperty =
            $Scenario.PSObject.Properties['exactSingleBinKey']
        $declaredExactSingleBinKeyPresent = $null -ne $declaredProperty
        if ($declaredExactSingleBinKeyPresent) {
            $declaredExactSingleBinKey = [int]$declaredProperty.Value
        }
    }
    if ($MatrixPreset -ceq 'formal-amd-r9700-contention-v1' -and
        -not $declaredExactSingleBinKeyPresent) {
        throw (
            "Formal contention scenario '$scenarioId' lacks " +
            'exactSingleBinKey.')
    }
    if ($declaredExactSingleBinKeyPresent -and
        $declaredExactSingleBinKey -ne $expectedExactSingleBinKey) {
        throw (
            "Scenario '$scenarioId' exactSingleBinKey differs " +
            'from generator-v3 unchecked uint seed modulo C.')
    }
    return [ordered]@{
        scenarioId = $scenarioId
        elementCount = $elementCount
        binCount = $binCount
        distribution = $distribution
        seed = $seed
        exactSingleBinKey = $expectedExactSingleBinKey
        dominantSetCardinality = $dominantSetCardinality
        bracketGroup = $bracketGroup
    }
}

$contentionFormalContract = [ordered]@{
    unityVersion = '6000.5.2f1'
    deviceIndex = 0
    matrixPreset = 'formal-amd-r9700-contention-v1'
    matrixRole = 'holdout'
    superRounds = 4
    expectedPairCount = 8
    warmupFrames = 60
    sampleFrames = 900
    cooldownFrames = 15
    dispatchesPerFrame = 1
    editModeTimeoutMinutes = 30
    primitiveBackend = 'wave-ops'
    keyDomain = 'guaranteed-in-range'
    orderingContract = 'unspecified-within-bin'
    signedImprovementConvention = 'positive-radix-faster'
    selectorPolicyClaimKind =
        'classification-replay-not-recordadaptive-timing'
    requiredInnerProfilerMarkersEnabled = $false
    activeDevice = [ordered]@{
        graphicsDeviceVendorId = 0x1002
        graphicsDeviceId = 0x7551
        graphicsDeviceType = 'Direct3D12'
    }
    crossoverBracket = [ordered]@{
        axis = 'dominant-set-cardinality'
        fixedElementCount = 1048576
        fixedBinCount = 16
        orderedDominantSetCardinalities = @(1, 4, 16)
        candidatePairs = @('1-4', '1-16')
        commonBracketSeed = 20261002
        hotset4ColdTailFraction = 0.125
        requireSignChangingAcceptedWinner = $true
        requiredLowerWinner = 'radix'
        requiredUpperWinner = 'direct'
        requiredValidatedPairCount = 2
        requireAllCandidatePairsValidated = $true
        selectorPolicy =
            'radix-if-exact-calibrated-cell-else-direct'
        selectorPolicyPredicate =
            'full-element-count-bin-count-distribution-exact-single-bin-key-tuple'
        exactRadixCandidateCells = @(
            [ordered]@{
                elementCount = 262144
                binCount = 16
                distribution = 'singlebin'
                exactSingleBinKey = 9
            },
            [ordered]@{
                elementCount = 1048576
                binCount = 16
                distribution = 'singlebin'
                exactSingleBinKey = 10
            })
        requiredPredictionMatchCount = 5
    }
    selectorTailGuard = [ordered]@{
        expectedPairCount = 8
        minimumWinningP99Pairs = 7
        minimumWorstPairP99ImprovementPercent = -10.0
        requiredValidatedCellCount = 5
        requireAcceptedWinner = $true
    }
    decisiveGate = [ordered]@{
        minimumMedianImprovementPercent = 5.0
        minimumMedianAbsoluteReductionMs = 0.005
        minimumWinningPairs = 6
        expectedPairs = 8
        requireAbBaSameSign = $true
        minimumMedianP99ImprovementPercent = -2.0
        maximumEmptyScopeP99Ms = 0.005
        minimumAcceptedCellsPerBackendForCrossover = 2
    }
}
$legacyFormalContract = [ordered]@{
    unityVersion = '6000.5.2f1'
    deviceIndex = 0
    matrixPreset = 'formal-amd-r9700-v1'
    matrixRole = 'holdout'
    superRounds = 4
    expectedPairCount = 8
    warmupFrames = 60
    sampleFrames = 900
    cooldownFrames = 15
    dispatchesPerFrame = 1
    editModeTimeoutMinutes = 30
    primitiveBackend = 'wave-ops'
    keyDomain = 'guaranteed-in-range'
    orderingContract = 'unspecified-within-bin'
    signedImprovementConvention = 'positive-radix-faster'
    activeDevice = [ordered]@{
        graphicsDeviceVendorId = 0x1002
        graphicsDeviceId = 0x7551
        graphicsDeviceType = 'Direct3D12'
    }
    decisiveGate = [ordered]@{
        minimumMedianImprovementPercent = 5.0
        minimumMedianAbsoluteReductionMs = 0.005
        minimumWinningPairs = 6
        expectedPairs = 8
        requireAbBaSameSign = $true
        minimumMedianP99ImprovementPercent = -2.0
        maximumEmptyScopeP99Ms = 0.005
        minimumAcceptedCellsPerBackendForCrossover = 2
    }
}
$formalContract = if ($MatrixPreset -ceq 'formal-amd-r9700-v1') {
    $legacyFormalContract
}
else {
    $contentionFormalContract
}
$discoveryScenarios = @(
    [ordered]@{ scenarioId='discover-uniform-n262144-c64'; elementCount=262144; binCount=64; distribution='uniform'; seed=20260801 },
    [ordered]@{ scenarioId='discover-hotset16-n262144-c64'; elementCount=262144; binCount=64; distribution='hotset16'; seed=20260802 },
    [ordered]@{ scenarioId='discover-uniform-n262144-c256'; elementCount=262144; binCount=256; distribution='uniform'; seed=20260803 },
    [ordered]@{ scenarioId='discover-hotset16-n262144-c256'; elementCount=262144; binCount=256; distribution='hotset16'; seed=20260804 },
    [ordered]@{ scenarioId='discover-uniform-n262144-c4096'; elementCount=262144; binCount=4096; distribution='uniform'; seed=20260805 },
    [ordered]@{ scenarioId='discover-hotset16-n262144-c4096'; elementCount=262144; binCount=4096; distribution='hotset16'; seed=20260806 },
    [ordered]@{ scenarioId='discover-uniform-n262144-c65536'; elementCount=262144; binCount=65536; distribution='uniform'; seed=20260807 },
    [ordered]@{ scenarioId='discover-hotset16-n262144-c65536'; elementCount=262144; binCount=65536; distribution='hotset16'; seed=20260808 },
    [ordered]@{ scenarioId='discover-uniform-n1048576-c64'; elementCount=1048576; binCount=64; distribution='uniform'; seed=20260809 },
    [ordered]@{ scenarioId='discover-hotset16-n1048576-c64'; elementCount=1048576; binCount=64; distribution='hotset16'; seed=20260810 },
    [ordered]@{ scenarioId='discover-uniform-n1048576-c256'; elementCount=1048576; binCount=256; distribution='uniform'; seed=20260811 },
    [ordered]@{ scenarioId='discover-hotset16-n1048576-c256'; elementCount=1048576; binCount=256; distribution='hotset16'; seed=20260812 },
    [ordered]@{ scenarioId='discover-uniform-n1048576-c4096'; elementCount=1048576; binCount=4096; distribution='uniform'; seed=20260813 },
    [ordered]@{ scenarioId='discover-hotset16-n1048576-c4096'; elementCount=1048576; binCount=4096; distribution='hotset16'; seed=20260814 },
    [ordered]@{ scenarioId='discover-uniform-n1048576-c65536'; elementCount=1048576; binCount=65536; distribution='uniform'; seed=20260815 },
    [ordered]@{ scenarioId='discover-hotset16-n1048576-c65536'; elementCount=1048576; binCount=65536; distribution='hotset16'; seed=20260816 }
)
$legacyFormalScenarios = @(
    [ordered]@{ scenarioId='uniform-n1048576-c64'; elementCount=1048576; binCount=64; distribution='uniform'; seed=20260901 },
    [ordered]@{ scenarioId='hotset16-n1048576-c64'; elementCount=1048576; binCount=64; distribution='hotset16'; seed=20260902 },
    [ordered]@{ scenarioId='uniform-n1048576-c256'; elementCount=1048576; binCount=256; distribution='uniform'; seed=20260903 },
    [ordered]@{ scenarioId='hotset16-n1048576-c256'; elementCount=1048576; binCount=256; distribution='hotset16'; seed=20260904 },
    [ordered]@{ scenarioId='uniform-n1048576-c4096'; elementCount=1048576; binCount=4096; distribution='uniform'; seed=20260905 },
    [ordered]@{ scenarioId='hotset16-n1048576-c4096'; elementCount=1048576; binCount=4096; distribution='hotset16'; seed=20260906 },
    [ordered]@{ scenarioId='uniform-n1048576-c65536'; elementCount=1048576; binCount=65536; distribution='uniform'; seed=20260907 },
    [ordered]@{ scenarioId='hotset16-n1048576-c65536'; elementCount=1048576; binCount=65536; distribution='hotset16'; seed=20260908 }
)
$contentionFormalScenarios = @(
    [ordered]@{ scenarioId='singlebin-n262144-c16'; elementCount=262144; binCount=16; distribution='singlebin'; seed=20261001; exactSingleBinKey=9 },
    [ordered]@{ scenarioId='singlebin-n1048576-c16'; elementCount=1048576; binCount=16; distribution='singlebin'; seed=20261002; exactSingleBinKey=10 },
    [ordered]@{ scenarioId='hotset4-n1048576-c16'; elementCount=1048576; binCount=16; distribution='hotset4'; seed=20261002; exactSingleBinKey=-1 },
    [ordered]@{ scenarioId='uniform-n1048576-c16'; elementCount=1048576; binCount=16; distribution='uniform'; seed=20261002; exactSingleBinKey=-1 },
    [ordered]@{ scenarioId='uniform-n1048576-c65536'; elementCount=1048576; binCount=65536; distribution='uniform'; seed=20261005; exactSingleBinKey=-1 }
)
$nvidiaCalibrationScenarios = @(
    [ordered]@{ scenarioId='cal-singlebin-n262144-c16'; elementCount=262144; binCount=16; distribution='singlebin'; seed=20261111 },
    [ordered]@{ scenarioId='cal-singlebin-n1048576-c16'; elementCount=1048576; binCount=16; distribution='singlebin'; seed=20261112 },
    [ordered]@{ scenarioId='cal-hotset4-n1048576-c16'; elementCount=1048576; binCount=16; distribution='hotset4'; seed=20261113 },
    [ordered]@{ scenarioId='cal-uniform-n1048576-c16'; elementCount=1048576; binCount=16; distribution='uniform'; seed=20261114 },
    [ordered]@{ scenarioId='cal-uniform-n1048576-c4096'; elementCount=1048576; binCount=4096; distribution='uniform'; seed=20261115 },
    [ordered]@{ scenarioId='cal-uniform-n1048576-c65536'; elementCount=1048576; binCount=65536; distribution='uniform'; seed=20261116 }
)

if ($FormalAcceptanceMode) {
    $violations = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in @(
        @('DeviceIndex', $DeviceIndex, $formalContract.deviceIndex),
        @('SuperRounds', $SuperRounds, $formalContract.superRounds),
        @('WarmupFrames', $WarmupFrames, $formalContract.warmupFrames),
        @('SampleFrames', $SampleFrames, $formalContract.sampleFrames),
        @('CooldownFrames', $CooldownFrames, $formalContract.cooldownFrames),
        @('DispatchesPerFrame', $DispatchesPerFrame,
            $formalContract.dispatchesPerFrame),
        @('EditModeTimeoutMinutes', $EditModeTimeoutMinutes,
            $formalContract.editModeTimeoutMinutes))) {
        if ([int64]$entry[1] -ne [int64]$entry[2]) {
            $violations.Add(
                "$($entry[0])=$($entry[1]); expected $($entry[2])")
        }
    }
    if ($MatrixPreset -cne $formalContract.matrixPreset) {
        $violations.Add(
            "MatrixPreset='$MatrixPreset'; expected '$($formalContract.matrixPreset)'")
    }
    if ($SkipBuild) { $violations.Add('SkipBuild is forbidden.') }
    if ($SkipSummary) { $violations.Add('SkipSummary is forbidden.') }
    if ($AllowMissingGpuTiming) {
        $violations.Add('AllowMissingGpuTiming is forbidden.')
    }
    if (-not [string]::IsNullOrWhiteSpace($EditModeResultsPath)) {
        $violations.Add(
            'EditModeResultsPath is discovery-only; formal evidence is ' +
            'generated by this runner in the clean worktree.')
    }
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $violations.Add(
            'OutputDirectory is required for formal evidence and must be ' +
            'outside the Git worktree.')
    }
    if ($violations.Count -ne 0) {
        throw "Formal acceptance contract rejected:`n$($violations -join "`n")"
    }
}
elseif ($MatrixPreset -in @(
        'formal-amd-r9700-v1',
        'formal-amd-r9700-contention-v1')) {
    throw (
        "MatrixPreset='$MatrixPreset' requires -FormalAcceptanceMode.")
}

if ($AllowMissingGpuTiming) {
    throw 'AllowMissingGpuTiming is not supported by this A/B benchmark.'
}

function Quote-ProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Value)
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Resolve-UnityEditor {
    param([string]$RequestedPath)
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [System.IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Unity editor is missing: $resolved"
        }
        return $resolved
    }
    $candidate =
        'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe'
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw 'UnityPath is required because the default editor is missing.'
    }
    return $candidate
}

function Get-CombinedSha256 {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo[]]$Files,
        [Parameter(Mandatory = $true)][string]$RelativeTo
    )
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $builder = [System.Text.StringBuilder]::new()
        foreach ($file in @($Files | Sort-Object {
                $_.FullName.Substring($RelativeTo.Length).
                    TrimStart([char[]]@('\', '/')).Replace('\', '/')
            })) {
            $relative = $file.FullName.Substring($RelativeTo.Length).
                TrimStart([char[]]@('\', '/')).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            [void]$builder.Append($relative)
            [void]$builder.Append("`0")
            [void]$builder.Append($hash)
            [void]$builder.Append("`n")
        }
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($builder.ToString())
        return ([BitConverter]::ToString(
            $sha.ComputeHash($bytes))).Replace('-', '')
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
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "EditMode results are missing: $resolved"
    }
    $rawXml = Get-Content -LiteralPath $resolved -Raw
    [xml]$xml = $rawXml
    $run = $xml.'test-run'
    if ($null -eq $run) {
        throw "EditMode result is not an NUnit test-run document: $resolved"
    }
    $observedIdentities =
        [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal)
    foreach ($assembly in @(
        $xml.SelectNodes("//test-suite[@type='Assembly']"))) {
        [void]$observedIdentities.Add([string]$assembly.name)
    }
    foreach ($fixture in @(
        $xml.SelectNodes("//test-suite[@type='TestFixture']"))) {
        [void]$observedIdentities.Add([string]$fixture.fullname)
    }
    $missingIdentities = @(
        $ExpectedIdentities | Where-Object {
            -not $observedIdentities.Contains($_)
        })
    $file = Get-Item -LiteralPath $resolved
    return [ordered]@{
        path = $resolved
        sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
        lastWriteUtc = $file.LastWriteTimeUtc.ToString('o')
        result = [string]$run.result
        total = [int]$run.total
        passed = [int]$run.passed
        failed = [int]$run.failed
        skipped = [int]$run.skipped
        inconclusive = [int]$run.inconclusive
        identityMatchMode = 'exact-nunit-node-v1'
        expectedIdentities = @($ExpectedIdentities)
        observedIdentities = @($observedIdentities | Sort-Object)
        missingIdentities = @($missingIdentities)
    }
}

$projectRoot = [System.IO.Path]::GetFullPath(
    (Split-Path -Parent $PSScriptRoot))
$projectVersionPath =
    Join-Path $projectRoot 'ProjectSettings\ProjectVersion.txt'
$projectVersionLine = Get-Content -LiteralPath $projectVersionPath |
    Where-Object { $_ -like 'm_EditorVersion:*' } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($projectVersionLine)) {
    throw "Unable to read Unity version from $projectVersionPath"
}
$projectUnityVersion = ($projectVersionLine -split ':', 2)[1].Trim()
if ($FormalAcceptanceMode -and
    $projectUnityVersion -cne $formalContract.unityVersion) {
    throw (
        "Formal acceptance requires Unity $($formalContract.unityVersion); " +
        "project declares $projectUnityVersion.")
}

$provenanceModulePath =
    Join-Path $PSScriptRoot 'GpuBenchmarkProvenance.psm1'
Import-Module -Name $provenanceModulePath -Force
$gitStart = Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
if (-not $AllowMissingGpuTiming -and [bool]$gitStart.dirty) {
    throw (
        "Strict GPU timing requires a clean worktree:`n" +
        "$(@($gitStart.statusLines) -join "`n")")
}
if ($FormalAcceptanceMode -and [string]$gitStart.branch -eq 'HEAD') {
    throw 'Formal acceptance requires a named Git branch.'
}
$gitCommit = [string]$gitStart.head
$gitBranch = [string]$gitStart.branch

$expectedEditModeIdentities = @(
    'Summit.GpuDirectBinning.Tests.Editor.dll',
    'Summit.GpuDirectBinning.Tests.CpuDirectBinningOracleTests',
    'Summit.GpuDirectBinning.Tests.GpuDirectSpatialBinnerContractTests',
    'Summit.GpuDirectBinning.Tests.GpuDirectSpatialBinnerIntegrationTests',
    'Summit.GpuPrimitives.Tests.Editor.dll',
    'Summit.GpuPrimitives.Tests.CpuPrimitiveOracleTests',
    'Summit.GpuPrimitives.Tests.GpuPrimitivesIntegrationTests',
    'Summit.GpuPrimitives.Tests.GpuRadixKeyBitBoundaryTests',
    'Summit.GpuAdaptiveBinning.Tests.Editor.dll',
    'Summit.GpuAdaptiveBinning.Tests.GpuAdaptiveSpatialBinnerContractTests',
    'Summit.GpuAdaptiveBinning.Tests.GpuRadixSpatialBinnerIntegrationTests',
    'Summit.GpuAdaptiveBinning.Benchmark.Tests.Editor.dll',
    'Summit.GpuAdaptiveBinning.Benchmark.Tests.GpuAdaptiveBinningBenchmarkScheduleTests',
    'Summit.GpuAdaptiveBinning.Benchmark.Tests.GpuAdaptiveBinningInputDistributionTests',
    'Summit.GpuAdaptiveBinning.Benchmark.Tests.GpuAdaptiveBinningCpuOracleTests'
)
$editModeResults = $null
if (-not $FormalAcceptanceMode -and
    -not [string]::IsNullOrWhiteSpace($EditModeResultsPath)) {
    $editModeResults = Get-NUnitMetadata `
        -Path $EditModeResultsPath `
        -ExpectedIdentities $expectedEditModeIdentities
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $projectRoot (
        "Reports\GpuAdaptiveBinning\$MatrixPreset-$stamp")
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$normalizedProjectRoot = $projectRoot.TrimEnd('\', '/')
if ($FormalAcceptanceMode -and
    ($outputRoot -ieq $normalizedProjectRoot -or
        $outputRoot.StartsWith(
            $normalizedProjectRoot +
                [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase))) {
    throw 'Formal report output must be outside the Git worktree.'
}

if ([string]::IsNullOrWhiteSpace($PlayerPath)) {
    $PlayerPath = Join-Path $projectRoot (
        'Builds\GpuAdaptiveBinningBenchmark\' +
        'GpuAdaptiveBinningBenchmark.exe')
}
elseif (-not [System.IO.Path]::IsPathRooted($PlayerPath)) {
    $PlayerPath = Join-Path $projectRoot $PlayerPath
}
$resolvedPlayerPath = [System.IO.Path]::GetFullPath($PlayerPath)
$playerPayloadRoot =
    [System.IO.Path]::GetDirectoryName($resolvedPlayerPath).
        TrimEnd('\', '/')
$normalizedOutputRoot = $outputRoot.TrimEnd('\', '/')
if ($normalizedOutputRoot -ieq $playerPayloadRoot -or
    $normalizedOutputRoot.StartsWith(
        $playerPayloadRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Report output may not be inside the Player payload.'
}
if ($FormalAcceptanceMode -and
    (Test-Path -LiteralPath $outputRoot)) {
    if (-not (Test-Path -LiteralPath $outputRoot -PathType Container)) {
        throw "Formal report path exists and is not a directory: $outputRoot"
    }
    if ($null -ne (
        Get-ChildItem -LiteralPath $outputRoot -Force |
            Select-Object -First 1)) {
        throw 'Formal report directory must be new or empty.'
    }
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
if ($null -ne $editModeResults) {
    $sourceEditModeResults = $editModeResults
    $copiedEditModePath =
        Join-Path $outputRoot 'editmode-results.xml'
    if ([System.IO.Path]::GetFullPath($sourceEditModeResults.path) -cne
        [System.IO.Path]::GetFullPath($copiedEditModePath)) {
        Copy-Item `
            -LiteralPath $sourceEditModeResults.path `
            -Destination $copiedEditModePath `
            -Force
    }
    $copiedEditModeResults = Get-NUnitMetadata `
        -Path $copiedEditModePath `
        -ExpectedIdentities $expectedEditModeIdentities
    if ([string]$sourceEditModeResults.sha256 -cne
        [string]$copiedEditModeResults.sha256) {
        throw 'Copied EditMode XML differs from the supplied evidence file.'
    }
    $editModeResults = [ordered]@{
        evidenceOrigin = 'caller-supplied-discovery'
        executedByRunner = $false
        gitCommit = ''
        sourceSnapshotSha256 = ''
        result = $copiedEditModeResults.result
        total = $copiedEditModeResults.total
        passed = $copiedEditModeResults.passed
        failed = $copiedEditModeResults.failed
        skipped = $copiedEditModeResults.skipped
        inconclusive = $copiedEditModeResults.inconclusive
        identityMatchMode = $copiedEditModeResults.identityMatchMode
        expectedIdentities = @(
            $copiedEditModeResults.expectedIdentities)
        observedIdentities = @(
            $copiedEditModeResults.observedIdentities)
        missingIdentities = @(
            $copiedEditModeResults.missingIdentities)
        sourcePath = $sourceEditModeResults.path
        sourceSha256 = $sourceEditModeResults.sha256
        sourceLastWriteUtc = $sourceEditModeResults.lastWriteUtc
        copiedPath = $copiedEditModeResults.path
        copiedSha256 = $copiedEditModeResults.sha256
        copiedLastWriteUtc = $copiedEditModeResults.lastWriteUtc
    }
}

$unityEditor = Resolve-UnityEditor -RequestedPath $UnityPath
$unityVersionInfo =
    [System.Diagnostics.FileVersionInfo]::GetVersionInfo($unityEditor)
$resolvedUnityVersion =
    ([string]$unityVersionInfo.ProductVersion -split '_', 2)[0]
if ($FormalAcceptanceMode -and
    $resolvedUnityVersion -cne $formalContract.unityVersion) {
    throw (
        "Formal acceptance requires editor $($formalContract.unityVersion); " +
        "resolved '$resolvedUnityVersion'.")
}

$adaptivePackageRoot =
    Join-Path $projectRoot 'Packages\com.summit.gpu-adaptive-binning'
$directPackageRoot =
    Join-Path $projectRoot 'Packages\com.summit.gpu-direct-binning'
$primitivesPackageRoot =
    Join-Path $projectRoot 'Packages\com.summit.gpu-primitives'
$timestampPackageRoot =
    Join-Path $projectRoot 'Packages\com.summit.gpu-timestamps'
$radixBinningShaderPath = Join-Path $adaptivePackageRoot (
    'Runtime\Resources\GpuAdaptiveBinning\GpuRadixBinning.compute')
$directBinningShaderPath = Join-Path $directPackageRoot (
    'Runtime\Resources\GpuDirectBinning\GpuDirectBinning.compute')
$primitivesPortableShaderPath = Join-Path $primitivesPackageRoot (
    'Runtime\Resources\GpuPrimitives\GpuPrimitivesPortable.compute')
$primitivesWaveShaderPath = Join-Path $primitivesPackageRoot (
    'Runtime\Resources\GpuPrimitives\GpuPrimitivesWave.compute')
$benchmarkRoot =
    Join-Path $projectRoot 'Assets\GpuAdaptiveBinningBenchmark'
$summarizerPath =
    Join-Path $PSScriptRoot 'Summarize-GpuAdaptiveBinningBenchmark.ps1'
$provenanceTestPath = Join-Path $PSScriptRoot (
    'Tests\Test-GpuAdaptiveBinningBenchmarkProvenance.ps1')
$buildScriptPath = Join-Path $benchmarkRoot (
    'Editor\GpuAdaptiveBinningBenchmarkBuild.cs')
$timestampDllPath = Join-Path $timestampPackageRoot (
    'Runtime\Plugins\x86_64\SummitGpuTimestamps.dll')
foreach ($requiredPath in @(
    $radixBinningShaderPath,
    $directBinningShaderPath,
    $primitivesPortableShaderPath,
    $primitivesWaveShaderPath,
    $summarizerPath,
    $provenanceTestPath,
    $buildScriptPath,
    $provenanceModulePath,
    $timestampDllPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required benchmark input is missing: $requiredPath"
    }
}

$sourceFileList = [System.Collections.Generic.List[System.IO.FileInfo]]::new()
foreach ($sourceRoot in @(
    $benchmarkRoot,
    $adaptivePackageRoot,
    $directPackageRoot,
    $primitivesPackageRoot,
    $timestampPackageRoot)) {
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File) {
        if ($file.Extension -in @(
            '.cs', '.compute', '.asmdef', '.json', '.md', '.dll')) {
            $sourceFileList.Add($file)
        }
    }
}
foreach ($file in @(
    Get-Item -LiteralPath $PSCommandPath
    Get-Item -LiteralPath $summarizerPath
    Get-Item -LiteralPath $provenanceTestPath
    Get-Item -LiteralPath $provenanceModulePath
    Get-Item -LiteralPath (Join-Path $projectRoot 'Packages\manifest.json')
    Get-Item -LiteralPath (Join-Path $projectRoot 'Packages\packages-lock.json')
    Get-Item -LiteralPath $projectVersionPath)) {
    $sourceFileList.Add($file)
}
$sourceFiles = [System.IO.FileInfo[]]@(
    $sourceFileList | Sort-Object FullName -Unique)
$sourceSnapshotSha256 =
    Get-CombinedSha256 -Files $sourceFiles -RelativeTo $projectRoot
$directShaderSha256 = Get-CombinedSha256 -RelativeTo $projectRoot -Files @(
    Get-Item -LiteralPath $directBinningShaderPath
    Get-Item -LiteralPath $primitivesPortableShaderPath
    Get-Item -LiteralPath $primitivesWaveShaderPath)
$radixShaderSha256 = Get-CombinedSha256 -RelativeTo $projectRoot -Files @(
    Get-Item -LiteralPath $radixBinningShaderPath
    Get-Item -LiteralPath $primitivesPortableShaderPath
    Get-Item -LiteralPath $primitivesWaveShaderPath)
$runtimeApiFiles = [System.IO.FileInfo[]]@(
    foreach ($packageRoot in @(
        $adaptivePackageRoot,
        $directPackageRoot,
        $primitivesPackageRoot)) {
        Get-ChildItem -LiteralPath (Join-Path $packageRoot 'Runtime') `
            -Recurse -File -Filter '*.cs'
    })
$runtimeApiSha256 =
    Get-CombinedSha256 -Files $runtimeApiFiles -RelativeTo $projectRoot

$postEditModeBeforeRestore = $null
$postEditModeAfterRestore = $null
$sourceHashesStableAcrossEditMode = $null
if ($FormalAcceptanceMode) {
    $generatedEditModePath =
        Join-Path $outputRoot 'editmode-results.xml'
    $editModeLogPath = Join-Path $outputRoot 'unity-editmode.log'
    $editModeStartedUtc = [DateTime]::UtcNow
    $editModeArguments = @(
        '-batchmode',
        '-force-d3d12',
        '-force-device-index', [string]$DeviceIndex,
        '-projectPath', (Quote-ProcessArgument $projectRoot),
        '-runTests',
        '-testPlatform', 'EditMode',
        '-testResults', (Quote-ProcessArgument $generatedEditModePath),
        '-logFile', (Quote-ProcessArgument $editModeLogPath)
    )
    $editModeProcess = Start-Process `
        -FilePath $unityEditor `
        -ArgumentList $editModeArguments `
        -WorkingDirectory $projectRoot `
        -WindowStyle Hidden `
        -PassThru
    $editModeDeadline = [DateTime]::UtcNow.AddMinutes(
        $EditModeTimeoutMinutes)
    while (-not $editModeProcess.HasExited -and
        [DateTime]::UtcNow -lt $editModeDeadline) {
        [void]$editModeProcess.WaitForExit(5000)
    }
    $editModeTimedOut = -not $editModeProcess.HasExited
    if ($editModeTimedOut) {
        $editModeProcess.Kill()
        $editModeProcess.WaitForExit()
    }
    $editModeExitCode = $editModeProcess.ExitCode
    $editModeEndedUtc = [DateTime]::UtcNow

    $postEditModeBeforeRestore =
        Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
    $postEditModeRestore = Restore-KnownUnityBenchmarkDrift `
        -ProjectRoot $projectRoot `
        -Snapshot $postEditModeBeforeRestore `
        -ExpectedHead $gitCommit
    $postEditModeAfterRestore = $postEditModeRestore.afterSnapshot
    $postEditModeSourceSha256 =
        Get-CombinedSha256 -Files $sourceFiles -RelativeTo $projectRoot
    $sourceHashesStableAcrossEditMode =
        $postEditModeSourceSha256 -ceq $sourceSnapshotSha256
    if (-not $sourceHashesStableAcrossEditMode) {
        throw 'Benchmark source changed during the formal EditMode run.'
    }
    if ($editModeTimedOut) {
        throw (
            "Unity EditMode tests exceeded $EditModeTimeoutMinutes minutes. " +
            "See $editModeLogPath")
    }
    if ($editModeExitCode -ne 0) {
        throw (
            "Unity EditMode tests failed with exit code $editModeExitCode. " +
            "See $editModeLogPath")
    }
    $generatedEditModeResults = Get-NUnitMetadata `
        -Path $generatedEditModePath `
        -ExpectedIdentities $expectedEditModeIdentities
    $generatedEditModeLastWriteUtc = [DateTime]::Parse(
        [string]$generatedEditModeResults.lastWriteUtc,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind)
    if ($generatedEditModeLastWriteUtc -lt
            $editModeStartedUtc.AddSeconds(-2) -or
        $generatedEditModeResults.result -cne 'Passed' -or
        $generatedEditModeResults.total -le 0 -or
        $generatedEditModeResults.passed -ne
            $generatedEditModeResults.total -or
        $generatedEditModeResults.failed -ne 0 -or
        $generatedEditModeResults.skipped -ne 0 -or
        $generatedEditModeResults.inconclusive -ne 0 -or
        @($generatedEditModeResults.missingIdentities).Count -ne 0) {
        throw (
            'Runner-generated formal EditMode evidence is not a fresh, ' +
            'non-empty, fully passed run with every required exact identity.')
    }
    $editModeLog = Get-Item -LiteralPath $editModeLogPath
    if ($editModeLog.PSIsContainer -or $editModeLog.Length -le 0) {
        throw 'Runner-generated Unity EditMode log is missing or empty.'
    }
    $editModeResults = [ordered]@{
        evidenceOrigin = 'runner-generated-unity-editmode'
        executedByRunner = $true
        gitCommit = $gitCommit
        sourceSnapshotSha256 = $sourceSnapshotSha256
        projectRoot = $projectRoot
        unityEditorPath = $unityEditor
        unityEditorResolvedVersion = $resolvedUnityVersion
        testPlatform = 'EditMode'
        graphicsApi = 'Direct3D12'
        deviceIndex = $DeviceIndex
        startedUtc = $editModeStartedUtc.ToString('o')
        endedUtc = $editModeEndedUtc.ToString('o')
        timeoutMinutes = $EditModeTimeoutMinutes
        exitCode = $editModeExitCode
        logPath = $editModeLogPath
        logSha256 =
            (Get-FileHash -LiteralPath $editModeLogPath -Algorithm SHA256).Hash
        logLastWriteUtc = $editModeLog.LastWriteTimeUtc.ToString('o')
        result = $generatedEditModeResults.result
        total = $generatedEditModeResults.total
        passed = $generatedEditModeResults.passed
        failed = $generatedEditModeResults.failed
        skipped = $generatedEditModeResults.skipped
        inconclusive = $generatedEditModeResults.inconclusive
        identityMatchMode = $generatedEditModeResults.identityMatchMode
        expectedIdentities = @(
            $generatedEditModeResults.expectedIdentities)
        observedIdentities = @(
            $generatedEditModeResults.observedIdentities)
        missingIdentities = @(
            $generatedEditModeResults.missingIdentities)
        sourcePath = $generatedEditModeResults.path
        sourceSha256 = $generatedEditModeResults.sha256
        sourceLastWriteUtc = $generatedEditModeResults.lastWriteUtc
        copiedPath = $generatedEditModeResults.path
        copiedSha256 = $generatedEditModeResults.sha256
        copiedLastWriteUtc = $generatedEditModeResults.lastWriteUtc
    }
}

$scenarios = switch ($MatrixPreset) {
    'formal-amd-r9700-v1' { $legacyFormalScenarios; break }
    'formal-amd-r9700-contention-v1' { $contentionFormalScenarios; break }
    'discovery-amd-r9700-v1' { $discoveryScenarios; break }
    'calibration-nvidia-rtx4090-v1' {
        $nvidiaCalibrationScenarios
        break
    }
    default {
        @([ordered]@{
            scenarioId = $ScenarioId
            elementCount = $ElementCount
            binCount = $BinCount
            distribution = $Distribution
            seed = $Seed
        })
    }
}
$scenarios = @(
    $scenarios | ForEach-Object {
        $scenario = $_
        ConvertTo-GpuAdaptiveBinningScenario `
            -Scenario $scenario `
            -MatrixPreset $MatrixPreset
    })

$windowsVideoControllers = @()
$windowsVideoControllerInventoryError = ''
try {
    $windowsVideoControllers = @(
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
    $windowsVideoControllerInventoryError = $_.Exception.Message
}

$editModeEvidenceBoundToSource = [bool](
    $FormalAcceptanceMode -and
    $null -ne $editModeResults -and
    [bool]$editModeResults.executedByRunner -and
    [string]$editModeResults.evidenceOrigin -ceq
        'runner-generated-unity-editmode' -and
    [string]$editModeResults.gitCommit -ceq $gitCommit -and
    [string]$editModeResults.sourceSnapshotSha256 -ceq
        $sourceSnapshotSha256 -and
    [bool]$sourceHashesStableAcrossEditMode -and
    $null -ne $postEditModeAfterRestore -and
    -not [bool]$postEditModeAfterRestore.dirty)
if ($FormalAcceptanceMode -and -not $editModeEvidenceBoundToSource) {
    throw 'Formal EditMode evidence is not bound to clean HEAD/source.'
}

$matrixRole = if ($MatrixPreset -in @(
        'formal-amd-r9700-v1',
        'formal-amd-r9700-contention-v1')) {
    'holdout'
}
elseif ($MatrixPreset -ceq 'discovery-amd-r9700-v1') {
    'discovery'
}
elseif ($MatrixPreset -ceq 'calibration-nvidia-rtx4090-v1') {
    'calibration'
}
else {
    'custom'
}
$runnerConfig = [ordered]@{
    schemaVersion = 10
    suite = 'summit.gpu-adaptive-binning'
    benchmarkSchemaVersion = 3
    formalAcceptanceMode = [bool]$FormalAcceptanceMode
    formalContract = $formalContract
    matrixPreset = $MatrixPreset
    matrixRole = $matrixRole
    primitiveBackend = 'wave-ops'
    keyDomain = 'guaranteed-in-range'
    orderingContract = 'unspecified-within-bin'
    signedImprovementConvention = 'positive-radix-faster'
    decisiveGate = $formalContract.decisiveGate
    scenarios = $scenarios
    editModeResults = $editModeResults
    editModeEvidenceBoundToSource = $editModeEvidenceBoundToSource
    sourceHashesStableAcrossEditMode = $sourceHashesStableAcrossEditMode
    projectRoot = $projectRoot
    projectUnityVersion = $projectUnityVersion
    unityPath = $unityEditor
    unityEditorResolvedVersion = $resolvedUnityVersion
    playerPath = $resolvedPlayerPath
    outputDirectory = $outputRoot
    deviceIndex = $DeviceIndex
    superRounds = $SuperRounds
    warmupFrames = $WarmupFrames
    sampleFrames = $SampleFrames
    cooldownFrames = $CooldownFrames
    dispatchesPerFrame = $DispatchesPerFrame
    validationTimeoutSeconds = $ValidationTimeoutSeconds
    editModeTimeoutMinutes = $EditModeTimeoutMinutes
    requireCompleteGpuTimings = (-not $AllowMissingGpuTiming)
    gitCommit = $gitCommit
    gitBranch = $gitBranch
    gitStart = $gitStart
    gitPostEditModeBeforeRestore = $postEditModeBeforeRestore
    gitPostEditModeAfterRestore = $postEditModeAfterRestore
    gitPostBuildBeforeRestore = $null
    gitPostBuildAfterRestore = $null
    gitFinalBeforeRestore = $null
    gitFinal = $null
    gitTreeDirty = [bool]$gitStart.dirty
    sourceSnapshotSha256 = $sourceSnapshotSha256
    sourceFileCount = $sourceFiles.Count
    sourceHashesStableAcrossBuild = $false
    directShaderSha256 = $directShaderSha256
    radixShaderSha256 = $radixShaderSha256
    runtimeApiSha256 = $runtimeApiSha256
    timestampNativeDllSha256 =
        (Get-FileHash -LiteralPath $timestampDllPath -Algorithm SHA256).Hash
    windowsVideoControllers = $windowsVideoControllers
    windowsVideoControllerInventoryError =
        $windowsVideoControllerInventoryError
    playerPayload = $null
    playerPayloadStableThroughRun = $false
    playerRuns = @()
    runnerConfigFinalized = $false
    formalContractSatisfied = $false
    startedUtc = (Get-Date).ToUniversalTime().ToString('o')
    finalizedUtc = ''
}
$runnerConfigPath = Join-Path $outputRoot 'runner-config.json'
$runnerConfig |
    ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $runnerConfigPath -Encoding utf8

$buildLog = Join-Path $outputRoot 'unity-build.log'
if (-not $SkipBuild) {
    $buildArguments = @(
        '-batchmode',
        '-nographics',
        '-quit',
        '-projectPath', (Quote-ProcessArgument $projectRoot),
        '-executeMethod', 'GpuAdaptiveBinningBenchmarkBuild.PerformBuild',
        '-gpu-adaptive-binning-player-path',
            (Quote-ProcessArgument $resolvedPlayerPath),
        '-logFile', (Quote-ProcessArgument $buildLog)
    )
    $buildProcess = Start-Process `
        -FilePath $unityEditor `
        -ArgumentList $buildArguments `
        -WorkingDirectory $projectRoot `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($buildProcess.ExitCode -ne 0) {
        throw (
            "Unity benchmark build failed with exit code " +
            "$($buildProcess.ExitCode). See $buildLog")
    }
}

$postBuildBeforeRestore =
    Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
$runnerConfig['gitPostBuildBeforeRestore'] = $postBuildBeforeRestore
$postBuildRestore = if ($FormalAcceptanceMode) {
    Restore-KnownUnityBenchmarkDrift `
        -ProjectRoot $projectRoot `
        -Snapshot $postBuildBeforeRestore `
        -ExpectedHead $gitCommit
}
else {
    [pscustomobject]@{
        restoredPaths = [string[]]@()
        driftKinds = [string[]]@()
        afterSnapshot = $postBuildBeforeRestore
    }
}
$runnerConfig['gitPostBuildAfterRestore'] =
    $postBuildRestore.afterSnapshot
if (-not (Test-Path -LiteralPath $resolvedPlayerPath -PathType Leaf)) {
    throw "Benchmark Player is missing: $resolvedPlayerPath"
}
$postBuildSourceSha256 =
    Get-CombinedSha256 -Files $sourceFiles -RelativeTo $projectRoot
$sourceHashesStableAcrossBuild =
    $postBuildSourceSha256 -ceq $sourceSnapshotSha256
$runnerConfig['sourceHashesStableAcrossBuild'] =
    $sourceHashesStableAcrossBuild
if ($FormalAcceptanceMode -and -not $sourceHashesStableAcrossBuild) {
    throw 'Benchmark source changed during the Player build.'
}

$postBuildPayload =
    Get-GpuBenchmarkPlayerPayload -PlayerPath $resolvedPlayerPath
$runnerConfig['playerPayload'] = $postBuildPayload
$payloadManifestPath =
    Join-Path $outputRoot 'player-payload-manifest.json'
$postBuildPayload |
    ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $payloadManifestPath -Encoding utf8

$matrixRows = [System.Collections.Generic.List[object]]::new()
$playerRuns = [System.Collections.Generic.List[object]]::new()
foreach ($scenario in $scenarios) {
    $scenarioRoot = Join-Path $outputRoot $scenario.scenarioId
    New-Item -ItemType Directory -Path $scenarioRoot -Force | Out-Null
    $playerLog = Join-Path $scenarioRoot 'player.log'
    $arguments = @(
        '-force-d3d12',
        '-force-device-index', [string]$DeviceIndex,
        '-screen-fullscreen', '0',
        '-screen-width', '640',
        '-screen-height', '360',
        '-gpu-adaptive-binning-benchmark',
        '-gpu-adaptive-binning-report-dir',
            (Quote-ProcessArgument $scenarioRoot),
        '-gpu-adaptive-binning-scenario-id',
            (Quote-ProcessArgument $scenario.scenarioId),
        '-gpu-adaptive-binning-super-rounds', [string]$SuperRounds,
        '-gpu-adaptive-binning-warmup-frames', [string]$WarmupFrames,
        '-gpu-adaptive-binning-sample-frames', [string]$SampleFrames,
        '-gpu-adaptive-binning-cooldown-frames', [string]$CooldownFrames,
        '-gpu-adaptive-binning-element-count',
            [string]$scenario.elementCount,
        '-gpu-adaptive-binning-bin-count', [string]$scenario.binCount,
        '-gpu-adaptive-binning-distribution',
            (Quote-ProcessArgument $scenario.distribution),
        '-gpu-adaptive-binning-seed', [string]$scenario.seed,
        '-gpu-adaptive-binning-dispatches-per-frame',
            [string]$DispatchesPerFrame,
        '-gpu-adaptive-binning-validation-timeout-seconds',
            [string]$ValidationTimeoutSeconds,
        '-gpu-adaptive-binning-require-complete-gpu-timings',
            $(if ($AllowMissingGpuTiming) { '0' } else { '1' }),
        '-gpu-adaptive-binning-build-commit', $gitCommit,
        '-gpu-adaptive-binning-direct-shader-sha256',
            $directShaderSha256,
        '-gpu-adaptive-binning-radix-shader-sha256',
            $radixShaderSha256,
        '-gpu-adaptive-binning-runtime-api-sha256',
            $runtimeApiSha256,
        '-logFile', (Quote-ProcessArgument $playerLog)
    )
    Write-Host (
        "Running adaptive-binning scenario '$($scenario.scenarioId)' " +
        "N=$($scenario.elementCount) C=$($scenario.binCount) " +
        "distribution=$($scenario.distribution)")
    $player = Start-Process `
        -FilePath $resolvedPlayerPath `
        -ArgumentList $arguments `
        -WorkingDirectory $projectRoot `
        -WindowStyle Hidden `
        -PassThru
    $completed =
        $player.WaitForExit($PlayerTimeoutMinutes * 60 * 1000)
    if (-not $completed) {
        $player.Kill()
        $player.WaitForExit()
        throw (
            "Scenario '$($scenario.scenarioId)' timed out. See $playerLog")
    }
    if ($player.ExitCode -ne 0) {
        throw (
            "Scenario '$($scenario.scenarioId)' failed with exit code " +
            "$($player.ExitCode). See $playerLog")
    }

    $runSummaryPath = Join-Path $scenarioRoot 'run-summary.txt'
    if (-not (Test-Path -LiteralPath $runSummaryPath -PathType Leaf)) {
        throw "Scenario did not produce run-summary.txt: $scenarioRoot"
    }
    $runSummary = @{}
    foreach ($line in Get-Content -LiteralPath $runSummaryPath) {
        $parts = $line -split '=', 2
        if ($parts.Count -eq 2) {
            $runSummary[$parts[0]] = $parts[1]
        }
    }
    if ([int]$runSummary.passed -ne 1) {
        throw "Scenario quality gate failed: $scenarioRoot"
    }
    $acceptedStatuses = if ($AllowMissingGpuTiming) {
        @('completed', 'completed-correctness-only')
    }
    else { @('completed') }
    if ($runSummary.status -notin $acceptedStatuses) {
        throw (
            "Unexpected scenario status '$($runSummary.status)': " +
            $scenarioRoot)
    }
    $rawPids = @(
        Import-Csv -LiteralPath (
            Join-Path $scenarioRoot 'raw-frames.csv') |
            Select-Object -ExpandProperty processId -Unique)
    if ($rawPids.Count -ne 1 -or
        [string]$rawPids[0] -cne [string]$player.Id) {
        throw "Scenario PID evidence is inconsistent: $scenarioRoot"
    }

    $playerRuns.Add([ordered]@{
        scenarioId = $scenario.scenarioId
        processId = $player.Id
        exitCode = $player.ExitCode
        reportDirectory = $scenarioRoot
        playerLog = $playerLog
    })
    $matrixRows.Add([pscustomobject]@{
        scenarioId = $scenario.scenarioId
        elementCount = $scenario.elementCount
        binCount = $scenario.binCount
        distribution = $scenario.distribution
        seed = $scenario.seed
        exactSingleBinKey = $scenario.exactSingleBinKey
        dominantSetCardinality = $scenario.dominantSetCardinality
        bracketGroup = $scenario.bracketGroup
        processId = $player.Id
        status = $runSummary.status
        passed = $runSummary.passed
        rawSampleCount = $runSummary.rawSampleCount
    })
}
$matrixRows |
    Export-Csv -LiteralPath (
        Join-Path $outputRoot 'matrix.csv') -NoTypeInformation -Encoding utf8

$finalBeforeRestore =
    Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
$runnerConfig['gitFinalBeforeRestore'] = $finalBeforeRestore
$finalRestore = if ($FormalAcceptanceMode) {
    Restore-KnownUnityBenchmarkDrift `
        -ProjectRoot $projectRoot `
        -Snapshot $finalBeforeRestore `
        -ExpectedHead $gitCommit
}
else {
    [pscustomobject]@{
        restoredPaths = [string[]]@()
        driftKinds = [string[]]@()
        afterSnapshot = $finalBeforeRestore
    }
}
$finalGit = $finalRestore.afterSnapshot
$finalPayload =
    Get-GpuBenchmarkPlayerPayload -PlayerPath $resolvedPlayerPath
$payloadStable =
    [string]$finalPayload.sha256 -ceq [string]$postBuildPayload.sha256 -and
    [int]$finalPayload.fileCount -eq [int]$postBuildPayload.fileCount -and
    [int64]$finalPayload.lengthBytes -eq
        [int64]$postBuildPayload.lengthBytes
$runnerConfig['gitFinal'] = $finalGit
$runnerConfig['gitTreeDirty'] = [bool]$finalGit.dirty
$runnerConfig['playerPayload'] = $finalPayload
$runnerConfig['playerPayloadStableThroughRun'] = $payloadStable
$runnerConfig['playerRuns'] = @($playerRuns)
$runnerConfig['formalContractSatisfied'] =
    [bool](
        $FormalAcceptanceMode -and
        $editModeEvidenceBoundToSource -and
        $sourceHashesStableAcrossEditMode -and
        $sourceHashesStableAcrossBuild -and
        $payloadStable -and
        -not [bool]$finalGit.dirty)
$runnerConfig['runnerConfigFinalized'] = $true
$runnerConfig['finalizedUtc'] =
    (Get-Date).ToUniversalTime().ToString('o')
$finalPayload |
    ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $payloadManifestPath -Encoding utf8
$runnerConfig |
    ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $runnerConfigPath -Encoding utf8

if ($FormalAcceptanceMode -and -not $payloadStable) {
    throw 'Player payload changed during the formal matrix.'
}
if ($FormalAcceptanceMode -and [bool]$finalGit.dirty) {
    throw 'Formal benchmark worktree is dirty after execution.'
}

if (-not $SkipSummary) {
    & $summarizerPath -ReportDirectory $outputRoot
    if (-not $?) {
        throw 'Adaptive-binning benchmark summarization failed.'
    }
}

Write-Host "Completed GPU adaptive-binning benchmark: $outputRoot"
