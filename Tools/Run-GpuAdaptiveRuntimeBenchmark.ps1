[CmdletBinding()]
param(
    [ValidateSet('smoke','discovery','evaluation')][string]$Phase = 'smoke',
    [string]$UnityEditorPath = 'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe',
    [string]$ValidationLockScript = '',
    [string]$PlayerPath = '',
    [string]$OutputDirectory = '',
    [string]$MatrixPath = '',
    [int]$ElementCount = 4096, [int]$BinCount = 16,
    [int]$FramesPerSegment = 6, [int]$Repeats = 1, [int]$Seed = 9701,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (!$PlayerPath) { $PlayerPath = Join-Path $project 'Builds\AdaptiveRuntime\AdaptiveRuntime.exe' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $project ('Reports\AdaptiveRuntime-' + $Phase + '-' + (Get-Date -Format yyyyMMdd-HHmmss)) }
$PlayerPath = [IO.Path]::GetFullPath($PlayerPath)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ($Phase -eq 'evaluation' -and !(Test-Path -LiteralPath $MatrixPath)) { throw 'Evaluation requires a frozen matrix.' }
if (!$ValidationLockScript -or !(Test-Path -LiteralPath $ValidationLockScript)) { throw 'Pass the shared Invoke-SerializedValidation.ps1 path; Unity/GPU execution must be serialized.' }
if (Test-Path -LiteralPath (Join-Path $OutputDirectory 'runtime-report.json')) { throw 'Use a new output directory to preserve previous evidence.' }
$playerDirectory = Split-Path -Parent $PlayerPath
if ($OutputDirectory.StartsWith($playerDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Reports must be outside the immutable Player directory.' }
function Get-ArtifactDigest([string]$Root, [string[]]$Paths) {
    $rows = foreach ($path in ($Paths | Sort-Object)) {
        $full = [IO.Path]::GetFullPath($path)
        $relative = [IO.Path]::GetRelativePath($Root, $full).Replace('\','/')
        $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
        "$relative=$hash"
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($rows -join "`n"))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($sha.ComputeHash($bytes)).ToLowerInvariant() } finally { $sha.Dispose() }
}
& $ValidationLockScript -Action {
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    if (!$SkipBuild) {
        if (!(Test-Path -LiteralPath $UnityEditorPath)) { throw 'Unity editor unavailable.' }
        New-Item -ItemType Directory -Force -Path $playerDirectory | Out-Null
        $sourceCommit = (& git -C $project rev-parse HEAD).Trim()
        $sourceWasDirty = @(& git -C $project status --porcelain).Count -ne 0
        $sourceFiles = @(& rg --files $project -g '*.cs' -g '*.compute' -g '*.hlsl' -g '*.shader' -g '*.asmdef' -g 'package.json' -g 'ProjectSettings.asset')
        $sourceTreeDigest = Get-ArtifactDigest $project $sourceFiles
        $buildLog = Join-Path $OutputDirectory 'build.log'
        $buildArgs = @('-batchmode','-quit','-force-d3d12','-projectPath', ('"' + $project + '"'),
            '-executeMethod','GpuAdaptiveBinningBenchmarkBuild.PerformBuild',
            '-gpu-adaptive-binning-player-path', ('"' + $PlayerPath + '"'), '-logFile', ('"' + $buildLog + '"'))
        $process = Start-Process -FilePath $UnityEditorPath -ArgumentList $buildArgs -PassThru -Wait -WindowStyle Hidden
        if ($process.ExitCode -ne 0) { throw "Player build failed: $buildLog" }
    }
    if (!(Test-Path -LiteralPath $PlayerPath)) { throw 'Player missing.' }
    $identityPath = Join-Path $playerDirectory 'adaptive-build-identity.json'
    $artifactPaths = @(Get-ChildItem -LiteralPath $playerDirectory -Recurse -File | Where-Object {
        $_.FullName -ne $identityPath -and $_.Name -ne 'build-summary.txt'
    } | ForEach-Object FullName)
    $buildDigest = Get-ArtifactDigest $playerDirectory $artifactPaths
    if (!$SkipBuild) {
        $shaderPaths = @($artifactPaths | Where-Object { $_ -match '\.(assets|resS|resource)$' })
        if ($shaderPaths.Count -eq 0) { throw 'Compiled shader container identity unavailable.' }
        $compiler = Join-Path (Split-Path -Parent $UnityEditorPath) 'Data\Tools\UnityShaderCompiler.exe'
        if (!(Test-Path -LiteralPath $compiler)) { throw 'Shader compiler unavailable.' }
        $compilerHash = (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash.ToLowerInvariant()
        $settingsHash = (Get-FileHash -LiteralPath (Join-Path $project 'ProjectSettings/ProjectSettings.asset') -Algorithm SHA256).Hash.ToLowerInvariant()
        $identity = [ordered]@{ schemaVersion = 1; buildIdentity = $buildDigest;
            sourceCommit = $sourceCommit; sourceWasDirty = $sourceWasDirty; sourceTreeSha256 = $sourceTreeDigest;
            shaderIdentity = Get-ArtifactDigest $playerDirectory $shaderPaths;
            compilerIdentity = "UnityShaderCompiler-sha256:$compilerHash;Windows64;DX12;Development;PlayerSettings-sha256:$settingsHash" }
        $identity | ConvertTo-Json | Set-Content -LiteralPath $identityPath -Encoding utf8
    } else {
        if (!(Test-Path -LiteralPath $identityPath)) { throw 'Rebuild: independently generated artifact identity is missing.' }
        $identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
        if ($identity.schemaVersion -ne 1 -or $identity.buildIdentity -ne $buildDigest) { throw 'Player contents changed since build; rebuild before calibration/evaluation.' }
    }
    Copy-Item -LiteralPath $identityPath -Destination (Join-Path $OutputDirectory 'build-identity.json')
    $adapters = @(Get-CimInstance Win32_VideoController | Where-Object { $_.PNPDeviceID -match 'VEN_1002&DEV_7551' })
    if ($adapters.Count -ne 1 -or !$adapters[0].DriverVersion) { throw 'Cannot independently identify one R9700 driver version.' }
    $playerArgs = @('-batchmode','-force-d3d12','-screen-width','64','-screen-height','64',
        '-gpu-adaptive-runtime','-runtime-output', ('"' + $OutputDirectory + '"'),
        '-runtime-phase',$Phase,'-runtime-n',$ElementCount,'-runtime-c',$BinCount,
        '-runtime-frames',$FramesPerSegment,'-runtime-repeats',$Repeats,'-runtime-seed',$Seed,
        '-runtime-driver',$adapters[0].DriverVersion,'-runtime-build',$identity.buildIdentity,'-runtime-shader',$identity.shaderIdentity,
        '-runtime-compiler',('"' + $identity.compilerIdentity + '"'),
        '-logFile',('"' + (Join-Path $OutputDirectory 'player.log') + '"'))
    if ($MatrixPath) { $playerArgs += @('-runtime-matrix', ('"' + [IO.Path]::GetFullPath($MatrixPath) + '"')) }
    $playerArgs | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'arguments.json') -Encoding utf8
    $process = Start-Process -FilePath $PlayerPath -ArgumentList $playerArgs -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "Runtime benchmark failed: $OutputDirectory" }
    $result = Get-Content -LiteralPath (Join-Path $OutputDirectory 'runtime-report.json') -Raw | ConvertFrom-Json
    if (!$result.passed -or $result.samples.Count -lt 1) { throw 'Missing or failed runtime evidence.' }
    Write-Output "Adaptive runtime $Phase passed: $($result.samples.Count) samples, $($result.validationCount) CSR checks. $OutputDirectory"
}
