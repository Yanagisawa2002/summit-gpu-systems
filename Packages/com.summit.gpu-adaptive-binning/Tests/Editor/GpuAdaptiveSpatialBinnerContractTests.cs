using System;
using NUnit.Framework;
using Summit.GpuPrimitives;
using UnityEngine.Rendering;

namespace Summit.GpuAdaptiveBinning.Tests
{
    public sealed class GpuAdaptiveSpatialBinnerContractTests
    {
        private const int AmdVendorId = 0x1002;
        private const int R9700DeviceId = 0x7551;
        private const string CandidateProfileId =
            "test-device-dx12-bounded-cells-v3";

        [TestCase(1, 1)]
        [TestCase(2, 1)]
        [TestCase(3, 2)]
        [TestCase(16, 4)]
        [TestCase(17, 5)]
        [TestCase(4096, 12)]
        [TestCase(4097, 13)]
        [TestCase(65536, 16)]
        public void RequiredKeyBitCountIsMinimal(
            int binCount,
            int expected)
        {
            Assert.That(
                GpuRadixSpatialBinner.GetRequiredKeyBitCount(binCount),
                Is.EqualTo(expected));
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void RequiredKeyBitCountRejectsInvalidCounts(int binCount)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => GpuRadixSpatialBinner.GetRequiredKeyBitCount(
                    binCount));
        }

        [Test]
        public void BackendEnumDoesNotExposeUncalibratedAuto()
        {
            Assert.That(
                Enum.GetNames(typeof(GpuAdaptiveBinningBackend)),
                Is.EqualTo(new[] { "Direct", "Radix" }));
        }

        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            true,
            9u,
            GpuAdaptiveBinningBackend.Radix)]
        [TestCase(
            1048576,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            true,
            10u,
            GpuAdaptiveBinningBackend.Radix)]
        [TestCase(
            524288,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            true,
            9u,
            GpuAdaptiveBinningBackend.Direct)]
        [TestCase(
            524288,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            true,
            10u,
            GpuAdaptiveBinningBackend.Direct)]
        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            true,
            10u,
            GpuAdaptiveBinningBackend.Direct)]
        [TestCase(
            1048576,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            true,
            9u,
            GpuAdaptiveBinningBackend.Direct)]
        [TestCase(
            262144,
            15,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            true,
            9u,
            GpuAdaptiveBinningBackend.Direct)]
        [TestCase(
            262144,
            17,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            true,
            9u,
            GpuAdaptiveBinningBackend.Direct)]
        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.Hotset,
            false,
            0u,
            GpuAdaptiveBinningBackend.Direct)]
        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.General,
            false,
            0u,
            GpuAdaptiveBinningBackend.Direct)]
        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.Unknown,
            false,
            0u,
            GpuAdaptiveBinningBackend.Direct)]
        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            false,
            0u,
            GpuAdaptiveBinningBackend.Direct)]
        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            true,
            16u,
            GpuAdaptiveBinningBackend.Direct)]
        public void SelectorMatchesOnlyRegisteredExactCells(
            int elementCount,
            int binCount,
            GpuAdaptiveBinningWorkloadConcentration concentration,
            bool hasExactSingleBinKey,
            uint exactSingleBinKey,
            GpuAdaptiveBinningBackend expected)
        {
            GpuAdaptiveBinningCalibrationProfile profile =
                CreateCandidateProfile();
            var hint = new GpuAdaptiveBinningWorkloadHint(
                elementCount,
                binCount,
                concentration,
                hasExactSingleBinKey,
                exactSingleBinKey);
            GpuAdaptiveBinningDeviceIdentity identity =
                CreateR9700Dx12Identity();

            Assert.That(
                Select(
                    profile,
                    hint,
                    identity,
                    elementCount,
                    binCount),
                Is.EqualTo(expected));
        }

        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            true,
            9u,
            true)]
        [TestCase(
            1048576,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            true,
            10u,
            true)]
        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            false,
            0u,
            false)]
        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            true,
            16u,
            false)]
        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.Hotset,
            false,
            0u,
            true)]
        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.Hotset,
            true,
            9u,
            false)]
        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.General,
            false,
            0u,
            true)]
        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.Unknown,
            false,
            0u,
            false)]
        [TestCase(
            0,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            true,
            9u,
            false)]
        public void WorkloadHintRequiresConsistentCallerOwnedKeyEvidence(
            int elementCount,
            int binCount,
            GpuAdaptiveBinningWorkloadConcentration concentration,
            bool hasExactSingleBinKey,
            uint exactSingleBinKey,
            bool expectedValid)
        {
            var hint = new GpuAdaptiveBinningWorkloadHint(
                elementCount,
                binCount,
                concentration,
                hasExactSingleBinKey,
                exactSingleBinKey);

            Assert.That(hint.IsValid, Is.EqualTo(expectedValid));
        }

        [Test]
        public void HintProblemSizeMismatchFailsClosedToDirect()
        {
            GpuAdaptiveBinningCalibrationProfile profile =
                CreateCandidateProfile();
            GpuAdaptiveBinningWorkloadHint hint =
                CreateSingleBinHint(262144, 16, 9u);
            GpuAdaptiveBinningDeviceIdentity identity =
                CreateR9700Dx12Identity();

            AssertDirect(profile, hint, identity, 262145, 16);
            AssertDirect(profile, hint, identity, 262144, 17);
        }

        [Test]
        public void UntrustedDomainFailsClosedToDirect()
        {
            GpuAdaptiveBinningCalibrationProfile profile =
                CreateCandidateProfile();
            GpuAdaptiveBinningWorkloadHint hint =
                CreateSingleBinHint(262144, 16, 9u);
            GpuAdaptiveBinningDeviceIdentity identity =
                CreateR9700Dx12Identity();

            Assert.That(
                Select(
                    profile,
                    hint,
                    identity,
                    262144,
                    16,
                    keyDomain: GpuAdaptiveBinningKeyDomain.Untrusted),
                Is.EqualTo(GpuAdaptiveBinningBackend.Direct));
        }

        [Test]
        public void ExactR9700ProfileRejectsEveryHardwareMismatch()
        {
            GpuAdaptiveBinningCalibrationProfile profile =
                CreateCandidateProfile();
            GpuAdaptiveBinningWorkloadHint hint =
                CreateSingleBinHint(262144, 16, 9u);
            GpuAdaptiveBinningDeviceIdentity matching =
                CreateR9700Dx12Identity();
            var wrongVendor = new GpuAdaptiveBinningDeviceIdentity(
                0x10de,
                R9700DeviceId,
                GraphicsDeviceType.Direct3D12);
            var wrongDevice = new GpuAdaptiveBinningDeviceIdentity(
                AmdVendorId,
                R9700DeviceId + 1,
                GraphicsDeviceType.Direct3D12);
            var wrongApi = new GpuAdaptiveBinningDeviceIdentity(
                AmdVendorId,
                R9700DeviceId,
                GraphicsDeviceType.Vulkan);

            Assert.That(
                Select(profile, hint, matching, 262144, 16),
                Is.EqualTo(GpuAdaptiveBinningBackend.Radix));
            AssertDirect(profile, hint, wrongVendor, 262144, 16);
            AssertDirect(profile, hint, wrongDevice, 262144, 16);
            AssertDirect(profile, hint, wrongApi, 262144, 16);
        }

        [TestCase(
            GpuPrimitiveBackend.WaveOps,
            false,
            GpuAdaptiveBinningBackend.Radix)]
        [TestCase(
            GpuPrimitiveBackend.Auto,
            false,
            GpuAdaptiveBinningBackend.Direct)]
        [TestCase(
            GpuPrimitiveBackend.Portable,
            false,
            GpuAdaptiveBinningBackend.Direct)]
        [TestCase(
            GpuPrimitiveBackend.WaveOps,
            true,
            GpuAdaptiveBinningBackend.Direct)]
        [TestCase(
            (GpuPrimitiveBackend)99,
            false,
            GpuAdaptiveBinningBackend.Direct)]
        public void SelectorRequiresMeasuredWaveOpsMarkersOffVariant(
            GpuPrimitiveBackend primitiveBackend,
            bool profilerMarkersEnabled,
            GpuAdaptiveBinningBackend expected)
        {
            GpuAdaptiveBinningCalibrationProfile profile =
                CreateCandidateProfile();
            GpuAdaptiveBinningWorkloadHint hint =
                CreateSingleBinHint(262144, 16, 9u);
            GpuAdaptiveBinningDeviceIdentity identity =
                CreateR9700Dx12Identity();

            Assert.That(
                Select(
                    profile,
                    hint,
                    identity,
                    262144,
                    16,
                    primitiveBackend: primitiveBackend,
                    profilerMarkersEnabled: profilerMarkersEnabled),
                Is.EqualTo(expected));
        }

        [TestCase(GpuPrimitiveBackend.WaveOps, false, true)]
        [TestCase(GpuPrimitiveBackend.Auto, false, false)]
        [TestCase(GpuPrimitiveBackend.Portable, false, false)]
        [TestCase(GpuPrimitiveBackend.WaveOps, true, false)]
        [TestCase((GpuPrimitiveBackend)99, false, false)]
        public void SchemaV3ProfileAcceptsOnlyMeasuredExecutionContract(
            GpuPrimitiveBackend requiredPrimitiveBackend,
            bool requiredProfilerMarkersEnabled,
            bool expectedValid)
        {
            GpuAdaptiveBinningCalibrationProfile profile =
                CreateCandidateProfile(
                    requiredPrimitiveBackend,
                    requiredProfilerMarkersEnabled);

            Assert.That(profile.IsValid, Is.EqualTo(expectedValid));
        }

        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            true,
            9u,
            true)]
        [TestCase(
            1048576,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            true,
            10u,
            true)]
        [TestCase(
            0,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            true,
            9u,
            false)]
        [TestCase(
            262144,
            0,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            true,
            0u,
            false)]
        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.Hotset,
            false,
            0u,
            true)]
        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            false,
            0u,
            true)]
        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            false,
            9u,
            false)]
        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.General,
            false,
            0u,
            true)]
        [TestCase(
            262144,
            16,
            GpuAdaptiveBinningWorkloadConcentration.SingleBinGuaranteed,
            true,
            16u,
            false)]
        public void CalibrationCellRequiresConsistentConcentrationEvidence(
            int elementCount,
            int binCount,
            GpuAdaptiveBinningWorkloadConcentration concentration,
            bool hasExactSingleBinKey,
            uint exactSingleBinKey,
            bool expectedValid)
        {
            var cell = new GpuAdaptiveBinningCalibrationCell(
                elementCount,
                binCount,
                concentration,
                hasExactSingleBinKey,
                exactSingleBinKey);

            Assert.That(cell.IsValid, Is.EqualTo(expectedValid));
        }

        [Test]
        public void LegacyConstructorAcceptsOneOrTwoDistinctValidCellsOnly()
        {
            GpuAdaptiveBinningCalibrationCell cell0 =
                CreateCandidateCell0();
            GpuAdaptiveBinningCalibrationCell cell1 =
                CreateCandidateCell1();
            var invalidCell = new GpuAdaptiveBinningCalibrationCell(
                262144,
                16,
                GpuAdaptiveBinningWorkloadConcentration
                    .SingleBinGuaranteed,
                hasExactSingleBinKey: false,
                exactSingleBinKey: 9u);

            Assert.That(
                CreateProfile(1, cell0, default).IsValid,
                Is.True);
            Assert.That(
                CreateProfile(2, cell0, cell1).IsValid,
                Is.True);
            Assert.That(
                CreateProfile(0, cell0, default).IsValid,
                Is.False);
            Assert.That(
                CreateProfile(3, cell0, cell1).IsValid,
                Is.False);
            Assert.That(
                CreateProfile(1, cell0, cell1).IsValid,
                Is.False);
            Assert.That(
                CreateProfile(2, cell0, default).IsValid,
                Is.False);
            Assert.That(
                CreateProfile(2, cell0, invalidCell).IsValid,
                Is.False);
            Assert.That(
                CreateProfile(2, cell0, cell0).IsValid,
                Is.False);
        }

        [Test]
        public void ProfileSupportsBoundedMixedSurfaceAndRejectsOverlap()
        {
            var anySingleBin = new GpuAdaptiveBinningCalibrationCell(
                1048576,
                16,
                GpuAdaptiveBinningWorkloadConcentration
                    .SingleBinGuaranteed,
                hasExactSingleBinKey: false,
                exactSingleBinKey: 0u);
            var exactSingleBin = new GpuAdaptiveBinningCalibrationCell(
                1048576,
                16,
                GpuAdaptiveBinningWorkloadConcentration
                    .SingleBinGuaranteed,
                hasExactSingleBinKey: true,
                exactSingleBinKey: 5u);
            var hotset = new GpuAdaptiveBinningCalibrationCell(
                1048576,
                16,
                GpuAdaptiveBinningWorkloadConcentration.Hotset,
                hasExactSingleBinKey: false,
                exactSingleBinKey: 0u);
            var general = new GpuAdaptiveBinningCalibrationCell(
                1048576,
                16,
                GpuAdaptiveBinningWorkloadConcentration.General,
                hasExactSingleBinKey: false,
                exactSingleBinKey: 0u);
            GpuAdaptiveBinningCalibrationProfile profile =
                CreateArrayProfile(anySingleBin, hotset, general);

            Assert.That(profile.IsValid, Is.True);
            Assert.That(profile.RadixCellCount, Is.EqualTo(3));
            Assert.That(
                profile.MatchesRadixCell(
                    new GpuAdaptiveBinningWorkloadHint(
                        1048576,
                        16,
                        GpuAdaptiveBinningWorkloadConcentration
                            .SingleBinGuaranteed,
                        hasExactSingleBinKey: true,
                        exactSingleBinKey: 13u)),
                Is.True);
            Assert.That(
                profile.MatchesRadixCell(
                    new GpuAdaptiveBinningWorkloadHint(
                        1048576,
                        16,
                        GpuAdaptiveBinningWorkloadConcentration.Hotset,
                        hasExactSingleBinKey: false,
                        exactSingleBinKey: 0u)),
                Is.True);
            Assert.That(
                CreateArrayProfile(anySingleBin, exactSingleBin).IsValid,
                Is.False);

            var tooMany = new GpuAdaptiveBinningCalibrationCell[
                GpuAdaptiveBinningCalibrationProfile
                    .MaximumRadixCellCount + 1];
            for (int index = 0; index < tooMany.Length; index++)
            {
                tooMany[index] = new GpuAdaptiveBinningCalibrationCell(
                    1048576 + index,
                    16,
                    GpuAdaptiveBinningWorkloadConcentration.General,
                    hasExactSingleBinKey: false,
                    exactSingleBinKey: 0u);
            }

            Assert.That(CreateArrayProfile(tooMany).IsValid, Is.False);
        }

        [Test]
        public void ProfileCopiesCallerOwnedCellStorage()
        {
            GpuAdaptiveBinningCalibrationCell original =
                CreateCandidateCell0();
            var cells = new[] { original };
            GpuAdaptiveBinningCalibrationProfile profile =
                CreateArrayProfile(cells);

            cells[0] = default;

            Assert.That(profile.IsValid, Is.True);
            Assert.That(profile.RadixCell0.IsSameCell(original), Is.True);
        }

        [Test]
        public void SchemaIdentityAndHardwareFieldsAreMandatory()
        {
            GpuAdaptiveBinningCalibrationCell cell0 =
                CreateCandidateCell0();
            GpuAdaptiveBinningCalibrationCell cell1 =
                CreateCandidateCell1();
            var missingVendor = new GpuAdaptiveBinningDeviceBinding(
                0,
                R9700DeviceId,
                GraphicsDeviceType.Direct3D12);
            var missingDevice = new GpuAdaptiveBinningDeviceBinding(
                AmdVendorId,
                0,
                GraphicsDeviceType.Direct3D12);
            var missingApi = new GpuAdaptiveBinningDeviceBinding(
                AmdVendorId,
                R9700DeviceId,
                GraphicsDeviceType.Null);

            Assert.That(
                CreateProfile(
                    2,
                    cell0,
                    cell1,
                    schemaVersion:
                        GpuAdaptiveBinningCalibrationProfile
                            .CurrentSchemaVersion - 1).IsValid,
                Is.False);
            Assert.That(
                CreateProfile(
                    2,
                    cell0,
                    cell1,
                    profileId: string.Empty).IsValid,
                Is.False);
            Assert.That(
                CreateProfile(
                    2,
                    cell0,
                    cell1,
                    profileRevision: 0).IsValid,
                Is.False);
            Assert.That(
                CreateProfile(
                    2,
                    cell0,
                    cell1,
                    deviceBinding: default,
                    useExactDeviceBinding: false).IsValid,
                Is.False);
            Assert.That(
                CreateProfile(
                    2,
                    cell0,
                    cell1,
                    deviceBinding: missingVendor,
                    useExactDeviceBinding: false).IsValid,
                Is.False);
            Assert.That(
                CreateProfile(
                    2,
                    cell0,
                    cell1,
                    deviceBinding: missingDevice,
                    useExactDeviceBinding: false).IsValid,
                Is.False);
            Assert.That(
                CreateProfile(
                    2,
                    cell0,
                    cell1,
                    deviceBinding: missingApi,
                    useExactDeviceBinding: false).IsValid,
                Is.False);
        }

        private static GpuAdaptiveBinningCalibrationProfile
            CreateCandidateProfile(
                GpuPrimitiveBackend requiredPrimitiveBackend =
                    GpuPrimitiveBackend.WaveOps,
                bool requiredProfilerMarkersEnabled = false)
        {
            return CreateProfile(
                2,
                CreateCandidateCell0(),
                CreateCandidateCell1(),
                requiredPrimitiveBackend:
                    requiredPrimitiveBackend,
                requiredProfilerMarkersEnabled:
                    requiredProfilerMarkersEnabled);
        }

        private static GpuAdaptiveBinningCalibrationProfile
            CreateProfile(
                int radixCellCount,
                GpuAdaptiveBinningCalibrationCell radixCell0,
                GpuAdaptiveBinningCalibrationCell radixCell1,
                GpuAdaptiveBinningDeviceBinding deviceBinding =
                    default,
                bool useExactDeviceBinding = true,
                GpuPrimitiveBackend requiredPrimitiveBackend =
                    GpuPrimitiveBackend.WaveOps,
                bool requiredProfilerMarkersEnabled = false,
                int schemaVersion =
                    GpuAdaptiveBinningCalibrationProfile
                        .CurrentSchemaVersion,
                string profileId = CandidateProfileId,
                int profileRevision = 2)
        {
            if (useExactDeviceBinding)
            {
                deviceBinding = CreateR9700Dx12Binding();
            }

            return new GpuAdaptiveBinningCalibrationProfile(
                schemaVersion,
                profileId,
                profileRevision,
                deviceBinding,
                requiredPrimitiveBackend,
                requiredProfilerMarkersEnabled,
                radixCellCount,
                radixCell0,
                radixCell1);
        }

        private static GpuAdaptiveBinningCalibrationProfile
            CreateArrayProfile(
                params GpuAdaptiveBinningCalibrationCell[] radixCells)
        {
            return new GpuAdaptiveBinningCalibrationProfile(
                GpuAdaptiveBinningCalibrationProfile.CurrentSchemaVersion,
                CandidateProfileId,
                profileRevision: 3,
                CreateR9700Dx12Binding(),
                GpuPrimitiveBackend.WaveOps,
                requiredProfilerMarkersEnabled: false,
                radixCells);
        }

        private static GpuAdaptiveBinningCalibrationCell
            CreateCandidateCell0()
        {
            return new GpuAdaptiveBinningCalibrationCell(
                262144,
                16,
                GpuAdaptiveBinningWorkloadConcentration
                    .SingleBinGuaranteed,
                hasExactSingleBinKey: true,
                exactSingleBinKey: 9u);
        }

        private static GpuAdaptiveBinningCalibrationCell
            CreateCandidateCell1()
        {
            return new GpuAdaptiveBinningCalibrationCell(
                1048576,
                16,
                GpuAdaptiveBinningWorkloadConcentration
                    .SingleBinGuaranteed,
                hasExactSingleBinKey: true,
                exactSingleBinKey: 10u);
        }

        private static GpuAdaptiveBinningWorkloadHint
            CreateSingleBinHint(
                int elementCount,
                int binCount,
                uint exactSingleBinKey)
        {
            return new GpuAdaptiveBinningWorkloadHint(
                elementCount,
                binCount,
                GpuAdaptiveBinningWorkloadConcentration
                    .SingleBinGuaranteed,
                hasExactSingleBinKey: true,
                exactSingleBinKey);
        }

        private static GpuAdaptiveBinningDeviceBinding
            CreateR9700Dx12Binding()
        {
            return new GpuAdaptiveBinningDeviceBinding(
                AmdVendorId,
                R9700DeviceId,
                GraphicsDeviceType.Direct3D12);
        }

        private static GpuAdaptiveBinningDeviceIdentity
            CreateR9700Dx12Identity()
        {
            return new GpuAdaptiveBinningDeviceIdentity(
                AmdVendorId,
                R9700DeviceId,
                GraphicsDeviceType.Direct3D12);
        }

        private static GpuAdaptiveBinningBackend Select(
            GpuAdaptiveBinningCalibrationProfile profile,
            GpuAdaptiveBinningWorkloadHint hint,
            GpuAdaptiveBinningDeviceIdentity identity,
            int elementCount,
            int binCount,
            GpuAdaptiveBinningKeyDomain keyDomain =
                GpuAdaptiveBinningKeyDomain.GuaranteedInRange,
            GpuPrimitiveBackend primitiveBackend =
                GpuPrimitiveBackend.WaveOps,
            bool profilerMarkersEnabled = false)
        {
            return GpuAdaptiveBinningSelector.SelectBackend(
                in profile,
                in hint,
                in identity,
                elementCount,
                binCount,
                keyDomain,
                primitiveBackend,
                profilerMarkersEnabled);
        }

        private static void AssertDirect(
            GpuAdaptiveBinningCalibrationProfile profile,
            GpuAdaptiveBinningWorkloadHint hint,
            GpuAdaptiveBinningDeviceIdentity identity,
            int elementCount,
            int binCount)
        {
            Assert.That(
                Select(
                    profile,
                    hint,
                    identity,
                    elementCount,
                    binCount),
                Is.EqualTo(GpuAdaptiveBinningBackend.Direct));
        }
    }
}
