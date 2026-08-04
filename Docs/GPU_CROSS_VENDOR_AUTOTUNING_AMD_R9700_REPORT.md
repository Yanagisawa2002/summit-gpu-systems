# Device-keyed GPU Autotuning: AMD Radeon AI PRO R9700

## Outcome

The formal AMD run accepted a WaveOps backend for exclusive scan, 32-bit radix
sort, and stable compaction. Selection used only calibration rounds 1-2; the
performance result below comes from untouched rounds 3-6.

| Workload | Selected backend | Evaluation GPU average | Evaluation GPU P99 | Evaluation wins |
|---|---|---:|---:|---:|
| Exclusive scan | WaveOps | 29.90% faster | 20.49% faster | 4/4 |
| Radix sort 32 | WaveOps | 15.33% faster | 13.93% faster | 4/4 |
| Stable compaction | WaveOps | 24.72% faster | 23.92% faster | 4/4 |

The run produced 37,800/37,800 valid native DX12 timestamp samples, 12/12
candidate correctness validations, zero measurement readback bytes, and a
416/416 passing EditMode test result.

## What was implemented

- A stable GPU fingerprint containing vendor/device IDs, graphics API, device
  name, and shader level.
- A versioned JSON profile with per-workload backend selections and calibration
  distributions.
- A selector that requires output validation, a minimum median improvement, and
  a P99 guardrail before replacing the portable baseline.
- A runtime resolver that rejects stale or device-mismatched profiles and falls
  back to `GpuPrimitiveBackend.Auto`.
- An atomic profile store under Unity's persistent data path.
- A hardware-indexed runner that uses the same protocol for AMD and NVIDIA.

This changes the decision from “the API supports WaveOps, therefore use it” to
“the exact GPU and workload measured this validated implementation as faster.”

## Method

- Hardware: AMD Radeon AI PRO R9700, 32,476 MiB; Ryzen 9 9950X.
- API: Direct3D 12, feature level 12.2.
- Unity: 6000.5.2f1.
- 1,048,576 elements, portable and WaveOps candidates, one dispatch per frame.
- Six counterbalanced rounds with 60 warm-up and 900 measured frames per block.
- Rounds 1-2: calibration, 1,800 samples per candidate and workload.
- Rounds 3-6: independent evaluation, four comparisons per workload.
- Formal gates: 3/4 positive average wins, at least 3% and 0.005 ms median
  GPU-average reduction, median GPU-P99 no worse than -5%, exact output hashes,
  and zero measurement readback.

Compact evidence is retained under
`Reports/GpuAutotuning/formal-amd-r9700-bd60f3e-v1`. Raw per-frame samples and
large logs remain in the external benchmark fixture directory.

## Claim boundary

The infrastructure is vendor-neutral, but the performance profile and results
are validated only on AMD Radeon AI PRO R9700. NVIDIA execution is implemented
through the same device-indexed runner and portable fallback, but remains pending
until NVIDIA hardware is available. Do not claim NVIDIA validation yet.

This benchmark tunes between portable group-shared HLSL and WaveOps algorithm
backends. It does not yet tune driver settings, compiler flags, or 64/128/256
thread-group variants.

## Resume-ready wording

Built a device-keyed GPU autotuning pipeline for DX12 compute primitives with
validated portable fallback, disjoint calibration/evaluation sets, P99
guardrails, and persistent per-workload profiles. On AMD Radeon AI PRO R9700,
autotuning selected WaveOps for scan, radix sort, and stable compaction; an
independent 4x900-frame evaluation improved GPU average by 15.3-29.9% and GPU
P99 by 13.9-23.9%, with 4/4 wins per workload and 37,800/37,800 valid native
timestamp samples.
