[CmdletBinding()]
param(
    [string]$UnityPath = 'C:/Program Files/Unity/Hub/Editor/6000.5.2f1/Editor/Unity.exe',
    [ValidateSet('smoke', 'formal')][string]$Preset = 'smoke',
    [string]$OutputDirectory = '',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot ('Reports/GpuDeadlineScheduler/runtime-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$playerPath = Join-Path $taskRoot 'Builds/GpuRuntimeScheduler/GpuRuntimeScheduler.exe'
$buildProvenancePath = Join-Path (Split-Path -Parent $playerPath) 'runtime-build-provenance.json'
function Quote-Argument([string]$Value) { return '"' + $Value.Replace('"', '\"') + '"' }
function Get-SourceFingerprint {
    $paths = @(& git -C $taskRoot ls-files --cached --others --exclude-standard -- Assets Packages ProjectSettings) | Sort-Object -Unique
    if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate build inputs.' }
    $lines = foreach ($path in $paths) {
        $absolute = Join-Path $taskRoot $path
        if (Test-Path -LiteralPath $absolute -PathType Leaf) { $path + ':' + (Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash }
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))) }
    finally { $sha.Dispose() }
}
function Run-Bounded([string]$Executable, [string[]]$Arguments, [int]$Minutes) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit($Minutes * 60000)) { $process.Kill(); $process.WaitForExit(); throw 'Validation process timed out.' }
        if ($process.ExitCode -ne 0) { throw "Validation process failed: $($process.ExitCode). See $OutputDirectory" }
    } finally { $process.Dispose() }
}
# Caller must hold the shared Unity/GPU lock for this entire script, including child process exit.
if (-not $SkipBuild) {
    & (Join-Path $taskRoot 'Tools/Build-SummitGpuTimestampPlugin.ps1') -UnityEditorPath (Split-Path -Parent (Split-Path -Parent $UnityPath))
    Run-Bounded $UnityPath @('-batchmode', '-quit', '-force-d3d12', '-projectPath', (Quote-Argument $taskRoot),
        '-executeMethod', 'GpuDeadlineSchedulerBenchmarkBuild.PerformBuild', '-gpu-deadline-player-path', (Quote-Argument $playerPath),
        '-logFile', (Quote-Argument (Join-Path $OutputDirectory 'build.log'))) 30
    @{
        commit=(& git -C $taskRoot rev-parse HEAD).Trim()
        dirty=(@(& git -C $taskRoot status --porcelain).Count -gt 0)
        sourceFingerprint=Get-SourceFingerprint
        playerSha256=(Get-FileHash -LiteralPath $playerPath -Algorithm SHA256).Hash
        builtAtUtc=[DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath $buildProvenancePath
}
if (-not (Test-Path -LiteralPath $buildProvenancePath)) { throw 'Missing build provenance; rebuild the Player.' }
$buildProvenance = Get-Content -LiteralPath $buildProvenancePath -Raw | ConvertFrom-Json
if ($buildProvenance.sourceFingerprint -ne (Get-SourceFingerprint) -or
    $buildProvenance.playerSha256 -ne (Get-FileHash -LiteralPath $playerPath -Algorithm SHA256).Hash) {
    throw 'Player or source inputs changed since build; rebuild instead of using stale evidence.'
}
$batches = if ($Preset -eq 'smoke') { 4 } else { 256 }
$rounds = if ($Preset -eq 'smoke') { 1 } else { 4 }
@{ build=$buildProvenance; preset=$Preset; batches=$batches; rounds=$rounds; timestamp=[DateTime]::UtcNow.ToString('o') } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'provenance.json')
Run-Bounded $playerPath @('-batchmode', '-force-d3d12', '-screen-width', '320', '-screen-height', '200',
    '-gpu-runtime-scheduler-output', (Quote-Argument $OutputDirectory), '-gpu-runtime-scheduler-batches', [string]$batches,
    '-gpu-runtime-scheduler-rounds', [string]$rounds, '-logFile', (Quote-Argument (Join-Path $OutputDirectory 'player.log'))) 30
if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'completed.txt'))) { throw 'Player did not report completion.' }
if ($buildProvenance.sourceFingerprint -ne (Get-SourceFingerprint)) { throw 'Source inputs changed during measurement.' }
$rows = Get-ChildItem -LiteralPath $OutputDirectory -Filter '*-measured.json' | ForEach-Object {
    $result = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
    if ($result.completed -ne $result.offered) { throw "Incomplete trace: $($_.Name)" }
    $result | Select-Object scenario, variant, timingDomain, offered, completed, criticalP99Us, criticalMissRate, makespanUs,
        gpuTimelineMakespanUs, maxBackgroundWaitUs, starvedBackground, backpressureAttempts, acceptedCostSamples, planningTicks, planningAllocatedBytes
}
if (@($rows).Count -ne 10 * $rounds) { throw 'Missing measured scenario/policy results.' }
$rows | Export-Csv -NoTypeInformation -LiteralPath (Join-Path $OutputDirectory 'summary.csv')
Write-Output "Runtime comparison complete: $OutputDirectory"
