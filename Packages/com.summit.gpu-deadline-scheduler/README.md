# SUMMIT GPU Deadline Scheduler

This package separates portable scheduling policy from Unity queue submission.
`GpuDeadlinePlanner` produces a deterministic FIFO or least-slack-first plan;
the application maps that plan to graphics, urgent compute, default compute,
and background compute queues according to runtime capability.

`GpuDeadlineWorkload` is a deterministic benchmark workload. It exposes an
explicit copy dependency, independent compute jobs, a graphics-pressure pass,
and small per-job digests for correctness checks. It is not a production sensor
model.

The public Unity backend deliberately reports no dedicated copy-queue claim.
Its copy stage is submitted on the main graphics queue and fenced before async
compute begins.

## Bounded runtime API (vNext)

`GpuRuntimeScheduler` adds persistent admission, aging, DAG dependencies and an
in-flight submission limit. `GpuRuntimeCostEstimator` learns bounded costs from
delayed per-dispatch GPU timestamps. `GpuRuntimeQueueExecutor` executes prepared
jobs with reusable Unity command buffers and explicit cross-queue fences.
The legacy `GpuDeadlinePlanner.BuildPlan` API remains unchanged for the original
frozen benchmark; its queue suggestions are not measured async admission.

See `Docs/GPU_DEADLINE_SCHEDULER_RUNTIME.md` in the repository for lifetimes,
backpressure/retry semantics, measurement requirements and comparison commands.
