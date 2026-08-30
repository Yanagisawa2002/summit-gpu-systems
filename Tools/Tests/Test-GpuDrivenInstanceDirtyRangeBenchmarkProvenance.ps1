[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolsRoot = Split-Path -Parent $PSScriptRoot
$runnerPath = Join-Path $toolsRoot (
    'Run-GpuDrivenInstanceDirtyRangeBenchmark.ps1')
if (-not (Test-Path -LiteralPath $runnerPath -PathType Leaf)) {
    throw "Runner is missing: $runnerPath"
}
$runner = Get-Content -LiteralPath $runnerPath -Raw

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not $Text.Contains($Needle)) {
        throw "$Label is missing required token: $Needle"
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if ($Text.Contains($Needle)) {
        throw "$Label contains forbidden token: $Needle"
    }
}

foreach ($requirement in @(
        @('$ErrorActionPreference = ''Stop''', 'Fail-fast errors'),
        @('Set-StrictMode -Version Latest', 'Strict mode'),
        @('Get-GpuBenchmarkGitSnapshot', 'Git snapshot'),
        @('Benchmark requires a clean worktree', 'Clean-tree gate'),
        @('Get-CombinedSha256', 'Source snapshot hash'),
        @('Benchmark source hashes changed', 'Source stability gate'),
        @('Get-GpuBenchmarkPlayerPayload', 'Player payload receipt'),
        @('Player payload changed', 'Player payload stability gate'),
        @('GpuDrivenInstanceMacrobenchmarkBuild.PerformBuild',
            'Shared macro Player build entrypoint'),
        @('-gpu-driven-instance-macro-player-path',
            'Shared build output argument'),
        @('-gpu-driven-instance-upload-benchmark',
            'Upload benchmark enable flag'),
        @('-gpu-driven-instance-upload-output', 'Player output argument'),
        @('-gpu-driven-instance-upload-instance-count',
            'Instance-count argument'),
        @('-gpu-driven-instance-upload-moving-percent',
            'Moving-percent argument'),
        @('-gpu-driven-instance-upload-seed', 'Seed argument'),
        @('-gpu-driven-instance-upload-warmup', 'Warm-up argument'),
        @('-gpu-driven-instance-upload-sample', 'Sample argument'),
        @('-force-d3d12', 'D3D12 launch'),
        @("graphicsDeviceType -cne 'Direct3D12'", 'D3D12 receipt gate'),
        @('status'' $scenario.scenarioId', 'Completed run receipt'),
        @('mainThreadAllocatedBytes', 'Zero-allocation evidence'),
        @('slotWaitFrames', 'Zero-slot-wait evidence'),
        @('changedInstanceCount', 'Changed-record evidence'),
        @('inputRangeCount', 'Input-range evidence'),
        @('dirtyRecordCount', 'Dirty-union evidence'),
        @('uploadedRecordCount', 'Uploaded-record evidence'),
        @('logicalUploadBytes', 'Logical-upload evidence'),
        @('uploadCallCount', 'Upload-call evidence'),
        @('frameTimingValid', 'Frame timing validity'),
        @('cpuFrameMs', 'CPU frame-tail evidence'),
        @('cpuMainThreadFrameMs', 'CPU main-tail evidence'),
        @('updateHash', 'Per-sample update hash'),
        @('actualStateHash', 'Validation state hash'),
        @('measurementReadbackBytes', 'No measured readback gate'),
        @('$instanceCount in @(10000, 100000)', 'Frozen N matrix'),
        @('$movingPercent in @(0, 1, 10, 100)',
            'Frozen movement matrix'),
        @('$viewCount = 1', 'Frozen one-view cell'),
        @('$drawGroupCount = 1', 'Frozen one-group cell'),
        @('$visibility = ''visible25''', 'Frozen visibility'),
        @('[int]$WarmupFrames = 30', 'Default warm-up'),
        @('[int]$SampleFrames = 240', 'Default sample count'),
        @('$formalSampleFrames = 900', 'Formal sample count'),
        @('$stateStride = 48', 'Frozen instance stride'),
        @('$maximumDirtyUploadCalls = 16', 'Dirty call upper bound'),
        @('full-upload accounting is not exact', 'Full accounting gate'),
        @('dirty-upload accounting is not exact', 'Dirty accounting gate'),
        @('full/dirty update-hash parity failed', 'Raw parity gate'),
        @('state-hash parity failed', 'Validation parity gate'),
        @('performanceGateApplied = $false', 'No device speed gate'),
        @('reported-only', '100-percent reported-only decision'),
        @('matrix-summary.csv', 'Matrix summary'),
        @('BENCHMARK_REPORT.md', 'Human-readable report'))) {
    Assert-Contains $runner $requirement[0] $requirement[1]
}

foreach ($forbidden in @(
        @('SkipBuild',
            'Unbound pre-existing Player reuse'),
        @('cpuSubmissionP95MinimumImprovement',
            'Uncalibrated CPU improvement threshold'),
        @('throw ''100% non-inferiority',
            '100-percent performance fail gate'),
        @('throw "100% non-inferiority',
            '100-percent performance fail gate'))) {
    Assert-NotContains $runner $forbidden[0] $forbidden[1]
}

$playerArgumentsStart = $runner.IndexOf(
    '$playerArguments = @(',
    [StringComparison]::Ordinal)
$playerArgumentsEnd = $runner.IndexOf(
    '$runSummaryPath =',
    $playerArgumentsStart,
    [StringComparison]::Ordinal)
if ($playerArgumentsStart -lt 0 -or
    $playerArgumentsEnd -le $playerArgumentsStart) {
    throw 'Player launch block could not be isolated.'
}
$playerLaunch = $runner.Substring(
    $playerArgumentsStart,
    $playerArgumentsEnd - $playerArgumentsStart)
Assert-NotContains $playerLaunch "'-batchmode'" 'Player launch'
Assert-NotContains $playerLaunch '-WindowStyle Hidden' 'Player launch'
Assert-Contains $playerLaunch "'-screen-fullscreen', '0'" `
    'Windowed Player launch'

$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $runnerPath,
    [ref]$tokens,
    [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    $messages = @($parseErrors | ForEach-Object { $_.Message }) -join '; '
    throw "Runner has PowerShell parse errors: $messages"
}

$requiredFunctions = @(
    'Require-CsvColumns',
    'Number',
    'Percentile',
    'Get-ChangedInstanceCount',
    'Get-RequiredCpuSubmissionValues',
    'Assert-UploadRows',
    'Get-OptionalCsvValue',
    'Assert-ValidationParity')
$functionAsts = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $requiredFunctions -contains $node.Name
}, $true))
if ($functionAsts.Count -ne $requiredFunctions.Count) {
    throw 'Runner analysis helpers are incomplete.'
}
$harnessText = ($functionAsts |
    Sort-Object { [Array]::IndexOf($requiredFunctions, $_.Name) } |
    ForEach-Object { $_.Extent.Text }) -join "`n`n"
$harnessText += @'

$fullVariant = 'full'
$dirtyVariant = 'dirty'
$stateStride = 48
$maximumDirtyUploadCalls = 16

function New-Row {
    param(
        [string]$Variant,
        [int]$BlockIndex,
        [int]$PairIndex,
        [int]$SampleIndex,
        [int]$Changed,
        [int]$InputRanges,
        [int]$DirtyRecords,
        [int]$UploadedRecords,
        [int64]$Bytes,
        [int]$Calls,
        [int64]$Allocated = 0,
        [int]$WaitFrames = 0
    )
    return [pscustomobject][ordered]@{
        processId = '42'
        scenarioId = 'synthetic'
        blockIndex = $BlockIndex
        pairIndex = $PairIndex
        variant = $Variant
        sampleIndex = $SampleIndex
        logicalOrdinal = 100 + $SampleIndex
        totalCpuSubmissionMs = '1.25'
        mainThreadAllocatedBytes = $Allocated
        slotWaitFrames = $WaitFrames
        changedInstanceCount = $Changed
        planRangeCount = $InputRanges
        inputRangeCount = $InputRanges
        dirtyRecordCount = $DirtyRecords
        dirtyRecordCountExact = 1
        uploadedRecordCount = $UploadedRecords
        logicalUploadBytes = $Bytes
        uploadCallCount = $Calls
        rangePlanHash = 'PLAN'
        updateHash = 'HASH-' + $SampleIndex
        frameTimingValid = 1
        cpuFrameMs = '2.5'
        cpuMainThreadFrameMs = '2.0'
        measurementReadbackBytes = 0
    }
}

$variants = @(
    $fullVariant,
    $dirtyVariant,
    $dirtyVariant,
    $fullVariant,
    $dirtyVariant,
    $fullVariant,
    $fullVariant,
    $dirtyVariant)
$pairs = @(1, 1, 2, 2, 3, 3, 4, 4)
$rows = @()
for ($block = 1; $block -le 8; $block++) {
    foreach ($sample in 1..2) {
        if ($variants[$block - 1] -ceq $fullVariant) {
            $rows += New-Row $fullVariant $block $pairs[$block - 1] `
                $sample 10 0 100 100 4800 1
        }
        else {
            $rows += New-Row $dirtyVariant $block $pairs[$block - 1] `
                $sample 10 2 10 10 480 2
        }
    }
}
$receipt = Assert-UploadRows `
    -Rows $rows `
    -InstanceCount 100 `
    -MovingPercent 10 `
    -SampleFrames 2 `
    -ExpectedProcessId '42' `
    -ScenarioId 'synthetic'
if ($receipt.FullRows.Count -ne 8 -or
    $receipt.DirtyRows.Count -ne 8 -or
    $receipt.ChangedCount -ne 10 -or
    $receipt.FullBytes -ne 4800 -or
    $receipt.DirtyBytes -ne 480) {
    throw 'Synthetic upload accounting returned an unexpected receipt.'
}

$zeroRows = @()
$fullDirtyRows = @()
for ($block = 1; $block -le 8; $block++) {
    if ($variants[$block - 1] -ceq $fullVariant) {
        $zeroRows += New-Row $fullVariant $block $pairs[$block - 1] `
            1 0 0 100 100 4800 1
        $fullDirtyRows += New-Row $fullVariant $block `
            $pairs[$block - 1] 1 100 0 100 100 4800 1
    }
    else {
        $zeroRows += New-Row $dirtyVariant $block $pairs[$block - 1] `
            1 0 0 0 0 0 0
        $fullDirtyRows += New-Row $dirtyVariant $block `
            $pairs[$block - 1] 1 100 1 100 100 4800 1
    }
}
[void](Assert-UploadRows $zeroRows 100 0 1 '42' 'synthetic')

[void](Assert-UploadRows $fullDirtyRows 100 100 1 '42' 'synthetic')

$invalidRows = @()
foreach ($row in $rows) {
    $copy = $row.PSObject.Copy()
    $invalidRows += $copy
}
$invalidRows[0].mainThreadAllocatedBytes = 1
$rejected = $false
try {
    [void](Assert-UploadRows $invalidRows 100 10 2 '42' 'synthetic')
}
catch {
    $rejected = $true
}
if (-not $rejected) {
    throw 'Allocation-bearing synthetic evidence was not rejected.'
}

$p95 = Percentile ([double[]]@(1.0, 2.0, 3.0, 4.0)) 0.95
if ([Math]::Abs($p95 - 3.85) -gt 0.000001) {
    throw "Percentile helper returned $p95; expected 3.85."
}
if ((Get-ChangedInstanceCount 100000 1) -ne 1000) {
    throw 'Changed-instance helper returned the wrong one-percent count.'
}

$validation = @()
for ($block = 1; $block -le 8; $block++) {
    $validation += [pscustomobject]@{
        blockIndex = $block
        pairIndex = $pairs[$block - 1]
        variant = $variants[$block - 1]
        completionFencePassed = 1
        expectedStateHash = 'STATE'
        actualStateHash = 'STATE'
        passed = 1
        readbackBytes = 4800
    }
}
Assert-ValidationParity $validation 'synthetic' 100
$validation[1].actualStateHash = 'DIFFERENT'
$rejected = $false
try {
    Assert-ValidationParity $validation 'synthetic' 100
}
catch {
    $rejected = $true
}
if (-not $rejected) {
    throw 'Mismatched state-hash validation was not rejected.'
}
'@

$harness = [ScriptBlock]::Create($harnessText)
& $harness

Write-Host 'GpuDrivenInstance dirty-range benchmark provenance tests passed.'
