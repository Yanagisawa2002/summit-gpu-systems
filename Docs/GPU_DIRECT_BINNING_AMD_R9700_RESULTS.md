# GPU Direct Spatial Binning: AMD R9700 Formal Result

## Decision

The reusable GPU-resident count-scan-scatter implementation is
correctness-validated and the A/B evidence is usable. It did **not** meet the
frozen performance-improvement gate on the tested AMD Radeon AI PRO R9700.

- `aBDataUsable=1`
- `crossWorkloadImprovementClaimUsable=0`
- `measurementReadbackBytes=0`
- one workload was neutral or inconclusive;
- two workloads were negative;
- no end-to-end, FPS, city-scene, bandwidth, or memory-saving claim is
  supported.

The result is useful negative evidence: a custom direct histogram/count path
does not automatically outperform a transparent portable primitive
composition on this device. Any adaptive direct-versus-radix stage must first
demonstrate a real forced-backend crossover.

## Measured source and system

- Measured Git commit:
  `215c2eba9686542e1be6693af2fbf2f345f5867b`
- Branch: `codex/gpu-direct-spatial-binning`
- Unity: `6000.5.2f1`
- OS: Windows 11
- Graphics API: Direct3D 12, feature level 12.2
- GPU: AMD Radeon AI PRO R9700
- Unity-reported graphics memory: 32,476 MiB
- Driver: `32.0.31035.1003`
- Native timestamp ABI: 2
- Native timestamp capability mask: `0x1F`

The runner recorded a clean named branch at the start and finish. Source
hashes and the built Player payload remained stable through all three child
processes.

## Implementation under test

Production direct path:

```text
clear -> count -> portable exclusive scan -> prepare offsets/write heads
      -> atomic scatter
```

Reference path:

```text
portable histogram -> portable exclusive scan
                   -> 256-thread prepare -> 256-thread atomic scatter
```

Both paths use the same:

- key/value inputs;
- counts, exclusive offsets, binned-value, and diagnostic contract;
- portable scan implementation;
- validation oracle;
- logical per-case `GraphicsBuffer` payload.

Invalid keys are excluded. Bin-local output order is unspecified, so the CPU
oracle compares canonical sorted membership within each bin.

## Formal protocol

Frozen preset: `amd-r9700-v1`.

| Scenario | Elements | Bins | Distribution | Seed |
|---|---:|---:|---|---:|
| `uniform-c4096` | 1,048,576 | 4,096 | uniform | 20260730 |
| `hotset16-c4096` | 1,048,576 | 4,096 | 87.5% directed to 16 hot bins | 20260731 |
| `uniform-c65536` | 1,048,576 | 65,536 | uniform | 20260732 |

Each scenario used one Player process and this position-balanced schedule:

```text
control-pre; ABBA; BAAB; control-post
```

Each block used 15 cooldown frames, 60 case-local warmup frames, and 900
measured frames. The matrix contains:

- 27,000 native timestamp rows;
- 21,600 measured A/B rows;
- 5,400 empty-scope control rows;
- four adjacent A/B pairs per scenario;
- four correctness validations per scenario;
- zero measurement readback;
- 16 timestamp-instrumentation bytes per completed sample.

Unrelated production informational logs are suppressed before scene load only
when the benchmark command-line flag is present. Warnings and errors remain
enabled. Each final Player log is approximately 2.7 KB rather than the
superseded run's approximately 54 MB.

## Formal A/B result

Positive percentages mean the direct path was faster. The values below are
medians of the four adjacent position-balanced pair deltas.

| Scenario | Positive pairs | GPU average | Absolute average reduction | GPU P99 | AB median | BA median | Logical case bytes | Classification |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| `uniform-c4096` | 3/4 | +0.506193% | +0.000284956 ms | +4.681413% | +5.528943% | +0.499386% | 29,966,480 | neutral or inconclusive |
| `hotset16-c4096` | 2/4 | -0.226114% | -0.000260267 ms | +5.083515% | +2.245247% | -0.440835% | 29,966,480 | negative |
| `uniform-c65536` | 2/4 | -0.304526% | -0.000194689 ms | +2.520186% | +0.222912% | -0.304526% | 30,703,760 | negative |

All three P99 medians improved in this run. That is not enough to claim an
optimization: the frozen gate also requires at least three positive average
pairs, at least 5% median average improvement, at least 0.005 ms absolute
average reduction, nonnegative AB and BA subgroup medians, and no material
P99 or logical-residency regression.

No scenario met the complete gate. Direct and reference logical case payloads
were identical.

## Correctness and reliability

- Final EditMode suite: 100/100 passed.
  - production package: 93/93;
  - benchmark harness: 7/7.
- PowerShell provenance regression: 56 assertions passed.
- Exact NUnit identity mode: `exact-nunit-node-v1`.
  - expected identities: 8;
  - observed exact identities: 8;
  - missing identities: 0.
- All native timestamp samples were ready.
- No timestamp acquire failures, result failures, or timeouts occurred.
- Private fence values, timestamp intervals, frequency, and device generation
  passed the formal consistency gates.
- Empty-scope P99 was 0.00008 ms in every scenario.
- All warmup and final CPU-oracle validations passed.
- Correctness hashes:
  - `uniform-c4096`: `C9953124`
  - `hotset16-c4096`: `164BA331`
  - `uniform-c65536`: `B2EE18E3`

## Evidence provenance

Retained report:

`Reports/GpuDirectBinning/formal-amd-r9700-215c2eb-v3`

The added manifest covers the 37 original runner artifacts:

- covered bytes: 10,005,334;
- manifest SHA-256:
  `1AEA1FADA9C6D6801681F1F0D237801F0D546E5CF1565DD125F85CC1B0B7E127`.

Key bindings:

- source snapshot SHA-256:
  `039D7927C310796D7694FB29B846DFCEBEFB20541FE3C424545C72A3758D7D41`
- Player payload: 346 files, 180,882,320 bytes;
- Player payload SHA-256:
  `C9E9A849CE3CBBBA918AB5D2802CF286E404ECEF878B4DCC460FC37603D73C81`
- EditMode XML SHA-256:
  `C3C1F2A1F205A3CE61C60E2D0987136C98916CB9A5ADBF8F98C195509C42D263`
- runtime shader SHA-256:
  `F7A0B9CD69FDD253125D1E52E3DA89361655CD4D5B4635C3FB3D1FA61EC915E8`
- reference shader SHA-256:
  `13F8AE81C8ABBBCDA66B6C0AA31E861B26C7E3BA5DBC06AA4AA3B909822158AA`
- runtime API SHA-256:
  `03934F09D858ED7688FE3EAD7BC26401E898A6F9C98047ACF0BC13727DB756B1`
- timestamp DLL SHA-256:
  `5CA8D566D3B10902571B805AC870D829B50B55ABC64DB91BD7EF88C1B0B676DA`

## Superseded runs

Do not quote the earlier `4835d7b` formal metrics. Those Player processes
contained repeated production informational logs and were measured before
the final exact-identity evidence gates.

Do not quote the intermediate `92e8dc9` metrics either. Its exact identities
were checked, but the observed identity set was not yet persisted in
`runner-config.json` for independent summarizer verification.

Only the `215c2eb` report above is the retained S2 formal result.

## Safe resume wording

> Implemented and correctness-validated a reusable GPU-resident
> count-scan-scatter spatial-binning primitive with native DX12 timestamping,
> exact NUnit/source/Player provenance, and a position-balanced same-process
> A/B harness. On an AMD Radeon AI PRO R9700, the frozen three-workload matrix
> found no reproducible latency advantage over a transparent portable
> primitive-composed reference; complete neutral and negative evidence was
> retained and used to gate the next adaptive-backend investigation.

Do not claim:

- an FPS or city-scene improvement;
- a visually observable gain;
- measured DRAM bandwidth or driver-reported residency reduction;
- a proven atomic, scan, or cache bottleneck;
- async-compute overlap;
- NVIDIA or cross-vendor validation;
- driver-level optimization;
- statistical significance.

The next independent stage should first force direct and radix backends across
a calibration matrix. An adaptive selector is justified only if the matrix
contains decisive, repeatable win regions for both backends.
