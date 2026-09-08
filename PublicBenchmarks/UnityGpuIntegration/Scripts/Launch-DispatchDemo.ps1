param(
 [string]$BuildRoot=(Join-Path $PSScriptRoot '..'),
 [ValidateSet('cell','chunks','scan','batch')][string]$Arm='scan',
 [ValidateSet('uniform','hotspot')][string]$Distribution='hotspot'
)
$ErrorActionPreference='Stop'
$build=[IO.Path]::GetFullPath($BuildRoot)
$out=Join-Path $build ('Runs/'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff')+'-'+$Distribution+'-'+$Arm)
& (Join-Path $PSScriptRoot 'Run-QueryBoundary.ps1') -BuildRoot $build -OutputRoot $out -Mode business -Arm $Arm -Distribution $Distribution
Write-Output "Verified run and per-order results: $out"
