# AMD GPU sensor-data pipeline results — 2026-07-27

## Decision

Keep this branch as an opt-in benchmark and integration prototype. Do not describe it as a
general frame-time optimization and do not enable it by default.

Two different sensor products were tested:

- A dense 1280 x 720 point cloud is not a useful CPU-consumed GPU optimization on this machine.
  Moving projection to compute reduced CPU submission cost, but did not improve throughput, GPU
  time, or frame time, and added roughly one frame of asynchronous completion latency.
- A compact 256 x 256 bird's-eye-view (BEV) occupancy-count grid has conditional value. It
  processes all 921,600 input samples while reducing output from 14,745,600 bytes to 262,144
  bytes per update (56.25x smaller). GPU dispatch reduced measured host work by about 99%, but
  final GPU and frame time were slightly worse and readback P99 was 34.760 ms.

The production choice is therefore explicit:

- Prefer the Burst CPU BEV path when a CPU consumer needs the current result with low latency.
- Prefer the GPU BEV path only when the downstream consumer remains on the GPU, or when a delayed
  asynchronous CPU result is acceptable.
- The GPU-resident BEV consumer follow-up is now implemented and measured in
  `GPU_SENSOR_RESIDENT_RESULTS_2026-07-28.md`. Avoiding readback produced a repeatable CPU-headroom
  gain, while one-sensor frame and GPU timing remained neutral.

## What was implemented

The benchmark is command-line gated and remains off during normal production runs.

- Deterministic 1280 x 720 depth input at 30 Hz.
- Burst CPU references for dense projection and BEV binning.
- D3D12 compute kernels for depth-to-point-cloud and depth-to-BEV conversion.
- Three-slot `GraphicsBuffer` rings and callback-based `AsyncGPUReadback`.
- Preallocated callbacks, no per-update closure allocation, explicit drain checks, and dropped
  request/error counters.
- Dense validation against the CPU reference with a `0.0005` tolerance.
- Exact BEV validation across all 65,536 cells, using the same projection and bounds on CPU and
  GPU.
- A PowerShell A/B runner that rejects missing GPU timing samples, zero-residency scene loads, and
  correctness failures.

The BEV kernel uses integer atomics because multiple depth samples can map to the same cell. The
host-work metric covers synchronous CPU work or GPU dispatch/readback submission. GPU execution
is accounted for separately by Unity's `FrameTimingManager`.

## Reproducible baseline

- Branch base: `b69c718`.
- Unity: 6000.5.2f1, Windows Development Player, Direct3D 12, 1280 x 720.
- GPU: AMD Radeon AI PRO R9700, 32,476 MiB, feature level 12.2.
- Driver: `32.0.31021.5001`.
- Source legacy production scene SHA256:
  `B25020CCBD1CEC1C17A70A4220FC46307CB01EE51643B9AD70748D1928942398`.
- Prepared local benchmark scene SHA256:
  `B1E650A08F43F8A47C470CB1EB4796FCF6926209BE36DBFED7E5E5FB2CA6FB5E`.
- Final Player executable SHA256:
  `34A412B81651ED571B97F4D1BA71A9CA79457FF5779D56969B8F0C4772AD2CEE`.
- Final Player `Assembly-CSharp.dll` SHA256:
  `6B87D0F20406C69DBB16C54E085D8F7DC8E7C9101C4DD5013975D0434FA2A5DE`.
- Final Player `level0` SHA256:
  `C1971EED9AA0BD60756213AB1531A690187932BAE0E6CD94BF3E081F9921972E`.
- Workload: one camera, HeavyStorm, 30 second warm-up, 600 sampled frames, three repeats.
- Sensor: 1280 x 720, 30 Hz; BEV output: 256 x 256 unsigned occupancy counts.
- Pair order per round: off, Burst CPU, GPU compute.
- Every accepted final run had 227 total BFP2 packs, 73 resident packs, 1,932,028,912
  resident GPU bytes, and 600/600 GPU timing samples.

The full production scene came from the read-only migration source. Water v1 repair and the BFP2
renderer configurator were applied only to the copied worktree scene. The heavy scene, generated
GIS assets, Player, and multi-megabyte runtime logs remain local and outside the commit.

## Final compact BEV matrix

Values are arithmetic means across the three final rebuilt-Player runs.

| Mode | Frame avg / P95 / P99 | GPU avg / P95 / P99 | Host avg / P99 | Completion avg / P99 | Throughput |
| --- | ---: | ---: | ---: | ---: | ---: |
| Off | 9.570 / 9.676 / 10.310 ms | 9.448 / 9.568 / 9.638 ms | unavailable | unavailable | unavailable |
| Burst CPU BEV | 9.582 / 9.683 / 9.778 ms | 9.465 / 9.575 / 9.636 ms | 1.111 / 1.261 ms | 1.111 / 1.261 ms | 27.680 Mpts/s |
| GPU BEV | 9.645 / 9.724 / 10.797 ms | 9.500 / 9.613 / 9.684 ms | 0.010 / 0.010 ms | 28.305 / 34.760 ms | 27.658 Mpts/s |

GPU BEV versus Burst CPU BEV:

| Metric | Change | Interpretation |
| --- | ---: | --- |
| Host work average | -99.07% | Real CPU headroom gain |
| Host work P99 | -99.18% | Repeatable across all three rounds |
| Output bytes per update | -98.22% versus dense output | 56.25x smaller product |
| Sensor throughput | -0.08% | Effectively unchanged |
| GPU average | +0.37% | Slight regression |
| Frame average | +0.66% | Slight regression; about 104.36 to 103.68 FPS |
| GPU P99 | +0.50% | Slight regression |
| Completion latency P99 | +33.50 ms | Material cost for CPU consumers |

All three paired rounds regressed frame average by 0.18% to 1.21%. Frame P99 also regressed in all
three pairs; the 25.21% first-round spike makes the mean P99 unsuitable as a headline benefit.
This result does not claim a frame-time or GPU-time win.

All nine final runs completed successfully. GPU BEV had zero dropped updates, zero readback
errors, and exact cell-for-cell correctness in every repeat.

The version-controlled raw summary is:
`Docs/BenchmarkData/AMD_R9700_SENSOR_BEV_2026-07-27.csv`.

## Dense point-cloud control

The dense control exists to test the tempting but usually incomplete optimization of moving only
depth projection to the GPU while still reading every point back to the CPU. Values below are
means across three exploratory runs on the same R9700 production-scene workload.

| Mode | Frame avg / P95 / P99 | GPU avg / P95 / P99 | Host avg / P99 | Completion avg / P99 | Throughput |
| --- | ---: | ---: | ---: | ---: | ---: |
| Off | 9.451 / 9.508 / 9.705 ms | 9.332 / 9.399 / 9.437 ms | unavailable | unavailable | unavailable |
| Burst CPU dense | 9.483 / 9.540 / 10.088 ms | 9.351 / 9.422 / 9.481 ms | 0.414 / 0.506 ms | 0.414 / 0.506 ms | 27.698 Mpts/s |
| GPU dense | 9.550 / 9.618 / 9.695 ms | 9.438 / 9.512 / 9.556 ms | 0.008 / 0.014 ms | 28.643 / 32.085 ms | 27.666 Mpts/s |

Compared with Burst CPU, GPU dense projection was 0.70% worse in frame average, 0.93% worse in
GPU average, and 0.12% lower in throughput. It reduced host submission P99 by about 97%, but
increased completion P99 by 31.58 ms. The dense path is retained only as a reproducible control,
not as a candidate production feature.

The version-controlled raw summary is:
`Docs/BenchmarkData/AMD_R9700_SENSOR_DENSE_2026-07-27.csv`.

## Reproduction

Build the benchmark Player:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe' `
  -batchmode -quit `
  -projectPath 'C:\path\to\SUMMIT-gpu-sensor-data' `
  -executeMethod NYCGISFullCityWeatherPerformanceQA.BuildFullCityWeatherPerformancePlayerBatch `
  -logFile 'C:\path\to\SUMMIT-gpu-sensor-data\Logs\gpu-sensor-build.log'
```

Run the final BEV matrix:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-NYCGISSensorDataAB.ps1 `
  -DataRoot C:\path\to\NYCGISData `
  -DeviceIndex 0 `
  -CameraCount 1 `
  -SensorWidth 1280 `
  -SensorHeight 720 `
  -SensorRateHz 30 `
  -Workload Bev `
  -WarmupSeconds 30 `
  -SampleFrames 600 `
  -Repeats 3
```

Use `-Workload Dense` for the point-cloud control. Run the Player minimized rather than hidden;
the runner requires complete `FrameTimingManager` GPU samples.

## Platform status and resume-safe wording

No connected NVIDIA adapter was available. The workstation exposed the discrete R9700 and an AMD
integrated GPU only, so this branch supports an AMD result and a future NVIDIA validation plan,
not an AMD-versus-NVIDIA claim.

A defensible resume statement is:

> Built and benchmarked Burst CPU and D3D12 compute sensor-data pipelines in a full-city Unity
> simulation on an AMD Radeon AI PRO R9700. A compact 256 x 256 BEV path reduced CPU host work by
> 99% and output size by 56.25x versus dense point-cloud readback, with exact GPU/CPU validation;
> profiling also showed no GPU/frame-time gain and a 34.8 ms P99 asynchronous-readback latency,
> leading to an explicit GPU-resident-consumer or latency-tolerant adoption boundary.

Do not add NVIDIA validation, stable-frame-rate improvement, draw-call reduction, or GPU bandwidth
improvement to that statement until those measurements exist.
