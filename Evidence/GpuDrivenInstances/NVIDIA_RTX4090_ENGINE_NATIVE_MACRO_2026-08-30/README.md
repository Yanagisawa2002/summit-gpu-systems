# Engine-native macrobenchmark retained evidence

This directory retains compact evidence for the formal RTX 4090 CPU versus
GPU-driven instance matrix at tested commit
`f7178c8f038deed3c93c7fc094339ec0e40715c0`.

- `matrix-summary.csv`: all retained timing, payload, and gate metrics.
- `scenario-quality.csv`: process IDs and per-scenario completeness gates.
- `validation.csv`: warm-up/final CPU/GPU result and image parity.
- `device.json`: measured device and native timestamp capabilities.
- `runner-receipt.json`: source, test, Player, and evidence hashes.

The formal evidence is valid, but the overall performance decision is
`NO-GO`: `3/4` cells passed the CPU threshold and `3/4` passed the native GPU
P99 guardrail. Raw frame rows and the generated Player remain ignored. See
`Docs/GPU_DRIVEN_INSTANCES_ENGINE_NATIVE_MACRO_RTX4090_FORMAL_2026-08-30.md`
for interpretation and claim boundaries.
