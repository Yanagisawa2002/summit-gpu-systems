using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Summit.GpuResidencyManager;

static class Program
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static void Reject(Action action) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } Check(rejected, "Expected rejection"); }
    static void Verify(GpuPageResidencyPlanner planner, GpuResidencyFramePlan plan, int[] oracle)
    {
        var targets = new bool[oracle.Length];
        for (int i = 0; i < plan.DeltaCount; i++)
        {
            var d = plan.Deltas[i]; Check(!targets[d.VirtualPage], "Racing duplicate page-table delta"); targets[d.VirtualPage] = true;
            oracle[d.VirtualPage] = unchecked((int)d.PhysicalSlot);
        }
        var slots = new bool[planner.PhysicalSlotCount];
        for (int p = 0; p < oracle.Length; p++)
        {
            Check(oracle[p] == planner.PhysicalSlotForVirtualPage(p), "CPU delta oracle mismatch");
            if (oracle[p] >= 0) { Check(!slots[oracle[p]], "Two pages share one slot"); slots[oracle[p]] = true; }
        }
        for (int i = 0; i < plan.RequestedCount; i++)
            if (plan.RequestedPhysicalSlots[i] >= 0) Check(oracle[plan.RequestedPages[i]] == plan.RequestedPhysicalSlots[i], "Unavailable page claimed resident");
        for (int i = 0; i < plan.UploadCount; i++) Check(oracle[plan.Uploads[i].VirtualPage] == plan.Uploads[i].PhysicalSlot, "Upload mapping mismatch");
    }
    static void Correctness()
    {
        foreach (GpuResidencyPolicy policy in Enum.GetValues<GpuResidencyPolicy>())
        {
            var p = new GpuPageResidencyPlanner(24, 4, 12, policy);
            Reject(() => p.PlanFrame(new[] { 1, 2, 1 }, 0)); Reject(() => p.PlanFrame(new[] { 1, 99 }, 0));
            var a = p.PlanFrame(new[] { 1, 2 }, 0); Check(a.UploadCount == 2, "Invalid input corrupted retry"); a.OwnerComplete();
            Reject(() => p.PlanFrame(Array.Empty<int>(), 0));
            var empty = p.PlanFrame(Array.Empty<int>(), 1); Check(empty.RequestedCount == 0, "Empty demand"); empty.OwnerComplete();
            Reject(() => p.PlanFrame(new[] { new GpuPageRequest(1) }, long.MaxValue, 1));
            p.Reset(); var oracle = Enumerable.Repeat(-1, 24).ToArray(); var served = new bool[12];
            var interest = Enumerable.Range(0, 12).Select(x => new GpuPageRequest(x)).ToArray();
            for (int frame = 0; frame < 50; frame++)
            {
                a = p.PlanFrame(interest, frame, 2); Verify(p, a, oracle); Check(a.UploadCount <= 2, "Budget exceeded");
                for (int i = 0; i < a.RequestedCount; i++) if (a.RequestedPhysicalSlots[i] >= 0) served[a.RequestedPages[i]] = true;
                a.OwnerComplete();
            }
            Check(served.All(x => x), "Overcapacity starvation");
            for (int frame = 50; frame < 80; frame++)
            {
                var teleport = Enumerable.Range(12, 12).Select(x => new GpuPageRequest(x, x % 3, x % 4 == 0)).ToArray();
                a = p.PlanFrame(teleport, frame, frame % 4); Verify(p, a, oracle); a.OwnerComplete();
            }
        }
        var planner = new GpuPageResidencyPlanner(16, 2, 4, GpuResidencyPolicy.PersistentHeapLru, 2);
        var first = planner.PlanFrame(new[] { 0, 1 }, 0);
        var original = first.Uploads[0]; var second = planner.PlanFrame(new[] { 2, 3 }, 1);
        Check(second.UploadCount == 0 && second.DeferredDemandCount == 2, "In-flight consumers overwritten");
        Check(first.RequestedPages[0] == 0 && first.Uploads[0].VirtualPage == original.VirtualPage, "Leased arrays corrupted");
        Reject(() => planner.PlanFrame(new[] { 4 }, 2)); Reject(() => planner.Reset());
        first.OwnerComplete(); second.OwnerComplete(); var retry = planner.PlanFrame(new[] { 2, 3 }, 2); Check(retry.UploadCount == 2, "Lease backpressure retry"); retry.OwnerComplete();
        planner.Reset();
        var pref = planner.PlanFrame(new[] { new GpuPageRequest(0, prefetch: true) }, 0L, 1); Check(pref.RequestedCount == 0 && pref.PrefetchUploadCount == 1, "Prefetch absent"); pref.OwnerComplete();
        var demand = planner.PlanFrame(new[] { 0 }, 1); Check(demand.HitCount == 1, "Prefetch not reused"); demand.OwnerComplete();
        var priorities = new[] { new GpuPageRequest(1), new GpuPageRequest(2, 255), new GpuPageRequest(3, 255) };
        bool lowServed = false;
        for (int frame = 2; frame < 2060; frame++)
        { var f = planner.PlanFrame(priorities, frame, 1); for (int i = 0; i < f.RequestedCount; i++) if (f.RequestedPages[i] == 1 && f.RequestedPhysicalSlots[i] >= 0) lowServed = true; f.OwnerComplete(); }
        Check(lowServed, "Priority aging starvation");
        // Differential equivalence across partial budgets, teleports, working sets, and prefetch.
        var scan = new GpuPageResidencyPlanner(2048, 384, 512, GpuResidencyPolicy.PersistentLru);
        var heap = new GpuPageResidencyPlanner(2048, 384, 512, GpuResidencyPolicy.PersistentHeapLru);
        var input = new GpuPageRequest[512]; var state = Enumerable.Repeat(-1, 2048).ToArray();
        for (int frame = 0; frame < 180; frame++)
        {
            for (int i = 0; i < input.Length; i++) input[i] = new GpuPageRequest((i + frame * (frame % 19 == 0 ? 97 : 3)) % 2048, i % 5, i % 11 == 0);
            var a = scan.PlanFrame(input, frame, frame % 67); var b = heap.PlanFrame(input, frame, frame % 67);
            Check(a.UploadCount == b.UploadCount && a.HitCount == b.HitCount && a.DeltaCount == b.DeltaCount, "Policy mismatch");
            for (int i = 0; i < a.UploadCount; i++) Check(a.Uploads[i].Equals(b.Uploads[i]), "Victim tie mismatch");
            Verify(heap, b, state); a.OwnerComplete(); b.OwnerComplete();
        }
        Console.WriteLine("PASS: atomic validation/retry, frame IDs, overflow, empty, duplicate, budgets, pending, fairness, prefetch, teleport, delta oracle, in-flight isolation, scan/heap differential (180 frames).");
    }
    static object Measure(int slots, string trace, GpuResidencyPolicy policy, int frames)
    {
        int count = trace == "overcapacity" ? slots + slots / 2 : Math.Min(slots, 2048);
        int virtualCount = slots * 4;
        var p = new GpuPageResidencyPlanner(virtualCount, slots, count, policy, 1);
        int prefillFrames = 0;
        var prefill = new int[Math.Min(count, slots)];
        for (int offset = 0; offset < slots; offset += prefill.Length)
        {
            for (int i = 0; i < prefill.Length; i++) prefill[i] = (offset + i) % slots;
            p.PlanFrame(prefill, prefillFrames++).OwnerComplete();
        }
        var input = new GpuPageRequest[count]; var times = new double[frames];
        long bytes = 0, hits = 0, misses = 0, uploads = 0, deferred = 0, maxLatency = 0; double latency = 0;
        int budget = trace == "budgeted" ? Math.Max(1, count / 8) : Math.Min(slots, count);
        ulong availabilityHash = 1469598103934665603UL;
        long allocated = 0; int collections = 0;
        for (int frame = 0; frame < frames + 16; frame++)
        {
            int offset = trace == "teleport" ? (frame / 8 + 1) * slots + (frame / 32 * count) % slots : slots + frame * 7;
            for (int i = 0; i < count; i++) input[i] = new GpuPageRequest((offset + i) % virtualCount, 0, trace == "prefetch" && i >= count * 3 / 4);
            long gc = GC.GetAllocatedBytesForCurrentThread(); int gen0 = GC.CollectionCount(0); long start = Stopwatch.GetTimestamp();
            var plan = p.PlanFrame(input, frame + prefillFrames, budget);
            double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (frame < 16) { p.CompleteFrame(plan); continue; }
            uploads += plan.UploadCount; hits += plan.HitCount; misses += plan.MissCount; deferred += plan.DeferredDemandCount;
            bytes += (long)plan.UploadCount * 64 * 16 + (long)plan.UploadCount * 8 + (long)plan.DeltaCount * 8 + (long)plan.RequestedCount * 8;
            for (int i = 0; i < plan.RequestedCount; i++) availabilityHash = unchecked((availabilityHash ^ (uint)(plan.RequestedPhysicalSlots[i] + 1)) * 1099511628211UL);
            latency += plan.TotalServiceLatencyFrames; maxLatency = Math.Max(maxLatency, plan.MaximumServiceLatencyFrames);
            long retireStart = Stopwatch.GetTimestamp(); p.CompleteFrame(plan);
            times[frame - 16] = elapsed + Stopwatch.GetElapsedTime(retireStart).TotalMilliseconds;
            allocated += GC.GetAllocatedBytesForCurrentThread() - gc; collections += GC.CollectionCount(0) - gen0;
        }
        Check(allocated == 0, "Steady-state planner allocated managed memory");
        Array.Sort(times);
        return new { slots, trace, policy = policy.ToString(), frames, budgetPages = budget, averageCpuPlanAndRetireMs = times.Average(), p99CpuPlanAndRetireMs = times[(int)Math.Ceiling(frames * .99) - 1], managedAllocatedBytes = allocated, gen0Collections = collections, logicalUploadBytes = bytes, uploads, hits, misses, deferredDemand = deferred, averageUploadQueueLatencyFrames = uploads == 0 ? 0 : latency / uploads, maximumUploadQueueLatencyFrames = maxLatency, availabilityHash = availabilityHash.ToString("X16"), gpuTimingMeasured = false, sparseResourceClaim = false };
    }
    static int Main(string[] args)
    {
        Correctness();
        if (args.Length == 0) return 0;
        bool smoke = args[0] == "smoke"; int frames = smoke ? 8 : 240;
        var rows = new System.Collections.Generic.List<object>();
        var policies = args.Contains("reverse") ? new[] { GpuResidencyPolicy.PersistentHeapLru, GpuResidencyPolicy.PersistentLru } : new[] { GpuResidencyPolicy.PersistentLru, GpuResidencyPolicy.PersistentHeapLru };
        foreach (int slots in new[] { 384, 4096, 32768 })
            foreach (string trace in smoke ? new[] { "teleport" } : new[] { "coherent", "teleport", "overcapacity", "budgeted", "prefetch" })
                foreach (var policy in policies)
                    rows.Add(Measure(slots, trace, policy, frames));
        for (int i = 0; i < rows.Count; i += 2)
        {
            var a = JsonSerializer.SerializeToElement(rows[i]); var b = JsonSerializer.SerializeToElement(rows[i + 1]);
            foreach (string key in new[] { "availabilityHash", "uploads", "hits", "misses", "logicalUploadBytes" })
                Check(a.GetProperty(key).ToString() == b.GetProperty(key).ToString(), "Comparison output mismatch: " + key);
        }
        string path = args.Length > 1 ? args[1] : "residency-planner.json";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { runtime = Environment.Version.ToString(), os = Environment.OSVersion.ToString(), scope = "CPU planning plus lease retirement; synthetic logical uploads, no GPU or disk timing", smoke, rows }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Wrote " + Path.GetFullPath(path)); return 0;
    }
}
