using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuSensorPipeline
{
    public enum GpuSensorIndexExecutionMode { Original = 0, GpuDriven = 1 }
    [Flags]
    public enum GpuSensorIndexRebuildReason
    {
        None = 0, Initial = 1, Forced = 2, Churn = 4,
        Fragmentation = 8, CellCapacity = 16
    }

    /// <summary>
    /// Optional GPU-resident, stable-slot spatial index. Outputs ordinary CSR
    /// ranges containing uint.MaxValue holes, accepted by the sensor consumer.
    /// All records and consumers must execute in order on one queue (or behind
    /// explicit caller fences). No CPU readback, allocation, or frame state in RecordUpdate.
    /// </summary>
    public sealed class GpuSensorIncrementalIndex : IDisposable
    {
        public const int DiagnosticWordCount = 16;
        public const int HolesWord = 2;
        public const int ChangedWord = 3;
        public const int RemovedWord = 4;
        public const int InsertedWord = 5;
        public const int OverflowWord = 6;
        public const int InvalidInputWord = 7;
        public const int RebuildReasonWord = 8;
        public const int ActiveCountWord = 9;
        public const int CsrExtentWord = 10;
        public const int RebuildCountWord = 11;
        public const int IncrementalCountWord = 12;
        public const int ReusedWord = 13;
        public const int InspectedWord = 14;
        public const int NonemptyDispatchesWord = 15;
        public const int RecordedDispatchCount = 13;
        private const int Bins = GpuSensorPipeline.FixedBinCount;
        private readonly ComputeShader shader;
        private readonly GraphicsBuffer previousKeys, nextKeys, positions, reservations;
        private readonly GraphicsBuffer heads, counts, blockSums;
        private readonly GraphicsBuffer changedIds, dispatchArguments;
        private readonly int begin, detect, decide, clear, count, scan, scanBlocks,
            prepare, clearMembers, rebuild, remove, insert, finish;
        private bool disposed;

        public GpuSensorIncrementalIndex(int capacity, int staticSlotCount = 0,
            int churnPermille = 200, int fragmentationPermille = 250,
            GpuSensorIndexExecutionMode executionMode = GpuSensorIndexExecutionMode.Original)
        {
            // Three membership words per slot must fit a 65535-group dispatch.
            if (capacity < 1 || capacity > 256 * 65535 / 3)
                throw new ArgumentOutOfRangeException(nameof(capacity));
            if (staticSlotCount < 0 || staticSlotCount > capacity)
                throw new ArgumentOutOfRangeException(nameof(staticSlotCount));
            if (churnPermille < 0 || churnPermille > 1000)
                throw new ArgumentOutOfRangeException(nameof(churnPermille));
            if (fragmentationPermille < 0 || fragmentationPermille > 1000)
                throw new ArgumentOutOfRangeException(nameof(fragmentationPermille));
            Capacity = capacity;
            if (executionMode != GpuSensorIndexExecutionMode.Original && executionMode != GpuSensorIndexExecutionMode.GpuDriven)
                throw new ArgumentOutOfRangeException(nameof(executionMode));
            ExecutionMode = executionMode;
            StaticSlotCount = staticSlotCount;
            ChurnThreshold = (int)((long)capacity * churnPermille / 1000);
            FragmentationThreshold = (int)((long)capacity * fragmentationPermille / 1000);
            shader = Resources.Load<ComputeShader>("GpuSensorPipeline/GpuSensorIncrementalIndex" +
                (executionMode == GpuSensorIndexExecutionMode.GpuDriven ? "Optimized" : ""));
            if (shader == null) throw new InvalidOperationException("Incremental index shader unavailable.");
            if (executionMode == GpuSensorIndexExecutionMode.GpuDriven)
                shader = UnityEngine.Object.Instantiate(shader);
            begin = shader.FindKernel("BeginUpdate");
            detect = shader.FindKernel("DetectChanges");
            decide = shader.FindKernel("DecideRebuild");
            clear = shader.FindKernel("ClearCounts");
            count = shader.FindKernel("CountMembers");
            scan = shader.FindKernel("ScanCells");
            scanBlocks = shader.FindKernel("ScanBlocks");
            prepare = shader.FindKernel("PrepareCells");
            clearMembers = shader.FindKernel("ClearMembers");
            rebuild = shader.FindKernel("RebuildMembers");
            remove = shader.FindKernel("RemoveMembers");
            insert = shader.FindKernel("InsertMembers");
            finish = shader.FindKernel("FinishUpdate");
            try
            {
                Samples = Buffer(capacity, 16);
                previousKeys = Buffer(capacity);
                nextKeys = Buffer(capacity);
                positions = Buffer(capacity);
                reservations = Buffer(capacity);
                BinOffsets = Buffer(Bins + 1);
                BinnedIds = Buffer(capacity * 3);
                heads = Buffer(Bins);
                counts = Buffer(Bins);
                blockSums = Buffer(Bins / 256);
                Diagnostics = Buffer(DiagnosticWordCount);
                // Initialization only. Subsequent updates have no CPU-side state.
                Diagnostics.SetData(new uint[DiagnosticWordCount]);
                BinOffsets.SetData(new uint[Bins + 1]);
                heads.SetData(new uint[Bins]);
                if (ExecutionMode == GpuSensorIndexExecutionMode.GpuDriven)
                {
                    changedIds = Buffer(capacity);
                    dispatchArguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Raw, 30, 4);
                    // A private shader instance owns immutable index bindings.
                    // Dynamic snapshot/flags/revisions are still recorded per call.
                    foreach (int kernel in new[] { begin, detect, decide, clear, count, scan, scanBlocks,
                        prepare, clearMembers, rebuild, remove, insert, finish })
                        BindOwnedBuffers(kernel);
                }
            }
            catch { Dispose(); throw; }
        }

        public int Capacity { get; }
        public GpuSensorIndexExecutionMode ExecutionMode { get; }
        public int StaticSlotCount { get; }
        public int ChurnThreshold { get; }
        public int FragmentationThreshold { get; }
        public GraphicsBuffer Samples { get; }
        public GraphicsBuffer BinOffsets { get; }
        public GraphicsBuffer BinnedIds { get; }
        public GraphicsBuffer Diagnostics { get; }
        public long ResidentBytes => (long)Capacity * 44 +
            (long)(3 * Bins + 1 + Bins / 256 + DiagnosticWordCount) * 4 +
            (ExecutionMode == GpuSensorIndexExecutionMode.GpuDriven ? (long)Capacity * 4 + 120 : 0);

        /// <summary>
        /// Full external snapshot: uint4 samples and uint activity (0/1), indexed
        /// by stable slot ID, both covering Capacity. Slots [0, StaticSlotCount)
        /// are copied/reclassified only on first use or staticRevision change;
        /// dynamic slots are inspected every update. Revise staticRevision for
        /// ANY static payload/activity/position change. Coordinates must fit uint16;
        /// malformed active samples/flags are excluded and counted diagnostically.
        /// Removed IDs may be reused only after prior consumers complete. Keep
        /// inputs alive/immutable until this update completes. forceRebuild also
        /// refreshes static slots and is the compacting recovery/reference path.
        /// </summary>
        public void RecordUpdate(CommandBuffer commands, GraphicsBuffer inputSamples,
            GraphicsBuffer activeSlots, uint staticRevision = 0, bool forceRebuild = false)
        {
            if (disposed) throw new ObjectDisposedException(nameof(GpuSensorIncrementalIndex));
            if (commands == null) throw new ArgumentNullException(nameof(commands));
            ValidateInput(inputSamples, 16, nameof(inputSamples));
            ValidateInput(activeSlots, 4, nameof(activeSlots));
            if (inputSamples == Samples || inputSamples == BinnedIds || inputSamples == BinOffsets ||
                inputSamples == Diagnostics || activeSlots == Samples || activeSlots == BinnedIds ||
                activeSlots == BinOffsets || activeSlots == Diagnostics)
                throw new ArgumentException("Inputs must not alias index-owned output buffers.");
            commands.SetComputeIntParam(shader, "_Capacity", Capacity);
            commands.SetComputeIntParam(shader, "_StaticCount", StaticSlotCount);
            commands.SetComputeIntParam(shader, "_StaticRevision", unchecked((int)staticRevision));
            commands.SetComputeIntParam(shader, "_Force", forceRebuild ? 1 : 0);
            commands.SetComputeIntParam(shader, "_ChurnThreshold", ChurnThreshold);
            commands.SetComputeIntParam(shader, "_FragmentationThreshold", FragmentationThreshold);
            commands.BeginSample("Summit.SensorIndex/DirtyDetection");
            Dispatch(commands, begin, 1);
            commands.SetComputeBufferParam(shader, detect, "_InputSamples", inputSamples);
            commands.SetComputeBufferParam(shader, detect, "_ActiveSlots", activeSlots);
            Dispatch(commands, detect, Groups(Capacity));
            Dispatch(commands, decide, 1);
            commands.EndSample("Summit.SensorIndex/DirtyDetection");
            commands.BeginSample("Summit.SensorIndex/FallbackRebuild");
            Dispatch(commands, clear, Bins / 256);
            Dispatch(commands, count, Groups(Capacity));
            Dispatch(commands, scan, Bins / 256);
            Dispatch(commands, scanBlocks, 1);
            Dispatch(commands, prepare, Bins / 256);
            Dispatch(commands, clearMembers, Groups(Capacity * 3));
            Dispatch(commands, rebuild, Groups(Capacity));
            commands.EndSample("Summit.SensorIndex/FallbackRebuild");
            commands.BeginSample("Summit.SensorIndex/IncrementalMaintenance");
            // Separate dispatches guarantee removals cannot race inserted IDs.
            Dispatch(commands, remove, Groups(Capacity));
            Dispatch(commands, insert, Groups(Capacity));
            Dispatch(commands, finish, 1);
            commands.EndSample("Summit.SensorIndex/IncrementalMaintenance");
        }

        private void Dispatch(CommandBuffer c, int kernel, int groups)
        {
            if (ExecutionMode == GpuSensorIndexExecutionMode.GpuDriven)
            {
                int offset = kernel == clear ? 0 : kernel == count ? 12 : kernel == scan ? 24 :
                    kernel == scanBlocks ? 36 : kernel == prepare ? 48 : kernel == clearMembers ? 60 :
                    kernel == rebuild ? 72 : kernel == remove ? 84 : kernel == insert ? 96 : kernel == finish ? 108 : -1;
                if (offset >= 0) c.DispatchCompute(shader, kernel, dispatchArguments, (uint)offset);
                else c.DispatchCompute(shader, kernel, groups, 1, 1);
                return;
            }
            c.SetComputeBufferParam(shader, kernel, "_Samples", Samples);
            c.SetComputeBufferParam(shader, kernel, "_PreviousKeys", previousKeys);
            c.SetComputeBufferParam(shader, kernel, "_NextKeys", nextKeys);
            c.SetComputeBufferParam(shader, kernel, "_Positions", positions);
            c.SetComputeBufferParam(shader, kernel, "_Reservations", reservations);
            c.SetComputeBufferParam(shader, kernel, "_Offsets", BinOffsets);
            c.SetComputeBufferParam(shader, kernel, "_Members", BinnedIds);
            c.SetComputeBufferParam(shader, kernel, "_Heads", heads);
            c.SetComputeBufferParam(shader, kernel, "_Counts", counts);
            c.SetComputeBufferParam(shader, kernel, "_BlockSums", blockSums);
            c.SetComputeBufferParam(shader, kernel, "_State", Diagnostics);
            c.DispatchCompute(shader, kernel, groups, 1, 1);
        }

        private void BindOwnedBuffers(int kernel)
        {
            shader.SetBuffer(kernel, "_Samples", Samples);
            shader.SetBuffer(kernel, "_PreviousKeys", previousKeys);
            shader.SetBuffer(kernel, "_NextKeys", nextKeys);
            shader.SetBuffer(kernel, "_Positions", positions);
            shader.SetBuffer(kernel, "_Reservations", reservations);
            shader.SetBuffer(kernel, "_Offsets", BinOffsets);
            shader.SetBuffer(kernel, "_Members", BinnedIds);
            shader.SetBuffer(kernel, "_Heads", heads);
            shader.SetBuffer(kernel, "_Counts", counts);
            shader.SetBuffer(kernel, "_BlockSums", blockSums);
            shader.SetBuffer(kernel, "_State", Diagnostics);
            shader.SetBuffer(kernel, "_ChangedIds", changedIds);
            shader.SetBuffer(kernel, "_DispatchArguments", dispatchArguments);
        }

        private void ValidateInput(GraphicsBuffer buffer, int stride, string name)
        {
            if (buffer == null) throw new ArgumentNullException(name);
            if ((buffer.target & GraphicsBuffer.Target.Structured) == 0 ||
                buffer.stride != stride || buffer.count < Capacity)
                throw new ArgumentException("Snapshot buffer has wrong shape/capacity.", name);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Samples?.Dispose(); previousKeys?.Dispose(); nextKeys?.Dispose();
            positions?.Dispose(); reservations?.Dispose(); BinOffsets?.Dispose();
            BinnedIds?.Dispose(); heads?.Dispose(); counts?.Dispose();
            blockSums?.Dispose(); Diagnostics?.Dispose();
            changedIds?.Dispose(); dispatchArguments?.Dispose();
            if (ExecutionMode == GpuSensorIndexExecutionMode.GpuDriven && shader != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(shader);
                else UnityEngine.Object.DestroyImmediate(shader);
            }
        }

        private static GraphicsBuffer Buffer(int count, int stride = 4) =>
            new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);
        private static int Groups(int count) => (count + 255) / 256;
    }
}
