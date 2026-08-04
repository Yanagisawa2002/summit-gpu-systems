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
            if (physicalSlotCount < maximumRequestedPages)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(physicalSlotCount));
            }
            if (pointsPerPage < 1 || pointsPerPage > 16384)
            {
                throw new ArgumentOutOfRangeException(nameof(pointsPerPage));
            }
            if (maximumRequestedPages < 1 ||
                maximumRequestedPages > physicalSlotCount)
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
            GraphicsBuffer selectedPhysical = null;
            GraphicsBuffer selectedPageTable = null;
            GraphicsBuffer selectedDigests = null;
            try
            {
                selectedPayload = CreateBuffer(
                    checked(maximumRequestedPages * pointsPerPage),
                    PointStride,
                    "GPU Residency Upload Payload");
                selectedUploads = CreateBuffer(
                    maximumRequestedPages,
                    UploadStride,
                    "GPU Residency Upload Descriptors");
                selectedDeltas = CreateBuffer(
                    checked(maximumRequestedPages * 2),
                    DeltaStride,
                    "GPU Residency Page Table Deltas");
                selectedRequests = CreateBuffer(
                    maximumRequestedPages,
                    sizeof(uint),
                    "GPU Residency Requested Pages");
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
            PhysicalPoints = selectedPhysical;
            PageTable = selectedPageTable;
            PageDigests = selectedDigests;
        }

        public int VirtualPageCount { get; }
        public int PhysicalSlotCount { get; }
        public int PointsPerPage { get; }
        public int MaximumRequestedPages { get; }
        public GraphicsBuffer UploadPayload { get; }
        public GraphicsBuffer UploadDescriptors { get; }
        public GraphicsBuffer PageTableDeltas { get; }
        public GraphicsBuffer RequestedPages { get; }
        public GraphicsBuffer PhysicalPoints { get; }
        public GraphicsBuffer PageTable { get; }
        public GraphicsBuffer PageDigests { get; }

        public long ResidentBytes => checked(
            (long)MaximumRequestedPages * PointsPerPage * PointStride +
            (long)MaximumRequestedPages * UploadStride +
            (long)MaximumRequestedPages * 2L * DeltaStride +
            (long)MaximumRequestedPages * sizeof(uint) +
            (long)PhysicalSlotCount * PointsPerPage * PointStride +
            (long)VirtualPageCount * sizeof(uint) +
            (long)MaximumRequestedPages * DigestStride);

        public void RecordReset(CommandBuffer commands)
        {
            ValidateCommands(commands);
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

        public void RecordFrame(
            CommandBuffer commands,
            GpuResidencyFramePlan plan,
            GpuPointPageValue[] uploadPayload)
        {
            ValidateCommands(commands);
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }
            if (uploadPayload == null)
            {
                throw new ArgumentNullException(nameof(uploadPayload));
            }
            int payloadCount = checked(plan.UploadCount * PointsPerPage);
            if (uploadPayload.Length < payloadCount)
            {
                throw new ArgumentException(
                    "Upload payload does not cover the frame plan.",
                    nameof(uploadPayload));
            }

            commands.SetBufferData(
                RequestedPages,
                plan.RequestedPages,
                0,
                0,
                plan.RequestedPages.Length);
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
                    DivideRoundUp(payloadCount, ThreadGroupSize),
                    1,
                    1);
            }

            commands.SetComputeIntParam(
                shader,
                RequestedPageCountId,
                plan.RequestedPages.Length);
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
                    plan.RequestedPages.Length,
                    ThreadGroupSize),
                1,
                1);
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
