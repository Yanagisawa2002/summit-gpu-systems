# Focused collection and frame-stability diagnosis

The collector now reports an unsupported allocation counter honestly and avoids several deterministic per-frame allocations and repeated whole-process serialization. The single frozen confirmation passed correctness, but **stable engine-cadence gains remain inconclusive and complete engine GPU comparisons involving the new query remain unavailable**. Keep the query backend opt-in; no runtime index, query algorithm, timestamp plugin, driver, power setting or global cache was changed.

Measured source: `4a63663c2ea0d089e8ca88c229f93b333d0df23e`, based on published main `20191e5050805adb6b44f2803eaa966bd1571d8e`. Release build GUID `dcbaaad04a0b467187aed50d95ff0c18`, Unity 6000.5.2f1, D3D12, Radeon AI PRO R9700, driver 32.0.31041.1004. The formal processes ran September 7, 2026, 18:10:40–18:28:08 UTC. Exact executable/dependency hashes and process receipts accompany the results.

## What the evidence establishes

The previous experiment's 92,160 logical frames had no missing QPC mappings. There were 15,456 stored zero GPU values; both positive and zero values were first observed exactly four frames later. The fifteen rejected records were process-start records outside the first recorded Update window, not measured logical frames. There were no ambiguous mappings. This verifies the old mapping implementation against its recorded inputs, not against an independent engine-frame trace.

Unity documents a four-frame availability delay and warns that GPU data is not guaranteed to be available or accurate. That documented delay is not itself an application defect. See [Unity's FrameTimingManager availability description](https://docs.unity3d.com/6000.0/Documentation/Manual/frame-timing-manager.html).

The old collector discarded repeated frame-start keys. A targeted Release diagnosis therefore retained every returned snapshot before deduplication. Three scenes/four arms produced **74,328 observations, 4,668 unique keys and 69,660 duplicate observations**. The recorded CPU frame/GPU frame/present/completion tuple never changed. Zero values never became positive. Steady frames were generally observed sixteen times, through source-frame lag nineteen. Delayed deduplication consequently cannot recover this machine/runtime's missing values within that observed window. This does not prove the internal driver or Unity backend reason; it shows that the missing values persist at the API boundary rather than being lost by the application's mapping or first-observation filter.

All prior steady zero GPU rows also had the completion timestamp equal to the Present timestamp. This is consistent with a missing-GPU-data fallback, but is not an independent low-level diagnosis. No zero value is substituted with native scene time, CPU waiting or interpolation.

## Allocation calibration and the minimal repair

The first repeated-snapshot diagnosis created managed objects while `GC.GetAllocatedBytesForCurrentThread()` still returned no allocation. A retained 1,048,576-byte array calibration then reported a counter delta of **0**, while `Profiler.GetMonoUsedSizeLong()` increased by **1,052,672 bytes**. All three calibration diagnostics and all fifteen formal processes failed the counter calibration. Runtime fields were CLR `4.0.30319.42000`, GC mode Enabled, maximum generation 0, incremental GC enabled and a 3,000,000 ns incremental time slice. This is a measured limitation of these Player builds, not a claim that all .NET runtimes lack this counter.

The raw per-thread allocation field is now **-1 when unavailable**, and analysis emits **null**. Old uncalibrated zeros must not be cited as zero allocation. The Mono heap delta is a calibration cross-check, not a substitute per-frame allocation rate. Legacy collection-count fields remain raw API values; maximum generation 0 does not support a generational-GC interpretation.

The implementation also:

- Reuses one `WaitForEndOfFrame` object and stores `logicalFrame` as an integer instead of constructing a phase string every frame.
- Uses value-type timing/process rows and preallocates their lists and timestamp-key storage for the planned run.
- Writes a complete single-arm checkpoint after each arm instead of serializing the entire growing process result repeatedly. The final complete result and all process Update intervals remain available.
- Keeps repeated raw snapshots explicitly diagnostic-only and rejects them in formal mode.

Three repaired Release diagnostic processes passed all twelve arms. Collector CPU time was roughly 1.8–2.4 microseconds per Update in those diagnostics, versus roughly 2.7–3.8 microseconds in the earlier snapshot diagnostics. These are descriptive diagnostics with additional tracing, not a paired formal performance claim. Long frames near 48 ms still appeared in repaired diagnostics, so the changes did not establish a cause or cure for the long-frame behavior.

All **240 formal arm checkpoints** were independently compared with the corresponding final report; every field matched except the checkpoint's own write duration, which is necessarily known only after the write. All fifteen allocation calibrations and every unavailable sentinel were also checked.

## One frozen confirmation

`protocol-focused-costs.json` retains three scenes, four arms, 262144 slots, 384 frames/arm, 64 warmup frames, four balanced blocks/process and five independent processes/scene. Independent payload seeds are 928201, 928203, 928207, 928211 and 928213. There is no additional time warmup, trimming, parameter search or repeated formal cell. Fifteen fresh CPU-oracle processes preceded fifteen formal processes. All **240 arms, 92,160 frames and 276,480 native timing scopes passed**, with zero formal failures. Eight pre-collection analysis tests passed; a separate audit independently recomputed all fifteen cadence contrast ratios and confidence intervals.

For `old-full` versus `new-full`:

| Scene | Engine cadence ratio [95% CI] | Baseline CV | Candidate CV | Baseline drift | p95 ratio | Failed gates |
|---|---:|---:|---:|---:|---:|---|
| Sparse static | 3.050 [2.798, 3.324] | 5.766% | 6.247% | 32.169% | 2.551 | Both CVs, drift |
| Hotspot dynamic | 83.339 [77.306, 89.843] | 0.212% | 7.379% | 0.831% | 64.568 | Candidate CV |
| Streaming switch | 3.268 [2.875, 3.714] | 2.158% | 10.580% | 18.114% | 2.647 | Candidate CV, drift |

Ratios above one favor the candidate. Gates remain both CVs <=5%, baseline drift <=15%, mean-ratio CI lower bound >1 and p95 ratio >=1.01. All primary cadence comparisons remain inconclusive. Changing both query and index gives cadence ratios 3.062, 87.948 and 2.957, also inconclusive. The old-query incremental-index ratios remain below one (0.892, 0.877, 0.881); those negative results are preserved. Cross-round differences cannot identify a causal effect of the collector repair, because old and repaired collectors were not formally paired within the same processes.

Narrow results are mixed under the same gates: new-full query GPU passes for sparse/hotspot (34.973 and 1719.722), but streaming query GPU fails candidate CV (7.547%). Its explicit scene GPU interval passes for hotspot/streaming (148.087 and 10.471), but sparse scene GPU fails candidate CV (6.807%). All point estimates, intervals and failed gates remain in `analysis-v1`; earlier-round narrow wins are not automatically carried forward as new confirmations.

## Missing complete GPU data and remaining long frames

Positive full-engine GPU coverage for new-full/new-incremental ranged across steady blocks as follows:

| Scene | New-full coverage | New-incremental coverage |
|---|---:|---:|
| Sparse static | 77.50–86.56% | 68.44–92.19% |
| Hotspot dynamic | 50.00–60.00% | 63.75–74.69% |
| Streaming switch | 68.44–92.50% | 50.94–63.75% |

Every complete-engine GPU comparison involving the new query fails the >=95% per-block coverage requirement. These metrics remain **Unavailable**. Native scene GPU is an instrumented command interval, not a replacement for the complete engine frame. OS presentation was not collected.

There were thirteen new-query frames above 16.67 ms, all in steady windows. Unity's main-thread Present-wait field accounts for **98.040–99.391%** of their cadence; command recording is below 0.4 ms in each. Twelve have native scene intervals below 1.1 ms; one has a 16.878 ms native scene interval while the engine GPU field reports 0.293 ms. The raw scopes must therefore remain distinct. The complete table, including source and adjacent engine CPU values, is in `focused-audit.json` and `long-frame-audit.json`.

Unity defines this Present-wait field to include waits for Present and target frame rate. It is not an OS present trace. The application requests targetFrameRate -1 and vSyncCount 0, but these data do not identify compositor, driver, scheduling, focus or plugin contributions. See [Unity's field definition](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/FrameTiming-cpuMainThreadPresentWaitTime.html). No nearby completed-GC-cycle increment was observed for the thirteen frames; that does not rule out incremental GC slices or other runtime work. The immediate reported cost is waiting, not the measured command-recording interval; the underlying cause remains unconfirmed.

The native timestamp plugin itself has an instrument effect: `GpuTimestampScope.RecordEnd` emits a completion event. `ConfigureEvents` applies `FlushCommandBuffers | SyncWorkerThreads` to that event, and `RecordCompletion` signals the Unity main queue fence. This experiment keeps the same three scopes per frame and the same plugin binary across all arms. Equal counts do not prove equal or negligible overhead. Inner completions can affect the surrounding scene interval, and all three can affect CPU/frame behavior. No plugin flag was removed without proving fence/readback correctness. These effects prevent attributing all variation to shader kernels, DVFS or GC.

## Reproduce and review

From this project directory, use fresh output folders:

```powershell
./Scripts/Build.ps1 -Unity '<your Unity 6000.5.2f1 executable>' -OutputRoot ./Artifacts/focused-build
./Scripts/Matrix.ps1 -BuildRoot ./Artifacts/focused-build -OutputRoot ./Artifacts/oracles-v1 -Mode oracle -ProtocolPath ./protocol-focused-costs.json
./Scripts/Matrix.ps1 -BuildRoot ./Artifacts/focused-build -OutputRoot ./Artifacts/formal-v1 -Mode formal -OracleRoot ./Artifacts/oracles-v1 -ProtocolPath ./protocol-focused-costs.json
python ./Scripts/test_analysis.py
python ./Scripts/analyze.py ./Artifacts/formal-v1 ./Artifacts/analysis-v1
python ./Scripts/audit_focused.py ./Artifacts
```

For the repeated-snapshot diagnosis, use `Run.ps1` with mode `validate`, `engineTimingAudit=true`, the corresponding oracle and unchanged scene/arms/frames. `audit_observations.py` summarizes first-versus-later values. `focused_audit.py` reproduces the offline prior-data review. Exact executed configs and commands are retained in the output folder. Full attestation audit also expects the frozen receipt files supplied with published evidence.

Evidence includes `freeze.json`, `offline-v1`, all six diagnostic processes and their two builds, `observation-audit-v1.json`, `lean-diagnostic-audit-v1.json`, `oracle-audit.json`, the final build, both fifteen-process matrices, `analysis-v1`, `independent-audit.json`, `focused-audit.json`, and all checkpoints. One build preflight rejected stale Git stat metadata after line-ending normalization; content hashes were unchanged, refreshing the index restored a clean state, and no Unity or Player process had been launched by that rejected attempt. No formal data was retried or overwritten. The final audit helper and this report were added after collection; measured Player source remains the SHA above.

The limited benchmark reproduction license is preserved. Publication and original-checkout integration remain the parent task's responsibility.
