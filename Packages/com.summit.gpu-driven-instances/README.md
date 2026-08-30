# SUMMIT GPU Driven Instances

`com.summit.gpu-driven-instances` records a scene-independent GPU pipeline for
multi-view sphere visibility, distance LOD selection, view/group binning, and
indirect draw argument generation. The package knows nothing about traffic,
buildings, terrain, sensors, networking, meshes, materials, or city data.

## Contract

The caller owns four read-only inputs:

- `GpuInstanceState` records containing a bounding sphere, up to four ordered
  LOD distances, an opaque application ID, a draw-group base, and a view mask;
- six normalized frustum planes per view;
- one camera-position/LOD-scale vector per view;
- one `GpuDrawTemplate` per draw group.

`GpuDrivenInstancePipeline.Record` produces:

- one count and CSR offset per `(view, draw group)`;
- a grouped instance-index stream whose visible prefix ends at
  `groupOffsets[VisibleBinCount]`;
- either a final culled bin or a visible-only stream that discards rejected
  pairs before atomic scatter;
- five-word indexed-indirect arguments per visible bin;
- fail-closed diagnostics for malformed instance or view contracts.

The GPU path performs no readback and allocates no buffers while recording.
Gameplay and simulation authority remain with the host. Atomic scatter leaves
order within a draw group unspecified; membership, counts, offsets, and
arguments are the stable public contract.

## Dynamic instance-state uploads

`GpuInstanceStateUploader` adds an optional engine-native upload path for a
host-owned persistent `NativeArray<GpuInstanceState>` mirror. It deliberately
exposes separate `RecordFull` and `RecordDirty` entry points instead of hiding
the choice behind an unproven heuristic:

- dirty ranges may arrive in any order and are normalized into sorted,
  non-overlapping half-open ranges;
- overlapping and adjacent ranges are unioned, with an optional fixed clean-gap
  bridge chosen by the caller;
- zero dirty ranges record zero upload commands and zero logical bytes;
- exceeding the predeclared range capacity fails safe to one full upload rather
  than dropping an update;
- every call returns exact command, dirty-record, uploaded-record, bridged-clean,
  and logical-byte accounting. A capacity fallback marks the dirty count as
  unavailable (`DirtyRecordCountExact == false` and count zero) rather than
  pretending it is exact.

The receipt's logical byte count is the number of bytes requested through
Unity's buffer-upload API. It is not a measurement of PCIe traffic, driver
staging, or copy-engine traffic. A source array recorded into a command buffer
must remain immutable until an `AllGPUOperations` fence has passed. Immediate
`UploadFull` and `UploadDirty` helpers exist for initialization and validation;
timed rendering should use the matching command-buffer APIs for full and dirty
variants. Version 0.2 requires Unity 6000.5 because its persistent scratch and
public source contract use Unity Collections 6.5.

## Safety and limits

- View capacity is limited to 32 because `ViewMask` is a `uint`.
- LOD counts must be in `[1, 4]`; used distances must be positive and
  nondecreasing.
- `DrawGroupBase + LodCount` must not exceed the active draw-group count.
- Frustum planes must be normalized and view LOD scale must be positive.
- Malformed records set diagnostics and are routed to the culled bin or the
  explicit non-error discard key, depending on `GpuDrivenInstanceOutputMode`.
- Unknown/unsupported devices must retain a host-owned CPU or conventional
  renderer path. `SupportsCurrentDevice` is a capability gate, not a
  performance claim.

## Validation status

Editor tests include an independent CPU oracle, dispatch boundaries, multiple
views, view masks, LOD selection, culled-tail and visible-only membership,
exact indirect arguments, zero work, invalid-contract diagnostics, argument
validation, disposal, dirty-range normalization, safety fallback, allocation-
free warmed planning, and full-buffer GPU readback after partial updates.

This first package PR establishes correctness and a benchmarkable API. It does
not claim a frame-time improvement. A later PR must add a frozen CPU-versus-GPU
benchmark with native timestamps and retain neutral or negative cells.
