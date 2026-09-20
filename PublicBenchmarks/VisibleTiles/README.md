# Standalone visible tiles: original compute, procedural draw consumer

An asset-independent Unity host for the actual scalar, compact, wave-copy and
32/64-triangle visible-tile producers from this repository. It stages the two
original compute files and their shared multi-camera cone helper byte-for-byte;
there is no imitation producer or CPU rendering fallback. A small unlit draw
shader consumes their original 32-bit index/descriptor contract.

**Status: implementation and build/run entry points provided; Unity import,
managed Unity API compilation and actual device execution are not established
by CPU or DXC checks. No prebuilt Windows Player or new performance result is
included.** Read the actual PR checks for hosted results. The acceptance command
below must produce a complete device receipt before calling the sample validated.

This is the next step after the [multi-camera cone fix](../../Docs/MULTICAMERA_CONE_FIX.md).
It reproduces the representation mechanism, NOT the historical NYCGIS workload,
385.7/8.04 MB sizes or 52.5% result. Full NYCGIS streaming, materials and datasets
remain outside this sample. It is not a ninth package or a full scene benchmark.

## One-command device acceptance

From this repository root, on Windows with Python 3.10+, Git, PowerShell 7 and
an already installed/licensed Unity **6000.5.2f1** Editor with Windows x64 build
support and a D3D12 wave-capable device:

```powershell
pwsh -File PublicBenchmarks/VisibleTiles/Run.ps1 `
  -Unity 'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe'
```

The runner creates a NEW workspace under the system temporary directory, stages
the project, builds a non-Development Windows Player, then executes validation.
Use `-Workspace 'D:\VisibleTiles\run-001'` for another new destination. It takes
the existing shared GPU mutex without waiting; a busy or abandoned mutex stops
before work. That cooperative lock does not prove the entire device is idle.
Only an owned child can be stopped at its timeout; other projects and editors
are untouched. All output and failures are retained. There are no background
retries, global cache resets, new installs, or changes to hardware settings.

Successful execution requires **12 cases x 2 binding paths x 5 producer arms =
120 full-output and two-view image checks**. Unsupported kernels, a non-D3D12
backend, source drift, incomplete checks, mismatched pixels or Unity errors fail
rather than silently falling back. The runner prints the evidence destination.
This command is a correctness run, not a speed comparison.

## Inspect in Unity instead

```powershell
python PublicBenchmarks/VisibleTiles/prepare.py --output D:/VisibleTiles/inspect-001
```

Open that generated project using the stated Editor and D3D12. Choose **Tools >
Visible Tiles > Create validation scene**, then Play. The scene builder checks
all nine staged source/license hashes before creating the scene. The view shows
ScalarAoS, WaveCompact and Tile32 outputs from both viewpoints after validation;
ScalarCompact and Tile64 are also tested. Evidence goes to a unique directory
under `Application.persistentDataPath/VisibleTiles` and is printed in the log.
To modify the implementation, edit the repository sources and stage a new project.

Once prepared, the generated project needs no NYCGIS host or repository-relative
package. Its only package dependencies are Unity's built-in IMGUI, image conversion
and JSON modules. The original source-to-staged-path SHA256 manifest is embedded
in Resources; the Player receipt also records Unity, build GUID, backend and device.

## What is actually checked

Each original producer performs clear, optional compact-record preparation and
culling. Its output feeds a real indirect draw **before** audit readbacks. Both
direct ComputeShader calls and CommandBuffer-recorded bindings are exercised on
the same graphics queue. All buffers remain alive through blocking output and
image readback; this sample has no cross-queue or asynchronous lifetime contract.

The fixture varies cluster triangle counts across 1,31,32,33,63,64,65, uses
non-identity source indices and includes a 129-cluster dispatch tail. It covers
front/back views, union and reversed union, all-rejected, an accepting eighth
view, sparse visibility, a zero-distance view, empty input, zero-count legacy
camera fallback, disabled cones and winding flips. Frustum, distance and
screen-size culling are disabled to isolate the cone/index contract; their
full geometric correctness is not certified here. The draw is intentionally
two-sided/unlit, not a reproduction of NYCGIS lighting or physical backfaces.

An independent geometric +/-Z normal oracle decides which fixture clusters must
survive. It builds an ordinary CPU mesh for the reference images, not a scalar
GPU result. Complete oriented triangle multisets preserve winding and duplicate
multiplicity. Counter and draw-argument checks include expected padded vertices,
zero overflow and exact kept/culled counts. Image comparison allows at most one
8-bit unit per channel at each pixel and requires foreground for nonempty cases.
No sampled hashes or mutual scalar/wave agreement substitute for these checks.

Worst-case capacity is derived from ALL input clusters before commands are
submitted, including per-cluster tile rounding. Undersized output is rejected by
the host. This sample does not exercise or certify the original producer's
post-reservation overflow path. Tile32Index16 is not part of this sample's
32-bit consumer; its original compute entry point remains compile-checked only.

Each arm retains complete output/args/stats/expected binary files and both actual
and CPU-reference PNGs for each view. `result.json` records all accepted checks,
logical output bytes (not physical memory traffic), source manifest and build/
device identity. `player-sha256.txt`, build log and Player log accompany the run.
There is intentionally no GPU-time, FPS, speedup or historical-percentage claim.
SourceDirty in the manifest remains visible; a dirty staging is development
identity, never a qualified frozen performance source.

## CPU and source checks

Run these from the repository root:

```powershell
dotnet run --project PublicBenchmarks/VisibleTiles/Tests/VisibleTiles.Contracts.csproj -c Release
python -B -m unittest discover -s PublicBenchmarks/VisibleTiles/Tests -p 'test_*.py' -v
```

The .NET 10 project compiles the actual fixture/oracle and tests roundtrips,
permuted cluster order, tails, winding, malformed descriptors and capacity
rejection. Roslyn from the installed SDK syntax-checks the Unity host files;
that is explicitly not compilation against Unity assemblies. Seven Python
staging tests use synthetic file bytes and check exact copying, tampering,
missing input, path boundaries and refusal to overwrite. CI separately stages
the actual repository sources and compiles the draw functions with pinned DXC.
Neither path runs a GPU or grants a Unity/device PASS.

## Next acceptance boundary

A successful real Windows build and complete `result.json` are still required.
Only then freeze a separate performance protocol that includes draw consumption,
fixed cameras, adequate capacity, matched outputs and original-source/build
identity. Do not turn this synchronous correctness runner into a benchmark by
reporting its wall clock as GPU time. Historical reports remain unchanged.

The root [license](../../LICENSE.md) and its limited benchmark reproduction
permission apply. The unchanged license is copied into the generated project.
