using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuPrimitives
{
    // Owns only candidate scratch. Existing storage remains available for explicit
    // Portable fallback, histogram and compaction. Its bytes are reported separately.
    internal sealed class GpuPrimitiveCandidateRunner : IDisposable
    {
        internal readonly GpuPrimitiveCandidate Candidate;
        internal readonly int ObservedWaveSize;
        internal long ScratchBytes { get; private set; }
        private readonly ComputeShader shader;
        private readonly int scan, add, reduce, histogram, scatter;
        private readonly List<GraphicsBuffer> owned = new List<GraphicsBuffer>();
        private GraphicsBuffer[] sums, offsets;
        private GraphicsBuffer keys, values, hist, histOffsets;

        internal GpuPrimitiveCandidateRunner(string id, int capacity)
        {
            Candidate = GpuPrimitiveCandidates.Get(id);
            if (!GpuPrimitiveCandidates.TryProbeWaveSize(id, out int observed, out string reason))
                throw new NotSupportedException(id + ": " + reason);
            ObservedWaveSize = observed;
            shader = Resources.Load<ComputeShader>(Candidate.Resource);
            scan = shader.FindKernel("Scan"); add = shader.FindKernel("Add");
            reduce = shader.FindKernel("Reduce"); histogram = shader.FindKernel("Histogram"); scatter = shader.FindKernel("Scatter");
            Allocate(capacity);
        }
        internal void Allocate(int capacity)
        {
            Dispose();
            try
            {
                int histogramCount = Groups(capacity) * Candidate.BinCount;
                keys = Buffer(capacity); values = Buffer(capacity);
                hist = Buffer(histogramCount); histOffsets = Buffer(histogramCount);
                int n = Math.Max(capacity, histogramCount), levels = 0;
                do { levels++; n = Groups(n); } while (n > 1);
                sums = new GraphicsBuffer[levels]; offsets = new GraphicsBuffer[levels];
                n = Math.Max(capacity, histogramCount);
                for (int i = 0; i < levels; i++)
                { n = Groups(n); sums[i] = Buffer(n); if (n > 1) offsets[i] = Buffer(n); }
            }
            catch { Dispose(); throw; }
        }
        private GraphicsBuffer Buffer(int count)
        {
            var b = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(1, count), 4);
            owned.Add(b); ScratchBytes += Math.Max(1, count) * 4L; return b;
        }
        private int Groups(int n) => GpuPrimitiveCandidate.Divide(n, Candidate.TileSize);
        private void Bind(CommandBuffer cmd, int kernel, string name, GraphicsBuffer b) =>
            cmd.SetComputeBufferParam(shader, kernel, name, b);
        internal void Scan(CommandBuffer cmd, GraphicsBuffer input, GraphicsBuffer output, int count)
        {
            if (count == 0) return;
            ScanLevel(cmd, input, output, count, 0);
        }
        private void ScanLevel(CommandBuffer cmd, GraphicsBuffer input, GraphicsBuffer output, int n, int level)
        {
            int groups = Groups(n);
            cmd.SetComputeIntParam(shader, "_Count", n);
            Bind(cmd, scan, "_ScanInput", input); Bind(cmd, scan, "_ScanOutput", output);
            Bind(cmd, scan, "_BlockSums", sums[level]);
            cmd.DispatchCompute(shader, scan, groups, 1, 1);
            if (groups == 1) return;
            ScanLevel(cmd, sums[level], offsets[level], groups, level + 1);
            cmd.SetComputeIntParam(shader, "_Count", n);
            Bind(cmd, add, "_ScanOutput", output); Bind(cmd, add, "_BlockOffsets", offsets[level]);
            cmd.DispatchCompute(shader, add, groups, 1, 1);
        }
        internal void Reduce(CommandBuffer cmd, GraphicsBuffer input, GraphicsBuffer output, int count)
        {
            int level = 0;
            do
            {
                int groups = Math.Max(1, Groups(count));
                GraphicsBuffer target = groups == 1 ? output : sums[level];
                cmd.SetComputeIntParam(shader, "_Count", count);
                Bind(cmd, reduce, "_ScanInput", input); Bind(cmd, reduce, "_BlockSums", target);
                cmd.DispatchCompute(shader, reduce, groups, 1, 1);
                if (groups == 1) break;
                input = target; count = groups; level++;
            } while (true);
        }
        internal void Sort(CommandBuffer cmd, GraphicsBuffer ki, GraphicsBuffer vi,
            GraphicsBuffer ko, GraphicsBuffer vo, int count, int bits)
        {
            if (count == 0) return;
            int groups = Groups(count), passes = Candidate.RadixPasses(bits);
            for (int pass = 0; pass < passes; pass++)
            {
                bool final = (pass & 1) == ((passes - 1) & 1);
                var targetKeys = final ? ko : keys; var targetValues = final ? vo : values;
                cmd.SetComputeIntParam(shader, "_Count", count);
                cmd.SetComputeIntParam(shader, "_RadixShift", pass * Candidate.RadixBits);
                cmd.SetComputeIntParam(shader, "_RadixGroupCount", groups);
                Bind(cmd, histogram, "_RadixKeysIn", ki); Bind(cmd, histogram, "_RadixGroupHistograms", hist);
                cmd.DispatchCompute(shader, histogram, groups, 1, 1);
                Scan(cmd, hist, histOffsets, groups * Candidate.BinCount);
                // The recursive scan changes _Count; restore before scatter.
                cmd.SetComputeIntParam(shader, "_Count", count);
                Bind(cmd, scatter, "_RadixKeysIn", ki); Bind(cmd, scatter, "_RadixValuesIn", vi);
                Bind(cmd, scatter, "_RadixKeysOut", targetKeys); Bind(cmd, scatter, "_RadixValuesOut", targetValues);
                Bind(cmd, scatter, "_RadixGroupOffsets", histOffsets);
                cmd.DispatchCompute(shader, scatter, groups, 1, 1);
                ki = targetKeys; vi = targetValues;
            }
        }
        public void Dispose()
        {
            foreach (var b in owned) b.Dispose(); owned.Clear(); ScratchBytes = 0;
        }
    }
}
