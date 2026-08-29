# GPU-Driven Instances Experiment Tracker

| Run ID | Milestone | Purpose | System/variant | Split | Metrics | Priority | Status | Notes |
|---|---|---|---|---|---|---|---|---|
| R001 | Foundation | Cross-vendor primitive anchor | Portable vs WaveOps on RTX 4090 | 3 paired formal rounds | Native GPU average/P99, hashes | MUST | DONE | 29,700/29,700 timestamps; scan and stable compaction accepted |
| R002 | Foundation | Validate mixed device policy | Autotuner on RTX 4090 | 2 calibration + 4 evaluation rounds | Native GPU average/P99, selection confirmation | MUST | DONE | 37,800/37,800; WaveOps scan/compaction, Portable radix |
| R003 | M0 | Freeze portable instance data contracts | CPU oracle only | deterministic fixtures | pose/visibility/group hashes | MUST | DONE | Generic 48-byte state, view planes, draw templates, no project symbols |
| R004 | M0 | GPU correctness smoke | GPU mirror + cull + compact + indirect args | 1/255/256/257/4097 instances, 1–2 views | exact membership/counts/offsets/args, diagnostics | MUST | DONE | 16/16 focused and 527/527 full D3D12 EditMode |
| R005 | M1 | Strong CPU baseline | CPU cull + instanced draw | 10K/100K | CPU P95/P99, draws, frame tails | MUST | TODO | Retain identical workload |
| R006 | M1 | GPU baseline | GPU cull + indirect | 10K/100K | CPU/GPU P95/P99, buffers | MUST | TODO | One-PID counterbalanced |
| R007 | M2 | Frozen RTX matrix | Final package | primary 4-cell matrix | all decisive metrics | MUST | TODO | Launch only after matrix freeze |
| R008 | M3 | Mechanism deletion | no pose/no cache/no batching/append | primary cells | paired deltas | MUST | TODO | Cut components without decisive value |
| R009 | M4 | External engine baseline | Unity GraphicsSamples | pinned external commit | CPU/GPU/frame tails, parity | MUST | TODO | Keep external repo separate |
| R010 | M4 | Optional asset fixture | Khronos NodePerformanceTest | pinned CC0 asset | node/mesh scaling | NICE | TODO | Do not use disputed Sponza package |
| R011 | M5 | AMD validation | final package on R9700 | same frozen matrix | same metrics | NICE | BLOCKED | Hardware currently unavailable; report unavailable |
| R012 | M5 | Resume figures | accepted runs only | cross-device | table/plot provenance | MUST | TODO | Preserve negative and neutral results |
