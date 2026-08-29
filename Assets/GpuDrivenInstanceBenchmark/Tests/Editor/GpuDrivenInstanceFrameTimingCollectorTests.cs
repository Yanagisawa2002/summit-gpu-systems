using System;
using NUnit.Framework;

namespace Summit.GpuDrivenInstance.Benchmark.Tests
{
    public sealed class GpuDrivenInstanceFrameTimingCollectorTests
    {
        private const ulong CpuFrequency = 1_000_000u;
        private const ulong GpuFrequency = 1_000_000_000u;

        [Test]
        public void EvaluateCalculatesAllMetricsAndSubmissionDuration()
        {
            GpuDrivenInstanceFrameTimingRaw raw = ValidRaw(
                frameStartTimestamp: 1_000_000u);

            GpuDrivenInstanceFrameTimingSample sample =
                GpuDrivenInstanceFrameTimingCollector.Evaluate(
                    raw,
                    CpuFrequency,
                    GpuFrequency);

            Assert.That(sample.Valid, Is.True);
            Assert.That(
                sample.Status,
                Is.EqualTo(GpuDrivenInstanceFrameTimingStatus.Valid));
            Assert.That(sample.SubmissionWindowValid, Is.True);
            Assert.That(
                sample.SubmissionWindowStatus,
                Is.EqualTo(
                    GpuDrivenInstanceSubmissionWindowStatus.Valid));
            Assert.That(sample.FrameStartTimestamp, Is.EqualTo(1_000_000u));
            Assert.That(sample.CpuFrameMs, Is.EqualTo(16.25).Within(1e-9));
            Assert.That(
                sample.CpuMainThreadMs,
                Is.EqualTo(4.5).Within(1e-9));
            Assert.That(
                sample.CpuRenderThreadMs,
                Is.EqualTo(3.25).Within(1e-9));
            Assert.That(sample.GpuFrameMs, Is.EqualTo(6.75).Within(1e-9));
            Assert.That(sample.CpuRenderThreadValid, Is.True);
            Assert.That(sample.GpuFrameValid, Is.True);
            Assert.That(
                sample.CpuSubmissionMs,
                Is.EqualTo(5.0).Within(1e-9));
        }

        [Test]
        public void CpuTimerFrequencyOnlyControlsSubmissionWindow()
        {
            GpuDrivenInstanceFrameTimingSample sample =
                GpuDrivenInstanceFrameTimingCollector.Evaluate(
                    ValidRaw(1_000_000u),
                    0u,
                    GpuFrequency);

            AssertMetricsOnly(
                sample,
                GpuDrivenInstanceSubmissionWindowStatus
                    .InvalidCpuTimerFrequency);
        }

        [Test]
        public void GpuTimerFrequencyDoesNotInvalidateMillisecondMetrics()
        {
            GpuDrivenInstanceFrameTimingSample sample =
                GpuDrivenInstanceFrameTimingCollector.Evaluate(
                    ValidRaw(1_000_000u),
                    CpuFrequency,
                    0u);

            Assert.That(sample.Valid, Is.True);
            Assert.That(sample.SubmissionWindowValid, Is.True);
        }

        [TestCase(0u, 1_002_000u, 1_007_000u, 1_009_000u)]
        [TestCase(1_000_000u, 999_999u, 1_007_000u, 1_009_000u)]
        [TestCase(1_000_000u, 1_002_000u, 1_001_999u, 1_009_000u)]
        public void InvalidSubmissionTimestampsKeepFrameMetrics(
            ulong frameStart,
            ulong firstSubmit,
            ulong presentCalled,
            ulong frameComplete)
        {
            GpuDrivenInstanceFrameTimingRaw raw = Raw(
                16.25,
                4.5,
                3.25,
                6.75,
                frameStart,
                firstSubmit,
                presentCalled,
                frameComplete);

            AssertMetricsOnly(
                GpuDrivenInstanceFrameTimingCollector.Evaluate(
                    raw,
                    CpuFrequency,
                    GpuFrequency),
                GpuDrivenInstanceSubmissionWindowStatus.InvalidTimestamp);
        }

        [Test]
        public void FrameCompleteMayPrecedePresentWithoutInvalidatingWindow()
        {
            GpuDrivenInstanceFrameTimingRaw raw = Raw(
                16.25,
                4.5,
                3.25,
                6.75,
                1_000_000u,
                1_002_000u,
                1_007_000u,
                1_006_000u);

            GpuDrivenInstanceFrameTimingSample sample =
                GpuDrivenInstanceFrameTimingCollector.Evaluate(
                    raw,
                    CpuFrequency,
                    GpuFrequency);
            Assert.That(sample.Valid, Is.True);
            Assert.That(sample.SubmissionWindowValid, Is.True);
        }

        [TestCase(0.0)]
        [TestCase(-1.0)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        public void EvaluateRejectsUnavailableOrNonFiniteMetrics(
            double cpuFrameMs)
        {
            GpuDrivenInstanceFrameTimingRaw valid = ValidRaw(1_000_000u);
            GpuDrivenInstanceFrameTimingRaw raw = Raw(
                cpuFrameMs,
                valid.CpuMainThreadMs,
                valid.CpuRenderThreadMs,
                valid.GpuFrameMs,
                valid.FrameStartTimestamp,
                valid.FirstSubmitTimestamp,
                valid.CpuTimePresentCalled,
                valid.CpuTimeFrameComplete);

            AssertUnavailable(
                GpuDrivenInstanceFrameTimingCollector.Evaluate(
                    raw,
                    CpuFrequency,
                    GpuFrequency),
                GpuDrivenInstanceFrameTimingStatus.InvalidMetric);
        }

        [TestCase(0.0)]
        [TestCase(-1.0)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        public void EvaluateRejectsUnavailableMainThreadMetric(
            double cpuMainThreadMs)
        {
            GpuDrivenInstanceFrameTimingRaw valid = ValidRaw(1_000_000u);
            GpuDrivenInstanceFrameTimingRaw raw = Raw(
                valid.CpuFrameMs,
                cpuMainThreadMs,
                valid.CpuRenderThreadMs,
                valid.GpuFrameMs,
                valid.FrameStartTimestamp,
                valid.FirstSubmitTimestamp,
                valid.CpuTimePresentCalled,
                valid.CpuTimeFrameComplete);

            AssertUnavailable(
                GpuDrivenInstanceFrameTimingCollector.Evaluate(
                    raw,
                    CpuFrequency,
                    GpuFrequency),
                GpuDrivenInstanceFrameTimingStatus.InvalidMetric);
        }

        [TestCase(0.0, 6.75, false, true)]
        [TestCase(3.25, 0.0, true, false)]
        [TestCase(0.0, 0.0, false, false)]
        public void OptionalRenderAndGpuMetricsDoNotInvalidateCpuTail(
            double renderThreadMs,
            double gpuFrameMs,
            bool renderValid,
            bool gpuValid)
        {
            GpuDrivenInstanceFrameTimingRaw valid = ValidRaw(1_000_000u);
            GpuDrivenInstanceFrameTimingSample sample =
                GpuDrivenInstanceFrameTimingCollector.Evaluate(
                    Raw(
                        valid.CpuFrameMs,
                        valid.CpuMainThreadMs,
                        renderThreadMs,
                        gpuFrameMs,
                        valid.FrameStartTimestamp,
                        valid.FirstSubmitTimestamp,
                        valid.CpuTimePresentCalled,
                        valid.CpuTimeFrameComplete),
                    CpuFrequency,
                    GpuFrequency);

            Assert.That(sample.Valid, Is.True);
            Assert.That(sample.CpuRenderThreadValid, Is.EqualTo(renderValid));
            Assert.That(sample.GpuFrameValid, Is.EqualTo(gpuValid));
            Assert.That(
                double.IsNaN(sample.CpuRenderThreadMs),
                Is.EqualTo(!renderValid));
            Assert.That(
                double.IsNaN(sample.GpuFrameMs),
                Is.EqualTo(!gpuValid));
        }

        [Test]
        public void CollectorDeduplicatesByFrameStartTimestamp()
        {
            var collector = new GpuDrivenInstanceFrameTimingCollector();

            GpuDrivenInstanceFrameTimingSample first = collector.Collect(
                ValidRaw(1_000_000u),
                CpuFrequency,
                GpuFrequency);
            GpuDrivenInstanceFrameTimingSample duplicate = collector.Collect(
                ValidRaw(1_000_000u),
                CpuFrequency,
                GpuFrequency);
            GpuDrivenInstanceFrameTimingSample next = collector.Collect(
                ValidRaw(2_000_000u),
                CpuFrequency,
                GpuFrequency);

            Assert.That(first.Valid, Is.True);
            AssertUnavailable(
                duplicate,
                GpuDrivenInstanceFrameTimingStatus
                    .DuplicateFrameStartTimestamp);
            Assert.That(next.Valid, Is.True);
        }

        [Test]
        public void CollectorRejectsRegressionUntilReset()
        {
            var collector = new GpuDrivenInstanceFrameTimingCollector();
            Assert.That(
                collector.Collect(
                    ValidRaw(2_000_000u),
                    CpuFrequency,
                    GpuFrequency).Valid,
                Is.True);

            AssertUnavailable(
                collector.Collect(
                    ValidRaw(1_000_000u),
                    CpuFrequency,
                    GpuFrequency),
                GpuDrivenInstanceFrameTimingStatus
                    .NonMonotonicFrameStartTimestamp);

            collector.Reset();
            Assert.That(
                collector.Collect(
                    ValidRaw(1_000_000u),
                    CpuFrequency,
                    GpuFrequency).Valid,
                Is.True);
        }

        [Test]
        public void InjectedCollectionHasNoSteadyStateManagedAllocation()
        {
            var collector = new GpuDrivenInstanceFrameTimingCollector();
            collector.Collect(
                ValidRaw(1_000_000u),
                CpuFrequency,
                GpuFrequency);

            long before = GC.GetAllocatedBytesForCurrentThread();
            double observed = 0.0;
            for (ulong index = 2u; index < 10_002u; index++)
            {
                GpuDrivenInstanceFrameTimingSample sample =
                    collector.Collect(
                        ValidRaw(index * 1_000_000u),
                        CpuFrequency,
                        GpuFrequency);
                observed += sample.CpuSubmissionMs;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.That(observed, Is.EqualTo(50_000.0).Within(1e-6));
            Assert.That(allocated, Is.Zero);
        }

        private static void AssertUnavailable(
            GpuDrivenInstanceFrameTimingSample sample,
            GpuDrivenInstanceFrameTimingStatus status)
        {
            Assert.That(sample.Valid, Is.False);
            Assert.That(sample.Status, Is.EqualTo(status));
            Assert.That(double.IsNaN(sample.CpuFrameMs), Is.True);
            Assert.That(double.IsNaN(sample.CpuMainThreadMs), Is.True);
            Assert.That(double.IsNaN(sample.CpuRenderThreadMs), Is.True);
            Assert.That(double.IsNaN(sample.GpuFrameMs), Is.True);
            Assert.That(double.IsNaN(sample.CpuSubmissionMs), Is.True);
        }

        private static void AssertMetricsOnly(
            GpuDrivenInstanceFrameTimingSample sample,
            GpuDrivenInstanceSubmissionWindowStatus status)
        {
            Assert.That(sample.Valid, Is.True);
            Assert.That(sample.Status,
                Is.EqualTo(GpuDrivenInstanceFrameTimingStatus.Valid));
            Assert.That(sample.SubmissionWindowValid, Is.False);
            Assert.That(sample.SubmissionWindowStatus, Is.EqualTo(status));
            Assert.That(sample.CpuFrameMs, Is.EqualTo(16.25).Within(1e-9));
            Assert.That(sample.CpuMainThreadMs,
                Is.EqualTo(4.5).Within(1e-9));
            Assert.That(sample.CpuRenderThreadMs,
                Is.EqualTo(3.25).Within(1e-9));
            Assert.That(sample.GpuFrameMs, Is.EqualTo(6.75).Within(1e-9));
            Assert.That(double.IsNaN(sample.CpuSubmissionMs), Is.True);
        }

        private static GpuDrivenInstanceFrameTimingRaw ValidRaw(
            ulong frameStartTimestamp)
        {
            return Raw(
                16.25,
                4.5,
                3.25,
                6.75,
                frameStartTimestamp,
                frameStartTimestamp + 2_000u,
                frameStartTimestamp + 7_000u,
                frameStartTimestamp + 9_000u);
        }

        private static GpuDrivenInstanceFrameTimingRaw Raw(
            double cpuFrameMs,
            double cpuMainThreadMs,
            double cpuRenderThreadMs,
            double gpuFrameMs,
            ulong frameStartTimestamp,
            ulong firstSubmitTimestamp,
            ulong cpuTimePresentCalled,
            ulong cpuTimeFrameComplete)
        {
            return new GpuDrivenInstanceFrameTimingRaw(
                cpuFrameMs,
                cpuMainThreadMs,
                cpuRenderThreadMs,
                gpuFrameMs,
                frameStartTimestamp,
                firstSubmitTimestamp,
                cpuTimePresentCalled,
                cpuTimeFrameComplete);
        }
    }
}
