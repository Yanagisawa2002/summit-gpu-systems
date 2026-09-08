# Capacity fallback and reserved consumer work

The single empty-cell reserve candidate does not pass the frozen engineering
screen. Keep the incremental index opt-in and retain the existing restriction
for hotspot + CellSerial and streaming + BatchedPointScanWave. Do not promote
this candidate or spend another GPU confirmation matrix on it. This decision
does **not** establish measured candidate slowdown or rule out all capacity
policies. No production runtime, API, default, public scene or timestamp plugin
was changed in this round.

This follows [the measured focused-cost diagnosis](FocusedIndexCosts.md) from
published main `573c873e672cbb5d6216efd8bc465d4904662a12`. The previous GPU
measurements remain the performance evidence. New work is a deterministic CPU
mechanism replay, with no GPU timings, bundle loading, rendering or presentation.
The candidate changes only the reserve of an empty cell from zero to one;
occupied reserves, append-only heads, churn and fragmentation thresholds stay
the same. It exists only in the diagnostic model.

## Frozen experiment and correspondence

The [plan](../Tools/IndexCostDiagnostics/CausalPlan.md) was committed before
execution as `1631907420e4a0678e27f1329142138b485a13c2`. Both original trajectories
use N262144, seed927101, 384 frames, and 64 excluded warmup frames. The replay
compiles the unchanged production contracts, fixture, trace, deterministic
generator and CPU query oracle together with the diagnostic model. Content
registration at 128/240 and unregistration at 224/320 uses the original
ContentSample generator and seed. It reproduces the loaded data's effects;
it does not claim to reproduce loading cost. The actual prior bundle-loading
processes and their raw evidence remain unchanged.

On every frame, all nine four-word query digests and four metadata words match
the previously stored independent oracle. Before accepting that frame's
intervention, the baseline model matches all 16 state words and all eight
reserved CSR counters from the retained phases-off GPU history. Candidate
head, capacity, members and positions evolve independently. Both modeled CSRs
are checked for exact active ID membership, duplicates, correct cells and
recorded positions on every frame.

The model inserts IDs in ascending order; GPU atomic ordering need not match.
Aggregate per-cell reservations, overflow totals, reserve extents and rebuild
decisions are order independent. Exact correspondence is established for these
states, not GPU member order, cache behavior or runtime cost. Sample sequence
hashes include the complete uint4 sample and activity arrays after each update.

| Audit | Passed |
|---|---:|
| Original oracle words, including metadata | 30,720 |
| Original GPU state words | 12,288 |
| Original GPU CSR counter words | 6,144 |
| Baseline + candidate exact modeled CSR checks | 1,536 |
| Input file hashes / compiled assembly hash | 14 / 1 |

One successful replay process ran under the shared GPU/Unity mutex, PID 23424,
PowerShell 7.6.5 / .NET 10.0.11, 2026-09-08 01:44:04–01:44:16 UTC. Its wall time
is an execution receipt, not a CPU benchmark. There were no failed replay or
GPU processes and no retries. Parent audit independently verified input and
assembly hashes, all 1,536 CSV rows, 18,432 baseline GPU state/counter words,
and the steady-state screen. It reviewed the candidate model but did not
implement a second candidate oracle or run candidate GPU measurements.

## Why one empty slot is insufficient

Both production incremental shaders reserve
`count + ceil(count/2) + 1` words for an occupied cell and zero for an empty
cell. DetectChanges reserves destinations before RemoveMembers runs. Heads
append monotonically between rebuilds: neither a same-update removal nor an
earlier tombstone supplies a reusable reservation. Any overflow sets
CellCapacity for the whole update; reducing thousands of overflows to one
does not avoid the global rebuild.

All following counts use the 320 steady-state frames. A reservation count is
not a distinct-cell count. "Empty at rebuild" records occupancy at the last
rebuild; the one-slot policy gives those cells nonzero capacity.

| Trajectory / policy | Capacity rebuild frames | Overflow reservations | Of those: empty at last rebuild |
|---|---:|---:|---:|
| Hotspot / baseline | 89 | 106 | 83 |
| Hotspot / empty-one | 30 | 87 | 20 |
| Streaming / baseline | 320 | 362,688 | 356,696 |
| Streaming / empty-one | 320 | 16,136 | 10,144 |

In baseline streaming, 318 frames overflow only zero-capacity cells. The two
registration frames also exhaust occupied-cell reserves. Empty-one eliminates
most failed reservations but **not one streaming rebuild**:

* Across the 318 non-registration frames, each frame has 1–7 previously empty
  destination cells receiving exactly two insertions. The preceding candidate
  frame rebuilt, so each starts at head 0 / capacity 1. The second reservation
  overflows. There are 1,597 such cells/reservations in total, and no occupied
  destination overflow in these 318 frames. This includes the two unregister
  frames; "non-registration" does not mean "no content event."
* At frame 128, 1,917 previously empty cells generate 4,216 failed reservations;
  previously occupied cells add another 2,979 failures. At frame 240 the counts
  are 1,961 cells / 4,331 failures, plus 3,013 failures in occupied cells.
  Maximum incoming reservations across all destinations is six at each of
  these registration frames; it is two on all other steady-state frames.

The multiplicity inference uses the independent candidate history, not a
counterfactual applied to baseline state. For every non-registration frame,
`emptyAtRebuildOverflow == emptyAtRebuildCells == totalOverflow`, all are
positive, and maximum incoming is two. With head 0 / capacity 1 this uniquely
identifies two arrivals at each overflowing empty cell. The replay CSV retains
these fields; the analysis checks this relation explicitly.

Hotspot baseline has 82 zero-capacity-only, six nonzero-capacity-only, one
mixed, and 231 no-overflow frames. Empty-one has 30 nonzero-capacity-overflow
frames and 290 no-overflow frames. Twenty of its overflow frames include a
cell empty at its last rebuild. Unlike streaming, preceding frames can perform
maintenance, so those one-slot failures can reflect accumulated append use;
the two-same-frame-arrivals argument is not applied to hotspot.

## Consumer work and complete cost boundaries

The fixed screen required at least a 50% reduction in streaming capacity
rebuilds and no increase in logical consumer traversal in either trajectory.
Both conditions fail. This is a conservative engineering screen, not a GPU
performance model or a statistical significance test.

| Steady-state work | Baseline | Empty-one |
|---|---:|---:|
| Hotspot CSR extent, words | 397,130 | 656,660 |
| Hotspot physical Invalid fraction | 33.99% | 60.08% |
| Hotspot nine-query range visits, total | 627,222,278 | 793,333,551 |
| Streaming mean extent / member reads per batch | 566,478.634 | 674,148.325 |
| Streaming mean physical Invalid fraction | 57.93% | 64.65% |
| Streaming member reads across 320 batches | 181,273,163 | 215,727,464 |

CellSerial enumerates actual inclusive query-AABB cells and loops over each
cell's entire reserved range before rejecting Invalid IDs. The replay computes
this exact cell set separately for each of the nine queries. Its total logical
range visits rise 26.48%; it does **not** multiply the full extent by nine.
Per-query results and the maximum inner-loop iteration count for one of the
256 logical threads are retained. For whole-domain query 0, that maximum's
mean changes from 388,621.206 to 389,642.116. For center query 2 it changes from
388,597.206 to 388,603.116. Empty-one leaves the concentrated cell's long serial
loop essentially intact. Total visits and worst-lane counts do not predict a
percentage timing change: reduction work, instruction behavior, ordering and
memory effects are not measured here.

BatchedPointScanWave scans the CSR once for the nine-query batch. With the
fixed consumer entry capacity 3N, both policies launch 3,072 groups / 786,432
threads for ConsumeBatch, plus the same ClearBatch dispatch. The shader gates
membership reads on `cursor < extent`, then gates sample loads on a valid ID;
the wave query loop still runs across the launched threads. Consequently the
candidate's 19.01% higher extent represents more logical membership reads,
**not** more dispatched groups or a measured GPU time increase. Each live
sample is logically loaded once per batch under either policy. These are
source/model work counts, not hardware load transactions or DRAM counters.

All existing maintenance and full-rebuild paths remain charged conceptually.
Streaming still takes the complete fallback path on every frame: detection,
decision, count, scan, preparation, member clearing/scatter and finish. The
candidate increases the cleared member extent and subsequent consumer reads.
The fixed full-snapshot upload remains 5,242,880 logical bytes per changed frame.
No per-frame CPU selection, GPU readback, free conversion, or switch to the
full-index implementation is introduced. No per-pass timestamp values are
added together, nor compared across different native completion boundaries.

At this N=BinCount, both policies fit the existing 3N member allocation, and
the logical GPU-driven index resident allocation remains 15,732,924 bytes.
For empty-one, summing reserves gives
`BinCount + sum_occupied(count + ceil(count/2)) <= BinCount + 2*activeCount`.
This is at most 3N here. It is **not a general API-capacity proof**: for N below
BinCount, a one-slot reserve in every empty cell may exceed the existing 3N
allocation and would require a separate capacity/allocation design. The
model's fixed allocation bound is checked on every rebuild.

## Compact-consumer feasibility and decision

A compact CSR could remove the reserved words exposed to CellSerial, but it
must be maintained or constructed. The published `_Counts` array is refreshed
only on rebuild; append/remove maintenance does not keep it current. A separate
compact output therefore needs current per-cell live counts, prefix offsets,
and a safe scatter of live IDs, or an equivalent algorithm with all of those
costs accounted for. Reusing old counts after maintenance would be incorrect.

For a straightforward separate compact output, the output offsets and N-word
members alone require 2,097,156 logical bytes at this scale, before additional
count/scan scratch or lifetime needs. A scan of an existing extent E and write
of A active IDs entails at least 4E bytes of membership inspection plus 4A bytes
of member output for that construction strategy; obtaining per-cell counts,
scanning, output offsets, additional passes and synchronization are extra.
These are source-level accounting terms, not measured traffic or a lower bound
on GPU time. More specialized/in-place approaches need their own correctness
and hazard proofs.

The previous full-index comparison paid a complete reconstruction and changed
ordering/layout with compactness. It neither measured a dedicated compactor
nor isolated holes alone. This round does not add a second compact candidate,
silently substitute full rebuild, or claim that compaction cannot be useful.
The one-slot candidate already misses the frozen screening conditions. Retain
the scoped recommendation and opt-in API, and stop this candidate before GPU
validation. Existing new-query hotspot frame results and streaming engine
cadence remain inconclusive; no new five-process CI/CV/drift/p95 gate was run
or passed.

## Reproduction and evidence

Use PowerShell 7 with .NET APIs supported by the recorded .NET 10 runtime. The
runner refuses an existing output directory or uncommitted source, hashes all
inputs and the compiled assembly, and acquires the shared mutex once. It never
closes a user process. The original raw oracle and GPU history paths are inputs;
they are read-only and are not regenerated by this command.

```powershell
Tools/IndexCostDiagnostics/Run-CapacityReplay.ps1 `
  -OutputDirectory <fresh-replay-directory> `
  -SceneEvidence <unified-benchmark/scene> `
  -FocusedEvidence <focused-costs/index> `
  -LockScript <Invoke-SerializedValidation.ps1> `
  -StatusPath <owner-index.json>
python -B Tools/IndexCostDiagnostics/analyze_capacity.py `
  --replay <replay-directory> --output <fresh-analysis-directory>
```

The status JSON must contain `phase`, `currentProcess` and `updatedUtc`; only
the index owner's report may be supplied. Preserve the repository's limited
benchmark reproduction license with exported sources and binaries.

The delivery includes the frozen plan, measured source revision, exact replay
assembly, input/assembly hashes, both full CSV traces, receipts, analysis/audit,
parent audit and failure ledger. The analyzed sample sequence SHA256 values are:

* Hotspot: `ced637f5cfb67f511637801c8bc57667255147611f9f08983aa3bff676999735`
* Streaming: `b0d43105cb46f5821a4930ceee9cbdccaaa4a8da290e1ae632db0f32b3985237`

Final documentation/analyzer commits do not imply the recorded replay assembly
was rebuilt. Its source revision remains 1631907420e4a0678e27f1329142138b485a13c2.
