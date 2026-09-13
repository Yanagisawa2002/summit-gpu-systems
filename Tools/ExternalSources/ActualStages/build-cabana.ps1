$ErrorActionPreference='Stop'
$actualRoot=Join-Path (Get-Location).Path 'Artifacts/actual-20260910'
$deps=Join-Path $actualRoot 'dependencies'
$cmake=Join-Path $deps 'cmake-3.31.10-windows-x86_64/bin/cmake.exe'
Import-Module 'C:/Program Files/Microsoft Visual Studio/18/Community/Common7/Tools/Microsoft.VisualStudio.DevShell.dll'
Enter-VsDevShell -VsInstallPath 'C:/Program Files/Microsoft Visual Studio/18/Community' -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null
$kokkosInstall=Join-Path $actualRoot 'k-install'
& $cmake -S (Join-Path $deps 'kokkos') -B (Join-Path $actualRoot 'k-build') -G 'NMake Makefiles' '-DCMAKE_BUILD_TYPE=Release' '-DCMAKE_CXX_STANDARD=20' "-DCMAKE_INSTALL_PREFIX=$kokkosInstall" '-DKokkos_ENABLE_SERIAL=ON' '-DKokkos_ENABLE_TESTS=OFF' '-DKokkos_ENABLE_EXAMPLES=OFF'
if($LASTEXITCODE -ne 0){throw 'Kokkos configure failed.'}
& $cmake --build (Join-Path $actualRoot 'k-build') --target install --parallel 1
if($LASTEXITCODE -ne 0){throw 'Kokkos build failed.'}
& $cmake -S (Join-Path $deps 'cabana') -B (Join-Path $actualRoot 'cabana-build') -G 'NMake Makefiles' '-DCMAKE_BUILD_TYPE=Release' '-DCMAKE_CXX_STANDARD=20' "-DCMAKE_PREFIX_PATH=$kokkosInstall" '-DCabana_ENABLE_GRID=OFF' '-DCabana_REQUIRE_MPI=OFF' '-DCMAKE_DISABLE_FIND_PACKAGE_MPI=ON' '-DCabana_REQUIRE_ARBORX=OFF' '-DCMAKE_DISABLE_FIND_PACKAGE_ArborX=ON' '-DCabana_ENABLE_PERFORMANCE_TESTING=ON' '-DCabana_ENABLE_TESTING=OFF' '-DCabana_ENABLE_EXAMPLES=OFF'
if($LASTEXITCODE -ne 0){throw 'Cabana configure failed.'}
& $cmake --build (Join-Path $actualRoot 'cabana-build') --target LinkedCellPerformance --parallel 1
if($LASTEXITCODE -ne 0){throw 'Cabana native benchmark build failed.'}
