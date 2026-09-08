# Competitive query baselines and a transport consumer — 2026-09-08

The new index-free parallel scan removes the weak-baseline assumption. The batch
candidate has **not demonstrated a user-visible advantage over that reference**.
In the hotspot transport scenario, mean process p95 delivery is 720.807 ms for
parallel scan and 720.774 ms for batch: effectively the same observed outcome.
CellSerial is the pathological outlier, at 8507.475 ms. This is a simulated
transport workflow, not a production warehouse or a full-frame FPS benchmark.

All 45 formal processes passed correctness: 360 query timing samples, 5120 sensor
updates, and 46080 cart transports. **Every formal comparison remains inconclusive**
under the frozen gates. No runtime default was changed; no cell was rerun or
workload/rate selected after seeing the results.

## What was implemented

- `GpuSensorParallelScanQuery`: one 256-thread point block per query, native wave
  reductions, one result clear plus a 2D scan dispatch. It reads original samples
  and active flags directly. It does not need spatial sorting, bin offsets or an
  index build. Only flag value 1 is active. Results use the existing count/hash
  digest semantics; no claim of full hit-list output is made.
- `RecordExternalIndexQueriesOnly`: queries a trusted index without the optional
  frame-digest dispatch. Existing `RecordExternalIndexQueries` behavior is preserved.
  This prevents charging an unrelated aggregate digest only to indexed algorithms.
- Four competitors: CellSerial, existing PointChunksWave, the new index-free scan,
  and BatchedPointScanWave. The latter two both use native wave operations.
- `QueryBoundaryPlayer`: separate opt-in `-boundary-config` route. All old scene,
  paced-video and queue-latency routes remain available without behavior changes.

## Fixed query boundary experiment

262144 points; six cases fixed before collection. Small radius is 255, wider
radius is 8192 in the 16-bit coordinate space. There are nine fixed query centers,
except `uniform-one`, which has one central query. Initial hotspot concentration
is approximately 99% in one cell. This is a stress case, not a claim about its
frequency in deployed applications. Seeds vary payloads; geometry is fixed.

Each process rotates case and algorithm order, warms each algorithm four times,
and collects three blocks. One timing spans 16 complete query-set repetitions on
the same hot input, followed by one async readback. Index construction and upload
are outside that interval for **every** arm. The scan does not use the prebuilt
index. CPU command recording is separately reported. Raw tick pairs are retained.

Numbers below are milliseconds per **whole query set**, not per individual query:
mean across five processes of each process's median of three elapsed/16 samples.
These are host-observed batch completion measurements, **not hardware GPU times**.

| Workload | CellSerial | PointChunksWave | Parallel scan | Batched scan |
|---|---:|---:|---:|---:|
| sparse-small | 0.0321 | 0.2633 | 0.2322 | 0.1541 |
| uniform-small | 0.0344 | 0.1783 | 0.1976 | 0.0914 |
| uniform-wide | 0.1987 | 0.8571 | 0.3121 | 0.2408 |
| hotspot-small | 62.8654 | 0.4652 | 0.3310 | 0.1963 |
| hotspot-wide | 59.7639 | 0.3393 | 0.2222 | 0.1309 |
| uniform-one | 0.1062 | 0.1915 | 0.0375 | 0.0406 |

The direction has a plausible structural explanation, but this table does not
establish a stable crossover threshold:

- Sparse/small: CellSerial can visit a few cells and skip almost all point data.
  Scanning every active point pays unnecessary reads and comparisons.
- Hotspot: one CellSerial thread can inherit nearly the entire populated cell;
  its group waits at reduction barriers. Point-based competitors distribute that
  work across many groups. This explains why beating CellSerial alone was weak evidence.
- Multiple queries: the batch candidate reuses each indexed point load/hash across
  query tests. Independent per-query scan rereads samples, but can avoid hashing
  misses and avoids index indirection. It is a real competitor rather than a
  deliberately serialized baseline.
- One query: batch reuse has little opportunity to help. Observed times for scan
  and batch are close. Chunk planning/merging can also cost more than it saves.
- These are explanations from implementation structure, not measured percentages
  attributed to memory, dispatch, synchronization or occupancy counters.

Scan process CV ranges from 6.0% to 126.1% across the six cases. Even directional
95% intervals cannot override failed CV/drift gates. The next measurement repair
should isolate host polling and scheduling from GPU work, with an independently
validated hardware-timestamp collection path; expanding this parameter grid is
not justified. Repeated-input results do not establish cold-workset behavior.

## Query results drive a complete consumer

Nine regions represent possible transport corridors. A fixed cohort of 4096
obstacles moves to region `job % 9` on each sensor update. Occupancy thresholds
are frozen at background count +2048. Every update creates nine transport orders.
The GPU count decides whether each cart takes the direct route (350 ms) or detour
(700 ms). A cart stays at its origin until **its own** result is read back and
validated, then advances according to monotonic real elapsed time on that route.
Completion occurs only after the configured route duration has elapsed.

The dashboard draws those cart states; it is not an independent decorative
progress animation. This is a deterministic kinematic consumer, not NavMesh,
collision physics or a real vehicle controller. Count queries are sufficient for
this occupancy decision; a hit-ID list would be a different output contract.

128 sensor updates arrive at 30/s, one sensor job is in flight, no job is dropped
or coalesced. Raw input and active flags are uploaded identically for all arms.
Indexed arms rebuild their spatial index; scan legitimately skips that work.
The live dashboard can draw a different total number of frames as runs take
different times; equal work means the same input/transport orders, not equal total
rendering commands. Update counts are retained. All algorithms are preallocated
for the shared harness, so no resident-memory
advantage is claimed. The CPU reference is prepared before warmup and timing.

Each order records arrival, prepare/upload/record costs, GPU submission, host
readback observation, validated route application, first subsequent Unity
end-of-frame, and transport completion. Nine routes, full digest histories and
all tick-derived times are independently audited. The 100 ms decision deadline
is a declared synthetic responsiveness target, not a measured human threshold.

Below, each value is the arithmetic mean of **five per-process p95 values**
(except >100 ms, the mean per-process fraction). It is not a pooled task p95.

| Distribution | Algorithm | Route decision p95 ms | First engine frame p95 ms | Transport p95 ms | Decisions >100 ms |
|---|---|---:|---:|---:|---:|
| uniform | cell | 20.84 | 21.22 | 722.82 | 0.00 |
| uniform | chunks | 18.11 | 18.53 | 720.40 | 0.00 |
| uniform | scan | 22.59 | 23.15 | 725.07 | 0.00 |
| uniform | batch | 18.25 | 18.62 | 721.86 | 0.00 |
| hotspot | cell | 7802.94 | 7803.81 | 8507.47 | 99.22 |
| hotspot | chunks | 19.22 | 19.57 | 721.30 | 0.00 |
| hotspot | scan | 19.10 | 19.54 | 720.81 | 0.00 |
| hotspot | batch | 18.88 | 19.28 | 720.77 | 0.00 |

All three point-based methods avoid the legacy hotspot backlog in these observed
runs. Against the stronger scan baseline, batch's hotspot route-decision ratio is
1.002 with nominal 95% CI [0.730, 1.377]; transport ratio is 1.000 with interval
[0.993, 1.008]. There is no demonstrated extra user-facing benefit from batch here.
The approximately 700 ms route duration and host delivery costs dominate any
sub-millisecond query differences. Uniform-distribution transport results also
have overlapping intervals.

Five processes contain unfocused workload updates: `2-hotspot-scan`,
`2-hotspot-batch`, `2-hotspot-cell`, `3-uniform-scan`, `4-hotspot-batch`.
These observations are retained. Focus alone is not established as the cause of
all noise. No recording was active during formal collection. Unity end-of-frame
is an engine milestone, not measured physical presentation; no smoother-frame
claim follows from this test.

## Verification and statistics

18 hardware correctness/contract checks covered 513 points (nonmultiple tail),
partial removal, all inactive, last stable ID at the coordinate boundary, zero
logical length clearing stale results, and alias rejection before GPU commands.
All four algorithms were checked against the CPU oracle. A corrupted job-1 oracle
caused failure with only job 0 routed; no cart from the invalid job was released.
Digest equality is not collision-free proof of exact membership, but both count
and all three payload aggregates match. Every formal business binary history is
byte-identical to its original oracle.

The independent unit is one of five process replicates. Ratios are scan time /
indexed-arm time. Student-t intervals use five log ratios, df4. Gates require
both arms CV <=5%, both last/first drift <=15%, interval excluding 1, and for
business zero unfocused work updates. All comparisons failed at least one gate.
Intervals are nominal per comparison, not multiple-comparison-adjusted familywise
claims. The five analysis tests check retained losses, noisy gains, focus exclusion,
process-level independence and tail interpolation. The existing eight analysis
tests passed. A legacy queue-route regression on the new Player also passed all
384 original job histories, checking the preserved frame-digest API path.

## Reproduce

Measured Release Player source: `d66757f4b8cc01f1253443cf1a22faa85125b7ba`.
Frozen protocol: `protocol-query-boundary.json`. The evidence contains all 45
process folders, original oracles, validation failures, build attestations and
source-bound binary hashes. The later launcher/analyzer do not alter the Player.

Use Unity 6000.5.2f1 with Windows build support and the existing `Build.ps1`.
The downloadable Windows Player is the same measured build; it requires D3D12
wave operations and was tested on AMD Radeon AI PRO R9700. A new Unity install
is not required to run that Player. PowerShell 7 is required for the launcher.

```powershell
./Scripts/Launch-DispatchDemo.ps1 -BuildRoot '<build>' -Arm scan -Distribution hotspot
./Scripts/Launch-DispatchDemo.ps1 -BuildRoot '<build>' -Arm batch -Distribution hotspot
./Scripts/Run-QueryBoundary.ps1 -BuildRoot '<build>' -OutputRoot '<fresh>/validate' -Mode validate
./Scripts/Matrix-QueryBoundary.ps1 -BuildRoot '<build>' -OutputRoot '<fresh>/formal'
python ./Scripts/analyze_query_boundary.py '<formal>' '<fresh>/analysis'
python ./Scripts/test_query_boundary_analysis.py
```

The demo closes after all transports finish and retains per-order JSON and binary
results in a fresh `Runs` folder. Each script takes the same shared GPU mutex,
starts only its own Player and preserves failures. No administrator permissions,
company repositories, driver/power changes or global cache resets are used.
Existing limited benchmark reproduction permission and third-party notices apply.

The useful result is a narrower claim: a competitive baseline and an executable
consumer now exist, and the original batch candidate has not earned a default
switch or a stable-speedup claim against that baseline.
