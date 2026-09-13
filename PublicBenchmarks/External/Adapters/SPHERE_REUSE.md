# Reuse a point snapshot across complete sphere-query batches

`GpuSphereWorkloadAdapter` retains `Upload(domain, points, queries)` +
`Record(commands)` for callers that want one complete snapshot/rebuild/query.
The separate preparation path serves applications with multiple query batches
against the same point snapshot. It uses the same index and exact full-CSR query
implementation; there is no cached-result or CPU fallback in this path.

```csharp
using var adapter = new GpuSphereWorkloadAdapter(pointCapacity, queryCapacity, true);
using var commands = new CommandBuffer();

// Start whole-task timing before this call when comparing total task cost.
adapter.UploadPoints(domain, points);
adapter.RecordIndexBuild(commands);
Graphics.ExecuteCommandBuffer(commands);
adapter.CompleteIndexBuild(); // Synchronous eight-byte completion readback.
commands.Clear();

foreach (SourceSphere[] queries in queryBatches)
{
    adapter.UploadQueries(queries);
    GraphicsFence batchFence = adapter.RecordQueries(commands);
    Graphics.ExecuteCommandBuffer(commands);
    // Retain adapter until this batch's full offsets/IDs and all consumers have
    // completed. Synchronous GetData blocks; async consumers must finish both
    // readbacks before the next UploadQueries, UploadPoints or Dispose.
    var offsets = new uint[queries.Length + 1];
    adapter.Offsets.GetData(offsets, 0, 0, offsets.Length);
    var ids = new uint[offsets[queries.Length]];
    if (ids.Length != 0) adapter.Ids.GetData(ids, 0, 0, ids.Length);
    ConsumeAllRows(offsets, ids); // Application's actual consumer.
    commands.Clear();
}
```

`RecordIndexBuild` records GPU work and returns a graphics fence; it does not
submit or execute. An asynchronous application can wait for that returned fence
before calling `CompleteIndexBuild`. The latter reads a unique build serial
written after the index commands and checks the current point generation. A
recorded, unsubmitted, or discarded build cannot certify an old index. Readiness
is then cached on the CPU for query batches until it is invalidated.
Unity documents [command-buffer data uploads](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Rendering.CommandBuffer.SetBufferData.html)
and [fence completion](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Rendering.GraphicsFence-passed.html);
the actual GPU fixtures additionally exercise the abandoned-build boundary.

Call on Unity's main thread and retain one owner for one outstanding batch.
Submit each recorded command buffer once in record order. Do not replay old
commands after point/query replacement. GPU fences do not finish arbitrary later
readbacks or application consumers: those remain the caller's responsibility.
For canceled work, discard unsubmitted commands before disposing their owner;
never dispose buffers that may still be referenced by submitted work. Create a
new owner after abandoning a preparation sequence if completion is unknown.

| Operation | Invalidation and ownership |
| --- | --- |
| `UploadPoints(domain, points)` | Requires previous reusable GPU work complete. Starts a new point generation and invalidates index and queries. An invalid value/capacity replacement leaves them invalid. Domain changes use this same call, even with the same points. |
| `UploadQueries(queries)` | Requires uploaded points and completed previous reusable GPU work. Encodes bounds against the stored domain, changes query generation and retains the prepared index. A rejected query batch invalidates queries only. |
| `RecordIndexBuild` | Requires uploaded points and graphics-fence support. Always records a real full rebuild, including an empty point set. Invalidates prior readiness and creates a unique build serial. |
| `CompleteIndexBuild` | Requires the recorded current build to have executed. Confirms its command-ordered stamp with readback; recording alone is insufficient. It never submits commands. |
| `RecordQueries` | Requires the confirmed current index, validated queries and completion of previous reusable GPU work. Records full count/scan/scatter and returns this batch's fence. No index rebuild or point upload. |
| `Upload` + `Record` | Original convenience behavior, including rebuild on every nonempty `Record`. Caller retains its original fence/readback ownership obligation. A legacy record does not certify a reusable index. |
| `Dispose` | Idempotent. Caller must first drain submissions/readbacks or discard never-submitted commands. Subsequent operation calls throw. |

`PointGeneration` and `QueryGeneration` identify the snapshot/batch for consumer
bookkeeping; they are not hardware counters or proof that arbitrary external
consumers completed. Buffer contents belong to the last completed query. Do not
publish stale readbacks after application-world/snapshot invalidation.

Capacity is still bounded complete CSR: `pointCapacity * queryCapacity` IDs or
rejection, never truncation. Smaller/empty replacements clear the active mask;
distinct IDs at duplicate coordinates remain distinct. Every batch is returned
in original query order; IDs within a query may be unordered.

The [actual optimization protocol](../../../Docs/SPHERE_REUSE_PROTOCOL_2026-09-10.md)
charges point preparation and one rebuild inside each complete repetition. Using
this interface does not establish a speedup for an application or change default
candidate/profile eligibility.
