# Engineering case: maintenance savings can increase task cost

SUMMIT's main engineering question is how to represent and consume changing
GPU-resident data so the **complete task** is worthwhile. The reusable packages
serve that question. An individual kernel improvement is supporting evidence.

## The observed failure and implemented response

In the retained [focused index comparison](FocusedIndexCosts.md), hotspot
maintenance fell from 0.339788 to 0.325407 ms, while the old CellSerial query grew
from 50.829039 to 58.234276 ms. These are the N262144 hotspot trajectory's native
GPU interval means across the original five processes and balanced blocks,
not whole-engine GPU or presentation times. Those historical observations
explain why choosing on maintenance alone can make the consumer worse;
they do not prove every hotspot or current candidate is slower.

The current implementation adds spatially pruned CellSpans queries, optional
maintained live counts and a compact consumer view. Its planner exposes
maintenance, conversion, query work and additional memory together. Its units
are structural estimates, not predicted milliseconds. The implementations and
CPU contracts are available; complete GPU/scene benefits remain **Unmeasured**.
The separate [competitive query comparison](../PublicBenchmarks/UnityGpuIntegration/RESULTS-query-boundary-2026-09-08.md)
also retains an index-free parallel scan baseline and unproven extra user benefit.

## Try the decision interface before integrating the GPU path

```powershell
dotnet run --project Tools/Examples/IndexQueryPlanning/IndexQueryPlanning.csproj -c Release
```

The [compilable example](../Tools/Examples/IndexQueryPlanning/README.md) exercises
the real pure API with explicitly synthetic facts. It demonstrates default
behavior, opt-in planning, missing query facts and a known capacity failure.
It performs no measurements. Use it to understand the returned reasons and
cost components, not to choose a faster algorithm from illustrative inputs.
Unknown maintenance/query/total work is exported as JSON `null`, preserving the
API's `-1` sentinel. Zero conversion and additional storage mean no extra view or
scratch is selected, not free maintenance or zero total GPU memory.

## Apply a plan to one immutable snapshot

1. Supply actual capacity, live membership, query structure, consumer count and
   available memory. If query visits are unknown, leave
   `HasExactQueryStructure=false`; do not collect a hidden readback to guess them.
   `InspectedSlotCount` is the slots inspected during maintenance, not changed
   memberships. All dynamic slots are inspected; static slots also refresh on
   first use, `staticRevision` change or a forced rebuild. Candidate visits are
   CSR entries before exact filtering, not matches. `ConsumerPasses` repeats one
   query segment on the same snapshot and multiplies only query work.
2. For full rebuild, record `GpuSensorFullRebuildIndex.RecordUpdate` directly.
   A known capacity failure must bypass the failing incremental update.
3. For incremental maintenance, use the incremental snapshot's samples and IDs.
   After bypassing it with a full rebuild, set `IncrementalStateValid=false`.
   Resume only after `index.RecordUpdate(..., forceRebuild: true)` refreshes its
   state with queue ordering/fences honored. That operation rebuilds the reserved
   index; it does not build the separate compact view.
4. For a compact view, enable maintained live counts and refresh the view after
   membership changes. Charge its construction and storage; reuse it only while
   that snapshot remains unchanged.
5. Record the chosen consumer against that snapshot. Keep source, view and
   scratch buffers alive until all submitted consumers finish. Cross-queue
   reuse needs an explicit fence.

The selected representation determines which sample buffer belongs to the IDs:

| Plan mode | Samples for the consumer | CSR offsets and IDs |
| --- | --- | --- |
| `FullRebuild` | Original `inputSamples` used by `full.RecordUpdate` | `full.BinOffsets`, `full.BinnedIds` |
| `Incremental` | Updated `index.Samples` | `index.BinOffsets`, `index.BinnedIds` |
| `IncrementalCompactView` | Updated `index.Samples` | `view.BinOffsets`, `view.BinnedIds` |

Use `Capacity`, not `ActiveCount`, as the stable-ID address bound: live IDs may
be sparse and near the end of the sample buffer. Never combine a different
snapshot's IDs and payloads. A reserved-CSR consumer must accommodate the actual
`index.BinnedIds.count`, which can exceed sample capacity. Full rebuild keeps the
original input alive and immutable through all consumers. Incremental maintenance
requires its inputs through the update, and its owned sample snapshot plus any
view through the consumers. Payload changes on static slots require a revised
`staticRevision`, even if cell membership did not change.

`AdditionalResidentBytes` budgets only extra query scratch and the compact view.
Charge the input/output buffers, source indices and simultaneously retained
backends separately. The planner's total covers its modeled maintenance,
conversion and query terms, not upload, synchronization or application use.

The existing [recording example and API contract](SensorCellSpansAndCompactView.md)
shows the concrete index/view/query calls. Planning never submits them for you.

## What makes a complete result

Compare equivalent inputs and **fully consumed outputs**, including conversion,
maintenance, traversal, predicate evaluation, result materialization and required
synchronization. Keep CPU submission, GPU queue completion, engine frames and
presentation observations distinct; unavailable values remain unavailable.

[Cabana and ArborX adapters](../PublicBenchmarks/External/README.md) preserve their
own generators, predicates and full output contracts. [Official ECS integration](../PublicBenchmarks/External/README.md)
adds a real scene consumer. An upstream benchmark, an adapted implementation and
an application scene must retain distinct identities.

The next evidence milestone is a reproduced complete-task comparison with an
explicit fallback and memory budget. The historical NYCGIS no-copy result and
HLSL's native kernel results cannot satisfy that milestone for this source.
