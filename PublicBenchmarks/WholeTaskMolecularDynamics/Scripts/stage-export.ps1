. "$PSScriptRoot/environment.ps1"
& "$mdRun/native-build/UpstreamMolecularOriginal.exe"
if($LASTEXITCODE -ne 0){throw 'Unmodified upstream example failed'}
& "$mdRun/native-build/SummitMolecularNative.exe" export "$mdRun/inputs"
if($LASTEXITCODE -ne 0){throw 'Original/extension input export or full-CSR cross-check failed'}
