# GPU Page Residency Manager: AMD Radeon AI PRO R9700

## Outcome

The persistent page-cache path passed the frozen formal acceptance contract on
both workloads. Compared with rebuilding and uploading the full 256-page visible
set every frame, the persistent LRU path reduced average upload traffic by more
than 92%, GPU average time by about 46%, and GPU P99 by 30-36%.

| Workload | GPU average | GPU P99 | CPU preparation | Average upload | Paired GPU wins |
|---|---:|---:|---:|---:|---:|
| 1,024 points/page | 45.88% faster | 35.90% faster | 80.93% faster | 92.31% lower | 8/8 |
| 2,048 points/page | 45.63% faster | 29.74% faster | 87.25% faster | 92.32% lower | 8/8 |

The optimized miss rate was 7.66%, versus 100% for the rebuild baseline. All 40
GPU digest validation rows passed, all 416 EditMode tests passed, and measurement
frames performed zero readback bytes.

## What changed

The baseline rebuilt the 256-page visible working set on every frame and uploaded
every page payload again. The optimized path keeps a fixed 384-slot physical GPU
cache and updates it incrementally:

1. A portable LRU planner maps 4,096 virtual pages to physical slots.
2. Camera motion produces compact page-table deltas and upload descriptors only
   for misses and evictions.
3. A compute scatter kernel writes new payloads into the persistent physical
   point cache.
4. GPU consumers resolve virtual pages through the page table and read the stable
   cache in place.
5. A deterministic GPU digest is compared with a CPU oracle outside measurement
   frames.

The workload moves a 16x16 visible window by one page column per frame and
teleports every 128 frames, so the result covers both steady traversal and burst
replacement rather than a permanently warm cache.

## Method

- Hardware: AMD Radeon AI PRO R9700, 32,476 MiB; Ryzen 9 9950X.
- API: Direct3D 12, feature level 12.2.
- Unity: 6000.5.2f1.
- Two workloads: 1,024 and 2,048 points per page.
- Four counterbalanced super-rounds, producing eight A/B pairs per workload.
- 60 warm-up frames and 900 measured frames per block.
- Native DX12 timestamps around upload, page-table update, scatter, and query.
- Formal gates were frozen after discovery: at least 10% GPU-average improvement,
  non-negative GPU P99, at least 10% CPU-preparation improvement, at least 85%
  upload reduction, and 8/8 paired GPU wins for both workloads.

Evidence is retained under
`Reports/GpuResidencyManager/formal-amd-r9700-f65aa48-v1` without the large raw
sample and Player log files.

## Claim boundary

This is an application-level virtual-page cache backed by fixed-capacity Unity
`GraphicsBuffer` allocations. It is not a Direct3D 12 reserved-resource or tiled
resource implementation, and all evidence records `sparseResourceClaim=false`.
The current performance result is validated only on AMD Radeon AI PRO R9700; it
must not be presented as NVIDIA-validated.

## Resume-ready wording

Implemented and profiled a GPU-resident page-cache for large-map and point-cloud
streaming on DX12. Replaced full visible-set rebuilds with an LRU virtual-to-
physical page table, compact delta uploads, and compute scatter into fixed GPU
storage. In counterbalanced 4x900-frame tests on AMD Radeon AI PRO R9700, reduced
average transfer traffic by 92.3%, GPU average time by 45.6-45.9%, and GPU P99 by
29.7-35.9%, with 8/8 paired wins per workload and deterministic GPU/CPU digest
validation.
