# Finite sphere reuse comparison

Run from the repository root. This runner uses the retained native inputs and
executable, the retained old Player, and a separate new Player. It never fetches
dependencies or regenerates inputs. Read the [protocol](../../../Docs/SPHERE_REUSE_PROTOCOL_2026-09-10.md)
and [public API ownership guide](../../../PublicBenchmarks/External/Adapters/SPHERE_REUSE.md).

Every build/Player/native stage is called through
`Tools/ExternalSources/Invoke-ActualStage.ps1`, which acquires
`Local\CodexR9700VNextUnityGpu`, checks conflicting processes and the 20 GiB
reserve, and saves success/failure receipts. `common.ps1` additionally bounds
this optimization directory plus projected growth to 6 GiB. Never run a stage
body directly without that outer guard. Existing output names are rejected;
retain failures and choose a new correctness/build attempt when repairing.

1. Guarded `prepare.ps1` hashes every old artifact, then copies only the small
   Unity host/cache to the new directory. Original input, old Player and native
   executable are reused in place.
2. Guarded `build.ps1 -Attempt r1` stages current public sources into the copied
   host and builds `player-r1`. A wrapper script can supply a different attempt.
3. Guarded wrappers call `run.ps1 -Arm new -Name validation-api-r1 -Kind sphere-api
   -Attempt r1 -ValidateOnly`, then `validation-arborx-r1`/`arborx` and
   `validation-cabana-r1`/`cabana`. They use actual D3D12 and save complete CSR;
   no measured clock samples are generated.
4. `python Tools/ExternalSources/SphereReuse/analyze.py --attempt r1 --validate-only`
   independently decodes both complete ArborX outputs and all 40 Cabana outputs.
   Review the API fixture receipt and all failures; commit source and protocol.
5. `python Tools/ExternalSources/SphereReuse/freeze.py --attempt r1` requires a
   clean committed tree and successful correctness. It hashes both Player trees,
   actual staged source, original inputs/dependency receipts/toolchain and exact
   commands, and writes twelve stage bodies in the fixed four-round order.
6. Run each generated body separately through `Invoke-ActualStage`, with
   `-EstimatedAdditionalGiB 0.3`, strictly in `frozen-run.json` process order.
   Each body rejects mismatched arm/attempt/name and checks frozen hashes.
   Preserve any failed process; do not replace it with a favorable rerun.
7. `python Tools/ExternalSources/SphereReuse/analyze.py --attempt r1` audits every
   full GPU result and native row, command/build identity, fixed order, all
   warmups/measurements, per-repetition preparation and process-mean statistics.
   The analysis output directory must be new.
8. Run `prepare.py verify-baseline` under a final guarded audit, then write the
   results/report and release receipt. All old file paths and SHA256 must match
   their pre-phase inventory. Commit only source, protocol, report and small
   evidence; leave Players, caches and raw outputs in ignored artifacts.

The scripts themselves do not introduce a runtime consent API. This is explicit
reproduction tooling with resource and evidence checks, not automatic benchmark
startup or default/profile promotion. Four process means are the independent
units; intra-process repetitions are retained but do not enlarge that sample size.
