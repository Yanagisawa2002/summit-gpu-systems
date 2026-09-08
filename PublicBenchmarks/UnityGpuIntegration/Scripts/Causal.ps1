[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$OracleRoot,
    [Parameter(Mandatory)][string]$SerializedRunner,
    [string]$PresentMon
)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $OutputRoot){throw 'Fresh diagnostic root required'}
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$build=Get-Content (Join-Path $BuildRoot 'build-attestation.json') -Raw | ConvertFrom-Json
$scenes=@('sparse-low-change','hotspot-dynamic','streaming-switch')
$orders=@(@('none','whole','three'),@('whole','three','none'),@('three','none','whole'))
$cells=@()
for($s=0;$s -lt 3;$s++){
    foreach($probe in $orders[$s]){
        $id=$scenes[$s]+'-'+$probe
        $oracle=Join-Path $OracleRoot ('oracle-dev-'+$scenes[$s]+'-v3/expected.bin')
        if((Get-Item -LiteralPath $oracle).Length -ne 61440){throw 'Invalid oracle length'}
        $config=[ordered]@{mode='validate';scenario=$scenes[$s];nativeProbeMode=$probe;output=(Join-Path $OutputRoot $id);
            oracle=$oracle;sourceSha=$build.sourceSha;seed=920071;frames=384;warmup=64;blocks=1;processReplicate=0;
            arms=@('old-full','new-full','old-incremental','new-incremental');screenshot=$false;engineTimingAudit=$false}
        $configPath=Join-Path $OutputRoot ($id+'.json')
        $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $configPath -Encoding utf8
        $cells+= [ordered]@{id=$id;config=$configPath;configSha256=(Get-FileHash $configPath).Hash.ToLowerInvariant();
            oracleSha256=(Get-FileHash $oracle).Hash.ToLowerInvariant();status='pending'}
    }
}
$freeze=[ordered]@{kind='diagnostic-only';createdUtc=[DateTime]::UtcNow.ToString('o');sourceSha=$build.sourceSha;
    buildAttestationSha256=(Get-FileHash (Join-Path $BuildRoot 'build-attestation.json')).Hash.ToLowerInvariant();
    nativeLifecycle='Unchanged three-event scope with private fence completion flush/sync';
    selection='Nine fixed cells, one process each; Latin probe order; unchanged four-arm Williams order; no statistical retakes';
    captureRule='Owned Player PID only, ordinary current token. If first capture fails, retain failure and disable capture for remaining cells; no elevation request.';
    limits='Exploratory within-workload instrumentation controls, not formal performance acceptance or pure algorithm attribution';cells=$cells}
$freeze | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $OutputRoot 'freeze.json') -Encoding utf8
foreach($cell in $cells){
    try{
        $cell.status='running';$cells | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $OutputRoot 'matrix.json') -Encoding utf8
        & (Join-Path $PSScriptRoot 'Run.ps1') -BuildRoot $BuildRoot -ConfigPath $cell.config -SerializedRunner $SerializedRunner -PresentMon $PresentMon
        $cell.status='completed'
        if($PresentMon){
            $receipt=Get-Content (Join-Path (Join-Path $OutputRoot $cell.id) 'process.json') -Raw | ConvertFrom-Json
            if($receipt.presentation.status -ne 'exited'){$PresentMon=''}
        }
    }catch{$cell.status='failed';$cell.error=$_.Exception.ToString();throw}
    finally{$cells | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $OutputRoot 'matrix.json') -Encoding utf8}
}
