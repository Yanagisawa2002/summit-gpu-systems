using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuResidencyManager
{
    public sealed class GpuPointPageCache : IDisposable
    {
        public const int PointStride = 16;
        public const int DigestStride = 16;
        public const int UploadStride = 8;
        public const int DeltaStride = 8;
        public const int ThreadGroupSize = 256;
        public const int MaximumDispatchGroups = 65535;

        private const string ResourcePath =
            "GpuResidencyManager/GpuPointPageCache";

        private static readonly int VirtualPageCountId =
            Shader.PropertyToID("_VirtualPageCount");
        private static readonly int PointsPerPageId =
            Shader.PropertyToID("_PointsPerPage");
        private static readonly int RequestedPageCountId =
            Shader.PropertyToID("_RequestedPageCount");
        private static readonly int UploadCountId =
            Shader.PropertyToID("_UploadCount");
        private static readonly int DeltaCountId =
            Shader.PropertyToID("_DeltaCount");
        private static readonly int UploadPayloadId =
            Shader.PropertyToID("_UploadPayload");
        private static readonly int UploadsId =
            Shader.PropertyToID("_Uploads");
        private static readonly int DeltasId =
            Shader.PropertyToID("_Deltas");
        private static readonly int RequestedPagesId =
            Shader.PropertyToID("_RequestedPages");
        private static readonly int PhysicalPointsId =
            Shader.PropertyToID("_PhysicalPoints");
        private static readonly int PageTableId =
            Shader.PropertyToID("_PageTable");
        private static readonly int PageDigestsId =
            Shader.PropertyToID("_PageDigests");

        private readonly ComputeShader shader;
        private readonly int clearKernel;
        private readonly int applyDeltasKernel;
        private readonly int scatterKernel;
        private readonly int queryKernel;
        private bool disposed;
        private GraphicsFence consumerFence;
        private bool hasConsumerFence;
        private GpuPageResidencyPlanner recordedPlanner;
        private long lastRecordedFrame = -1;
        private static readonly int RequestedSlotsId = Shader.PropertyToID("_RequestedSlots");

        public GpuPointPageCache(
            int virtualPageCount,
            int physicalSlotCount,
            int pointsPerPage,
            int maximumRequestedPages,
            ComputeShader shader = null)
        {
            if (virtualPageCount < 1 || virtualPageCount > 1048576)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(virtualPageCount));
            }
            if (physicalSlotCount < 1 || physicalSlotCount > virtualPageCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(physicalSlotCount));
            }
            if (pointsPerPage < 1 || pointsPerPage > 16384)
            {
                throw new ArgumentOutOfRangeException(nameof(pointsPerPage));
            }
            if (maximumRequestedPages < 1 ||
                maximumRequestedPages > virtualPageCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumRequestedPages));
            }

            ComputeShader selected = shader != null
                ? shader
                : Resources.Load<ComputeShader>(ResourcePath);
            if (selected == null)
            {
                throw new InvalidOperationException(
                    $"Compute shader resource '{ResourcePath}' was not found.");
            }

            GraphicsBuffer selectedPayload = null;
            GraphicsBuffer selectedUploads = null;
            GraphicsBuffer selectedDeltas = null;
            GraphicsBuffer selectedRequests = null;
            GraphicsBuffer selectedSlots = null;
            GraphicsBuffer selectedPhysical = null;
            GraphicsBuffer selectedPageTable = null;
            GraphicsBuffer selectedDigests = null;
            try
            {
                selectedPayload = CreateBuffer(
                    checked(Math.Min(maximumRequestedPages, physicalSlotCount) * pointsPerPage),
                    PointStride,
                    "GPU Residency Upload Payload");
                selectedUploads = CreateBuffer(
                    Math.Min(maximumRequestedPages, physicalSlotCount),
                    UploadStride,
                    "GPU Residency Upload Descriptors");
                selectedDeltas = CreateBuffer(
                    checked(physicalSlotCount + Math.Min(maximumRequestedPages, physicalSlotCount)),
                    DeltaStride,
                    "GPU Residency Page Table Deltas");
                selectedRequests = CreateBuffer(
                    maximumRequestedPages,
                    sizeof(uint),
                    "GPU Residency Requested Pages");
                selectedSlots = CreateBuffer(maximumRequestedPages, sizeof(int), "GPU Residency Request Slot Snapshot");
                selectedPhysical = CreateBuffer(
                    checked(physicalSlotCount * pointsPerPage),
                    PointStride,
                    "GPU Residency Physical Point Cache");
                selectedPageTable = CreateBuffer(
                    virtualPageCount,
                    sizeof(uint),
                    "GPU Residency Virtual Page Table");
                selectedDigests = CreateBuffer(
                    maximumRequestedPages,
                    DigestStride,
                    "GPU Residency Query Digests",
                    GraphicsBuffer.Target.CopySource);
            }
            catch
            {
                selectedPayload?.Dispose();
                selectedUploads?.Dispose();
                selectedDeltas?.Dispose();
                selectedRequests?.Dispose();
                selectedSlots?.Dispose();
                selectedPhysical?.Dispose();
                selectedPageTable?.Dispose();
                selectedDigests?.Dispose();
                throw;
            }

            VirtualPageCount = virtualPageCount;
            PhysicalSlotCount = physicalSlotCount;
            PointsPerPage = pointsPerPage;
            MaximumRequestedPages = maximumRequestedPages;
            this.shader = selected;
            clearKernel = selected.FindKernel("ClearPageTable");
            applyDeltasKernel = selected.FindKernel("ApplyPageTableDeltas");
            scatterKernel = selected.FindKernel("ScatterPageUploads");
            queryKernel = selected.FindKernel("QueryPageDigests");
            UploadPayload = selectedPayload;
            UploadDescriptors = selectedUploads;
            PageTableDeltas = selectedDeltas;
            RequestedPages = selectedRequests;
            RequestedSlots = selectedSlots;
            PhysicalPoints = selectedPhysical;
            PageTable = selectedPageTable;
            PageDigests = selectedDigests;
        }

        public int VirtualPageCount { get; }
        public int PhysicalSlotCount { get; }
        public int PointsPerPage { get; }
        public int MaximumRequestedPages { get; }
        public int MaximumUploadPagesPerFrame => MaximumDispatchGroups * ThreadGroupSize / PointsPerPage;
        public GraphicsBuffer UploadPayload { get; }
        public GraphicsBuffer UploadDescriptors { get; }
        public GraphicsBuffer PageTableDeltas { get; }
        public GraphicsBuffer RequestedPages { get; }
        public GraphicsBuffer RequestedSlots { get; }
        public GraphicsBuffer PhysicalPoints { get; }
        public GraphicsBuffer PageTable { get; }
        public GraphicsBuffer PageDigests { get; }

        public long ResidentBytes => checked(
            (long)Math.Min(MaximumRequestedPages, PhysicalSlotCount) * PointsPerPage * PointStride +
            (long)Math.Min(MaximumRequestedPages, PhysicalSlotCount) * UploadStride +
            ((long)PhysicalSlotCount + Math.Min(MaximumRequestedPages, PhysicalSlotCount)) * DeltaStride +
            (long)MaximumRequestedPages * sizeof(uint) * 2 +
            (long)PhysicalSlotCount * PointsPerPage * PointStride +
            (long)VirtualPageCount * sizeof(uint) +
            (long)MaximumRequestedPages * DigestStride);

        public void RecordReset(CommandBuffer commands)
        {
            ValidateCommands(commands);
            WaitForConsumers(commands);
            recordedPlanner = null; lastRecordedFrame = -1;
            commands.SetComputeIntParam(
                shader,
                VirtualPageCountId,
                VirtualPageCount);
            commands.SetComputeBufferParam(
                shader,
                clearKernel,
                PageTableId,
                PageTable);
            commands.DispatchCompute(
                shader,
                clearKernel,
                DivideRoundUp(VirtualPageCount, ThreadGroupSize),
                1,
                1);
        }

        public GraphicsFence RecordFrame(
            CommandBuffer commands,
            GpuResidencyFramePlan plan,
            GpuPointPageValue[] uploadPayload)
        {
            ValidateCommands(commands);
            if (plan == null || !plan.IsActive)
            {
                throw new ArgumentNullException(nameof(plan));
            }
            if (uploadPayload == null)
            {
                throw new ArgumentNullException(nameof(uploadPayload));
            }
            int uploadGroups = UploadDispatchGroupCount(plan.UploadCount, PointsPerPage);
            int payloadCount = checked(plan.UploadCount * PointsPerPage);
            if (uploadPayload.Length < payloadCount)
            {
                throw new ArgumentException(
                    "Upload payload does not cover the frame plan.",
                    nameof(uploadPayload));
            }

            if (!SystemInfo.supportsGraphicsFence) throw new NotSupportedException("Graphics fences are required.");
            if (plan.RequestedCount > MaximumRequestedPages || plan.UploadCount > UploadDescriptors.count ||
                plan.DeltaCount > PageTableDeltas.count || plan.Owner.VirtualPageCount != VirtualPageCount ||
                plan.Owner.PhysicalSlotCount != PhysicalSlotCount)
                throw new ArgumentException("Plan dimensions do not match this cache.", nameof(plan));
            if ((recordedPlanner != null && recordedPlanner != plan.Owner) || plan.PreviousFrameId != lastRecordedFrame)
                throw new InvalidOperationException("Record every accepted plan once in order; reset both cache and planner together.");
            WaitForConsumers(commands);
            if (plan.RequestedCount > 0)
            {
                commands.SetBufferData(RequestedSlots, plan.RequestedPhysicalSlots, 0, 0, plan.RequestedCount);
                commands.SetBufferData(RequestedPages, plan.RequestedPages, 0, 0, plan.RequestedCount);
            }
            if (plan.UploadCount > 0)
            {
                commands.SetBufferData(
                    UploadDescriptors,
                    plan.Uploads,
                    0,
                    0,
                    plan.UploadCount);
                commands.SetBufferData(
                    UploadPayload,
                    uploadPayload,
                    0,
                    0,
                    payloadCount);
            }
            if (plan.DeltaCount > 0)
            {
                commands.SetBufferData(
                    PageTableDeltas,
                    plan.Deltas,
                    0,
                    0,
                    plan.DeltaCount);
            }

            if (plan.DeltaCount > 0)
            {
                commands.SetComputeIntParam(
                    shader,
                    DeltaCountId,
                    plan.DeltaCount);
                commands.SetComputeBufferParam(
                    shader,
                    applyDeltasKernel,
                    DeltasId,
                    PageTableDeltas);
                commands.SetComputeBufferParam(
                    shader,
                    applyDeltasKernel,
                    PageTableId,
                    PageTable);
                commands.DispatchCompute(
                    shader,
                    applyDeltasKernel,
                    DivideRoundUp(plan.DeltaCount, ThreadGroupSize),
                    1,
                    1);
            }

            if (plan.UploadCount > 0)
            {
                commands.SetComputeIntParam(
                    shader,
                    UploadCountId,
                    plan.UploadCount);
                commands.SetComputeIntParam(
                    shader,
                    PointsPerPageId,
                    PointsPerPage);
                commands.SetComputeBufferParam(
                    shader,
                    scatterKernel,
                    UploadPayloadId,
                    UploadPayload);
                commands.SetComputeBufferParam(
                    shader,
                    scatterKernel,
                    UploadsId,
                    UploadDescriptors);
                commands.SetComputeBufferParam(
                    shader,
                    scatterKernel,
                    PhysicalPointsId,
                    PhysicalPoints);
                commands.DispatchCompute(
                    shader,
                    scatterKernel,
                    uploadGroups,
                    1,
                    1);
            }

            if (plan.RequestedCount == 0) return FinishFrame(commands, plan);
            commands.SetComputeBufferParam(shader, queryKernel, RequestedSlotsId, RequestedSlots);
            commands.SetComputeIntParam(
                shader,
                RequestedPageCountId,
                plan.RequestedCount);
            commands.SetComputeIntParam(
                shader,
                PointsPerPageId,
                PointsPerPage);
            commands.SetComputeBufferParam(
                shader,
                queryKernel,
                RequestedPagesId,
                RequestedPages);
            commands.SetComputeBufferParam(
                shader,
                queryKernel,
                PageTableId,
                PageTable);
            commands.SetComputeBufferParam(
                shader,
                queryKernel,
                PhysicalPointsId,
                PhysicalPoints);
            commands.SetComputeBufferParam(
                shader,
                queryKernel,
                PageDigestsId,
                PageDigests);
            commands.DispatchCompute(
                shader,
                queryKernel,
                DivideRoundUp(
                    plan.RequestedCount,
                    ThreadGroupSize),
                1,
                1);
            return FinishFrame(commands, plan);
        }

        private GraphicsFence FinishFrame(CommandBuffer commands, GpuResidencyFramePlan plan)
        {
            var fence = RecordConsumerFence(commands);
            recordedPlanner = plan.Owner; lastRecordedFrame = plan.FrameId;
            return fence;
        }

        /// <summary>Call again after external GPU consumers (including digest copies). Submit buffers once, in record order.</summary>
        public GraphicsFence RecordConsumerFence(CommandBuffer commands)
        {
            ValidateCommands(commands);
            if (!SystemInfo.supportsGraphicsFence)
                throw new NotSupportedException("Residency streaming requires graphics fences.");
            consumerFence = commands.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation,
                SynchronisationStageFlags.AllGPUOperations);
            hasConsumerFence = true;
            return consumerFence;
        }

        private void WaitForConsumers(CommandBuffer commands)
        {
            if (hasConsumerFence) commands.WaitOnAsyncGraphicsFence(consumerFence);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            UploadPayload.Dispose();
            UploadDescriptors.Dispose();
            PageTableDeltas.Dispose();
            RequestedPages.Dispose();
            RequestedSlots.Dispose();
            PhysicalPoints.Dispose();
            PageTable.Dispose();
            PageDigests.Dispose();
        }

        private static GraphicsBuffer CreateBuffer(
            int count,
            int stride,
            string name,
            GraphicsBuffer.Target extra = 0)
        {
            return new GraphicsBuffer(
                GraphicsBuffer.Target.Structured | extra,
                count,
                stride)
            {
                name = name
            };
        }

        /// <summary>Validates DX12's per-dimension limit before recording any commands; zero uploads produce zero groups.</summary>
        public static int UploadDispatchGroupCount(int uploadCount, int pointsPerPage)
        {
            if (uploadCount < 0) throw new ArgumentOutOfRangeException(nameof(uploadCount));
            if (pointsPerPage < 1 || pointsPerPage > 16384) throw new ArgumentOutOfRangeException(nameof(pointsPerPage));
            long points = (long)uploadCount * pointsPerPage;
            if (points > (long)MaximumDispatchGroups * ThreadGroupSize)
                throw new ArgumentOutOfRangeException(nameof(uploadCount), "Upload burst exceeds DX12 dispatch limits; budget against MaximumUploadPagesPerFrame before planning.");
            return (int)((points + ThreadGroupSize - 1) / ThreadGroupSize);
        }

        private static int DivideRoundUp(int value, int divisor)
        {
            return checked((value + divisor - 1) / divisor);
        }

        private void ValidateCommands(CommandBuffer commands)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(GpuPointPageCache));
            }
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }
        }
    }
}
