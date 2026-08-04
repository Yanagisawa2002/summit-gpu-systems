# SUMMIT GPU Residency Manager

This package implements an application-level virtual-page to physical-slot GPU
cache for large maps and point clouds. It keeps a fixed GPU allocation, updates
a virtual page table with compact deltas, scatters uploaded page payloads into
physical slots, and supports deterministic digest queries for validation.

`GpuPageResidencyPlanner` is portable C# policy code. The benchmark includes a
full-visible-set rebuild policy and a persistent LRU policy.

This is not a Direct3D 12 reserved-resource or sparse-binding implementation.
The physical cache is a normal `GraphicsBuffer`; the benchmark reports
`sparseResourceClaim=false`.
