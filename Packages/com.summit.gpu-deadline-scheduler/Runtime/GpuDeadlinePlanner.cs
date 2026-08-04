using System;
using System.Collections.Generic;

namespace Summit.GpuDeadlineScheduler
{
    public static class GpuDeadlinePlanner
    {
        public static IReadOnlyList<GpuDeadlineDispatch> BuildPlan(
            IReadOnlyList<GpuDeadlineJob> jobs,
            GpuDeadlinePolicy policy,
            bool supportsAsyncCompute)
        {
            if (jobs == null)
            {
                throw new ArgumentNullException(nameof(jobs));
            }

            var result = new List<GpuDeadlineDispatch>(jobs.Count);
            for (int index = 0; index < jobs.Count; index++)
            {
                GpuDeadlineJob job = jobs[index];
                int slack = checked(
                    job.RelativeDeadlineMicroseconds -
                    job.EstimatedCostMicroseconds);
                result.Add(new GpuDeadlineDispatch(
                    job,
                    ResolveQueue(policy, job, supportsAsyncCompute),
                    slack));
            }

            if (policy == GpuDeadlinePolicy.LeastSlackAsync)
            {
                result.Sort(CompareLeastSlackFirst);
            }
            else if (policy != GpuDeadlinePolicy.FifoGraphics)
            {
                throw new ArgumentOutOfRangeException(nameof(policy));
            }

            return result;
        }

        private static GpuDeadlineQueue ResolveQueue(
            GpuDeadlinePolicy policy,
            GpuDeadlineJob job,
            bool supportsAsyncCompute)
        {
            if (policy == GpuDeadlinePolicy.FifoGraphics ||
                !supportsAsyncCompute)
            {
                return GpuDeadlineQueue.MainGraphics;
            }

            switch (job.DeadlineClass)
            {
                case GpuDeadlineClass.Critical:
                    return GpuDeadlineQueue.ComputeUrgent;
                case GpuDeadlineClass.Normal:
                    return GpuDeadlineQueue.ComputeDefault;
                case GpuDeadlineClass.Background:
                    return GpuDeadlineQueue.ComputeBackground;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(job.DeadlineClass));
            }
        }

        private static int CompareLeastSlackFirst(
            GpuDeadlineDispatch left,
            GpuDeadlineDispatch right)
        {
            int comparison = left.EstimatedSlackMicroseconds.CompareTo(
                right.EstimatedSlackMicroseconds);
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = left.Job.RelativeDeadlineMicroseconds.CompareTo(
                right.Job.RelativeDeadlineMicroseconds);
            if (comparison != 0)
            {
                return comparison;
            }
            return left.Job.Sequence.CompareTo(right.Job.Sequence);
        }
    }
}
