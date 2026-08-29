# General GPU Optimization PR Roadmap — 2026-08-29

## Ordering principle

Each optimization is isolated as one reviewable PR. A PR must first preserve
the semantic contract, then prove whether it improves a named bottleneck. A
candidate that misses its frozen gate remains a documented negative result and
does not silently become the default.

| Order | PR | General mechanism | Primary gate | Status |
|---:|---|---|---|---|
| 1 | Device-keyed backend selection | Calibrate and independently confirm Portable/WaveOps per device/workload | correctness, P99 guardrail, disjoint holdout | GitHub PR #1 open |
| 2 | GPU-driven instances core | Multi-view visibility, LOD, grouping, CSR output, indirect args | exact CPU oracle and D3D12 contract suite | GitHub PR #2 open |
| 3 | Filtered binning / visible-only scatter | Discard invisible keys without writing a culled payload tail; native paired GPU A/B | material win below all-visible; parity guard at 100%; exact oracle | implemented and formally accepted on RTX 4090; PR pending |
| 4 | CPU-vs-GPU procedural macrobenchmark | Strong CPU/engine-native baseline for 10K/100K instances | CPU submission P95 `>=20%` and `>=0.20 ms`; native GPU-region P99 no worse than `-5%` | implemented; formal evidence valid but overall NO-GO (3/4 + 3/4 gates) |
| 5 | Dirty-range state mirroring | Merge changed ranges and avoid full-buffer upload | upload bytes and CPU P95 improve at 0/1/10% motion; 100% motion remains an explicit one-command control | implemented; formal D3D12 quality evidence valid, sparse cells win and 100% control regresses |
| 6 | Hierarchical multi-view culling | Coarse cluster visibility before per-instance classification | wins at low visibility and many views; all-visible fallback protected | queued |
| 7 | Device/workload policy integration | Select flat/hierarchical, filtered/full, and primitive backends by measured profile | calibration choice confirmed on untouched rounds | queued |
| 8 | External macrobenchmark adapter | Consume the package in a pinned engine-native sample without vendoring it | directionally consistent result in at least two cells | queued |

## Why this order

The core intentionally exposed a measurable culled-tail baseline. Inspection
showed that rejected pairs contended on one bin and still wrote a full payload,
so the filtered-scatter ablation moved ahead of the broader CPU macrobenchmark.
The formal dispersed-input result isolated that mechanism: `44.84%–93.13%`
mean GPU-region
reduction at `5%–75%` visibility and parity at `100%` visibility. The next PR
returns to the stronger CPU/engine-native comparison. Dirty-range mirroring
must remain separate so upload savings are not confused with culling gains.

## Deferred candidates

- CommandBuffer batching remains an ablation because prior evidence reduced
  submissions but not main-thread average.
- Stable ordering is optional; indirect rendering needs group membership, not
  deterministic atomic order. Pay for stable compaction only when a consumer
  contract requires it.
- Async compute is not assumed beneficial. Queue placement belongs behind the
  existing deadline/slack policy and must be rejected on saturated devices.
- Bindless/material-table work is deferred until a neutral API and cross-backend
  fallback can be expressed without engine/project asset contracts.
- Runtime mesh construction is portable but is a CPU/Burst project rather than
  the next GPU flagship.

## Evidence boundary

PR 3 supports a device- and workload-scoped GPU microbenchmark claim only. It
does not establish CPU submission savings, end-to-end FPS, visual parity, or an
external-engine result. Until PR 8 completes, the package is asset-independent
but not externally macro-validated. New AMD results remain unavailable until
that hardware is accessible.
