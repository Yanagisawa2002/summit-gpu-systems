. "$PSScriptRoot/environment.ps1"
& python -B "$PSScriptRoot/build_artifact.py" native --cmake "$mdRun/dependencies/cmake/bin/cmake.exe" --cxx "$mdVc/bin/Hostx64/x64/cl.exe" --backend serial --jobs 4
if($LASTEXITCODE -ne 0){throw 'Observed native build failed; inspect immutable build receipt and logs'}
