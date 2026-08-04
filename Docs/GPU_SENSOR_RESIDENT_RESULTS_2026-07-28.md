# GPU-resident BEV obstacle-cost results — 2026-07-28

## Decision

The GPU-resident pipeline is a successful CPU-headroom and data-locality optimization, but it is
not a meaningful one-sensor frame-rate optimization on the measured AMD system.

Keep the implementation opt-in:

- Use it when a robotics, policy, visualization, or data-generation consumer can read the
  obstacle-cost map on the GPU.
- Do not read the product back every update. The earlier readback benchmark showed that doing so
  adds roughly one frame of CPU-visible completion latency.
- Keep the Burst CPU path for latency-sensitive CPU consumers.
- Do not claim a material FPS or GPU P99 improvement from the one-sensor result.

The architectural acceptance gates passed: the GPU produced the same 256 x 256 obstacle-cost map
as the Burst CPU reference, measurement-time readback was zero, and CPU host-work P99 fell by
99.46%. Frame and GPU timing remained statistically neutral, with slight P99 regressions.

## GPU-resident product

The benchmark now represents a small robotics data pipeline rather than a projection-only kernel:

1. Project deterministic 1280 x 720 depth into a 256 x 256 BEV occupancy grid.
2. Use integer atomics to accumulate colliding depth samples.
3. Consume the occupancy grid in a second compute dispatch.
4. Produce a 0–255 obstacle-cost map with 0.25 m cells and a 0.75 m inflation radius.
5. Leave the 256 KiB cost map in a `GraphicsBuffer` for the next GPU consumer.

The CPU control produces exactly the same product using Burst jobs. The GPU path performs one
full 65,536-cell correctness readback during the 30-second warm-up and performs no readback during
the 600-frame measurement window.

`sensorCompletionSemantics=gpu-resident-enqueued` is intentional. The benchmark measures
submission cost and frame-level GPU execution through Unity's `FrameTimingManager`; it does not
invent a CPU-visible completion latency for work that stays on the GPU.

## Reproducible baseline

- Branch base before this follow-up: `3d8d444`.
- Unity: 6000.5.2f1, Windows Development Player, Direct3D 12, 1280 x 720.
- GPU: AMD Radeon AI PRO R9700, 32,476 MiB, feature level 12.2.
- Driver: `32.0.31021.5001`.
- Prepared production-scene SHA256:
  `B1E650A08F43F8A47C470CB1EB4796FCF6926209BE36DBFED7E5E5FB2CA6FB5E`.
- Player executable SHA256:
  `34A412B81651ED571B97F4D1BA71A9CA79457FF5779D56969B8F0C4772AD2CEE`.
- Player `Assembly-CSharp.dll` SHA256:
  `09B4ED146EA89493CC80B5F9A11765A8E6A5965DDCB3FD5D6B2BFC6C3E840957`.
- Player `level0` SHA256:
  `A2D62AACE1AD1082373B26B62C9365A9396429539578724D22A9E7622412F374`.
- Workload: one camera, HeavyStorm, 30-second warm-up, 600 sampled frames, three repeats.
- Sensor: 1280 x 720 at 30 Hz.
- Pair order: off, Burst CPU obstacle cost, GPU-resident obstacle cost.
- Every accepted run: 227 total BFP2 packs, 73 resident packs, 1,932,028,912 resident GPU
  bytes, 66 orthophoto base pages, 180 LOD0 pages, and 600/600 GPU timing samples.
- CPU and GPU modes each produced 169 sensor updates in every round.

The heavy scene, Player, generated GIS assets, and multi-megabyte logs remain local to the
isolated benchmark worktree. Only source, documentation, and the compact CSV summary are
versioned.

## AMD Radeon AI PRO R9700

### Per-round results

| Round | Mode | Frame avg / P95 / P99 | GPU avg / P95 / P99 | Host avg / P99 | Throughput |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | Off | 9.301 / 9.383 / 9.533 ms | 9.200 / 9.274 / 9.407 ms | unavailable | unavailable |
| 1 | Burst CPU cost | 9.401 / 9.500 / 9.638 ms | 9.285 / 9.378 / 9.488 ms | 1.382 / 1.478 ms | 27.614 Mpts/s |
| 1 | GPU-resident cost | 9.371 / 9.468 / 9.637 ms | 9.272 / 9.373 / 9.522 ms | 0.006 / 0.008 ms | 27.702 Mpts/s |
| 2 | Off | 9.397 / 9.488 / 9.584 ms | 9.302 / 9.381 / 9.490 ms | unavailable | unavailable |
| 2 | Burst CPU cost | 9.395 / 9.465 / 9.537 ms | 9.299 / 9.369 / 9.402 ms | 1.386 / 1.528 ms | 27.632 Mpts/s |
| 2 | GPU-resident cost | 9.372 / 9.444 / 9.623 ms | 9.266 / 9.341 / 9.373 ms | 0.006 / 0.009 ms | 27.699 Mpts/s |
| 3 | Off | 9.367 / 9.429 / 9.497 ms | 9.253 / 9.322 / 9.354 ms | unavailable | unavailable |
| 3 | Burst CPU cost | 9.350 / 9.421 / 9.473 ms | 9.244 / 9.312 / 9.345 ms | 1.383 / 1.462 ms | 27.765 Mpts/s |
| 3 | GPU-resident cost | 9.359 / 9.434 / 9.499 ms | 9.259 / 9.335 / 9.379 ms | 0.006 / 0.007 ms | 27.737 Mpts/s |

### Three-run means

| Mode | Frame avg / P95 / P99 | GPU avg / P95 / P99 | Host avg / P99 | Throughput |
| --- | ---: | ---: | ---: | ---: |
| Off | 9.355 / 9.433 / 9.538 ms | 9.252 / 9.326 / 9.417 ms | unavailable | unavailable |
| Burst CPU cost | 9.382 / 9.462 / 9.549 ms | 9.276 / 9.353 / 9.412 ms | 1.384 / 1.489 ms | 27.670 Mpts/s |
| GPU-resident cost | 9.367 / 9.449 / 9.586 ms | 9.266 / 9.350 / 9.425 ms | 0.006 / 0.008 ms | 27.713 Mpts/s |

GPU-resident versus Burst CPU:

| Metric | Mean change | Per-round behavior | Decision |
| --- | ---: | --- | --- |
| Host work average | -99.57% | Stable | Material CPU-headroom gain |
| Host work P99 | -99.46% | -99.41% to -99.52% | Material and repeatable |
| Sensor throughput | +0.15% | -0.10% to +0.32% | Neutral |
| Frame average | +0.16% improvement | -0.10% to +0.32% | Neutral |
| Frame P95 | +0.14% improvement | -0.14% to +0.34% | Neutral |
| Frame P99 | -0.39% regression | -0.90% to +0.01% | Does not clear P99 gate |
| GPU average | +0.11% improvement | -0.16% to +0.36% | Neutral |
| GPU P99 | -0.14% regression | -0.36% to +0.31% | Neutral |

The aggregate frame averages correspond to approximately 106.59 FPS for Burst CPU and 106.75 FPS
for GPU resident. This difference is below a defensible usefulness threshold.

## Correctness and transfer proof

All three GPU-resident runs reported:

- `sensorCorrectnessPassed=1`.
- `sensorMaximumPointError=0`.
- `sensorReadbackErrors=0`.
- `sensorDroppedFrames=0`.
- `sensorMeasurementReadbackBytesPerFrame=0`.
- `sensorMeasurementReadbackRequests=0`.
- `sensorGpuResidentProduct=1`.

This proves zero CPU transfer in the measured path, not zero memory traffic inside the GPU. The
occupancy and cost buffers still consume GPU memory bandwidth, and Unity's frame timing includes
their dispatch cost.

The compact raw summary is:
`Docs/BenchmarkData/AMD_R9700_SENSOR_BEV_RESIDENT_2026-07-28.csv`.

## Reproduction

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-NYCGISSensorDataAB.ps1 `
  -DataRoot C:\path\to\NYCGISData `
  -DeviceIndex 0 `
  -CameraCount 1 `
  -SensorWidth 1280 `
  -SensorHeight 720 `
  -SensorRateHz 30 `
  -Workload BevResident `
  -WarmupSeconds 30 `
  -SampleFrames 600 `
  -Repeats 3
```

The runner rejects incomplete GPU timing, zero production residency, or failed sensor
correctness.

## Employment-safe wording

A defensible resume statement is:

> Implemented and profiled a GPU-resident robotics sensor pipeline in Unity/D3D12, converting
> 1280 x 720 depth into a validated 256 x 256 BEV obstacle-cost map on an AMD Radeon AI PRO
> R9700. Eliminated measurement-time CPU readback and reduced per-update CPU host-work P99 by
> 99.46%, while reporting the neutral frame/GPU timing and P99 trade-offs rather than claiming an
> unsupported FPS gain.

Do not claim NVIDIA validation, a material FPS improvement, or GPU bandwidth reduction. The next
high-value experiment is multi-sensor scaling or connecting the resident cost buffer directly to
a real GPU policy, visualization, or data-generation consumer.
