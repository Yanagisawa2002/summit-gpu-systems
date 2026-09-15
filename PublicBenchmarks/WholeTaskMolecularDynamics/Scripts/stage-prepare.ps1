. "$PSScriptRoot/environment.ps1"
& python "$PSScriptRoot/prepare_hardware.py"
if($LASTEXITCODE -ne 0){throw 'Dependency/source staging failed.'}
