# GPU Primitives Formal Results — NVIDIA GeForce RTX 4090

## Outcome

A formal same-process Direct3D 12 run on NVIDIA GeForce RTX 4090 produced performance-usable evidence for all five portable-versus-WaveOps comparisons. Exclusive scan and stable compaction cleared the frozen improvement gate. Radix sort was directionally positive but below the gate; append compaction and histogram-16 were negative.

| Operation | WaveOps GPU average | WaveOps GPU P99 | Paired average wins | Classification |
|---|---:|---:|---:|---|
| Exclusive scan | `+38.81%` | `+37.50%` | `3/3` | Accepted |
| Stable compaction | `+29.35%` | `+28.57%` | `3/3` | Accepted |
| Radix sort 32 | `+1.42%` | `+0.85%` | `3/3` | Neutral or inconclusive |
| Append compaction | `-1.43%` | `0.00%` | `1/3` | Negative |
| Histogram 16 | `-31.36%` | `-8.95%` | `0/3` | Negative |

Positive values mean WaveOps was faster than the portable group-shared implementation. Percentages are paired medians across the three counterbalanced rounds.

## Method

- Source: `bb98dbb7c44428ce6dccf3d6327f2ecbd3be7ffc`
- Unity: `6000.5.2f1`
- GPU: NVIDIA GeForce RTX 4090, 24,138 MiB reported by Unity
- Driver: NVIDIA `591.86` (`32.0.15.9186` through WMI)
- API: Direct3D 12, feature level 12.2
- Workload: 1,048,576 elements, one dispatch per frame
- Schedule: three counterbalanced rounds; 60 local warm-up, 900 measured, and 15 cooldown frames per block
- Timing: native D3D12 timestamp queries around the submitted command regions
- Correctness: CPU oracle and identical portable/WaveOps result hashes before and after measurement

## Evidence quality

- `29,700/29,700` native timestamp samples were ready and internally consistent.
- `20/20` warm-up/final correctness rows passed.
- Algorithm measurement readback was `0` bytes; timestamp instrumentation read back 16 bytes per completed sample.
- The dedicated ABI/fail-closed EditMode suite passed `48/48` with zero skips.
- A separate full D3D12 repository run passed `511/511` with zero skips.
- One Player PID was used, order was counterbalanced, source and Player hashes stayed stable, and the worktree was clean at start and finish.
- Empty-scope P99 was `0.000000 ms`, below the `0.005 ms` gate; overhead was not subtracted.

Compact evidence is retained in `../Evidence/GpuPrimitives/NVIDIA_RTX4090_2026-08-29`. The excluded raw frame CSV contained 29,700 rows and is represented by the runner, summary, and retained artifact hashes.

## Cross-vendor interpretation

| Operation | AMD R9700 | NVIDIA RTX 4090 | Supported conclusion |
|---|---:|---:|---|
| Exclusive scan | `+29.70%` | `+38.81%` | WaveOps accepted on both measured devices |
| Stable compaction | `+26.57%` | `+29.35%` | WaveOps accepted on both measured devices |
| Radix sort 32 | `+16.82%` | `+1.42%` | Backend choice is hardware-specific |

The NVIDIA result rejects a universal “WaveOps is faster” rule. Capability checks remain necessary but are insufficient; the selected backend must be keyed to measured device/workload evidence.

## Claim boundary

These values measure the named command regions, not whole-application frame time. They do not establish driver-level tuning, universal performance across element counts or distributions, or performance on GPUs other than the two reported devices. Negative and neutral rows are retained and must not be presented as wins.

## Resume-ready wording

Built and cross-vendor validated DX12 GPU primitives with deterministic CPU oracles, counterbalanced same-process A/B tests, and native timestamp instrumentation. On RTX 4090, WaveOps improved 1M-element exclusive scan by `38.8%` and stable compaction by `29.3%`; retained the portable radix path after WaveOps failed the frozen improvement gate, demonstrating hardware-aware rather than capability-only optimization.
