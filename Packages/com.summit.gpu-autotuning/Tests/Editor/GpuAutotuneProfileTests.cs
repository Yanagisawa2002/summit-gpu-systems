using NUnit.Framework;
using Summit.GpuPrimitives;

namespace Summit.GpuAutotuning.Tests
{
    public sealed class GpuAutotuneProfileTests
    {
        [Test]
        public void ResolvesAcceptedSelectionForExactDevice()
        {
            GpuDeviceFingerprint device = Device(0x1002, 0x7551);
            GpuAutotuneProfile profile = Profile(device, true, "WaveOps");
            Assert.That(profile.TryResolve(
                "exclusive-scan", device, out GpuPrimitiveBackend backend),
                Is.True);
            Assert.That(backend, Is.EqualTo(GpuPrimitiveBackend.WaveOps));
        }

        [Test]
        public void RejectsProfileFromDifferentDevice()
        {
            GpuAutotuneProfile profile = Profile(
                Device(0x1002, 0x7551), true, "WaveOps");
            Assert.That(profile.TryResolve(
                "exclusive-scan",
                Device(0x10DE, 0x2B85),
                out GpuPrimitiveBackend backend), Is.False);
            Assert.That(backend, Is.EqualTo(GpuPrimitiveBackend.Auto));
        }

        [Test]
        public void ResolverFallsBackToCapabilityAutoForMissingWorkload()
        {
            GpuDeviceFingerprint device = Device(0x1002, 0x7551);
            GpuPrimitiveBackendResolver resolver =
                new GpuPrimitiveBackendResolver(Profile(device, true, "WaveOps"), device);
            Assert.That(resolver.Resolve("radix-sort-32"),
                Is.EqualTo(GpuPrimitiveBackend.Auto));
        }

        [Test]
        public void EvidenceSafeResolverFallsBackPortableForMissingWorkload()
        {
            GpuDeviceFingerprint device = Device(0x1002, 0x7551);
            GpuPrimitiveBackendResolver resolver =
                new GpuPrimitiveBackendResolver(
                    Profile(device, true, "WaveOps"), device);
            Assert.That(resolver.ResolveMeasuredOrPortable("radix-sort-32"),
                Is.EqualTo(GpuPrimitiveBackend.Portable));
            Assert.That(resolver.ResolveMeasuredOrPortable("exclusive-scan"),
                Is.EqualTo(GpuPrimitiveBackend.WaveOps));
        }

        private static GpuAutotuneProfile Profile(
            GpuDeviceFingerprint device,
            bool accepted,
            string backend)
        {
            return new GpuAutotuneProfile
            {
                device = device,
                workloads = new[]
                {
                    new GpuAutotuneWorkloadSelection
                    {
                        workloadId = "exclusive-scan",
                        selectedBackend = backend,
                        accepted = accepted
                    }
                }
            };
        }

        private static GpuDeviceFingerprint Device(int vendor, int device)
        {
            return new GpuDeviceFingerprint
            {
                vendorId = vendor,
                deviceId = device,
                vendor = vendor == 0x1002 ? "ATI" : "NVIDIA",
                deviceName = "test-device",
                graphicsApi = "Direct3D12",
                graphicsVersion = "Direct3D 12",
                shaderLevel = 50
            };
        }
    }
}
