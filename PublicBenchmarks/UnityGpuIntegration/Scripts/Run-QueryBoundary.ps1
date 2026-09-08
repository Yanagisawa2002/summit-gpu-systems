[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$BuildRoot,
 [Parameter(Mandatory)][string]$OutputRoot,
 [ValidateSet('validate','boundary','business')][string]$Mode='boundary',
 [ValidateSet('cell','chunks','scan','batch')][string]$Arm='scan',
 [ValidateSet('uniform','hotspot')][string]$Distribution='uniform',
 [uint32]$Seed=928301,[int]$Replicate=0,[switch]$CorruptOracle
)
$ErrorActionPreference='Stop'
$build=[IO.Path]::GetFullPath($BuildRoot);$out=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $out){throw 'Fresh output required.'}
$att=Get-Content -LiteralPath (Join-Path $build 'build-attestation.json') -Raw|ConvertFrom-Json
if($att.status -ne 'built' -or $att.sourceDirty -or $att.development){throw 'Clean Release required.'}
foreach($f in $att.files){if((Get-FileHash -LiteralPath (Join-Path $build $f.path)).Hash.ToLowerInvariant() -ne $f.sha256){throw 'Frozen binary changed.'}}
New-Item -ItemType Directory -Path $out|Out-Null
$run=Join-Path $out 'player';New-Item -ItemType Directory -Path $run|Out-Null
$cfg=[ordered]@{mode=$Mode;arm=$Arm;distribution=$Distribution;output=$run;sourceSha=$att.sourceSha;seed=$Seed;replicate=$Replicate;blocks=3;repeats=16;jobs=128;corruptOracle=[bool]$CorruptOracle}
$config=Join-Path $out 'config.json';$cfg|ConvertTo-Json|Set-Content -LiteralPath $config
$receipt=[ordered]@{status='prepared';config=$cfg;startedUtc=[DateTime]::UtcNow.ToString('o')}
if(-not ('BoundaryWindow' -as [type])){Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class BoundaryWindow {
 [DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);
}
'@}
try {
 & (Join-Path $PSScriptRoot 'Invoke-Serialized.ps1') -Action {
  $player=$null
  try {
   $arguments=@('-force-d3d12','-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-boundary-config',('"'+$config+'"'),'-logFile',('"'+(Join-Path $run 'player.log')+'"'))
   $player=Start-Process -FilePath (Join-Path $build 'Player/Integration.exe') -ArgumentList $arguments -PassThru
   $receipt.pid=$player.Id;$receipt.arguments=$arguments
   if($Mode -eq 'business') {
    $deadline=[DateTime]::UtcNow.AddSeconds(180)
    while(!(Test-Path -LiteralPath (Join-Path $run 'ready.signal'))){if($player.HasExited -or [DateTime]::UtcNow -gt $deadline){throw 'Business preparation failed.'};Start-Sleep -Milliseconds 100}
    $player.Refresh();[void][BoundaryWindow]::SetForegroundWindow($player.MainWindowHandle)
    Start-Sleep -Milliseconds 500
    Set-Content -LiteralPath (Join-Path $run 'start.signal') -Value 'start'
   }
   if(!$player.WaitForExit(240000)){throw 'Owned benchmark timeout.'}
   $r=Get-Content -LiteralPath (Join-Path $run 'result.json') -Raw|ConvertFrom-Json
   if($CorruptOracle){
    if($r.status -ne 'failed' -or $r.completed -ne 1 -or $r.orders[1].verified -or $r.error -notlike '*GPU/oracle mismatch*'){throw 'Negative test did not fail closed.'}
    $receipt.status='expected-rejection'
   } else {
    if($player.ExitCode -ne 0 -or $r.status -ne 'completed' -or !$r.verified){throw 'Benchmark verification failed.'}
    $receipt.status='completed'
   }
   $receipt.playerExitCode=$player.ExitCode
  } finally {if($player -and !$player.HasExited){$player.Kill();$player.WaitForExit()}}
 }
}catch{$receipt.status='failed';$receipt.error=$_.Exception.ToString();throw}
finally{$receipt.endedUtc=[DateTime]::UtcNow.ToString('o');$receipt|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $out 'receipt.json')}
