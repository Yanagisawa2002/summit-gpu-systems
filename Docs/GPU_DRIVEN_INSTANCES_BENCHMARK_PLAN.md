# Experiment Plan: GPU-Driven Instances

**Problem**: Per-object CPU simulation-to-render submission, repeated visibility work, and backend assumptions limit scalable real-time rendering across projects and GPU architectures.

**Method thesis**: A neutral, CPU-authoritative GPU mirror that performs pose evaluation, multi-view visibility/LOD, stable compaction, grouping, and indirect argument generation can reduce CPU/render submission and tail latency without project-specific data contracts; device-keyed selection prevents hardware regressions.

**Date**: 2026-08-29

## Claim map

| Claim | Why it matters | Minimum convincing evidence | Linked blocks |
|---|---|---|---|
| C1: The portable GPU-driven instance pipeline removes a real submission/visibility bottleneck without semantic divergence | This is the transferable contribution behind the SUMMIT traffic experiment | At least 3/4 primary scenarios improve CPU render-submission P95 by `>=20%` and `>=0.20 ms`; GPU P99 no worse than `-5%`; all visibility, pose, group-count, overflow, and image/hash gates pass | B1, B2, B3 |
| C2: Hardware/workload-aware backend selection is required | RTX 4090 already rejects a universal WaveOps radix rule | Disjoint calibration/evaluation confirms the selected backend on both measured devices; rejected candidates remain below the frozen upgrade gate | B3, B4 |

Anti-claim to rule out: the gain comes only from SUMMIT city data, fewer rendered objects, weaker correctness, hidden readback, or a deliberately weak CPU baseline.

## Paper and portfolio storyline

- Main evidence must prove C1 on asset-free workloads and one independent external environment.
- The backend/caching/command-batching deletion study must isolate which mechanism matters.
- NVIDIA is available now. New-workload AMD evidence remains pending until the R9700 is available again and must be reported as unavailable until measured.
- Visual polish, extra assets, and many weak scene comparisons are intentionally cut.

## Experiment blocks

### B1: Procedural scaling anchor

- Claim tested: C1
- Dataset/task: deterministic procedural instances with counts `10K/100K/1M`, visibility `5%/25%/75%`, views `1/4/8`, groups `1/8/32`, and moving fractions `0%/10%/100%`
- Compared systems: CPU frustum + instanced draws; GPU visibility + indirect; GPU visibility + dirty-range updates; optional Unity BRG/Entities Graphics baseline where contracts match
- Metrics: CPU main/render submission P50/P95/P99, native GPU scope P50/P95/P99, frame P95/P99, uploads, dispatches, draws, explicit buffer bytes, validation/readback bytes
- Correctness: CPU visibility oracle, stable group counts, deterministic pose hash, overflow/fallback tests, and image/silhouette comparison for representative cells
- Success criterion: the C1 gate above, with no hidden measurement readback and exact workload parity
- Failure interpretation: if CPU falls but GPU P99 regresses, the pipeline moved rather than removed the bottleneck; if only 1M wins, scope the claim to high-cardinality workloads
- Target: main table and scaling curves
- Priority: MUST-RUN

### B2: Independent macrobenchmark

- Claim tested: C1 outside SUMMIT
- Environment: pinned Unity `EntityComponentSystemSamples/GraphicsSamples`; optional CC0 Khronos `NodePerformanceTest` fixture
- Compared systems: unmodified engine-native path versus the package adapter under identical camera, instance, material, and visibility schedules
- Metrics and gates: same as B1, plus import/build provenance and external commit/license receipts
- Success criterion: directionally consistent CPU/submission improvement in at least two external workload cells with correctness and GPU P99 gates intact
- Failure interpretation: a synthetic-only win limits the resume claim to a systems microbenchmark
- Target: external-validity table
- Priority: MUST-RUN after B1

### B3: Mechanism isolation

- Claim tested: C1 and the anti-claim
- Ablations: remove GPU pose evaluation; remove GPU culling; replace stable compaction with append; disable dirty-range upload; disable visibility cache; split versus batch compatible command submission; force one backend
- Success criterion: the final compact pipeline explains most of the gain; complexity that adds no decisive benefit is deleted
- Failure interpretation: if command batching remains near zero as in BFP2 evidence, keep it as an implementation detail rather than a named contribution
- Target: ablation table
- Priority: MUST-RUN

### B4: Hardware-aware policy

- Claim tested: C2
- Devices: RTX 4090 now; AMD R9700 only when available
- Protocol: two calibration rounds and four untouched evaluation rounds per workload/device; profile keyed by vendor/device/API/name/shader level
- Success criterion: each selected backend is independently confirmed; a profile mismatch or contradictory evaluation fails closed
- Failure interpretation: retain Portable/Auto for any unconfirmed workload and report the negative result
- Target: device-policy table
- Priority: MUST-RUN on NVIDIA, NICE-TO-HAVE on AMD this week

PR7 scope clarification: the instance-policy matrix calibrates only upload
(`None/Dirty/Full`) and culling (`Flat/Hierarchy`). Output is a caller-owned
semantic match constraint. Primitive backend selection is independently
calibrated by the PR1 `GpuPrimitiveBackendResolver`; PR7 keeps `Portable` fixed
and tests only the composition boundary.

### B5: Failure and stress analysis

- Cases: capacity overflow, zero instances, all invisible, all visible, camera teleport, scene reload, device loss/recreate, unsupported WaveOps, 32-bit indirect-argument overflow, and allocation soak
- Success criterion: deterministic fail-closed behavior, zero steady-state managed allocations, and no stale visibility after invalidation
- Target: appendix/engineering checklist
- Priority: MUST-RUN for correctness; long soak is NICE-TO-HAVE

## Run order and milestones

| Milestone | Goal | Runs | Decision gate | Cost | Risk |
|---|---|---|---|---|---|
| M0 | Freeze contracts and oracle | Small CPU/GPU integration tests | Exact parity, overflow and fallback pass | 0.5 day | API accidentally leaks SUMMIT types |
| M1 | Establish strong baselines | 10K/100K smoke grid | Stable markers, hashes, no readback | 0.5 day | Baseline is unfair or CPU-bound elsewhere |
| M2 | Main RTX 4090 result | Frozen primary matrix, 3 counterbalanced rounds | C1 passes in at least 3/4 primary cells | 0.5–1 day | GPU tail regression or buffer pressure |
| M3 | Decisive ablations | One axis at a time | Contribution isolated; dead complexity removed | 0.5 day | Too many coupled mechanisms |
| M4 | External environment | Pinned GraphicsSamples + optional NodePerformanceTest | Same direction outside synthetic host | 1 day | package/version/import incompatibility |
| M5 | AMD and polish | R9700 profile, charts, failure table | C2 confirmed or scoped honestly | 0.5–1 day when hardware is available | hardware unavailable |

## Compute and data budget

- RTX 4090: approximately 4–6 local GPU-hours including rebuilds, correctness sweeps, and formal A/B
- AMD R9700: approximately 3–4 GPU-hours when available
- Data: generated procedural state plus pinned external sample commits; no SUMMIT assets
- Human visual check: 20–30 minutes for representative parity cells
- Biggest bottleneck: fair integration with the external engine-native renderer, not raw compute time

## Risks and mitigations

- Ownership boundary: use a clean-room API and generated workloads; do not copy project assets, symbols, formats, constants, or comments.
- Benchmark gaming: freeze the matrix before formal runs and retain negative cells.
- Interruption integrity: require an immutable run contract, an exclusive lock,
  atomic sealed setup/phase receipts, and exact duplicate/missing/unexpected
  receipt rejection before any resume or final aggregation.
- Environment timing gate: a short `SingleScenario` run is diagnostic only;
  sample count cannot guarantee that Unity will publish whole-frame GPU values.
  Before a new formal matrix, replay one frozen formal-equivalent cell and
  require all existing coverage gates. If the same frozen Player loses GPU
  timing availability, classify the environment as invalid and do not start or
  resume the matrix, relax the gates, or substitute zero for `unavailable`.
- CPU baseline weakness: include engine-native instancing/Entities Graphics where applicable.
- Hardware overclaim: keep results per device and mark unrun hardware unavailable.
- Timing ambiguity: report native GPU scopes separately from CPU markers and end-to-end frame tails.

## Final checklist

- [x] Main procedural anchor table is complete; overall frozen result is retained as NO-GO
- [x] Correctness and visual parity are exact for the procedural anchor
- [ ] Novelty is isolated through deletion studies
- [ ] Portable retention is accepted when a candidate misses the gate
- [x] Formal runner has strict checkpoint/resume and rejects legacy partial runs
- [ ] External macrobenchmark is pinned and licensed
- [ ] AMD new-workload evidence is measured or explicitly unavailable
- [ ] Nice-to-have runs do not delay resume-ready evidence
