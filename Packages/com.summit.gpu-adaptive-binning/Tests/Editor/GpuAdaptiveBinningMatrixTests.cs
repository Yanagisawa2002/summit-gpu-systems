using NUnit.Framework;
using Summit.GpuAutotuning;

namespace Summit.GpuAdaptiveBinning.Tests
{
    public sealed class GpuAdaptiveBinningMatrixTests
    {
        internal static GpuDeviceFingerprint Device() => new GpuDeviceFingerprint
        { vendorId = 0x1002, deviceId = 0x7551, graphicsApi = "Direct3D12",
          graphicsVersion = "graphics-a", driverVersion = "driver-a", deviceName = "R9700", shaderLevel = 50 };
        internal static GpuCalibrationEnvironment Environment() => new GpuCalibrationEnvironment
        { unityVersion = "unity-a", compilerIdentity = "compiler-a", shaderIdentity = "shader-a", buildIdentity = "build-a" };
        internal static GpuAdaptiveBinningFeatures Features(int count = 128, string workload = "fixture-v4/singlebin") =>
            new GpuAdaptiveBinningFeatures { workloadId = workload, elementCount = count,
            binCount = 16, concentration = GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            occupiedBinCount = 1, maximumBinOccupancy = count, singleBinKey = 7 };
        internal static GpuAdaptiveBinningMatrixDocument Document()
        {
            var rows = new GpuAdaptiveBinningMatrixRow[8];
            for (int i = 0; i < rows.Length; i++) rows[i] = new GpuAdaptiveBinningMatrixRow
            { features = Features(128 + i), backend = GpuAdaptiveBinningBackend.Radix,
              primitiveCandidateId = "Portable", validationPassed = true, evidenceId = "independent-test-oracle", calibrationSamples = 3 };
            return new GpuAdaptiveBinningMatrixDocument { matrixId = "test-matrix", revision = 1,
                phase = "frozen", calibrationRunId = "calibration-a", device = Device(), environment = Environment(), rows = rows };
        }
        private static GpuAdaptiveBinningDecision Select(GpuAdaptiveBinningStableSelector selector,
            GpuAdaptiveBinningFeatures features, GpuAdaptiveBinningKeyDomain domain = GpuAdaptiveBinningKeyDomain.GuaranteedInRange) =>
            selector.Select(in features, features.elementCount, features.binCount, domain, "Portable", false);

        [Test]
        public void SupportsMoreThanTwoCellsWithoutInterpolatingOrAliasingWorkloads()
        {
            var matrix = new GpuAdaptiveBinningMatrix(Document());
            Assert.That(matrix.CellCount, Is.EqualTo(8));
            var selector = new GpuAdaptiveBinningStableSelector(matrix, Device(), Environment(), 1);
            for (int i = 0; i < 8; i++) Assert.That(Select(selector, Features(128 + i)).Backend, Is.EqualTo(GpuAdaptiveBinningBackend.Radix));
            Assert.That(Select(selector, Features(136)).Reason, Is.EqualTo(GpuAdaptiveBinningSelectionReason.UnknownCell));
            Assert.That(Select(selector, Features(workload: "different-generator")).Backend, Is.EqualTo(GpuAdaptiveBinningBackend.Direct));
        }
        [Test]
        public void GeneralAndHotsetCellsMatchFullOccupancyFeatures()
        {
            foreach (var concentration in new[] { GpuAdaptiveBinningWorkloadConcentration.General, GpuAdaptiveBinningWorkloadConcentration.Hotset })
            {
                var doc = Document(); var f = Features();
                f.concentration = concentration; f.occupiedBinCount = 16; f.maximumBinOccupancy = 8; f.singleBinKey = 0;
                doc.rows[0].features = f;
                var selector = new GpuAdaptiveBinningStableSelector(new GpuAdaptiveBinningMatrix(doc), Device(), Environment(), 1);
                Assert.That(Select(selector, f).Backend, Is.EqualTo(GpuAdaptiveBinningBackend.Radix));
                f.maximumBinOccupancy++;
                Assert.That(Select(selector, f).Reason, Is.EqualTo(GpuAdaptiveBinningSelectionReason.UnknownCell));
            }
        }
        [Test]
        public void HysteresisRequiresConsecutiveSameCellAndFallbackIsImmediate()
        {
            var selector = new GpuAdaptiveBinningStableSelector(new GpuAdaptiveBinningMatrix(Document()), Device(), Environment());
            Assert.That(Select(selector, Features()).Reason, Is.EqualTo(GpuAdaptiveBinningSelectionReason.HysteresisPending));
            Assert.That(Select(selector, Features(129)).Reason, Is.EqualTo(GpuAdaptiveBinningSelectionReason.HysteresisPending));
            Select(selector, Features()); Select(selector, Features());
            var promoted = Select(selector, Features());
            Assert.That(promoted.Backend, Is.EqualTo(GpuAdaptiveBinningBackend.Radix)); Assert.That(promoted.Switched, Is.True);
            var fallback = Select(selector, Features(), GpuAdaptiveBinningKeyDomain.Untrusted);
            Assert.That(fallback.Backend, Is.EqualTo(GpuAdaptiveBinningBackend.Direct)); Assert.That(fallback.Switched, Is.True);
            Assert.That(Select(selector, Features()).Reason, Is.EqualTo(GpuAdaptiveBinningSelectionReason.HysteresisPending));
        }
        [Test]
        public void SnapshotCannotBeChangedThroughSerializedDocument()
        {
            var doc = Document(); var matrix = new GpuAdaptiveBinningMatrix(doc);
            doc.device.graphicsVersion = "tampered"; doc.environment.buildIdentity = "tampered";
            doc.rows[0].validationPassed = false;
            Assert.That(Select(new GpuAdaptiveBinningStableSelector(matrix, Device(), Environment(), 1), Features()).Backend,
                Is.EqualTo(GpuAdaptiveBinningBackend.Radix));
        }
        [TestCase("discovery")]
        [TestCase("duplicate")]
        [TestCase("schema")]
        [TestCase("unvalidated")]
        [TestCase("samples")]
        [TestCase("candidate")]
        [TestCase("driver")]
        [TestCase("build")]
        public void BadEvidenceFailsClosed(string fault)
        {
            var doc = Document(); var device = Device(); var environment = Environment();
            switch (fault)
            {
                case "discovery": doc.phase = "discovery"; break;
                case "duplicate": doc.rows[1].features = doc.rows[0].features; break;
                case "schema": doc.schemaVersion = 2; break;
                case "unvalidated": doc.rows[0].validationPassed = false; break;
                case "samples": doc.rows[0].calibrationSamples = 2; break;
                case "candidate": doc.rows[0].primitiveCandidateId = "unimplemented-id"; break;
                case "driver": device.driverVersion = "new driver"; break;
                case "build": environment.buildIdentity = "new build"; break;
            }
            Assert.That(Select(new GpuAdaptiveBinningStableSelector(new GpuAdaptiveBinningMatrix(doc), device, environment, 1), Features()).Backend,
                Is.EqualTo(GpuAdaptiveBinningBackend.Direct));
        }
        [Test]
        public void SteadyStateSelectionDoesNotAllocate()
        {
            var selector = new GpuAdaptiveBinningStableSelector(new GpuAdaptiveBinningMatrix(Document()), Device(), Environment());
            var features = Features();
            for (int i = 0; i < 100; i++) Select(selector, features);
            long start = System.GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) Select(selector, features);
            long allocated = System.GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.That(allocated, Is.Zero);
        }

        [Test]
        public void FeatureGatheringRejectsUntrustedKeysAndUsesActualCounts()
        {
            var keys = new uint[] { 0, 1, 1, 3, 3 }; var scratch = new int[4];
            Assert.That(GpuAdaptiveBinningFeatures.TryGather(keys, 5, 4, "fixture", GpuAdaptiveBinningWorkloadConcentration.General,
                scratch, out var f), Is.True);
            Assert.That(f.occupiedBinCount, Is.EqualTo(3)); Assert.That(f.maximumBinOccupancy, Is.EqualTo(2));
            keys[0] = 4;
            Assert.That(GpuAdaptiveBinningFeatures.TryGather(keys, 5, 4, "fixture", GpuAdaptiveBinningWorkloadConcentration.General,
                scratch, out _), Is.False);
        }
    }
}
