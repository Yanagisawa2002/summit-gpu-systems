$ErrorActionPreference='Stop'
$mdRepo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$mdRun=Join-Path $mdRepo 'Artifacts/whole-task-md-20260915'
$mdVc='C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.44.35207'
$mdKit='C:\Program Files (x86)\Windows Kits\10'
if(!(Test-Path -LiteralPath "$mdVc/bin/Hostx64/x64/cl.exe")){throw 'Pinned MSVC 14.44.35207 unavailable'}
$env:PATH="$mdVc\bin\Hostx64\x64;$mdRun\dependencies\cmake\bin;$mdKit\bin\10.0.26100.0\x64;"+$env:PATH
$env:INCLUDE="$mdVc\include;$mdKit\Include\10.0.26100.0\ucrt;$mdKit\Include\10.0.26100.0\shared;$mdKit\Include\10.0.26100.0\um;$mdKit\Include\10.0.26100.0\winrt"
$env:LIB="$mdVc\lib\x64;$mdKit\Lib\10.0.26100.0\ucrt\x64;$mdKit\Lib\10.0.26100.0\um\x64"
$env:TEMP="$mdRun\temp";$env:TMP=$env:TEMP
$null=New-Item -ItemType Directory -Path $env:TEMP -Force
$mdUnity='C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe'
