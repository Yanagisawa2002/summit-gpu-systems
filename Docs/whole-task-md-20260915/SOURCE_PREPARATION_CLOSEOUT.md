# Source preparation after the Linux review

This addendum follows `REVIEW_FOR_LINUX_5090.md`; it does not replace the earlier
audit or turn source inspection into execution evidence.

## Implemented in the draft

* Required canonical inputs/golden files now fail closed, including empty or
  missing directories, wrong dimensions/IDs/radius, malformed CSR, and nonfinite
  state. Required binaries must appear in an observed build's product manifest.
* Build wrappers snapshot source/dependency inputs before and after the actual
  commands, record tools/logs/results, and preserve each build attempt. Unity
  verifies original-to-staged source mapping and rejects unmapped code. The
  source dependency archive and unpacked file manifest are linked to the build.
* Freeze requires an explicit plan, clean candidate commit, unchanged build
  inputs/products/toolchain, and complete-output validation receipts. It
  independently re-reads saved full CSR/state and checks process identity. There
  is no automatic four-arm/64-process confirmation schedule anymore.
* Native code selects Serial, OpenMP, or CUDA. Canonical random initialization
  is Serial-only. Device-layout-preserving host mirrors upload inside the task;
  full audit readbacks follow completed task timing. CPU mirrors alias working
  storage. OpenMP query rows and force/update run in the selected space; the
  conventional grid construction/prefix remain serial.
* The native runner has portable executable paths, bounded requested CPU
  workers and explicit build identity. Missing Linux monitoring blocks formal
  performance. Nonformal runs would be correctness-only until that gap closes.
* Added source definitions for zero-neighbour and partial-query-batch fixtures.
  The extracted upstream force/update expressions remain unchanged.

## Lightweight validation performed

Python 3.10 syntax parsing (12 files), PowerShell source parsing, CLI help import
checks, and 12 small filesystem/extraction regression tests passed locally. These tests use
synthetic files and check missing/changed artifacts, staged-source identity,
ambiguous source anchors, and byte-preserved force/update extraction. They do
not compile or execute the molecular-dynamics task.
The command output and source hashes are saved under
`Artifacts/whole-task-md-20260915/source-preflight-portable/` (`tests.log`,
`checks.json`, and `powershell.log`).

The first filesystem-test pass had a synthetic fixture path-format mismatch
(Windows backslashes versus normalized manifest slashes). The fixture was
corrected to use the real tool-record normalizer; the subsequent full suite
passed. No numerical result was generated, replaced, or discarded.

`Artifacts/whole-task-md-20260915/generated-portable-preflight/` contains only
the generated header and extraction receipt. The upstream source SHA256 remains
`fc292b286270367980fbb1cc99a4944230138b064d4075c264472b7690828b16`.
The portable generated header SHA256 is
`b96e201c9767253025847b5933248387af4ad14260e6922af659ae644391371b`.
Earlier `generated-preflight/` evidence remains intact.

## Open gates

No dependency download, native/Unity build, remote connection, GPU experiment,
numerical correctness result, diagnosis, or confirmation has been performed by
this task. C++/CUDA API and toolchain compatibility remain unvalidated. The
ordinary GPU control and SUMMIT GPU consumer are scoped but not implemented.
Linux graphics capability and cgroup/GPU noise monitoring remain unverified.
There is no frozen plan and no performance winner or SUMMIT validation claim.

The next executable step requires coordinator assignment of the host/resource
stage after Data Layout's Linux release. The recommended order and native-port
cost boundary are in `PORTABLE_IMPLEMENTATION_SCOPE.md`. This task remains open;
no terminal handoff or hardware-release claim is issued by this addendum.
