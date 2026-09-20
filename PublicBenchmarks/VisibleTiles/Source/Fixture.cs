using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Summit.VisibleTiles
{
    [StructLayout(LayoutKind.Sequential)]
    public struct P3
    {
        public float x, y, z;
        public P3(float x, float y, float z) { this.x=x; this.y=y; this.z=z; }
    }

    // No Unity dependency: the exact fixture/oracle is also compiled by CPU CI.
    public sealed class Fixture
    {
        public P3[] Positions, Centers;
        public uint[] Indices, Words;
        public int[] Counts, Starts, Facing;
        public int ClusterCount => Counts.Length;
        public static Fixture Create(int count, bool sparse)
        {
            if (count < 0 || count > 4096) throw new ArgumentOutOfRangeException(nameof(count));
            var f = new Fixture { Centers=new P3[count], Counts=new int[count],
                Starts=new int[count], Facing=new int[count], Words=new uint[count*20] };
            var vertices=new List<P3>(); var indices=new List<uint>();
            int[] triangles={1,31,32,33,63,64,65};
            for(int i=0;i<count;++i)
            {
                P3 c=count==1 ? new P3(0,0,0) : new P3((i%16-7.5f)*1.4f,(i/16-4)*1.4f,0);
                f.Centers[i]=c; f.Counts[i]=triangles[i%triangles.Length]*3;
                f.Starts[i]=indices.Count; f.Facing[i]=!sparse || i%8==0 ? 1 : -1;
                int baseVertex=vertices.Count;
                for(int t=0;t<f.Counts[i]/3;++t)
                {
                    float x=c.x-0.55f+(t%9)*0.12f, y=c.y-0.5f+(t/9)*0.12f;
                    vertices.Add(new P3(x,y,0)); vertices.Add(new P3(x+0.09f,y,0));
                    vertices.Add(new P3(x+0.045f,y+0.09f,0));
                }
                // Deliberately not identity indices. Preserve cyclic winding.
                for(int t=f.Counts[i]/3-1;t>=0;--t)
                { indices.Add((uint)(baseVertex+t*3+2)); indices.Add((uint)(baseVertex+t*3)); indices.Add((uint)(baseVertex+t*3+1)); }
                int w=i*20; f.Words[w]=(uint)f.Starts[i]; f.Words[w+1]=(uint)f.Counts[i];
                f.Words[w+8]=Bits(c.x); f.Words[w+9]=Bits(c.y); f.Words[w+10]=Bits(c.z);
                f.Words[w+11]=Bits(0.7f); f.Words[w+12]=Bits(0.7f); f.Words[w+13]=Bits(0.001f);
                f.Words[w+14]=f.Facing[i]>0 ? 0u : 0x7fff7fffu; // oct16 +Z/-Z
                f.Words[w+15]=0; // cutoff zero
            }
            f.Positions=vertices.ToArray(); f.Indices=indices.ToArray(); return f;
        }
        static uint Bits(float v) => unchecked((uint)BitConverter.SingleToInt32Bits(v));

        public bool[] Visibility(P3[] views, P3 fallback, bool cone)
        {
            if(views==null || views.Length>8) throw new ArgumentException("Expected 0-8 culling views.");
            P3[] active=views.Length==0 ? new[]{fallback} : views;
            var result=new bool[ClusterCount];
            for(int i=0;i<ClusterCount;++i)
            {
                result[i]=!cone;
                foreach(P3 p in active)
                {
                    // Independent geometric oracle for the fixture's known +/-Z
                    // normals. Does not call or translate the HLSL cone decoder.
                    double dx=(double)p.x-Centers[i].x, dy=(double)p.y-Centers[i].y, dz=(double)p.z-Centers[i].z;
                    double d=Math.Sqrt(dx*dx+dy*dy+dz*dz);
                    if(!(d>0.001f) || Facing[i]*dz>=0) result[i]=true;
                }
            }
            return result;
        }
        public uint[] Expected(bool[] visible, bool flip)
        {
            if(visible.Length!=ClusterCount) throw new ArgumentException("Visibility length.");
            var output=new List<uint>();
            for(int i=0;i<ClusterCount;++i) if(visible[i])
                for(int j=0;j<Counts[i];j+=3)
                {
                    int s=Starts[i]+j;
                    output.Add(Indices[s]); output.Add(Indices[s+(flip?2:1)]); output.Add(Indices[s+(flip?1:2)]);
                }
            return output.ToArray();
        }
        public int RequiredWords(int tileTriangles)
        {
            if(tileTriangles==0) return Math.Max(1,Indices.Length);
            if(tileTriangles!=32 && tileTriangles!=64) throw new ArgumentOutOfRangeException(nameof(tileTriangles));
            int n=0; foreach(int c in Counts) n=checked(n+2*((c+tileTriangles*3-1)/(tileTriangles*3)));
            return Math.Max(1,n);
        }
        public void RequireCapacity(int words, int tileTriangles)
        {
            if(words<RequiredWords(tileTriangles)) throw new ArgumentException("Insufficient worst-case output capacity; no dispatch permitted.");
        }
        public int ExpectedDrawCount(bool[] visible, int tileTriangles)
        {
            int total=0;
            for(int i=0;i<ClusterCount;++i) if(visible[i])
                total+=tileTriangles==0 ? Counts[i] : ((Counts[i]+tileTriangles*3-1)/(tileTriangles*3))*tileTriangles*3;
            return total;
        }
        public uint[] Decode(uint[] output, uint drawCount, int tileTriangles, bool flip)
        {
            if(tileTriangles==0)
            {
                if(drawCount>output.Length || drawCount%3!=0) throw new ArgumentException("Copy output extent.");
                var copy=new uint[(int)drawCount]; Array.Copy(output,copy,copy.Length); return copy;
            }
            if(tileTriangles!=32 && tileTriangles!=64) throw new ArgumentException("Tile size.");
            uint tileSize=(uint)tileTriangles*3;
            if(drawCount%tileSize!=0 || (ulong)(drawCount/tileSize)*2>(ulong)output.Length)
                throw new ArgumentException("Tile output extent.");
            var expanded=new List<uint>();
            for(int t=0;t<(int)(drawCount/tileSize);++t)
            {
                uint start=output[t*2], count=output[t*2+1];
                if(count==0 || count>tileSize || count%3!=0 || (ulong)start+count>(ulong)Indices.Length)
                    throw new ArgumentException("Invalid descriptor source range.");
                for(uint j=0;j<count;j+=3)
                {
                    expanded.Add(Indices[start+j]);
                    expanded.Add(Indices[start+j+(flip?2u:1u)]);
                    expanded.Add(Indices[start+j+(flip?1u:2u)]);
                }
            }
            return expanded.ToArray();
        }
        public static string[] CanonicalTriangles(uint[] indices)
        {
            if(indices.Length%3!=0) throw new ArgumentException("Triangle alignment.");
            var triangles=new string[indices.Length/3];
            for(int i=0;i<triangles.Length;++i)
                triangles[i]=indices[i*3].ToString("X8")+":"+indices[i*3+1].ToString("X8")+":"+indices[i*3+2].ToString("X8");
            Array.Sort(triangles,StringComparer.Ordinal); return triangles;
        }
        public static void RequireEqualTriangles(uint[] expected, uint[] actual)
        {
            string[] a=CanonicalTriangles(expected), b=CanonicalTriangles(actual);
            if(a.Length!=b.Length) throw new InvalidOperationException("Triangle count mismatch.");
            for(int i=0;i<a.Length;++i) if(a[i]!=b[i]) throw new InvalidOperationException("Triangle membership/winding mismatch.");
        }
    }
}
