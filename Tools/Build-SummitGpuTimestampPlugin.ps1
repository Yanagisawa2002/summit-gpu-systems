[CmdletBinding()]
param(
    [string]$UnityEditorPath =
        'C:\Program Files\Unity\Hub\Editor\6000.5.2f1',
    [string]$ScopeCppSdkRoot =
        'C:\Program Files\Microsoft Visual Studio\18\Community\SDK\ScopeCppSDK\vc15',
    [switch]$KeepIntermediate
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$nativeRoot =
    Join-Path $repoRoot 'Packages\com.summit.gpu-timestamps\Native~'
$source = Join-Path $nativeRoot 'SummitGpuTimestamps.cpp'
$outputRoot =
    Join-Path $repoRoot 'Packages\com.summit.gpu-timestamps\Runtime\Plugins\x86_64'
$intermediateRoot = Join-Path $nativeRoot 'Build'

$cl = Join-Path $ScopeCppSdkRoot 'VC\bin\cl.exe'
$link = Join-Path $ScopeCppSdkRoot 'VC\bin\link.exe'
$vcInclude = Join-Path $ScopeCppSdkRoot 'VC\include'
$sdkInclude = Join-Path $ScopeCppSdkRoot 'SDK\include'
$vcLib = Join-Path $ScopeCppSdkRoot 'VC\lib'
$sdkLib = Join-Path $ScopeCppSdkRoot 'SDK\lib'
$unityPluginApi = Join-Path $UnityEditorPath 'Editor\Data\PluginAPI'

$requiredPaths = @(
    $cl,
    $link,
    $source,
    (Join-Path $unityPluginApi 'IUnityGraphicsD3D12.h'),
    (Join-Path $sdkInclude 'um\d3d12.h'),
    (Join-Path $sdkLib 'd3d12.lib')
)
foreach ($requiredPath in $requiredPaths) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required native build input was not found: $requiredPath"
    }
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
New-Item -ItemType Directory -Force -Path $intermediateRoot | Out-Null

$dll = Join-Path $outputRoot 'SummitGpuTimestamps.dll'
$pdb = Join-Path $intermediateRoot 'SummitGpuTimestamps.pdb'
$importLibrary = Join-Path $intermediateRoot 'SummitGpuTimestamps.lib'
$obj = Join-Path $intermediateRoot 'SummitGpuTimestamps.obj'

$compileArguments = @(
    '/nologo',
    '/c',
    '/std:c++14',
    '/O2',
    '/GL',
    '/MT',
    '/W4',
    '/WX',
    '/DWIN32',
    '/D_WINDOWS',
    '/DNDEBUG',
    "/I$nativeRoot",
    "/I$unityPluginApi",
    "/I$vcInclude",
    "/I$(Join-Path $sdkInclude 'shared')",
    "/I$(Join-Path $sdkInclude 'um')",
    "/I$(Join-Path $sdkInclude 'ucrt')",
    "/Fo$obj",
    $source
)

& $cl @compileArguments
if ($LASTEXITCODE -ne 0) {
    throw "C++ compilation failed with exit code $LASTEXITCODE."
}

$linkArguments = @(
    '/NOLOGO',
    '/DLL',
    '/MACHINE:X64',
    '/INCREMENTAL:NO',
    '/LTCG',
    '/OPT:REF',
    '/OPT:ICF',
    "/OUT:$dll",
    "/PDB:$pdb",
    "/IMPLIB:$importLibrary",
    "/LIBPATH:$vcLib",
    "/LIBPATH:$sdkLib",
    $obj,
    'd3d12.lib',
    'dxgi.lib',
    'kernel32.lib',
    'user32.lib',
    'uuid.lib'
)

& $link @linkArguments
if ($LASTEXITCODE -ne 0) {
    throw "DLL link failed with exit code $LASTEXITCODE."
}

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $dll).Hash
Write-Host "Built: $dll"
Write-Host "SHA256: $hash"

if (-not $KeepIntermediate) {
    Remove-Item -LiteralPath $intermediateRoot -Recurse -Force
}
