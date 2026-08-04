# Shared GPU spatial index for multiple sensors: AMD R9700 report

## Outcome

One dynamic GPU-resident CSR spatial index is now built once per frame and
consumed by multiple segmented sensor-query ranges. The portable comparison
path rebuilds the same index independently for every sensor. Both paths use
the same producer, query data, output layout, and frame digest.

On an AMD Radeon AI PRO R9700 under Direct3D 12, the formal 4 x 900-frame
counterbalanced A/B produced the following results:

| Workload | Rebuilt-per-sensor GPU avg | Shared-index GPU avg | GPU avg improvement | GPU P99 improvement | Paired GPU wins |
|---|---:|---:|---:|---:|---:|
| 1,048,576 elements, 4 sensors, 64 queries/sensor | 0.35051 ms | 0.11086 ms | 68.37% | 66.16% | 8/8 |
| 262,144 elements, 4 sensors, 32 queries/sensor | 0.14014 ms | 0.04849 ms | 65.40% | 67.99% | 8/8 |

The corresponding average frame-time improvements were 70.01% and 40.88%.
All 20 validation checkpoints passed, measurement-time workload readback was
zero, and all 32,400 measured GPU regions returned native DX12 timestamps.

## What changed

The baseline records `producer -> count -> scan -> scatter -> queries` once
per sensor. The optimized path records `producer -> count -> scan -> scatter`
once, then dispatches one query segment per sensor against the same CSR cell
offset and element-index buffers. `_QueryStart` makes each dispatch operate on
its assigned query range without copying query or result data.

This is producer-consumer reuse, not a reduced-quality approximation. It
removes redundant index construction while retaining exact output digests.

## Residency projection

| Workload | Independent index residency | Shared index residency | Bytes avoided |
|---|---:|---:|---:|
| 1,048,576 elements, 4 sensors | 80,249,024 B | 20,062,256 B | 60,186,768 B |
| 262,144 elements, 4 sensors | 42,500,288 B | 10,625,072 B | 31,875,216 B |

The A/B harness deliberately reuses its physical buffers sequentially so both
variants can alternate in one process. Therefore these residency values are an
isolated-deployment projection derived from the exact buffer contract, not a
claim about the benchmark process working set.

## Measurement contract

- GPU: AMD Radeon AI PRO R9700, 32,476 MiB reported graphics memory.
- API: Direct3D 12, feature level 12.2.
- CPU: AMD Ryzen 9 9950X.
- Schedule: four AB/BA super-rounds, 60 warmup frames, 900 samples per block,
  and 15 cooldown frames.
- Timing: native DX12 timestamp queries on the main graphics command list.
- Correctness: CPU/GPU digest comparison before, after, and between every
  measured pair; 66/66 Unity EditMode tests passed.
- Evidence: `Reports/GpuMultiSensorSharedIndex/formal-amd-r9700-614402b-v1`.

## Claim boundary

This result validates shared-index reuse on one AMD RDNA 4 device. It does not
claim NVIDIA validation, async-compute overlap, copy-queue coverage, sensor
fidelity, or end-to-end application FPS. Those require separate experiments.

## Resume-ready engineering statement

Implemented and validated a GPU-resident CSR spatial index shared by four
sensor consumers, replacing four producer/count/scan/scatter builds with one
build and segmented GPU queries. A formal counterbalanced DX12 benchmark on
AMD RDNA 4 improved GPU average time by 65.40%-68.37% and GPU P99 by
66.16%-67.99%, won all 16 paired comparisons across two workloads, preserved
exact CPU/GPU digests, and reduced projected isolated index residency by
31.9-60.2 MB.
