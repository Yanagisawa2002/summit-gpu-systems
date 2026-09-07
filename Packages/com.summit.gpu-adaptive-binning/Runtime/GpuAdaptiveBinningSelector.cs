using Summit.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuAdaptiveBinning
{
    /// <summary>
    /// Allocation-free numeric identity used to validate a calibration profile
    /// against the active graphics device.
    /// </summary>
    public readonly struct GpuAdaptiveBinningDeviceIdentity
    {
        public GpuAdaptiveBinningDeviceIdentity(
            int vendorId,
            int deviceId,
            GraphicsDeviceType graphicsApi)
        {
            VendorId = vendorId;
            DeviceId = deviceId;
            GraphicsApi = graphicsApi;
        }

        public int VendorId { get; }

        public int DeviceId { get; }

        public GraphicsDeviceType GraphicsApi { get; }

        public bool IsValid =>
            VendorId > 0 &&
            DeviceId > 0 &&
            GraphicsApi != GraphicsDeviceType.Null;

        /// <summary>
        /// Captures the current Unity device without copying vendor or device
        /// name strings. Cache the result outside a per-frame Record call.
        /// </summary>
        public static GpuAdaptiveBinningDeviceIdentity CaptureCurrent()
        {
            return new GpuAdaptiveBinningDeviceIdentity(
                SystemInfo.graphicsDeviceVendorID,
                SystemInfo.graphicsDeviceID,
                SystemInfo.graphicsDeviceType);
        }
    }

    /// <summary>
    /// Exact hardware binding for measured calibration evidence. The current
    /// schema requires vendor, device, and graphics API; it has no wildcards.
    /// </summary>
    public readonly struct GpuAdaptiveBinningDeviceBinding
    {
        public GpuAdaptiveBinningDeviceBinding(
            int vendorId,
            int deviceId,
            GraphicsDeviceType graphicsApi)
        {
            VendorId = vendorId;
            DeviceId = deviceId;
            GraphicsApi = graphicsApi;
        }

        public int VendorId { get; }

        public int DeviceId { get; }

        public GraphicsDeviceType GraphicsApi { get; }

        public bool IsValid =>
            VendorId > 0 &&
            DeviceId > 0 &&
            GraphicsApi != GraphicsDeviceType.Null;

        public bool Matches(
            in GpuAdaptiveBinningDeviceIdentity identity)
        {
            return IsValid &&
                identity.IsValid &&
                VendorId == identity.VendorId &&
                DeviceId == identity.DeviceId &&
                GraphicsApi == identity.GraphicsApi;
        }
    }

    /// <summary>
    /// Coarse concentration claim supplied by the producer.
    /// </summary>
    public enum GpuAdaptiveBinningWorkloadConcentration
    {
        Unknown = 0,
        SingleBinGuaranteed = 1,
        Hotset = 2,
        General = 3,
    }

    /// <summary>
    /// One exact workload cell classified through forced-backend replay.
    /// </summary>
    public readonly struct GpuAdaptiveBinningCalibrationCell
    {
        public GpuAdaptiveBinningCalibrationCell(
            int elementCount,
            int binCount,
            GpuAdaptiveBinningWorkloadConcentration concentration,
            bool hasExactSingleBinKey,
            uint exactSingleBinKey)
        {
            ElementCount = elementCount;
            BinCount = binCount;
            Concentration = concentration;
            HasExactSingleBinKey = hasExactSingleBinKey;
            ExactSingleBinKey = exactSingleBinKey;
        }

        public int ElementCount { get; }

        public int BinCount { get; }

        public GpuAdaptiveBinningWorkloadConcentration
            Concentration { get; }

        public bool HasExactSingleBinKey { get; }

        public uint ExactSingleBinKey { get; }

        public bool IsEmpty =>
            ElementCount == 0 &&
            BinCount == 0 &&
            Concentration ==
                GpuAdaptiveBinningWorkloadConcentration.Unknown &&
            !HasExactSingleBinKey &&
            ExactSingleBinKey == 0u;

        public bool IsValid =>
            ElementCount > 0 &&
            BinCount > 0 &&
            Concentration ==
                GpuAdaptiveBinningWorkloadConcentration
                    .SingleBinGuaranteed &&
            HasExactSingleBinKey &&
            ExactSingleBinKey < (uint)BinCount;

        public bool Matches(
            in GpuAdaptiveBinningWorkloadHint workloadHint)
        {
            return IsValid &&
                workloadHint.IsValid &&
                ElementCount == workloadHint.ElementCount &&
                BinCount == workloadHint.BinCount &&
                Concentration == workloadHint.Concentration &&
                workloadHint.HasExactSingleBinKey &&
                ExactSingleBinKey ==
                    workloadHint.ExactSingleBinKey;
        }

        public bool IsSameCell(GpuAdaptiveBinningCalibrationCell other)
        {
            return ElementCount == other.ElementCount &&
                BinCount == other.BinCount &&
                Concentration == other.Concentration &&
                HasExactSingleBinKey ==
                    other.HasExactSingleBinKey &&
                ExactSingleBinKey == other.ExactSingleBinKey;
        }
    }

    /// <summary>
    /// Versioned, caller-owned exact-cell classification derived from
    /// forced-backend replay. There is no built-in profile.
    /// </summary>
    public readonly struct GpuAdaptiveBinningCalibrationProfile
    {
        public const int CurrentSchemaVersion = 2;

        public GpuAdaptiveBinningCalibrationProfile(
            int schemaVersion,
            string profileId,
            int profileRevision,
            GpuAdaptiveBinningDeviceBinding deviceBinding,
            GpuPrimitiveBackend requiredPrimitiveBackend,
            bool requiredProfilerMarkersEnabled,
            int radixCellCount,
            GpuAdaptiveBinningCalibrationCell radixCell0,
            GpuAdaptiveBinningCalibrationCell radixCell1)
        {
            SchemaVersion = schemaVersion;
            ProfileId = profileId;
            ProfileRevision = profileRevision;
            DeviceBinding = deviceBinding;
            RequiredPrimitiveBackend = requiredPrimitiveBackend;
            RequiredProfilerMarkersEnabled =
                requiredProfilerMarkersEnabled;
            RadixCellCount = radixCellCount;
            RadixCell0 = radixCell0;
            RadixCell1 = radixCell1;
        }

        public int SchemaVersion { get; }

        public string ProfileId { get; }

        public int ProfileRevision { get; }

        public GpuAdaptiveBinningDeviceBinding DeviceBinding { get; }

        public GpuPrimitiveBackend RequiredPrimitiveBackend { get; }

        public bool RequiredProfilerMarkersEnabled { get; }

        public int RadixCellCount { get; }

        public GpuAdaptiveBinningCalibrationCell RadixCell0 { get; }

        public GpuAdaptiveBinningCalibrationCell RadixCell1 { get; }

        public bool IsValid =>
            SchemaVersion == CurrentSchemaVersion &&
            !string.IsNullOrWhiteSpace(ProfileId) &&
            ProfileRevision > 0 &&
            DeviceBinding.IsValid &&
            RequiredPrimitiveBackend ==
                GpuPrimitiveBackend.WaveOps &&
            !RequiredProfilerMarkersEnabled &&
            HasValidCells();

        public bool IsCompatibleWith(
            in GpuAdaptiveBinningDeviceIdentity identity)
        {
            return IsValid && DeviceBinding.Matches(in identity);
        }

        public bool MatchesRadixCell(
            in GpuAdaptiveBinningWorkloadHint workloadHint)
        {
            if (!IsValid || !workloadHint.IsValid)
            {
                return false;
            }

            return RadixCell0.Matches(in workloadHint) ||
                (RadixCellCount == 2 &&
                 RadixCell1.Matches(in workloadHint));
        }

        private bool HasValidCells()
        {
            if (RadixCellCount == 1)
            {
                return RadixCell0.IsValid && RadixCell1.IsEmpty;
            }

            return RadixCellCount == 2 &&
                RadixCell0.IsValid &&
                RadixCell1.IsValid &&
                !RadixCell0.IsSameCell(RadixCell1);
        }
    }

    /// <summary>
    /// Caller-provided problem size and concentration evidence. The default
    /// value and Unknown concentration intentionally fail validation.
    /// </summary>
    public readonly struct GpuAdaptiveBinningWorkloadHint
    {
        public GpuAdaptiveBinningWorkloadHint(
            int elementCount,
            int binCount,
            GpuAdaptiveBinningWorkloadConcentration concentration,
            bool hasExactSingleBinKey,
            uint exactSingleBinKey)
        {
            ElementCount = elementCount;
            BinCount = binCount;
            Concentration = concentration;
            HasExactSingleBinKey = hasExactSingleBinKey;
            ExactSingleBinKey = exactSingleBinKey;
        }

        public int ElementCount { get; }

        public int BinCount { get; }

        public GpuAdaptiveBinningWorkloadConcentration
            Concentration { get; }

        public bool HasExactSingleBinKey { get; }

        public uint ExactSingleBinKey { get; }

        public bool IsValid
        {
            get
            {
                if (ElementCount < 0 || BinCount < 1)
                {
                    return false;
                }

                if (Concentration ==
                    GpuAdaptiveBinningWorkloadConcentration
                        .SingleBinGuaranteed)
                {
                    return ElementCount > 0 &&
                        HasExactSingleBinKey &&
                        ExactSingleBinKey < (uint)BinCount;
                }

                if (Concentration ==
                        GpuAdaptiveBinningWorkloadConcentration.Hotset ||
                    Concentration ==
                        GpuAdaptiveBinningWorkloadConcentration.General)
                {
                    return !HasExactSingleBinKey &&
                        ExactSingleBinKey == 0u;
                }

                return false;
            }
        }

        public bool MatchesProblemSize(
            int elementCount,
            int binCount)
        {
            return IsValid &&
                ElementCount == elementCount &&
                BinCount == binCount;
        }
    }

    /// <summary>
    /// Legacy v2 classification replay only. Runtime RecordAdaptive requires a v3 matrix
    /// with independent environment identity; the legacy runtime overload falls back to Direct.
    /// </summary>
    public static class GpuAdaptiveBinningSelector
    {
        public static GpuAdaptiveBinningBackend SelectBackend(
            in GpuAdaptiveBinningCalibrationProfile calibrationProfile,
            in GpuAdaptiveBinningWorkloadHint workloadHint,
            in GpuAdaptiveBinningDeviceIdentity deviceIdentity,
            int elementCount,
            int binCount,
            GpuAdaptiveBinningKeyDomain keyDomain,
            GpuPrimitiveBackend primitiveBackend,
            bool profilerMarkersEnabled)
        {
            if (keyDomain !=
                    GpuAdaptiveBinningKeyDomain.GuaranteedInRange ||
                !calibrationProfile.IsCompatibleWith(in deviceIdentity) ||
                !workloadHint.MatchesProblemSize(
                    elementCount,
                    binCount) ||
                primitiveBackend !=
                    calibrationProfile.RequiredPrimitiveBackend ||
                profilerMarkersEnabled !=
                    calibrationProfile.RequiredProfilerMarkersEnabled)
            {
                return GpuAdaptiveBinningBackend.Direct;
            }

            return calibrationProfile.MatchesRadixCell(
                in workloadHint)
                    ? GpuAdaptiveBinningBackend.Radix
                    : GpuAdaptiveBinningBackend.Direct;
        }
    }
}
