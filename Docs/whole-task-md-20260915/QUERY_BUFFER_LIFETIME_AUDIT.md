# SUMMIT query-buffer ownership and the future Unity GPU consumer

Source audit of public repository base
`b8d63bff657bae54eaca09864f92fb065becb30c`. No API/runtime change or Unity test was
performed. All file references below are repository-relative, with current
one-based lines. The proposed consumer still awaits the Linux Player gate.

## Confirmed API behavior

| Evidence | Consequence |
| --- | --- |
| `PublicBenchmarks/External/Adapters/GpuSphereWorkloadAdapter.cs:18-36,50-56` | Each adapter owns one mutable set of points, encoded samples, active slots, query spheres/cells/counts, offsets/IDs, index and build stamp. Only offsets and IDs are public GPU buffers. Source positions and the index owner are private. |
| Same file `:75-91,190-196` | `UploadPoints` invalidates the index/query generations, clears the full active mask, and immediately calls `GraphicsBuffer.SetData`. Failed point/domain replacement also invalidates readiness. |
| Same file `:97-113` | `UploadQueries` requires reusable idle, increments the query generation and immediately overwrites the one spheres/cells pair with `SetData`. Query replacement retains a completed point index. These are not command-ordered uploads for many queued batches. |
| Same file `:118-144` | `RecordIndexBuild` records the actual rebuild, an ordered 64-bit serial stamp and a fence. Recording does not make the index ready. `CompleteIndexBuild` performs an eight-byte `GetData` and confirms the serial/current point generation. This CPU-visible completion step is required by the public reusable API. |
| Same file `:26,149-157,199-201` | `RecordQueries` requires CPU-confirmed readiness and a passed prior reusable fence. It records count/scan/scatter and inserts its fence **before any application consumer appended later**. The idle guard therefore covers that query work, not later readers of the outputs. |
| Same file `:159-187` | The legacy path rebuilds for each nonempty query call and does not certify a reusable index. Recording itself never submits work. Count/scan/scatter reuse the same buffers. |
| Same file `:211-215` | `Dispose` releases buffers without checking for pending GPU readers. Draining submitted consumers/readbacks or discarding unsubmitted commands is the caller's responsibility. |
| `Packages/com.summit.gpu-sensor-pipeline/Runtime/GpuSensorFullRebuildIndex.cs:24-28,52-68` | The lower-level index exposes bin offsets and binned IDs, records key preparation and binning, and reports index resident bytes. It does not implement the adapter's CPU stamp/readiness protocol. Using it directly would be a new integration with its own lifetime proof. |

The existing API contract already states the owner/drain responsibility in
`GpuSphereWorkloadAdapter.cs:10-14` and `PublicBenchmarks/External/Adapters/SPHERE_REUSE.md`.
This is an integration constraint, not a claim that the existing documented
CPU-readback caller is defective.

## The consumer fence must follow the force dispatch

The future adapter-preserving implementation must use this order:

1. Own immutable device positions for the input snapshot plus velocity, force
   and output positions. The adapter's private point buffer is not accessible;
   a separate consumer position upload is charged unless a reviewed buffer-view
   extension is added. This benchmark's contiguous original IDs are explicitly
   validated; arbitrary public source IDs cannot silently become array indices.
2. Upload the points/domain, record and submit the index build, then complete
   its stamp readback. Keep that wait/readback inside the complete-task clock.
3. For each <=128-query batch, upload its spheres, record the query, append the
   force dispatch reading `Offsets`/`Ids` and immutable input positions, then
   append an **application consumer fence**. Submit this command buffer once.
4. Wait for that consumer fence before uploading another batch, replacing
   points, or disposing. Waiting only for the adapter's earlier query fence is
   insufficient: it could pass while force still reads IDs that the next query
   will overwrite. Any audit readback must also finish before reuse.
5. Write each batch only to its own global force rows. Keep positions unchanged
   until all batches have consumed the original snapshot. Then dispatch the
   original full velocity/position update and wait for its final completion
   fence. End timing with complete device-resident state.

The exact predicate is `PublicBenchmarks/External/Adapters/Resources/SummitExternalSphere.compute:23-33`.
Traversal/count/scatter at `:35-66` emits all sphere members, including the
particle's own ID. The current benchmark removes only that ID on the CPU at
`PublicBenchmarks/WholeTaskMolecularDynamics/Unity/MolecularPlayer.cs:188-191`
before CPU `State.Consume` at `:195`. The future force kernel must perform the
same original-ID exclusion. It must retain coincident **distinct** IDs for the
membership fixture; that fixture must not invoke the singular force law.

## What cannot be assumed

* Recording all batches up front against one adapter is invalid: immediate
  query-buffer overwrites and the pending reusable-fence guard prevent that
  ownership model. CPU generation counters are not buffer versions retained
  for old command buffers.
* Two adapters are two complete owners with two indices and their allocations/
  builds; a ring of adapters does not give free sharing of one prepared index.
* The eight-byte build completion can only be removed by changing the
  integration/protocol and validating the replacement. It cannot be relabelled
  as GPU-only simply because later force consumption is on the device.
* Clearing an unsubmitted recorded rebuild does not reset the owner to ready.
  Discard the commands and use fresh ownership if completion is unknown. Never
  replay stale recorded commands across point or query generations.
* Use one ordered graphics queue for the initial consumer. Async compute or
  cross-queue overlap requires an additional explicit dependency/lifetime proof.

For the primary 4000-particle task, 128-query batches mean 32 batches and a
2,048,000-byte worst-case ID buffer. At 32000 particles the same cap means
16,384,000 bytes. The adapter's other buffers, index/scratch, consumer positions/
velocities/forces and any audit storage are additional. The current 64 MiB ID
cap does not authorize an unbounded all-query `N*Q` allocation.

## Validation required when implementation is admitted

Existing source fixtures in `PublicBenchmarks/External/Actual/SphereReuseFunctional.cs:68-125`
exercise before-submit replacement rejection, changed/shrunken/empty queries,
domain replacement, invalid input invalidation, legacy/prepared transitions and
discarded build stamps. They were not rerun here and do not test a later GPU
force reader. Add focused device validation for: query fence passing before a
delayed consumer; consumer-fence-protected reuse; 129-query tail; zero neighbours;
point/domain replacement after full consumption; canceled unsubmitted work;
and full CSR plus force/velocity/position comparison against the common snapshot.
The initial safe design retains per-batch host waits. Their cost is part of the
result and may make the adapter unsuitable for this consumer; no gain is assumed.
