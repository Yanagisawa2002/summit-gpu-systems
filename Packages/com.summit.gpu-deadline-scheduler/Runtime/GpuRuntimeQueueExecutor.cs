using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuDeadlineScheduler
{
    /// <summary>One persistent command buffer/fence per scheduler slot. Submit in topological order;
    /// never group an entire DAG by queue (that can create a cross-queue deadlock).
    /// Application resources must survive until completion. Dispose only after PollCompletions drains all jobs.</summary>
    public sealed class GpuRuntimeQueueExecutor : IDisposable
    {
        private readonly GpuRuntimeScheduler scheduler;
        private readonly CommandBuffer[] commands;
        private readonly GraphicsFence[] fences;
        private readonly long[] tickets;
        private bool disposed;

        public GpuRuntimeQueueExecutor(GpuRuntimeScheduler scheduler)
        {
            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            commands = new CommandBuffer[scheduler.Capacity];
            fences = new GraphicsFence[scheduler.Capacity]; tickets = new long[scheduler.Capacity];
            for (int i = 0; i < commands.Length; i++) commands[i] = new CommandBuffer { name = "GPU Runtime DAG Job" };
        }

        /// <summary>The recorder must not submit commands or mutate the scheduler. Its output resources
        /// are application-owned. Poll completions before admission/reclamation. Async evidence defaults closed.</summary>
        public bool SubmitNext(long now, Action<CommandBuffer, GpuRuntimeDispatch> record,
            out GpuRuntimeDispatch dispatch, GpuAsyncAdmissionEvidence evidence = default)
        {
            ThrowIfDisposed();
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (!SystemInfo.supportsGraphicsFence) throw new NotSupportedException("Graphics fences are required.");
            if (!scheduler.TryPrepare(now, SystemInfo.supportsAsyncCompute, evidence, out dispatch)) return false;
            CommandBuffer buffer = commands[dispatch.Slot];
            buffer.Clear();
            bool async = dispatch.Queue != GpuDeadlineQueue.MainGraphics;
            buffer.SetExecutionFlags(async ? CommandBufferExecutionFlags.AsyncCompute : CommandBufferExecutionFlags.None);
            for (int p = 0; p < scheduler.Capacity; p++)
            {
                if (!scheduler.DependsOn(dispatch.Slot, p) || scheduler.State(p) == GpuRuntimeJobState.Completed) continue;
                if (tickets[p] != scheduler.Ticket(p)) throw new InvalidOperationException("Prerequisite was not submitted by this executor.");
                if (scheduler.Queue(p) != dispatch.Queue) buffer.WaitOnAsyncGraphicsFence(fences[p]);
            }
            record(buffer, dispatch);
            GraphicsFence fence = buffer.CreateGraphicsFence(GraphicsFenceType.AsyncQueueSynchronisation,
                SynchronisationStageFlags.AllGPUOperations);
            if (async) Graphics.ExecuteCommandBufferAsync(buffer, ComputeQueueType.Urgent);
            else Graphics.ExecuteCommandBuffer(buffer);
            scheduler.MarkSubmitted(dispatch);
            fences[dispatch.Slot] = fence; tickets[dispatch.Slot] = dispatch.Ticket;
            return true;
        }

        public int PollCompletions()
        {
            ThrowIfDisposed(); int count = 0;
            for (int i = 0; i < commands.Length; i++)
                if (scheduler.State(i) == GpuRuntimeJobState.Submitted && scheduler.Ticket(i) == tickets[i] && fences[i].passed &&
                    scheduler.TryComplete(i, tickets[i])) count++;
            return count;
        }

        /// <summary>Record a GPU join for all submitted jobs. This does not block the CPU.</summary>
        public void RecordJoin(CommandBuffer destination)
        {
            ThrowIfDisposed();
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            for (int i = 0; i < commands.Length; i++)
                if (scheduler.State(i) == GpuRuntimeJobState.Submitted && scheduler.Ticket(i) == tickets[i])
                    destination.WaitOnAsyncGraphicsFence(fences[i]);
        }

        public void Dispose()
        {
            if (disposed) return;
            PollCompletions();
            if (scheduler.InFlightCount != 0) throw new InvalidOperationException("Wait for GPU completion before disposing the executor/resources.");
            disposed = true;
            for (int i = 0; i < commands.Length; i++) commands[i].Dispose();
        }

        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(GpuRuntimeQueueExecutor)); }
    }
}
