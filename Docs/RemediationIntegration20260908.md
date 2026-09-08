# September 8 implementation integration — Unmeasured

Baseline: `63c5bfd6fdb7c44516cbc8c4a70cb95c28013cf1`. The baseline already contains
the earlier vNext primitive, query, adaptive, scheduler and residency work; those
branches were inspected rather than reimplemented. New code remains opt-in.
No performance, algorithm timing comparison, benchmark, calibration/autotuning,
profiling, real counter collection, GPU dispatch, Player or Unity scene was run.
This is a code/functional integration, not new performance evidence.

| Gap | Delivered behavior | Validation boundary |
| --- | --- | --- |
| Full CSR scans and reserved-slot consumer cost | `CellSpans`/`CellSpansWave` prune intersecting x-ranges per y/z row, split candidate work into bounded chunks, and consume exact membership. | CPU coverage/digest/recording tests and shader compilation; GPU behavior and speed unmeasured. |
| Incremental tombstones and expensive fallback | Optional maintained live counts, compact index view and a conservative maintenance+conversion+query planner; full rebuild is chosen before a known capacity failure. | Integer structural work estimates are not elapsed-time predictions. See [algorithm contract](SensorCellSpansAndCompactView.md). |
| Missing/ambiguous whole-system timing | `Observation` preserves source/build/device-driver, queue, clock, source frame and observation frame. Explicit GPU, engine GPU, CPU, cadence, queue completion and OS presentation remain distinct. | Injected replay and CSV tests; missing values have explicit states/reasons. No inferred full-engine or OS data. |
| Unreliable zero allocation counters | Injected positive control with current-thread scope, unsupported/reset/thread-migration rejection, and `-1` legacy unavailable values. | Virtual counters only; no actual counter probe executed. Existing IntegrationPlayer now uses the common abstraction. |
| Real source/result consumption | Actual Boids/targets feed the sphere adapter; complete CSR IDs resolve back to source-frame Entity handles. Cancellation/world replacement invalidates results and retains submitted buffers until readbacks finish. | Managed API compilation and pure lifecycle tests; pinned full scene import/execution unperformed. |
| External benchmark semantics | Frozen Cabana LinkedCell and ArborX bvh_driver sources, licenses, input/result contracts, native snapshot hook, complete-CSR adapters and build-only entry. | Source/hash checks, CPU boundary cases, C++ hook and HLSL compilation. Upstream executable linking/execution is not claimed. |
| HLSL profile-only integration | Verified three-file source artifact, strict SDK profile bridge, real Raw scan recorder, explicit two-copy Structured bridge and `GpuPrimitives` injection. | Real profile-package/Unity API compilation, source/hash and record doubles, DXC; Unity SM6.6 import and GPU execution unvalidated. |
| Default validation mixed with performance | Explicit CPU functional projects and source validation in PR CI; compile tools never start Unity. Existing runtime and benchmark commands remain available. | No chat authorization token, hardcoded product shutdown or changed default optimization. |

## Observation and export contract

`Packages/com.summit.gpu-timestamps/Runtime/Observation*.cs` defines a versioned,
lossless CSV interchange for replay and correlation with exported ETW, PresentMon
or PIX events. It is **not** a native `.etl`/`.pix3` parser, and starts no capture.
Imported rows must state their original collector, exact clock domain/frequency,
process/build/source identity and event ID. Unavailable observations carry null
values and reasons. A zero allocation delta can be available only after the positive
control; missing/zero engine timing values remain unavailable.

`IntegrationObservations` exports existing IntegrationPlayer rows at checkpoint
time. FTM frame starts are associated with the first subsequent Update only when
QPC domains/frequencies match and a complete preceding interval exists. Delayed
observation frames are retained separately. Native source frame, queue and device
generation survive export. `QueueObservations` exports actual submitted and
verified job/route ticks with their source and observed Unity frames, labeled as
host-observed completion, not hardware GPU or physical display time. OS presentation
remains unavailable without an external source. The old no-probe Present-wait
diagnosis remains unchanged; this code does not establish the cause of long frames.

The CSV reader strictly checks schema, shape, quoting, enums, finite values and
units; the ledger rejects duplicate event identities. Each source field can be
explicitly `unknown`, allowing unavailable historical information to survive.
Unknown identity never grants eligibility to an HLSL profile selection.

## Cost and lifecycle boundaries

Sphere adaptation retains original float coordinates and IDs through SUMMIT's
integer broad phase. Conversion, index maintenance/build, traversal, exact sphere
filter, count scan, full result materialization, synchronization and real consumption
all belong to future complete-cost measurements. Neither a digest nor count-only
result is comparable to full CSR. Native sphere inputs come from upstream's
generator through the compiled snapshot hook; the Boids input is actual scene
entity state, separately labeled as an external application observation consumer.

The HLSL Structured bridge adds two explicit copies and `8*N` raw-buffer bytes to
the scan scratch. `GpuPrimitives.ExternalScanScratchBytes` exposes that caller-owned
storage; `ScratchBytes` continues to describe storage owned by the primitives
instance. Explicit Portable/WaveOps operations and default constructor behavior
are preserved. An unavailable external shader returns a refusal reason so the
host retains local primitives. No compaction/sort identity is fabricated.

## Reproducible non-performance checks

```powershell
./Tools/Run-FunctionalChecks.ps1
./Tools/Compile-ManagedSources.ps1 -UnityEditorPath '<Unity>/Editor/Unity.exe'
# Optional compile against actual installed Entities + the upstream Boid types:
./Tools/Compile-ManagedSources.ps1 -UnityEditorPath '<Unity>/Editor/Unity.exe' `
  -EntitiesAssemblyDirectory '<read-only host>/Library/ScriptAssemblies' `
  -ReferenceDirectory 'Artifacts/external-references/entities-boids' `
  -HlslProfilePackageDirectory '<HLSL repo>/unity/com.edwinliu.hlslperf-profile'
```

These compile source against real Unity DLLs without launching Unity or invoking
native APIs. The external reference environment used Unity 6000.5.2f1 and an
existing Entities installation; the pinned official sample requires 6000.2.10f1.
Compilation against those available APIs does not establish exact pinned-project
import. The standalone C++ snapshot hook compiled with MSVC 14.51; external sphere
and conversion shaders compiled with DXC. The fixed HLSL asset compiled at cs_6_6.
Algorithm-specific CPU, recording-double and shader compile commands are in the
[query document](SensorCellSpansAndCompactView.md).

The integrated validation run passed:

- 132 deterministic observation/allocation/external/HLSL assertions, six query
  correctness suites, and the real query/index command recorders against inert
  resource doubles. No Unity/native library is loaded by these executable tests.
- Four offline source/staging tests, including complete tracked-project copying,
  cache exclusion, local-content attestation, drift and nested-path rejection.
- 79 C# source files compiled against real Unity/Entities and the HLSL profile SDK,
  including the Editor asset factory. The existing unused `QueryBoundaryPlayer.ready`
  warning remains; compilation has no errors.
- 36 query/index entry points compiled with FXC/DXC, plus seven external sphere,
  Raw/Structured conversion and fixed HLSL scan entry points compiled with DXC.
  The snapshot hook compiled to an object file only.
- Repository layout and whitespace checks. The default CI explicitly names the
  CPU projects above and does not discover benchmark or Player test suites.

The query dependency `19639cd1cd5060dd9d78985cd7572fef973521cb` was integrated as
`2615bb2` after the observation/consumer integration `11887bc`. The accompanying
HLSL artifact remains pinned to source
`274e5077455e6c08959a274a84379dfc4e1345dd`, with the independent asset digest and
ABI recorded in [the consumer contract](../Integrations/HlslKernelPipeline/README.md).
The HLSL repository's final integration is
`de978aff15d5cd6e405a3d3bcfef3fbf0d7b46e0`; it retains those exact artifact bytes.

`PublicBenchmarks/External/sources.lock.json` contains full source identities and
licenses. Large official sample assets and complete Kokkos/Boost builds were not
downloaded; upstream native executable builds and all performance execution remain
unperformed. Source preparation defaults to prepare-only. Explicit runtime commands
and genuine capability/data-validation gates remain functional.

## Historical evidence identity

The README's 52.5% figure remains the dedicated NYCGIS
`codex/gpu-no-copy-visible-tiles` result identified in the historical portfolio
index, retained at repository commit `63c5bfd6fdb7c44516cbc8c4a70cb95c28013cf1`.
That index's normalized-LF SHA256 is
`05f7a33f0f957dbbb9ca650cb6e15110625224439ecffa53a452f5d08be03109`.
This is a pin of the historical report, not a claim that the new integration commit
was measured or that a missing original binary/source attestation can be recreated.
No old performance samples or failed gates were changed or relabeled.
