# Contributing

This repository is currently a private portfolio and engineering-evidence
project, not an open-source contribution target. Do not copy or submit code
from an employer, client, restricted dataset, proprietary Unity project, or
third-party asset without documented authorization.

## Change boundaries

- Stable package runtime code must remain independent of NYCGIS, BFP2,
  FishNet, city assets, host scenes, and application-specific types.
- Diagnostics and Sensor/Residency/Scheduler Labs must not become default
  dependencies of `com.yanagisawa.gpu-systems-toolkit`.
- Preserve `com.summit.*` package IDs and `Summit.*` namespaces until a major
  migration includes adapters, obsolete shims, tests, and an upgrade guide.
- Output layout is caller-owned semantics. Policy may select upload and culling
  only within its measured and validated contract.
- Never replace unavailable measurements with zero or a different timing scope.

## Required checks

Run the static boundary and script contracts:

```powershell
.\Tools\Test-RepositoryLayout.ps1
Get-ChildItem .\Tools\Tests\Test-*.ps1 |
  ForEach-Object { pwsh -NoProfile -File $_.FullName }
```

Changes to C#, compute shaders, package metadata, or public contracts also
require Unity `6000.5.2f1` EditMode validation on a real D3D12 device:

```powershell
.\Tools\Run-UnityEditModeTests.ps1 -UseGraphics -ForceDirect3D12
```

The Null Device run is useful for compilation and CPU-only contracts but may
skip GPU integration tests and must not be reported as the full suite.

## Performance changes

A performance PR must state before running:

1. baseline and candidate commits;
2. device, driver, Unity version, graphics API, and workload matrix;
3. exact measured scope and correctness oracle;
4. sample count, warm-up, counterbalanced order, and tail guardrails;
5. bounded attempt count and stop condition.

Correctness, image parity, provenance, and timing coverage are gates, not
post-hoc caveats. A valid negative result is retained and the selector falls
back; it is not hidden by tuning the matrix after inspection.

## Generated content

Do not commit Unity `Library/`, generated Players, raw captures, large result
directories, upstream benchmark projects, or restricted assets. Compact
reports, small diagrams, lock files, and reviewed receipts may be promoted to
`Docs/` or `Evidence/`.

## Public-release gate

Do not change the repository to an open-source license, publish UPM packages,
or redistribute source/assets until ownership is resolved in writing or a
clean-room repository has been completed and reviewed.
