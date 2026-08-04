# GPU Cluster Screen-Space Culling Benchmark

## Scope

This is the first low-risk phase of the BFP2 GPU optimization work. It replaces the
resolution-independent angular threshold with a projected pixel-radius threshold while keeping
the existing compute-culling and indirect-draw architecture.

The implementation is opt-in. Production scene defaults are unchanged until the benchmark and
visual review pass on both NVIDIA and AMD hardware.

This phase does not generate simplified geometry or claim a complete cluster LOD system. Its
purpose is to establish the screen-space error metric, the cross-vendor measurement path, and a
safe workload-reduction baseline before offline LOD generation is added.

## Runtime behavior

- A cluster is removed only when its projected bounding-sphere radius is below the configured
  threshold in every active BFP2 camera.
- Perspective cameras use their current projection matrix and scaled output height.
- Orthographic cameras use their pixel-to-world scale.
- Multi-camera union rendering retains a cluster if any active camera can resolve it.
- Passing no override preserves the serialized scene configuration.
- Passing `0` disables screen-size culling for the control run.

## Command-line A/B protocol

Use the same Development Player, scene, data root, camera count, resolution, weather profile, and
driver state for every run. Disable VSync and close unrelated GPU workloads.

Control:

```text
SUMMIT.exe -nycgis-weather-perf -nycgis-weather-cameras 1 -nycgis-weather-warmup-seconds 60 -nycgis-weather-sample-frames 900 -nycgis-bfp2-screen-cull-pixels 0 -nycgis-weather-report Reports/gpu-cull-off.txt
```

Conservative pilot:

```text
SUMMIT.exe -nycgis-weather-perf -nycgis-weather-cameras 1 -nycgis-weather-warmup-seconds 60 -nycgis-weather-sample-frames 900 -nycgis-bfp2-screen-cull-pixels 1 -nycgis-weather-report Reports/gpu-cull-1px.txt
```

Aggressive diagnostic:

```text
SUMMIT.exe -nycgis-weather-perf -nycgis-weather-cameras 1 -nycgis-weather-warmup-seconds 60 -nycgis-weather-sample-frames 900 -nycgis-bfp2-screen-cull-pixels 2 -nycgis-weather-report Reports/gpu-cull-2px.txt
```

Repeat the control and selected pilot at least three times for each of the `1`, `4`, and `6`
camera configurations. Alternate control and pilot runs to reduce thermal and background-load
bias.

## Decision metrics

Use these primary comparisons:

- GPU frame time average, P95, and P99.
- Frame time average, P95, and P99.
- Stable FPS or deadline-attainment change.
- BFP2 resident GPU bytes and total process memory.
- Draw calls as a guardrail; this change is expected to reduce index and vertex work, not draw
  submission count.

The report records the GPU name, vendor, graphics API/version string, memory size, culling mode,
and pixel threshold. Treat vendor profiler captures as authoritative when Unity reports GPU frame
time as unavailable.

## Acceptance gate

Promote a threshold to the production baseline only when all of the following hold:

1. No missing near-field geometry, roof holes, silhouette popping, or camera-to-camera mismatch is
   found on the deterministic review path.
2. The selected threshold improves GPU P95 on both NVIDIA and AMD, or is neutral on one vendor and
   materially positive on the other.
3. Frame-time P99 does not regress by more than 2%.
4. The result is repeatable across at least three runs and is not explained by different residency,
   camera scheduling, resolution, or thermal state.

If the total GPU improvement is below 3%, retain the implementation as an available culling mode
but do not present it as a performance win. Profile the orthophoto, projected-shadow, and pixel
bandwidth paths before raising the threshold.
