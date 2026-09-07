[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ReportDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$result = Get-Content -LiteralPath (Join-Path $ReportDirectory 'measurements.json') -Raw | ConvertFrom-Json
$provenance = Get-Content -LiteralPath (Join-Path $ReportDirectory 'provenance.json') -Raw | ConvertFrom-Json
if ($result.status -cne 'complete' -or $provenance.status -cne 'complete') { throw 'Incomplete benchmark or provenance.' }
if ($result.mode -cne $provenance.mode -or ($result.mode -ceq 'Compare' -and $provenance.dirty)) { throw 'Invalid comparison provenance.' }
$config = $result.configuration
$expected = $config.distributions.Count * $config.elementCounts.Count * 6 * 3 * $config.samples * 2
if ($result.measurements.Count -ne $expected) { throw "Missing timing samples: expected $expected." }
if ($result.validatedOutputs.Count -ne $config.distributions.Count * $config.elementCounts.Count) { throw 'Missing oracle outputs.' }
$summary = @()
$orders = @(@('CellSerial','PointChunks','PointChunksWave'), @('PointChunksWave','PointChunks','CellSerial'),
    @('PointChunks','PointChunksWave','CellSerial'), @('CellSerial','PointChunksWave','PointChunks'),
    @('PointChunksWave','CellSerial','PointChunks'), @('PointChunks','CellSerial','PointChunksWave'))
foreach ($distribution in $config.distributions) {
    foreach ($count in $config.elementCounts) {
        foreach ($backend in @('CellSerial','PointChunks','PointChunksWave','EmptyControl')) {
            $rows = @($result.measurements | Where-Object { $_.distribution -ceq $distribution -and $_.elementCount -eq $count -and $_.backend -ceq $backend })
            $multiplier = if ($backend -ceq 'EmptyControl') { 3 } else { 1 }
            if ($rows.Count -ne 6 * $config.samples * $multiplier) { throw 'Missing candidate/fixture timings.' }
            $seen = @{}
            foreach ($row in $rows) {
                $key = "$($row.round)/$($row.order)/$($row.sample)"
                if ($seen.ContainsKey($key)) { throw 'Duplicate timing sample.' }; $seen[$key] = $true
                if ($row.round -lt 0 -or $row.round -ge 6 -or $row.order -lt 0 -or $row.order -ge 3 -or $row.sample -lt 0 -or $row.sample -ge $config.samples -or
                    $row.gpuMs -lt 0 -or [double]::IsNaN($row.gpuMs) -or [double]::IsInfinity($row.gpuMs) -or
                    $row.recordCpuMs -lt 0 -or [double]::IsNaN($row.recordCpuMs) -or [double]::IsInfinity($row.recordCpuMs)) { throw 'Invalid timing sample.' }
                if ($row.status -cne 'Ready' -or $row.frequency -le 0 -or $row.endTicks -lt $row.beginTicks -or
                    $row.sourceFrame -gt $row.resultFrame -or $row.token -le 0) { throw 'Invalid native timing evidence.' }
                if ($row.pairedBackend -cne $orders[$row.round][$row.order] -or
                    ($backend -cne 'EmptyControl' -and $backend -cne $row.pairedBackend)) { throw 'Invalid counterbalanced order.' }
                $rawMs = ([double]($row.endTicks - $row.beginTicks) / $row.frequency) * 1000
                if ([Math]::Abs($rawMs - $row.gpuMs) -gt [Math]::Max(0.000001, $rawMs * 0.000001)) { throw 'Raw ticks disagree with duration.' }
                $dispatches = if ($backend -ceq 'EmptyControl') { 0 } elseif ($backend -ceq 'CellSerial') { 1 } else { 4 * $result.queries.Count }
                if ($row.queryCount -ne $result.queries.Count -or $row.dispatches -ne $dispatches) { throw 'Mismatched timing scope.' }
            }
            $gpu = @($rows.gpuMs | Sort-Object)
            $summary += [pscustomobject]@{ distribution=$distribution; elements=$count; backend=$backend;
                samples=$rows.Count; gpuMedianMs=$gpu[[int][Math]::Floor($gpu.Count / 2)];
                gpuP99Ms=$gpu[[int][Math]::Ceiling($gpu.Count * 0.99) - 1];
                gpuP95Ms=$gpu[[int][Math]::Ceiling($gpu.Count * 0.95) - 1];
                cpuRecordMeanMs=($rows.recordCpuMs | Measure-Object -Average).Average;
                dispatches=$rows[0].dispatches; scratchBytes=$rows[0].scratchBytes }
        }
    }
}
$summary | Export-Csv -LiteralPath (Join-Path $ReportDirectory 'summary.csv') -NoTypeInformation
$summary | Format-Table -AutoSize
if ($result.mode -ceq 'Smoke') { Write-Host 'Smoke only: these samples do not establish a performance ranking.' }
