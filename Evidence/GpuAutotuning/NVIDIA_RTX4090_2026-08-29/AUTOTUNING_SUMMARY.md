# Cross-vendor GPU autotuning

Acceptance: **accepted**

Device: **NVIDIA GeForce RTX 4090** / Direct3D12

| Workload | Selected | Calibration WaveOps delta | Evaluation WaveOps avg | Evaluation WaveOps P99 | WaveOps wins | Confirmed |
|---|---|---:|---:|---:|---:|---:|
| exclusive-scan | WaveOps | 33.33% | 38.86% | 37.50% | 4/4 | 1 |
| radix-sort-32 | Portable | 0.95% | 1.15% | 1.01% | 4/4 | 1 |
| stable-compaction | WaveOps | 29.27% | 29.38% | 28.57% | 4/4 | 1 |

Calibration and evaluation use disjoint rounds. WaveOps is selected only when it clears the improvement and P99 gates; otherwise the validated profile keeps the portable backend. A mismatched or missing device profile falls back to capability-based Auto selection.
