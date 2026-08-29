[CmdletBinding()]
param(
    [string]$UnityPath =
        'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe',
    [string]$OutputDirectory,
    [string]$PlayerPath,
    [ValidateRange(0, 16)]
    [int]$DeviceIndex = 0,
    [ValidateSet('single', 'visibility-sweep-v1')]
    [string]$MatrixPreset = 'single',
    [string]$ScenarioId = 'custom',
    [ValidateSet('visible5', 'visible25', 'visible75', 'visible100')]
    [string]$Visibility = 'visible25',
    [ValidateRange(1024, 4194240)]
    [int]$InstanceCount = 1048576,
    [ValidateRange(1, 32)]
    [int]$ViewCount = 4,
    [int]$Seed = 20260829,
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
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$formalContract = [ordered]@{
    unityVersion = '6000.5.2f1'
    deviceIndex = 0
    matrixPreset = 'visibility-sweep-v1'
    instanceCount = 1048576
    viewCount = 4
    superRounds = 2
    warmupFrames = 60
    sampleFrames = 900
    cooldownFrames = 15
    dispatchesPerFrame = 1
}
$formalScenarios = @(
    [ordered]@{
        scenarioId = 'visible5-n1048576-v4'
        visibility = 'visible5'
        instanceCount = 1048576
        viewCount = 4
        seed = 20260829
    },
    [ordered]@{
        scenarioId = 'visible25-n1048576-v4'
        visibility = 'visible25'
        instanceCount = 1048576
        viewCount = 4
        seed = 20260829
    },
    [ordered]@{
        scenarioId = 'visible75-n1048576-v4'
        visibility = 'visible75'
        instanceCount = 1048576
        viewCount = 4
        seed = 20260829
    },
    [ordered]@{
        scenarioId = 'visible100-n1048576-v4'
        visibility = 'visible100'
        instanceCount = 1048576
        viewCount = 4
        seed = 20260829
    }
)

function Quote-ProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Value)
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Get-CombinedSha256 {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo[]]$Files,
        [Parameter(Mandatory = $true)][string]$RelativeTo
    )
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $builder = [System.Text.StringBuilder]::new()
        foreach ($file in @($Files | Sort-Object FullName)) {
            $relative = $file.FullName.Substring($RelativeTo.Length).
                TrimStart([char[]]@('\', '/')).Replace('\', '/')
            $hash = (Get-FileHash `
                -LiteralPath $file.FullName `
                -Algorithm SHA256).Hash
            [void]$builder.Append($relative)
            [void]$builder.Append("`0")
            [void]$builder.Append($hash)
            [void]$builder.Append("`n")
        }
        return ([BitConverter]::ToString(
            $sha.ComputeHash(
                [Text.Encoding]::UTF8.GetBytes($builder.ToString())))).
            Replace('-', '')
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
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "EditMode results are missing: $resolved"
    }
    [xml]$xml = Get-Content -LiteralPath $resolved -Raw
    $run = $xml.'test-run'
    if ($null -eq $run) {
        throw "EditMode result is not NUnit XML: $resolved"
    }
    $identities = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $nodes = @($xml.SelectNodes("//test-suite[@type='Assembly']")) +
        @($xml.SelectNodes("//test-suite[@type='TestFixture']"))
    foreach ($node in $nodes) {
        [void]$identities.Add(
            $(if ([string]$node.type -ceq 'Assembly') {
                [string]$node.name
            }
            else {
                [string]$node.fullname
            }))
    }
    $missing = @(
        $ExpectedIdentities |
            Where-Object { -not $identities.Contains($_) })
    return [ordered]@{
        path = $resolved
        sha256 =
            (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
        result = [string]$run.result
        total = [int]$run.total
        passed = [int]$run.passed
        failed = [int]$run.failed
        skipped = [int]$run.skipped
        inconclusive = [int]$run.inconclusive
        expectedIdentities = $ExpectedIdentities
        observedIdentities = @($identities | Sort-Object)
        missingIdentities = $missing
    }
}

function Read-KeyValueFile {
    param([Parameter(Mandatory = $true)][string]$Path)
    $result = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        $parts = $line -split '=', 2
        if ($parts.Count -eq 2) {
            $result[$parts[0]] = $parts[1]
        }
    }
    return $result
}

function Number {
    param([Parameter(Mandatory = $true)]$Value)
    return [double]::Parse(
        [string]$Value,
        [Globalization.CultureInfo]::InvariantCulture)
}

function Mean {
    param([Parameter(Mandatory = $true)][double[]]$Values)
    if ($Values.Count -eq 0) { throw 'Cannot average an empty array.' }
    return [double](($Values | Measure-Object -Average).Average)
}

function Percentile {
    param(
        [Parameter(Mandatory = $true)][double[]]$Values,
        [Parameter(Mandatory = $true)][double]$Probability
    )
    if ($Values.Count -eq 0) { throw 'Cannot rank an empty array.' }
    $sorted = [double[]]@($Values | Sort-Object)
    $rank = ($sorted.Count - 1) * $Probability
    $low = [int][Math]::Floor($rank)
    $high = [int][Math]::Ceiling($rank)
    if ($low -eq $high) { return $sorted[$low] }
    return $sorted[$low] +
        ($sorted[$high] - $sorted[$low]) * ($rank - $low)
}

function ImprovementPercent {
    param([double]$Baseline, [double]$Optimized)
    if ($Baseline -le 0.0) { return [double]::NaN }
    return 100.0 * ($Baseline - $Optimized) / $Baseline
}

if ([int64]$InstanceCount * [int64]$ViewCount -gt 16776960L) {
    throw 'InstanceCount * ViewCount exceeds the GPU primitive capacity.'
}
if ($FormalAcceptanceMode) {
    $violations = [Collections.Generic.List[string]]::new()
    foreach ($entry in @(
            @('DeviceIndex', $DeviceIndex, $formalContract.deviceIndex),
            @('InstanceCount', $InstanceCount,
                $formalContract.instanceCount),
            @('ViewCount', $ViewCount, $formalContract.viewCount),
            @('SuperRounds', $SuperRounds, $formalContract.superRounds),
            @('WarmupFrames', $WarmupFrames,
                $formalContract.warmupFrames),
            @('SampleFrames', $SampleFrames,
                $formalContract.sampleFrames),
            @('CooldownFrames', $CooldownFrames,
                $formalContract.cooldownFrames),
            @('DispatchesPerFrame', $DispatchesPerFrame,
                $formalContract.dispatchesPerFrame))) {
        if ([int64]$entry[1] -ne [int64]$entry[2]) {
            $violations.Add(
                "$($entry[0])=$($entry[1]); expected $($entry[2])")
        }
    }
    if ($MatrixPreset -cne $formalContract.matrixPreset) {
        $violations.Add(
            "MatrixPreset='$MatrixPreset'; expected " +
            "'$($formalContract.matrixPreset)'")
    }
    if ($SkipBuild) { $violations.Add('SkipBuild is forbidden.') }
    if ([string]::IsNullOrWhiteSpace($EditModeResultsPath)) {
        $violations.Add('EditModeResultsPath is required.')
    }
    if ($violations.Count -ne 0) {
        throw "Formal acceptance contract rejected:`n$($violations -join "`n")"
    }
}
elseif ($MatrixPreset -cne 'single') {
    throw "Non-formal runs require MatrixPreset='single'."
}

$projectRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent $PSScriptRoot))
$provenanceModulePath =
    Join-Path $PSScriptRoot 'GpuBenchmarkProvenance.psm1'
Import-Module -Name $provenanceModulePath -Force
$gitStart = Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
if ([bool]$gitStart.dirty) {
    throw "Benchmark requires a clean worktree:`n" +
        (@($gitStart.statusLines) -join "`n")
}
if ([string]$gitStart.branch -ceq 'HEAD') {
    throw 'Benchmark requires a named Git branch.'
}
$gitCommit = [string]$gitStart.head

$projectVersionPath =
    Join-Path $projectRoot 'ProjectSettings\ProjectVersion.txt'
$versionLine = Get-Content -LiteralPath $projectVersionPath |
    Where-Object { $_ -like 'm_EditorVersion:*' } |
    Select-Object -First 1
$projectUnityVersion = ($versionLine -split ':', 2)[1].Trim()
if ($FormalAcceptanceMode -and
    $projectUnityVersion -cne $formalContract.unityVersion) {
    throw "Formal benchmark requires Unity $($formalContract.unityVersion)."
}
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw "Unity editor is missing: $UnityPath"
}

$expectedTestIdentities = @(
    'Summit.GpuDrivenInstance.Benchmark.Tests.Editor.dll',
    'Summit.GpuDrivenInstance.Benchmark.Tests.GpuDrivenInstanceBenchmarkCpuOracleTests',
    'Summit.GpuDrivenInstance.Benchmark.Tests.GpuDrivenInstanceBenchmarkScheduleTests',
    'Summit.GpuDrivenInstance.Benchmark.Tests.GpuDrivenInstanceInputGeneratorTests',
    'Summit.GpuDrivenInstances.Tests.Editor.dll',
    'Summit.GpuDrivenInstances.Tests.GpuDrivenInstancePipelineContractTests',
    'Summit.GpuDrivenInstances.Tests.GpuDrivenInstancePipelineIntegrationTests',
    'Summit.GpuDirectBinning.Tests.Editor.dll',
    'Summit.GpuDirectBinning.Tests.GpuDirectSpatialBinnerContractTests',
    'Summit.GpuDirectBinning.Tests.GpuDirectSpatialBinnerIntegrationTests'
)
$testMetadata = $null
if (-not [string]::IsNullOrWhiteSpace($EditModeResultsPath)) {
    $testMetadata = Get-NUnitMetadata `
        -Path $EditModeResultsPath `
        -ExpectedIdentities $expectedTestIdentities
}
if ($FormalAcceptanceMode -and
    ($null -eq $testMetadata -or
        $testMetadata.result -cne 'Passed' -or
        $testMetadata.total -le 0 -or
        $testMetadata.passed -ne $testMetadata.total -or
        $testMetadata.failed -ne 0 -or
        $testMetadata.skipped -ne 0 -or
        $testMetadata.inconclusive -ne 0 -or
        @($testMetadata.missingIdentities).Count -ne 0)) {
    $missing = if ($null -eq $testMetadata) {
        'metadata unavailable'
    }
    else { @($testMetadata.missingIdentities) -join ', ' }
    throw "Formal benchmark requires complete focused tests; missing: $missing"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $projectRoot (
        "Reports\GpuDrivenInstance\$MatrixPreset-$stamp")
}
elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ($FormalAcceptanceMode -and (Test-Path -LiteralPath $outputRoot)) {
    if ($null -ne (
            Get-ChildItem -LiteralPath $outputRoot -Force |
                Select-Object -First 1)) {
        throw 'Formal report directory must be new or empty.'
    }
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($PlayerPath)) {
    $PlayerPath = Join-Path $projectRoot (
        'Builds\GpuDrivenInstanceBenchmark\' +
        'GpuDrivenInstanceBenchmark.exe')
}
elseif (-not [IO.Path]::IsPathRooted($PlayerPath)) {
    $PlayerPath = Join-Path $projectRoot $PlayerPath
}
$resolvedPlayerPath = [IO.Path]::GetFullPath($PlayerPath)
if ($outputRoot.StartsWith(
        (Split-Path -Parent $resolvedPlayerPath) +
            [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Report output may not be inside the Player payload.'
}

$benchmarkRoot =
    Join-Path $projectRoot 'Assets\GpuDrivenInstanceBenchmark'
$instanceRuntime =
    Join-Path $projectRoot 'Packages\com.summit.gpu-driven-instances\Runtime'
$binningRuntime =
    Join-Path $projectRoot 'Packages\com.summit.gpu-direct-binning\Runtime'
$primitiveRuntime =
    Join-Path $projectRoot 'Packages\com.summit.gpu-primitives\Runtime'
$timestampRuntime =
    Join-Path $projectRoot 'Packages\com.summit.gpu-timestamps\Runtime'
$runtimeShaderPath = Join-Path $instanceRuntime (
    'Resources\GpuDrivenInstances\GpuDrivenInstances.compute')
$binningShaderPath = Join-Path $binningRuntime (
    'Resources\GpuDirectBinning\GpuDirectBinning.compute')
$timestampDllPath = Join-Path $timestampRuntime (
    'Plugins\x86_64\SummitGpuTimestamps.dll')
$buildScriptPath = Join-Path $benchmarkRoot (
    'Editor\GpuDrivenInstanceBenchmarkBuild.cs')
foreach ($path in @(
        $runtimeShaderPath,
        $binningShaderPath,
        $timestampDllPath,
        $buildScriptPath,
        $provenanceModulePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required benchmark input is missing: $path"
    }
}

$runtimeApiFiles = [IO.FileInfo[]]@(
    Get-ChildItem -LiteralPath $instanceRuntime -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.asmdef') }
    Get-ChildItem -LiteralPath $binningRuntime -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.asmdef') }
    Get-ChildItem -LiteralPath $primitiveRuntime -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.asmdef') }
)
$sourceFiles = [IO.FileInfo[]]@(
    Get-ChildItem -LiteralPath $benchmarkRoot -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.compute', '.asmdef') }
    Get-ChildItem -LiteralPath $instanceRuntime -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.compute', '.asmdef') }
    Get-ChildItem -LiteralPath $binningRuntime -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.compute', '.asmdef') }
    Get-ChildItem -LiteralPath $primitiveRuntime -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.compute', '.asmdef') }
    Get-ChildItem -LiteralPath $timestampRuntime -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.asmdef') }
    Get-Item -LiteralPath $timestampDllPath
    Get-Item -LiteralPath $PSCommandPath
    Get-Item -LiteralPath $provenanceModulePath
    Get-Item -LiteralPath $projectVersionPath
    Get-Item -LiteralPath (Join-Path $projectRoot 'Packages\manifest.json')
    Get-Item -LiteralPath (Join-Path $projectRoot 'Packages\packages-lock.json')
)
$sourceSnapshotSha256 =
    Get-CombinedSha256 -Files $sourceFiles -RelativeTo $projectRoot
$runtimeShaderSha256 =
    (Get-FileHash -LiteralPath $runtimeShaderPath -Algorithm SHA256).Hash
$binningShaderSha256 =
    (Get-FileHash -LiteralPath $binningShaderPath -Algorithm SHA256).Hash
$runtimeApiSha256 =
    Get-CombinedSha256 -Files $runtimeApiFiles -RelativeTo $projectRoot

$scenarios = if ($FormalAcceptanceMode) {
    $formalScenarios
}
else {
    @([ordered]@{
        scenarioId = $ScenarioId
        visibility = $Visibility
        instanceCount = $InstanceCount
        viewCount = $ViewCount
        seed = $Seed
    })
}

$videoControllers = @()
$videoControllerError = ''
try {
    $videoControllers = @(
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
    $videoControllerError = $_.Exception.Message
}

$runnerConfig = [ordered]@{
    schemaVersion = 1
    suite = 'summit.gpu-driven-instance'
    formalAcceptanceMode = [bool]$FormalAcceptanceMode
    formalContract = $formalContract
    matrixPreset = $MatrixPreset
    scenarios = $scenarios
    editModeResults = $testMetadata
    projectRoot = $projectRoot
    projectUnityVersion = $projectUnityVersion
    unityPath = [IO.Path]::GetFullPath($UnityPath)
    playerPath = $resolvedPlayerPath
    outputDirectory = $outputRoot
    deviceIndex = $DeviceIndex
    superRounds = $SuperRounds
    warmupFrames = $WarmupFrames
    sampleFrames = $SampleFrames
    cooldownFrames = $CooldownFrames
    dispatchesPerFrame = $DispatchesPerFrame
    validationTimeoutSeconds = $ValidationTimeoutSeconds
    gitCommit = $gitCommit
    gitBranch = [string]$gitStart.branch
    gitStart = $gitStart
    sourceSnapshotSha256 = $sourceSnapshotSha256
    runtimeShaderSha256 = $runtimeShaderSha256
    binningShaderSha256 = $binningShaderSha256
    runtimeApiSha256 = $runtimeApiSha256
    timestampNativeDllSha256 =
        (Get-FileHash -LiteralPath $timestampDllPath -Algorithm SHA256).Hash
    windowsVideoControllers = $videoControllers
    windowsVideoControllerInventoryError = $videoControllerError
    playerPayload = $null
    playerPayloadStableThroughRun = $false
    sourceHashesStableAcrossBuild = $false
    playerRuns = @()
    gitFinal = $null
    formalContractSatisfied = $false
    runnerConfigFinalized = $false
    startedUtc = (Get-Date).ToUniversalTime().ToString('o')
    finalizedUtc = ''
}
$runnerConfigPath = Join-Path $outputRoot 'runner-config.json'
$runnerConfig | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath $runnerConfigPath -Encoding utf8
if ($null -ne $testMetadata) {
    Copy-Item -LiteralPath $testMetadata.path `
        -Destination (Join-Path $outputRoot 'editmode-results.xml') -Force
}

$buildLog = Join-Path $outputRoot 'unity-build.log'
if (-not $SkipBuild) {
    New-Item -ItemType Directory -Force `
        -Path (Split-Path -Parent $resolvedPlayerPath) | Out-Null
    $buildArguments = @(
        '-batchmode',
        '-nographics',
        '-quit',
        '-projectPath', (Quote-ProcessArgument $projectRoot),
        '-executeMethod', 'GpuDrivenInstanceBenchmarkBuild.PerformBuild',
        '-gpu-driven-instance-player-path',
            (Quote-ProcessArgument $resolvedPlayerPath),
        '-logFile', (Quote-ProcessArgument $buildLog)
    )
    $build = Start-Process `
        -FilePath $UnityPath `
        -ArgumentList $buildArguments `
        -WorkingDirectory $projectRoot `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    if ($build.ExitCode -ne 0) {
        throw "Unity Player build failed; see $buildLog"
    }
}
if (-not (Test-Path -LiteralPath $resolvedPlayerPath -PathType Leaf)) {
    throw "Benchmark Player is missing: $resolvedPlayerPath"
}
$postBuildGit = Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
if ($postBuildGit.head -ine $gitCommit -or [bool]$postBuildGit.dirty) {
    throw 'Git state changed during the Player build.'
}
$postBuildSourceHash =
    Get-CombinedSha256 -Files $sourceFiles -RelativeTo $projectRoot
$sourceStable = $postBuildSourceHash -ceq $sourceSnapshotSha256
if (-not $sourceStable) {
    throw 'Benchmark source hashes changed during the Player build.'
}
$initialPayload =
    Get-GpuBenchmarkPlayerPayload -PlayerPath $resolvedPlayerPath
$runnerConfig['sourceHashesStableAcrossBuild'] = $sourceStable
$runnerConfig['playerPayload'] = $initialPayload
$initialPayload | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath (
        Join-Path $outputRoot 'player-payload-manifest.json') -Encoding utf8

$matrixRows = [Collections.Generic.List[object]]::new()
$summaryRows = [Collections.Generic.List[object]]::new()
$playerRuns = [Collections.Generic.List[object]]::new()
foreach ($scenario in $scenarios) {
    if ([int64]$scenario.instanceCount * [int64]$scenario.viewCount -gt
        16776960L) {
        throw "Scenario '$($scenario.scenarioId)' exceeds capacity."
    }
    $scenarioRoot = Join-Path $outputRoot $scenario.scenarioId
    New-Item -ItemType Directory -Path $scenarioRoot -Force | Out-Null
    $playerLog = Join-Path $scenarioRoot 'player.log'
    $arguments = @(
        '-batchmode',
        '-force-d3d12',
        '-force-device-index', [string]$DeviceIndex,
        '-screen-fullscreen', '0',
        '-screen-width', '640',
        '-screen-height', '360',
        '-gpu-driven-instance-benchmark',
        '-gpu-driven-instance-report-dir',
            (Quote-ProcessArgument $scenarioRoot),
        '-gpu-driven-instance-scenario-id',
            (Quote-ProcessArgument $scenario.scenarioId),
        '-gpu-driven-instance-visibility',
            (Quote-ProcessArgument $scenario.visibility),
        '-gpu-driven-instance-super-rounds', [string]$SuperRounds,
        '-gpu-driven-instance-warmup-frames', [string]$WarmupFrames,
        '-gpu-driven-instance-sample-frames', [string]$SampleFrames,
        '-gpu-driven-instance-cooldown-frames', [string]$CooldownFrames,
        '-gpu-driven-instance-instance-count',
            [string]$scenario.instanceCount,
        '-gpu-driven-instance-view-count', [string]$scenario.viewCount,
        '-gpu-driven-instance-seed', [string]$scenario.seed,
        '-gpu-driven-instance-dispatches-per-frame',
            [string]$DispatchesPerFrame,
        '-gpu-driven-instance-validation-timeout-seconds',
            [string]$ValidationTimeoutSeconds,
        '-gpu-driven-instance-require-complete-gpu-timings', '1',
        '-gpu-driven-instance-build-commit', $gitCommit,
        '-gpu-driven-instance-runtime-shader-sha256',
            $runtimeShaderSha256,
        '-gpu-driven-instance-reference-shader-sha256',
            $binningShaderSha256,
        '-gpu-driven-instance-runtime-api-sha256',
            $runtimeApiSha256,
        '-logFile', (Quote-ProcessArgument $playerLog)
    )
    Write-Host (
        "Running '$($scenario.scenarioId)': " +
        "$($scenario.visibility), N=$($scenario.instanceCount), " +
        "views=$($scenario.viewCount)")
    $player = Start-Process `
        -FilePath $resolvedPlayerPath `
        -ArgumentList $arguments `
        -WorkingDirectory $projectRoot `
        -WindowStyle Hidden `
        -PassThru
    if (-not $player.WaitForExit($PlayerTimeoutMinutes * 60 * 1000)) {
        $player.Kill()
        $player.WaitForExit()
        throw "Scenario '$($scenario.scenarioId)' timed out."
    }
    if ($player.ExitCode -ne 0) {
        throw "Scenario '$($scenario.scenarioId)' failed; see $playerLog"
    }

    $runSummaryPath = Join-Path $scenarioRoot 'run-summary.txt'
    $configPath = Join-Path $scenarioRoot 'config.json'
    $rawPath = Join-Path $scenarioRoot 'raw-frames.csv'
    $validationPath = Join-Path $scenarioRoot 'validation.csv'
    foreach ($path in @(
            $runSummaryPath, $configPath, $rawPath, $validationPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Scenario output is incomplete: $path"
        }
    }
    $run = Read-KeyValueFile -Path $runSummaryPath
    if ([int]$run.passed -ne 1 -or $run.status -cne 'completed' -or
        [int]$run.validationFailures -ne 0 -or
        [int]$run.gpuRegionTimingComplete -ne 1 -or
        [int]$run.nativeTimestampPendingRows -ne 0) {
        throw "Scenario quality gate failed: $($scenario.scenarioId)"
    }
    $config = Get-Content -LiteralPath $configPath -Raw |
        ConvertFrom-Json
    if ([string]$config.buildCommit -cne $gitCommit -or
        [string]$config.runtimeShaderSha256 -cne
            $runtimeShaderSha256 -or
        [string]$config.referenceShaderSha256 -cne
            $binningShaderSha256 -or
        [string]$config.runtimeApiSha256 -cne $runtimeApiSha256) {
        throw "Scenario provenance mismatch: $($scenario.scenarioId)"
    }
    $validation = @(Import-Csv -LiteralPath $validationPath)
    if ($validation.Count -ne 4 -or
        @($validation | Where-Object { [int]$_.passed -ne 1 }).Count -ne 0) {
        throw "Scenario validation failed: $($scenario.scenarioId)"
    }
    $raw = @(Import-Csv -LiteralPath $rawPath)
    $expectedRows = ($SuperRounds * 4 + 2) * $SampleFrames
    if ($raw.Count -ne $expectedRows -or
        @($raw | Where-Object {
            $_.nativeTimestampStatus -cne 'ready'
        }).Count -ne 0) {
        throw "Scenario timestamp rows are incomplete: $($scenario.scenarioId)"
    }
    $baselineRows = @($raw | Where-Object {
        $_.variant -ceq 'culled-tail-portable'
    })
    $optimizedRows = @($raw | Where-Object {
        $_.variant -ceq 'visible-only-discard-key-portable'
    })
    $expectedCaseRows = $SuperRounds * 2 * $SampleFrames
    if ($baselineRows.Count -ne $expectedCaseRows -or
        $optimizedRows.Count -ne $expectedCaseRows) {
        throw "Scenario case sample counts differ: $($scenario.scenarioId)"
    }
    $baselineTimes = [double[]]@(
        $baselineRows | ForEach-Object { Number $_.gpuRegionElapsedMs })
    $optimizedTimes = [double[]]@(
        $optimizedRows | ForEach-Object { Number $_.gpuRegionElapsedMs })
    $pairSpeedups = [Collections.Generic.List[double]]::new()
    foreach ($pairIndex in 1..($SuperRounds * 2)) {
        $pairBaseline = [double[]]@(
            $baselineRows |
                Where-Object { [int]$_.pairIndex -eq $pairIndex } |
                ForEach-Object { Number $_.gpuRegionElapsedMs })
        $pairOptimized = [double[]]@(
            $optimizedRows |
                Where-Object { [int]$_.pairIndex -eq $pairIndex } |
                ForEach-Object { Number $_.gpuRegionElapsedMs })
        if ($pairBaseline.Count -ne $SampleFrames -or
            $pairOptimized.Count -ne $SampleFrames) {
            throw "Pair $pairIndex is incomplete: $($scenario.scenarioId)"
        }
        $pairSpeedups.Add(
            (ImprovementPercent `
                -Baseline (Mean $pairBaseline) `
                -Optimized (Mean $pairOptimized)))
    }
    $baselineMean = Mean $baselineTimes
    $optimizedMean = Mean $optimizedTimes
    $pairMedian = Percentile ([double[]]$pairSpeedups) 0.50
    $pairMinimum =
        [double](($pairSpeedups | Measure-Object -Minimum).Minimum)
    $pairMaximum =
        [double](($pairSpeedups | Measure-Object -Maximum).Maximum)
    $decision = if ($pairMedian -ge 1.0 -and $pairMinimum -gt 0.0) {
        'material-improvement'
    }
    elseif ([Math]::Abs($pairMedian) -lt 1.0) {
        'parity'
    }
    else {
        'regression-or-unstable'
    }
    $summaryRows.Add([pscustomobject][ordered]@{
        scenarioId = $scenario.scenarioId
        visibility = $scenario.visibility
        visibilityPercent =
            [int]([string]$scenario.visibility).Substring(7)
        instanceCount = $scenario.instanceCount
        viewCount = $scenario.viewCount
        pairCount = $SuperRounds * 2
        samplesPerVariant = $expectedCaseRows
        baselineGpuMeanMs = $baselineMean
        optimizedGpuMeanMs = $optimizedMean
        meanSpeedupPercent =
            ImprovementPercent $baselineMean $optimizedMean
        baselineGpuP50Ms = Percentile $baselineTimes 0.50
        optimizedGpuP50Ms = Percentile $optimizedTimes 0.50
        p50SpeedupPercent = ImprovementPercent `
            (Percentile $baselineTimes 0.50) `
            (Percentile $optimizedTimes 0.50)
        baselineGpuP95Ms = Percentile $baselineTimes 0.95
        optimizedGpuP95Ms = Percentile $optimizedTimes 0.95
        p95SpeedupPercent = ImprovementPercent `
            (Percentile $baselineTimes 0.95) `
            (Percentile $optimizedTimes 0.95)
        pairedMedianSpeedupPercent = $pairMedian
        pairedMinSpeedupPercent = $pairMinimum
        pairedMaxSpeedupPercent = $pairMaximum
        decision = $decision
        validationRows = $validation.Count
        validationFailures = 0
        nativeTimestampReadyRows = [int]$run.nativeTimestampReadyRows
        processId = [int]$run.processId
    })
    $matrixRows.Add([pscustomobject][ordered]@{
        scenarioId = $scenario.scenarioId
        visibility = $scenario.visibility
        instanceCount = $scenario.instanceCount
        viewCount = $scenario.viewCount
        seed = $scenario.seed
        processId = [int]$run.processId
        status = $run.status
        passed = $run.passed
        rawSampleCount = $run.rawSampleCount
    })
    $playerRuns.Add([ordered]@{
        scenarioId = $scenario.scenarioId
        processId = [int]$run.processId
        exitCode = $player.ExitCode
        reportDirectory = $scenarioRoot
        playerLog = $playerLog
    })
}

$matrixRows | Export-Csv -LiteralPath (
    Join-Path $outputRoot 'matrix.csv') -NoTypeInformation -Encoding utf8
$summaryRows | Export-Csv -LiteralPath (
    Join-Path $outputRoot 'matrix-summary.csv') `
    -NoTypeInformation -Encoding utf8

$reportLines = [Collections.Generic.List[string]]::new()
$reportLines.Add('# GPU-driven visible-only benchmark')
$reportLines.Add('')
$reportLines.Add(
    "Commit: ``$gitCommit``  ")
$reportLines.Add(
    "Protocol: same-process paired ABBA/BAAB, native D3D12 timestamps, " +
    "$SampleFrames samples per block.  ")
$reportLines.Add(
    'Correctness: CPU oracle before and after measurement; no timed readback.')
$reportLines.Add('')
$reportLines.Add(
    '| Visible | Baseline mean (ms) | Visible-only mean (ms) | ' +
    'Mean speedup | Paired median | Pair range | Decision |')
$reportLines.Add('|---:|---:|---:|---:|---:|---:|:---|')
foreach ($row in $summaryRows) {
    $reportLines.Add(
        (('| {0}% | {1:F4} | {2:F4} | {3:F2}% | {4:F2}% | ' +
          '{5:F2}% to {6:F2}% | {7} |') -f
            $row.visibilityPercent,
            $row.baselineGpuMeanMs,
            $row.optimizedGpuMeanMs,
            $row.meanSpeedupPercent,
            $row.pairedMedianSpeedupPercent,
            $row.pairedMinSpeedupPercent,
            $row.pairedMaxSpeedupPercent,
            $row.decision))
}
$reportLines.Add('')
$material = @($summaryRows | Where-Object {
    [string]$_.decision -ceq 'material-improvement'
})
if ($material.Count -eq 0) {
    $reportLines.Add(
        'Decision: retain `CulledTail` as the default; this matrix did not ' +
        'show a material, consistently positive paired result.')
}
else {
    $maxVisibility = ($material |
        Measure-Object -Property visibilityPercent -Maximum).Maximum
    $reportLines.Add(
        "Decision evidence: `VisibleOnly` had at least 1% paired-median " +
        "speedup with every pair positive through the measured " +
        "$maxVisibility% visibility cell. Cells below 1% are classified " +
        'as parity. The public API remains explicit; no runtime policy is ' +
        'inferred outside this matrix.')
}
$reportLines | Set-Content -LiteralPath (
    Join-Path $outputRoot 'BENCHMARK_REPORT.md') -Encoding utf8

$finalGit = Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
$finalPayload =
    Get-GpuBenchmarkPlayerPayload -PlayerPath $resolvedPlayerPath
$payloadStable =
    [string]$initialPayload.sha256 -ceq [string]$finalPayload.sha256 -and
    [int]$initialPayload.fileCount -eq [int]$finalPayload.fileCount -and
    [int64]$initialPayload.lengthBytes -eq [int64]$finalPayload.lengthBytes
if ($finalGit.head -ine $gitCommit -or [bool]$finalGit.dirty) {
    throw 'Git state changed during benchmark execution.'
}
if (-not $payloadStable) {
    throw 'Player payload changed during benchmark execution.'
}
$runnerConfig['gitFinal'] = $finalGit
$runnerConfig['playerPayload'] = $finalPayload
$runnerConfig['playerPayloadStableThroughRun'] = $payloadStable
$runnerConfig['playerRuns'] = @($playerRuns)
$runnerConfig['formalContractSatisfied'] = [bool]$FormalAcceptanceMode
$runnerConfig['runnerConfigFinalized'] = $true
$runnerConfig['finalizedUtc'] =
    (Get-Date).ToUniversalTime().ToString('o')
$runnerConfig | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath $runnerConfigPath -Encoding utf8
$finalPayload | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath (
        Join-Path $outputRoot 'player-payload-manifest.json') -Encoding utf8

Write-Host "Completed GPU-driven instance benchmark: $outputRoot"
Write-Host "Summary: $(Join-Path $outputRoot 'matrix-summary.csv')"
