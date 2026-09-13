# Frozen external-task protocol — 2026-09-10

This protocol is fixed before input capture or formal performance execution.
The handoff source is `e0db6af185985c5911a4de482e555584387faa01`. Final adapters,
executables, input manifest and their hashes are frozen after correctness and
before the four formal pairs; a correctness repair gets a new source identity.

Pre-formal build repair: Unity's FXC compiler rejected `CountMatches` with
`internal error: flattened side effect` despite a successful Player build result.
The adapter now explicitly uses Unity DXC, preserving its precise predicate and
traversal. Both Player versions and the original error log are retained; only
the corrected `player-r1` is eligible for the complete-GPU-output gate and runs.

## Workloads and classifications

- Cabana `dd6bd7ccbb28974f81c365ff6b1fd1f3a080b802`: its own native LinkedCell
  benchmark, default N=100/1000, requested cell widths=3/4, double positions,
  default `InitRandom` seed 342343901, ten original iterations. Capture each
  original pre-permutation position snapshot and complete post-build CSR using
  a hook outside the native timed interval. Preserve original neighbor iteration
  and permutation. Capture timings are diagnostic, not formal observations.
- ArborX `375875dfb6b2e7631b1ba599cd26ee5c1e68ab90`: its native registered
  no-callback sphere query, filled_box source/target, 50,000 points, 20,000
  queries, desired neighbors=10, predicate sorting=true, buffer=0. Use original
  generator functions and the original double-to-float radius conversion.
  Export every point/predicate and full reference offsets/IDs. kNN and count-only
  callback workloads are excluded from this comparison.
- The CPU-native versus SUMMIT D3D12 comparison is a **cross-backend adaptation
  replay of those external inputs**, not an original native GPU score or a
  same-device kernel ranking. Canonical native benchmark outputs are separate.
- Official ECS Boids is an application sample, not a benchmark. Its pinned
  Unity 6000.2.10f1 is absent. A different scene/editor does not substitute for it.

## Implementation and identity

Use Kokkos 4.7.02, Serial only (one execution thread), MSVC 19.51 Release/C++20,
Boost 1.87.0, Google Benchmark 1.9.1 and CMake 3.31.10. Download hashes and
expanded sizes are recorded independently from native version strings.
ArborX's upstream CMake skips its driver on Windows; the supplementary target
links the unchanged driver with the same-toolchain static dependencies.

SUMMIT uses the existing Portable direct-binning/sphere adapters and full CSR.
ArborX's default N*Q worst-case buffer exceeds the current adapter bound. Split
only the query array into fixed batches of 128; retain all queries and all IDs,
concatenate offsets in original query order, and charge every batch's conversion,
upload, index rebuild, filter, scan, scatter, readback and aggregation. Reusing
an index across batches is not introduced or tuned in this run.

Inputs, original source files, generated capture patch, dependency artifacts,
compilers, built Player and native executables receive SHA256 receipts. Snapshot
file I/O, original generation, process startup and initial buffer construction
are outside the repeated comparison and are reported separately where available.

## Correctness gate

Before formal runs, actual GPU outputs must match every reference CSR offset,
all IDs and multiplicities for all 40 Cabana snapshots and all 20,000 ArborX
queries. Within-row ID order is not promised; sort only for validation, outside
the timed interval. No count-only/CPU-model/sampled substitute is accepted.
Every measured replay also verifies full output after the timed interval; any
failure stops that comparison and preserves its complete logs.

## Process ordering and repetitions

Four independent process pairs per comparison, fixed order:

1. Native CPU, SUMMIT GPU.
2. SUMMIT GPU, native CPU.
3. Native CPU, SUMMIT GPU.
4. SUMMIT GPU, native CPU.

For each Cabana case, warm up ten times on its first snapshot, then consume
the ten original snapshots in original iteration order. For ArborX, warm up
ten complete repetitions and then measure ten complete repetitions of the
same frozen default snapshot. No outlier deletion or favorable rerun; an
execution failure is recorded, not replaced by an extra formal sample.

Also execute the canonical unmodified Cabana driver with its default ten runs,
and the unchanged ArborX registered radius search with Google Benchmark's
default minimum-time/iteration logic, filtered to Serial. Those native-only
measurements retain their own narrower scope and are not pooled with replay.

## Boundaries and statistics

The primary repeated observation is synchronized **host wall time** from input
staging through complete result consumption. Native replay copies neutral source
points into the native view, builds the index, performs the required sphere
query if applicable, materializes full CSR and consumes all `(row, ID)` pairs.
SUMMIT converts/uploads, records/submits GPU work, reads all offsets and valid
IDs back, aggregates batch results if needed, and consumes all `(row, ID)` pairs.
The returned order-independent checksum keeps this host consumption observable;
full equality is checked separately. This is not an application simulation step.
Native ArborX's per-repetition tree and temporary native output destruction is
inside its output/consumption interval. Initial persistent buffers and final
persistent-buffer disposal are outside the repeated observations on both sides.
For the batched GPU sphere task, aggregation and per-batch clock overhead remain
in primary host wall time; report their remainder rather than assigning it to
an unobserved GPU phase. Generation/export has its own diagnostic receipt and
does not enter the repeated host-wall ratio.

Report input copy/encoding/upload, native index or index+query, GPU
record/submit/readback/synchronization, and output consumption subintervals where
the host exposes them. These CPU clocks are not native GPU timestamps, whole
engine frame times or presentation times. No scope is filled from another.

For each process use the mean of its ten measured repetitions. Report all four
paired native/GPU ratios, their geometric mean and a two-sided Student-t 95% CI
over log ratios (df=3). Report process dispersion/order sensitivity and min/max
without deleting failures or outliers. A CI crossing one is inconclusive; even
a separated CI applies only to this frozen host-observed task and backend pair.
No default promotion, deployment profile or inferred frame improvement follows.

## Resource and evidence policy

Every build, extraction, Unity import and native/GPU execution holds
`Local\CodexR9700VNextUnityGpu`, serially. Check conflicting processes and free
space at each stage. The estimated peak must leave at least 20 GiB. Never delete
old evidence/caches or stop user editors to proceed. New evidence stays under
`Artifacts/actual-20260910/` with distinct failure and run directories.

Final outcome: `Artifacts/actual-20260910/resume-outcome.json`, plus a tracked
scoped report. No publication, push, driver/power/priority/cache change or other
repository's execution is part of this protocol.
