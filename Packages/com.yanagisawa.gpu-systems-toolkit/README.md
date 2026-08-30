# GPU Systems Toolkit

This package is the stable aggregation surface for the reusable GPU systems in
this repository:

- compute primitives with Portable and WaveOps backends;
- Direct/Radix GPU-resident CSR spatial binning;
- persistent instance-state upload, multi-view visibility, grouping, and
  indirect arguments; and
- device-keyed primitive and instance policy with fail-closed fallback.

`com.summit.*` package IDs and `Summit.*` C# namespaces are retained as legacy
compatibility identifiers. They are not a dependency on SUMMIT scenes, GIS
data, buildings, sensors, assets, or application code. A future namespace
migration must ship adapters and an upgrade guide instead of silently breaking
existing users or invalidating measured commits.

## Installation

Until these packages are published to a scoped registry, installing only this
Git package is insufficient: Unity cannot resolve sibling monorepo packages by
their semantic versions. Use the repository's
`Tools/Install-GpuSystemsToolkit.ps1` script, which writes every stable package
as a commit-pinned Git UPM dependency and emits a receipt.

The stable default intentionally excludes:

- `com.summit.gpu-timestamps` — optional D3D12 diagnostics;
- `com.summit.gpu-sensor-pipeline` — experimental lab;
- `com.summit.gpu-residency-manager` — experimental lab; and
- `com.summit.gpu-deadline-scheduler` — experimental lab.

Use `-IncludeDiagnostics` or `-IncludeLabs` only when the additional platform
and evidence boundaries are acceptable.

## Public contract

- Output layout is a caller semantic constraint; policy does not silently
  change visible-only versus culled-tail behavior.
- Device policy may select upload/culling modes, while primitive selection is
  independently device/workload keyed.
- Unknown, stale, mismatched, or unvalidated policy profiles fall back to the
  conservative path.
- Callers own authoritative state and must obey documented buffer lifetime,
  fence, revision, and readback contracts in the component packages.

See the repository benchmark reports for device-scoped positive and negative
results. Installing this package is not itself a performance claim.
