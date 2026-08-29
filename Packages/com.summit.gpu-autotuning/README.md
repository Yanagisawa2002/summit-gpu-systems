# SUMMIT GPU Autotuning

This package turns measured GPU timing distributions into device-keyed runtime
profiles. It deliberately separates capability fallback from performance
selection: unsupported or stale profiles fall back to `GpuPrimitiveBackend.Auto`,
while matching profiles may select a measured portable or WaveOps backend per
workload.

Profiles are keyed by vendor ID, device ID, graphics API, GPU name, and shader
level. Formal measurements now cover AMD Radeon AI PRO R9700 and NVIDIA GeForce
RTX 4090. The NVIDIA profile deliberately mixes backends: WaveOps for exclusive
scan and stable compaction, and Portable for radix sort because the measured
WaveOps delta did not clear the frozen upgrade gate.
