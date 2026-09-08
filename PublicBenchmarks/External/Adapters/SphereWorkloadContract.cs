using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Summit.GpuSensorPipeline;

namespace Summit.ExternalWorkloads
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct SourcePoint
    {
        public float X, Y, Z;
        public uint Id;
        public SourcePoint(float x, float y, float z, uint id) { X = x; Y = y; Z = z; Id = id; }
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct SourceSphere
    {
        public float X, Y, Z, Radius;
        public SourceSphere(float x, float y, float z, float radius) { X = x; Y = y; Z = z; Radius = radius; }
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct CandidateCells
    {
        public uint MinX, MinY, MinZ, Empty, MaxX, MaxY, MaxZ, Reserved;
        public bool Contains(GpuSensorSample p) => Empty == 0 &&
            (p.X >> 10) >= MinX && (p.X >> 10) <= MaxX && (p.Y >> 10) >= MinY &&
            (p.Y >> 10) <= MaxY && (p.Z >> 10) >= MinZ && (p.Z >> 10) <= MaxZ;
    }

    /// <summary>Explicit finite cube for conservative broad phase only. Exact tests
    /// always use original float coordinates; no clipped, deduplicated, or regenerated input.</summary>
    public sealed class SphereWorkloadDomain
    {
        public readonly double MinX, MinY, MinZ, Extent;
        public SphereWorkloadDomain(double minX, double minY, double minZ, double extent)
        {
            if (!Finite(minX) || !Finite(minY) || !Finite(minZ) || !Finite(extent) || extent <= 0 ||
                Math.Abs(minX) > 1e8 || Math.Abs(minY) > 1e8 || Math.Abs(minZ) > 1e8 ||
                Math.Abs(minX + extent) > 1e8 || Math.Abs(minY + extent) > 1e8 || Math.Abs(minZ + extent) > 1e8)
                throw new ArgumentException("Domain must be a positive finite cube within [-1e8,1e8].");
            MinX = minX; MinY = minY; MinZ = minZ; Extent = extent;
        }
        static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
        internal static void ValidateCoordinate(float n)
        { if (!Finite(n) || Math.Abs(n) > 1e8) throw new ArgumentException("Coordinate outside supported finite float domain."); }
        public GpuSensorSample Encode(SourcePoint p)
        {
            ValidateCoordinate(p.X); ValidateCoordinate(p.Y); ValidateCoordinate(p.Z);
            return new GpuSensorSample(EncodeAxis(p.X, MinX), EncodeAxis(p.Y, MinY), EncodeAxis(p.Z, MinZ), p.Id);
        }
        uint EncodeAxis(double p, double min)
        {
            if (p < min || p > min + Extent) throw new ArgumentException("Point is outside the declared domain; rebuild the domain, do not clamp.");
            return (uint)Math.Min(65535, Math.Floor((p - min) / Extent * 65535));
        }
        public CandidateCells Bounds(SourceSphere s)
        {
            ValidateCoordinate(s.X); ValidateCoordinate(s.Y); ValidateCoordinate(s.Z); ValidateCoordinate(s.Radius);
            if (s.Radius < 0) return new CandidateCells { Empty = 1 };
            // Covers float subtraction/product/sum/sqrt rounding for the bounded domain
            // and squared-distance subnormal underflow (including radius zero). This is
            // only a conservative broad-phase halo, never a change to the exact predicate.
            double r = (double)s.Radius * (1 + 8 * 1.1920928955078125e-7) + 1e-18;
            if (!Axis(s.X, r, MinX, out uint x0, out uint x1) ||
                !Axis(s.Y, r, MinY, out uint y0, out uint y1) ||
                !Axis(s.Z, r, MinZ, out uint z0, out uint z1)) return new CandidateCells { Empty = 1 };
            return new CandidateCells { MinX = x0, MinY = y0, MinZ = z0, MaxX = x1, MaxY = y1, MaxZ = z1 };
        }
        bool Axis(double center, double radius, double min, out uint lo, out uint hi)
        {
            lo = hi = 0;
            if (center + radius < min || center - radius > min + Extent) return false;
            lo = EncodeAxis(Math.Max(min, center - radius), min) >> 10;
            hi = EncodeAxis(Math.Min(min + Extent, center + radius), min) >> 10;
            return true;
        }
    }

    public static class SphereWorkloadContract
    {
        public const string PerformanceStatus = "Unmeasured";
        // ArborX 375875df... src/geometry/algorithms/ArborX_{Intersects,Distance}.hpp:
        // Point<3,float>, Sphere<3,float>, distance=sqrt(sum(tmp*tmp)), distance<=radius.
        // Explicit intermediates mirror scalar float evaluation, not a squared-radius rewrite.
        public static bool Intersects(SourcePoint p, SourceSphere s)
        {
            float x = p.X - s.X, y = p.Y - s.Y, z = p.Z - s.Z;
            float xx = x * x, yy = y * y, zz = z * z;
            float sum = xx; sum += yy; sum += zz;
            return (float)Math.Sqrt(sum) <= s.Radius;
        }
        public static GpuSensorSample[] Encode(SphereWorkloadDomain domain, IReadOnlyList<SourcePoint> points)
        {
            if (domain == null || points == null) throw new ArgumentNullException();
            var ids = new HashSet<uint>(); var encoded = new GpuSensorSample[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                if (!ids.Add(points[i].Id)) throw new ArgumentException("Duplicate point IDs are ambiguous; duplicate coordinates with distinct IDs are preserved.");
                encoded[i] = domain.Encode(points[i]);
            }
            return encoded;
        }
        // Pure CPU functional model. This is not a benchmark generator or measured baseline.
        public static uint[][] Filter(SphereWorkloadDomain domain, IReadOnlyList<SourcePoint> points,
            IReadOnlyList<SourceSphere> spheres)
        {
            var encoded = Encode(domain, points); var rows = new uint[spheres.Count][];
            for (int q = 0; q < spheres.Count; q++)
            {
                var bounds = domain.Bounds(spheres[q]); var matches = new List<uint>();
                for (int i = 0; i < points.Count; i++)
                    if (bounds.Contains(encoded[i]) && Intersects(points[i], spheres[q])) matches.Add(points[i].Id);
                rows[q] = matches.ToArray();
            }
            return rows;
        }
        public static void RequireFullMembership(IReadOnlyList<uint[]> expected, IReadOnlyList<uint[]> actual)
        {
            if (expected.Count != actual.Count) throw new ArgumentException("CSR query count mismatch.");
            for (int q = 0; q < expected.Count; q++)
            {
                // Atomic bin scatter may reorder IDs; multiplicity and every original ID must match.
                var a = (uint[])expected[q].Clone(); var b = (uint[])actual[q].Clone();
                Array.Sort(a); Array.Sort(b);
                if (a.Length != b.Length) throw new ArgumentException("Full CSR membership length mismatch.");
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) throw new ArgumentException("Full CSR membership mismatch.");
            }
        }
    }
}
