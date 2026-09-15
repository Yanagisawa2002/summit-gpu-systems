. "$PSScriptRoot/environment.ps1"
& python -B "$PSScriptRoot/build_artifact.py" player --unity $mdUnity
if($LASTEXITCODE -ne 0){throw 'Observed Player build failed; inspect immutable build receipt and logs'}
