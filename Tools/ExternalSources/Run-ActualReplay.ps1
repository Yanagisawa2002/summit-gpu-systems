[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('cabana','arborx')][string]$Kind,
    [Parameter(Mandatory)][ValidateSet('native','gpu')][string]$Backend,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$Name,
    [switch]$ValidateOnly
)
# This process entry belongs inside Invoke-ActualStage, not a concurrent launcher.
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$actualRoot=Join-Path $repoRoot 'Artifacts/actual-20260910'
$manifest=Join-Path $actualRoot 'input-manifest.json'
$sourceCommit=(& git -C $repoRoot rev-parse HEAD).Trim()+'+working-tree-correctness'
if(!$ValidateOnly){
    $frozen=Get-Content "$actualRoot/frozen-run.json" -Raw | ConvertFrom-Json
    $sourceCommit=$frozen.sourceCommit
    foreach($entry in $frozen.files){
        if((Get-FileHash -LiteralPath (Join-Path $repoRoot $entry.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256){throw "Frozen source/build/input changed: $($entry.path)"}
    }
}
$inputManifest=Get-Content $manifest -Raw | ConvertFrom-Json
foreach($entry in $inputManifest.files){
    if((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256){throw "Native input changed: $($entry.name)"}
}
$destination=Join-Path $actualRoot "runs/$Name"
if(Test-Path -LiteralPath $destination){throw 'Run already exists; failed and successful runs are never overwritten.'}
$null=New-Item -ItemType Directory -Path $destination
if($Backend -eq 'gpu'){
    $config=[ordered]@{kind=$Kind;manifest=$manifest;expectedManifestSha256=(Get-FileHash $manifest -Algorithm SHA256).Hash.ToLowerInvariant();output="$destination/gpu";sourceCommit=$sourceCommit;validateOnly=[bool]$ValidateOnly}
    $configPath="$destination/config.json"
    $config | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding utf8
    & "$PSScriptRoot/Invoke-ActualProcess.ps1" -FilePath "$actualRoot/player-r1/SummitExternalReplay.exe" -ProcessArguments @('-batchmode','-force-d3d12','-screen-width','64','-screen-height','64','-actual-config',$configPath,'-logFile',"$destination/player.log") -ReceiptPrefix "$destination/process"
    $result=Get-Content "$destination/gpu/result.json" -Raw | ConvertFrom-Json
    if($result.status -ne 'completed' -or ($result.rows | Where-Object {!$_.verified})){throw 'Actual GPU full-CSR validation did not complete.'}
    Write-Output "PASS $Name GPU: $($result.rows.Count) complete outputs; $($result.deviceName)"
}else{
    if($ValidateOnly){throw 'Native replay retains diagnostic clocks; use the correctness-labelled native stage separately.'}
    if($Kind -eq 'cabana'){$exe="$actualRoot/native-build/CabanaReplay.exe";$processArgs=@("$actualRoot/inputs","$destination/native.csv")}
    else{$exe="$actualRoot/native-build/ArborXReplay.exe";$processArgs=@('replay',"$actualRoot/inputs/arborx-default.bin","$destination/native.csv")}
    & "$PSScriptRoot/Invoke-ActualProcess.ps1" -FilePath $exe -ProcessArguments $processArgs -ReceiptPrefix "$destination/process"
    Write-Output "PASS $Name native complete output replay"
}
