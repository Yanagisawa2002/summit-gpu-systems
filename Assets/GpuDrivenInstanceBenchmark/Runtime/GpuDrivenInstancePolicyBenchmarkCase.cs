using System;
using Summit.GpuAutotuning;
using Summit.GpuDrivenInstances;
using Summit.GpuPrimitives;

internal enum GpuDrivenInstancePolicyBenchmarkCaseKind
{
    SafeBaseline = 0,
    ForcedSelected = 1,
    ActualAuto = 2,
    ForcedFullFlat = 3,
    ForcedFullHierarchy = 4,
    ForcedDirtyFlat = 5,
    ForcedDirtyHierarchy = 6,
    ForcedNoneFlat = 7,
    ForcedNoneHierarchy = 8,
}

internal enum GpuDrivenInstancePolicyDecisionSource
{
    SafeBaseline = 0,
    ForcedSelected = 1,
    ActualAuto = 2,
    ForcedCalibration = 3,
}

/// <summary>
/// Frozen case identity for policy holdout replay and calibration sweeps.
/// Only ActualAuto authorizes a selector call.
/// </summary>
internal readonly struct GpuDrivenInstancePolicyBenchmarkCase
{
    private readonly GpuDrivenInstancePolicyDecision suppliedDecision;

    private GpuDrivenInstancePolicyBenchmarkCase(
        GpuDrivenInstancePolicyBenchmarkCaseKind kind,
        bool hasSuppliedDecision,
        in GpuDrivenInstancePolicyDecision decision)
    {
        Kind = kind;
        HasSuppliedDecision = hasSuppliedDecision;
        suppliedDecision = decision;
    }

    internal GpuDrivenInstancePolicyBenchmarkCaseKind Kind { get; }

    internal bool HasSuppliedDecision { get; }

    internal bool InvokesSelector =>
        Kind == GpuDrivenInstancePolicyBenchmarkCaseKind.ActualAuto;

    internal GpuDrivenInstancePolicyDecision SuppliedDecision
    {
        get
        {
            if (!HasSuppliedDecision)
            {
                throw new InvalidOperationException(
                    "This benchmark case has no supplied policy decision.");
            }
            return suppliedDecision;
        }
    }

    internal string CaseId
    {
        get
        {
            switch (Kind)
            {
                case GpuDrivenInstancePolicyBenchmarkCaseKind.SafeBaseline:
                    return "gpu-driven-policy/safe-baseline";
                case GpuDrivenInstancePolicyBenchmarkCaseKind.ForcedSelected:
                    return "gpu-driven-policy/forced-selected";
                case GpuDrivenInstancePolicyBenchmarkCaseKind.ActualAuto:
                    return "gpu-driven-policy/actual-auto";
                case GpuDrivenInstancePolicyBenchmarkCaseKind.ForcedFullFlat:
                    return "gpu-driven-policy/calibration/full-flat";
                case GpuDrivenInstancePolicyBenchmarkCaseKind
                    .ForcedFullHierarchy:
                    return "gpu-driven-policy/calibration/full-hierarchy";
                case GpuDrivenInstancePolicyBenchmarkCaseKind
                    .ForcedDirtyFlat:
                    return "gpu-driven-policy/calibration/dirty-flat";
                case GpuDrivenInstancePolicyBenchmarkCaseKind
                    .ForcedDirtyHierarchy:
                    return "gpu-driven-policy/calibration/dirty-hierarchy";
                case GpuDrivenInstancePolicyBenchmarkCaseKind.ForcedNoneFlat:
                    return "gpu-driven-policy/calibration/none-flat";
                case GpuDrivenInstancePolicyBenchmarkCaseKind
                    .ForcedNoneHierarchy:
                    return "gpu-driven-policy/calibration/none-hierarchy";
                default:
                    throw new ArgumentOutOfRangeException(nameof(Kind));
            }
        }
    }

    internal string Marker
    {
        get
        {
            switch (Kind)
            {
                case GpuDrivenInstancePolicyBenchmarkCaseKind.SafeBaseline:
                    return "GPU.DrivenPolicy/SafeBaseline";
                case GpuDrivenInstancePolicyBenchmarkCaseKind.ForcedSelected:
                    return "GPU.DrivenPolicy/ForcedSelected";
                case GpuDrivenInstancePolicyBenchmarkCaseKind.ActualAuto:
                    return "GPU.DrivenPolicy/ActualAuto";
                case GpuDrivenInstancePolicyBenchmarkCaseKind.ForcedFullFlat:
                    return "GPU.DrivenPolicy/Calibration/FullFlat";
                case GpuDrivenInstancePolicyBenchmarkCaseKind
                    .ForcedFullHierarchy:
                    return "GPU.DrivenPolicy/Calibration/FullHierarchy";
                case GpuDrivenInstancePolicyBenchmarkCaseKind
                    .ForcedDirtyFlat:
                    return "GPU.DrivenPolicy/Calibration/DirtyFlat";
                case GpuDrivenInstancePolicyBenchmarkCaseKind
                    .ForcedDirtyHierarchy:
                    return "GPU.DrivenPolicy/Calibration/DirtyHierarchy";
                case GpuDrivenInstancePolicyBenchmarkCaseKind.ForcedNoneFlat:
                    return "GPU.DrivenPolicy/Calibration/NoneFlat";
                case GpuDrivenInstancePolicyBenchmarkCaseKind
                    .ForcedNoneHierarchy:
                    return "GPU.DrivenPolicy/Calibration/NoneHierarchy";
                default:
                    throw new ArgumentOutOfRangeException(nameof(Kind));
            }
        }
    }

    internal static GpuDrivenInstancePolicyBenchmarkCase SafeBaseline()
    {
        GpuDrivenInstancePolicyDecision decision = default;
        return new GpuDrivenInstancePolicyBenchmarkCase(
            GpuDrivenInstancePolicyBenchmarkCaseKind.SafeBaseline,
            false,
            in decision);
    }

    internal static GpuDrivenInstancePolicyBenchmarkCase ActualAuto()
    {
        GpuDrivenInstancePolicyDecision decision = default;
        return new GpuDrivenInstancePolicyBenchmarkCase(
            GpuDrivenInstancePolicyBenchmarkCaseKind.ActualAuto,
            false,
            in decision);
    }

    internal static GpuDrivenInstancePolicyBenchmarkCase ForcedSelected(
        in GpuDrivenInstancePolicyDecision decision)
    {
        return new GpuDrivenInstancePolicyBenchmarkCase(
            GpuDrivenInstancePolicyBenchmarkCaseKind.ForcedSelected,
            true,
            in decision);
    }

    internal static GpuDrivenInstancePolicyBenchmarkCase ForcedFullFlat()
    {
        return ForcedCalibration(
            GpuDrivenInstancePolicyBenchmarkCaseKind.ForcedFullFlat);
    }

    internal static GpuDrivenInstancePolicyBenchmarkCase
        ForcedFullHierarchy()
    {
        return ForcedCalibration(
            GpuDrivenInstancePolicyBenchmarkCaseKind.ForcedFullHierarchy);
    }

    internal static GpuDrivenInstancePolicyBenchmarkCase ForcedDirtyFlat()
    {
        return ForcedCalibration(
            GpuDrivenInstancePolicyBenchmarkCaseKind.ForcedDirtyFlat);
    }

    internal static GpuDrivenInstancePolicyBenchmarkCase
        ForcedDirtyHierarchy()
    {
        return ForcedCalibration(
            GpuDrivenInstancePolicyBenchmarkCaseKind.ForcedDirtyHierarchy);
    }

    internal static GpuDrivenInstancePolicyBenchmarkCase ForcedNoneFlat()
    {
        return ForcedCalibration(
            GpuDrivenInstancePolicyBenchmarkCaseKind.ForcedNoneFlat);
    }

    internal static GpuDrivenInstancePolicyBenchmarkCase
        ForcedNoneHierarchy()
    {
        return ForcedCalibration(
            GpuDrivenInstancePolicyBenchmarkCaseKind.ForcedNoneHierarchy);
    }

    internal GpuDrivenInstancePolicyResolvedDecision ResolveCalibration(
        GpuDrivenInstanceOutputMode requiredOutputMode)
    {
        GpuDrivenInstanceUploadMode uploadMode;
        GpuDrivenInstanceCullingMode cullingMode;
        switch (Kind)
        {
            case GpuDrivenInstancePolicyBenchmarkCaseKind.ForcedFullFlat:
                uploadMode = GpuDrivenInstanceUploadMode.Full;
                cullingMode = GpuDrivenInstanceCullingMode.Flat;
                break;
            case GpuDrivenInstancePolicyBenchmarkCaseKind
                .ForcedFullHierarchy:
                uploadMode = GpuDrivenInstanceUploadMode.Full;
                cullingMode = GpuDrivenInstanceCullingMode.Hierarchy;
                break;
            case GpuDrivenInstancePolicyBenchmarkCaseKind.ForcedDirtyFlat:
                uploadMode = GpuDrivenInstanceUploadMode.Dirty;
                cullingMode = GpuDrivenInstanceCullingMode.Flat;
                break;
            case GpuDrivenInstancePolicyBenchmarkCaseKind
                .ForcedDirtyHierarchy:
                uploadMode = GpuDrivenInstanceUploadMode.Dirty;
                cullingMode = GpuDrivenInstanceCullingMode.Hierarchy;
                break;
            case GpuDrivenInstancePolicyBenchmarkCaseKind.ForcedNoneFlat:
                uploadMode = GpuDrivenInstanceUploadMode.None;
                cullingMode = GpuDrivenInstanceCullingMode.Flat;
                break;
            case GpuDrivenInstancePolicyBenchmarkCaseKind
                .ForcedNoneHierarchy:
                uploadMode = GpuDrivenInstanceUploadMode.None;
                cullingMode = GpuDrivenInstanceCullingMode.Hierarchy;
                break;
            default:
                throw new InvalidOperationException(
                    "The case is not a forced calibration case.");
        }

        return new GpuDrivenInstancePolicyResolvedDecision(
            uploadMode,
            requiredOutputMode,
            cullingMode,
            GpuPrimitiveBackend.Portable,
            -1,
            GpuDrivenInstancePolicyDecisionFlags.None,
            GpuDrivenInstancePolicyDecisionSource.ForcedCalibration);
    }

    private static GpuDrivenInstancePolicyBenchmarkCase ForcedCalibration(
        GpuDrivenInstancePolicyBenchmarkCaseKind kind)
    {
        GpuDrivenInstancePolicyDecision decision = default;
        return new GpuDrivenInstancePolicyBenchmarkCase(
            kind,
            false,
            in decision);
    }
}

/// <summary>
/// Uniform execution decision for selector and non-selector benchmark cases.
/// </summary>
internal readonly struct GpuDrivenInstancePolicyResolvedDecision
{
    internal GpuDrivenInstancePolicyResolvedDecision(
        GpuDrivenInstanceUploadMode uploadMode,
        GpuDrivenInstanceOutputMode outputMode,
        GpuDrivenInstanceCullingMode cullingMode,
        GpuPrimitiveBackend primitiveBackend,
        int profileRuleIndex,
        GpuDrivenInstancePolicyDecisionFlags flags,
        GpuDrivenInstancePolicyDecisionSource source)
    {
        UploadMode = uploadMode;
        OutputMode = outputMode;
        CullingMode = cullingMode;
        PrimitiveBackend = primitiveBackend;
        ProfileRuleIndex = profileRuleIndex;
        Flags = flags;
        Source = source;
    }

    internal GpuDrivenInstanceUploadMode UploadMode { get; }

    internal GpuDrivenInstanceOutputMode OutputMode { get; }

    internal GpuDrivenInstanceCullingMode CullingMode { get; }

    internal GpuPrimitiveBackend PrimitiveBackend { get; }

    internal int ProfileRuleIndex { get; }

    internal GpuDrivenInstancePolicyDecisionFlags Flags { get; }

    internal GpuDrivenInstancePolicyDecisionSource Source { get; }

    internal bool UsesProfileRule => ProfileRuleIndex >= 0;

    internal static GpuDrivenInstancePolicyResolvedDecision FromPolicy(
        in GpuDrivenInstancePolicyDecision decision,
        GpuDrivenInstancePolicyDecisionSource source)
    {
        return new GpuDrivenInstancePolicyResolvedDecision(
            decision.UploadMode,
            decision.OutputMode,
            decision.CullingMode,
            decision.PrimitiveBackend,
            decision.ProfileRuleIndex,
            decision.Flags,
            source);
    }

    internal static GpuDrivenInstancePolicyResolvedDecision
        FromExecutionPolicy(
            in GpuDrivenInstanceExecutionPolicy decision,
            GpuDrivenInstancePolicyDecisionSource source)
    {
        return new GpuDrivenInstancePolicyResolvedDecision(
            decision.UploadMode,
            decision.OutputMode,
            decision.CullingMode,
            decision.PrimitiveBackend,
            decision.InstancePolicyRuleIndex,
            decision.Flags,
            source);
    }

    internal GpuDrivenInstancePolicyResolvedDecision WithPrimitiveBackend(
        GpuPrimitiveBackend backend,
        bool accepted)
    {
        GpuDrivenInstancePolicyDecisionFlags composedFlags = Flags;
        if (!accepted)
        {
            composedFlags |= GpuDrivenInstancePolicyDecisionFlags
                .BackendGateFallback;
        }
        return new GpuDrivenInstancePolicyResolvedDecision(
            UploadMode,
            OutputMode,
            CullingMode,
            accepted ? backend : GpuPrimitiveBackend.Portable,
            ProfileRuleIndex,
            composedFlags,
            Source);
    }
}
