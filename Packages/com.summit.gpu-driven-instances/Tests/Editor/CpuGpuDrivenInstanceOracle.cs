using System;
using System.Collections.Generic;
using UnityEngine;

namespace Summit.GpuDrivenInstances.Tests
{
    internal sealed class CpuGpuDrivenInstanceResult
    {
        internal CpuGpuDrivenInstanceResult(
            uint[] counts,
            uint[] offsets,
            uint[] groupedInstanceIndices,
            uint[] indirectArguments,
            uint contractViolationCount,
            uint errorFlags)
        {
            Counts = counts;
            Offsets = offsets;
            GroupedInstanceIndices = groupedInstanceIndices;
            IndirectArguments = indirectArguments;
            ContractViolationCount = contractViolationCount;
            ErrorFlags = errorFlags;
        }

        internal uint[] Counts { get; }

        internal uint[] Offsets { get; }

        internal uint[] GroupedInstanceIndices { get; }

        internal uint[] IndirectArguments { get; }

        internal uint ContractViolationCount { get; }

        internal uint ErrorFlags { get; }
    }

    /// <summary>
    /// Independent CPU model of the public classification contract.
    /// </summary>
    internal static class CpuGpuDrivenInstanceOracle
    {
        internal static CpuGpuDrivenInstanceResult Build(
            GpuInstanceState[] instances,
            Vector4[] viewPlanes,
            Vector4[] viewParameters,
            GpuDrawTemplate[] drawTemplates,
            GpuDrivenInstanceOutputMode outputMode =
                GpuDrivenInstanceOutputMode.CulledTail)
        {
            if (instances == null)
            {
                throw new ArgumentNullException(nameof(instances));
            }
            if (viewPlanes == null)
            {
                throw new ArgumentNullException(nameof(viewPlanes));
            }
            if (viewParameters == null || viewParameters.Length < 1)
            {
                throw new ArgumentException(
                    "At least one view parameter is required.",
                    nameof(viewParameters));
            }
            if (viewParameters.Length >
                GpuDrivenInstancePipeline.MaximumViewCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(viewParameters));
            }
            if (viewPlanes.Length !=
                viewParameters.Length *
                GpuDrivenInstancePipeline.FrustumPlaneCount)
            {
                throw new ArgumentException(
                    "Exactly six planes are required per view.",
                    nameof(viewPlanes));
            }
            if (drawTemplates == null || drawTemplates.Length < 1)
            {
                throw new ArgumentException(
                    "At least one draw template is required.",
                    nameof(drawTemplates));
            }
            if (outputMode != GpuDrivenInstanceOutputMode.CulledTail &&
                outputMode != GpuDrivenInstanceOutputMode.VisibleOnly)
            {
                throw new ArgumentOutOfRangeException(nameof(outputMode));
            }

            int viewCount = viewParameters.Length;
            int drawGroupCount = drawTemplates.Length;
            int visibleBinCount = checked(viewCount * drawGroupCount);
            bool includeCulledTail =
                outputMode == GpuDrivenInstanceOutputMode.CulledTail;
            int totalBinCount = includeCulledTail
                ? checked(visibleBinCount + 1)
                : visibleBinCount;
            var bins = new List<uint>[totalBinCount];
            for (int bin = 0; bin < bins.Length; bin++)
            {
                bins[bin] = new List<uint>();
            }

            uint violationCount = 0u;
            uint errorFlags = 0u;
            for (int viewIndex = 0; viewIndex < viewCount; viewIndex++)
            {
                for (int instanceIndex = 0;
                     instanceIndex < instances.Length;
                     instanceIndex++)
                {
                    GpuInstanceState instance = instances[instanceIndex];
                    uint instanceErrors = ValidateInstance(
                        instance,
                        drawGroupCount);
                    if (instanceErrors != 0u)
                    {
                        if (viewIndex == 0)
                        {
                            violationCount++;
                            errorFlags |= instanceErrors;
                        }
                        AddRejected(
                            bins,
                            visibleBinCount,
                            includeCulledTail,
                            instanceIndex);
                        continue;
                    }

                    Vector4 view = viewParameters[viewIndex];
                    if (!(view.w > 0f))
                    {
                        if (instanceIndex == 0)
                        {
                            violationCount++;
                            errorFlags |= (uint)
                                GpuDrivenInstanceErrorFlags
                                    .InvalidViewLodScale;
                        }
                        AddRejected(
                            bins,
                            visibleBinCount,
                            includeCulledTail,
                            instanceIndex);
                        continue;
                    }

                    uint viewBit = 1u << viewIndex;
                    Vector3 center = new Vector3(
                        instance.PositionRadius.x,
                        instance.PositionRadius.y,
                        instance.PositionRadius.z);
                    if ((instance.ViewMask & viewBit) == 0u ||
                        !IsSphereVisible(
                            center,
                            instance.PositionRadius.w,
                            viewPlanes,
                            viewIndex))
                    {
                        AddRejected(
                            bins,
                            visibleBinCount,
                            includeCulledTail,
                            instanceIndex);
                        continue;
                    }

                    Vector3 camera = new Vector3(view.x, view.y, view.z);
                    float scaledDistance =
                        Vector3.Distance(center, camera) * view.w;
                    int selectedLod = SelectLod(instance, scaledDistance);
                    if (selectedLod < 0)
                    {
                        AddRejected(
                            bins,
                            visibleBinCount,
                            includeCulledTail,
                            instanceIndex);
                        continue;
                    }

                    int drawGroup = checked(
                        (int)instance.DrawGroupBase + selectedLod);
                    int binIndex = checked(
                        viewIndex * drawGroupCount + drawGroup);
                    bins[binIndex].Add((uint)instanceIndex);
                }
            }

            var counts = new uint[totalBinCount];
            var offsets = new uint[totalBinCount + 1];
            uint running = 0u;
            for (int bin = 0; bin < totalBinCount; bin++)
            {
                offsets[bin] = running;
                counts[bin] = checked((uint)bins[bin].Count);
                running = checked(running + counts[bin]);
            }
            offsets[totalBinCount] = running;

            var grouped = new uint[checked((int)running)];
            for (int bin = 0; bin < totalBinCount; bin++)
            {
                bins[bin].CopyTo(grouped, checked((int)offsets[bin]));
            }

            var indirect = new uint[
                checked(
                    visibleBinCount *
                    GpuDrivenInstancePipeline.IndirectArgumentWordCount)];
            for (int bin = 0; bin < visibleBinCount; bin++)
            {
                int group = bin % drawGroupCount;
                GpuDrawTemplate draw = drawTemplates[group];
                int argumentBase = checked(
                    bin *
                    GpuDrivenInstancePipeline.IndirectArgumentWordCount);
                indirect[argumentBase + 0] = draw.IndexCountPerInstance;
                indirect[argumentBase + 1] = counts[bin];
                indirect[argumentBase + 2] = draw.StartIndex;
                indirect[argumentBase + 3] = draw.BaseVertex;
                indirect[argumentBase + 4] = offsets[bin];
            }

            return new CpuGpuDrivenInstanceResult(
                counts,
                offsets,
                grouped,
                indirect,
                violationCount,
                errorFlags);
        }

        internal static uint[] CanonicalizeBins(
            uint[] values,
            uint[] offsets,
            uint[] counts)
        {
            if (values == null || offsets == null || counts == null)
            {
                throw new ArgumentNullException();
            }
            if (offsets.Length != counts.Length + 1)
            {
                throw new ArgumentException(
                    "Offsets must include one terminal entry.");
            }

            var canonical = (uint[])values.Clone();
            for (int bin = 0; bin < counts.Length; bin++)
            {
                int start = checked((int)offsets[bin]);
                int count = checked((int)counts[bin]);
                if (offsets[bin + 1] != offsets[bin] + counts[bin] ||
                    start < 0 ||
                    count < 0 ||
                    start > canonical.Length ||
                    count > canonical.Length - start)
                {
                    throw new ArgumentException($"Invalid bin {bin}.");
                }
                Array.Sort(canonical, start, count);
            }
            if (offsets[offsets.Length - 1] !=
                (uint)canonical.Length)
            {
                throw new ArgumentException(
                    "Terminal offset does not cover the stream.");
            }
            return canonical;
        }

        internal static Vector4[] CreateBoxPlanes(
            int viewCount,
            float extent)
        {
            if (viewCount < 1 || !(extent > 0f))
            {
                throw new ArgumentOutOfRangeException();
            }
            var planes = new Vector4[
                viewCount *
                GpuDrivenInstancePipeline.FrustumPlaneCount];
            for (int view = 0; view < viewCount; view++)
            {
                int offset =
                    view * GpuDrivenInstancePipeline.FrustumPlaneCount;
                planes[offset + 0] = new Vector4(1f, 0f, 0f, extent);
                planes[offset + 1] = new Vector4(-1f, 0f, 0f, extent);
                planes[offset + 2] = new Vector4(0f, 1f, 0f, extent);
                planes[offset + 3] = new Vector4(0f, -1f, 0f, extent);
                planes[offset + 4] = new Vector4(0f, 0f, 1f, extent);
                planes[offset + 5] = new Vector4(0f, 0f, -1f, extent);
            }
            return planes;
        }

        private static uint ValidateInstance(
            GpuInstanceState instance,
            int drawGroupCount)
        {
            uint flags = 0u;
            if (!(instance.PositionRadius.w >= 0f))
            {
                flags |= (uint)GpuDrivenInstanceErrorFlags.InvalidRadius;
            }

            uint lodCount = instance.LodCount;
            if (lodCount < 1u ||
                lodCount > GpuInstanceState.MaximumLodCount)
            {
                flags |= (uint)
                    GpuDrivenInstanceErrorFlags.InvalidLodContract;
                return flags;
            }

            float previous = 0f;
            for (uint lod = 0u; lod < lodCount; lod++)
            {
                float distance = LodDistance(
                    instance.LodDistances,
                    checked((int)lod));
                if (!(distance > 0f) || distance < previous)
                {
                    flags |= (uint)
                        GpuDrivenInstanceErrorFlags.InvalidLodContract;
                }
                previous = distance;
            }

            if (instance.DrawGroupBase >= (uint)drawGroupCount ||
                lodCount > (uint)drawGroupCount - instance.DrawGroupBase)
            {
                flags |= (uint)
                    GpuDrivenInstanceErrorFlags.InvalidDrawGroupRange;
            }
            return flags;
        }

        private static void AddRejected(
            List<uint>[] bins,
            int visibleBinCount,
            bool includeCulledTail,
            int instanceIndex)
        {
            if (includeCulledTail)
            {
                bins[visibleBinCount].Add(checked((uint)instanceIndex));
            }
        }

        private static bool IsSphereVisible(
            Vector3 center,
            float radius,
            Vector4[] planes,
            int viewIndex)
        {
            int planeBase =
                viewIndex * GpuDrivenInstancePipeline.FrustumPlaneCount;
            for (int planeIndex = 0;
                 planeIndex < GpuDrivenInstancePipeline.FrustumPlaneCount;
                 planeIndex++)
            {
                Vector4 plane = planes[planeBase + planeIndex];
                float distance =
                    plane.x * center.x +
                    plane.y * center.y +
                    plane.z * center.z +
                    plane.w;
                if (distance < -radius)
                {
                    return false;
                }
            }
            return true;
        }

        private static int SelectLod(
            GpuInstanceState instance,
            float scaledDistance)
        {
            for (int lod = 0; lod < instance.LodCount; lod++)
            {
                if (scaledDistance <=
                    LodDistance(instance.LodDistances, lod))
                {
                    return lod;
                }
            }
            return -1;
        }

        private static float LodDistance(Vector4 distances, int lod)
        {
            switch (lod)
            {
                case 0:
                    return distances.x;
                case 1:
                    return distances.y;
                case 2:
                    return distances.z;
                case 3:
                    return distances.w;
                default:
                    throw new ArgumentOutOfRangeException(nameof(lod));
            }
        }
    }
}
