# BFP2 Mission-Area Residency

`Bfp2GpuIndirectRenderer` treats runtime file layout and residency policy as separate concerns.
Outside a committed mission it may prefer generated mega packs. While a mission-area snapshot is
active, it scans the spatial source `.bfp2` packs instead: a legacy full-city `.megabfp2` AABB is
not admissible evidence for a bounded mission.

## Admission and hard limits

- Buildings require the mission `Buildings` layer. Roadbed, parking-lot, entrance, and other
  non-building BFP2 layers require `StreetSpace`.
- A pack is eligible only when its transformed world bounds intersect the snapshot's resident
  world bounds.
- Camera/frustum state ranks eligible packs for loading and drawing; it does not expand the
  mission bounds.
- Accepted packs are limited by the renderer's per-layer count/byte limits and by the lower of
  `maxTotalResidentPacks` and the mission `maxBfp2Packs` limit.
- The mission `maxBfp2GpuMegabytes` limit reserves both the normal indirect-render buffers and the
  optional secondary visible-index/args/stats output used by adaptive multi-camera culling.
- `loadAllEnabledPacksResident`, `drawAllResidentPacks`, pinning, and mega-pack preferences cannot
  bypass mission admission or the hard count/byte limits.
- Persistent roaming buffer pooling and duplicate render-scheduler groups are suppressed while a
  mission is active. Buffers from the previous revision are retired before replacement uploads.

The registered-camera union, procedural indirect draw path, and the 2.5x adaptive cull split are
unchanged. Only feeds scheduled for the current frame participate.

## Revision convergence report

The renderer publishes `NYCGISMissionAreaConsumer.Bfp2` through
`NYCGISMissionAreaResidencyContext.Report`. `ready` is true only when the report is for the current
revision, requested spatial layers exist, every eligible pack fits the configured limits, every
accepted pack is resident, no old-revision resident or pending pack remains, and actual allocated
pack/upload/pool GPU bytes remain under the mission ceiling. If the bounded area needs more packs
or bytes than allowed, the report remains not ready rather than silently loading a partial city.

## Offline macro packs

`Tools > NYC GIS Demo > BFP2 > Build Mega Packs For Current Renderer` now Morton-orders source-pack
centres and writes spatial macro packs of at most eight source packs. Rebuilding with overwrite
enabled removes stale `.megabfp2` outputs first. Mission mode currently continues to use original
spatial source packs because the existing mega format has no explicit spatial-layout manifest;
this prevents an old full-city mega file from being mistaken for a bounded macro.

The external-data contract is enumerated in
`ProjectSettings/NYCGISDriveDataManifest.json`: 59 building, 128 roadbed, 32 parking, and 8
entrance source packs (227 total). Missing packs do not trigger an automatic fallback to a
full-city mega pack. The residency report names the missing configured layers and remains
fail-closed so a data-installation problem cannot silently turn into an unbounded CPU/GPU load.

## Validation boundary

The runtime and editor C# assemblies compile after these changes. Full-city convergence, actual GPU
memory, and camera-union behaviour still require Unity Play Mode and target-hardware profiling; no
production scene was modified by this change.
