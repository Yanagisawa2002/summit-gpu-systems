# Evidence index

[Project overview](../README.md) · [Rendering case](../Docs/NO_COPY_VISIBLE_TILES.md) · [Run or inspect](../Docs/RUNNING.md)

This is a navigation layer, not a replacement for the original reports. Read each report's source/build identity, output contract, timing boundary and failed gates before reusing a number. Historical reports, figure inputs and raw evidence are not rewritten by this presentation update.

## Start with the engineering question

| Question | Report | Supported result and limitation |
| --- | --- | --- |
| Can the renderer avoid copying visible indices? | [No-copy case and source map](../Docs/NO_COPY_VISIBLE_TILES.md); [historical results index](../Docs/GPU_PERFORMANCE_ENGINEERING_PORTFOLIO_INDEX_2026-07-31.md) | Historical NYCGIS logical output 385.7 MB → 8.04 MB; dedicated single-camera GPU average down 52.5% versus WaveCompact. Host-dependent snapshot, not current-checkout reproduction. |
| Can one task reuse its points and spatial index? | [Sphere reuse results](../Docs/SPHERE_REUSE_RESULTS_2026-09-10.md); [machine-readable evidence](../Docs/SPHERE_REUSE_EVIDENCE_2026-09-10.json); [frozen protocol](../Docs/SPHERE_REUSE_PROTOCOL_2026-09-10.md) | Old/new 784.17/135.98 ms host-wall means; paired geometric ratio 5.86. Native Serial remained faster at 43.75 ms. Complete GPU CSR output was checked. |
| Does cheaper maintenance produce a cheaper task? | [Complete-task decisions](../Docs/WHOLE_TASK_DECISIONS.md); [focused index costs](../Docs/FocusedIndexCosts.md); [capacity replay](../Docs/CausalIndexCosts.md) | Historical maintenance savings could increase query cost. The current structural planner's complete-task benefit remains Unmeasured. |
| Does batch querying beat a competitive simple baseline? | [Query boundary and transport](../PublicBenchmarks/UnityGpuIntegration/RESULTS-query-boundary-2026-09-08.md) | All 45 processes passed correctness. No demonstrated extra user-facing benefit over index-free parallel scan; all formal stability decisions inconclusive. |
| Are native CPU/CUDA consumers numerically equivalent? | [Linux/RTX 5090 numerical results](../Docs/whole-task-md-20260915/LINUX_5090_NUMERICAL_RESULTS.md); [portable evidence package](../Docs/evidence/whole-task-md-linux-20260915/README.md) | 112 numerical repetitions: 98 full-state and 14 membership-only. Formal performance NO-GO; native CUDA does not validate Unity/HLSL or the unfinished SUMMIT GPU consumer. |

## Earlier whole-task and integration investigations

| Report | Why retain it |
| --- | --- |
| [Actual external baseline](../Docs/EXTERNAL_ACTUAL_RESULTS_2026-09-10.md) | Original Cabana/ArborX comparisons. All five SUMMIT upload/rebuild/readback/consume cases cost more than native Serial; later reuse work does not erase that baseline. |
| [Implementation integration](../Docs/RemediationIntegration20260908.md) | CPU/compile-only acceptance, observation contracts, external adapters and historical-report identity. It is not a performance run. |
| [Unified integration matrix](../PublicBenchmarks/UnityGpuIntegration/RESULTS-2026-09-08.md) | Narrow query/explicit-scene measurements with unresolved engine-cadence and full-engine GPU coverage. |
| [Matching microbenchmarks](../Docs/UnifiedMicrobenchmarkResults.md) | Keeps failed stability gates and the absence of a confirmed complete-GPU incremental-index benefit. |
| [Focused collection diagnosis](../PublicBenchmarks/UnityGpuIntegration/RESULTS-focused-costs-2026-09-08.md) | Collection repair, query/index tradeoffs and unavailable allocation counters. |
| [Probe-control diagnosis](../PublicBenchmarks/UnityGpuIntegration/RESULTS-causal-costs-2026-09-08.md) | Long Present waits and missing engine GPU observations also occurred without native probes; no new stable-frame claim. |
| [Legacy queue/latency demonstration](../PublicBenchmarks/UnityGpuIntegration/RESULTS-queue-latency-2026-09-08.md) | Verified task completion and a scoped video, not superiority over the later competitive scan baseline. |

## Historical portable-system measurements

These reports retain their original AMD Radeon AI PRO R9700 / D3D12 workload and timing boundaries. The list is not a claim that all packages ran together or that their improvements can be added.

| System | Original report |
| --- | --- |
| GPU primitives | [Formal results](../Docs/GPU_PRIMITIVES_AMD_R9700_FORMAL_RESULTS_2026-07-30.md) |
| Direct spatial binning | [Results and failed performance gates](../Docs/GPU_DIRECT_BINNING_AMD_R9700_RESULTS.md) |
| Adaptive direct/radix backend | [Forced backends and offline policy replay](../Docs/GPU_ADAPTIVE_BINNING_AMD_R9700_FORMAL_2026-07-31.md) |
| Device-keyed autotuning | [AMD evaluation](../Docs/GPU_CROSS_VENDOR_AUTOTUNING_AMD_R9700_REPORT.md) |
| GPU-resident sensor pipeline | [Formal results](../Docs/GPU_DYNAMIC_SENSOR_PIPELINE_AMD_R9700_FORMAL_2026-07-31.md) |
| Packed SoA and fusion | [Workload-specific results](../Docs/GPU_SENSOR_DATA_PACKING_FUSION_V2_AMD_R9700_REPORT.md) |
| Shared multi-sensor index | [Reuse results](../Docs/GPU_MULTI_SENSOR_SHARED_INDEX_AMD_R9700_REPORT.md) |
| Deadline-aware scheduling | [Latency and admission results](../Docs/GPU_DEADLINE_SCHEDULER_AMD_R9700_REPORT.md) |
| Residency manager | [Residency results](../Docs/GPU_RESIDENCY_MANAGER_AMD_R9700_REPORT.md) |
| Native D3D12 timestamps | [Smoke evidence](../Docs/GPU_NATIVE_DX12_TIMESTAMPS_SMOKE_EVIDENCE_2026-07-30.md) |

## Historical city-rendering archive

[Cluster culling](../Docs/GPU_CLUSTER_CULLING_RESULTS_2026-07-27.md) · [Stress showcase protocol](../Docs/GPU_STRESS_SHOWCASE.md) · [Cinematic benchmark](../Docs/GPU_CINEMATIC_BENCHMARK.md) · [Portfolio result index](../Docs/GPU_PERFORMANCE_ENGINEERING_PORTFOLIO_INDEX_2026-07-31.md)

The portfolio index retains the invalidated cinematic pilot and pending clean rerun. Do not promote it into a retained cinematic speedup. The separate cluster-local uint16 experiment has a memory-format result, not a stable frame-time win.

## Reading evidence without changing its meaning

**Source identity.** A report commit, measured implementation commit, build identity and final delivery commit are different objects. The [no-copy report pin](../Docs/RemediationIntegration20260908.md#historical-evidence-identity) does not recreate missing original binary/source attestation.

**Timing boundary.** Native GPU scopes, complete-task host-wall time, engine cadence and physical display timing are not interchangeable. The sphere comparison is cross-backend; the query-boundary table contains host-observed completion rather than hardware GPU timestamps.

**Data boundary.** Logical payload bytes are not physical memory counters. Count/hash digests are not full hit-list output. Full CSR acceptance and force/velocity/position acceptance also establish different contracts.

**Hardware boundary.** Historical Unity performance reports are AMD-specific. Later native RTX 5090 numerical correctness is real, but is neither NVIDIA Unity/HLSL performance validation nor a cross-vendor speedup result.

**Access boundary.** Many generated sessions live outside normal Git source. Follow the report's release/archive references; a local artifact path or hash alone does not mean that file is publicly downloadable. The numerical archive and query-demo release are explicitly linked above.
