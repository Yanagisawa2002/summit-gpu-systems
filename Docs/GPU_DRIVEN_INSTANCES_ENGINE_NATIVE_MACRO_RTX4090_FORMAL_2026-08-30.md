# Engine-native GPU-driven instances — RTX 4090 formal result

## Outcome

Commit `f7178c8f038deed3c93c7fc094339ec0e40715c0` adds an asset-free
Unity macrobenchmark comparing a Burst/Jobs CPU culler plus
`DrawMeshInstanced` against the package's visible-only GPU culler plus
`DrawMeshInstancedIndirect`.

The evidence is valid, but the frozen overall decision is **NO-GO**. Three of
four cells passed both the CPU-submission threshold and the native-GPU P99
guardrail. The smallest `10K / 1 view / 1 group` cell narrowly missed both:
its CPU P95 saving was `0.1919 ms` rather than the required `0.20 ms`, and its
native GPU P99 rose by `0.003072 ms`, which is small in absolute terms but
`8.11%` relative to the `0.037888 ms` baseline.

| Workload | CPU submission P95 CPU/GPU | Reduction | Native GPU P99 CPU/GPU | GPU delta | Cell gates |
|:---|---:|---:|---:|---:|:---|
| 10K, 1 view, 1 group | 0.2546 / 0.0627 ms | 75.37%, 0.1919 ms | 0.0379 / 0.0410 ms | +8.11% | miss / miss |
| 100K, 1 view, 1 group | 1.7963 / 0.0729 ms | 95.94%, 1.7234 ms | 0.3195 / 0.1331 ms | -58.33% | pass / pass |
| 10K, 4 views, 8 groups | 0.5756 / 0.0968 ms | 83.18%, 0.4788 ms | 0.1137 / 0.0532 ms | -53.15% | pass / pass |
| 100K, 4 views, 8 groups | 5.2686 / 0.0842 ms | 98.40%, 5.1844 ms | 2.0357 / 0.6974 ms | -65.74% | pass / pass |

The accepted claim is therefore scoped: GPU-driven culling and indirect
submission remove substantial CPU submission cost for higher-cardinality or
multi-view work, while fixed dispatch/indirect overhead is not yet protected
for the smallest single-view cell.

## CPU and frame-tail evidence

The CPU reference is not a per-object managed loop. It uses Burst jobs,
persistent native arrays, stable per-chunk histogram/prefix/scatter, and
engine-native instanced draws capped at 1,023 matrices per call. The GPU path
records one portable visible-only pipeline and one indirect draw per visible
bin.

- CPU submission P95 improved in all four cells by `75.37%–98.40%`.
- The predeclared CPU gate passed `3/4` cells because the smallest cell missed
  the absolute `0.20 ms` floor by `0.0081 ms`.
- CPU total/main frame tails were available for all `36,000` rows under the
  documented four-frame `FrameTimingManager` alignment.
- Full-frame GPU timing was available for `30,385/36,000` rows and remains an
  optional diagnostic; it was not substituted for the complete native scope.
- The engine first-submit-to-present window was available for
  `35,979/36,000` rows and remains optional.

The CPU baseline submitted `160 KB`, `1.6 MB`, `640 KB`, and `6.4 MB` of
engine instance-matrix payload per timed frame across the four cells. The GPU
path kept instance state resident and submitted no per-frame engine matrix
payload. Both paths had zero explicit timed buffer upload in this static-state
matrix; dynamic dirty-range upload is intentionally a separate PR.

## Frozen protocol

- Unity `6000.5.2f1`, Windows 11, Direct3D 12 feature level 12.2.
- Intel Core Ultra 7 265K; NVIDIA GeForce RTX 4090; driver `32.0.15.9186`.
- `10K/100K` instances crossed with `1 view / 1 group` and
  `4 views / 8 groups`, at exact seeded `25%` visibility.
- One process per cell; `control-pre; ABBA; BAAB; control-post`.
- `60` case-local warm-up, `900` measured, and `15` cooldown frames per block.
- Four paired CPU/GPU blocks and `3,600` measured samples per variant per cell.
- Native D3D12 begin/end timestamp queries around the exact submitted command
  buffer, with private completion fences.
- CPU total/main frame timing required; render-thread, full-frame GPU, and
  extended submission-window values independently availability-gated.

The frozen decision required at least three CPU cells to improve P95 by both
`20%` and `0.20 ms`, while every cell had to keep native GPU-region P99
regression at or below `5%`. The CPU rule passed `3/4`; the GPU rule passed
`3/4`; therefore the matrix remained NO-GO.

## Correctness and provenance

- Full D3D12 EditMode suite: `584/584` passed, zero skipped.
- Formal scenarios: `4/4` completed.
- Warm-up/final CPU/GPU validations: `16/16` passed with identical result and
  image hashes, zero invalid keys, and zero diagnostic flags.
- Native D3D12 timestamps: `36,000/36,000` ready.
- Required CPU frame-tail rows: `36,000/36,000` ready.
- Timed managed allocation rows/bytes: `0 / 0`.
- Timed benchmark-output readback bytes: `0`.
- Validation readback retries: `0`; validation requests were serialized after
  a discovery run exposed a long-run concurrent-readback limit.
- All block completion fences passed; source hashes and the Player payload
  remained stable; final Git state was clean.
- Source snapshot SHA-256:
  `CA60D6590E92484A2CFBFB465675393231E5EBB45997AFE554081CC2C64924BF`.
- Runtime shader SHA-256:
  `C8F365BC2883FD85C6F9DEE94AD738DA7D2DFD061B785E935FCF2E41421B278D`.
- Macro render shader SHA-256:
  `A686325CC7FB292919FD57841E64916A8B7194BD673F0EA7D3BF1C33CBE80FB6`.
- Combined runtime API SHA-256:
  `9B1AB83CCEA676329822784183AFDC50BC7823CCC5EC22D36ADE0582C86D8F89`.
- Player payload: `283` files, `155,991,977` bytes, SHA-256
  `2D937C60BC9039327FD0910DF6479E60BC3F8822E27B193FBEFEB5F380610878`.

Compact retained evidence is under
`Evidence/GpuDrivenInstances/NVIDIA_RTX4090_ENGINE_NATIVE_MACRO_2026-08-30`.
Raw frames and generated Player files remain ignored under `Reports/` and
`Builds/`.

## Claim boundary and next action

This result establishes a strong procedural, engine-native CPU comparison and
identifies the remaining fixed-overhead cell. It is not SUMMIT performance,
an external GraphicsSamples result, an AMD result, or evidence for dynamic
state updates. The next PR should measure dirty-range state mirroring without
mixing it into this static-state baseline; hierarchical multi-view culling and
automatic policy selection remain separate follow-ups.

## Resume-safe wording

> Built a reusable Unity/D3D12 GPU-driven visibility and indirect-rendering
> system plus a fail-closed engine-native macrobenchmark; across four
> 10K/100K single- and multi-view cells on an RTX 4090, reduced CPU submission
> P95 by 75.4%–98.4%, with 3/4 cells also improving native GPU P99 by
> 53.2%–65.7%; retained a formal NO-GO for the smallest cell's 3.1-microsecond
> GPU overhead, while 36,000/36,000 native timestamps and CPU frame tails,
> exact image/hash parity, zero timed allocation, and zero timed readback
> passed provenance gates.
