using System;
using System.Collections.Generic;

// These in-memory doubles have no device/queue/execution methods or native imports.
// Production hosts also compile against real Unity in SensorRuntimeCompile.csproj.
namespace UnityEngine
{
    public class Object
    {
        public static T Instantiate<T>(T original) where T : Object => (T)(Object)new ComputeShader();
        public static void Destroy(Object value) { }
        public static void DestroyImmediate(Object value) { }
    }
    public static class Application { public static bool isPlaying => false; }
    public static class Resources
    {
        public static bool Available = true;
        public static T Load<T>(string path) where T : Object => Available ? (T)(Object)new ComputeShader() : null;
    }
    public sealed class ComputeShader : Object
    {
        private readonly List<string> names = new List<string>();
        internal readonly Dictionary<(int, string), GraphicsBuffer> Buffers = new Dictionary<(int, string), GraphicsBuffer>();
        public int FindKernel(string name) { if (!names.Contains(name)) names.Add(name); return names.IndexOf(name); }
        public bool IsSupported(int kernel) => true;
        public string KernelName(int kernel) => names[kernel];
        public void SetBuffer(int kernel, string name, GraphicsBuffer buffer) => Buffers[(kernel, name)] = buffer;
    }
    public sealed class GraphicsBuffer : IDisposable
    {
        [Flags] public enum Target { Structured = 1, IndirectArguments = 2, Raw = 4 }
        public static int LiveBuffers;
        public readonly Target target;
        public readonly int count, stride;
        public bool IsDisposed { get; private set; }
        public GraphicsBuffer(Target target, int count, int stride)
        {
            if (count < 1 || stride < 1) throw new ArgumentException("Invalid allocation.");
            this.target = target; this.count = count; this.stride = stride; LiveBuffers++;
        }
        public void SetData(Array value)
        {
            if (value.Length > count) throw new ArgumentException("Upload exceeds buffer.");
        }
        public void Dispose() { if (IsDisposed) return; IsDisposed = true; LiveBuffers--; }
    }
}
namespace UnityEngine.Rendering
{
    using UnityEngine;
    public sealed class CommandBuffer
    {
        public sealed class Record
        {
            public string Kernel;
            public int X, Y, Z;
            public GraphicsBuffer Indirect;
            public uint Offset;
            public Dictionary<string, int> Integers;
            public Dictionary<string, GraphicsBuffer> Buffers;
        }
        private readonly Dictionary<(ComputeShader, string), int> integers = new Dictionary<(ComputeShader, string), int>();
        private readonly Dictionary<(ComputeShader, int, string), GraphicsBuffer> buffers = new Dictionary<(ComputeShader, int, string), GraphicsBuffer>();
        public readonly List<Record> Records = new List<Record>();
        public void SetComputeIntParam(ComputeShader shader, string name, int value) => integers[(shader, name)] = value;
        public void SetComputeBufferParam(ComputeShader shader, int kernel, string name, GraphicsBuffer value) => buffers[(shader, kernel, name)] = value;
        public void BeginSample(string name) { }
        public void EndSample(string name) { }
        public void DispatchCompute(ComputeShader shader, int kernel, int x, int y, int z) => Add(shader, kernel, x, y, z, null, 0);
        public void DispatchCompute(ComputeShader shader, int kernel, GraphicsBuffer args, uint offset) => Add(shader, kernel, 0, 0, 0, args, offset);
        private void Add(ComputeShader shader, int kernel, int x, int y, int z, GraphicsBuffer args, uint offset)
        {
            var record = new Record { Kernel = shader.KernelName(kernel), X = x, Y = y, Z = z, Indirect = args,
                Offset = offset, Integers = new Dictionary<string, int>(), Buffers = new Dictionary<string, GraphicsBuffer>() };
            foreach (var pair in integers) if (pair.Key.Item1 == shader) record.Integers[pair.Key.Item2] = pair.Value;
            foreach (var pair in shader.Buffers) if (pair.Key.Item1 == kernel) record.Buffers[pair.Key.Item2] = pair.Value;
            foreach (var pair in buffers) if (pair.Key.Item1 == shader && pair.Key.Item2 == kernel) record.Buffers[pair.Key.Item3] = pair.Value;
            foreach (var buffer in record.Buffers.Values) if (buffer == null || buffer.IsDisposed) throw new InvalidOperationException("Dead binding.");
            Records.Add(record);
        }
    }
}
namespace Summit.GpuSensorPipeline
{
    public static class GpuSensorPipeline { public const int FixedBinCount = 262144; }
    public static class GpuSensorChunkedRangeQuery { public static bool SupportsWaveOperations = true; }
}
