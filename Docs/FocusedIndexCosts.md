# Focused incremental-index cost audit — 2026-09-08

Do not recommend the current incremental index for the measured N262144
hotspot trajectory with **CellSerial**, or the measured streaming trajectory
with **BatchedPointScanWave**. Keep the API and GpuDriven mode as explicit
opt-ins; no default or runtime algorithm is changed by this audit. The hotspot
with the new query had an inconclusive frame result in the earlier experiment;
this is not a stable-loss claim for all hotspot consumers.

The two adverse results have different causes. For the hotspot/old-query pair,
maintenance is slightly cheaper, but consuming reserved CSR is substantially
more expensive. In streaming/new-query, every measured update takes the
capacity fallback and rebuilds a reserved representation, paying both additional
maintenance and additional consumer cost. These are observed design costs, not
evidence of a correctness failure or a reason to remove a public API.

## Existing five-process formal evidence

The original public-scene evidence is preserved unchanged. Ratios are full /
incremental, above one favoring incremental. GPU values below are means across
the original five processes and four balanced blocks per process, after the
predeclared 64 warmup frames. They are native subintervals, not whole-engine GPU
or OS presentation times.

| Fixed comparison | Full ms | Incremental ms | Paired ratio [95% CI] | Interpretation |
|---|---:|---:|---|---|
| Hotspot, old query: engine cadence | 51.877501 | 59.167847 | 0.876783 [0.875866, 0.877701] | Stable observed slowdown; both CVs <0.4%, drift <0.5% |
| Hotspot, old query: full scene GPU | 51.369425 | 58.745087 | 0.874445 [0.873911, 0.874980] | Stable GPU slowdown |
| Hotspot, old query: index GPU | 0.339788 | 0.325407 | 1.043994 [1.023413, 1.064989] | Maintenance mean is lower |
| Hotspot, old query: query GPU | 50.829039 | 58.234276 | 0.872836 [0.872183, 0.873489] | Consumer adds 7.405237 ms |
| Streaming, new query: engine cadence | 0.559535 | 0.615321 | 0.908094 [0.809069, 1.019240] | Inconclusive; CI crosses 1, CV >7%, drift 43.0% |
| Streaming, new query: full scene GPU | 0.159016 | 0.277985 | 0.572132 [0.557408, 0.587245] | Stable GPU slowdown |
| Streaming, new query: index GPU | 0.025801 | 0.117053 | 0.220440 [0.214747, 0.226284] | Maintenance adds 0.091252 ms |
| Streaming, new query: query GPU | 0.022289 | 0.051176 | 0.435728 [0.418449, 0.453720] | Consumer adds 0.028887 ms |

The hotspot query increase explains almost the whole 7.375662 ms native scene
increase; blaming the maintenance interval would reverse the evidence. In
streaming, the two increases explain the 0.118969 ms scene increase, but do not
turn its noisy engine-cadence point estimate into a confirmed frame slowdown.
The earlier new-query hotspot cadence ratio remains 1.069 [0.953, 1.200].

Existing `recordCpuMs` includes fixture advancement, content work, uploads and
recording. It cannot isolate upload or API record overhead. Its old-query
hotspot means are 0.208197 /0.209023 ms and streaming/new-query means are
0.224144 /0.219913 ms; neither supports CPU recording as the cause of the
reported GPU representation penalty.

## Bounded diagnostic and equality checks

Four valid Release diagnostic processes replay exactly the existing two
trajectories, 384 frames each, seed 927101 from the existing first formal
replicate, with 64 warmup frames. No new N/rate/query axis, tuning search or
formal confirmation run was added. Every process uses the original fixture and
content code (with normalized source line endings), the exact previous content-0/content-1 AssetBundle bytes, and the
same load/cancel/register/unregister/unload event frames. No simulated loading.

Each frame executes both complete indices and both CSR consumers. Both
consumers read the **same incremental Samples buffer**, and index/query order
alternates through the four parity combinations. Additional GPU validation
proves every frame has the exact active ID set without duplicates, correct
cell membership and a snapshot equal to the input. Both complete query histories
match the existing independent CPU oracle word for word. There are 1,536 valid
frames, 3,072 exact CSR validations, 3,072 query histories and 25,344 native
intervals. Independent local and parent audits checked original binary history,
all native tick conversions/nesting/source-frame ownership, counts and events.

The controlled graph renders explicitly with Camera.Render into a 1280x720
R8G8B8A8_UNorm target, requested depth24, one sample, HDR/MSAA disabled. Original two draw
calls still execute, but this is **not the original automatic-render scene**.
No explicit resolve was added. Camera.Render CPU duration is separately recorded and may include internal
submission/back-pressure; it is not GPU time. The outer diagnostic graph
includes both complete indices, both queries, six extra exact-membership
validation dispatches, one history dispatch and both draws. Full compact
reconstruction is charged, not offered as a free optimization.

### Representation and fallback evidence

These are dedicated GPU shader counts over the entire CSR, not hardware
performance counters or a count of slots actually visited by each query.
Compact/reserved layouts also differ in member ordering, capacity and placement.
The experiment identifies **representation cost**, not a separate causal effect
of holes alone.

| Measured diagnostic frames per mode | Hotspot / old query | Streaming / new query |
|---|---:|---:|
| Frames | 320 | 320 |
| Capacity-fallback frames (`State[8] == 16`) | 89 | 320 |
| Non-fallback frames | 231 | 0 |
| Reserved CSR extent | 397,130 | 551,260–578,959 |
| Physical Invalid fraction, mean | 33.9904% | 57.9279% |
| Membership changes per ordinary frame | 26 | 2,621 |
| Input slots inspected each frame | 262,144 | 262,144 |
| Recorded incremental dispatches | 13 | 13 |
| Nonempty dispatches | 6 or 11 | 11 |

The hotspot has 262,144 live slots and 134,986 physical Invalid slots. Streaming
has 229,376 or 245,760 live slots as the unchanged bundles register/unregister.
`HolesWord` is **not** this physical Invalid count: it counts removals since
rebuild. It is zero throughout the measured streaming trace, while roughly 58%
of the reserved representation is still Invalid. Both diagnostic modes have
identical state and counter histories.

Source explains why. `ScanCells` reserves `count + ceil(count/2) + 1` words for
each occupied cell and zero for empty cells. Reservations append through each
cell's head; removed positions are not immediately recycled. An insertion into
an empty or exhausted destination triggers `CellCapacity`, even when the change
rate is low. Streaming reports 1,023–11,089 overflowing reservations per measured
frame. Membership changes peak at 19,005, below the 52,428-slot global churn
threshold; capacity failure alone forces the rebuild. The overflow counter is
reservations, not distinct overflowing cells.
Rebuilding clears the removed-since-rebuild counter; it does not make CSR compact.

### Same-Samples representation comparison, phase markers off

| Diagnostic GPU scope | Hotspot ms | Streaming ms |
|---|---:|---:|
| Full compact index | 0.399522 | 0.032585 |
| Incremental index | 0.235646 | 0.108619 |
| Compact CSR query | 50.601423 | 0.022677 |
| Reserved CSR query | 57.997747 | 0.050138 |
| Additional exact CSR validation + history | 0.085967 | 0.039318 |
| Original two draws | 0.080294 | 0.029035 |
| Complete paired diagnostic graph | 109.563331 | 0.357466 |

The hotspot representation difference is 7.396324 ms despite identical sample
storage and exact output equality. This agrees with the earlier consumer-cost
finding while retaining the layout/ordering/holes attribution caveat. Streaming
pays about 0.027461 ms extra consumer cost as well as more expensive maintenance.
These one-process-per-mode observations are descriptive, not new five-process
speedup/regression estimates.

### Stage instrumentation changes queue behavior

The native timestamp implementation is unchanged. Every scope end resolves
queries and issues a Completion event configured with
`FlushCommandBuffers | SyncWorkerThreads`, then signals a fence on Unity's
main D3D12 queue. Phases-off has 7 scopes/frame; phases-on has 26, adding 19
completion flush/synchronization events. It does not merely add 19 harmless
numbers to an otherwise identical stream.

| Scope | Hotspot off → on ms | Streaming off → on ms |
|---|---:|---:|
| Complete paired graph | 109.563331 → 110.800451 | 0.357466 → 0.753300 |
| Full index | 0.399522 → 0.509156 | 0.032585 → 0.147542 |
| Incremental index | 0.235646 → 0.746894 | 0.108619 → 0.345064 |

The streaming `ScanBlocks` stage is conspicuous at 0.047653 ms in the instrumented
path. Source performs a dependent 1,024-entry prefix loop on one thread; full
Direct uses a hierarchical parallel scan. Other streaming incremental stages
are roughly 0.0002–0.0105 ms each. Zero-X remove/insert branches still have
recorded commands and instrumentation, rather than disappearing from accounting.
`stage-costs.csv` reports every stage conditional on rebuild/no-rebuild.

Do not sum these stages into an alleged uninstrumented algorithm cost. In the
instrumented streaming index, 0.257604 ms lies inside the enclosing interval but
outside the stage begin/end intervals; full index has 0.110464 ms of such
remainder. Completion flushes, synchronization and intervening queue work occur
there. The remainder is not a clean measure of one driver operation. No marker
cost was subtracted. GPU core clocks/cache counters were not captured, so no
DVFS/cache cause is asserted. Timestamp frequency is not GPU core frequency.

### CPU and memory boundaries

Phase-off upload calls average 0.192329 ms for hotspot and 0.159411 ms for
streaming, with the same 5,242,880 logical bytes uploaded per changed frame.
This shared CPU/driver staging cost must not be hidden as a known-dirty shortcut.
Full/incremental API recording averages 0.006775/0.006418 ms for hotspot and
0.002198/0.002033 ms for streaming. Complete diagnostic recording, extra
validation and Camera.Render are separately retained in `cpu-costs.csv`.
This timing boundary differs from the original scene's broader `recordCpuMs`.

The known-allocation probe allocates a retained 1 MiB array. On all four runs,
GetAllocatedBytesForCurrentThread returns a zero delta while managed heap rises
1,052,672 bytes. The counter is unavailable here, and every row reports -1;
there is no zero-allocation claim. Full/incremental owned logical GPU storage
is 10,625,072 /15,732,924 bytes, excluding common diagnostic resources and driver
alignment. Incremental inspection also writes a 16-byte snapshot for each of N
dynamic slots, a source-level 4 MiB store per update that full-index reference
avoids by consuming the original input. This is a logical access estimate,
not a measured DRAM/PCIe traffic counter.

## Development failures and source identity

Baseline is public main `20191e5050805adb6b44f2803eaa966bd1571d8e`.
The retained hotspot-off process used diagnostic source
`812d4b8` and build GUID `15895a1154b64015acacb4c2bc8fa73e`.
The other three successful processes used `1bf9746` and build GUID
`666952621be54d3abf62a6c8a9610421`. Between those receipts, only generator,
runner and generator regression test changed: the generator corrected an
accidentally renamed shader resource string, while the runner retained the
already successful case. The controller, allocation probe, startup check,
render target, shaders, fixture and content source hashes are identical.
These remain separate-process, potentially different-build observations;
phase-off/on is a descriptive instrumentation check, not a strict same-build
paired causal estimate. No completed valid case was rerun for a better result.

All failures remain in diagnostic-v1/v2/v3: a build receipt int/uint mismatch;
a hidden auto-render submission that left 1,024 timestamp slots pending; and
a phase-on adapter resource lookup failure before any frame. None is presented
as algorithm or device-removal evidence. Explicit offscreen submission now
requires the first frame's scopes to become Ready before continuing the fixed
trajectory, records concrete native failure state, and never enlarges the ring.

No speculative production fix was made. Improving the serial prefix loop might
reduce one cost, but does not resolve per-frame capacity fallback or consumer
representation cost; it has no new formal benefit claim. Applicability guidance
is updated in the package README and index contract. All PublicBenchmarks source
and runtime package C#/compute code are unchanged. The API remains available for
workloads that independently verify capacity behavior, CSR extent and complete
maintenance-plus-consumer cost.

Reproduction and source generation are in
[Tools/IndexCostDiagnostics](../Tools/IndexCostDiagnostics/README.md). The delivered
evidence includes exact commands/PIDs, source/Player/content hashes, all original
histories/counters, prior formal excerpts, stage/CPU/order CSVs, both independent
audits and a failure ledger. All Unity/build/GPU work held the shared mutex. No
push, default promotion, API deletion, global cache deletion or driver/power
change was performed by this task. Existing limited benchmark reproduction
licenses are retained. Parent task owns final integration and publication.
