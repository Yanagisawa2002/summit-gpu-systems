using System;
using Summit.GpuTimestamps;
using UnityEngine;
using UnityEngine.Rendering;

internal sealed class GpuSensorPipelineNativeTimestampBackend : IDisposable
{
    private readonly GpuTimestampSession session;
    private readonly CommandBuffer frequencyCommands;
    private bool disposed;

    private GpuSensorPipelineNativeTimestampBackend(
        GpuTimestampSession session)
    {
        this.session = session;
        frequencyCommands = new CommandBuffer
        {
            name = "GPU.SensorPipeline/NativeTimestamp/Frequency"
        };
        session.RecordFrequencyInitialization(frequencyCommands);
    }

    public GpuTimestampSupport Support => session.Support;

    public int Capacity => session.Capacity;

    public int ActiveSampleCount => session.ActiveSampleCount;

    public int ReservedSampleCount => session.ReservedSampleCount;

    public int SubmittedSampleCount => session.SubmittedSampleCount;

    public bool IsTerminal => session.IsTerminal;

    public GpuTimestampStatus TerminalStatus => session.TerminalStatus;

    public static bool TryCreate(
        out GpuSensorPipelineNativeTimestampBackend backend,
        out GpuTimestampSupport support)
    {
        backend = null;
        if (!GpuTimestampSession.TryCreate(
                out GpuTimestampSession session,
                out support))
        {
            return false;
        }
        try
        {
            backend = new GpuSensorPipelineNativeTimestampBackend(session);
            return true;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public void ExecuteFrequencyInitialization()
    {
        ThrowIfDisposed();
        Graphics.ExecuteCommandBuffer(frequencyCommands);
    }

    public GpuTimestampStatus Acquire(
        ulong userTag,
        GpuTimestampSampleFlags flags,
        int sourceFrame,
        out GpuTimestampToken token)
    {
        ThrowIfDisposed();
        return session.Acquire(userTag, flags, sourceFrame, out token);
    }

    public void RecordBegin(int scopeIndex, CommandBuffer commands)
    {
        ThrowIfDisposed();
        session.Scopes[scopeIndex].RecordBegin(commands);
    }

    public void RecordEnd(int scopeIndex, CommandBuffer commands)
    {
        ThrowIfDisposed();
        session.Scopes[scopeIndex].RecordEnd(commands);
    }

    public GpuTimestampStatus MarkSubmitted(GpuTimestampToken token)
    {
        ThrowIfDisposed();
        return session.MarkSubmitted(token);
    }

    public GpuTimestampStatus TryConsume(
        GpuTimestampToken token,
        int resultFrame,
        out GpuTimestampResult result)
    {
        ThrowIfDisposed();
        return session.TryConsume(token, resultFrame, out result);
    }

    public GpuTimestampStatus Cancel(GpuTimestampToken token)
    {
        ThrowIfDisposed();
        return session.Cancel(token);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        frequencyCommands.Dispose();
        session.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(
                nameof(GpuSensorPipelineNativeTimestampBackend));
        }
    }
}
