$ErrorActionPreference='Stop'
$actualRoot=Join-Path (Get-Location).Path 'Artifacts/actual-20260910'
python Tools/ExternalSources/prepare_actual_replay.py stage-player
if($LASTEXITCODE -ne 0){throw 'Player staging failed.'}
Copy-Item -LiteralPath "$actualRoot/player-staging-receipt.json" -Destination "$actualRoot/player-r1-staging-receipt.json"
$null=New-Item -ItemType Directory -Path "$actualRoot/player-r1"
try {
    $env:SUMMIT_ACTUAL_PLAYER="$actualRoot/player-r1/SummitExternalReplay.exe"
    ./Tools/ExternalSources/Invoke-ActualProcess.ps1 -FilePath 'C:/Program Files/Unity/Hub/Editor/6000.5.2f1/Editor/Unity.exe' -ProcessArguments @('-batchmode','-nographics','-projectPath',"$actualRoot/unity-host",'-executeMethod','Summit.ActualWorkloads.BuildActualPlayer.Run','-logFile',"$actualRoot/stages/player-build-editor.log") -ReceiptPrefix "$actualRoot/stages/player-build"
} finally {Remove-Item Env:SUMMIT_ACTUAL_PLAYER -ErrorAction SilentlyContinue}
if(Select-String -LiteralPath "$actualRoot/stages/player-build-editor.log" -Pattern 'Shader error|error CS\d+|Build Finished, Result: (?!Success)'){throw 'Build reported a shader/C# error.'}
if(!(Test-Path "$actualRoot/player-r1/SummitExternalReplay.exe")){throw 'Player executable missing.'}
