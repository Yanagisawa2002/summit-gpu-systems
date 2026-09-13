# SUMMIT positioning and adoption update — 2026-09-10

Baseline: `b310d827d59c1fa0e891c39b7898c9238a0cdfa1`.

SUMMIT's focus is the choice of GPU data representation across production,
maintenance, conversion, queries and actual result consumption. The package
catalog supports that complete-task decision. Historical kernel/NYCGIS results
retain their original scope; the current planner and compact-view integration
have no new performance result from this update.

## Delivered changes

- [Root README](../README.md), [portfolio entry](portfolio/README.md) and the
  [worktree roadmap](GPU_PERFORMANCE_ENGINEERING_WORKTREE_ROADMAP.md) lead with
  the maintenance-versus-consumer case and the implemented adoption path. The
  quick start begins with a pure .NET example; Unity is needed for GPU integration.
- [Whole-task decisions](WHOLE_TASK_DECISIONS.md) connects the retained cost
  observations to the existing planner. It specifies sample/CSR ownership,
  stable-ID bounds, incremental invalidation after a bypass, forced refresh,
  compact-view freshness and input/index/view/scratch lifetimes.
- The [sensor package README](../Packages/com.summit.gpu-sensor-pipeline/README.md)
  now acknowledges the shipped pure planner and optional cell-span/compact-view
  paths. It separates explicit planning from automatic runtime selection and
  preserves the historical incremental-index applicability limitations.
- [IndexQueryPlanning](../Tools/Examples/IndexQueryPlanning/README.md) links the
  actual `GpuSensorContracts`, `GpuSensorQueryBackend`, `GpuSensorCellSpanLayout`
  and `GpuSensorIndexQueryPlanner` source files. It has no package dependency,
  Unity reference, copied algorithm, hardware probe or GPU stub. Its project is
  explicitly included in Git despite the Unity-generated `*.csproj` ignore rule.

The example uses all-dynamic inspection (`InspectedSlotCount=Capacity`) rather
than treating 64 changed memberships as an inspection shortcut. Wave support
and the extra 8 MiB budget are printed assumptions for the opt-in cases. The
default case calls `Select(facts)` with the actual API defaults. Unknown query
facts use `HasExactQueryStructure=false` and unavailable candidate fields.

Work fields are structural surrogate units, never milliseconds or measured
traffic. Unavailable maintenance/query/total work becomes JSON `null`; the API
sentinel remains `-1`. Zero conversion work or extra storage means no selected
extra conversion/scratch, not free maintenance or zero application memory.
`AdditionalResidentBytes` remains an additional logical-byte estimate. The
planner's total does not account for every upload, synchronization or application
consumption cost; those still belong in a future complete-task measurement.

No runtime algorithm, heuristic, default candidate, shader or benchmark entry
was changed. All original measurements and failure records remain untouched.

The CPU workflow also explicitly builds and runs this example using its existing
.NET 10 setup. This keeps the adoption path checked when its linked public APIs
change. The workflow edit was reviewed locally; no remote CI run was triggered.

## Executed validation

The installed .NET SDK was `10.0.302`. The example restored against an empty
local feed with NuGet audit disabled, then compiled incrementally with
`--no-restore`. No external dependency was downloaded. Commands from the root:

```powershell
New-Item -ItemType Directory -Force Artifacts/Positioning20260910/empty-feed
dotnet restore Tools/Examples/IndexQueryPlanning/IndexQueryPlanning.csproj `
  --source "$PWD/Artifacts/Positioning20260910/empty-feed" `
  --ignore-failed-sources -p:NuGetAudit=false --verbosity quiet
dotnet build Tools/Examples/IndexQueryPlanning/IndexQueryPlanning.csproj `
  -c Release --no-restore --nologo --verbosity minimal
dotnet run --project Tools/Examples/IndexQueryPlanning/IndexQueryPlanning.csproj `
  -c Release --no-build --no-restore --no-launch-profile
```

Build result: **0 warnings, 0 errors**. Execution produced four JSON lines and
passed its unavailable-cost/default/fallback assertions:

| Synthetic API case | Index plan | Query form | Reason |
| --- | --- | --- | --- |
| Default | FullRebuild | CellSerial | CandidatesDisabled |
| Explicit opt-in with known structure | Incremental | CellSpansWave | IncrementalWorkReduction |
| Unknown query structure | FullRebuild | CellSerial | QueryStructureUnknown |
| Known capacity failure | FullRebuild | CellSpansWave | CapacityFallbackKnown |

These are illustrative API responses, not measured choices or recommendations.
Every line reports `Unmeasured`. The executable does not construct or submit GPU
work, mutate an index, generate a benchmark workload or collect a clock/counter.

The narrative's historical hotspot maintenance values `0.339788 / 0.325407 ms`
and query values `50.829039 / 58.234276 ms` were cross-checked against the retained
[FocusedIndexCosts table](FocusedIndexCosts.md). Their label now states N262144,
legacy CellSerial and native GPU interval means across the original processes
and blocks. No raw evidence was recomputed or reclassified.

All 48 local Markdown targets in the seven changed documents and all five compile
inputs (the example plus four linked package sources) were checked; Git whitespace
validation passed. Broader Unity/GPU or benchmark suites were not run for this
documentation and pure-example change. Existing caches were retained.

## Remaining evidence boundary

Complete current-source GPU/scene performance, Unity import/Player behavior,
device capability checks, real queue/resource lifetimes, external workload
comparisons and actual counter availability remain outside this update's
validation. The CPU example only teaches the decision interface. Existing
historical and external native results do not confirm a current SUMMIT
complete-task gain. No performance queue was restarted and no commit was pushed.
