#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$UnityPath = 'C:/Program Files/Unity/Hub/Editor/6000.5.2f1/Editor/Unity.exe',
    [ValidateSet('smoke', 'formal')][string]$Preset = 'smoke',
    [string]$OutputDirectory = '',
    [switch]$SkipBuild,
    [switch]$RebuildNative
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
Import-Module (Join-Path $PSScriptRoot 'GpuRuntimeSchedulerEvidence.psm1') -Force
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot ('Reports/GpuDeadlineScheduler/runtime-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-ffff')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
Assert-RuntimeFreshOutput $OutputDirectory
$initialStatus = @(& git -C $taskRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read Git state.' }
if ($Preset -eq 'formal' -and $initialStatus.Count -gt 0) { throw 'Formal comparison requires clean user source before building/running.' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$buildRoot = Join-Path $taskRoot 'Builds/GpuRuntimeScheduler'
$buildProvenancePath = Join-Path $buildRoot 'runtime-build-provenance.json'
function Quote-Argument([string]$Value) { return '"' + $Value.Replace('"', '\"') + '"' }
function Run-Bounded([string]$Executable, [string[]]$Arguments, [int]$Minutes) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForExit($Minutes * 60000)) { $process.Kill(); $process.WaitForExit(); throw 'Validation process timed out.' }
        if ($process.ExitCode -ne 0) { throw "Validation process failed: $($process.ExitCode). See $OutputDirectory" }
    } finally { $process.Dispose() }
}
function Get-Drivers {
    return @(Get-CimInstance Win32_VideoController | Sort-Object PNPDeviceID | Select-Object Name,DriverVersion,DriverDate,PNPDeviceID,DeviceID)
}
# Caller holds the shared Unity/GPU mutex throughout, including every child process exit.
if (-not $SkipBuild) {
    $before = Get-RuntimeManifest $taskRoot -Source
    $before | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'source-before-build.json')
    $beforeText = @{}
    foreach ($path in @('ProjectSettings/ProjectSettings.asset','ProjectSettings/SceneTemplateSettings.json')) {
        $absolute=Join-Path $taskRoot $path
        $beforeText[$path] = if (Test-Path -LiteralPath $absolute) { [IO.File]::ReadAllText($absolute) } else { $null }
    }
    $buildCommit=(& git -C $taskRoot rev-parse HEAD).Trim()
    # Fresh directory prevents old managed/native DLLs from lingering in the built payload.
    $payloadRoot = Join-Path $buildRoot ('player-' + [Guid]::NewGuid().ToString('N'))
    $playerPath = Join-Path $payloadRoot 'GpuRuntimeScheduler.exe'
    if ($RebuildNative) {
        & (Join-Path $taskRoot 'Tools/Build-SummitGpuTimestampPlugin.ps1') -UnityEditorPath (Split-Path -Parent (Split-Path -Parent $UnityPath))
    }
    Run-Bounded $UnityPath @('-batchmode','-quit','-force-d3d12','-projectPath',(Quote-Argument $taskRoot),
        '-executeMethod','GpuDeadlineSchedulerBenchmarkBuild.PerformBuild','-gpu-deadline-player-path',(Quote-Argument $playerPath),
        '-logFile',(Quote-Argument (Join-Path $OutputDirectory 'build.log'))) 30
    $after = Get-RuntimeManifest $taskRoot -Source
    $old = @{}; foreach ($file in $before.files) { $old[$file.path]=$file }
    $new = @{}; foreach ($file in $after.files) { $new[$file.path]=$file }
    $drift = @(foreach ($path in @(@($old.Keys)+@($new.Keys) | Sort-Object -Unique)) {
        $a=$old[$path]; $b=$new[$path]
        if ($null -eq $a -or $null -eq $b -or $a.sha256 -ne $b.sha256) {
            [pscustomobject]@{ path=$path; before=$a; after=$b;
                beforeText=$beforeText[$path]; afterText=if ($beforeText.ContainsKey($path) -and (Test-Path -LiteralPath (Join-Path $taskRoot $path))) { [IO.File]::ReadAllText((Join-Path $taskRoot $path)) } else { $null } }
        }
    })
    $drift | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'generated-build-drift.json')
    $allowed=@('ProjectSettings/ProjectSettings.asset','ProjectSettings/SceneTemplateSettings.json')
    if ($RebuildNative) { $allowed += 'Packages/com.summit.gpu-timestamps/Runtime/Plugins/x86_64/SummitGpuTimestamps.dll' }
    if (@($drift | Where-Object path -NotIn $allowed).Count -gt 0) { throw 'Unexpected source drift during build; see exact drift record.' }
    & git -C $taskRoot diff --binary | Set-Content -LiteralPath (Join-Path $OutputDirectory 'build-worktree.diff')
    @{
        schemaVersion=2; commit=$buildCommit; initialGitStatus=$initialStatus; initialDirty=($initialStatus.Count -gt 0)
        sourceBefore=$before; sourceAfter=$after; generatedBuildDrift=$drift
        payloadRoot=$payloadRoot; payload=(Get-RuntimeManifest $payloadRoot); playerPath=$playerPath
        nativeRebuilt=[bool]$RebuildNative; builtAtUtc=[DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $buildProvenancePath
}
if (-not (Test-Path -LiteralPath $buildProvenancePath)) { throw 'Missing build provenance; rebuild the Player.' }
$build = Get-Content -LiteralPath $buildProvenancePath -Raw | ConvertFrom-Json
if ($build.schemaVersion -ne 2) { throw 'Old build provenance; rebuild the Player.' }
if ($Preset -eq 'formal' -and $build.initialDirty) { throw 'Formal comparison refuses a build from dirty user source.' }
$playerPath=$build.playerPath
if ($build.sourceAfter.sha256 -ne (Get-RuntimeManifest $taskRoot -Source).sha256 -or
    $build.payload.sha256 -ne (Get-RuntimeManifest $build.payloadRoot).sha256) { throw 'Player payload or source inputs changed since build.' }
$batches=if ($Preset -eq 'smoke') { 4 } else { 256 }; $rounds=if ($Preset -eq 'smoke') { 1 } else { 4 }
$drivers=Get-Drivers
if ($drivers.Count -eq 0 -or @($drivers | Where-Object { [string]::IsNullOrWhiteSpace($_.DriverVersion) }).Count -gt 0) { throw 'Exact Windows driver versions unavailable.' }
@{ build=$build; preset=$Preset; batches=$batches; rounds=$rounds; drivers=$drivers; timestamp=[DateTime]::UtcNow.ToString('o') } |
    ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'provenance.json')
Run-Bounded $playerPath @('-batchmode','-force-d3d12','-screen-width','320','-screen-height','200',
    '-gpu-runtime-scheduler-output',(Quote-Argument $OutputDirectory),'-gpu-runtime-scheduler-batches',[string]$batches,
    '-gpu-runtime-scheduler-rounds',[string]$rounds,'-logFile',(Quote-Argument (Join-Path $OutputDirectory 'player.log'))) 30
if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'completed.txt')) -or (Test-Path -LiteralPath (Join-Path $OutputDirectory 'failure.txt'))) { throw 'Player did not complete successfully.' }
$postSource=Get-RuntimeManifest $taskRoot -Source; $postPayload=Get-RuntimeManifest $build.payloadRoot; $postDrivers=Get-Drivers
@{ source=$postSource; payload=$postPayload; drivers=$postDrivers } | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'post-run-manifests.json')
if ($build.sourceAfter.sha256 -ne $postSource.sha256 -or $build.payload.sha256 -ne $postPayload.sha256 -or
    ($drivers | ConvertTo-Json -Compress) -ne ($postDrivers | ConvertTo-Json -Compress)) { throw 'Source, Player payload or driver changed during measurement.' }
$environment=Get-Content -LiteralPath (Join-Path $OutputDirectory 'environment.json') -Raw | ConvertFrom-Json
$matched=@($drivers | Where-Object Name -EQ $environment.gpu)
if ($matched.Count -ne 1) { throw 'Cannot unambiguously match Player GPU to Windows driver identity.' }
$environment | Add-Member -NotePropertyName windowsVideoController -NotePropertyValue $matched[0]
$environment | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'environment.json')
$validation=Test-RuntimeMatrix $OutputDirectory $rounds $batches
$validation | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'evidence-validation.json')
Write-Output "Runtime comparison complete: $OutputDirectory ($($validation.cases) validated cases, $($validation.nativeSamples) matched native samples)."
