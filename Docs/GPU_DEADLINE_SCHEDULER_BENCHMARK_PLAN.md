# Deadline-aware async GPU scheduling benchmark

## Question

Can latency-sensitive robotics sensor jobs coexist with graphics work more
predictably when FIFO main-queue execution is replaced by least-slack-first
planning, queue-class routing, and an explicit cross-queue fence join?

## Frozen A/B

- A: copy dependency, graphics-pressure pass, then all sensor jobs in arrival
  order on the main graphics queue. The critical job intentionally arrives
  after two background jobs and one normal job.
- B: the same copy, pressure, jobs, dispatch sizes, iterations, and output
  digests. Jobs are ordered by estimated slack. A startup calibration compares
  an ordered main-queue backend against a split backend where critical and
  normal jobs enter one urgent async-compute deadline lane while background
  jobs follow graphics pressure on the main bulk lane. Async is selected only
  when it improves average makespan by at least 2% without worsening P99.

Each job owns independent output resources, so the async variant does not rely
on simultaneous cross-queue UAV writes to one resource. The copied control
buffer is fenced before compute consumption.

The calibration decision is evidence, not a platform-name allowlist. A
compute-saturated device can select main-queue EDF, while a workload/device with
real queue overlap can select the split backend.

The synthetic job cost model converts dependent integer-mix work into an
estimated duration before slack ordering. The scale is intentionally separate
from measured timestamps: incorrect cost scale can make a long background job
look overdue and invert EDF priority, so the planner accepts application-owned
cost estimates instead of hiding a device constant in policy code.

## Metrics

- Native DX12 GPU makespan from main-queue timestamp begin through async-fence
  join and timestamp end.
- Critical-job and total fence-observed deadline-miss rates.
- Critical latency P99 and complete-schedule P99.
- Paired wins under four AB/BA super-rounds.
- Correctness digest, measurement readback bytes, and queue capability claims.

Formal acceptance requires at least 50% critical P99 improvement, critical
latency wins in every counterbalanced pair, a lower critical miss rate, and no
more than 2% regression in total GPU average. Total throughput is a guardrail,
not the primary success metric.

The retained matrix covers a balanced workload near the sub-millisecond to
low-millisecond range and a compute-saturated workload near 5 ms on the local
AMD device. This prevents conclusions based only on queue overhead or only on
an unrealistically long kernel.

## Claim boundary

Unity's public command-buffer API exposes async compute queues but not a
dedicated DX12 copy-queue submission contract. This backend executes the copy
stage on the main graphics queue and reports `copyQueueClaim=false`. It does not
present the benchmark's synthetic ALU workload as a sensor-fidelity result.
