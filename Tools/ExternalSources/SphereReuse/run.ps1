param(
    [Parameter(Mandatory)][ValidateSet('native','old','new')][string]$Arm,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$Name,
    [ValidateSet('arborx','cabana','sphere-api')][string]$Kind='arborx',
    [ValidatePattern('^r[0-9]+$')][string]$Attempt='r1',
    [switch]$ValidateOnly
)
. "$PSScriptRoot/common.ps1"
Assert-SphereReuseBudget 0.3
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$oldRoot=Join-Path $repoRoot 'Artifacts/actual-20260910'
$manifest=Join-Path $oldRoot 'input-manifest.json'
$oldSource=(Get-Content "$oldRoot/frozen-run.json" -Raw | ConvertFrom-Json).sourceCommit
$source=if($Arm -eq 'new'){(& git rev-parse HEAD).Trim()+'+working-tree-correctness'}else{$oldSource}
if(!$ValidateOnly){
    $frozen=Get-Content "$sphereReuseRoot/frozen-run.json" -Raw | ConvertFrom-Json
    if($Kind -ne 'arborx'){throw 'Formal protocol covers only the unchanged default ArborX sphere task.'}
    $planned=@($frozen.processes | Where-Object {$_.name -eq $Name})
    if($planned.Count -ne 1 -or $planned[0].arm -ne $Arm -or ($Arm -eq 'new' -and $Attempt -ne $frozen.newPlayer.attempt)){throw 'Process/arm/Player does not match the frozen round schedule.'}
    foreach($entry in $frozen.files){
        if((Get-FileHash -LiteralPath (Join-Path $repoRoot $entry.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256){throw "Frozen identity changed: $($entry.path)"}
    }
    foreach($entry in $frozen.toolchain){
        if((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256){throw "Frozen toolchain changed: $($entry.path)"}
    }
    foreach($earlier in $frozen.processes){
        if($earlier.name -eq $Name){break}
        $prior=Get-Content "$sphereReuseRoot/runs/$($earlier.name)/process.process.json" -Raw | ConvertFrom-Json
        if(!$prior.exited -or $prior.exitCode -ne 0){throw 'Previous frozen process did not complete; do not replace a failed sample.'}
    }
    if($Arm -eq 'new'){$source=$frozen.sourceCommit}
}
elseif($Arm -ne 'new'){throw 'Untimed correctness mode uses the new Player.'}
$destination=Join-Path $sphereReuseRoot "runs/$Name"
if(Test-Path -LiteralPath $destination){throw 'Run destination exists; keep every previous attempt.'}
$null=New-Item -ItemType Directory -Path $destination
if($Arm -eq 'native'){
    ./Tools/ExternalSources/Invoke-ActualProcess.ps1 -FilePath (Join-Path $repoRoot $planned[0].executable) -ProcessArguments $planned[0].arguments -ReceiptPrefix "$destination/process"
}else{
    $player=if($Arm -eq 'old'){"$oldRoot/player-r1/SummitExternalReplay.exe"}else{"$sphereReuseRoot/player-$Attempt/SummitExternalReplay.exe"}
    $config=[ordered]@{kind=$Kind;manifest=$manifest;expectedManifestSha256=(Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash.ToLowerInvariant();output="$destination/gpu";sourceCommit=$source;validateOnly=[bool]$ValidateOnly}
    $config | ConvertTo-Json | Set-Content -LiteralPath "$destination/config.json" -Encoding utf8
    $arguments=if($ValidateOnly){@('-batchmode','-force-d3d12','-screen-width','64','-screen-height','64','-actual-config',"$destination/config.json",'-logFile',"$destination/player.log")}else{$planned[0].arguments}
    ./Tools/ExternalSources/Invoke-ActualProcess.ps1 -FilePath $player -ProcessArguments $arguments -ReceiptPrefix "$destination/process"
    $report=Get-Content "$destination/gpu/result.json" -Raw | ConvertFrom-Json
    if($report.status -ne 'completed' -or ($report.rows | Where-Object {!$_.verified})){throw 'Actual complete-GPU-output validation failed.'}
    if($Kind -eq 'sphere-api'){
        $checks=(Get-Content "$destination/gpu/api-checks.json" -Raw | ConvertFrom-Json).checks
        if(!$checks.Count -or $checks[-1].name -ne 'all-fixtures-completed' -or ($checks | Where-Object {!$_.passed})){throw 'API/lifetime fixtures did not complete.'}
        Write-Output "PASS $Name : $($checks.Count) untimed actual-GPU API checks"
    }else{Write-Output "PASS $Name : $($report.rows.Count) full GPU outputs"}
}
