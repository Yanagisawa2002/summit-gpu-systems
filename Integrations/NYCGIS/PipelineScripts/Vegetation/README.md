# NYC full-city GPU vegetation

This path draws every record in the current NYC Tree Map export while keeping
per-frame CPU work nearly constant.

## Current payload

- 892,947 trees, all grounded against the full-city DEM
- 10,541 spatial clusters at 256 m
- 27.25 MiB tree records and 0.32 MiB cluster records on disk
- About 34.4 MiB of runtime GPU buffers, including near/far visibility indices
- EPSG:32118 horizontal coordinates and NAVD88 / Geoid 12B terrain heights

The payload is generated under:

`C:\Users\EdwinLiu\Downloads\NYCGISData\data\vegetation_processed\nyc_tree_gpu_full_v1`

## Rebuild

Run from the SUMMIT repository:

```powershell
python PipelineScripts\Vegetation\build_nyc_gpu_vegetation.py `
  --input C:\Users\EdwinLiu\Downloads\NYCGISData\data\vegetation_processed\nyc_tree_map_citywide_v1\nyc_tree_map_citywide.csv `
  --terrain-manifest C:\Users\EdwinLiu\Downloads\NYCGISData\data\terrain_processed\nyc_dem_terrain_full_city\manifest.json `
  --output-dir C:\Users\EdwinLiu\Downloads\NYCGISData\data\vegetation_processed\nyc_tree_gpu_full_v1
```

## Runtime design

- File reads happen asynchronously.
- Uploads are throttled to 8 MiB per editor/player frame.
- One structured tree buffer and one cluster buffer stay GPU-resident.
- A compute shader rejects clusters and classifies visible trees into near and
  far append buffers.
- Culling only refreshes after a meaningful camera move, rotation, or projection
  change.
- Rendering uses three procedural indirect submissions: trunks, near crowns,
  and far crowns.
- The forward and ShadowCaster passes use the same GPU-generated geometry.

This avoids per-tree GameObjects, Transforms, renderers, CPU frustum tests, and
draw-call construction.

## Unity tools

Use `Tools > NYC GIS Demo > Vegetation GPU`:

- `Install Full-City Renderer`
- `Validate Full-City Renderer`
- `Focus 200m Tree Detail`
- `Pause Rendering (Keep GPU Residency)`
- `Resume Rendering`

The pause/resume pair is intended for A/B profiling without changing residency
or triggering another full data upload.

## Performance note

The current full-shadow stress configuration intentionally uses a 100 km draw
distance and no minimum screen-size rejection. In the tested wide city editor
view, the compute pass classified 778,298 trees as visible. The DX12 3D engine
was already saturated by the full-city building view even while vegetation
draws were paused, so GPU-utilization percentage alone cannot isolate the tree
increment in that view.

For a production default, keep all records resident but add a screen-size
cutoff, restrict real-time tree shadows to a near distance, and use the far
octahedral crown only as a color/occlusion cue.

## Coverage caveat

"Full city" means every record present in the current NYC Tree Map export. It
does not infer unrecorded natural woodland trees or canopy mass. Those areas
need a separate canopy or land-cover fill layer if visual coverage must match
orthophotos rather than the inventory.
