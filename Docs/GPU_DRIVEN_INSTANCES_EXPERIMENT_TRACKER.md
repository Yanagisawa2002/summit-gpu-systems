# GPU-Driven Instances Experiment Tracker

| Run ID | Milestone | Purpose | System/variant | Split | Metrics | Priority | Status | Notes |
|---|---|---|---|---|---|---|---|---|
| R001 | Foundation | Cross-vendor primitive anchor | Portable vs WaveOps on RTX 4090 | 3 paired formal rounds | Native GPU average/P99, hashes | MUST | DONE | 29,700/29,700 timestamps; scan and stable compaction accepted |
| R002 | Foundation | Validate mixed device policy | Autotuner on RTX 4090 | 2 calibration + 4 evaluation rounds | Native GPU average/P99, selection confirmation | MUST | DONE | 37,800/37,800; WaveOps scan/compaction, Portable radix |
| R003 | M0 | Freeze portable instance data contracts | CPU oracle only | deterministic fixtures | pose/visibility/group hashes | MUST | DONE | Generic 48-byte state, view planes, draw templates, no project symbols |
| R004 | M0 | GPU correctness smoke | GPU mirror + cull + compact + indirect args | 1/255/256/257/4097 instances, 1–2 views | exact membership/counts/offsets/args, diagnostics | MUST | DONE | Current full D3D12 suite 544/544, zero skipped |
| R005 | M0.5 | Isolate rejected-pair contention | CulledTail vs VisibleOnly discard-key scatter | 1.05M instances, 4 views, 5/25/75/100% seeded dispersed visibility | native GPU mean/P50/P95, paired range, oracle | MUST | DONE | 93.13%/76.14%/44.84% mean reduction; 100% parity; 36,000/36,000 timestamps |
| R006 | M1 | Strong CPU baseline | Burst/Jobs cull + engine-native instanced draw | 10K/100K, 1V/1G and 4V/8G | CPU submission/frame P95/P99, draws, payload bytes | MUST | IMPLEMENTED | One-PID ABBA/BAAB; formal run pending |
| R007 | M1 | GPU macro baseline | GPU visible-only cull + engine indirect | 10K/100K, 1V/1G and 4V/8G | CPU/native-GPU P95/P99, buffers, parity | MUST | IMPLEMENTED | Native timestamps and fixed four-frame CPU-tail alignment; formal run pending |
| R008 | M2 | Frozen RTX macro matrix | Final package | primary 4-cell matrix | all decisive metrics | MUST | TODO | Launch only after CPU baseline freeze |
| R009 | M3 | Mechanism deletion | no pose/no cache/no batching/append | primary cells | paired deltas | MUST | IN PROGRESS | Visible-only deletion study accepted; remaining axes pending |
| R010 | M4 | External engine baseline | Unity GraphicsSamples | pinned external commit | CPU/GPU/frame tails, parity | MUST | TODO | Keep external repo separate |
| R011 | M4 | Optional asset fixture | Khronos NodePerformanceTest | pinned CC0 asset | node/mesh scaling | NICE | TODO | Do not use disputed Sponza package |
| R012 | M5 | AMD validation | final package on R9700 | same frozen matrix | same metrics | NICE | BLOCKED | Hardware currently unavailable; report unavailable |
| R013 | M5 | Resume figures | accepted runs only | cross-device | table/plot provenance | MUST | IN PROGRESS | Microbenchmark table retained; macro claim still pending |
