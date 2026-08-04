# SUMMIT GPU Autotuning

This package turns measured GPU timing distributions into device-keyed runtime
profiles. It deliberately separates capability fallback from performance
selection: unsupported or stale profiles fall back to `GpuPrimitiveBackend.Auto`,
while matching profiles may select a measured portable or WaveOps backend per
workload.

Profiles are keyed by vendor ID, device ID, graphics API, GPU name, and shader
level. NVIDIA support is implemented by the same generic schema and benchmark
runner, but no NVIDIA result is claimed until that hardware is measured.
