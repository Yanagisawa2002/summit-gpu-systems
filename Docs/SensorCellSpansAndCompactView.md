# Cell spans, compact consumer views and explicit index planning

Status: **Unmeasured**. This change implements candidates and deterministic CPU
checks. No GPU workload, Unity Editor/Player, benchmark, calibration, profiling,
hardware counter collection or performance comparison was executed. `CellSerial`
remains the pipeline default, and incremental execution retains its existing
default. Source compatibility is retained by appending optional parameters;
precompiled consumers of the incremental constructor must rebuild.

The baseline is `63c5bfd6fdb7c44516cbc8c4a70cb95c28013cf1`. After fetching origin,
`origin/main` matched that revision. Existing main and vNext index/query/adaptive
worktrees were inspected; their already-integrated algorithms were retained.
The historical [focused](FocusedIndexCosts.md) and [capacity replay](CausalIndexCosts.md)
reports are unchanged. Their timings do not describe these new source files.
The rejected one-slot empty-cell reserve policy is not reintroduced.

## Spatial pruning with bounded work per group

`GpuSensorQueryBackend.CellSpans` and `CellSpansWave` are explicit options in
`GpuSensorPipeline`. The standalone `GpuSensorCellSpanQuery` accepts the same
samples/CSR/query/digest arguments as `GpuSensorChunkedRangeQuery`. Both support
reserved CSR tombstones, sparse stable IDs, intensity quantization, segment
offsets and repeated ordered scratch reuse. Their exact predicate remains the
inclusive clamped uint16 **AABB**, not a sphere or kNN query.

In the existing 64 x 64 x 64 grid, x varies fastest. Every candidate x-cell range
in a fixed y/z row is therefore a single CSR interval. A query needs at most
4,096 such spans. The three recorded dispatches per query are:

1. `BuildSpans`: 16 groups build span descriptors, scan their chunk counts within
   each 256-row block and clear the selected output digest. Empty rows write zero
   descriptors, including padding from a previously larger query.
2. `PrepareSpanDispatch`: scan 16 block totals and write indirect dispatch shape.
3. `ConsumeSpans`: two upper-bound searches map each group to a span and its
   256-member chunk. Repeated prefix values skip empty spans/blocks. Each group
   handles at most 256 CSR entries, including holes. A hotspot contributes an
   integer chunk count during setup, avoiding a serial per-chunk descriptor loop.

Candidate cells are spatially pruned before membership/sample access. Adjacent
x-cells share chunks, so many short cells do not each need an underfilled group.
The portable reduction uses group shared memory; the explicit wave variant uses
native-width wave operations without assuming Wave32/64. Final filtering and all
four digest words retain the existing semantics and uint wraparound behavior.

For a reserved entry limit `S`, the sum of row chunk counts is bounded by
`floor(S / 256) + min(S, 4096)`. Spans within one query are disjoint. Indirect groups
use x/y dimensions above 65,535 groups; padded groups contribute nothing. An empty
query still records one guarded consume group after clearing the digest. Zero
query count records no commands; zero sample-address limit yields empty digests.

Scratch is exactly `4096*16 + 17*4 + 12 = 65,616` logical bytes, independent of
sample, CSR-entry and query capacities. This includes the span directory, block
prefixes and indirect arguments. `QueryScratchBytes` and `ResidentBytes` include
it. Extra descriptor/search/reduction work can outweigh pruning, especially for
small or broad query batches. No runtime benefit is inferred from the smaller
directory or the 256-entry work bound.

The trusted CSR requirements remain: monotonic offsets starting at zero and
terminating within the ID buffer; each valid stable ID appears once in its true
cell; samples and query centers/radii fit uint16; all buffers remain distinct,
alive and ordered. IDs at or above the sample-address limit are tombstones.
Malformed GPU CSR contents are outside this API's contract. CPU buffer validation
runs before recording any commands and rejects entry capacity or shape errors.

`GpuSensorCellSpanLayout.GetSpanCount(query)` and
`GetSpanCells(query, span, out firstCell, out endCell)` expose the pure integer
row mapping. The candidate ID range is `[offsets[firstCell], offsets[endCell])`.
This is a candidate interval, not a list of final predicate matches. External
float sphere adapters must retain original coordinates/IDs, use conservative
per-axis quantized bounds, apply the original sphere predicate, and produce their
required complete CSR output. This digest API alone is not such an adapter.

## Live counts and a separate compact view

The incremental index's count buffer previously described the last rebuild.
`maintainLiveCounts: true` now explicitly keeps it current: removals decrement
their old cell, insertions increment their new cell, and rebuilds recount all
valid keys. All removals precede all insertions in distinct dispatches. This works
in `Original` and `GpuDriven`, including skipped unchanged membership, static
revision refresh, inactive/invalid slots, ID reuse, force and capacity rebuilds.
Payload-only changes still update samples even when membership stays unchanged.
No new count buffer is allocated; the optional upkeep adds one atomic per removed
or inserted membership. With the option disabled, the previous count semantics
and reserve policy remain in effect.

`GpuSensorCompactIndexView.Record(commands, index)` uses four additional
dispatches: scan live cell counts, parallel scan of 1,024 block totals, finalize
offsets/reset heads, and scatter current stable-slot keys. It reads `N` keys and
does not walk the reserved extent. Each live ID is written exactly once; the
terminal compact offset is the active count. Tail storage beyond this offset is
not cleared and must never be consumed. A never-updated index produces an empty
view. An index without the live-count option, a disposed index or a capacity
mismatch is rejected before command recording.

The extra owned storage is `4*(N + 2*262144 + 1 + 1024)` bytes: compact IDs, compact
offsets, scatter heads and block offsets. At N=262144 this is 3,149,828 bytes,
excluding source index storage and query scratch. The current sample snapshot
remains `index.Samples`; the view does not duplicate payloads. An ordinary
refresh performs four dispatches and the full grid scan even if few IDs changed.
Constructing a view is not a free conversion and does not repair reserved index
capacity or recycle tombstones.

```csharp
using var index = new GpuSensorIncrementalIndex(capacity,
    executionMode: GpuSensorIndexExecutionMode.GpuDriven,
    maintainLiveCounts: true);
using var view = new GpuSensorCompactIndexView(capacity);
using var consumer = new GpuSensorPipeline(capacity, queryCapacity,
    backend: GpuPrimitiveBackend.Portable, emitProfilerMarkers: false,
    queryBackend: GpuSensorQueryBackend.CellSpans);
consumer.SetQueries(queries);

// Recording order on one queue; these calls do not submit or wait for GPU work.
index.RecordUpdate(commands, inputSamples, activeSlots, staticRevision);
view.Record(commands, index);
consumer.RecordExternalIndexQueriesOnly(commands, index.Samples,
    view.BinOffsets, view.BinnedIds, capacity, queries.Length);
```

The view must be refreshed after membership changes before any consumer uses it.
There is no hidden dirty tracking or freshness readback. Reuse its output across
several consumers of the same immutable snapshot to amortize construction.
Concurrent queues require separate owners or explicit fences before reuse;
dispose only after prior GPU consumers complete.

## Conservative planner and complete costs

`GpuSensorIndexQueryPlanner.Select` is a pure planning API. It does not probe a
device, record commands, collect counters or mutate an existing pipeline. The
caller must supply already-known structural facts for one snapshot and query
segment. Unknown facts must use `HasExactQueryStructure=false`; no timing or
readback is needed to fall back safely. `allowUnmeasuredCandidates` defaults to
false, yielding full rebuild plus `CellSerial` and unavailable work (`-1`).

The opt-in planner returns `FullRebuild`, `Incremental` or
`IncrementalCompactView`, an explicit query backend, a reason and separate
maintenance/conversion/query work fields. All results report `Unmeasured`.
Missing valid incremental state, a forced rebuild, or known capacity fallback
selects the compact full-rebuild path. The caller must then use
`GpuSensorFullRebuildIndex.RecordUpdate` directly, rather than first paying a
failing reserved update and then another reconstruction.

After bypassing incremental updates, mark `IncrementalStateValid=false` until an
explicit forced incremental rebuild refreshes its state. Sample ownership follows
the selected producer: full rebuild queries `inputSamples`, incremental queries
`index.Samples`. Never substitute one snapshot's IDs with another one's payloads.
`CapacityFallbackKnown` must describe trustworthy existing knowledge; collecting
GPU diagnostics for this planner was neither necessary nor performed here.

The score is a **structural surrogate**, not time, bytes transferred, hardware
instructions, an autotuned profile or a measured recommendation. The fixed units
make the tradeoffs reviewable; they cannot establish an algorithm ranking. With
N slots, A active slots, I inspected slots, C changed memberships and B=262144:

| Term | Surrogate units |
|---|---|
| Full maintenance | `10N + 8B + 4A` |
| Incremental maintenance | `3N + 9I + 8C` |
| Optional conversion and live-count upkeep | `N + 6B + 3A + 3B/256 + 1 + 4C` |
| Span query, Q queries and V candidate entries | `4096Q + 2R + 2V + 12K` |
| Batched query over configured capacity S, extent E | `E + A + roundUp(S,256)*Q` |

R is the sum of candidate row counts; K uses the conservative aggregate chunk
bound `floor(V/256) + min(V,R)`. The batch term accounts for predicates on padded
dispatch lanes even when they fail the CSR extent guard. These formulas combine
different source-level operations into fixed surrogate units. They omit hardware
latencies, cache effects, contention, lane duplication, scheduling and compilation
choices, and are not upper/lower bounds on device time or actual traffic.

`ConsumerPasses` multiplies only query work. A compact view is constructed once
per snapshot. The planner first compares available query forms for each index
representation, then compares complete maintenance+conversion+query totals.
Changing index strategy requires a default 25% surrogate reduction versus full
rebuild, configurable as an engineering margin, not a performance confidence
interval. If a compact conversion does not pay for itself in the model it is not
selected. A known capacity failure overrides a favorable score.

`additionalMemoryBudgetBytes` covers only the extra view and query scratch.
Caller-owned input, output, full/incremental indices and simultaneously retained
backends must be budgeted separately through their existing `ResidentBytes`.
Without scratch budget or wave support, selection stays with the available
`CellSerial` form. No support or memory fallback allocates hidden buffers.

## Non-performance validation

The dedicated `Tools/SensorQueryFunctional` projects are allowlisted separately
from existing GPU and benchmark suites:

```powershell
dotnet run --project Tools/SensorQueryFunctional/SensorQueryFunctional.csproj -c Release --no-launch-profile
dotnet run --project Tools/SensorQueryFunctional/SensorRecordingFunctional.csproj -c Release --no-launch-profile
dotnet build Tools/SensorQueryFunctional/SensorRuntimeCompile.csproj -c Release `
  '-p:UnityManagedPath=C:/Program Files/Unity/Hub/Editor/6000.5.2f1/Editor/Data/Managed/UnityEngine'
& ./Tools/SensorQueryFunctional/Compile-Shaders.ps1 `
  -FxcPath 'C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/fxc.exe' `
  -DxcPath 'C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/dxc.exe'
```

The CPU model covers cell/domain boundaries, empty rows/results, sparse and
single-cell/hotspot membership, 1/255/256/257/1025/4097 sample counts, duplicate
prefixes, the 65,535-group boundary, tombstones, exact ID sets, wraparound digests,
quantization, live-count transitions, parallel block-prefix logic, static
revisions, invalid inputs, removal/reuse and planner fallbacks/resource budgets.
Expected digests use the existing independent brute-force oracle; its historical
filename contains `Benchmark`, but the compiled file contains only pure functions.
It does not start a benchmark. Synthetic cases here are correctness fixtures,
not external benchmark workloads or performance evidence.

The recording project compiles the actual new hosts and incremental host against
in-memory command/buffer doubles with **no Unity/native reference**. It checks
zero-query behavior, selected segments, scratch reuse parameters, bindings,
capacity/alias rejection before partial recording, disposal and explicit gates.
The separate compile-only library builds the full sensor/direct-binning/primitive
runtime against real Unity managed assemblies, checking actual API signatures.

Validation on 2026-09-08: CPU model and recording suites passed; the real-Unity
compile-only library passed with zero warnings/errors; all 36 shader entry points
compiled with warnings as errors (portable FXC cs_5_0 and wave DXC cs_6_0). The
shader script strips only Unity importer pragmas into ignored build artifacts;
kernel bodies/defines/includes remain unchanged. Artifacts are under
`Artifacts/SensorQueryFunctional`, intermediates under the tool's ignored `obj`.
These checks do not validate GPU execution, atomics/barriers on a device, Unity
shader import, native wave behavior or end-to-end performance. Those remain
unexecuted and require separate authorization.

## Algorithm references and external scope

The prefix-scan approach follows the general scan construction described in
[GPU Gems 3, chapter 39](https://developer.nvidia.com/gpugems/gpugems3/part-vi-gpu-computing/chapter-39-parallel-prefix-sum-scan-cuda).
The span directory, CSR consumer, live-count maintenance and hosts here are
original implementations for this repository; no third-party source was copied
or vendored, and the repository's existing license remains in force.

[Cabana's native benchmark catalog](https://github.com/ECP-copa/Cabana/wiki/3-Benchmarks)
and [ArborX's BVH driver](https://github.com/arborx/ArborX/tree/master/benchmarks/bvh_driver)
were checked as external references. They are not substituted by these CPU
fixtures. Exact upstream revision/license locks, unchanged native workload
preparation and the sphere/full-CSR adapter belong to the separate integration
change. This query change makes no claim of sphere/kNN parity or new external
benchmark results.
