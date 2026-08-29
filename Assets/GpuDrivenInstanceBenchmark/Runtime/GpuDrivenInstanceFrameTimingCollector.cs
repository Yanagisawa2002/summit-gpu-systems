using System;
using UnityEngine;

internal enum GpuDrivenInstanceFrameTimingStatus
{
    Valid = 0,
    FeatureUnavailable,
    NoTimingAvailable,
    DuplicateFrameStartTimestamp,
    NonMonotonicFrameStartTimestamp,
    InvalidCpuTimerFrequency,
    InvalidGpuTimerFrequency,
    InvalidTimestamp,
    InvalidMetric,
}

internal enum GpuDrivenInstanceSubmissionWindowStatus
{
    Valid = 0,
    FrameTimingUnavailable,
    InvalidCpuTimerFrequency,
    InvalidTimestamp,
    InvalidMetric,
}

internal readonly struct GpuDrivenInstanceFrameTimingRaw
{
    public GpuDrivenInstanceFrameTimingRaw(
        double cpuFrameMs,
        double cpuMainThreadMs,
        double cpuRenderThreadMs,
        double gpuFrameMs,
        ulong frameStartTimestamp,
        ulong firstSubmitTimestamp,
        ulong cpuTimePresentCalled,
        ulong cpuTimeFrameComplete)
    {
        CpuFrameMs = cpuFrameMs;
        CpuMainThreadMs = cpuMainThreadMs;
        CpuRenderThreadMs = cpuRenderThreadMs;
        GpuFrameMs = gpuFrameMs;
        FrameStartTimestamp = frameStartTimestamp;
        FirstSubmitTimestamp = firstSubmitTimestamp;
        CpuTimePresentCalled = cpuTimePresentCalled;
        CpuTimeFrameComplete = cpuTimeFrameComplete;
    }

    public double CpuFrameMs { get; }

    public double CpuMainThreadMs { get; }

    public double CpuRenderThreadMs { get; }

    public double GpuFrameMs { get; }

    public ulong FrameStartTimestamp { get; }

    public ulong FirstSubmitTimestamp { get; }

    public ulong CpuTimePresentCalled { get; }

    public ulong CpuTimeFrameComplete { get; }

    public static GpuDrivenInstanceFrameTimingRaw FromFrameTiming(
        FrameTiming timing)
    {
        return new GpuDrivenInstanceFrameTimingRaw(
            timing.cpuFrameTime,
            timing.cpuMainThreadFrameTime,
            timing.cpuRenderThreadFrameTime,
            timing.gpuFrameTime,
            timing.frameStartTimestamp,
            timing.firstSubmitTimestamp,
            timing.cpuTimePresentCalled,
            timing.cpuTimeFrameComplete);
    }
}

internal readonly struct GpuDrivenInstanceFrameTimingSample
{
    private GpuDrivenInstanceFrameTimingSample(
        GpuDrivenInstanceFrameTimingStatus status,
        GpuDrivenInstanceSubmissionWindowStatus submissionWindowStatus,
        ulong frameStartTimestamp,
        ulong firstSubmitTimestamp,
        ulong cpuTimePresentCalled,
        ulong cpuTimeFrameComplete,
        double cpuFrameMs,
        double cpuMainThreadMs,
        double cpuRenderThreadMs,
        double gpuFrameMs,
        double cpuSubmissionMs)
    {
        Status = status;
        SubmissionWindowStatus = submissionWindowStatus;
        FrameStartTimestamp = frameStartTimestamp;
        FirstSubmitTimestamp = firstSubmitTimestamp;
        CpuTimePresentCalled = cpuTimePresentCalled;
        CpuTimeFrameComplete = cpuTimeFrameComplete;
        CpuFrameMs = cpuFrameMs;
        CpuMainThreadMs = cpuMainThreadMs;
        CpuRenderThreadMs = cpuRenderThreadMs;
        GpuFrameMs = gpuFrameMs;
        CpuSubmissionMs = cpuSubmissionMs;
    }

    public bool Valid => Status == GpuDrivenInstanceFrameTimingStatus.Valid;

    public bool SubmissionWindowValid =>
        SubmissionWindowStatus ==
        GpuDrivenInstanceSubmissionWindowStatus.Valid;

    public bool CpuRenderThreadValid =>
        IsPositiveFinite(CpuRenderThreadMs);

    public bool GpuFrameValid => IsPositiveFinite(GpuFrameMs);

    public GpuDrivenInstanceFrameTimingStatus Status { get; }

    public GpuDrivenInstanceSubmissionWindowStatus SubmissionWindowStatus
    {
        get;
    }

    public ulong FrameStartTimestamp { get; }

    public ulong FirstSubmitTimestamp { get; }

    public ulong CpuTimePresentCalled { get; }

    public ulong CpuTimeFrameComplete { get; }

    public double CpuFrameMs { get; }

    public double CpuMainThreadMs { get; }

    public double CpuRenderThreadMs { get; }

    public double GpuFrameMs { get; }

    public double CpuSubmissionMs { get; }

    public static GpuDrivenInstanceFrameTimingSample CreateValid(
        GpuDrivenInstanceFrameTimingRaw raw,
        double cpuSubmissionMs,
        GpuDrivenInstanceSubmissionWindowStatus submissionWindowStatus)
    {
        return new GpuDrivenInstanceFrameTimingSample(
            GpuDrivenInstanceFrameTimingStatus.Valid,
            submissionWindowStatus,
            raw.FrameStartTimestamp,
            raw.FirstSubmitTimestamp,
            raw.CpuTimePresentCalled,
            raw.CpuTimeFrameComplete,
            raw.CpuFrameMs,
            raw.CpuMainThreadMs,
            NormalizeOptionalMetric(raw.CpuRenderThreadMs),
            NormalizeOptionalMetric(raw.GpuFrameMs),
            cpuSubmissionMs);
    }

    private static double NormalizeOptionalMetric(double value)
    {
        return IsPositiveFinite(value) ? value : double.NaN;
    }

    private static bool IsPositiveFinite(double value)
    {
        return value > 0.0 &&
            !double.IsNaN(value) &&
            !double.IsInfinity(value);
    }

    public static GpuDrivenInstanceFrameTimingSample Unavailable(
        GpuDrivenInstanceFrameTimingStatus status,
        ulong frameStartTimestamp = 0u)
    {
        if (status == GpuDrivenInstanceFrameTimingStatus.Valid)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new GpuDrivenInstanceFrameTimingSample(
            status,
            GpuDrivenInstanceSubmissionWindowStatus
                .FrameTimingUnavailable,
            frameStartTimestamp,
            0u,
            0u,
            0u,
            double.NaN,
            double.NaN,
            double.NaN,
            double.NaN,
            double.NaN);
    }

    public static GpuDrivenInstanceFrameTimingSample Unavailable(
        GpuDrivenInstanceFrameTimingStatus status,
        GpuDrivenInstanceFrameTimingRaw raw)
    {
        if (status == GpuDrivenInstanceFrameTimingStatus.Valid)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new GpuDrivenInstanceFrameTimingSample(
            status,
            GpuDrivenInstanceSubmissionWindowStatus
                .FrameTimingUnavailable,
            raw.FrameStartTimestamp,
            raw.FirstSubmitTimestamp,
            raw.CpuTimePresentCalled,
            raw.CpuTimeFrameComplete,
            double.NaN,
            double.NaN,
            double.NaN,
            double.NaN,
            double.NaN);
    }
}

/// <summary>
/// Collects unique Unity frame-timing rows without allocating after
/// construction. Invalid or unsupported timing is represented explicitly by
/// an unavailable sample whose metric values are NaN, never zero.
/// </summary>
internal sealed class GpuDrivenInstanceFrameTimingCollector
{
    private readonly FrameTiming[] latestTimings = new FrameTiming[1];
    private bool hasLastFrameStartTimestamp;
    private ulong lastFrameStartTimestamp;

    public void CaptureFrameTimings()
    {
        FrameTimingManager.CaptureFrameTimings();
    }

    public GpuDrivenInstanceFrameTimingSample CollectLatest()
    {
        if (!FrameTimingManager.IsFeatureEnabled())
        {
            return GpuDrivenInstanceFrameTimingSample.Unavailable(
                GpuDrivenInstanceFrameTimingStatus.FeatureUnavailable);
        }

        uint count = FrameTimingManager.GetLatestTimings(1u, latestTimings);
        if (count == 0u)
        {
            return GpuDrivenInstanceFrameTimingSample.Unavailable(
                GpuDrivenInstanceFrameTimingStatus.NoTimingAvailable);
        }

        return Collect(
            GpuDrivenInstanceFrameTimingRaw.FromFrameTiming(
                latestTimings[0]),
            FrameTimingManager.GetCpuTimerFrequency(),
            FrameTimingManager.GetGpuTimerFrequency());
    }

    public bool TryCollectLatest(
        out GpuDrivenInstanceFrameTimingSample sample)
    {
        sample = CollectLatest();
        return sample.Valid;
    }

    public GpuDrivenInstanceFrameTimingSample Collect(
        GpuDrivenInstanceFrameTimingRaw raw,
        ulong cpuTimerFrequency,
        ulong gpuTimerFrequency)
    {
        ulong frameStartTimestamp = raw.FrameStartTimestamp;
        if (frameStartTimestamp != 0u && hasLastFrameStartTimestamp)
        {
            if (frameStartTimestamp == lastFrameStartTimestamp)
            {
                return GpuDrivenInstanceFrameTimingSample.Unavailable(
                    GpuDrivenInstanceFrameTimingStatus
                        .DuplicateFrameStartTimestamp,
                    raw);
            }

            if (frameStartTimestamp < lastFrameStartTimestamp)
            {
                return GpuDrivenInstanceFrameTimingSample.Unavailable(
                    GpuDrivenInstanceFrameTimingStatus
                        .NonMonotonicFrameStartTimestamp,
                    raw);
            }
        }

        if (frameStartTimestamp != 0u)
        {
            lastFrameStartTimestamp = frameStartTimestamp;
            hasLastFrameStartTimestamp = true;
        }

        return Evaluate(raw, cpuTimerFrequency, gpuTimerFrequency);
    }

    public bool TryCollect(
        GpuDrivenInstanceFrameTimingRaw raw,
        ulong cpuTimerFrequency,
        ulong gpuTimerFrequency,
        out GpuDrivenInstanceFrameTimingSample sample)
    {
        sample = Collect(raw, cpuTimerFrequency, gpuTimerFrequency);
        return sample.Valid;
    }

    public void Reset()
    {
        hasLastFrameStartTimestamp = false;
        lastFrameStartTimestamp = 0u;
    }

    public static GpuDrivenInstanceFrameTimingSample Evaluate(
        GpuDrivenInstanceFrameTimingRaw raw,
        ulong cpuTimerFrequency,
        ulong gpuTimerFrequency)
    {
        // CPU total and main-thread time are the required frame-tail
        // evidence. Unity can legitimately publish zero for render-thread
        // or full-GPU frame time on very light frames; those optional metrics
        // are normalized to NaN independently instead of invalidating the
        // CPU frame-tail row or representing unavailable data as zero.
        if (!IsPositiveFinite(raw.CpuFrameMs) ||
            !IsPositiveFinite(raw.CpuMainThreadMs))
        {
            return GpuDrivenInstanceFrameTimingSample.Unavailable(
                GpuDrivenInstanceFrameTimingStatus.InvalidMetric,
                raw);
        }

        double cpuSubmissionMs = double.NaN;
        GpuDrivenInstanceSubmissionWindowStatus submissionStatus;
        if (cpuTimerFrequency == 0u)
        {
            submissionStatus = GpuDrivenInstanceSubmissionWindowStatus
                .InvalidCpuTimerFrequency;
        }
        else if (!HasValidSubmissionTimestampOrder(raw))
        {
            submissionStatus = GpuDrivenInstanceSubmissionWindowStatus
                .InvalidTimestamp;
        }
        else
        {
            ulong submissionTicks =
                raw.CpuTimePresentCalled - raw.FirstSubmitTimestamp;
            cpuSubmissionMs =
                ((double)submissionTicks / cpuTimerFrequency) * 1000.0;
            submissionStatus = IsPositiveFinite(cpuSubmissionMs)
                ? GpuDrivenInstanceSubmissionWindowStatus.Valid
                : GpuDrivenInstanceSubmissionWindowStatus.InvalidMetric;
            if (submissionStatus !=
                GpuDrivenInstanceSubmissionWindowStatus.Valid)
            {
                cpuSubmissionMs = double.NaN;
            }
        }

        return GpuDrivenInstanceFrameTimingSample.CreateValid(
            raw,
            cpuSubmissionMs,
            submissionStatus);
    }

    private static bool HasValidSubmissionTimestampOrder(
        GpuDrivenInstanceFrameTimingRaw raw)
    {
        return raw.FrameStartTimestamp != 0u &&
            raw.FirstSubmitTimestamp != 0u &&
            raw.CpuTimePresentCalled != 0u &&
            raw.FrameStartTimestamp <= raw.FirstSubmitTimestamp &&
            raw.FirstSubmitTimestamp < raw.CpuTimePresentCalled;
    }

    private static bool IsPositiveFinite(double value)
    {
        return value > 0.0 &&
            !double.IsNaN(value) &&
            !double.IsInfinity(value);
    }
}
