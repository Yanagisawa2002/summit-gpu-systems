using System;
using Summit.GpuDrivenInstanceBenchmark;
using Summit.GpuDrivenInstances;
using Summit.GpuPrimitives;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

internal sealed class GpuDrivenInstanceMacrobenchmarkValidationResult
{
    public string Phase;
    public string CaseId;
    public string Variant;
    public bool Passed;
    public string Message;
    public long ReadbackBytes;
    public string ResultHash;
    public string ImageHash;
    public int ValidCount;
    public uint InvalidKeyCount;
    public uint DiagnosticFlags;
    public bool RetryableReadbackFailure;
    public string FailedReadbackRequests;
    public int AttemptCount;
    public string RetryHistory;
}

/// <summary>
/// Owns the identical procedural workload rendered by the strong CPU baseline
/// and the GPU-driven indirect path. Validation readbacks are never issued
/// from measured frames.
/// </summary>
internal sealed class GpuDrivenInstanceMacrobenchmarkAdapter : IDisposable
{
    internal const int FixedDrawGroupCount = 8;
    internal const int RenderTargetSize = 512;
    internal const string CpuCaseId =
        "gpu-driven-instances/cpu-burst-engine-native";
    internal const string GpuCaseId =
        "gpu-driven-instances/gpu-visible-only-engine-indirect";
    internal const string CpuVariantName = "cpu-burst-engine-native";
    internal const string GpuVariantName =
        "gpu-visible-only-engine-indirect";
    internal const string CpuMarker =
        "GPU.DrivenInstanceMacro/CPU/BurstEngineNative";
    internal const string GpuMarker =
        "GPU.DrivenInstanceMacro/GPU/VisibleOnlyEngineIndirect";

    private const string ShaderResource =
        "GpuDrivenInstanceBenchmark/GpuDrivenInstanceMacro";
    private readonly int instanceCount;
    private readonly int viewCount;
    private readonly int drawGroupCount;
    private readonly int visibleBinCount;
    private readonly GpuInstanceState[] instanceData;
    private readonly Vector4[] viewPlaneData;
    private readonly Vector4[] viewParameterData;
    private readonly GpuDrawTemplate[] drawTemplateData;
    private readonly GpuDrivenInstanceExpectedResult expected;
    private readonly GpuDrivenInstanceMacrobenchmarkCpuBackend cpuBackend;
    private readonly GpuDrivenInstancePipeline pipeline;
    private readonly GraphicsBuffer instances;
    private readonly GraphicsBuffer viewPlanes;
    private readonly GraphicsBuffer viewParameters;
    private readonly GraphicsBuffer drawTemplates;
    private readonly GraphicsBuffer groupCounts;
    private readonly GraphicsBuffer groupOffsets;
    private readonly GraphicsBuffer groupedInstanceIndices;
    private readonly GraphicsBuffer indirectArgumentWords;
    private readonly GraphicsBuffer renderIndirectArguments;
    private readonly GraphicsBuffer diagnostics;
    private readonly Mesh mesh;
    private readonly Material cpuMaterial;
    private readonly Material gpuMaterial;
    private readonly RenderTexture renderTarget;
    private readonly GameObject[] cameraHosts;
    private readonly Camera[] cameras;
    private readonly GameObject presentationCameraHost;
    private readonly Camera presentationCamera;
    private readonly MaterialPropertyBlock[] cpuDrawProperties;
    private readonly MaterialPropertyBlock[] gpuDrawProperties;
    private readonly int[] cpuBatchOffsets;
    private readonly Matrix4x4[][] cpuBatchMatrices;
    private readonly long managedCpuBatchMatrixPayloadBytes;
    private PendingValidation pendingValidation;
    private bool disposed;

    internal GpuDrivenInstanceMacrobenchmarkAdapter(
        int instanceCount,
        int viewCount,
        string visibility,
        int seed,
        int drawGroupCount = FixedDrawGroupCount)
    {
        if (instanceCount < 1 ||
            instanceCount > global::Summit.GpuPrimitives.GpuPrimitives.MaxElementCount)
        {
            throw new ArgumentOutOfRangeException(nameof(instanceCount));
        }
        if (viewCount < 1 ||
            viewCount > GpuDrivenInstancePipeline.MaximumViewCount)
        {
            throw new ArgumentOutOfRangeException(nameof(viewCount));
        }
        if ((long)instanceCount * viewCount >
            global::Summit.GpuPrimitives.GpuPrimitives.MaxElementCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(viewCount),
                "Instance/view pair count exceeds primitive capacity.");
        }
        if (drawGroupCount < 1 || drawGroupCount > FixedDrawGroupCount)
        {
            throw new ArgumentOutOfRangeException(nameof(drawGroupCount));
        }
        if (!SystemInfo.supportsInstancing ||
            !SystemInfo.supportsIndirectArgumentsBuffer)
        {
            throw new NotSupportedException(
                "The macrobenchmark requires instancing and indirect draws.");
        }

        this.instanceCount = instanceCount;
        this.viewCount = viewCount;
        this.drawGroupCount = drawGroupCount;
        visibleBinCount = checked(viewCount * drawGroupCount);

        mesh = CreateCubeMesh();
        instanceData = new GpuInstanceState[instanceCount];
        viewPlaneData = new Vector4[
            checked(
                viewCount *
                GpuDrivenInstancePipeline.FrustumPlaneCount)];
        viewParameterData = new Vector4[viewCount];
        drawTemplateData = new GpuDrawTemplate[drawGroupCount];
        VisibleInstanceCount = GpuDrivenInstanceInputGenerator.Populate(
            instanceData,
            viewPlaneData,
            viewParameterData,
            drawTemplateData,
            visibility,
            seed);
        for (int group = 0; group < drawGroupCount; group++)
        {
            drawTemplateData[group] = new GpuDrawTemplate(
                mesh.GetIndexCount(0),
                mesh.GetIndexStart(0),
                checked((uint)mesh.GetBaseVertex(0)));
        }
        expected = GpuDrivenInstanceBenchmarkCpuOracle.Build(
            instanceData,
            viewPlaneData,
            viewParameterData,
            drawTemplateData,
            GpuDrivenInstanceOutputMode.VisibleOnly);
        VisiblePairCount = expected.GroupedInstanceIndices.Length;
        cpuBackend = new GpuDrivenInstanceMacrobenchmarkCpuBackend(
            instanceData,
            viewPlaneData,
            viewParameterData,
            drawGroupCount,
            expected.Counts);

        instances = CreateStructured(
            instanceCount,
            GpuInstanceState.Stride,
            "GPU Driven Macro Instance States");
        viewPlanes = CreateStructured(
            viewPlaneData.Length,
            sizeof(float) * 4,
            "GPU Driven Macro View Planes");
        viewParameters = CreateStructured(
            viewCount,
            sizeof(float) * 4,
            "GPU Driven Macro View Parameters");
        drawTemplates = CreateStructured(
            drawGroupCount,
            GpuDrawTemplate.Stride,
            "GPU Driven Macro Draw Templates");
        groupCounts = CreateStructured(
            visibleBinCount,
            sizeof(uint),
            "GPU Driven Macro Group Counts");
        groupOffsets = CreateStructured(
            visibleBinCount + 1,
            sizeof(uint),
            "GPU Driven Macro Group Offsets");
        groupedInstanceIndices = CreateStructured(
            checked(instanceCount * viewCount),
            sizeof(uint),
            "GPU Driven Macro Grouped Indices");
        indirectArgumentWords = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured |
            GraphicsBuffer.Target.IndirectArguments |
            GraphicsBuffer.Target.CopySource,
            checked(
                visibleBinCount *
                GpuDrivenInstancePipeline.IndirectArgumentWordCount),
            sizeof(uint))
        {
            name = "GPU Driven Macro Pipeline Indirect Words"
        };
        renderIndirectArguments = new GraphicsBuffer(
            GraphicsBuffer.Target.IndirectArguments |
            GraphicsBuffer.Target.CopyDestination,
            visibleBinCount,
            GraphicsBuffer.IndirectDrawIndexedArgs.size)
        {
            name = "GPU Driven Macro Engine Indirect Arguments"
        };
        diagnostics = CreateStructured(
            GpuDrivenInstancePipeline.DiagnosticWordCount,
            sizeof(uint),
            "GPU Driven Macro Diagnostics");
        // Unity 6 Mono players can omit the framework Unsafe assembly from
        // their scripting assembly table even though the Array overload of
        // GraphicsBuffer.SetData references it. The NativeArray overload is
        // also the representation used by the measured CPU path and avoids
        // that player-only dependency. These one-time uploads are outside the
        // measured loop.
        SetDataFromArray(instances, instanceData);
        SetDataFromArray(viewPlanes, viewPlaneData);
        SetDataFromArray(viewParameters, viewParameterData);
        SetDataFromArray(drawTemplates, drawTemplateData);
        pipeline = new GpuDrivenInstancePipeline(
            instanceCount,
            viewCount,
            drawGroupCount,
            emitProfilerMarkers: true);

        Shader shader = Resources.Load<Shader>(ShaderResource);
        if (shader == null)
        {
            throw new InvalidOperationException(
                "Macrobenchmark shader resource could not be loaded: " +
                ShaderResource);
        }
        cpuMaterial = new Material(shader)
        {
            name = "GPU Driven Macro CPU Material",
            enableInstancing = true
        };
        gpuMaterial = new Material(shader)
        {
            name = "GPU Driven Macro GPU Material",
            enableInstancing = true
        };
        gpuMaterial.EnableKeyword("GPU_DRIVEN_MACRO");

        renderTarget = new RenderTexture(
            RenderTargetSize,
            RenderTargetSize,
            24,
            RenderTextureFormat.ARGB32,
            RenderTextureReadWrite.Linear)
        {
            name = "GPU Driven Macro Offscreen Target",
            antiAliasing = 1,
            useMipMap = false,
            autoGenerateMips = false
        };
        if (!renderTarget.Create())
        {
            throw new InvalidOperationException(
                "Macrobenchmark render target creation failed.");
        }

        cameraHosts = new GameObject[viewCount];
        cameras = new Camera[viewCount];
        CreateCameras();
        presentationCameraHost = CreatePresentationCamera(
            out presentationCamera);
        cpuDrawProperties = new MaterialPropertyBlock[visibleBinCount];
        gpuDrawProperties = new MaterialPropertyBlock[visibleBinCount];
        CreateDrawProperties();
        cpuBatchOffsets = new int[visibleBinCount + 1];
        int totalCpuBatches = 0;
        for (int bin = 0; bin < visibleBinCount; bin++)
        {
            cpuBatchOffsets[bin] = totalCpuBatches;
            totalCpuBatches += DivideRoundUp(
                checked((int)expected.Counts[bin]),
                GpuDrivenInstanceMacrobenchmarkCpuBackend.MaxInstancesPerDraw);
        }
        cpuBatchOffsets[visibleBinCount] = totalCpuBatches;
        cpuBatchMatrices = new Matrix4x4[totalCpuBatches][];
        for (int bin = 0; bin < visibleBinCount; bin++)
        {
            int remaining = checked((int)expected.Counts[bin]);
            for (int batch = cpuBatchOffsets[bin];
                 batch < cpuBatchOffsets[bin + 1];
                 batch++)
            {
                int count = Math.Min(
                    GpuDrivenInstanceMacrobenchmarkCpuBackend
                        .MaxInstancesPerDraw,
                    remaining);
                cpuBatchMatrices[batch] = new Matrix4x4[count];
                remaining -= count;
            }
        }
        managedCpuBatchMatrixPayloadBytes =
            EstimateManagedBatchMatrixPayloadBytes(cpuBatchMatrices);
    }

    internal int InstanceCount => instanceCount;

    internal int ViewCount => viewCount;

    internal int DrawGroupCount => drawGroupCount;

    internal int VisibleBinCount => visibleBinCount;

    internal int VisibleInstanceCount { get; }

    internal int VisiblePairCount { get; }

    internal string ExpectedResultHash => expected.ResultHash;

    internal long SharedInputBytes => checked(
        (long)instanceCount * GpuInstanceState.Stride +
        (long)viewPlaneData.Length * sizeof(float) * 4L +
        (long)viewCount * sizeof(float) * 4L +
        (long)drawGroupCount * GpuDrawTemplate.Stride);

    internal long SharedOutputBytes => checked(
        (long)visibleBinCount * sizeof(uint) +
        (long)(visibleBinCount + 1) * sizeof(uint) +
        (long)instanceCount * viewCount * sizeof(uint) +
        2L * visibleBinCount *
        GraphicsBuffer.IndirectDrawIndexedArgs.size +
        GpuDrivenInstancePipeline.DiagnosticWordCount * sizeof(uint));

    internal long CpuPersistentNativeArrayPayloadBytes =>
        cpuBackend.PersistentNativeArrayPayloadBytes;

    /// <summary>
    /// Estimated managed payload held by the jagged CPU batch-matrix array:
    /// outer-array reference slots plus all Matrix4x4 elements. Managed array
    /// and object headers, alignment, and allocator metadata are excluded.
    /// </summary>
    internal long ManagedCpuBatchMatrixPayloadBytes =>
        managedCpuBatchMatrixPayloadBytes;

    /// <summary>
    /// Estimated logical resident payload for the measured CPU path. This is
    /// not a process-memory measurement and excludes all object/container
    /// headers and allocator overhead.
    /// </summary>
    internal long EstimatedCpuResidentPayloadBytes =>
        EstimateCpuResidentPayloadBytes(
            CpuPersistentNativeArrayPayloadBytes,
            ManagedCpuBatchMatrixPayloadBytes);

    // Retained for the existing config schema. The value now uses the complete
    // estimated payload above instead of the former pair-count approximation.
    internal long CpuResidentBytes => EstimatedCpuResidentPayloadBytes;

    internal long GpuResidentBytes => checked(
        SharedInputBytes + SharedOutputBytes + pipeline.ScratchBytes);

    internal long GpuScratchBytes => pipeline.ScratchBytes;

    internal int CpuDrawCallCount => cpuBackend.DrawCallCount;

    internal long CpuEngineInstancePayloadBytes => checked(
        (long)VisiblePairCount * sizeof(float) * 16L);

    internal static long EstimateManagedBatchMatrixPayloadBytes(
        Matrix4x4[][] batches)
    {
        if (batches == null)
        {
            throw new ArgumentNullException(nameof(batches));
        }

        long bytes = checked((long)batches.Length * IntPtr.Size);
        for (int batch = 0; batch < batches.Length; batch++)
        {
            Matrix4x4[] matrices = batches[batch];
            if (matrices == null)
            {
                throw new ArgumentException(
                    "Every CPU matrix batch must be allocated.",
                    nameof(batches));
            }
            bytes = checked(
                bytes +
                (long)matrices.Length * sizeof(float) * 16L);
        }
        return bytes;
    }

    internal static long EstimateCpuResidentPayloadBytes(
        long persistentNativeArrayPayloadBytes,
        long managedBatchMatrixPayloadBytes)
    {
        if (persistentNativeArrayPayloadBytes < 0L)
        {
            throw new ArgumentOutOfRangeException(
                nameof(persistentNativeArrayPayloadBytes));
        }
        if (managedBatchMatrixPayloadBytes < 0L)
        {
            throw new ArgumentOutOfRangeException(
                nameof(managedBatchMatrixPayloadBytes));
        }
        return checked(
            persistentNativeArrayPayloadBytes +
            managedBatchMatrixPayloadBytes);
    }

    internal int GpuRenderApiCallCount => visibleBinCount;

    internal int GpuLogicalDrawCommandCount => visibleBinCount;

    internal RenderTexture RenderTarget => renderTarget;

    internal Camera PresentationCamera => presentationCamera;

    internal string CaseId(GpuDrivenInstanceBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuDrivenInstanceBenchmarkVariant.Reference:
                return CpuCaseId;
            case GpuDrivenInstanceBenchmarkVariant.Direct:
                return GpuCaseId;
            case GpuDrivenInstanceBenchmarkVariant.Control:
                return "control/empty-render-frame";
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    internal string VariantName(GpuDrivenInstanceBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuDrivenInstanceBenchmarkVariant.Reference:
                return CpuVariantName;
            case GpuDrivenInstanceBenchmarkVariant.Direct:
                return GpuVariantName;
            case GpuDrivenInstanceBenchmarkVariant.Control:
                return "empty-render-frame";
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    internal string Marker(GpuDrivenInstanceBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuDrivenInstanceBenchmarkVariant.Reference:
                return CpuMarker;
            case GpuDrivenInstanceBenchmarkVariant.Direct:
                return GpuMarker;
            case GpuDrivenInstanceBenchmarkVariant.Control:
                return "GPU.DrivenInstanceMacro/Control/EmptyRenderFrame";
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    internal void CullAndPackCpu()
    {
        ThrowIfDisposed();
        cpuBackend.CullAndPack();
    }

    internal int RecordCpuDraws(CommandBuffer commands)
    {
        ThrowIfDisposed();
        if (commands == null)
        {
            throw new ArgumentNullException(nameof(commands));
        }
        RecordRenderTargetPreamble(commands);
        NativeArray<Matrix4x4> groupedMatrices =
            cpuBackend.GroupedMatrices;
        int calls = 0;
        for (int view = 0; view < viewCount; view++)
        {
            RecordView(commands, view);
            for (int group = 0; group < drawGroupCount; group++)
            {
                int bin = checked(view * drawGroupCount + group);
                int batchCount = cpuBackend.GetBatchCount(bin);
                for (int batch = 0; batch < batchCount; batch++)
                {
                    int count =
                        cpuBackend.GetBatchInstanceCount(bin, batch);
                    int sourceStart =
                        cpuBackend.GetBatchStartInstance(bin, batch);
                    Matrix4x4[] batchData = cpuBatchMatrices[
                        cpuBatchOffsets[bin] + batch];
                    for (int index = 0; index < count; index++)
                    {
                        batchData[index] =
                            groupedMatrices[sourceStart + index];
                    }
                    commands.DrawMeshInstanced(
                        mesh,
                        0,
                        cpuMaterial,
                        0,
                        batchData,
                        count,
                        cpuDrawProperties[bin]);
                    calls++;
                }
            }
        }
        return calls;
    }

    internal int RecordGpuPipelineAndDraws(CommandBuffer commands)
    {
        ThrowIfDisposed();
        if (commands == null)
        {
            throw new ArgumentNullException(nameof(commands));
        }
        pipeline.Record(
            commands,
            instances,
            viewPlanes,
            viewParameters,
            drawTemplates,
            groupCounts,
            groupOffsets,
            groupedInstanceIndices,
            indirectArgumentWords,
            diagnostics,
            instanceCount,
            viewCount,
            drawGroupCount,
            GpuPrimitiveBackend.Portable,
            GpuDrivenInstanceOutputMode.VisibleOnly);
        commands.CopyBuffer(
            indirectArgumentWords,
            renderIndirectArguments);
        RecordRenderTargetPreamble(commands);
        for (int view = 0; view < viewCount; view++)
        {
            RecordView(commands, view);
            for (int group = 0; group < drawGroupCount; group++)
            {
                int bin = checked(view * drawGroupCount + group);
                commands.DrawMeshInstancedIndirect(
                    mesh,
                    0,
                    gpuMaterial,
                    0,
                    renderIndirectArguments,
                    checked(
                        bin *
                        GraphicsBuffer.IndirectDrawIndexedArgs.size),
                    gpuDrawProperties[bin]);
            }
        }
        return visibleBinCount;
    }

    internal void RecordControlFrame(CommandBuffer commands)
    {
        ThrowIfDisposed();
        if (commands == null)
        {
            throw new ArgumentNullException(nameof(commands));
        }
        RecordRenderTargetPreamble(commands);
    }

    private void RecordRenderTargetPreamble(CommandBuffer commands)
    {
        commands.SetRenderTarget(renderTarget);
        commands.SetViewport(
            new Rect(0f, 0f, RenderTargetSize, RenderTargetSize));
        commands.ClearRenderTarget(true, true, Color.black);
    }

    private void RecordView(CommandBuffer commands, int view)
    {
        Camera camera = cameras[view];
        Rect normalized = camera.rect;
        commands.SetViewport(new Rect(
            normalized.x * RenderTargetSize,
            normalized.y * RenderTargetSize,
            normalized.width * RenderTargetSize,
            normalized.height * RenderTargetSize));
        commands.SetViewProjectionMatrices(
            camera.worldToCameraMatrix,
            GL.GetGPUProjectionMatrix(camera.projectionMatrix, true));
    }

    internal void BeginValidation(
        GpuDrivenInstanceBenchmarkVariant variant,
        string phase)
    {
        ThrowIfDisposed();
        if (variant == GpuDrivenInstanceBenchmarkVariant.Control)
        {
            throw new ArgumentOutOfRangeException(nameof(variant));
        }
        if (pendingValidation != null)
        {
            throw new InvalidOperationException(
                "A macrobenchmark validation is already pending.");
        }

        bool cpuPassed = true;
        string cpuMessage = string.Empty;
        if (variant == GpuDrivenInstanceBenchmarkVariant.Reference)
        {
            cpuBackend.CullAndPack();
            cpuPassed = cpuBackend.Validate(expected, out cpuMessage);
        }

        using (var commands = new CommandBuffer
               {
                   name = Marker(variant) + "/Validation"
               })
        {
            if (variant == GpuDrivenInstanceBenchmarkVariant.Reference)
            {
                RecordCpuDraws(commands);
            }
            else
            {
                RecordGpuPipelineAndDraws(commands);
            }
            Graphics.ExecuteCommandBuffer(commands);
        }

        long readbackBytes =
            (long)RenderTargetSize * RenderTargetSize * 4L;
        if (variant == GpuDrivenInstanceBenchmarkVariant.Direct)
        {
            readbackBytes += SharedOutputBytes;
        }
        pendingValidation = new PendingValidation
        {
            Variant = variant,
            Phase = phase,
            CpuPassed = cpuPassed,
            CpuMessage = cpuMessage,
            ReadbackBytes = readbackBytes
        };
    }

    internal void RequestValidationReadback()
    {
        ThrowIfDisposed();
        if (pendingValidation == null)
        {
            throw new InvalidOperationException(
                "No macrobenchmark validation is pending.");
        }
        if (pendingValidation.ReadbackStarted)
        {
            throw new InvalidOperationException(
                "Macrobenchmark validation readback was already requested.");
        }
        pendingValidation.ReadbackStarted = true;
        pendingValidation.NextRequestIndex = 0;
        IssueNextValidationReadback(pendingValidation);
    }

    internal bool TryCompleteValidation(
        out GpuDrivenInstanceMacrobenchmarkValidationResult result)
    {
        result = null;
        if (pendingValidation == null ||
            !pendingValidation.ReadbackStarted ||
            !pendingValidation.HasInFlightRequest)
        {
            return false;
        }
        if (!pendingValidation.InFlightRequest.done)
        {
            return false;
        }

        PendingValidation completed = pendingValidation;
        AsyncGPUReadbackRequest request = completed.InFlightRequest;
        string requestName = completed.InFlightRequestName;
        completed.HasInFlightRequest = false;
        if (request.hasError)
        {
            pendingValidation = null;
            result = CreateValidationResult(completed);
            result.Passed = false;
            result.RetryableReadbackFailure = true;
            result.FailedReadbackRequests = requestName;
            result.Message =
                "Async GPU validation readback failed: " +
                requestName + ".";
            result.ImageHash = "unavailable";
            return true;
        }

        CaptureValidationReadback(
            completed,
            completed.NextRequestIndex,
            request);
        completed.NextRequestIndex++;
        if (completed.NextRequestIndex <
            ValidationRequestCount(completed.Variant))
        {
            IssueNextValidationReadback(completed);
            return false;
        }

        pendingValidation = null;
        result = CreateValidationResult(completed);
        result.ImageHash = completed.ImageHash;
        bool imageContainsGeometry = completed.ImageContainsGeometry;
        if (completed.Variant == GpuDrivenInstanceBenchmarkVariant.Reference)
        {
            if (!imageContainsGeometry)
            {
                result.Passed = false;
                result.Message = AppendMessage(
                    result.Message,
                    "Rendered image contains no non-black geometry pixels.");
            }
            if (result.Passed && string.IsNullOrEmpty(result.Message))
            {
                result.Message =
                    "CPU counts and stable membership match the oracle.";
            }
            return true;
        }

        result.InvalidKeyCount = completed.Diagnostics[0];
        result.DiagnosticFlags = completed.Diagnostics[1];
        bool outputPassed = GpuDrivenInstanceBenchmarkCpuOracle.Validate(
            expected,
            completed.Counts,
            completed.Offsets,
            completed.Grouped,
            completed.PipelineArguments,
            completed.Diagnostics,
            out string outputMessage,
            out string resultHash);
        bool copyPassed = PrefixEqual(
            completed.PipelineArguments,
            completed.EngineArguments,
            expected.IndirectArguments.Length);
        result.Passed = outputPassed && copyPassed && imageContainsGeometry;
        result.ResultHash = resultHash;
        result.Message = copyPassed
            ? outputMessage + " Engine indirect argument copy matches."
            : outputMessage + " Engine indirect argument copy differs.";
        if (!imageContainsGeometry)
        {
            result.Message = AppendMessage(
                result.Message,
                "Rendered image contains no non-black geometry pixels.");
        }
        return true;
    }

    private GpuDrivenInstanceMacrobenchmarkValidationResult
        CreateValidationResult(PendingValidation completed)
    {
        return new GpuDrivenInstanceMacrobenchmarkValidationResult
        {
            Phase = completed.Phase,
            CaseId = CaseId(completed.Variant),
            Variant = VariantName(completed.Variant),
            Passed = completed.CpuPassed,
            Message = completed.CpuMessage,
            ReadbackBytes = completed.ReadbackBytes,
            ResultHash = expected.ResultHash,
            ValidCount = VisiblePairCount
        };
    }

    private static int ValidationRequestCount(
        GpuDrivenInstanceBenchmarkVariant variant)
    {
        return variant == GpuDrivenInstanceBenchmarkVariant.Direct ? 7 : 1;
    }

    private void IssueNextValidationReadback(PendingValidation validation)
    {
        int index = validation.NextRequestIndex;
        if (validation.Variant == GpuDrivenInstanceBenchmarkVariant.Reference)
        {
            validation.InFlightRequest = AsyncGPUReadback.Request(
                renderTarget,
                0,
                TextureFormat.RGBA32);
            validation.InFlightRequestName = "render-target";
        }
        else
        {
            switch (index)
            {
                case 0:
                    validation.InFlightRequest =
                        AsyncGPUReadback.Request(groupCounts);
                    validation.InFlightRequestName = "group-counts";
                    break;
                case 1:
                    validation.InFlightRequest =
                        AsyncGPUReadback.Request(groupOffsets);
                    validation.InFlightRequestName = "group-offsets";
                    break;
                case 2:
                    validation.InFlightRequest =
                        AsyncGPUReadback.Request(groupedInstanceIndices);
                    validation.InFlightRequestName =
                        "grouped-instance-indices";
                    break;
                case 3:
                    validation.InFlightRequest =
                        AsyncGPUReadback.Request(indirectArgumentWords);
                    validation.InFlightRequestName =
                        "pipeline-indirect-arguments";
                    break;
                case 4:
                    validation.InFlightRequest =
                        AsyncGPUReadback.Request(renderIndirectArguments);
                    validation.InFlightRequestName =
                        "engine-indirect-arguments";
                    break;
                case 5:
                    validation.InFlightRequest =
                        AsyncGPUReadback.Request(diagnostics);
                    validation.InFlightRequestName = "diagnostics";
                    break;
                case 6:
                    validation.InFlightRequest = AsyncGPUReadback.Request(
                        renderTarget,
                        0,
                        TextureFormat.RGBA32);
                    validation.InFlightRequestName = "render-target";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
        validation.HasInFlightRequest = true;
    }

    private static void CaptureValidationReadback(
        PendingValidation validation,
        int index,
        AsyncGPUReadbackRequest request)
    {
        if (validation.Variant == GpuDrivenInstanceBenchmarkVariant.Reference)
        {
            NativeArray<byte> image = request.GetData<byte>();
            validation.ImageHash = HashBytes(image);
            validation.ImageContainsGeometry = HasNonBlackPixel(image);
            return;
        }

        switch (index)
        {
            case 0:
                validation.Counts = ToArray(request.GetData<uint>());
                break;
            case 1:
                validation.Offsets = ToArray(request.GetData<uint>());
                break;
            case 2:
                validation.Grouped = ToArray(request.GetData<uint>());
                break;
            case 3:
                validation.PipelineArguments =
                    ToArray(request.GetData<uint>());
                break;
            case 4:
                validation.EngineArguments =
                    ToArray(request.GetData<uint>());
                break;
            case 5:
                validation.Diagnostics = ToArray(request.GetData<uint>());
                break;
            case 6:
                NativeArray<byte> image = request.GetData<byte>();
                validation.ImageHash = HashBytes(image);
                validation.ImageContainsGeometry = HasNonBlackPixel(image);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        cpuBackend.Dispose();
        pipeline.Dispose();
        diagnostics.Dispose();
        renderIndirectArguments.Dispose();
        indirectArgumentWords.Dispose();
        groupedInstanceIndices.Dispose();
        groupOffsets.Dispose();
        groupCounts.Dispose();
        drawTemplates.Dispose();
        viewParameters.Dispose();
        viewPlanes.Dispose();
        instances.Dispose();
        if (renderTarget != null)
        {
            renderTarget.Release();
            UnityEngine.Object.Destroy(renderTarget);
        }
        UnityEngine.Object.Destroy(cpuMaterial);
        UnityEngine.Object.Destroy(gpuMaterial);
        UnityEngine.Object.Destroy(mesh);
        for (int index = 0; index < cameraHosts.Length; index++)
        {
            if (cameraHosts[index] != null)
            {
                UnityEngine.Object.Destroy(cameraHosts[index]);
            }
        }
        if (presentationCameraHost != null)
        {
            UnityEngine.Object.Destroy(presentationCameraHost);
        }
    }

    private void CreateCameras()
    {
        int columns = Mathf.CeilToInt(Mathf.Sqrt(viewCount));
        int rows = Mathf.CeilToInt(viewCount / (float)columns);
        for (int view = 0; view < viewCount; view++)
        {
            GameObject host = new GameObject(
                "GPU Driven Macro Camera " + view);
            host.hideFlags = HideFlags.HideAndDontSave;
            Camera camera = host.AddComponent<Camera>();
            camera.enabled = false;
            camera.targetTexture = renderTarget;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.cullingMask = unchecked(1 << view);
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.useOcclusionCulling = false;
            camera.depthTextureMode = DepthTextureMode.None;
            camera.orthographic = true;
            camera.orthographicSize = 100f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 500f;
            camera.depth = view;
            int column = view % columns;
            int row = view / columns;
            camera.rect = new Rect(
                column / (float)columns,
                row / (float)rows,
                1f / columns,
                1f / rows);
            Vector4 viewParameter = viewParameterData[view];
            host.transform.position = new Vector3(
                viewParameter.x,
                viewParameter.y,
                -150f);
            host.transform.rotation = Quaternion.identity;
            cameraHosts[view] = host;
            cameras[view] = camera;
        }
    }

    private static GameObject CreatePresentationCamera(
        out Camera camera)
    {
        var host = new GameObject(
            "GPU Driven Macro Presentation Camera");
        host.hideFlags = HideFlags.HideAndDontSave;
        camera = host.AddComponent<Camera>();
        camera.enabled = true;
        camera.targetTexture = null;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.cullingMask = 0;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.useOcclusionCulling = false;
        camera.depthTextureMode = DepthTextureMode.None;
        camera.depth = -1000f;
        return host;
    }

    private void CreateDrawProperties()
    {
        for (int view = 0; view < viewCount; view++)
        {
            for (int group = 0; group < drawGroupCount; group++)
            {
                int bin = checked(view * drawGroupCount + group);
                MaterialPropertyBlock properties =
                    new MaterialPropertyBlock();
                properties.SetInt("_CpuGroupIndex", group);
                cpuDrawProperties[bin] = properties;

                MaterialPropertyBlock gpuProperties =
                    new MaterialPropertyBlock();
                gpuProperties.SetBuffer("_GpuInstanceStates", instances);
                gpuProperties.SetBuffer(
                    "_GpuGroupedInstanceIndices",
                    groupedInstanceIndices);
                gpuProperties.SetBuffer("_GpuGroupOffsets", groupOffsets);
                gpuProperties.SetInt("_GpuBinIndex", bin);
                gpuProperties.SetInt("_GpuGroupIndex", group);
                gpuDrawProperties[bin] = gpuProperties;
            }
        }
    }

    private static Mesh CreateCubeMesh()
    {
        var result = new Mesh
        {
            name = "GPU Driven Macro Procedural Cube",
            indexFormat = IndexFormat.UInt16
        };
        result.vertices = new[]
        {
            new Vector3(-0.5f, -0.5f, -0.5f),
            new Vector3( 0.5f, -0.5f, -0.5f),
            new Vector3( 0.5f,  0.5f, -0.5f),
            new Vector3(-0.5f,  0.5f, -0.5f),
            new Vector3(-0.5f, -0.5f,  0.5f),
            new Vector3( 0.5f, -0.5f,  0.5f),
            new Vector3( 0.5f,  0.5f,  0.5f),
            new Vector3(-0.5f,  0.5f,  0.5f)
        };
        result.triangles = new[]
        {
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4,
            2, 3, 7, 2, 7, 6,
            0, 4, 7, 0, 7, 3,
            1, 2, 6, 1, 6, 5
        };
        result.bounds = new Bounds(Vector3.zero, Vector3.one);
        result.UploadMeshData(true);
        return result;
    }

    private static GraphicsBuffer CreateStructured(
        int count,
        int stride,
        string name)
    {
        return new GraphicsBuffer(
            GraphicsBuffer.Target.Structured,
            count,
            stride)
        {
            name = name
        };
    }

    private static void SetDataFromArray<T>(
        GraphicsBuffer buffer,
        T[] source)
        where T : struct
    {
        using (var native = new NativeArray<T>(source, Allocator.Temp))
        {
            buffer.SetData(native);
        }
    }

    private static bool PrefixEqual(
        uint[] left,
        uint[] right,
        int length)
    {
        if (left == null || right == null ||
            left.Length < length || right.Length < length)
        {
            return false;
        }
        for (int index = 0; index < length; index++)
        {
            if (left[index] != right[index])
            {
                return false;
            }
        }
        return true;
    }

    private static uint[] ToArray(NativeArray<uint> source)
    {
        uint[] result = new uint[source.Length];
        source.CopyTo(result);
        return result;
    }

    private static string HashBytes(NativeArray<byte> bytes)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        for (int index = 0; index < bytes.Length; index++)
        {
            hash = (hash ^ bytes[index]) * prime;
        }
        return hash.ToString("X16");
    }

    private static bool HasNonBlackPixel(NativeArray<byte> bytes)
    {
        for (int index = 0; index + 3 < bytes.Length; index += 4)
        {
            if (bytes[index] != 0 ||
                bytes[index + 1] != 0 ||
                bytes[index + 2] != 0)
            {
                return true;
            }
        }
        return false;
    }

    private static string AppendMessage(string current, string addition)
    {
        return string.IsNullOrEmpty(current)
            ? addition
            : current + " " + addition;
    }

    private static int DivideRoundUp(int value, int divisor)
    {
        return value <= 0
            ? 0
            : checked((value + divisor - 1) / divisor);
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(
                nameof(GpuDrivenInstanceMacrobenchmarkAdapter));
        }
    }

    private sealed class PendingValidation
    {
        public GpuDrivenInstanceBenchmarkVariant Variant;
        public string Phase;
        public bool ReadbackStarted;
        public int NextRequestIndex;
        public bool HasInFlightRequest;
        public AsyncGPUReadbackRequest InFlightRequest;
        public string InFlightRequestName;
        public uint[] Counts;
        public uint[] Offsets;
        public uint[] Grouped;
        public uint[] PipelineArguments;
        public uint[] EngineArguments;
        public uint[] Diagnostics;
        public string ImageHash;
        public bool ImageContainsGeometry;
        public bool CpuPassed;
        public string CpuMessage;
        public long ReadbackBytes;
    }
}
