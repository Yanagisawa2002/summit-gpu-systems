# GPU-Driven Instances Core Validation — 2026-08-29

## Outcome

Commit `9d6d07c` adds the ninth standalone UPM package,
`com.summit.gpu-driven-instances`. It implements a project-neutral GPU path for
multi-view sphere visibility, distance LOD selection, view/draw-group CSR
binning, grouped instance indices, and indexed-indirect argument generation.

This is a correctness and API milestone, not a performance acceptance report.
No frame-time, FPS, CPU, GPU, upload, power, or VRAM improvement is claimed.

## Clean-room boundary

- Inputs are generic instance records, six planes per view, camera/LOD vectors,
  and draw templates.
- Outputs are counts, offsets, grouped source indices, indirect arguments, and
  two diagnostic words.
- The package contains no traffic, building, terrain, weather, orthophoto,
  BFP2, GIS, sensor, networking, scene, mesh, material, or asset contract.
- Gameplay/simulation authority remains on the CPU; recording performs no GPU
  readback.

## Correctness contract

- Up to 32 views through an explicit `uint` view mask.
- One to four positive, nondecreasing LOD distances per instance.
- Exact `(view, draw group)` counts and CSR offsets.
- Exact indexed-indirect template fields, instance counts, and start-instance
  offsets.
- Atomic group order is unspecified, but per-bin membership is checked against
  an independent CPU oracle.
- Invalid radius, LOD, group range, or view LOD scale fails closed into the
  culled bin and raises stable diagnostics.
- Zero instances, disposal, writable aliases, and dispatch boundaries are
  explicit test cases.

## Validation receipts

Environment: Unity `6000.5.2f1`, Direct3D 12, NVIDIA GeForce RTX 4090.

- Focused Null graphics: `6/16` passed; the 10 GPU integration cases skipped.
- Focused D3D12: `16/16` passed, zero skipped.
- Full repository Null graphics: `288/527` passed, `239` GPU-dependent cases
  skipped.
- Full repository D3D12: `527/527` passed, zero skipped.
- Repository boundary/layout: nine packages and 132 portable shader/C# files
  validated; project-specific integration remained isolated.
- Six PowerShell provenance/selector suites: `1,058` assertions passed.
- `git diff --check`: passed.

The focused D3D12 tests cover instance counts `1`, `255`, `256`, `257`, and
`4,097`, plus two-view visibility, view masks, LOD selection, culled membership,
indirect arguments, malformed contracts, zero work, alias rejection, and
disposal.

## Next evidence gate

The next PR should benchmark this exact core against a strong CPU
frustum/LOD/grouping baseline in one Player process. It must measure native GPU
regions and CPU submission separately, report readback and buffer bytes, freeze
visibility/view/group/update cells before formal execution, and retain negative
results. Only that PR may establish a performance claim.
