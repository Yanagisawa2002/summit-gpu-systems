# Deadline-aware GPU scheduling: AMD R9700 report

## Outcome

A portable least-slack-first planner, explicit copy dependency, urgent
async-compute deadline lane, main graphics/bulk lane, and cross-queue fence join
were implemented in an independent Unity package and benchmark Player.

The AMD Radeon AI PRO R9700 calibration rejected async admission for both
formal workloads because the kernels already saturated compute resources. The
optimized A/B therefore used least-slack-first ordering on the main queue. This
is the intended adaptive behavior, not a disabled feature: the async backend
was executed and timed during startup calibration, then rejected by average and
P99 guardrails.

## Formal 4 x 900-frame A/B

| Workload | Selected backend | Total GPU average | Total GPU P99 | Critical P99 | Critical miss rate | Critical paired wins |
|---|---|---:|---:|---:|---:|---:|
| Balanced, 131,072 work items | least-slack main | -1.61% | +29.59% | +88.92% | 0.83% to 0.00% | 8/8 |
| Saturated, 262,144 work items | least-slack main | +0.18% | -1.48% | +77.55% | 2.64% to 0.014% | 8/8 |

Positive percentages denote improvement. The balanced workload traded 1.61%
of average total GPU time for a much lower deadline tail; this remained inside
the frozen 2% throughput guardrail. The saturated workload was effectively
throughput-neutral.

The calibration measurements that selected the backend were:

| Workload | EDF main average | EDF split-async average | Decision |
|---|---:|---:|---|
| Balanced | 0.8984 ms | 1.6628 ms | main queue |
| Saturated | 5.1241 ms | 5.3998 ms | main queue |

The result is a latency/service-level optimization, not a claim of large
throughput acceleration.

## What changed

The FIFO baseline records the copy dependency, graphics pressure, two
background jobs, one normal job, and finally the critical job on the main
graphics queue. The optimized policy retains the same dispatches, work sizes,
iterations, resources, and digests, but uses predicted cost and deadline to
sort by estimated slack. Graphics pressure keeps its place; the critical and
normal jobs move ahead of background sensor work.

Before the formal run, a counterbalanced calibration compares EDF-main against
a split backend. In the split backend, critical and normal jobs run on an
urgent async-compute queue while graphics pressure and background jobs run on
the main bulk lane. The main queue timestamps the full GPU interval from copy
start through async-fence join. Async is admitted only if it improves average
makespan by at least 2% without worsening P99.

Each job owns separate UAV output and digest buffers, avoiding unsafe
cross-queue writes to one resource. A copied resident control buffer is fenced
before compute consumption.

## Correctness and evidence

- GPU: AMD Radeon AI PRO R9700, Direct3D 12 feature level 12.2.
- Schedule: four AB/BA super-rounds, 30 warmups per block, and 900 measured
  samples per block.
- Timing: 28,800 native DX12 makespan samples across the two workloads.
- Correctness: all 40 formal validation checkpoints passed; every GPU job
  digest matched the portable CPU oracle.
- Tests: 417/417 Unity EditMode tests passed on the DX12 device.
- Measurement-time workload readback: zero bytes.
- Retained evidence:
  `Reports/GpuDeadlineScheduler/formal-amd-r9700-9f6bbd1-v1`.

## Claim boundary

Unity's public command-buffer API does not expose a dedicated DX12 copy-queue
submission contract. The copy stage is explicit but uses a main-graphics queue
fallback, so `copyQueueClaim=false`. The formal optimized blocks selected the
main-queue backend; they must not be described as async-compute speedups. There
is no NVIDIA validation yet.

## Resume-ready engineering statement

Built a deadline-aware GPU task scheduler for robotics sensor workloads with
least-slack-first planning, copy-to-compute dependencies, an urgent async lane,
and cross-queue DX12 fence joins. Added runtime calibration that rejected
counterproductive async admission on compute-saturated RDNA 4 and selected a
same-queue EDF fallback. In a formal 4 x 900-frame A/B, the selected policy cut
critical-job P99 by 77.55%-88.92%, won all 16 paired critical comparisons, and
reduced deadline misses from 0.83%-2.64% to 0%-0.014%, while keeping total GPU
average within a 1.61% guardrail.
