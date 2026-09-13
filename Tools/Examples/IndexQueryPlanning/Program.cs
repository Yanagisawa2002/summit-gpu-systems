using System;
using System.Text.Json;
using Summit.GpuSensorPipeline;

// API demonstration only: these structural facts are invented, not benchmark data.
// The program links the real pure planner. It opens no Unity or GPU backend.
var snapshot = new GpuSensorIndexQueryWorkload
{
    // An all-dynamic index inspects every capacity slot, not only changed/live slots.
    Capacity = 65536, ActiveCount = 32768, InspectedSlotCount = 65536,
    MembershipChanges = 64, ReservedExtent = 49152, IndexEntryCapacity = 196608,
    QueryCount = 16, ConsumerPasses = 4,
    CandidateReservedVisits = 4096, CandidateLiveVisits = 2048,
    CandidateSpanCount = 16, HasExactQueryStructure = true,
    IncrementalStateValid = true
};

var conservative = Emit("default-no-opt-in", snapshot);
RequireUnavailable(conservative, GpuSensorIndexPlanReason.CandidatesDisabled);

Emit("known-structure-opt-in", snapshot, true);
var unknownFacts = snapshot;
unknownFacts.HasExactQueryStructure = false;
unknownFacts.CandidateReservedVisits = unknownFacts.CandidateLiveVisits = unknownFacts.CandidateSpanCount = -1;
var unknown = Emit("unknown-query-structure", unknownFacts, true);
RequireUnavailable(unknown, GpuSensorIndexPlanReason.QueryStructureUnknown);

var recoveryFacts = snapshot;
recoveryFacts.CapacityFallbackKnown = true;
var recovery = Emit("known-capacity-failure", recoveryFacts, true);
if (recovery.IndexMode != GpuSensorIndexPlanMode.FullRebuild ||
    recovery.Reason != GpuSensorIndexPlanReason.CapacityFallbackKnown)
    throw new InvalidOperationException("Known capacity failure must bypass incremental maintenance.");

static GpuSensorIndexQueryPlan Emit(string name, GpuSensorIndexQueryWorkload facts, bool optIn = false)
{
    const bool illustrativeWaveSupport = true; // An assumption, never a hardware probe.
    const long extraStorageBudget = 8L * 1024 * 1024;
    var plan = optIn ? GpuSensorIndexQueryPlanner.Select(facts,
        allowUnmeasuredCandidates: true, supportsWaveOperations: illustrativeWaveSupport,
        allowIncremental: true, allowCompactView: true,
        additionalMemoryBudgetBytes: extraStorageBudget) : GpuSensorIndexQueryPlanner.Select(facts);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        example = name, inputOrigin = "synthetic API illustration",
        execution = "CPU plan construction only", evidence = plan.EvidenceStatus,
        options = new
        {
            allowUnmeasuredCandidates = optIn,
            assumedWaveSupport = optIn && illustrativeWaveSupport,
            additionalMemoryBudgetBytes = optIn ? extraStorageBudget : 0
        },
        mode = plan.IndexMode.ToString(), query = plan.QueryBackend.ToString(),
        reason = plan.Reason.ToString(),
        maintenanceWorkUnits = Available(plan.MaintenanceWorkUnits),
        conversionWorkUnits = Available(plan.ConversionWorkUnits),
        queryWorkUnits = Available(plan.QueryWorkUnits),
        totalWorkUnits = Available(plan.TotalWorkUnits),
        additionalResidentBytes = plan.AdditionalResidentBytes,
        unitMeaning = "work fields: structural surrogate; storage field: additional logical bytes",
        measurement = "none; work is not milliseconds or measured traffic"
    }));
    return plan;
}

static long? Available(long value) => value < 0 ? null : value;

static void RequireUnavailable(GpuSensorIndexQueryPlan plan, GpuSensorIndexPlanReason reason)
{
    if (plan.Reason != reason || plan.IndexMode != GpuSensorIndexPlanMode.FullRebuild ||
        plan.QueryBackend != GpuSensorQueryBackend.CellSerial ||
        Available(plan.MaintenanceWorkUnits) != null || Available(plan.QueryWorkUnits) != null ||
        Available(plan.TotalWorkUnits) != null || plan.ConversionWorkUnits != 0 || plan.AdditionalResidentBytes != 0)
        throw new InvalidOperationException("Unknown work must remain null; no extra view does not mean a free rebuild.");
}
