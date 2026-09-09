# SUMMIT GPU Sensor Pipeline

`com.summit.gpu-sensor-pipeline` records two deterministic producer paths into
one shared GPU-resident spatial consumer:

```text
CPU producer -> CommandBuffer.SetBufferData(samples + keys) --+
                                                             +-> trusted Direct CSR -> range query -> digests
GPU deterministic producer ---------------------------------+
```

The package is scene-independent. It also accepts external snapshots through
full-rebuild and incremental indices, with optional query/compact-view candidates
and a pure `GpuSensorIndexQueryPlanner`. The planner returns an explicit plan;
it does not select a measured winner or change a running pipeline. There is no
automatic production-scene policy or benchmark controller in the package.

## Fixed data contract

The synthetic sensor domain is exactly `65536 x 65536 x 65536` integer units,
partitioned into a `64 x 64 x 64` grid (`262144` bins). Coordinates use the low
16 bits of each axis. The linear key is:

```text
key = (x >> 10) | ((y >> 10) << 6) | ((z >> 10) << 12)
```

Every generated key is therefore in `[0, 262144)`. There are exactly 64 logical
states (`0..63`). Each state applies whole-cell modular translations to a
stateless base sample, preserving the occupancy histogram up to a permutation
of cell identifiers while changing positions and keys.

The GPU sample, query and digest records have fixed 16-byte layouts:

- `GpuSensorSample`: `uint4(x, y, z, payload)`.
- `GpuSensorRangeQuery`: `uint4(centerX, centerY, centerZ, radius)`.
- `GpuSensorQueryDigest`: `uint4(count, xorHash, sumHash0, sumHash1)`.

The query enumerates CSR bins intersecting an inclusive cubic range, fetches
scattered stable IDs, consumes the corresponding sample payload and position,
and filters candidates against exact bounds. Digest reduction uses XOR and
modulo-`uint` sums, so equal-key atomic-scatter order cannot affect correctness.

## Recording

Create the pipeline and initialize stable identity IDs and queries before
recording consumers:

```csharp
using var pipeline = new GpuSensorPipeline(
    elementCapacity,
    queryCapacity,
    GpuPrimitiveBackend.WaveOps,
    emitProfilerMarkers: false);

pipeline.SetStableIds(identityIds);
pipeline.SetQueries(queries);
```

`RecordCpuProduced` records two `CommandBuffer.SetBufferData` commands followed
by the common CSR and query stages. `RecordGpuProduced` records the deterministic
producer dispatch followed by the same common stages. Both use
`GpuDirectSpatialBinner.RecordGuaranteedInRange`; no adaptive policy is involved.

The CPU upload arrays must remain alive and immutable until the submitted
command buffer has completed. Applications with multiple frames in flight
should use persistent staging slots guarded by graphics fences. This package
does not insert CPU waits or own staging memory.

The explicit `WaveOps` backend fails closed when the active device cannot run
the imported wave shader. `Portable` is the cross-platform fallback. `Auto` is
rejected so a measurement cannot silently change algorithms.

## Multi-sensor index reuse

When several consumers query the same dynamic point set, record one producer
and CSR build for all segmented query batches:

```csharp
pipeline.RecordGpuProducedSharedSensorIndex(
    commands,
    seed,
    logicalState,
    elementCount,
    sensorCount,
    queriesPerSensor);
```

Queries are stored sensor-major in the existing query buffer. The method
dispatches one range-query segment per sensor so consumer work and dispatch
shape match `RecordGpuProducedRebuiltPerSensor`, the explicit A/B control that
repeats producer and count/scan/scatter for every sensor. Both paths reduce all
query digests into the same per-state frame digest.

`SpatialIndexResidentBytes` reports the logical key/CSR/scratch contract for
one index. `IndependentSensorSpatialIndexBytes(sensorCount)` is an isolated
deployment projection; it is not driver-reported physical VRAM residency.

## Trusted-key boundary

The trusted Direct path omits per-element range checks. Passing any key outside
`[0, BinCount)` permits out-of-bounds GPU access. The deterministic generator's
integer formula proves the contract, but arbitrary CPU arrays remain the
caller's responsibility. `RecordValidateKeys` is a validation-only pass that
counts and hashes invalid keys; run it outside performance windows before using
new producers. The Direct path's own zero diagnostics do not prove key safety.

## Readback and accounting

Record methods allocate no buffers and request no readback. `QueryDigests`,
`FrameDigest`, `KeyValidationDiagnostics`, and `ComparisonDigest` remain GPU
resident. `FrameDigest` contains 64 structured digest slots; each invocation
writes only its `logicalState` slot, so a complete 64-state block can retain all
per-state hashes without measurement-time readback. Validation code may read
them only outside a measurement window.
Timestamp instrumentation readback must be reported separately from workload
output readback.

`CpuUploadLogicalBytes` and `GpuProducerLogicalWriteBytes` both model 20 bytes
per active element: a 16-byte sample plus a 4-byte key. They are logical payload
counts, not measured PCIe or VRAM traffic. `ResidentBytes` includes all
package-owned buffers plus referenced Direct/primitives scratch, but excludes
driver allocation alignment and shader assets.

## Optional point-chunk queries

The optional `GpuSensorQueryBackend.PointChunks` and `PointChunksWave` candidates
split dense CSR ranges into 256-point work items for cooperative consumption.
`CellSerial` remains the default. The standalone `GpuSensorChunkedRangeQuery`
accepts external CSR buffers, including reserved tombstone slots, and uses a
separate index-entry capacity for bounded scratch. `GpuSensorPipeline.RecordQueries`
records a query segment against the existing index without rebuilding it.
See [query contracts and comparison commands](../../Docs/GPU_SENSOR_QUERY_BACKENDS.md)
for capacities, queue lifetime, fallbacks, native timing scope and validation.

## Incremental index applicability

The [complete-task adoption example](../../Tools/Examples/IndexQueryPlanning/README.md)
calls the real planner on the CPU with labeled synthetic facts and no measurements.
`CellSpans`/`CellSpansWave`, maintained live counts and a separate compact view
are implemented opt-ins; [their contract](../../Docs/SensorCellSpansAndCompactView.md)
describes the exact calls and storage. Their complete-task performance is
**Unmeasured**. The planner defaults to full rebuild plus `CellSerial` with
unavailable work (`-1`), which the example exports as `null`. Its work score is
not milliseconds, and `AdditionalResidentBytes` covers extra view/query scratch
only, not all index, input/output or concurrently retained storage.

Full-rebuild consumers use the original `inputSamples`; incremental and
compact-view consumers use `index.Samples` with the matching offsets/IDs. Keep
the stable-ID address bound at capacity even when active IDs are sparse. After
bypassing incremental maintenance with a full rebuild, mark its state invalid
until an explicit forced incremental update refreshes it. A compact view must
refresh after membership changes and remain alive through its consumers.
See [snapshot ownership and complete costs](../../Docs/WHOLE_TASK_DECISIONS.md).

The incremental index remains an explicit opt-in; defaults and public APIs are
unchanged. The September 2026 R9700 cost audit does **not recommend it for the
measured N262144 hotspot trajectory with CellSerial, or the measured streaming
trajectory with BatchedPointScanWave**. In the former, reserved-CSR consumer
cost outweighs maintenance savings. In the latter, every measured update falls
back to a reserved rebuild and the consumer also scans a more expensive
representation. Low change percentage alone does not imply an index benefit.

The earlier hotspot experiment with the new query had an inconclusive frame
result; this is not a claim that every hotspot consumer suffers a stable loss.
Any other use must validate complete maintenance plus consumer cost, capacity
fallback frequency and CSR extent, not maintenance time alone. `HolesWord`
counts removals since rebuild and does not measure all reserved Invalid slots.
See [the scoped evidence and diagnostic limitations](../../Docs/FocusedIndexCosts.md)
and [the external snapshot contract](../../Docs/GPU_SENSOR_INCREMENTAL_INDEX.md).

The subsequent [capacity mechanism replay](../../Docs/CausalIndexCosts.md)
matched the original GPU state history and screened one minimal policy: reserve
one slot in an empty cell. Streaming still rebuilt on all 320 steady-state
updates because some empty destinations received two inserts in one update;
logical consumer reads also increased. The candidate was rejected before GPU
testing, with no new performance claim or production change. This does not
rule out other capacity or compact-consumer designs, which must include their
complete maintenance, conversion and memory costs.
