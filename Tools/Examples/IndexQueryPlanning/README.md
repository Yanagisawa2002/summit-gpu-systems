# Inspect a complete index/query plan

This example calls the shipped pure planner using four explicitly synthetic
application situations. It builds no scene, generates no benchmark workload and
records no timings, counters or GPU commands. It is an adoption example, not an
additional performance harness.

With .NET 10 installed, from the repository root:

```powershell
dotnet run --project Tools/Examples/IndexQueryPlanning/IndexQueryPlanning.csproj -c Release
```

The output separates maintenance, conversion, query work and additional storage,
shows unknown costs as null, and demonstrates a full rebuild before a known
capacity failure. The work units are a structural surrogate. A smaller number
cannot establish faster execution. The example links the actual package's pure
source files; it does not use a copied planner or Unity stubs.

The four JSON lines cover the unmodified API defaults, explicit opt-in with known
facts, unknown query structure, and known incremental capacity failure. Unknown
maintenance/query/total work is `null` (the API uses `-1`). Conversion work and
additional storage are `0` when no extra view or query scratch is selected; those
zeros do not mean the full rebuild has no cost or the application uses no memory.
`additionalResidentBytes` is logical storage in bytes, not a work score or measured
VRAM. Input/output buffers, the index and concurrently retained backends need
their own budget.

The illustrative opt-in settings assume wave support and an 8 MiB **additional**
storage budget; both are printed, and neither is discovered from this computer.
`InspectedSlotCount=65536` models an all-dynamic index inspecting every capacity
slot, despite only 64 changed memberships. Candidate visits count entries before
predicate filtering, not returned matches; `ConsumerPasses=4` repeats the same
query segment on one unchanged snapshot and multiplies only query work. These
facts are invented to explain the interface and do not select a production policy.

An application must replace these illustrative facts with trustworthy facts for
its own immutable snapshot, check real capabilities and execute the returned
plan explicitly. Unknown query structure must remain unknown. Planning itself
neither mutates nor refreshes an index.

If the application executes a full rebuild while bypassing the incremental index,
its old incremental state becomes invalid. Set `IncrementalStateValid=false` for
subsequent planning until an explicit `forceRebuild: true` incremental update has
refreshed it in queue order. Full rebuild consumers use the original `inputSamples`;
incremental and compact-view consumers use `index.Samples`. Pass the capacity as
the stable-ID address bound, not `ActiveCount`, and retain buffers until every
consumer finishes. The pure example does not record those GPU operations.

[Application ownership and evidence](../../../Docs/WHOLE_TASK_DECISIONS.md).
