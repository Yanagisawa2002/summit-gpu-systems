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
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )
    Assert-True ($Text.Contains($Expected)) (
        "$Label must contain '$Expected'.")
}

function Assert-Parses {
    param([Parameter(Mandatory = $true)][string]$Path)
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$errors)
    $errorText = @($errors | ForEach-Object { $_.Message }) -join '; '
    Assert-True ($errors.Count -eq 0) (
        "$Path must parse without PowerShell errors: $errorText")
}

$runnerPath =
    Join-Path $ProjectRoot 'Tools\Run-GpuAdaptiveBinningBenchmark.ps1'
$summarizerPath =
    Join-Path $ProjectRoot 'Tools\Summarize-GpuAdaptiveBinningBenchmark.ps1'
$provenancePath =
    Join-Path $ProjectRoot 'Tools\GpuBenchmarkProvenance.psm1'
$schedulePath = Join-Path $ProjectRoot (
    'Assets\GpuAdaptiveBinningBenchmark\Runtime\' +
    'GpuAdaptiveBinningBenchmarkSchedule.cs')
$adapterPath = Join-Path $ProjectRoot (
    'Assets\GpuAdaptiveBinningBenchmark\Runtime\' +
    'GpuAdaptiveBinningBenchmarkAdapter.cs')
$controllerPath = Join-Path $ProjectRoot (
    'Assets\GpuAdaptiveBinningBenchmark\Runtime\' +
    'GpuAdaptiveBinningBenchmarkController.cs')
$oraclePath = Join-Path $ProjectRoot (
    'Assets\GpuAdaptiveBinningBenchmark\Runtime\' +
    'GpuAdaptiveBinningCpuOracle.cs')
$discoveryDocPath = Join-Path $ProjectRoot (
    'Docs\GPU_ADAPTIVE_BINNING_AMD_R9700_DISCOVERY_' +
    '2026-07-31.md')

foreach ($path in @(
    $runnerPath,
    $summarizerPath,
    $provenancePath,
    $schedulePath,
    $adapterPath,
    $controllerPath,
    $oraclePath,
    $discoveryDocPath)) {
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) (
        "Required harness file must exist: $path")
}
Assert-Parses $runnerPath
Assert-Parses $summarizerPath
Assert-Parses $provenancePath

$runnerTokens = $null
$runnerErrors = $null
$runnerAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $runnerPath,
    [ref]$runnerTokens,
    [ref]$runnerErrors)
$runnerProbeFunctionNames = @(
    'Get-GeneratorV3SingleBinKey',
    'ConvertTo-GpuAdaptiveBinningScenario')
$runnerProbeFunctionAsts = @(
    $runnerAst.FindAll({
        param($node)
        $node -is
            [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -in $runnerProbeFunctionNames
    }, $true))
Assert-True ($runnerProbeFunctionAsts.Count -eq 2) (
    'Runner must expose generator-v3 and pure scenario-normalization helpers.')
. ([scriptblock]::Create(
    @($runnerProbeFunctionAsts | ForEach-Object {
        $_.Extent.Text
    }) -join "`n"))
$contentionNormalizationInputs = @(
    [ordered]@{ scenarioId='singlebin-n262144-c16'; elementCount=262144; binCount=16; distribution='singlebin'; seed=20261001; exactSingleBinKey=9 },
    [ordered]@{ scenarioId='singlebin-n1048576-c16'; elementCount=1048576; binCount=16; distribution='singlebin'; seed=20261002; exactSingleBinKey=10 },
    [ordered]@{ scenarioId='hotset4-n1048576-c16'; elementCount=1048576; binCount=16; distribution='hotset4'; seed=20261002; exactSingleBinKey=-1 },
    [ordered]@{ scenarioId='uniform-n1048576-c16'; elementCount=1048576; binCount=16; distribution='uniform'; seed=20261002; exactSingleBinKey=-1 },
    [ordered]@{ scenarioId='uniform-n1048576-c65536'; elementCount=1048576; binCount=65536; distribution='uniform'; seed=20261005; exactSingleBinKey=-1 })
$normalizedContentionRows = @(
    $contentionNormalizationInputs | ForEach-Object {
        $scenario = $_
        ConvertTo-GpuAdaptiveBinningScenario `
            -Scenario $scenario `
            -MatrixPreset 'formal-amd-r9700-contention-v1'
    })
$normalizedContentionTuples = @(
    $normalizedContentionRows | ForEach-Object {
        "$($_.scenarioId)|$($_.elementCount)|$($_.binCount)|" +
        "$($_.distribution)|$($_.seed)|$($_.exactSingleBinKey)|" +
        "$($_.dominantSetCardinality)|$($_.bracketGroup)"
    })
$expectedContentionTuples = @(
    'singlebin-n262144-c16|262144|16|singlebin|20261001|9|1|',
    'singlebin-n1048576-c16|1048576|16|singlebin|20261002|10|1|n1048576-c16-contention',
    'hotset4-n1048576-c16|1048576|16|hotset4|20261002|-1|4|n1048576-c16-contention',
    'uniform-n1048576-c16|1048576|16|uniform|20261002|-1|16|n1048576-c16-contention',
    'uniform-n1048576-c65536|1048576|65536|uniform|20261005|-1|65536|')
Assert-True ($normalizedContentionRows.Count -eq 5) (
    'Executable runner normalization probe must cover all five contention rows.')
Assert-True (
    ($normalizedContentionTuples -join ';') -ceq
    ($expectedContentionTuples -join ';')) (
    'Runner must normalize the exact five-row contention matrix, including ' +
    'uniform C16/C65536 cardinalities, without switch-variable rebinding.')

$summaryTokens = $null
$summaryErrors = $null
$summaryAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $summarizerPath,
    [ref]$summaryTokens,
    [ref]$summaryErrors)
$probeFunctionNames = @(
    'Improvement',
    'Expected-Blocks',
    'Expected-ScheduleContract',
    'Test-SignChangingAcceptedWinner',
    'Test-CrossoverClaimUsable',
    'Test-SelectorDirectionValidated',
    'Test-SelectorPolicyClaimUsable',
    'Test-SelectorTailCellValidated',
    'Test-SelectorPolicyTailClaimUsable',
    'Test-FormalActiveDevice',
    'Get-GeneratorV3SingleBinKey',
    'Test-ExactSingleBinKeyContract',
    'Get-ExactCellSelectorPrediction',
    'Test-ExactCellSelectorPolicyContract',
    'Test-RequiredProfilerMarkersDisabled')
$probeFunctionAsts = @(
    $summaryAst.FindAll({
        param($node)
        $node -is
            [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -in $probeFunctionNames
    }, $true))
Assert-True ($probeFunctionAsts.Count -eq 15) (
    'Summarizer must expose fifteen executable schedule/policy/tail/device/exact-cell helpers.')
$probeSource = @(
    $probeFunctionAsts | ForEach-Object { $_.Extent.Text }) -join "`n"
. ([scriptblock]::Create($probeSource))
$probeBlocks = @(Expected-Blocks -SuperRounds 4)
$probeMeasurement = @(
    $probeBlocks | Where-Object { $_.blockType -ceq 'measurement' })
$probePairOrders = @(
    $probeMeasurement | Group-Object pairIndex | ForEach-Object {
        @($_.Group | Select-Object -ExpandProperty pairOrder -Unique)[0]
    })
$scheduleProbe = [pscustomobject]@{
    blockCount = $probeBlocks.Count
    measurementBlockCount = $probeMeasurement.Count
    pairCount = @(
        $probeMeasurement.pairIndex | Select-Object -Unique).Count
    abPairCount = @(
        $probePairOrders | Where-Object { $_ -ceq 'AB' }).Count
    baPairCount = @(
        $probePairOrders | Where-Object { $_ -ceq 'BA' }).Count
    directBlockCount = @(
        $probeMeasurement | Where-Object {
            $_.variant -ceq 'direct-trusted-count-scan-scatter-wave-ops'
        }).Count
    radixBlockCount = @(
        $probeMeasurement | Where-Object {
            $_.variant -ceq 'radix-low-bit-wave-ops'
        }).Count
    scheduleContract = Expected-ScheduleContract -SuperRounds 4
    signedRadixFasterPercent = Improvement 10.0 8.0
    signedDirectFasterPercent = Improvement 8.0 10.0
}
Assert-True ($scheduleProbe.blockCount -eq 18) (
    'Four super-rounds must produce 18 blocks including two controls.')
Assert-True ($scheduleProbe.measurementBlockCount -eq 16) (
    'Four super-rounds must produce 16 measurement blocks.')
Assert-True ($scheduleProbe.pairCount -eq 8) (
    'Four super-rounds must produce exactly eight adjacent A/B pairs.')
Assert-True (
    $scheduleProbe.abPairCount -eq 4 -and
    $scheduleProbe.baPairCount -eq 4) (
    'The schedule must balance four AB and four BA pairs.')
Assert-True (
    $scheduleProbe.directBlockCount -eq 8 -and
    $scheduleProbe.radixBlockCount -eq 8) (
    'The schedule must balance eight Direct and eight Radix blocks.')
Assert-True ($scheduleProbe.scheduleContract -ceq
    'control-pre;ABBA;BAAB;ABBA;BAAB;control-post') (
    'The executable schedule contract must match the frozen v1 sequence.')
Assert-True ([Math]::Abs(
    $scheduleProbe.signedRadixFasterPercent - 20.0) -lt 1.0e-12) (
    'Signed improvement must be positive when Radix is faster.')
Assert-True ([Math]::Abs(
    $scheduleProbe.signedDirectFasterPercent - (-25.0)) -lt 1.0e-12) (
    'Signed improvement must be negative when Direct is faster.')

$exactPolicyLabel = 'radix-if-exact-calibrated-cell-else-direct'
$exactPolicyPredicate =
    'full-element-count-bin-count-distribution-exact-single-bin-key-tuple'
$exactPolicyCells = @(
    [pscustomobject]@{ elementCount=262144; binCount=16; distribution='singlebin'; exactSingleBinKey=9 },
    [pscustomobject]@{ elementCount=1048576; binCount=16; distribution='singlebin'; exactSingleBinKey=10 })
$wrongKeyPolicyCells = @(
    [pscustomobject]@{ elementCount=262144; binCount=16; distribution='singlebin'; exactSingleBinKey=10 },
    [pscustomobject]@{ elementCount=1048576; binCount=16; distribution='singlebin'; exactSingleBinKey=10 })
$missingKeyPolicyCells = @(
    [pscustomobject]@{ elementCount=262144; binCount=16; distribution='singlebin' },
    [pscustomobject]@{ elementCount=1048576; binCount=16; distribution='singlebin'; exactSingleBinKey=10 })
$exactSmallKey = Get-GeneratorV3SingleBinKey -Seed 20261001 -BinCount 16
$exactLargeKey = Get-GeneratorV3SingleBinKey -Seed 20261002 -BinCount 16

$predicateProbe = [pscustomobject]@{
    directToRadix = Test-SignChangingAcceptedWinner `
        -LowerWinner direct -UpperWinner radix
    radixToDirect = Test-SignChangingAcceptedWinner `
        -LowerWinner radix -UpperWinner direct
    sameWinner = Test-SignChangingAcceptedWinner `
        -LowerWinner direct -UpperWinner direct
    neutralEndpoint = Test-SignChangingAcceptedWinner `
        -LowerWinner none -UpperWinner radix
    allCrossoverGates = Test-CrossoverClaimUsable `
        -ABDataUsable $true `
        -RadixAcceptedCellCount 2 `
        -DirectAcceptedCellCount 2 `
        -BracketEvidenceComplete $true `
        -SignChangingBracketObserved $true
    invalidAbData = Test-CrossoverClaimUsable `
        -ABDataUsable $false `
        -RadixAcceptedCellCount 2 `
        -DirectAcceptedCellCount 2 `
        -BracketEvidenceComplete $true `
        -SignChangingBracketObserved $true
    insufficientRadixCells = Test-CrossoverClaimUsable `
        -ABDataUsable $true `
        -RadixAcceptedCellCount 1 `
        -DirectAcceptedCellCount 2 `
        -BracketEvidenceComplete $true `
        -SignChangingBracketObserved $true
    insufficientDirectCells = Test-CrossoverClaimUsable `
        -ABDataUsable $true `
        -RadixAcceptedCellCount 2 `
        -DirectAcceptedCellCount 1 `
        -BracketEvidenceComplete $true `
        -SignChangingBracketObserved $true
    incompleteBracket = Test-CrossoverClaimUsable `
        -ABDataUsable $true `
        -RadixAcceptedCellCount 2 `
        -DirectAcceptedCellCount 2 `
        -BracketEvidenceComplete $false `
        -SignChangingBracketObserved $true
    noSignChange = Test-CrossoverClaimUsable `
        -ABDataUsable $true `
        -RadixAcceptedCellCount 2 `
        -DirectAcceptedCellCount 2 `
        -BracketEvidenceComplete $true `
        -SignChangingBracketObserved $false
    requiredSelectorDirection = Test-SelectorDirectionValidated `
        -LowerWinner radix -UpperWinner direct
    reversedSelectorDirection = Test-SelectorDirectionValidated `
        -LowerWinner direct -UpperWinner radix
    sameSelectorDirection = Test-SelectorDirectionValidated `
        -LowerWinner radix -UpperWinner radix
    selectorPolicyAllGates = Test-SelectorPolicyClaimUsable `
        -ABDataUsable $true `
        -RadixAcceptedCellCount 2 `
        -DirectAcceptedCellCount 3 `
        -BracketEvidenceComplete $true `
        -SelectorDirectionValidatedCount 2 `
        -PolicyCellCount 5 `
        -PredictionMatchCount 5
    selectorPolicyReversed = Test-SelectorPolicyClaimUsable `
        -ABDataUsable $true `
        -RadixAcceptedCellCount 2 `
        -DirectAcceptedCellCount 3 `
        -BracketEvidenceComplete $true `
        -SelectorDirectionValidatedCount 0 `
        -PolicyCellCount 5 `
        -PredictionMatchCount 5
    selectorPolicyPredictionMismatch = Test-SelectorPolicyClaimUsable `
        -ABDataUsable $true `
        -RadixAcceptedCellCount 2 `
        -DirectAcceptedCellCount 3 `
        -BracketEvidenceComplete $true `
        -SelectorDirectionValidatedCount 2 `
        -PolicyCellCount 5 `
        -PredictionMatchCount 4
    tailCellAtBoundary = Test-SelectorTailCellValidated `
        -PredictionMatchesWinner $true `
        -WinnerAccepted $true `
        -WinnerP99WinningPairCount 7 `
        -WorstPairP99ImprovementPercent (-10.0)
    tailCellSixOfEight = Test-SelectorTailCellValidated `
        -PredictionMatchesWinner $true `
        -WinnerAccepted $true `
        -WinnerP99WinningPairCount 6 `
        -WorstPairP99ImprovementPercent (-10.0)
    tailCellWorstPairRegression = Test-SelectorTailCellValidated `
        -PredictionMatchesWinner $true `
        -WinnerAccepted $true `
        -WinnerP99WinningPairCount 8 `
        -WorstPairP99ImprovementPercent (-10.01)
    tailCellPredictionMismatch = Test-SelectorTailCellValidated `
        -PredictionMatchesWinner $false `
        -WinnerAccepted $true `
        -WinnerP99WinningPairCount 8 `
        -WorstPairP99ImprovementPercent 1.0
    selectorTailAllCells = Test-SelectorPolicyTailClaimUsable `
        -SelectorPolicyClaimUsable $true `
        -PolicyCellCount 5 `
        -TailValidatedCellCount 5
    selectorTailAverageClaimFailed = Test-SelectorPolicyTailClaimUsable `
        -SelectorPolicyClaimUsable $false `
        -PolicyCellCount 5 `
        -TailValidatedCellCount 5
    selectorTailOneCellFailed = Test-SelectorPolicyTailClaimUsable `
        -SelectorPolicyClaimUsable $true `
        -PolicyCellCount 5 `
        -TailValidatedCellCount 4
    formalDeviceExact = Test-FormalActiveDevice `
        -GraphicsDeviceVendorId 0x1002 `
        -GraphicsDeviceId 0x7551 `
        -GraphicsDeviceType Direct3D12
    formalDeviceWrongVendor = Test-FormalActiveDevice `
        -GraphicsDeviceVendorId 0x10DE `
        -GraphicsDeviceId 0x7551 `
        -GraphicsDeviceType Direct3D12
    formalDeviceWrongId = Test-FormalActiveDevice `
        -GraphicsDeviceVendorId 0x1002 `
        -GraphicsDeviceId 0x7550 `
        -GraphicsDeviceType Direct3D12
    formalDeviceWrongApi = Test-FormalActiveDevice `
        -GraphicsDeviceVendorId 0x1002 `
        -GraphicsDeviceId 0x7551 `
        -GraphicsDeviceType Direct3D11
}
$exactCellProbe = [pscustomobject]@{
    generatorSmallKey = $exactSmallKey
    generatorLargeKey = $exactLargeKey
    validSmallKey = Test-ExactSingleBinKeyContract -Distribution singlebin -Seed 20261001 -BinCount 16 -ExactSingleBinKey 9
    validLargeKey = Test-ExactSingleBinKeyContract -Distribution singlebin -Seed 20261002 -BinCount 16 -ExactSingleBinKey 10
    missingKey = Test-ExactSingleBinKeyContract -Distribution singlebin -Seed 20261001 -BinCount 16 -ExactSingleBinKey $null
    mismatchedKey = Test-ExactSingleBinKeyContract -Distribution singlebin -Seed 20261001 -BinCount 16 -ExactSingleBinKey 10
    validNonSingleSentinel = Test-ExactSingleBinKeyContract -Distribution uniform -Seed 20261002 -BinCount 16 -ExactSingleBinKey (-1)
    wrongNonSingleSentinel = Test-ExactSingleBinKeyContract -Distribution uniform -Seed 20261002 -BinCount 16 -ExactSingleBinKey 0
    exactSmallPrediction = Get-ExactCellSelectorPrediction -ElementCount 262144 -BinCount 16 -Distribution singlebin -ExactSingleBinKey 9
    exactLargePrediction = Get-ExactCellSelectorPrediction -ElementCount 1048576 -BinCount 16 -Distribution singlebin -ExactSingleBinKey 10
    midpointPrediction = Get-ExactCellSelectorPrediction -ElementCount 524288 -BinCount 16 -Distribution singlebin -ExactSingleBinKey 10
    wrongKeyPrediction = Get-ExactCellSelectorPrediction -ElementCount 262144 -BinCount 16 -Distribution singlebin -ExactSingleBinKey 10
    exactPolicyContract = Test-ExactCellSelectorPolicyContract -PolicyLabel $exactPolicyLabel -PolicyPredicate $exactPolicyPredicate -ExactRadixCandidateCells $exactPolicyCells
    wrongPolicyLabel = Test-ExactCellSelectorPolicyContract -PolicyLabel 'radix-if-dominant-set-cardinality-eq-1-else-direct' -PolicyPredicate $exactPolicyPredicate -ExactRadixCandidateCells $exactPolicyCells
    wrongPolicyKey = Test-ExactCellSelectorPolicyContract -PolicyLabel $exactPolicyLabel -PolicyPredicate $exactPolicyPredicate -ExactRadixCandidateCells $wrongKeyPolicyCells
    missingPolicyKey = Test-ExactCellSelectorPolicyContract -PolicyLabel $exactPolicyLabel -PolicyPredicate $exactPolicyPredicate -ExactRadixCandidateCells $missingKeyPolicyCells
    markersDisabled = Test-RequiredProfilerMarkersDisabled -FieldPresent $true -Value $false
    markersEnabled = Test-RequiredProfilerMarkersDisabled -FieldPresent $true -Value $true
    markersMissing = Test-RequiredProfilerMarkersDisabled -FieldPresent $false -Value $null
    markersStringFalse = Test-RequiredProfilerMarkersDisabled -FieldPresent $true -Value 'false'
}

Assert-True (
    $predicateProbe.directToRadix -and
    $predicateProbe.radixToDirect) (
    'Accepted Direct/Radix winners must form a sign-changing bracket.')
Assert-True (
    -not $predicateProbe.sameWinner -and
    -not $predicateProbe.neutralEndpoint) (
    'Same or neutral winners must not form a sign-changing bracket.')
Assert-True ($predicateProbe.allCrossoverGates) (
    'Crossover must be usable when every frozen gate passes.')
Assert-True (-not $predicateProbe.invalidAbData) (
    'Crossover must require formally usable A/B data.')
Assert-True (-not $predicateProbe.insufficientRadixCells) (
    'Crossover must require at least two accepted Radix cells.')
Assert-True (-not $predicateProbe.insufficientDirectCells) (
    'Crossover must require at least two accepted Direct cells.')
Assert-True (-not $predicateProbe.incompleteBracket) (
    'Crossover must require complete bracket evidence.')
Assert-True (-not $predicateProbe.noSignChange) (
    'Crossover must require an observed accepted-winner sign change.')
Assert-True ($predicateProbe.requiredSelectorDirection) (
    'Selector direction must accept Radix at the lower endpoint and Direct at the upper endpoint.')
Assert-True (
    -not $predicateProbe.reversedSelectorDirection -and
    -not $predicateProbe.sameSelectorDirection) (
    'Selector direction must reject reversed and same-winner brackets.')
Assert-True ($predicateProbe.selectorPolicyAllGates) (
    'Selector policy claim must pass only when both exact brackets and all five predictions match.')
Assert-True (-not $predicateProbe.selectorPolicyReversed) (
    'Selector policy claim must reject a reversed crossover direction.')
Assert-True (-not $predicateProbe.selectorPolicyPredictionMismatch) (
    'Selector policy claim must reject any prediction/forced-winner mismatch.')
Assert-True ($predicateProbe.tailCellAtBoundary) (
    'Tail gate must accept exactly 7/8 P99 wins with a -10% worst pair.')
Assert-True (
    -not $predicateProbe.tailCellSixOfEight -and
    -not $predicateProbe.tailCellWorstPairRegression -and
    -not $predicateProbe.tailCellPredictionMismatch) (
    'Tail gate must reject 6/8 wins, worse than -10%, and policy mismatches.')
Assert-True ($predicateProbe.selectorTailAllCells) (
    'Selector tail claim must require every one of the five cells to pass.')
Assert-True (
    -not $predicateProbe.selectorTailAverageClaimFailed -and
    -not $predicateProbe.selectorTailOneCellFailed) (
    'Selector tail claim must fail independently when policy or one cell fails.')
Assert-True ($predicateProbe.formalDeviceExact) (
    'Formal device gate must accept the AMD R9700 Direct3D12 identity.')
Assert-True (
    -not $predicateProbe.formalDeviceWrongVendor -and
    -not $predicateProbe.formalDeviceWrongId -and
    -not $predicateProbe.formalDeviceWrongApi) (
    'Formal device gate must reject vendor, device-ID, or graphics-API drift.')
Assert-True (
    $exactCellProbe.generatorSmallKey -eq 9 -and
    $exactCellProbe.generatorLargeKey -eq 10) (
    'Generator v3 must map the two formal seeds to exact keys 9 and 10.')
Assert-True (
    $exactCellProbe.validSmallKey -and
    $exactCellProbe.validLargeKey -and
    $exactCellProbe.validNonSingleSentinel) (
    'Exact-key validation must accept both formal keys and non-singlebin -1.')
Assert-True (
    -not $exactCellProbe.missingKey -and
    -not $exactCellProbe.mismatchedKey -and
    -not $exactCellProbe.wrongNonSingleSentinel) (
    'Exact-key validation must reject missing, mismatched, and wrong sentinel values.')
Assert-True (
    $exactCellProbe.exactSmallPrediction -ceq 'radix' -and
    $exactCellProbe.exactLargePrediction -ceq 'radix') (
    'Only the two exact calibrated tuples must predict Radix.')
Assert-True (
    $exactCellProbe.midpointPrediction -ceq 'direct' -and
    $exactCellProbe.wrongKeyPrediction -ceq 'direct') (
    'Midpoint N and same-shape wrong-key cells must predict Direct.')
Assert-True ($exactCellProbe.exactPolicyContract) (
    'The exact two-cell selector policy contract must validate.')
Assert-True (
    -not $exactCellProbe.wrongPolicyLabel -and
    -not $exactCellProbe.wrongPolicyKey -and
    -not $exactCellProbe.missingPolicyKey) (
    'Policy validation must reject the old label and wrong/missing candidate keys.')
Assert-True ($exactCellProbe.markersDisabled) (
    'Profiler-marker contract must accept an explicitly present Boolean false.')
Assert-True (
    -not $exactCellProbe.markersEnabled -and
    -not $exactCellProbe.markersMissing -and
    -not $exactCellProbe.markersStringFalse) (
    'Profiler-marker contract must reject true, missing, and non-Boolean false.')


$runner = Get-Content -LiteralPath $runnerPath -Raw
$summarizer = Get-Content -LiteralPath $summarizerPath -Raw
$schedule = Get-Content -LiteralPath $schedulePath -Raw
$adapter = Get-Content -LiteralPath $adapterPath -Raw
$controller = Get-Content -LiteralPath $controllerPath -Raw
$oracle = Get-Content -LiteralPath $oraclePath -Raw
$discoveryDoc = Get-Content -LiteralPath $discoveryDocPath -Raw

foreach ($requiredSource in @(
    'com.summit.gpu-adaptive-binning',
    'com.summit.gpu-direct-binning',
    'com.summit.gpu-primitives',
    'com.summit.gpu-timestamps',
    'Assets\GpuAdaptiveBinningBenchmark',
    'Packages\manifest.json',
    'Packages\packages-lock.json',
    'ProjectSettings\ProjectVersion.txt',
    'GpuBenchmarkProvenance.psm1',
    'Summarize-GpuAdaptiveBinningBenchmark.ps1',
    'Test-GpuAdaptiveBinningBenchmarkProvenance.ps1')) {
    Assert-Contains $runner $requiredSource 'Runner source snapshot'
}
foreach ($requiredProvenance in @(
    'sourceSnapshotSha256',
    'sourceFileCount',
    'sourceHashesStableAcrossEditMode',
    'sourceHashesStableAcrossBuild',
    'editModeEvidenceBoundToSource',
    'directShaderSha256',
    'radixShaderSha256',
    'runtimeApiSha256',
    'timestampNativeDllSha256',
    'playerPayloadStableThroughRun',
    'runnerConfigFinalized',
    'formalContractSatisfied',
    'gitPostEditModeBeforeRestore',
    'gitPostEditModeAfterRestore',
    'gitPostBuildBeforeRestore',
    'gitPostBuildAfterRestore',
    'gitFinalBeforeRestore')) {
    Assert-Contains $runner $requiredProvenance 'Runner provenance'
}
foreach ($formalSelfTestToken in @(
    "EditModeResultsPath is discovery-only",
    'Formal report output must be outside the Git worktree.',
    "'-runTests'",
    "'-force-d3d12'",
    "'-force-device-index'",
    "'-testPlatform', 'EditMode'",
    "'-testResults'",
    'runner-generated-unity-editmode',
    'Restore-KnownUnityBenchmarkDrift',
    'sourceHashesStableAcrossEditMode',
    'editModeEvidenceBoundToSource',
    'logSha256')) {
    Assert-Contains $runner $formalSelfTestToken 'Formal self-test provenance'
}
foreach ($formalSummaryToken in @(
    'runner-generated-unity-editmode',
    'sourceHashesStableAcrossEditMode',
    'editModeEvidenceBoundToSource',
    'gitPostEditModeBeforeRestore',
    'gitPostEditModeAfterRestore',
    'runner.editModeResults.sourceSnapshotSha256',
    'Formal EditMode evidence was not generated by this runner')) {
    Assert-Contains $summarizer $formalSummaryToken (
        'Formal self-test summarizer gate')
}

foreach ($cliArgument in @(
    '-gpu-adaptive-binning-direct-shader-sha256',
    '-gpu-adaptive-binning-radix-shader-sha256',
    '-gpu-adaptive-binning-runtime-api-sha256')) {
    Assert-Contains $runner $cliArgument 'Runner CLI provenance'
    Assert-Contains $controller $cliArgument 'Controller CLI provenance'
}

$discoveryScenarios = [regex]::Matches(
    $runner,
    "scenarioId='discover-[^']+'")
$legacyFormalScenarios = [regex]::Matches(
    $runner,
    "scenarioId='(?:uniform|hotset16)-n1048576-c(?:64|256|4096|65536)'; " +
        "elementCount=1048576; binCount=(?:64|256|4096|65536); " +
        "distribution='(?:uniform|hotset16)'; seed=202609[0-9]{2}")
$contentionFormalScenarios = [regex]::Matches(
    $runner,
    "scenarioId='[^']+'; elementCount=[0-9]+; binCount=[0-9]+; " +
        "distribution='(?:singlebin|hotset4|uniform)'; seed=2026100[1-5]")
Assert-True ($discoveryScenarios.Count -eq 16) (
    'Discovery matrix must contain exactly 16 preregistered cells.')
Assert-True ($legacyFormalScenarios.Count -eq 8) (
    'Legacy formal holdout entry must retain exactly 8 cells.')
Assert-True ($contentionFormalScenarios.Count -eq 5) (
    'Contention formal holdout matrix must contain exactly 5 cells.')
foreach ($scenarioLine in @(
    "scenarioId='singlebin-n262144-c16'; elementCount=262144; binCount=16; distribution='singlebin'; seed=20261001; exactSingleBinKey=9",
    "scenarioId='singlebin-n1048576-c16'; elementCount=1048576; binCount=16; distribution='singlebin'; seed=20261002; exactSingleBinKey=10",
    "scenarioId='hotset4-n1048576-c16'; elementCount=1048576; binCount=16; distribution='hotset4'; seed=20261002; exactSingleBinKey=-1",
    "scenarioId='uniform-n1048576-c16'; elementCount=1048576; binCount=16; distribution='uniform'; seed=20261002; exactSingleBinKey=-1",
    "scenarioId='uniform-n1048576-c65536'; elementCount=1048576; binCount=65536; distribution='uniform'; seed=20261005; exactSingleBinKey=-1")) {
    Assert-Contains $runner $scenarioLine 'Frozen contention formal cell'
}
$discoverySeeds = @(
    [regex]::Matches($runner, 'seed=(202608[0-9]{2})') |
        ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
$contentionSeedValues = @(
    [regex]::Matches($runner, 'seed=(2026100[1-5])') |
        ForEach-Object { $_.Groups[1].Value })
$contentionSeeds = @($contentionSeedValues | Select-Object -Unique)
$reusedSeeds = @($contentionSeeds | Where-Object {
    $_ -in $discoverySeeds
})
Assert-True ($discoverySeeds.Count -eq 16) (
    'Discovery seeds must remain unique across 16 cells.')
Assert-True (
    $contentionSeedValues.Count -eq 5 -and
    $contentionSeeds.Count -eq 3 -and
    @($contentionSeedValues | Where-Object {
        $_ -ceq '20261002'
    }).Count -eq 3 -and
    $reusedSeeds.Count -eq 0) (
    'The five holdout cells must use three unseen seeds, intentionally reusing 20261002 across the fixed N/C bracket.')
Assert-Contains $runner "[int]`$SuperRounds = 4" 'Runner defaults'
foreach ($matrixToken in @(
    'discovery-amd-r9700-v1',
    'formal-amd-r9700-v1',
    'formal-amd-r9700-contention-v1',
    "matrixRole = 'holdout'")) {
    Assert-Contains $runner $matrixToken 'Runner matrices'
}
Assert-Contains $runner 'schemaVersion = 10' 'Runner schema'
Assert-Contains $runner 'benchmarkSchemaVersion = 3' 'Benchmark schema'
Assert-Contains $summarizer '$runnerSchemaVersion -notin @(9, 10)' (
    'Summarizer compatible runner schemas')

foreach ($explicitContract in @(
    "primitiveBackend = 'wave-ops'",
    "keyDomain = 'guaranteed-in-range'",
    "orderingContract = 'unspecified-within-bin'",
    "signedImprovementConvention = 'positive-radix-faster'")) {
    Assert-Contains $runner $explicitContract 'Runner explicit contract'
    Assert-Contains $summarizer $explicitContract 'Summarizer explicit contract'
}
foreach ($bracketContract in @(
    "axis = 'dominant-set-cardinality'",
    'fixedElementCount = 1048576',
    'fixedBinCount = 16',
    'orderedDominantSetCardinalities = @(1, 4, 16)',
    "candidatePairs = @('1-4', '1-16')",
    'hotset4ColdTailFraction = 0.125',
    'requireSignChangingAcceptedWinner = $true',
    'commonBracketSeed = 20261002',
    "requiredLowerWinner = 'radix'",
    "requiredUpperWinner = 'direct'",
    'requiredValidatedPairCount = 2',
    'requireAllCandidatePairsValidated = $true',
    "'radix-if-exact-calibrated-cell-else-direct'",
    "'full-element-count-bin-count-distribution-exact-single-bin-key-tuple'",
    'exactRadixCandidateCells = @(',
    'elementCount = 262144',
    'elementCount = 1048576',
    'exactSingleBinKey = 9',
    'exactSingleBinKey = 10',
    'requiredPredictionMatchCount = 5',
    'requiredInnerProfilerMarkersEnabled = $false',
    "'classification-replay-not-recordadaptive-timing'",
    'graphicsDeviceVendorId = 0x1002',
    'graphicsDeviceId = 0x7551',
    "graphicsDeviceType = 'Direct3D12'",
    'minimumWinningP99Pairs = 7',
    'minimumWorstPairP99ImprovementPercent = -10.0',
    'requiredValidatedCellCount = 5',
    'requireAcceptedWinner = $true')) {
    Assert-Contains $runner $bracketContract (
        'Runner frozen contention-bracket contract')
}
foreach ($gateToken in @(
    'minimumMedianImprovementPercent = 5.0',
    'minimumMedianAbsoluteReductionMs = 0.005',
    'minimumWinningPairs = 6',
    'expectedPairs = 8',
    'requireAbBaSameSign = $true',
    'minimumMedianP99ImprovementPercent = -2.0',
    'minimumAcceptedCellsPerBackendForCrossover = 2')) {
    Assert-Contains $runner $gateToken 'Runner frozen decisive gate'
}
foreach ($summaryGate in @(
    '$paired.Count -eq 8',
    '$radixWinningPairs -ge 6',
    '$directWinningPairs -ge 6',
    '$radixAverageMedian -ge 5.0',
    '$directAverageMedian -ge 5.0',
    '$radixAbsoluteMedian -ge 0.005',
    '$directAbsoluteMedian -ge 0.005',
    '$radixP99Median -ge -2.0',
    '$directP99Median -ge -2.0',
    '$abSignedMedian -gt 0.0',
    '$baSignedMedian -gt 0.0',
    '$abSignedMedian -lt 0.0',
    '$baSignedMedian -lt 0.0',
    '$RadixAcceptedCellCount -ge 2',
    '$DirectAcceptedCellCount -ge 2',
    '$bracketEvidenceComplete',
    '$signChangingBracketObserved',
    "lower = 1; upper = 4",
    "lower = 1; upper = 16",
    'crossover-bracket-evidence.csv',
    'crossoverBracketApplicable=$([int]$contentionFormal)',
    'crossoverBracketReason=contention-formal-holdout',
    'crossoverBracketReason=not-contention-formal-preset',
    'crossoverRequiresBothRadixToDirectBrackets=1',
    '$SelectorDirectionValidatedCount -eq 2',
    '$PolicyCellCount -eq 5',
    '$PredictionMatchCount -eq 5',
    '$WinnerP99WinningPairCount -ge 7',
    '$WorstPairP99ImprovementPercent -ge -10.0',
    '$TailValidatedCellCount -eq 5',
    'selectorDirectionValidated',
    'selectorPolicyPrediction',
    'selectorPolicyPredictionMatchesForcedWinner',
    'selectorPolicyClaimUsable=$([int]$selectorPolicyClaimUsable)',
    'selectorPolicyTailClaimUsable=$([int]$selectorPolicyTailClaimUsable)',
    'classification-replay-not-recordadaptive-timing',
    'selectorRequiredInnerProfilerMarkersEnabled=0',
    'selectorPolicyClaimKind=not-applicable',
    'selectorPolicySupportDomain=not-applicable',
    'crossoverRequiresBothRadixToDirectBrackets=0',
    'selectorPolicyTimingMeasured=0',
    'recordAdaptiveTimingMeasured=0',
    'formalGraphicsDeviceVendorId=0x1002',
    'formalGraphicsDeviceId=0x7551',
    'formalGraphicsDeviceType=Direct3D12',
    '$crossoverClaimUsable = if ($contentionFormal)',
    'Get-ExactCellSelectorPrediction',
    'Test-ExactSingleBinKeyContract',
    'gpu-adaptive-binning-input-v3',
    'selectorPolicy=radix-if-exact-calibrated-cell-else-direct',
    'selectorPolicyPredicate=full-element-count-bin-count-distribution-exact-single-bin-key-tuple',
    'selectorExactRadixCandidateCell1=N262144,C16,singlebin,key9',
    'selectorExactRadixCandidateCell2=N1048576,C16,singlebin,key10',
    'selectorPolicySupportDomain=exact-five-cell-holdout-only',
    'selectorBroaderThresholdValidated=0',
    'selectorRuntimeOverheadMeasured=0',
    'selectorProductionReady=0',
    'exactSingleBinKey',
    'hotset4ColdTailFraction=0.125',
    'positive-means-radix-faster',
    'No universal winner may be claimed from this matrix.')) {
    Assert-Contains $summarizer $summaryGate 'Summarizer frozen/crossover gate'
}

foreach ($markerContractToken in @(
    'Test-RequiredProfilerMarkersDisabled',
    "'requiredInnerProfilerMarkersEnabled'",
    "'innerProfilerMarkersEnabled'",
    'Formal contention contract requires inner profiler markers off.',
    'config.innerProfilerMarkersEnabled=false.')) {
    Assert-Contains $summarizer $markerContractToken (
        'Summarizer formal markers-off contract')
}
Assert-Contains $adapter (
    'public const bool InnerProfilerMarkersEnabled = false;') (
    'Adapter formal markers-off source')
foreach ($controllerMarkerToken in @(
    'innerProfilerMarkersEnabled =',
    'GpuAdaptiveBinningBenchmarkAdapter.InnerProfilerMarkersEnabled',
    'public bool innerProfilerMarkersEnabled;')) {
    Assert-Contains $controller $controllerMarkerToken (
        'Controller config markers-off serialization')
}

Assert-Contains $summarizer (
    '$scenarioSelectorPolicyClaimKind = if ($contentionFormal) {') (
    'Per-scenario selector-policy claim conditioning')
Assert-Contains $summarizer (
    'selectorPolicyClaimKind = $scenarioSelectorPolicyClaimKind') (
    'Per-scenario summary conditioned claim kind')
Assert-Contains $summarizer (
    '"selectorPolicyClaimKind=$scenarioSelectorPolicyClaimKind"') (
    'Per-scenario quality conditioned claim kind')

foreach ($uniqueContentionClaim in @(
    'selectorPolicySupportDomain=exact-five-cell-holdout-only',
    'crossoverRequiresBothRadixToDirectBrackets=1')) {
    Assert-True (
        ([regex]::Matches(
            $summarizer,
            [regex]::Escape($uniqueContentionClaim))).Count -eq 1) (
        "Contention-only claim '$uniqueContentionClaim' must occur once.")
}
$conditionalQualityPattern =
    '(?s)if \(\$contentionFormal\) \{\s*\$qualityLines \+= @\(\s*' +
    "'crossoverBracketAxis=dominant-set-cardinality'.*?" +
    "'selectorPolicyClaimKind=" +
    "classification-replay-not-recordadaptive-timing'.*?" +
    "'selectorPolicySupportDomain=exact-five-cell-holdout-only'.*?" +
    "'crossoverRequiresBothRadixToDirectBrackets=1'.*?" +
    '\)\s*\}\s*else \{\s*\$qualityLines \+= @\(\s*' +
    "'selectorPolicyClaimKind=not-applicable'.*?" +
    "'selectorPolicySupportDomain=not-applicable'.*?" +
    "'crossoverRequiresBothRadixToDirectBrackets=0'"
Assert-True ([regex]::IsMatch(
    $summarizer,
    $conditionalQualityPattern)) (
    'Exact-five-cell selector claims must be confined to the contention ' +
    'quality branch with explicit non-contention not-applicable metadata.')

Assert-Contains $discoveryDoc 'summarizer accepts schemas 9 and 10' (
    'Discovery evidence compatibility note')
Assert-True (-not $discoveryDoc.Contains(
    'summarizer accepts schema 8')) (
    'Discovery evidence note must not identify stale schema 8 as current.')

foreach ($variant in @(
    'direct-trusted-count-scan-scatter-wave-ops',
    'radix-low-bit-wave-ops')) {
    Assert-Contains $summarizer $variant 'Summarizer forced variant'
    Assert-Contains $adapter $variant 'Adapter forced variant'
}
Assert-True ([regex]::IsMatch(
    $schedule,
    'GpuAdaptiveBinningBenchmarkVariant\.Direct,\s*' +
        'GpuAdaptiveBinningBenchmarkVariant\.Radix,\s*' +
        'GpuAdaptiveBinningBenchmarkVariant\.Radix,\s*' +
        'GpuAdaptiveBinningBenchmarkVariant\.Direct')) (
    'Schedule must encode Direct/Radix/Radix/Direct (ABBA).')
Assert-True ([regex]::IsMatch(
    $schedule,
    'GpuAdaptiveBinningBenchmarkVariant\.Radix,\s*' +
        'GpuAdaptiveBinningBenchmarkVariant\.Direct,\s*' +
        'GpuAdaptiveBinningBenchmarkVariant\.Direct,\s*' +
        'GpuAdaptiveBinningBenchmarkVariant\.Radix')) (
    'Schedule must encode Radix/Direct/Direct/Radix (BAAB).')
foreach ($adapterToken in @(
    'spatial-binning/direct-trusted-count-scan-scatter-wave-ops',
    'spatial-binning/radix-low-bit-wave-ops',
    'GPU.AdaptiveBinning/DirectTrustedCountScanScatter/WaveOps',
    'GPU.AdaptiveBinning/RadixLowBits/WaveOps',
    'GpuPrimitiveBackend.WaveOps',
    'GpuAdaptiveBinningKeyDomain.GuaranteedInRange',
    'PrimitiveScratchOwnership',
    'DirectPrimitiveScratchBytes',
    'RadixPrimitiveScratchBytes',
    'DirectInternalScratchBytes',
    'RadixInternalScratchBytes',
    'ActualBenchmarkBufferResidentBytes')) {
    Assert-Contains $adapter $adapterToken 'Adaptive benchmark adapter'
}
Assert-Contains $oracle 'ComputeCanonicalSha256' 'Canonical CPU oracle'

foreach ($evidenceGate in @(
    "nativeTimestampStatus -cne 'ready'",
    'nativeTimestampFenceValue',
    'nativeTimestampDeviceGeneration',
    'nativeTimestampWarmupFrequency',
    'timestampInstrumentationReadbackBytes -ne 16',
    'measurementReadbackBytes -ne 0',
    'gpuRegionTimingComplete')) {
    Assert-Contains $summarizer $evidenceGate 'Summarizer evidence gate'
}
Assert-Contains $controller 'MeasurementReadbackBytes = 0' (
    'Controller zero-measurement-readback contract')
foreach ($controllerKeyToken in @(
    'exactSingleBinKey = string.Equals',
    'unchecked((uint)seed) % checked((uint)binCount)',
    'public int exactSingleBinKey;')) {
    Assert-Contains $controller $controllerKeyToken (
        'Controller exactSingleBinKey config contract')
}
foreach ($runnerKeyToken in @(
    'function Get-GeneratorV3SingleBinKey',
    'exactSingleBinKey = $expectedExactSingleBinKey',
    'exactSingleBinKey = $scenario.exactSingleBinKey',
    'generator-v3 unchecked uint seed modulo C')) {
    Assert-Contains $runner $runnerKeyToken 'Runner exact-key contract'
}
$legacyCardinalityPolicy = 'radix-if-dominant-set-cardinality-eq-1-else-direct'
Assert-True (-not $runner.Contains($legacyCardinalityPolicy)) (
    'Runner must not retain the cardinality-only selector policy.')
Assert-True (-not $summarizer.Contains($legacyCardinalityPolicy)) (
    'Summarizer must not retain the cardinality-only selector policy.')
$configWorkloadThrowToken =
    '"Scenario ''$scenarioId'' config workload differs from matrix.csv.")'
$configExactKeyIfToken =
    'if ($null -eq $config.PSObject.Properties[''exactSingleBinKey''] -or'
$configExactKeySiblingPattern =
    [regex]::Escape($configWorkloadThrowToken) +
    '\s*\}\s*' +
    [regex]::Escape($configExactKeyIfToken)
Assert-True ([regex]::IsMatch(
    $summarizer,
    $configExactKeySiblingPattern)) (
    'Config exact-key validation must be a sibling after workload mismatch closes.')

$expectedIdentities = @(
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
Assert-True ($expectedIdentities.Count -eq 15) (
    'Regression fixture must freeze exactly 15 NUnit identities.')
foreach ($identity in $expectedIdentities) {
    Assert-Contains $runner "'$identity'" 'Runner NUnit identity set'
    Assert-Contains $summarizer "'$identity'" 'Summarizer NUnit identity set'
}

foreach ($legacyVariant in @(
    'reference-compose-portable',
    'direct-count-scan-scatter-portable')) {
    Assert-True (-not $runner.Contains($legacyVariant)) (
        "Runner must not retain legacy variant '$legacyVariant'.")
    Assert-True (-not $summarizer.Contains($legacyVariant)) (
        "Summarizer must not retain legacy variant '$legacyVariant'.")
}

$tempBase = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::GetTempPath())
$fixtureRoot = Join-Path $tempBase (
    'summit-adaptive-binning-provenance-' + [Guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($fixtureRoot)
try {
    [ordered]@{
        schemaVersion = 8
        suite = 'summit.gpu-adaptive-binning'
        benchmarkSchemaVersion = 2
        runnerConfigFinalized = $true
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (
        Join-Path $fixtureRoot 'runner-config.json') -Encoding utf8
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $tamperOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass `
        -File $summarizerPath -ReportDirectory $fixtureRoot 2>&1
    $tamperExitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousPreference
    $tamperText = @($tamperOutput | ForEach-Object { [string]$_ }) -join "`n"
    Assert-True ($tamperExitCode -ne 0) (
        'Summarizer must reject a stale/tampered runner schema.')
    Assert-True ($tamperText.Contains(
        'Runner schema/suite contract does not match adaptive-binning.')) (
        'Schema rejection must identify the adaptive-binning contract.')
}
finally {
    $resolvedFixture = [System.IO.Path]::GetFullPath($fixtureRoot)
    Assert-True ($resolvedFixture.StartsWith(
        $tempBase,
        [System.StringComparison]::OrdinalIgnoreCase)) (
        'Temporary fixture cleanup must remain inside the system temp directory.')
    if (Test-Path -LiteralPath $resolvedFixture -PathType Container) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}

$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$legacyOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File $runnerPath `
    -MatrixPreset formal-amd-r9700-v1 2>&1
$legacyExitCode = $LASTEXITCODE
$ErrorActionPreference = $previousPreference
$legacyText = @($legacyOutput | ForEach-Object { [string]$_ }) -join "`n"
Assert-True ($legacyExitCode -ne 0) (
    'Legacy formal preset must still reject non-formal invocation.')
Assert-True ($legacyText.Contains(
    "MatrixPreset='formal-amd-r9700-v1' requires " +
        '-FormalAcceptanceMode.')) (
    'Legacy formal entry must retain its fail-closed public contract.')

$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$legacyFormalOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File $runnerPath `
    -FormalAcceptanceMode `
    -MatrixPreset formal-amd-r9700-v1 `
    -SuperRounds 4 `
    -WarmupFrames 60 `
    -SampleFrames 900 `
    -CooldownFrames 15 `
    -DispatchesPerFrame 1 `
    -SkipSummary 2>&1
$legacyFormalExitCode = $LASTEXITCODE
$ErrorActionPreference = $previousPreference
$legacyFormalText = @(
    $legacyFormalOutput | ForEach-Object { [string]$_ }) -join "`n"
Assert-True ($legacyFormalExitCode -ne 0) (
    'Legacy formal contract must reject forbidden switches before Unity.')
Assert-True ($legacyFormalText.Contains('SkipSummary is forbidden.')) (
    'Legacy formal preset must select its own frozen formal contract.')
Assert-True (-not $legacyFormalText.Contains(
    "MatrixPreset='formal-amd-r9700-v1'; expected " +
        "'formal-amd-r9700-contention-v1'")) (
    'Legacy formal preset must not be redirected to the contention contract.')

$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$formalOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File $runnerPath `
    -FormalAcceptanceMode `
    -MatrixPreset formal-amd-r9700-contention-v1 `
    -SuperRounds 4 `
    -WarmupFrames 60 `
    -SampleFrames 900 `
    -CooldownFrames 15 `
    -DispatchesPerFrame 1 `
    -SkipSummary 2>&1
$formalExitCode = $LASTEXITCODE
$ErrorActionPreference = $previousPreference
$formalText = @($formalOutput | ForEach-Object { [string]$_ }) -join "`n"
Assert-True ($formalExitCode -ne 0) (
    'Formal runner must reject forbidden switches before resolving Unity.')
Assert-True ($formalText.Contains('Formal acceptance contract rejected:')) (
    'Formal preflight rejection must identify the frozen contract.')
Assert-True ($formalText.Contains('SkipSummary is forbidden.')) (
    'Formal preflight rejection must explicitly identify SkipSummary.')

$externalFormalOutputRoot = Join-Path $tempBase (
    'summit-adaptive-formal-external-xml-' +
    [Guid]::NewGuid().ToString('N'))
$missingUnityPath = Join-Path $tempBase (
    'summit-definitely-missing-unity.exe')
$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$externalXmlOutput = & powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File $runnerPath `
    -UnityPath $missingUnityPath `
    -OutputDirectory $externalFormalOutputRoot `
    -EditModeResultsPath $runnerPath `
    -FormalAcceptanceMode `
    -MatrixPreset formal-amd-r9700-contention-v1 `
    -SuperRounds 4 `
    -WarmupFrames 60 `
    -SampleFrames 900 `
    -CooldownFrames 15 `
    -DispatchesPerFrame 1 2>&1
$externalXmlExitCode = $LASTEXITCODE
$ErrorActionPreference = $previousPreference
$externalXmlText = @(
    $externalXmlOutput | ForEach-Object { [string]$_ }) -join "`n"
Assert-True ($externalXmlExitCode -ne 0) (
    'Formal runner must reject caller-supplied XML before resolving Unity.')
Assert-True ($externalXmlText.Contains(
    'EditModeResultsPath is discovery-only; formal evidence is generated ' +
        'by this runner in the clean worktree.')) (
    'Formal external-XML rejection must be explicit and fail closed.')
Assert-True (-not (Test-Path -LiteralPath $externalFormalOutputRoot)) (
    'Formal external-XML rejection must occur before report creation.')

[pscustomobject]@{
    status = 'passed'
    assertions = $assertions
    unityLaunched = $false
} | ConvertTo-Json -Compress
