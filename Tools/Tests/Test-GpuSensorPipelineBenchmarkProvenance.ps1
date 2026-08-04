[CmdletBinding()]
param(
    [string]$ProjectRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot '..\..'))
}
else {
    $ProjectRoot = [System.IO.Path]::GetFullPath($ProjectRoot)
}

$assertions = 0
function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) {
        throw "ASSERTION FAILED: $Message"
    }
    $script:assertions++
}

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Context
    )
    Assert-True ($Text.Contains($Needle)) (
        "$Context must contain '$Needle'.")
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Context
    )
    Assert-True (-not $Text.Contains($Needle)) (
        "$Context must not contain stale token '$Needle'.")
}

function Assert-Parses {
    param([Parameter(Mandatory = $true)][string]$Path)
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$errors)
    $errorText = @(
        $errors | ForEach-Object { $_.Message }) -join '; '
    Assert-True (@($errors).Count -eq 0) (
        "$Path must parse without PowerShell errors: $errorText")
}

$runnerPath =
    Join-Path $ProjectRoot 'Tools\Run-GpuSensorPipelineBenchmark.ps1'
$summarizerPath =
    Join-Path $ProjectRoot 'Tools\Summarize-GpuSensorPipelineBenchmark.ps1'
$provenanceModulePath =
    Join-Path $ProjectRoot 'Tools\GpuBenchmarkProvenance.psm1'
$planPath = Join-Path $ProjectRoot (
    'Docs\GPU_DYNAMIC_SENSOR_PIPELINE_BENCHMARK_PLAN.md')
$schedulePath = Join-Path $ProjectRoot (
    'Assets\GpuSensorPipelineBenchmark\Runtime\' +
    'GpuSensorPipelineBenchmarkSchedule.cs')
$controllerPath = Join-Path $ProjectRoot (
    'Assets\GpuSensorPipelineBenchmark\Runtime\' +
    'GpuSensorPipelineBenchmarkController.cs')
$adapterPath = Join-Path $ProjectRoot (
    'Assets\GpuSensorPipelineBenchmark\Runtime\' +
    'GpuSensorPipelineBenchmarkAdapter.cs')
$runtimePackageRoot =
    Join-Path $ProjectRoot 'Packages\com.summit.gpu-sensor-pipeline'
$runtimeApiPath =
    Join-Path $runtimePackageRoot 'Runtime\GpuSensorPipeline.cs'
$runtimeShaderPath = Join-Path $runtimePackageRoot (
    'Runtime\Resources\GpuSensorPipeline\GpuSensorPipeline.compute')
$directPackageRoot =
    Join-Path $ProjectRoot 'Packages\com.summit.gpu-direct-binning'
$primitivesPackageRoot =
    Join-Path $ProjectRoot 'Packages\com.summit.gpu-primitives'
$timestampDllPath = Join-Path $ProjectRoot (
    'Packages\com.summit.gpu-timestamps\Runtime\Plugins\x86_64\' +
    'SummitGpuTimestamps.dll')

$requiredFiles = @(
    $runnerPath,
    $summarizerPath,
    $provenanceModulePath,
    $planPath,
    $schedulePath,
    $controllerPath,
    $adapterPath,
    $runtimeApiPath,
    $runtimeShaderPath,
    $timestampDllPath,
    (Join-Path $directPackageRoot 'package.json'),
    (Join-Path $primitivesPackageRoot 'package.json'))
foreach ($path in $requiredFiles) {
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) (
        "Required S4 benchmark input must exist: $path")
}

$benchmarkScripts = @(
    $runnerPath,
    $summarizerPath,
    $provenanceModulePath,
    $PSCommandPath) | Sort-Object -Unique
foreach ($scriptPath in $benchmarkScripts) {
    Assert-Parses $scriptPath
}

$runner = Get-Content -LiteralPath $runnerPath -Raw
$summarizer = Get-Content -LiteralPath $summarizerPath -Raw
$schedule = Get-Content -LiteralPath $schedulePath -Raw
$controller = Get-Content -LiteralPath $controllerPath -Raw
$adapter = Get-Content -LiteralPath $adapterPath -Raw
$powerShellHarness = $runner + "`n" + $summarizer

$summarizerTokens = $null
$summarizerErrors = $null
$summarizerAst =
    [System.Management.Automation.Language.Parser]::ParseFile(
        $summarizerPath,
        [ref]$summarizerTokens,
        [ref]$summarizerErrors)
$sourceFrameOrderExpressions = @(
    $summarizerAst.FindAll(
        {
            param($node)
            $node -is
                [System.Management.Automation.Language.BinaryExpressionAst] -and
                $node.Left.Extent.Text -ceq '$sourceFrame' -and
                $node.Right.Extent.Text -ceq '$previousSourceUnityFrame'
        },
        $true))
Assert-True ($sourceFrameOrderExpressions.Count -eq 1) (
    'Summarizer must have one executable source-frame ordering predicate.')
$sourceFrameOrderPredicate = [scriptblock]::Create(
    'param([int]$sourceFrame, [int]$previousSourceUnityFrame) ' +
    $sourceFrameOrderExpressions[0].Extent.Text)
$equalSourceFrameRejected = [bool](& $sourceFrameOrderPredicate 312 312)
Assert-True (-not $equalSourceFrameRejected) (
    'Equal sourceUnityFrame values must be accepted within one Unity frame.')
$decreasingSourceFrameRejected = [bool](& $sourceFrameOrderPredicate 311 312)
Assert-True $decreasingSourceFrameRejected (
    'A decreasing sourceUnityFrame must remain rejected.')

$expectedBlocksFunction = $summarizerAst.Find(
    {
        param($node)
        $node -is
            [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -ceq 'Expected-Blocks'
    },
    $true)
Assert-True ($null -ne $expectedBlocksFunction) (
    'Summarizer must define the executable Expected-Blocks function.')
$CpuVariant = 'cpu-produced-uploaded'
$GpuVariant = 'gpu-produced-resident'
$ControlVariant = 'empty-main-graphics-control'
$CpuCaseId = 'sensor-pipeline/cpu-produced-uploaded-v1'
$GpuCaseId = 'sensor-pipeline/gpu-produced-resident-v1'
$ControlCaseId = 'control/empty-main-graphics-command-buffer'
$CpuMarker = 'GPU.SensorPipeline/CpuProducedUploaded/MainGraphics'
$GpuMarker = 'GPU.SensorPipeline/GpuProducedResident/MainGraphics'
$ControlMarker = 'GPU.SensorPipeline/Control/EmptyMainGraphics'
Invoke-Expression $expectedBlocksFunction.Extent.Text
$extractedBlocks = @(Expected-Blocks -SuperRounds 4)
Assert-True ($extractedBlocks.Count -eq 18) (
    'Extracted Expected-Blocks must produce 18 blocks for four rounds.')
$extractedMeasurementBlocks = @(
    $extractedBlocks | Where-Object { $_.blockType -ceq 'measurement' })
$extractedPairs = @(
    $extractedMeasurementBlocks | Group-Object pairIndex)
Assert-True ($extractedPairs.Count -eq 8) (
    'Extracted Expected-Blocks must produce eight measurement pairs.')
$wellFormedPairCount = @(
    $extractedPairs | Where-Object {
        $_.Count -eq 2 -and
        @($_.Group | Select-Object -ExpandProperty pairOrder -Unique).Count -eq
            1 -and
        @($_.Group | Select-Object -ExpandProperty variant -Unique).Count -eq
            2
    }).Count
Assert-True ($wellFormedPairCount -eq 8) (
    'Every extracted pair must contain one A and one B with one pair order.')
$extractedPairOrders = @(
    $extractedPairs | ForEach-Object {
        [string]$_.Group[0].pairOrder
    })
Assert-True (
    @($extractedPairOrders | Where-Object { $_ -ceq 'AB' }).Count -eq 4) (
    'Extracted Expected-Blocks must produce four AB pairs.')
Assert-True (
    @($extractedPairOrders | Where-Object { $_ -ceq 'BA' }).Count -eq 4) (
    'Extracted Expected-Blocks must produce four BA pairs.')
Remove-Item -LiteralPath Function:\Expected-Blocks

foreach ($token in @(
    'schemaVersion = 10',
    "suite = 'summit.gpu-sensor-pipeline'",
    'benchmarkSchemaVersion = 10',
    "matrixPreset = 'amd-r9700-dynamic-v1'",
    "matrixPreset = 'amd-r9700-dynamic-discovery-v1'",
    'binCount = 262144',
    'stateCount = 64',
    'superRounds = 4',
    'sampleFrames = 900',
    'sampleFrames = 240',
    "scenarioId = 'dynamic-n262144-q64'",
    "scenarioId = 'dynamic-n1048576-q256'",
    'elementCount = 262144',
    'elementCount = 1048576',
    'queryCount = 64',
    'queryCount = 256')) {
    Assert-Contains $runner $token 'Runner frozen contract'
}

$formalContractMatch = [regex]::Match(
    $runner,
    '(?s)\$formalContract\s*=\s*\[ordered\]@\{(.*?)\}\s*\r?\n\$discoveryContract')
Assert-True $formalContractMatch.Success (
    'Runner must expose the frozen formal contract.')
$formalContractText = $formalContractMatch.Groups[1].Value
Assert-Contains $formalContractText 'playerTimeoutMinutes = 90' (
    'Frozen formal Player timeout contract')
Assert-Contains $runner (
    "@('PlayerTimeoutMinutes', `$PlayerTimeoutMinutes, " +
        "`$formalContract.playerTimeoutMinutes)") (
    'Formal Player timeout validation binding')
$runnerConfigMatch = [regex]::Match(
    $runner,
    '(?s)\$runnerConfig\s*=\s*\[ordered\]@\{(.*?)\r?\n\}\r?\n\$runnerConfigPath')
Assert-True $runnerConfigMatch.Success (
    'Runner must expose the emitted runner-config block.')
Assert-Contains $runnerConfigMatch.Groups[1].Value (
    'playerTimeoutMinutes = $PlayerTimeoutMinutes') (
    'Runner-config Player timeout binding')

$discoveryContractMatch = [regex]::Match(
    $runner,
    '(?s)\$discoveryContract\s*=\s*\[ordered\]@\{(.*?)\}\s*\r?\n\$formalScenarios')
Assert-True $discoveryContractMatch.Success (
    'Runner must expose the frozen discovery contract.')
$discoveryContractText = $discoveryContractMatch.Groups[1].Value
foreach ($token in @(
    "unityVersion = '6000.5.2f1'",
    'deviceIndex = 0',
    "matrixPreset = 'amd-r9700-dynamic-discovery-v1'")) {
    Assert-Contains $discoveryContractText $token (
        'Frozen discovery Unity/device contract')
}
foreach ($token in @(
    "@('DeviceIndex', `$DeviceIndex, `$discoveryContract.deviceIndex)",
    'Frozen discovery requires Unity',
    'Frozen discovery requires editor',
    'discoveryContract = $discoveryContract')) {
    Assert-Contains $runner $token 'Frozen discovery enforcement'
}
$discoverySummaryMatch = [regex]::Match(
    $summarizer,
    "(?s)elseif \(\[string\]\`$runner\.matrixPreset -ceq\s*'amd-r9700-dynamic-discovery-v1'\) \{(.*?)\}\s*elseif")
Assert-True $discoverySummaryMatch.Success (
    'Summarizer must expose the frozen discovery validation branch.')
$discoverySummaryText = $discoverySummaryMatch.Groups[1].Value
foreach ($token in @(
    '$runner.deviceIndex -ne 0',
    '$runner.projectUnityVersion',
    '$runner.unityEditorResolvedVersion',
    '$runner.discoveryContract',
    '$contract.deviceIndex',
    '$contract.unityVersion')) {
    Assert-Contains $discoverySummaryText $token (
        'Summarizer frozen discovery enforcement')
}

$formalScenarioMatch = [regex]::Match(
    $runner,
    '(?s)\$formalScenarios\s*=\s*@\((.*?)\)\s*\r?\n\s*if\s*\(\$FormalAcceptanceMode\)')
Assert-True $formalScenarioMatch.Success (
    'Runner must expose one frozen formal-scenario matrix.')
$formalScenarioText = $formalScenarioMatch.Groups[1].Value
Assert-True (
    [regex]::Matches($formalScenarioText, '\bscenarioId\s*=').Count -eq 2) (
    'Formal matrix must contain exactly two scenarios.')
foreach ($pattern in @(
    ("(?s)dynamic-n262144-q64'.*?elementCount\s*=\s*262144.*?" +
        'binCount\s*=\s*262144.*?queryCount\s*=\s*64.*?' +
        'logicalStateCount\s*=\s*64'),
    ("(?s)dynamic-n1048576-q256'.*?elementCount\s*=\s*1048576.*?" +
        'binCount\s*=\s*262144.*?queryCount\s*=\s*256.*?' +
        'logicalStateCount\s*=\s*64'))) {
    Assert-True ([regex]::IsMatch($formalScenarioText, $pattern)) (
        "Formal scenario contract does not match '$pattern'.")
}

$expectedIdentities = @(
    'Summit.GpuSensorPipeline.Tests.Editor.dll',
    'Summit.GpuSensorPipeline.Tests.GpuSensorDeterministicGeneratorTests',
    'Summit.GpuSensorPipeline.Tests.GpuSensorPipelineContractTests',
    'Summit.GpuSensorPipeline.Tests.GpuSensorPipelineIntegrationTests',
    'Summit.GpuSensorPipeline.Benchmark.Tests.Editor.dll',
    'Summit.GpuSensorPipeline.Benchmark.Tests.GpuSensorPipelineBenchmarkScheduleTests'
)
$identityBlockMatch = [regex]::Match(
    $runner,
    '(?s)\$expectedEditModeIdentities\s*=\s*@\((.*?)\)\s*\r?\n\s*\$editModeResults')
Assert-True $identityBlockMatch.Success (
    'Runner must define the exact EditMode identity block.')
$runnerIdentities = @(
    [regex]::Matches(
        $identityBlockMatch.Groups[1].Value,
        "'([^']+)'") |
        ForEach-Object { $_.Groups[1].Value })
Assert-True (
    [string]::Join("`n", $runnerIdentities) -ceq
    [string]::Join("`n", $expectedIdentities)) (
    'Runner EditMode identities must be exactly the frozen six identities.')
foreach ($identity in $expectedIdentities) {
    Assert-Contains $summarizer $identity (
        'Summarizer exact EditMode identity contract')
}
foreach ($token in @(
    "test-suite[@type='Assembly']",
    "test-suite[@type='TestFixture']",
    'observedIdentities.Contains',
    'identityMatchMode',
    'missingIdentities')) {
    Assert-Contains $powerShellHarness $token (
        'Exact parsed NUnit identity validation')
}
Assert-NotContains $powerShellHarness '$rawXml.IndexOf(' (
    'PowerShell harness')
Assert-NotContains $powerShellHarness '$editModeXml.IndexOf(' (
    'PowerShell harness')

foreach ($token in @(
    'Get-GpuBenchmarkGitSnapshot',
    'Restore-KnownUnityBenchmarkDrift',
    'Get-GpuBenchmarkPlayerPayload',
    'sourceSnapshotSha256',
    'sourceHashesStableAcrossEditMode',
    'sourceHashesStableAcrossBuild',
    'editModeEvidenceBoundToSource',
    'playerPayloadStableThroughRun',
    'runnerConfigFinalized',
    'formalContractSatisfied',
    "'-runTests'",
    "'-testPlatform', 'EditMode'",
    'com.summit.gpu-sensor-pipeline',
    'com.summit.gpu-direct-binning',
    'com.summit.gpu-primitives',
    'GpuBenchmarkProvenance.psm1',
    'Summarize-GpuSensorPipelineBenchmark.ps1',
    'Test-GpuSensorPipelineBenchmarkProvenance.ps1',
    'GPU_DYNAMIC_SENSOR_PIPELINE_BENCHMARK_PLAN.md',
    'SummitGpuTimestamps.dll',
    'runnerToolSha256',
    'summarizerToolSha256',
    'provenanceTestSha256',
    'benchmarkPlanSha256',
    'timestampNativeDllSha256')) {
    Assert-Contains $runner $token 'Runner source/provenance binding'
}

foreach ($token in @(
    'OutputDirectory is required and must be outside the Git worktree.',
    'Benchmark report output must be outside the Git worktree.',
    'Report directory must be new or empty.',
    'Report output may not be inside the Player payload.')) {
    Assert-Contains $runner $token (
        'Runner external immutable-evidence output contract')
}
$outputRootAssignment =
    '$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)'
$outputRootFirstUse = 'if ($outputRoot -ieq $normalizedProjectRoot -or'
Assert-True (
    $runner.IndexOf($outputRootAssignment) -ge 0 -and
    $runner.IndexOf($outputRootAssignment) -lt
        $runner.IndexOf($outputRootFirstUse)) (
    'Runner must assign outputRoot before the containment check uses it.')

foreach ($token in @(
    'GpuSensorPipelineBenchmarkVariant.CpuProducedUploaded',
    'GpuSensorPipelineBenchmarkVariant.GpuProducedResident',
    'bool abba = (superRound & 1) != 0',
    'FormalSuperRoundCount = 4',
    'FormalStateCount = 64',
    'zeroBasedSampleIndex % stateCount',
    'superRoundCount * 4 + 2')) {
    Assert-Contains $schedule $token 'Balanced dynamic A/B schedule'
}
foreach ($staleVariant in @(
    'GpuSensorPipelineBenchmarkVariant.Reference',
    'GpuSensorPipelineBenchmarkVariant.Direct')) {
    Assert-NotContains $schedule $staleVariant 'Dynamic A/B schedule'
}

foreach ($token in @(
    'MeasurementReadbackBytes = 0',
    'uploadQueueCoverageVerified = false',
    'asyncComputeClaim = false',
    'copyQueueClaim = false',
    'mainGraphicsQueueTimestamp = true',
    'MarkSubmitted(token)')) {
    Assert-Contains $controller $token 'Controller claim/readback boundary'
}
foreach ($token in @(
    'RecordCpuProduced',
    'RecordGpuProduced',
    'BeginCaptureBlock',
    'TryCompleteCapture',
    'BeginCompareCapturedBlocks',
    'TryCompleteComparison',
    'pipeline.RecordValidateKeys',
    'pipeline.FrameDigest',
    'pipeline.ComparisonDigest')) {
    Assert-Contains $adapter $token 'Shared-consumer validation path'
}
foreach ($token in @(
    'measurementWorkloadReadbackBytes = 0',
    'timestampInstrumentationReadbackDisclosedSeparately = $true',
    'uploadQueueCoverageVerified = $false',
    'asyncComputeClaim = $false',
    'copyQueueClaim = $false')) {
    Assert-Contains $runner $token 'Runner claim/readback boundary'
}

foreach ($outputName in @(
    'pair-summary.csv',
    'matrix-summary.csv',
    'quality-summary.json',
    'quality-summary.txt',
    'scenario-summary.csv')) {
    Assert-Contains $summarizer $outputName 'Summarizer retained output contract'
}
foreach ($token in @(
    'nativeTimestampElapsedNanoseconds',
    'measurementReadbackBytes',
    'timestampInstrumentationReadbackBytes',
    'logicalUploadBytes',
    'gpuProducerLogicalWriteBytes',
    'uploadQueueCoverageVerified',
    'mainGraphicsQueueTimestamp',
    'allStateDigestComparison',
    'finalStateKeyValidation',
    'positive-gpu-resident-faster')) {
    Assert-Contains $summarizer $token 'Summarizer quality/claim contract'
}

foreach ($staleToken in @(
    "'amd-r9700-v1'",
    "'uniform-c4096'",
    "'hotset16-c4096'",
    "'uniform-c65536'",
    'referenceShaderSha256',
    'dispatchesPerFrame',
    'GpuSensorPipelineBenchmarkVariant.Reference',
    'GpuSensorPipelineBenchmarkVariant.Direct',
    'reference-compose-portable',
    'direct-count-scan-scatter',
    'CpuDirectBinningOracleTests',
    'GpuDirectSpatialBinnerContractTests',
    'GpuDirectSpatialBinnerIntegrationTests',
    'GpuSensorPipelineInputDistributionTests',
    'GpuSensorPipelineCpuOracleTests')) {
    Assert-NotContains $powerShellHarness $staleToken 'S4 PowerShell harness'
}
Assert-True (
    -not [regex]::IsMatch(
        $powerShellHarness,
        '\b\d+(?:UL|LU|U)\b')) (
    'PowerShell harness must not contain C# unsigned numeric suffix literals.')

$tamperRoot = Join-Path (
    [System.IO.Path]::GetTempPath()) (
    'summit-sensor-pipeline-tamper-' +
    [Guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($tamperRoot)
try {
    [ordered]@{
        schemaVersion = 10
        suite = 'summit.gpu-sensor-pipeline'
        benchmarkSchemaVersion = 10
        formalAcceptanceMode = $true
        formalContractSatisfied = $true
        runnerConfigFinalized = $true
        sourceHashesStableAcrossEditMode = $true
        sourceHashesStableAcrossBuild = $true
        editModeEvidenceBoundToSource = $true
        playerPayloadStableThroughRun = $true
        gitTreeDirty = $false
        gitFinal = [ordered]@{ dirty = $false }
        matrixPreset = 'amd-r9700-dynamic-v1'
        superRounds = 4
        warmupFrames = 60
        sampleFrames = 899
        cooldownFrames = 15
        binCount = 262144
        logicalStateCount = 64
        stagingSlotCount = 4
        projectUnityVersion = '6000.5.2f1'
        unityEditorResolvedVersion = '6000.5.2f1'
        requireCompleteGpuTimings = $true
        scenarios = @()
        playerRuns = @()
    } |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (
            Join-Path $tamperRoot 'runner-config.json') -Encoding utf8

    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $tamperOutput = & powershell.exe `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $summarizerPath `
        -ReportDirectory $tamperRoot 2>&1
    $tamperExitCode = $LASTEXITCODE
    $ErrorActionPreference = $savedPreference
    Assert-True ($tamperExitCode -ne 0) (
        'Summarizer must reject a tampered formal sampleFrames value.')
}
finally {
    if (Test-Path -LiteralPath $tamperRoot -PathType Container) {
        Remove-Item -LiteralPath $tamperRoot -Recurse -Force
    }
}

$savedPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$formalOutput = & powershell.exe `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $runnerPath `
    -MatrixPreset 'amd-r9700-dynamic-v1' `
    -DeviceIndex 0 `
    -SuperRounds 4 `
    -WarmupFrames 60 `
    -SampleFrames 900 `
    -CooldownFrames 15 `
    -StagingSlotCount 4 `
    -PlayerTimeoutMinutes 89 `
    -FormalAcceptanceMode `
    -EditModeResultsPath 'intentionally-supplied.xml' `
    -SkipSummary 2>&1
$formalExitCode = $LASTEXITCODE
$ErrorActionPreference = $savedPreference
$formalText = @($formalOutput | ForEach-Object { [string]$_ }) -join "`n"
Assert-True ($formalExitCode -ne 0) (
    'Formal runner must reject bypasses before starting Unity.')
Assert-True ($formalText.Contains('SkipSummary is forbidden.')) (
    'Formal SkipSummary rejection must be explicit.')
Assert-True (
    $formalText.Contains(
        'EditModeResultsPath is discovery-only; formal evidence is generated in-run.')) (
    'Formal runner must reject caller-supplied EditMode evidence.')
Assert-True (
    $formalText.Contains('PlayerTimeoutMinutes=89; expected 90')) (
    'Formal runner must reject a non-frozen Player timeout explicitly.')

$savedPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$discoveryOutput = & powershell.exe `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $runnerPath `
    -MatrixPreset 'amd-r9700-dynamic-discovery-v1' `
    -DeviceIndex 1 `
    -SuperRounds 4 `
    -WarmupFrames 60 `
    -SampleFrames 240 `
    -CooldownFrames 15 `
    -StagingSlotCount 4 `
    -ValidationTimeoutSeconds 60 2>&1
$discoveryExitCode = $LASTEXITCODE
$ErrorActionPreference = $savedPreference
$discoveryText =
    @($discoveryOutput | ForEach-Object { [string]$_ }) -join "`n"
Assert-True ($discoveryExitCode -ne 0) (
    'Frozen discovery must reject a nonzero device index before Unity starts.')
Assert-True ($discoveryText.Contains('DeviceIndex=1; expected 0')) (
    'Frozen discovery device-index rejection must be explicit.')

Write-Host (
    'PASS: GPU sensor-pipeline benchmark provenance tests; ' +
    "assertions=$assertions")
