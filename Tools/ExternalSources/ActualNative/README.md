# Actual native workload and GPU snapshot replay

This is the source for the [September 10 protocol](../../../Docs/EXTERNAL_ACTUAL_PROTOCOL_2026-09-10.md)
and [results](../../../Docs/EXTERNAL_ACTUAL_RESULTS_2026-09-10.md). Cabana and ArborX
are external libraries' own native benchmarks. The SUMMIT executable is an
adaptation host for their exact exported data, not a new public benchmark or an
official application scene. Google Benchmark supplies ArborX's timing framework.

The committed entry points call actual Cabana/Kokkos/ArborX APIs and original
generator helpers. No algorithm is copied or replaced with a CPU/GPU stub.
`CMakeLists.txt` also compiles the unchanged ArborX driver as `ArborXNative`,
resolving the upstream Windows CMake omission with same-toolchain linking.
`CabanaCapture` is generated from the pinned upstream driver: its only execution
hook exports positions and complete CSR after the native build timer stops,
before the original neighbor traversal/permutation continues.

## Reproduce without disturbing retained evidence

Run from a clean checkout of this delivery on the documented Windows toolchain.
The dated directory is deliberately fixed. Existing receipts, inputs and Player
outputs cause rejection; keep an existing run intact. For a fresh reproduction,
use a fresh checkout and the same relative artifact layout. Dependency archives
and caches from the actual run remain available in its original worktree.

Every executable build, extraction, import and run must be a body passed to
`Invoke-ActualStage.ps1`. It takes the shared mutex and checks background work and
the 20 GiB reserve. Stage bodies in `../ActualStages/` preserve the commands used
for the native builds/capture; `build-player.ps1` stages the corrected current
shader directly into a new `player-r1`. The first failed FXC build and subsequent
incremental repair have separate original scripts/logs under the retained run's
`stages/` directory.

Toolchain: VS Community 18 / MSVC 19.51.36248, NMake, C++20 Release; Kokkos
4.7.02 commit `6739bc623081648af9e752b616d9671527922cbf`, Serial only; Boost 1.87.0;
Google Benchmark 1.9.1 commit `c58e6d0710581e3a08d65c349664128a8d9a2461`;
CMake 3.31.10; installed Unity 6000.5.2f1, Windows Mono/D3D12. Do not substitute a
toolchain/backend while retaining the measured build identity.

The fresh-run sequence is:

1. In a guarded preparation body, create `Artifacts/actual-20260910/downloads`,
   `dependencies`, and `stages`; run `python Tools/ExternalSources/prepare_actual_dependencies.py`
   and `python Tools/ExternalSources/ActualStages/prepare-boost.py` serially.
   Check exit codes. Archive SHA/expanded-byte receipts are written before builds.
2. Guard `ActualStages/build-cabana.ps1`, then `build-arborx.ps1`. These build the
   real Kokkos Serial library and the two canonical native targets. Use a 6 GiB
   additional-space budget for the dependency/native stages.
3. Run `python Tools/ExternalSources/prepare_actual_replay.py capture-source` in
   a source preparation body, then guard `ActualStages/build-replays.ps1` and
   `capture-inputs.ps1`. Capture must yield all 40 Cabana snapshots and one
   complete ArborX default snapshot. Source-file SHA verification is mandatory.
4. Guard `ActualStages/build-player.ps1` with an 8 GiB budget. It copies the three
   real runtime packages and external adapters into a minimal host, imports and
   builds it, and rejects shader errors even if Unity reports build success.
5. Guard two bodies calling `Run-ActualReplay.ps1 -Kind cabana|arborx -Backend gpu
   -Name validation-cabana-r1|validation-arborx-r1 -ValidateOnly` with the matching
   kind/name. These save every actual GPU output and perform full equality, with
   no measured samples. Run the native `CabanaReplay <inputs> <new-csv>` and
   `ArborXReplay replay <arborx-default.bin> <new-csv>` into
   `native-correctness/cabana.csv` and `arborx.csv`; diagnostic clocks are excluded.
6. With all source/protocol committed, run `python Tools/ExternalSources/freeze_actual_replay.py`.
   It verifies native and real GPU gates and staged source hashes before freezing
   sources, inputs, libraries, executables and Player files. Rebuild/repair requires
   a distinct identity and successful correctness gate before any formal timing.
7. For each kind, run four pairs with names `{kind}-p01-native`, `{kind}-p01-gpu`,
   etc., through guarded bodies calling `Run-ActualReplay.ps1`. Order is native/GPU,
   GPU/native, native/GPU, GPU/native. The host performs the frozen ten warmups and
   ten measured repetitions; every repetition verifies complete output.
8. Also run unchanged `LinkedCellPerformance <new-timers.txt>` and
   `ArborXNative --benchmark_filter=^BM_radius_search<ArborX::BVH<Serial>>/50000/20000/10/1/0/0/0/manual_time$
   --benchmark_out=<new-json> --benchmark_out_format=json` in guarded bodies.
   Preserve default iteration/minimum-time behavior and separate these outputs.
9. Run `python Tools/ExternalSources/analyze_actual_replay.py` after execution is
   finished. It independently decodes every raw GPU CSR, verifies native checksums
   and frozen hashes, checks process ordering, and includes all formal samples.

Example guard, from the repository root (unique stage name/output required):

```powershell
./Tools/ExternalSources/Invoke-ActualStage.ps1 `
  -Stage repro-build-cabana `
  -ScriptFile ./Tools/ExternalSources/ActualStages/build-cabana.ps1 `
  -EvidenceDirectory ./Artifacts/actual-20260910/stages `
  -EstimatedAdditionalGiB 6
```

Original actual stage bodies, their SHA receipts, exact process arguments, exit
codes, raw logs, source-capture diff and all outputs remain in
`Artifacts/actual-20260910/`. Do not run reproduction commands into that completed
evidence directory. No automatic queue, profile emission or publication is part
of these tools.
