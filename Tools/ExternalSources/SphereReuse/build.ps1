param([ValidatePattern('^r[0-9]+$')][string]$Attempt='r1')
. "$PSScriptRoot/common.ps1"
Assert-SphereReuseBudget 1
python "$PSScriptRoot/prepare.py" stage-sources
if($LASTEXITCODE -ne 0){throw 'New Player source staging failed.'}
$playerDirectory=Join-Path $sphereReuseRoot "player-$Attempt"
if(Test-Path -LiteralPath $playerDirectory){throw 'Player attempt exists; preserve it.'}
$null=New-Item -ItemType Directory -Path $playerDirectory
$log=Join-Path $sphereReuseRoot "stages/build-$Attempt-editor.log"
try {
    $env:SUMMIT_ACTUAL_PLAYER=Join-Path $playerDirectory 'SummitExternalReplay.exe'
    ./Tools/ExternalSources/Invoke-ActualProcess.ps1 -FilePath 'C:/Program Files/Unity/Hub/Editor/6000.5.2f1/Editor/Unity.exe' -ProcessArguments @('-batchmode','-nographics','-projectPath',"$sphereReuseRoot/unity-host",'-executeMethod','Summit.ActualWorkloads.BuildActualPlayer.Run','-logFile',$log) -ReceiptPrefix "$sphereReuseRoot/stages/build-$Attempt"
} finally {Remove-Item Env:SUMMIT_ACTUAL_PLAYER -ErrorAction SilentlyContinue}
if(Select-String -LiteralPath $log -Pattern 'Shader error|error CS\d+|Build Finished, Result: (?!Success)'){throw 'Shader/C# build failure; preserve this attempt.'}
if(!(Test-Path "$playerDirectory/SummitExternalReplay.exe")){throw 'New Player missing.'}
