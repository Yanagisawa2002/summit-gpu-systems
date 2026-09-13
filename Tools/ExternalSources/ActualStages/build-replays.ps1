$ErrorActionPreference='Stop'
$actualRoot=(Join-Path (Get-Location).Path 'Artifacts/actual-20260910').Replace('\','/')
$deps="$actualRoot/dependencies"
$cmake="$deps/cmake-3.31.10-windows-x86_64/bin/cmake.exe"
Import-Module 'C:/Program Files/Microsoft Visual Studio/18/Community/Common7/Tools/Microsoft.VisualStudio.DevShell.dll'
Enter-VsDevShell -VsInstallPath 'C:/Program Files/Microsoft Visual Studio/18/Community' -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
& $cmake -S Tools/ExternalSources/ActualNative -B "$actualRoot/native-build" "-DCABANA_CAPTURE_SOURCE=$actualRoot/native-generated/CabanaCapture.cpp"
if($LASTEXITCODE -ne 0){throw 'Replay configure failed.'}
foreach($target in @('CabanaCapture','CabanaReplay','ArborXReplay')){
    & $cmake --build "$actualRoot/native-build" --target $target
    if($LASTEXITCODE -ne 0){throw "Native $target build failed."}
}
