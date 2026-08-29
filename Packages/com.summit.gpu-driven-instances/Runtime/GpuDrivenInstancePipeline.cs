using System;
using Summit.GpuDirectBinning;
using Summit.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuDrivenInstances
{
    /// <summary>
    /// Records multi-view visibility, LOD grouping, CSR compaction, and
    /// indexed-indirect argument generation without CPU readback.
    /// </summary>
    public sealed class GpuDrivenInstancePipeline : IDisposable
    {
        public const int ThreadGroupSize = 256;
        public const int MaximumViewCount = 32;
        public const int FrustumPlaneCount = 6;
        public const int IndirectArgumentWordCount = 5;
        public const int DiagnosticWordCount = 2;
        public const int ContractViolationCountWord = 0;
        public const int ErrorFlagsWord = 1;

        private const string ResourcePath =
            "GpuDrivenInstances/GpuDrivenInstances";
        private const string PipelineSample =
            "Summit.GpuDrivenInstances/Pipeline";
        private const string ClassifySample =
            "Summit.GpuDrivenInstances/Classify";
        private const string ArgumentsSample =
            "Summit.GpuDrivenInstances/BuildIndirectArguments";

        private static readonly int ClearCountId =
            Shader.PropertyToID("_ClearCount");
        private static readonly int InstanceCountId =
            Shader.PropertyToID("_InstanceCount");
        private static readonly int ViewCountId =
            Shader.PropertyToID("_ViewCount");
        private static readonly int DrawGroupCountId =
            Shader.PropertyToID("_DrawGroupCount");
        private static readonly int VisibleBinCountId =
            Shader.PropertyToID("_VisibleBinCount");
        private static readonly int ClearBufferId =
            Shader.PropertyToID("_ClearBuffer");
        private static readonly int InstancesId =
            Shader.PropertyToID("_Instances");
        private static readonly int ViewPlanesId =
            Shader.PropertyToID("_ViewPlanes");
        private static readonly int ViewParametersId =
            Shader.PropertyToID("_ViewParameters");
        private static readonly int DrawTemplatesId =
            Shader.PropertyToID("_DrawTemplates");
        private static readonly int KeysId =
            Shader.PropertyToID("_Keys");
        private static readonly int ValuesId =
            Shader.PropertyToID("_Values");
        private static readonly int BinCountsId =
            Shader.PropertyToID("_BinCounts");
        private static readonly int BinOffsetsId =
            Shader.PropertyToID("_BinOffsets");
        private static readonly int IndirectArgumentsId =
            Shader.PropertyToID("_IndirectArguments");
        private static readonly int DiagnosticsId =
            Shader.PropertyToID("_Diagnostics");

        private readonly ComputeShader shader;
        private readonly int clearUintKernel;
        private readonly int classifyInstancesKernel;
        private readonly int buildIndirectArgumentsKernel;
        private readonly GpuDirectSpatialBinner binner;
        private readonly GraphicsBuffer keys;
        private readonly GraphicsBuffer values;
        private readonly bool emitProfilerMarkers;
        private bool disposed;

        public GpuDrivenInstancePipeline(
            int instanceCapacity,
            int viewCapacity,
            int drawGroupCapacity,
            ComputeShader shader = null,
            bool emitProfilerMarkers = true)
        {
            ValidatePositiveCapacity(
                instanceCapacity,
                nameof(instanceCapacity));
            ValidateViewCapacity(viewCapacity);
            ValidatePositiveCapacity(
                drawGroupCapacity,
                nameof(drawGroupCapacity));

            int pairCapacity = CheckedProduct(
                instanceCapacity,
                viewCapacity,
                nameof(instanceCapacity));
            int visibleBinCapacity = CheckedProduct(
                viewCapacity,
                drawGroupCapacity,
                nameof(drawGroupCapacity));
            if (visibleBinCapacity >=
                global::Summit.GpuPrimitives.GpuPrimitives.MaxElementCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(drawGroupCapacity),
                    "Visible bins plus the culled bin exceed the supported " +
                    "GPU primitive capacity.");
            }

            ComputeShader selectedShader = shader != null
                ? shader
                : Resources.Load<ComputeShader>(ResourcePath);
            if (selectedShader == null)
            {
                throw new InvalidOperationException(
                    $"Compute shader resource '{ResourcePath}' could not " +
                    "be loaded.");
            }

            int selectedClearKernel = selectedShader.FindKernel("ClearUint");
            int selectedClassifyKernel =
                selectedShader.FindKernel("ClassifyInstances");
            int selectedArgumentsKernel =
                selectedShader.FindKernel("BuildIndirectArguments");

            GpuDirectSpatialBinner selectedBinner = null;
            GraphicsBuffer selectedKeys = null;
            GraphicsBuffer selectedValues = null;
            try
            {
                selectedBinner = new GpuDirectSpatialBinner(
                    pairCapacity,
                    checked(visibleBinCapacity + 1),
                    emitProfilerMarkers: emitProfilerMarkers);
                selectedKeys = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    pairCapacity,
                    sizeof(uint));
                selectedValues = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    pairCapacity,
                    sizeof(uint));
                selectedKeys.name = "GPU Driven Instance Classification Keys";
                selectedValues.name = "GPU Driven Instance Source Indices";
            }
            catch
            {
                selectedValues?.Dispose();
                selectedKeys?.Dispose();
                selectedBinner?.Dispose();
                throw;
            }

            InstanceCapacity = instanceCapacity;
            ViewCapacity = viewCapacity;
            DrawGroupCapacity = drawGroupCapacity;
            PairCapacity = pairCapacity;
            VisibleBinCapacity = visibleBinCapacity;
            this.shader = selectedShader;
            clearUintKernel = selectedClearKernel;
            classifyInstancesKernel = selectedClassifyKernel;
            buildIndirectArgumentsKernel = selectedArgumentsKernel;
            binner = selectedBinner;
            keys = selectedKeys;
            values = selectedValues;
            this.emitProfilerMarkers = emitProfilerMarkers;
        }

        public int InstanceCapacity { get; }

        public int ViewCapacity { get; }

        public int DrawGroupCapacity { get; }

        public int PairCapacity { get; }

        public int VisibleBinCapacity { get; }

        public bool EmitsProfilerMarkers => emitProfilerMarkers;

        public static bool SupportsCurrentDevice =>
            SystemInfo.supportsComputeShaders &&
            SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        public long ClassificationScratchBytes =>
            checked((long)PairCapacity * sizeof(uint) * 2L);

        public long BinningScratchBytes => binner.ScratchBytes;

        public long ScratchBytes =>
            checked(ClassificationScratchBytes + BinningScratchBytes);

        public long ResidentBytes => ScratchBytes;

        public static int GetVisibleBinCount(
            int viewCount,
            int drawGroupCount)
        {
            if (viewCount < 1 || viewCount > MaximumViewCount)
            {
                throw new ArgumentOutOfRangeException(nameof(viewCount));
            }
            if (drawGroupCount < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(drawGroupCount));
            }
            return checked(viewCount * drawGroupCount);
        }

        public static int GetCulledBinIndex(
            int viewCount,
            int drawGroupCount)
        {
            return GetVisibleBinCount(viewCount, drawGroupCount);
        }

        /// <summary>
        /// Records the complete GPU classification and argument pipeline.
        /// </summary>
        /// <remarks>
        /// <paramref name="groupCounts"/> contains VisibleBinCount + 1
        /// entries; the final entry is the culled-bin count.
        /// <paramref name="groupOffsets"/> contains one terminal offset after
        /// those bins. The grouped output therefore contains all active
        /// view/instance pairs; consumers draw only the visible prefix.
        /// </remarks>
        public void Record(
            CommandBuffer commands,
            GraphicsBuffer instances,
            GraphicsBuffer viewPlanes,
            GraphicsBuffer viewParameters,
            GraphicsBuffer drawTemplates,
            GraphicsBuffer groupCounts,
            GraphicsBuffer groupOffsets,
            GraphicsBuffer groupedInstanceIndices,
            GraphicsBuffer indirectArguments,
            GraphicsBuffer diagnostics,
            int instanceCount,
            int viewCount,
            int drawGroupCount,
            GpuPrimitiveBackend scanBackend = GpuPrimitiveBackend.Auto)
        {
            ValidateRecordArguments(
                commands,
                instances,
                viewPlanes,
                viewParameters,
                drawTemplates,
                groupCounts,
                groupOffsets,
                groupedInstanceIndices,
                indirectArguments,
                diagnostics,
                instanceCount,
                viewCount,
                drawGroupCount,
                scanBackend);

            int pairCount = checked(instanceCount * viewCount);
            int visibleBinCount = checked(viewCount * drawGroupCount);
            int totalBinCount = checked(visibleBinCount + 1);

            BeginSample(commands, PipelineSample);
            RecordClearDiagnostics(commands, diagnostics);

            BeginSample(commands, ClassifySample);
            if (pairCount > 0)
            {
                commands.SetComputeIntParam(
                    shader,
                    InstanceCountId,
                    instanceCount);
                commands.SetComputeIntParam(shader, ViewCountId, viewCount);
                commands.SetComputeIntParam(
                    shader,
                    DrawGroupCountId,
                    drawGroupCount);
                commands.SetComputeIntParam(
                    shader,
                    VisibleBinCountId,
                    visibleBinCount);
                commands.SetComputeBufferParam(
                    shader,
                    classifyInstancesKernel,
                    InstancesId,
                    instances);
                commands.SetComputeBufferParam(
                    shader,
                    classifyInstancesKernel,
                    ViewPlanesId,
                    viewPlanes);
                commands.SetComputeBufferParam(
                    shader,
                    classifyInstancesKernel,
                    ViewParametersId,
                    viewParameters);
                commands.SetComputeBufferParam(
                    shader,
                    classifyInstancesKernel,
                    KeysId,
                    keys);
                commands.SetComputeBufferParam(
                    shader,
                    classifyInstancesKernel,
                    ValuesId,
                    values);
                commands.SetComputeBufferParam(
                    shader,
                    classifyInstancesKernel,
                    DiagnosticsId,
                    diagnostics);
                commands.DispatchCompute(
                    shader,
                    classifyInstancesKernel,
                    DivideRoundUp(pairCount, ThreadGroupSize),
                    1,
                    1);
            }
            EndSample(commands, ClassifySample);

            binner.RecordGuaranteedInRangeWithoutDiagnosticClear(
                commands,
                keys,
                values,
                groupCounts,
                groupOffsets,
                groupedInstanceIndices,
                diagnostics,
                pairCount,
                totalBinCount,
                scanBackend);

            BeginSample(commands, ArgumentsSample);
            commands.SetComputeIntParam(
                shader,
                DrawGroupCountId,
                drawGroupCount);
            commands.SetComputeIntParam(
                shader,
                VisibleBinCountId,
                visibleBinCount);
            commands.SetComputeBufferParam(
                shader,
                buildIndirectArgumentsKernel,
                DrawTemplatesId,
                drawTemplates);
            commands.SetComputeBufferParam(
                shader,
                buildIndirectArgumentsKernel,
                BinCountsId,
                groupCounts);
            commands.SetComputeBufferParam(
                shader,
                buildIndirectArgumentsKernel,
                BinOffsetsId,
                groupOffsets);
            commands.SetComputeBufferParam(
                shader,
                buildIndirectArgumentsKernel,
                IndirectArgumentsId,
                indirectArguments);
            commands.DispatchCompute(
                shader,
                buildIndirectArgumentsKernel,
                DivideRoundUp(visibleBinCount, ThreadGroupSize),
                1,
                1);
            EndSample(commands, ArgumentsSample);
            EndSample(commands, PipelineSample);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            values?.Dispose();
            keys?.Dispose();
            binner?.Dispose();
        }

        private void RecordClearDiagnostics(
            CommandBuffer commands,
            GraphicsBuffer diagnostics)
        {
            commands.SetComputeIntParam(
                shader,
                ClearCountId,
                DiagnosticWordCount);
            commands.SetComputeBufferParam(
                shader,
                clearUintKernel,
                ClearBufferId,
                diagnostics);
            commands.DispatchCompute(shader, clearUintKernel, 1, 1, 1);
        }

        private void ValidateRecordArguments(
            CommandBuffer commands,
            GraphicsBuffer instances,
            GraphicsBuffer viewPlanes,
            GraphicsBuffer viewParameters,
            GraphicsBuffer drawTemplates,
            GraphicsBuffer groupCounts,
            GraphicsBuffer groupOffsets,
            GraphicsBuffer groupedInstanceIndices,
            GraphicsBuffer indirectArguments,
            GraphicsBuffer diagnostics,
            int instanceCount,
            int viewCount,
            int drawGroupCount,
            GpuPrimitiveBackend scanBackend)
        {
            ThrowIfDisposed();
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }
            if (instanceCount < 0 || instanceCount > InstanceCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(instanceCount),
                    $"Instance count must be in [0, {InstanceCapacity}].");
            }
            if (viewCount < 1 || viewCount > ViewCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(viewCount),
                    $"View count must be in [1, {ViewCapacity}].");
            }
            if (drawGroupCount < 1 ||
                drawGroupCount > DrawGroupCapacity)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(drawGroupCount),
                    "Draw-group count exceeds the configured capacity.");
            }
            if (scanBackend != GpuPrimitiveBackend.Auto &&
                scanBackend != GpuPrimitiveBackend.Portable &&
                scanBackend != GpuPrimitiveBackend.WaveOps)
            {
                throw new ArgumentOutOfRangeException(nameof(scanBackend));
            }

            int pairCount = checked(instanceCount * viewCount);
            int visibleBinCount = checked(viewCount * drawGroupCount);
            int totalBinCount = checked(visibleBinCount + 1);
            ValidateStructuredBuffer(
                instances,
                instanceCount,
                GpuInstanceState.Stride,
                nameof(instances));
            ValidateStructuredBuffer(
                viewPlanes,
                checked(viewCount * FrustumPlaneCount),
                sizeof(float) * 4,
                nameof(viewPlanes));
            ValidateStructuredBuffer(
                viewParameters,
                viewCount,
                sizeof(float) * 4,
                nameof(viewParameters));
            ValidateStructuredBuffer(
                drawTemplates,
                drawGroupCount,
                GpuDrawTemplate.Stride,
                nameof(drawTemplates));
            ValidateStructuredBuffer(
                groupCounts,
                totalBinCount,
                sizeof(uint),
                nameof(groupCounts));
            ValidateStructuredBuffer(
                groupOffsets,
                checked(totalBinCount + 1),
                sizeof(uint),
                nameof(groupOffsets));
            ValidateStructuredBuffer(
                groupedInstanceIndices,
                pairCount,
                sizeof(uint),
                nameof(groupedInstanceIndices));
            ValidateIndirectArgumentsBuffer(
                indirectArguments,
                checked(visibleBinCount * IndirectArgumentWordCount),
                nameof(indirectArguments));
            ValidateStructuredBuffer(
                diagnostics,
                DiagnosticWordCount,
                sizeof(uint),
                nameof(diagnostics));

            RequireNotInput(
                groupCounts,
                nameof(groupCounts),
                instances,
                viewPlanes,
                viewParameters,
                drawTemplates);
            RequireNotInput(
                groupOffsets,
                nameof(groupOffsets),
                instances,
                viewPlanes,
                viewParameters,
                drawTemplates);
            RequireNotInput(
                groupedInstanceIndices,
                nameof(groupedInstanceIndices),
                instances,
                viewPlanes,
                viewParameters,
                drawTemplates);
            RequireNotInput(
                indirectArguments,
                nameof(indirectArguments),
                instances,
                viewPlanes,
                viewParameters,
                drawTemplates);
            RequireNotInput(
                diagnostics,
                nameof(diagnostics),
                instances,
                viewPlanes,
                viewParameters,
                drawTemplates);

            RequireDistinct(
                groupCounts,
                nameof(groupCounts),
                groupOffsets,
                nameof(groupOffsets));
            RequireDistinct(
                groupCounts,
                nameof(groupCounts),
                groupedInstanceIndices,
                nameof(groupedInstanceIndices));
            RequireDistinct(
                groupCounts,
                nameof(groupCounts),
                indirectArguments,
                nameof(indirectArguments));
            RequireDistinct(
                groupCounts,
                nameof(groupCounts),
                diagnostics,
                nameof(diagnostics));
            RequireDistinct(
                groupOffsets,
                nameof(groupOffsets),
                groupedInstanceIndices,
                nameof(groupedInstanceIndices));
            RequireDistinct(
                groupOffsets,
                nameof(groupOffsets),
                indirectArguments,
                nameof(indirectArguments));
            RequireDistinct(
                groupOffsets,
                nameof(groupOffsets),
                diagnostics,
                nameof(diagnostics));
            RequireDistinct(
                groupedInstanceIndices,
                nameof(groupedInstanceIndices),
                indirectArguments,
                nameof(indirectArguments));
            RequireDistinct(
                groupedInstanceIndices,
                nameof(groupedInstanceIndices),
                diagnostics,
                nameof(diagnostics));
            RequireDistinct(
                indirectArguments,
                nameof(indirectArguments),
                diagnostics,
                nameof(diagnostics));
        }

        private void BeginSample(
            CommandBuffer commands,
            string sampleName)
        {
            if (emitProfilerMarkers)
            {
                commands.BeginSample(sampleName);
            }
        }

        private void EndSample(
            CommandBuffer commands,
            string sampleName)
        {
            if (emitProfilerMarkers)
            {
                commands.EndSample(sampleName);
            }
        }

        private static void ValidatePositiveCapacity(
            int capacity,
            string parameterName)
        {
            if (capacity < 1 ||
                capacity >
                global::Summit.GpuPrimitives.GpuPrimitives.MaxElementCount)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }
        }

        private static void ValidateViewCapacity(int viewCapacity)
        {
            if (viewCapacity < 1 || viewCapacity > MaximumViewCount)
            {
                throw new ArgumentOutOfRangeException(nameof(viewCapacity));
            }
        }

        private static int CheckedProduct(
            int first,
            int second,
            string parameterName)
        {
            long product = checked((long)first * second);
            if (product < 1L ||
                product >
                global::Summit.GpuPrimitives.GpuPrimitives.MaxElementCount)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    "The configured capacity product exceeds the supported " +
                    "GPU primitive capacity.");
            }
            return checked((int)product);
        }

        private static void ValidateStructuredBuffer(
            GraphicsBuffer buffer,
            int requiredCount,
            int requiredStride,
            string parameterName)
        {
            int allocationCount = Math.Max(1, requiredCount);
            if (buffer == null)
            {
                throw new ArgumentNullException(parameterName);
            }
            if ((buffer.target & GraphicsBuffer.Target.Structured) == 0 ||
                buffer.stride != requiredStride ||
                buffer.count < allocationCount)
            {
                throw new ArgumentException(
                    $"{parameterName} must be a structured GraphicsBuffer " +
                    $"with stride {requiredStride} and at least " +
                    $"{allocationCount} elements.",
                    parameterName);
            }
        }

        private static void ValidateIndirectArgumentsBuffer(
            GraphicsBuffer buffer,
            int requiredCount,
            string parameterName)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(parameterName);
            }
            GraphicsBuffer.Target requiredTargets =
                GraphicsBuffer.Target.Structured |
                GraphicsBuffer.Target.IndirectArguments;
            if ((buffer.target & requiredTargets) != requiredTargets ||
                buffer.stride != sizeof(uint) ||
                buffer.count < requiredCount)
            {
                throw new ArgumentException(
                    $"{parameterName} must be a structured indirect-" +
                    $"arguments GraphicsBuffer with stride {sizeof(uint)} " +
                    $"and at least {requiredCount} elements.",
                    parameterName);
            }
        }

        private static void RequireNotInput(
            GraphicsBuffer writable,
            string writableName,
            GraphicsBuffer instances,
            GraphicsBuffer viewPlanes,
            GraphicsBuffer viewParameters,
            GraphicsBuffer drawTemplates)
        {
            RequireDistinct(
                writable,
                writableName,
                instances,
                nameof(instances));
            RequireDistinct(
                writable,
                writableName,
                viewPlanes,
                nameof(viewPlanes));
            RequireDistinct(
                writable,
                writableName,
                viewParameters,
                nameof(viewParameters));
            RequireDistinct(
                writable,
                writableName,
                drawTemplates,
                nameof(drawTemplates));
        }

        private static void RequireDistinct(
            GraphicsBuffer first,
            string firstName,
            GraphicsBuffer second,
            string secondName)
        {
            if (ReferenceEquals(first, second))
            {
                throw new ArgumentException(
                    $"{firstName} must not alias {secondName}.",
                    firstName);
            }
        }

        private static int DivideRoundUp(int value, int divisor)
        {
            return (value + divisor - 1) / divisor;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(
                    nameof(GpuDrivenInstancePipeline));
            }
        }
    }
}
