using System;
using Summit.GpuDrivenInstanceBenchmark;
using Summit.GpuDrivenInstances;
using Summit.GpuPrimitives;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

internal enum GpuDrivenInstanceUploadBenchmarkVariant
{
    FullUpload = 0,
    DirtyRangeUpload = 1,
}

internal readonly struct GpuDrivenInstanceUploadPreparationReceipt
{
    internal GpuDrivenInstanceUploadPreparationReceipt(
        int slotIndex,
        GpuDrivenInstanceUploadBenchmarkVariant variant,
        GpuDrivenInstanceUploadPlan plan,
        uint logicalOrdinal,
        int stateRecordsWritten)
    {
        SlotIndex = slotIndex;
        Variant = variant;
        Plan = plan;
        LogicalOrdinal = logicalOrdinal;
        StateRecordsWritten = stateRecordsWritten;
        UpdateHash = GpuDrivenInstanceUploadInputGenerator.ComputeUpdateHash(
            plan,
            logicalOrdinal);
    }

    internal int SlotIndex { get; }

    internal GpuDrivenInstanceUploadBenchmarkVariant Variant { get; }

    internal GpuDrivenInstanceUploadPlan Plan { get; }

    internal uint LogicalOrdinal { get; }

    /// <summary>
    /// Exact CPU staging records written while preparing this slot. For the
    /// dirty variant this includes records restored from the slot's preceding
    /// logical state and the records written for the new state.
    /// </summary>
    internal int StateRecordsWritten { get; }

    internal ulong UpdateHash { get; }
}

internal readonly struct GpuDrivenInstanceUploadRecordReceipt
{
    internal GpuDrivenInstanceUploadRecordReceipt(
        int slotIndex,
        GpuDrivenInstanceUploadBenchmarkVariant variant,
        uint logicalOrdinal,
        GpuInstanceUploadReceipt upload,
        int renderApiCallCount,
        int logicalDrawCommandCount)
    {
        SlotIndex = slotIndex;
        Variant = variant;
        LogicalOrdinal = logicalOrdinal;
        Upload = upload;
        RenderApiCallCount = renderApiCallCount;
        LogicalDrawCommandCount = logicalDrawCommandCount;
    }

    internal int SlotIndex { get; }

    internal GpuDrivenInstanceUploadBenchmarkVariant Variant { get; }

    internal uint LogicalOrdinal { get; }

    internal GpuInstanceUploadReceipt Upload { get; }

    internal int RenderApiCallCount { get; }

    internal int LogicalDrawCommandCount { get; }
}

/// <summary>
/// Allocation-free full-versus-dirty upload workload over the existing
/// visible-only GPU culling and engine indirect-draw path.
/// </summary>
/// <remarks>
/// Each staging slot owns a Persistent NativeArray and a command buffer. A
/// submitted slot remains immutable until its AllGPUOperations fence passes.
/// The caller must execute a recorded command buffer exactly once and then
/// immediately call MarkWorkSlotSubmitted with the fence returned by
/// AppendLifetimeFence. Validation readbacks belong outside measured frames.
/// </remarks>
internal sealed class GpuDrivenInstanceUploadBenchmarkAdapter : IDisposable
{
    internal const GraphicsFenceType LifetimeFenceType =
        GraphicsFenceType.AsyncQueueSynchronisation;

    internal const int DefaultStagingSlotCount = 8;
    internal const int FixedDrawGroupCount = 8;
    internal const int RenderTargetSize = 512;
    internal const string FullCaseId =
        "gpu-driven-instances/full-state-upload-visible-only";
    internal const string DirtyCaseId =
        "gpu-driven-instances/dirty-range-upload-visible-only";
    internal const string FullMarker =
        "GPU.DrivenInstanceUpload/Full/VisibleOnlyEngineIndirect";
    internal const string DirtyMarker =
        "GPU.DrivenInstanceUpload/Dirty/VisibleOnlyEngineIndirect";

    private const string ShaderResource =
        "GpuDrivenInstanceBenchmark/GpuDrivenInstanceMacro";
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private int instanceCount;
    private int viewCount;
    private int drawGroupCount;
    private int visibleBinCount;
    private int visibleInstanceCount;
    private int visiblePairCount;
    private string expectedResultHash;
    private Vector4[] viewParameterData;
    private NativeArray<GpuInstanceState> immutableBase;
    private WorkSlot[] workSlots;
    private int nextSlot;
    private int consecutiveSlotWaitFrames;
    private GpuInstanceStateUploader uploader;
    private GpuDrivenInstancePipeline pipeline;
    private GraphicsBuffer instances;
    private GraphicsBuffer viewPlanes;
    private GraphicsBuffer viewParameters;
    private GraphicsBuffer drawTemplates;
    private GraphicsBuffer groupCounts;
    private GraphicsBuffer groupOffsets;
    private GraphicsBuffer groupedInstanceIndices;
    private GraphicsBuffer indirectArgumentWords;
    private GraphicsBuffer renderIndirectArguments;
    private GraphicsBuffer diagnostics;
    private Mesh mesh;
    private Material gpuMaterial;
    private RenderTexture renderTarget;
    private GameObject[] cameraHosts;
    private Camera[] cameras;
    private MaterialPropertyBlock[] gpuDrawProperties;
    private bool disposed;

    internal GpuDrivenInstanceUploadBenchmarkAdapter(
        int instanceCount,
        int viewCount,
        string visibility,
        int seed,
        int drawGroupCount = FixedDrawGroupCount,
        int stagingSlotCount = DefaultStagingSlotCount,
        int maximumMergedGapRecords = 0)
    {
        ValidateConstructionInputs(
            instanceCount,
            viewCount,
            drawGroupCount,
            stagingSlotCount,
            maximumMergedGapRecords);

        this.instanceCount = instanceCount;
        this.viewCount = viewCount;
        this.drawGroupCount = drawGroupCount;
        visibleBinCount = checked(viewCount * drawGroupCount);

        try
        {
            mesh = CreateCubeMesh();
            var instanceData = new GpuInstanceState[instanceCount];
            var viewPlaneData = new Vector4[checked(
                viewCount * GpuDrivenInstancePipeline.FrustumPlaneCount)];
            viewParameterData = new Vector4[viewCount];
            var drawTemplateData = new GpuDrawTemplate[drawGroupCount];
            visibleInstanceCount = GpuDrivenInstanceInputGenerator.Populate(
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

            GpuDrivenInstanceExpectedResult expected =
                GpuDrivenInstanceBenchmarkCpuOracle.Build(
                    instanceData,
                    viewPlaneData,
                    viewParameterData,
                    drawTemplateData,
                    GpuDrivenInstanceOutputMode.VisibleOnly);
            visiblePairCount = expected.GroupedInstanceIndices.Length;
            expectedResultHash = expected.ResultHash;

            immutableBase = new NativeArray<GpuInstanceState>(
                instanceData,
                Allocator.Persistent);
            workSlots = new WorkSlot[stagingSlotCount];
            for (int index = 0; index < workSlots.Length; index++)
            {
                workSlots[index] = new WorkSlot(
                    immutableBase,
                    GpuDrivenInstanceUploadInputGenerator.MaximumRangeCount,
                    index);
            }

            uploader = new GpuInstanceStateUploader(
                GpuDrivenInstanceUploadInputGenerator.MaximumRangeCount,
                maximumMergedGapRecords);
            CreateGpuResources(
                viewPlaneData,
                viewParameterData,
                drawTemplateData);
            pipeline = new GpuDrivenInstancePipeline(
                instanceCount,
                viewCount,
                drawGroupCount,
                emitProfilerMarkers: true);
            CreateRenderingResources();
        }
        catch
        {
            DisposeCreatedResources();
            disposed = true;
            throw;
        }
    }

    internal int InstanceCount => instanceCount;

    internal int ViewCount => viewCount;

    internal int DrawGroupCount => drawGroupCount;

    internal int VisibleBinCount => visibleBinCount;

    internal int VisibleInstanceCount => visibleInstanceCount;

    internal int VisiblePairCount => visiblePairCount;

    internal int StagingSlotCount => workSlots.Length;

    internal string ExpectedResultHash => expectedResultHash;

    internal GraphicsBuffer InstanceStateBuffer => instances;

    internal RenderTexture RenderTarget => renderTarget;

    internal long PersistentStagingPayloadBytes => checked(
        (long)(workSlots.Length + 1) *
        instanceCount * GpuInstanceState.Stride +
        (long)workSlots.Length *
        GpuDrivenInstanceUploadInputGenerator.MaximumRangeCount *
        sizeof(int) * 2L);

    internal string CaseId(GpuDrivenInstanceUploadBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuDrivenInstanceUploadBenchmarkVariant.FullUpload:
                return FullCaseId;
            case GpuDrivenInstanceUploadBenchmarkVariant.DirtyRangeUpload:
                return DirtyCaseId;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    internal string Marker(GpuDrivenInstanceUploadBenchmarkVariant variant)
    {
        switch (variant)
        {
            case GpuDrivenInstanceUploadBenchmarkVariant.FullUpload:
                return FullMarker;
            case GpuDrivenInstanceUploadBenchmarkVariant.DirtyRangeUpload:
                return DirtyMarker;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }

    /// <summary>
    /// Acquires any available ring slot. waitFrames reports how many preceding
    /// calls found every slot busy since the previous successful acquisition.
    /// </summary>
    internal bool TryAcquireWorkSlot(
        out int slotIndex,
        out int waitFrames)
    {
        ThrowIfDisposed();
        for (int attempt = 0; attempt < workSlots.Length; attempt++)
        {
            int candidate = (nextSlot + attempt) % workSlots.Length;
            if (!workSlots[candidate].TryAcquire())
            {
                continue;
            }

            nextSlot = (candidate + 1) % workSlots.Length;
            slotIndex = candidate;
            waitFrames = consecutiveSlotWaitFrames;
            consecutiveSlotWaitFrames = 0;
            return true;
        }

        consecutiveSlotWaitFrames = checked(
            consecutiveSlotWaitFrames + 1);
        slotIndex = -1;
        waitFrames = consecutiveSlotWaitFrames;
        return false;
    }

    internal CommandBuffer Commands(int slotIndex)
    {
        ThrowIfDisposed();
        return GetSlot(slotIndex).Commands;
    }

    /// <summary>
    /// Reconstructs one deterministic logical state without managed or native
    /// allocations. Full writes every state record. Dirty restores only the
    /// records dirtied by this slot's prior use, then writes the new ranges.
    /// </summary>
    internal GpuDrivenInstanceUploadPreparationReceipt PrepareSlot(
        int slotIndex,
        GpuDrivenInstanceUploadBenchmarkVariant variant,
        int movingPercent,
        int seed,
        uint logicalOrdinal)
    {
        ThrowIfDisposed();
        WorkSlot slot = GetSlot(slotIndex);
        slot.RequireAcquired();

        int restoredRecordCount = 0;
        if (variant ==
            GpuDrivenInstanceUploadBenchmarkVariant.DirtyRangeUpload)
        {
            restoredRecordCount = slot.RestorePreviousDirtyRecords(
                immutableBase);
        }
        else if (variant !=
                 GpuDrivenInstanceUploadBenchmarkVariant.FullUpload)
        {
            throw new ArgumentOutOfRangeException(nameof(variant));
        }

        GpuDrivenInstanceUploadPlan plan =
            GpuDrivenInstanceUploadInputGenerator.BuildDirtyRanges(
                instanceCount,
                movingPercent,
                seed,
                slot.DirtyRanges);
        if (variant == GpuDrivenInstanceUploadBenchmarkVariant.FullUpload)
        {
            GpuDrivenInstanceUploadInputGenerator.PopulateFullState(
                immutableBase.AsReadOnly(),
                slot.States,
                slot.DirtyRanges,
                plan,
                logicalOrdinal);
        }
        else
        {
            GpuDrivenInstanceUploadInputGenerator.ApplyDirtyUpdates(
                immutableBase.AsReadOnly(),
                slot.States,
                slot.DirtyRanges,
                plan,
                logicalOrdinal);
        }

        slot.MarkPrepared(variant, plan, logicalOrdinal);
        int stateRecordsWritten = variant ==
            GpuDrivenInstanceUploadBenchmarkVariant.FullUpload
                ? checked(instanceCount + plan.ChangedInstanceCount)
                : checked(
                    restoredRecordCount + plan.ChangedInstanceCount);
        return new GpuDrivenInstanceUploadPreparationReceipt(
            slotIndex,
            variant,
            plan,
            logicalOrdinal,
            stateRecordsWritten);
    }

    /// <summary>
    /// Records upload, visible-only cull/pack, argument copy, and identical
    /// indirect draws into the slot command buffer. The lifetime fence must be
    /// appended after any timestamp end marker so it covers all GPU consumers.
    /// </summary>
    internal GpuDrivenInstanceUploadRecordReceipt RecordWorkload(
        int slotIndex,
        GpuDrivenInstanceUploadBenchmarkVariant variant)
    {
        ThrowIfDisposed();
        WorkSlot slot = GetSlot(slotIndex);
        slot.RequirePrepared(variant);

        GpuInstanceUploadReceipt upload;
        switch (variant)
        {
            case GpuDrivenInstanceUploadBenchmarkVariant.FullUpload:
                upload = uploader.RecordFull(
                    slot.Commands,
                    instances,
                    slot.States,
                    instanceCount);
                break;
            case GpuDrivenInstanceUploadBenchmarkVariant.DirtyRangeUpload:
                upload = uploader.RecordDirty(
                    slot.Commands,
                    instances,
                    slot.States,
                    instanceCount,
                    slot.DirtyRanges,
                    slot.Plan.RangeCount);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }

        RecordGpuPipelineAndDraws(slot.Commands);
        slot.MarkWorkloadRecorded();
        return new GpuDrivenInstanceUploadRecordReceipt(
            slotIndex,
            variant,
            slot.LogicalOrdinal,
            upload,
            visibleBinCount,
            visibleBinCount);
    }

    internal GraphicsFence AppendLifetimeFence(int slotIndex)
    {
        ThrowIfDisposed();
        WorkSlot slot = GetSlot(slotIndex);
        slot.RequireWorkloadRecorded();
        GraphicsFence fence = slot.Commands.CreateGraphicsFence(
            LifetimeFenceType,
            SynchronisationStageFlags.AllGPUOperations);
        slot.MarkFenceAppended();
        return fence;
    }

    internal void MarkWorkSlotSubmitted(
        int slotIndex,
        GraphicsFence fence)
    {
        ThrowIfDisposed();
        GetSlot(slotIndex).MarkSubmitted(fence);
    }

    /// <summary>
    /// Releases a slot only when its command buffer was not submitted. This is
    /// intended for exception cleanup before Graphics.ExecuteCommandBuffer.
    /// </summary>
    internal void AbandonUnsubmittedWorkSlot(int slotIndex)
    {
        ThrowIfDisposed();
        GetSlot(slotIndex).AbandonUnsubmitted();
    }

    internal int PendingCompletionFenceCount
    {
        get
        {
            ThrowIfDisposed();
            int count = 0;
            foreach (WorkSlot slot in workSlots)
            {
                if (!slot.TryMakeAvailable())
                {
                    count++;
                }
            }
            return count;
        }
    }

    internal bool AllCompletionFencesPassed =>
        PendingCompletionFenceCount == 0;

    /// <summary>
    /// Computes the expected state-buffer digest for validation outside the
    /// timed window. The slot remains immutable while submitted, so reading it
    /// on the CPU does not alter the fence lifetime contract.
    /// </summary>
    internal ulong ComputeSlotStateHash(int slotIndex)
    {
        ThrowIfDisposed();
        WorkSlot slot = GetSlot(slotIndex);
        slot.RequirePreparedOrLater();
        return ComputeStateHash(slot.States, instanceCount);
    }

    internal static bool ValidateStateHash(
        NativeArray<GpuInstanceState> readback,
        int expectedCount,
        ulong expectedHash,
        out ulong actualHash)
    {
        actualHash = ComputeStateHash(readback, expectedCount);
        return actualHash == expectedHash;
    }

    internal static ulong ComputeStateHash(
        NativeArray<GpuInstanceState> states,
        int count)
    {
        if (!states.IsCreated || count < 0 || count > states.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        ulong hash = FnvOffsetBasis;
        hash = HashUInt32(hash, checked((uint)count));
        for (int index = 0; index < count; index++)
        {
            GpuInstanceState state = states[index];
            Vector4 positionRadius = state.PositionRadius;
            Vector4 lodDistances = state.LodDistances;
            hash = HashUInt32(hash, math.asuint(positionRadius.x));
            hash = HashUInt32(hash, math.asuint(positionRadius.y));
            hash = HashUInt32(hash, math.asuint(positionRadius.z));
            hash = HashUInt32(hash, math.asuint(positionRadius.w));
            hash = HashUInt32(hash, math.asuint(lodDistances.x));
            hash = HashUInt32(hash, math.asuint(lodDistances.y));
            hash = HashUInt32(hash, math.asuint(lodDistances.z));
            hash = HashUInt32(hash, math.asuint(lodDistances.w));
            hash = HashUInt32(hash, state.ApplicationId);
            hash = HashUInt32(hash, state.DrawGroupBase);
            hash = HashUInt32(hash, state.LodCount);
            hash = HashUInt32(hash, state.ViewMask);
        }
        return hash;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        if (!AllCompletionFencesPassed)
        {
            throw new InvalidOperationException(
                "Cannot dispose upload benchmark resources while a staging " +
                "slot is submitted or an acquired slot is unaccounted for.");
        }

        disposed = true;
        DisposeCreatedResources();
    }

    private void CreateGpuResources(
        Vector4[] viewPlaneData,
        Vector4[] viewParametersData,
        GpuDrawTemplate[] drawTemplateData)
    {
        instances = CreateStructured(
            instanceCount,
            GpuInstanceState.Stride,
            "GPU Driven Upload Instance States");
        viewPlanes = CreateStructured(
            viewPlaneData.Length,
            sizeof(float) * 4,
            "GPU Driven Upload View Planes");
        viewParameters = CreateStructured(
            viewCount,
            sizeof(float) * 4,
            "GPU Driven Upload View Parameters");
        drawTemplates = CreateStructured(
            drawGroupCount,
            GpuDrawTemplate.Stride,
            "GPU Driven Upload Draw Templates");
        groupCounts = CreateStructured(
            visibleBinCount,
            sizeof(uint),
            "GPU Driven Upload Group Counts");
        groupOffsets = CreateStructured(
            visibleBinCount + 1,
            sizeof(uint),
            "GPU Driven Upload Group Offsets");
        groupedInstanceIndices = CreateStructured(
            checked(instanceCount * viewCount),
            sizeof(uint),
            "GPU Driven Upload Grouped Indices");
        indirectArgumentWords = new GraphicsBuffer(
            GraphicsBuffer.Target.Structured |
            GraphicsBuffer.Target.IndirectArguments |
            GraphicsBuffer.Target.CopySource,
            checked(
                visibleBinCount *
                GpuDrivenInstancePipeline.IndirectArgumentWordCount),
            sizeof(uint))
        {
            name = "GPU Driven Upload Pipeline Indirect Words"
        };
        renderIndirectArguments = new GraphicsBuffer(
            GraphicsBuffer.Target.IndirectArguments |
            GraphicsBuffer.Target.CopyDestination,
            visibleBinCount,
            GraphicsBuffer.IndirectDrawIndexedArgs.size)
        {
            name = "GPU Driven Upload Engine Indirect Arguments"
        };
        diagnostics = CreateStructured(
            GpuDrivenInstancePipeline.DiagnosticWordCount,
            sizeof(uint),
            "GPU Driven Upload Diagnostics");

        instances.SetData(immutableBase);
        SetDataFromArray(viewPlanes, viewPlaneData);
        SetDataFromArray(viewParameters, viewParametersData);
        SetDataFromArray(drawTemplates, drawTemplateData);
    }

    private void CreateRenderingResources()
    {
        Shader shader = Resources.Load<Shader>(ShaderResource);
        if (shader == null)
        {
            throw new InvalidOperationException(
                "Upload benchmark shader resource could not be loaded: " +
                ShaderResource);
        }
        gpuMaterial = new Material(shader)
        {
            name = "GPU Driven Upload GPU Material",
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
            name = "GPU Driven Upload Offscreen Target",
            antiAliasing = 1,
            useMipMap = false,
            autoGenerateMips = false
        };
        if (!renderTarget.Create())
        {
            throw new InvalidOperationException(
                "Upload benchmark render target creation failed.");
        }

        cameraHosts = new GameObject[viewCount];
        cameras = new Camera[viewCount];
        CreateCameras();
        gpuDrawProperties = new MaterialPropertyBlock[visibleBinCount];
        CreateDrawProperties();
    }

    private void RecordGpuPipelineAndDraws(CommandBuffer commands)
    {
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

    private void CreateCameras()
    {
        int columns = Mathf.CeilToInt(Mathf.Sqrt(viewCount));
        int rows = Mathf.CeilToInt(viewCount / (float)columns);
        for (int view = 0; view < viewCount; view++)
        {
            var host = new GameObject(
                "GPU Driven Upload Camera " + view);
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

    private void CreateDrawProperties()
    {
        for (int view = 0; view < viewCount; view++)
        {
            for (int group = 0; group < drawGroupCount; group++)
            {
                int bin = checked(view * drawGroupCount + group);
                var properties = new MaterialPropertyBlock();
                properties.SetBuffer("_GpuInstanceStates", instances);
                properties.SetBuffer(
                    "_GpuGroupedInstanceIndices",
                    groupedInstanceIndices);
                properties.SetBuffer("_GpuGroupOffsets", groupOffsets);
                properties.SetInt("_GpuBinIndex", bin);
                properties.SetInt("_GpuGroupIndex", group);
                gpuDrawProperties[bin] = properties;
            }
        }
    }

    private WorkSlot GetSlot(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= workSlots.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        }
        return workSlots[slotIndex];
    }

    private static void ValidateConstructionInputs(
        int instanceCount,
        int viewCount,
        int drawGroupCount,
        int stagingSlotCount,
        int maximumMergedGapRecords)
    {
        if (instanceCount < 1 ||
            instanceCount >
            global::Summit.GpuPrimitives.GpuPrimitives.MaxElementCount)
        {
            throw new ArgumentOutOfRangeException(nameof(instanceCount));
        }
        if (viewCount < 1 ||
            viewCount > GpuDrivenInstancePipeline.MaximumViewCount ||
            (long)instanceCount * viewCount >
            global::Summit.GpuPrimitives.GpuPrimitives.MaxElementCount)
        {
            throw new ArgumentOutOfRangeException(nameof(viewCount));
        }
        if (drawGroupCount < 1 || drawGroupCount > FixedDrawGroupCount)
        {
            throw new ArgumentOutOfRangeException(nameof(drawGroupCount));
        }
        if (stagingSlotCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(stagingSlotCount));
        }
        if (maximumMergedGapRecords < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumMergedGapRecords));
        }
        if (!SystemInfo.supportsComputeShaders ||
            !SystemInfo.supportsInstancing ||
            !SystemInfo.supportsIndirectArgumentsBuffer ||
            !SystemInfo.supportsGraphicsFence ||
            !SystemInfo.supportsAsyncCompute)
        {
            throw new NotSupportedException(
                "The upload macrobenchmark requires compute shaders, " +
                "instancing, indirect arguments, graphics fences, and " +
                "CPU-queryable asynchronous-compute fences.");
        }
    }

    private static Mesh CreateCubeMesh()
    {
        var result = new Mesh
        {
            name = "GPU Driven Upload Procedural Cube",
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

    private static ulong HashUInt32(ulong hash, uint value)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= FnvPrime;
        }
        return hash;
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(
                nameof(GpuDrivenInstanceUploadBenchmarkAdapter));
        }
    }

    private void DisposeCreatedResources()
    {
        if (workSlots != null)
        {
            foreach (WorkSlot slot in workSlots)
            {
                slot?.Dispose();
            }
        }
        uploader?.Dispose();
        pipeline?.Dispose();
        diagnostics?.Dispose();
        renderIndirectArguments?.Dispose();
        indirectArgumentWords?.Dispose();
        groupedInstanceIndices?.Dispose();
        groupOffsets?.Dispose();
        groupCounts?.Dispose();
        drawTemplates?.Dispose();
        viewParameters?.Dispose();
        viewPlanes?.Dispose();
        instances?.Dispose();
        if (immutableBase.IsCreated)
        {
            immutableBase.Dispose();
        }
        if (cameraHosts != null)
        {
            foreach (GameObject host in cameraHosts)
            {
                if (host != null)
                {
                    DestroyOwnedObject(host);
                }
            }
        }
        if (renderTarget != null)
        {
            renderTarget.Release();
            DestroyOwnedObject(renderTarget);
        }
        if (gpuMaterial != null)
        {
            DestroyOwnedObject(gpuMaterial);
        }
        if (mesh != null)
        {
            DestroyOwnedObject(mesh);
        }
    }

    private static void DestroyOwnedObject(UnityEngine.Object value)
    {
        if (Application.isPlaying)
        {
            UnityEngine.Object.Destroy(value);
        }
        else
        {
            UnityEngine.Object.DestroyImmediate(value);
        }
    }

    private enum WorkSlotState
    {
        Available = 0,
        Acquired = 1,
        Prepared = 2,
        WorkloadRecorded = 3,
        FenceAppended = 4,
        Submitted = 5,
    }

    private sealed class WorkSlot : IDisposable
    {
        private WorkSlotState state;
        private GraphicsFence fence;
        private int previousDirtyRangeCount;

        internal WorkSlot(
            NativeArray<GpuInstanceState> immutableBase,
            int rangeCapacity,
            int slotIndex)
        {
            States = new NativeArray<GpuInstanceState>(
                immutableBase.Length,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
            NativeArray<GpuInstanceState>.Copy(immutableBase, States);
            DirtyRanges = new NativeArray<GpuInstanceDirtyRange>(
                rangeCapacity,
                Allocator.Persistent,
                NativeArrayOptions.ClearMemory);
            Commands = new CommandBuffer
            {
                name = "GPU.DrivenInstanceUpload/StagingSlot/" + slotIndex
            };
            state = WorkSlotState.Available;
        }

        internal NativeArray<GpuInstanceState> States;

        internal NativeArray<GpuInstanceDirtyRange> DirtyRanges;

        internal CommandBuffer Commands { get; }

        internal GpuDrivenInstanceUploadPlan Plan { get; private set; }

        internal uint LogicalOrdinal { get; private set; }

        internal GpuDrivenInstanceUploadBenchmarkVariant Variant
        {
            get;
            private set;
        }

        internal bool TryAcquire()
        {
            if (!TryMakeAvailable())
            {
                return false;
            }
            Commands.Clear();
            state = WorkSlotState.Acquired;
            return true;
        }

        internal bool TryMakeAvailable()
        {
            if (state == WorkSlotState.Available)
            {
                return true;
            }
            if (state != WorkSlotState.Submitted || !fence.passed)
            {
                return false;
            }
            state = WorkSlotState.Available;
            return true;
        }

        internal int RestorePreviousDirtyRecords(
            NativeArray<GpuInstanceState> immutableBase)
        {
            int restored = 0;
            for (int rangeIndex = 0;
                 rangeIndex < previousDirtyRangeCount;
                 rangeIndex++)
            {
                GpuInstanceDirtyRange range = DirtyRanges[rangeIndex];
                for (int index = range.StartIndex;
                     index < range.EndIndex;
                     index++)
                {
                    States[index] = immutableBase[index];
                }
                restored = checked(restored + range.Count);
            }
            previousDirtyRangeCount = 0;
            return restored;
        }

        internal void RequireAcquired()
        {
            RequireState(WorkSlotState.Acquired);
        }

        internal void MarkPrepared(
            GpuDrivenInstanceUploadBenchmarkVariant variant,
            GpuDrivenInstanceUploadPlan plan,
            uint logicalOrdinal)
        {
            RequireState(WorkSlotState.Acquired);
            Variant = variant;
            Plan = plan;
            LogicalOrdinal = logicalOrdinal;
            previousDirtyRangeCount = plan.RangeCount;
            state = WorkSlotState.Prepared;
        }

        internal void RequirePrepared(
            GpuDrivenInstanceUploadBenchmarkVariant variant)
        {
            RequireState(WorkSlotState.Prepared);
            if (Variant != variant)
            {
                throw new InvalidOperationException(
                    "The recorded variant does not match slot preparation.");
            }
        }

        internal void MarkWorkloadRecorded()
        {
            RequireState(WorkSlotState.Prepared);
            state = WorkSlotState.WorkloadRecorded;
        }

        internal void RequireWorkloadRecorded()
        {
            RequireState(WorkSlotState.WorkloadRecorded);
        }

        internal void MarkFenceAppended()
        {
            RequireState(WorkSlotState.WorkloadRecorded);
            state = WorkSlotState.FenceAppended;
        }

        internal void MarkSubmitted(GraphicsFence submittedFence)
        {
            RequireState(WorkSlotState.FenceAppended);
            fence = submittedFence;
            state = WorkSlotState.Submitted;
        }

        internal void AbandonUnsubmitted()
        {
            if (state == WorkSlotState.Available ||
                state == WorkSlotState.Submitted)
            {
                throw new InvalidOperationException(
                    "Only acquired, unsubmitted work can be abandoned.");
            }
            Commands.Clear();
            state = WorkSlotState.Available;
        }

        internal void RequirePreparedOrLater()
        {
            if (state < WorkSlotState.Prepared)
            {
                throw new InvalidOperationException(
                    "The staging slot does not contain a prepared state.");
            }
        }

        public void Dispose()
        {
            Commands.Dispose();
            if (DirtyRanges.IsCreated)
            {
                DirtyRanges.Dispose();
            }
            if (States.IsCreated)
            {
                States.Dispose();
            }
        }

        private void RequireState(WorkSlotState expected)
        {
            if (state != expected)
            {
                throw new InvalidOperationException(
                    "Invalid staging slot transition. Expected " +
                    expected + ", actual " + state + ".");
            }
        }
    }
}
