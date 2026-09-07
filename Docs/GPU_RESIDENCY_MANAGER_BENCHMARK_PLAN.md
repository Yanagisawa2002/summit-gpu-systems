# Large-map and point-cloud GPU residency benchmark

## Frozen A/B

- A rebuilds all 256 visible pages every frame, uploads their complete point
  payload, rewrites the page-table mappings, scatters the payload into a fixed
  physical cache, and runs the visible-page digest query.
- B uses the same physical cache capacity, visible set, camera path, query,
  point data, and output digests. It retains virtual pages in 384 LRU-managed
  physical slots and uploads only page misses plus compact page-table deltas.

Both paths start each block from an empty cache and receive identical warmup
and measurement paths. The path moves one page column per frame and teleports
every 128 frames, so the benchmark contains both coherent streaming and burst
faults.

## Metrics

- Native DX12 GPU average and P99 for upload, scatter, and query commands.
- Point payload and total logical upload bytes per frame.
- Page hit, miss, eviction, and teleport-burst behavior.
- CPU planning/staging average and P99.
- Counterbalanced paired wins and CPU/GPU digest correctness.
- Formal frozen gates: two scenarios, eight pairs each, at least 10% GPU-average
  improvement, non-negative GPU P99, at least 10% CPU preparation improvement,
  at least 85% average upload reduction, and all paired GPU averages winning.

## Claim boundary

This is an application-level page cache over ordinary Unity `GraphicsBuffer`
allocations. It does not claim D3D12 reserved resources, tiled-resource sparse
binding, driver memory-budget callbacks, disk or network I/O, or lossless point
cloud compression.

## vNext scan/heap streaming comparison (prepared, not promoted)

`Tools/Run-ResidencyPlannerComparison.ps1 -Mode correctness` executes a standalone
.NET 10 portable harness against the actual package sources. It checks an independent
page-table-delta oracle, full-scan/heap victim equivalence, retries, overflow, leases,
prefetch, fairness, budgets, and teleports. It needs the .NET 10 SDK, not Unity/GPU.

`-Mode smoke -Repetitions 2` runs short forward/reverse order cases at 384, 4096,
and 32768 physical slots. `-Mode comparison -Repetitions 4` prepares the longer
counterbalanced comparison over coherent, teleport, overcapacity (1.5x slots),
budgeted (1/8 uploads), and prefetch traces. Caches are prefilled before warmup so
large-slot eviction cases exercise occupied caches. JSON reports CPU planning plus
retirement average/P99, managed allocated bytes, Gen0 collections, synthetic logical
upload bytes (64 points/page), hits/misses, deferred demand, completed upload queue
latency, and identical availability hashes. Source/environment metadata is written
beside each result. Timing excludes trace construction, output hashing, and JSON I/O;
allocation counters span the allocation-free plan/metrics/retire loop. Construction
and backing state memory are not per-frame GC. This is .NET CPU evidence, not Unity
or GPU timing. Pending waits are censored; interpret latency with deferred counts.

For actual Unity/DX12 GPU timestamps, the existing runner has an opt-in
`-CompareLruPolicies` switch. A becomes persistent scan LRU; B becomes persistent
heap LRU with identical cache, interest, payload, and consumer query. Variant names
and case IDs explicitly identify these v2 policies. The historic rebuild gates are
not applied to this different comparison. Raw samples add `planningAllocatedBytes`
in Unity, alongside planning/staging/record/submission timing, actual logical upload
volume, hit/miss rate, and native GPU timing. Frame preparation is a value type.
The runner supports `-PhysicalSlots 384|4096|32768` and square power-of-two virtual
grids via `-VirtualPages 4096|16384|65536`. All GPU/Unity invocations must be wrapped
by the shared task lock, held until the child exits:

```powershell
$control = 'C:/Users/EdwinLiu/Documents/Codex/2026-09-07/w-m/work/r9700-vnext'
$repo = 'C:/Users/EdwinLiu/Downloads/SUMMIT-gpu-r9700-vnext-residency'
# Authorized short validation only:
& "$control/Invoke-SerializedValidation.ps1" -Action {
    & "$repo/Tools/Run-GpuResidencyBenchmark.ps1" -MatrixPreset smoke -CompareLruPolicies
}
# AFTER integration and the user's formal-evaluation decision:
& "$repo/Tools/Run-ResidencyPlannerComparison.ps1" -Mode comparison -Repetitions 4
& "$control/Invoke-SerializedValidation.ps1" -Action {
    & "$repo/Tools/Run-GpuResidencyBenchmark.ps1" -MatrixPreset formal -CompareLruPolicies -PhysicalSlots 384 -VirtualPages 4096
    & "$repo/Tools/Run-GpuResidencyBenchmark.ps1" -MatrixPreset formal -CompareLruPolicies -PhysicalSlots 4096 -VirtualPages 16384
    & "$repo/Tools/Run-GpuResidencyBenchmark.ps1" -MatrixPreset formal -CompareLruPolicies -PhysicalSlots 32768 -VirtualPages 65536
}
```

The GPU path keeps its 256-demand moving-window workload; the portable harness covers
larger demand and budget/fairness stress. Do not infer large-cache eviction wins from
a low-pressure GPU window. Use the prefilled CPU eviction traces and inspect miss
volume when interpreting large-cache GPU samples. No default policy is promoted and
no new formal speedup claim is made. Historical v1 evidence must not be pooled with
v2 snapshots/fence overhead or changed CPU admission behavior. See package
`STREAMING.md` for ownership, pending-interest, and producer/consumer contracts.

The scan/heap runner invokes Tools/Summarize-ResidencyLruComparison.ps1, which rejects missing pairs, inconsistent per-sample inputs/upload volumes, failed validation, and mismatched paired digest hashes. It writes lru-comparison.json without automatic promotion. All current runner case IDs use v2.

The scan baseline does not maintain the candidate heap. Unity planningMs and planningAllocatedBytes include retirement of the preceding CPU lease, so heap maintenance on retirement is not hidden outside the measured scope.
