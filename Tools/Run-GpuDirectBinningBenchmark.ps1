[CmdletBinding()]
param(
    [string]$UnityPath,
    [string]$OutputDirectory,
    [string]$PlayerPath,
    [ValidateRange(0, 16)]
    [int]$DeviceIndex = 0,
    [string]$MatrixPreset = 'single',
    [string]$ScenarioId = 'custom',
    [ValidateRange(1024, 16776960)]
    [int]$ElementCount = 1048576,
    [ValidateRange(1, 16776960)]
    [int]$BinCount = 4096,
    [ValidateSet('uniform', 'hotset16')]
    [string]$Distribution = 'uniform',
    [int]$Seed = 20260730,
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
    [switch]$AllowMissingGpuTiming,
    [switch]$SkipBuild,
    [switch]$SkipSummary
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$formalContract = [ordered]@{
    unityVersion = '6000.5.2f1'
    deviceIndex = 0
    matrixPreset = 'amd-r9700-v1'
    superRounds = 2
    warmupFrames = 60
    sampleFrames = 900
    cooldownFrames = 15
    dispatchesPerFrame = 1
}
$formalScenarios = @(
    [ordered]@{
        scenarioId = 'uniform-c4096'
        elementCount = 1048576
        binCount = 4096
        distribution = 'uniform'
        seed = 20260730
    },
    [ordered]@{
        scenarioId = 'hotset16-c4096'
        elementCount = 1048576
        binCount = 4096
        distribution = 'hotset16'
        seed = 20260731
    },
    [ordered]@{
        scenarioId = 'uniform-c65536'
        elementCount = 1048576
        binCount = 65536
        distribution = 'uniform'
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
        @('DispatchesPerFrame', $DispatchesPerFrame,
            $formalContract.dispatchesPerFrame))) {
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
    if ([string]::IsNullOrWhiteSpace($EditModeResultsPath)) {
        $violations.Add('EditModeResultsPath is required.')
    }
    if ($violations.Count -ne 0) {
        throw "Formal acceptance contract rejected:`n$($violations -join "`n")"
    }
}
elseif ($MatrixPreset -cne 'single') {
    throw "Non-formal runs support MatrixPreset='single' only."
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
    'Summit.GpuDirectBinning.Benchmark.Tests.Editor.dll',
    'Summit.GpuDirectBinning.Benchmark.Tests.GpuDirectBinningBenchmarkScheduleTests',
    'Summit.GpuDirectBinning.Benchmark.Tests.GpuDirectBinningInputDistributionTests',
    'Summit.GpuDirectBinning.Benchmark.Tests.GpuDirectBinningCpuOracleTests'
)
$editModeResults = $null
if (-not [string]::IsNullOrWhiteSpace($EditModeResultsPath)) {
    $editModeResults = Get-NUnitMetadata `
        -Path $EditModeResultsPath `
        -ExpectedIdentities $expectedEditModeIdentities
}
if ($FormalAcceptanceMode -and
    ($null -eq $editModeResults -or
        $editModeResults.result -cne 'Passed' -or
        $editModeResults.total -le 0 -or
        $editModeResults.passed -ne $editModeResults.total -or
        $editModeResults.failed -ne 0 -or
        $editModeResults.skipped -ne 0 -or
        $editModeResults.inconclusive -ne 0 -or
        @($editModeResults.missingIdentities).Count -ne 0)) {
    $missing = if ($null -eq $editModeResults) {
        'metadata unavailable'
    }
    else {
        @($editModeResults.missingIdentities) -join ', '
    }
    throw (
        'Formal acceptance requires a non-empty, fully passed EditMode ' +
        "result with all direct-binning identities; missing: $missing")
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $projectRoot (
        "Reports\GpuDirectBinning\$MatrixPreset-$stamp")
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)

if ([string]::IsNullOrWhiteSpace($PlayerPath)) {
    $PlayerPath = Join-Path $projectRoot (
        'Builds\GpuDirectBinningBenchmark\' +
        'GpuDirectBinningBenchmark.exe')
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

$runtimePackageRoot =
    Join-Path $projectRoot 'Packages\com.summit.gpu-direct-binning'
$runtimeShaderPath = Join-Path $runtimePackageRoot (
    'Runtime\Resources\GpuDirectBinning\GpuDirectBinning.compute')
$runtimeApiPath =
    Join-Path $runtimePackageRoot 'Runtime\GpuDirectSpatialBinner.cs'
$benchmarkRoot =
    Join-Path $projectRoot 'Assets\GpuDirectBinningBenchmark'
$referenceShaderPath = Join-Path $benchmarkRoot (
    'Runtime\Resources\GpuDirectBinningBenchmarkReference.compute')
$summarizerPath =
    Join-Path $PSScriptRoot 'Summarize-GpuDirectBinningBenchmark.ps1'
$buildScriptPath = Join-Path $benchmarkRoot (
    'Editor\GpuDirectBinningBenchmarkBuild.cs')
$timestampDllPath = Join-Path $projectRoot (
    'Packages\com.summit.gpu-timestamps\Runtime\Plugins\x86_64\' +
    'SummitGpuTimestamps.dll')
foreach ($requiredPath in @(
    $runtimeShaderPath,
    $runtimeApiPath,
    $referenceShaderPath,
    $summarizerPath,
    $buildScriptPath,
    $provenanceModulePath,
    $timestampDllPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required benchmark input is missing: $requiredPath"
    }
}

$sourceFiles = [System.IO.FileInfo[]]@(
    Get-ChildItem -LiteralPath $benchmarkRoot -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.compute', '.asmdef') }
    Get-ChildItem -LiteralPath (
        Join-Path $runtimePackageRoot 'Runtime') -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.compute', '.asmdef') }
    Get-ChildItem -LiteralPath (
        Join-Path $projectRoot 'Packages\com.summit.gpu-primitives\Runtime') `
        -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.compute', '.asmdef') }
    Get-ChildItem -LiteralPath (
        Join-Path $projectRoot 'Packages\com.summit.gpu-timestamps\Runtime') `
        -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.asmdef') }
    Get-Item -LiteralPath $PSCommandPath
    Get-Item -LiteralPath $summarizerPath
    Get-Item -LiteralPath $provenanceModulePath
    Get-Item -LiteralPath $timestampDllPath
    Get-Item -LiteralPath (Join-Path $projectRoot 'Packages\manifest.json')
    Get-Item -LiteralPath (Join-Path $projectRoot 'Packages\packages-lock.json')
    Get-Item -LiteralPath $projectVersionPath
)
$sourceSnapshotSha256 =
    Get-CombinedSha256 -Files $sourceFiles -RelativeTo $projectRoot
$runtimeShaderSha256 =
    (Get-FileHash -LiteralPath $runtimeShaderPath -Algorithm SHA256).Hash
$referenceShaderSha256 =
    (Get-FileHash -LiteralPath $referenceShaderPath -Algorithm SHA256).Hash
$runtimeApiSha256 =
    (Get-FileHash -LiteralPath $runtimeApiPath -Algorithm SHA256).Hash

$scenarios = if ($FormalAcceptanceMode) {
    $formalScenarios
}
else {
    @([ordered]@{
        scenarioId = $ScenarioId
        elementCount = $ElementCount
        binCount = $BinCount
        distribution = $Distribution
        seed = $Seed
    })
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
    schemaVersion = 5
    suite = 'summit.gpu-direct-binning'
    benchmarkSchemaVersion = 1
    formalAcceptanceMode = [bool]$FormalAcceptanceMode
    formalContract = $formalContract
    matrixPreset = $MatrixPreset
    scenarios = $scenarios
    editModeResults = $editModeResults
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
    sourceHashesStableAcrossBuild = $false
    runtimeShaderSha256 = $runtimeShaderSha256
    referenceShaderSha256 = $referenceShaderSha256
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
        '-executeMethod', 'GpuDirectBinningBenchmarkBuild.PerformBuild',
        '-gpu-direct-binning-player-path',
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
        '-gpu-direct-binning-benchmark',
        '-gpu-direct-binning-report-dir',
            (Quote-ProcessArgument $scenarioRoot),
        '-gpu-direct-binning-scenario-id',
            (Quote-ProcessArgument $scenario.scenarioId),
        '-gpu-direct-binning-super-rounds', [string]$SuperRounds,
        '-gpu-direct-binning-warmup-frames', [string]$WarmupFrames,
        '-gpu-direct-binning-sample-frames', [string]$SampleFrames,
        '-gpu-direct-binning-cooldown-frames', [string]$CooldownFrames,
        '-gpu-direct-binning-element-count',
            [string]$scenario.elementCount,
        '-gpu-direct-binning-bin-count', [string]$scenario.binCount,
        '-gpu-direct-binning-distribution',
            (Quote-ProcessArgument $scenario.distribution),
        '-gpu-direct-binning-seed', [string]$scenario.seed,
        '-gpu-direct-binning-dispatches-per-frame',
            [string]$DispatchesPerFrame,
        '-gpu-direct-binning-validation-timeout-seconds',
            [string]$ValidationTimeoutSeconds,
        '-gpu-direct-binning-require-complete-gpu-timings',
            $(if ($AllowMissingGpuTiming) { '0' } else { '1' }),
        '-gpu-direct-binning-build-commit', $gitCommit,
        '-gpu-direct-binning-runtime-shader-sha256',
            $runtimeShaderSha256,
        '-gpu-direct-binning-reference-shader-sha256',
            $referenceShaderSha256,
        '-gpu-direct-binning-runtime-api-sha256',
            $runtimeApiSha256,
        '-logFile', (Quote-ProcessArgument $playerLog)
    )
    Write-Host (
        "Running direct-binning scenario '$($scenario.scenarioId)' " +
        "N=$($scenario.elementCount) C=$($scenario.binCount)")
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
        throw 'Direct-binning benchmark summarization failed.'
    }
}

Write-Host "Completed GPU direct-binning benchmark: $outputRoot"
