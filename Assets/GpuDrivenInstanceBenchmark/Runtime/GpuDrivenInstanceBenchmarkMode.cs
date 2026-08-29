using System;

internal enum GpuDrivenInstanceBenchmarkMode
{
    FilteredBinning,
    HierarchicalCulling
}

internal static class GpuDrivenInstanceBenchmarkModes
{
    public const string FilteredBinningId = "filtered-binning";
    public const string HierarchicalCullingId = "hierarchical-culling";

    public static GpuDrivenInstanceBenchmarkMode Parse(string value)
    {
        if (string.Equals(
                value,
                FilteredBinningId,
                StringComparison.OrdinalIgnoreCase))
        {
            return GpuDrivenInstanceBenchmarkMode.FilteredBinning;
        }
        if (string.Equals(
                value,
                HierarchicalCullingId,
                StringComparison.OrdinalIgnoreCase))
        {
            return GpuDrivenInstanceBenchmarkMode.HierarchicalCulling;
        }
        throw new ArgumentOutOfRangeException(
            nameof(value),
            value,
            "Supported benchmark modes are filtered-binning and " +
            "hierarchical-culling.");
    }

    public static string ToId(GpuDrivenInstanceBenchmarkMode mode)
    {
        switch (mode)
        {
            case GpuDrivenInstanceBenchmarkMode.FilteredBinning:
                return FilteredBinningId;
            case GpuDrivenInstanceBenchmarkMode.HierarchicalCulling:
                return HierarchicalCullingId;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }
}
