[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repo=[IO.Path]::GetFullPath((Join-Path $project '../..'))
# Deliberate allowlist: the root Unity project and all Integrations are excluded.
$records=@()
$packages=@('com.summit.gpu-primitives','com.summit.gpu-direct-binning','com.summit.gpu-sensor-pipeline','com.summit.gpu-timestamps')
foreach ($package in $packages) {
    foreach ($file in Get-ChildItem (Join-Path $repo ('Packages/'+$package)) -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/]Native~[\\/]Build[\\/]' }) {
        $records+=@{path=$file.FullName.Substring($repo.Length+1).Replace('\','/');sha256=(Get-FileHash $file.FullName).Hash.ToLowerInvariant()}
    }
}
$artifacts=Join-Path $project 'Artifacts'
New-Item -ItemType Directory -Force $artifacts | Out-Null
@{schemaVersion=1;repository='https://github.com/Yanagisawa2002/summit-gpu-systems';sourceCommit=(& git -C $repo rev-parse HEAD);
    unity='6000.5.2f1';files=$records;license='Limited benchmark reproduction permission; complete root and package license texts retained';
    excluded=@('Integrations','root project Assets');absoluteAuthorDependencies=@()} |
    ConvertTo-Json -Depth 8 | Set-Content (Join-Path $artifacts 'dependency-inventory.json') -Encoding utf8
Write-Output "Standalone project ready: $project"
