# NYCGIS / BFP2 integration snapshot

This directory preserves project-authored integration code for the city-rendering and sensor case studies. It is intentionally outside the standalone Unity project's `Assets` directory and is not compiled by the root benchmark host.

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

The snapshot references NYCGIS streaming frames, mission residency contracts, data-root configuration, weather/payload camera registries, BFP2 files, URP materials, and full-city build automation that are not included here. It therefore cannot be copied into an arbitrary Unity project unchanged.

To use it in an authorized SUMMIT host:

1. Copy or package the required `Assets` paths into the host while preserving relative shader/resource paths.
2. Copy the scripts from this directory's `Tools` folder into the host's root `Tools` folder. Those scripts intentionally resolve the Unity project as the parent of `Tools`.
3. Restore the host-only NYCGIS contracts and data configuration.
4. Validate scalar output first, then WaveCompact, visible tiles, and local-index variants with identical hashes and camera schedules.
5. Keep all city data and generated benchmark output in the host repository or external data root, not in this GPU systems repository.

The long-term direction is to replace the host types with narrow interfaces and move the reusable cluster stream/visibility implementation into a ninth UPM package. Until that contract work is complete, this directory is an integration reference rather than a portable library.
