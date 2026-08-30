# GPU-driven single-bin visible-only fast path — RTX 4090 result

## Outcome

Commit `8e15414acb04c9f1f8e79cdd97582a2a70d79a07` replaces the general
CSR construction used by the visible-only `1 view / 1 draw group` shape with
two dispatches: initialization, then group-shared visibility compaction with
one global span reservation per thread group. Public counts, terminal offset,
indirect arguments, diagnostics, and unordered visible membership are
unchanged.

The implementation is a useful forced-GPU improvement, but the frozen T05
system decision is **NO-GO**. A fresh single-cell comparison showed that the
fast path reduced GPU-path CPU submission P95 from `0.042005 ms` to
`0.033500 ms` (`20.25%`) and native GPU-region P99 from `0.033792 ms` to
`0.022528 ms` (`33.33%`). In the complete four-cell formal matrix, however,
the fast GPU path was still slower than the same-process engine-native GPU
scope in the smallest cell, and the unchanged `10K / 4 views / 8 groups`
cell also missed the frozen relative tail guardrail.

| Workload | CPU submission P95 saving | Native GPU P99 CPU/GPU | GPU delta | Gates |
|:---|---:|---:|---:|:---|
| 10K, 1 view, 1 group | 85.94%, 0.236605 ms | 0.014336 / 0.021504 ms | +50.00% | CPU pass / GPU miss |
| 100K, 1 view, 1 group | 98.04%, 1.572100 ms | 0.304128 / 0.061440 ms | -79.80% | pass / pass |
| 10K, 4 views, 8 groups | 83.11%, 0.450200 ms | 0.036864 / 0.056320 ms | +52.78% | CPU pass / GPU miss |
| 100K, 4 views, 8 groups | 98.26%, 5.151435 ms | 2.967624 / 0.900106 ms | -69.67% | pass / pass |

CPU submission passed `4/4` cells. Native GPU P99 passed `2/4`, so the
unchanged formal rule failed closed.

## Why the old path paid fixed cost

The general visible-only path records diagnostic clear, classification,
bin-count clear, count, exclusive scan, write-head preparation, scatter, and
indirect-argument construction. For one bin, almost all of that work is
metadata orchestration rather than useful classification.

The fast path instead:

1. clears the single count, two offsets, diagnostics, and invariant draw words
   in one initialization dispatch;
2. classifies instances while each 256-thread group performs a shared-memory
   visibility prefix;
3. reserves one global output span per group and updates the count, terminal
   offset, and indirect instance count without a scan or scatter pass.

Injected shaders lacking both optional kernels retain the general path. The
optimization performs no CPU readback, no timed allocation, and no per-frame
buffer upload.

## Candidate-2 fallback proof

The planned second candidate was to select the engine-native path for small
cells. It cannot satisfy this measured matrix without another rendering
mechanism:

- falling back either one of the two GPU-miss cells leaves the other GPU miss;
- falling back both makes all GPU-tail comparisons safe, but removes the CPU
  submission win from two cells, leaving only `2/4` CPU gates where the frozen
  contract requires at least `3/4`.

No selector implementation or threshold search was run because the available
choices cannot meet the declared acceptance rule. The correct current policy
boundary is therefore engine-native for the smallest latency-sensitive shape,
GPU-driven for the validated higher-cardinality shapes, and no claim that this
alone makes the original four-cell system matrix GO.

## Evidence and correctness

- Unity `6000.5.2f1`, Direct3D 12, NVIDIA GeForce RTX 4090, seed `20260830`.
- Targeted D3D12 integration tests: `18/18`, zero skipped.
- Full D3D12 EditMode suite: `773/773`, zero skipped.
- Fresh general/fast single-cell runs: `18,000` measured rows and `8/8`
  validations total.
- Formal candidate matrix: `36,000` measured rows and `16/16` validations;
  result/image hashes, native timestamps, required CPU frame tails, Player
  payload stability, source stability, zero timed allocation, and zero timed
  readback all passed.
- Baseline runner-config SHA-256:
  `B0A043A8BE808F2D71905FBADC0ED3D6DD5399D0676CAA5315B847BB7D656EAD`.
- Candidate runner-config SHA-256:
  `BC0BBE822ADB9932EEF1CDD6FD2D0209E255D2409B40C5F8A0B028508A34A8F4`.
- Formal runner-config SHA-256:
  `FD3C4EC13970EADA9EB6B674190147BAB6C14963266A80B6B9FD18383D6ED1AD`.
- Full EditMode XML SHA-256:
  `78C801C6E9D4CEFDC59FC8DDD27EFBEAC651B193C6D2DC6495E751644A197D36`.

Raw artifacts are retained under `artifacts/t05-single-bin-baseline-d31d590`,
`artifacts/t05-single-bin-candidate-8e15414`,
`artifacts/t05-formal-8e15414`, and
`artifacts/t05-small-cell-fastpath-8e15414`.

## Claim boundary

Allowed: on this RTX 4090 protocol, the specialized forced-GPU path reduced
the existing package path's submission P95 and GPU P99 for the `10K / 1 / 1`
cell, while the complete formal matrix remained NO-GO.

Not allowed: claiming the smallest workload beats engine-native rendering,
claiming the four-cell system matrix passed, claiming stable frame-time/FPS
improvement, or generalizing beyond this device, API, visibility, and sample.
