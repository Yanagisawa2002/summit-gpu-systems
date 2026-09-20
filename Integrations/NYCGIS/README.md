# NYCGIS / BFP2 integration snapshot

This directory preserves project-authored integration code for the city-rendering and sensor case studies. It is intentionally outside the standalone Unity project's `Assets` directory and is not compiled by the root benchmark host.

## Asset-independent core inspection

The [standalone visible-tile sample](../../PublicBenchmarks/VisibleTiles/README.md)
stages the original scalar/wave compute producers and shared multi-camera cone
helper byte-for-byte into a small generated Unity project with procedural input,
a real indirect-draw consumer and independent geometry/image acceptance checks.
Its build/run entry points do not require the NYCGIS host below. CPU/DXC checks
do not establish actual Unity import or GPU execution; read the sample status.
It is a mechanism reproduction, not the historical city workload or its speedup.

## Included techniques

- Scalar and compact cluster culling fallbacks.
- Wave64 prefix compaction with one output reservation per wave.
- No-copy 32/64-triangle visible-tile descriptors with direct source-index fetch in the vertex shader.
- Cluster-local packed 16-bit indices plus `baseVertex` reconstruction.
- Multi-camera culling input and explicit camera render grouping.
- GPU-resident sensor preprocessing and spatial-index experiments.
- GPU vegetation culling and indirect rendering.
- Cinematic/stress A/B orchestration and native GPU timestamp integration.

## Host dependencies

The full snapshot references NYCGIS streaming frames, mission residency contracts, data-root configuration, weather/payload camera registries, BFP2 files, URP materials, and full-city build automation that are not included here. The full renderer therefore cannot be copied into an arbitrary Unity project unchanged; the sample isolates only the shader producer/consumer contract.

To use it in an authorized SUMMIT host:

1. Copy or package the required `Assets` paths into the host while preserving relative shader/resource paths, including the shared `Bfp2NormalConeVisibility.hlsl` include.
2. Copy the scripts from this directory's `Tools` folder into the host's root `Tools` folder. Those scripts intentionally resolve the Unity project as the parent of `Tools`.
3. Restore the host-only NYCGIS contracts and data configuration.
4. Validate scalar output first, then WaveCompact, visible tiles, and local-index variants with identical hashes and camera schedules. Use an independent visibility oracle; scalar/wave agreement alone is insufficient.
5. Keep all city data and generated benchmark output in the host repository or external data root, not in this GPU systems repository.

Before further generalization, complete the standalone sample's actual device
acceptance. A ninth UPM package is not a prerequisite for this bounded delivery.
The full host-dependent renderer remains an integration reference, not a portable
library. Historical measurements and their original scope are unchanged.
