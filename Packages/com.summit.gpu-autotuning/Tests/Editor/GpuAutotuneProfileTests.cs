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
                "exclusive-scan", device, Environment(), out GpuPrimitiveBackend backend),
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
                Device(0x10DE, 0x2B85), Environment(),
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

        private static GpuCalibrationEnvironment Environment() => new GpuCalibrationEnvironment
        { unityVersion = "6000.5", compilerIdentity = "dxc-test-flags", shaderIdentity = "shader-a", buildIdentity = "build-a" };

        [TestCase("unityVersion")]
        [TestCase("compilerIdentity")]
        [TestCase("shaderIdentity")]
        [TestCase("buildIdentity")]
        public void RejectsIndependentlyChangedEnvironment(string field)
        {
            var device = Device(0x1002, 0x7551);
            var profile = Profile(device, true, "WaveOps");
            profile.sourceCommit = "same-commit-does-not-prove-compatibility";
            var current = Environment();
            typeof(GpuCalibrationEnvironment).GetField(field).SetValue(current, "changed");
            Assert.That(profile.TryResolve("exclusive-scan", device, current, out _), Is.False);
        }

        [Test]
        public void DriverChangeAndMissingDriverInvalidateCalibration()
        {
            var device = Device(0x1002, 0x7551);
            var profile = Profile(device, true, "WaveOps");
            var current = device.Copy(); current.driverVersion = "new driver";
            Assert.That(profile.TryResolve("exclusive-scan", current, Environment(), out _), Is.False);
            current.driverVersion = "";
            Assert.That(profile.TryResolve("exclusive-scan", current, Environment(), out _), Is.False);
        }

        [Test]
        public void OpaqueCandidateRequiresKnownExecutableIdAndUniqueWorkload()
        {
            var device = Device(0x1002, 0x7551);
            var profile = Profile(device, true, "WaveOps");
            const string id = "primitives-v1-wave-t128-e4-r4";
            profile.workloads[0].selectedCandidateId = id;
            Assert.That(profile.TryResolveCandidate("exclusive-scan", device, Environment(),
                new[] { id }, out var selected, out _), Is.True);
            Assert.That(selected, Is.EqualTo(id));
            Assert.That(profile.TryResolve("exclusive-scan", device, Environment(), out _), Is.False);
            Assert.That(profile.TryResolveCandidate("exclusive-scan", device, Environment(),
                new[] { "unknown" }, out _, out _), Is.False);
            profile.workloads = new[] { profile.workloads[0], profile.workloads[0] };
            Assert.That(profile.TryResolveCandidate("exclusive-scan", device, Environment(),
                new[] { id }, out _, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("duplicate-workload"));
        }

        [Test]
        public void LegacySchemaAndIdentityFreeApisFallBackAndStoreRoundTrips()
        {
            var device = Device(0x1002, 0x7551);
            var profile = Profile(device, true, "WaveOps");
            string path = System.IO.Path.GetTempFileName();
            try
            {
                Assert.That(profile.TryResolve("exclusive-scan", device, out _), Is.False);
                GpuAutotuneProfileStore.Save(path, profile);
                Assert.That(GpuAutotuneProfileStore.TryLoad(path, device, out _), Is.False);
                Assert.That(GpuAutotuneProfileStore.TryLoad(path, device, Environment(), out var loaded), Is.True);
                Assert.That(loaded.TryResolve("exclusive-scan", device, Environment(), out _), Is.True);
                profile.schemaVersion = 1;
                GpuAutotuneProfileStore.Save(path, profile);
                Assert.That(GpuAutotuneProfileStore.TryLoad(path, device, Environment(), out _), Is.False);
                System.IO.File.WriteAllText(path, "{malformed");
                Assert.That(GpuAutotuneProfileStore.TryLoad(path, device, Environment(), out _), Is.False);
            }
            finally { System.IO.File.Delete(path); }
        }

        [Test]
        public void SteadyStateResolverDoesNotAllocate()
        {
            var device = Device(0x1002, 0x7551);
            var resolver = new GpuPrimitiveBackendResolver(Profile(device, true, "WaveOps"), device, Environment());
            for (int i = 0; i < 100; i++) resolver.Resolve("exclusive-scan");
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) resolver.Resolve("exclusive-scan");
            long allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.Zero);
        }

        [Test]
        public void GraphicsApiVersionChangeInvalidatesEvenWhenDriverMatches()
        {
            var device = Device(0x1002, 0x7551);
            var profile = Profile(device, true, "WaveOps");
            var current = device.Copy(); current.graphicsVersion = "changed graphics runtime";
            Assert.That(profile.TryResolve("exclusive-scan", current, Environment(), out _), Is.False);
        }

        private static GpuAutotuneProfile Profile(
            GpuDeviceFingerprint device,
            bool accepted,
            string backend)
        {
            return new GpuAutotuneProfile
            {
                device = device,
                environment = Environment(),
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
                driverVersion = "test-driver",
                shaderLevel = 50
            };
        }
    }
}
