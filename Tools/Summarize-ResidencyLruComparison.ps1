[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ReportDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$rows = @()
foreach ($configFile in Get-ChildItem -LiteralPath $ReportDirectory -Filter config.json -Recurse) {
    $folder = $configFile.DirectoryName
    $config = Get-Content -LiteralPath $configFile.FullName -Raw | ConvertFrom-Json
    if ($config.baselinePolicy -ne 'persistent full-scan LRU v2') { continue }
    $validation = @(Import-Csv -LiteralPath (Join-Path $folder 'validation.csv'))
    if ($validation.Count -eq 0 -or @($validation | Where-Object passed -ne '1').Count) { throw "Invalid correctness evidence: $folder" }
    $raw = @(Import-Csv -LiteralPath (Join-Path $folder 'raw-samples.csv'))
    $blocks = @(Import-Csv -LiteralPath (Join-Path $folder 'block-summary.csv'))
    foreach ($pair in $blocks | Group-Object pairIndex) {
        $a = @($pair.Group | Where-Object variant -eq 'persistent-scan-lru')
        $b = @($pair.Group | Where-Object variant -eq 'persistent-heap-lru')
        if ($a.Count -ne 1 -or $b.Count -ne 1) { throw "Incomplete scan/heap pair $($pair.Name)" }
        $a = $a[0]; $b = $b[0]
        $va = @($validation | Where-Object phase -eq "block-$($a.blockIndex)")
        $vb = @($validation | Where-Object phase -eq "block-$($b.blockIndex)")
        if ($va.Count -ne 1 -or $vb.Count -ne 1 -or $va[0].resultHash -ne $vb[0].resultHash) { throw 'Paired digest mismatch.' }
        $samples = @($raw | Where-Object pairIndex -eq $pair.Name)
        if ($samples.Count -ne [int]$a.sampleCount + [int]$b.sampleCount) { throw 'Missing timing samples.' }
        foreach ($sample in $samples | Group-Object sampleIndex) {
            if ($sample.Count -ne 2) { throw 'Incomplete paired sample.' }
            foreach ($key in @('pathFrame','requestedPages','hitPages','missPages','pointPayloadBytes','totalUploadBytes')) {
                if ($sample.Group[0].$key -ne $sample.Group[1].$key) { throw "Paired input/output mismatch: $key" }
            }
        }
        $allocated = ($samples | Measure-Object -Property planningAllocatedBytes -Sum).Sum
        $rows += [pscustomobject]@{
            scenario = $config.scenarioId; physicalSlots = $config.physicalSlots; pair = [int]$pair.Name; order = $a.pairOrder
            correctnessPassed = $true; sampleCountPerVariant = [int]$a.sampleCount; planningAllocatedBytes = $allocated
            scanGpuAverageMs = [double]$a.gpuAverageMs; heapGpuAverageMs = [double]$b.gpuAverageMs
            scanGpuP99Ms = [double]$a.gpuP99Ms; heapGpuP99Ms = [double]$b.gpuP99Ms
            scanCpuPreparationAverageMs = [double]$a.cpuPreparationAverageMs; heapCpuPreparationAverageMs = [double]$b.cpuPreparationAverageMs
            scanCpuPreparationP99Ms = [double]$a.cpuPreparationP99Ms; heapCpuPreparationP99Ms = [double]$b.cpuPreparationP99Ms
            identicalLogicalUploadBytesPerFrame = [double]$a.uploadAverageBytes; missRate = [double]$a.missRate
        }
    }
}
if ($rows.Count -eq 0) { throw 'No complete v2 scan/heap comparisons found.' }
@{ schemaVersion=1; sparseResourceClaim=$false; automaticPromotion=$false; pairs=@($rows) } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $ReportDirectory 'lru-comparison.json')
Write-Output "Validated $($rows.Count) paired scan/heap comparisons. No automatic performance promotion."
