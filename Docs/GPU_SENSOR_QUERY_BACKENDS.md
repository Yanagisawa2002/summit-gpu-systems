# Optional load-balanced sensor range queries

## Status and scope

`CellSerial` remains the default and the existing AoS query baseline. The new
`PointChunks` and `PointChunksWave` candidates have no formal performance ranking.
This change does not modify index construction/update logic or the packed SoA
pipeline. Compare candidates with the same payload representation and index;
previous packed-layout results do not establish an AoS query-kernel winner.

The pipeline constructor appends optional `queryBackend` and
`queryIndexEntryCapacity` parameters. Existing source calls continue to compile;
precompiled consumers must rebuild for the extended constructor signature.
`QueryBackend` and `QueryScratchBytes` expose the selection and additional memory;
`ResidentBytes` includes that scratch. The original primitive `Backend` parameter
continues to select index primitives independently.

```csharp
using var pipeline = new GpuSensorPipeline(elementCapacity, queryCapacity,
    queryBackend: GpuSensorQueryBackend.PointChunksWave,
    queryIndexEntryCapacity: reservedCsrCapacity);
// Existing producer/index/query calls use the explicit query candidate.
// After an index has been built, record only the query segment:
pipeline.RecordQueries(commands, elementCount, queryStart, queryCount);
```

`GpuSensorChunkedRangeQuery` is also a standalone consumer with public external
sample, offset, ID, query and output buffers. Its constructor accepts separate
sample and CSR-entry capacities. `Record` accepts the sample-address limit,
query start/count and optional quantization. It records no index work, upload or
readback. Callers must validate query centers/radii in the 16-bit domain and supply
a trusted fixed-grid CSR: offsets start at zero, are monotonic, the terminal offset
fits both the ID buffer and reserved entry capacity, and membership matches sample
coordinates. IDs outside the sample-address limit, including `uint.MaxValue`
tombstones, are skipped; valid IDs are hashed as stable payload slots. A CSR can
contain more reserved slots than samples. All buffers must be distinct and alive
until GPU completion. Malformed GPU CSR contents are outside this trusted API.

## Work distribution, capacity and synchronization

Each query records four dispatches: clear its digest and work count; build the
point-chunk work list; prepare indirect arguments; consume the chunks. Sixteen
setup groups traverse only the candidate cells in a clamped inclusive AABB. Each
nonempty cell reserves `ceil(occupancy / 256)` entries, including tombstone slots.
Each entry is an eight-byte begin/end pair consumed by a 256-thread group. Point
filtering, quantization, hashing, uint wraparound sum and XOR match the old query.
Only the selected output segment is overwritten. Query count zero records no
commands; standalone sample-address count zero produces empty digests. Pipeline
producer methods retain their previous positive-element/count contract.

For CSR entry capacity `S`, the allocated bound is
`floor(S / 256) + min(S, 262144)` chunks. Summing ceil over nonempty cells cannot
exceed this bound, regardless of skew. Scratch uses `8 * bound + 16` bytes and is
reused between queries, avoiding a query-count multiplier. Indirect groups span
two dimensions when the work list exceeds 65,535 entries; padded groups contribute
zero. Empty work lists execute one zero-contribution group. Allocations and shape
validation happen before dispatch. An ID buffer larger than the reserved entry
capacity is rejected before recording; there is no silent truncation. Reserve the
external index's slot capacity, not its live-point count. A caller can explicitly
select the original CellSerial pipeline when the candidate is unsuitable.

Scratch is owned per consumer, reused by ordered commands on one queue. Multiple
recorded submissions on the same ordered queue are safe when input lifetimes are
respected. Concurrent queues must use separate consumers or explicit fences before
scratch reuse; wait for GPU completion before disposing buffers. This is not an
in-flight ownership/fence manager. The pipeline allocates no query scratch for
CellSerial. It does not automatically promote or silently substitute candidates.

## Reduction experiment

`PointChunks` uses the portable 256-lane shared-memory tree. `PointChunksWave`
uses DXC/SM6 native-width `WaveActiveSum`, `WaveActiveBitXor` and first-lane atomics.
All lanes participate in partial chunks; no Wave32/64 assumption is made. This
removes shared storage and group barriers from the consume reduction, at the cost
of one four-word atomic digest per wave instead of per threadgroup. The paired
candidates isolate this tradeoff using identical work lists and setup commands.
The original CellSerial shared-memory reduction is unchanged. Wave selection is
explicit, guarded by primitive capability probing and actual kernel support; use
portable chunks or CellSerial when unsupported. No wave width or speedup is claimed.

## Correctness and comparison

`GpuSensorChunkedRangeQueryTests` exercises sparse, uniform, 99-percent hotspot and
single-cell fixtures; counts 1, 255, 256, 257, 1025, 4097 and 65541; empty results;
grid/domain boundaries; full-domain and zero-radius queries; segment offsets;
repeated scratch reuse; quantized payloads; frame digests; external CSR tombstones;
zero sample-address count and zero query count; and capacity rejection. Expected
results come from the existing independent brute-force CPU oracle, unchanged by
the backend implementation. All three backends are compared against that oracle.

The dedicated benchmark is an opt-in Windows x64 development Player microbenchmark with
native DX12 timestamps, not an end-to-end frame-latency test. All candidates share one
immutable sample/query/CSR buffer set. Every warmup and measured output is checked
against the CPU oracle outside the timed GPU scope. Six orders put each candidate
in every position twice. Timing includes **all four dispatches per query**, every
work-list reset/build and indirect-argument setup, plus consumption and digest
atomics. Index construction, upload, CPU oracle, synchronous validation readback
and final frame reduction are outside the query-only GPU scope. CPU command
recording time is reported separately. Per-sample synchronization makes this a
latency microbenchmark, not an overlapped throughput claim.

The runner requires the shared Unity/GPU lock script. Use smoke now; retain the
comparison command for the integration task after parameters are frozen:

The native timestamp plugin requires the canonical Unity 6000.5 dictionary-form
`PluginImporter.platformData` metadata from the parallel index task. That shared
metadata fix is intentionally not duplicated in the query commit. Integration
must merge it before native timing. The old metadata can silently disable the
plugin even when its text says Editor/Windows enabled; missing native support is
a hard timing failure, not permission to substitute CPU time.

```powershell
$lock = 'C:/Users/EdwinLiu/Documents/Codex/2026-09-07/w-m/work/r9700-vnext/Invoke-SerializedValidation.ps1'
& ./Tools/Run-GpuSensorQueryBenchmark.ps1 -ValidationLockScript $lock -Mode Smoke
# Future comparison only; requires a clean worktree (no AllowDirty override):
& ./Tools/Run-GpuSensorQueryBenchmark.ps1 -ValidationLockScript $lock -Mode Compare `
    -ElementCounts 4097,65541,262145 -Warmup 10 -Samples 120
```

Smoke fixes N=257, one warmup and two measured samples per order/candidate across
all four distributions. Its output is only harness/correctness evidence. Compare
uses all four distributions and the requested counts. Outputs include exact query
definitions, expected four-word digests, every native GPU/CPU sample, interleaved empty controls, raw ticks/frequency/token/frame/status, dispatch and
scratch accounting, device/API/driver/Unity details, Git state, and SHA256 hashes of
actual query source, fixtures, oracle, native DLL/import settings and built Player files. The runner rejects source
changes during a run and stale output directories. Missing/invalid timestamps,
incomplete candidate cells, duplicate sample identifiers or oracle mismatch fail
the run; no substitute timing is used. `summary.csv` reports median/P95/P99 GPU (including empty controls) and
mean CPU recording time. Short samples cannot support tail-latency conclusions.

Potential regressions to evaluate after integration include four dispatches per
query versus one dispatch for the entire baseline batch, serialized query setup,
empty/sparse-cell work-list overhead, and global atomic pressure in hotspots.
No full R9700 performance matrix is run as part of implementation.

## Implementation validation (2026-09-07)

- Unity 6000.5.2f1, Windows/DX12, AMD Radeon AI PRO R9700.
- Final sensor package regression: **66/66 passed, zero skipped**, including all
  ten new chunk-query cases and both reduction variants.
- Windows Player smoke: **144/144 workload intervals and 144/144 empty controls**
  ready; additionally 72 workload and 72 control warmups completed. All workload
  outputs matched the independent oracle. N=257 only; no performance ranking.
- Summary validation: a synthetic valid report plus seven corruption cases
  (missing/duplicate samples, inconsistent ticks, wrong order/dispatch scope,
  incomplete provenance, dirty comparison) passed. Synthetic values are not GPU
  evidence. The actual smoke also passed the strengthened summary checks.
- Native smoke used the index task's canonical plugin metadata temporarily;
  baseline metadata was restored in this worktree afterward. Shader/runtime
  correctness tests also passed with the restored baseline metadata.
- `measurements.json` retains Unity's graphics device version string in its
  legacy `driver` field. Actual Windows driver package versions are captured by
  `provenance.json.deviceDrivers` in the runner; do not interpret the graphics API
  version string as a driver package number.

Raw local evidence is under `TestResults/query-final` and
`Reports/GpuSensorQuery/player-smoke-v2-20260907` (ignored by Git). This is working
tree smoke evidence with source hashes, not frozen final-commit performance data.
