# Residency streaming and plan ownership

This is an application-level cache in ordinary Unity `GraphicsBuffer` allocations.
It does not implement sparse resources, reserved/tiled resources, disk I/O, driver
memory-budget callbacks, compression, or a dedicated copy queue.

## Policies and complexity

`PersistentLru` retains full physical-slot victim scans as the comparison baseline.
`PersistentHeapLru` is an opt-in candidate: a free-slot stack serves empty slots in
O(1), and an indexed min-heap serves deterministic LRU victims in O(log slots).
Both break equal last-use frame ties by physical slot index. Pins remove entries
from the eligible heap; retirement reinserts them. No physical-slot scan occurs
per miss in the heap policy. `RebuildVisibleSet` remains available and deliberately
invalidates/reuploads its admitted subset; it requires previous leases retired.
The existing GPU benchmark keeps its rebuild/scan defaults until formal evaluation.

Interest admission costs O(requests log requests) using reusable in-place heapsort.
It orders by `lastServiceOrFirstInterestFrame - priority * 8 + (prefetch ? 4096 : 0)`,
then virtual page ID. Priority is 0..255. Waiting interests retain their age;
serviced pages reset it. Under a continuously present finite interest set, increasing
frame IDs, positive budgets, and eventually retired consumers, lower-priority pages
make progress. A finite priority or prefetch advantage can delay service for thousands
of frame-ID units. There is no real-time guarantee under blocked consumers or zero
budget. Prefetch normally yields to demand, but aging can eventually admit it.

Only the best `physicalSlotCount` interests are admitted per frame. Their resident
hits are protected before missing pages are serviced. When demand fits, current hits
are never evicted. When demand exceeds capacity, aged admission rotates service;
unadmitted interests can be evicted even if they appear in this frame's interest
snapshot. A request remains explicitly unavailable unless its slot is protected by
this plan. A hit in the pre-planning mapping is not a guarantee of admission.

## Request and budget contract

```csharp
var planner = new GpuPageResidencyPlanner(65536, 4096, 8192,
    GpuResidencyPolicy.PersistentHeapLru, maximumInFlightFrames: 3);
var interest = new[] {
    new GpuPageRequest(40, priority: 8),
    new GpuPageRequest(41, prefetch: true)
};
GpuResidencyFramePlan plan = planner.PlanFrame(interest, frameId: 100L,
    uploadBudgetPages: 32);
```

The interest array is a complete snapshot; omit a pending interest to cancel it.
The planner copies it before returning. Demand and prefetch share one bounded
capacity, and duplicate pages across either class are rejected. Empty snapshots
and zero budgets are valid. Invalid pages/priorities, duplicates, invalid frame IDs,
and exhausted lease pools fail before residency/aging/frame state changes, so the
same frame ID can be retried after correcting the cause. Successful frame IDs must
strictly increase; successful calls are not idempotent retries. The long overload
reserves the final 8192 values for safe priority arithmetic; reset only when no leases
are live. The int overload remains, with an unlimited-by-budget physical-capacity
upload limit. It now supports empty and oversubscribed demand.

Budgets are pages, including prefetch uploads. To enforce a payload-byte ceiling,
use `min(pageLimit, byteLimit / (pointsPerPage * 16L))` before planning. Descriptor,
delta, and request-snapshot traffic is separate; metrics include those bytes. Deferred
pages consume no payload upload and receive no fabricated resident mapping.

`HitCount`/`MissCount` describe demand mappings at plan entry (rebuild counts all as
misses). `AvailableCount`/`DeferredDemandCount` describe demand admitted and protected
by this plan. `PendingCount` counts nonresident demand plus prefetch interests after
planning. A deferred but still mapped unadmitted page is not pending upload yet.
`TotalServiceLatencyFrames`/`MaximumServiceLatencyFrames` measure completed uploads'
wait since first missing interest or eviction, using caller frame-ID units. Report
pending/deferred counts alongside latency: unresolved waits are censored, not zero.

## Plan leases and GPU ordering

Plans and their arrays are allocated once at construction, one independent set per
in-flight lease. Successful steady-state planning and retirement allocate no managed
memory. Use `RequestedCount`, `UploadCount`, and `DeltaCount`; array lengths are
capacities. Arrays are read-only to callers. Plans are valid only until retirement,
and a retired plan object can be recycled. Do not retain it, read its arrays later,
or call completion through a stale reference. The planner is single-threaded.

`PhysicalSlotForVirtualPage` exposes a **scheduled** CPU mapping, not evidence of a
completed GPU upload. `RequestedPhysicalSlots` is the authoritative per-plan snapshot;
-1 means unavailable. The built-in query returns a uint4 all-ones sentinel for these
requests, even if some unrelated/unadmitted mapping survives in the global page table.
Payload must contain actual data for every upload descriptor; CPU reservation alone
never constitutes completed GPU residency for external consumers.

1. Plan, stage the descriptor-ordered payload, and record the accepted plan. On an
   invalid payload, fix it and retry recording the same active plan. Do not plan a
   replacement with the same successful ID. The cache rejects skipped, replayed,
   out-of-order, or foreign-planner plans before recording writes.
2. `cache.RecordFrame(commands, plan, payload)` returns a graphics fence after upload,
   page-table updates, and the built-in query. Submit every recorded command buffer
   exactly once and in recording order. The cache waits for its prior consumer fence
   before writing shared staging buffers, page table, physical payload, and digests.
3. Keep leases, command buffers, cache buffers and CPU staging alive until the fence
   passes. Then call `planner.CompleteFrame(plan)` (or `plan.OwnerComplete()`). Other
   active leases keep their pins, so retirement may occur out of order safely.
4. External consumers must wait on the returned fence. Chain consumers in order and
   call `RecordConsumerFence` after the last consumer or digest copy, **before**
   recording the next writer. For a separate command buffer/queue, explicitly insert
   `WaitOnAsyncGraphicsFence` on the preceding fence first. Arbitrary unregistered
   fan-out consumers are unsupported; the last registered fence must dominate all
   reads. This protocol serializes conflicting work; it does not promise overlap.
5. An advanced single-queue caller can retire CPU leases after submitting every
   consumer if it guarantees all future cache writes remain ordered behind them.
   The benchmark instead waits on its per-frame fence before preparing the next frame.
   Early retirement without this ordering is invalid.

A successfully planned frame reserves mappings for its uploads. It cannot be silently
abandoned: execute the complete ordered chain, or drain outstanding GPU work, retire
all leases, and reset both planner and GPU cache before continuing. Likewise, do not
clear/replay an already recorded command buffer or dispose buffers still in use.
`RecordReset` orders its clear after registered consumers and resets the recording
sequence. GPU fences/compute are required; no unsupported-device fallback is claimed.
The cache uses a single reusable set of GPU buffers with explicit ordered updates,
while CPU plan arrays are independent per lease. There are no overlapping writes to
shared request snapshots. See queued-frame DX12 tests for two plans recorded before
either is submitted.

## Migration

The existing constructor and int `PlanFrame` overload remain. Callers must now retire
plans, read explicit counts instead of array lengths, and reset CPU/GPU state together.
`RecordFrame` returns a fence (ordinary statement calls remain source compatible).
Its input must be an active lease from the planner bound since the last GPU reset.
`GpuResidencyFramePreparation` in the benchmark is now a value type; its former extra
per-frame wrapper allocation is removed. No global default switches to the heap policy.

## GPU upload dispatch ceiling

Capacity does not imply that every slot can be uploaded in one dispatch. DX12 limits the X dimension to 65535 groups. Before planning, clamp uploadBudgetPages to cache.MaximumUploadPagesPerFrame (floor(65535 * 256 / PointsPerPage)). RecordFrame calls UploadDispatchGroupCount before any command mutation and rejects larger bursts, using long arithmetic to avoid multiplication overflow. For example, 32768 pages * 512 points requires 65536 groups and is rejected; 32767 pages is valid. Keep the accepted plan and retry only for repairable recording inputs; if an already accepted plan exceeds this bound, follow the documented drain/reset recovery, then plan with the correct budget. The cache does not silently truncate or falsely publish those pages as GPU resident. CPU-only tens-of-thousands-slot planning fixtures are unaffected.
