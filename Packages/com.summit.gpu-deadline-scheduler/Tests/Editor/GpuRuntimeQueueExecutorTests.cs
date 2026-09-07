using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace Summit.GpuDeadlineScheduler.Tests
{
    public sealed class GpuRuntimeQueueExecutorTests
    {
        [UnityTest]
        public IEnumerator MainQueueDependencyChainReadsRealProducerOutputs() => RunChain(false);
        [UnityTest]
        public IEnumerator AlternatingQueueDependencyChainReadsRealProducerOutputs() => RunChain(true);

        private static IEnumerator RunChain(bool async)
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsGraphicsFence ||
                SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null || (async && !SystemInfo.supportsAsyncCompute))
                Assert.Ignore("A compute/fence capable device (and async for the async case) is required.");
            var costs = new GpuRuntimeCostEstimator(1, 8, 10000); costs.Configure(0, 1, 100, 1, 10000);
            var scheduler = new GpuRuntimeScheduler(4, 10000, 1000, 4, costs);
            var jobs = new GpuRuntimeJob[4]; var edges = new GpuJobDependency[3];
            for (int i = 0; i < 4; i++) jobs[i] = new GpuRuntimeJob(new GpuDeadlineJob(i, i,
                i % 2 == 0 ? GpuDeadlineClass.Background : GpuDeadlineClass.Critical, 1000, 100, 1, 1, 1), 0);
            for (int i = 0; i < 3; i++) edges[i] = new GpuJobDependency(i, i + 1);
            Assert.That(scheduler.TryAdmit(jobs, 4, edges, 3, 0), Is.EqualTo(GpuAdmissionResult.Accepted));
            var shader = Resources.Load<ComputeShader>("GpuDeadlineScheduler/GpuRuntimeDependencyProbe");
            Assert.That(shader, Is.Not.Null); int kernel = shader.FindKernel("DependencyProbe");
            var buffers = new GraphicsBuffer[5];
            var executor = new GpuRuntimeQueueExecutor(scheduler);
            try
            {
                for (int i = 0; i < buffers.Length; i++) { buffers[i] = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4); buffers[i].SetData(new uint[] { i == 0 ? 7u : 0u }); }
                // Fabricated passing evidence is TEST ONLY: forces fence correctness coverage, not production admission.
                var evidence = async ? new GpuAsyncAdmissionEvidence(32, 100, 90, 110, 100, 50, 40, 0, 0, 0, 1000) : default;
                Action<CommandBuffer, GpuRuntimeDispatch> record = (commands, dispatch) =>
                {
                    int i = dispatch.Job.JobId;
                    commands.SetComputeBufferParam(shader, kernel, "_Input", buffers[i]);
                    commands.SetComputeBufferParam(shader, kernel, "_Output", buffers[i + 1]);
                    commands.DispatchCompute(shader, kernel, 1, 1, 1);
                };
                int submitted = 0;
                while (executor.SubmitNext(0, record, out var dispatch, evidence))
                {
                    Assert.That(dispatch.Job.JobId, Is.EqualTo(submitted++));
                    Assert.That(dispatch.Queue, Is.EqualTo(async && dispatch.Job.JobId % 2 == 1 ? GpuDeadlineQueue.ComputeUrgent : GpuDeadlineQueue.MainGraphics));
                }
                Assert.That(submitted, Is.EqualTo(4));
                double timeout = Time.realtimeSinceStartupAsDouble + 10;
                while (scheduler.InFlightCount > 0 && Time.realtimeSinceStartupAsDouble < timeout)
                { executor.PollCompletions(); yield return null; }
                Assert.That(scheduler.InFlightCount, Is.Zero, "Dependency execution timed out.");
                var actual = new uint[1]; buffers[4].GetData(actual);
                Assert.That(actual[0], Is.EqualTo(607u)); // (((7*3+1)*3+1)*3+1)*3+1
                Assert.That(scheduler.ReleaseCompleted(), Is.EqualTo(4));
            }
            finally
            {
                executor.Dispose();
                foreach (var buffer in buffers) buffer?.Dispose();
            }
        }
    }
}
