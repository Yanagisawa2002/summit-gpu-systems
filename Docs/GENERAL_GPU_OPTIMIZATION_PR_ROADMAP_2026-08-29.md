# General GPU Optimization PR Roadmap — 2026-08-29

## Ordering principle

Each optimization is isolated as one reviewable PR. A PR must first preserve
the semantic contract, then prove whether it improves a named bottleneck. A
candidate that misses its frozen gate remains a documented negative result and
does not silently become the default.

| Order | PR | General mechanism | Primary gate | Status |
|---:|---|---|---|---|
| 1 | Device-keyed backend selection | Calibrate and independently confirm Portable/WaveOps per device/workload | correctness, P99 guardrail, disjoint holdout | GitHub PR #1 open |
| 2 | GPU-driven instances core | Multi-view visibility, LOD, grouping, CSR output, indirect args | exact CPU oracle and D3D12 contract suite | implemented; PR pending |
| 3 | CPU-vs-GPU procedural benchmark | Strong CPU baseline and native region timing for 10K/100K/1M instances | CPU submission P95 `>=20%` and `>=0.20 ms`; GPU P99 no worse than `-5%` | next |
| 4 | Filtered binning / visible-only scatter | Discard invisible keys without writing a culled payload tail | output writes scale with visible pairs; no diagnostic ambiguity | queued |
| 5 | Dirty-range state mirroring | Merge changed ranges and avoid full-buffer upload | upload bytes and CPU P95 improve at 0/1/10% motion; 100% motion does not regress materially | queued |
| 6 | Hierarchical multi-view culling | Coarse cluster visibility before per-instance classification | wins at low visibility and many views; all-visible fallback protected | queued |
| 7 | Device/workload policy integration | Select flat/hierarchical, filtered/full, and primitive backends by measured profile | calibration choice confirmed on untouched rounds | queued |
| 8 | External macrobenchmark adapter | Consume the package in a pinned engine-native sample without vendoring it | directionally consistent result in at least two cells | queued |

## Why this order

The current core intentionally exposes a measurable baseline. Its largest known
generic costs are `instanceCount × viewCount` classification and writing a
culled payload tail. Filtered scatter and hierarchical culling therefore have
clear counterfactuals. Dirty-range mirroring addresses CPU-to-GPU bandwidth but
must be measured separately so upload savings are not confused with culling
gains.

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

Until PR 3 completes, the new instance package is “implemented and D3D12
correct,” not “faster.” Until PR 8 completes, it is asset-independent but not
externally macro-validated. New AMD results remain unavailable until that
hardware is accessible.
