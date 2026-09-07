using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuPrimitives
{
    /// <summary>Immutable, versioned implementation identity; never an autotuning recommendation.</summary>
    public sealed class GpuPrimitiveCandidate
    {
        public string Id { get; }
        public int Threads { get; }
        public int ElementsPerThread { get; }
        public int RadixBits { get; }
        /// <summary>0 = portable, -1 = device-selected, 32/64 = requested explicit width.</summary>
        public int WaveSize { get; }
        public int TileSize => Threads * ElementsPerThread;
        public int BinCount => 1 << RadixBits;
        internal string Resource { get; }
        internal GpuPrimitiveCandidate(string mode, int threads, int elements, int bits, int wave, string resource)
        {
            Id = $"primitives-v1-{mode}-t{threads}-e{elements}-r{bits}";
            Threads = threads; ElementsPerThread = elements; RadixBits = bits; WaveSize = wave;
            Resource = "GpuPrimitives/Candidate" + resource;
        }
        public int RadixPasses(int keyBits)
        {
            if (keyBits < 1 || keyBits > 32) throw new ArgumentOutOfRangeException(nameof(keyBits));
            return (keyBits + RadixBits - 1) / RadixBits;
        }
        public int ScanDispatches(int count)
        {
            CheckCount(count);
            if (count == 0) return 0;
            int levels = 1;
            while ((count = Divide(count, TileSize)) > 1) levels++;
            return 2 * levels - 1;
        }
        public int ReductionDispatches(int count)
        {
            CheckCount(count);
            int levels = 1;
            while ((count = Divide(count, TileSize)) > 1) levels++;
            return levels;
        }
        public int RadixDispatches(int count, int keyBits = 32)
        {
            CheckCount(count);
            int passes = RadixPasses(keyBits);
            return count == 0 ? 0 : passes * (2 + ScanDispatches(Divide(count, TileSize) * BinCount));
        }
        internal static int Divide(int n, int d) => (n + d - 1) / d;
        private static void CheckCount(int n)
        {
            if (n < 0 || n > GpuPrimitives.MaxElementCount) throw new ArgumentOutOfRangeException(nameof(n));
        }
    }

    public static class GpuPrimitiveCandidates
    {
        // Legacy calibration names are preserved; Auto is a selector, not a frozen identity.
        public const string PortableDefaultId = "Portable";
        public const string WaveDefaultId = "WaveOps";
        private static readonly GpuPrimitiveCandidate[] entries = {
            new GpuPrimitiveCandidate("portable",128,4,4,0,"Portable128x4R4"),
            new GpuPrimitiveCandidate("portable",128,4,8,0,"Portable128x4R8"),
            new GpuPrimitiveCandidate("wave",128,4,4,-1,"Wave128x4R4"),
            new GpuPrimitiveCandidate("wave",256,2,4,-1,"Wave256x2R4"),
            new GpuPrimitiveCandidate("wave32",128,4,4,32,"Wave32R4"),
            new GpuPrimitiveCandidate("wave64",128,4,4,64,"Wave64R4")
        };
        public static IReadOnlyList<GpuPrimitiveCandidate> All { get; } = Array.AsReadOnly(entries);
        public static GpuPrimitiveCandidate Get(string id)
        {
            foreach (var c in entries) if (c.Id == id) return c;
            throw new ArgumentException("Unknown primitive candidate: " + id, nameof(id));
        }
        /// <summary>Read-only import/device check. Does not execute a GPU probe.</summary>
        public static bool IsSupported(string id, out string reason)
        {
            GpuPrimitiveCandidate c;
            try { c = Get(id); } catch (ArgumentException) { reason = "unknown-candidate"; return false; }
            if (!SystemInfo.supportsComputeShaders || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            { reason = "compute-unavailable"; return false; }
            if (c.WaveSize != 0 && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 &&
                SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan)
            { reason = "wave-api-unavailable"; return false; }
            // Compiled import evidence on Unity 6000.5.2f1: WaveSize is rejected
            // by its SM6.0 profile. Experimental~/ExplicitWaves contains actual
            // attributes for a future SM6.6-capable importer; never substitute
            // an automatic-width kernel under an explicit-width identity.
            if (c.WaveSize > 0)
            { reason = "explicit-waves-disabled-unity-sm66-import-not-validated"; return false; }
            var shader = Resources.Load<ComputeShader>(c.Resource);
            if (shader == null) { reason = "shader-resource-missing"; return false; }
            foreach (string k in new[] { "Scan", "Add", "Reduce", "Histogram", "Scatter", "Probe" })
            {
                if (!shader.HasKernel(k) || !shader.IsSupported(shader.FindKernel(k)))
                { reason = "unsupported-imported-kernel:" + k; return false; }
                shader.GetKernelThreadGroupSizes(shader.FindKernel(k), out uint x, out uint y, out uint z);
                if (x != c.Threads || y != 1 || z != 1) { reason = "compiled-thread-group-mismatch"; return false; }
            }
            reason = c.WaveSize > 0 ? "import-supported-runtime-wave-probe-required" : "import-supported";
            return true;
        }
        /// <summary>Untimed initialization evidence, synchronous GPU readback. Never call in Record or timing scopes.</summary>
        public static bool TryProbeWaveSize(string id, out int observed, out string reason)
        {
            observed = 0;
            if (!IsSupported(id, out reason)) return false;
            var c = Get(id);
            if (c.WaveSize == 0) { reason = "portable-no-wave"; return true; }
            var shader = Resources.Load<ComputeShader>(c.Resource);
            using (var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, c.Threads, 4))
            using (var cmd = new CommandBuffer())
            {
                int kernel = shader.FindKernel("Probe");
                cmd.SetComputeBufferParam(shader, kernel, "_Probe", buffer);
                cmd.DispatchCompute(shader, kernel, 1, 1, 1);
                Graphics.ExecuteCommandBuffer(cmd);
                var lanes = new uint[c.Threads]; buffer.GetData(lanes);
                observed = (int)lanes[0];
                if (observed < 4 || observed > 128 || (observed & (observed - 1)) != 0)
                { reason = "invalid-observed-wave-size"; return false; }
                foreach (uint width in lanes) if (width != observed)
                { reason = "inconsistent-observed-wave-size"; return false; }
                if (c.WaveSize > 0 && observed != c.WaveSize)
                { reason = "requested-wave-size-not-selected"; return false; }
            }
            reason = "runtime-probe-verified"; return true;
        }
    }
}
