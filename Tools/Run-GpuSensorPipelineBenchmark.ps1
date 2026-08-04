[CmdletBinding()]
param(
    [string]$UnityPath,
    [string]$OutputDirectory,
    [string]$PlayerPath,
    [ValidateRange(0, 16)]
    [int]$DeviceIndex = 0,
    [string]$MatrixPreset = 'single',
    [string]$ScenarioId = 'custom-dynamic',
    [ValidateRange(1024, 16776960)]
    [int]$ElementCount = 262144,
    [ValidateRange(1, 4096)]
    [int]$QueryCount = 64,
    [int]$Seed = 20260731,
    [ValidateRange(1, 4)]
    [int]$SuperRounds = 4,
    [ValidateRange(5, 1800)]
    [int]$WarmupFrames = 60,
    [ValidateRange(60, 7200)]
    [int]$SampleFrames = 240,
    [ValidateRange(0, 600)]
    [int]$CooldownFrames = 15,
    [ValidateRange(2, 16)]
    [int]$StagingSlotCount = 4,
    [ValidateRange(5, 600)]
    [int]$ValidationTimeoutSeconds = 60,
    [ValidateRange(5, 240)]
    [int]$PlayerTimeoutMinutes = 90,
    [ValidateRange(5, 120)]
    [int]$EditModeTimeoutMinutes = 30,
    [switch]$FormalAcceptanceMode,
    [string]$EditModeResultsPath,
    [switch]$AllowMissingGpuTiming,
    [switch]$SkipBuild,
    [switch]$SkipSummary
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$formalContract = [ordered]@{
    unityVersion = '6000.5.2f1'
    deviceIndex = 0
    matrixPreset = 'amd-r9700-dynamic-v1'
    binCount = 262144
    stateCount = 64
    stagingSlotCount = 4
    superRounds = 4
    warmupFrames = 60
    sampleFrames = 900
    cooldownFrames = 15
    editModeTimeoutMinutes = 30
    validationTimeoutSeconds = 60
    playerTimeoutMinutes = 90
}
$discoveryContract = [ordered]@{
    unityVersion = '6000.5.2f1'
    deviceIndex = 0
    matrixPreset = 'amd-r9700-dynamic-discovery-v1'
    superRounds = 4
    warmupFrames = 60
    sampleFrames = 240
    cooldownFrames = 15
    stagingSlotCount = 4
    validationTimeoutSeconds = 60
}
$formalScenarios = @(
    [ordered]@{
        scenarioId = 'dynamic-n262144-q64'
        elementCount = 262144
        binCount = 262144
        queryCount = 64
        logicalStateCount = 64
        seed = 20260731
    },
    [ordered]@{
        scenarioId = 'dynamic-n1048576-q256'
        elementCount = 1048576
        binCount = 262144
        queryCount = 256
        logicalStateCount = 64
        seed = 20260732
    }
)

if ($FormalAcceptanceMode) {
    $violations = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in @(
        @('DeviceIndex', $DeviceIndex, $formalContract.deviceIndex),
        @('SuperRounds', $SuperRounds, $formalContract.superRounds),
        @('WarmupFrames', $WarmupFrames, $formalContract.warmupFrames),
        @('SampleFrames', $SampleFrames, $formalContract.sampleFrames),
        @('CooldownFrames', $CooldownFrames, $formalContract.cooldownFrames),
        @('StagingSlotCount', $StagingSlotCount, $formalContract.stagingSlotCount),
        @('EditModeTimeoutMinutes', $EditModeTimeoutMinutes, $formalContract.editModeTimeoutMinutes),
        @('ValidationTimeoutSeconds', $ValidationTimeoutSeconds, $formalContract.validationTimeoutSeconds),
        @('PlayerTimeoutMinutes', $PlayerTimeoutMinutes, $formalContract.playerTimeoutMinutes))) {
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
            'EditModeResultsPath is discovery-only; formal evidence is generated in-run.')
    }
    if ($violations.Count -ne 0) {
        throw "Formal acceptance contract rejected:`n$($violations -join "`n")"
    }
}
elseif ($MatrixPreset -ceq $discoveryContract.matrixPreset) {
    $violations = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in @(
        @('DeviceIndex', $DeviceIndex, $discoveryContract.deviceIndex),
        @('SuperRounds', $SuperRounds, $discoveryContract.superRounds),
        @('WarmupFrames', $WarmupFrames, $discoveryContract.warmupFrames),
        @('SampleFrames', $SampleFrames, $discoveryContract.sampleFrames),
        @('CooldownFrames', $CooldownFrames, $discoveryContract.cooldownFrames),
        @('StagingSlotCount', $StagingSlotCount, $discoveryContract.stagingSlotCount),
        @('ValidationTimeoutSeconds', $ValidationTimeoutSeconds, $discoveryContract.validationTimeoutSeconds))) {
        if ([int64]$entry[1] -ne [int64]$entry[2]) {
            $violations.Add(
                "$($entry[0])=$($entry[1]); expected $($entry[2])")
        }
    }
    if ($violations.Count -ne 0) {
        throw "Discovery contract rejected:`n$($violations -join "`n")"
    }
}
elseif ($MatrixPreset -cne 'single') {
    throw "Unsupported MatrixPreset '$MatrixPreset'."
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
elseif ($MatrixPreset -ceq $discoveryContract.matrixPreset -and
    $projectUnityVersion -cne $discoveryContract.unityVersion) {
    throw (
        "Frozen discovery requires Unity $($discoveryContract.unityVersion); " +
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
    'Summit.GpuSensorPipeline.Tests.Editor.dll',
    'Summit.GpuSensorPipeline.Tests.GpuSensorDeterministicGeneratorTests',
    'Summit.GpuSensorPipeline.Tests.GpuSensorPipelineContractTests',
    'Summit.GpuSensorPipeline.Tests.GpuSensorPipelineIntegrationTests',
    'Summit.GpuSensorPipeline.Benchmark.Tests.Editor.dll',
    'Summit.GpuSensorPipeline.Benchmark.Tests.GpuSensorPipelineBenchmarkScheduleTests'
)
$editModeResults = $null
if (-not [string]::IsNullOrWhiteSpace($EditModeResultsPath)) {
    $editModeResults = Get-NUnitMetadata `
        -Path $EditModeResultsPath `
        -ExpectedIdentities $expectedEditModeIdentities
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    throw (
        'OutputDirectory is required and must be outside the Git worktree.')
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
$normalizedProjectRoot = $projectRoot.TrimEnd('\', '/')
if ($outputRoot -ieq $normalizedProjectRoot -or
    $outputRoot.StartsWith(
        $normalizedProjectRoot +
            [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Benchmark report output must be outside the Git worktree.'
}

if ([string]::IsNullOrWhiteSpace($PlayerPath)) {
    $PlayerPath = Join-Path $projectRoot (
        'Builds\GpuSensorPipelineBenchmark\' +
        'GpuSensorPipelineBenchmark.exe')
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
if (Test-Path -LiteralPath $outputRoot) {
    if (-not (Test-Path -LiteralPath $outputRoot -PathType Container)) {
        throw "Report path exists and is not a directory: $outputRoot"
    }
    if ($null -ne (
        Get-ChildItem -LiteralPath $outputRoot -Force |
            Select-Object -First 1)) {
        throw 'Report directory must be new or empty.'
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
elseif ($MatrixPreset -ceq $discoveryContract.matrixPreset -and
    $resolvedUnityVersion -cne $discoveryContract.unityVersion) {
    throw (
        "Frozen discovery requires editor $($discoveryContract.unityVersion); " +
        "resolved '$resolvedUnityVersion'.")
}

$runtimePackageRoot =
    Join-Path $projectRoot 'Packages\com.summit.gpu-sensor-pipeline'
$runtimeShaderPath = Join-Path $runtimePackageRoot (
    'Runtime\Resources\GpuSensorPipeline\GpuSensorPipeline.compute')
$runtimeApiPath =
    Join-Path $runtimePackageRoot 'Runtime\GpuSensorPipeline.cs'
$generatorPath =
    Join-Path $runtimePackageRoot 'Runtime\GpuSensorDeterministicGenerator.cs'
$directPackageRoot =
    Join-Path $projectRoot 'Packages\com.summit.gpu-direct-binning'
$primitivesPackageRoot =
    Join-Path $projectRoot 'Packages\com.summit.gpu-primitives'
$benchmarkRoot =
    Join-Path $projectRoot 'Assets\GpuSensorPipelineBenchmark'
$summarizerPath =
    Join-Path $PSScriptRoot 'Summarize-GpuSensorPipelineBenchmark.ps1'
$provenanceTestPath = Join-Path $PSScriptRoot (
    'Tests\Test-GpuSensorPipelineBenchmarkProvenance.ps1')
$planPath = Join-Path $projectRoot (
    'Docs\GPU_DYNAMIC_SENSOR_PIPELINE_BENCHMARK_PLAN.md')
$buildScriptPath = Join-Path $benchmarkRoot (
    'Editor\GpuSensorPipelineBenchmarkBuild.cs')
$timestampDllPath = Join-Path $projectRoot (
    'Packages\com.summit.gpu-timestamps\Runtime\Plugins\x86_64\' +
    'SummitGpuTimestamps.dll')
foreach ($requiredPath in @(
    $runtimeShaderPath,
    $runtimeApiPath,
    $generatorPath,
    $summarizerPath,
    $provenanceTestPath,
    $planPath,
    $buildScriptPath,
    $provenanceModulePath,
    $timestampDllPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required benchmark input is missing: $requiredPath"
    }
}

$sourceFiles = [System.IO.FileInfo[]]@(
    Get-ChildItem -LiteralPath $benchmarkRoot -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.compute', '.asmdef', '.json', '.md') }
    Get-ChildItem -LiteralPath $runtimePackageRoot -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.compute', '.asmdef', '.json', '.md') }
    Get-ChildItem -LiteralPath $directPackageRoot -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.compute', '.asmdef', '.json', '.md') }
    Get-ChildItem -LiteralPath $primitivesPackageRoot -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.compute', '.asmdef', '.json', '.md') }
    Get-ChildItem -LiteralPath (
        Join-Path $projectRoot 'Packages\com.summit.gpu-timestamps\Runtime') `
        -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.asmdef') }
    Get-Item -LiteralPath $PSCommandPath
    Get-Item -LiteralPath $summarizerPath
    Get-Item -LiteralPath $provenanceModulePath
    Get-Item -LiteralPath $provenanceTestPath
    Get-Item -LiteralPath $planPath
    Get-Item -LiteralPath $timestampDllPath
    Get-Item -LiteralPath (Join-Path $projectRoot 'Packages\manifest.json')
    Get-Item -LiteralPath (Join-Path $projectRoot 'Packages\packages-lock.json')
    Get-Item -LiteralPath $projectVersionPath
)
$sourceFiles = [System.IO.FileInfo[]]@(
    $sourceFiles | Sort-Object FullName -Unique)
$sourceSnapshotSha256 =
    Get-CombinedSha256 -Files $sourceFiles -RelativeTo $projectRoot
$runtimeShaderFiles = [System.IO.FileInfo[]]@(
    foreach ($packageRoot in @(
        $runtimePackageRoot,
        $directPackageRoot,
        $primitivesPackageRoot)) {
        Get-ChildItem -LiteralPath (Join-Path $packageRoot 'Runtime') `
            -Recurse -File -Filter '*.compute'
    })
$runtimeShaderSha256 = Get-CombinedSha256 `
    -Files $runtimeShaderFiles `
    -RelativeTo $projectRoot
$runtimeApiFiles = [System.IO.FileInfo[]]@(
    foreach ($packageRoot in @(
        $runtimePackageRoot,
        $directPackageRoot,
        $primitivesPackageRoot)) {
        Get-ChildItem -LiteralPath (Join-Path $packageRoot 'Runtime') `
            -Recurse -File -Filter '*.cs'
    })
$runtimeApiSha256 =
    Get-CombinedSha256 -Files $runtimeApiFiles -RelativeTo $projectRoot
$generatorSha256 =
    (Get-FileHash -LiteralPath $generatorPath -Algorithm SHA256).Hash
$runnerToolSha256 =
    (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
$summarizerToolSha256 =
    (Get-FileHash -LiteralPath $summarizerPath -Algorithm SHA256).Hash
$provenanceTestSha256 =
    (Get-FileHash -LiteralPath $provenanceTestPath -Algorithm SHA256).Hash
$benchmarkPlanSha256 =
    (Get-FileHash -LiteralPath $planPath -Algorithm SHA256).Hash
$timestampNativeDllSha256 =
    (Get-FileHash -LiteralPath $timestampDllPath -Algorithm SHA256).Hash


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

$scenarios = switch ($MatrixPreset) {
    'amd-r9700-dynamic-v1' { $formalScenarios; break }
    'amd-r9700-dynamic-discovery-v1' { $formalScenarios; break }
    default {
        @([ordered]@{
            scenarioId = $ScenarioId
            elementCount = $ElementCount
            binCount = $formalContract.binCount
            queryCount = $QueryCount
            logicalStateCount = $formalContract.stateCount
            seed = $Seed
        })
    }
}

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

$runnerConfig = [ordered]@{
    schemaVersion = 10
    suite = 'summit.gpu-sensor-pipeline'
    benchmarkSchemaVersion = 10
    formalAcceptanceMode = [bool]$FormalAcceptanceMode
    formalContract = $formalContract
    discoveryContract = $discoveryContract
    matrixPreset = $MatrixPreset
    matrixRole = if ($FormalAcceptanceMode) {
        'formal-holdout'
    }
    elseif ($MatrixPreset -ceq $discoveryContract.matrixPreset) {
        'discovery'
    }
    else {
        'custom'
    }
    signedImprovementConvention = 'positive-gpu-resident-faster'
    scenarios = $scenarios
    editModeResults = $editModeResults
    editModeEvidenceBoundToSource = $editModeEvidenceBoundToSource
    sourceHashesStableAcrossEditMode = $sourceHashesStableAcrossEditMode
    gitPostEditModeBeforeRestore = $postEditModeBeforeRestore
    gitPostEditModeAfterRestore = $postEditModeAfterRestore
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
    binCount = $formalContract.binCount
    logicalStateCount = $formalContract.stateCount
    stagingSlotCount = $StagingSlotCount
    editModeTimeoutMinutes = $EditModeTimeoutMinutes
    validationTimeoutSeconds = $ValidationTimeoutSeconds
    playerTimeoutMinutes = $PlayerTimeoutMinutes
    requireCompleteGpuTimings = (-not $AllowMissingGpuTiming)
    gitCommit = $gitCommit
    gitBranch = $gitBranch
    gitStart = $gitStart
    gitPostBuildBeforeRestore = $null
    gitPostBuildAfterRestore = $null
    gitFinalBeforeRestore = $null
    gitFinal = $null
    gitTreeDirty = [bool]$gitStart.dirty
    sourceSnapshotSha256 = $sourceSnapshotSha256
    sourceFileCount = $sourceFiles.Count
    sourceHashesStableAcrossBuild = $false
    runtimeShaderSha256 = $runtimeShaderSha256
    runtimeApiSha256 = $runtimeApiSha256
    generatorSha256 = $generatorSha256
    runnerToolSha256 = $runnerToolSha256
    summarizerToolSha256 = $summarizerToolSha256
    provenanceTestSha256 = $provenanceTestSha256
    benchmarkPlanSha256 = $benchmarkPlanSha256
    timestampNativeDllSha256 = $timestampNativeDllSha256
    uploadQueueCoverageVerified = $false
    asyncComputeClaim = $false
    copyQueueClaim = $false
    measurementWorkloadReadbackBytes = 0
    timestampInstrumentationReadbackDisclosedSeparately = $true
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
        '-executeMethod', 'GpuSensorPipelineBenchmarkBuild.PerformBuild',
        '-gpu-sensor-pipeline-player-path',
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
$postBuildRestore = Restore-KnownUnityBenchmarkDrift `
    -ProjectRoot $projectRoot `
    -Snapshot $postBuildBeforeRestore `
    -ExpectedHead $gitCommit
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
if (-not $sourceHashesStableAcrossBuild) {
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
        '-gpu-sensor-pipeline-benchmark',
        '-gpu-sensor-pipeline-report-dir',
            (Quote-ProcessArgument $scenarioRoot),
        '-gpu-sensor-pipeline-scenario-id',
            (Quote-ProcessArgument $scenario.scenarioId),
        '-gpu-sensor-pipeline-super-rounds', [string]$SuperRounds,
        '-gpu-sensor-pipeline-warmup-frames', [string]$WarmupFrames,
        '-gpu-sensor-pipeline-sample-frames', [string]$SampleFrames,
        '-gpu-sensor-pipeline-cooldown-frames', [string]$CooldownFrames,
        '-gpu-sensor-pipeline-element-count',
            [string]$scenario.elementCount,
        '-gpu-sensor-pipeline-bin-count', [string]$scenario.binCount,
        '-gpu-sensor-pipeline-query-count', [string]$scenario.queryCount,
        '-gpu-sensor-pipeline-seed', [string]$scenario.seed,
        '-gpu-sensor-pipeline-staging-slot-count', [string]$StagingSlotCount,
        '-gpu-sensor-pipeline-validation-timeout-seconds',
            [string]$ValidationTimeoutSeconds,
        '-gpu-sensor-pipeline-require-complete-gpu-timings',
            $(if ($AllowMissingGpuTiming) { '0' } else { '1' }),
        '-gpu-sensor-pipeline-build-commit', $gitCommit,
        '-gpu-sensor-pipeline-runtime-shader-sha256',
            $runtimeShaderSha256,
        '-gpu-sensor-pipeline-runtime-api-sha256',
            $runtimeApiSha256,
        '-gpu-sensor-pipeline-native-timestamp-dll-sha256',
            $timestampNativeDllSha256,
        '-logFile', (Quote-ProcessArgument $playerLog)
    )
    Write-Host (
        "Running dynamic sensor-pipeline scenario '$($scenario.scenarioId)' " +
        "N=$($scenario.elementCount) C=$($scenario.binCount) " +
        "Q=$($scenario.queryCount) F=$($scenario.logicalStateCount)")
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
    $expectedRawCount =
        ($SuperRounds * 4 + 2) * $SampleFrames
    $expectedValidationRows = $SuperRounds * 2 + 2
    $expectedCaseFrames = $SuperRounds * 2 * $SampleFrames
    $expectedLogicalBytes =
        [int64]$expectedCaseFrames *
        [int64]$scenario.elementCount * 20L
    $expectedSummary = [ordered]@{
        schemaVersion = '10'
        suite = 'summit.gpu-sensor-pipeline'
        rawSampleCount = [string]$expectedRawCount
        validationRows = [string]$expectedValidationRows
        expectedValidationRows = [string]$expectedValidationRows
        validationFailures = '0'
        measurementReadbackBytes = '0'
        timestampInstrumentationReadbackBytes =
            [string]([int64]$expectedRawCount * 16L)
        logicalUploadBytes = [string]$expectedLogicalBytes
        gpuProducerLogicalWriteBytes = [string]$expectedLogicalBytes
        nativeTimestampAcquireFailures = '0'
        nativeTimestampResultFailures = '0'
        nativeTimestampTimeouts = '0'
        nativeTimestampPendingRows = '0'
        gpuRegionTimingComplete = '1'
        logicalStateCount = '64'
        finalStateKeyValidation = '1'
        allStateDigestComparison = '1'
        uploadQueueCoverageVerified = '0'
        asyncComputeClaim = '0'
        copyQueueClaim = '0'
        mainGraphicsQueueTimestamp = '1'
    }
    foreach ($field in $expectedSummary.Keys) {
        if ([string]$runSummary[$field] -cne
            [string]$expectedSummary[$field]) {
            throw (
                "Scenario '$($scenario.scenarioId)' summary field " +
                "'$field' is '$($runSummary[$field])'; expected " +
                "'$($expectedSummary[$field])'.")
        }
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
        queryCount = $scenario.queryCount
        logicalStateCount = $scenario.logicalStateCount
        seed = $scenario.seed
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
$finalRestore = Restore-KnownUnityBenchmarkDrift `
    -ProjectRoot $projectRoot `
    -Snapshot $finalBeforeRestore `
    -ExpectedHead $gitCommit
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

if (-not $payloadStable) {
    throw 'Player payload changed during the benchmark matrix.'
}
if ([bool]$finalGit.dirty) {
    throw 'Benchmark worktree is dirty after execution.'
}

if (-not $SkipSummary) {
    & $summarizerPath -ReportDirectory $outputRoot
    if (-not $?) {
        throw 'Dynamic sensor-pipeline benchmark summarization failed.'
    }
}

Write-Host "Completed dynamic GPU sensor-pipeline benchmark: $outputRoot"
