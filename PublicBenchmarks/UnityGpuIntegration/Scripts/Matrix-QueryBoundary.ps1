param([Parameter(Mandatory)][string]$BuildRoot,[Parameter(Mandatory)][string]$OutputRoot)
$ErrorActionPreference='Stop'
$out=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $out){throw 'Fresh matrix required.'}
New-Item -ItemType Directory -Path $out|Out-Null
$protocolPath=Join-Path $PSScriptRoot '../protocol-query-boundary.json'
Copy-Item -LiteralPath $protocolPath -Destination (Join-Path $out 'protocol.json')
$protocol=Get-Content -LiteralPath $protocolPath -Raw|ConvertFrom-Json
$att=Get-Content -LiteralPath (Join-Path $BuildRoot 'build-attestation.json') -Raw|ConvertFrom-Json
$worker=Join-Path $PSScriptRoot 'Run-QueryBoundary.ps1'
$hash=(Get-FileHash -LiteralPath $worker).Hash
$rows=@()
for($rep=0;$rep -lt 5;$rep++) {
 $rows+=[ordered]@{id="$rep-boundary";mode='boundary';arm='scan';distribution='uniform';replicate=$rep;seed=$protocol.seeds[$rep];status='pending'}
 for($d=0;$d -lt 2;$d++) {
  $distribution=$protocol.businessDistributions[($d+$rep)%2]
  for($a=0;$a -lt 4;$a++) {
   $pos=if($rep%2 -eq 0){$a}else{3-$a};$arm=$protocol.arms[($pos+$rep)%4]
   $rows+=[ordered]@{id="$rep-$distribution-$arm";mode='business';arm=$arm;distribution=$distribution;replicate=$rep;seed=$protocol.seeds[$rep];status='pending'}
  }
 }
}
$state=[ordered]@{status='running';sourceSha=$att.sourceSha;protocolSha256=(Get-FileHash $protocolPath).Hash;workerSha256=$hash;startedUtc=[DateTime]::UtcNow.ToString('o');runs=$rows}
$state|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $out 'matrix.json')
foreach($row in $rows) {
 try {
  if((Get-FileHash -LiteralPath $worker).Hash -ne $hash){throw 'Frozen worker changed.'}
  & $worker -BuildRoot $BuildRoot -OutputRoot (Join-Path $out $row.id) -Mode $row.mode -Arm $row.arm -Distribution $row.distribution -Seed ([uint32]$row.seed) -Replicate $row.replicate
  $receipt=Get-Content -LiteralPath (Join-Path $out "$($row.id)/receipt.json") -Raw|ConvertFrom-Json
  if($receipt.status -ne 'completed'){throw 'Incomplete run.'}
  $row.status='completed';Write-Output "$($row.id) verified"
 }catch{$row.status='failed';$row.error=$_.Exception.ToString();$state.status='failed';throw}
 finally{$state|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $out 'matrix.json')}
}
$state.status='completed';$state.endedUtc=[DateTime]::UtcNow.ToString('o')
$state|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $out 'matrix.json')
