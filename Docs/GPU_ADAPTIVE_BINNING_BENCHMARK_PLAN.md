# GPU Adaptive Spatial Binning: Forced-Backend Benchmark Plan

## Decision under test

This stage asks one narrow forced-backend question:

> Does the same GPU-resident uint key/value to CSR operation have reproducible
> workload regions where Direct count-scan-atomic-scatter wins, and other
> regions where low-bit Radix sort plus sorted-run extraction wins?

The contention holdout also evaluates a preregistered selector policy by
classification replay: it compares the policy prediction for each cell with
the accepted winner from forced Direct/Radix measurements. It does not time
`RecordAdaptive`, include selector overhead, or establish an end-to-end
adaptive-path speedup.

## Shared contract

Both timed implementations receive:

- `keys[N]`: structured uint keys;
- `values[N]`: structured uint values, with `value[i] == i` in the benchmark;
- `C`: active bin count.

Both publish:

- `binCounts[C]`;
- `binOffsets[C + 1]`, including the terminal valid count;
- `binnedValues[N]`;
- `diagnostics[2]`.

The forced A/B contract is `GuaranteedInRange`: every key is strictly less
than `C`, and diagnostics must remain zero. Ordering inside each bin is
unspecified by the shared contract. Radix stability is an implementation
property, not a cross-backend correctness requirement.

Correctness compares exact counts and offsets, then canonicalizes the defined
output prefix independently inside each bin. The undefined output tail is
excluded. A versioned SHA-256 hash covers the canonical CSR result.

## Implementations

### Direct

```text
clear counts/diagnostics
    -> trusted count with atomics (no per-element key validation)
    -> exclusive scan
    -> initialize write heads and terminal offset
    -> trusted atomic scatter (no per-element key/destination validation)
```

### Radix

```text
clear counts/diagnostics
    -> stable 4-bit LSD radix sort of only the required low key bits
    -> mark sorted-run boundaries
    -> finalize counts
    -> exclusive scan
    -> terminal offset
```

For power-of-two valid domains:

| C | Key bits | Radix passes |
|---:|---:|---:|
| 16 | 4 | 1 |
| 256 | 8 | 2 |
| 4,096 | 12 | 3 |
| 65,536 | 16 | 4 |
| 262,144 | 18 | 5 |

The measured AMD path explicitly requests WaveOps. Portable HLSL remains a
correctness fallback and is not used for the performance conclusion.

## Discovery matrix

The versioned `discovery-amd-r9700-v1` preset sweeps:

- `N`: 262,144 and 1,048,576;
- `C`: 64, 256, 4,096, and 65,536;
- distributions: uniform and hotset16.

The single-scenario runner also accepts hotset4 and singlebin, arbitrary valid
`C`, and therefore supports targeted `C > N`, same-bin contention, and radix
pass-boundary probes such as 16/17 and 256/257. Discovery runs are exploratory
and use four super rounds by default (eight adjacent pairs) with 240 measured
frames per block.

Input-generator contract `gpu-adaptive-binning-input-v3` defines the
`singlebin` target as `unchecked((uint)seed) % C`. This is a deterministic
mapping, not a distribution-quality claim. It keeps exactly one occupied bin,
while values remain immutable source indices. Retained discovery evidence
used v2, whose singlebin target was fixed at bin 0; each v3 formal singlebin
seed therefore supplies an unseen target-bin input. The
`N = 1,048,576`, `C = 16` singlebin, hotset4, and uniform bracket cells all
use seed `20261002`, so the fixed-axis comparison does not also change the
seed.

## AMD R9700 contention holdout

The new decision preset is `formal-amd-r9700-contention-v1`. Its five cells
were selected after discovery and use seeds that do not occur in the discovery
matrix:

| Scenario | N | C | Distribution | Seed |
|---|---:|---:|---|---:|
| `singlebin-n262144-c16` | 262,144 | 16 | singlebin | 20261001 |
| `singlebin-n1048576-c16` | 1,048,576 | 16 | singlebin | 20261002 |
| `hotset4-n1048576-c16` | 1,048,576 | 16 | hotset4 | 20261002 |
| `uniform-n1048576-c16` | 1,048,576 | 16 | uniform | 20261002 |
| `uniform-n1048576-c65536` | 1,048,576 | 65,536 | uniform | 20261005 |

The fixed `N = 1,048,576`, `C = 16` cells define an ordered
**dominant-set-cardinality** axis of 1, 4, and 16:

- singlebin sends every element to one bin;
- hotset4 sends 7/8 of elements to a four-bin dominant set and 1/8 through a
  cold tail over all 16 bins;
- uniform distributes over all 16 bins.

Therefore hotset4 is not described as having only four nonempty or active
bins. The bracket label records the dominant set that controls contention.
The preregistered candidate brackets are 1 to 4 and 1 to 16 at the same
`N/C` and common unseen seed `20261002`. Both brackets must have an accepted
Radix winner at cardinality 1 and an accepted Direct winner at cardinality 4
or 16. A reversed sign change is recorded but fails
`selectorDirectionValidated` and cannot make `crossoverClaimUsable` true.

The preregistered `radix-if-exact-calibrated-cell-else-direct` classifier
predicts Radix only for these two complete registered tuples:

- `(N=262144, C=16, distribution=singlebin, exactSingleBinKey=9)`;
- `(N=1048576, C=16, distribution=singlebin, exactSingleBinKey=10)`.

It predicts Direct for the other three holdout rows. Tuple membership uses
all four fields; dominant-set cardinality alone cannot select Radix.
`exactSingleBinKey` must equal generator-v3
`unchecked((uint)seed) % C`; non-singlebin rows use the explicit `-1`
not-applicable sentinel.

`selectorPolicyClaimUsable` requires all five predictions to match, both
brackets to validate in the required direction, at least two accepted cells
per backend, and otherwise usable A/B evidence.
This supports only classification replay over these exact five registered
cells. A broader threshold, midpoint `N`, other single-bin keys, and
generalization outside the five-cell matrix remain unvalidated. Measured
`RecordAdaptive` selection overhead also remains unvalidated. A broader
sweep and end-to-end overhead measurement are required before any
production-selector claim.
`crossover-bracket-evidence.csv` retains both endpoints, seeds, winners,
signed medians, generic sign-change status, and exact-direction status even
when the policy claim fails.

`formal-amd-r9700-v1` remains available as a legacy reproducibility entry.
The new AMD R9700 decision run uses only the contention holdout above.

## Formal schedule and instrumentation

Formal acceptance uses one Player build and one process per workload:

```text
control-pre
ABBA
BAAB
ABBA
BAAB
control-post
```

`A = Direct`, `B = Radix`: eight adjacent pairs, four AB and four BA. Every
measurement block has 60 case-local warmup frames, 900 measured frames, and
15 cooldown frames.

The complete command-buffer region is measured with native D3D12 timestamp
queries. Measurement readback is zero. Correctness readback runs before and
after measurement. Both timed paths disable detailed nested profiler markers
and retain the same outer A/B marker; the default runtime path keeps detailed
markers enabled for RGP/Profiler analysis.

Formal mode starts from a clean named HEAD, runs its own DX12 EditMode suite,
and binds the fully passing result XML and log to that HEAD and the source
snapshot hash. Configuration records the requested primitive backend, key
domain, trusted Direct contract, input-generator contract, key bits, pass
count, logical buffer bytes, source hashes, Player hash, GPU, driver, API,
Unity version, timestamp ABI, and exact NUnit identities.

Formal evidence additionally fails closed unless every `device.json` reports
`graphicsDeviceName = AMD Radeon AI PRO R9700`,
`graphicsDeviceVendorId = 0x1002`, `graphicsDeviceId = 0x7551`, and
`graphicsDeviceType = Direct3D12`. The decimal JSON values for the two IDs are
4098 and 30033.

## Frozen gates

A workload is a decisive Radix win only when all correctness/provenance gates
pass and:

- paired median speedup is at least 5%;
- paired median absolute reduction is at least 0.005 ms;
- Radix wins at least 6 of 8 pair averages;
- AB and BA subgroup medians both favor Radix;
- paired P99 median regression is no worse than 2%.

Direct uses the symmetric decisive gate. A workload that passes neither
backend's decisive gate is `neutral-or-inconclusive`; there is no independent
tie band.

The formal contention result is a forced-backend classifier replay, not
measured `RecordAdaptive` timing. Its average-policy claim requires at least
two decisive Direct cells, at least two decisive Radix cells, exact
Radix-to-Direct direction in both fixed-`N/C` brackets (1 to 4 and 1 to 16),
and prediction agreement in all five holdout cells. The independent tail
claim additionally requires each cell's predicted winner to win at least 7
of 8 paired P99 comparisons and its worst paired P99 improvement to be at
least -10%.

## Claim boundary

This microbenchmark can support claims about a reusable GPU-resident spatial
binning primitive on the named AMD/DX12 configuration. It does not establish
whole-scene FPS, city rendering improvement, measured DRAM bandwidth, driver
optimization, asynchronous scheduling, NVIDIA behavior, or a universal
adaptive policy.
