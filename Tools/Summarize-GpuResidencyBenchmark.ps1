[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReportDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [System.IO.Path]::GetFullPath($ReportDirectory)
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$runnerPath = Join-Path $root 'runner-config.json'
if (-not (Test-Path -LiteralPath $runnerPath -PathType Leaf)) {
    throw "Runner configuration is missing: $runnerPath"
}
$runner = Get-Content -LiteralPath $runnerPath -Raw | ConvertFrom-Json

function N($Value) {
    return [double]::Parse([string]$Value,
        [System.Globalization.NumberStyles]::Float, $invariant)
}
function Avg([object[]]$Values) {
    return ($Values | Measure-Object -Average).Average
}
function P99([double[]]$Values) {
    [double[]]$sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return 0.0 }
    return $sorted[[Math]::Max(0,
        [int][Math]::Ceiling($sorted.Count * 0.99) - 1)]
}
function Improve([double]$A, [double]$B) {
    if ($A -eq 0.0) { return 0.0 }
    return (($A - $B) / $A) * 100.0
}

$matrix = [System.Collections.Generic.List[object]]::new()
$pairs = [System.Collections.Generic.List[object]]::new()
foreach ($directory in Get-ChildItem -LiteralPath $root -Directory) {
    $summaryPath = Join-Path $directory.FullName 'run-summary.txt'
    if (-not (Test-Path -LiteralPath $summaryPath)) { continue }
    $raw = @(Import-Csv (Join-Path $directory.FullName 'raw-samples.csv'))
    $blocks = @(Import-Csv (Join-Path $directory.FullName 'block-summary.csv'))
    $validation = @(Import-Csv (Join-Path $directory.FullName 'validation.csv'))
    $config = Get-Content (Join-Path $directory.FullName 'config.json') -Raw |
        ConvertFrom-Json
    $device = Get-Content (Join-Path $directory.FullName 'device.json') -Raw |
        ConvertFrom-Json
    if (@($validation | Where-Object { $_.passed -ne '1' }).Count -ne 0) {
        throw "Validation failed in $($directory.Name)."
    }
    if (@($raw | Where-Object {
            [int64]$_.measurementReadbackBytes -ne 0 }).Count -ne 0) {
        throw "Measurement readback is non-zero in $($directory.Name)."
    }
    if ([bool]$config.sparseResourceClaim -or
        [bool]$device.sparseResourceClaim) {
        throw "Sparse-resource claim must remain false."
    }
    $a = @($raw | Where-Object { $_.variant -ceq 'rebuild-visible-set' })
    $b = @($raw | Where-Object { $_.variant -ceq 'persistent-lru-delta' })
    if ($a.Count -eq 0 -or $b.Count -eq 0) {
        throw "A/B samples are missing in $($directory.Name)."
    }
    [double]$aGpu = Avg @($a | ForEach-Object { N $_.gpuMs })
    [double]$bGpu = Avg @($b | ForEach-Object { N $_.gpuMs })
    [double]$aGpuP99 = P99 @($a | ForEach-Object { N $_.gpuMs })
    [double]$bGpuP99 = P99 @($b | ForEach-Object { N $_.gpuMs })
    [double]$aCpu = Avg @($a | ForEach-Object {
        (N $_.planningMs) + (N $_.stagingMs) })
    [double]$bCpu = Avg @($b | ForEach-Object {
        (N $_.planningMs) + (N $_.stagingMs) })
    [double]$aUpload = Avg @($a | ForEach-Object { N $_.totalUploadBytes })
    [double]$bUpload = Avg @($b | ForEach-Object { N $_.totalUploadBytes })
    [double]$aUploadP99 = P99 @($a | ForEach-Object { N $_.totalUploadBytes })
    [double]$bUploadP99 = P99 @($b | ForEach-Object { N $_.totalUploadBytes })
    [double]$aMiss = Avg @($a | ForEach-Object {
        (N $_.missPages) / (N $_.requestedPages) })
    [double]$bMiss = Avg @($b | ForEach-Object {
        (N $_.missPages) / (N $_.requestedPages) })

    $gpuWins = 0
    $cpuWins = 0
    foreach ($pairId in @(
        $blocks | Select-Object -ExpandProperty pairIndex -Unique)) {
        $rows = @($blocks | Where-Object { $_.pairIndex -eq $pairId })
        $pa = @($rows | Where-Object {
            $_.variant -ceq 'rebuild-visible-set' })
        $pb = @($rows | Where-Object {
            $_.variant -ceq 'persistent-lru-delta' })
        if ($pa.Count -ne 1 -or $pb.Count -ne 1) {
            throw "Pair $pairId is incomplete in $($directory.Name)."
        }
        [double]$paGpu = N $pa[0].gpuAverageMs
        [double]$pbGpu = N $pb[0].gpuAverageMs
        [double]$paCpu = N $pa[0].cpuPreparationAverageMs
        [double]$pbCpu = N $pb[0].cpuPreparationAverageMs
        if ($pbGpu -lt $paGpu) { $gpuWins++ }
        if ($pbCpu -lt $paCpu) { $cpuWins++ }
        $pairs.Add([pscustomobject]@{
            scenarioId = $directory.Name
            pairIndex = [int]$pairId
            pairOrder = $pa[0].pairOrder
            baselineGpuAverageMs = $paGpu
            optimizedGpuAverageMs = $pbGpu
            gpuWinner = if ($pbGpu -lt $paGpu) { 'optimized' } else { 'baseline' }
            baselineCpuPreparationMs = $paCpu
            optimizedCpuPreparationMs = $pbCpu
            cpuWinner = if ($pbCpu -lt $paCpu) { 'optimized' } else { 'baseline' }
        })
    }
    $matrix.Add([pscustomobject]@{
        scenarioId = $directory.Name
        graphicsDeviceName = [string]$device.graphicsDeviceName
        pointsPerPage = [int]$config.pointsPerPage
        gpuResidentBytes = [int64]$config.gpuResidentBytes
        baselineGpuAverageMs = $aGpu
        optimizedGpuAverageMs = $bGpu
        gpuAverageImprovementPercent = Improve $aGpu $bGpu
        baselineGpuP99Ms = $aGpuP99
        optimizedGpuP99Ms = $bGpuP99
        gpuP99ImprovementPercent = Improve $aGpuP99 $bGpuP99
        baselineCpuPreparationMs = $aCpu
        optimizedCpuPreparationMs = $bCpu
        cpuPreparationImprovementPercent = Improve $aCpu $bCpu
        baselineUploadAverageBytes = $aUpload
        optimizedUploadAverageBytes = $bUpload
        uploadAverageReductionPercent = Improve $aUpload $bUpload
        baselineUploadP99Bytes = $aUploadP99
        optimizedUploadP99Bytes = $bUploadP99
        uploadP99ReductionPercent = Improve $aUploadP99 $bUploadP99
        baselineMissRate = $aMiss
        optimizedMissRate = $bMiss
        pairedGpuWins = $gpuWins
        pairedCpuWins = $cpuWins
        pairedCount = @($pairs | Where-Object {
            $_.scenarioId -ceq $directory.Name }).Count
        validationRows = $validation.Count
        validationPassed = 1
    })
}
if ($matrix.Count -eq 0) { throw "No evidence found in $root." }
$matrix | Export-Csv (Join-Path $root 'matrix-summary.csv') -NoTypeInformation
$pairs | Export-Csv (Join-Path $root 'pair-summary.csv') -NoTypeInformation
$acceptanceStatus = 'not-formal'
if ([string]$runner.matrixPreset -ceq 'formal') {
    $gate = $runner.formalAcceptance
    $violations = [System.Collections.Generic.List[string]]::new()
    if ($matrix.Count -ne [int]$gate.requiredScenarioCount) {
        $violations.Add(
            "Expected $($gate.requiredScenarioCount) scenarios; got $($matrix.Count).")
    }
    foreach ($row in $matrix) {
        if ([int]$row.pairedCount -ne [int]$gate.requiredPairsPerScenario) {
            $violations.Add(
                "$($row.scenarioId): expected $($gate.requiredPairsPerScenario) pairs; got $($row.pairedCount).")
        }
        if ([double]$row.gpuAverageImprovementPercent -lt
            [double]$gate.minimumGpuAverageImprovementPercent) {
            $violations.Add(
                "$($row.scenarioId): GPU average gate failed ($($row.gpuAverageImprovementPercent)%).")
        }
        if ([double]$row.gpuP99ImprovementPercent -lt
            [double]$gate.minimumGpuP99ImprovementPercent) {
            $violations.Add(
                "$($row.scenarioId): GPU P99 gate failed ($($row.gpuP99ImprovementPercent)%).")
        }
        if ([double]$row.cpuPreparationImprovementPercent -lt
            [double]$gate.minimumCpuPreparationImprovementPercent) {
            $violations.Add(
                "$($row.scenarioId): CPU preparation gate failed ($($row.cpuPreparationImprovementPercent)%).")
        }
        if ([double]$row.uploadAverageReductionPercent -lt
            [double]$gate.minimumUploadAverageReductionPercent) {
            $violations.Add(
                "$($row.scenarioId): upload reduction gate failed ($($row.uploadAverageReductionPercent)%).")
        }
        if ([bool]$gate.requireAllPairedGpuWins -and
            [int]$row.pairedGpuWins -ne [int]$row.pairedCount) {
            $violations.Add(
                "$($row.scenarioId): paired GPU wins were $($row.pairedGpuWins)/$($row.pairedCount).")
        }
    }
    if ($violations.Count -ne 0) {
        $violations | Set-Content (Join-Path $root 'acceptance-violations.txt')
        throw "Formal acceptance rejected:`n$($violations -join "`n")"
    }
    $acceptanceStatus = 'accepted'
}
$acceptanceStatus | Set-Content (Join-Path $root 'acceptance-status.txt')
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# GPU residency manager A/B')
$lines.Add('')
$lines.Add("Acceptance: **$acceptanceStatus**")
$lines.Add('')
$lines.Add('| Scenario | GPU avg improvement | GPU P99 improvement | CPU prep improvement | Upload reduction | Miss rate A-to-B | GPU wins |')
$lines.Add('|---|---:|---:|---:|---:|---:|---:|')
foreach ($row in $matrix) {
    $lines.Add((
        '| {0} | {1:F2}% | {2:F2}% | {3:F2}% | {4:F2}% | {5:P2} to {6:P2} | {7}/{8} |' -f
        $row.scenarioId,
        $row.gpuAverageImprovementPercent,
        $row.gpuP99ImprovementPercent,
        $row.cpuPreparationImprovementPercent,
        $row.uploadAverageReductionPercent,
        $row.baselineMissRate,
        $row.optimizedMissRate,
        $row.pairedGpuWins,
        $row.pairedCount))
}
$lines.Add('')
$lines.Add('Both variants use the same fixed GraphicsBuffer capacity. This is an application-level page cache; sparse-resource claim is false.')
$lines | Set-Content (Join-Path $root 'SUMMARY.md')
Write-Output "Validated GPU residency evidence: $root"
