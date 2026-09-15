# Linux monitoring and ordinary GPU: source preparation closeout

**Source preparation complete; hardware validation pending.** This is the second
2026-09-15 preparation pass, following `SOURCE_PREPARATION_CLOSEOUT.md`. It is not
a terminal whole-task handoff. At 09:21:14 UTC, the coordinator granted SUMMIT
generation 2 after independently verifying Data Layout's release. The source
checkpoint below precedes remote execution; later device receipts must be read
separately. CUDA execution and formal performance are not in that grant.

## What now exists

| Area | Prepared implementation | Evidence still required |
| --- | --- | --- |
| Linux timing qualification | cgroup-v2 quota/cpuset/visible ancestor limits, affinity/SMT counters, owned live/reaped CPU accounting, throttling, memory headroom/events, assigned GPU utilization/VRAM/processes, raw failure retention | Actual host mount/counter support, observed collection cost, PID mapping and live calibration |
| Calibration gate | Corroborate actual validated CUDA PID/UUID, monotonic task-clock relationship, task/sample overlap, CPU accounting and noise; bind source, build and complete outputs; re-evaluate retained records | A real admitted CUDA calibration process; offline fixtures do not qualify |
| Ordinary GPU control | Shared point tiles, exactly 128/256 team sizes; common CPU snapshot, inclusive float radius and original-ID self exclusion; unchanged force/update source blocks | Compilation, actual 5090 execution, full numerical acceptance and later timing discovery |
| Bounded GPU output audit | Complete original-snapshot CSR count/scan/scatter outside the task clock; 64 MiB ID cap; partial-team barriers; explicit per-tile one-ID capacity rejection before scatter | Both observed capacity checks plus complete boundary/empty/tail/full-case state checks |
| Independent numerical oracle | Binary32 rounding after each operation, complete scalar state, full all-pairs membership <=4000 particles, one oracle comparing both tiles' complete first/last files | Real exported inputs and real CUDA outputs; no real MD case has been run |
| Freeze integration | Mandatory scalar/capacity receipts for selected tiles; clean candidate/build identities, complete saved output rechecks and actual calibrated Linux monitor | A reviewed discovery-backed confirmation plan and completed validated artifacts |
| SUMMIT query-buffer audit | Precise public API ownership, CPU build readiness and later-consumer fence requirements documented | SUMMIT Unity GPU force consumer implementation and Linux Player/graphics capability |

The ordinary GPU control is an external comparison. It is not a new native
SUMMIT backend. The current SUMMIT Unity arms still return full CSR to the CPU
and perform the consumer there. An external CUDA PASS cannot become a SUMMIT
PASS, even if it is faster than the CPU controls.

## Important implementation boundaries

* Actual capacity is bounded by cgroup quota and affinity. A 25-core time quota
  with 208 visible CPUs is treated as at most 25 cores, not 208. Visible ancestor
  throttling/events are checked as well. CPU accounting subtraction only
  classifies background work; it never subtracts monitoring from task latency.
* A failed or unavailable telemetry field, zero elapsed CPU ticks, accounting
  reset, unexplained GPU activity, unknown PID, changed controls or insufficient
  sampling coverage rejects formal timing. The first observation is checked
  too. All rows and raw GPU parser failures are retained.
* Same-PID GPU ownership is accepted only after a real CUDA process and native
  UUID/PID output corroborate NVML records. Host/container remapping is not
  guessed. Cgroup ancestors hidden by the container and unsampled bursts remain
  limitations; the monitor does not claim exclusive physical CPU ownership.
* Hash/build revalidation can take longer than one sampling period. It is kept
  in a separate preparation phase, then followed by a full fresh quiet window.
  That time is not mislabelled as an idle sampled interval or an application task.
* CPU workers and CUDA backend/PID/UUID are checked from actual native output,
  not only command-line intent. The first parallel control requests 8 workers;
  the planned builds use 4 workers under the reported 25-core quota.
* Timed ordinary GPU execution fuses neighbour visits with force accumulation
  and then performs the exact original update. The complete CSR audit runs from
  the original snapshot afterward. Its cost is recorded separately and may
  affect subsequent iterations; discovery must assess that before confirmation.
* The adapter's own fence precedes an appended force reader. Safe future Unity
  integration retains its CPU build-stamp completion and uses a later consumer
  fence before overwriting query/output buffers. Those waits count inside the
  complete task and may make this API unsuitable for this consumer.

See `QUERY_BUFFER_LIFETIME_AUDIT.md` for verified source lines and the initial
safe ownership sequence. No existing public API was changed by this preparation.

## Lightweight checks and evidence

The local Python suite exercises only tiny synthetic records/files and short
arithmetic fixtures. It checks missing/stale build products, extraction identity,
quota-versus-cpuset accounting, process exit races, noise at either observation
endpoint, unavailable counters, CPU/memory throttling events, invalid GPU mapping,
task sampling coverage, retained calibration preparation, requested versus actual
execution space/workers, bounded-output rejection identity and modified complete
state files. Synthetic calibration arithmetic does not write a calibrated host
receipt. No compiler or native/Unity/CUDA task is executed by these tests.

The final source-check output, Python 3.10 syntax parsing, PowerShell parsing,
CLI-help import results and exact source hashes are retained under
`Artifacts/whole-task-md-20260915/source-preflight-linux-tiled/`. The result
manifest is `checks.json`; raw unit output is `tests.log`. The earlier portable
check directory remains untouched and describes only its earlier source state.

The extracted header in `generated-tiled-preflight/` has SHA256
`c3c4243ab85ca9139f67c4126a16f3c634642cc41bd9cfdcf1056090ac4d2d06`.
The audited upstream source remains
`fc292b286270367980fbb1cc99a4944230138b064d4075c264472b7690828b16`.
Both older generated-preflight directories remain intact. These headers and
source checks do not prove C++/CUDA API or toolchain compatibility.

## Next reviewable grant

`NEXT_LINUX_STAGE_COMMANDS.md` contains exact pinned dependencies, task-private
paths, existing-runtime reuse rules, archive/expansion limits, 4-worker builds,
8-worker CPU correctness, per-stage time/storage ceilings, full per-arm matrices,
capacity checks and scalar acceptance commands. Capability/Serial/OpenMP/CUDA
correctness comes first. A fixed live-monitor calibration pilot is explicitly
deferred to a separate grant; no formal performance starts automatically.

No remote login, dependency download, actual build, numerical experiment or
performance run has occurred in this task. There is no frozen campaign, winner,
speedup, hardware-release claim or terminal handoff. All actual device correctness,
monitoring overhead, GPU/CPU timing, SUMMIT GPU consumer and Linux Player evidence
remain pending. Check current coordinator status for ownership before executing.
