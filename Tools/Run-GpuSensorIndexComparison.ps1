[CmdletBinding()]
param(
    [ValidateSet('smoke', 'matrix')][string]$Mode = 'smoke',
    [string]$OutputDirectory = '',
    [int]$SampleFrames = 0,
    [int]$WarmupFrames = 0,
    [int]$Rounds = 0,
    [string]$UnityEditorPath = 'C:/Program Files/Unity/Hub/Editor/6000.5.2f1/Editor/Unity.exe'
)
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root ('Reports/sensor-index-' + $Mode) }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
if ($SampleFrames -le 0) { $SampleFrames = if ($Mode -eq 'smoke') { 2 } else { 60 } }
if ($WarmupFrames -le 0) { $WarmupFrames = if ($Mode -eq 'smoke') { 1 } else { 12 } }
if ($Rounds -le 0) { $Rounds = if ($Mode -eq 'smoke') { 1 } else { 4 } }
if ($Mode -eq 'matrix' -and ($Rounds -lt 2 -or $Rounds % 2 -ne 0)) {
    throw 'Matrix comparison requires an even number of rounds for balanced AB/BA order.'
}
$commit = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve source commit.' }
$dirty = @(& git -C $root status --porcelain --untracked-files=all)
if ($Mode -eq 'matrix' -and $dirty.Count -ne 0) { throw 'Matrix requires a clean committed source tree.' }

function Get-SourceHashes {
    $paths = @('Packages/com.summit.gpu-sensor-pipeline', 'Packages/com.summit.gpu-direct-binning',
        'Packages/com.summit.gpu-primitives', 'Packages/com.summit.gpu-timestamps',
        'Assets/GpuSensorIndexBenchmark', 'Tools/Run-GpuSensorIndexComparison.ps1',
        'Tools/Summarize-GpuSensorIndexComparison.ps1', 'Tools/Run-UnityEditModeTests.ps1')
    @($paths | ForEach-Object {
        Get-ChildItem -LiteralPath (Join-Path $root $_) -File -Recurse
    } | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($root.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
}
$before = Get-SourceHashes
$names = @('SUMMIT_INDEX_COMPARISON_OUTPUT', 'SUMMIT_INDEX_COMPARISON_MODE',
    'SUMMIT_INDEX_COMPARISON_SAMPLES', 'SUMMIT_INDEX_COMPARISON_WARMUP',
    'SUMMIT_INDEX_COMPARISON_ROUNDS', 'SUMMIT_INDEX_SOURCE_COMMIT')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:SUMMIT_INDEX_COMPARISON_OUTPUT = Join-Path $OutputDirectory 'comparison.json'
    $env:SUMMIT_INDEX_COMPARISON_MODE = $Mode
    $env:SUMMIT_INDEX_COMPARISON_SAMPLES = [string]$SampleFrames
    $env:SUMMIT_INDEX_COMPARISON_WARMUP = [string]$WarmupFrames
    $env:SUMMIT_INDEX_COMPARISON_ROUNDS = [string]$Rounds
    $env:SUMMIT_INDEX_SOURCE_COMMIT = $commit
    # Caller must hold the shared cross-task GPU/Unity lock throughout this call.
    if ($Mode -eq 'matrix') {
        $correctnessPath = Join-Path $OutputDirectory 'correctness/results.xml'
        & (Join-Path $PSScriptRoot 'Run-UnityEditModeTests.ps1') -UnityEditorPath $UnityEditorPath `
            -ProjectPath $root -ResultsPath $correctnessPath `
            -TestFilter 'Summit.GpuSensorPipeline.Tests' -UseGraphics -ForceDirect3D12
        [xml]$correctness = Get-Content -Raw $correctnessPath
        if ([int]$correctness.'test-run'.passed -eq 0 -or [int]$correctness.'test-run'.skipped -ne 0) {
            throw 'Matrix requires fresh passing, non-skipped sensor correctness results.'
        }
    }
    & (Join-Path $PSScriptRoot 'Run-UnityEditModeTests.ps1') -UnityEditorPath $UnityEditorPath `
        -ProjectPath $root -ResultsPath (Join-Path $OutputDirectory 'results.xml') `
        -TestFilter 'Summit.GpuSensorIndex.Benchmark.Tests.GpuSensorIndexComparison.Compare' `
        -UseGraphics -ForceDirect3D12
    [xml]$tests = Get-Content -Raw (Join-Path $OutputDirectory 'results.xml')
    if ([int]$tests.'test-run'.passed -ne 1 -or [int]$tests.'test-run'.skipped -ne 0) {
        throw 'Comparison must execute exactly one passing test, with no skipped cases.'
    }
    $after = Get-SourceHashes
    if (($before | ConvertTo-Json -Depth 5 -Compress) -ne ($after | ConvertTo-Json -Depth 5 -Compress)) {
        throw 'Source or native plugin payload changed during comparison.'
    }
    [ordered]@{ schemaVersion=1; commit=$commit; mode=$Mode; dirtyAtStart=$dirty;
        sourceHashes=$before; sourceStable=$true; samples=$SampleFrames; warmup=$WarmupFrames;
        rounds=$Rounds; editorOnly=$true; formalPerformanceEvidence=$false;
        reportSha256=(Get-FileHash -LiteralPath $env:SUMMIT_INDEX_COMPARISON_OUTPUT -Algorithm SHA256).Hash
    } | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutputDirectory 'provenance.json')
    & (Join-Path $PSScriptRoot 'Summarize-GpuSensorIndexComparison.ps1') `
        -ReportPath $env:SUMMIT_INDEX_COMPARISON_OUTPUT -OutputPath (Join-Path $OutputDirectory 'summary.json')
}
finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
