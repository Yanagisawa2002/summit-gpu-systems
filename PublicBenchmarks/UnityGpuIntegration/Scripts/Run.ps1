[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildRoot,
    [Parameter(Mandatory)][string]$ConfigPath,
    [string]$SerializedRunner=(Join-Path $PSScriptRoot 'Invoke-Serialized.ps1'),
    [int]$TimeoutSeconds=1800
)
$ErrorActionPreference='Stop'
$build=[IO.Path]::GetFullPath($BuildRoot)
$config=Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
if($config.arms -isnot [array] -or $config.arms.Count -lt 1){throw 'Arms must be a nonempty JSON array.'}
$output=[IO.Path]::GetFullPath($config.output)
if(Test-Path -LiteralPath $output){throw 'Fresh run output required; retain every previous attempt.'}
$attestation=Get-Content -LiteralPath (Join-Path $build 'build-attestation.json') -Raw | ConvertFrom-Json
if($attestation.status -ne 'built' -or $attestation.development){throw 'A completed Release build is required.'}
if($config.mode -eq 'formal' -and $attestation.sourceDirty){throw 'Formal source must be a clean committed checkout.'}
if($config.sourceSha -ne $attestation.sourceSha){throw 'Configuration source does not match build attestation.'}
foreach($entry in $attestation.files){
    $file=Join-Path $build $entry.path
    if((Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant() -ne $entry.sha256){throw "Binary inventory mismatch: $($entry.path)"}
}
New-Item -ItemType Directory -Path $output | Out-Null
$savedConfig=Join-Path $output 'config.json'
$config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $savedConfig -Encoding utf8
$receipt=[ordered]@{status='waiting';sourceSha=$config.sourceSha;configSha256=(Get-FileHash $savedConfig).Hash.ToLowerInvariant();
    buildAttestationSha256=(Get-FileHash (Join-Path $build 'build-attestation.json')).Hash.ToLowerInvariant();
    os=[Environment]::OSVersion.ToString();logicalProcessors=[Environment]::ProcessorCount;
    video=@(Get-CimInstance Win32_VideoController | Select-Object Name,DriverVersion,PNPDeviceID);
    cpu=@(Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)}
try {
    & $SerializedRunner -Action {
        $receipt.startedUtc=[DateTime]::UtcNow.ToString('o')
        $arguments=@('-force-d3d12','-screen-fullscreen','0','-screen-width','1280','-screen-height','720',
            '-integration-config',('"'+$savedConfig+'"'),'-logFile',('"'+(Join-Path $output 'player.log')+'"'))
        $owned=Start-Process -FilePath (Join-Path $build 'Player/Integration.exe') -ArgumentList $arguments -PassThru
        $receipt.processId=$owned.Id;$receipt.arguments=$arguments;$receipt.status='running'
        $receipt | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $output 'process.json') -Encoding utf8
        if(!$owned.WaitForExit($TimeoutSeconds*1000)){$owned.Kill();$owned.WaitForExit();throw 'Owned Player timed out; failed attempt retained.'}
        $receipt.exitCode=$owned.ExitCode
        if($owned.ExitCode -ne 0){throw "Player exited $($owned.ExitCode)"}
        $result=Get-Content -LiteralPath (Join-Path $output 'result.json') -Raw | ConvertFrom-Json
        if($result.status -ne 'completed'){throw "Player result status: $($result.status)"}
        if($result.runs.Count -ne $config.blocks*$config.arms.Count -or @($result.runs | Where-Object {!$_.verified}).Count){throw 'Incomplete verified arm matrix'}
        $buildReceipt=Get-Content -LiteralPath (Join-Path $build 'release-build.json') -Raw | ConvertFrom-Json
        if($result.buildGuid -ne $buildReceipt.buildGuid){throw 'Player build identity mismatch'}
        $receipt.status='completed'
    }
} catch {$receipt.status='failed';$receipt.error=$_.Exception.ToString();throw}
finally {$receipt.endedUtc=[DateTime]::UtcNow.ToString('o');$receipt | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $output 'process.json') -Encoding utf8}
