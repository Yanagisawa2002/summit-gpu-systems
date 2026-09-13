[CmdletBinding()]
param(
    [ValidateSet('Plan','Build')][string]$Mode='Plan',
    [Parameter(Mandatory)][ValidateSet('cabana','arborx')][string]$Source,
    [Parameter(Mandatory)][string]$SourceDirectory,
    [string]$DependencyPrefix='',
    [string]$CMakePath='cmake',
    [string]$OutputDirectory=''
)
$ErrorActionPreference='Stop'
$externalRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$locked=Get-Content -LiteralPath (Join-Path $externalRoot 'PublicBenchmarks/External/sources.lock.json') -Raw | ConvertFrom-Json
$identity=$locked.sources | Where-Object id -eq $Source
$target=if($Source -eq 'cabana'){'LinkedCellPerformance'}else{'ArborX_Benchmark_BoundingVolumeHierarchy.exe'}
$options=if($Source -eq 'cabana'){@('-DCabana_ENABLE_TESTING=OFF','-DCabana_ENABLE_EXAMPLES=OFF','-DCabana_ENABLE_PERFORMANCE_TESTING=ON')}else{@('-DARBORX_ENABLE_TESTS=OFF','-DARBORX_ENABLE_EXAMPLES=OFF','-DARBORX_ENABLE_BENCHMARKS=ON','-DARBORX_ENABLE_CALIPER=OFF')}
if($Mode -eq 'Plan'){
    [pscustomobject]@{source=$Source;commit=$identity.commit;classification=$identity.classification;target=$target;configure=$options;defaultAction='prepare-only';performanceStatus='Unmeasured';dependencies=if($Source -eq 'cabana'){'Kokkos >=4.1 (matching upstream requirement)'}else{'Kokkos >=4.5, Boost program_options >=1.56, Google Benchmark >=1.5.4 (framework only)'};action='Configure/build only; no executable or CTest invocation.'}|ConvertTo-Json -Depth 4
    return
}
if(!$DependencyPrefix -or !(Test-Path -LiteralPath $DependencyPrefix)){throw 'Supply an existing dependency installation prefix; preparation never installs or retunes dependencies implicitly.'}
$nativeSource=[IO.Path]::GetFullPath($SourceDirectory)
$prefix=[IO.Path]::GetFullPath($DependencyPrefix)
if(!(Test-Path -LiteralPath (Join-Path $nativeSource 'CMakeLists.txt'))){throw 'Native source CMakeLists.txt is missing.'}
if(!(Get-Command $CMakePath -CommandType Application -ErrorAction SilentlyContinue)){throw 'CMake is missing; provide an installed executable with -CMakePath.'}
if(!(Get-ChildItem -LiteralPath $prefix -Filter 'KokkosConfig.cmake' -Recurse -File | Select-Object -First 1)){throw 'Dependency prefix has no installed KokkosConfig.cmake.'}
& python (Join-Path $PSScriptRoot 'prepare.py') verify-checkout $Source --directory $SourceDirectory
if($LASTEXITCODE -ne 0){throw 'Pinned upstream validation failed.'}
if(!$OutputDirectory){$OutputDirectory=Join-Path $externalRoot "Artifacts/native-$Source"}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if($IsWindows -and $OutputDirectory.Length -gt 100){throw 'Use a short native build path such as C:/b/summit-native with -OutputDirectory.'}
foreach($inputDirectory in @($nativeSource,$prefix)){
    if($OutputDirectory.Equals($inputDirectory,[StringComparison]::OrdinalIgnoreCase) -or
       $OutputDirectory.StartsWith($inputDirectory+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){
        throw 'Build output must be separate from the upstream source and installed dependencies.'
    }
}
if(Test-Path -LiteralPath $OutputDirectory){throw 'Choose a new build directory to retain source/configuration identity.'}
$null=New-Item -ItemType Directory -Path $OutputDirectory
# Retain exact resolved dependency files, not only a loose version requirement.
$dependencies=Get-ChildItem -LiteralPath $prefix -Recurse -File | ForEach-Object {
    [pscustomobject]@{path=[IO.Path]::GetRelativePath($prefix,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$dependencies | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'dependency-files.json') -Encoding utf8
& $CMakePath -S $SourceDirectory -B $OutputDirectory "-DCMAKE_PREFIX_PATH=$prefix" '-DCMAKE_BUILD_TYPE=Release' @options
if($LASTEXITCODE -ne 0){throw 'Native upstream configure failed; no program was run.'}
& $CMakePath --build $OutputDirectory --config Release --target $target
if($LASTEXITCODE -ne 0){throw 'Native upstream compile failed; no program was run.'}
Write-Host "Built $target only. Invoke the original native executable separately when running an explicitly selected workload."
