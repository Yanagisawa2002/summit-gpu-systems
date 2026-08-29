# Retained evidence index

This directory is the compact evidence entry point. Detailed experiment designs and result reports live in `../Docs`; raw generated sessions remain outside Git.

## Portable systems

- GPU primitives: `../Docs/GPU_PRIMITIVES_AMD_R9700_FORMAL_RESULTS_2026-07-30.md`, `../Docs/GPU_PRIMITIVES_NVIDIA_RTX4090_FORMAL_RESULTS_2026-08-29.md`
- Direct spatial binning: `../Docs/GPU_DIRECT_BINNING_AMD_R9700_RESULTS.md`
- Adaptive spatial backend: `../Docs/GPU_ADAPTIVE_BINNING_AMD_R9700_FORMAL_2026-07-31.md`
- Device-keyed autotuning: `../Docs/GPU_CROSS_VENDOR_AUTOTUNING_AMD_R9700_REPORT.md`, `../Docs/GPU_CROSS_VENDOR_AUTOTUNING_NVIDIA_RTX4090_REPORT_2026-08-29.md`
- GPU-resident sensor pipeline: `../Docs/GPU_DYNAMIC_SENSOR_PIPELINE_AMD_R9700_FORMAL_2026-07-31.md`
- Packed SoA and fusion: `../Docs/GPU_SENSOR_DATA_PACKING_FUSION_V2_AMD_R9700_REPORT.md`
- Shared multi-sensor index: `../Docs/GPU_MULTI_SENSOR_SHARED_INDEX_AMD_R9700_REPORT.md`
- Deadline-aware scheduling: `../Docs/GPU_DEADLINE_SCHEDULER_AMD_R9700_REPORT.md`
- Residency manager: `../Docs/GPU_RESIDENCY_MANAGER_AMD_R9700_REPORT.md`
- Native D3D12 timestamps: `../Docs/GPU_NATIVE_DX12_TIMESTAMPS_SMOKE_EVIDENCE_2026-07-30.md`

## NYCGIS case study

- Cluster culling: `../Docs/GPU_CLUSTER_CULLING_RESULTS_2026-07-27.md`
- Stress showcase protocol: `../Docs/GPU_STRESS_SHOWCASE.md`
- Native-scoped cinematic benchmark: `../Docs/GPU_CINEMATIC_BENCHMARK.md`

## Claim boundary

Retained hardware results cover only the device named in each report. GPU primitives and device-keyed autotuning now include AMD Radeon AI PRO R9700 and NVIDIA GeForce RTX 4090 Direct3D 12 evidence; other systems remain AMD-only unless stated otherwise. Percentages describe the exact workloads, scopes, sample counts, and baselines in their reports; they are not generalized product-wide speedups.
