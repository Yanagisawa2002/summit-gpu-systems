# Run, reproduce or inspect

[Project overview](../README.md) · [No-copy rendering case](NO_COPY_VISIBLE_TILES.md) · [Evidence index](../Evidence/README.md)

Choose the task first. A successful CPU test, an offline evidence audit and a new GPU performance run establish different things. None automatically reproduces the historical NYCGIS result.

| Goal | Entry point | Requirements | What it establishes |
| --- | --- | --- | --- |
| Understand the rendering optimization | [No-copy visible tiles](NO_COPY_VISIBLE_TILES.md) | Browser | Source and historical report inspection; no execution. |
| Run an existing consumer without Unity Editor | [Windows query/transport release](https://github.com/Yanagisawa2002/summit-gpu-systems/releases/tag/r9700-query-boundary-2026-09-08) | Windows x64, PowerShell 7, D3D12 wave support | A separate query-driven transport demonstration, not the no-copy renderer. |
| Check decision and recording contracts | CPU commands below | .NET 10, PowerShell 7, Python, Git | Deterministic CPU functionality; no GPU timing. |
| Recheck recorded CPU/CUDA outputs | Offline command below | Python 3.10+ | Acceptance of retained numerical/build evidence; no new hardware run. |
| Build the procedural Unity benchmark | [Standalone build guide](../PublicBenchmarks/UnityGpuIntegration/README.md) | Unity 6000.5.2f1, Windows build support, PowerShell 7, Python 3.12+, compatible D3D12 GPU | New execution of the selected procedural task and protocol. |
| Reproduce the historical city renderer | [NYCGIS host requirements](../Integrations/NYCGIS/README.md) | Separate authorized NYCGIS host and data | Not available from the standalone root project alone. |

## Run the published Windows consumer

Open the release linked above and extract `query-transport-windows.zip`. From the extracted package root:

```powershell
pwsh -File Scripts/Launch-DispatchDemo.ps1 -Arm scan -Distribution hotspot
```

The release was tested on AMD Radeon AI PRO R9700. It does not require Unity Editor. Keep its included licenses, launcher and build hashes together. The [comparison report](../PublicBenchmarks/UnityGpuIntegration/RESULTS-query-boundary-2026-09-08.md) explains the scope: 45 formal processes passed correctness, but the batch candidate did not establish additional user-facing benefit over parallel scan. Demo animation is not a physical-presentation or FPS benchmark.

## CPU functional checks

Run from the repository root with .NET 10, PowerShell 7, Python and Git on `PATH`:

```powershell
pwsh -File Tools/Run-FunctionalChecks.ps1
dotnet run --project Tools/Examples/IndexQueryPlanning/IndexQueryPlanning.csproj -c Release
```

The first command explicitly selects CPU assertions, inert command-recording contracts, external source checks and repository layout checks. The second exercises four synthetic planner examples. The planner emits structural work estimates and `Unmeasured` status, not predicted milliseconds or measured algorithm winners. Neither command starts Unity or a GPU benchmark.

## Offline numerical evidence audit

From the repository root, choose an output filename that does not already exist:

```text
python -B PublicBenchmarks/WholeTaskMolecularDynamics/Scripts/verify_evidence.py Docs/evidence/whole-task-md-linux-20260915/evidence.tar.xz --output verification-local.json
```

The [Linux/RTX 5090 report](whole-task-md-20260915/LINUX_5090_NUMERICAL_RESULTS.md) describes the retained native Serial/OpenMP/CUDA numerical matrix and source identities. An offline PASS validates retained files and contracts; it does not rerun CUDA, validate Unity/HLSL on NVIDIA, or remove the campaign's formal-performance **NO-GO**.

## New Unity measurements

Use the [standalone project's exact build commands and prerequisites](../PublicBenchmarks/UnityGpuIntegration/README.md#build-and-reproduce). For a historical comparison, check out the measured source identified by that experiment's build attestation, rather than assuming today's branch or a release tag identifies the measured Player source.

Use new output directories and retain failed attempts. Select the matching protocol and correctness oracle before collecting timings. Keep CPU recording, GPU command scopes, queue completion, engine cadence and physical presentation distinct. Missing values must remain unavailable.

The root project's package-specific scripts are additional, separately selected workloads:

```powershell
./Tools/Run-GpuPrimitiveBenchmark.ps1
./Tools/Run-GpuDirectBinningBenchmark.ps1
./Tools/Run-GpuSensorPipelineBenchmark.ps1
```

Inspect `Get-Help <script> -Detailed` and each script's parameter block before execution. Native D3D12 timestamp-plugin rebuilding additionally requires the Visual Studio C++ toolchain and Unity native plugin headers. These commands are not the historical NYCGIS no-copy experiment.

## Consuming packages

Use a local `file:` dependency during development, or pin a Git UPM dependency to an existing commit. For example, this source snapshot is known to exist; it is not a measured-release certification:

```json
{
  "dependencies": {
    "com.summit.gpu-primitives": "https://github.com/Yanagisawa2002/summit-gpu-systems.git?path=/Packages/com.summit.gpu-primitives#58ef769503d7bfe843d562155c9022fa31ef46ee"
  }
}
```

Merge the relevant entry into the consumer's existing manifest rather than replacing its other dependencies. Packages with internal `com.summit.*` dependencies need those packages as well. Inspect their manifests before integration.

Public visibility is not a general reuse license. Read the [repository license](../LICENSE.md), [standalone benchmark license](../PublicBenchmarks/UnityGpuIntegration/LICENSE.md), and applicable package/third-party notices. This documentation change does not grant additional rights.
