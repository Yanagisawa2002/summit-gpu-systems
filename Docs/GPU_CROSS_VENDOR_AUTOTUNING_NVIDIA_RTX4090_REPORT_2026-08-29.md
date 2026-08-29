# Device-Keyed GPU Autotuning — NVIDIA GeForce RTX 4090

## Outcome

The formal NVIDIA run accepted a mixed device profile. Calibration rounds 1–2 selected WaveOps for exclusive scan and stable compaction, but retained Portable for radix sort. Untouched rounds 3–6 independently confirmed all three decisions.

| Workload | Selected backend | Calibration WaveOps delta | Independent WaveOps average | Independent WaveOps P99 | Decision confirmed |
|---|---|---:|---:|---:|---:|
| Exclusive scan | WaveOps | `+33.33%` | `+38.86%` | `+37.50%` | Yes |
| Radix sort 32 | Portable | `+0.95%` | `+1.15%` | `+1.01%` | Yes |
| Stable compaction | WaveOps | `+29.27%` | `+29.38%` | `+28.57%` | Yes |

The radix WaveOps candidate won all four evaluation pairs directionally, but its `1.15%` median improvement remained below the frozen `3%` upgrade threshold. The accepted profile therefore avoided switching backends for a marginal effect.

## Generalization implemented

The earlier formal selector assumed every workload must replace the portable backend. That assumption worked on the measured AMD device but could not represent a valid mixed NVIDIA profile. The selector now:

- applies one explicit calibration upgrade gate to WaveOps;
- permits Portable to remain selected when WaveOps is below the gate;
- evaluates both candidates on disjoint rounds regardless of the selected backend;
- accepts a profile only when the independent evaluation confirms the calibration decision;
- marks a calibration/evaluation disagreement as unaccepted so runtime resolution falls back safely;
- records the measured vendor/device/API instead of hard-coding NVIDIA as unvalidated.

Synthetic positive, portable-retention, and calibration-mismatch tests cover the three decision paths.

## Method and gates

- Source: `43ffac93ff549afd39675b34efb958991a8f2da9`
- Unity: `6000.5.2f1`
- GPU/API: NVIDIA GeForce RTX 4090 / Direct3D 12
- Workload: 1,048,576 elements, one dispatch per frame
- Six counterbalanced rounds, 60 warm-up and 900 measured frames per block
- Rounds 1–2: 1,800 calibration samples per backend/workload
- Rounds 3–6: four independent evaluation pairs
- Upgrade gate: at least 3/4 positive wins, at least `3%` and `0.005 ms` median average reduction, and median P99 no worse than `-5%`

## Evidence quality

- Formal acceptance status: `accepted`
- `37,800/37,800` native timestamp samples valid
- `12/12` candidate correctness validations passed
- Algorithm measurement readback: `0` bytes
- Full D3D12 EditMode suite: `511/511` passed, zero skipped
- Device-keyed profile: vendor `0x10DE`, device `0x2684`, Direct3D 12
- Worktree clean at the measured source commit

Compact evidence is retained in `../Evidence/GpuAutotuning/NVIDIA_RTX4090_2026-08-29`.

## Claim boundary

The profile validates three workloads at one element count on one RTX 4090 driver. It is not a universal NVIDIA profile and does not tune driver settings, compiler flags, thread-group size, or arbitrary kernels. Unknown or mismatched devices still use capability-based Auto selection until their own profile is measured.

## Resume-ready wording

Designed a device-keyed GPU autotuner with disjoint calibration/evaluation splits, correctness hashes, P99 guardrails, and safe fallback. Cross-vendor validation produced hardware-specific policies: AMD selected WaveOps for scan/radix/compaction, while RTX 4090 selected WaveOps for scan and compaction but retained portable radix; the NVIDIA evaluation delivered `29.4–38.9%` GPU-average gains on selected WaveOps workloads across `37,800` valid native timestamp samples.
