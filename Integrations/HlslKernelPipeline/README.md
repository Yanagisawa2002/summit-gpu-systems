# Verified HLSL scan consumption

The delivered shader is an actual HLSL Kernel Pipeline wave-tiled scan, pinned at
source `274e5077455e6c08959a274a84379dfc4e1345dd`. Its aggregate dependency identity is
`117f226d4f83c9a19155d03f2d6179e55db676e19fc58c76ccb1aaef836b6f16`.
`Artifact~/consumer-manifest.json` binds the exact wrapper, transitive header and
license bytes. `Artifact~` is outside portable Unity Assets to avoid importing an
SM6.6 asset into unsupported Unity importers. Performance is **Unmeasured**.

`HlslScanArtifact.Verify` reads the installed files and compares their hashes and
aggregate against app-owned pins. Incoming profiles cannot supply their own proof.
The optional Editor factory obtains the `ComputeShader` from the verified source
path. The profile bridge revalidates through the real HLSL SDK with exact device and
driver policy, confirmed nonhistorical evidence, workload/ABI/kernel/manifest and
deployment identities, then requires the exact ten defines of the fixed variant.
The app-owned profile-to-asset catalog mapping is an additional requirement. There
is no measured profile for this new Unity consumer in this delivery; explicit
Unmeasured selection is supported without inventing one.

The raw recorder binds `Input0=input`, `Output0=output`, `Output1=scratch`, after
resetting scratch through the reset kernel's `Output0`. It records reset then scan
on the same queue; no execution/readback occurs inside the recorder. Capacity
allocates `8 + 12 * ceil(capacity/4096)` bytes; each invocation resets its actual
logical partition count. Empty scans record no commands and preserve sentinels.
Raw/Structured shape mismatches and aliases fail before recording.

Existing SUMMIT consumers use Structured uint buffers. `GpuHlslStructuredScanBridge`
explicitly copies Structured→Raw, records the real reset/scan, and copies Raw→Structured.
Both copies, raw buffers and external scratch count toward total consumer cost.
Inject the bridge through `new GpuPrimitives(capacity, externalScan: bridge)`;
public `RecordExclusiveScan(..., Auto)` then records that real external path. Without
injection, resolution is unchanged. Explicit Portable/WaveOps calls keep their
implementations. The bridge and its raw consumer are caller-owned and must outlive
all ordered GPU work; they are disposed after completion, separately from the
`GpuPrimitives` object. Grow by rebuilding the external bridge outside recording.

`GpuHlslScanConsumer.TryCreate` returns an explicit reason for missing source,
profile/device/driver mismatch, absent shader, unsupported SM6.6/Wave32 import or
wrong compiled thread shape. The host can retain ordinary SUMMIT primitives on
failure. Unity 6000.5.2f1 import/execution support is **not validated**; DXC cs_6_6
success does not prove Unity import. No capability probe dispatch is used.

HLSL compaction's value-mask predicate does not match SUMMIT's separate nonzero
predicate buffer. Compaction and sort are not mapped under the scan identity.
The original native primitive benchmarks remain in the HLSL repository; this
adapter supplies Unity consumption and does not turn kernel measurements into
scene/frame performance claims. The optional integration assembly requires that
repository's `com.edwinliu.hlslperf-profile` package.
