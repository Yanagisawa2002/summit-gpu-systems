# SUMMIT GPU Primitives

`com.summit.gpu-primitives` is a small Unity/D3D12-oriented library for reusable
GPU data-parallel building blocks. It separates algorithms such as histogram,
exclusive scan, stable stream compaction, and key/value radix sorting from the
NYC scene and from any specific sensor.

The package is an experimental performance-engineering surface. Correctness is
the first shipping gate. No performance claim is valid until the benchmark has
produced same-process, counterbalanced A/B data on a named GPU, driver, graphics
API, Unity build, input distribution, and input size.

## Why these primitives

Many scene-level problems reduce to the same lower-level pipeline:

```text
predicate or key generation
    -> count / histogram
    -> exclusive scan
    -> stable scatter / compaction
    -> indirect downstream work
```

This pattern applies to spatial binning, point-cloud filtering, visibility,
particle systems, collision broad phase, occupancy mapping, annotation, and
GPU-side ETL. Keeping it in a package makes the implementation, validation, and
profiling methodology portable across projects.

## Primitive contracts

- **Histogram** counts keys in `[0, bucketCount)` and must sum to the logical
  input count.
- **Exclusive scan** emits zero at element zero and the sum of all preceding
  elements at each later element.
- **Stable compaction** emits each selected payload exactly once and preserves
  original input order.
- **Stable radix sort** emits nondecreasing keys, preserves relative order for
  equal keys, and keeps key/payload association intact.

The implementation owns GPU buffers and dispatch recording; callers own the
meaning of keys and payloads. Logical element counts are separate from padded
dispatch sizes. Kernels must not read or publish padded lanes.

## Runtime API

The runtime entry point is `Summit.GpuPrimitives.GpuPrimitives`. Construct it
with a logical capacity, then record work into a caller-owned
`UnityEngine.Rendering.CommandBuffer`:

```csharp
using var primitives = new Summit.GpuPrimitives.GpuPrimitives(capacity);
primitives.RecordExclusiveScan(
    commandBuffer,
    input,
    output,
    count,
    GpuPrimitiveBackend.Auto);
```

Available recording methods:

- `RecordExclusiveScan`
- `RecordHistogram`
- `RecordStableCompaction`
- `RecordAppendCompaction`
- `RecordRadixSort32`

All public buffers are structured `uint` buffers. Histogram domains must be a
power of two and no larger than `GpuPrimitives.MaxElementCount`; with the
current dispatch bound, the largest permitted power of two is 8,388,608 bins.
The wave histogram supports up to 16 bins. The portable path uses a
group-shared 16-bin reduction for the same small domain and retains the global
atomic fallback for larger domains. Stable compaction defines order; append compaction only
defines membership and count. Radix sorting is a stable unsigned 32-bit
key/value sort.

A logical count of zero is supported with a one-word dummy input/output buffer:
scan and radix record no data work and leave outputs unchanged; histogram still
clears its requested bins when `clearOutput` is true; compaction clears its
output count to zero and does not publish payload. Buffers remain non-null even
for zero logical work because Unity does not allocate a zero-length
`GraphicsBuffer`.

`GpuPrimitiveBackend.Auto` selects a supported path.
`GpuPrimitiveBackend.Portable` and `GpuPrimitiveBackend.WaveOps` exist for
validation, profiling, and an explicit fallback policy. Check
`GpuPrimitives.SupportsWaveOperations` before explicitly requesting wave ops.
Recording methods do not perform readback and do not allocate caller-visible
buffers. Dispose the runtime instance when its scratch storage is no longer
needed.

`ScratchBytes` reports the exact logical payload bytes requested for persistent
scratch `GraphicsBuffer` instances. `ResidentBytes` currently equals
`ScratchBytes`; neither value includes driver allocation alignment,
caller-owned input/output buffers, or shader assets.

## Validation

Editor tests cover the dispatch and wave boundaries:

`1, 31, 32, 33, 63, 64, 65, 127, 128, 129, 255, 256, 257, 4095, 4096, 4097`

The test suite checks:

- exact output against deterministic CPU reference implementations;
- histogram totals and scan offsets;
- stable compaction order;
- append-compaction count and unordered membership;
- wave and portable-fallback output equivalence;
- radix key order, payload permutation, key/payload association, and stability.

Run EditMode tests with Unity Test Framework. A graphics-capable D3D12 device is
required for GPU integration cases. Pure CPU-oracle tests remain runnable when
compute shaders are unavailable. A skipped GPU test is not a passed GPU gate.

## Benchmark policy

Performance runs use prerecorded command buffers and deterministic input data.
Both variants execute in one Player process with warmup and a counterbalanced
block order. A valid GPU timing source, rather than CPU enqueue duration, is the
primary metric. The current harness requests whole-frame
`FrameTimingManager`/profiler timing; direct primitive-region D3D12 timestamps
remain a formal-benchmark requirement. Validation readback is allowed before
measurement; measurement must report zero readback bytes per sample.

Required reporting:

- GPU P50, P95, P99, mean, and sample count per variant;
- CPU frame timing and deadline misses as guardrails;
- input size, distribution, bucket count, selected implementation, and repeat;
- GPU, driver, graphics API, Unity version, build hash, and shader hash;
- correctness result and whether the GPU gate ran or was skipped;
- temporary and resident memory estimates;
- clock/thermal drift indicators when available.

See `Docs/GPU_PRIMITIVES_BENCHMARK_DESIGN.md` in the host repository for the
full experiment design.

## Known limitations

- Initial validation is AMD/D3D12 only unless a result explicitly names another
  platform.
- Shader Model and wave-operation support depend on the active backend.
- Device-specific wave width must be queried, not assumed.
- Unity profiler markers identify command regions but do not replace GPU
  timestamp queries or vendor-profiler captures.
- A synthetic distribution validates the primitive, not a complete dynamic
  LiDAR or robotics pipeline.
- Benchmark thresholds are workload-dependent; the package does not yet claim
  a universal adaptive crossover policy.

## Integration rule

Keep scene adapters outside this package. A scene may generate keys, submit
buffers, and consume outputs, but the primitive library must not reference
NYCGIS types, production scenes, MonoBehaviour singletons, or asset paths.
