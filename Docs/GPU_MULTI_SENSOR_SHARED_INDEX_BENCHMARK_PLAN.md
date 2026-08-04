# Multi-sensor shared GPU spatial-index benchmark

## Claim boundary

This benchmark evaluates reuse of one dynamic GPU-resident CSR index by
multiple sensor-query consumers. It does not model sensor fidelity, ROS
transport, copy-queue overlap, async compute, or city-scene frame time.

The two paths consume the same deterministic point set, identical segmented
query batches, and the same final digest reduction:

```text
A: for each sensor: produce -> count -> scan -> scatter -> query
B: produce -> count -> scan -> scatter -> query sensor 0..N
```

The A path reuses physical benchmark buffers sequentially so the same-process
A/B can alternate without multiplying allocation pressure. Reported
independent-index residency is therefore an isolated deployment projection
from the exact buffer contract, not the benchmark process working set.

## Evidence contract

- Unity 6000.5.2f1, Direct3D 12, native timestamp ABI 2.
- Four super-rounds with ABBA/BAAB ordering: eight adjacent pairs.
- 60 case-local warmup frames and 240 discovery or 900 formal frames.
- 64 changing logical point states per block.
- Native timestamps bracket the complete main-graphics-command-list path.
- Query digests and frame digests must match after every pair.
- Key validation and digest readback occur outside measurement.
- Measurement workload readback is zero bytes per frame.

Frozen AMD workloads:

| Scenario | Elements | Sensors | Queries/sensor | Total queries |
|---|---:|---:|---:|---:|
| `shared-n262144-s4-q32` | 262,144 | 4 | 32 | 128 |
| `shared-n1048576-s4-q64` | 1,048,576 | 4 | 64 | 256 |

The result may support claims about avoided redundant index builds, GPU
interval changes, and logical index residency. It cannot support NVIDIA or
cross-vendor claims until those devices are measured.
