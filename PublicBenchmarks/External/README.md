# External workloads and application consumers

These are prepared source contracts and opt-in adapters. All new SUMMIT paths are
**Unmeasured**. No benchmark, GPU dispatch, Player, profiling, allocation counter,
calibration, or autotuning was executed for this change. Normal runtime APIs and
existing benchmark commands remain available. Preparation tools default to source
preparation; CI runs an explicit CPU functional allowlist.

| Source | Classification and preserved contract | SUMMIT connection |
| --- | --- | --- |
| [Cabana](https://github.com/ECP-copa/Cabana/blob/dd6bd7ccbb28974f81c365ff6b1fd1f3a080b802/benchmark/core/Cabana_LinkedCellPerformance.cpp), `dd6bd7ccbb28974f81c365ff6b1fd1f3a080b802`, BSD-3-Clause | External library's native LinkedCell benchmark. Original default sizes 100/1000, requested widths 3/4, 10 runs; native double positions and generator remain upstream. | `LinkedCellWorkloadContract` preserves floor-derived grid dimensions, adjusted actual widths, z-fast keys and upper endpoint handling. `GpuLinkedCellWorkloadAdapter` feeds those keys and original IDs into SUMMIT direct binning and produces complete CSR. Neighbor iteration and particle permutation remain separate upstream operations. |
| [ArborX](https://github.com/arborx/ArborX/tree/375875dfb6b2e7631b1ba599cd26ee5c1e68ab90/benchmarks/bvh_driver), `375875dfb6b2e7631b1ba599cd26ee5c1e68ab90`, BSD-3-Clause | Native construction, spatial and nearest-query registrations. Retain the original `Spec`, point generator, buffer settings and predicate sorting. This adapter covers spatial spheres; kNN is outside its contract. | Native snapshot hook, input SHA verification, conservative SUMMIT CSR broad phase, original-float exact sphere predicate, count/scan/scatter to **complete IDs and offsets**. This is a SUMMIT adaptation of upstream workload inputs, not an ArborX native score. |
| [Unity ECS Boids](https://github.com/Unity-Technologies/EntityComponentSystemSamples/tree/6786a741ee1f118ed14cecfa02beae8e926937b0/EntitiesSamples/Assets/Boids), `6786a741ee1f118ed14cecfa02beae8e926937b0`, Unity Companion License | Official application sample; Unity 6000.2.10f1 and the checked-in package lock/scene/assets. Not an industry-standard benchmark. | Optional `BoidsSphereConsumer` reads actual `Boid/LocalToWorld` entities and moving `BoidTarget`s, consumes complete result IDs back into snapshot Entity handles, and displays per-target occupancy. The upstream flocking/spawning/rendering systems and assets remain intact. The added observation consumer and its radius are separately labeled work. |

Every selected source file has a URL, commit, git blob, SHA256 and byte length in
`sources.lock.json`; original notices are retained under `Notices/`. The Boids
scene, subscenes, project settings, package manifest/lock and assets also have git
blob identities. Large textures/models were not downloaded. Google Benchmark is
ArborX's measurement framework, not the workload. GPUPrefixSums/GPUSorting native
primitive adapters and BabelStream's separate bandwidth workload are prepared in
the companion HLSL repository; the [HLSL consumer](../../Integrations/HlslKernelPipeline/README.md)
identifies the exact delivered scan assets. BabelStream does not establish spatial
query, full-frame or PCIe performance.

## Prepare and compile

From this repository root:

```powershell
python Tools/ExternalSources/prepare.py fetch-references cabana --directory Artifacts/refs/cabana
python Tools/ExternalSources/prepare.py fetch-references arborx --directory Artifacts/refs/arborx
python Tools/ExternalSources/prepare.py verify-checkout entities-boids --directory C:/src/official-ecs
python Tools/ExternalSources/prepare.py stage-boids entities-boids --directory C:/src/official-ecs --output C:/src/boids-summit
./Tools/ExternalSources/Build-Native.ps1 -Source cabana -SourceDirectory C:/src/cabana
```

`stage-boids` requires an untouched checkout at the pin and a new destination. It
copies the complete committed official project and adds the three embedded SUMMIT
packages and optional consumer sources. Ignored local caches are excluded. Staging
rejects nested paths, changed source bytes, dependency collisions and long Windows
paths before copying. The receipt records every upstream file and the hashes of
the actual added files, so a dirty local adapter cannot inherit an unchanged commit's
identity. Upstream dependency versions and the original package lock remain
preserved. The default `Build-Native` action prints a build
plan. `-Mode Build -DependencyPrefix <installed-prefix>` compiles only the named
upstream target, records hashes of the resolved dependency installation and never
invokes its executable or CTest. Supply an installed CMake with `-CMakePath`.
Build mode checks the source, installed Kokkos configuration and CMake before
creating output; use a short, separate `-OutputDirectory C:/b/summit-native`.
Upstream CMake validates the remaining dependency versions during configuration.
The C++ snapshot hook also has a standalone **object-library-only** CMake target.

In an explicitly launched Boids host, attach `BoidsSphereConsumer` and opt in to
the Unmeasured consumer. No auto-start hook or altered scene is shipped. Unsupported
compute/readback uses the labeled CPU complete-CSR fallback. A complete-result
capacity limit fails visibly instead of truncating entities. Readbacks retain the
owner until both offsets and IDs finish; disable/world replacement invalidates old
results without freeing submitted buffers prematurely. Readback extraction and
result-validation errors still finish both callbacks; failure while recording
creates no pending submission. An error after submission is attempted retains
ownership until completion is known. Published Entity handles
belong to `SourceFrame`; callers must check version/existence before acting on a
later world's entities.

## Native sphere snapshot and full-result fidelity

`Native/ArborXSnapshotBridge.hpp::write_arborx_snapshot` is a small export hook for
host mirrors of **the upstream generated inputs and no-callback complete CSR**.
Callbacks return the unchanged source point `(float3, original ID)`, actual
`predicate._geometry` centroid/radius, original offsets and result IDs. For the
pinned driver, `PrimitivesWithRadius` stores the radius in the point coordinate
type (`float`), even though the radius formula is calculated in double. Preserve
that converted value. Record the upstream Spec, compiler/backend, commit and
SHA256 of the `SMSPH001` file alongside the export. The hook contains no generator,
clock, `main`, or Kokkos startup. It was instantiated and compiled, never executed.

`ArborXSnapshot.Read` verifies the independently supplied SHA, source commit,
complete file length, positive upstream radius, unique original IDs and full CSR.
Pass its points and spheres to `GpuSphereWorkloadAdapter.Upload` with an explicit
domain. `Record` constructs SUMMIT's compact index, counts exact matches through
conservative candidate spans, scans counts, materializes the final offset, and
scatters **all original IDs**. Allocate worst-case `pointCapacity * queryCapacity`
result IDs or reject the request; there is no truncation/overflow success path.
Read back full CSR outside a future measured window and compare membership with
`SphereWorkloadContract.RequireFullMembership`; ordering within a query is not
promised, while multiplicity and every ID are required.

The exact test follows upstream `sqrt(sum(float delta * delta)) <= float radius`,
including the inclusive boundary. Supported finite coordinates/centers/radii are
bounded to magnitude 1e8; the upstream driver uses positive radii. The broader
functional predicate additionally defines zero and negative radii. Nonfinite,
out-of-domain points and duplicate IDs fail; distinct IDs at duplicate coordinates
remain distinct. Quantization only supplies a broad phase. Its axis halo is
`radius * (1 + 8 * float_epsilon) + 1e-18`, covering bounded float rounding and
squared-distance underflow before outward cell selection. Original floats are
never clamped or replaced by quantized coordinates in the final predicate.
GPU floating-point boundary behavior still requires execution validation; CPU
models and DXC compilation are not that validation.

Future complete-cost accounting must include original input export/staging or
equivalent production upload, quantization/key conversion, index construction or
maintenance, candidate traversal, exact filtering, scan, complete CSR materialization,
copies, synchronization, and actual consumption. Keep count-only/callback, full CSR,
neighbor iteration, permutation and kNN results in their distinct contracts.

The exact pinned Unity Editor and complete native Kokkos/Boost dependency builds
were not installed for this task. Validation compiled the adapters against actual
Unity 6000.5.2f1 and existing Entities assemblies, compiled the C++ snapshot hook and
HLSL entry points, and ran only small CPU contract cases. Full pinned application
import and upstream benchmark linking/execution are separate, unperformed checks.
