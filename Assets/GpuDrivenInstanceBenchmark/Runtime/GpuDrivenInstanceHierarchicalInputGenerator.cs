using System;
using Summit.GpuDrivenInstances;
using UnityEngine;

internal readonly struct GpuDrivenInstanceHierarchyExpectedStatistics
{
    public GpuDrivenInstanceHierarchyExpectedStatistics(
        uint coarseVisibleClusterViewCount,
        uint candidateInstanceViewCount)
    {
        CoarseVisibleClusterViewCount = coarseVisibleClusterViewCount;
        CandidateInstanceViewCount = candidateInstanceViewCount;
    }

    public uint CoarseVisibleClusterViewCount { get; }

    public uint CandidateInstanceViewCount { get; }
}

internal static class GpuDrivenInstanceHierarchicalInputGenerator
{
    internal const string VisibilityLayoutId =
        "spatial-clustered-multiview-64-v2";
    internal const int InstancesPerCluster =
        GpuInstanceCluster.MaximumInstanceCount;

    public static int Populate(
        GpuInstanceState[] instances,
        Vector4[] viewPlanes,
        Vector4[] viewParameters,
        GpuDrawTemplate[] drawTemplates,
        string visibility,
        int seed)
    {
        Validate(
            instances,
            viewPlanes,
            viewParameters,
            drawTemplates);

        int visibilityPercent =
            GpuDrivenInstanceInputGenerator.ParseVisibilityPercent(visibility);
        int viewCount = viewParameters.Length;
        int targetVisiblePairCount = checked((int)(
            (long)instances.Length * viewCount * visibilityPercent / 100L));
        uint allViewMask = viewCount == 32
            ? uint.MaxValue
            : (1u << viewCount) - 1u;

        PopulateViews(viewPlanes, viewParameters);
        PopulateDrawTemplates(drawTemplates);

        int spatiallyRejectedFirst = SelectSpatiallyRejectedFirst(
            instances.Length,
            viewCount,
            targetVisiblePairCount,
            visibilityPercent);
        int visibleSpaceInstanceCount = spatiallyRejectedFirst;
        int baseViewCount = visibleSpaceInstanceCount == 0
            ? 0
            : targetVisiblePairCount / visibleSpaceInstanceCount;
        int extraViewCount = visibleSpaceInstanceCount == 0
            ? 0
            : targetVisiblePairCount % visibleSpaceInstanceCount;
        int allocationRotation = visibleSpaceInstanceCount == 0
            ? 0
            : checked((int)(
                Mix32(unchecked((uint)seed) ^ 0xA511E9B3u) %
                checked((uint)visibleSpaceInstanceCount)));

        for (int index = 0; index < instances.Length; index++)
        {
            int clusterIndex = index / InstancesPerCluster;
            int lane = index % InstancesPerCluster;
            bool spatiallyRejected = index >= spatiallyRejectedFirst;
            uint clusterHash = Mix32(
                checked((uint)clusterIndex) ^
                unchecked((uint)seed) ^
                0x8DA6B343u);
            uint laneHash = Mix32(
                checked((uint)lane) ^ clusterHash ^ 0xD8163841u);
            Vector3 center = spatiallyRejected
                ? RejectedClusterCenter(clusterHash)
                : VisibleClusterCenter(clusterHash);
            Vector3 position = center + new Vector3(
                SignedUnit(laneHash) * 0.20f,
                SignedUnit(Mix32(laneHash + 1u)) * 0.20f,
                SignedUnit(Mix32(laneHash + 2u)) * 0.20f);
            uint viewMask;
            if (visibilityPercent == 100)
            {
                viewMask = allViewMask;
            }
            else if (spatiallyRejected)
            {
                // These bits deliberately reach the coarse spatial test and
                // are rejected by the frozen out-of-frustum cluster sphere.
                viewMask = 1u << checked((int)(
                    laneHash % checked((uint)viewCount)));
            }
            else
            {
                int logicalIndex = index + allocationRotation;
                if (logicalIndex >= visibleSpaceInstanceCount)
                {
                    logicalIndex -= visibleSpaceInstanceCount;
                }
                int selectedViewCount = baseViewCount +
                    (ReceivesFractionalView(
                        logicalIndex,
                        extraViewCount,
                        visibleSpaceInstanceCount)
                        ? 1
                        : 0);
                int firstView = checked((int)(
                    Mix32(
                        checked((uint)index) ^
                        unchecked((uint)seed) ^
                        0x63D83595u) %
                    checked((uint)viewCount)));
                viewMask = BuildCyclicViewMask(
                    firstView,
                    selectedViewCount,
                    viewCount);
            }

            uint drawGroup =
                Mix32(checked((uint)index) ^ unchecked((uint)seed)) %
                checked((uint)drawTemplates.Length);
            instances[index] = new GpuInstanceState(
                position,
                0.25f,
                new Vector4(4096f, 0f, 0f, 0f),
                checked((uint)index),
                drawGroup,
                1u,
                viewMask);
        }

        CountExactVisibility(
            instances,
            viewPlanes,
            viewParameters,
            out int visibleInstanceCount,
            out int visiblePairCount);
        if (visiblePairCount != targetVisiblePairCount)
        {
            throw new InvalidOperationException(
                "The frozen hierarchical layout did not produce its exact " +
                "visible-pair target.");
        }
        return visibleInstanceCount;
    }

    internal static GpuDrivenInstanceHierarchyExpectedStatistics
        ComputeExpectedHierarchyStatistics(
            GpuInstanceState[] instances,
            GpuInstanceCluster[] clusters,
            Vector4[] viewPlanes,
            int viewCount)
    {
        if (instances == null)
        {
            throw new ArgumentNullException(nameof(instances));
        }
        if (clusters == null)
        {
            throw new ArgumentNullException(nameof(clusters));
        }
        if (viewPlanes == null ||
            viewCount < 1 ||
            viewPlanes.Length !=
            viewCount * GpuDrivenInstancePipeline.FrustumPlaneCount)
        {
            throw new ArgumentException(
                "Exactly six view planes are required per view.",
                nameof(viewPlanes));
        }

        uint coarseVisibleClusterViewCount = 0u;
        uint candidateInstanceViewCount = 0u;
        for (int clusterIndex = 0;
            clusterIndex < clusters.Length;
            clusterIndex++)
        {
            GpuInstanceCluster cluster = clusters[clusterIndex];
            int firstInstance = checked((int)cluster.FirstInstance);
            int instanceCount = checked((int)cluster.InstanceCount);
            if (instanceCount < 1 ||
                instanceCount > GpuInstanceCluster.MaximumInstanceCount ||
                firstInstance < 0 ||
                firstInstance > instances.Length - instanceCount)
            {
                throw new ArgumentException(
                    "Cluster ranges must be valid contiguous active-prefix " +
                    "ranges.",
                    nameof(clusters));
            }

            Vector3 clusterCenter = new Vector3(
                cluster.PositionRadius.x,
                cluster.PositionRadius.y,
                cluster.PositionRadius.z);
            for (int view = 0; view < viewCount; view++)
            {
                uint viewBit = 1u << view;
                if ((cluster.UnionViewMask & viewBit) == 0u ||
                    !IsSphereVisible(
                        clusterCenter,
                        cluster.PositionRadius.w,
                        viewPlanes,
                        view))
                {
                    continue;
                }

                coarseVisibleClusterViewCount = checked(
                    coarseVisibleClusterViewCount + 1u);
                int end = firstInstance + instanceCount;
                for (int instanceIndex = firstInstance;
                    instanceIndex < end;
                    instanceIndex++)
                {
                    if ((instances[instanceIndex].ViewMask & viewBit) != 0u)
                    {
                        candidateInstanceViewCount = checked(
                            candidateInstanceViewCount + 1u);
                    }
                }
            }
        }

        return new GpuDrivenInstanceHierarchyExpectedStatistics(
            coarseVisibleClusterViewCount,
            candidateInstanceViewCount);
    }

    private static void Validate(
        GpuInstanceState[] instances,
        Vector4[] viewPlanes,
        Vector4[] viewParameters,
        GpuDrawTemplate[] drawTemplates)
    {
        if (instances == null)
        {
            throw new ArgumentNullException(nameof(instances));
        }
        if (viewParameters == null ||
            viewParameters.Length < 1 ||
            viewParameters.Length >
            GpuDrivenInstancePipeline.MaximumViewCount)
        {
            throw new ArgumentOutOfRangeException(nameof(viewParameters));
        }
        if (viewPlanes == null ||
            viewPlanes.Length !=
            viewParameters.Length *
            GpuDrivenInstancePipeline.FrustumPlaneCount)
        {
            throw new ArgumentException(
                "Exactly six view planes are required per view.",
                nameof(viewPlanes));
        }
        if (drawTemplates == null || drawTemplates.Length < 1)
        {
            throw new ArgumentException(
                "At least one draw template is required.",
                nameof(drawTemplates));
        }
    }

    private static void PopulateViews(
        Vector4[] viewPlanes,
        Vector4[] viewParameters)
    {
        int viewCount = viewParameters.Length;
        for (int view = 0; view < viewCount; view++)
        {
            float angle =
                (2f * Mathf.PI * view / viewCount) + 0.17320508f;
            Vector3 frustumCenter = new Vector3(
                Mathf.Cos(angle) * 8f,
                Mathf.Sin(angle) * 8f,
                ((view % 3) - 1) * 2f);
            Vector3 extent = new Vector3(
                48f + (view % 2),
                47f + ((view + 1) % 3),
                32f + (view % 3));
            int planeBase =
                view * GpuDrivenInstancePipeline.FrustumPlaneCount;
            viewPlanes[planeBase + 0] = new Vector4(
                1f,
                0f,
                0f,
                extent.x - frustumCenter.x);
            viewPlanes[planeBase + 1] = new Vector4(
                -1f,
                0f,
                0f,
                extent.x + frustumCenter.x);
            viewPlanes[planeBase + 2] = new Vector4(
                0f,
                1f,
                0f,
                extent.y - frustumCenter.y);
            viewPlanes[planeBase + 3] = new Vector4(
                0f,
                -1f,
                0f,
                extent.y + frustumCenter.y);
            viewPlanes[planeBase + 4] = new Vector4(
                0f,
                0f,
                1f,
                extent.z - frustumCenter.z);
            viewPlanes[planeBase + 5] = new Vector4(
                0f,
                0f,
                -1f,
                extent.z + frustumCenter.z);
            viewParameters[view] = new Vector4(
                Mathf.Cos(angle) * (12f + view),
                Mathf.Sin(angle) * (12f + view),
                -6f + view * 3f,
                1f + view * 0.03125f);
        }
    }

    private static void PopulateDrawTemplates(
        GpuDrawTemplate[] drawTemplates)
    {
        for (int group = 0; group < drawTemplates.Length; group++)
        {
            drawTemplates[group] = new GpuDrawTemplate(
                checked((uint)(36 + group)),
                checked((uint)(group * 7)),
                checked((uint)(group * 3)));
        }
    }

    private static int SelectSpatiallyRejectedFirst(
        int instanceCount,
        int viewCount,
        int targetVisiblePairCount,
        int visibilityPercent)
    {
        if (instanceCount == 0 || visibilityPercent == 100)
        {
            return instanceCount;
        }

        int minimumVisibleSpaceInstances = checked((int)(
            ((long)targetVisiblePairCount + viewCount - 1L) / viewCount));
        int availableRejectedInstances =
            instanceCount - minimumVisibleSpaceInstances;
        int lastClusterFirst =
            ((instanceCount - 1) / InstancesPerCluster) * InstancesPerCluster;
        int lastClusterCount = instanceCount - lastClusterFirst;
        return lastClusterCount <= availableRejectedInstances
            ? lastClusterFirst
            : instanceCount;
    }

    private static bool ReceivesFractionalView(
        int logicalIndex,
        int extraViewCount,
        int instanceCount)
    {
        if (extraViewCount == 0)
        {
            return false;
        }
        long before = (long)logicalIndex * extraViewCount / instanceCount;
        long after =
            (long)(logicalIndex + 1) * extraViewCount / instanceCount;
        return after != before;
    }

    private static uint BuildCyclicViewMask(
        int firstView,
        int selectedViewCount,
        int viewCount)
    {
        if (selectedViewCount < 0 || selectedViewCount > viewCount)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedViewCount));
        }
        uint result = 0u;
        for (int offset = 0; offset < selectedViewCount; offset++)
        {
            int view = firstView + offset;
            if (view >= viewCount)
            {
                view -= viewCount;
            }
            result |= 1u << view;
        }
        return result;
    }

    private static void CountExactVisibility(
        GpuInstanceState[] instances,
        Vector4[] viewPlanes,
        Vector4[] viewParameters,
        out int visibleInstanceCount,
        out int visiblePairCount)
    {
        visibleInstanceCount = 0;
        visiblePairCount = 0;
        for (int instanceIndex = 0;
            instanceIndex < instances.Length;
            instanceIndex++)
        {
            GpuInstanceState instance = instances[instanceIndex];
            Vector3 center = new Vector3(
                instance.PositionRadius.x,
                instance.PositionRadius.y,
                instance.PositionRadius.z);
            bool visibleInAnyView = false;
            for (int view = 0; view < viewParameters.Length; view++)
            {
                uint viewBit = 1u << view;
                if ((instance.ViewMask & viewBit) == 0u ||
                    !IsSphereVisible(
                        center,
                        instance.PositionRadius.w,
                        viewPlanes,
                        view))
                {
                    continue;
                }
                float scaledDistance = Vector3.Distance(
                    center,
                    new Vector3(
                        viewParameters[view].x,
                        viewParameters[view].y,
                        viewParameters[view].z)) * viewParameters[view].w;
                if (scaledDistance > instance.LodDistances.x)
                {
                    continue;
                }
                visiblePairCount = checked(visiblePairCount + 1);
                visibleInAnyView = true;
            }
            if (visibleInAnyView)
            {
                visibleInstanceCount = checked(visibleInstanceCount + 1);
            }
        }
    }

    private static bool IsSphereVisible(
        Vector3 center,
        float radius,
        Vector4[] viewPlanes,
        int view)
    {
        int planeBase =
            view * GpuDrivenInstancePipeline.FrustumPlaneCount;
        for (int plane = 0;
            plane < GpuDrivenInstancePipeline.FrustumPlaneCount;
            plane++)
        {
            Vector4 equation = viewPlanes[planeBase + plane];
            float distance =
                equation.x * center.x +
                equation.y * center.y +
                equation.z * center.z +
                equation.w;
            if (distance < -radius)
            {
                return false;
            }
        }
        return true;
    }

    private static Vector3 VisibleClusterCenter(uint hash)
    {
        return new Vector3(
            SignedUnit(hash) * 30f,
            SignedUnit(Mix32(hash + 1u)) * 30f,
            SignedUnit(Mix32(hash + 2u)) * 12f);
    }

    private static Vector3 RejectedClusterCenter(uint hash)
    {
        return new Vector3(
            512f + (hash & 255u),
            SignedUnit(Mix32(hash + 1u)) * 30f,
            SignedUnit(Mix32(hash + 2u)) * 12f);
    }

    private static float SignedUnit(uint value)
    {
        return ((value & 0x00FFFFFFu) / 8388607.5f) - 1f;
    }

    private static uint Mix32(uint value)
    {
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        value ^= value >> 16;
        return value;
    }
}
