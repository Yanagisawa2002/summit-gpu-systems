using System;
using Boids;
using Summit.ExternalWorkloads;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.ExternalBoids
{
    /// <summary>Optional observation consumer of the upstream boids and moving
    /// targets. Does not replace flocking, spawn particles, or edit sample assets.
    /// Full CSR is consumed to resolve original entities; only counts are displayed.</summary>
    public sealed class BoidsSphereConsumer : MonoBehaviour
    {
        public bool AllowUnmeasured;
        public float Radius = 8;
        public int MaximumResultIds = 16 * 1024 * 1024;
        public string Status { get; private set; } = "Disabled / Unmeasured";
        public Entity[][] MatchedEntities { get; private set; } = Array.Empty<Entity[]>();
        public int SourceFrame { get; private set; } = -1;
        readonly ConsumerEpoch epoch = new ConsumerEpoch();
        World sourceWorld;
        EntityQuery boids, targets;
        GpuSphereWorkloadAdapter adapter;
        bool hasQueries, busy, stopped;

        void LateUpdate()
        {
            if (!AllowUnmeasured) { if (!stopped) Invalidate(); return; }
            stopped = false;
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) { Invalidate(); return; }
            if (world != sourceWorld)
            {
                Invalidate(); ReleaseQueries(); sourceWorld = world;
                boids = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<Boid>(), ComponentType.ReadOnly<LocalToWorld>());
                targets = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<BoidTarget>(), ComponentType.ReadOnly<LocalToWorld>());
                hasQueries = true; stopped = false;
            }
            if (busy) { Status = "Pending complete CSR / Unmeasured"; return; }
            try { SubmitSnapshot(); }
            catch (Exception e) { Invalidate(); Status = e.Message + " / Unmeasured"; enabled = false; }
        }
        void SubmitSnapshot()
        {
            // Complete producers through the entity query read dependency. This CPU
            // staging and allocation are part of future complete-consumer cost.
            using var entities = boids.ToEntityArray(Allocator.Temp);
            using var positions = boids.ToComponentDataArray<LocalToWorld>(Allocator.Temp);
            using var targetPositions = targets.ToComponentDataArray<LocalToWorld>(Allocator.Temp);
            var points = new SourcePoint[positions.Length]; var identities = entities.ToArray();
            double lo = 0, hi = 0;
            for (int i = 0; i < points.Length; i++)
            {
                var p = positions[i].Position;
                points[i] = new SourcePoint(p.x, p.y, p.z, (uint)i);
                lo = Math.Min(lo, Math.Min(p.x, Math.Min(p.y, p.z)));
                hi = Math.Max(hi, Math.Max(p.x, Math.Max(p.y, p.z)));
            }
            var spheres = new SourceSphere[targetPositions.Length];
            for (int i = 0; i < spheres.Length; i++)
            { var p = targetPositions[i].Position; spheres[i] = new SourceSphere(p.x, p.y, p.z, Radius); }
            var domain = new SphereWorkloadDomain(lo, lo, lo, Math.Max(1, hi - lo));
            int n = Math.Max(1, points.Length), q = Math.Max(1, spheres.Length);
            if ((long)n*q > MaximumResultIds) throw new InvalidOperationException("Full CSR capacity exceeds explicit budget; no entities discarded");
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
            {
                adapter?.Dispose(); adapter = null;
                MatchedEntities = Resolve(SphereWorkloadContract.Filter(domain, points, spheres), identities);
                SourceFrame = Time.frameCount; Status = "CPU complete-CSR fallback / Unmeasured"; return;
            }
            if (adapter == null || adapter.PointCapacity < n || adapter.QueryCapacity < q)
            {
                epoch.RequireDrained(); adapter?.Dispose(); adapter = null;
                adapter = new GpuSphereWorkloadAdapter(n, q, true);
            }
            adapter.Upload(domain, points, spheres);
            var commands = new CommandBuffer { name = "SUMMIT external boids complete sphere consumer" };
            try
            {
                adapter.Record(commands);
                ulong ticket = 0; int frame = Time.frameCount; var owner = adapter;
                // Both readbacks follow scatter. Keep the owner alive until BOTH complete,
                // including error, disable, destruction, and world replacement paths.
                uint[] offsets = null, ids = null; bool failed = false; int remaining = 2;
                void Finish()
                {
                    if (--remaining != 0) return;
                    bool current = epoch.Complete(ticket); busy = false;
                    try
                    {
                        if (current && !failed && this != null && AllowUnmeasured && isActiveAndEnabled && sourceWorld != null && sourceWorld.IsCreated)
                        {
                            if (offsets == null || ids == null || offsets.Length < spheres.Length + 1 || offsets[0] != 0)
                                throw new InvalidOperationException("Malformed complete CSR header");
                            var rows = new uint[spheres.Length][];
                            for (int j = 0; j < rows.Length; j++)
                            {
                                if (offsets[j+1] < offsets[j] || offsets[j+1] > ids.Length) throw new InvalidOperationException("Malformed complete CSR");
                                rows[j] = new uint[offsets[j+1]-offsets[j]];
                                Array.Copy(ids, offsets[j], rows[j], 0, rows[j].Length);
                            }
                            MatchedEntities = Resolve(rows, identities); SourceFrame = frame; Status = "GPU complete CSR consumed / Unmeasured";
                        }
                        else if (failed && this != null) { Status = "GPU result unavailable / Unmeasured"; Invalidate(); }
                    }
                    catch (Exception e)
                    {
                        failed = true;
                        if (this != null) { Status = e.Message + " / Unmeasured"; Invalidate(); }
                    }
                    finally
                    {
                        if (!current || failed || this == null || !isActiveAndEnabled || !AllowUnmeasured)
                        { owner.Dispose(); if (ReferenceEquals(owner, adapter)) adapter = null; }
                    }
                }
                commands.RequestAsyncReadback(owner.Offsets, request =>
                {
                    try { failed |= request.hasError; if (!request.hasError) offsets = request.GetData<uint>().ToArray(); }
                    catch (Exception) { failed = true; }
                    finally { Finish(); }
                });
                commands.RequestAsyncReadback(owner.Ids, request =>
                {
                    try { failed |= request.hasError; if (!request.hasError) ids = request.GetData<uint>().ToArray(); }
                    catch (Exception) { failed = true; }
                    finally { Finish(); }
                });
                // Recording failure has submitted nothing and must not create a pending
                // ticket. After submission is attempted, retain ownership until callbacks
                // complete even when the graphics API reports an error.
                ticket = epoch.Submit(); busy = true;
                Graphics.ExecuteCommandBuffer(commands);
            }
            finally { commands.Dispose(); }
        }
        static Entity[][] Resolve(uint[][] rows, Entity[] identities)
        {
            var result = new Entity[rows.Length][];
            for (int q = 0; q < rows.Length; q++)
            {
                result[q] = new Entity[rows[q].Length];
                for (int i = 0; i < rows[q].Length; i++)
                {
                    if (rows[q][i] >= identities.Length) throw new InvalidOperationException("Unknown source entity ID");
                    result[q][i] = identities[rows[q][i]];
                }
            }
            return result;
        }
        void Invalidate()
        {
            if (!stopped) { epoch.Invalidate(); stopped = true; }
            MatchedEntities = Array.Empty<Entity[]>(); SourceFrame = -1;
            if (!busy) { epoch.RequireDrained(); adapter?.Dispose(); adapter = null; }
        }
        void ReleaseQueries()
        {
            if (hasQueries && sourceWorld != null && sourceWorld.IsCreated) { boids.Dispose(); targets.Dispose(); }
            hasQueries = false;
        }
        void OnDisable() { Invalidate(); }
        void OnDestroy() { Invalidate(); ReleaseQueries(); }
        void OnGUI()
        {
            if (!AllowUnmeasured) return;
            GUILayout.Label(Status + " | source Unity frame " + SourceFrame);
            for (int q = 0; q < MatchedEntities.Length; q++) GUILayout.Label("Upstream target " + q + ": " + MatchedEntities[q].Length + " source boids");
        }
    }
}
