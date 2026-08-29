using System;
using Summit.GpuDrivenInstances;
using UnityEngine;

internal static class GpuDrivenInstanceInputGenerator
{
    internal const string VisibilityLayoutId =
        "seeded-coprime-permutation-v1";

    public static int ParseVisibilityPercent(string visibility)
    {
        if (string.Equals(
                visibility,
                "visible5",
                StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }
        if (string.Equals(
                visibility,
                "visible25",
                StringComparison.OrdinalIgnoreCase))
        {
            return 25;
        }
        if (string.Equals(
                visibility,
                "visible75",
                StringComparison.OrdinalIgnoreCase))
        {
            return 75;
        }
        if (string.Equals(
                visibility,
                "visible100",
                StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }
        throw new ArgumentOutOfRangeException(
            nameof(visibility),
            visibility,
            "Supported visibility cells are visible5, visible25, " +
            "visible75, and visible100.");
    }

    public static int Populate(
        GpuInstanceState[] instances,
        Vector4[] viewPlanes,
        Vector4[] viewParameters,
        GpuDrawTemplate[] drawTemplates,
        string visibility,
        int seed)
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

        int visibilityPercent = ParseVisibilityPercent(visibility);
        int visibleCount = checked(
            (int)((long)instances.Length * visibilityPercent / 100L));
        int permutationStride = SelectPermutationStride(
            instances.Length,
            seed);
        int permutationOffset = instances.Length == 0
            ? 0
            : checked((int)(
                Mix32(unchecked((uint)seed) ^ 0xA511E9B3u) %
                checked((uint)instances.Length)));
        uint viewMask = viewParameters.Length == 32
            ? uint.MaxValue
            : (1u << viewParameters.Length) - 1u;
        for (int index = 0; index < instances.Length; index++)
        {
            uint hash = Mix32(
                checked((uint)index) ^ unchecked((uint)seed));
            int visibilityRank = checked((int)(
                ((long)index * permutationStride + permutationOffset) %
                instances.Length));
            bool visible = visibilityRank < visibleCount;
            float x = visible
                ? SignedUnit(hash) * 50f
                : 1000f + (hash & 1023u);
            float y = SignedUnit(Mix32(hash + 1u)) * 50f;
            float z = SignedUnit(Mix32(hash + 2u)) * 50f;
            uint drawGroup = hash % checked((uint)drawTemplates.Length);
            instances[index] = new GpuInstanceState(
                new Vector3(x, y, z),
                0.5f,
                new Vector4(4096f, 0f, 0f, 0f),
                checked((uint)index),
                drawGroup,
                1u,
                viewMask);
        }

        for (int view = 0; view < viewParameters.Length; view++)
        {
            int planeBase =
                view * GpuDrivenInstancePipeline.FrustumPlaneCount;
            const float extent = 100f;
            viewPlanes[planeBase + 0] =
                new Vector4(1f, 0f, 0f, extent);
            viewPlanes[planeBase + 1] =
                new Vector4(-1f, 0f, 0f, extent);
            viewPlanes[planeBase + 2] =
                new Vector4(0f, 1f, 0f, extent);
            viewPlanes[planeBase + 3] =
                new Vector4(0f, -1f, 0f, extent);
            viewPlanes[planeBase + 4] =
                new Vector4(0f, 0f, 1f, extent);
            viewPlanes[planeBase + 5] =
                new Vector4(0f, 0f, -1f, extent);
            viewParameters[view] = new Vector4(
                view * 2f,
                0f,
                0f,
                1f);
        }

        for (int group = 0; group < drawTemplates.Length; group++)
        {
            drawTemplates[group] = new GpuDrawTemplate(
                checked((uint)(36 + group)),
                checked((uint)(group * 7)),
                checked((uint)(group * 3)));
        }
        return visibleCount;
    }

    private static float SignedUnit(uint value)
    {
        return ((value & 0x00FFFFFFu) / 8388607.5f) - 1f;
    }

    private static int SelectPermutationStride(int count, int seed)
    {
        if (count <= 1)
        {
            return 1;
        }
        if (count == 2)
        {
            return 1;
        }

        uint mixed = Mix32(unchecked((uint)seed) ^ 0x63D83595u);
        int candidate = checked(2 + (int)(mixed % (uint)(count - 2)));
        while (GreatestCommonDivisor(candidate, count) != 1)
        {
            candidate++;
            if (candidate >= count)
            {
                candidate = 2;
            }
        }
        return candidate;
    }

    private static int GreatestCommonDivisor(int left, int right)
    {
        while (right != 0)
        {
            int remainder = left % right;
            left = right;
            right = remainder;
        }
        return left;
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
