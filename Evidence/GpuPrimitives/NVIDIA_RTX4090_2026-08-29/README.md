# NVIDIA RTX 4090 GPU primitive evidence

Formal source commit: `bb98dbb7c44428ce6dccf3d6327f2ecbd3be7ffc`

This directory retains compact device metadata, correctness rows, block/operation summaries, paired deltas, and run status. `raw-frames.csv`, Player binaries, Unity logs, build output, and session files containing machine-local absolute paths remain excluded from Git.

Excluded session artifact hashes are preserved without copying their path-bearing contents:

- `config.json`: `A5D55327A888CBCA46BA55C606DE8C2D4450D2C30065052FD96F53FDFA7749ED`
- `quality-summary.txt`: `1D0D77C37B73782C81DB8604A2069B8EB19932CFC9EB5EF6835DC2FB10031F21`
- `runner-config.json`: `B483ABD129BEAC19904773A2698535B961136178B3F12FE036BACEDD9C7AFDCC`

See `../../../Docs/GPU_PRIMITIVES_NVIDIA_RTX4090_FORMAL_RESULTS_2026-08-29.md` for the reviewed interpretation.
