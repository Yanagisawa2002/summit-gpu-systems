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
