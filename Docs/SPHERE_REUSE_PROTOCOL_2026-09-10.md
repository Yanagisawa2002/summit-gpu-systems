# Sphere index reuse — finite implementation and three-arm protocol

Base: `e54a35eff9cf19540d94a34a35fee955c433c1b1`. This is a new optimization
round. The [earlier baseline](EXTERNAL_ACTUAL_RESULTS_2026-09-10.md), its raw
measurements, original binaries and entire artifact directory remain intact.
New evidence belongs only in `Artifacts/optimization-20260910-sphere-reuse/`.

## Fixed implementation and boundary

The public `GpuSphereWorkloadAdapter` separates `UploadPoints`,
`RecordIndexBuild`/`CompleteIndexBuild`, `UploadQueries` and `RecordQueries`.
The original `Upload` + `Record` still uploads the snapshot and rebuilds for every
nonempty query call. No shader predicate, binning algorithm, full-result capacity,
default candidate or deployment profile changes in this optimization.

Each **whole-task** new-Player repetition starts its host clock before uploading
and encoding the 50,000 original points. It records one real full index rebuild,
submits it and confirms its unique generation through an eight-byte command-
ordered readback. All 157 query batches then use that completed index. Each batch
still pays its query validation/bounds/upload, count/filter/scan/scatter, complete
offset/ID readback and synchronization. Final concatenation and consumption of
every `(query, ID)` pair remain in the primary clock. No cached query result or
cross-repetition point/index preparation is used.

Query batch size stays **128**, final batch **32**, all 20,000 original spheres,
all 50,000 points and **213,313 reference ID occurrences**. Same source pin,
float inputs/radius, point IDs, domain [-37,37]^3, sorting semantics and complete
CSR contract as the baseline. Input manifest SHA256 remains
`9d35f71c55f924d84b031316932ff025bc13ada43e8d450640ab92b957575737`;
`arborx-default.bin` remains
`750d9a9fa8331b005fe5709e8f25395d8b4ce856d0b174d470639a78fa9fad59`.

File I/O, process/engine startup, initial persistent-buffer allocation and the
already-exported original generation are outside all repeated task intervals,
as in the baseline. Point encoding/upload and at least one index build are
inside every warmup and measured repetition. Full equality checks and raw-output
file writes remain outside the timer. The eight-byte build confirmation is
inside the new arm's index build/submission/completion interval. It is additional
execution evidence, not a replacement for complete query-result validation.

Subintervals: point encoding/validation/upload; index recording/submission/
confirmation; query bounds/upload; query recording/submission/full readback;
host aggregation and consumption. These are host observations, not individual
GPU kernels, PCIe-only bandwidth, GPU counters, engine frames or presentation.

## Correctness and freeze before formal timing

Before any formal process: build the new Player into a distinct path; run real
GPU API fixtures for point/domain/query replacement, changed capacities, empty
inputs, duplicate IDs/coordinates, invalid order, unsubmitted/discarded commands,
legacy convenience, repeated calls and disposal. Their hand-specified small
memberships are untimed functional tests, not new benchmark inputs or a CPU
fallback. Also run two complete consecutive GPU repetitions of the unchanged
ArborX snapshot, compare every offset/ID/multiplicity, and run the existing
40-snapshot Cabana correctness gate without Cabana performance repetitions.

After those pass, commit reviewable source/API documentation and this protocol.
Freeze input/manifest hashes, old/new source identities, original native binary,
old/new Player trees, runtime/shader sources, compiler/editor identity, reused
dependency receipts, process arguments and round order. Changes after freeze
must not modify the measured implementation. A correctness repair is retained
with a distinct attempt and build identity; no failed result enters formal data.

## Three arms and four rounds

- **native**: the retained `actual-20260910/native-build/ArborXReplay.exe`, which
  calls real ArborX/Kokkos Serial on the original snapshot and covers point copy,
  index construction, full sphere query, output materialization and consumption.
  The separate `ArborXNative.exe` canonical query-only timer is not mixed into
  this complete-task comparison.
- **old**: original `actual-20260910/player-r1/SummitExternalReplay.exe`, measured
  source `acc4a669f0403e9ff4d6f5d152db974da050c430`. Its configuration retains that
  identity and directs all new output/logs to the optimization directory. The
  old Player itself is neither copied nor rebuilt.
- **new**: newly built Player from the successfully validated public reuse API.
  Its source commit, staging receipt and whole Player tree receive new hashes.

| Round | Fixed sequential process order |
| --- | --- |
| 1 | native / old / new |
| 2 | old / new / native |
| 3 | new / native / old |
| 4 | new / old / native |

Each of these twelve independent processes runs ten whole-task warmups and ten
measured repetitions. Keep all raw CSV/JSON and GPU CSR outputs from every
warmup/measurement. Any failure is retained and stops that comparison, without
an extra replacement sample or algorithm tuning. Every repetition checks complete
CSR, ignoring only order within each row by comparing all sorted IDs with their
multiplicity. No count-only, sampled or CPU-model gate is sufficient.

Use each process's ten-sample mean as one independent observation. Report all
four **old/new** ratios and **native/new** ratios, their geometric means and
nominal two-sided log-ratio Student-t 95% intervals, df=3. Report per-process
min/max and phase means, round ordering/dispersion, and all failures. No deletion
of outliers or favorable reruns. The old/new effect uses these same-round old
processes, not the previous session's 751.60794 ms. Native/new remains a Serial
CPU versus Unity Mono/D3D12 GPU adaptation comparison on this one machine.

## Resource boundary and handoff

All builds/imports/heavy preparation and native/GPU processes are serialized
under `Local\CodexR9700VNextUnityGpu`, with conflict and space preflight. Keep at
least 20 GiB free and bound the new optimization directory plus estimated stage
growth to 6 GiB. Reuse existing dependencies and inputs; copy only the 56.5 MB
Unity host/cache into the new directory so that the original host/cache stays
unchanged. No new external application/editor download is needed.

Record and respect background conflicts without closing user processes. Preserve
original caches, failure logs, source/HLSL locks and every old measurement. Do
not modify driver, power, affinity, priority or system-cache settings. Do not
start Boids/Megacity/other tasks, promote defaults, emit profiles, publish or push.
Verify owned processes exit and release the mutex at handoff.

Final receipt: `Artifacts/optimization-20260910-sphere-reuse/resume-outcome.json`,
with base/measured/final commits, both Player identities, implementation changes,
correctness, comparisons, unchanged-input proof, scope, raw paths, resource
release and blockers. Parent task owns global reports and the remaining queue.
