[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReportDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$root = [System.IO.Path]::GetFullPath($ReportDirectory)
$runnerConfig = Get-Content -LiteralPath (
    Join-Path $root 'runner-config.json') -Raw | ConvertFrom-Json

function Number($Value) {
    return [double]::Parse(
        [string]$Value,
        [System.Globalization.NumberStyles]::Float,
        $invariant)
}

function Average([object[]]$Values) {
    return ($Values | Measure-Object -Average).Average
}

function Percentile99([double[]]$Values) {
    [double[]]$sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return 0.0 }
    $index = [Math]::Max(
        0,
        [int][Math]::Ceiling($sorted.Count * 0.99) - 1)
    return $sorted[$index]
}

function Improvement([double]$Baseline, [double]$Optimized) {
    if ($Baseline -eq 0.0) { return 0.0 }
    return (($Baseline - $Optimized) / $Baseline) * 100.0
}

$matrix = [System.Collections.Generic.List[object]]::new()
$pairs = [System.Collections.Generic.List[object]]::new()
$scenarioDirectories = Get-ChildItem -LiteralPath $root -Directory |
    Where-Object { Test-Path -LiteralPath (
        Join-Path $_.FullName 'run-summary.txt') }
foreach ($directory in $scenarioDirectories) {
    $raw = @(Import-Csv -LiteralPath (
        Join-Path $directory.FullName 'raw-samples.csv'))
    $blocks = @(Import-Csv -LiteralPath (
        Join-Path $directory.FullName 'block-summary.csv'))
    $validation = @(Import-Csv -LiteralPath (
        Join-Path $directory.FullName 'validation.csv'))
    $config = Get-Content -LiteralPath (
        Join-Path $directory.FullName 'config.json') -Raw |
        ConvertFrom-Json
    $device = Get-Content -LiteralPath (
        Join-Path $directory.FullName 'device.json') -Raw |
        ConvertFrom-Json

    if (@($validation | Where-Object { $_.passed -ne '1' }).Count -ne 0) {
        throw "Validation failed in $($directory.Name)."
    }
    if (@($raw | Where-Object {
            [int64]$_.measurementReadbackBytes -ne 0 }).Count -ne 0) {
        throw "Measurement readback is non-zero in $($directory.Name)."
    }
    if (-not [bool]$device.supportsAsyncCompute -or
        -not [bool]$device.asyncComputePathExercised -or
        [bool]$device.copyQueueClaim) {
        throw "Queue capability claims are inconsistent in $($directory.Name)."
    }

    $baseline = @($raw | Where-Object {
        $_.variant -ceq 'fifo-main-graphics' })
    $optimized = @($raw | Where-Object {
        $_.variant -like 'least-slack-*' })
    if ($baseline.Count -eq 0 -or $optimized.Count -eq 0) {
        throw "A/B samples are missing in $($directory.Name)."
    }

    [double]$baselineGpuAvg = Average @(
        $baseline | ForEach-Object { Number $_.gpuMakespanMs })
    [double]$optimizedGpuAvg = Average @(
        $optimized | ForEach-Object { Number $_.gpuMakespanMs })
    [double]$baselineGpuP99 = Percentile99 @(
        $baseline | ForEach-Object { Number $_.gpuMakespanMs })
    [double]$optimizedGpuP99 = Percentile99 @(
        $optimized | ForEach-Object { Number $_.gpuMakespanMs })
    [double]$baselineCriticalP99 = Percentile99 @(
        $baseline | ForEach-Object { Number $_.criticalLatencyMs })
    [double]$optimizedCriticalP99 = Percentile99 @(
        $optimized | ForEach-Object { Number $_.criticalLatencyMs })
    [double]$baselineCriticalMiss = Average @(
        $baseline | ForEach-Object { Number $_.criticalMisses })
    [double]$optimizedCriticalMiss = Average @(
        $optimized | ForEach-Object { Number $_.criticalMisses })
    [double]$baselineTotalMiss = Average @(
        $baseline | ForEach-Object { (Number $_.totalMisses) / 4.0 })
    [double]$optimizedTotalMiss = Average @(
        $optimized | ForEach-Object { (Number $_.totalMisses) / 4.0 })

    $gpuWins = 0
    $criticalWins = 0
    foreach ($pairId in @(
        $blocks | Select-Object -ExpandProperty pairIndex -Unique)) {
        $pairRows = @($blocks | Where-Object { $_.pairIndex -eq $pairId })
        $a = @($pairRows | Where-Object {
            $_.variant -ceq 'fifo-main-graphics' })
        $b = @($pairRows | Where-Object {
            $_.variant -like 'least-slack-*' })
        if ($a.Count -ne 1 -or $b.Count -ne 1) {
            throw "Pair $pairId is incomplete in $($directory.Name)."
        }
        [double]$aGpu = Number $a[0].gpuAverageMs
        [double]$bGpu = Number $b[0].gpuAverageMs
        [double]$aCritical = Number $a[0].criticalP99LatencyMs
        [double]$bCritical = Number $b[0].criticalP99LatencyMs
        if ($bGpu -lt $aGpu) { $gpuWins++ }
        if ($bCritical -lt $aCritical) { $criticalWins++ }
        $pairs.Add([pscustomobject]@{
            scenarioId = $directory.Name
            pairIndex = [int]$pairId
            pairOrder = $a[0].pairOrder
            baselineGpuAverageMs = $aGpu
            optimizedGpuAverageMs = $bGpu
            gpuWinner = if ($bGpu -lt $aGpu) { 'optimized' } else { 'baseline' }
            baselineCriticalP99Ms = $aCritical
            optimizedCriticalP99Ms = $bCritical
            criticalWinner = if ($bCritical -lt $aCritical) {
                'optimized'
            } else { 'baseline' }
        })
    }

    $matrix.Add([pscustomobject]@{
        scenarioId = $directory.Name
        graphicsDeviceName = [string]$device.graphicsDeviceName
        selectedBackend = [string]$config.selectedBackend
        optimizedUsesAsyncCompute =
            [bool]$config.optimizedUsesAsyncCompute
        calibrationMainGpuAverageMs =
            [double]$config.calibrationMainGpuAverageMs
        calibrationAsyncGpuAverageMs =
            [double]$config.calibrationAsyncGpuAverageMs
        workItems = [int]$config.workItems
        pressureItems = [int]$config.pressureItems
        baselineGpuAverageMs = $baselineGpuAvg
        optimizedGpuAverageMs = $optimizedGpuAvg
        gpuAverageImprovementPercent = Improvement `
            $baselineGpuAvg $optimizedGpuAvg
        baselineGpuP99Ms = $baselineGpuP99
        optimizedGpuP99Ms = $optimizedGpuP99
        gpuP99ImprovementPercent = Improvement `
            $baselineGpuP99 $optimizedGpuP99
        baselineCriticalP99Ms = $baselineCriticalP99
        optimizedCriticalP99Ms = $optimizedCriticalP99
        criticalP99ImprovementPercent = Improvement `
            $baselineCriticalP99 $optimizedCriticalP99
        baselineCriticalDeadlineMissRate = $baselineCriticalMiss
        optimizedCriticalDeadlineMissRate = $optimizedCriticalMiss
        criticalDeadlineMissReductionPoints =
            ($baselineCriticalMiss - $optimizedCriticalMiss) * 100.0
        baselineTotalDeadlineMissRate = $baselineTotalMiss
        optimizedTotalDeadlineMissRate = $optimizedTotalMiss
        pairedGpuWins = $gpuWins
        pairedCriticalWins = $criticalWins
        pairedCount = @($pairs | Where-Object {
            $_.scenarioId -ceq $directory.Name }).Count
        validationRows = $validation.Count
        validationPassed = 1
    })
}

if ($matrix.Count -eq 0) {
    throw "No scenario evidence found in $root."
}
if ([string]$runnerConfig.matrixPreset -ceq 'formal') {
    foreach ($row in $matrix) {
        if ($row.criticalP99ImprovementPercent -lt 50.0) {
            throw "Formal critical P99 improvement is below 50% in $($row.scenarioId)."
        }
        if ($row.pairedCriticalWins -ne $row.pairedCount) {
            throw "Formal critical latency did not win every pair in $($row.scenarioId)."
        }
        if ($row.gpuAverageImprovementPercent -lt -2.0) {
            throw "Formal total GPU average regressed by more than 2% in $($row.scenarioId)."
        }
        if ($row.optimizedCriticalDeadlineMissRate -ge
            $row.baselineCriticalDeadlineMissRate) {
            throw "Formal critical deadline miss rate did not improve in $($row.scenarioId)."
        }
    }
}
$matrix | Export-Csv -LiteralPath (
    Join-Path $root 'matrix-summary.csv') -NoTypeInformation
$pairs | Export-Csv -LiteralPath (
    Join-Path $root 'pair-summary.csv') -NoTypeInformation

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# Deadline-aware GPU scheduling A/B')
$lines.Add('')
$lines.Add('| Scenario | Selected backend | GPU avg improvement | GPU P99 improvement | Critical P99 improvement | Critical miss rate A-to-B | Critical wins |')
$lines.Add('|---|---|---:|---:|---:|---:|---:|')
foreach ($row in $matrix) {
    $lines.Add((
        '| {0} | {1} | {2:F2}% | {3:F2}% | {4:F2}% | {5:P2} to {6:P2} | {7}/{8} |' -f
        $row.scenarioId,
        $row.selectedBackend,
        $row.gpuAverageImprovementPercent,
        $row.gpuP99ImprovementPercent,
        $row.criticalP99ImprovementPercent,
        $row.baselineCriticalDeadlineMissRate,
        $row.optimizedCriticalDeadlineMissRate,
        $row.pairedCriticalWins,
        $row.pairedCount))
}
$lines.Add('')
$lines.Add('GPU makespan is a native DX12 main-queue timestamp spanning the copy start and the async-fence join. The public Unity backend uses a main-graphics copy fallback, so no dedicated copy-queue claim is made.')
$lines | Set-Content -LiteralPath (Join-Path $root 'SUMMARY.md')
Write-Output "Validated deadline scheduler evidence: $root"
