using System;

namespace Summit.GpuDeadlineScheduler
{
    public enum GpuDeadlinePolicy
    {
        FifoGraphics = 0,
        LeastSlackAsync = 1
    }

    public enum GpuDeadlineClass
    {
        Critical = 0,
        Normal = 1,
        Background = 2
    }

    public enum GpuDeadlineQueue
    {
        MainGraphics = 0,
        ComputeUrgent = 1,
        ComputeDefault = 2,
        ComputeBackground = 3
    }

    public readonly struct GpuDeadlineJob
    {
        public GpuDeadlineJob(
            int jobId,
            int sequence,
            GpuDeadlineClass deadlineClass,
            int relativeDeadlineMicroseconds,
            int estimatedCostMicroseconds,
            int workItemCount,
            int iterations,
            uint seed)
        {
            if (jobId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(jobId));
            }
            if (sequence < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sequence));
            }
            if (relativeDeadlineMicroseconds < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(relativeDeadlineMicroseconds));
            }
            if (estimatedCostMicroseconds < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(estimatedCostMicroseconds));
            }
            if (workItemCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(workItemCount));
            }
            if (iterations < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(iterations));
            }

            JobId = jobId;
            Sequence = sequence;
            DeadlineClass = deadlineClass;
            RelativeDeadlineMicroseconds = relativeDeadlineMicroseconds;
            EstimatedCostMicroseconds = estimatedCostMicroseconds;
            WorkItemCount = workItemCount;
            Iterations = iterations;
            Seed = seed;
        }

        public int JobId { get; }

        public int Sequence { get; }

        public GpuDeadlineClass DeadlineClass { get; }

        public int RelativeDeadlineMicroseconds { get; }

        public int EstimatedCostMicroseconds { get; }

        public int WorkItemCount { get; }

        public int Iterations { get; }

        public uint Seed { get; }
    }

    public readonly struct GpuDeadlineDispatch
    {
        public GpuDeadlineDispatch(
            GpuDeadlineJob job,
            GpuDeadlineQueue queue,
            int estimatedSlackMicroseconds)
        {
            Job = job;
            Queue = queue;
            EstimatedSlackMicroseconds = estimatedSlackMicroseconds;
        }

        public GpuDeadlineJob Job { get; }

        public GpuDeadlineQueue Queue { get; }

        public int EstimatedSlackMicroseconds { get; }
    }
}
