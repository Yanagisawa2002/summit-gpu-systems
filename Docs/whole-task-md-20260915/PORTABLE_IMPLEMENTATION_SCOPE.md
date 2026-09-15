# Portable validation and bounded SUMMIT GPU consumer

Status: source preparation only. No build, device test, numerical validation,
or performance result exists. Linux 5090 execution belongs to Data Layout
until the coordinator assigns this task a host and a bounded stage.

## Fixed task

Keep the pinned ArborX molecular-dynamics example's initialization, particle
identity, radius-3 membership, self exclusion, float force law, and single
velocity/position update. Start from one captured CPU snapshot. End with all
forces, velocities, and positions completed in the chosen backend's memory.
Rebuild the index from the current snapshot in every timed task. CPU input
packing and uploads belong inside the task; file decoding and audit readback
belong outside. Allocation and first use remain separate observations.

One **Serial-generated** snapshot and full reference CSR/state are shared by
all execution spaces. CUDA/OpenMP must not independently regenerate random
velocities. Full CSR membership/multiplicity and every state component are
checked outside the timed interval. The duplicate-point fixture tests only
membership because the upstream force law is singular at zero separation.

## Source changes now in scope

1. Fail-closed provenance: mandatory input/golden sets, immutable build
   receipts, source and dependency manifests before/after each build, actual
   tool identities and commands, staged Unity source mapping, and product
   hashes. A clean candidate commit is required for a formal freeze.
2. A portable native harness using explicit host mirrors and deep copies.
   Execution-space selection is Serial, OpenMP, or CUDA at compile time.
   The exact extracted force/update computation runs in that execution space.
   Export of canonical snapshots is Serial-only. The original sample remains
   a separate unchanged executable for source-attribution checks.
3. A conventional CPU grid baseline: serial grid construction with independent
   query rows and force/update work using the selected CPU execution space.
   Report this bounded implementation honestly; it is not a fully parallel
   grid builder. Try explicit 1/4/8/16 worker choices only during discovery,
   then freeze one justified setting. The observed 25-core-time cgroup quota
   does not authorize 208 workers or imply exclusive physical cores.
4. ArborX CUDA uses the same snapshot, complete query, force, and update.
   This validates the external consumer/backend; alone it does not validate
   SUMMIT or establish a gain for SUMMIT.

These are harness adaptations, not a native rewrite of SUMMIT. Build failures
are repaired within these files and pinned upstream API usage; no broad
dependency upgrade or global tool installation is implicit.

## Conventional GPU control

Use a straightforward tiled all-pairs radius/force kernel as the first control:
fixed 128/256-thread tiles, original ID self exclusion, exact radius predicate,
original force expression, then a separate update kernel. It keeps state on
the GPU and avoids manufacturing a CPU CSR requirement. The primary task is
4,000 particles; the 32,000-particle extension is explicitly quadratic for this
control. Discovery selects a tile size once; no sweeping many implementations.

The timed path need not materialize CSR when force consumes neighbours
directly. Its validation path must emit every accepted ID using the same
predicate/traversal for full membership comparison, with bounded output and
overflow rejection. Validation output readback is outside the task clock.
This control is not implemented or validated yet.

## SUMMIT GPU consumer: preferred bounded route

First test whether a Linux Unity Player can create the supported Vulkan
graphics device and run a tiny compute dispatch on the assigned host. An ICD
file and driver libraries alone are insufficient. No Unity installation,
license change, display server, or shared tool modification is authorized
merely by this document.

If an authorized Unity runtime is feasible, retain the existing SUMMIT HLSL
index and query implementation. Add a task-specific GPU force kernel consuming
bounded query batches, followed by a velocity/position update kernel. Required
scope is the benchmark adapter/consumer, ordered query uploads or owned query
buffers, and explicit lifetime/completion checks. Audit the existing API's
CPU-ready/index-completion transitions before selecting the upload mechanism;
recording multiple batches against overwritten buffers is invalid. Do not
change public API semantics merely to hide a wait. No stale index reuse across
different snapshots. Peak live buffers and overflow rules are measured.

Expected work, as a planning estimate rather than measured cost: 1-2 focused
implementation passes plus a device correctness pass if Player/Vulkan works;
the API's CPU completion requirement may force an extra pass. Stop and report
the precise missing capability if a supported graphics context cannot be
created. Existing D3D12-only timestamp code cannot supply Vulkan device time;
host task time remains usable only after actual completion and noise checks.

## Native rewrite decision boundary

A native CUDA/Vulkan SUMMIT port would need command/resource ownership,
scan/binning, bounded neighbour traversal, force/update, and complete output
validation. That is a new backend with different compiler and scheduling
behavior, not validation of existing Unity/D3D12. Estimated scope is several
implementation/validation passes and materially higher risk than adapting the
harness. It is outside this preparation stage. Do not start it merely because
ArborX CUDA builds. The coordinator decides whether that cost is justified or
whether to finish with an evidence-backed NO-GO and resume the existing backend
on an appropriate host later.

## Execution gates and stop conditions

After host authorization, perform one bounded capability/toolchain stage,
then Serial export + full CPU validation, then CUDA complete-state validation.
Only then run limited discovery. Freeze actual validated binaries, inputs,
settings, and independent-process schedule before confirmation. Final claims
need SUMMIT, a reasonable CPU control, and an applicable ordinary/external GPU
control under the same task/output boundary. External-only success, missing
GPU consumer, numerical mismatch, unavailable noise qualification, or an
unaffordable backend port yields a scoped NO-GO, not a manufactured speedup.

Linux monitoring must read the actual cgroup quota/cpuset, memory and throttle
counters, and usable per-device/process GPU observations. Host CPU percentages
over 208 visible CPUs must not be interpreted as this container's 25-core-time
capacity. Formal performance stays blocked until monitoring is implemented
and validated on the assigned host. No formal schedule is chosen by this file.
