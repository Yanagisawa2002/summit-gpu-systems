# R9700 adaptive runtime calibration vNext

## Implemented contract

This protocol actually times `GpuAdaptiveSpatialBinner.RecordAdaptive`, including
CPU feature gathering, input upload, selector/state update, command recording and
submission, plus the full binning GPU region with native D3D12 timestamp queries.
The same resident facade and buffers execute forced Direct and Radix references.
Both backends remain allocated; switching needs no scratch allocation or resource
migration. Selection returns a reason and whether the backend changed.

Matrix schema v3 stores an explicit workload/N/C/concentration/occupancy tuple and
primitive candidate ID, validation status, sample count and evidence identifier.
It accepts any number of exact cells up to 65,536, including general and hotset
cells. There are no wildcard or interpolated matches. Invalid documents and
unknown/unvalidated cells fall back to Direct. Untrusted keys always use Direct's
validated path. Hysteresis delays Radix promotion for three consecutive same-cell
observations; fallback is immediate. CPU and GPU tests cover transitions, partial
workgroups, missing evidence, untrusted invalid keys and independent CSR equality.

Compatibility is enforced using an immutable snapshot of hardware/API/graphics
version, independent OS driver version and Unity/compiler/shader/build identity.
The runner identifies exactly one R9700 via `Win32_VideoController`; ambiguous or
missing driver information fails. The Player build identity is a SHA-256 digest of
its file contents, and shader identity covers the compiled asset containers. The
compiler identity includes the UnityShaderCompiler binary digest and target/build
configuration. SkipBuild rehashes the Player and refuses changed contents. These
values come from the installed build and OS, not the matrix. `sourceCommit` alone
has no authority. Legacy adaptive v2 runtime calls fall back to Direct; legacy
primitive autotune v1 and identity-free APIs fall back to Auto.

The matrix and autotune resolver support opaque candidate IDs. Current adaptive
wrappers actually construct legacy Portable/WaveOps primitives, and so only those
IDs can authorize their selections. New primitive candidate IDs are rejected until
wrapper construction explicitly binds the same candidate. Candidate catalogue and
capability checks are owned by the primitive task; no unmeasured variant is promoted.

## Measurement and evidence limits

The deterministic trace is uniform → single-bin → uniform → hotset4 → single-bin →
hotset16. `r9700-exact-histogram-v1/*` fixtures preserve exact histograms while an
unseen seed shuffles input order. This extends only those explicit workload
identities and histogram cells, not arbitrary distributions with a similar label.
Fixture generation/CPU oracle construction happen outside timed regions. Every
sample rescans its actual CPU input, uploads the same keys/values, and records the
operation again. CPU feature scratch, fixture arrays, shared buffers and union GPU
scratch are separately reported. GPU-only feature acquisition is not implemented
or claimed: external GPU producers need current evidence or Untrusted fallback.

Raw records retain native begin/end/elapsed ticks, frequency, token, flags, device
generation, source/result frame and completion fence. Three empty-scope controls
run before and after the workload trace. Native instrumentation reads 16 bytes per
sample, reported separately. Correctness CSR readback occurs only in warmup and
at segment boundaries, outside measurement; workload measurement readback is zero.
Each observed timestamp must be complete and internally valid. Independent CPU
oracles validate counts, terminal offsets, canonical per-bin membership and flags.

This first runtime harness drains each native timestamp before the next input
submission. It measures sequential dispatch latency and dynamic selection, not
saturated throughput or a steady multi-frame queue. Boundary readbacks and warmup
can affect caches; formal conclusions must retain this protocol limitation. CPU
and GPU durations overlap and must not be added as if they were a measured frame
latency. Timer and timestamp instrumentation overhead remain visible in controls.

Discovery uses Direct/Radix/Radix/Direct order. Evaluation uses
Direct/Radix/Adaptive/Adaptive/Radix/Direct, with identical input trace per repeat.
The frozen selector never trains on evaluation samples. The summary records
Adaptive-minus-selected-forced CPU recording/GPU deltas at the same repeat,
segment, observation and exact feature cell, and distinguishes switched samples.
These deltas include measurement noise and are not a causal isolated kernel switch
cost. `switchStateCpuMs` measures only the selector's state maintenance.

## Commands

All commands below run from the assigned worktree. PowerShell 7 and Python 3
stdlib are used. Supply the same shared validation lock to every Unity/GPU runner.
The runner holds it until the child process exits. Keep reports outside the
immutable Player directory. It refuses to overwrite a prior report/frozen matrix.

```powershell
$lock = 'C:/Users/EdwinLiu/Documents/Codex/2026-09-07/w-m/work/r9700-vnext/Invoke-SerializedValidation.ps1'

# Authorized short correctness/instrumentation smoke. Synthetic selections
# exercise switching and can NEVER be frozen as performance evidence.
./Tools/Run-GpuAdaptiveRuntimeBenchmark.ps1 -Phase smoke -ValidationLockScript $lock `
  -OutputDirectory ./Reports/AdaptiveRuntime/smoke

# CPU-only freezer/evaluation contract regressions.
python ./Tools/Tests/Test_AdaptiveRuntimeCalibration.py
```

Prepared comparison matrix (run after integration, not as part of this task's
formal performance evidence):

```powershell
# One immutable Player, explicit N/C cells, no evaluation inputs used for selection.
$first = $true
$discovery = @()
foreach ($n in @(262144, 1048576)) {
  foreach ($c in @(16, 256, 4096)) {
    $out = "./Reports/AdaptiveRuntime/discovery-n$n-c$c"
    $run = @{Phase='discovery'; ValidationLockScript=$lock; ElementCount=$n;
      BinCount=$c; FramesPerSegment=30; Repeats=2; Seed=9701; OutputDirectory=$out}
    if (!$first) { $run.SkipBuild = $true }
    ./Tools/Run-GpuAdaptiveRuntimeBenchmark.ps1 @run
    $first = $false
    $discovery += "$out/runtime-report.json"
  }
}
python ./Tools/AdaptiveRuntimeCalibration.py freeze --reports $discovery `
  --output ./Reports/AdaptiveRuntime/frozen-matrix.json

foreach ($n in @(262144, 1048576)) {
  foreach ($c in @(16, 256, 4096)) {
    $out = "./Reports/AdaptiveRuntime/evaluation-n$n-c$c"
    ./Tools/Run-GpuAdaptiveRuntimeBenchmark.ps1 -Phase evaluation -SkipBuild `
      -ValidationLockScript $lock -ElementCount $n -BinCount $c `
      -FramesPerSegment 60 -Repeats 2 -Seed 9702 -OutputDirectory $out `
      -MatrixPath ./Reports/AdaptiveRuntime/frozen-matrix.json
    python ./Tools/AdaptiveRuntimeCalibration.py summarize --report "$out/runtime-report.json" `
      --matrix ./Reports/AdaptiveRuntime/frozen-matrix.json --output "$out/comparison.json"
  }
}
```

The freezer requires complete successful discovery traces, pre/post controls,
independent identities, validated native samples and at least 12 samples per
candidate per exact cell. It rejects duplicated runs and mixed devices/builds.
Radix needs at least 1% median improvement with no more than 2% P99 regression;
otherwise the validated Direct baseline is retained. Thresholds and source report
hashes are frozen into the matrix. These are calibration rules, not a formal claim
of statistical significance. Evaluation rejects any reused calibration seed and
must name the byte-identical frozen matrix. Short protocol smoke data are not
production calibration and no runtime profile is shipped as an enabled default.

## Deliberately unmeasured

The integrated full-size R9700 comparison matrix, saturated-throughputput behavior,
GPU-generated feature costs, other devices/APIs and optional direct-binning wave
aggregation are not measured here. No new performance win is claimed. Wave
aggregation was deferred to keep this change bounded around calibration,
compatibility, measured runtime selection and correctness.
