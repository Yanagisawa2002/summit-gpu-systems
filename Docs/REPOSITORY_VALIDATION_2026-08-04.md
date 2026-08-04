# Standalone repository validation — 2026-08-04

## Environment

- Unity: `6000.5.2f1 (eb73d3b415a1)`
- OS: Windows 11 x64
- GPU: AMD Radeon AI PRO R9700
- Graphics API: Direct3D 12, feature level 12.2
- Source snapshot: `codex/gpu-cinematic-hero-benchmark` at `732bc37ffbcf13d33d69db27da195608440872c9`
- Adaptive overlay: `codex/gpu-adaptive-spatial-backend` at `a881f5bec7f099a4d581ca5fe534c5d0b043dc7e`

## Repository checks

`Tools/Test-RepositoryLayout.ps1` passed:

- 8/8 expected UPM packages present and embedded.
- All eight packages marked testable.
- 124 portable C#/shader files scanned.
- No `NYCGIS`, `Bfp2`, `FishNet`, or `FullCityWeather` reference leaked into the portable `Packages` or root benchmark `Assets` trees.
- Project-specific source isolated under `Integrations/NYCGIS`.
- All retained PowerShell scripts parsed successfully.
- All root and package JSON manifests parsed successfully.

## Unity tests

Null Device import/contract run:

- Total: 511
- Passed: 282
- Failed: 0
- Skipped: 229 GPU-dependent tests

Real-GPU Direct3D 12 run:

- Total: 511
- Passed: 511
- Failed: 0
- Skipped: 0
- Test duration reported by NUnit XML: 6.8469533 seconds

## Native timestamp plugin

- Rebuilt the Windows x64 D3D12 DLL from `Native~/SummitGpuTimestamps.cpp` with the repository build script and `/O2 /GL /W4 /WX`.
- Rebuilt DLL SHA-256: `BE42A0E925F8BFEC28F8096C841940F8B559C11AF99EA83B9BF5FDE83DF5D503`.
- Re-ran the full real-GPU D3D12 suite after the native build: `511/511` passed, `0` failed, `0` skipped.

The generated XML and Unity logs are intentionally excluded from Git under `TestResults/`. This document retains the reviewed result summary; future regressions should produce a new dated record rather than editing historical evidence.
