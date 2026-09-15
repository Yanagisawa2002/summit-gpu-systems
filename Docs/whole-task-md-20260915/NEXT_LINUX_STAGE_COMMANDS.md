# Next Linux grant: capability and correctness only

**Prepared commands; the source checkpoint precedes execution.** At 09:21:14 UTC
the coordinator granted SUMMIT generation 2 after independently verifying Data
Layout's owned processes were gone and the actual flock was released. The grant
covers staging, a two-minute capability probe, sources plus Serial build (12 min,
2 GiB growth), Serial correctness (6 min, 0.5 GiB), and OpenMP build/correctness
(12 min, 2 GiB). These granted ceilings override the earlier planning table below.
CUDA is read-only toolchain inspection only; CUDA install/execution, formal
performance and server lifecycle actions remain outside this grant. Each later
block requires its corresponding separate grant.

## Boundaries and identities

Use the coordinator-assigned **new task checkout**, at a committed source
checkpoint. All writable output stays beneath that checkout's
`Artifacts/whole-task-md-20260915/`: archives, dependencies, generated header,
builds, logs, temporary files, inputs and complete outputs. Do not use or modify
Data Layout's checkout, data or environment. Existing isolated Python/.NET may
be read-only references after checking their versions and executable permissions;
.NET is not needed for the native recipes. A missing compiler/toolkit stops its
stage and does not authorize an installer or changes to a shared environment.

The coordinator-reported quota was `2500000 / 100000 = 25` CPU-core seconds per
second, with visible cpuset `0-207`. Re-read the actual controls on admission;
208 visible logical CPUs do not mean 208 available full cores. Use **4 compiler
workers** sequentially for each build and **8 OpenMP workers** for the first
parallel correctness check. The assigned stable affinity must contain at least
8 logical CPUs. Do not borrow Data Layout's single-CPU wrapper for a parallel CPU
claim. No thread-count search or affinity/power/driver changes are requested.

Pinned sources and CUDA acceptance:

| Item | Exact identity / required observation |
| --- | --- |
| ArborX | `375875dfb6b2e7631b1ba599cd26ee5c1e68ab90` |
| Kokkos | `6739bc623081648af9e752b616d9671527922cbf` (4.7.02) |
| Consumer source | SHA256 `fc292b286270367980fbb1cc99a4944230138b064d4075c264472b7690828b16` |
| Native toolchain | Existing Python >=3.10, CMake >=3.22, GCC with C++20, make; observed versions and hashes |
| CUDA target | Explicit `Kokkos_ARCH_BLACKWELL120=ON`; nvcc must actually list `sm_120`. CUDA >=12.8 is the proposed minimum, not a claim that a toolkit is installed. |
| Actual GPU | Coordinator supplies a full GPU UUID; native CUDA must report that same UUID and its actual PID. A successful architecture configure is not a device-execution pass. |

Both external ArborX CUDA and ordinary tiled GPU code exist **as uncompiled
source**. Ordinary tiles are restricted to 128/256 and are not a SUMMIT backend.
The SUMMIT Unity GPU force consumer is **not implemented**; current Unity arms
read full CSR to CPU and consume it there. Linux Unity Player/graphics capability
also remains untested. External CUDA success cannot validate SUMMIT.

## Budget and stop points

These are ceilings and storage reservations, not observed costs. Rough planning
expectation is 15-40 minutes for native preparation/build/correctness plus a
separate scalar check; the actual first build can exceed that estimate. Grant
stages incrementally. Never reserve or launch the sum without review.

| Stage | Total wall ceiling | Additional storage reserve | Stop condition |
| --- | ---: | ---: | --- |
| A: tools/device/observer | 2 min | 0.05 GiB | Missing permissions/counters, ambiguous GPU or unsupported tools; no installs |
| B: pinned archives + extraction | 3 min | 1.5 GiB | Each archive >100 MiB, each expanded tree >512 MiB, source identity mismatch |
| C: Serial build | 9 min | 2 GiB | Build/receipt failure; 4 workers |
| D: original example + canonical export | 3 min | 0.25 GiB | Original example failure or full Serial/grid/brute membership mismatch |
| E: Serial correctness matrix | 9 min | 0.5 GiB | First nonzero exit, timeout or full-state/CSR mismatch |
| F: OpenMP build | 9 min | 2 GiB | Build failure; 4 workers |
| G: 8-worker OpenMP matrix | 9 min | 0.5 GiB | First failure or actual reported worker/concurrency mismatch |
| H: CUDA build | 15 min | 3 GiB | nvcc/architecture/build failure; 4 workers |
| I: CUDA correctness + capacity checks | 20 min | 0.75 GiB | First failure, wrong actual UUID/PID, incomplete CSR/state or wrong overflow result |
| J: independent scalar acceptance | 12 min | 0.25 GiB | First mismatch/timeout; an unfinished matrix remains incomplete |

Keep **10 GiB free data storage beyond the next stage's reserve** and **8 GiB
actual memory headroom**, measured against visible cgroup/host limits. Estimates
are growth caps for coordinator monitoring, not automatic disk quotas. Retain
failed/partial builds and logs; do not delete evidence to recover the budget.
An outer GNU `timeout` bounds the owned stage group, with up to 10 seconds for
termination; the coordinator's host lock must live outside that group. No retry
is automatic. A new attempt needs a separately named/coordinator-assigned stage;
the build/export recipes intentionally refuse to overwrite existing outputs.

## Common setup, inside the assigned checkout and held host lock

```bash
md_scripts=PublicBenchmarks/WholeTaskMolecularDynamics/Scripts
md_run=Artifacts/whole-task-md-20260915
# MD_GPU_UUID and checkout are explicitly supplied by the coordinator.
# MD_PYTHON may be an approved read-only isolated runtime; otherwise use python3.
MD_PYTHON="${MD_PYTHON:-$(command -v python3)}"
MD_CXX="$(command -v g++)"
MD_CMAKE="$(command -v cmake)"
test -n "$MD_GPU_UUID" && test -x "$MD_PYTHON" && test -x "$MD_CXX" && test -x "$MD_CMAKE" || exit 1
export md_scripts md_run MD_GPU_UUID MD_PYTHON MD_CXX MD_CMAKE
```

## A. Read-only tool/device capability and monitor probe

Save stdout/stderr from the entire granted stage. Before building, verify the
checkout commit/clean status, Python/CMake/GCC versions, `make`, real free space,
cgroup memory/CPU controls and GPU ownership. This stage creates only its own
small observation files. An unavailable optional nvcc is reported explicitly;
CPU work may proceed only if the coordinator's grant permits it, and H/I stop.

```bash
"$MD_PYTHON" --version
"$MD_CMAKE" --version
"$MD_CXX" --version
make --version
git status --short
git rev-parse HEAD
df -Pk .
nvidia-smi -i "$MD_GPU_UUID" -q -x
if command -v nvcc; then
  nvcc --version
  nvcc --list-gpu-code
fi
timeout --signal=TERM --kill-after=10s 60s "$MD_PYTHON" -B "$md_scripts/linux_monitor_probe.py" \
  --gpu-uuid "$MD_GPU_UUID" --seconds 6 --output "$md_run/host-probe-r1"
```

The probe retains raw GPU XML, cgroup controls, visible ancestor limits,
affinity/SMT counters, memory and collection overhead. It cannot create a live
calibration or a performance-eligible result. Missing/uninterpretable values
cannot be treated as idle. Actual capability observations remain pending.

## B-D. Sources, Serial build, original example and canonical snapshot

Run separately under B, C and D respectively:

```bash
timeout --signal=TERM --kill-after=10s 180s "$MD_PYTHON" -B "$md_scripts/prepare_hardware.py" --native-only

timeout --signal=TERM --kill-after=10s 540s "$MD_PYTHON" -B "$md_scripts/build_artifact.py" \
  native --backend serial --cxx "$MD_CXX" --cmake "$MD_CMAKE" --jobs 4

timeout --signal=TERM --kill-after=10s 30s "$md_run/native-build/UpstreamMolecularOriginal" && \
  timeout --signal=TERM --kill-after=10s 120s "$md_run/native-build/SummitMolecularNative" export "$md_run/inputs"
```

Only Serial exports canonical inputs. Every other backend reads the identical
bytes; no independently initialized GPU/OpenMP velocities enter a comparison.
Downloaded archive and expanded-source manifests are checked before compilation.
The upstream source hash and generated extraction receipt must match.

## E/G/I. Explicit correctness matrix, not performance

Define the function once; definition itself launches nothing. The two complete
repetitions retain first/last full outputs and every repetition's validation.
Monitor bootstrap records are deliberately performance-ineligible without an
actual calibration, even when numerical correctness passes.

```bash
md_correctness() {
  local md_prefix="$1" md_threads="$2"
  shift 2
  local md_arm md_case
  for md_arm in "$@"; do
    for md_case in boundary-membership isolated-3 tail-129 small-6 discovery-8 original-10 perturbed-10 large-20; do
      timeout --signal=TERM --kill-after=10s 45s "$MD_PYTHON" -B "$md_scripts/run_process.py" \
        "$md_arm" "$md_case" "$md_prefix-$md_case-$md_arm" --warmups 0 --measured 2 \
        --threads "$md_threads" --gpu-uuid "$MD_GPU_UUID" || return $?
    done
  done
}
export -f md_correctness
```

E, only after C/D pass:

```bash
timeout --signal=TERM --kill-after=10s 540s bash -c 'md_correctness cpu-r1 1 arborx grid'
```

F then G, separate grants:

```bash
timeout --signal=TERM --kill-after=10s 540s "$MD_PYTHON" -B "$md_scripts/build_artifact.py" \
  native --backend openmp --cxx "$MD_CXX" --cmake "$MD_CMAKE" --jobs 4

timeout --signal=TERM --kill-after=10s 540s bash -c 'md_correctness omp-r1 8 arborx-openmp grid-openmp'
```

H requires the existing nvcc path/version and actual `sm_120` support. The recipe
uses that nvcc with the pinned Kokkos wrapper and disables FMA contraction. It
does not install CUDA. Stop if host compiler/architecture compatibility fails.

```bash
MD_NVCC="$(command -v nvcc)"
test -x "$MD_NVCC" || exit 1
timeout --signal=TERM --kill-after=10s 900s "$MD_PYTHON" -B "$md_scripts/build_artifact.py" \
  native --backend cuda --cxx "$MD_CXX" --cmake "$MD_CMAKE" --nvcc "$MD_NVCC" --jobs 4
```

I: first prove tiny boundary/empty/tail kernels, then the full matrix. A
compilation or first real device failure stops the loop.
A separate smoke invocation is unnecessary because every run performs full
validation. All arms must report the assigned actual device in their native
execution-space record, not merely accept `CUDA_VISIBLE_DEVICES`.

```bash
md_cuda_correctness() {
  md_correctness cuda-r1 1 arborx-cuda tiled128 tiled256 || return $?
  local md_tile
  for md_tile in 128 256; do
    timeout --signal=TERM --kill-after=10s 45s "$MD_PYTHON" -B "$md_scripts/run_overflow.py" \
      "$md_tile" "overflow-r1-$md_tile" --gpu-uuid "$MD_GPU_UUID" || return $?
  done
}
export -f md_cuda_correctness
timeout --signal=TERM --kill-after=10s 1200s bash -c md_cuda_correctness
```

Capacity acceptance requires each tile's bounded audit count to equal the golden
count and exceed the declared one-ID capacity before allocation/scatter. The
wrapper binds the observed exit, PID, actual GPU UUID, binary/build, canonical
input/CSR and raw report. Other exceptions do not count as expected rejection.

## J. Independent scalar acceptance of both tiles, all complete outputs

Compute each independent oracle once and compare both saved repetitions from
both tiles. Full scalar all-pairs membership is required through 4000 points;
large-20 uses the already fully cross-checked canonical CSR and still checks
every force/velocity/position component. The singular boundary case is CSR-only.

```bash
md_scalar_matrix() {
  local md_case
  local -a md_membership
  for md_case in isolated-3 tail-129 small-6 discovery-8 original-10 perturbed-10 large-20; do
    md_membership=(--full-membership)
    if [ "$md_case" = large-20 ]; then md_membership=(); fi
    timeout --signal=TERM --kill-after=10s 300s "$MD_PYTHON" -B "$md_scripts/scalar_oracle.py" "$md_case" \
      --actual "$md_run/runs/cuda-r1-$md_case-tiled128/output/step-0.state" \
      --actual "$md_run/runs/cuda-r1-$md_case-tiled128/output/step-1.state" \
      --actual "$md_run/runs/cuda-r1-$md_case-tiled256/output/step-0.state" \
      --actual "$md_run/runs/cuda-r1-$md_case-tiled256/output/step-1.state" \
      --output "$md_run/scalar-r1-$md_case" "${md_membership[@]}" || return $?
  done
}
export -f md_scalar_matrix
timeout --signal=TERM --kill-after=10s 720s bash -c md_scalar_matrix
```

The matrix-wide ceiling takes precedence over the per-case ceiling. A timeout
keeps started/partial receipts and makes acceptance incomplete; do not reduce
coverage or change tolerances to finish. Freeze requires these numerical and
per-tile capacity receipts and rechecks their identities/full saved states.

## Deferred: separate live calibration grant, no formal performance

After the coordinator accepts A-J, it may assign a **separate** three-minute,
0.25 GiB monitoring calibration pilot. It is outside the requested first stage:

```bash
timeout --signal=TERM --kill-after=10s 120s "$MD_PYTHON" -B "$md_scripts/run_process.py" \
  tiled128 original-10 monitor-bootstrap-r1 --warmups 0 --measured 400 --gpu-uuid "$MD_GPU_UUID" && \
  timeout --signal=TERM --kill-after=10s 30s "$MD_PYTHON" -B "$md_scripts/monitor_calibration.py" \
    "$md_run/runs/monitor-bootstrap-r1" --output "$md_run/linux-monitor-calibration-r1.json"
```

This fixed pilot can fail duration/coverage, PID mapping, throttling or overhead.
Keep the failure, and obtain a newly scoped stage before another attempt. Offline
fixtures never substitute for live host calibration. Success still does not
start performance: SUMMIT candidate implementation/capability, discovery and an
explicit frozen plan remain separate gates. No terminal handoff is issued here.
