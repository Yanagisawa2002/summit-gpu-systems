# Cross-vendor GPU Autotuning Benchmark Plan

## Purpose

Replace a capability-only backend choice with measured, device-keyed selection.
The same pipeline accepts AMD and NVIDIA device fingerprints, retains portable
fallbacks, and refuses stale profiles. Current hardware permits AMD calibration;
NVIDIA validation remains explicitly pending.

## Candidates and workloads

- Candidates: portable group-shared HLSL and Shader Model 6 WaveOps.
- Workloads: exclusive scan, stable compaction, and 32-bit radix sort.
- Baseline: portable backend.
- Candidate correctness: warm-up and final GPU readback hashes must match for
  both backends before either result can be selected.

## Evidence separation

Formal measurement uses six counterbalanced rounds. Rounds 1-2 are calibration;
rounds 3-6 are an untouched evaluation set. Native DX12 timestamp samples are
used for selection and evaluation, with no readback during measured frames.

## Frozen formal gates

- Exactly three workloads and four evaluation comparisons per workload.
- At least three of four evaluation GPU averages improve.
- Median GPU-average improvement is at least 3% and 0.005 ms absolute.
- Median GPU-P99 regression is no worse than 5%.
- All correctness validation passes and measurement readback is zero.

The generated profile is keyed by vendor ID, device ID, graphics API, GPU name,
and shader level. Missing or mismatched profiles fall back to
`GpuPrimitiveBackend.Auto`.
