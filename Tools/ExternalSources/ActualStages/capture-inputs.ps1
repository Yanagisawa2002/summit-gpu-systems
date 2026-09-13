$ErrorActionPreference='Stop'
$actualRoot=Join-Path (Get-Location).Path 'Artifacts/actual-20260910'
$inputRoot=Join-Path $actualRoot 'inputs'
$captureRoot=Join-Path $actualRoot 'capture'
if((Test-Path $inputRoot) -or (Test-Path $captureRoot)){throw 'Input or capture destination exists.'}
$null=New-Item -ItemType Directory -Path $inputRoot,$captureRoot
try {
    $env:SUMMIT_CAPTURE_DIR=$inputRoot
    & "$actualRoot/native-build/CabanaCapture.exe" "$captureRoot/cabana-native-capture-timings.txt"
    if($LASTEXITCODE -ne 0){throw 'Original Cabana capture failed.'}
} finally {Remove-Item Env:SUMMIT_CAPTURE_DIR -ErrorAction SilentlyContinue}
& "$actualRoot/native-build/ArborXReplay.exe" export "$inputRoot/arborx-default.bin" "$captureRoot/arborx-generation.csv"
if($LASTEXITCODE -ne 0){throw 'Original ArborX capture failed.'}
python Tools/ExternalSources/prepare_actual_replay.py manifest
if($LASTEXITCODE -ne 0){throw 'Input manifest/source verification failed.'}
