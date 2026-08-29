# Dirty-range instance upload — RTX 4090 formal result

## Outcome

Commit `7d9b2c94b22274586410a73ee0fb7252bd9f26ed` adds a reusable,
engine-native dirty-range upload path for persistent GPU instance state. The
formal D3D12 evidence is valid. Sparse update cells improved CPU submission
and logical upload volume; the explicit 100% dirty control regressed, so this
PR does not claim that dirty upload is universally faster.

| Workload | Full / dirty logical bytes | Reduction | CPU submission P95 full / dirty | Change | CPU frame P99 full / dirty |
|:---|---:|---:|---:|---:|---:|
| 10K, 0% moving | 480,000 / 0 | 100% | 0.1500 / 0.0450 ms | -70.00% | 1.4024 / 1.2821 ms |
| 10K, 1% moving | 480,000 / 4,800 | 99% | 0.1564 / 0.0592 ms | -62.15% | 1.3438 / 1.2600 ms |
| 10K, 10% moving | 480,000 / 48,000 | 90% | 0.2019 / 0.1240 ms | -38.59% | 1.3504 / 1.2514 ms |
| 10K, 100% moving | 480,000 / 480,000 | 0% | 0.7060 / 0.8980 ms | +27.20% | 1.1247 / 1.2228 ms |
| 100K, 0% moving | 4,800,000 / 0 | 100% | 1.2009 / 0.0465 ms | -96.13% | 3.2429 / 1.2277 ms |
| 100K, 1% moving | 4,800,000 / 48,000 | 99% | 1.2240 / 0.1313 ms | -89.27% | 2.2479 / 1.2222 ms |
| 100K, 10% moving | 4,800,000 / 480,000 | 90% | 1.7187 / 0.8616 ms | -49.87% | 3.5832 / 1.1645 ms |
| 100K, 100% moving | 4,800,000 / 4,800,000 | 0% | 7.0465 / 9.2107 ms | +30.71% | 7.7327 / 10.0112 ms |

Logical bytes are the bytes requested through Unity's upload API. They are not
PCIe, driver-staging, or physical copy-engine measurements. The dense negative
control is expected to remain available to the later policy PR: a forced dirty
path performs range/update bookkeeping even when one full command is cheaper.

## CPU and frame-tail evidence

- CPU submission P95 improved by `38.59%–96.13%` in all six sparse cells.
- Required CPU total/main FrameTiming rows were available for all
  `57,600/57,600` measured frames under the frozen four-frame alignment.
- Sparse-cell CPU frame P99 improved in all six tested cells; the 100% controls
  regressed consistently with their submission cost.
- GPU frame timing was availability-gated and incomplete in several cells. It
  was retained as a diagnostic and was not used for a GPU performance claim.
- Native GPU-region timestamps and image hashes were not collected in this
  focused upload benchmark; the broader macrobenchmark remains the GPU/render
  evidence source.

## Frozen protocol

- Unity `6000.5.2f1`, Windows 11, Direct3D 12 feature level 12.2.
- Intel Core Ultra 7 265K; NVIDIA GeForce RTX 4090; driver `32.0.15.9186`.
- `10K/100K` instances crossed with `0/1/10/100%` movement, one view, one
  draw group, exact seeded 25% visibility, and input layout
  `seeded-striped-contiguous-16-v1`.
- One fresh-built Player per cell; eight `ABBA;BAAB` blocks, 30 warm-up and
  900 measured frames per block, four paired full/dirty blocks, and 3,600
  measured samples per variant per cell.
- Eight persistent staging slots protected by `AllGPUOperations` fences.
- Full and dirty variants used the same logical ordinal, update hash, range-plan
  hash, visible-only pipeline, mesh, material, render target, and indirect draw.
- No hardware speed threshold was used as a correctness gate. The 100% cells
  were frozen reported-only negative controls.

## Correctness and provenance

- Full D3D12 EditMode suite: `615/615` passed, zero skipped.
- Formal scenarios: `8/8` completed; measured rows: `57,600/57,600`.
- Block-final full/dirty GPU state validations: `64/64` passed with exact
  expected/actual 48-byte-record hashes.
- Timed managed allocation rows/bytes: `0 / 0`.
- Staging-slot wait frames: `0`; measurement readback bytes: `0`.
- Every completion fence passed. Source hashes and the freshly built Player
  payload remained stable through the run; final Git state was clean.
- Source snapshot SHA-256:
  `523091B17BEAD8CE579209E7DD39FD75985ACBA5B289F0BB40F1B64EB1CEEDD4`.
- Runtime shader SHA-256:
  `C8F365BC2883FD85C6F9DEE94AD738DA7D2DFD061B785E935FCF2E41421B278D`.
- Render shader SHA-256:
  `A686325CC7FB292919FD57841E64916A8B7194BD673F0EA7D3BF1C33CBE80FB6`.
- Player payload: `283` files, `156,055,842` bytes, SHA-256
  `9066A22AFF9833D1AA4D3F32A0F456A9F9EB0CABBDDFF2B9D3745DF67BFE99D9`.

Compact retained evidence is under
`Evidence/GpuDrivenInstances/NVIDIA_RTX4090_DIRTY_RANGE_UPLOAD_2026-08-30`.
Raw frames and generated Player files remain ignored under `Reports/` and
`Builds/`.

## Claim boundary and next action

This establishes a general dirty-range upload mechanism and device evidence for
sparse dynamic state. It is not a physical bus measurement, a SUMMIT scene
result, an AMD result, or proof of an automatic threshold. The 100% controls
show why policy must select full upload at high update density. Hierarchical
multi-view culling and automatic policy selection remain separate PRs.

## Resume-safe wording

> Built a reusable Unity/D3D12 dirty-range GPU instance-state uploader with
> fence-protected persistent staging and fail-safe full-upload fallback; across
> 10K/100K workloads at 0%–10% motion on an RTX 4090, reduced API-requested
> upload bytes by 90%–100% and CPU submission P95 by 38.6%–96.1%, with
> 57,600/57,600 CPU frame-tail rows, 64/64 exact GPU state validations, zero
> timed allocation/readback and zero staging waits; retained 100% update
> regressions of 27.2%–30.7% as the negative control for automatic selection.
