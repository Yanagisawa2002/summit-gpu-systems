[CmdletBinding()]
param(
    [string]$UnityEditorPath =
        'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe',
    [string]$ProjectPath = '',
    [string]$BuildDirectory = '',
    [string]$EvidenceDirectory = '',
    [int]$InstanceCount = 262144,
    [int]$ViewCount = 4,
    [ValidateSet('visible5', 'visible25', 'visible75', 'visible100')]
    [string]$Visibility = 'visible25',
    [int]$Seed = 20260831,
    [int]$WarmupFrames = 60,
    [int]$VideoFrameCount = 48,
    [int]$VideoFrameInterval = 3,
    [ValidateRange(1, 60)]
    [int]$VideoFrameRate = 8,
    [string]$VideoPath = '',
    [string]$FfmpegPath = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = [IO.Path]::GetFullPath(
        (Split-Path -Parent $PSScriptRoot))
}
$ProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
if (-not (Test-Path -LiteralPath $UnityEditorPath -PathType Leaf)) {
    throw "Unity editor not found: $UnityEditorPath"
}
if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    $BuildDirectory = Join-Path $ProjectPath 'Builds\GpuSystemsShowcase'
}
if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $EvidenceDirectory = Join-Path $ProjectPath 'Reports\GpuSystemsShowcase'
}
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
New-Item -ItemType Directory -Force -Path $BuildDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null

$playerPath = Join-Path $BuildDirectory 'GpuSystemsShowcase.exe'
$buildLog = Join-Path $BuildDirectory 'showcase-build-unity.log'
$buildSummary = Join-Path $BuildDirectory 'showcase-build-summary.txt'
$commit = (& git -C $ProjectPath rev-parse HEAD).Trim()
if ($commit -notmatch '^[0-9a-f]{40}$') {
    throw 'Unable to resolve a full Git commit for the showcase receipt.'
}

if (-not $SkipBuild) {
    $buildArguments = @(
        '-batchmode',
        '-quit',
        '-projectPath', $ProjectPath,
        '-executeMethod', 'GpuSystemsShowcaseBuild.PerformBuild',
        '-gpu-systems-showcase-player-path', $playerPath,
        '-logFile', $buildLog
    )
    $build = Start-Process `
        -FilePath $UnityEditorPath `
        -ArgumentList $buildArguments `
        -PassThru `
        -Wait `
        -WindowStyle Hidden
    if ($build.ExitCode -ne 0) {
        throw "Showcase build failed with exit code $($build.ExitCode): $buildLog"
    }
}
if (-not (Test-Path -LiteralPath $playerPath -PathType Leaf)) {
    throw "Showcase Player is missing: $playerPath"
}
if (-not (Test-Path -LiteralPath $buildSummary -PathType Leaf) -or
    -not (Select-String -LiteralPath $buildSummary -Quiet -Pattern '^result=Succeeded$')) {
    throw "Showcase build summary is missing or unsuccessful: $buildSummary"
}

$screenshot = Join-Path $EvidenceDirectory 'gpu-systems-showcase.png'
$frameDirectory = Join-Path $EvidenceDirectory 'frames'
$receipt = Join-Path $EvidenceDirectory 'showcase-receipt.json'
$playerLog = Join-Path $EvidenceDirectory 'showcase-player.log'
foreach ($stale in @($screenshot, $receipt)) {
    if (Test-Path -LiteralPath $stale -PathType Leaf) {
        Remove-Item -LiteralPath $stale -Force
    }
}
if (Test-Path -LiteralPath $frameDirectory -PathType Container) {
    Get-ChildItem -LiteralPath $frameDirectory -Filter 'frame-*.png' -File |
        Remove-Item -Force
}
$playerArguments = @(
    '-force-d3d12',
    '-screen-fullscreen', '0',
    '-screen-width', '1600',
    '-screen-height', '900',
    '-logFile', $playerLog,
    '-gpu-systems-showcase-instance-count', $InstanceCount,
    '-gpu-systems-showcase-view-count', $ViewCount,
    '-gpu-systems-showcase-visibility', $Visibility,
    '-gpu-systems-showcase-seed', $Seed,
    '-gpu-systems-showcase-warmup-frames', $WarmupFrames,
    '-gpu-systems-showcase-screenshot', $screenshot,
    '-gpu-systems-showcase-frame-dir', $frameDirectory,
    '-gpu-systems-showcase-frame-count', $VideoFrameCount,
    '-gpu-systems-showcase-frame-interval', $VideoFrameInterval,
    '-gpu-systems-showcase-receipt', $receipt,
    '-gpu-systems-showcase-build-commit', $commit
)
$player = Start-Process `
    -FilePath $playerPath `
    -ArgumentList $playerArguments `
    -PassThru `
    -Wait
if ($player.ExitCode -ne 0) {
    throw "Showcase Player failed with exit code $($player.ExitCode): $playerLog"
}
if (-not (Test-Path -LiteralPath $receipt -PathType Leaf)) {
    throw "Showcase Player did not write a receipt: $receipt"
}
$result = Get-Content -Raw -LiteralPath $receipt | ConvertFrom-Json
if (-not [bool]$result.accepted -or
    [bool]$result.formalTiming -or
    [bool]$result.timingClaimsAllowed -or
    -not [bool]$result.imageParity -or
    [int]$result.writtenFrameCount -ne $VideoFrameCount) {
    throw "Showcase receipt rejected preview evidence: $receipt"
}

if (-not [string]::IsNullOrWhiteSpace($VideoPath)) {
    if ([string]::IsNullOrWhiteSpace($FfmpegPath)) {
        $ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
        if ($null -eq $ffmpeg) {
            throw 'VideoPath requires ffmpeg or an explicit FfmpegPath.'
        }
        $FfmpegPath = $ffmpeg.Source
    }
    if (-not (Test-Path -LiteralPath $FfmpegPath -PathType Leaf)) {
        throw "ffmpeg not found: $FfmpegPath"
    }
    $VideoPath = [IO.Path]::GetFullPath($VideoPath)
    New-Item -ItemType Directory -Force -Path (
        Split-Path -Parent $VideoPath) | Out-Null
    & $FfmpegPath `
        -y `
        -framerate $VideoFrameRate `
        -i (Join-Path $frameDirectory 'frame-%04d.png') `
        -c:v libx264 `
        -preset medium `
        -crf 18 `
        -pix_fmt yuv420p `
        -movflags +faststart `
        $VideoPath
    if ($LASTEXITCODE -ne 0 -or
        -not (Test-Path -LiteralPath $VideoPath -PathType Leaf)) {
        throw "ffmpeg failed to create showcase video: $VideoPath"
    }
}

Write-Host 'GPU Systems Toolkit showcase validated.'
Write-Host "Commit: $commit"
Write-Host "Screenshot: $screenshot"
Write-Host "Receipt: $receipt"
Write-Host "Image parity: $($result.imageParity)"
Write-Host "Frames: $($result.writtenFrameCount)/$VideoFrameCount"
if (-not [string]::IsNullOrWhiteSpace($VideoPath)) {
    Write-Host "Video: $VideoPath"
}
