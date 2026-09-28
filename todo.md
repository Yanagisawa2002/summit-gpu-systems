# summit-gpu-systems TODO

Updated: 2026-09-28. Repository-specific handoff for testing on another device. All tasks below are pending on that device; this documentation change runs no benchmarks or application tests.

Source baseline: `58ef769503d7bfe843d562155c9022fa31ef46ee` on main. Start with the [pinned README](https://github.com/Yanagisawa2002/summit-gpu-systems/blob/58ef769503d7bfe843d562155c9022fa31ef46ee/README.md) for prerequisites and the scope of historical evidence.

## Prepare the checkout

- [ ] Clone into a fresh directory and select the recorded source commit. If deliberately using a newer revision, record the new SHA and review the intervening changes.

```powershell
gh repo clone Yanagisawa2002/summit-gpu-systems
Set-Location summit-gpu-systems
git switch --detach 58ef769503d7bfe843d562155c9022fa31ef46ee
git rev-parse HEAD
```

## Functional checks first

- [ ] Install the .NET 10 SDK, Python and PowerShell 7. Run the CPU example and deterministic functional checks from the repository root:

```powershell
dotnet run --project Tools/Examples/IndexQueryPlanning/IndexQueryPlanning.csproj -c Release
pwsh Tools/Run-FunctionalChecks.ps1
```

- [ ] Inspect unavailable costs and capacity recovery in the example. Its synthetic planning scores are not measured speedups.
- [ ] For Unity work, use the documented Unity 6000.5.2f1 host and a supported Windows graphics environment; record the exact Editor, graphics API, GPU and driver. Rebuild native timestamp instrumentation only with the documented native toolchain.
- [ ] Import the embedded packages and run an asset-independent procedural correctness workload before selecting a performance experiment.
- [ ] If benchmarking later, use the repository's existing workload/repetition protocol. Retain upload, maintenance, conversion, query, readback and consumer costs separately and as a complete task.
- [ ] Keep current planner/default eligibility Unmeasured until demonstrated. Do not transfer historical R9700 results, kernel gains or native Serial comparisons to the new machine without a matching experiment.
- [ ] Validate the optional HLSL scan bridge only when selected, preserving exact source/profile identity and real buffer bindings.

## Record the new-device outcome

- [ ] Record source and binary identity, machine/OS, toolchain, hardware/driver, command/exit code, input/configuration hashes and the actual PASS / FAIL / BLOCKED / NOT RUN result.
- [ ] Keep generated binaries, captures and large assets outside source control. Link the retained evidence from the affected validation document/PR.
- [ ] Preserve unavailable metrics and negative outcomes. Separate static/functional success, runtime correctness and performance conclusions.

Create a focused `codex/` branch before implementing a fix. Existing unexecuted acceptance items remain open until the selected workflow is actually run.
