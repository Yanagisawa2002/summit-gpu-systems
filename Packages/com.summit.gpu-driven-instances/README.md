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

## Hierarchical multi-view visible-only path

Version 0.3 adds an explicit, opt-in hierarchy capacity and
`RecordHierarchicalVisibleOnly`. The original constructor and flat `Record`
API remain available without hierarchy scratch. The host builds immutable
contiguous descriptors with `GpuInstanceClusterBuilder` (or an equivalent
producer): each `GpuInstanceCluster` contains a conservative sphere, a range of
at most 64 instances, and the union of those instances' view masks. Active
ranges must be non-overlapping and exactly cover the active instance prefix.
All four sphere components must be finite and the radius must be non-negative;
invalid bounds fail closed before coarse classification.

The GPU records these stages without readback:

1. validate every range boundary and fail closed on an invalid cover;
2. test each `(cluster, view)` sphere after its union-mask check;
3. launch one 64-thread fine group per cluster, using a two-dimensional
   dispatch when the cluster count exceeds 65,535;
4. visit only bits surviving both the coarse mask and instance view mask,
   perform exact instance visibility/LOD, and densely append visible
   `(bin, instance)` pairs while pre-counting bins;
5. scan the pre-counted bins and scatter the GPU-count prefix indirectly;
6. reuse the normal five-word indexed-indirect argument builder.

No wave intrinsics, `SV_DrawID`, asynchronous compute, CPU count readback, or
scene-specific policy is required. Outputs are always visible-only. Callers
also provide a three-word `hierarchyStatistics` buffer, cleared on every
record: coarse-visible cluster/view count, fine candidate instance/view count,
and visible-pair count. The last word is the same append counter consumed by
the indirect scatter, so evidence does not rely on a second counter.

Cluster bounds and union masks must be rebuilt when member positions, radii,
membership, or view masks change. Malformed ranges set
`InvalidClusterContract`, keep statistics/counts/offsets and instance counts in
draw arguments at zero, and do not reuse the previous frame's dispatch size.
Any later hierarchy or pre-counted-binning diagnostic also forces every draw
instance count and base-instance offset to zero before indirect rendering;
callers must still reject the diagnostic-marked CSR payload itself.

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
public source contract use Unity Collections 6.5. Version 0.4 adds the
single-use planned-upload contract described below.

An external policy can inspect a normalized decision without paying for a
second normalization pass:

1. increment a caller-owned nonzero source revision whenever the authoritative
   source contents or backing allocation changes;
2. retain the nonzero resident destination revision from which the dirty ranges
   were computed;
3. call `PlanDirtyUpload(destination, source, activeCount, sourceRevision,
   expectedResidentStateRevision, ranges, count)`;
4. inspect the returned `GpuInstanceDirtyUploadPlan.Receipt`;
5. pass that same token and source revision to `RecordPlanned` if the policy
   selects dirty upload.

The immutable token is bound to its uploader, scratch generation, active
count, nonzero source revision, expected nonzero resident revision, exact source
array, and exact destination buffer. In checked Unity Collections builds, the
token also retains the source array's original `AtomicSafetyHandle`;
`RecordPlanned` verifies that handle
still exists before accepting any replacement allocation, including a native
pointer reuse, and `IsValid` becomes false once the source allocation is
disposed. The revision remains mandatory in every build, so release builds do
not reduce source identity to a raw address comparison.

Only one plan can be live per uploader because the uploader owns one fixed-
capacity range scratch. Creating another plan invalidates the earlier token,
and successful recording consumes every copy of a token. Stale, copied-after-
use, cross-uploader, revision, source, destination, and active-count mismatches
are rejected before any command is recorded. Capacity checks are repeated at
record time. Argument validation failures do not consume an otherwise-current
token, so a caller may correct the call without replanning. Warm planning and
recording allocate no managed memory. The 64-bit scratch generation never
wraps: after its state space is exhausted, that uploader permanently rejects
later planning and must be replaced before another dirty plan.

`RecordDirty` remains source-compatible and uses a private immediate single-
plan path that does not expose a reusable token. Receipt-only `PlanDirty`,
immediate uploads, and explicit `RecordFull` also remain available. Any later
planning or full-upload call invalidates an outstanding planned token rather
than risking use of overwritten scratch ranges. The `None`, `DirtyRanges`, and
capacity-fallback `Full` semantics and accounting are identical across the
legacy and token APIs.

The legacy `PlanDirtyUpload` overload without
`expectedResidentStateRevision` remains source-compatible and records zero for
that fact. Such an unbound plan can still be recorded explicitly by its owner,
but it cannot authorize the automatic selector's `Dirty` policy; first-frame or
unknown-resident callers therefore fail closed to `Full` until they can provide
an exact nonzero resident base revision.

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
free warmed planning/recording, planned-token ownership/revision/generation/
single-use and generation-exhaustion checks, disposed-source safety-handle
rejection, full-buffer GPU readback after partial updates, cluster
builder boundaries, hierarchical/flat/oracle parity, malformed-cover fail-
closed behavior, exact causal statistics, and the two-dimensional fine-dispatch
boundary.

This first package PR establishes correctness and a benchmarkable API. It does
not claim a frame-time improvement. A later PR must add a frozen CPU-versus-GPU
benchmark with native timestamps and retain neutral or negative cells.
