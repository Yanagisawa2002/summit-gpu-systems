using System;
using NUnit.Framework;

namespace Summit.GpuTimestamps.Tests
{
    [TestFixture]
    public sealed class GpuTimestampResultValidationTests
    {
        [Test]
        public void ReadyResultWithInvalidContractEvidence_IsMalformedAndTerminal()
        {
            AssertMalformedResult(
                value =>
                {
                    value.Flags = (uint)GpuTimestampSampleFlags.None;
                    return value;
                });
            AssertMalformedResult(
                value =>
                {
                    value.FenceValue = 0;
                    return value;
                });
        }

        private static void AssertMalformedResult(
            Func<NativeTimestampResult, NativeTimestampResult> mutation)
        {
            var native = new FakeGpuTimestampNativeApi(1);
            bool created = GpuTimestampSession.TryCreateForTesting(
                native,
                out GpuTimestampSession session,
                out GpuTimestampSupport support);
            Assert.That(created, Is.True, support.Message);
            native.ResetCallCounts();

            try
            {
                Assert.That(
                    session.Acquire(
                        501,
                        GpuTimestampSampleFlags.EmptyScope,
                        100,
                        out GpuTimestampToken token),
                    Is.EqualTo(GpuTimestampStatus.Ready));
                Assert.That(
                    session.MarkSubmitted(token),
                    Is.EqualTo(GpuTimestampStatus.Ready));
                native.MakeReady(token.Value);
                native.MutateReadyResult(token.Value, mutation);

                Assert.That(
                    session.TryConsume(token, 101, out _),
                    Is.EqualTo(GpuTimestampStatus.MalformedNativeResult));
                Assert.That(session.IsTerminal, Is.True);
                Assert.That(
                    session.TerminalStatus,
                    Is.EqualTo(GpuTimestampStatus.MalformedNativeResult));
                Assert.That(session.ActiveSampleCount, Is.EqualTo(0));
            }
            finally
            {
                session.Dispose();
            }
        }
    }
}
