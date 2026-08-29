# GPU-driven hierarchical multi-view culling benchmark

Commit: `cf5b21b557356b522c920d591844d73ebf4299f5`  
Mode: `hierarchical-culling`; layout: `spatial-clustered-multiview-64-v2`.  
Protocol: same-process paired ABBA/BAAB, native D3D12 timestamps, 900 samples per block.  
Correctness: CPU oracle before and after measurement; no timed readback; zero main-thread allocated bytes in every sampled row and block.
Allocation evidence: 36000 raw rows, 0 allocation rows, 0 allocated bytes.  
Material gate: paired median >= 1% with every pair positive, native GPU P95 non-regression, and no more than 5% regression in frame P99 or enqueue P99.

| Visible | Flat visible-only mean (ms) | Hierarchical visible-only mean (ms) | Mean speedup | Paired median | GPU P95 speedup | Frame P99 regression | Enqueue P99 regression | Pair range | Decision |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|:---|
| 5% | 0.3423 | 0.2550 | 25.51% | 25.28% | 32.49% | -13.20% | 20.78% | 24.45% to 27.00% | regression-or-unstable |
| 25% | 1.0696 | 0.8057 | 24.67% | 24.49% | 18.71% | -17.41% | 10.05% | 24.38% to 25.32% | regression-or-unstable |
| 75% | 2.1423 | 2.0364 | 4.94% | 4.89% | 3.56% | -3.36% | 8.63% | 4.70% to 5.28% | regression-or-unstable |
| 100% | 2.6445 | 2.5155 | 4.88% | 4.88% | 3.29% | -3.78% | 10.30% | 4.83% to 4.94% | regression-or-unstable |

| Visible | Coarse cluster-view pairs | Candidate instance-view pairs | Visible pairs | Candidate reduction vs flat |
|---:|---:|---:|---:|---:|
| 5% | 63876 | 209715 | 209715 | 95.00% |
| 25% | 65532 | 1048576 | 1048576 | 75.00% |
| 75% | 65532 | 3145728 | 3145728 | 25.00% |
| 100% | 65536 | 4194304 | 4194304 | 0.00% |

Decision: do not select hierarchical culling by default; this matrix did not show a material, consistently positive paired result. The explicit hierarchical API remains available.
