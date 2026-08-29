using System;
using Unity.Collections;
using UnityEngine;

namespace Summit.GpuDrivenInstances
{
    /// <summary>
    /// Builds fixed-membership clusters without managed allocations.
    /// </summary>
    public static class GpuInstanceClusterBuilder
    {
        public const float MinimumConservativeRadiusEpsilon = 0.00001f;
        private const float RelativeConservativeRadiusEpsilon = 0.000001f;

        public static int GetRequiredClusterCount(
            int activeCount,
            int instancesPerCluster)
        {
            ValidateInstancesPerCluster(instancesPerCluster);
            if (activeCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(activeCount),
                    "The active instance count cannot be negative.");
            }

            return activeCount == 0
                ? 0
                : ((activeCount - 1) / instancesPerCluster) + 1;
        }

        /// <summary>
        /// Partitions the active prefix into contiguous clusters and returns the
        /// number of descriptors written to <paramref name="outputClusters"/>.
        /// </summary>
        public static int BuildContiguous(
            NativeArray<GpuInstanceState> instances,
            int activeCount,
            int instancesPerCluster,
            NativeArray<GpuInstanceCluster> outputClusters)
        {
            int requiredClusterCount = GetRequiredClusterCount(
                activeCount,
                instancesPerCluster);
            if (activeCount > instances.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(activeCount),
                    "The active instance count exceeds the source length.");
            }
            if (requiredClusterCount > outputClusters.Length)
            {
                throw new ArgumentException(
                    "The output does not have capacity for every cluster.",
                    nameof(outputClusters));
            }

            for (int clusterIndex = 0;
                clusterIndex < requiredClusterCount;
                clusterIndex++)
            {
                int firstInstance = clusterIndex * instancesPerCluster;
                int instanceCount = Math.Min(
                    instancesPerCluster,
                    activeCount - firstInstance);
                outputClusters[clusterIndex] = BuildCluster(
                    instances,
                    firstInstance,
                    instanceCount);
            }

            return requiredClusterCount;
        }

        private static GpuInstanceCluster BuildCluster(
            NativeArray<GpuInstanceState> instances,
            int firstInstance,
            int instanceCount)
        {
            Vector4 firstPositionRadius =
                instances[firstInstance].PositionRadius;
            Vector3 firstPosition = new Vector3(
                firstPositionRadius.x,
                firstPositionRadius.y,
                firstPositionRadius.z);
            float firstRadius = Mathf.Max(0f, firstPositionRadius.w);
            Vector3 radiusExtent = new Vector3(
                firstRadius,
                firstRadius,
                firstRadius);
            Vector3 minimum = firstPosition - radiusExtent;
            Vector3 maximum = firstPosition + radiusExtent;
            uint unionViewMask = instances[firstInstance].ViewMask;

            int endInstance = firstInstance + instanceCount;
            for (int instanceIndex = firstInstance + 1;
                instanceIndex < endInstance;
                instanceIndex++)
            {
                GpuInstanceState instance = instances[instanceIndex];
                Vector4 positionRadius = instance.PositionRadius;
                Vector3 position = new Vector3(
                    positionRadius.x,
                    positionRadius.y,
                    positionRadius.z);
                float radius = Mathf.Max(0f, positionRadius.w);
                radiusExtent = new Vector3(radius, radius, radius);
                minimum = Vector3.Min(minimum, position - radiusExtent);
                maximum = Vector3.Max(maximum, position + radiusExtent);
                unionViewMask |= instance.ViewMask;
            }

            Vector3 center =
                (minimum * 0.5f) + (maximum * 0.5f);
            float conservativeRadius = 0f;
            for (int instanceIndex = firstInstance;
                instanceIndex < endInstance;
                instanceIndex++)
            {
                Vector4 positionRadius =
                    instances[instanceIndex].PositionRadius;
                Vector3 position = new Vector3(
                    positionRadius.x,
                    positionRadius.y,
                    positionRadius.z);
                float radius = Mathf.Max(0f, positionRadius.w);
                conservativeRadius = Mathf.Max(
                    conservativeRadius,
                    Vector3.Distance(center, position) + radius);
            }

            float epsilon = Mathf.Max(
                MinimumConservativeRadiusEpsilon,
                conservativeRadius * RelativeConservativeRadiusEpsilon);
            conservativeRadius += epsilon;

            return new GpuInstanceCluster(
                new Vector4(
                    center.x,
                    center.y,
                    center.z,
                    conservativeRadius),
                (uint)firstInstance,
                (uint)instanceCount,
                unionViewMask);
        }

        private static void ValidateInstancesPerCluster(
            int instancesPerCluster)
        {
            if (instancesPerCluster < 1 ||
                instancesPerCluster >
                    GpuInstanceCluster.MaximumInstanceCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(instancesPerCluster),
                    "The cluster size must be in [1, " +
                    GpuInstanceCluster.MaximumInstanceCount + "].");
            }
        }
    }
}
