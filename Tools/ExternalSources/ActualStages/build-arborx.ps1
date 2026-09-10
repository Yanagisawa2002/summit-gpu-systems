$ErrorActionPreference='Stop'
$actualRoot=Join-Path (Get-Location).Path 'Artifacts/actual-20260910'
$deps=Join-Path $actualRoot 'dependencies'
$cmake=Join-Path $deps 'cmake-3.31.10-windows-x86_64/bin/cmake.exe'
Import-Module 'C:/Program Files/Microsoft Visual Studio/18/Community/Common7/Tools/Microsoft.VisualStudio.DevShell.dll'
Enter-VsDevShell -VsInstallPath 'C:/Program Files/Microsoft Visual Studio/18/Community' -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
& $cmake -S Tools/ExternalSources/ActualNative -B (Join-Path $actualRoot 'native-build') -G 'NMake Makefiles' '-DCMAKE_BUILD_TYPE=Release' "-DCMAKE_PREFIX_PATH=$actualRoot/k-install" "-DCABANA_SOURCE=$deps/cabana" "-DARBORX_SOURCE=$deps/arborx" "-DBENCHMARK_SOURCE=$deps/benchmark" "-DBOOST_SOURCE=$deps/boost_1_87_0"
if($LASTEXITCODE -ne 0){throw 'Native driver configure failed.'}
& $cmake --build (Join-Path $actualRoot 'native-build') --target ArborXNative
if($LASTEXITCODE -ne 0){throw 'ArborX native driver build failed.'}
