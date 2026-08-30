[CmdletBinding()]
param(
    [string]$UnityPath,

    [string]$OutputDirectory,

    [string]$PlayerPath,

    [ValidateRange(0, 16)]
    [int]$DeviceIndex = 0,

    [ValidateRange(1, 12)]
    [int]$Rounds = 3,

    [ValidateRange(5, 1800)]
    [int]$WarmupFrames = 60,

    [ValidateRange(60, 7200)]
    [int]$SampleFrames = 240,

    [ValidateRange(0, 600)]
    [int]$CooldownFrames = 15,

    [ValidateRange(1024, 16776960)]
    [int]$ElementCount = 1048576,

    [int]$Seed = 20260730,

    [ValidateRange(1, 128)]
    [int]$DispatchesPerFrame = 1,

    [string]$Operations = '*',

    [string]$Backends = 'portable,wave-ops',

    [ValidateSet('uniform-16', 'hotset-4', 'single-bin')]
    [string]$HistogramDistribution = 'uniform-16',

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
    rounds = 3
    warmupFrames = 60
    sampleFrames = 900
    cooldownFrames = 15
    elementCount = 1048576
    seed = 20260730
    dispatchesPerFrame = 1
    operations = '*'
    backends = 'portable,wave-ops'
    histogramDistribution = 'uniform-16'
}
if ($FormalAcceptanceMode) {
    $formalViolations = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in @(
        @('DeviceIndex', $DeviceIndex, $formalContract.deviceIndex),
        @('Rounds', $Rounds, $formalContract.rounds),
        @('WarmupFrames', $WarmupFrames, $formalContract.warmupFrames),
        @('SampleFrames', $SampleFrames, $formalContract.sampleFrames),
        @('CooldownFrames', $CooldownFrames, $formalContract.cooldownFrames),
        @('ElementCount', $ElementCount, $formalContract.elementCount),
        @('Seed', $Seed, $formalContract.seed),
        @('DispatchesPerFrame', $DispatchesPerFrame,
            $formalContract.dispatchesPerFrame))) {
        if ([int64]$entry[1] -ne [int64]$entry[2]) {
            $formalViolations.Add(
                "$($entry[0])=$($entry[1]); expected $($entry[2])")
        }
    }
    if (-not [string]::Equals(
            $Operations, $formalContract.operations,
            [System.StringComparison]::Ordinal)) {
        $formalViolations.Add(
            "Operations='$Operations'; expected '$($formalContract.operations)'")
    }
    if (-not [string]::Equals(
            $Backends, $formalContract.backends,
            [System.StringComparison]::Ordinal)) {
        $formalViolations.Add(
            "Backends='$Backends'; expected '$($formalContract.backends)'")
    }
    if (-not [string]::Equals(
            $HistogramDistribution,
            $formalContract.histogramDistribution,
            [System.StringComparison]::Ordinal)) {
        $formalViolations.Add(
            "HistogramDistribution='$HistogramDistribution'; expected " +
            "'$($formalContract.histogramDistribution)'")
    }
    if ($SkipBuild) { $formalViolations.Add('SkipBuild is forbidden.') }
    if ($SkipSummary) { $formalViolations.Add('SkipSummary is forbidden.') }
    if ($AllowMissingGpuTiming) {
        $formalViolations.Add('AllowMissingGpuTiming is forbidden.')
    }
    if ([string]::IsNullOrWhiteSpace($EditModeResultsPath)) {
        $formalViolations.Add('EditModeResultsPath is required.')
    }
    if ($formalViolations.Count -ne 0) {
        throw "Formal acceptance contract rejected:`n$($formalViolations -join "`n")"
    }
}
if ($HistogramDistribution -ne 'uniform-16' -and
    -not [string]::Equals(
        $Operations,
        'histogram-16',
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw (
        "HistogramDistribution='$HistogramDistribution' requires " +
        "Operations='histogram-16' so other primitive workloads remain unchanged.")
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = [System.IO.Path]::GetFullPath($projectRoot)
$projectVersionPath = Join-Path $projectRoot (
    'ProjectSettings\ProjectVersion.txt')
$projectVersionLine = Get-Content -LiteralPath $projectVersionPath |
    Where-Object { $_ -like 'm_EditorVersion:*' } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($projectVersionLine)) {
    throw "Unable to read Unity version from $projectVersionPath"
}
$projectUnityVersion = ($projectVersionLine -split ':', 2)[1].Trim()
if ($FormalAcceptanceMode -and
    $projectUnityVersion -ne $formalContract.unityVersion) {
    throw (
        "Formal acceptance requires Unity $($formalContract.unityVersion); " +
        "ProjectVersion.txt declares $projectUnityVersion.")
}
$provenanceModulePath = Join-Path $PSScriptRoot 'GpuBenchmarkProvenance.psm1'
Import-Module -Name $provenanceModulePath -Force
$gitStartSnapshot =
    Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
$gitTreeDirty = [bool]$gitStartSnapshot.dirty
if (-not $AllowMissingGpuTiming -and $gitTreeDirty) {
    throw (
        "Strict GPU timing requires a clean worktree, including non-ignored " +
        "untracked files. Commit or remove:`n" +
        "$(@($gitStartSnapshot.statusLines) -join "`n")")
}
if ($FormalAcceptanceMode -and
    [string]$gitStartSnapshot.branch -eq 'HEAD') {
    throw 'Formal acceptance requires a named Git branch, not detached HEAD.'
}

function Resolve-UnityEditor {
    param([string]$RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = (Resolve-Path -LiteralPath $RequestedPath).Path
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Unity Editor is missing: $resolved"
        }
        return $resolved
    }

    $versionFile = Join-Path $projectRoot 'ProjectSettings\ProjectVersion.txt'
    $versionLine = Get-Content -LiteralPath $versionFile |
        Where-Object { $_ -like 'm_EditorVersion:*' } |
        Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($versionLine)) {
        throw "Unable to read Unity version from $versionFile"
    }
    $version = ($versionLine -split ':', 2)[1].Trim()
    $candidates = @(
        (Join-Path ${env:ProgramFiles} "Unity\Hub\Editor\$version\Editor\Unity.exe"),
        (Join-Path ${env:ProgramFiles} "Unity $version\Editor\Unity.exe")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }
    throw "Unity $version was not found. Pass -UnityPath explicitly."
}

function Quote-ProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Value)

    return '"' + $Value.Replace('"', '\"') + '"'
}

function Get-CombinedSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo[]]$Files,

        [Parameter(Mandatory = $true)]
        [string]$RelativeTo
    )

    if ($Files.Count -eq 0) {
        throw 'Cannot hash an empty file set.'
    }
    $builder = [System.Text.StringBuilder]::new()
    foreach ($file in @($Files | Sort-Object -Property FullName)) {
        $relativePath = $file.FullName.Substring($RelativeTo.Length).
            TrimStart([char[]]@('\', '/')).Replace('\', '/')
        $fileHash =
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        [void]$builder.Append($relativePath)
        [void]$builder.Append('=')
        [void]$builder.Append($fileHash)
        [void]$builder.Append("`n")
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($builder.ToString())
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [System.BitConverter]::ToString(
            $sha256.ComputeHash($bytes)).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
}

function Get-NUnitResultMetadata {
    param([Parameter(Mandatory = $true)][string]$Path)

    $candidate = $Path
    if (-not [System.IO.Path]::IsPathRooted($candidate)) {
        $candidate = Join-Path $projectRoot $candidate
    }
    $resolved = (Resolve-Path -LiteralPath $candidate).Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "EditMode NUnit XML is missing: $resolved"
    }
    try {
        [xml]$document = Get-Content -LiteralPath $resolved -Raw
    }
    catch {
        throw (
            "Unable to parse EditMode NUnit XML '$resolved': " +
            $_.Exception.Message)
    }
    $testRun = $document.DocumentElement
    if ($null -eq $testRun -or $testRun.LocalName -ne 'test-run') {
        throw "EditMode results root must be <test-run>: $resolved"
    }

    $attributes = [ordered]@{}
    foreach ($name in @(
            'total', 'passed', 'failed', 'skipped', 'inconclusive')) {
        $text = $testRun.GetAttribute($name)
        [int]$value = 0
        if ([string]::IsNullOrWhiteSpace($text)) {
            if ($name -eq 'inconclusive') {
                $attributes[$name] = 0
                continue
            }
            throw "EditMode results lack '$name': $resolved"
        }
        if (-not [int]::TryParse(
                $text,
                [System.Globalization.NumberStyles]::Integer,
                [System.Globalization.CultureInfo]::InvariantCulture,
                [ref]$value) -or $value -lt 0) {
            throw "EditMode results '$name' is invalid: '$text'."
        }
        $attributes[$name] = $value
    }

    $result = $testRun.GetAttribute('result')
    if ($result -ne 'Passed' -or
        $attributes.failed -ne 0 -or
        $attributes.skipped -ne 0 -or
        $attributes.inconclusive -ne 0 -or
        $attributes.passed -ne $attributes.total) {
        throw (
            "EditMode acceptance failed: result=$result total=" +
            "$($attributes.total) passed=$($attributes.passed) " +
            "failed=$($attributes.failed) skipped=$($attributes.skipped) " +
            "inconclusive=$($attributes.inconclusive).")
    }

    return [ordered]@{
        path = $resolved
        sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
        result = $result
        total = $attributes.total
        passed = $attributes.passed
        failed = $attributes.failed
        skipped = $attributes.skipped
        inconclusive = $attributes.inconclusive
    }
}

$editModeResults = $null
if (-not [string]::IsNullOrWhiteSpace($EditModeResultsPath)) {
    $editModeResults = Get-NUnitResultMetadata -Path $EditModeResultsPath
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $projectRoot (
        "Reports\GpuPrimitives\same-process-$timestamp-" +
        "device-$DeviceIndex-n$ElementCount")
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)

if ([string]::IsNullOrWhiteSpace($PlayerPath)) {
    $PlayerPath = Join-Path $projectRoot (
        'Builds\GpuPrimitiveBenchmark\GpuPrimitiveBenchmark.exe')
}
elseif (-not [System.IO.Path]::IsPathRooted($PlayerPath)) {
    $PlayerPath = Join-Path $projectRoot $PlayerPath
}
$resolvedPlayerPath = [System.IO.Path]::GetFullPath($PlayerPath)
$playerPayloadRoot =
    [System.IO.Path]::GetDirectoryName($resolvedPlayerPath).TrimEnd('\', '/')
$normalizedOutputRoot = $outputRoot.TrimEnd('\', '/')
$playerPayloadPrefix = $playerPayloadRoot +
    [System.IO.Path]::DirectorySeparatorChar
$reportInsidePlayerPayload =
    [string]::Equals(
        $normalizedOutputRoot,
        $playerPayloadRoot,
        [System.StringComparison]::OrdinalIgnoreCase) -or
    $normalizedOutputRoot.StartsWith(
        $playerPayloadPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)
if ($reportInsidePlayerPayload) {
    throw (
        'The benchmark report directory may not be the Player payload root ' +
        'or one of its descendants.')
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$unityEditor = Resolve-UnityEditor -RequestedPath $UnityPath
$unityEditorVersionInfo =
    [System.Diagnostics.FileVersionInfo]::GetVersionInfo($unityEditor)
$unityEditorProductVersion = [string]$unityEditorVersionInfo.ProductVersion
$unityEditorResolvedVersion =
    ($unityEditorProductVersion -split '_', 2)[0]
if ($FormalAcceptanceMode -and
    $unityEditorResolvedVersion -ne $formalContract.unityVersion) {
    throw (
        "Formal acceptance requires Unity $($formalContract.unityVersion); " +
        "the resolved editor reports '$unityEditorProductVersion': " +
        $unityEditor)
}
$buildLog = Join-Path $outputRoot 'unity-build.log'
$playerLog = Join-Path $outputRoot 'player.log'

$portableShaderPath = Join-Path $projectRoot (
    'Packages\com.summit.gpu-primitives\Runtime\Resources\GpuPrimitives\' +
    'GpuPrimitivesPortable.compute')
$waveShaderPath = Join-Path $projectRoot (
    'Packages\com.summit.gpu-primitives\Runtime\Resources\GpuPrimitives\' +
    'GpuPrimitivesWave.compute')
$runtimeApiPath = Join-Path $projectRoot (
    'Packages\com.summit.gpu-primitives\Runtime\GpuPrimitives.cs')
$timestampRuntimePath = Join-Path $projectRoot (
    'Packages\com.summit.gpu-timestamps\Runtime')
$timestampNativeDllPath = Join-Path $timestampRuntimePath (
    'Plugins\x86_64\SummitGpuTimestamps.dll')
$timestampNativeSourcePath = Join-Path $projectRoot (
    'Packages\com.summit.gpu-timestamps\Native~\SummitGpuTimestamps.cpp')
$timestampNativeHeaderPath = Join-Path $projectRoot (
    'Packages\com.summit.gpu-timestamps\Native~\SummitGpuTimestamps.h')
$timestampBuildScriptPath = Join-Path $PSScriptRoot (
    'Build-SummitGpuTimestampPlugin.ps1')
$benchmarkControllerPath = Join-Path $projectRoot (
    'Assets\GpuPrimitiveBenchmark\Runtime\GpuPrimitiveBenchmarkController.cs')
$benchmarkCoreAdapterPath = Join-Path $projectRoot (
    'Assets\GpuPrimitiveBenchmark\Runtime\GpuPrimitiveBenchmarkCoreAdapter.cs')
$benchmarkNativeBackendPath = Join-Path $projectRoot (
    'Assets\GpuPrimitiveBenchmark\Runtime\GpuPrimitiveNativeTimestampBackend.cs')
$benchmarkSummarizerPath = Join-Path $PSScriptRoot (
    'Summarize-GpuPrimitiveBenchmark.ps1')
$playerBuildScriptPath = Join-Path $projectRoot (
    'Assets\GpuPrimitiveBenchmark\Editor\GpuPrimitiveBenchmarkBuild.cs')
$packagesManifestPath = Join-Path $projectRoot 'Packages\manifest.json'
$packagesLockPath = Join-Path $projectRoot 'Packages\packages-lock.json'
$benchmarkHarnessFiles = [System.IO.FileInfo[]]@(
    Get-Item -LiteralPath $benchmarkControllerPath
    Get-Item -LiteralPath $benchmarkCoreAdapterPath
    Get-Item -LiteralPath $benchmarkNativeBackendPath
    Get-Item -LiteralPath $PSCommandPath
    Get-Item -LiteralPath $benchmarkSummarizerPath
    Get-Item -LiteralPath $provenanceModulePath
)
$benchmarkHarnessRelativePaths = @(
    $benchmarkHarnessFiles | ForEach-Object {
        $_.FullName.Substring($projectRoot.Length).
            TrimStart([char[]]@('\', '/')).Replace('\', '/')
    }
)
$timestampManagedRuntimeFiles = @(
    Get-ChildItem -LiteralPath $timestampRuntimePath -File |
        Where-Object { $_.Extension -in @('.cs', '.asmdef') }
)
$timestampNativeDllExists =
    Test-Path -LiteralPath $timestampNativeDllPath -PathType Leaf
if (-not $timestampNativeDllExists -and -not $AllowMissingGpuTiming) {
    throw "Native timestamp plugin is missing: $timestampNativeDllPath"
}
$sourceSnapshotFiles = [System.IO.FileInfo[]]@(
    $benchmarkHarnessFiles
    $timestampManagedRuntimeFiles
    Get-Item -LiteralPath $portableShaderPath
    Get-Item -LiteralPath $waveShaderPath
    Get-Item -LiteralPath $runtimeApiPath
    Get-Item -LiteralPath $timestampNativeSourcePath
    Get-Item -LiteralPath $timestampNativeHeaderPath
    Get-Item -LiteralPath $timestampBuildScriptPath
    Get-Item -LiteralPath $playerBuildScriptPath
    Get-Item -LiteralPath $packagesManifestPath
    Get-Item -LiteralPath $packagesLockPath
    Get-Item -LiteralPath $projectVersionPath
    if ($timestampNativeDllExists) {
        Get-Item -LiteralPath $timestampNativeDllPath
    }
)
$sourceSnapshotRelativePaths = @(
    $sourceSnapshotFiles | ForEach-Object {
        $_.FullName.Substring($projectRoot.Length).
            TrimStart([char[]]@('\', '/')).Replace('\', '/')
    }
)
$portableShaderSha256 = (Get-FileHash -LiteralPath $portableShaderPath -Algorithm SHA256).Hash
$waveShaderSha256 = (Get-FileHash -LiteralPath $waveShaderPath -Algorithm SHA256).Hash
$runtimeApiSha256 = (Get-FileHash -LiteralPath $runtimeApiPath -Algorithm SHA256).Hash
$timestampManagedRuntimeSha256 = Get-CombinedSha256 `
    -Files $timestampManagedRuntimeFiles `
    -RelativeTo $projectRoot
$timestampNativeDllSha256 = if ($timestampNativeDllExists) {
    (Get-FileHash -LiteralPath $timestampNativeDllPath -Algorithm SHA256).Hash
}
else { 'missing' }
$timestampNativeSourceSha256 =
    (Get-FileHash -LiteralPath $timestampNativeSourcePath -Algorithm SHA256).Hash
$timestampNativeHeaderSha256 =
    (Get-FileHash -LiteralPath $timestampNativeHeaderPath -Algorithm SHA256).Hash
$timestampBuildScriptSha256 =
    (Get-FileHash -LiteralPath $timestampBuildScriptPath -Algorithm SHA256).Hash
$playerBuildScriptSha256 =
    (Get-FileHash -LiteralPath $playerBuildScriptPath -Algorithm SHA256).Hash
$packagesManifestSha256 =
    (Get-FileHash -LiteralPath $packagesManifestPath -Algorithm SHA256).Hash
$packagesLockSha256 =
    (Get-FileHash -LiteralPath $packagesLockPath -Algorithm SHA256).Hash
$projectVersionSha256 =
    (Get-FileHash -LiteralPath $projectVersionPath -Algorithm SHA256).Hash
$benchmarkHarnessSha256 = Get-CombinedSha256 `
    -Files $benchmarkHarnessFiles `
    -RelativeTo $projectRoot
$sourceSnapshotSha256 = Get-CombinedSha256 `
    -Files $sourceSnapshotFiles `
    -RelativeTo $projectRoot
$gitCommit = [string]$gitStartSnapshot.head
$windowsVideoControllers = @()
$windowsVideoControllerInventoryError = ''
if ([System.Environment]::OSVersion.Platform -eq
    [System.PlatformID]::Win32NT) {
    try {
        $windowsVideoControllers = @(
            Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop |
                ForEach-Object {
                    [ordered]@{
                        name = [string]$_.Name
                        pnpDeviceId = [string]$_.PNPDeviceID
                        deviceId = [string]$_.DeviceID
                        driverVersion = [string]$_.DriverVersion
                    }
                }
        )
        if ($windowsVideoControllers.Count -eq 0) {
            $windowsVideoControllerInventoryError =
                'Win32_VideoController returned no rows.'
        }
    }
    catch {
        $windowsVideoControllerInventoryError =
            $_.Exception.GetType().Name + ': ' + $_.Exception.Message
    }
}
else {
    $windowsVideoControllerInventoryError = 'Unavailable on non-Windows host.'
}

$runnerConfiguration = [ordered]@{
    schemaVersion = 5
    formalAcceptanceMode = [bool]$FormalAcceptanceMode
    formalContract = $formalContract
    editModeResults = $editModeResults
    runnerConfigFinalized = $false
    buildRequested = (-not $SkipBuild)
    buildPerformed = $false
    summaryRequested = (-not $SkipSummary)
    playerExecutableSha256 = ''
    playerExecutableLengthBytes = 0
    playerExecutableLastWriteUtc = ''
    playerPayload = $null
    playerPayloadStableThroughRun = $false
    playerPayloadManifestPath = ''
    finalizedUtc = ''
    projectRoot = $projectRoot
    unityPath = $unityEditor
    projectUnityVersion = $projectUnityVersion
    unityEditorProductVersion = $unityEditorProductVersion
    unityEditorResolvedVersion = $unityEditorResolvedVersion
    playerPath = $resolvedPlayerPath
    outputDirectory = $outputRoot
    deviceIndex = $DeviceIndex
    rounds = $Rounds
    warmupFrames = $WarmupFrames
    sampleFrames = $SampleFrames
    cooldownFrames = $CooldownFrames
    elementCount = $ElementCount
    seed = $Seed
    dispatchesPerFrame = $DispatchesPerFrame
    operations = $Operations
    backends = $Backends
    histogramDistribution = $HistogramDistribution
    validationTimeoutSeconds = $ValidationTimeoutSeconds
    requireCompleteGpuTimings = (-not $AllowMissingGpuTiming)
    singlePlayerProcess = $true
    startedUtc = (Get-Date).ToUniversalTime().ToString('o')
    gitCommit = $gitCommit
    gitBranch = [string]$gitStartSnapshot.branch
    gitTreeDirty = $gitTreeDirty
    gitTreeDirtyAtStart = $gitTreeDirty
    gitStart = $gitStartSnapshot
    gitPostBuildBeforeRestore = $null
    gitPostBuildAfterRestore = $null
    gitPostBuildRestoredPaths = [string[]]@()
    gitPostBuildDriftKinds = [string[]]@()
    gitFinalBeforeRestore = $null
    gitFinal = $null
    gitFinalRestoredPaths = [string[]]@()
    gitFinalDriftKinds = [string[]]@()
    benchmarkHarnessSha256 = $benchmarkHarnessSha256
    benchmarkHarnessFiles = $benchmarkHarnessRelativePaths
    sourceSnapshotSha256 = $sourceSnapshotSha256
    preBuildSourceSnapshotSha256 = $sourceSnapshotSha256
    sourceSnapshotFiles = $sourceSnapshotRelativePaths
    windowsVideoControllers = $windowsVideoControllers
    windowsVideoControllerInventoryError =
        $windowsVideoControllerInventoryError
    portableShaderSha256 = $portableShaderSha256
    waveShaderSha256 = $waveShaderSha256
    runtimeApiSha256 = $runtimeApiSha256
    timestampBackendRequested = 'native-d3d12-timestamp-query'
    timestampManagedRuntimeSha256 = $timestampManagedRuntimeSha256
    timestampNativeDllSha256 = $timestampNativeDllSha256
    timestampNativeSourceSha256 = $timestampNativeSourceSha256
    timestampNativeHeaderSha256 = $timestampNativeHeaderSha256
    timestampBuildScriptSha256 = $timestampBuildScriptSha256
    timestampNativeDllPresent = $timestampNativeDllExists
    playerBuildScriptSha256 = $playerBuildScriptSha256
    packagesManifestSha256 = $packagesManifestSha256
    packagesLockSha256 = $packagesLockSha256
    projectVersionSha256 = $projectVersionSha256
    sourceHashesStableAcrossBuild = $false
}
$runnerConfigPath = Join-Path $outputRoot 'runner-config.json'
$runnerConfiguration |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $runnerConfigPath -Encoding utf8

$buildFailure = ''
$buildWasPerformed = $false
if (-not $SkipBuild) {
    try {
        $buildArguments = @(
            '-batchmode',
            '-nographics',
            '-quit',
            '-projectPath', (Quote-ProcessArgument $projectRoot),
            '-executeMethod', 'GpuPrimitiveBenchmarkBuild.PerformBuild',
            '-gpu-primitive-player-path',
                (Quote-ProcessArgument $resolvedPlayerPath),
            '-logFile', (Quote-ProcessArgument $buildLog)
        )
        Write-Host "Building minimal benchmark Player with $unityEditor"
        $buildProcess = Start-Process `
            -FilePath $unityEditor `
            -ArgumentList $buildArguments `
            -WorkingDirectory $projectRoot `
            -WindowStyle Hidden `
            -Wait `
            -PassThru
        if ($buildProcess.ExitCode -ne 0) {
            $buildFailure = (
                "Unity Player build failed with exit code " +
                "$($buildProcess.ExitCode). See $buildLog")
        }
        else {
            $buildWasPerformed = $true
        }
    }
    catch {
        $buildFailure =
            'Unity Player build failed: ' + $_.Exception.Message
    }
}

$postBuildBeforeRestore =
    Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
$runnerConfiguration['gitPostBuildBeforeRestore'] =
    $postBuildBeforeRestore
$runnerConfiguration |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $runnerConfigPath -Encoding utf8
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
$runnerConfiguration['gitPostBuildAfterRestore'] =
    $postBuildRestore.afterSnapshot
$runnerConfiguration['gitPostBuildRestoredPaths'] =
    [string[]]@($postBuildRestore.restoredPaths)
$runnerConfiguration['gitPostBuildDriftKinds'] =
    [string[]]@($postBuildRestore.driftKinds)
$runnerConfiguration['buildPerformed'] = $buildWasPerformed
if (-not [string]::IsNullOrWhiteSpace($buildFailure)) {
    $runnerConfiguration |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $runnerConfigPath -Encoding utf8
    throw $buildFailure
}

if (-not (Test-Path -LiteralPath $resolvedPlayerPath -PathType Leaf)) {
    throw "Benchmark Player is missing: $resolvedPlayerPath"
}

$playerExecutable = Get-Item -LiteralPath $resolvedPlayerPath
$postBuildBenchmarkHarnessSha256 = Get-CombinedSha256 `
    -Files $benchmarkHarnessFiles `
    -RelativeTo $projectRoot
$postBuildSourceSnapshotSha256 = Get-CombinedSha256 `
    -Files $sourceSnapshotFiles `
    -RelativeTo $projectRoot
$sourceHashesStableAcrossBuild =
    $postBuildBenchmarkHarnessSha256 -eq $benchmarkHarnessSha256 -and
    $postBuildSourceSnapshotSha256 -eq $sourceSnapshotSha256

$runnerConfiguration['benchmarkHarnessSha256'] =
    $postBuildBenchmarkHarnessSha256
$runnerConfiguration['sourceSnapshotSha256'] =
    $postBuildSourceSnapshotSha256
$runnerConfiguration['sourceHashesStableAcrossBuild'] =
    $sourceHashesStableAcrossBuild
$postBuildPlayerPayload =
    Get-GpuBenchmarkPlayerPayload -PlayerPath $resolvedPlayerPath
$runnerConfiguration['playerPayload'] = $postBuildPlayerPayload
$runnerConfiguration['playerExecutableSha256'] =
    $postBuildPlayerPayload.playerExecutableSha256
$runnerConfiguration['playerExecutableLengthBytes'] =
    [int64]$postBuildPlayerPayload.playerExecutableLengthBytes
$runnerConfiguration['playerExecutableLastWriteUtc'] =
    $playerExecutable.LastWriteTimeUtc.ToString('o')
$playerPayloadManifestPath =
    Join-Path $outputRoot 'player-payload-manifest.json'
$runnerConfiguration['playerPayloadManifestPath'] =
    $playerPayloadManifestPath
$postBuildPlayerPayload |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $playerPayloadManifestPath -Encoding utf8
$runnerConfiguration |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $runnerConfigPath -Encoding utf8

if ($FormalAcceptanceMode -and -not $sourceHashesStableAcrossBuild) {
    throw (
        'Formal acceptance rejected because benchmark source or harness hashes ' +
        'changed during the Player build. See runner-config.json.')
}

$playerArguments = @(
    '-force-d3d12',
    '-force-device-index', [string]$DeviceIndex,
    '-screen-fullscreen', '0',
    '-screen-width', '640',
    '-screen-height', '360',
    '-gpu-primitive-benchmark',
    '-gpu-primitive-report-dir', (Quote-ProcessArgument $outputRoot),
    '-gpu-primitive-rounds', [string]$Rounds,
    '-gpu-primitive-warmup-frames', [string]$WarmupFrames,
    '-gpu-primitive-sample-frames', [string]$SampleFrames,
    '-gpu-primitive-cooldown-frames', [string]$CooldownFrames,
    '-gpu-primitive-element-count', [string]$ElementCount,
    '-gpu-primitive-seed', [string]$Seed,
    '-gpu-primitive-dispatches-per-frame', [string]$DispatchesPerFrame,
    '-gpu-primitive-operations', (Quote-ProcessArgument $Operations),
    '-gpu-primitive-backends', (Quote-ProcessArgument $Backends),
    '-gpu-primitive-histogram-distribution',
        (Quote-ProcessArgument $HistogramDistribution),
    '-gpu-primitive-validation-timeout-seconds', [string]$ValidationTimeoutSeconds,
    '-gpu-primitive-require-complete-gpu-timings',
        $(if ($AllowMissingGpuTiming) { '0' } else { '1' }),
    '-gpu-primitive-build-commit', $gitCommit,
    '-gpu-primitive-portable-shader-sha256',
        $portableShaderSha256,
    '-gpu-primitive-wave-shader-sha256',
        $waveShaderSha256,
    '-gpu-primitive-runtime-api-sha256',
        $runtimeApiSha256,
    '-logFile', (Quote-ProcessArgument $playerLog)
)

Write-Host (
    "Running one Player PID with $Rounds counterbalanced rounds, " +
    "$SampleFrames measured frames per block.")
$playerProcess = Start-Process `
    -FilePath $resolvedPlayerPath `
    -ArgumentList $playerArguments `
    -WorkingDirectory $projectRoot `
    -WindowStyle Hidden `
    -PassThru
$completed = $playerProcess.WaitForExit($PlayerTimeoutMinutes * 60 * 1000)
$playerFailure = ''
if (-not $completed) {
    $playerProcess.Kill()
    $playerProcess.WaitForExit()
    $playerFailure =
        "Benchmark Player timed out after $PlayerTimeoutMinutes minutes. See $playerLog"
}
elseif ($playerProcess.ExitCode -ne 0) {
    $playerFailure =
        "Benchmark Player failed with exit code $($playerProcess.ExitCode). See $playerLog"
}

$finalBeforeRestore =
    Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
$runnerConfiguration['gitFinalBeforeRestore'] = $finalBeforeRestore
$runnerConfiguration |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $runnerConfigPath -Encoding utf8
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
$finalGitSnapshot = $finalRestore.afterSnapshot
$finalPlayerPayload =
    Get-GpuBenchmarkPlayerPayload -PlayerPath $resolvedPlayerPath
$playerPayloadStableThroughRun =
    [string]$finalPlayerPayload.sha256 -ceq
        [string]$postBuildPlayerPayload.sha256 -and
    [int]$finalPlayerPayload.fileCount -eq
        [int]$postBuildPlayerPayload.fileCount -and
    [int64]$finalPlayerPayload.lengthBytes -eq
        [int64]$postBuildPlayerPayload.lengthBytes
$runnerConfiguration['gitFinal'] = $finalGitSnapshot
$runnerConfiguration['gitFinalRestoredPaths'] =
    [string[]]@($finalRestore.restoredPaths)
$runnerConfiguration['gitFinalDriftKinds'] =
    [string[]]@($finalRestore.driftKinds)
$runnerConfiguration['gitTreeDirty'] = [bool]$finalGitSnapshot.dirty
$runnerConfiguration['playerPayload'] = $finalPlayerPayload
$runnerConfiguration['playerPayloadStableThroughRun'] =
    $playerPayloadStableThroughRun
$runnerConfiguration['playerExecutableSha256'] =
    $finalPlayerPayload.playerExecutableSha256
$runnerConfiguration['formalContractSatisfied'] =
    [bool](
        $FormalAcceptanceMode -and
        [string]::IsNullOrWhiteSpace($playerFailure) -and
        $sourceHashesStableAcrossBuild -and
        $playerPayloadStableThroughRun -and
        -not [bool]$finalGitSnapshot.dirty)
$runnerConfiguration['runnerConfigFinalized'] = $true
$runnerConfiguration['finalizedUtc'] =
    (Get-Date).ToUniversalTime().ToString('o')
$finalPlayerPayload |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $playerPayloadManifestPath -Encoding utf8
$runnerConfiguration |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $runnerConfigPath -Encoding utf8
if ($FormalAcceptanceMode -and -not $playerPayloadStableThroughRun) {
    throw (
        'Formal provenance rejected because the Player payload changed ' +
        'during the run.')
}
if (-not [string]::IsNullOrWhiteSpace($playerFailure)) {
    throw $playerFailure
}

$runSummaryPath = Join-Path $outputRoot 'run-summary.txt'
if (-not (Test-Path -LiteralPath $runSummaryPath -PathType Leaf)) {
    throw "Benchmark Player did not write run-summary.txt. See $playerLog"
}
$runSummary = @{}
foreach ($line in Get-Content -LiteralPath $runSummaryPath) {
    $parts = $line -split '=', 2
    if ($parts.Count -eq 2) {
        $runSummary[$parts[0]] = $parts[1]
    }
}
if ([int]$runSummary.passed -ne 1) {
    throw "Benchmark quality gate failed. See $runSummaryPath and validation.csv"
}
$acceptedStatuses = if ($AllowMissingGpuTiming) {
    @('completed', 'completed-correctness-only')
}
else {
    @('completed')
}
if ($runSummary.status -notin $acceptedStatuses) {
    throw "Unexpected benchmark status '$($runSummary.status)'. See $runSummaryPath"
}

$rawPath = Join-Path $outputRoot 'raw-frames.csv'
$processIds = @(Import-Csv -LiteralPath $rawPath |
    Select-Object -ExpandProperty processId -Unique)
if ($processIds.Count -ne 1) {
    throw "Expected exactly one Player process ID, observed: $($processIds -join ', ')"
}
if ([string]$processIds[0] -ne [string]$playerProcess.Id) {
    throw (
        "CSV process ID $($processIds[0]) does not match launched Player " +
        "PID $($playerProcess.Id).")
}

if (-not $SkipSummary) {
    & (Join-Path $PSScriptRoot 'Summarize-GpuPrimitiveBenchmark.ps1') `
        -ReportDirectory $outputRoot
    if (-not $?) {
        throw "Benchmark summarization failed."
    }
}

Write-Host "Completed one-PID GPU primitive benchmark: $outputRoot"
Write-Host "Player PID: $($playerProcess.Id)"
Write-Host "Raw frames: $rawPath"
