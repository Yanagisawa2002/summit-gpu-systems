using System;
using System.Collections.Generic;

namespace Summit.ExternalWorkloads
{
    public readonly struct LinkedCellPoint
    {
        public readonly double X, Y, Z;
        public readonly uint Id;
        public LinkedCellPoint(double x, double y, double z, uint id) { X=x; Y=y; Z=z; Id=id; }
    }
    /// <summary>Cabana dd6bd7cc CartesianGrid<double> grouping semantics, ported to
    /// C# for direct-binning input. Retains adjusted cell width and z-fast numbering.
    /// Particle generation, neighbor iteration, and permute are NOT replaced.</summary>
    public sealed class LinkedCellWorkloadContract
    {
        readonly double[] min, max, reciprocal;
        readonly int[] n;
        public int BinCount { get; }
        public LinkedCellWorkloadContract(double[] gridMin, double[] gridMax, double[] requestedCellWidth)
        {
            if (gridMin == null || gridMax == null || requestedCellWidth == null || gridMin.Length != 3 || gridMax.Length != 3 || requestedCellWidth.Length != 3)
                throw new ArgumentException("Three-dimensional grid required.");
            min=(double[])gridMin.Clone(); max=(double[])gridMax.Clone(); reciprocal=new double[3]; n=new int[3];
            long bins=1;
            for(int d=0;d<3;d++)
            {
                double extent=max[d]-min[d], width=requestedCellWidth[d];
                if (!Finite(min[d]) || !Finite(max[d]) || !Finite(extent) || !Finite(width) || extent<=0 || width<=0)
                    throw new ArgumentException("Finite nondegenerate grid required.");
                double cells=Math.Floor(extent*(1.0/width));
                if(cells<1 || cells>16776960) throw new ArgumentException("Unsupported grid cell count.");
                n[d]=(int)cells; reciprocal[d]=1.0/(extent/n[d]);
                if (!Finite(reciprocal[d])) throw new ArgumentException("Cell width underflow.");
                bins=checked(bins*n[d]);
                if(bins>16776960) throw new ArgumentException("Grid exceeds SUMMIT bin capacity.");
            }
            BinCount=(int)bins;
        }
        static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
        int Axis(double value,int d)
        {
            if(!Finite(value) || value<min[d] || value>max[d]) throw new ArgumentException("Point outside declared Cabana grid; no clamp.");
            int cell=(int)Math.Floor((value-min[d])*reciprocal[d]);
            if(cell==n[d]) cell--;
            if(cell<0 || cell>=n[d]) throw new ArgumentException("Point cell is not representable.");
            return cell;
        }
        public uint Key(LinkedCellPoint p) => (uint)((Axis(p.X,0)*n[1]+Axis(p.Y,1))*n[2]+Axis(p.Z,2));
        public uint[] Encode(IReadOnlyList<LinkedCellPoint> points)
        {
            if(points==null) throw new ArgumentNullException(nameof(points));
            var ids=new HashSet<uint>(); var keys=new uint[points.Count];
            for(int i=0;i<points.Count;i++)
            { if(!ids.Add(points[i].Id)) throw new ArgumentException("Duplicate source particle ID."); keys[i]=Key(points[i]); }
            return keys;
        }
        public uint[][] Membership(IReadOnlyList<LinkedCellPoint> points)
        {
            var keys=Encode(points); var bins=new List<uint>[BinCount];
            for(int i=0;i<BinCount;i++) bins[i]=new List<uint>();
            for(int i=0;i<points.Count;i++) bins[keys[i]].Add(points[i].Id);
            var output=new uint[BinCount][];
            for(int i=0;i<BinCount;i++) output[i]=bins[i].ToArray();
            return output;
        }
    }
}
