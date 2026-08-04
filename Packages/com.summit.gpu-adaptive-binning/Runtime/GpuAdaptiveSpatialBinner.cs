using System;
using Summit.GpuDirectBinning;
using Summit.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuAdaptiveBinning
{
    /// <summary>
    /// Backend-neutral facade for forced direct/radix spatial binning and
    /// explicitly calibrated, fail-closed adaptive selection.
    /// </summary>
    /// <remarks>
    /// There is no universal automatic policy. Adaptive calls require a
    /// caller-owned calibration profile, workload hint, and device identity.
    /// </remarks>
    public sealed class GpuAdaptiveSpatialBinner : IDisposable
    {
        private readonly GpuDirectSpatialBinner direct;
        private readonly GpuRadixSpatialBinner radix;
        private bool disposed;

        public GpuAdaptiveSpatialBinner(
            int elementCapacity,
            int binCapacity,
            bool emitProfilerMarkers = true)
        {
            GpuDirectSpatialBinner selectedDirect = null;
            GpuRadixSpatialBinner selectedRadix = null;
            try
            {
                selectedDirect = new GpuDirectSpatialBinner(
                    elementCapacity,
                    binCapacity,
                    emitProfilerMarkers: emitProfilerMarkers);
                selectedRadix = new GpuRadixSpatialBinner(
                    elementCapacity,
                    binCapacity,
                    emitProfilerMarkers: emitProfilerMarkers);
            }
            catch
            {
                selectedRadix?.Dispose();
                selectedDirect?.Dispose();
                throw;
            }

            ElementCapacity = elementCapacity;
            BinCapacity = binCapacity;
            direct = selectedDirect;
            radix = selectedRadix;
        }

        public int ElementCapacity { get; }

        public int BinCapacity { get; }

        public bool EmitsProfilerMarkers => direct.EmitsProfilerMarkers;

        public long DirectScratchBytes => direct.ScratchBytes;

        public long RadixScratchBytes => radix.ScratchBytes;

        /// <summary>
        /// Logical persistent scratch when both forceable backends are resident.
        /// </summary>
        public long UnionScratchBytes =>
            checked(DirectScratchBytes + RadixScratchBytes);

        public void Record(
            CommandBuffer commands,
            GraphicsBuffer keys,
            GraphicsBuffer values,
            GraphicsBuffer binCounts,
            GraphicsBuffer binOffsets,
            GraphicsBuffer binnedValues,
            GraphicsBuffer diagnostics,
            int elementCount,
            int binCount,
            GpuAdaptiveBinningBackend backend,
            GpuAdaptiveBinningKeyDomain keyDomain,
            GpuPrimitiveBackend primitiveBackend =
                GpuPrimitiveBackend.Auto)
        {
            ThrowIfDisposed();
            switch (backend)
            {
                case GpuAdaptiveBinningBackend.Direct:
                    if (keyDomain ==
                        GpuAdaptiveBinningKeyDomain.GuaranteedInRange)
                    {
                        direct.RecordGuaranteedInRange(
                            commands,
                            keys,
                            values,
                            binCounts,
                            binOffsets,
                            binnedValues,
                            diagnostics,
                            elementCount,
                            binCount,
                            primitiveBackend);
                    }
                    else if (keyDomain ==
                        GpuAdaptiveBinningKeyDomain.Untrusted)
                    {
                        direct.Record(
                            commands,
                            keys,
                            values,
                            binCounts,
                            binOffsets,
                            binnedValues,
                            diagnostics,
                            elementCount,
                            binCount,
                            primitiveBackend);
                    }
                    else
                    {
                        throw new ArgumentOutOfRangeException(nameof(keyDomain));
                    }
                    return;
                case GpuAdaptiveBinningBackend.Radix:
                    radix.Record(
                        commands,
                        keys,
                        values,
                        binCounts,
                        binOffsets,
                        binnedValues,
                        diagnostics,
                        elementCount,
                        binCount,
                        keyDomain,
                        primitiveBackend);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(backend));
            }
        }

        /// <summary>
        /// Selects through an explicit calibration profile, records the chosen
        /// implementation, and returns the backend for telemetry.
        /// </summary>
        /// <remarks>
        /// Unknown, invalid, or incompatible evidence falls back to Direct.
        /// The normal path is allocation-free and readback-free. Capture the
        /// device identity outside the frame loop.
        /// </remarks>
        public GpuAdaptiveBinningBackend RecordAdaptive(
            CommandBuffer commands,
            GraphicsBuffer keys,
            GraphicsBuffer values,
            GraphicsBuffer binCounts,
            GraphicsBuffer binOffsets,
            GraphicsBuffer binnedValues,
            GraphicsBuffer diagnostics,
            int elementCount,
            int binCount,
            GpuAdaptiveBinningKeyDomain keyDomain,
            in GpuAdaptiveBinningCalibrationProfile calibrationProfile,
            in GpuAdaptiveBinningWorkloadHint workloadHint,
            in GpuAdaptiveBinningDeviceIdentity deviceIdentity,
            GpuPrimitiveBackend primitiveBackend =
                GpuPrimitiveBackend.Auto)
        {
            ThrowIfDisposed();
            GpuAdaptiveBinningBackend selectedBackend =
                GpuAdaptiveBinningSelector.SelectBackend(
                    in calibrationProfile,
                    in workloadHint,
                    in deviceIdentity,
                    elementCount,
                    binCount,
                    keyDomain,
                    primitiveBackend,
                    EmitsProfilerMarkers);

            Record(
                commands,
                keys,
                values,
                binCounts,
                binOffsets,
                binnedValues,
                diagnostics,
                elementCount,
                binCount,
                selectedBackend,
                keyDomain,
                primitiveBackend);
            return selectedBackend;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            radix.Dispose();
            direct.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(
                    nameof(GpuAdaptiveSpatialBinner));
            }
        }
    }
}
