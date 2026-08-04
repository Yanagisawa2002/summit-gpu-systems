# GPU-Resident Spatial Index: AMD R9700 A/B Results

Date: 2026-07-30
Branch: `codex/gpu-resident-spatial-index`
Worktree: `C:\Users\EdwinLiu\Downloads\SUMMIT-gpu-spatial-index`
Base commit: `197ba2628d4eb5456da004558e4659451869fa70`

## Technical summary

This experiment adds a GPU-resident spatial-query path for dense sensor data in the full NYC production scene. The workload contains 921,600 deterministic depth samples in sensor-frustum coordinates. Each update rebuilds a Morton-sorted 64³ uniform grid entirely on the GPU and answers a batch of cell-aligned voxel range-count queries without measurement-phase readback.

The benchmark uses a deterministic depth texture uploaded at initialization, so it measures the cost of rebuilding and querying the index on every update; it does not claim validation on a changing live LiDAR stream.

The implementation is not a scene-specific visibility heuristic. It is a reusable data-oriented GPU algorithm with:

- sensor-frustum voxelization and Morton encoding;
- five-pass, four-bit LSD radix sorting;
- wave-aggregated histograms and wave-prefix stable scatter;
- dense cell begin/end ranges;
- cell-aligned AABB count queries;
- a GPU brute-force comparison path;
- a selectable CPU Burst/`NativeArray.Sort` oracle;
- explicit GPU markers for build, sort, cell-range construction, indexed queries, and brute-force queries.

The result is workload-dependent. At 1,024 batched queries, all three paired production-scene runs favored the indexed path. At 256 queries, the indexed path had no reproducible whole-frame benefit. Fixed build/sort overhead is a plausible explanation, but it was not isolated with per-region GPU timestamps in this experiment.

## Key findings

For the 1,024-query workload:

- Brute force performed 943,718,400 direct point-query inspections per update.
- The indexed path performed 351,232 cell-range lookups; those ranges represented 1,329,902 returned point-query memberships.
- The index therefore avoided 99.859079% of the brute-force point-query pairs.
- All three paired runs improved whole-frame GPU average and GPU P99.
- The paired-median GPU-average improvement was 28.293%.
- The paired-median GPU-P99 improvement was 25.951%.
- The paired-median frame-average and frame-P99 improvements were 25.131% and 29.315%.
- GPU workload runs had zero per-query count mismatches against the CPU oracle and zero measurement-phase readback.

The exact percentage magnitude remains provisional because separate Unity player processes showed 86.448% drift in the `off` GPU-average baseline. The direction is stronger evidence than the exact effect size: all three counterbalanced pairs improved despite the large external drift.

## Scope and production baseline

| Dimension | Value |
| --- | --- |
| Unity | 6000.5.2f1 |
| Graphics API | Direct3D 12, feature level 12.2 |
| GPU | AMD Radeon AI PRO R9700 |
| Driver | 32.0.31035.1003 |
| Scene | `Assets/Scenes/NYCGISDemoFull.unity` |
| Scene SHA-256 | `B1E650A08F43F8A47C470CB1EB4796FCF6926209BE36DBFED7E5E5FB2CA6FB5E` |
| Sensor workload | 1280×720, 921,600 deterministic depth samples |
| Target update rate | 30 Hz |
| Spatial grid | 64³, 262,144 cells |
| Query radius | 3 cells |
| Production signature | 227 total BFP2 packs; 73 resident; 0 loading; 1,932,028,912 resident GPU bytes; 66 base and 180 LOD0 orthophoto pages |
| Formal design | 3 rounds × 900 frames, complete Latin-square mode ordering |

The full production scene and its generated assets were selectively synchronized into this isolated worktree. Unrelated changes from the source worktrees were not copied.

## Algorithm and implementation

The indexed path executes:

1. Encode each point into a 64³ sensor-frustum voxel and an 18-bit Morton key.
2. Run five four-bit LSD radix passes.
3. Build dense begin/end offsets for occupied cells.
4. Visit only cells overlapped by each cell-aligned query.
5. Sum each selected cell range to produce an exact occupancy count.

The portable first implementation used a serialized group-local stable ranking step. Profiling exposed it as a major bottleneck: the initial indexed GPU path was approximately 70.240 ms average versus 35.092 ms for brute force in the first formal round. That result was rejected, not hidden. The scatter was then replaced with wave-prefix stable ranking and wave-aggregated histograms under Shader Model 6/DXC. The GPU compute asset requires Shader Model 6; the CPU Burst path remains a separately selectable reference and portability option rather than an automatic runtime fallback.

Measurement-phase execution uses prerecorded command buffers and performs zero bytes of readback per frame. A small validation readback is used only during warmup.

GPU marker names:

- `Spatial/Build`
- `Spatial/MortonEncode`
- `Spatial/RadixSort`
- `Spatial/CellRanges`
- `Spatial/IndexedQuery`
- `Spatial/BruteForceQuery`

## Formal A/B: 1,024 queries

Each round launched fresh player processes in a complete Latin-square order:

| Round | Order | Brute GPU avg (ms) | Index GPU avg (ms) | GPU avg improvement | Brute GPU P99 (ms) | Index GPU P99 (ms) | GPU P99 improvement |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | off → brute → index | 22.433 | 18.117 | 19.240% | 28.156 | 23.670 | 15.933% |
| 2 | brute → index → off | 17.090 | 10.855 | 36.483% | 28.065 | 17.208 | 38.685% |
| 3 | index → off → brute | 42.626 | 30.566 | 28.293% | 60.480 | 44.785 | 25.951% |

| Paired metric | Median improvement | Range |
| --- | ---: | ---: |
| GPU average | 28.293% | 19.240% to 36.483% |
| GPU P99 | 25.951% | 15.933% to 38.685% |
| Frame average | 25.131% | 17.030% to 38.189% |
| Frame P99 | 29.315% | 24.447% to 36.830% |

The median update-rate change was +0.911%. The 30 Hz value is a scheduling target, not a proven sustained rate: raw reports contain nonzero scheduled-update drops, including 93 drops in indexed round 3. Update rate and drop count are guardrails rather than the primary timing metric, and the current summary CSV does not preserve the drop-count field.

## Query-volume contrast: 256 queries

At 256 queries, the conceptual point-query-pair reduction remained high at 99.858468%, but the indexed path showed no reproducible whole-frame benefit:

| Metric | Paired median | Range |
| --- | ---: | ---: |
| GPU-average improvement | -13.653% | -36.684% to +0.460% |
| GPU-P99 improvement | -0.580% | -57.878% to +5.032% |
| Frame-average improvement | -16.204% | — |
| Frame-P99 improvement | -11.033% | — |

The 256-query and 1,024-query sweeps were produced by two successive Player builds. The q256 binary was overwritten and its hash was not retained, so this contrast is useful directional evidence rather than a strict same-binary crossover. A fixed build/sort cost is the working hypothesis, not a timestamp-isolated conclusion. The production policy should be adaptive, and both query counts should be rerun from one hashed Player build before shipping a threshold.

## Correctness and quality gates

The 1,024-query formal run set passed these scoped gates:

- 9/9 player processes completed, emitted the expected timing row, and matched the exact production signature.
- 6/6 GPU workload runs (`brute` and `index`) had zero per-query count mismatches against the CPU oracle and zero measurement-phase readback.
- 3/3 indexed runs had zero sorted-order, cell-range, and bounds violations; permutation XOR, sum, and unique-count invariants also matched.
- The deterministic CPU-oracle result hash was stable at `17954676537386309527`.

The hash is derived from CPU expected query counts and is reported as an oracle identifier; it is not a hash computed from GPU output. GPU result equivalence is established by the warmup mismatch counter's exact per-query count comparison. The index permutation fingerprint is a strong invariant check, not a claim of complete bit-for-bit index equivalence.

The selectable CPU reference produced the same expected total query-hit count and oracle hash. It is not a directly comparable GPU timing measurement because CPU completion and GPU enqueue semantics differ.

## Evidence quality

| Claim | Confidence | Reason |
| --- | --- | --- |
| GPU query-count equivalence | High | 6/6 GPU workload runs had zero per-query mismatches against the CPU oracle |
| Indexed structure invariants | High | 3/3 indexed runs passed sort, cell-range, bounds, XOR, sum, and unique-count gates |
| Point-pair avoidance | High | Deterministic query geometry and counters |
| Positive performance direction at 1,024 queries | Medium | 3/3 paired GPU-average and P99 improvements |
| Exact 28.293% / 25.951% effect size | Low-to-medium | Separate-process `off` baseline drift was 86.448% |
| Sustained 30 Hz | Unverified | Raw reports include scheduled-update drops and the current summary omits that field |
| Exact q256/q1024 crossover | Low | Sweeps used successive Player builds; the overwritten q256 binary hash was not retained |
| Benefit at 256 queries | Rejected for tested build | The q256 paired results do not support it |
| NVIDIA portability/performance | Unverified | Only the local AMD platform was measured |

## Recommended next steps

1. Rerun q256 and q1024 from one hashed Player build and retain binary hashes.
2. Add same-process interleaving of brute and indexed dispatches to reduce thermal, clock, and scene-load drift.
3. Add GPU timestamp queries around the existing marker regions to separate build, radix, range, and query cost.
4. Preserve scheduled-update drops and loading-pack count in the summary and quality gates.
5. Capture RGP counters for occupancy, VGPR use, cache behavior, wave utilization, and memory bandwidth.
6. Add an adaptive crossover policy based on point count, query count, and expected index reuse.
7. Reuse one built index across LiDAR, radar, occupancy, nearest-obstacle, and annotation consumers.
8. Test changing depth inputs and add an NVIDIA run before claiming live-stream or cross-vendor validation.

## Reproduction

The 361 MB production scene and ignored generated assets are maintained as an
external benchmark fixture rather than Git blobs. Synchronize and verify the
fixture into the current worktree first:

```powershell
pwsh -File Tools/Sync-NYCGISGpuBenchmarkFixture.ps1 `
  -FixtureRoot 'C:\path\to\SUMMIT-GPU-Benchmark-Fixtures'
```

Then run the production-scene A/B harness after replacing the data-root
placeholder:

```powershell
pwsh -File Tools/Run-NYCGISGpuSpatialIndexAB.ps1 `
  -DataRoot 'D:\path\to\NYC-GIS-data' `
  -Repeats 3 `
  -SampleFrames 900 `
  -SensorWidth 1280 `
  -SensorHeight 720 `
  -SensorRateHz 30 `
  -SpatialQueryCount 1024
```

Summarize and validate the formal result set:

```powershell
pwsh -File Tools/Summarize-NYCGISGpuSpatialIndexAB.ps1 `
  -SummaryPath Reports/GpuSpatialIndex/formal-wave-q1024-amd-r9700-1280x720-30hz-3x900/summary.csv
```

Primary evidence:

- `Reports/GpuSpatialIndex/formal-wave-q1024-amd-r9700-1280x720-30hz-3x900/summary.csv`
- `Reports/GpuSpatialIndex/formal-wave-q1024-amd-r9700-1280x720-30hz-3x900/paired-deltas.csv`
- `Reports/GpuSpatialIndex/formal-wave-q1024-amd-r9700-1280x720-30hz-3x900/quality-summary.txt`
- `Reports/GpuSpatialIndex/formal-wave-amd-r9700-1280x720-30hz-3x900/quality-summary.txt`
- `Reports/GpuSpatialIndex/resident-cpu-reference-q1024.txt`
