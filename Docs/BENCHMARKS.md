# Benchmark guide

The root Unity project is intentionally asset-free. Every benchmark builder creates an empty scene, camera, and controller at build time, produces a Windows x64 development player, and then deletes the temporary scene.

## Common workflow

1. Run `Tools/Test-RepositoryLayout.ps1`.
2. Run `Tools/Run-UnityEditModeTests.ps1` after changing package contracts or compute kernels.
   Use `-UseGraphics -ForceDirect3D12` to execute GPU integration tests on a real D3D12 device; the default Null Device run validates CPU contracts and compilation.
3. Use the relevant `Run-*Benchmark.ps1` script for discovery.
4. Freeze workload parameters before a formal comparison.
5. Run counterbalanced A/B orders with multiple repetitions.
6. Require identical output hashes or CPU-oracle results before interpreting timing.
7. Record device, driver, graphics API, Unity version, warm-up, sample count, workload dimensions, and exact timing scope.

## Entry points

| Workload | Runner |
| --- | --- |
| Scan, radix sort, stable compaction | `Tools/Run-GpuPrimitiveBenchmark.ps1` |
| Direct count/scan/scatter binning | `Tools/Run-GpuDirectBinningBenchmark.ps1` |
| Adaptive direct/radix selection | `Tools/Run-GpuAdaptiveBinningBenchmark.ps1` |
| Device-keyed autotuning | `Tools/Run-GpuAutotuningBenchmark.ps1` |
| GPU-driven visibility and visible-only scatter | `Tools/Run-GpuDrivenInstanceBenchmark.ps1` |
| Engine-native CPU versus GPU-driven instances | `Tools/Run-GpuDrivenInstanceMacrobenchmark.ps1` |
| GPU-resident sensor pipeline | `Tools/Run-GpuSensorPipelineBenchmark.ps1` |
| Quantized SoA and producer-consumer fusion | `Tools/Run-GpuSensorDataPackingBenchmark.ps1` |
| Shared multi-sensor spatial index | `Tools/Run-GpuMultiSensorSharedIndexBenchmark.ps1` |
| Deadline-aware scheduling | `Tools/Run-GpuDeadlineSchedulerBenchmark.ps1` |
| Large-data residency | `Tools/Run-GpuResidencyBenchmark.ps1` |

Generated outputs default to `Reports/` and `Builds/`, both ignored by Git. Promote only compact, reviewed evidence into `Docs/` or `Evidence/`.

## Metric rules

- `GPU average` is useful for throughput but does not describe stutter.
- `GPU P99` is the 99th-percentile duration of the explicitly documented GPU scope.
- `Frame P99` includes CPU, synchronization, presentation, and other frame work; it must not be mislabeled as a kernel measurement.
- FPS is presentation-friendly but should be accompanied by milliseconds and tail latency.
- A result is invalid if correctness hashes differ, timing samples are missing, the scope changes between variants, or ordering is not counterbalanced.

The D3D12 timestamp package fails closed when the graphics API or native plugin is unavailable. Scripts must report that state instead of silently substituting an estimated whole-frame value.

## Preview is not measurement

`Tools/Run-GpuSystemsShowcase.ps1` builds a human-facing side-by-side preview.
It renders the same deterministic input through the CPU reference and
GPU-driven path, validates the two offscreen image hashes, and then captures
UI-labelled frames. It deliberately writes `formalTiming=false` and
`timingClaimsAllowed=false` in its receipt.

The showcase must never be inserted into a formal timing loop: GUI layout,
screen capture, two simultaneous adapters, and video-frame encoding all alter
CPU/GPU work. Formal benchmark Players remain offscreen and use their own
counterbalanced schedules, validation phases, timestamp coverage gates, and
atomic receipts.

## External benchmark boundary

The official Unity BRG Shooter fixture is pinned and prepared outside this
repository. Its current adapter smoke is not formal performance evidence:
baseline/candidate image parity and native GPU timing coverage did not pass.
The diagnostic CPU/upload observations from that run must not be quoted as
speedups. Upstream content is not vendored.
