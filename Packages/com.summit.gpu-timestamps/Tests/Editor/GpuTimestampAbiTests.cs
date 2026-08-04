using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;

namespace Summit.GpuTimestamps.Tests
{
    [TestFixture]
    public sealed class GpuTimestampAbiTests
    {
        [Test]
        public void AbiVersionAndStatusValues_MatchReviewedNativeHeader()
        {
            Assert.That(GpuTimestampSession.AbiVersion, Is.EqualTo(2u));
            Assert.That(Enum.GetUnderlyingType(typeof(GpuTimestampStatus)), Is.EqualTo(typeof(int)));

            Assert.That((int)GpuTimestampStatus.Ready, Is.EqualTo(1));
            Assert.That((int)GpuTimestampStatus.Pending, Is.EqualTo(0));
            Assert.That((int)GpuTimestampStatus.Error, Is.EqualTo(-1));
            Assert.That((int)GpuTimestampStatus.InvalidArgument, Is.EqualTo(-2));
            Assert.That((int)GpuTimestampStatus.InvalidToken, Is.EqualTo(-3));
            Assert.That((int)GpuTimestampStatus.RingFull, Is.EqualTo(-4));
            Assert.That((int)GpuTimestampStatus.NotInitialized, Is.EqualTo(-5));
            Assert.That((int)GpuTimestampStatus.Unsupported, Is.EqualTo(-6));
            Assert.That((int)GpuTimestampStatus.DeviceLost, Is.EqualTo(-7));
            Assert.That((int)GpuTimestampStatus.CallbackError, Is.EqualTo(-8));
            Assert.That((int)GpuTimestampStatus.FrequencyUnavailable, Is.EqualTo(-9));

            Assert.That((int)GpuTimestampStatus.StaleManagedToken, Is.EqualTo(-100));
            Assert.That((int)GpuTimestampStatus.MalformedNativeResult, Is.EqualTo(-101));
        }

        [Test]
        public void AvailabilityAndFlags_HaveStableManagedValues()
        {
            Assert.That(Enum.GetUnderlyingType(typeof(GpuTimestampAvailability)), Is.EqualTo(typeof(int)));
            Assert.That((int)GpuTimestampAvailability.Available, Is.EqualTo(1));
            Assert.That((int)GpuTimestampAvailability.UnsupportedPlatform, Is.EqualTo(-1));
            Assert.That((int)GpuTimestampAvailability.UnsupportedGraphicsApi, Is.EqualTo(-2));
            Assert.That((int)GpuTimestampAvailability.PluginNotFound, Is.EqualTo(-3));
            Assert.That((int)GpuTimestampAvailability.EntryPointMissing, Is.EqualTo(-4));
            Assert.That((int)GpuTimestampAvailability.AbiMismatch, Is.EqualTo(-5));
            Assert.That((int)GpuTimestampAvailability.NativeUnsupported, Is.EqualTo(-6));
            Assert.That((int)GpuTimestampAvailability.SessionAlreadyActive, Is.EqualTo(-7));
            Assert.That((int)GpuTimestampAvailability.InitializationFailed, Is.EqualTo(-8));

            Assert.That(
                Enum.GetUnderlyingType(typeof(GpuTimestampSampleFlags)),
                Is.EqualTo(typeof(uint)));
            Assert.That((uint)GpuTimestampSampleFlags.None, Is.EqualTo(0u));
            Assert.That((uint)GpuTimestampSampleFlags.EmptyScope, Is.EqualTo(1u));
        }

        [Test]
        public void NativeEventIds_HasExactPackSizeAndOffsets()
        {
            AssertSequentialPack8<NativeEventIds>();
            Assert.That(Marshal.SizeOf(typeof(NativeEventIds)), Is.EqualTo(16));
            AssertOffset<NativeEventIds>(nameof(NativeEventIds.Begin), 0);
            AssertOffset<NativeEventIds>(nameof(NativeEventIds.End), 4);
            AssertOffset<NativeEventIds>(nameof(NativeEventIds.Frequency), 8);
            AssertOffset<NativeEventIds>(nameof(NativeEventIds.Completion), 12);
        }

        [Test]
        public void NativeSupportInfo_HasExactPackSizeAndOffsets()
        {
            AssertSequentialPack8<NativeSupportInfo>();
            Assert.That(Marshal.SizeOf(typeof(NativeSupportInfo)), Is.EqualTo(40));
            AssertOffset<NativeSupportInfo>(nameof(NativeSupportInfo.StructSize), 0);
            AssertOffset<NativeSupportInfo>(nameof(NativeSupportInfo.AbiVersion), 4);
            AssertOffset<NativeSupportInfo>(nameof(NativeSupportInfo.Supported), 8);
            AssertOffset<NativeSupportInfo>(nameof(NativeSupportInfo.CapabilityFlags), 12);
            AssertOffset<NativeSupportInfo>(nameof(NativeSupportInfo.RingCapacity), 16);
            AssertOffset<NativeSupportInfo>(nameof(NativeSupportInfo.RendererType), 20);
            AssertOffset<NativeSupportInfo>(nameof(NativeSupportInfo.DeviceGeneration), 24);
            AssertOffset<NativeSupportInfo>(nameof(NativeSupportInfo.FrequencyReady), 28);
            AssertOffset<NativeSupportInfo>(nameof(NativeSupportInfo.TimestampFrequency), 32);
        }

        [Test]
        public void NativeTimestampResult_HasExactPackSizeAndOffsets()
        {
            AssertSequentialPack8<NativeTimestampResult>();
            Assert.That(Marshal.SizeOf(typeof(NativeTimestampResult)), Is.EqualTo(80));
            AssertOffset<NativeTimestampResult>(nameof(NativeTimestampResult.StructSize), 0);
            AssertOffset<NativeTimestampResult>(nameof(NativeTimestampResult.Flags), 4);
            AssertOffset<NativeTimestampResult>(nameof(NativeTimestampResult.Token), 8);
            AssertOffset<NativeTimestampResult>(nameof(NativeTimestampResult.UserTag), 16);
            AssertOffset<NativeTimestampResult>(nameof(NativeTimestampResult.BeginTicks), 24);
            AssertOffset<NativeTimestampResult>(nameof(NativeTimestampResult.EndTicks), 32);
            AssertOffset<NativeTimestampResult>(nameof(NativeTimestampResult.ElapsedTicks), 40);
            AssertOffset<NativeTimestampResult>(nameof(NativeTimestampResult.TimestampFrequency), 48);
            AssertOffset<NativeTimestampResult>(nameof(NativeTimestampResult.ElapsedMilliseconds), 56);
            AssertOffset<NativeTimestampResult>(nameof(NativeTimestampResult.FenceValue), 64);
            AssertOffset<NativeTimestampResult>(nameof(NativeTimestampResult.DeviceGeneration), 72);
            AssertOffset<NativeTimestampResult>(nameof(NativeTimestampResult.Reserved), 76);
        }

        [Test]
        public void NativeImports_UseExactLibraryExportsAndStdCall()
        {
            Type nativeMethods = typeof(PInvokeGpuTimestampNativeApi).GetNestedType(
                "NativeMethods",
                BindingFlags.NonPublic);
            Assert.That(nativeMethods, Is.Not.Null);

            var expected = new Dictionary<string, string>
            {
                { "GetAbiVersion", "SGT_GetAbiVersion" },
                { "GetRenderEventAndDataFunc", "SGT_GetRenderEventAndDataFunc" },
                { "GetEventIds", "SGT_GetEventIds" },
                { "GetSupportInfo", "SGT_GetSupportInfo" },
                { "AcquireSample", "SGT_AcquireSample" },
                { "MarkSubmitted", "SGT_MarkSubmitted" },
                { "CancelSample", "SGT_CancelSample" },
                { "TryConsumeResult", "SGT_TryConsumeResult" }
            };

            MethodInfo[] methods = nativeMethods.GetMethods(
                BindingFlags.Static |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly);
            Assert.That(methods.Length, Is.EqualTo(expected.Count));
            foreach (MethodInfo method in methods)
            {
                Assert.That(expected.ContainsKey(method.Name), Is.True, method.Name);
                object[] attributes = method.GetCustomAttributes(
                    typeof(DllImportAttribute),
                    false);
                Assert.That(attributes.Length, Is.EqualTo(1), method.Name);
                var import = (DllImportAttribute)attributes[0];
                Assert.That(import.Value, Is.EqualTo("SummitGpuTimestamps"), method.Name);
                Assert.That(import.EntryPoint, Is.EqualTo(expected[method.Name]), method.Name);
                Assert.That(
                    import.CallingConvention,
                    Is.EqualTo(CallingConvention.StdCall),
                    method.Name);
            }
        }

        [Test]
        public void DefaultTokenAndScope_AreInvalid()
        {
            Assert.That(default(GpuTimestampToken).IsValid, Is.False);
            Assert.That(default(GpuTimestampScope).IsValid, Is.False);
        }

        [Test]
        public void TokenValueEquality_IncludesSessionAndOpaqueGenerationValue()
        {
            var original = new GpuTimestampToken(
                11,
                0x0000000200000001UL,
                99,
                GpuTimestampSampleFlags.EmptyScope,
                0,
                17);
            var same = new GpuTimestampToken(
                11,
                0x0000000200000001UL,
                99,
                GpuTimestampSampleFlags.EmptyScope,
                0,
                17);
            var differentGeneration = new GpuTimestampToken(
                11,
                0x0000000300000001UL,
                99,
                GpuTimestampSampleFlags.EmptyScope,
                0,
                17);
            var differentSession = new GpuTimestampToken(
                12,
                0x0000000200000001UL,
                99,
                GpuTimestampSampleFlags.EmptyScope,
                0,
                17);

            Assert.That(original.IsValid, Is.True);
            Assert.That(original.Equals(same), Is.True);
            Assert.That(original.Equals(differentGeneration), Is.False);
            Assert.That(original.Equals(differentSession), Is.False);
        }

        private static void AssertSequentialPack8<T>()
        {
            StructLayoutAttribute layout = typeof(T).StructLayoutAttribute;
            Assert.That(layout, Is.Not.Null);
            Assert.That(layout.Value, Is.EqualTo(LayoutKind.Sequential));
            Assert.That(layout.Pack, Is.EqualTo(8));
        }

        private static void AssertOffset<T>(string fieldName, int expectedOffset)
        {
            int actual = Marshal.OffsetOf(typeof(T), fieldName).ToInt32();
            Assert.That(actual, Is.EqualTo(expectedOffset), typeof(T).Name + "." + fieldName);
        }
    }
}
