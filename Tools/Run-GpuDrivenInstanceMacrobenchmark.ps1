[CmdletBinding()]
param(
    [string]$UnityPath =
        'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe',
    [string]$OutputDirectory,
    [string]$PlayerPath,
    [ValidateRange(0, 16)]
    [int]$DeviceIndex = 0,
    [ValidateSet('smoke-10k-v1-g1', 'formal-primary-v1')]
    [string]$MatrixPreset = 'smoke-10k-v1-g1',
    [ValidateRange(1, 4)]
    [int]$SuperRounds = 2,
    [ValidateRange(5, 1800)]
    [int]$WarmupFrames = 30,
    [ValidateRange(30, 7200)]
    [int]$SampleFrames = 60,
    [ValidateRange(0, 600)]
    [int]$CooldownFrames = 5,
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

$cpuVariant = 'cpu-burst-engine-native'
$gpuVariant = 'gpu-visible-only-engine-indirect'
$visibility = 'visible25'
$seed = 20260830
$formalContract = [ordered]@{
    unityVersion = '6000.5.2f1'
    deviceIndex = 0
    matrixPreset = 'formal-primary-v1'
    superRounds = 2
    warmupFrames = 60
    sampleFrames = 900
    cooldownFrames = 15
    visibility = $visibility
    seed = $seed
    cpuSubmissionP95MinimumImprovementPercent = 20.0
    cpuSubmissionP95MinimumImprovementMilliseconds = 0.20
    requiredCpuSubmissionCells = 3
    nativeGpuRegionP99MaximumRegressionPercent = 5.0
}
$smokeScenario = [ordered]@{
    scenarioId = 'smoke-n10000-v1-g1-visible25'
    visibility = $visibility
    instanceCount = 10000
    viewCount = 1
    drawGroupCount = 1
    seed = $seed
}
$formalScenarios = @(
    [ordered]@{
        scenarioId = 'primary-n10000-v1-g1-visible25'
        visibility = $visibility
        instanceCount = 10000
        viewCount = 1
        drawGroupCount = 1
        seed = $seed
    },
    [ordered]@{
        scenarioId = 'primary-n100000-v1-g1-visible25'
        visibility = $visibility
        instanceCount = 100000
        viewCount = 1
        drawGroupCount = 1
        seed = $seed
    },
    [ordered]@{
        scenarioId = 'primary-n10000-v4-g8-visible25'
        visibility = $visibility
        instanceCount = 10000
        viewCount = 4
        drawGroupCount = 8
        seed = $seed
    },
    [ordered]@{
        scenarioId = 'primary-n100000-v4-g8-visible25'
        visibility = $visibility
        instanceCount = 100000
        viewCount = 4
        drawGroupCount = 8
        seed = $seed
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
        foreach ($file in @($Files | Sort-Object FullName -Unique)) {
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

function MetricValues {
    param(
        [Parameter(Mandatory = $true)][object[]]$Rows,
        [Parameter(Mandatory = $true)][string]$Property,
        [switch]$RequirePositive
    )
    $values = [Collections.Generic.List[double]]::new()
    foreach ($row in $Rows) {
        $value = Number $row.$Property
        if ([double]::IsNaN($value) -or [double]::IsInfinity($value) -or
            ($RequirePositive -and $value -le 0.0)) {
            throw "Metric '$Property' contains an invalid value: $value"
        }
        $values.Add($value)
    }
    return [double[]]$values.ToArray()
}

function Mean {
    param([Parameter(Mandatory = $true)][double[]]$Values)
    if ($Values.Count -eq 0) {
        throw 'Cannot average an empty array.'
    }
    return [double](($Values | Measure-Object -Average).Average)
}

function Percentile {
    param(
        [Parameter(Mandatory = $true)][double[]]$Values,
        [Parameter(Mandatory = $true)][double]$Probability
    )
    if ($Values.Count -eq 0) {
        throw 'Cannot rank an empty array.'
    }
    $sorted = [double[]]@($Values | Sort-Object)
    $rank = ($sorted.Count - 1) * $Probability
    $low = [int][Math]::Floor($rank)
    $high = [int][Math]::Ceiling($rank)
    if ($low -eq $high) {
        return $sorted[$low]
    }
    return $sorted[$low] +
        ($sorted[$high] - $sorted[$low]) * ($rank - $low)
}

function OptionalPercentile {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][double[]]$Values,
        [Parameter(Mandatory = $true)][double]$Probability
    )
    if ($Values.Count -eq 0) {
        return 'unavailable'
    }
    return Percentile -Values $Values -Probability $Probability
}

function ImprovementPercent {
    param([double]$Baseline, [double]$Optimized)
    if ($Baseline -le 0.0) {
        return [double]::NaN
    }
    return 100.0 * ($Baseline - $Optimized) / $Baseline
}

function RegressionPercent {
    param([double]$Baseline, [double]$Optimized)
    if ($Baseline -le 0.0) {
        return [double]::PositiveInfinity
    }
    return 100.0 * ($Optimized - $Baseline) / $Baseline
}

function Test-StringSequence {
    param([string[]]$Actual, [string[]]$Expected)
    if ($Actual.Count -ne $Expected.Count) {
        return $false
    }
    for ($index = 0; $index -lt $Actual.Count; $index++) {
        if ([string]$Actual[$index] -cne [string]$Expected[$index]) {
            return $false
        }
    }
    return $true
}

function Assert-MacroSchedule {
    param(
        [Parameter(Mandatory = $true)][object[]]$Raw,
        [Parameter(Mandatory = $true)][int]$SamplesPerBlock,
        [Parameter(Mandatory = $true)][int]$ExpectedProcessId,
        [Parameter(Mandatory = $true)][string]$ScenarioId
    )
    $expectedVariants = [string[]]@(
        'empty-render-frame',
        $cpuVariant, $gpuVariant, $gpuVariant, $cpuVariant,
        $gpuVariant, $cpuVariant, $cpuVariant, $gpuVariant,
        'empty-render-frame')
    $expectedBlockTypes = [string[]]@(
        'control-pre',
        'measurement', 'measurement', 'measurement', 'measurement',
        'measurement', 'measurement', 'measurement', 'measurement',
        'control-post')
    $groups = @(
        $Raw |
            Group-Object blockIndex |
            Sort-Object { [int]$_.Name })
    if ($groups.Count -ne $expectedVariants.Count) {
        throw "ABBA/BAAB block count is invalid: $ScenarioId"
    }
    for ($index = 0; $index -lt $groups.Count; $index++) {
        $group = $groups[$index]
        $first = $group.Group[0]
        if ($group.Count -ne $SamplesPerBlock -or
            [int]$group.Name -ne ($index + 1) -or
            [string]$first.variant -cne $expectedVariants[$index] -or
            [string]$first.blockType -cne $expectedBlockTypes[$index]) {
            throw "ABBA/BAAB schedule is invalid at block $($index + 1): $ScenarioId"
        }
    }
    $processIds = @($Raw | Select-Object -ExpandProperty processId -Unique)
    if ($processIds.Count -ne 1 -or
        [int]$processIds[0] -ne $ExpectedProcessId) {
        throw "Variants were not measured in one PID: $ScenarioId"
    }
}

if ($FormalAcceptanceMode) {
    $violations = [Collections.Generic.List[string]]::new()
    foreach ($entry in @(
            @('DeviceIndex', $DeviceIndex, $formalContract.deviceIndex),
            @('SuperRounds', $SuperRounds, $formalContract.superRounds),
            @('WarmupFrames', $WarmupFrames,
                $formalContract.warmupFrames),
            @('SampleFrames', $SampleFrames,
                $formalContract.sampleFrames),
            @('CooldownFrames', $CooldownFrames,
                $formalContract.cooldownFrames))) {
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
    if ($SkipBuild) {
        $violations.Add('SkipBuild is forbidden.')
    }
    if ([string]::IsNullOrWhiteSpace($EditModeResultsPath)) {
        $violations.Add('EditModeResultsPath is required.')
    }
    if ($violations.Count -ne 0) {
        throw "Formal acceptance contract rejected:`n$($violations -join "`n")"
    }
}
else {
    if ($MatrixPreset -cne 'smoke-10k-v1-g1') {
        throw "MatrixPreset='formal-primary-v1' requires FormalAcceptanceMode."
    }
    if ($SuperRounds -ne 2) {
        throw 'Smoke protocol requires SuperRounds=2 for ABBA/BAAB.'
    }
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
    'Summit.GpuDrivenInstance.Benchmark.Tests.GpuDrivenInstanceMacrobenchmarkCpuBackendTests',
    'Summit.GpuDrivenInstance.Benchmark.Tests.GpuDrivenInstanceFrameTimingCollectorTests',
    'Summit.GpuDrivenInstances.Tests.Editor.dll',
    'Summit.GpuDrivenInstances.Tests.GpuDrivenInstancePipelineContractTests',
    'Summit.GpuDrivenInstances.Tests.GpuDrivenInstancePipelineIntegrationTests',
    'Summit.GpuDirectBinning.Tests.Editor.dll',
    'Summit.GpuDirectBinning.Tests.GpuDirectSpatialBinnerContractTests',
    'Summit.GpuDirectBinning.Tests.GpuDirectSpatialBinnerIntegrationTests')
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
    else {
        @($testMetadata.missingIdentities) -join ', '
    }
    throw "Formal benchmark requires complete focused tests; missing: $missing"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $projectRoot (
        "Reports\GpuDrivenInstanceMacrobenchmark\$MatrixPreset-$stamp")
}
elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) {
    if ($null -ne (
            Get-ChildItem -LiteralPath $outputRoot -Force |
                Select-Object -First 1)) {
        throw 'Benchmark report directory must be new or empty.'
    }
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($PlayerPath)) {
    $PlayerPath = Join-Path $projectRoot (
        'Builds\GpuDrivenInstanceMacrobenchmark\' +
        'GpuDrivenInstanceMacrobenchmark.exe')
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
        $playerPayloadRoot +
            [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Report output may not be inside the Player payload.'
}

$benchmarkRoot =
    Join-Path $projectRoot 'Assets\GpuDrivenInstanceBenchmark'
$benchmarkRuntime = Join-Path $benchmarkRoot 'Runtime'
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
$macroShaderPath = Join-Path $benchmarkRuntime (
    'Resources\GpuDrivenInstanceBenchmark\GpuDrivenInstanceMacro.shader')
$timestampDllPath = Join-Path $timestampRuntime (
    'Plugins\x86_64\SummitGpuTimestamps.dll')
$buildScriptPath = Join-Path $benchmarkRoot (
    'Editor\GpuDrivenInstanceMacrobenchmarkBuild.cs')
$runnerTestPath = Join-Path $PSScriptRoot (
    'Tests\Test-GpuDrivenInstanceMacrobenchmarkProvenance.ps1')
foreach ($path in @(
        $runtimeShaderPath,
        $macroShaderPath,
        $timestampDllPath,
        $buildScriptPath,
        $runnerTestPath,
        $provenanceModulePath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required benchmark input is missing: $path"
    }
}

$runtimeApiFiles = [IO.FileInfo[]]@(
    Get-ChildItem -LiteralPath $benchmarkRuntime -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.asmdef') }
    Get-ChildItem -LiteralPath $instanceRuntime -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.asmdef') }
    Get-ChildItem -LiteralPath $binningRuntime -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.asmdef') }
    Get-ChildItem -LiteralPath $primitiveRuntime -Recurse -File |
        Where-Object { $_.Extension -in @('.cs', '.asmdef') })
$sourceFiles = [IO.FileInfo[]]@(
    Get-ChildItem -LiteralPath $benchmarkRoot -Recurse -File |
        Where-Object {
            $_.Extension -in @('.cs', '.compute', '.shader', '.asmdef')
        }
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
    Get-Item -LiteralPath $runnerTestPath
    Get-Item -LiteralPath $provenanceModulePath
    Get-Item -LiteralPath $projectVersionPath
    Get-Item -LiteralPath (Join-Path $projectRoot 'Packages\manifest.json')
    Get-Item -LiteralPath (Join-Path $projectRoot 'Packages\packages-lock.json'))
$sourceSnapshotSha256 =
    Get-CombinedSha256 -Files $sourceFiles -RelativeTo $projectRoot
$runtimeShaderSha256 =
    (Get-FileHash -LiteralPath $runtimeShaderPath -Algorithm SHA256).Hash
$macroShaderSha256 =
    (Get-FileHash -LiteralPath $macroShaderPath -Algorithm SHA256).Hash
$runtimeApiSha256 =
    Get-CombinedSha256 -Files $runtimeApiFiles -RelativeTo $projectRoot
$scenarios = if ($FormalAcceptanceMode) {
    $formalScenarios
}
else {
    @($smokeScenario)
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
    suite = 'summit.gpu-driven-instance-macro'
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
    validationTimeoutSeconds = $ValidationTimeoutSeconds
    gitCommit = $gitCommit
    gitBranch = [string]$gitStart.branch
    gitStart = $gitStart
    sourceSnapshotSha256 = $sourceSnapshotSha256
    runtimeShaderSha256 = $runtimeShaderSha256
    macroShaderSha256 = $macroShaderSha256
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
    evidenceValid = $false
    performanceDecisionPassed = $false
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
        '-executeMethod',
            'GpuDrivenInstanceMacrobenchmarkBuild.PerformBuild',
        '-gpu-driven-instance-macro-player-path',
            (Quote-ProcessArgument $resolvedPlayerPath),
        '-logFile', (Quote-ProcessArgument $buildLog))
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
    $scenarioRoot = Join-Path $outputRoot $scenario.scenarioId
    New-Item -ItemType Directory -Path $scenarioRoot -Force | Out-Null
    $playerLog = Join-Path $scenarioRoot 'player.log'
    $arguments = @(
        '-force-d3d12',
        '-force-device-index', [string]$DeviceIndex,
        '-screen-fullscreen', '0',
        '-screen-width', '640',
        '-screen-height', '360',
        '-gpu-driven-instance-macrobenchmark',
        '-gpu-driven-instance-macro-report-dir',
            (Quote-ProcessArgument $scenarioRoot),
        '-gpu-driven-instance-macro-scenario-id',
            (Quote-ProcessArgument $scenario.scenarioId),
        '-gpu-driven-instance-macro-visibility',
            (Quote-ProcessArgument $scenario.visibility),
        '-gpu-driven-instance-macro-super-rounds', [string]$SuperRounds,
        '-gpu-driven-instance-macro-warmup-frames', [string]$WarmupFrames,
        '-gpu-driven-instance-macro-sample-frames', [string]$SampleFrames,
        '-gpu-driven-instance-macro-cooldown-frames', [string]$CooldownFrames,
        '-gpu-driven-instance-macro-instance-count',
            [string]$scenario.instanceCount,
        '-gpu-driven-instance-macro-view-count', [string]$scenario.viewCount,
        '-gpu-driven-instance-macro-draw-group-count',
            [string]$scenario.drawGroupCount,
        '-gpu-driven-instance-macro-seed', [string]$scenario.seed,
        '-gpu-driven-instance-macro-validation-timeout-seconds',
            [string]$ValidationTimeoutSeconds,
        '-gpu-driven-instance-macro-require-complete-gpu-timings', '1',
        '-gpu-driven-instance-macro-require-complete-frame-timings', '1',
        '-gpu-driven-instance-macro-build-commit', $gitCommit,
        '-gpu-driven-instance-macro-runtime-shader-sha256',
            $runtimeShaderSha256,
        '-gpu-driven-instance-macro-render-shader-sha256',
            $macroShaderSha256,
        '-gpu-driven-instance-macro-runtime-api-sha256',
            $runtimeApiSha256,
        '-logFile', (Quote-ProcessArgument $playerLog))
    Write-Host (
        "Running '$($scenario.scenarioId)': " +
        "N=$($scenario.instanceCount), views=$($scenario.viewCount), " +
        "groups=$($scenario.drawGroupCount), $($scenario.visibility)")
    $player = Start-Process `
        -FilePath $resolvedPlayerPath `
        -ArgumentList $arguments `
        -WorkingDirectory $projectRoot `
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
    $devicePath = Join-Path $scenarioRoot 'device.json'
    $rawPath = Join-Path $scenarioRoot 'raw-frames.csv'
    $blockPath = Join-Path $scenarioRoot 'block-summary.csv'
    $validationPath = Join-Path $scenarioRoot 'validation.csv'
    foreach ($path in @(
            $runSummaryPath,
            $configPath,
            $devicePath,
            $rawPath,
            $blockPath,
            $validationPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Scenario output is incomplete: $path"
        }
    }

    $run = Read-KeyValueFile -Path $runSummaryPath
    $expectedRows = ($SuperRounds * 4 + 2) * $SampleFrames
    $expectedCaseRows = $SuperRounds * 2 * $SampleFrames
    if ([int]$run.passed -ne 1 -or
        [string]$run.status -cne 'completed' -or
        [int]$run.rawSampleCount -ne $expectedRows -or
        [int]$run.blockCount -ne ($SuperRounds * 4 + 2) -or
        [int]$run.validationRows -ne 4 -or
        [int]$run.validationFailures -ne 0 -or
        [int]$run.validationMaxAttempts -ne 3 -or
        [int]$run.validationReadbackRetries -lt 0 -or
        [int]$run.imageParityFailures -ne 0 -or
        [int64]$run.measurementReadbackBytes -ne 0 -or
        [int]$run.nativeTimestampReadyRows -ne $expectedRows -or
        [int]$run.frameTimingReadyRows -ne $expectedRows -or
        [int]$run.nativeTimestampWarmupPassed -ne 1 -or
        [int]$run.nativeTimestampAcquireFailures -ne 0 -or
        [int]$run.nativeTimestampResultFailures -ne 0 -or
        [int]$run.nativeTimestampTimeouts -ne 0 -or
        [int]$run.nativeTimestampPendingRows -ne 0 -or
        [int]$run.gpuRegionTimingComplete -ne 1 -or
        [int]$run.frameTimingComplete -ne 1 -or
        [int]$run.completionFencesComplete -ne 1 -or
        [int]$run.timedAllocationFree -ne 1 -or
        [int]$run.mainThreadAllocationRows -ne 0 -or
        [int64]$run.mainThreadAllocatedBytes -ne 0) {
        throw "Scenario quality gate failed: $($scenario.scenarioId)"
    }

    $config = Get-Content -LiteralPath $configPath -Raw |
        ConvertFrom-Json
    $expectedSchedule = [string[]]@(
        'control-pre:empty-render-frame',
        "measurement:$cpuVariant",
        "measurement:$gpuVariant",
        "measurement:$gpuVariant",
        "measurement:$cpuVariant",
        "measurement:$gpuVariant",
        "measurement:$cpuVariant",
        "measurement:$cpuVariant",
        "measurement:$gpuVariant",
        'control-post:empty-render-frame')
    if ([string]$config.suite -cne 'summit.gpu-driven-instance-macro' -or
        [int]$config.processId -ne [int]$run.processId -or
        [string]$config.unityVersion -cne $projectUnityVersion -or
        [string]$config.scenarioId -cne [string]$scenario.scenarioId -or
        [string]$config.visibility -cne [string]$scenario.visibility -or
        [int]$config.superRounds -ne $SuperRounds -or
        [int]$config.warmupFrames -ne $WarmupFrames -or
        [int]$config.sampleFrames -ne $SampleFrames -or
        [int]$config.cooldownFrames -ne $CooldownFrames -or
        [int]$config.instanceCount -ne [int]$scenario.instanceCount -or
        [int]$config.viewCount -ne [int]$scenario.viewCount -or
        [int]$config.drawGroupCount -ne [int]$scenario.drawGroupCount -or
        [int]$config.seed -ne [int]$scenario.seed -or
        [string]$config.buildCommit -cne $gitCommit -or
        [string]$config.visibilityLayout -cne
            'seeded-coprime-permutation-v1' -or
        [string]$config.baselineId -cne
            'cpu-burst-engine-native-v1' -or
        [string]$config.directId -cne
            'gpu-visible-only-engine-indirect-v1' -or
        [string]$config.scheduleContract -cne
            'control-pre;ABBA;BAAB;control-post' -or
        -not [bool]$config.sameProcessPaired -or
        [int]$config.vSyncCount -ne 0 -or
        [int]$config.targetFrameRate -ne -1 -or
        [string]$config.frameTimingAlignment -cne
            'capture-fifo-fixed-four-frame-latency-v1' -or
        [int]$config.frameTimingResultLatencyFrames -ne 4 -or
        [int]$config.measurementReadbackBytesPerFrame -ne 0 -or
        -not [bool]$config.requireCompleteGpuTimings -or
        -not [bool]$config.requireCompleteFrameTimings -or
        [string]$config.runtimeShaderSha256 -cne
            $runtimeShaderSha256 -or
        [string]$config.macroShaderSha256 -cne $macroShaderSha256 -or
        [string]$config.runtimeApiSha256 -cne $runtimeApiSha256 -or
        -not (Test-StringSequence `
            -Actual ([string[]]@($config.schedule)) `
            -Expected $expectedSchedule)) {
        throw "Scenario provenance mismatch: $($scenario.scenarioId)"
    }
    $device = Get-Content -LiteralPath $devicePath -Raw |
        ConvertFrom-Json
    if ([int]$device.processId -ne [int]$run.processId -or
        [string]$device.graphicsDeviceType -cne 'Direct3D12' -or
        -not [bool]$device.supportsComputeShaders -or
        -not [bool]$device.supportsGraphicsFence -or
        -not [bool]$device.supportsInstancing -or
        -not [bool]$device.supportsIndirectArgumentsBuffer -or
        [int]$device.nativeTimestampAbiVersion -ne 2 -or
        (([uint32]$device.nativeTimestampCapabilityFlags -band
                [uint32]0x1F) -ne [uint32]0x1F)) {
        throw "Scenario device contract failed: $($scenario.scenarioId)"
    }

    $validation = @(Import-Csv -LiteralPath $validationPath)
    if ($validation.Count -ne 4 -or
        @($validation | Where-Object {
            [int]$_.passed -ne 1 -or
            [int]$_.attemptCount -lt 1 -or
            [int]$_.attemptCount -gt 3
        }).Count -ne 0) {
        throw "Scenario validation failed: $($scenario.scenarioId)"
    }
    $validationRetryCount = [int](
        ($validation | ForEach-Object {
            [int]$_.attemptCount - 1
        } | Measure-Object -Sum).Sum)
    if ($validationRetryCount -ne
        [int]$run.validationReadbackRetries) {
        throw (
            'Validation retry receipt mismatch: ' +
            "$($scenario.scenarioId)")
    }
    foreach ($phase in @('warmup', 'final')) {
        $phaseRows = @($validation | Where-Object { $_.phase -ceq $phase })
        $cpuValidation = @($phaseRows | Where-Object {
            $_.variant -ceq $cpuVariant
        })
        $gpuValidation = @($phaseRows | Where-Object {
            $_.variant -ceq $gpuVariant
        })
        if ($phaseRows.Count -ne 2 -or
            $cpuValidation.Count -ne 1 -or
            $gpuValidation.Count -ne 1 -or
            [string]::IsNullOrWhiteSpace($cpuValidation[0].imageHash) -or
            [string]$cpuValidation[0].imageHash -cne
                [string]$gpuValidation[0].imageHash -or
            [string]$cpuValidation[0].resultHash -cne
                [string]$gpuValidation[0].resultHash) {
            throw "CPU/GPU validation parity failed: $($scenario.scenarioId)/$phase"
        }
    }

    $raw = @(Import-Csv -LiteralPath $rawPath)
    if ($raw.Count -ne $expectedRows -or
        @($raw | Where-Object {
            $_.nativeTimestampStatus -cne 'ready' -or
            [int]$_.frameTimingValid -ne 1 -or
            [int]$_.frameTimingCaptureLatencyFrames -ne 4 -or
            ([int]$_.frameTimingResultUnityFrame -
                [int]$_.sourceUnityFrame) -ne 4 -or
            [int64]$_.measurementReadbackBytes -ne 0 -or
            [int64]$_.mainThreadAllocatedBytes -ne 0
        }).Count -ne 0) {
        throw "Scenario timing/readback rows are incomplete: $($scenario.scenarioId)"
    }
    $blocks = @(Import-Csv -LiteralPath $blockPath)
    if ($blocks.Count -ne ($SuperRounds * 4 + 2) -or
        @($blocks | Where-Object {
            [int]$_.processId -ne [int]$run.processId -or
            [int]$_.samples -ne $SampleFrames -or
            [int]$_.nativeGpuValidSamples -ne $SampleFrames -or
            [int]$_.frameTimingValidSamples -ne $SampleFrames -or
            [int]$_.mainThreadAllocationRows -ne 0 -or
            [int64]$_.mainThreadAllocatedBytes -ne 0 -or
            [int]$_.fenceSupported -ne 1 -or
            [int]$_.fencePassed -ne 1
        }).Count -ne 0) {
        throw "Scenario block evidence is incomplete: $($scenario.scenarioId)"
    }
    Assert-MacroSchedule `
        -Raw $raw `
        -SamplesPerBlock $SampleFrames `
        -ExpectedProcessId ([int]$run.processId) `
        -ScenarioId $scenario.scenarioId
    $baselineRows = @($raw | Where-Object {
        $_.variant -ceq $cpuVariant
    })
    $optimizedRows = @($raw | Where-Object {
        $_.variant -ceq $gpuVariant
    })
    if ($baselineRows.Count -ne $expectedCaseRows -or
        $optimizedRows.Count -ne $expectedCaseRows) {
        throw "Scenario case sample counts differ: $($scenario.scenarioId)"
    }

    $metricNames = @(
        'totalCpuSubmissionMs',
        'cpuFrameMs',
        'cpuMainThreadFrameMs',
        'nativeGpuRegionMs')
    $baselineMetrics = @{}
    $optimizedMetrics = @{}
    foreach ($metricName in $metricNames) {
        $baselineMetrics[$metricName] = [double[]]@(
            MetricValues `
                -Rows $baselineRows `
                -Property $metricName `
                -RequirePositive)
        $optimizedMetrics[$metricName] = [double[]]@(
            MetricValues `
                -Rows $optimizedRows `
                -Property $metricName `
                -RequirePositive)
    }

    $baselineSubmissionWindowRows = @($baselineRows | Where-Object {
        [int]$_.engineSubmissionWindowValid -eq 1
    })
    $optimizedSubmissionWindowRows = @($optimizedRows | Where-Object {
        [int]$_.engineSubmissionWindowValid -eq 1
    })
    [double[]]$baselineSubmissionWindowValues = @()
    [double[]]$optimizedSubmissionWindowValues = @()
    if ($baselineSubmissionWindowRows.Count -gt 0) {
        $baselineSubmissionWindowValues = [double[]]@(
            MetricValues `
                -Rows $baselineSubmissionWindowRows `
                -Property renderSubmissionWindowMs `
                -RequirePositive)
    }
    if ($optimizedSubmissionWindowRows.Count -gt 0) {
        $optimizedSubmissionWindowValues = [double[]]@(
            MetricValues `
                -Rows $optimizedSubmissionWindowRows `
                -Property renderSubmissionWindowMs `
                -RequirePositive)
    }

    $baselineRenderThreadRows = @($baselineRows | Where-Object {
        [int]$_.cpuRenderThreadFrameValid -eq 1
    })
    $optimizedRenderThreadRows = @($optimizedRows | Where-Object {
        [int]$_.cpuRenderThreadFrameValid -eq 1
    })
    $baselineGpuFrameRows = @($baselineRows | Where-Object {
        [int]$_.gpuFrameValid -eq 1
    })
    $optimizedGpuFrameRows = @($optimizedRows | Where-Object {
        [int]$_.gpuFrameValid -eq 1
    })
    [double[]]$baselineRenderThreadValues = @()
    [double[]]$optimizedRenderThreadValues = @()
    [double[]]$baselineGpuFrameValues = @()
    [double[]]$optimizedGpuFrameValues = @()
    if ($baselineRenderThreadRows.Count -gt 0) {
        $baselineRenderThreadValues = [double[]]@(
            MetricValues $baselineRenderThreadRows `
                cpuRenderThreadFrameMs -RequirePositive)
    }
    if ($optimizedRenderThreadRows.Count -gt 0) {
        $optimizedRenderThreadValues = [double[]]@(
            MetricValues $optimizedRenderThreadRows `
                cpuRenderThreadFrameMs -RequirePositive)
    }
    if ($baselineGpuFrameRows.Count -gt 0) {
        $baselineGpuFrameValues = [double[]]@(
            MetricValues $baselineGpuFrameRows gpuFrameMs `
                -RequirePositive)
    }
    if ($optimizedGpuFrameRows.Count -gt 0) {
        $optimizedGpuFrameValues = [double[]]@(
            MetricValues $optimizedGpuFrameRows gpuFrameMs `
                -RequirePositive)
    }

    $baselineSubmissionP95 = Percentile `
        $baselineMetrics.totalCpuSubmissionMs 0.95
    $optimizedSubmissionP95 = Percentile `
        $optimizedMetrics.totalCpuSubmissionMs 0.95
    $submissionP95Absolute =
        $baselineSubmissionP95 - $optimizedSubmissionP95
    $submissionP95Percent = ImprovementPercent `
        $baselineSubmissionP95 $optimizedSubmissionP95
    $baselineNativeGpuP99 =
        Percentile $baselineMetrics.nativeGpuRegionMs 0.99
    $optimizedNativeGpuP99 =
        Percentile $optimizedMetrics.nativeGpuRegionMs 0.99
    $nativeGpuRegression = RegressionPercent `
        $baselineNativeGpuP99 $optimizedNativeGpuP99
    $cpuGatePassed =
        $submissionP95Percent -ge
            $formalContract.cpuSubmissionP95MinimumImprovementPercent -and
        $submissionP95Absolute -ge
            $formalContract.cpuSubmissionP95MinimumImprovementMilliseconds
    $nativeGpuGatePassed =
        $nativeGpuRegression -le
            $formalContract.nativeGpuRegionP99MaximumRegressionPercent

    $pairImprovements = [Collections.Generic.List[double]]::new()
    foreach ($pairIndex in 1..($SuperRounds * 2)) {
        $pairBaselineRows = @($baselineRows | Where-Object {
            [int]$_.pairIndex -eq $pairIndex
        })
        $pairOptimizedRows = @($optimizedRows | Where-Object {
            [int]$_.pairIndex -eq $pairIndex
        })
        if ($pairBaselineRows.Count -ne $SampleFrames -or
            $pairOptimizedRows.Count -ne $SampleFrames) {
            throw "Pair $pairIndex is incomplete: $($scenario.scenarioId)"
        }
        $pairBaselineP95 = Percentile `
            (MetricValues $pairBaselineRows totalCpuSubmissionMs `
                -RequirePositive) 0.95
        $pairOptimizedP95 = Percentile `
            (MetricValues $pairOptimizedRows totalCpuSubmissionMs `
                -RequirePositive) 0.95
        $pairImprovements.Add(
            (ImprovementPercent $pairBaselineP95 $pairOptimizedP95))
    }

    $summaryRows.Add([pscustomobject][ordered]@{
        scenarioId = $scenario.scenarioId
        visibility = $scenario.visibility
        instanceCount = $scenario.instanceCount
        viewCount = $scenario.viewCount
        drawGroupCount = $scenario.drawGroupCount
        processId = [int]$run.processId
        pairCount = $SuperRounds * 2
        samplesPerVariant = $expectedCaseRows
        cpuSubmissionBaselineP50Ms =
            Percentile $baselineMetrics.totalCpuSubmissionMs 0.50
        cpuSubmissionOptimizedP50Ms =
            Percentile $optimizedMetrics.totalCpuSubmissionMs 0.50
        cpuSubmissionBaselineP95Ms = $baselineSubmissionP95
        cpuSubmissionOptimizedP95Ms = $optimizedSubmissionP95
        cpuSubmissionBaselineP99Ms =
            Percentile $baselineMetrics.totalCpuSubmissionMs 0.99
        cpuSubmissionOptimizedP99Ms =
            Percentile $optimizedMetrics.totalCpuSubmissionMs 0.99
        cpuSubmissionP95ImprovementMs = $submissionP95Absolute
        cpuSubmissionP95ImprovementPercent = $submissionP95Percent
        pairedCpuSubmissionP95ImprovementMedianPercent =
            Percentile ([double[]]$pairImprovements) 0.50
        pairedCpuSubmissionP95ImprovementMinPercent =
            [double](($pairImprovements | Measure-Object -Minimum).Minimum)
        pairedCpuSubmissionP95ImprovementMaxPercent =
            [double](($pairImprovements | Measure-Object -Maximum).Maximum)
        cpuSubmissionGatePassed = [int]$cpuGatePassed
        cpuFrameBaselineP95Ms = Percentile $baselineMetrics.cpuFrameMs 0.95
        cpuFrameOptimizedP95Ms = Percentile $optimizedMetrics.cpuFrameMs 0.95
        cpuFrameBaselineP99Ms = Percentile $baselineMetrics.cpuFrameMs 0.99
        cpuFrameOptimizedP99Ms = Percentile $optimizedMetrics.cpuFrameMs 0.99
        cpuMainThreadBaselineP95Ms =
            Percentile $baselineMetrics.cpuMainThreadFrameMs 0.95
        cpuMainThreadOptimizedP95Ms =
            Percentile $optimizedMetrics.cpuMainThreadFrameMs 0.95
        cpuMainThreadBaselineP99Ms =
            Percentile $baselineMetrics.cpuMainThreadFrameMs 0.99
        cpuMainThreadOptimizedP99Ms =
            Percentile $optimizedMetrics.cpuMainThreadFrameMs 0.99
        cpuRenderThreadBaselineValidSamples =
            $baselineRenderThreadValues.Count
        cpuRenderThreadOptimizedValidSamples =
            $optimizedRenderThreadValues.Count
        cpuRenderThreadBaselineP95Ms =
            OptionalPercentile $baselineRenderThreadValues 0.95
        cpuRenderThreadOptimizedP95Ms =
            OptionalPercentile $optimizedRenderThreadValues 0.95
        cpuRenderThreadBaselineP99Ms =
            OptionalPercentile $baselineRenderThreadValues 0.99
        cpuRenderThreadOptimizedP99Ms =
            OptionalPercentile $optimizedRenderThreadValues 0.99
        renderSubmissionWindowBaselineValidSamples =
            $baselineSubmissionWindowValues.Count
        renderSubmissionWindowOptimizedValidSamples =
            $optimizedSubmissionWindowValues.Count
        renderSubmissionWindowBaselineP95Ms =
            OptionalPercentile $baselineSubmissionWindowValues 0.95
        renderSubmissionWindowOptimizedP95Ms =
            OptionalPercentile $optimizedSubmissionWindowValues 0.95
        renderSubmissionWindowBaselineP99Ms =
            OptionalPercentile $baselineSubmissionWindowValues 0.99
        renderSubmissionWindowOptimizedP99Ms =
            OptionalPercentile $optimizedSubmissionWindowValues 0.99
        gpuFrameBaselineValidSamples = $baselineGpuFrameValues.Count
        gpuFrameOptimizedValidSamples = $optimizedGpuFrameValues.Count
        gpuFrameBaselineP95Ms =
            OptionalPercentile $baselineGpuFrameValues 0.95
        gpuFrameOptimizedP95Ms =
            OptionalPercentile $optimizedGpuFrameValues 0.95
        gpuFrameBaselineP99Ms =
            OptionalPercentile $baselineGpuFrameValues 0.99
        gpuFrameOptimizedP99Ms =
            OptionalPercentile $optimizedGpuFrameValues 0.99
        nativeGpuRegionBaselineP95Ms =
            Percentile $baselineMetrics.nativeGpuRegionMs 0.95
        nativeGpuRegionOptimizedP95Ms =
            Percentile $optimizedMetrics.nativeGpuRegionMs 0.95
        nativeGpuRegionBaselineP99Ms =
            Percentile $baselineMetrics.nativeGpuRegionMs 0.99
        nativeGpuRegionOptimizedP99Ms =
            Percentile $optimizedMetrics.nativeGpuRegionMs 0.99
        nativeGpuRegionP99RegressionPercent = $nativeGpuRegression
        nativeGpuRegionP99GatePassed = [int]$nativeGpuGatePassed
        baselineRenderApiCallsMean = Mean (
            MetricValues $baselineRows renderApiCalls)
        optimizedRenderApiCallsMean = Mean (
            MetricValues $optimizedRows renderApiCalls)
        baselineExplicitBufferUploadBytesMean = Mean (
            MetricValues $baselineRows explicitBufferUploadBytes)
        optimizedExplicitBufferUploadBytesMean = Mean (
            MetricValues $optimizedRows explicitBufferUploadBytes)
        baselineEngineInstancePayloadBytesMean = Mean (
            MetricValues $baselineRows engineInstancePayloadBytes)
        optimizedEngineInstancePayloadBytesMean = Mean (
            MetricValues $optimizedRows engineInstancePayloadBytes)
        mainThreadAllocationRows = [int]$run.mainThreadAllocationRows
        mainThreadAllocatedBytes = [int64]$run.mainThreadAllocatedBytes
        validationRows = $validation.Count
        validationFailures = 0
        validationReadbackRetries = $validationRetryCount
        imageParityFailures = 0
        measurementReadbackBytes = 0
        nativeTimestampReadyRows = [int]$run.nativeTimestampReadyRows
        frameTimingReadyRows = [int]$run.frameTimingReadyRows
        renderThreadFrameReadyRows =
            [int]$run.renderThreadFrameReadyRows
        gpuFrameReadyRows = [int]$run.gpuFrameReadyRows
        engineSubmissionWindowReadyRows =
            [int]$run.engineSubmissionWindowReadyRows
    })
    $matrixRows.Add([pscustomobject][ordered]@{
        scenarioId = $scenario.scenarioId
        visibility = $scenario.visibility
        instanceCount = $scenario.instanceCount
        viewCount = $scenario.viewCount
        drawGroupCount = $scenario.drawGroupCount
        seed = $scenario.seed
        processId = [int]$run.processId
        sameProcessPaired = 1
        schedule = 'control-pre;ABBA;BAAB;control-post'
        status = $run.status
        passed = $run.passed
        rawSampleCount = $run.rawSampleCount
        validationRows = $run.validationRows
        measurementReadbackBytes = $run.measurementReadbackBytes
        nativeTimestampReadyRows = $run.nativeTimestampReadyRows
        frameTimingReadyRows = $run.frameTimingReadyRows
        engineSubmissionWindowReadyRows =
            $run.engineSubmissionWindowReadyRows
        completionFencesComplete = $run.completionFencesComplete
        timedAllocationFree = $run.timedAllocationFree
        mainThreadAllocationRows = $run.mainThreadAllocationRows
        mainThreadAllocatedBytes = $run.mainThreadAllocatedBytes
        cpuSubmissionGatePassed = [int]$cpuGatePassed
        nativeGpuRegionP99GatePassed = [int]$nativeGpuGatePassed
    })
    $playerRuns.Add([ordered]@{
        scenarioId = $scenario.scenarioId
        processId = [int]$run.processId
        exitCode = $player.ExitCode
        reportDirectory = $scenarioRoot
        playerLog = $playerLog
    })
}

$matrixPath = Join-Path $outputRoot 'matrix.csv'
$summaryPath = Join-Path $outputRoot 'matrix-summary.csv'
$matrixRows | Export-Csv `
    -LiteralPath $matrixPath `
    -NoTypeInformation `
    -Encoding utf8
$summaryRows | Export-Csv `
    -LiteralPath $summaryPath `
    -NoTypeInformation `
    -Encoding utf8

$cpuPassedCells = @($summaryRows | Where-Object {
    [int]$_.cpuSubmissionGatePassed -eq 1
}).Count
$nativeGpuPassedCells = @($summaryRows | Where-Object {
    [int]$_.nativeGpuRegionP99GatePassed -eq 1
}).Count
$performanceDecisionPassed =
    $cpuPassedCells -ge $formalContract.requiredCpuSubmissionCells -and
    $nativeGpuPassedCells -eq $summaryRows.Count

$reportLines = [Collections.Generic.List[string]]::new()
$reportLines.Add('# Engine-native CPU versus GPU-driven macrobenchmark')
$reportLines.Add('')
$reportLines.Add("Commit: ``$gitCommit``  ")
$reportLines.Add(
    "Protocol: same-process paired ABBA/BAAB; $SampleFrames samples per " +
    'block; native D3D12 timestamps and CPU FrameTiming tails required.  ')
$reportLines.Add(
    'Correctness: CPU/GPU result and image parity before and after ' +
    'measurement; measurement readback is exactly zero.')
$reportLines.Add('')
$reportLines.Add(
    '| Workload | CPU submit P95 CPU/GPU (ms) | Improvement | ' +
    'CPU frame P99 CPU/GPU (ms) | Native GPU region P99 CPU/GPU (ms) | ' +
    'Native GPU regression | Gates |')
$reportLines.Add('|:---|---:|---:|---:|---:|---:|:---|')
foreach ($row in $summaryRows) {
    $rowTemplate =
        '| N={0}, V={1}, G={2} | {3:F3} / {4:F3} | ' +
        '{5:F2}% ({6:F3} ms) | {7:F3} / {8:F3} | ' +
        '{9:F3} / {10:F3} | {11:F2}% | CPU={12}, NativeGPU={13} |'
    $reportLines.Add(($rowTemplate -f
        $row.instanceCount,
        $row.viewCount,
        $row.drawGroupCount,
        $row.cpuSubmissionBaselineP95Ms,
        $row.cpuSubmissionOptimizedP95Ms,
        $row.cpuSubmissionP95ImprovementPercent,
        $row.cpuSubmissionP95ImprovementMs,
        $row.cpuFrameBaselineP99Ms,
        $row.cpuFrameOptimizedP99Ms,
        $row.nativeGpuRegionBaselineP99Ms,
        $row.nativeGpuRegionOptimizedP99Ms,
        $row.nativeGpuRegionP99RegressionPercent,
        $(if ([int]$row.cpuSubmissionGatePassed -eq 1) {
            'pass'
        }
        else {
            'miss'
        }),
        $(if ([int]$row.nativeGpuRegionP99GatePassed -eq 1) {
            'pass'
        }
        else {
            'miss'
        })))
}
$reportLines.Add('')
$reportLines.Add(
    "CPU submission gate: $cpuPassedCells/$($summaryRows.Count) cells " +
    'met both >=20% and >=0.20 ms P95 improvement.  ')
$reportLines.Add(
    "Native GPU region-tail gate: $nativeGpuPassedCells/" +
    "$($summaryRows.Count) cells kept P99 regression at or below 5%.  ")
if ($FormalAcceptanceMode) {
    $decision = if ($performanceDecisionPassed) { 'PASS' } else { 'NO-GO' }
    $reportLines.Add("Formal decision: **$decision**.")
}
else {
    $reportLines.Add(
        'Decision: smoke evidence only; formal performance acceptance is ' +
        'not evaluated.')
}
$reportLines.Add('')
$reportLines.Add(
    'Required CPU total/main tails, native GPU tails, draw calls, split upload ' +
    'evidence, allocations, and evidence counts are in ' +
    '`matrix-summary.csv`.')
$reportLines.Add(
    'Engine submission-window tails are optional evidence; a missing valid ' +
    'sample set is reported as `unavailable`.')
$reportLines.Add(
    'CPU render-thread and full-frame GPU tails are also optional; valid ' +
    'sample counts are emitted and unavailable values are never encoded as zero.')
$reportPath = Join-Path $outputRoot 'BENCHMARK_REPORT.md'
$reportLines | Set-Content -LiteralPath $reportPath -Encoding utf8

$finalGit = Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
$finalSourceHash =
    Get-CombinedSha256 -Files $sourceFiles -RelativeTo $projectRoot
$finalPayload =
    Get-GpuBenchmarkPlayerPayload -PlayerPath $resolvedPlayerPath
$payloadStable =
    [string]$initialPayload.sha256 -ceq [string]$finalPayload.sha256 -and
    [int]$initialPayload.fileCount -eq [int]$finalPayload.fileCount -and
    [int64]$initialPayload.lengthBytes -eq [int64]$finalPayload.lengthBytes
if ($finalGit.head -ine $gitCommit -or [bool]$finalGit.dirty) {
    throw 'Git state changed during benchmark execution.'
}
if ($finalSourceHash -cne $sourceSnapshotSha256) {
    throw 'Benchmark source hashes changed during benchmark execution.'
}
if (-not $payloadStable) {
    throw 'Player payload changed during benchmark execution.'
}
$runnerConfig['gitFinal'] = $finalGit
$runnerConfig['playerPayload'] = $finalPayload
$runnerConfig['playerPayloadStableThroughRun'] = $payloadStable
$runnerConfig['playerRuns'] = @($playerRuns)
$runnerConfig['evidenceValid'] = $true
$runnerConfig['performanceDecisionPassed'] = $performanceDecisionPassed
$runnerConfig['formalContractSatisfied'] =
    [bool]($FormalAcceptanceMode -and $performanceDecisionPassed)
$runnerConfig['runnerConfigFinalized'] = $true
$runnerConfig['finalizedUtc'] =
    (Get-Date).ToUniversalTime().ToString('o')
$runnerConfig | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath $runnerConfigPath -Encoding utf8
$finalPayload | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath (
        Join-Path $outputRoot 'player-payload-manifest.json') -Encoding utf8

Write-Host "Finalized GPU-driven instance macrobenchmark evidence: $outputRoot"
if ($FormalAcceptanceMode -and -not $performanceDecisionPassed) {
    throw (
        'Formal macrobenchmark performance gate failed closed; reports ' +
        "were retained at $outputRoot")
}
Write-Host "Completed GPU-driven instance macrobenchmark: $outputRoot"
Write-Host "Summary: $summaryPath"
