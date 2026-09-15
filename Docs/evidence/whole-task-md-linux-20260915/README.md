# Complete Linux molecular dynamics evidence

See [results and limitations](../../whole-task-md-20260915/LINUX_5090_NUMERICAL_RESULTS.md).

`evidence.tar.xz` contains all 11,453 files from the verified host export. It
stores 3,541 unique byte strings as `blobs/<sha256>`. `index.json` maps every
original logical path, size and mode to one blob. This preserves full copies
of failed attempts, inputs, output state/CSR files, logs, source/dependency
trees and native build products while removing duplicate byte storage.
Git metadata and mutable runtime caches remain preserved on the host and
are listed as excluded directories in the original export manifest.

| Artifact | SHA256 |
| --- | --- |
| Portable archive (19,060,020 bytes) | `9485945c0cc56dcd30217c68ac588fce1a6a45860bebef664b3cc2f62fd866de` |
| Original host archive (125,351,284 bytes) | `9df30f37df8e103e470f2d98446494979e04b88a77e1a64ef6f4e1985019eebb` |
| Original file manifest | `6d1ac476b0784a2fb089a3ab441e13253e0223c9c2758ccf191d6bdcf5b8672c` |

The original archive remains in the task's local ignored artifacts and on the
host. The portable archive retains every exported file, so verification does
not require access to that host. These are observed execution records, not a
signed attestation or a claim that an arbitrary toolchain reproduces the build.

From the repository root:

```text
python -B PublicBenchmarks/WholeTaskMolecularDynamics/Scripts/verify_evidence.py Docs/evidence/whole-task-md-linux-20260915/evidence.tar.xz --output verification.json
```

Python 3.10+ and its standard library are sufficient. This reads all hashes,
complete saved numerical data and build input/product manifests; recomputes
all seven scalar states; and reproduces the rejected monitoring qualification.
It executes no stored binary and starts no benchmark. The original costly
all-pairs scalar membership checks remain bound to their remote receipts.

To inspect a logical file without extracting executable files:

```python
import sys
from pathlib import Path
sys.path.insert(0, "PublicBenchmarks/WholeTaskMolecularDynamics/Scripts")
from verify_evidence import Snapshot
snapshot = Snapshot(Path("Docs/evidence/whole-task-md-linux-20260915/evidence.tar.xz"))
print(snapshot.file("gen2-cuda-r4/records/cuda-r4/result.json").read_text())
```

Key logical locations inside the archive:

| Prefix | Contents |
| --- | --- |
| `gen2-r1/records/` | Capability, successful Serial/OpenMP gates, first CUDA failure |
| `gen2-cuda-r2/records/` | Retained admission/metadata recovery records and CUDA r3 failure |
| `gen2-cuda-r4/records/` | Corrected staging receipt, successful CUDA/scalar gates, calibration rejection |
| Each `checkout/Artifacts/whole-task-md-20260915/inputs/` | Hash-identical canonical inputs and goldens |
| Each `checkout/Artifacts/whole-task-md-20260915/runs/` | Per-process settings, raw measurements, GPU XML and full first/last outputs |
| `gen2-cuda-r4/checkout/Artifacts/whole-task-md-20260915/scalar-r4-*/` | Independent scalar state/receipts |
| Same prefix, `overflow-r4-128/` and `overflow-r4-256/` | Actual bounded-capacity rejection evidence |

Adjacent JSON/log files provide the offline verification result, exact CPU
quiet-gate rejection, scalar component errors, download/export checks and
source validation. `preparation-logs/` retains local staging observations that
preceded or accompanied the remote records. The original mistaken r4 staging
hash field remains in the archive alongside its separately verified correction.
