[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$ProjectPath,
 [Parameter(Mandatory)][string]$OutputDirectory,
 [Parameter(Mandatory)][string]$OracleRoot,
 [Parameter(Mandatory)][string]$LockScript,
 [string]$CompletedHotspotOff='',
 [string]$StatusPath='',
 [string]$UnityPath='C:/Program Files/Unity/Hub/Editor/6000.5.2f1/Editor/Unity.exe'
)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$ProjectPath=[IO.Path]::GetFullPath($ProjectPath);$out=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $out){throw 'Fresh output required; no overwrite/retry'}
New-Item -ItemType Directory -Path $out -Force | Out-Null
$sha=(& git -C $root rev-parse HEAD).Trim()
if(@(& git -C $root status --porcelain).Count){throw 'Commit diagnostic sources before execution'}
$generation=Get-Content (Join-Path $ProjectPath 'generation.json') -Raw | ConvertFrom-Json -Depth 20
foreach($file in $generation.generated){if((Get-FileHash (Join-Path $ProjectPath $file.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256){throw "Generated input changed: $($file.path)"}}
foreach($file in $generation.sourceInputs){if((Get-FileHash (Join-Path $root $file.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256){throw "Original input changed: $($file.path)"}}
Copy-Item (Join-Path $ProjectPath 'generation.json') (Join-Path $out 'generation.json')
$sourceFiles=@(& git -C $root ls-files Packages Tools/IndexCostDiagnostics PublicBenchmarks/UnityGpuIntegration | ForEach-Object { [pscustomobject]@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $root $_) -Algorithm SHA256).Hash} })
$sourceFiles | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'source-sha256.json')
$receipt=[ordered]@{status='running';diagnosticOnly=$true;sourceSha=$sha;project=$ProjectPath;startUtc=[DateTime]::UtcNow.ToString('o');processes=@();gpu=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion);commands=@();failure=$null}
function Save { $receipt | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $out 'receipt.json') }
function State([string]$phase,$process) {
 if($StatusPath){$s=Get-Content $StatusPath -Raw | ConvertFrom-Json -Depth 20;$s.phase=$phase;$s.currentProcess=$process;$s.updatedUtc=[DateTime]::UtcNow.ToString('o');$s.evidence=@($s.evidence)+@($out)|Sort-Object -Unique;$s|ConvertTo-Json -Depth 20|Set-Content $StatusPath}
}
function Owned([string]$exe,[string[]]$arguments,[string]$label){
 $receipt.commands+=@{exe=$exe;arguments=$arguments};Save
 $started=[DateTime]::UtcNow;$p=Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
 State "Diagnostic $label under shared lock" @{pid=$p.Id;label=$label;startedUtc=$started.ToString('o')}
 if(!$p.WaitForExit(1800000)){Stop-Process -Id $p.Id -Force;throw 'Owned diagnostic process timeout; no retry'}
 $receipt.processes+=@{label=$label;pid=$p.Id;startUtc=$started.ToString('o');endUtc=[DateTime]::UtcNow.ToString('o');exitCode=$p.ExitCode};Save;State "Diagnostic $label exited" $null
 if($p.ExitCode -ne 0){throw "$label failed: $($p.ExitCode). No automatic retry."}
}
try {
 & $LockScript -Action {
  $player=Join-Path $out 'Player/IndexCosts.exe';New-Item -ItemType Directory -Path (Split-Path $player -Parent) -Force|Out-Null
  Owned $UnityPath @('-batchmode','-quit','-force-d3d12','-projectPath',"`"$ProjectPath`"",'-executeMethod','Summit.IndexCostDiagnostics.IndexCostBuild.BuildRelease','-index-cost-player',"`"$player`"",'-logFile',"`"$(Join-Path $out 'build.log')`"") 'build'
  if(!(Test-Path $player)){throw 'No built Player'}
  $binary=@(Get-ChildItem (Split-Path $player -Parent) -File -Recurse|ForEach-Object{[pscustomobject]@{path=[IO.Path]::GetRelativePath((Split-Path $player -Parent),$_.FullName);sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash;bytes=$_.Length}})
  $binary|ConvertTo-Json -Depth 5|Set-Content (Join-Path $out 'player-sha256.json')
  $jobs=@(@{scene='hotspot-dynamic';phases=$false},@{scene='streaming-switch';phases=$true},@{scene='hotspot-dynamic';phases=$true},@{scene='streaming-switch';phases=$false})
  foreach($job in $jobs){
   $name=$job.scene+'-'+$(if($job.phases){'phases-on'}else{'phases-off'});$folder=Join-Path $out $name;New-Item -ItemType Directory -Path $folder -Force|Out-Null
   if($CompletedHotspotOff -and $name -eq 'hotspot-dynamic-phases-off'){
    $old=Get-Content (Join-Path $CompletedHotspotOff 'result.json') -Raw | ConvertFrom-Json -Depth 30
    if($old.status -ne 'complete' -or $old.verifiedFrames -ne 384 -or $old.verifiedExactMembershipFrames -ne 384 -or $old.config.phases -or $old.config.scenario -ne 'hotspot-dynamic' -or $old.config.seed -ne 927101){throw 'Prior completed diagnostic does not match fixed case'}
    Get-ChildItem -LiteralPath $CompletedHotspotOff -File | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $folder}
    $receipt.reusedCompleted=@{path=$CompletedHotspotOff;sourceSha=$old.config.sourceSha;pid=$old.pid;resultSha256=(Get-FileHash (Join-Path $folder 'result.json') -Algorithm SHA256).Hash;reason='Retain completed phase-off case; subsequent generator resource-name fix affects phase-on adapter only. No performance resampling.'};Save
    continue
   }
   $oracle=Join-Path $OracleRoot ('0-'+$job.scene+'/expected.bin')
   $config=[ordered]@{scenario=$job.scene;phases=$job.phases;seed=927101;frames=384;warmup=64;output=$folder;oracle=$oracle;sourceSha=$sha;oracleSha256=(Get-FileHash $oracle -Algorithm SHA256).Hash}
   $cfg=Join-Path $out ($name+'.config.json');$config|ConvertTo-Json|Set-Content $cfg
   Owned $player @('-force-d3d12','-index-cost-config',"`"$cfg`"",'-logFile',"`"$(Join-Path $folder 'player.log')`"") $name
   $result=Get-Content (Join-Path $folder 'result.json') -Raw|ConvertFrom-Json -Depth 30
   if($result.status -ne 'complete' -or $result.verifiedFrames -ne 384 -or $result.verifiedExactMembershipFrames -ne 384){throw 'Incomplete exact per-frame oracle/membership validation'}
   Write-Output "$name complete: every frame/CSR/query matched."
  }
  foreach($file in $sourceFiles){if((Get-FileHash -LiteralPath (Join-Path $root $file.path) -Algorithm SHA256).Hash -ne $file.sha256){throw 'Source changed during diagnostic'}}
  foreach($file in $binary){if((Get-FileHash -LiteralPath (Join-Path (Split-Path $player -Parent) $file.path) -Algorithm SHA256).Hash -ne $file.sha256){throw 'Binary changed during diagnostic'}}
 }
 $receipt.status='complete'
}catch{$receipt.status='failed';$receipt.failure=$_.ToString();throw}
finally{$receipt.endUtc=[DateTime]::UtcNow.ToString('o');Save;State "Diagnostic $($receipt.status): $out" $null}
