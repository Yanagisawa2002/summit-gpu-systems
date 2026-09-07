[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Unity,
    [Parameter(Mandatory)][string]$OutputRoot,
    [string]$SerializedRunner=(Join-Path $PSScriptRoot 'Invoke-Serialized.ps1'),
    [switch]$AllowDirtySource
)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repo=[IO.Path]::GetFullPath((Join-Path $project '../..'))
$output=[IO.Path]::GetFullPath($OutputRoot)
if(Test-Path -LiteralPath $output){throw 'Fresh build directory required; preserve previous binaries.'}
$dirty=[bool](& git -C $repo status --porcelain)
if($dirty -and !$AllowDirtySource){throw 'Freeze a clean source commit before building formal inputs.'}
New-Item -ItemType Directory -Path $output | Out-Null
& (Join-Path $PSScriptRoot 'Bootstrap.ps1')
$env:INTEGRATION_SOURCE_SHA=(& git -C $repo rev-parse HEAD)
$player=Join-Path $output 'Player/Integration.exe'
$arguments=@('-batchmode','-nographics','-quit','-projectPath',('"'+$project+'"'),'-executeMethod','Summit.PublicIntegration.IntegrationBuild.BuildRelease',
    '-integration-player',('"'+$player+'"'),'-logFile',('"'+(Join-Path $output 'build.log')+'"'))
$receipt=[ordered]@{sourceSha=$env:INTEGRATION_SOURCE_SHA;sourceDirty=$dirty;unity=$Unity;unitySha256=(Get-FileHash $Unity).Hash.ToLowerInvariant();arguments=$arguments;startedUtc=[DateTime]::UtcNow.ToString('o');status='building';development=$false}
try {
    & $SerializedRunner -Action {
        $owned=Start-Process -FilePath $Unity -ArgumentList $arguments -WindowStyle Hidden -PassThru
        $receipt.processId=$owned.Id
        if(!$owned.WaitForExit(1800000)) { $owned.Kill();$owned.WaitForExit();throw 'Owned Unity build timed out' }
        if($owned.ExitCode -ne 0){throw "Unity build exit $($owned.ExitCode)"}
    }
    if(!(Test-Path -LiteralPath $player)){throw 'Release Player missing'}
    Copy-Item (Join-Path $project 'Artifacts/release-build.json'),(Join-Path $project 'Artifacts/dependency-inventory.json') -Destination $output
    $receipt.status='built'
    $receipt.files=@(Get-ChildItem (Join-Path $output 'Player') -File -Recurse | ForEach-Object {
        @{path=$_.FullName.Substring($output.Length+1);bytes=$_.Length;sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}
    })
} catch {$receipt.status='failed';$receipt.error=$_.Exception.ToString();throw}
finally {$receipt.endedUtc=[DateTime]::UtcNow.ToString('o');$receipt | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $output 'build-attestation.json') -Encoding utf8}
