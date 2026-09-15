# Molecular dynamics query consumer: complete-task investigation

**Current status: native CPU/CUDA numerical matrices passed; formal performance
remains NO-GO.** On Linux/RTX 5090, Serial and eight-worker OpenMP passed at
`8032841`; ArborX CUDA and both ordinary tiled controls passed at `9932ec8`.
There are 112 checked repetitions across seven arms and eight cases: 98 full
force/velocity/position checks and 14 singular-boundary membership-only checks.
Both tiled controls also passed the actual one-ID capacity rejection fixture.
The SUMMIT GPU consumer and Unity Linux backend remain unimplemented/unvalidated.
These correctness results do not establish a performance winner. See the
[execution report](../../Docs/whole-task-md-20260915/LINUX_5090_NUMERICAL_RESULTS.md)
for independent scalar acceptance, monitor calibration, failures and raw evidence.

This harness follows the **force-to-position/velocity dependency slice** of the
[pinned ArborX public single-step example](https://github.com/arborx/ArborX/blob/375875dfb6b2e7631b1ba599cd26ee5c1e68ab90/examples/molecular_dynamics/example_molecular_dynamics.cpp).
It is not a production simulation or a library random-query replay. See the
[caller audit](../../Docs/whole-task-md-20260915/CALLER_AUDIT.md) for source,
residency, the excluded unused energy diagnostic and scope.

## Contract

The primary `original-10` case uses the original setup: 4,000 points, spacing
1.7, the original Kokkos XorShift64 initialization (seed 5374857), radius 3,
self exclusion by original ID, float force arithmetic, mass 1 and timestep
0.005. `discovery-8` is a separate source-derived 2,048-point discovery case.
`small-6` (864 points), `large-20` (32,000 points) and `perturbed-10` are explicitly
labeled extensions. `boundary-membership` is an eight-point functional fixture
with a float-radius boundary, adjacent float values, negative coordinates and
distinct IDs at identical coordinates. That last case verifies membership only:
the original force law is singular for coincident distinct particles.
`isolated-3` verifies zero-neighbour state completion. `tail-129` verifies full
state across a partial 128-query batch. These are functional fixtures, not
performance cases or replacements for the upstream default.

Each task begins with the same CPU snapshot. Native arms copy it into their
working views; GPU arms pay their required encoding/upload. The task ends when
forces, velocities and positions are ready in the backend's memory. This is
backend-resident completion, not CPU-return or scene frame latency. Every task
rebuilds the current point index; only its query batches may reuse it. Owner
construction and contiguous first-use latency are reported separately from the
steady reusable-owner task. Input file decoding and the common initialization
producer are outside the snapshot API boundary. Verification and evidence-file
writes occur after each task timer.

## Initial arms

* `arborx`: original library's BVH query + callback removing self + unchanged
  force/update source blocks extracted by `extract_upstream.py`.
* `grid`: a bounded conventional CPU uniform-cell CSR, exact float predicate,
  same extracted force/update blocks, same compiler and Kokkos Serial.
* `arborx-openmp` / `grid-openmp`: explicit OpenMP builds with a recorded 1-16
  worker setting. The grid's construction/prefix remain serial; independent
  query rows and force/update use OpenMP.
* `arborx-cuda`: external ArborX CUDA query and unchanged force/update on the
  device. It consumes the same Serial-exported input. This arm alone does not
  validate SUMMIT. CUDA cannot select the CPU grid as a GPU arm.
* `tiled128` / `tiled256`: ordinary CUDA all-pairs traversal using Kokkos teams
  and shared point tiles. The timed kernel directly consumes exact neighbours
  using the unchanged per-neighbour force block, then the unchanged update.
  Only these two tile sizes exist. This control does not use or validate SUMMIT.
* `legacy`: public adapter Upload/Record (rebuild per batch), necessary complete
  CPU CSR readback and aggregation, then a scalar float port of the consumer.
* `reuse`: public adapter prepare/build once per task, then its existing batches
  of at most 128 queries, complete CPU CSR readback and the same consumer.

The GPU adapter's worst-case result buffer is capped at **64 MiB**, rather than
increasing `N*Q` allocation to cover the whole task. The fixed index and scratch
buffers are additional and must be reported. Native backends do not pay an
artificial upload into Unity. A later candidate can use a true GPU consumer,
but must preserve every member and the resulting state before adoption.
The current adapter exposes one mutable query/output buffer set. Its query
fence precedes an appended force consumer; safe reuse needs a later consumer
fence and wait. The required build stamp/readback and per-batch waits are in the
[buffer lifetime audit](../../Docs/whole-task-md-20260915/QUERY_BUFFER_LIFETIME_AUDIT.md).

## Validation and measurement

Every native/GPU iteration checks **every offset and every sorted within-row
ID**, retaining multiplicity and self exclusion. Every force, velocity and
position component must be finite and pass predeclared bounds:

| Field | Absolute tolerance | Relative tolerance |
| --- | ---: | ---: |
| Position | 0.00002 | 0.000002 |
| Velocity | 0.00002 | 0.00002 |
| Force | 0.002 | 0.00002 |

These allow float accumulation-order differences, not changed input precision
or predicates. Brute-force membership independently checks up to 4,000 particles;
large-case ArborX and the conventional grid cross-check all members. The offline
reader independently decodes the saved complete first/last outputs from every
formal process. Checksums bind files; they are never a correctness substitute.

The tiled control reconstructs complete CSR from the original input snapshot
after the task clock. Count/scan precede a bounded allocation, and scatter checks
both row capacity and count agreement. An explicit one-ID-capacity fixture must
reject before scatter for each tile. These observed process receipts bind actual
CUDA UUID/PID, input, executable and raw report. An independent binary32 scalar
implementation checks all state components and, through 4000 particles, all
members. One oracle per case checks both tiles' first/last complete state files.
Freeze requires these receipts for selected tiles and rechecks their files and
full scalar state. Costly scalar membership is performed when the receipt is
created; its canonical input identity remains bound during later rechecks.

The direct tiled task does not create an artificial CSR inside its primary timer.
Its separate full CSR audit uses the same tiled predicate/traversal, and `auditMs`
is retained. This extra audit work can influence the next iteration's thermal or
cache state; actual discovery must evaluate that effect before freezing timings.

Diagnostic processes add D3D12 begin/end timestamp scopes and explicit completion
fences. They separately record host validation/encoding/upload, command recording,
submission, completion wait, post-fence readback/copy, aggregation and consumption.
Raw device ticks/frequency/fence values and an empty-scope control are retained.
Post-fence readback still includes Unity API/transfer/copy overhead: it is not
PCIe-only time. Performance processes disable these probes and keep the original
blocking readback; their `readbackMs` includes any implicit wait, and `waitMs` is
not a separate execution estimate. Device duration overlaps the host wait and
must not be added to it. Historical 125.82 ms is not retrospectively decomposed.

Only after discovery and correctness should `freeze.py create --plan <path>`
freeze an explicit version-2 plan. There is no default confirmation schedule.
The plan selects observed build receipts, arm executables, complete-output
validation receipts, case/arm/block order, worker counts, repetition counts,
noise limits, and comparisons. The prepared analysis supports four or six
independent paired process blocks; repetitions inside one process are not
independent samples. Discovery must establish sufficient task duration and
counter coverage before these values are frozen. Keep every failed attempt and
outlier; no silent replacement or filtering is allowed.

`provenance.py` requires nonempty source/dependency/product directories and
explicit required executables. `build_artifact.py` records source manifests
before/after the actual commands, tool hashes, command logs, dependency
identities and product hashes. Unity additionally verifies original-to-staged
source mapping and rejects unmapped compilation inputs. Formal freeze rejects
missing input/golden files, stale binaries, dirty/different source commits,
changed tools, incomplete validation or changed directory contents. It also
re-reads complete saved CSR/state outputs, rather than trusting a PASS flag.
These are local observed-build records, not a signed reproducible-build proof.

The Windows draft's initial CPU 25% / GPU-engine 20% admission gate only excludes severe conflict.
Formal qualification additionally requires a five-second observed quiet window
and process-lifetime background samples every 250 ms (including a final sample).
The limits are 5% background CPU and 3% background GPU engine activity after
identifying this process's own work. No counter availability means unavailable,
not zero. All raw observations are saved. Any observed interference, within-
process coefficient of variation over 20%, first/last-half ratio over 1.15 in
either direction, or across-process mean ratio over 1.5 makes the **whole
confirmation campaign** ineligible. No individual observation is removed or
replaced to make a favorable result survive. Periodic counters cannot rule out
unsampled bursts; nominal confidence intervals must be interpreted accordingly.
Those provisional thresholds require discovery review.

Linux telemetry has observed this host during the native validation campaign.
Its separate live-calibration result is recorded in the execution report.
It reads effective cgroup-v2 quotas/cpusets and visible ancestors, parent affinity
and SMT-sibling CPU counters, live/reaped owned CPU time, throttling and memory
events/headroom. Capacity uses the actual quota/affinity, not the visible CPU
count. The assigned full GPU UUID selects utilization, VRAM and process records;
unknown or foreign PIDs are rejected. The profile requires a five-second quiet
window, one-second samples, collection <=0.5 seconds, gaps <=2 seconds, at least
two during-process samples, <=0.25 background cores and <=5% of actual capacity,
idle GPU <=3%, and >=8 GiB actual memory headroom. Any throttle increase rejects.
Missing counters or uninterpretable fields never mean zero load.

`monitor_calibration.py` must corroborate a real validated CUDA process, actual
UUID/PID, positive owned CPU accounting, retained GPU XML, sample overhead and
overlap with actual task time windows. Different host/container PID mappings
remain unresolved and reject. Source/host changes invalidate calibration.
Calibration file revalidation is retained as preparation, followed by a fresh
full quiet window. Offline parser tests cannot calibrate a host. Nonformal
correctness runs remain performance-ineligible; monitoring overhead is never
subtracted from task latency. Periodic observations cannot exclude unsampled
bursts or hidden cgroup ancestors and are not intra-task GPU phase timings.

## Foreground reproduction

The following is the earlier Windows recipe, requiring a separate currently
valid Windows hardware grant. Use a fresh checkout on a `codex/` branch.
`Invoke-Stage.ps1` first
requires hlsl's terminal hardware-release handoff, then independently acquires
`Local\CodexR9700VNextUnityGpu` and checks other processes, load and free space.
It never schedules or auto-starts a later run. Do not bypass a rejected gate.

```powershell
$s = 'PublicBenchmarks/WholeTaskMolecularDynamics/Scripts'
& "$s/Invoke-Stage.ps1" -Name 01-prepare -Script "$s/stage-prepare.ps1" -EstimatedAdditionalGiB 2
& "$s/Invoke-Stage.ps1" -Name 02-native -Script "$s/stage-build-native.ps1" -EstimatedAdditionalGiB 2
& "$s/Invoke-Stage.ps1" -Name 03-player -Script "$s/stage-build-player.ps1" -EstimatedAdditionalGiB 1
& "$s/Invoke-Stage.ps1" -Name 04-export -Script "$s/stage-export.ps1" -EstimatedAdditionalGiB 1
& "$s/Invoke-Stage.ps1" -Name 05-discovery -Script "$s/stage-discovery.ps1" -EstimatedAdditionalGiB 1
```

The scripts use existing MSVC 14.44.35207, SDK 10.0.26100.0 and Unity 6000.5.2f1
Mono/D3D12. Source dependencies are pinned ArborX and Kokkos; CMake 3.31.10's
publisher checksum is verified. Dependencies, host cache, Players, inputs, full
outputs and failures are isolated under `Artifacts/whole-task-md-20260915`.
No global driver cache, power setting, affinity, other project or old frozen
evidence is changed. Actual final tools/hardware/results come from the receipts.

## Portable native preparation

Linux hardware access requires a separate coordinator admission; the Windows
mutex/handoff does not grant access to the Linux 5090. SUMMIT received generation
2 for staged capability/correctness work after Data Layout's verified release;
scope revision 2 also admitted CUDA, independent scalar acceptance and one fixed
live-calibration attempt. Formal performance was not authorized by that grant.
The earlier source checkpoint and stage budgets are retained here:
[commands, pinned identities, budgets and stop points](../../Docs/whole-task-md-20260915/NEXT_LINUX_STAGE_COMMANDS.md).

After admission, `prepare_hardware.py --native-only` prepares pinned source
dependencies/header without downloading Windows CMake or staging Unity.
`build_artifact.py native --backend serial|openmp|cuda --cxx <existing-compiler>`
uses only the run root and existing tools; compilation is bounded to 1-8 jobs.
The CUDA recipe uses the pinned Kokkos nvcc wrapper, explicit BLACKWELL120,
and disabled FMA contraction. Toolkit/architecture support still requires a
real host capability check. Export canonical inputs once using the Serial
binary; all other builds read those bytes and never reinitialize their RNG.

Host mirrors preserve device layout for valid copies. On CPU they alias the
working views; on CUDA they stage the upload inside the task boundary. Complete
CSR and state audit copies occur after backend completion and timing. See
[Kokkos mirror documentation](https://kokkos.org/kokkos-core-wiki/API/core/view/create_mirror.html)
and [deep-copy requirements](https://kokkos.org/kokkos-core-wiki/API/core/view/deep_copy.html).

The current source is **pre-confirmation investigation code**. Do not infer a
performance winner or deployment eligibility from compilation or this README.
