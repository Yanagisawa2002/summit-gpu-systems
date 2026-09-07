[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ReportDirectory)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$scratchRoot = Join-Path $root 'work'
$fixture = Join-Path $scratchRoot ('primitive-summary-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$summarizer = Join-Path $PSScriptRoot '../Summarize-R9700PrimitiveCandidates.ps1'
function Expect-Rejection([scriptblock]$Action, [string]$Name) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid evidence accepted: $Name" }
}
try {
    Get-ChildItem -LiteralPath $ReportDirectory -File | Where-Object Extension -in '.csv','.json','.txt' |
        Copy-Item -Destination $fixture
    & $summarizer -ReportDirectory $fixture | Out-Null
    $rawPath = Join-Path $fixture 'raw-frames.csv'
    $rawText = [IO.File]::ReadAllText($rawPath)
    $rows = @(Import-Csv $rawPath)
    $rows[0].nativeTimestampElapsedTicks = [string]([uint64]$rows[0].nativeTimestampElapsedTicks + 1)
    $rows | Export-Csv $rawPath -NoTypeInformation
    Expect-Rejection { & $summarizer -ReportDirectory $fixture } 'native tick tampering'
    [IO.File]::WriteAllText($rawPath, $rawText)
    $rows = @(Import-Csv $rawPath)
    $rows[1..($rows.Count-1)] | Export-Csv $rawPath -NoTypeInformation
    Expect-Rejection { & $summarizer -ReportDirectory $fixture } 'missing raw sample'
    [IO.File]::WriteAllText($rawPath, $rawText)
    [IO.File]::WriteAllText($rawPath, $rawText.Replace('enqueueGcBytes','unavailableGcBytes'))
    Expect-Rejection { & $summarizer -ReportDirectory $fixture } 'missing GC measurement'
    [IO.File]::WriteAllText($rawPath, $rawText)
    $runPath = Join-Path $fixture 'runner-config.json'
    $run = Get-Content $runPath -Raw | ConvertFrom-Json
    $run.gitStart.dirty = $true
    $run | ConvertTo-Json -Depth 30 | Set-Content $runPath
    Expect-Rejection { & $summarizer -ReportDirectory $fixture -Comparison } 'dirty comparison provenance'
    Write-Output 'Candidate summary tests passed: valid smoke accepted; native tampering, missing samples, missing GC, and dirty comparison rejected.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($scratchRoot) + [IO.Path]::DirectorySeparatorChar)) { throw 'Unsafe scratch cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
