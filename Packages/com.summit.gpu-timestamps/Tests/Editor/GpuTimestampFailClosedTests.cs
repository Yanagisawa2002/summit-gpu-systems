using System;
using NUnit.Framework;

namespace Summit.GpuTimestamps.Tests
{
    [TestFixture]
    public sealed class GpuTimestampFailClosedTests
    {
        [Test]
        public void AcquireException_TerminalizesWithoutPublishingAToken()
        {
            var native = new FakeGpuTimestampNativeApi(1);
            GpuTimestampSession session = CreateSession(native);
            native.AcquireException = new InvalidOperationException("synthetic acquire failure");
            try
            {
                Assert.That(
                    session.Acquire(
                        601,
                        GpuTimestampSampleFlags.None,
                        200,
                        out GpuTimestampToken token),
                    Is.EqualTo(GpuTimestampStatus.Error));
                Assert.That(token.IsValid, Is.False);
                Assert.That(session.IsTerminal, Is.True);
                Assert.That(session.TerminalStatus, Is.EqualTo(GpuTimestampStatus.Error));
                Assert.That(session.ActiveSampleCount, Is.EqualTo(0));

                native.ResetCallCounts();
                Assert.That(
                    session.Acquire(
                        602,
                        GpuTimestampSampleFlags.None,
                        201,
                        out _),
                    Is.EqualTo(GpuTimestampStatus.Error));
                Assert.That(native.AcquireCallCount, Is.EqualTo(0));
            }
            finally
            {
                session.Dispose();
            }
        }

        [Test]
        public void ConsumeException_TerminalizesAndRetainsSubmittedOwnership()
        {
            var native = new FakeGpuTimestampNativeApi(1);
            GpuTimestampSession session = CreateSession(native);
            try
            {
                Assert.That(
                    session.Acquire(
                        611,
                        GpuTimestampSampleFlags.None,
                        210,
                        out GpuTimestampToken token),
                    Is.EqualTo(GpuTimestampStatus.Ready));
                Assert.That(
                    session.MarkSubmitted(token),
                    Is.EqualTo(GpuTimestampStatus.Ready));
                native.ConsumeException = new InvalidOperationException(
                    "synthetic consume failure");

                Assert.That(
                    session.TryConsume(token, 211, out _),
                    Is.EqualTo(GpuTimestampStatus.Error));
                Assert.That(session.IsTerminal, Is.True);
                Assert.That(session.TerminalStatus, Is.EqualTo(GpuTimestampStatus.Error));
                Assert.That(session.ActiveSampleCount, Is.EqualTo(1));
                Assert.That(session.SubmittedSampleCount, Is.EqualTo(1));
                Assert.That(native.ActiveNativeSampleCount, Is.EqualTo(1));
            }
            finally
            {
                native.ResetCallCounts();
                session.Dispose();
                Assert.That(native.CancelCallCount, Is.EqualTo(0));
                Assert.That(native.ActiveNativeSampleCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void CancelException_TerminalizesAndRetainsReservedOwnership()
        {
            var native = new FakeGpuTimestampNativeApi(1);
            GpuTimestampSession session = CreateSession(native);
            Assert.That(
                session.Acquire(
                    621,
                    GpuTimestampSampleFlags.None,
                    220,
                    out GpuTimestampToken token),
                Is.EqualTo(GpuTimestampStatus.Ready));
            native.CancelException = new InvalidOperationException(
                "synthetic cancel failure");

            Assert.That(session.Cancel(token), Is.EqualTo(GpuTimestampStatus.Error));
            Assert.That(session.IsTerminal, Is.True);
            Assert.That(session.TerminalStatus, Is.EqualTo(GpuTimestampStatus.Error));
            Assert.That(session.ActiveSampleCount, Is.EqualTo(1));
            Assert.That(session.ReservedSampleCount, Is.EqualTo(1));
            Assert.That(native.ActiveNativeSampleCount, Is.EqualTo(1));

            session.Dispose();
            Assert.That(native.ActiveNativeSampleCount, Is.EqualTo(1));
        }

        private static GpuTimestampSession CreateSession(FakeGpuTimestampNativeApi native)
        {
            bool created = GpuTimestampSession.TryCreateForTesting(
                native,
                out GpuTimestampSession session,
                out GpuTimestampSupport support);
            Assert.That(created, Is.True, support.Message);
            native.ResetCallCounts();
            return session;
        }
    }
}
