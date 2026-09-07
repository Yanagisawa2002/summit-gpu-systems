[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildRoot,
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][ValidateSet('oracle','formal')][string]$Mode,
    [string]$OracleRoot,
    [string]$ProtocolPath=(Join-Path $PSScriptRoot '../protocol.json'),
    [string]$SerializedRunner=(Join-Path $PSScriptRoot 'Invoke-Serialized.ps1')
)
$ErrorActionPreference='Stop'
$protocolPath=[IO.Path]::GetFullPath($ProtocolPath)
$protocol=Get-Content $protocolPath -Raw | ConvertFrom-Json
$build=[IO.Path]::GetFullPath($BuildRoot)
$source=(Get-Content (Join-Path $build 'build-attestation.json') -Raw | ConvertFrom-Json).sourceSha
$output=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $output){throw 'Fresh matrix directory required.'}
New-Item -ItemType Directory $output | Out-Null
Copy-Item $protocolPath (Join-Path $output 'protocol.json')
$entries=[Collections.Generic.List[object]]::new()
for($rep=0;$rep -lt $protocol.processSeeds.Count;$rep++){
    foreach($sceneIndex in $protocol.scenarioOrderByReplicate[$rep]){
        $scene=$protocol.scenarios[$sceneIndex];$id="$rep-$scene"
        $runOutput=Join-Path $output $id
        $oracle=if($Mode -eq 'oracle'){Join-Path $runOutput 'expected.bin'}else{Join-Path ([IO.Path]::GetFullPath($OracleRoot)) "$id/expected.bin"}
        if($Mode -eq 'formal' -and !(Test-Path -LiteralPath $oracle)){throw "Missing independent oracle: $id"}
        $cfg=[ordered]@{mode=$Mode;scenario=$scene;output=$runOutput;oracle=$oracle;sourceSha=$source;
            seed=$protocol.processSeeds[$rep];frames=$protocol.frames;warmup=$protocol.warmup;
            blocks=$(if($Mode -eq 'oracle'){1}else{$protocol.blocks});processReplicate=$rep;
            arms=@(if($Mode -eq 'oracle'){'old-full'}else{$protocol.arms});screenshot=$false}
        $configPath=Join-Path $output "$id.config.json";$cfg | ConvertTo-Json -Depth 12 | Set-Content $configPath -Encoding utf8
        $entry=[ordered]@{id=$id;status='running';config=$configPath;output=$runOutput}
        $entries.Add($entry)
        try {& (Join-Path $PSScriptRoot 'Run.ps1') -BuildRoot $build -ConfigPath $configPath -SerializedRunner $SerializedRunner -TimeoutSeconds $protocol.timeoutSeconds;$entry.status='completed'}
        catch {
            $entry.status='failed';$entry.error=$_.Exception.ToString()
            $resultPath=Join-Path $runOutput 'result.json'
            $reason=if(Test-Path $resultPath){(Get-Content $resultPath -Raw | ConvertFrom-Json).error}else{$entry.error}
            if($reason -match 'mismatch|identity|inventory|source|Oracle|Cached live count'){
                $entries | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $output 'matrix.json') -Encoding utf8
                throw 'Correctness or identity failure: remaining matrix stopped, evidence retained.'
            }
        }
        $entries | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $output 'matrix.json') -Encoding utf8
    }
}
