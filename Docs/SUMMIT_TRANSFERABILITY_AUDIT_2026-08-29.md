# SUMMIT Optimization Transferability Audit — 2026-08-29

## Decision

The standalone repository can absorb additional work from SUMMIT, but the transfer unit must be an algorithm and its independently generated test contract, not a project file or renamed city feature. The next high-value addition is a clean-room GPU-driven instance visibility and indirect-rendering package. Building, facade, BFP2, orthophoto, voxel-weather, payload-camera, mission, and traffic types remain outside the portable API.

The audit compared current SUMMIT `main` at `1d48c31fa1e543ac4682d5b187e7d2fbab5c69b2` with the standalone repository and reviewed the named optimization commits below. The SUMMIT checkout was read-only; its untracked `artifacts/` directory was not touched.

## Classification

| Source change | Mechanism | Portability | Decision |
|---|---|---|---|
| `0ba1fc28` ambient traffic GPU indirect | GPU pose evaluation, visibility/LOD, palette grouping, indirect arguments, no measurement readback, CPU-authoritative simulation | High after removing traffic, DEM, sensor, networking, shader, and asset contracts | **P1 clean-room package and benchmark** |
| `b50867c5` BFP2 command batching | Record compatible clear/cull work in one reusable `CommandBuffer`; preserve draw registration and fallback | Medium; mechanism is generic, source API is BFP2-specific, measured main-thread delta was neutral | **P2 ablation inside the new benchmark**, not a flagship package |
| `abc3acbd` Terrain Jobs + MeshData | Burst jobs, explicit native ownership, 16/32-bit indices, asynchronous mesh construction | High for runtime mesh construction, but primarily CPU/Burst rather than GPU | **Separate future package**, outside the GPU flagship claim |
| `918c3054` vegetation cull cache | Cache visibility and invalidate on exact camera/content changes | Medium; reusable invalidation pattern, renderer contract is project-specific | Reimplement as a cache ablation, do not copy the renderer |
| `f1dca7d2` dirty-driven shadows | Replace per-frame status/settings work with versioned dirty updates | Medium-low; valuable engineering pattern, not a low-level GPU algorithm | Keep as an optimization pattern, not a standalone project |
| `a6f67b3c` skip empty voxel passes | Prove zero contribution and avoid render-pass enqueue/dispatch | Medium-low; generic early-out, but the proof and state are weather-specific | Add an empty-work guardrail test only |
| `74efd130` ProxyCollider zero allocation | Pooling and steady-state allocation elimination | High as C# performance practice, unrelated to GPU | Exclude from GPU portfolio; retain as CPU optimization evidence |
| `f2bfd705` linear-time interpolation | Replace repeated history scans with monotonic cursor/indexing | High as an algorithmic pattern, unrelated to GPU | Exclude from GPU portfolio |
| `efd342c3` payload lightweight renderer | Sensor-camera-specific URP asset and renderer selection | Low | Keep in SUMMIT only |
| `e959da31` FPV presentation latency | UAV camera presentation and state-update tuning | Low | Keep in SUMMIT only |
| `4030c849` orthophoto LOD streaming | Full-city virtual-texture/Lod2 page policy and data-root integration | Low as source code; the general residency problem already has a portable package | Do not copy; extend `gpu-residency-manager` only through neutral page contracts |

## Proposed ninth package

Working name: `com.summit.gpu-driven-instances`.

Portable inputs:

- `GpuInstanceState`: transform/velocity/bounds and opaque application ID
- `GpuView`: view-projection, viewport, LOD scale, and view mask
- `GpuDrawGroup`: mesh/material-independent grouping key and indirect argument slot
- `GpuVisibilityPolicy`: frustum, distance/LOD, capacity, and overflow behavior

Portable pipeline:

1. Upload full or dirty-range instance state into a stable GPU buffer.
2. Optionally evaluate deterministic poses on GPU while retaining CPU authority for gameplay.
3. Cull instances per view and select LOD/group.
4. Use scan/compaction primitives to build stable visible ranges.
5. Build indirect arguments without CPU readback.
6. Render procedural benchmark geometry through an adapter owned by the host.
7. Fail closed to a CPU/portable path on unsupported devices, capacity overflow, or validation failure.

Explicit exclusions:

- no `AmbientTraffic`, `TrafficDirector`, `NYCGIS`, `BFP2`, `SUMO`, payload-sensor, FishNet, weather, terrain, or city-data symbols;
- no copied meshes, materials, textures, scenes, data formats, production constants, or source comments;
- no claim based on SUMMIT-only performance numbers;
- no CPU/GPU semantic claim without a deterministic oracle and overflow/fallback tests.

## Benchmark environments

The existing procedural Unity host remains the primary controlled micro/mid-level environment because it can vary instance count, visibility, camera count, grouping, movement, and update sparsity independently.

For the external macrobenchmark:

1. Prefer a pinned commit of Unity's official `EntityComponentSystemSamples/GraphicsSamples`, currently documented for Unity 6.2 and Entities Graphics 1.4, as the strong engine-native rendering baseline.
2. Use Khronos `glTF-Sample-Assets/NodePerformanceTest` as an optional CC0 asset fixture for node/mesh/primitive scaling.
3. Do not use the commonly circulated Khronos Sponza package: its own license file names the Cryengine Limited License Agreement, and an open licensing issue remains.

External repositories stay separate. The standalone package is consumed through a local or commit-pinned UPM dependency; third-party source/assets are not vendored into this repository.

## Resume boundary

Safe to claim now:

- cross-vendor primitive validation on AMD R9700 and NVIDIA RTX 4090;
- a device-keyed mixed backend policy with disjoint calibration/evaluation and safe fallback;
- exact RTX 4090 results in the two dated NVIDIA reports.

Not yet safe to claim:

- a portable GPU-driven instance package;
- performance in Unity Entities Graphics or a Khronos asset scene;
- AMD/NVIDIA results for the future instance workload;
- universal frame-time, FPS, GPU, VRAM, or power improvements.
