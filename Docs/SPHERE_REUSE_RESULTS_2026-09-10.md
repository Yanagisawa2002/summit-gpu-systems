# Sphere point/index reuse: implementation and actual three-arm comparison

The public sphere adapter now prepares points and builds their index once per
complete query task. On the unchanged ArborX snapshot, four same-round processes
per arm measured **784.168375 ms old SUMMIT, 135.980685 ms new SUMMIT, and
43.748410 ms native Serial**, averaging each process's ten measured repetitions.
The geometric mean of the four old/new ratios is **5.8552**, with nominal
log-ratio Student-t 95% CI **[4.2978, 7.9768]**. The new implementation is still
slower than native: native/new is **0.32657 [0.24791, 0.43020]**, equivalently a
new/native geometric cost ratio of about **3.0621**.

This is a finite implementation optimization with actual complete GPU outputs.
It preserves the [earlier baseline](EXTERNAL_ACTUAL_RESULTS_2026-09-10.md), its
original Player and every old artifact. The previous 751.60794 ms observation is
historical context only; it is not used in these paired improvement statistics.
No default candidate, deployment profile or broad performance eligibility changes.
The [frozen protocol](SPHERE_REUSE_PROTOCOL_2026-09-10.md) and
[small machine-readable evidence](SPHERE_REUSE_EVIDENCE_2026-09-10.json) contain
the boundaries, exact identities, per-process measurements and raw-evidence hashes.

The worktree base was `e54a35eff9cf19540d94a34a35fee955c433c1b1`.
The measured new source is **`360688314eaa275123db0dc3e0d14c2e54d9569a`**;
the old Player/native replay source is
**`acc4a669f0403e9ff4d6f5d152db974da050c430`**. Source and protocol were committed
and frozen after actual GPU correctness, before all twelve formal processes.
The final delivery commit adds this report/evidence and the README result link;
its SHA is recorded in the local outcome receipt, separately from the measured
source. No measured implementation changed after freezing.

The [public adapter](../PublicBenchmarks/External/Adapters/GpuSphereWorkloadAdapter.cs)
adds `UploadPoints`, `RecordIndexBuild`, `CompleteIndexBuild`, `UploadQueries`
and `RecordQueries`. The old `Upload`/`Record` signatures and rebuild-per-call
behavior remain available. Point/domain replacement invalidates the prepared
index and query batch. Query replacement retains the current point generation
and index. Invalid point values/capacity leave the point/index state invalid;
invalid queries invalidate that batch only. A unique build stamp, written in GPU
command order after a real full rebuild, must be read back before reuse becomes
ready. Merely recording or discarding a build cannot certify an old index.
Fences reject replacement while reusable commands remain outstanding. Callers
retain the owner until submissions, all output readbacks and consumers finish;
they discard never-submitted commands before disposal. The
[adoption guide](../PublicBenchmarks/External/Adapters/SPHERE_REUSE.md) describes
this ownership contract and real API call sequence. The shared index, sphere
predicate, scan/scatter shaders and source/HLSL locks were unchanged.

The real [replay application](../PublicBenchmarks/External/Actual/ExternalReplayPlayer.cs)
uses that public path. Every complete repetition starts its clock before point
encoding, validation and upload, then builds and confirms the index once inside
that clock. Its 157 batches reuse only that repetition's prepared index. All
20,000 query bounds/uploads, full count/scan/scatter, synchronous offsets/IDs
readback, output concatenation and consumption remain timed. The batch size is
still 128, with a 32-query tail; 50,000 original points and 213,313 ID occurrences
are preserved. Every new process reports point generations 1 through 20 across
its ten warmups and ten measurements. There is no cross-repetition preparation
cache, cached output, sampled/count-only result or CPU fallback.

The workload remains the pinned ArborX native library driver's default sphere
input, captured from its original generator, float coordinates/radius and source
pin `375875dfb6b2e7631b1ba599cd26ee5c1e68ab90`. No input was regenerated.
The retained no-callback case uses filled_box source/target, desired neighbors
10, predicate sorting enabled and buffer size 0, including the original
double-to-float radius conversion. Reused dependencies are Kokkos 4.7.02,
Boost 1.87.0, Google Benchmark 1.9.1 and CMake 3.31.10; the framework is not a
replacement workload or a new measurement in this round.
The manifest SHA256 is
`9d35f71c55f924d84b031316932ff025bc13ada43e8d450640ab92b957575737`;
the original `arborx-default.bin` SHA256 is
`750d9a9fa8331b005fe5709e8f25395d8b4ce856d0b174d470639a78fa9fad59`.
The comparator is the retained real **ArborXReplay.exe**, which rebuilds and queries
through ArborX/Kokkos Serial and materializes/consumes the full result on every
repetition. The canonical query-only ArborXNative timer, Google Benchmark
framework, historical NYCGIS/HLSL scores and application scenes are not these
measurements. Cabana LinkedCell CSR, neighbor iteration, permutation and ArborX
kNN retain their separate task contracts.

The machine used AMD Radeon AI PRO R9700, driver **32.0.31041.1004**, Unity
**6000.5.2f1 Mono/D3D12**, and Ryzen 9 9950X. Native uses the original MSVC
19.51.36248 build and **Kokkos Serial, one execution thread**. All processes
inherited affinity `FF` and Normal priority, exposing eight logical CPUs; none
of those settings were changed. Full compiler/editor/dependency hashes and
commands are in `frozen-run.json`. Serial CPU versus a Mono/D3D12 GPU adaptation
is a comparison across backends on one machine, not a same-device kernel claim.

| Player | Original location under `Artifacts/` | Build GUID | Whole-tree SHA256 |
| --- | --- | --- | --- |
| Old | `actual-20260910/player-r1/` | `614f4469570a4f0cbb76c499f5619901` | `4852e5ac1038a858e6bb8ba6fad982c8136174b87d957e2e9b923e7a7d1c4c9c` |
| New | `optimization-20260910-sphere-reuse/player-r1/` | `fe6baeb037424518afac7ff4aa4ed47c` | `3f1c94b5a3724b0f378bbce445d3810bb2822422ff29d5e165e0eee2d5a9f560` |

Only the small existing Unity host/cache was copied into the new directory.
The new Player was built incrementally once; the old Player was neither copied
nor rebuilt. Physical staged source hashes bind the tested pre-commit build to
the subsequent measured source commit. Both the public adapter assembly and
replay application assembly changed; their exact hashes are in the small
evidence JSON. The native executable SHA256 is
`34b47c852dfadee01a85c04edecac1ecdbeec9f51e9fe4ee3765c3af9fc9458c`.

Before formal timing, **44 actual GPU API/lifetime checks passed**, including
unsubmitted/discarded builds, outstanding query replacement, changed domains,
different/shrinking/empty point sets, query changes and tail/empty batches,
invalid capacity/values/order, duplicate-coordinate memberships, legacy calls
and disposal/fresh ownership. Their explicit small expected memberships are
untimed functionality fixtures. Two consecutive complete ArborX GPU replays
then matched all 20,001 offsets and all 213,313 IDs/multiplicity each. The existing
40-snapshot Cabana complete-CSR correctness gate also passed; its performance
suite was not rerun.

All eight formal GPU processes passed all warmup and measured full-output checks.
The independent offline decoder checked **202 complete upstream GPU outputs**
(162 ArborX and 40 Cabana), **3,241,000 rows** and **34,578,706 IDs**. It compares
every offset and sorted within-row ID, preserving multiplicity. The additional
13 small API fixture CSR files are retained separately. All four native replay
processes checked full membership internally and returned the expected consumed
checksum on every repetition. No failing GPU output or missing/selected sample
entered the formal data.

The following table contains whole-task host-wall process means in milliseconds.
Each cell averages ten measurements from its own process after ten complete-task
warmups. The three processes in a row were run separately in the declared order.

| Round | Order | Native ms | Old ms | New ms | Old/new | Native/new |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | native / old / new | 45.20826 | 787.05461 | 179.15137 | 4.39324 | 0.25235 |
| 2 | old / new / native | 42.80453 | 781.36268 | 123.46089 | 6.32883 | 0.34671 |
| 3 | new / native / old | 44.23788 | 778.32719 | 124.26420 | 6.26349 | 0.35600 |
| 4 | new / old / native | 42.74297 | 789.92902 | 117.04628 | 6.74886 | 0.36518 |

The independent units are four process means, not forty intra-process samples.
Intervals use `exp(mean(log(ratio)) ± 3.182446305 * sd(log(ratio)) / sqrt(4))`,
df=3. Every raw repetition, per-process min/max and phase mean is retained in
the evidence JSON and original output files. The first new process is visibly
slower (179.15137 ms versus 117.04628–124.26420 ms in the other three); it was
kept without a replacement or tuning. Its cause was not isolated. These nominal
intervals describe this finite run and do not establish cross-device/application
generalization.

The measured host intervals show where this implementation removed cost:

| Host interval, mean across four process means | Old ms | New ms |
| --- | ---: | ---: |
| Encoding/validation/upload, all batches combined | 615.12675 | 8.44358 |
| Recording/submission/synchronization/full readback | 168.45504 | 126.90748 |
| Final complete CSR checksum consumption | 0.26898 | 0.28653 |
| Aggregation and clock remainder | 0.31761 | 0.34310 |
| Complete task | 784.16838 | 135.98069 |

Within the new arm, point preparation/upload costs **4.66985 ms**, one real
index build/submission/eight-byte completion confirmation **1.08759 ms**, all
query bounds/uploads **3.77373 ms**, and query recording/submission/full readback
**125.81989 ms**. Those first four intervals sum to the two aggregate rows above.
Native charges **0.05516 ms** for input copy, **43.30261 ms** for construction
and query, and **0.39064 ms** for materialization/destruction/consumption.
File I/O, engine/process startup, persistent-buffer allocation and original
input generation/export are outside all repeated task intervals, as declared
in the original protocol. Equality validation and raw CSR file writes are also
outside; full result readback, concatenation and consumption are inside.

Repeated point preparation/upload dropped from 157 times to once, accounting
for the largest observed interval reduction. The remaining new query/submission/
readback interval is about 92.5% of its total task time. The implementation still
records and submits 157 small query batches with complete synchronized offsets
and IDs readback; reusing the index does not remove that work. These observations
explain both the delivered improvement and the remaining measured gap at this
boundary. No GPU timestamp/CPU profiler separated kernel execution, command
overhead, waiting and transfer costs, so 125.81989 ms is not labeled GPU kernel
or PCIe-only time. Batch size/algorithm were not retuned after these observations.

The initial `stages/03-api-r1.stage.json` is a background-process preflight
rejection **before any Player launch**. The subsequent interruption was an
intentional user pause, not a correctness failure. GPU work resumed only after
the parent's dated Forest completion clearance and a fresh process/space check;
the clearance and unchanged checkpoint/staging proof are preserved. Three
finished-handoff MissionServer services were retained as recorded by the parent;
no service, editor or other project's process was stopped. No automatic polling
script remained to start GPU work during the pause. The actual API, complete
upstream validation and formal processes then all passed on new Player r1.
The ordinary D3D12 info-queue diagnostic in Player logs is retained; it did not
prevent actual compute/full-output validation. The entire build/runtime logs
remain available, including the failed preflight.

All builds/imports and native/GPU stages held
`Local\CodexR9700VNextUnityGpu` serially and checked background work and capacity.
The lowest recorded stage-boundary free space was **25.7386 GiB**, above the
20 GiB reserve. At the later release probe this optimization directory occupied
**0.3064 GiB**, below its 6 GiB cap, with **24.9971 GiB** free. No new dependency
download, old-cache removal, driver/power/affinity/priority/system-cache change
or large application restore was performed. The final preservation audit proved
the original **93,447 files / 1,614,579,077 bytes** had unchanged paths and SHA256,
including all old measurements, failures, Players, dependencies and caches.
All 16 explicitly launched build/runtime processes exited successfully; all 19
stage receipts released their mutex, and the final probe acquired and released
it with no conflicting workload remaining. Final free space is sampled again
in the outcome receipt.

Raw evidence is under `Artifacts/optimization-20260910-sphere-reuse/`:
`runs/validation-{api,arborx,cabana}-r1/` contains correctness; `runs/r01-*`
through `runs/r04-*` contain all native CSVs, GPU JSON/CSR, configurations and
process logs. `stages/` contains each admission/execution receipt and build log.
The [reproduction tools](../Tools/ExternalSources/SphereReuse/README.md) document
the freeze, fixed schedule and independent full-output/statistical audit.
`evidence-inventory.json` hashes 364 small/raw evidence files; the complete
Player/source trees are independently bound by `frozen-run.json`. Resource,
final delivery and outcome receipts remain separate to avoid circular hashes.

| Evidence file relative to optimization root | SHA256 |
| --- | --- |
| `frozen-run.json` | `778bebadc377e89fd312ab11591dc069eb04d6c3662527f2eaf40bc1311f7fb3` |
| `evidence-inventory.json` | `f056514c396e300a63fa648a2140002cd884dc7e176d92496b04eb07b8da399d` |
| `analysis/comparisons.json` | `2525b47770224d85c528fa60f9c3a076743a1a7addb0cdb9329421110ceb3aee` |
| `analysis/full-csr-audit.json` | `2caf5b6cb404ff55c9b6b681f0943d4fdd6fde0df9248c9ed5515836a7b0dd22` |
| `baseline-preservation.json` | `fe5b3ab5b00834f6faebafe01864efec741bde0efd6ce3bf0db31f189d075dbf` |

All requested finite sphere comparisons are complete. Same-device native GPU,
ArborX kNN/callbacks, Cabana neighbor/permutation performance, GPU kernel/frame
timing, asynchronous consumer variants and actual Boids/Megacity/Forest scene
performance remain unmeasured here. This replay host does not substitute for
those external applications, and no broader suite or performance queue was
started. Parent coordination owns the overall queue and root outputs; the local
`resume-outcome.json` is the final machine-readable handoff. Nothing was pushed
or published.
