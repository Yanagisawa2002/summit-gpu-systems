[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ValidationLockScript,
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe',
    [ValidateSet('Smoke','Compare')][string]$Mode = 'Smoke',
    [string]$OutputDirectory = '',
    [ValidateRange(1,16776960)][int[]]$ElementCounts = @(4097,65541,262145),
    [ValidateRange(0,1000)][int]$Warmup = 10,
    [ValidateRange(1,10000)][int]$Samples = 120,
    [switch]$AllowDirty
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not (Test-Path -LiteralPath $ValidationLockScript -PathType Leaf)) { throw 'Shared validation lock script is required.' }
if ($Mode -eq 'Compare' -and $AllowDirty) { throw 'Comparison requires a clean worktree.' }
$gitStatus = @(git -C $projectRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) { throw 'Git status failed.' }
if ($gitStatus.Count -ne 0 -and -not $AllowDirty) { throw 'Commit changes first, or use -AllowDirty only for smoke.' }
$commit = (git -C $projectRoot rev-parse HEAD).Trim()
if ($Mode -eq 'Smoke') { $ElementCounts = @(257); $Warmup = 1; $Samples = 2 }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot ('Reports/GpuSensorQuery/' + $Mode + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$resultPath = Join-Path $outputRoot 'measurements.json'
if (Test-Path -LiteralPath $resultPath) { throw 'Use a fresh output directory; stale result files are rejected.' }
$configPath = Join-Path $outputRoot 'config.json'
@{
    mode=$Mode; outputPath=$resultPath; warmup=$Warmup; samples=$Samples
    elementCounts=@($ElementCounts); distributions=@('sparse','uniform','hotspot','single-cell')
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configPath -Encoding utf8
# Hash actual imported source and native plugin, including the independent oracle.
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Packages/com.summit.gpu-sensor-pipeline') -Recurse -File |
    Where-Object { $_.Extension -in '.cs','.compute','.hlsl','.asmdef' })
$sources += Get-Item -LiteralPath (Join-Path $projectRoot 'Packages/com.summit.gpu-timestamps/Runtime/Plugins/x86_64/SummitGpuTimestamps.dll')
$sources += Get-Item -LiteralPath (Join-Path $projectRoot 'Packages/com.summit.gpu-timestamps/Runtime/Plugins/x86_64/SummitGpuTimestamps.dll.meta')
$sources += Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Assets/GpuSensorPipelineBenchmark/Runtime') -Filter 'GpuSensorQuery*.cs'
$sources += Get-Item -LiteralPath $PSCommandPath, (Join-Path $PSScriptRoot 'Summarize-GpuSensorQueryBenchmark.ps1'),
    (Join-Path $projectRoot 'Assets/GpuSensorPipelineBenchmark/Editor/GpuSensorPipelineBenchmarkBuild.cs')
$before = @($sources | Sort-Object FullName | ForEach-Object {
    @{ path=$_.FullName.Substring($projectRoot.Length + 1); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$provenance = [ordered]@{ schemaVersion=1; gitCommit=$commit; dirty=($gitStatus.Count -ne 0);
    gitStatus=$gitStatus; unityExecutable=$UnityPath; capturedUtc=[DateTime]::UtcNow.ToString('o');
    deviceDrivers=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion);
    sources=$before; status='working'; mode=$Mode }
$provenancePath = Join-Path $outputRoot 'provenance.json'
$provenance | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $provenancePath -Encoding utf8
$previous = $env:SUMMIT_QUERY_BENCHMARK_CONFIG
try {
    $env:SUMMIT_QUERY_BENCHMARK_CONFIG = $configPath
    & $ValidationLockScript -Action {
        $playerPath = Join-Path $projectRoot 'Builds/GpuSensorQuery/GpuSensorQuery.exe'
        $buildArgs = @('-batchmode','-nographics','-quit','-projectPath',('"'+$projectRoot+'"'),
            '-executeMethod','GpuSensorPipelineBenchmarkBuild.PerformBuild',
            '-gpu-sensor-pipeline-player-path',('"'+$playerPath+'"'),
            '-logFile',('"'+(Join-Path $outputRoot 'build.log')+'"'))
        $build = Start-Process -FilePath $UnityPath -ArgumentList $buildArgs -WindowStyle Hidden -PassThru
        # Wait for Unity itself; Start-Process -Wait also waits for persistent
        # Roslyn compiler-server descendants even after Unity exits on an error.
        $build.WaitForExit()
        if ($build.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $playerPath)) { throw 'Query Player build failed; see build.log.' }
        $playerFiles = @(Get-ChildItem -LiteralPath (Split-Path -Parent $playerPath) -Recurse -File | Sort-Object FullName |
            ForEach-Object { @{path=$_.FullName; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash} })
        $playerFiles | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputRoot 'player-hashes.json') -Encoding utf8
        $playerArgs = @('-batchmode','-force-d3d12','-gpu-sensor-query-benchmark',
            '-logFile',('"'+(Join-Path $outputRoot 'player.log')+'"'))
        $player = Start-Process -FilePath $playerPath -ArgumentList $playerArgs -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
        if (-not $player.WaitForExit(30 * 60 * 1000)) {
            $player.Kill(); $player.WaitForExit(); throw 'Query Player timed out.'
        }
        if ($player.ExitCode -ne 0) { throw 'Query Player failed; see player.log.' }
    }
    if (-not (Test-Path -LiteralPath $resultPath)) { throw 'No native query benchmark result was produced.' }
    foreach ($entry in $before) {
        if ((Get-FileHash -LiteralPath (Join-Path $projectRoot $entry.path) -Algorithm SHA256).Hash -cne $entry.sha256) {
            throw "Source changed during benchmark: $($entry.path)"
        }
    }
    if ((git -C $projectRoot rev-parse HEAD).Trim() -cne $commit) { throw 'HEAD changed during benchmark.' }
    $provenance.status = 'complete'
    $provenance | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $provenancePath -Encoding utf8
    & (Join-Path $PSScriptRoot 'Summarize-GpuSensorQueryBenchmark.ps1') -ReportDirectory $outputRoot
}
finally { $env:SUMMIT_QUERY_BENCHMARK_CONFIG = $previous }
Write-Host "Completed query $Mode microbenchmark: $outputRoot"
