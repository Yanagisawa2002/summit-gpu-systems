using System;
using NUnit.Framework;

namespace Summit.GpuTimestamps.Tests
{
    [TestFixture]
    public sealed class GpuTimestampUnsupportedTests
    {
        [Test]
        public void NullBackend_IsProgrammerMisuse()
        {
            Assert.Throws<ArgumentNullException>(
                () => GpuTimestampSession.TryCreateForTesting(
                    null,
                    out _,
                    out _));
        }

        [Test]
        public void MissingNativeLibrary_ReturnsExplicitUnsupportedResult()
        {
            AssertInitializationFailure(
                new DllNotFoundException("missing test library"),
                GpuTimestampAvailability.PluginNotFound);
        }

        [Test]
        public void MissingNativeEntryPoint_ReturnsExplicitUnsupportedResult()
        {
            AssertInitializationFailure(
                new EntryPointNotFoundException("missing test export"),
                GpuTimestampAvailability.EntryPointMissing);
        }

        [Test]
        public void WrongNativeBitness_ReturnsExplicitUnsupportedResult()
        {
            AssertInitializationFailure(
                new BadImageFormatException("wrong test architecture"),
                GpuTimestampAvailability.PluginNotFound);
        }

        [Test]
        public void UnexpectedInitializationException_IsContainedAndDiagnosed()
        {
            AssertInitializationFailure(
                new InvalidOperationException("synthetic initialization failure"),
                GpuTimestampAvailability.InitializationFailed);
        }

        [Test]
        public void AbiVersionMismatch_IsRejectedBeforePayloadDiscovery()
        {
            var native = new FakeGpuTimestampNativeApi(1)
            {
                AbiVersion = GpuTimestampSession.AbiVersion + 1
            };

            bool created = GpuTimestampSession.TryCreateForTesting(
                native,
                out GpuTimestampSession session,
                out GpuTimestampSupport support);

            Assert.That(created, Is.False);
            Assert.That(session, Is.Null);
            Assert.That(support.IsAvailable, Is.False);
            Assert.That(support.Availability, Is.EqualTo(GpuTimestampAvailability.AbiMismatch));
            Assert.That(support.Message, Is.Not.Empty);
            Assert.That(native.AcquireCallCount, Is.EqualTo(0));
        }

        [Test]
        public void InvalidEventIds_AreRejectedBeforePayloadDiscovery()
        {
            for (int duplicateCase = 0; duplicateCase < 2; duplicateCase++)
            {
                var native = new FakeGpuTimestampNativeApi(1);
                if (duplicateCase == 0)
                {
                    native.EventIds.End = native.EventIds.Begin;
                }
                else
                {
                    native.EventIds.Completion = native.EventIds.Frequency;
                }

                bool created = GpuTimestampSession.TryCreateForTesting(
                    native,
                    out GpuTimestampSession session,
                    out GpuTimestampSupport support);

                Assert.That(created, Is.False);
                Assert.That(session, Is.Null);
                Assert.That(
                    support.Availability,
                    Is.EqualTo(GpuTimestampAvailability.InitializationFailed));
                Assert.That(support.Message, Is.Not.Empty);
                Assert.That(native.AcquireCallCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void NativeUnsupported_IsNotEncodedAsAZeroDurationSession()
        {
            var native = new FakeGpuTimestampNativeApi(2);
            native.SupportInfo.Supported = 0;
            native.SupportInfo.TimestampFrequency = 0;
            native.SupportInfo.FrequencyReady = 0;
            native.SupportStatus = (int)GpuTimestampStatus.Unsupported;

            bool created = GpuTimestampSession.TryCreateForTesting(
                native,
                out GpuTimestampSession session,
                out GpuTimestampSupport support);

            Assert.That(created, Is.False);
            Assert.That(session, Is.Null);
            Assert.That(
                support.Availability,
                Is.EqualTo(GpuTimestampAvailability.NativeUnsupported));
            Assert.That(support.IsAvailable, Is.False);
            Assert.That(support.TimestampFrequency, Is.EqualTo(0UL));
            Assert.That(support.FrequencyReady, Is.False);
            Assert.That(support.Message, Is.Not.Empty);
        }

        [Test]
        public void MalformedSupportLayout_IsRejectedAsAbiMismatch()
        {
            var native = new FakeGpuTimestampNativeApi(1);
            native.SupportInfo.StructSize = 0;

            bool created = GpuTimestampSession.TryCreateForTesting(
                native,
                out GpuTimestampSession session,
                out GpuTimestampSupport support);

            Assert.That(created, Is.False);
            Assert.That(session, Is.Null);
            Assert.That(support.Availability, Is.EqualTo(GpuTimestampAvailability.AbiMismatch));
            Assert.That(support.Message, Is.Not.Empty);
            Assert.That(native.AcquireCallCount, Is.EqualTo(0));

            native = new FakeGpuTimestampNativeApi(1);
            native.SupportInfo.CapabilityFlags = 0x0Fu;

            created = GpuTimestampSession.TryCreateForTesting(
                native,
                out session,
                out support);

            Assert.That(created, Is.False);
            Assert.That(session, Is.Null);
            Assert.That(
                support.Availability,
                Is.EqualTo(GpuTimestampAvailability.AbiMismatch));
            Assert.That(support.Message, Is.Not.Empty);
            Assert.That(native.AcquireCallCount, Is.EqualTo(0));
        }

        [Test]
        public void MissingRenderCallback_IsRejectedBeforePayloadDiscovery()
        {
            var native = new FakeGpuTimestampNativeApi(1)
            {
                Callback = IntPtr.Zero
            };

            bool created = GpuTimestampSession.TryCreateForTesting(
                native,
                out GpuTimestampSession session,
                out GpuTimestampSupport support);

            Assert.That(created, Is.False);
            Assert.That(session, Is.Null);
            Assert.That(
                support.Availability,
                Is.EqualTo(GpuTimestampAvailability.InitializationFailed));
            Assert.That(support.Message, Is.Not.Empty);
            Assert.That(native.AcquireCallCount, Is.EqualTo(0));
        }

        [Test]
        public void DuplicateDiscoveryPayload_CancelsEveryAcquiredNativeToken()
        {
            var native = new FakeGpuTimestampNativeApi(2)
            {
                DuplicatePayloadOnSecondAcquire = true
            };

            bool created = GpuTimestampSession.TryCreateForTesting(
                native,
                out GpuTimestampSession session,
                out GpuTimestampSupport support);

            Assert.That(created, Is.False);
            Assert.That(session, Is.Null);
            Assert.That(
                support.Availability,
                Is.EqualTo(GpuTimestampAvailability.InitializationFailed));
            Assert.That(native.AcquireCallCount, Is.EqualTo(2));
            Assert.That(native.CancelCallCount, Is.EqualTo(2));
            Assert.That(native.ActiveNativeSampleCount, Is.EqualTo(0));
        }

        [TestCase(GpuTimestampAvailability.UnsupportedPlatform)]
        [TestCase(GpuTimestampAvailability.UnsupportedGraphicsApi)]
        public void ExplicitPreconditionStatus_IsUnavailableAndDiagnosed(
            GpuTimestampAvailability availability)
        {
            GpuTimestampSupport support = GpuTimestampSupport.Unavailable(
                availability,
                "Synthetic unsupported precondition.");

            Assert.That(support.Availability, Is.EqualTo(availability));
            Assert.That(support.IsAvailable, Is.False);
            Assert.That(support.Message, Is.Not.Empty);
        }

        private static void AssertInitializationFailure(
            Exception exception,
            GpuTimestampAvailability expectedAvailability)
        {
            var native = new FakeGpuTimestampNativeApi(1)
            {
                GetAbiVersionException = exception
            };

            GpuTimestampSession session = null;
            GpuTimestampSupport support = default;
            bool created = true;
            Assert.DoesNotThrow(
                () => created = GpuTimestampSession.TryCreateForTesting(
                    native,
                    out session,
                    out support));

            Assert.That(created, Is.False);
            Assert.That(session, Is.Null);
            Assert.That(support.IsAvailable, Is.False);
            Assert.That(support.Availability, Is.EqualTo(expectedAvailability));
            Assert.That(support.Message, Is.Not.Empty);
        }
    }
}
