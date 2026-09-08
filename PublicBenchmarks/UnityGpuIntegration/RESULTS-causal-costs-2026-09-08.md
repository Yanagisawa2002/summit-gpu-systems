# Fixed native probe sensitivity diagnosis — 2026-09-08

Removing all per-frame native timestamp probes did **not** remove the observed approximately 50/30 ms Present-wait clusters or the unavailable Unity GPU timings. Keep the native plugin's required completion synchronization. These nine diagnostic processes do not establish a stable performance improvement, and no new formal confirmation or default promotion is justified.

Measured source: `4916abb074d9c522d9e918429abddfbb4b2c9e2b`, based on published `573c873e672cbb5d6216efd8bc465d4904662a12`. Release Unity 6000.5.2f1 / D3D12, AMD Radeon AI PRO R9700, driver 32.0.31041.1004. The protocol and mode order were committed before building and measuring. Raw process timestamps span 2026-09-08 01:49:55–01:52:33 UTC. Source, build receipts and raw output hashes accompany the evidence.

## Controlled work and safety

`protocol-causal-costs.json` and `Scripts/Causal.ps1` fix three scenes, N262144, nine queries, 384 frames, 64 warmup frames, seed920071 and all four original arms. Each scene has one process per native probe mode; the mode order rotates none/whole/three, whole/three/none, three/none/whole. Arm order is old-full, new-full, new-incremental, old-incremental in every process. This only partially balances time and cache effects; it does not replace independent replication.

The diagnostic `nativeProbeMode` setting defaults to `three`. `none` does not create a managed timestamp session or emit frequency/Begin/End/completion events; `whole` requests only the scene scope; `three` preserves the original scopes and submission behavior. Unrequested native values are `NotRequested` and -1 ms. Reduced modes are rejected for formal runs, and diagnostic outputs cannot pass the existing formal analysis gates. Full asynchronous history verification remains mandatory.

The native DLL is still present in the same build. Its Unity device-load/reset callbacks can create its fixed query/readback/fence resources and configure four event IDs. Thus `none` means no issued measurement events, not removal of the DLL or proof of zero plugin initialization cost. Source search found no other `IssuePluginEvent` call sites in the integration or its runtime package dependencies. Reported event counts count recorded events; valid returned samples additionally establish successful requested-scope completion.

Each `none` process recorded 0/0/0/0 frequency/Begin/End/completion events; `whole` recorded 1/1536/1536/1536; `three` recorded 1/4608/4608/4608. All 36 arms and 13,824 frames match the independent CPU oracle byte for byte, including every active-count/change sentinel. The audit verifies identical work across modes, prescribed real AssetBundle lifecycle, two actual draw calls, checkpoints, native scope bounds and all 18,432 requested tick conversions, binary hashes, distinct PIDs and nonoverlapping process windows.

The native completion callback is deliberately unchanged: End records timestamp queries and ResolveQueryData; completion flushes prior commands, synchronizes workers and then signals the private queue fence. Consumption requires that fence and overwritten poisoned readback sentinels before slot reuse. Removing flags alone could signal readiness before the resolve is submitted. This diagnosis does not justify that unsafe change.

## Observations

All 11,520 steady frames were sampled focused, with target frame rate -1, vSync count 0 and background execution enabled. No focus intervention was performed; instantaneous focus changes between Updates remain unobserved.

| Scene / arm | none mean cadence ms | whole | three |
|---|---:|---:|---:|
| Sparse / new-full | 0.3091 | 0.3020 | 0.3332 |
| Sparse / new-incremental | 0.3131 | 0.3063 | 0.3261 |
| Hotspot / new-full | 0.5901 | 0.5882 | 0.6012 |
| Hotspot / new-incremental | 0.5631 | 0.5643 | 0.5697 |
| Streaming / new-full | 0.5821 | 0.5740 | 0.6140 |
| Streaming / new-incremental | 0.5860 | 0.5822 | 0.6378 |

These are descriptive means from 320 steady frames in one process per cell, not independent frame replicates, confidence intervals or performance acceptance. Some reduced-probe runs have lower cadence or recording CPU costs, but the changes are small, non-monotonic and confounded by run order and timing boundaries. Removing probes is a measurement control, not an algorithm optimization.

The large wait clusters persisted in all three modes in the first old-full arm. For example, sparse `none` frames197/199 had cadence 51.2033/30.0592 ms and Unity Present wait 51.0641/29.9466 ms. Streaming `none` frames82/84 had cadence 52.7622/29.1577 ms and Present wait 52.0964/28.7133 ms. Whole and three controls also showed approximately 50/30 ms clusters. Therefore, per-frame native completion callbacks are not necessary for these observed waits. This does not identify whether driver, compositor, scheduling or another engine boundary caused them.

No new-query steady frame exceeded 16.67 ms in this short diagnosis. That does not establish that the previously reported new-query long frames were repaired or would not recur.

Engine QPC mapping had no missing logical frames or ambiguous matches. Nevertheless, GPU timing remained unavailable (stored zero): none 391/3840 steady frames, whole 791/3840, three 540/3840. All such zero rows had equal frame-complete and Present-called timestamps. This non-monotonic result shows that native probes are not necessary for missing engine GPU data. Native scope durations and CPU waits are never substituted for these missing values.

## System capture availability and decision

PresentMon 2.5.1 was attached to owned PID20060 using a unique session, QPC time and v2 metrics. The ordinary-token attempt failed with exit6 and `failed to start trace session: access denied`. Its exact executable hash, command, capture PID15688, timestamps and stderr are retained. Subsequent cells made no capture attempt under the frozen first-failure rule. WPR 10.0.26100 was only queried and reported not recording; no WPR session was started. No worker elevation, group changes or cancellation of existing trace sessions occurred.

There are no usable OS presentation samples. The failed capture attempt can perturb the first process startup. Even a future successful PID attachment would begin after process creation and must not be described as complete first-Present coverage. A separately authorized, single bounded sparse-none system capture could distinguish whether the recurring wait cluster is also visible in OS presentation, but no such follow-up was run as part of these nine controls.

Retain these diagnostic controls and focus/pacing telemetry. Do not modify native fence readiness, change the default probe mode, claim a stable speedup or start a full performance matrix from this evidence. The actionable result is a narrower explanation: repeated native completion may perturb measurement, but it cannot alone explain the reproduced long waits or missing engine GPU values.

## Reproduction

Build the clean measured commit using `Scripts/Build.ps1`, then run `Scripts/Causal.ps1` with a fresh build/output directory, the independently verified seed920071 oracle root, and the shared serialized runner. Its optional `-PresentMon` argument uses the current token only. Preserve all failed attempts and frozen configuration hashes. No statistical retakes are part of this protocol.

Run `python Scripts/audit_causal.py <scene-evidence-root>` for raw work/history and descriptive timing checks, and `python -m unittest discover -s Scripts -p test_analysis.py` for the existing eight analysis tests. A self-contained evidence handoff copies the three original oracle binaries into `oracles/<scene>.bin`; raw configuration paths remain unchanged provenance. The audit can read these portable copies. Full binary verification additionally requires the inventoried `build-release-v1/Player` directory.
