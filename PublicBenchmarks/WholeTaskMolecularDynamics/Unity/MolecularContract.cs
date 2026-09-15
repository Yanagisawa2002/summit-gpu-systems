using System;
using System.IO;
using Summit.ExternalWorkloads;

namespace Summit.WholeTaskMD
{
    internal sealed class Input
    {
        public SourcePoint[] Points;
        public float[] Velocities;
        public float Radius;
        public bool MembershipOnly;
        public static Input Read(string path)
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (new string(r.ReadChars(8)) != "SUMD0001") throw new InvalidDataException("Input ABI");
            int n = checked((int)r.ReadUInt32());
            if (n < 1 || n > 1000000) throw new InvalidDataException("Input capacity");
            var value = new Input { Radius = r.ReadSingle(), MembershipOnly = r.ReadUInt32() != 0,
                Points = new SourcePoint[n], Velocities = new float[n * 3] };
            for (int i = 0; i < n; i++)
            {
                value.Points[i] = new SourcePoint(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadUInt32());
                if (value.Points[i].Id != i) throw new InvalidDataException("Original IDs changed");
            }
            for (int i = 0; i < n * 3; i++) value.Velocities[i] = r.ReadSingle();
            if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Trailing input");
            return value;
        }
        public SphereWorkloadDomain Domain()
        {
            double lo = 0, hi = 0;
            foreach (var p in Points) { lo = Math.Min(lo, Math.Min(p.X, Math.Min(p.Y, p.Z))); hi = Math.Max(hi, Math.Max(p.X, Math.Max(p.Y, p.Z))); }
            return new SphereWorkloadDomain(lo, lo, lo, Math.Max(1, hi - lo));
        }
    }
    internal sealed class Csr
    {
        public uint[] Offsets, Ids;
        public static Csr Read(string path)
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (new string(r.ReadChars(8)) != "SUMDCSR1") throw new InvalidDataException("CSR ABI");
            int n = checked((int)r.ReadUInt32()), m = checked((int)r.ReadUInt32());
            var c = new Csr { Offsets = new uint[n + 1], Ids = new uint[m] };
            for (int i = 0; i <= n; i++) c.Offsets[i] = r.ReadUInt32();
            for (int i = 0; i < m; i++) c.Ids[i] = r.ReadUInt32();
            if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Trailing CSR");
            c.Validate(n); return c;
        }
        public void Write(string path)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write(System.Text.Encoding.ASCII.GetBytes("SUMDCSR1")); w.Write((uint)Offsets.Length - 1); w.Write((uint)Ids.Length);
            foreach (uint x in Offsets) w.Write(x); foreach (uint x in Ids) w.Write(x);
        }
        public void Validate(int n)
        {
            if (Offsets.Length != n + 1 || Offsets[0] != 0 || Offsets[n] != Ids.Length) throw new InvalidDataException("CSR shape");
            for (int i = 0; i < n; i++) if (Offsets[i + 1] < Offsets[i]) throw new InvalidDataException("CSR offsets");
            foreach (uint id in Ids) if (id >= n) throw new InvalidDataException("Unknown member");
        }
        public void Equal(Csr other)
        {
            Validate(Offsets.Length - 1); other.Validate(Offsets.Length - 1);
            if (Ids.Length != other.Ids.Length) throw new InvalidDataException("CSR ID length");
            for (int q = 0; q < Offsets.Length - 1; q++)
            {
                if (Offsets[q] != other.Offsets[q] || Offsets[q + 1] != other.Offsets[q + 1]) throw new InvalidDataException("CSR row length: " + q);
                var a = new uint[Offsets[q + 1] - Offsets[q]]; var b = new uint[a.Length];
                Array.Copy(Ids, Offsets[q], a, 0, a.Length); Array.Copy(other.Ids, other.Offsets[q], b, 0, b.Length);
                Array.Sort(a); Array.Sort(b);
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) throw new InvalidDataException("CSR membership/multiplicity: " + q);
            }
        }
    }
    internal sealed class State
    {
        public float[] Positions, Velocities, Forces;
        public static State Consume(Input input, Csr csr)
        {
            // Scalar float port of the exact upstream force/update dependency slice.
            int n = input.Points.Length;
            var s = new State { Positions = new float[n * 3], Velocities = (float[])input.Velocities.Clone(), Forces = new float[n * 3] };
            for (int i = 0; i < n; i++)
            {
                var p = input.Points[i]; float fx = 0, fy = 0, fz = 0;
                for (uint at = csr.Offsets[i]; at < csr.Offsets[i + 1]; at++)
                {
                    var j = input.Points[csr.Ids[at]];
                    float dx = p.X - j.X, dy = p.Y - j.Y, dz = p.Z - j.Z;
                    float xx = dx * dx, yy = dy * dy, zz = dz * dz;
                    float rsq = xx; rsq += yy; rsq += zz;
                    if (rsq < float.PositiveInfinity)
                    {
                        float r2inv = 1f / rsq; float r6inv = r2inv * r2inv; r6inv *= r2inv;
                        float fij = (r6inv * (r6inv - 1f)) * r2inv;
                        fx += dx * fij; fy += dy * fij; fz += dz * fij;
                    }
                }
                s.Forces[i * 3] = fx; s.Forces[i * 3 + 1] = fy; s.Forces[i * 3 + 2] = fz;
            }
            for (int i = 0; i < n; i++) for (int d = 0; d < 3; d++)
            {
                int at = i * 3 + d; s.Velocities[at] += .005f * s.Forces[at];
                var p = input.Points[i]; float x = d == 0 ? p.X : d == 1 ? p.Y : p.Z;
                s.Positions[at] = x + .005f * s.Velocities[at];
            }
            return s;
        }
        public static State Read(string path)
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (new string(r.ReadChars(8)) != "SUMDSTA1") throw new InvalidDataException("State ABI");
            int n = checked((int)r.ReadUInt32());
            var s = new State { Positions = new float[n * 3], Velocities = new float[n * 3], Forces = new float[n * 3] };
            foreach (var v in new[] { s.Positions, s.Velocities, s.Forces }) for (int i = 0; i < v.Length; i++) v[i] = r.ReadSingle();
            if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Trailing state"); return s;
        }
        public double[] Equal(State other)
        {
            var maxima = new double[3]; var all = new[] { Positions, Velocities, Forces }; var expected = new[] { other.Positions, other.Velocities, other.Forces };
            for (int f = 0; f < 3; f++)
            {
                if (all[f].Length != expected[f].Length) throw new InvalidDataException("State shape");
                double absTol = f == 2 ? .002 : .00002, relTol = f == 0 ? .000002 : .00002;
                for (int i = 0; i < all[f].Length; i++)
                {
                    double a = all[f][i], b = expected[f][i], err = Math.Abs(a - b); maxima[f] = Math.Max(maxima[f], err);
                    if (double.IsNaN(a) || double.IsInfinity(a) || double.IsNaN(b) || double.IsInfinity(b) || err > absTol + relTol * Math.Abs(b))
                        throw new InvalidDataException("State precision: field " + f + ", element " + i + ", actual " + a + ", reference " + b);
                }
            }
            return maxima;
        }
        public void Write(string path)
        {
            using var w = new BinaryWriter(File.Create(path));
            w.Write(System.Text.Encoding.ASCII.GetBytes("SUMDSTA1")); w.Write((uint)Positions.Length / 3);
            foreach (var v in new[] { Positions, Velocities, Forces }) foreach (float x in v) w.Write(x);
        }
    }
}
