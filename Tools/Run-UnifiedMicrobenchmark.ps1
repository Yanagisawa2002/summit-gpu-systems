[CmdletBinding()]
param(
    [ValidateSet('Diagnostic','Validation','Formal')][string]$Phase = 'Diagnostic',
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$LockScript,
    [string]$UnityPath = 'C:/Program Files/Unity/Hub/Editor/6000.5.2f1/Editor/Unity.exe',
    [string]$PlayerPath = '',
    [switch]$SkipBuild,
    [string]$StatusPath = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Output already exists; do not overwrite evidence: $output" }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$sourceCommit = (& git -C $projectRoot rev-parse HEAD).Trim()
$dirty = @(& git -C $projectRoot status --porcelain)
if ($Phase -ne 'Diagnostic' -and $dirty.Count -gt 0) { throw 'Validation/formal requires a committed clean source tree.' }
if (!$PlayerPath) { $PlayerPath = Join-Path $output 'Player/SummitUnified.exe' }
$PlayerPath = [IO.Path]::GetFullPath($PlayerPath)
$buildIdentityPath = Join-Path (Split-Path $PlayerPath -Parent) 'unified-build-identity.json'
function SourceManifest {
    @(& git -C $projectRoot ls-files --cached --others --exclude-standard Assets Packages Tools ProjectSettings) |
        Sort-Object -Unique | ForEach-Object {
            $file = Join-Path $projectRoot $_
            if (Test-Path -LiteralPath $file -PathType Leaf) {
                [pscustomobject]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }
            }
        }
}
function Digest([string]$value) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($value)))
}
function UpdateState([string]$phaseName, [int[]]$pids = @()) {
    if ($StatusPath) {
        $state = Get-Content -LiteralPath $StatusPath -Raw | ConvertFrom-Json
        $state.phase = $phaseName; $state.activeValidationPids = @($pids)
        $state.updatedUtc = [DateTime]::UtcNow.ToString('o')
        $state.rawPaths = @($state.rawPaths) + @($output) | Sort-Object -Unique
        $state | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $StatusPath
    }
}
$sourceManifest = @(SourceManifest)
$sourceDigest = Digest ($sourceManifest | ConvertTo-Json -Depth 5 -Compress)
$sourceManifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $output 'source-sha256.json')
$provenance = [ordered]@{ phase = $Phase; status = 'working'; sourceCommit = $sourceCommit; sourceDigest = $sourceDigest;
    dirtyDiagnostic = ($dirty.Count -gt 0); projectPath = $projectRoot; unityPath = $UnityPath; playerPath = $PlayerPath;
    lock = 'Local\CodexR9700VNextUnityGpu'; startUtc = [DateTime]::UtcNow.ToString('o'); commands = @(); processes = @() }
$provenance.hardware = [ordered]@{
    gpu = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, DriverDate, PNPDeviceID)
    cpu = @(Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors)
    os = [Environment]::OSVersion.VersionString
    powershell = $PSVersionTable.PSVersion.ToString()
}
function SaveProvenance { $provenance | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $output 'provenance.json') }
function RunOwned([string]$exe, [string[]]$arguments, [string]$label) {
    $provenance.commands += [pscustomobject]@{ executable = $exe; arguments = $arguments }
    SaveProvenance
    $started = [DateTime]::UtcNow
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    UpdateState "$Phase $label running under shared lock" @($process.Id)
    if (!$process.WaitForExit(1800000)) {
        Stop-Process -Id $process.Id -Force
        throw "Owned $label process $($process.Id) exceeded the fixed 30-minute timeout. No retry."
    }
    $provenance.processes += [pscustomobject]@{ label = $label; pid = $process.Id; startedUtc = $started.ToString('o'); endUtc = [DateTime]::UtcNow.ToString('o'); exitCode = $process.ExitCode }
    SaveProvenance; UpdateState "$Phase $label exited" @()
    if ($process.ExitCode -ne 0) { throw "$label failed: exit $($process.ExitCode). Evidence retained; no retry." }
}
try {
    & $LockScript -Action {
        if (!$SkipBuild) {
            New-Item -ItemType Directory -Path (Split-Path $PlayerPath -Parent) -Force | Out-Null
            RunOwned $UnityPath @('-batchmode','-quit','-force-d3d12','-projectPath',"`"$projectRoot`"",'-executeMethod','GpuSensorPipelineBenchmarkBuild.PerformBuild',
                '-gpu-sensor-pipeline-player-path',"`"$PlayerPath`"",'-summit-release-microbenchmark','-logFile',"`"$(Join-Path $output 'build.log')`"") 'ReleaseBuild'
            if (!(Test-Path $PlayerPath)) { throw 'Build did not produce Player.' }
            $identity = [ordered]@{ sourceCommit = $sourceCommit; sourceDigest = $sourceDigest; development = $false;
                files = @(Get-ChildItem -LiteralPath (Split-Path $PlayerPath -Parent) -File -Recurse | Where-Object { $_.FullName -ne $buildIdentityPath } |
                    ForEach-Object { [pscustomobject]@{ path = [IO.Path]::GetRelativePath((Split-Path $PlayerPath -Parent), $_.FullName); sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash } }) }
            $identity | ConvertTo-Json -Depth 10 | Set-Content $buildIdentityPath
        }
        $identity = Get-Content $buildIdentityPath -Raw | ConvertFrom-Json
        if ($identity.sourceDigest -ne $sourceDigest) { throw 'Player identity does not match current source.' }
        foreach ($file in $identity.files) {
            if ((Get-FileHash (Join-Path (Split-Path $PlayerPath -Parent) $file.path) -Algorithm SHA256).Hash -ne $file.sha256) { throw "Player file changed: $($file.path)" }
        }
        Copy-Item -LiteralPath $buildIdentityPath -Destination (Join-Path $output 'build-identity.json')
        $count = if ($Phase -eq 'Formal') { 5 } else { 1 }
        for ($i = 0; $i -lt $count; $i++) {
            $rawPath = Join-Path $output "process-$i.json"
            $config = [ordered]@{ phase = $Phase.ToLowerInvariant(); processIndex = $i; outputPath = $rawPath; sourceCommit = $sourceCommit;
                warmup = $(if ($Phase -eq 'Diagnostic') { 2 } else { 8 }); querySamples = $(if ($Phase -eq 'Diagnostic') { 2 } else { 30 });
                indexSamples = $(if ($Phase -eq 'Diagnostic') { 2 } else { 24 }) }
            $configPath = Join-Path $output "config-$i.json"
            $config | ConvertTo-Json | Set-Content $configPath
            $oldConfig = $env:SUMMIT_UNIFIED_CONFIG
            try {
                $env:SUMMIT_UNIFIED_CONFIG = $configPath
                RunOwned $PlayerPath @('-batchmode','-force-d3d12','-summit-unified-microbenchmark','-logFile',"`"$(Join-Path $output "player-$i.log")`"") "Player-$i"
            } finally { $env:SUMMIT_UNIFIED_CONFIG = $oldConfig }
            $raw = Get-Content $rawPath -Raw | ConvertFrom-Json -Depth 30
            if ($raw.status -ne 'complete' -or $raw.validation.Count -ne 18) { throw 'Incomplete matrix or failed validation; no retry.' }
            Write-Output "$Phase process $i complete: $($raw.rows.Count) raw rows."
        }
        $after = @(SourceManifest)
        if ((Digest ($after | ConvertTo-Json -Depth 5 -Compress)) -ne $sourceDigest) { throw 'Source changed during measurement.' }
    }
    $provenance.status = 'complete'
} catch {
    $provenance.status = 'failed'; $provenance.error = $_.ToString(); throw
} finally {
    $provenance.endUtc = [DateTime]::UtcNow.ToString('o'); SaveProvenance
    UpdateState "$Phase $($provenance.status); evidence retained at $output" @()
}
