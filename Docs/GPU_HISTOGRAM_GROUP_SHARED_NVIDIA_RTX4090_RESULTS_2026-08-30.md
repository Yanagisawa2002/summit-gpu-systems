# Histogram-16 Group-Shared Results — NVIDIA GeForce RTX 4090

## Outcome

The small-domain Portable histogram now aggregates 16 bins in group-shared
memory and emits at most one global atomic per non-empty group/bin. Across the
six frozen RTX 4090 cells, the final Portable path reduced the measured native
GPU scope by `58.86%–98.12%` (`0.009984–0.537862 ms`) versus the previous
one-global-atomic-per-key implementation. All six cells passed the frozen
`5% + 0.005 ms` gate and improved GPU P99 by `55.56%–98.96%`.

The baseline also falsified the earlier broad interpretation that WaveOps was
always a bad histogram backend. WaveOps lost on uniform input but won strongly
on the old global-atomic path under hotset and single-bin contention. The new
group-shared Portable path beat the retained WaveOps implementation in every
measured cell, so histogram `Auto` now selects Portable conservatively. A
device/workload profile may still force WaveOps explicitly.

## Frozen environment and protocol

- baseline harness commit: `de16eeda12d58ead910ae5daeb7f50f6f600b018`
- group-shared implementation commit: `3bc44a32477c025f349a45ac43fd95d8bef89801`
- final selector commit: `a9a1694341491cf7ccd319122b7cb73eefff0018`
- device: NVIDIA GeForce RTX 4090
- driver: `32.0.15.9186`
- Unity: `6000.5.2f1`
- graphics API: Direct3D 12
- element counts: `65,536` and `1,048,576`
- distributions: `uniform-16`, `hotset-4`, and `single-bin`
- seed: `20260830`
- per cell: four counterbalanced rounds, 60 local warmup frames, 900 measured
  frames, 15 cooldown frames, one dispatch per frame
- timing: native D3D12 timestamp query around the pre-recorded command buffer;
  no measurement readback

## Results

Positive percentages mean lower GPU time than the old Portable global-atomic
path. Times are medians of the four round-level GPU means.

| Elements | Distribution | Old Portable | Old Wave vs old Portable | Final Portable | Final improvement | Absolute reduction | Final P99 improvement |
|---:|---|---:|---:|---:|---:|---:|---:|
| 65,536 | uniform-16 | `0.016962 ms` | `-19.95%` | `0.006978 ms` | `+58.86%` | `0.009984 ms` | `+55.56%` |
| 65,536 | hotset-4 | `0.023484 ms` | `+52.68%` | `0.007587 ms` | `+67.69%` | `0.015897 ms` | `+64.58%` |
| 65,536 | single-bin | `0.034501 ms` | `+78.96%` | `0.008682 ms` | `+74.84%` | `0.025819 ms` | `+65.71%` |
| 1,048,576 | uniform-16 | `0.203278 ms` | `-35.91%` | `0.016162 ms` | `+92.05%` | `0.187116 ms` | `+95.09%` |
| 1,048,576 | hotset-4 | `0.296572 ms` | `+78.73%` | `0.010648 ms` | `+96.41%` | `0.285923 ms` | `+98.64%` |
| 1,048,576 | single-bin | `0.548193 ms` | `+96.18%` | `0.010331 ms` | `+98.12%` | `0.537862 ms` | `+98.96%` |

## Correctness, selection, and evidence

- final D3D12 EditMode suite: `766/766`, zero skipped or failed
- baseline matrix: 64,800 measured rows and 24/24 validations
- candidate Portable/Wave matrix: 64,800 measured rows and 24/24 validations
- final Portable/Auto matrix: 64,800 measured rows and 24/24 validations
- total: 194,400 measured rows and 72/72 benchmark validations
- Portable and Auto produced identical validation hashes in all six selected
  cells; the source routes both through the same group-shared kernel
- baseline Player payload SHA-256:
  `9B2A42BA9D3D0344E501E37FE121B507FB1FA2BDCC05D068332700BA4F7F3C42`
- candidate Player payload SHA-256:
  `921F0FF93E8167AD3881D7A9CB87C319303F37DA3DBA93D00DC2999BDF90B37B`
- final selected Player payload SHA-256:
  `6B2073EE3CCDAC92744AB8B6AB1C9E66E95036D3334AB387B2E07B71C7E4C1BE`
- canonical six-file raw-manifest hashes:
  - baseline: `A656D76E001B90DB895CF3A8355C434880F18EFF43346F294754A0C82A8B0670`
  - candidate: `2532BA31BDA03FA4251280D184A0095CA2DC056967F0C6E775E07739B4A5737C`
  - final selected: `4F04308A7EF80477B60B741CF3763EB34D71B1EB4E01AF7489D20240C4E321B0`
- full EditMode XML SHA-256:
  `E305B0203EF07B0D4C9267AEB473C59AEFFED5AC4D5687886FBD26D76BFFA78F`

Local artifact prefixes are `t04-hist-baseline-de16eed-*`,
`t04-hist-candidate-3bc44a3-*`, and `t04-hist-auto-a9a1694-*` under the
repository-level `artifacts` directory.

## Evidence boundary

- These are primitive-scope native GPU timings, not frame time, FPS, CPU
  submission, or a visible-quality result.
- The mechanism is validated only for 16-bin domains on one RTX 4090 and one
  D3D12 driver. Larger domains still use the old Portable global-atomic path.
- AMD was not available for this rerun. The historical Radeon AI PRO R9700
  uniform result remains negative for the old Wave implementation, but it does
  not validate this new group-shared kernel.
- The old-versus-new mechanism comparison uses separately built Players at two
  source commits with an otherwise frozen environment and protocol. The final
  Portable-versus-Auto selection check is same-process.
- No universal WaveOps, cross-vendor, whole-engine, or visually perceptible
  speedup is claimed.
