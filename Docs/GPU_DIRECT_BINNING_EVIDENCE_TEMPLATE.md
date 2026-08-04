# GPU Direct Spatial Binning Evidence

## Claim status

- Date:
- Git commit:
- Git branch:
- Unity:
- Graphics API:
- GPU:
- Driver:
- Matrix preset:
- `aBDataUsable`:
- `crossWorkloadImprovementClaimUsable`:

Do not write a performance claim unless the corresponding quality-summary
field is `1`.

## Implementation

- Production path:
  `GpuDirectSpatialBinner.Record` — clear/count/exclusive scan/prepare/scatter.
- Reference:
  portable generic histogram + portable scan + 256-thread reference
  prepare/scatter.
- Queue:
  direct graphics queue.
- Timestamp:
  native DX12 ABI 2, private completion fence.
- Measurement readback:
  zero bytes.
- Correctness:
  CPU canonical counts/offsets/per-bin membership/diagnostics hash.

## Provenance

- Runner config SHA-256:
- Player payload SHA-256:
- Player payload file count:
- Player payload bytes:
- Runtime shader SHA-256:
- Reference shader SHA-256:
- Runtime API SHA-256:
- Timestamp DLL SHA-256:
- EditMode result SHA-256:
- Source hashes stable across build:
- Player payload stable through all runs:
- Informational Player logs suppressed before scene load:
- Final worktree clean:

## Matrix results

| Scenario | Paired rounds | GPU average median | AB median | BA median | GPU P99 median | Logical resident bytes | Classification |
|---|---:|---:|---:|---:|---:|---:|---|
| `uniform-c4096` | | | | | | | |
| `hotset16-c4096` | | | | | | | |
| `uniform-c65536` | | | | | | | |

## Quality checks

- All raw timestamp rows ready:
- Unique sequential user tags:
- Strictly increasing source frames:
- Consecutive private fence values:
- Non-overlapping timestamp intervals:
- Measurement frequency equals warmup:
- Stable device generation:
- Independent tick-to-nanosecond conversion:
- Empty-scope P99 ≤ 0.005 ms:
- Four warmup/final validation rows per scenario:
- A/B canonical hashes identical:
- Measurement readback bytes = 0:
- AB-order and BA-order median improvements are nonnegative:
- Active device identity is identical across the matrix:
- Negative/neutral results retained:

## Truthful result wording

If all frozen scenarios are accepted:

> Built and correctness-validated a reusable GPU-resident
> count–scan–scatter spatial-binning primitive. On the tested AMD R9700/DX12
> system, the frozen position-balanced same-process A/B matrix observed
> [insert measured median/P99 and logical buffer figures]; all three workloads
> met the predefined engineering gate. Native ABI 2 timestamps, private
> completion fences, and canonical CPU membership validation were used with
> zero measurement readback.

If only some scenarios are accepted, name only those scenarios and do not use
“cross-workload.”

If results are neutral or negative:

> Implemented and rigorously benchmarked a reusable GPU-resident direct
> spatial-binning primitive against a transparent primitive-composed
> reference. The formal A/B found no general performance win under the tested
> AMD workloads; retained evidence was consistent with workload-dependent
> contention and/or scan-cost sensitivity, without proving a causal bottleneck.

Meeting the frozen gate means the observed data met a predefined engineering
heuristic; it is not a statistical-significance claim. Logical resident bytes
are `GraphicsBuffer` payload accounting, not driver-measured VRAM residency.
This fixed-input primitive benchmark does not establish end-to-end FPS,
dynamic sensor-pipeline, or whole-frame gains.

Never claim NVIDIA validation, async-compute overlap, driver-level
optimization, or bandwidth reduction without separate evidence.
