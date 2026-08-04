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
    Join-Path $ProjectRoot 'Tools\Run-GpuSensorDataPackingBenchmark.ps1'
$summarizerPath =
    Join-Path $ProjectRoot (
        'Tools\Summarize-GpuSensorDataPackingBenchmark.ps1')
$provenanceModulePath =
    Join-Path $ProjectRoot 'Tools\GpuBenchmarkProvenance.psm1'
$schedulePath = Join-Path $ProjectRoot (
    'Assets\GpuSensorPipelineBenchmark\Runtime\' +
    'GpuSensorDataPackingBenchmarkSchedule.cs')
$controllerPath = Join-Path $ProjectRoot (
    'Assets\GpuSensorPipelineBenchmark\Runtime\' +
    'GpuSensorDataPackingBenchmarkController.cs')
$adapterPath = Join-Path $ProjectRoot (
    'Assets\GpuSensorPipelineBenchmark\Runtime\' +
    'GpuSensorDataPackingBenchmarkAdapter.cs')
$runtimeRoot =
    Join-Path $ProjectRoot 'Packages\com.summit.gpu-sensor-pipeline'
$baselineRuntimePath =
    Join-Path $runtimeRoot 'Runtime\GpuSensorPipeline.cs'
$packedRuntimePath =
    Join-Path $runtimeRoot 'Runtime\GpuSensorPackedSoaPipeline.cs'
$quantizerPath =
    Join-Path $runtimeRoot 'Runtime\GpuSensorIntensityQuantizer.cs'
$baselineShaderPath = Join-Path $runtimeRoot (
    'Runtime\Resources\GpuSensorPipeline\GpuSensorPipeline.compute')
$packedShaderPath = Join-Path $runtimeRoot (
    'Runtime\Resources\GpuSensorPipeline\' +
    'GpuSensorPackedSoaPipeline.compute')
$timestampDllPath = Join-Path $ProjectRoot (
    'Packages\com.summit.gpu-timestamps\Runtime\Plugins\x86_64\' +
    'SummitGpuTimestamps.dll')

$requiredFiles = @(
    $runnerPath,
    $summarizerPath,
    $provenanceModulePath,
    $schedulePath,
    $controllerPath,
    $adapterPath,
    $baselineRuntimePath,
    $packedRuntimePath,
    $quantizerPath,
    $baselineShaderPath,
    $packedShaderPath,
    $timestampDllPath,
    $PSCommandPath)
foreach ($path in $requiredFiles) {
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) (
        "Required S5 benchmark input must exist: $path")
}
foreach ($scriptPath in @(
    $runnerPath,
    $summarizerPath,
    $provenanceModulePath,
    $PSCommandPath)) {
    Assert-Parses $scriptPath
}

$runner = Get-Content -LiteralPath $runnerPath -Raw
$summarizer = Get-Content -LiteralPath $summarizerPath -Raw
$schedule = Get-Content -LiteralPath $schedulePath -Raw
$controller = Get-Content -LiteralPath $controllerPath -Raw
$adapter = Get-Content -LiteralPath $adapterPath -Raw
$baselineRuntime =
    Get-Content -LiteralPath $baselineRuntimePath -Raw
$packedRuntime = Get-Content -LiteralPath $packedRuntimePath -Raw
$quantizer = Get-Content -LiteralPath $quantizerPath -Raw
$packedShader = Get-Content -LiteralPath $packedShaderPath -Raw
$powerShellHarness = $runner + "`n" + $summarizer

$summaryTokens = $null
$summaryErrors = $null
$summaryAst =
    [System.Management.Automation.Language.Parser]::ParseFile(
        $summarizerPath,
        [ref]$summaryTokens,
        [ref]$summaryErrors)
$sourceFrameOrderExpressions = @(
    $summaryAst.FindAll(
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
Assert-True (-not [bool](& $sourceFrameOrderPredicate 312 312)) (
    'Equal sourceUnityFrame values must remain accepted.')
Assert-True ([bool](& $sourceFrameOrderPredicate 311 312)) (
    'A decreasing sourceUnityFrame must remain rejected.')

$gpuDurationFunction = $summaryAst.Find(
    {
        param($node)
        $node -is
            [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -ceq 'Test-GpuDurationContract'
    },
    $true)
Assert-True ($null -ne $gpuDurationFunction) (
    'Summarizer must define an executable GPU-duration contract.')
Invoke-Expression $gpuDurationFunction.Extent.Text
Assert-True (
    Test-GpuDurationContract 'control-pre' 0 0 0.0) (
    'A same-tick control-pre sample must remain accepted.')
Assert-True (
    Test-GpuDurationContract 'control-post' 0 0 0.0) (
    'A same-tick control-post sample must remain accepted.')
Assert-True (
    -not (Test-GpuDurationContract 'control-pre' -1 0 0.0)) (
    'A negative control duration must remain rejected.')
Assert-True (
    Test-GpuDurationContract 'precondition' 1 10 0.0001) (
    'A positive precondition duration must remain accepted.')
Assert-True (
    -not (Test-GpuDurationContract 'precondition' 0 0 0.0)) (
    'A same-tick precondition sample must remain rejected.')
Assert-True (
    Test-GpuDurationContract 'measurement' 1 10 0.0001) (
    'A positive measurement duration must remain accepted.')
Assert-True (
    -not (Test-GpuDurationContract 'measurement' 0 0 0.0)) (
    'A same-tick measurement sample must remain rejected.')
Assert-True (
    -not (Test-GpuDurationContract 'unknown' 1 10 0.0001)) (
    'An unknown block type must remain rejected.')
Remove-Item -LiteralPath Function:\Test-GpuDurationContract

$expectedBlocksFunction = $summaryAst.Find(
    {
        param($node)
        $node -is
            [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -ceq 'Expected-Blocks'
    },
    $true)
Assert-True ($null -ne $expectedBlocksFunction) (
    'Summarizer must define executable Expected-Blocks.')
$BaselineVariant = 'expanded-aos-quantized-view'
$PackedVariant = 'packed-soa-fused-end-cursor'
$ControlVariant = 'empty-main-graphics-control'
$BaselineCaseId =
    'sensor-data-packing/expanded-aos-quantized-view-v1'
$PackedCaseId =
    'sensor-data-packing/packed-soa-fused-end-cursor-v2'
$ControlCaseId = 'control/empty-main-graphics-command-buffer'
$BaselineMarker =
    'GPU.SensorDataPacking/ExpandedAoSQuantizedView/MainGraphics'
$PackedMarker =
    'GPU.SensorDataPacking/PackedSoAFusedEndCursor/MainGraphics'
$ControlMarker = 'GPU.SensorDataPacking/Control/EmptyMainGraphics'
$PreconditioningSampleFramesPerBlock = 240
Invoke-Expression $expectedBlocksFunction.Extent.Text
$extractedBlocks = @(
    Expected-Blocks -SuperRounds 4 -SampleFrames 900)
Assert-True ($extractedBlocks.Count -eq 20) (
    'Expected-Blocks must produce 20 blocks for four rounds.')
$preconditionBlocks = @(
    $extractedBlocks | Where-Object {
        $_.blockType -ceq 'precondition'
    })
Assert-True (
    $preconditionBlocks.Count -eq 2 -and
    [int]$preconditionBlocks[0].blockIndex -eq 2 -and
    [string]$preconditionBlocks[0].variant -ceq $BaselineVariant -and
    [int]$preconditionBlocks[1].blockIndex -eq 3 -and
    [string]$preconditionBlocks[1].variant -ceq $PackedVariant) (
    'Expected-Blocks must expose fixed baseline/packed preconditioning.')
Assert-True (
    @($preconditionBlocks | Where-Object {
        [int]$_.expectedSamples -ne 240 -or
        [int]$_.pairIndex -ne 0 -or
        [int]$_.withinPairPosition -ne 0 -or
        [string]$_.pairOrder -cne ''
    }).Count -eq 0) (
    'Preconditioning blocks must be 240-frame unscored entries.')
$measurementBlocks = @(
    $extractedBlocks | Where-Object {
        $_.blockType -ceq 'measurement'
    })
Assert-True ($measurementBlocks.Count -eq 16) (
    'Expected-Blocks must produce 16 scored measurement blocks.')
$pairs = @($measurementBlocks | Group-Object pairIndex)
Assert-True ($pairs.Count -eq 8) (
    'Expected-Blocks must produce eight adjacent A/B pairs.')
foreach ($pair in $pairs) {
    Assert-True (
        $pair.Count -eq 2 -and
        @($pair.Group | Select-Object -ExpandProperty variant -Unique).
            Count -eq 2 -and
        @($pair.Group | Select-Object -ExpandProperty pairOrder -Unique).
            Count -eq 1) (
        "Pair $($pair.Name) must contain one baseline and one packed block.")
}
Assert-True (
    @($pairs | Where-Object {
        [string]$_.Group[0].pairOrder -ceq 'AB'
    }).Count -eq 4) 'Schedule must expose four AB pairs.'
Assert-True (
    @($pairs | Where-Object {
        [string]$_.Group[0].pairOrder -ceq 'BA'
    }).Count -eq 4) 'Schedule must expose four BA pairs.'
Remove-Item -LiteralPath Function:\Expected-Blocks

$runnerTokens = $null
$runnerErrors = $null
$runnerAst =
    [System.Management.Automation.Language.Parser]::ParseFile(
        $runnerPath,
        [ref]$runnerTokens,
        [ref]$runnerErrors)
$rendererFunction = $runnerAst.Find(
    {
        param($node)
        $node -is
            [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -ceq 'Get-PlayerRendererMetadata'
    },
    $true)
Assert-True ($null -ne $rendererFunction) (
    'Runner must define executable Player Renderer parsing.')
Invoke-Expression $rendererFunction.Extent.Text
$rendererFixturePath = Join-Path (
    [System.IO.Path]::GetTempPath()) (
    'summit-s5-renderer-fixture-' +
    [Guid]::NewGuid().ToString('N') + '.log')
try {
    @(
        'Direct3D:',
        '    Version:         Direct3D 12 [level 12.2]',
        '    Renderer:        AMD Radeon AI PRO R9700 (ID=0x7551)',
        '    Vendor:          ATI',
        '    Driver:          32.0.31035.1003') |
        Set-Content -LiteralPath $rendererFixturePath -Encoding utf8
    $rendererEvidence =
        Get-PlayerRendererMetadata -Path $rendererFixturePath
    Assert-True ([bool]$rendererEvidence.parseComplete) (
        'Renderer fixture must parse completely.')
    Assert-True (
        [string]$rendererEvidence.graphicsApi -ceq 'Direct3D 12' -and
        [string]$rendererEvidence.rendererName -ceq
            'AMD Radeon AI PRO R9700' -and
        [string]$rendererEvidence.rendererIdHex -ceq '7551' -and
        [int]$rendererEvidence.rendererId -eq 30033 -and
        [string]$rendererEvidence.driverVersion -ceq
            '32.0.31035.1003') (
        'Renderer fixture must match the exact frozen R9700 identity.')
}
finally {
    Remove-Item -LiteralPath $rendererFixturePath -Force `
        -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath Function:\Get-PlayerRendererMetadata `
        -ErrorAction SilentlyContinue
}

foreach ($token in @(
    'schemaVersion = 12',
    "suite = 'summit.gpu-sensor-data-packing'",
    'benchmarkSchemaVersion = 12',
    "matrixPreset = 'amd-r9700-packing-v2'",
    "matrixPreset = 'amd-r9700-packing-discovery-v2'",
    'binCount = 262144',
    'stateCount = 64',
    'superRounds = 4',
    'sampleFrames = 900',
    'sampleFrames = 240',
    'preconditioningBlockCount = 2',
    'preconditioningSampleFramesPerBlock = 240',
    '$ExpectedBlockCount = 20',
    '$ExpectedScoredMeasurementBlockCount = 16',
    '$ExpectedScoredPairCount = 8',
    'expectedBlockCount = $ExpectedBlockCount',
    'expectedScoredMeasurementBlockCount =',
    'expectedScoredPairCount = $ExpectedScoredPairCount',
    "scenarioId = 'holdout-packing-v2-n262144-q64'",
    "scenarioId = 'holdout-packing-v2-n1048576-q256'",
    "scenarioId = 'discovery-packing-v2-n262144-q64'",
    "scenarioId = 'discovery-packing-v2-n1048576-q256'",
    'elementCount = 262144',
    'elementCount = 1048576',
    'queryCount = 64',
    'queryCount = 256',
    'commandSlotCount = 4',
    'validationReadbackBytesPerComparison = 56')) {
    Assert-Contains $runner $token 'Runner frozen S5 contract'
}

$formalContractMatch = [regex]::Match(
    $runner,
    '(?s)\$formalContract\s*=\s*\[ordered\]@\{(.*?)\}\s*\r?\n\$discoveryContract')
Assert-True $formalContractMatch.Success (
    'Runner must expose the frozen formal contract.')
$formalText = $formalContractMatch.Groups[1].Value
foreach ($token in @(
    "unityVersion = '6000.5.2f1'",
    'deviceIndex = 0',
    "matrixPreset = 'amd-r9700-packing-v2'",
    'preconditioningBlockCount = 2',
    'preconditioningSampleFramesPerBlock = 240',
    'commandSlotCount = 4',
    'playerTimeoutMinutes = 90')) {
    Assert-Contains $formalText $token 'Frozen formal contract'
}
$discoveryContractMatch = [regex]::Match(
    $runner,
    '(?s)\$discoveryContract\s*=\s*\[ordered\]@\{(.*?)\}\s*\r?\n\$formalScenarios')
Assert-True $discoveryContractMatch.Success (
    'Runner must expose the frozen discovery contract.')
$discoveryText = $discoveryContractMatch.Groups[1].Value
foreach ($token in @(
    "unityVersion = '6000.5.2f1'",
    'deviceIndex = 0',
    "matrixPreset = 'amd-r9700-packing-discovery-v2'",
    'preconditioningBlockCount = 2',
    'preconditioningSampleFramesPerBlock = 240',
    'sampleFrames = 240',
    'commandSlotCount = 4')) {
    Assert-Contains $discoveryText $token 'Frozen discovery contract'
}

$formalScenariosMatch = [regex]::Match(
    $runner,
    '(?s)\$formalScenarios\s*=\s*@\((.*?)\)\s*\r?\n\$discoveryScenarios')
Assert-True $formalScenariosMatch.Success (
    'Runner must expose one frozen scenario matrix.')
$formalScenariosText = $formalScenariosMatch.Groups[1].Value
Assert-True (
    [regex]::Matches(
        $formalScenariosText,
        '\bscenarioId\s*=').Count -eq 2) (
    'Frozen scenario matrix must contain exactly two scenarios.')
foreach ($pattern in @(
    ("(?s)holdout-packing-v2-n262144-q64'.*?" +
        'elementCount\s*=\s*262144.*?' +
        'binCount\s*=\s*262144.*?queryCount\s*=\s*64.*?' +
        'logicalStateCount\s*=\s*64.*?seed\s*=\s*20260817'),
    ("(?s)holdout-packing-v2-n1048576-q256'.*?" +
        'elementCount\s*=\s*1048576.*?' +
        'binCount\s*=\s*262144.*?queryCount\s*=\s*256.*?' +
        'logicalStateCount\s*=\s*64.*?seed\s*=\s*20260818'))) {
    Assert-True ([regex]::IsMatch($formalScenariosText, $pattern)) (
        "Frozen scenario contract does not match '$pattern'.")
}

$discoveryScenariosMatch = [regex]::Match(
    $runner,
    '(?s)\$discoveryScenarios\s*=\s*@\((.*?)\)\s*\r?\n\s*if\s*\(\$FormalAcceptanceMode\)')
Assert-True $discoveryScenariosMatch.Success (
    'Runner must expose a separate pre-frozen discovery matrix.')
$discoveryScenariosText = $discoveryScenariosMatch.Groups[1].Value
Assert-True (
    [regex]::Matches(
        $discoveryScenariosText,
        '\bscenarioId\s*=').Count -eq 2) (
    'Discovery matrix must contain exactly two scenarios.')
foreach ($pattern in @(
    ("(?s)discovery-packing-v2-n262144-q64'.*?" +
        'elementCount\s*=\s*262144.*?' +
        'queryCount\s*=\s*64.*?seed\s*=\s*20260731'),
    ("(?s)discovery-packing-v2-n1048576-q256'.*?" +
        'elementCount\s*=\s*1048576.*?' +
        'queryCount\s*=\s*256.*?seed\s*=\s*20260732'))) {
    Assert-True ([regex]::IsMatch($discoveryScenariosText, $pattern)) (
        "Discovery scenario contract does not match '$pattern'.")
}
$formalSeeds = @(
    [regex]::Matches(
        $formalScenariosText,
        '\bseed\s*=\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value })
$discoverySeeds = @(
    [regex]::Matches(
        $discoveryScenariosText,
        '\bseed\s*=\s*(\d+)') |
        ForEach-Object { [int]$_.Groups[1].Value })
Assert-True (
    @($formalSeeds | Where-Object {
        $discoverySeeds -contains $_
    }).Count -eq 0) (
    'Formal data/query seeds must be disjoint from discovery seeds.')

$expectedIdentities = @(
    'Summit.GpuSensorPipeline.Tests.Editor.dll',
    'Summit.GpuSensorPipeline.Tests.GpuSensorDeterministicGeneratorTests',
    'Summit.GpuSensorPipeline.Tests.GpuSensorPipelineContractTests',
    'Summit.GpuSensorPipeline.Tests.GpuSensorPipelineIntegrationTests',
    'Summit.GpuSensorPipeline.Tests.GpuSensorIntensityQuantizerTests',
    'Summit.GpuSensorPipeline.Tests.GpuSensorPackedSoaPipelineContractTests',
    'Summit.GpuSensorPipeline.Tests.GpuSensorPackedSoaPipelineIntegrationTests',
    'Summit.GpuSensorPipeline.Benchmark.Tests.Editor.dll',
    'Summit.GpuSensorPipeline.Benchmark.Tests.GpuSensorDataPackingBenchmarkScheduleTests'
)
$identityMatch = [regex]::Match(
    $runner,
    '(?s)\$expectedEditModeIdentities\s*=\s*@\((.*?)\)\s*\r?\n\s*\$editModeResults')
Assert-True $identityMatch.Success (
    'Runner must expose the exact EditMode identity list.')
$observedIdentities = @(
    [regex]::Matches($identityMatch.Groups[1].Value, "'([^']+)'") |
        ForEach-Object { $_.Groups[1].Value })
Assert-True (
    [string]::Join("`n", $observedIdentities) -ceq
    [string]::Join("`n", $expectedIdentities)) (
    'Runner EditMode identities must exactly cover all nine S5 identities.')
foreach ($identity in $expectedIdentities) {
    Assert-Contains $summarizer $identity (
        'Summarizer exact EditMode identity contract')
}

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
    'GpuSensorPackedSoaPipeline.compute',
    'GpuSensorPackedSoaPipeline.cs',
    'GpuSensorIntensityQuantizer.cs',
    'GpuBenchmarkProvenance.psm1',
    'Summarize-GpuSensorDataPackingBenchmark.ps1',
    'Test-GpuSensorDataPackingBenchmarkProvenance.ps1',
    'SummitGpuTimestamps.dll',
    'runnerToolSha256',
    'summarizerToolSha256',
    'provenanceTestSha256',
    'timestampNativeDllSha256')) {
    Assert-Contains $runner $token 'Runner source/provenance binding'
}
foreach ($token in @(
    'scenarioIsolationContract',
    "discoverySetId = 's5-packing-discovery-v2'",
    "formalHoldoutSetId = 's5-packing-formal-holdout-v2'",
    'dataAndQuerySeedSetsDisjoint = $true',
    'formalHoldoutUsedForDiscovery = $false',
    'Get-CimInstance -Query',
    "Name='Unity.exe'",
    "Name='GpuSensorDataPackingBenchmark.exe'",
    'preflightCompetingGpuProcessCheckPassed',
    'Frozen GPU benchmark preflight rejected competing Unity or',
    'buildRequested = (-not $SkipBuild)',
    'buildExecuted = $buildExecuted',
    'buildStartedUtc',
    'buildEndedUtc',
    'buildExitCode',
    'Get-PlayerRendererMetadata',
    'sourceBindingSatisfied',
    'gpuIdentityContractSatisfied',
    'allPlayerSourceBindingsSatisfied',
    'allPlayerGpuIdentityContractsSatisfied',
    "[string]`$playerConfig.buildCommit -ceq",
    "[string]`$playerConfig.runtimeShaderSha256 -ceq",
    "[string]`$playerConfig.runtimeApiSha256 -ceq",
    "[string]`$playerConfig.nativeTimestampDllSha256 -ceq",
    "graphicsDeviceVendorId = 4098",
    "graphicsDeviceId = 30033",
    "graphicsDeviceName = 'AMD Radeon AI PRO R9700'",
    "rendererIdHex = '0x7551'",
    "driverVersion = '32.0.31035.1003'",
    'luidAvailable = $false',
    'this evidence makes no LUID identity claim.')) {
    Assert-Contains $runner $token 'Runner methodology audit contract'
}
foreach ($token in @(
    '$MinimumWinningPairs = 7',
    'predeclared-engineering-gate-not-statistical-significance',
    'statisticalSignificanceClaim = $false',
    '[int]($formal -and $engineeringGatePassed)',
    '$formal -and',
    'discoveryPerformanceClaimUsable = $false',
    'holdout-packing-v2-n262144-q64',
    'discovery-packing-v2-n262144-q64',
    'Read-PlayerRendererMetadata',
    "[string]`$config.buildCommit -cne",
    "[string]`$config.runtimeShaderSha256 -cne",
    "[string]`$config.runtimeApiSha256 -cne",
    "[string]`$config.nativeTimestampDllSha256 -cne",
    "[int]`$device.graphicsDeviceVendorId -ne 4098",
    "[int]`$device.graphicsDeviceId -ne 30033",
    'AMD Radeon AI PRO R9700',
    '32.0.31035.1003',
    'explicit no-LUID boundary failed.')) {
    Assert-Contains $summarizer $token (
        'Summarizer methodology audit contract')
}
Assert-NotContains $summarizer 'statisticalSignificanceClaim = $true' (
    'Summarizer non-statistical engineering gate')
foreach ($token in @(
    'OutputDirectory is required and must be outside the Git worktree.',
    'Benchmark report output must be outside the Git worktree.',
    'Report directory must be new or empty.',
    'Report output may not be inside the Player payload.')) {
    Assert-Contains $runner $token (
        'Runner external immutable-evidence output contract')
}

foreach ($token in @(
    'GpuSensorDataPackingBenchmarkVariant',
    '.ExpandedAosQuantizedView',
    '.PackedSoaFusedEndCursor',
    'bool abba = (superRound & 1) != 0',
    'FormalSuperRoundCount = 4',
    'FormalStateCount = 64',
    'PreconditionSampleCount = 240',
    'zeroBasedSampleIndex % stateCount',
    'superRoundCount * 4 + 4',
    '"precondition"')) {
    Assert-Contains $schedule $token 'Balanced packing A/B schedule'
}
Assert-Contains $controller 'entry.BlockType == "measurement"' (
    'Only scored measurement blocks may emit pair comparisons.')
Assert-True (
    [regex]::IsMatch(
        $controller,
        '(?s)if \(entry\.BlockType == "measurement"\)\s*\{\s*' +
        'bool capturePassed = false;\s*yield return CaptureBlock')) (
    'Only scored measurement blocks may occupy correctness-capture slots.')
foreach ($token in @(
    '$PreconditioningBlockCount = 2',
    '$PreconditioningSampleFramesPerBlock = 240',
    '$ExpectedBlockCount = 20',
    '$ExpectedScoredMeasurementBlockCount = 16',
    '$ExpectedScoredPairCount = 8',
    'preconditioningExcludedFromScoring = $true',
    'preconditioningGpuTimingsRecorded = $true',
    'control-pre;unscored-precondition-A-B;',
    "'ABBA/BAAB;control-post'")) {
    Assert-Contains $runner $token 'Runner preconditioning contract'
}
foreach ($token in @(
    'preconditioningBlockCount = 2',
    'preconditioningSampleFramesPerBlock =',
    'preconditioningExcludedFromScoring = true',
    'preconditioningGpuTimingsRecorded = true',
    'control-pre;unscored-precondition-A-B;',
    ' balanced scored super-rounds;ABBA/BAAB;control-post')) {
    Assert-Contains $controller $token 'Controller preconditioning contract'
}
foreach ($token in @(
    '$ExpectedPreconditioningBlockCount = 2',
    '$PreconditioningSampleFramesPerBlock = 240',
    '$ExpectedScoredMeasurementBlockCount = 16',
    '$ExpectedBlockCount = 20',
    '$ExpectedPairCount = 8',
    '-not [bool]$runner.preconditioningExcludedFromScoring',
    '-not [bool]$runner.preconditioningGpuTimingsRecorded',
    '-not [bool]$config.preconditioningExcludedFromScoring',
    '-not [bool]$config.preconditioningGpuTimingsRecorded',
    'control-pre;unscored-precondition-A-B;',
    "'ABBA/BAAB;control-post'")) {
    Assert-Contains $summarizer $token 'Summarizer preconditioning contract'
}
foreach ($source in @($runner, $controller, $summarizer)) {
    Assert-Contains $source 'scheduleContract' (
        'Every evidence layer must preserve the named schedule contract.')
}

foreach ($token in @(
    'baselineCaseId',
    'packedCaseId',
    'controlCaseId',
    'baselineProducerLogicalWriteBytesPerFrame',
    'packedProducerLogicalWriteBytesPerFrame',
    'baselinePipelineElementMaterializedWriteBytesPerFrame',
    'packedPipelineElementMaterializedWriteBytesPerFrame',
    'baselineSpatialBuildAddressedReadBytesPerFrame',
    'packedSpatialBuildAddressedReadBytesPerFrame',
    'baselineCountAtomicOperationsPerFrame',
    'packedCountAtomicOperationsPerFrame',
    'baselineScatterReservationAtomicOperationsPerFrame',
    'packedScatterReservationAtomicOperationsPerFrame',
    'baselineTotalAtomicOperationsPerFrame',
    'packedTotalAtomicOperationsPerFrame',
    'baselinePipelineResidentBytes',
    'packedPipelineResidentBytes',
    'baselineIsolatedGpuResidentBytes',
    'packedIsolatedGpuResidentBytes',
    'blockDigestBufferBytes',
    'actualBenchmarkGpuResidentBytes',
    'validationReadbackBytesPerComparison',
    'producerMaterializedWriteBytes',
    'pipelineElementMaterializedWriteBytes',
    'spatialBuildAddressedReadBytes',
    'binCountAtomicOperations',
    'scatterAtomicOperations')) {
    Assert-Contains $controller $token 'Controller schema-12 contract'
    Assert-Contains $summarizer $token 'Summarizer schema-12 contract'
}
Assert-Contains $controller 'schemaVersion = 12' (
    'Controller schema-12 version')
Assert-Contains $controller 'suite = "summit.gpu-sensor-data-packing"' (
    'Controller schema-12 suite')
Assert-Contains $summarizer '$SchemaVersion = 12' (
    'Summarizer schema-12 version')
Assert-Contains $summarizer (
    "`$Suite = 'summit.gpu-sensor-data-packing'") (
    'Summarizer schema-12 suite')
Assert-Contains $controller (
    'profilerMarkers = adapter.EmitsProfilerMarkers') (
    'Controller profiler-marker provenance')
Assert-True (
    [regex]::Matches(
        $adapter,
        'emitProfilerMarkers:\s*true').Count -eq 2) (
    'Both measured pipelines must emit GPU profiler markers.')

foreach ($coverage in @(
    'combined-packed-soa-q16-producer-count-offset-cursor-',
    'fusion-lazy-payload',
    '64 aggregate frame digests; no full-buffer readback',
    'in-place end-offset/count/membership plus ',
    'count/xor/sum/mixed-sum invariants',
    'pipeline-owned GraphicsBuffers plus block digests; ',
    'excludes timestamp/command/driver allocations')) {
    Assert-Contains $runner $coverage 'Runner attribution/coverage contract'
    Assert-Contains $controller $coverage (
        'Controller attribution/coverage contract')
    Assert-Contains $summarizer $coverage (
        'Summarizer attribution/coverage contract')
}
Assert-Contains $runner 'profilerMarkers = $true' (
    'Runner profiler-marker contract')
Assert-Contains $summarizer '-not [bool]$config.profilerMarkers' (
    'Summarizer profiler-marker contract')

foreach ($token in @(
    'BaselineCaseId',
    'PackedCaseId',
    'ControlCaseId',
    'WorkloadReadbackBytesPerFrame = 0L',
    'ValidationReadbackBytesPerComparison',
    'BaselinePipelineResidentBytes',
    'PackedPipelineResidentBytes',
    'BaselineIsolatedGpuResidentBytes',
    'PackedIsolatedGpuResidentBytes',
    'ActualBenchmarkGpuResidentBytes',
    'BaselineProducerLogicalWriteBytesPerFrame',
    'PackedProducerLogicalWriteBytesPerFrame',
    'BaselinePipelineElementMaterializedWriteBytesPerFrame',
    'PackedPipelineElementMaterializedWriteBytesPerFrame',
    'BaselineSpatialBuildAddressedReadBytesPerFrame',
    'PackedSpatialBuildAddressedReadBytesPerFrame',
    'BaselineCountAtomicOperationsPerFrame',
    'PackedFusedCountAtomicOperationsPerFrame',
    'BaselineScatterReservationAtomicOperationsPerFrame',
    'PackedScatterReservationAtomicOperationsPerFrame',
    'BaselineTotalAtomicOperationsPerFrame',
    'PackedTotalAtomicOperationsPerFrame',
    'BeginCaptureBlock',
    'BeginCompareCapturedBlocks',
    'TryCompleteComparison',
    'PackedSampleMismatchCount',
    'PackedCsrMismatchCount')) {
    Assert-Contains $adapter $token 'Adapter S5 accounting/validation API'
}

foreach ($token in @(
    'GpuProducerLogicalWriteBytes',
    'RecordGpuProducedQuantizedView')) {
    Assert-Contains $baselineRuntime $token 'Baseline quantized-view API'
}
foreach ($token in @(
    'PackedDynamicBytes',
    'PipelineElementMaterializedWriteBytes',
    'SpatialBuildAddressedReadBytes',
    'FusedCountAtomicOperations',
    'ScatterReservationAtomicOperations',
    'ValidationWordCount = 8',
    'BinOffsetsRwId',
    'BinnedIdsRwId')) {
    Assert-Contains $packedRuntime $token 'Packed SoA runtime contract'
}
Assert-NotContains $packedRuntime 'WriteHeads' (
    'Packed runtime must not retain a separate scatter cursor buffer.')
foreach ($token in @(
    'MaxRawError = 32768',
    '65537')) {
    Assert-Contains $quantizer $token 'Quantizer error contract'
}
foreach ($token in @(
    '#pragma kernel ProducePackedSoaAndCount',
    '#pragma kernel ScatterPackedIds',
    '#pragma kernel RangeQueryPackedSoaDigest',
    '#pragma kernel ValidatePackedSamples',
    '#pragma kernel ValidateCsrBins',
    'RWStructuredBuffer<uint> _BinOffsetsRW',
    'RWStructuredBuffer<uint> _BinnedIdsRW',
    'StructuredBuffer<uint> _BinOffsets',
    'StructuredBuffer<uint> _BinnedIds',
    'if (key > 0u)',
    'if (bin > 0u)',
    'begin = _BinOffsets[key - 1u];',
    'uint end = _BinOffsets[key];',
    'begin = _BinOffsets[bin - 1u];',
    'uint end = _BinOffsets[bin];')) {
    Assert-Contains $packedShader $token 'Packed SoA shader contract'
}
Assert-True (
    [regex]::Matches(
        $packedShader,
        '(?s)InterlockedAdd\(\s*_BinOffsetsRW\[ComputeKey\(').Count -eq 2) (
    'Both packed lanes must atomically advance the in-place end cursor.')
Assert-NotContains $packedShader 'PrepareOffsetsAndWriteHeads' (
    'Packed shader must not retain the prepare/copy kernel.')
Assert-NotContains $packedShader '_WriteHeads' (
    'Packed shader must not retain a separate scatter cursor buffer.')
Assert-NotContains $packedShader 'key == 0u ?' (
    'Bin-zero query access must use an explicit short-circuit guard.')
Assert-NotContains $packedShader 'bin == 0u ?' (
    'Bin-zero validation access must use an explicit short-circuit guard.')

$claimFields = @(
    'hostUploadEliminationClaim',
    'uploadQueueCoverageVerified',
    'asyncComputeClaim',
    'copyQueueClaim',
    'pcieTrafficClaim',
    'measuredDramTrafficClaim',
    'driverReportedVramClaim',
    'endToEndSensorLatencyClaim',
    'liveSensorInputClaim',
    'sensorFidelityClaim',
    'citySceneClaim',
    'fpsClaim',
    'nvidiaValidationClaim')
foreach ($claim in $claimFields) {
    Assert-Contains $runner "$claim = `$false" (
        'Runner conservative claim boundary')
    Assert-Contains $controller "$claim = false" (
        'Controller conservative claim boundary')
    Assert-Contains $summarizer "$claim = `$false" (
        'Summarizer conservative claim boundary')
}
foreach ($token in @(
    'Algorithmic shader-visible typed buffer operations; not',
    'measured DRAM transactions.',
    'Explicit GraphicsBuffer capacities; not driver allocation.',
    'logicalAccountingEvidenceUsable',
    'workloadReadbackBytesPerFrame = 0',
    'validationReadbackBytesPerComparison',
    'positive-packed-soa-fused-end-cursor-faster',
    'pair-summary.csv',
    'matrix-summary.csv',
    'quality-summary.json',
    'quality-summary.txt',
    'scenario-summary.csv')) {
    Assert-Contains $summarizer $token 'Summary evidence/claim boundary'
}
foreach ($formula in @(
    '20L * $elementCount',
    '8L * $elementCount',
    '24L * $elementCount',
    '12L * $elementCount',
    '6L * $elementCount',
    '2L * $elementCount')) {
    Assert-Contains $summarizer $formula (
        'Smoke-verified exact algorithmic accounting')
}

foreach ($staleToken in @(
    "'amd-r9700-dynamic-v1'",
    "'amd-r9700-dynamic-discovery-v1'",
    "'dynamic-n262144-q64'",
    "'dynamic-n1048576-q256'",
    "'cpu-produced-uploaded'",
    "'gpu-produced-resident'",
    'sensor-pipeline/cpu-produced-uploaded-v1',
    'sensor-pipeline/gpu-produced-resident-v1',
    'logicalUploadBytes',
    'gpuProducerLogicalWriteBytes',
    'stagingSlotCount',
    'hostUploadEliminationClaimUsable',
    'benchmarkPlanSha256',
    'GPU_DYNAMIC_SENSOR_PIPELINE_BENCHMARK_PLAN',
    "'amd-r9700-packing-v1'",
    "'amd-r9700-packing-discovery-v1'",
    "'s5-packing-discovery-v1'",
    "'s5-packing-formal-holdout-v1'",
    "'holdout-packing-n262144-q64'",
    "'holdout-packing-n1048576-q256'",
    "'discovery-packing-n262144-q64'",
    "'discovery-packing-n1048576-q256'",
    "'packed-soa-fused'",
    'sensor-data-packing/packed-soa-fused-v1',
    'GPU.SensorDataPacking/PackedSoAFused/MainGraphics',
    'combined-packed-soa-q16-producer-count-fusion-lazy-payload',
    'offset/count/write-head/membership plus ',
    'positive-packed-soa-fused-faster')) {
    Assert-NotContains $powerShellHarness $staleToken (
        'S5 PowerShell harness')
}
Assert-True (
    -not [regex]::IsMatch(
        $powerShellHarness,
        '\b\d+(?:UL|LU|U)\b')) (
    'PowerShell harness must not contain C# unsigned numeric suffixes.')

$tamperRoot = Join-Path (
    [System.IO.Path]::GetTempPath()) (
    'summit-sensor-packing-schema-tamper-' +
    [Guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($tamperRoot)
try {
    [ordered]@{
        schemaVersion = 11
        benchmarkSchemaVersion = 11
        suite = 'summit.gpu-sensor-data-packing'
    } |
        ConvertTo-Json |
        Set-Content -LiteralPath (
            Join-Path $tamperRoot 'runner-config.json') -Encoding utf8
    'scenarioId,elementCount,binCount,queryCount,logicalStateCount,' +
        'seed,processId,status,passed,rawSampleCount' |
        Set-Content -LiteralPath (
            Join-Path $tamperRoot 'matrix.csv') -Encoding utf8
    [ordered]@{
        sha256 = ('0' * 64)
        fileCount = 0
        lengthBytes = 0
    } |
        ConvertTo-Json |
        Set-Content -LiteralPath (
            Join-Path $tamperRoot 'player-payload-manifest.json') `
            -Encoding utf8

    $savedPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $tamperOutput = & powershell.exe `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $summarizerPath `
        -ReportDirectory $tamperRoot 2>&1
    $tamperExitCode = $LASTEXITCODE
    $ErrorActionPreference = $savedPreference
    $tamperText =
        @($tamperOutput | ForEach-Object { [string]$_ }) -join "`n"
    Assert-True ($tamperExitCode -ne 0) (
        'Summarizer must reject schema-11 evidence.')
    Assert-True (
        $tamperText.Contains(
            'Runner schema/suite contract does not match data-packing v12.')) (
        'Schema-version rejection must be explicit.')
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
    -MatrixPreset 'amd-r9700-packing-v2' `
    -DeviceIndex 0 `
    -SuperRounds 4 `
    -WarmupFrames 60 `
    -SampleFrames 900 `
    -CooldownFrames 15 `
    -CommandSlotCount 4 `
    -PlayerTimeoutMinutes 89 `
    -FormalAcceptanceMode `
    -EditModeResultsPath 'intentionally-supplied.xml' `
    -SkipSummary 2>&1
$formalExitCode = $LASTEXITCODE
$ErrorActionPreference = $savedPreference
$formalOutputText =
    @($formalOutput | ForEach-Object { [string]$_ }) -join "`n"
Assert-True ($formalExitCode -ne 0) (
    'Formal runner must reject bypasses before Unity starts.')
Assert-True ($formalOutputText.Contains('SkipSummary is forbidden.')) (
    'Formal SkipSummary rejection must be explicit.')
Assert-True (
    $formalOutputText.Contains(
        'EditModeResultsPath is discovery-only; formal evidence is generated in-run.')) (
    'Formal runner must reject caller-supplied EditMode evidence.')
Assert-True (
    $formalOutputText.Contains(
        'PlayerTimeoutMinutes=89; expected 90')) (
    'Formal runner must reject a non-frozen Player timeout explicitly.')

$savedPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$discoveryOutput = & powershell.exe `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $runnerPath `
    -MatrixPreset 'amd-r9700-packing-discovery-v2' `
    -DeviceIndex 1 `
    -SuperRounds 4 `
    -WarmupFrames 60 `
    -SampleFrames 240 `
    -CooldownFrames 15 `
    -CommandSlotCount 4 `
    -ValidationTimeoutSeconds 60 2>&1
$discoveryExitCode = $LASTEXITCODE
$ErrorActionPreference = $savedPreference
$discoveryText =
    @($discoveryOutput | ForEach-Object { [string]$_ }) -join "`n"
Assert-True ($discoveryExitCode -ne 0) (
    'Frozen discovery must reject a nonzero device index before Unity.')
Assert-True (
    $discoveryText.Contains('DeviceIndex=1; expected 0')) (
    'Frozen discovery device-index rejection must be explicit.')

$savedPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$skipBuildOutput = & powershell.exe `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $runnerPath `
    -MatrixPreset 'amd-r9700-packing-discovery-v2' `
    -DeviceIndex 0 `
    -SuperRounds 4 `
    -WarmupFrames 60 `
    -SampleFrames 240 `
    -CooldownFrames 15 `
    -CommandSlotCount 4 `
    -ValidationTimeoutSeconds 60 `
    -SkipBuild 2>&1
$skipBuildExitCode = $LASTEXITCODE
$ErrorActionPreference = $savedPreference
$skipBuildText =
    @($skipBuildOutput | ForEach-Object { [string]$_ }) -join "`n"
Assert-True ($skipBuildExitCode -ne 0) (
    'Frozen discovery must reject SkipBuild before preflight/Unity.')
Assert-True ($skipBuildText.Contains('SkipBuild is forbidden.')) (
    'Frozen discovery SkipBuild rejection must be explicit.')

Write-Host (
    'PASS: GPU sensor data-packing benchmark provenance/schema tests; ' +
    "assertions=$assertions")
