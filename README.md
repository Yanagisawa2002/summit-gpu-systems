# SUMMIT GPU Systems

**GPU data representation for real-time rendering and simulation.**

I build Unity/HLSL systems that reduce redundant data movement between producers and consumers, then measure the cost of the resulting task. The focus is not just a faster kernel: it is avoiding intermediate work that the application never needed.

[Rendering case study](Docs/NO_COPY_VISIBLE_TILES.md) · [Run or inspect](Docs/RUNNING.md) · [Evidence index](Evidence/README.md)

## Featured case: stop copying the visible index list

The NYCGIS renderer replaces a copied list of visible indices with compact tile descriptors. The draw-side shader follows those descriptors into the original index buffer: both the producer and the consumer change.

| Historical NYCGIS observation | Exact scope |
| --- | --- |
| **385.7 MB → 8.04 MB** | Approximate logical visible-output payload: copied indices versus descriptors, not total VRAM or measured DRAM traffic. |
| **52.5% lower GPU average time** | Dedicated single-camera comparison against WaveCompact on AMD Radeon AI PRO R9700 / D3D12 / Unity 6000.5.2f1. |

**These are retained historical results, not measurements of the current checkout.** The NYCGIS source is a host-dependent integration snapshot; the standalone query demo below does not reproduce this renderer. [Mechanism, source map, tradeoffs and report identity](Docs/NO_COPY_VISIBLE_TILES.md).

[![Historical NYCGIS representation change and logical-output comparison](Docs/portfolio/overview.svg)](Docs/NO_COPY_VISIBLE_TILES.md)

## Three ways to review the work

**Rendering and engine integration.** Start with [no-copy visible tiles](Docs/NO_COPY_VISIBLE_TILES.md), then inspect the compute producer, vertex consumer and buffer bindings. This is the central representation-change case.

**Complete-task performance engineering.** The [sphere reuse comparison](Docs/SPHERE_REUSE_RESULTS_2026-09-10.md) reduced repeated point preparation/index construction from 157 batches to one preparation/build per complete task. Old/new SUMMIT host-wall means were **784.17/135.98 ms**; the paired geometric old/new ratio was **5.86** (nominal 95% CI 4.30–7.98). Native Serial remained faster at **43.75 ms**. These are cross-backend task observations, not a same-device kernel victory.

**Decisions that did not earn adoption.** [Incremental index maintenance](Docs/WHOLE_TASK_DECISIONS.md) could save update work while increasing consumer cost. Its new planner is implemented but its complete-task benefit remains **Unmeasured**. The [competitive scan comparison](PublicBenchmarks/UnityGpuIntegration/RESULTS-query-boundary-2026-09-08.md) also found no demonstrated extra user-facing benefit from the batch candidate over the stronger parallel-scan baseline.

## Run an existing consumer

The [Windows query/transport demo and evidence](https://github.com/Yanagisawa2002/summit-gpu-systems/releases/tag/r9700-query-boundary-2026-09-08) runs without Unity Editor. It requires Windows x64, PowerShell 7 and D3D12 wave support; the tested device was AMD Radeon AI PRO R9700.

After extracting `query-transport-windows.zip`, run from its root:

```powershell
pwsh -File Scripts/Launch-DispatchDemo.ps1 -Arm scan -Distribution hotspot
```

This is a procedural query-to-route consumer, **not the historical no-copy city renderer**. All 45 formal processes passed correctness; stable superiority over the competitive scan baseline was not established. [Full comparison](PublicBenchmarks/UnityGpuIntegration/RESULTS-query-boundary-2026-09-08.md).

The [50-second legacy CellSerial video](https://github.com/Yanagisawa2002/summit-gpu-systems/releases/download/r9700-task-delivery-video-2026-09-08/gpu-task-delivery-english.mp4) is a separate diagnostic recording, not proof of a stable speedup or smoother frames. [Recording scope](PublicBenchmarks/UnityGpuIntegration/RESULTS-queue-latency-2026-09-08.md).

## Evidence status

| Area | What is supported | What is not established |
| --- | --- | --- |
| NYCGIS no-copy rendering | Historical AMD measurements and inspectable integration source | Independent reproduction from the root project; current-source performance certification |
| Sphere point/index reuse | Complete-output checks and scoped old/new task measurements | Beating native Serial; GPU-kernel-only or whole-engine speedup |
| CellSpans, compact view and planner | Implementation and CPU/compile contracts | Measured complete-task planner benefit or a new default winner |
| Linux / RTX 5090 molecular dynamics | Native Serial/OpenMP/CUDA numerical validation | Formal performance acceptance; SUMMIT GPU force/state consumer; Unity/HLSL NVIDIA validation |

[All report links and measurement boundaries](Evidence/README.md). The [Linux numerical report](Docs/whole-task-md-20260915/LINUX_5090_NUMERICAL_RESULTS.md) retains its **NO-GO** performance status; numerical correctness is not a speedup claim. Neither this presentation nor a merged implementation promotes runtime defaults.

## System map

The reusable packages support the case studies; they are not eight simultaneous features of every benchmark.

| Responsibility | Package |
| --- | --- |
| Scan, histogram, compaction and radix sort | [gpu-primitives](Packages/com.summit.gpu-primitives) |
| Count → scan → scatter spatial index | [gpu-direct-binning](Packages/com.summit.gpu-direct-binning) |
| Direct/radix backend selection | [gpu-adaptive-binning](Packages/com.summit.gpu-adaptive-binning) |
| Device fingerprints and calibration profiles | [gpu-autotuning](Packages/com.summit.gpu-autotuning) |
| Resident sensor data and query consumers | [gpu-sensor-pipeline](Packages/com.summit.gpu-sensor-pipeline) |
| Deadline-aware queue planning | [gpu-deadline-scheduler](Packages/com.summit.gpu-deadline-scheduler) |
| Virtual-page/physical-slot residency | [gpu-residency-manager](Packages/com.summit.gpu-residency-manager) |
| Native D3D12 timestamps and observation contracts | [gpu-timestamps](Packages/com.summit.gpu-timestamps) |

`Assets/Gpu*Benchmark` contains procedural package benchmarks. [PublicBenchmarks/UnityGpuIntegration](PublicBenchmarks/UnityGpuIntegration/README.md) is a separate standalone host. [Integrations/NYCGIS](Integrations/NYCGIS/README.md) preserves the city-rendering integration outside root `Assets`, because its host types and datasets are not included.

## My contribution

I implemented the reusable GPU packages, native D3D12 timestamp integration, deterministic A/B harnesses and benchmark automation, together with the isolated NYCGIS integration snapshot. [Migration/provenance manifest](MIGRATION_MANIFEST.md). External benchmark adapters retain their [upstream source contracts and notices](PublicBenchmarks/External/README.md); adapted workloads are not presented as upstream native benchmark results.

## Quick start

For CPU-only checks, install .NET 10, PowerShell 7, Python and Git, then run from the repository root:

```powershell
pwsh -File Tools/Run-FunctionalChecks.ps1
dotnet run --project Tools/Examples/IndexQueryPlanning/IndexQueryPlanning.csproj -c Release
```

These check functionality and demonstrate synthetic planning; they do not execute Unity/GPU benchmarks or establish a faster algorithm. [Requirements, offline evidence checks and separately selected GPU runs](Docs/RUNNING.md).

## Consuming a package from another Unity project

Use a local `file:` dependency or an existing commit-pinned Git UPM dependency. Add internal `com.summit.*` dependencies explicitly as needed. [Manifest example and prerequisites](Docs/RUNNING.md#consuming-packages).

Public source visibility is **not an open-source license**. Read the [limited benchmark reproduction permission](LICENSE.md#limited-benchmark-reproduction-permission) and package/third-party notices before reuse; this documentation does not change those rights.

## Relationship to HLSL Kernel Pipeline

**SUMMIT** studies data representation and the producer/index/consumer path in a Unity runtime. **[HLSL Kernel Pipeline](https://github.com/Yanagisawa2002/hlsl-kernel-pipeline)** studies engine-neutral kernel execution, correctness, autotuning and device-specific profiles. Their timing boundaries are different.

The [optional verified scan bridge](Integrations/HlslKernelPipeline/README.md) binds an exact HLSL artifact to Unity buffers and dispatch recording, including explicit Raw/Structured conversion. A kernel-level result does not certify SUMMIT scene or engine-frame performance.

## Repository policy

Portable packages and standalone harnesses must not depend on NYCGIS, BFP2, FishNet or external city assets. Host-specific adapters stay under `Integrations`. Large generated Players, captures and datasets stay outside normal source paths; explicitly retained evidence packages are documented separately.

Performance reports must identify workloads, correctness checks, measured source/build, hardware/driver, timing scope and statistical units. Preserve negative results and unavailable metrics. See the [evidence index](Evidence/README.md) for the historical investigations, including the invalidated cinematic pilot and unresolved full-engine timing coverage.
