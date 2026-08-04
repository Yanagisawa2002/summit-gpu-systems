# GPU-Resident Spatial Index: Resume and Interview Notes

Date: 2026-07-30
Validation scope: AMD Radeon AI PRO R9700, Direct3D 12

## Recommended resume bullet

Built a GPU-resident Morton/radix uniform-grid index for a deterministic 921.6K-depth-sample workload on DX12/AMD R9700. Replaced 943.7M direct point inspections across 1,024 cell-aligned sensor-frustum range-count queries with a five-pass wave-optimized radix build and 351K cell-range lookups covering 1.33M returned point-query memberships. In a 3×900-frame Latin-square production-scene A/B, all three paired runs improved whole-frame GPU average and P99; observed paired medians were 28.3% and 26.0%, with zero GPU-vs-CPU query-count mismatches and zero measurement-phase readback.

## Short version

Implemented a GPU-resident Morton/radix uniform-grid index for 921.6K deterministic depth samples. On AMD R9700, all 3×900-frame production-scene pairs at 1,024 cell-aligned range-count queries improved whole-frame GPU average/P99, with observed paired medians of 28.3%/26.0%, zero GPU-vs-CPU query-count mismatches, and zero measurement-phase readback.

## Interview narrative

The initial problem was not simply “make Unity faster.” A robotics-simulation workload needed many spatial occupancy-count queries, while a brute-force GPU baseline retested every point for every query.

The first index implementation was correct on its scoped validation gates but slower. A serialized group-local stable scatter dominated the radix path. The optimized variant used Shader Model 6 wave operations for aggregated histogram updates and stable prefix ranking. A selectable CPU Burst path supplied expected per-query counts for exact GPU-vs-CPU validation.

The result exposed a useful query-volume contrast. At 1,024 queries the index consistently won all three paired runs. At 256 queries, it had no reproducible whole-frame benefit even though the indexed search space was much smaller. Fixed build/sort overhead is the working hypothesis, not a timestamp-isolated conclusion. Because the query-count sweeps used successive Player builds and the q256 binary hash was not retained, the exact crossover threshold still needs a same-build rerun. The eventual production policy should be adaptive, or the index should be reused by multiple sensor and annotation consumers.

This is valuable GPU performance engineering because it demonstrates:

- data-structure and algorithm design, not only engine settings;
- GPU-resident execution and explicit readback avoidance;
- wave-level parallel primitives;
- profiling-driven rejection and redesign of a slow kernel;
- exact per-query GPU-vs-CPU validation;
- counterbalanced A/B methodology;
- workload crossover analysis;
- honest vendor and confidence boundaries.

## Safe claims

- AMD Radeon AI PRO R9700 / DX12 validated.
- Deterministic 921,600-depth-sample workload with 1,024 cell-aligned sensor-frustum range-count queries.
- Replaced 943.7M direct point inspections with 351,232 cell-range lookups covering 1,329,902 returned point-query memberships.
- 99.859079% of brute-force point-query pairs avoided conceptually.
- 3/3 paired runs improved GPU average and GPU P99.
- Observed paired medians: 28.293% GPU average, 25.951% GPU P99.
- 6/6 GPU workload runs had zero per-query count mismatches against the CPU oracle.
- 3/3 indexed runs had zero sort, cell-range, and bounds violations.
- Deterministic CPU-oracle hash: `17954676537386309527`.
- Zero measurement-phase readback.
- At 256 queries, that tested build did not show a reproducible benefit; the exact cross-build threshold is not yet established.

## Claims to avoid

- Do not call the paired percentages kernel-only speedups.
- Do not claim the exact effect size is production-final before GPU timestamps or same-process interleaving.
- Do not claim sustained 30 Hz; raw reports contain scheduled-update drops.
- Do not claim NVIDIA validation.
- Do not call this an LBVH; it is a Morton-sorted dense uniform grid.
- Do not describe the benchmark as a changing live LiDAR stream; the deterministic depth input is uploaded once and the index is rebuilt each update.
- Do not describe the query as a general point-cloud AABB lookup returning point IDs; it is a cell-aligned sensor-frustum occupancy-count query.
- Do not say the indexed kernel tested 1.33M candidate points; it summed 351K cell ranges representing 1.33M returned memberships.
- Do not call the CPU path an automatic fallback on unsupported GPUs.
- Do not claim complete bit-exact index equivalence from the permutation fingerprint.
- Do not claim a universal win at every query count.
- Do not claim driver-level optimization.

## Important caveat

The 1,024-query paired direction is reproducible, but the exact magnitude has low-to-medium confidence because the separate-process `off` baseline drifted by 86.448%. The 256- and 1,024-query sweeps also used successive Player builds. The next rigor step is one hashed build, same-process interleaving, preserved drop counts, and GPU timestamp queries around the existing profiler markers.
