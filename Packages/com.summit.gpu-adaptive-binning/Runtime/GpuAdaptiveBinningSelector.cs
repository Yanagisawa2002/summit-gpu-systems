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
    /// One workload cell classified through forced-backend replay. Problem
    /// size and concentration always match exactly. A single-bin cell may
    /// either bind one measured key or accept any caller-validated key.
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

        public bool IsValid
        {
            get
            {
                if (ElementCount <= 0 || BinCount <= 0)
                {
                    return false;
                }

                if (Concentration ==
                    GpuAdaptiveBinningWorkloadConcentration
                        .SingleBinGuaranteed)
                {
                    return HasExactSingleBinKey
                        ? ExactSingleBinKey < (uint)BinCount
                        : ExactSingleBinKey == 0u;
                }

                return (Concentration ==
                            GpuAdaptiveBinningWorkloadConcentration.Hotset ||
                        Concentration ==
                            GpuAdaptiveBinningWorkloadConcentration.General) &&
                    !HasExactSingleBinKey &&
                    ExactSingleBinKey == 0u;
            }
        }

        public bool Matches(
            in GpuAdaptiveBinningWorkloadHint workloadHint)
        {
            if (!IsValid ||
                !workloadHint.IsValid ||
                ElementCount != workloadHint.ElementCount ||
                BinCount != workloadHint.BinCount ||
                Concentration != workloadHint.Concentration)
            {
                return false;
            }

            if (Concentration !=
                GpuAdaptiveBinningWorkloadConcentration
                    .SingleBinGuaranteed)
            {
                return true;
            }

            return workloadHint.HasExactSingleBinKey &&
                (!HasExactSingleBinKey ||
                 ExactSingleBinKey == workloadHint.ExactSingleBinKey);
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

        internal bool Overlaps(
            GpuAdaptiveBinningCalibrationCell other)
        {
            if (!IsValid ||
                !other.IsValid ||
                ElementCount != other.ElementCount ||
                BinCount != other.BinCount ||
                Concentration != other.Concentration)
            {
                return false;
            }

            if (Concentration !=
                GpuAdaptiveBinningWorkloadConcentration
                    .SingleBinGuaranteed)
            {
                return true;
            }

            return !HasExactSingleBinKey ||
                !other.HasExactSingleBinKey ||
                ExactSingleBinKey == other.ExactSingleBinKey;
        }
    }

    /// <summary>
    /// Versioned, caller-owned bounded cell classification derived from
    /// forced-backend replay. There is no built-in profile. Cell storage is
    /// copied once at construction; selection itself is allocation-free.
    /// </summary>
    public readonly struct GpuAdaptiveBinningCalibrationProfile
    {
        public const int CurrentSchemaVersion = 3;

        public const int MaximumRadixCellCount = 8;

        private readonly GpuAdaptiveBinningCalibrationCell[] radixCells;

        private readonly bool radixCellsAreValid;

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
            : this(
                schemaVersion,
                profileId,
                profileRevision,
                deviceBinding,
                requiredPrimitiveBackend,
                requiredProfilerMarkersEnabled,
                CreateLegacyCellArray(
                    radixCellCount,
                    radixCell0,
                    radixCell1))
        {
        }

        public GpuAdaptiveBinningCalibrationProfile(
            int schemaVersion,
            string profileId,
            int profileRevision,
            GpuAdaptiveBinningDeviceBinding deviceBinding,
            GpuPrimitiveBackend requiredPrimitiveBackend,
            bool requiredProfilerMarkersEnabled,
            GpuAdaptiveBinningCalibrationCell[] radixCells)
        {
            SchemaVersion = schemaVersion;
            ProfileId = profileId;
            ProfileRevision = profileRevision;
            DeviceBinding = deviceBinding;
            RequiredPrimitiveBackend = requiredPrimitiveBackend;
            RequiredProfilerMarkersEnabled =
                requiredProfilerMarkersEnabled;
            this.radixCells = radixCells == null
                ? null
                : (GpuAdaptiveBinningCalibrationCell[])radixCells.Clone();
            radixCellsAreValid = HasValidCells(this.radixCells);
        }

        public int SchemaVersion { get; }

        public string ProfileId { get; }

        public int ProfileRevision { get; }

        public GpuAdaptiveBinningDeviceBinding DeviceBinding { get; }

        public GpuPrimitiveBackend RequiredPrimitiveBackend { get; }

        public bool RequiredProfilerMarkersEnabled { get; }

        public int RadixCellCount => radixCells?.Length ?? 0;

        public GpuAdaptiveBinningCalibrationCell RadixCell0 =>
            GetRadixCellOrDefault(0);

        public GpuAdaptiveBinningCalibrationCell RadixCell1 =>
            GetRadixCellOrDefault(1);

        public bool IsValid =>
            SchemaVersion == CurrentSchemaVersion &&
            !string.IsNullOrWhiteSpace(ProfileId) &&
            ProfileRevision > 0 &&
            DeviceBinding.IsValid &&
            RequiredPrimitiveBackend ==
                GpuPrimitiveBackend.WaveOps &&
            !RequiredProfilerMarkersEnabled &&
            radixCellsAreValid;

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

            for (int index = 0; index < radixCells.Length; index++)
            {
                if (radixCells[index].Matches(in workloadHint))
                {
                    return true;
                }
            }

            return false;
        }

        public GpuAdaptiveBinningCalibrationCell GetRadixCellOrDefault(
            int index)
        {
            return radixCells != null &&
                index >= 0 &&
                index < radixCells.Length
                    ? radixCells[index]
                    : default;
        }

        private static GpuAdaptiveBinningCalibrationCell[]
            CreateLegacyCellArray(
                int radixCellCount,
                GpuAdaptiveBinningCalibrationCell radixCell0,
                GpuAdaptiveBinningCalibrationCell radixCell1)
        {
            if (radixCellCount == 1 && radixCell1.IsEmpty)
            {
                return new[] { radixCell0 };
            }

            if (radixCellCount == 2)
            {
                return new[] { radixCell0, radixCell1 };
            }

            return null;
        }

        private static bool HasValidCells(
            GpuAdaptiveBinningCalibrationCell[] cells)
        {
            if (cells == null ||
                cells.Length < 1 ||
                cells.Length > MaximumRadixCellCount)
            {
                return false;
            }

            for (int first = 0; first < cells.Length; first++)
            {
                if (!cells[first].IsValid)
                {
                    return false;
                }

                for (int second = first + 1;
                    second < cells.Length;
                    second++)
                {
                    if (cells[first].Overlaps(cells[second]))
                    {
                        return false;
                    }
                }
            }

            return true;
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
    /// Pure, allocation-free, fail-closed backend selection.
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
