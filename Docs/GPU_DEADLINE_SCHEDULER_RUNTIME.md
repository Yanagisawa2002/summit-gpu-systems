# Bounded runtime deadline scheduler

The vNext API is opt-in and preserves the original frozen planner/benchmark.
The default execution queue is main graphics. Nothing here preempts an already
submitted dispatch or claims dedicated copy-queue support.

## Runtime contract

Construct `GpuRuntimeCostEstimator`, configure integer workload keys, then
construct `GpuRuntimeScheduler(capacity, outstandingCostBudgetMicroseconds,
backgroundAgingMicroseconds, maximumInFlightJobs, costs)`. Capacity is 1–1024;
the scheduler owns two entry arrays and two capacity-squared adjacency arrays.
All instances and methods are single-threaded. The caller supplies a monotonic,
nonnegative microsecond clock; there is no engine time or wall clock in planning.

Submit arrays to `TryAdmit(jobs, count, edges, edgeCount, now)`. Admission is
atomic: duplicate IDs, default/invalid jobs, unconfigured cost keys, missing
endpoints, duplicate edges, self-cycles and multi-node cycles reject the entire
batch. Edges can name existing retained prerequisites; dependents must be in the
new batch. No existing graph can be rewritten after admission. An empty batch
is supported. Capacity and outstanding estimated-cost limits apply to the whole
batch, so the application retains and retries rejected work. There is no hidden
unbounded overflow queue and no silent drop. A batch larger than capacity or
the cost budget must be split at valid DAG boundaries or rejected upstream.

Relative deadlines and aging start at successful admission. Preserve any earlier
external arrival/SLA separately (the comparison harness does this). Pending cost
is refreshed from the estimator at every admission/selection; submitted cost is
frozen for the admission budget. A changing estimate can exceed the budget for
already admitted work; that work is drained and new admission is backpressured.
This budget is predictive rather than a GPU execution-time guarantee.

`TryPrepare` returns a value without reserving it. `MarkSubmitted` commits that
value immediately after successful queue submission. Only submit prepared values
once; ticket generations reject stale slot reuse. `maximumInFlightJobs` bounds
how much work is irreversibly queued ahead. A ready job can have **submitted**
prerequisites; CPU completion is not required, but the executor must preserve GPU
dependencies. The supplied executor emits prerequisite fences for queue changes
and submits individual jobs in topological order. Grouping a whole DAG by queue
can deadlock an alternating main→async→main graph and is prohibited.

The oldest admitted job that reaches the aging threshold takes precedence over
unaged jobs, regardless of class. Applying this to every class also permits old
prerequisites of background work to progress. Before that threshold, least slack
wins, with admission ticket as a deterministic tie break. Once ready and aged,
a job cannot be overtaken by later admissions, though finite older work and
already submitted dispatches still delay it. This is not a hard wall-time latency
bound under overload, stalled GPU work or an executor that stops submitting.
The optional `fifo` constructor argument is a bounded comparison baseline.

Call `TryComplete(slot, ticket)` only after a trusted GPU completion observation,
or let the executor's `PollCompletions` do so. Completed IDs/fences remain retained
until `ReleaseCompleted`, which never reclaims a prerequisite of pending/submitted
work. After reclamation, new edges to that old ID are invalid. Schedule future
dependents before reclamation if they need the predecessor identity. Resource
ownership stays with the application: retain UAVs/input buffers until consumers
finish, not merely until their producer finishes. Do not mix external submissions
with a `GpuRuntimeQueueExecutor` instance. `RecordJoin` records a GPU wait; it does
not wait on the CPU. Dispose only after polling all submitted jobs complete.

## Cost samples and async admission

Keys must distinguish relevant kernel, size, queue and device/driver/build state.
The application owns this identity. `Configure` resets a key and invalidates all
outstanding samples even if its revision number is reused. Call `BeginCostSample`
on a submitted dispatch, retain its token alongside the native timestamp token,
then pass the **per-dispatch GPU duration** to `Costs.TryUpdate`. CPU fence latency
includes queue and observation delay and must never train GPU cost estimates.

The fixed sample ring fails closed on overwritten, duplicate, invalid, expired,
future-clock, wrong-revision and out-of-order tokens. A newer submitted sample
accepted for a key prevents later arrival of an older sample from overwriting it.
NaN, infinity and nonpositive durations are invalid. Positive outliers are clipped
to configured bounds and one-quarter/four-times the previous estimate; an EMA with
weight 0.25 limits cold-start spikes and follows sustained workload changes. The
application must submit samples promptly enough for its chosen age window. Ring
overflow loses measurements, never job completion or dependencies.

`GpuAsyncAdmissionEvidence` defaults closed. Explicit evidence requires at least
16 paired observations, at least 2% lower average makespan, non-worse total P99,
critical P99 and critical miss rate, finite positive timings and an unexpired time
window. The application must scope/refresh evidence for its actual workload and
environment and revoke it when these change. Evidence is not authenticated by
the struct. Background jobs remain on main graphics; admitted critical/normal
jobs use one urgent compute lane. The existing R9700 balanced and saturated data
rejected async, so the dynamic comparison harness supplies no admission evidence.
The cross-queue correctness test uses deliberately synthetic evidence only to
exercise fences; it is not calibration or a production default.

Successful admission, preparation, completion, reclamation and estimator updates
allocate no managed memory after construction (tested using the allocation
counter). Caller arrays/delegates should be reused. Exceptions, constructor work,
diagnostic result serialization and Unity/native instrumentation are outside this
claim. Planning/admission use bounded array scans, not an unbounded task queue.

## Executable comparisons

Pure deterministic CPU replay:

```powershell
& '<worktree>/Tools/Run-GpuRuntimeSchedulerSimulation.ps1' -BatchCount 64
```

DX12 Player smoke or the later formal comparison (hold the integration control
directory's shared lock across the entire call):

```powershell
& '<control>/Invoke-SerializedValidation.ps1' -Action {
    & '<worktree>/Tools/Run-GpuRuntimeSchedulerComparison.ps1' -Preset smoke
    # After integration, replace smoke with formal (256 batches, four AB/BA rounds).
}
```

The comparison covers bursts, changing costs, DAG chains, overload and a critical
stream with background work. Both policies receive identical deterministic offered
jobs and dependencies; FIFO freezes cost estimates while runtime learns them.
Backpressured batches retry in original order, so external head-of-line delay is
included. Outputs retain raw per-job arrival/admission/submission/completion times,
cost estimates, native GPU durations, planning CPU ticks/GC, critical P99/miss rate,
total makespan, background completion and maximum wait, threshold-exceeding
background count, backpressure attempts and accepted delayed samples. The field
`starvedBackground` counts waits above 1000 us (or unfinished jobs), not proof of
infinite starvation. Critical SLA is an intentionally demanding 400 us.

The simulation reports **synthetic fake-clock time**, never hardware performance.
The Player reports CPU fence-observed latency and native DX12 dispatch duration
separately. Its outer GPU timeline makespan includes submission gaps, Unity frame
polling and delayed sample drain; it is not a sum of kernel durations. Samples
are deliberately consumed at least two frames late. Per-job digests are checked
in a separate validation replay before each measured run; measured runs perform
zero workload readback. Dependency correctness additionally uses real producer
data in a four-job main/async/main/async test. Dynamic trace jobs themselves use
the original independent synthetic ALU workload, not a sensor dataflow model.

These are short jobs, so Player polling/instrumentation can dominate latency.
Compare CPU-observed P99/misses and GPU timeline/dispatch metrics together, retain
all AB/BA rounds, and make no production policy promotion from smoke results.
This extension has not run the post-integration formal R9700 matrix.

## Evidence retention and validation

The comparison requires PowerShell 7. Each invocation requires an empty output
directory. Each build writes to a new Player directory and records a sorted
path/size/SHA-256 manifest for **every** payload file, including managed/native
DLLs, shaders, resources and data files. The build provenance sidecar lives
outside that payload. Source manifests include tracked and untracked build/tool
inputs. Both manifests are checked before launch and again after the Player exits;
`-SkipBuild` rejects changed sources or payloads rather than stamping the current
checkout onto an old executable.

Formal runs require clean Git state before building, and refuse a Player built
from dirty user source. Build-generated drift is recorded with before/after hashes
and exact settings text/diff. Only `ProjectSettings.asset` and
`SceneTemplateSettings.json` are allowed automatic drift; other changes fail the
gate. The checked-in timestamp DLL is used by default. `-RebuildNative` is an
explicit diagnostic option that also records the exact native DLL drift. The
Unity 6 canonical importer metadata enables that DLL in Editor and Win64 Player.

Windows `Win32_VideoController` provides exact driver versions, dates and PNP
device identities. The Player GPU name must match one controller unambiguously;
its record is attached to `environment.json`. Driver records are checked again
after measurement. `SystemInfo.graphicsDeviceVersion` is retained as graphics API
context, never substituted for the Windows driver version.

Every job, outer scope and interleaved empty control retains token/user tag,
source/result frame, scope index, status, flags, device generation, begin/end/
elapsed ticks, frequency, fence value and raw duration. Unsigned 64-bit values
are decimal strings to preserve exactness in JSON readers. Empty controls bracket
each validation and measured case outside its latency/makespan clock; no control
subtraction is applied. `empty-controls.csv` reports sample count and P50/P95/P99
per case (two controls, so this is sparse overhead evidence, not a stable tail
estimate).

`GpuRuntimeSchedulerEvidence.psm1` requires the exact scenario × round × policy ×
validation/measured matrix and identical offered workload signatures. It rejects
missing/extra cases, duplicate tokens, invalid statuses/flags/frequencies,
nonfinite durations, mismatched tick conversion or aggregates, and dispatch
timestamps outside the outer scope. Summaries retain round and phase, CPU planning
average/P99, GPU dispatch average/P99, and the prior latency/makespan definitions.
Planning time/GC scope is specifically one successful standalone `TryPrepare`
probe per dispatched job; it excludes admission, the executor's subsequent
selection/submission, native instrumentation, completion polling and serialization.
No whole-frame allocation claim follows from that counter.

CPU-only evidence regression checks are executable with
`Tools/Tests/Test-GpuRuntimeSchedulerEvidence.ps1`; they include damaged matrix,
raw tick, token, status, aggregate, stale-directory and changed-DLL cases.
