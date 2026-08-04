using System;
using Summit.GpuTimestamps;
using UnityEngine;
using UnityEngine.Rendering;

internal sealed class GpuResidencyNativeTimestampBackend : IDisposable
{
    private readonly GpuTimestampSession session;
    private readonly CommandBuffer frequencyCommands;
    private bool disposed;

    private GpuResidencyNativeTimestampBackend(GpuTimestampSession session)
    {
        this.session = session;
        frequencyCommands = new CommandBuffer
        {
            name = "GPU.Residency/TimestampFrequency"
        };
        session.RecordFrequencyInitialization(frequencyCommands);
    }

    public GpuTimestampSupport Support => session.Support;

    public static bool TryCreate(
        out GpuResidencyNativeTimestampBackend backend,
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
            backend = new GpuResidencyNativeTimestampBackend(session);
            return true;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public void InitializeFrequency()
    {
        ThrowIfDisposed();
        Graphics.ExecuteCommandBuffer(frequencyCommands);
    }

    public GpuTimestampStatus Acquire(
        ulong tag,
        GpuTimestampSampleFlags flags,
        int frame,
        out GpuTimestampToken token)
    {
        ThrowIfDisposed();
        return session.Acquire(tag, flags, frame, out token);
    }

    public GpuTimestampStatus MarkSubmitted(GpuTimestampToken token)
    {
        ThrowIfDisposed();
        return session.MarkSubmitted(token);
    }

    public void RecordBegin(int index, CommandBuffer commands)
    {
        session.Scopes[index].RecordBegin(commands);
    }

    public void RecordEnd(int index, CommandBuffer commands)
    {
        session.Scopes[index].RecordEnd(commands);
    }

    public GpuTimestampStatus TryConsume(
        GpuTimestampToken token,
        int frame,
        out GpuTimestampResult result)
    {
        ThrowIfDisposed();
        return session.TryConsume(token, frame, out result);
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
                nameof(GpuResidencyNativeTimestampBackend));
        }
    }
}
