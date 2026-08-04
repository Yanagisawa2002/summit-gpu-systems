# GPU Performance Engineering Visual A/B Showcase

## Purpose

This Player turns the production full-city scene into a deterministic, high-pressure A/B demonstration. It is designed to answer two questions separately:

1. Is the difference visible as frame-time spikes and data age under load?
2. Does a counterbalanced, process-isolated capture show a repeatable improvement with identical outputs?

A and B are never timed side by side in one process. Sharing one GPU would make both results invalid. Formal capture launches each variant in a fresh Player process with the same seed, camera path, frame count, camera count, data, resolution, and validation state.

## What A and B execute

| Subsystem | A — standard pipeline | B — GPU performance stack |
|---|---|---|
| City geometry | Scalar AoS culling and full 32-bit visible-index materialization | Wave64 prefix compaction, Tile32 descriptors, direct source-index fetch in the vertex stage |
| Dynamic sensor data | CPU deterministic producer, CPU-to-GPU sample/key upload, one CSR rebuild per sensor | GPU-resident producer, one shared CSR build, four query consumers |
| GPU primitives | Portable scan/radix/compaction kernels | Device-keyed WaveOps selection when the measured profile matches |
| Large point/page data | Rebuild and upload the complete visible page window | Persistent LRU residency and page-table delta uploads |
| Deadline work | FIFO order on the main graphics queue | Least-slack-first order on the main queue |
| Camera pressure | One visible camera plus up to three deterministic offscreen payload cameras | Identical |
| Weather/quality | Production scene, heavy storm, same render settings | Identical |

The exact CPU baseline path is implemented as `RecordCpuProducedRebuiltPerSensor`: samples and keys are materialized on the CPU and uploaded once, then the same spatial index is independently rebuilt for each sensor. Query segmentation and final digest are identical to the GPU-produced shared-index path.

## Deliberately excluded from B

The showcase does not enable every experiment merely because code exists:

- Cluster-local uint16 indices reduce source-index memory but did not produce a stable frame-time win.
- Async compute is disabled on the current AMD R9700 because calibration made both average and P99 worse.
- Subpixel cluster culling is disabled to keep output quality identical.
- Fixed direct binning is not forced because it was not universally faster; the trusted CSR backend remains available as fallback.
- No driver modification or vendor-private API is claimed. This is DX12/HLSL/GPU-algorithm work above the driver boundary.

## Visual HUD

The Player shows:

- current frame time, FPS, GPU frame time, running average, and P99;
- a 240-frame graph with 16.7 ms and 33.3 ms thresholds;
- a large red `HITCH` indicator for frames above 33.3 ms;
- sensor submissions, dropped updates, data age, CPU producer cost, and logical upload volume;
- page-cache hit rate and pages uploaded by the most recent update;
- critical-job completion age and late observations;
- active A/B paths and the selected device profile.

The HUD is intentionally rendered in both variants. Use `-gpu-stress-no-hud` only when a clean capture without overlay is explicitly required.

## Build and run interactively

Open this worktree as the Unity project:

```text
C:\Users\EdwinLiu\Downloads\SUMMIT-gpu-stress-showcase
```

Use the Unity menu:

```text
Tools > NYC GIS Demo > GPU > Build Visual Stress A-B Player
```

Then launch A:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\Run-GpuStressShowcaseInteractive.ps1 -Variant baseline -SkipBuild
```

Close the Player and launch B:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\Run-GpuStressShowcaseInteractive.ps1 -Variant optimized -SkipBuild
```

If the GIS data location is not already available through the project configuration, pass `-DataRoot <path>` or set `NYCGIS_DATA_ROOT`.

## Run a formal counterbalanced A/B

```powershell
powershell -ExecutionPolicy Bypass -File Tools\Run-GpuStressShowcaseAB.ps1 `
  -DataRoot <NYC_GIS_DATA_ROOT> `
  -Rounds 3 `
  -CameraCount 4 `
  -SceneWarmupSeconds 60 `
  -WorkloadWarmupFrames 120 `
  -SampleFrames 900
```

The 60-second production-scene warmup matches the prior full-city formal
captures. Sampling starts only after BFP2 has resident packs, no active pack
loads, and that state remains stable for 30 consecutive frames.

The BFP2 indirect shader is explicitly retained in Player builds. A clean
worktree therefore uses the renderer's runtime-material fallback and does not
depend on a Git-ignored generated material from a previous Editor session.

The runner alternates A/B order between rounds and writes:

- one JSON report and raw-frame CSV per process;
- one screenshot per process;
- `runs.csv` and `report-summary.csv`;
- `SUMMARY.md` with median A/B improvements;
- Player and build logs.

Acceptance requires:

- all workload oracle checks pass;
- the city culling snapshot is valid with zero overflow;
- A actually runs `ScalarAoS`;
- B actually runs `WaveTile32`;
- every A/B final composite hash is identical.

## Local AMD high-pressure pilot (2026-07-31)

A one-round implementation pilot completed on the AMD Radeon AI PRO R9700
under DX12 with four cameras, 1,048,576 dynamic sensor elements, four sensors,
64 queries per sensor, 2,048 points per resident page, 60 seconds of scene
warmup, 120 workload-warmup frames, and 300 measured frames per process.

| Metric | A baseline | B optimized | B improvement |
|---|---:|---:|---:|
| Average frame time | 28.676 ms | 16.918 ms | 41.00% |
| Frame P99 | 85.370 ms | 55.613 ms | 34.86% |
| Average GPU time | 18.266 ms | 10.485 ms | 42.60% |
| GPU P99 | 24.626 ms | 18.689 ms | 24.11% |
| Frames above 33.3 ms | 19.67% (59/300) | 4.00% (12/300) | 79.66% lower rate |
| Logical sensor upload | 5,368,709,120 B | 0 B | 100.00% |
| Logical page upload | 2,148,842,648 B | 195,885,952 B | 90.88% |

Both processes converged at 73 resident BFP2 packs with zero active loads.
They produced the same 263,188 visible clusters, 100,951,038 visible indices,
city hash `1F7FFCCF8075F1E6`, and composite validation hash
`0D4AFF9D6F1A27D5`. B emitted 1,051,864 Tile32 descriptors instead of the
full visible-index stream. All sensor, page-residency, deadline-workload, city,
and renderer-path validation contracts passed.

This is implementation evidence, not the final resume number: it is one
A-then-B round and only 300 frames. Run the documented 3 x 900 counterbalanced
matrix before promoting the percentages. GPU timing used 229/71
FrameTimingManager/ProfilerRecorder samples for A and 252/48 for B. The
integrated Player-loop deadline P99 regressed (70.997 to 83.531 ms), so no
scheduler claim should be taken from this visual pilot; retain the dedicated
native timestamp result for that subsystem.

Verification on Unity 6000.5.2f1 included a successful Windows DX12
Development Player build, a completed high-pressure A/B run with 300/300
valid GPU timing samples in each process, and 69/69 passing targeted EditMode
tests across the sensor, residency, and deadline packages and adapters.

## Device profile behavior

The repository contains the reviewed AMD Radeon AI PRO R9700 profile from commit `bd60f3e`. It selects WaveOps only when the runtime fingerprint matches the measured device and all required primitives were accepted. Otherwise the sensor pipeline falls back to portable HLSL. Renderer capability checks still retain their own fallback.

Current cross-vendor status remains honest:

- AMD Radeon AI PRO R9700 / RDNA 4 / DX12: measured profile available.
- NVIDIA: implementation and fallback available, formal hardware validation pending.

## Measurement caveat

Frame and GPU timing are captured during the fixed sample interval. Digest readback, city-output validation, report writing, and screenshots occur afterward.

Critical completion age in the visual integration HUD is observed at Player-loop polling granularity. Resume-grade deadline numbers must continue to come from the dedicated native DX12 timestamp benchmark, not from this HUD counter.
