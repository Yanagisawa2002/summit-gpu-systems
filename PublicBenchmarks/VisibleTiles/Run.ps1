[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Unity,
    [string]$Workspace = (Join-Path ([IO.Path]::GetTempPath()) ('summit-visible-tiles-' + [Guid]::NewGuid().ToString('N')))
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The current device acceptance entry point requires Windows/D3D12.' }
if (-not (Test-Path -LiteralPath $Unity -PathType Leaf)) { throw 'Supply an existing Unity 6000.5.2f1 Editor executable.' }
$Workspace = [IO.Path]::GetFullPath($Workspace)
if (Test-Path -LiteralPath $Workspace) { throw 'Workspace must be a new directory; existing evidence is never overwritten.' }
$project = Join-Path $Workspace 'project'
$player = Join-Path $Workspace 'player/VisibleTiles.exe'
$evidence = Join-Path $Workspace 'evidence'
$mutex = [Threading.Mutex]::new($false, 'Local\CodexR9700VNextUnityGpu')
$owned = $false
function Invoke-OwnedProcess([string]$Executable, [string[]]$Arguments, [int]$TimeoutSeconds) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -PassThru
    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            # Only this invocation's child; never stop editors or other projects.
            Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
            throw "Owned process timed out; retain logs in $Workspace"
        }
        if ($process.ExitCode -ne 0) { throw "Owned process failed with exit $($process.ExitCode); retain logs in $Workspace" }
    }
    finally { $process.Dispose() }
}
try {
    try { $owned = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $owned=$true; throw 'Abandoned GPU lock: inspect the previous task before execution.' }
    if (-not $owned) { throw 'Shared GPU lock is busy. No build or Player was started.' }
    & python (Join-Path $PSScriptRoot 'prepare.py') --output $project
    if ($LASTEXITCODE -ne 0) { throw 'Source staging failed.' }
    Invoke-OwnedProcess $Unity @('-batchmode','-quit','-nographics','-projectPath',('"'+$project+'"'),
        '-executeMethod','Summit.VisibleTiles.Editor.Build.Windows','-visible-tiles-build',('"'+$player+'"'),
        '-logFile',('"'+(Join-Path $Workspace 'build.log')+'"')) 900
    Invoke-OwnedProcess $player @('-force-d3d12','-visible-tiles-validate','-visible-tiles-output',('"'+$evidence+'"'),
        '-logFile',('"'+(Join-Path $Workspace 'player.log')+'"')) 300
    $result = Get-Content -LiteralPath (Join-Path $evidence 'result.json') -Raw | ConvertFrom-Json
    if ($result.status -ne 'PASS' -or $result.checks.Count -ne 120 -or $result.expectedChecks -ne 120) {
        throw 'Complete 120-check device acceptance did not pass.'
    }
    Write-Host "PASS: complete device acceptance. Evidence: $evidence"
    Write-Host 'This is correctness evidence, not a timed performance comparison.'
}
finally {
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
