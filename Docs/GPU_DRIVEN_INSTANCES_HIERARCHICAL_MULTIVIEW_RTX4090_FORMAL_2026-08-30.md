# Hierarchical multi-view culling — RTX 4090 formal result

## Outcome

Commit `cf5b21b557356b522c920d591844d73ebf4299f5` adds a reusable,
engine-native hierarchical visible-only path. It validates immutable contiguous
clusters on GPU, performs coarse cluster/view rejection, expands only surviving
instance/view candidates, and feeds the existing direct binning and indirect
draw pipeline. The flat API remains unchanged and is the conservative default.

The hierarchy produced a real GPU-region improvement in every formal 1M x
four-view cell. It did not pass the frozen end-to-end material gate because
CPU enqueue P99 regressed by more than 5% in every cell. This is therefore an
explicit opt-in capability, not a universal-default performance claim.

| Visible | Flat / hierarchy GPU mean | Mean speedup | GPU P95 speedup | Frame P99 regression | Enqueue P99 regression | Decision |
|---:|---:|---:|---:|---:|---:|:---|
| 5% | 0.3423 / 0.2550 ms | 25.51% | 32.49% | -13.20% | +20.78% | regression-or-unstable |
| 25% | 1.0696 / 0.8057 ms | 24.67% | 18.71% | -17.41% | +10.05% | regression-or-unstable |
| 75% | 2.1423 / 2.0364 ms | 4.94% | 3.56% | -3.36% | +8.63% | regression-or-unstable |
| 100% | 2.6445 / 2.5155 ms | 4.88% | 3.29% | -3.78% | +10.30% | regression-or-unstable |

Negative frame-P99 regression means the hierarchy improved that tail. All four
same-process paired medians were positive (`4.88%–25.28%`), and every one of
the sixteen paired effects was positive. The failed guardrail is specifically
CPU command enqueue P99, not GPU timing, correctness, allocation, or frame P99.

## Algorithm and reusable API

- `GpuInstanceCluster` is a 32-byte immutable descriptor: conservative sphere,
  contiguous first/count range, union view mask, and reserved ABI word.
- `GpuInstanceClusterBuilder.BuildContiguous` creates conservative 64-instance
  clusters without warm-path managed allocation.
- `RecordHierarchicalVisibleOnly` validates the active-prefix cover, rejects
  cluster/view pairs, performs one 64-thread fine group per cluster, writes a
  dense key/value prefix, then scans and scatters from a GPU-owned count.
- Fine dispatch supports a two-dimensional cluster grid above 65,535 groups.
- Invalid ranges, non-finite bounds, capacity overflow, binning invariants, or
  any public diagnostic fail closed by zeroing every draw's instance count and
  base-instance offset.
- `RecordPrecountedPrefixIndirect` is a reusable direct-binning primitive with
  distinct validation/scatter indirect-argument buffers to avoid input/UAV
  aliasing on D3D12.

No scene name, building rule, occlusion heuristic, SUMMIT component, or project
asset appears in the runtime packages.

## Work-reduction evidence

| Visible | Coarse cluster/view pairs | Candidate instance/view pairs | Visible pairs | Candidate reduction vs flat |
|---:|---:|---:|---:|---:|
| 5% | 63,876 | 209,715 | 209,715 | 95.00% |
| 25% | 65,532 | 1,048,576 | 1,048,576 | 75.00% |
| 75% | 65,532 | 3,145,728 | 3,145,728 | 25.00% |
| 100% | 65,536 | 4,194,304 | 4,194,304 | 0.00% |

All three GPU statistics matched the independent frozen-input CPU oracle in
both warm-up and final validation for every cell. The 100% case is the explicit
negative work-reduction control; it keeps every instance/view candidate.

## CPU submission and frame-tail evidence

- Native GPU P95 and frame P99 passed their non-regression guardrails in all
  four cells.
- Enqueue P99 was `0.2005/0.2343/0.2645/0.2903 ms` for hierarchy versus
  `0.1660/0.2129/0.2435/0.2632 ms` for flat, causing the formal NO-GO.
- All `36,000/36,000` native timestamp rows were ready.
- Timed main-thread allocation rows/bytes were `0 / 0`; measurement readback
  bytes were zero. Timestamp instrumentation used its separate, declared
  16-byte completion channel per sample.
- The small 65,536-instance smoke cell was also retained as a boundary check:
  at 5% visibility it reduced candidates by 95% but regressed GPU mean by
  16.93%, reinforcing that fixed hierarchy overhead matters below scale.

## Frozen protocol and provenance

- Unity `6000.5.2f1`, Windows 11, Direct3D 12 feature level 12.2.
- Intel Core Ultra 7 265K; NVIDIA GeForce RTX 4090.
- Four cells: 1,048,576 instances, four distinct views, visibility
  `5/25/75/100%`, 16,384 clusters, eight draw groups, seed `20260829`.
- Same-process `control-pre;ABBA;BAAB;control-post`, 60 warm-up frames, 15
  cooldown frames, 900 measured frames per block, two super-rounds.
- Full D3D12 EditMode suite: `667/667` passed, zero skipped.
- Formal validations: `16/16`; hierarchy-statistic rows: `8/8` exact.
- Source and freshly built Player hashes stayed stable; Git was clean before
  and after the matrix.
- Source snapshot SHA-256:
  `2C9174B508AA1F9A8493432F2E98F44E3D97A29C39F2477C389582C37910A1E8`.
- Runtime shader SHA-256:
  `F8FDA97C0D7DD8F7344B8651867AA829606580F91969DD616F9037BF601E4070`.
- Binning shader SHA-256:
  `4A7BE834609D2D598DA587B49C2D353034EDE9FFBA128A491EB56E4BA048234C`.
- Runtime API SHA-256:
  `7B0547429FCF67D897732B3BA828BC791C92A10B27A1E68481FB2E25F2EFB411`.
- Player payload: `282` files, `156,135,376` bytes, SHA-256
  `047B15AEDAA282172941354501A521E13696D0B438120B3C03EA36E902647D40`.

Compact evidence is under
`Evidence/GpuDrivenInstances/NVIDIA_RTX4090_HIERARCHICAL_MULTIVIEW_2026-08-30`.
Raw samples and generated Player files remain ignored under `TestResults/` and
`Builds/`.

## Claim boundary and next action

This proves exact flat/hierarchy parity and a scalable GPU-work reduction on
one RTX 4090/D3D12 system. It is not an AMD result, a SUMMIT scene result, a
CPU-submission win, or proof that hierarchy should always be enabled. The next
PR must select among flat/hierarchical and full/dirty paths using explicit,
profiled policy inputs with hysteresis and fail-safe fallback; it must treat
enqueue tail as a first-class guardrail.

## Resume-safe wording

> Built a reusable Unity/D3D12 hierarchical multi-view culling and GPU-count
> binning pipeline for 1.05M instances; cut candidate instance/view work by up
> to 95% and GPU mean time by 24.7%–25.5% in sparse 4-view RTX 4090 workloads,
> with 36,000/36,000 native timestamps, 16/16 exact output validations, 8/8
> exact hierarchy-statistic validations, and zero timed allocation/readback;
> retained a conservative default-off decision because CPU enqueue P99 missed
> the predeclared 5% tail guardrail.
