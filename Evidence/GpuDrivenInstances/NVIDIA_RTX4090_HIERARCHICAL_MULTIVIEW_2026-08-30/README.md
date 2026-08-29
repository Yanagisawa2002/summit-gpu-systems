# RTX 4090 hierarchical multi-view evidence

This directory retains the compact, reviewable evidence for commit
`f9812cae015eff640f2d82131c180b52349f1746`.

The formal Unity/D3D12 matrix used 1,048,576 instances, four views, 64-instance
contiguous clusters, four frozen visibility cells, two ABBA/BAAB super-rounds,
and 900 samples per block. All 36,000 native timestamp rows were ready, all
flat/hierarchical output and hierarchy-statistic validations passed, and timed
main-thread allocation/readback stayed zero.

The hierarchy reduced candidate instance-view work by 95%, 75%, 25%, and 0%
at 5%, 25%, 75%, and 100% visibility. Native GPU mean improved in all four
cells. The 25% and 100% cells passed every material guardrail; the 5% and 75%
cells failed only enqueue P99. A preceding runtime-identical diagnostic matrix
tail-rejected all four cells, so the API remains explicit and PR7 must validate
selection with calibration/holdout rather than infer a threshold from one run.

Raw 36,000-row frame CSVs and the freshly built Player remain ignored under
`TestResults/` and `Builds/`. The retained summaries, test receipt, device
identity, and hashes are sufficient to audit the reported claims.
