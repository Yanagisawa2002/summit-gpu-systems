# SUMMIT GPU Autotuning

Measured profiles use schema v2 and fail closed to `GpuPrimitiveBackend.Auto` when
identity or accepted workload evidence is missing. Capability fallback remains
separate from a performance selection.

Compatibility requires exact vendor/device IDs, graphics API, GPU name, shader
level, graphics-version string, **driver version**, Unity version, compiler/toolchain
identity including build flags, shader content identity and build artifact identity.
`GpuDeviceFingerprint.Capture(osDriverVersion)` takes an independently queried OS
driver version. `Capture()` without it produces an identity that cannot authorize
calibration. In particular, R9700 Unity reports `Direct3D 12 [level 12.2]` separately
from its Windows driver version.

Use `GpuCalibrationEnvironment.Capture(compiler, shaderDigest, buildDigest)` with
values derived independently from the running build. `sourceCommit` is retained
only for provenance: a recorded commit, schema bump, copied environment or renamed
profile cannot demonstrate compatibility. Recreate cached resolvers when the device
or running artifact changes. There is no portable Unity API for the OS driver
number; callers must supply it, or use the safe fallback.

- `TryResolve(workload, device, currentEnvironment, out backend)` resolves the
  legacy Portable/WaveOps IDs without per-call allocation.
- `TryResolveCandidate(workload, device, currentEnvironment, supportedCandidateIds,
  out candidateId, out reason)` preserves opaque extensible IDs. Supply only IDs
  whose executable implementation and capabilities have been checked. Duplicate
  workload rows, unknown IDs and unaccepted rows fail closed. Candidate selection
  does not instantiate a primitive or bypass capability validation.
- `GpuPrimitiveBackendResolver(profile, device, environment)` caches independent
  identity copies; missing workload/evidence returns Auto.
- `GpuAutotuneProfileStore.TryLoad(path, device, environment, out profile)` rejects
  malformed, legacy or incompatible data. The old identity-free overloads are
  source-compatible and return false/Auto.

Schema-v1 saved profiles (including the historical exporter and integration
resource) remain historical evidence and must be recalibrated with complete current
identity. No automatic migration fills missing compatibility fields from the saved
profile. `Tools/Select-GpuAutotuningProfile.ps1` remains the legacy historical
round-split exporter; its schema-v1 output intentionally cannot authorize v2 runtime
selection. The adaptive runtime v3 matrix has a new separate discovery/freezer
protocol described in `Docs/GPU_ADAPTIVE_RUNTIME_VNEXT.md`.
