[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$inputRoot = (Resolve-Path -LiteralPath $InputDirectory).Path
$reports = @(
    Get-ChildItem -LiteralPath $inputRoot -Filter '*-r*.json' -File |
    ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    }
)
if ($reports.Count -lt 2) {
    throw "At least one baseline and one optimized report are required."
}
if (@($reports | Where-Object { -not $_.qualityPassed }).Count -gt 0) {
    throw "One or more reports failed deterministic output validation."
}

function Get-Median {
    param([double[]]$Values)
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return 0.0 }
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2.0
}

function Get-VariantMetric {
    param([string]$Variant, [string]$Property)
    [double[]]$values = @(
        $reports |
        Where-Object { $_.variant -eq $Variant } |
        ForEach-Object { [double]($_.$Property) })
    return Get-Median -Values $values
}

function Get-Improvement {
    param([double]$Baseline, [double]$Optimized)
    if ($Baseline -eq 0.0) { return 0.0 }
    return ($Baseline - $Optimized) / $Baseline * 100.0
}

$baselineFrameAverage = Get-VariantMetric 'baseline' 'frameAverageMs'
$optimizedFrameAverage = Get-VariantMetric 'optimized' 'frameAverageMs'
$baselineFrameP99 = Get-VariantMetric 'baseline' 'frameP99Ms'
$optimizedFrameP99 = Get-VariantMetric 'optimized' 'frameP99Ms'
$baselineGpuAverage = Get-VariantMetric 'baseline' 'gpuAverageMs'
$optimizedGpuAverage = Get-VariantMetric 'optimized' 'gpuAverageMs'
$baselineGpuP99 = Get-VariantMetric 'baseline' 'gpuP99Ms'
$optimizedGpuP99 = Get-VariantMetric 'optimized' 'gpuP99Ms'
$baselineLongRate = Get-VariantMetric 'baseline' 'longFrame33Rate'
$optimizedLongRate = Get-VariantMetric 'optimized' 'longFrame33Rate'
$baselineSensorUpload = Get-VariantMetric 'baseline' 'sensorLogicalUploadBytes'
$optimizedSensorUpload = Get-VariantMetric 'optimized' 'sensorLogicalUploadBytes'
$baselineResidencyUpload = Get-VariantMetric 'baseline' 'residencyLogicalUploadBytes'
$optimizedResidencyUpload = Get-VariantMetric 'optimized' 'residencyLogicalUploadBytes'
$baselineCriticalP99 = Get-VariantMetric 'baseline' 'criticalLatencyP99Ms'
$optimizedCriticalP99 = Get-VariantMetric 'optimized' 'criticalLatencyP99Ms'
$hashes = @($reports | Select-Object -ExpandProperty compositeOutputHash -Unique)
$hashMatch = $hashes.Count -eq 1

$rows = foreach ($report in $reports) {
    [pscustomobject]@{
        Variant = $report.variant
        FrameAverageMs = $report.frameAverageMs
        FrameP99Ms = $report.frameP99Ms
        GpuAverageMs = $report.gpuAverageMs
        GpuP99Ms = $report.gpuP99Ms
        LongFrame33Rate = $report.longFrame33Rate
        SensorUploadBytes = $report.sensorLogicalUploadBytes
        ResidencyUploadBytes = $report.residencyLogicalUploadBytes
        CriticalP99Ms = $report.criticalLatencyP99Ms
        Renderer = $report.rendererAlgorithm
        OutputHash = $report.compositeOutputHash
    }
}
$rows | Export-Csv -LiteralPath (Join-Path $inputRoot 'report-summary.csv') `
    -NoTypeInformation

$summary = [System.Text.StringBuilder]::new()
[void]$summary.AppendLine('# GPU Stress Showcase A/B Summary')
[void]$summary.AppendLine()
[void]$summary.AppendLine("- Reports: $($reports.Count)")
[void]$summary.AppendLine("- Deterministic output hashes identical: $hashMatch")
[void]$summary.AppendLine("- GPU: $($reports[0].graphicsDeviceName) / $($reports[0].graphicsApi)")
[void]$summary.AppendLine()
[void]$summary.AppendLine('| Metric | A baseline | B optimized | B improvement |')
[void]$summary.AppendLine('|---|---:|---:|---:|')
[void]$summary.AppendLine(('| Frame average | {0:F3} ms | {1:F3} ms | {2:F2}% |' -f $baselineFrameAverage, $optimizedFrameAverage, (Get-Improvement $baselineFrameAverage $optimizedFrameAverage)))
[void]$summary.AppendLine(('| Frame P99 | {0:F3} ms | {1:F3} ms | {2:F2}% |' -f $baselineFrameP99, $optimizedFrameP99, (Get-Improvement $baselineFrameP99 $optimizedFrameP99)))
[void]$summary.AppendLine(('| GPU average | {0:F3} ms | {1:F3} ms | {2:F2}% |' -f $baselineGpuAverage, $optimizedGpuAverage, (Get-Improvement $baselineGpuAverage $optimizedGpuAverage)))
[void]$summary.AppendLine(('| GPU P99 | {0:F3} ms | {1:F3} ms | {2:F2}% |' -f $baselineGpuP99, $optimizedGpuP99, (Get-Improvement $baselineGpuP99 $optimizedGpuP99)))
[void]$summary.AppendLine(('| Frames >33.3 ms | {0:P2} | {1:P2} | {2:F2}% |' -f $baselineLongRate, $optimizedLongRate, (Get-Improvement $baselineLongRate $optimizedLongRate)))
[void]$summary.AppendLine(('| Sensor CPU upload | {0:N0} B | {1:N0} B | {2:F2}% |' -f $baselineSensorUpload, $optimizedSensorUpload, (Get-Improvement $baselineSensorUpload $optimizedSensorUpload)))
[void]$summary.AppendLine(('| Page upload | {0:N0} B | {1:N0} B | {2:F2}% |' -f $baselineResidencyUpload, $optimizedResidencyUpload, (Get-Improvement $baselineResidencyUpload $optimizedResidencyUpload)))
[void]$summary.AppendLine(('| Critical completion P99* | {0:F3} ms | {1:F3} ms | {2:F2}% |' -f $baselineCriticalP99, $optimizedCriticalP99, (Get-Improvement $baselineCriticalP99 $optimizedCriticalP99)))
[void]$summary.AppendLine()
[void]$summary.AppendLine('A uses ScalarAoS full visible-index materialization, CPU sensor production/upload, per-sensor CSR rebuild, visible-page rebuild/upload, portable primitives, and FIFO scheduling.')
[void]$summary.AppendLine()
[void]$summary.AppendLine('B uses Wave64 no-copy Tile32, a GPU-resident producer with one shared CSR, persistent LRU delta uploads, the matching device profile, and least-slack scheduling on the main queue. Async compute remains disabled because the AMD calibration rejected it.')
[void]$summary.AppendLine()
[void]$summary.AppendLine('*Critical completion is observed at Player-loop polling granularity in this visual integration demo. Use the dedicated native timestamp benchmark for resume claims about sub-frame deadline latency.')
[void]$summary.AppendLine()
if (-not $hashMatch) {
    [void]$summary.AppendLine('**Acceptance failed:** output hashes differ between A and B.')
}
[System.IO.File]::WriteAllText(
    (Join-Path $inputRoot 'SUMMARY.md'),
    $summary.ToString())
if (-not $hashMatch) { exit 2 }
Write-Host "Summary written: $(Join-Path $inputRoot 'SUMMARY.md')"
