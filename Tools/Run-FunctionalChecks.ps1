[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$functionalRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# This explicit project contains only CPU contracts and hand-reviewed deterministic
# assertions. Do not replace it with discovery of the repository's benchmark suites.
& dotnet run --project (Join-Path $PSScriptRoot 'FunctionalChecks/Summit.FunctionalChecks.csproj') --configuration Release --verbosity quiet
if($LASTEXITCODE -ne 0){throw 'CPU functional checks failed.'}
& dotnet run --project (Join-Path $PSScriptRoot 'SensorQueryFunctional/SensorQueryFunctional.csproj') --configuration Release --verbosity quiet
if($LASTEXITCODE -ne 0){throw 'CPU sensor query contracts failed.'}
& dotnet run --project (Join-Path $PSScriptRoot 'SensorQueryFunctional/SensorRecordingFunctional.csproj') --configuration Release --verbosity quiet
if($LASTEXITCODE -ne 0){throw 'Inert sensor command recording contracts failed.'}
& python (Join-Path $PSScriptRoot 'ExternalSources/test_contracts.py')
if($LASTEXITCODE -ne 0){throw 'External source contract checks failed.'}
& (Join-Path $PSScriptRoot 'Test-RepositoryLayout.ps1') -RepositoryRoot $functionalRoot
& git -C $functionalRoot diff --check
if($LASTEXITCODE -ne 0){throw 'Whitespace validation failed.'}
