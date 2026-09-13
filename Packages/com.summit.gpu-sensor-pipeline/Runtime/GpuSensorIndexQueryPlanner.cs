using System;

namespace Summit.GpuSensorPipeline
{
    public enum GpuSensorIndexPlanMode { FullRebuild = 0, Incremental = 1, IncrementalCompactView = 2 }
    public enum GpuSensorIndexPlanReason
    {
        CandidatesDisabled, QueryStructureUnknown, FullRebuildRequired,
        CapacityFallbackKnown, FullRebuildPreferred, IncrementalWorkReduction, CompactViewWorkReduction
    }

    /// <summary>
    /// Caller-owned structural facts for ONE snapshot and ONE query segment.
    /// No measurements or GPU readbacks are performed by the planner. Exact query
    /// visits count CSR candidate entries before point filtering, not result hits.
    /// Refresh facts when inputs change; unknown facts must not be guessed.
    /// </summary>
    public struct GpuSensorIndexQueryWorkload
    {
        public int Capacity, ActiveCount, InspectedSlotCount, MembershipChanges;
        public int ReservedExtent, IndexEntryCapacity, QueryCount, ConsumerPasses;
        public long CandidateReservedVisits, CandidateLiveVisits, CandidateSpanCount;
        public bool HasExactQueryStructure, IncrementalStateValid, CapacityFallbackKnown, ForceFullRebuild;
    }

    public readonly struct GpuSensorIndexQueryPlan
    {
        public readonly GpuSensorIndexPlanMode IndexMode;
        public readonly GpuSensorQueryBackend QueryBackend;
        public readonly GpuSensorIndexPlanReason Reason;
        // -1 means unavailable, never a zero-cost assertion.
        public readonly long MaintenanceWorkUnits, ConversionWorkUnits, QueryWorkUnits;
        public readonly long AdditionalResidentBytes;
        public string EvidenceStatus => "Unmeasured";
        public long TotalWorkUnits => MaintenanceWorkUnits < 0 || QueryWorkUnits < 0 ? -1 :
            checked(MaintenanceWorkUnits + ConversionWorkUnits + QueryWorkUnits);
        internal GpuSensorIndexQueryPlan(GpuSensorIndexPlanMode mode, GpuSensorQueryBackend backend,
            GpuSensorIndexPlanReason reason, long maintenance, long conversion, long query, long extraBytes)
        {
            IndexMode = mode; QueryBackend = backend; Reason = reason;
            MaintenanceWorkUnits = maintenance; ConversionWorkUnits = conversion;
            QueryWorkUnits = query; AdditionalResidentBytes = extraBytes;
        }
    }

    /// <summary>
    /// Opt-in planning only: never changes an existing pipeline, records commands,
    /// probes hardware, times work or selects a measured winner. The integer score
    /// is a documented structural surrogate, NOT predicted time or memory traffic.
    /// Full compact rebuild is the recovery path for a known capacity fallback.
    /// </summary>
    public static class GpuSensorIndexQueryPlanner
    {
        private const long Bins = 262144;
        public static GpuSensorIndexQueryPlan Select(GpuSensorIndexQueryWorkload workload,
            bool allowUnmeasuredCandidates = false, bool supportsWaveOperations = false,
            bool allowIncremental = false, bool allowCompactView = false,
            long additionalMemoryBudgetBytes = 0, int minimumWorkReductionPermille = 250)
        {
            Validate(workload);
            if (additionalMemoryBudgetBytes < 0) throw new ArgumentOutOfRangeException(nameof(additionalMemoryBudgetBytes));
            if (minimumWorkReductionPermille < 0 || minimumWorkReductionPermille > 1000)
                throw new ArgumentOutOfRangeException(nameof(minimumWorkReductionPermille));
            if (!allowUnmeasuredCandidates || !workload.HasExactQueryStructure)
                return new GpuSensorIndexQueryPlan(GpuSensorIndexPlanMode.FullRebuild, GpuSensorQueryBackend.CellSerial,
                    !allowUnmeasuredCandidates ? GpuSensorIndexPlanReason.CandidatesDisabled : GpuSensorIndexPlanReason.QueryStructureUnknown,
                    -1, 0, -1, 0);

            // Surrogate units: inspected slot fields, changed membership records,
            // grid scan/preparation, query visits and prefix-directory operations.
            // They deliberately expose maintenance/conversion instead of treating
            // a compact snapshot or a full fallback reconstruction as free.
            long fullMaintenance = 10L * workload.Capacity + 8 * Bins + 4L * workload.ActiveCount;
            long incrementalMaintenance = 3L * workload.Capacity + 9L * workload.InspectedSlotCount + 8L * workload.MembershipChanges;
            long conversion = workload.Capacity + 6 * Bins + 3L * workload.ActiveCount + 3 * (Bins / 256) + 1 +
                4L * workload.MembershipChanges; // live-count upkeep charged as well
            long viewBytes = 4L * (workload.Capacity + 2 * Bins + 1 + Bins / 256);
            var full = Candidate(workload, GpuSensorIndexPlanMode.FullRebuild, fullMaintenance, 0,
                0, workload.ActiveCount, workload.Capacity, workload.CandidateLiveVisits,
                supportsWaveOperations, additionalMemoryBudgetBytes, GpuSensorIndexPlanReason.FullRebuildPreferred);
            if (workload.ForceFullRebuild || !workload.IncrementalStateValid || !allowIncremental ||
                workload.Capacity > GpuSensorCellSpanLayout.MaxEntryCapacity / 3)
                return WithReason(full, GpuSensorIndexPlanReason.FullRebuildRequired);
            if (workload.CapacityFallbackKnown)
                return WithReason(full, GpuSensorIndexPlanReason.CapacityFallbackKnown);

            var incremental = Candidate(workload, GpuSensorIndexPlanMode.Incremental, incrementalMaintenance, 0,
                0, workload.ReservedExtent, workload.IndexEntryCapacity, workload.CandidateReservedVisits,
                supportsWaveOperations, additionalMemoryBudgetBytes, GpuSensorIndexPlanReason.IncrementalWorkReduction);
            var best = incremental;
            if (allowCompactView && viewBytes <= additionalMemoryBudgetBytes)
            {
                var compact = Candidate(workload, GpuSensorIndexPlanMode.IncrementalCompactView, incrementalMaintenance, conversion,
                    viewBytes, workload.ActiveCount, workload.Capacity, workload.CandidateLiveVisits,
                    supportsWaveOperations, additionalMemoryBudgetBytes, GpuSensorIndexPlanReason.CompactViewWorkReduction);
                if (compact.TotalWorkUnits < best.TotalWorkUnits) best = compact;
            }
            // Apply the safety margin against the full maintenance+consumer plan.
            // Decimal avoids overflow for the largest legal query/pass counts.
            return (decimal)best.TotalWorkUnits * 1000 <
                (decimal)full.TotalWorkUnits * (1000 - minimumWorkReductionPermille) ? best : full;
        }

        private static GpuSensorIndexQueryPlan Candidate(GpuSensorIndexQueryWorkload w,
            GpuSensorIndexPlanMode mode, long maintenance, long conversion, long viewBytes,
            int extent, int entryCapacity, long visits, bool wave, long budget, GpuSensorIndexPlanReason reason)
        {
            // Upper bound for the sum of row chunks across a segment. Directory
            // setup is charged even for empty results and padded span descriptors.
            long chunks = visits / 256 + Math.Min(visits, w.CandidateSpanCount);
            long spans = w.QueryCount * 4096L + 2 * w.CandidateSpanCount + 2 * visits + 12 * chunks;
            // BatchedPointScan loops over the configured padded dispatch extent,
            // including lanes rejected by the CSR terminal offset.
            long batch = extent + (long)w.ActiveCount + ((entryCapacity + 255L) / 256 * 256) * w.QueryCount;
            bool canSpans = GpuSensorCellSpanLayout.ScratchBytes <= budget - viewBytes;
            GpuSensorQueryBackend backend;
            long query, extra = viewBytes;
            if (wave && (!canSpans || batch <= spans))
            {
                backend = GpuSensorQueryBackend.BatchedPointScanWave; query = batch;
            }
            else if (canSpans)
            {
                backend = wave ? GpuSensorQueryBackend.CellSpansWave : GpuSensorQueryBackend.CellSpans;
                query = spans; extra += GpuSensorCellSpanLayout.ScratchBytes;
            }
            else
            {
                // No hidden scratch allocation, no unsupported wave substitution.
                // Cell count is unavailable, so conservatively charge the whole grid.
                backend = GpuSensorQueryBackend.CellSerial; query = 2 * visits + 2 * Bins * w.QueryCount;
            }
            return new GpuSensorIndexQueryPlan(mode, backend, reason, maintenance, conversion,
                checked(query * w.ConsumerPasses), extra);
        }

        private static GpuSensorIndexQueryPlan WithReason(GpuSensorIndexQueryPlan p, GpuSensorIndexPlanReason reason) =>
            new GpuSensorIndexQueryPlan(p.IndexMode, p.QueryBackend, reason,
                p.MaintenanceWorkUnits, p.ConversionWorkUnits, p.QueryWorkUnits, p.AdditionalResidentBytes);

        private static void Validate(GpuSensorIndexQueryWorkload w)
        {
            GpuSensorCellSpanLayout.ValidateCapacity(w.Capacity);
            if (w.ActiveCount < 0 || w.ActiveCount > w.Capacity || w.InspectedSlotCount < 0 || w.InspectedSlotCount > w.Capacity ||
                w.MembershipChanges < 0 || w.MembershipChanges > w.InspectedSlotCount || w.ReservedExtent < w.ActiveCount ||
                w.IndexEntryCapacity < 1 || w.IndexEntryCapacity < w.ReservedExtent || w.IndexEntryCapacity > GpuSensorCellSpanLayout.MaxEntryCapacity ||
                w.QueryCount < 1 || w.QueryCount > 65535 || w.ConsumerPasses < 1 || w.ConsumerPasses > 65535)
                throw new ArgumentException("Invalid snapshot, membership, or query capacities.", nameof(w));
            if (!w.HasExactQueryStructure) return;
            if (w.CandidateLiveVisits < 0 || w.CandidateLiveVisits > (long)w.ActiveCount * w.QueryCount ||
                w.CandidateReservedVisits < w.CandidateLiveVisits || w.CandidateReservedVisits > (long)w.ReservedExtent * w.QueryCount ||
                w.CandidateReservedVisits - w.CandidateLiveVisits > (long)(w.ReservedExtent - w.ActiveCount) * w.QueryCount ||
                w.CandidateSpanCount < w.QueryCount || w.CandidateSpanCount > 4096L * w.QueryCount)
                throw new ArgumentException("Exact candidate visits/spans are inconsistent with the snapshot.", nameof(w));
        }
    }
}
