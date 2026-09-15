# Linux / RTX 5090 molecular dynamics numerical results

2026-09-15. **Native numerical validation passed. Formal performance remains
NO-GO for this campaign.** Seven native implementations completed the same
force-to-velocity-to-position slice. The separate monitoring calibration failed
its predeclared quiet-CPU gate. No independent performance confirmation was run.
The SUMMIT GPU consumer and Unity Linux backend remain open work.

## What the result covers

The consumer comes from the fixed
[ArborX molecular dynamics example](https://github.com/arborx/ArborX/blob/375875dfb6b2e7631b1ba599cd26ee5c1e68ab90/examples/molecular_dynamics/example_molecular_dynamics.cpp).
The selected dependency slice queries neighbours, computes every particle's
force, then updates velocity and position. It excludes the original unused
energy diagnostic and does not establish long-horizon physical accuracy.

Every implementation reads identical bytes exported once by the Serial producer.
The original case retains 4,000 particles, spacing 1.7, XorShift64 seed 5374857,
radius 3, original-ID self exclusion, float arithmetic, mass 1 and dt 0.005.
The complete-task API starts with the CPU snapshot and ends with completed
backend-resident force/velocity/position. Necessary uploads, index rebuild and
completion waits belong inside that boundary. Audit copies and file writing
occur afterward. The conventional tiled CUDA controls consume neighbours
directly; their complete CSR reconstruction is a separate audit.

| Case | Particles | Acceptance |
| --- | ---: | --- |
| `original-10` | 4,000 | Original setup; full members and state |
| `discovery-8` | 2,048 | Separate source-derived discovery case |
| `small-6` | 864 | Small extension |
| `large-20` | 32,000 | Large extension |
| `perturbed-10` | 4,000 | Explicitly perturbed extension |
| `boundary-membership` | 8 | Exact/adjacent-float radius, negative coordinates, coincident distinct IDs; CSR only |
| `isolated-3` | 3 | No-neighbour state completion |
| `tail-129` | 129 | Partial tile; full members and state |

Coincident distinct particles retain membership but have a singular original
force law. The boundary case therefore has no state golden and never receives
a full-state PASS label.

## Observed implementation and hardware identities

| Item | Observed identity |
| --- | --- |
| Serial / OpenMP source | `8032841a2bb7968fbf6e8e06a540fad123c77a63` |
| Successful CUDA source | `9932ec879798345e69cea79f65615a68d7115b33` |
| Intermediate CUDA flag fix | `7ceaaeb94f81db5d1f2a8136632a5a386db758ce` |
| ArborX | `375875dfb6b2e7631b1ba599cd26ee5c1e68ab90` |
| Kokkos | `6739bc623081648af9e752b616d9671527922cbf` / 4.7.2 |
| GPU / driver | NVIDIA GeForce RTX 5090, UUID `GPU-27a668ff-c748-4395-c3e1-32c396c485a0`, driver 580.76.05 |
| CUDA compiler | Existing `/usr/local/cuda-12.8/bin/nvcc`, 12.8.93; generated ELF inspection confirmed `sm_120` |
| Host tools | GCC 11.4.0, CMake 3.22.1, GNU Make 4.3, Python 3.12.11 |
| CPU allocation | cgroup quota 25 core-time; visible cpuset 0–207; selected CPUs 0–7 are eight distinct physical cores |
| CPU observation | Affinity plus SMT siblings 104–111; effective capacity denominator 8, not 208 |
| OpenMP / builds | Actual execution-space concurrency 8; compilation limited to four jobs |

The CUDA fixes changed compiler flag routing, explicit lambda captures, and
runtime UUID retrieval. The original force/update extraction, traversal order,
predicate, both tile barriers, inactive-lane handling and tolerances stayed
unchanged. The non-CUDA `main.cpp` content was compared after excluding its
CUDA-only identity block. CPU evidence keeps its original `8032841` binding;
it is not relabelled as a `9932ec8` build. Later documentation/verification commits
are delivery changes, not new GPU execution evidence.

## Completed numerical gates

| Implementations / gate | Actual checks | Result |
| --- | --- | --- |
| ArborX Serial + conventional CPU grid | 2 arms × 8 cases × 2 repetitions | PASS: 32 CSR checks, 28 full-state checks |
| ArborX OpenMP + CPU grid OpenMP | Same matrix, eight observed workers | PASS: 32 CSR checks, 28 full-state checks |
| ArborX CUDA + tiled128 + tiled256 | 3 arms × 8 cases × 2 repetitions | PASS: 48 CSR checks, 42 full-state checks |
| Tiled capacity rejection | Both tiles, tail-129, capacity of one ID | PASS: expected rejection before scatter, actual UUID/PID checked |
| Independent scalar acceptance | Seven non-singular cases; both tiles' first/last full states | PASS |
| Fixed calibration workload | One tiled128/original-10 process, 400 repetitions | Numerical PASS; monitoring calibration rejected |

Thus the numerical matrix has **112 repetitions: 98 full-state and 14
membership-only**. The 400 calibration repetitions are a separate process and
are not independent statistical samples. Every native repetition performed its
complete audit. Full first/last files were retained for independent reading:
**114 CSR files and 100 state files**, including the calibration process.

CSR acceptance checks every offset and the complete sorted ID multiset in every
row, including self exclusion. State acceptance checks every finite force,
velocity and position component using the bounds declared before execution:

| Field | Absolute term | Relative term |
| --- | ---: | ---: |
| Position | 0.00002 | 0.000002 |
| Velocity | 0.00002 | 0.00002 |
| Force | 0.002 | 0.00002 |

The allowed error is `absolute + relative * abs(reference)`. The independent
Python scalar implementation rounds every arithmetic operation to binary32.
It also checked all-pairs membership for every non-singular case up to 4,000
particles. At 32,000 it recomputed every state component using the complete CSR
already cross-checked by Serial ArborX, conventional grid and OpenMP.

Across the tiled results, maximum absolute errors were 1.90735e-6 for position,
9.15527e-5 for velocity and 0.015625 for force. The latter occurred against force
36,479, where the predeclared combined bound is 0.73158. The largest error as a
fraction of its own component's bound was 0.151989. No tolerance was increased.
[Full component details](../evidence/whole-task-md-linux-20260915/scalar-error-details.json)
retain the values and indices.

The offline verifier re-read all saved CSR/state files, checked all three
successful builds' complete source/product manifests and command-log hashes,
and recomputed the seven scalar states. It retains the observed remote
all-pairs membership receipt rather than repeating that expensive search.
External installed compiler binaries were hashed on the host; the offline
verifier does not execute them or claim a fresh toolchain validation.

## Exact monitoring rejection

The one predeclared 400-repetition calibration retained nine observations:
five before, three during and one after the process. CUDA's actual PID was
observed on the assigned GPU in all three during samples, and all three
overlapped recorded task windows. Clock mapping, GPU XML interpretation and
positive owned CPU accounting passed their preceding checks.

Three preflight intervals failed the absolute CPU-background gate:

| Interval | Affinity/SMT background, cores | cgroup background, cores | Percent of effective capacity |
| --- | ---: | ---: | ---: |
| 0 | 0.353088 | 0.162787 | 4.413597% |
| 1 | 0.389138 | 0.151399 | 4.864220% |
| 2 | 0.267595 | 0.146546 | 3.344943% |

The profile requires **both ≤0.25 background cores and ≤5% of capacity**.
These intervals exceeded the absolute limit. Their GPU utilization was zero,
with no foreign GPU process. No additional calibration, threshold relaxation,
sample removal or substitution was performed. The exact rejection is reproduced
by the offline verifier; see [raw-derived interval analysis](../evidence/whole-task-md-linux-20260915/calibration-rejection.json).

GPU compilation/execution is working and numerical checks passed. Relative
algorithm cost remains undetermined. Incidental timing CSVs stay in the archive;
they do not supply a qualified speedup, confidence interval or winner.

## Failed attempts and evidence corrections

All earlier directories and logs were retained:

* CUDA r1 failed during configuration because the pinned `nvcc_wrapper` was
  given `-Xcompiler=-ffp-contract=off`. The wrapper already forwards host flags;
  `7ceaaeb` uses `--fmad=false -ffp-contract=off`. The next actual command log
  confirmed the intended host/device routing.
* Source staging for r2 hit a bounded public Git fetch timeout. Its attempted
  CUDA stage rejected missing capability evidence before compiling or executing
  a kernel. Offline metadata recovery first rejected CRLF working-file bytes
  against LF Git objects. Raw `git cat-file` objects, their SHA256/Git IDs, and
  an incremental bundle established identity while leaving source bytes intact.
* CUDA r3 built Kokkos but rejected the application's first implicit captures
  inside `if constexpr` and nonexistent `cudaDeviceGetUuid` call. `9932ec8` added
  an explicit capture list and reads `cudaGetDeviceProperties(...).uuid` for the
  actual current device. The unchanged kernel body then compiled and passed r4.
  The capture rule is documented in the
  [CUDA 12.8.1 programming guide](https://docs.nvidia.com/cuda/archive/12.8.1/cuda-c-programming-guide/index.html#extended-lambda-restrictions).
* The r4 staging receipt's `archiveSha256` field accidentally recorded a Git
  blob ID after a Python variable was reused. The original archive/per-file
  checks had passed. A separate immutable `source-staging-r4-rechecked.json`
  rechecked the complete archive and all 453 staged files before CUDA launch,
  recorded the correct SHA256 and bound the original receipt. The original
  mistaken field remains available for inspection.
* An initial local archive-reader attempt was stopped because unordered xz
  seeks repeatedly decompressed the stream. Sequential archive reads completed
  the subsequent verification. This was offline evidence processing and did
  not repeat any benchmark workload.

The successful CUDA stage took 303.834 seconds including compilation and all
checks; scalar acceptance took 79.939 seconds; calibration took 12.024 seconds.
These are execution-budget observations, not algorithm latency comparisons.

## Evidence, reproduction and release

The [portable evidence package](../evidence/whole-task-md-linux-20260915/README.md)
retains **all 11,453 exported files**, including failures, full inputs/outputs,
sources, dependency/build trees, native products, command logs, GPU XML,
resource/PID observations and receipts. Identical bytes are stored once:
3,541 contents represent 506,793,142 logical bytes in a 19,060,020-byte archive.

* Portable package SHA256: `9485945c0cc56dcd30217c68ac588fce1a6a45860bebef664b3cc2f62fd866de`.
* Original host archive SHA256: `9df30f37df8e103e470f2d98446494979e04b88a77e1a64ef6f4e1985019eebb`.
* Original manifest SHA256: `6d1ac476b0784a2fb089a3ab441e13253e0223c9c2758ccf191d6bdcf5b8672c`.

From the repository root, Python 3.10+ with the standard library can run:

```text
python -B PublicBenchmarks/WholeTaskMolecularDynamics/Scripts/verify_evidence.py Docs/evidence/whole-task-md-linux-20260915/evidence.tar.xz --output verification.json
```

Use a new output filename; the verifier preserves existing receipts. CI runs
the same verification plus the 32 small validation-contract unit tests.
The recorded local result is [verification.json](../evidence/whole-task-md-linux-20260915/verification.json).

Each heavy remote stage held the actual campaign flock and checked disk/memory,
affinity, wall and growth bounds. Final export reacquired inode 31152802564,
found all 1,480 recorded PID/start references gone and no active process in the
three checkouts, then rehashed every exported file. The coordinator independently
repeated the process/lock/archive check at 2026-09-15T10:48:09.354668Z. No server
shutdown, shared cache change or artifact deletion occurred. This grant's remote
work is closed; the hardware handoff does not label the overall task complete.

## Remaining whole-task acceptance

1. Implement and validate the actual SUMMIT GPU force/state consumer and its
   buffer/fence lifetime; the [ownership audit](QUERY_BUFFER_LIFETIME_AUDIT.md)
   describes required completion before reuse.
2. Establish the chosen SUMMIT Linux/graphics route or explicitly revise the
   investigation's scope. Native CUDA controls do not validate Unity/HLSL.
3. Obtain qualified monitoring under the applicable resource allocation, then
   review and freeze a fair complete-task protocol with applicable candidates.
4. Run independently ordered confirmation blocks under a new valid hardware
   grant. Formal speedup, confidence intervals, device phase timing, true peak
   host/VRAM usage and deployment acceptance remain **unavailable**.
