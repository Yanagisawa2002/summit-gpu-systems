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
| GPU-resident sensor pipeline | `Tools/Run-GpuSensorPipelineBenchmark.ps1` |
| Quantized SoA and producer-consumer fusion | `Tools/Run-GpuSensorDataPackingBenchmark.ps1` |
| Shared multi-sensor spatial index | `Tools/Run-GpuMultiSensorSharedIndexBenchmark.ps1` |
| Optional sensor range-query backends | `Tools/Run-GpuSensorQueryBenchmark.ps1` (shared lock required) |
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
