$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$scratch = Join-Path $repo ('TestResults/QuerySummary/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $scratch | Out-Null
$summarizer = Join-Path $repo 'Tools/Summarize-GpuSensorQueryBenchmark.ps1'
$orders = @(@('CellSerial','PointChunks','PointChunksWave'), @('PointChunksWave','PointChunks','CellSerial'),
    @('PointChunks','PointChunksWave','CellSerial'), @('CellSerial','PointChunksWave','PointChunks'),
    @('PointChunksWave','CellSerial','PointChunks'), @('PointChunks','CellSerial','PointChunksWave'))
$rows = @()
for ($round=0; $round -lt 6; $round++) {
    for ($order=0; $order -lt 3; $order++) {
        foreach ($control in @($false,$true)) {
            $backend = $orders[$round][$order]
            $rows += [pscustomobject]@{ distribution='synthetic'; elementCount=1;
                backend=$(if($control){'EmptyControl'}else{$backend}); pairedBackend=$backend;
                round=$round; order=$order; sample=0; gpuMs=1.0; recordCpuMs=0.01;
                status='Ready'; frequency=1000; beginTicks=10; endTicks=11; token=$rows.Count+1;
                sourceFrame=1; resultFrame=2; queryCount=1;
                dispatches=$(if($control){0}elseif($backend -ceq 'CellSerial'){1}else{4}); scratchBytes=0 }
        }
    }
}
$original = @{status='complete';mode='Smoke'; configuration=@{distributions=@('synthetic'); elementCounts=@(1); samples=1};
    queries=@(@{CenterX=0;CenterY=0;CenterZ=0;Radius=0}); validatedOutputs=@('synthetic only'); measurements=$rows} | ConvertTo-Json -Depth 8
$provenance = @{status='complete';mode='Smoke';dirty=$true} | ConvertTo-Json
function Write-Fixture($result, $proof) {
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $scratch 'measurements.json')
    $proof | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $scratch 'provenance.json')
}
Write-Fixture ($original | ConvertFrom-Json) ($provenance | ConvertFrom-Json)
& $summarizer -ReportDirectory $scratch | Out-Null
$checks = @('missing','duplicate','ticks','order','dispatch','incomplete','dirty-compare')
foreach ($check in $checks) {
    $result = $original | ConvertFrom-Json
    $proof = $provenance | ConvertFrom-Json
    switch ($check) {
        'missing' { $result.measurements = @($result.measurements | Select-Object -Skip 1) }
        'duplicate' { $result.measurements[2] = $result.measurements[0] }
        'ticks' { $result.measurements[0].endTicks = 99 }
        'order' { $result.measurements[0].pairedBackend = 'PointChunksWave' }
        'dispatch' { $result.measurements[0].dispatches = 99 }
        'incomplete' { $proof.status = 'working' }
        'dirty-compare' { $result.mode='Compare'; $proof.mode='Compare' }
    }
    Write-Fixture $result $proof
    $rejected = $false
    try { & $summarizer -ReportDirectory $scratch | Out-Null } catch { $rejected=$true }
    if (-not $rejected) { throw "Corrupted benchmark was accepted: $check" }
}
Write-Host 'Query summary gates passed: valid fixture plus 7 corruption cases (synthetic, no GPU timing claim).'
