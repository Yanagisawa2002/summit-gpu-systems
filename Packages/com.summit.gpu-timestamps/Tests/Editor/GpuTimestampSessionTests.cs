using System;
using NUnit.Framework;

namespace Summit.GpuTimestamps.Tests
{
    [TestFixture]
    public sealed class GpuTimestampSessionTests
    {
        [Test]
        public void TryCreate_DiscoversAndRecyclesEveryPayload()
        {
            var native = new FakeGpuTimestampNativeApi(3);

            bool created = GpuTimestampSession.TryCreateForTesting(
                native,
                out GpuTimestampSession session,
                out GpuTimestampSupport support);
            try
            {
                Assert.That(created, Is.True);
                Assert.That(session, Is.Not.Null);
                Assert.That(support.IsAvailable, Is.True);
                Assert.That(support.RingCapacity, Is.EqualTo(3));
                Assert.That(session.Capacity, Is.EqualTo(3));
                Assert.That(session.Scopes.Count, Is.EqualTo(3));
                Assert.That(native.AcquireCallCount, Is.EqualTo(3));
                Assert.That(native.CancelCallCount, Is.EqualTo(3));
                Assert.That(native.ActiveNativeSampleCount, Is.EqualTo(0));
            }
            finally
            {
                session?.Dispose();
            }
        }

        [Test]
        public void Acquire_RejectsInvalidArgumentsWithoutCallingNative()
        {
            var native = new FakeGpuTimestampNativeApi(1);
            GpuTimestampSession session = CreateSession(native);
            try
            {
                Assert.That(
                    session.Acquire(
                        0,
                        GpuTimestampSampleFlags.None,
                        0,
                        out GpuTimestampToken zeroTag),
                    Is.EqualTo(GpuTimestampStatus.InvalidArgument));
                Assert.That(zeroTag.IsValid, Is.False);
                Assert.That(
                    session.Acquire(
                        1,
                        GpuTimestampSampleFlags.None,
                        -1,
                        out GpuTimestampToken negativeFrame),
                    Is.EqualTo(GpuTimestampStatus.InvalidArgument));
                Assert.That(negativeFrame.IsValid, Is.False);
                Assert.That(native.AcquireCallCount, Is.EqualTo(0));
            }
            finally
            {
                session.Dispose();
            }
        }

        [Test]
        public void ReservedSample_CannotBeConsumedAndCanBeCancelled()
        {
            var native = new FakeGpuTimestampNativeApi(1);
            GpuTimestampSession session = CreateSession(native);
            try
            {
                GpuTimestampToken token = Acquire(session, 17, 4);
                Assert.That(session.ReservedSampleCount, Is.EqualTo(1));
                Assert.That(session.SubmittedSampleCount, Is.EqualTo(0));
                Assert.That(session.GetScope(token).IsValid, Is.True);

                Assert.That(
                    session.TryConsume(token, 4, out GpuTimestampResult result),
                    Is.EqualTo(GpuTimestampStatus.InvalidArgument));
                Assert.That(result.Token.IsValid, Is.False);
                Assert.That(native.ConsumeCallCount, Is.EqualTo(0));

                Assert.That(session.Cancel(token), Is.EqualTo(GpuTimestampStatus.Ready));
                Assert.That(session.ActiveSampleCount, Is.EqualTo(0));
                Assert.That(session.ReservedSampleCount, Is.EqualTo(0));
                Assert.That(native.ActiveNativeSampleCount, Is.EqualTo(0));
                Assert.That(
                    session.Cancel(token),
                    Is.EqualTo(GpuTimestampStatus.StaleManagedToken));
                Assert.Throws<InvalidOperationException>(() => session.GetScope(token));
            }
            finally
            {
                session.Dispose();
            }
        }

        [Test]
        public void SubmittedSample_CannotBeCancelledOrReusedBeforeCompletion()
        {
            var native = new FakeGpuTimestampNativeApi(1);
            GpuTimestampSession session = CreateSession(native);
            try
            {
                GpuTimestampToken submitted = Acquire(session, 21, 7);
                Assert.That(
                    session.MarkSubmitted(submitted),
                    Is.EqualTo(GpuTimestampStatus.Ready));
                native.ResetCallCounts();

                Assert.That(
                    session.Cancel(submitted),
                    Is.EqualTo(GpuTimestampStatus.InvalidArgument));
                Assert.That(native.CancelCallCount, Is.EqualTo(0));
                Assert.That(session.SubmittedSampleCount, Is.EqualTo(1));
                Assert.That(native.ActiveNativeSampleCount, Is.EqualTo(1));

                Assert.That(
                    session.Acquire(
                        22,
                        GpuTimestampSampleFlags.None,
                        8,
                        out GpuTimestampToken replacement),
                    Is.EqualTo(GpuTimestampStatus.RingFull));
                Assert.That(replacement.IsValid, Is.False);
                Assert.That(native.ActiveNativeSampleCount, Is.EqualTo(1));
            }
            finally
            {
                native.ResetCallCounts();
                session.Dispose();
                Assert.That(
                    native.CancelCallCount,
                    Is.EqualTo(0),
                    "Dispose must not recycle a payload that queued callbacks can still reference.");
                Assert.That(native.ActiveNativeSampleCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void MarkSubmitted_OrdersNativeBeforeManagedAndQuarantinesFailure()
        {
            var native = new FakeGpuTimestampNativeApi(1);
            GpuTimestampSession session = CreateSession(native);
            try
            {
                GpuTimestampToken token = Acquire(session, 31, 10);
                bool observedPreTransitionState = false;
                native.MarkSubmittedObserver = observedToken =>
                {
                    Assert.That(observedToken, Is.EqualTo(token.Value));
                    Assert.That(session.ReservedSampleCount, Is.EqualTo(1));
                    Assert.That(session.SubmittedSampleCount, Is.EqualTo(0));
                    observedPreTransitionState = true;
                };

                Assert.Throws<InvalidOperationException>(
                    () => native.MakeReady(token.Value),
                    "Completion before submission must be rejected by the fake ABI state machine.");

                Assert.That(
                    session.MarkSubmitted(token),
                    Is.EqualTo(GpuTimestampStatus.Ready));
                Assert.That(observedPreTransitionState, Is.True);
                Assert.That(native.MarkSubmittedCallCount, Is.EqualTo(1));
                Assert.That(native.SubmittedTokens, Is.EqualTo(new[] { token.Value }));
                Assert.That(session.ReservedSampleCount, Is.EqualTo(0));
                Assert.That(session.SubmittedSampleCount, Is.EqualTo(1));
                Assert.That(
                    session.MarkSubmitted(token),
                    Is.EqualTo(GpuTimestampStatus.InvalidArgument));
                Assert.That(
                    native.MarkSubmittedCallCount,
                    Is.EqualTo(1),
                    "Duplicate managed transition must not call native twice.");

                native.MakeReady(token.Value);
                Assert.That(
                    session.TryConsume(token, 11, out _),
                    Is.EqualTo(GpuTimestampStatus.Ready));

                GpuTimestampToken uncertain = Acquire(session, 32, 12);
                native.ResetCallCounts();
                native.MarkSubmittedException = new InvalidOperationException(
                    "synthetic submission transition failure");

                Assert.That(
                    session.MarkSubmitted(uncertain),
                    Is.EqualTo(GpuTimestampStatus.Error));
                Assert.That(native.MarkSubmittedCallCount, Is.EqualTo(1));
                Assert.That(session.IsTerminal, Is.True);
                Assert.That(session.TerminalStatus, Is.EqualTo(GpuTimestampStatus.Error));
                Assert.That(session.ActiveSampleCount, Is.EqualTo(1));
                Assert.That(session.ReservedSampleCount, Is.EqualTo(0));
                Assert.That(session.SubmittedSampleCount, Is.EqualTo(0));

                native.ResetCallCounts();
                Assert.That(
                    session.Cancel(uncertain),
                    Is.EqualTo(GpuTimestampStatus.InvalidArgument));
                Assert.That(native.CancelCallCount, Is.EqualTo(0));
            }
            finally
            {
                native.ResetCallCounts();
                session.Dispose();
                Assert.That(
                    native.CancelCallCount,
                    Is.EqualTo(0),
                    "Indeterminate native ownership must never be cancelled.");
                Assert.That(native.ActiveNativeSampleCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void SubmittedPendingPoll_IsNonblockingAndPreservesOwnership()
        {
            var native = new FakeGpuTimestampNativeApi(1);
            GpuTimestampSession session = CreateSession(native);
            try
            {
                GpuTimestampToken token = Acquire(session, 41, 12);
                Assert.That(
                    session.MarkSubmitted(token),
                    Is.EqualTo(GpuTimestampStatus.Ready));

                Assert.That(
                    session.TryConsume(token, 13, out GpuTimestampResult result),
                    Is.EqualTo(GpuTimestampStatus.Pending));
                Assert.That(result.Token.IsValid, Is.False);
                Assert.That(native.ConsumeCallCount, Is.EqualTo(1));
                Assert.That(session.ActiveSampleCount, Is.EqualTo(1));
                Assert.That(session.SubmittedSampleCount, Is.EqualTo(1));
                Assert.That(native.ActiveNativeSampleCount, Is.EqualTo(1));
            }
            finally
            {
                session.Dispose();
            }
        }

        [Test]
        public void ReadyConsume_PreservesRawEvidenceAndRecyclesWithNewGeneration()
        {
            var native = new FakeGpuTimestampNativeApi(1);
            GpuTimestampSession session = CreateSession(native);
            try
            {
                Assert.That(
                    session.Acquire(
                        99,
                        GpuTimestampSampleFlags.EmptyScope,
                        20,
                        out GpuTimestampToken first),
                    Is.EqualTo(GpuTimestampStatus.Ready));
                Assert.That(
                    session.MarkSubmitted(first),
                    Is.EqualTo(GpuTimestampStatus.Ready));
                native.MakeReady(first.Value);

                Assert.That(
                    session.TryConsume(first, 22, out GpuTimestampResult result),
                    Is.EqualTo(GpuTimestampStatus.Ready));
                Assert.That(result.Token.Equals(first), Is.True);
                Assert.That(result.SourceFrame, Is.EqualTo(20));
                Assert.That(result.ResultFrame, Is.EqualTo(22));
                Assert.That(
                    result.NativeFlags,
                    Is.EqualTo((uint)GpuTimestampSampleFlags.EmptyScope));
                Assert.That(result.BeginTicks, Is.EqualTo(1000UL));
                Assert.That(result.EndTicks, Is.EqualTo(1250UL));
                Assert.That(result.ElapsedTicks, Is.EqualTo(250UL));
                Assert.That(result.TimestampFrequency, Is.EqualTo(10000000UL));
                Assert.That(result.ElapsedNanoseconds, Is.EqualTo(25000L));
                Assert.That(result.ElapsedMilliseconds, Is.EqualTo(0.025).Within(1.0e-12));
                Assert.That(result.FenceValue, Is.EqualTo(42UL));
                Assert.That(result.DeviceGeneration, Is.EqualTo(7u));
                Assert.That(session.ActiveSampleCount, Is.EqualTo(0));
                Assert.That(native.ActiveNativeSampleCount, Is.EqualTo(0));

                GpuTimestampToken second = Acquire(session, 100, 23);
                Assert.That(second.ScopeIndex, Is.EqualTo(first.ScopeIndex));
                Assert.That(second.Value, Is.Not.EqualTo(first.Value));
                Assert.That(
                    session.TryConsume(first, 23, out _),
                    Is.EqualTo(GpuTimestampStatus.StaleManagedToken));
                Assert.That(session.Cancel(second), Is.EqualTo(GpuTimestampStatus.Ready));
            }
            finally
            {
                session.Dispose();
            }
        }

        [Test]
        public void ResultFrameBeforeSourceFrame_IsRejectedWithoutPolling()
        {
            var native = new FakeGpuTimestampNativeApi(1);
            GpuTimestampSession session = CreateSession(native);
            try
            {
                GpuTimestampToken token = Acquire(session, 51, 30);
                Assert.That(
                    session.MarkSubmitted(token),
                    Is.EqualTo(GpuTimestampStatus.Ready));

                Assert.That(
                    session.TryConsume(token, 29, out _),
                    Is.EqualTo(GpuTimestampStatus.InvalidArgument));
                Assert.That(native.ConsumeCallCount, Is.EqualTo(0));
                Assert.That(session.SubmittedSampleCount, Is.EqualTo(1));
            }
            finally
            {
                session.Dispose();
            }
        }

        [TestCase(GpuTimestampStatus.DeviceLost)]
        [TestCase(GpuTimestampStatus.CallbackError)]
        public void FatalNativeStatus_TerminalizesSession(GpuTimestampStatus fatalStatus)
        {
            var native = new FakeGpuTimestampNativeApi(1);
            GpuTimestampSession session = CreateSession(native);
            try
            {
                GpuTimestampToken token = Acquire(session, 61, 40);
                Assert.That(
                    session.MarkSubmitted(token),
                    Is.EqualTo(GpuTimestampStatus.Ready));
                native.SetConsumeStatus(token.Value, fatalStatus);

                Assert.That(
                    session.TryConsume(token, 41, out _),
                    Is.EqualTo(fatalStatus));
                Assert.That(session.IsTerminal, Is.True);
                Assert.That(session.TerminalStatus, Is.EqualTo(fatalStatus));
                Assert.That(session.ActiveSampleCount, Is.EqualTo(1));
                Assert.That(session.SubmittedSampleCount, Is.EqualTo(1));

                native.ResetCallCounts();
                Assert.That(
                    session.Acquire(
                        62,
                        GpuTimestampSampleFlags.None,
                        42,
                        out GpuTimestampToken rejected),
                    Is.EqualTo(fatalStatus));
                Assert.That(rejected.IsValid, Is.False);
                Assert.That(native.AcquireCallCount, Is.EqualTo(0));
            }
            finally
            {
                session.Dispose();
            }
        }

        [Test]
        public void MalformedReadyResult_TerminalizesSessionAfterNativeConsume()
        {
            var native = new FakeGpuTimestampNativeApi(1);
            GpuTimestampSession session = CreateSession(native);
            try
            {
                GpuTimestampToken token = Acquire(session, 71, 50);
                Assert.That(
                    session.MarkSubmitted(token),
                    Is.EqualTo(GpuTimestampStatus.Ready));
                native.MakeReady(token.Value);
                native.MutateReadyResult(
                    token.Value,
                    value =>
                    {
                        value.TimestampFrequency = 0;
                        return value;
                    });

                Assert.That(
                    session.TryConsume(token, 51, out _),
                    Is.EqualTo(GpuTimestampStatus.MalformedNativeResult));
                Assert.That(session.IsTerminal, Is.True);
                Assert.That(
                    session.TerminalStatus,
                    Is.EqualTo(GpuTimestampStatus.MalformedNativeResult));
                Assert.That(session.ActiveSampleCount, Is.EqualTo(0));

                native.ResetCallCounts();
                Assert.That(
                    session.Acquire(
                        72,
                        GpuTimestampSampleFlags.None,
                        52,
                        out GpuTimestampToken rejected),
                    Is.EqualTo(GpuTimestampStatus.MalformedNativeResult));
            }
            finally
            {
                session.Dispose();
            }
        }

        [Test]
        public void CrossSessionToken_IsRejectedBeforeCallingNative()
        {
            var firstNative = new FakeGpuTimestampNativeApi(1);
            var secondNative = new FakeGpuTimestampNativeApi(1);
            GpuTimestampSession first = CreateSession(firstNative);
            GpuTimestampSession second = CreateSession(secondNative);
            try
            {
                GpuTimestampToken token = Acquire(first, 81, 60);
                secondNative.ResetCallCounts();

                Assert.That(
                    second.MarkSubmitted(token),
                    Is.EqualTo(GpuTimestampStatus.StaleManagedToken));
                Assert.That(
                    second.TryConsume(token, 60, out _),
                    Is.EqualTo(GpuTimestampStatus.StaleManagedToken));
                Assert.That(
                    second.Cancel(token),
                    Is.EqualTo(GpuTimestampStatus.StaleManagedToken));
                Assert.That(secondNative.ConsumeCallCount, Is.EqualTo(0));
                Assert.That(secondNative.CancelCallCount, Is.EqualTo(0));
                Assert.That(first.Cancel(token), Is.EqualTo(GpuTimestampStatus.Ready));
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }
        }

        [Test]
        public void RingExhaustion_DoesNotOverwriteLiveSamples()
        {
            var native = new FakeGpuTimestampNativeApi(2);
            GpuTimestampSession session = CreateSession(native);
            try
            {
                GpuTimestampToken first = Acquire(session, 91, 70);
                GpuTimestampToken second = Acquire(session, 92, 70);

                Assert.That(
                    session.Acquire(
                        93,
                        GpuTimestampSampleFlags.None,
                        70,
                        out GpuTimestampToken rejected),
                    Is.EqualTo(GpuTimestampStatus.RingFull));
                Assert.That(rejected.IsValid, Is.False);
                Assert.That(session.ActiveSampleCount, Is.EqualTo(2));
                Assert.That(native.ActiveNativeSampleCount, Is.EqualTo(2));
                Assert.That(session.Cancel(first), Is.EqualTo(GpuTimestampStatus.Ready));
                Assert.That(session.Cancel(second), Is.EqualTo(GpuTimestampStatus.Ready));
            }
            finally
            {
                session.Dispose();
            }
        }

        [Test]
        public void UnknownPayloadFromNative_IsCancelledAndRejected()
        {
            var native = new FakeGpuTimestampNativeApi(1);
            GpuTimestampSession session = CreateSession(native);
            try
            {
                native.PayloadOnNextAcquire = new IntPtr(0x7fffff);

                Assert.That(
                    session.Acquire(
                        101,
                        GpuTimestampSampleFlags.None,
                        80,
                        out GpuTimestampToken token),
                    Is.EqualTo(GpuTimestampStatus.MalformedNativeResult));
                Assert.That(token.IsValid, Is.False);
                Assert.That(native.CancelCallCount, Is.EqualTo(1));
                Assert.That(native.ActiveNativeSampleCount, Is.EqualTo(0));
                Assert.That(session.ActiveSampleCount, Is.EqualTo(0));
            }
            finally
            {
                session.Dispose();
            }
        }

        [Test]
        public void Dispose_CancelsReservedButNeverSubmittedSamples()
        {
            var native = new FakeGpuTimestampNativeApi(2);
            GpuTimestampSession session = CreateSession(native);
            GpuTimestampToken reserved = Acquire(session, 111, 90);
            GpuTimestampToken submitted = Acquire(session, 112, 90);
            Assert.That(
                session.MarkSubmitted(submitted),
                Is.EqualTo(GpuTimestampStatus.Ready));
            native.ResetCallCounts();

            session.Dispose();

            Assert.That(native.CancelCallCount, Is.EqualTo(1));
            Assert.That(native.CancelledTokens, Does.Contain(reserved.Value));
            Assert.That(native.CancelledTokens.Contains(submitted.Value), Is.False);
            Assert.That(native.ActiveNativeSampleCount, Is.EqualTo(1));
            Assert.Throws<ObjectDisposedException>(
                () => session.Acquire(
                    113,
                    GpuTimestampSampleFlags.None,
                    91,
                    out _));
        }

        private static GpuTimestampSession CreateSession(FakeGpuTimestampNativeApi native)
        {
            bool created = GpuTimestampSession.TryCreateForTesting(
                native,
                out GpuTimestampSession session,
                out GpuTimestampSupport support);
            Assert.That(created, Is.True, support.Message);
            Assert.That(session, Is.Not.Null);
            Assert.That(support.IsAvailable, Is.True);
            native.ResetCallCounts();
            return session;
        }

        private static GpuTimestampToken Acquire(
            GpuTimestampSession session,
            ulong userTag,
            int sourceFrame)
        {
            GpuTimestampStatus status = session.Acquire(
                userTag,
                GpuTimestampSampleFlags.None,
                sourceFrame,
                out GpuTimestampToken token);
            Assert.That(status, Is.EqualTo(GpuTimestampStatus.Ready));
            Assert.That(token.IsValid, Is.True);
            return token;
        }
    }
}
