[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ReportPath, [string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
$report = Get-Content -Raw -LiteralPath $ReportPath | ConvertFrom-Json
if (!$report.allDigestsMatch -or $report.rows.Count -eq 0 -or $report.timestampAbi -lt 2) {
    throw 'Missing correctness/native timing evidence.'
}
if (@($report.emptyControls).Count -eq 0) { throw 'Missing empty timing controls.' }
$nativeTokens = @{}
function Test-NativeSample($sample) {
    if ($sample.status -ne 'Ready' -or [ulong]$sample.token -eq 0 -or
        [ulong]$sample.frequency -eq 0 -or [ulong]$sample.endTicks -lt [ulong]$sample.beginTicks) {
        throw 'Invalid native timing sample.'
    }
    $key = [string]$sample.token
    if ($nativeTokens.ContainsKey($key)) { throw 'Duplicate native token.' }
    $nativeTokens[$key] = $true
    $milliseconds = ([decimal]$sample.endTicks - [decimal]$sample.beginTicks) * 1000 / [decimal]$sample.frequency
    if ([Math]::Abs([double]$milliseconds - [double]$sample.elapsedMs) -gt 0.000001) {
        throw 'Native elapsed time does not match raw ticks.'
    }
}
foreach ($control in $report.emptyControls) { Test-NativeSample $control }
foreach ($row in $report.rows) {
    if (@($row.nativeSamples).Count -ne 3) { throw 'Missing total/index/query native samples.' }
    for ($i = 0; $i -lt 3; $i++) {
        Test-NativeSample $row.nativeSamples[$i]
        $metric = @('gpuTotalMs','gpuIndexMs','gpuQueryMs')[$i]
        if ([Math]::Abs([double]$row.$metric - [double]$row.nativeSamples[$i].elapsedMs) -gt 0.000001) {
            throw 'Reported GPU metric differs from its native interval.'
        }
    }
}
$groups = @($report.rows | Where-Object measured | Group-Object scenario)
$summaries = @()
foreach ($group in $groups) {
    $a = @($group.Group | Where-Object backend -eq 'full-direct-waveops')
    $b = @($group.Group | Where-Object backend -eq 'incremental-reserved-csr')
    if ($a.Count -eq 0 -or $a.Count -ne $b.Count) { throw "Unpaired samples: $($group.Name)" }
    foreach ($row in $group.Group) {
        foreach ($metric in @('gpuTotalMs','gpuIndexMs','gpuQueryMs','cpuRecordMs','cpuSubmitMs')) {
            $number = [double]$row.$metric
            if ([double]::IsNaN($number) -or [double]::IsInfinity($number) -or $number -lt 0) { throw "Invalid $metric" }
        }
    }
    foreach ($row in $a) {
        $match = @($b | Where-Object { $_.round -eq $row.round -and $_.frame -eq $row.frame })
        if ($match.Count -ne 1) { throw 'Missing/duplicate paired frame.' }
        foreach ($field in @('countDigest','xorDigest','sumDigest0','sumDigest1','capacity','staticSlots','changePercent','crossingPercent')) {
            if ($row.$field -ne $match[0].$field) { throw "Paired $field mismatch." }
        }
    }
    $metrics = @{}
    foreach ($metric in @('gpuTotalMs','gpuIndexMs','gpuQueryMs','cpuRecordMs','cpuSubmitMs')) {
        $av = @($a | ForEach-Object { [double]$_.$metric } | Sort-Object)
        $bv = @($b | ForEach-Object { [double]$_.$metric } | Sort-Object)
        $metrics[$metric] = [ordered]@{
            fullMean=($av | Measure-Object -Average).Average;
            incrementalMean=($bv | Measure-Object -Average).Average;
            fullP99=$av[[Math]::Ceiling($av.Count * 0.99)-1];
            incrementalP99=$bv[[Math]::Ceiling($bv.Count * 0.99)-1]
        }
    }
    $summaries += [ordered]@{ scenario=$group.Name; samplesPerBackend=$a.Count; metrics=$metrics;
        fullIndexBytes=$a[0].indexResidentBytes; incrementalIndexBytes=$b[0].indexResidentBytes;
        incrementalOverheadBytes=([long]$b[0].indexResidentBytes-[long]$a[0].indexResidentBytes);
        fallbackFrames=@($b | Where-Object rebuildReason -ne 0).Count }
}
$output = [ordered]@{ schemaVersion=1; device=$report.device; evidenceClass='editor-comparison-unpromoted';
    formalPerformanceEvidence=$false; allDigestsMatch=$true;
    emptyControlCount=@($report.emptyControls).Count;
    emptyControlMeanMs=($report.emptyControls.elapsedMs | Measure-Object -Average).Average;
    scenarios=$summaries } | ConvertTo-Json -Depth 12
if ($OutputPath) { $output | Set-Content -LiteralPath $OutputPath }
Write-Output $output
