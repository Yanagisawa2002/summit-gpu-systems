. "$PSScriptRoot/environment.ps1"
foreach($arm in @('arborx','grid','legacy','reuse')){
    & python "$PSScriptRoot/run_process.py" $arm discovery-8 "discovery-$arm-r1" --warmups 2 --measured 3
    if($LASTEXITCODE -ne 0){throw "Discovery failed: $arm"}
}
foreach($arm in @('legacy','reuse')){
    & python "$PSScriptRoot/run_process.py" $arm discovery-8 "diagnostic-$arm-r1" --warmups 1 --measured 2 --diagnostic
    if($LASTEXITCODE -ne 0){throw "Diagnostic failed: $arm"}
}
