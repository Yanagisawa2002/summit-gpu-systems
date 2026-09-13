. "$PSScriptRoot/common.ps1"
Assert-SphereReuseBudget 0.5
python "$PSScriptRoot/prepare.py" preserve
if($LASTEXITCODE -ne 0){throw 'Baseline preservation/cache reuse failed.'}
