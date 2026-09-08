# Optional incremental sensor index

This candidate is opt-in. Existing `GpuSensorPipeline` producer/rebuild APIs and
query distribution/reduction are unchanged. No R9700 performance default is
promoted. The initial smoke verifies executable timing and correctness only.

The [September 2026 focused cost audit](FocusedIndexCosts.md) supersedes any
general performance expectation for this candidate. Do not recommend it for
the measured N262144 hotspot/CellSerial or streaming/BatchedPointScanWave
trajectories: representation cost dominates the former, and every measured
streaming update takes the capacity fallback. The hotspot with the new query
remains an inconclusive frame result, not a proven loss for every consumer.
The API is retained for explicit, workload-validated use; defaults are unchanged.

## External GPU snapshot contract

`GpuSensorIncrementalIndex(capacity, staticSlotCount, churnPermille,
fragmentationPermille)` owns a fixed allocation. `RecordUpdate(commands,
inputSamples, activeSlots, staticRevision, forceRebuild)` accepts application GPU
buffers; it does not depend on the deterministic fixture generator.

* `inputSamples`: structured 16-byte `uint4(x,y,z,payload)`, at least `capacity`
  slots. Coordinates occupy the inclusive integer domain `[0,65535]`; payload is
  an unrestricted uint. Cell key is `x>>10 | (y>>10)<<6 | (z>>10)<<12`.
* `activeSlots`: structured 4-byte uint per slot. Zero removes/excludes a slot,
  one makes it live. Other flags or out-of-domain active coordinates are safely
  excluded and counted in `InvalidInputWord` when inspected. Inactive payload
  contents need not be valid coordinates. Invalid static inputs remain excluded
  until the next static refresh; diagnostics describe this update's inspections.
* Stable ID is the **slot index**, not a compacted active-row number. IDs may be
  sparse, removed and later reactivated. The highest slot remains visible when
  every lower slot is absent. Applications with arbitrary object identifiers
  keep an object-to-slot mapping at ingestion; arbitrary uint IDs are not accepted
  as payload addresses. Reusing a slot for a different object reuses its digest
  identity; generation-tagged object identity is outside this API.
* `[0,staticSlotCount)` is the static partition, inspected/copied on first use,
  on `staticRevision` inequality, or on forced rebuild. Change the revision for
  **any** static payload, position, or activity change. The remainder is dynamic
  and inspected every update. Revision wrap is fine if consecutive submissions
  have different values; equal revisions assert equal static snapshots.
* Every inspected sample is copied even if its cell is unchanged. Same-cell
  movement and payload changes therefore reach consumers immediately. Static
  samples remain GPU resident between revisions.
* Capacity and static partition are immutable. To grow or repartition, allocate
  another index, submit a full snapshot to it, and switch after producer/consumer
  fences; dispose the old index only after its last consumer completes. No
  hidden CPU allocation, resize, readback, or dirty-state tracking occurs in
  `RecordUpdate`.

Producer, update, and all consumers must execute in recorded order on the same
queue, or use explicit cross-queue fences supplied by the application. Buffers
must stay alive; input snapshots must stay immutable until the update completes.
Do not mutate/dispose index outputs before all their consumers complete. Multiple
recorded updates and command-buffer replay work because initialization, revisions,
decisions, and counters reside on the GPU. Submission order determines state;
CPU recording order alone does not. No concurrent updates to the same index.

```csharp
// Setup outside the frame loop. Existing pipeline query backend remains default.
var index = new GpuSensorIncrementalIndex(slotCapacity, staticSlotCount);
var consumer = new GpuSensorPipeline(slotCapacity, queries.Length,
    GpuPrimitiveBackend.Portable);
consumer.SetQueries(queries);

// The application records its GPU producer before this update.
index.RecordUpdate(commands, gpuSamples, gpuActiveSlots, staticRevision);
consumer.RecordExternalIndexQueries(commands, index.Samples, index.BinOffsets,
    index.BinnedIds, index.Capacity, queries.Length, logicalState: 0);
// Submit, then retain all resources through completion.
```

`RecordExternalIndexQueries` is the only pipeline hook. It accepts structured
sample/offset/member buffers, configured queries, a stable-slot bound and a
logical digest state `[0,63]`. It does not require `SetStableIds`. External CSR
contents are a trusted producer contract: offsets start at zero, are monotonic,
have `262145` entries, and terminate at or below `binnedIds.count`. The query
skips IDs at or above `stableIdCapacity`; pass **Capacity, never active count**.
Chunk consumers must allocate for the CSR extent including holes, not the live
count. To combine this index with `PointChunks` or `PointChunksWave`, construct
the pipeline with `queryIndexEntryCapacity: 3 * slotCapacity` and the chosen
`queryBackend`. All three backends route the external buffers through
`RecordExternalIndexQueries`; insufficient reserved capacity rejects before
recording commands. The combined integration tests cover sparse stable IDs,
tombstones, payload updates and rebuild transitions against independent oracles.

## Membership reuse and rebuilding

CSR cells reserve `count + ceil(count/2) + 1` words when nonempty, zero otherwise.
The terminal offset is at most `3*activeCount <= 3*capacity`, with a fixed
`3*capacity` member allocation. Empty/reserved slots contain `uint.MaxValue` and
are skipped by the unchanged consumer. Equal-cell updates leave their member
positions unchanged, byte for byte. Static and dynamic members share the output
CSR so existing consumers need no second query or digest-combine pass; their
inspection/copy paths are separated by the static partition contract.

GPU detection reserves append positions atomically in destination cells and
counts changes. Before writing members, one GPU decision selects either complete
rebuild or remove/insert. Separate dispatches order removals before insertions.
Append positions never reuse tombstones until compaction. No insertion can write
out of bounds: insufficient destination range triggers rebuild before writes.
First insertion into an empty cell also rebuilds. Changed/dead members are removed
using an ID-to-member-position map; unchanged members are never scattered again.

Explicit rebuild reasons are a bitmask in `RebuildReasonWord`:

| Bit | Reason | Trigger |
|---|---|---|
| 1 | Initial | First executed update |
| 2 | Forced | `forceRebuild=true`, including static refresh |
| 4 | Churn | Changed membership slots exceed `floor(capacity*churnPermille/1000)` |
| 8 | Fragmentation | Accumulated vacated members plus this update's removals exceed `floor(capacity*fragmentationPermille/1000)` |
| 16 | CellCapacity | At least one destination reservation exceeds its cell segment |

Defaults are 200 permille churn and 250 permille fragmentation. Equality stays
incremental. Thresholds are explicit safety/performance policy, **not calibrated
R9700 optima**. Slots changing cell count once toward churn and once toward both
removed/inserted counts. Fragmentation counts vacated slots, excluding intentional
initial slack. Thresholds are relative to allocated stable-slot capacity.

Rebuild counts live keys from scratch, scans padded cell sizes, clears the new
CSR extent, and scatters all live slots. Its scan uses 256-thread local scans,
a bounded serial scan of 1024 block sums, and an offset-add pass. The no-rebuild
path still dispatches the fallback kernels, which early exit from a GPU decision;
their dispatch/check cost is included. No indirect dispatch or zero-cost fallback
claim is made. Worst-case concentration remains correct but atomics, padding,
and the unchanged serial-per-cell query can be slow. New-empty-cell insertions
and sustained churn can rebuild on every frame.

`GpuSensorFullRebuildIndex` is a separate reference/fallback implementation. It
derives all external GPU snapshot keys each update, uses the existing untrusted-key
`GpuDirectSpatialBinner.Record` count/scan/scatter path, and outputs compact CSR.
Query its CSR against the original input sample buffer. It has the same stable
slot and validity contract but no static revision shortcut. Applications can
switch consumers to this class at any ordered boundary. Switching back with a
forced incremental rebuild refreshes all retained state. The original pipeline
full rebuild APIs also remain available for their existing dense input contract.

## Memory and diagnostics

For `N=capacity`, `B=262144`, incremental owned logical GPU bytes are
`44*N + 4*(3*B + 1 + B/256 + 16)`: retained samples (16N), previous/next keys,
positions and reservations (16N), members (12N), offsets, heads, counts, block
sums and 16 state words. It allocates no primitive scratch. This includes the
retained payload copy, which the full reference reads directly from input.

The full reference owns `12*N + 4*(2*B+3) + binner.ScratchBytes`. Both arms also
need external snapshot buffers (20N). The harness reports common consumer bytes
separately; its `GpuSensorPipeline` owns unused legacy index resources too, so
these are real harness allocations, not a minimal specialized query consumer.
Byte metrics are logical buffer payloads, not driver-reported VRAM residency or
observed memory traffic. Incremental-minus-reference overhead may be negative
at small N because the reference's general primitives allocate more scratch.

Diagnostics expose membership changes, removals, insertions, reservation
overflows, malformed inspected inputs, active count, CSR extent, actual holes,
rebuild/incremental totals, reused members and inspected slots. Totals are uint
wrapping counters. Diagnostic readback is optional and is never required by the
runtime decision path. Full-reference diagnostics count inactive/invalid-key
exclusions together according to the existing Direct binner contract.

## Correctness and comparison

`GpuSensorIncrementalIndexTests` compares all query digests against both the
independent CPU oracle and full Direct rebuild. It verifies the CSR membership
itself (monotonic offsets, bounded extent, exactly-once IDs and correct cells),
the 0/1/5/20/100 percent change/crossing matrix, static-heavy scenes, same-cell
payload changes, sparse/high IDs, removal/reactivation, empty scenes, malformed
inputs, teleports, concentrated cells, reservation exhaustion, exact threshold
boundaries, multi-block scans, forced transitions, and command replay.

`GpuSensorIndexUpdateTrace` is solely a fixture. Change percentage applies to
dynamic slots; crossing percentage applies to the changed subset. Counts are
floored and copied into report configuration. Normal crossings toggle adjacent
x cells; teleports toggle the domain half. Static-heavy fixtures retain 90%
static slots. The matrix includes N=262144 and N=1048576, every 5x5 rate pair,
plus static-heavy, hotspot, teleport/transition and lifecycle cases.

The comparison harness uses native DX12 timestamps around total, index, and
consumer scopes, recording raw ticks, frequencies and tokens. The index scope
includes dirty detection and **all** maintenance/fallback dispatch costs; query
scope includes frame digest. Total also includes nested timestamp callback
overhead; total need not equal the sum of inner scopes. Empty controls are
reported without subtraction. CPU record and submission time, allocated memory,
fallback reasons, and all output digests are reported. CPU snapshot generation,
uploads, native result draining and CPU-oracle readback are outside GPU intervals.
It serializes one snapshot at a time; this is an Editor comparison, not an
application-throughput, upload, async, frame-latency or formal Player claim.

`Tools/Run-GpuSensorIndexComparison.ps1` runs one smoke pair by default. Matrix
mode requires a clean commit and even AB/BA rounds. It checks all paired outputs
and source/native-payload hash stability. The summary preserves positive or
negative results and reports nearest-rank P99 without promoting a winner. The
smoke's two measured frames per arm are insufficient performance evidence.
Formal frozen Player evaluation and integration query-backend comparisons remain
future work; the prepared matrix is explicitly `editor-comparison-unpromoted`.

All Unity/GPU commands for the R9700 vNext task must use the shared lock:

```powershell
$repo = 'C:/Users/EdwinLiu/Downloads/SUMMIT-gpu-r9700-vnext-index'
$lock = 'C:/Users/EdwinLiu/Documents/Codex/2026-09-07/w-m/work/r9700-vnext/Invoke-SerializedValidation.ps1'
& $lock -Action {
    & "$repo/Tools/Run-UnityEditModeTests.ps1" -ProjectPath $repo `
        -TestFilter 'Summit.GpuSensorPipeline.Tests' -UseGraphics -ForceDirect3D12
}
& $lock -Action { & "$repo/Tools/Run-GpuSensorIndexComparison.ps1" -Mode smoke }
# Prepared only: run after integration when performance evaluation is requested.
& $lock -Action {
    & "$repo/Tools/Run-GpuSensorIndexComparison.ps1" -Mode matrix `
        -SampleFrames 60 -WarmupFrames 12 -Rounds 4
}
```

The timestamp DLL metadata uses Unity 6000.5's `serializedVersion: 3` platform
dictionary. The old list encoding silently disabled Editor native loading.
Windows x64 Editor and Win64 Player are enabled; other targets are disabled.
The DLL bytes and native ABI are unchanged.

## Empty-cell capacity screening

The [fixed-trace causal audit](CausalIndexCosts.md) reproduces the published GPU
state and separates zero-capacity insertion from occupied-cell exhaustion.
A single modeled policy reserving one word in every empty cell did not pass
the frozen engineering screen: streaming still rebuilt on every steady-state
frame, while its CSR extent increased. It was not added to the runtime or
measured on GPU. The existing opt-in policy, default and APIs remain unchanged.
The audit also accounts for compact-consumer construction needs and explains
why logical range visits do not predict GPU time.
