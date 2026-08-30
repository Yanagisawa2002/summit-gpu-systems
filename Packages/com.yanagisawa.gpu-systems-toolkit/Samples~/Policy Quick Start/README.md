# Policy Quick Start

This sample demonstrates two public contracts without pretending that
synthetic timings are hardware benchmark evidence:

1. `GpuAutotuneSelector` rejects a fast candidate whose correctness validation
   failed and chooses the best validated candidate within the tail guardrail.
2. `GpuPrimitiveBackendResolver.ResolveMeasuredOrPortable` falls back to
   `Portable` when no exact-device, holdout-accepted profile is available.

To run it:

1. Create or open an empty scene.
2. Create an empty GameObject.
3. Add the `GpuSystemsPolicyQuickStart` component.
4. Enter Play Mode.

The values shown under **Synthetic validated selection** are fixed teaching
data. Use the repository benchmark runners and sealed evidence contract for
real device calibration.
