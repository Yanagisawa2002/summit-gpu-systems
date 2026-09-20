# No-copy visible tiles: change the representation, not just the kernel

[Project overview](../README.md) · [Evidence index](../Evidence/README.md) · [Run or inspect](RUNNING.md)

**Engineering question:** after visibility is known, does the renderer need a newly copied list of every visible index?

**Evidence status:** historical NYCGIS experiment on AMD Radeon AI PRO R9700 / D3D12 / Unity 6000.5.2f1. The current integration source is inspectable, but is not a standalone reproduction of the historical measurement. The root procedural benchmarks measure different tasks.

## Problem and change

The copied-index path culls clusters, reserves output ranges and copies their visible source indices into an intermediate buffer. The draw then consumes that copied output. Wave-level reservations reduce contention, but leave the index copying in place.

The no-copy path instead emits a small descriptor for each visible tile. Each descriptor identifies a source-index range and its valid length. The vertex shader resolves that range directly against the original index buffer. This removes the intermediate index copy; it does not remove the original geometry, source-index storage or every GPU memory access.

```text
Copied-index path
  visible clusters -> reserve output -> copy each index -> draw copied indices

Visible-tile path
  visible clusters -> write range descriptors -> draw from original indices
```

[![Historical NYCGIS logical-output comparison](portfolio/overview.svg)](portfolio/overview.png)

The figure's flow is schematic. Its bar values are logical output payload, not measured physical VRAM or DRAM traffic. [Figure inputs and rendering instructions](portfolio/README.md).

## Retained result and its boundary

| Recorded observation | Supported interpretation |
| --- | --- |
| Approximately 385.7 MB of visible indices replaced by 8.04 MB of descriptors | A much smaller logical intermediate output in the historical NYCGIS workload; not total application memory savings. |
| 52.5% lower GPU average time; 51.9% lower GPU P99 | The dedicated historical single-camera comparison against WaveCompact, not a measurement of the current checkout or universal full-engine frame-time improvement. |
| Wave64 compaction reduced returned atomic reservations by at least 98.19%; four-camera GPU average fell 2.28% | A separate experiment showing why eliminating atomic work and eliminating index-copy work are different optimizations. It is not an additional percentage to add to the single-camera result. |

Source: [historical portfolio index](GPU_PERFORMANCE_ENGINEERING_PORTFOLIO_INDEX_2026-07-31.md). The no-copy experiment is identified there as `codex/gpu-no-copy-visible-tiles`.

The [historical evidence identity note](RemediationIntegration20260908.md#historical-evidence-identity) pins the retained report at `63c5bfd6fdb7c44516cbc8c4a70cb95c28013cf1`, with normalized-LF SHA256 `05f7a33f0f957dbbb9ca650cb6e15110625224439ecffa53a452f5d08be03109`. That is a **report identity**, not an attestation of the measured binary or a newly recovered original implementation. No documentation refresh promotes current source into historical measured source.

## Read the producer and consumer together

| Responsibility | Source and symbols |
| --- | --- |
| Original scalar copy and compact culling input | [Bfp2GpuClusterCull.compute](../Integrations/NYCGIS/Assets/Shaders/NYCGISDemo/Bfp2GpuClusterCull.compute): `CullAndCompactBfp2Clusters`, `PackBfp2CompactCullClusters`. The latter builds a 40-byte hot record from the original 80-byte record. |
| Wave reservation, copied output and descriptor output | [Bfp2GpuClusterCullWave.compute](../Integrations/NYCGIS/Assets/Shaders/NYCGISDemo/Bfp2GpuClusterCullWave.compute): `CullAndCompactBfp2ClustersWave`, `Bfp2CullAndBuildVisibleTiles`, `CullAndBuildBfp2VisibleTiles32/64`. |
| Real draw-side consumption | [Bfp2GpuIndirectURP.shader](../Integrations/NYCGIS/Assets/Shaders/NYCGISDemo/Bfp2GpuIndirectURP.shader): `Bfp2ResolveSourceVertex` maps procedural vertex IDs through descriptors to source indices. |
| Buffer bindings, variants and host integration | [Bfp2GpuIndirectRenderer.cs](../Integrations/NYCGIS/Assets/Scripts/NYCGISDemo/Bfp2GpuIndirectRenderer.cs): `Bfp2ClusterCullAlgorithm` and renderer bindings. |

The 32-triangle variant covers up to 96 indices per tile; the 64-triangle variant covers up to 192. Both use two-word descriptors. The separate cluster-local uint16 variant adds packed-index decoding and base-vertex reconstruction; its memory-format result is not a stable frame-time win.

## Costs that still matter

The consumer now performs descriptor lookup and source-index addressing. Fixed-size tiles also submit padded work for a partial final tile; the shader maps invalid lanes to the first source index, producing degenerate tail triangles for valid triangle-list input. Smaller logical output alone does not establish a lower complete draw cost.

A fair comparison must therefore retain identical geometry, visibility rules, camera schedule, residency, shader work and output acceptance. Measure both production and consumption, and report the fixed-tile padding, descriptor storage and source-buffer lifetime. Multi-camera union visibility and optional normal-cone culling require their own correctness tests; agreement between variants sharing one predicate is not an independent visibility oracle.

## What can be reproduced today

The [NYCGIS integration guide](../Integrations/NYCGIS/README.md) lists the missing host contracts. This directory is deliberately outside root `Assets` and is **not compiled by the root Unity benchmark host**. Do not expect cloning the repository or running its query demo to reproduce this city-rendering result.

The existing [Windows query/transport demo](../PublicBenchmarks/UnityGpuIntegration/RESULTS-query-boundary-2026-09-08.md) is a separate executable consumer. The [run guide](RUNNING.md) distinguishes source inspection, offline evidence checks, CPU contracts and hardware execution.

## Next bounded acceptance milestone

Build an asset-independent renderer containing only Scalar copy, WaveCompact copy and Visible-tile no-copy. Use procedural clusters and real draws, with high/low visibility, partial tiles, and one/multiple cameras. Validate actual visible geometry and image equivalence, not only matching aggregate counters. Bind the source commit, built player, settings and raw samples before publishing new results.

This is planned work, not an existing demo or a promise to reproduce 52.5%. Do not add unrelated subsystems before this path is independently runnable.
