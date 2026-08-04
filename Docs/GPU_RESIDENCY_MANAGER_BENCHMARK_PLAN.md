# Large-map and point-cloud GPU residency benchmark

## Frozen A/B

- A rebuilds all 256 visible pages every frame, uploads their complete point
  payload, rewrites the page-table mappings, scatters the payload into a fixed
  physical cache, and runs the visible-page digest query.
- B uses the same physical cache capacity, visible set, camera path, query,
  point data, and output digests. It retains virtual pages in 384 LRU-managed
  physical slots and uploads only page misses plus compact page-table deltas.

Both paths start each block from an empty cache and receive identical warmup
and measurement paths. The path moves one page column per frame and teleports
every 128 frames, so the benchmark contains both coherent streaming and burst
faults.

## Metrics

- Native DX12 GPU average and P99 for upload, scatter, and query commands.
- Point payload and total logical upload bytes per frame.
- Page hit, miss, eviction, and teleport-burst behavior.
- CPU planning/staging average and P99.
- Counterbalanced paired wins and CPU/GPU digest correctness.
- Formal frozen gates: two scenarios, eight pairs each, at least 10% GPU-average
  improvement, non-negative GPU P99, at least 10% CPU preparation improvement,
  at least 85% average upload reduction, and all paired GPU averages winning.

## Claim boundary

This is an application-level page cache over ordinary Unity `GraphicsBuffer`
allocations. It does not claim D3D12 reserved resources, tiled-resource sparse
binding, driver memory-budget callbacks, disk or network I/O, or lossless point
cloud compression.
