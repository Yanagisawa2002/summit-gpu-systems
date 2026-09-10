$ErrorActionPreference='Stop'
$sphereReuseRoot=Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))) 'Artifacts/optimization-20260910-sphere-reuse'
function Assert-SphereReuseBudget([double]$EstimatedAdditionalGiB) {
    $bytes=(Get-ChildItem -LiteralPath $sphereReuseRoot -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
    if(($bytes/1GB)+$EstimatedAdditionalGiB -gt 6){throw 'This stage would exceed the 6 GiB optimization budget.'}
    if(((Get-PSDrive C).Free/1GB)-$EstimatedAdditionalGiB -lt 20){throw 'This stage would cross the 20 GiB free-space reserve.'}
}
