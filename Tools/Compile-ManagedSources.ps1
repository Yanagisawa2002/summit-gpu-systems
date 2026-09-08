[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$UnityEditorPath,
    [string]$EntitiesAssemblyDirectory = '',
    [string]$ReferenceDirectory = '',
    [string]$HlslProfilePackageDirectory = '',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$compileRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$unityManaged = Join-Path (Split-Path $UnityEditorPath -Parent) 'Data/Managed/UnityEngine'
if (-not (Test-Path -LiteralPath (Join-Path $unityManaged 'UnityEngine.CoreModule.dll'))) { throw 'Unity reference assemblies missing.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $compileRoot 'Artifacts/managed-compile' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$files = @(Get-ChildItem -LiteralPath (Join-Path $compileRoot 'Packages') -Filter '*.cs' -Recurse -File | Where-Object { $_.FullName -match '[\\/]Runtime[\\/]' })
$files += Get-ChildItem -LiteralPath (Join-Path $compileRoot 'PublicBenchmarks/UnityGpuIntegration/Assets/Runtime') -Filter '*.cs' -File
$files += Get-ChildItem -LiteralPath (Join-Path $compileRoot 'PublicBenchmarks/External/Adapters') -Filter '*.cs' -File
$references = @(Get-ChildItem -LiteralPath $unityManaged -Filter '*.dll' -File)
if ($EntitiesAssemblyDirectory) {
    if (!$ReferenceDirectory) { throw 'Pinned upstream source directory is required for Boids API compilation.' }
    foreach($assemblyName in @('Unity.Entities','Unity.Entities.Hybrid','Unity.Transforms','Unity.Mathematics','Unity.Collections','Unity.Burst')) {
        $assemblyPath = Join-Path $EntitiesAssemblyDirectory ($assemblyName + '.dll')
        if (!(Test-Path -LiteralPath $assemblyPath)) { throw "Missing dependency $assemblyPath" }
        $references += Get-Item -LiteralPath $assemblyPath
    }
    $files += Get-ChildItem -LiteralPath (Join-Path $compileRoot 'PublicBenchmarks/External/Boids') -Filter '*.cs' -File
    foreach($sourceName in @('BoidAuthoring.cs','BoidTargetAuthoring.cs')) {
        $files += Get-Item -LiteralPath (Join-Path $ReferenceDirectory "EntitiesSamples/Assets/Boids/Scripts/$sourceName")
    }
}
if ($HlslProfilePackageDirectory) {
    $files += Get-ChildItem -LiteralPath (Join-Path $HlslProfilePackageDirectory 'Runtime') -Filter '*.cs' -File
    $files += Get-Item -LiteralPath (Join-Path $compileRoot 'Integrations/HlslKernelPipeline/HlslProfileScanMapping.cs')
}
$xml = [Text.StringBuilder]::new()
[void]$xml.AppendLine('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.1</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><GenerateAssemblyInfo>false</GenerateAssemblyInfo><AllowUnsafeBlocks>true</AllowUnsafeBlocks><LangVersion>latest</LangVersion></PropertyGroup><ItemGroup>')
foreach($file in $files) { [void]$xml.AppendLine('<Compile Include="' + [Security.SecurityElement]::Escape($file.FullName) + '" />') }
foreach($reference in $references) { [void]$xml.AppendLine('<Reference Include="' + [Security.SecurityElement]::Escape($reference.BaseName) + '"><HintPath>' + [Security.SecurityElement]::Escape($reference.FullName) + '</HintPath></Reference>') }
[void]$xml.AppendLine('</ItemGroup></Project>')
$project = Join-Path $OutputDirectory 'Summit.SourceCompile.csproj'
[IO.File]::WriteAllText($project, $xml.ToString())
# Compile C# against real Unity assemblies. No Unity process, Editor callbacks,
# test discovery, native DLL loading, Player build, or shader dispatch occurs.
& dotnet build $project --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Managed source compilation failed.' }
Write-Host "Compiled $($files.Count) source files; did not execute Unity or compiled code."
