# Multi-camera normal-cone correctness

Runtime baseline: `58ef769503d7bfe843d562155c9022fa31ef46ee`.
Tracks [issue #16](https://github.com/Yanagisawa2002/summit-gpu-systems/issues/16).

The old scalar AoS, scalar compact and wave predicates used the nearest camera
for normal-cone rejection. That is not valid for a visibility list shared by
several cameras: the closest view can reject geometry another view must retain.

All three sites now call the same `Bfp2NormalConeRejectsAllCameras` HLSL helper.
It rejects only when every prepared camera rejects. A view at distance <=0.001
retains the cluster conservatively. Zero prepared frames retain the existing
single-camera fallback; the eight-camera storage bound is unchanged. Because
the fix is in the shaders, both direct and CommandBuffer binding paths consume
it without a C# toggle or a change to the default (cone culling remains opt-in).
Separate frustum/distance/screen/cone union tests can conservatively over-retain;
this patch does not claim a minimal per-camera visibility list.

## Checks

`Tools/Rendering/test_cone.cpp` includes the production HLSL helper through a
minimal C++ vector shim. It tests the issue counterexample, camera permutation,
all-rejected, near/zero-distance, equality, undefined direction, eighth view,
legacy fallback and randomized 1-8 view sets against a separate double-precision
oracle. Local GCC execution passed 21,256 assertions. This is CPU predicate
execution, not HLSL bytecode execution or a captured Unity scene.

```sh
g++ -std=c++17 -O2 -Wall -Wextra -Werror Tools/Rendering/test_cone.cpp -o /tmp/test-cone
/tmp/test-cone
python Tools/Rendering/check_cull_shaders.py --dxc /path/to/dxc
```

The rendering workflow compiles all nine original scalar/compact/wave/tile
compute entry points with an official checksum-pinned DXC. Wiring checks prevent
an old nearest-view cone block surviving in a variant. Hosted results must be
read from the actual PR checks, not inferred from the local predicate pass.

Unity import, direct/CommandBuffer GPU dispatch and visual acceptance require a
supported Unity/D3D12 runtime. This change makes no performance claim, changes
no historical measurement, and does not certify every culling predicate or the
host-dependent NYCGIS integration. A separate procedural visible-tile sample
will provide an asset-independent device acceptance route.
