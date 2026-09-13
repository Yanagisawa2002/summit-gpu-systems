using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Summit.ExternalWorkloads
{
    /// <summary>Bridge input exported from the pinned native ArborX workload, with
    /// its complete reference CSR. No point generator or performance runner.</summary>
    public sealed class ArborXSnapshot
    {
        public const string UpstreamCommit = "375875dfb6b2e7631b1ba599cd26ee5c1e68ab90";
        public SourcePoint[] Points { get; private set; }
        public SourceSphere[] Spheres { get; private set; }
        public uint[][] ReferenceMembership { get; private set; }

        public static ArborXSnapshot Read(Stream source, string upstreamCommit, string expectedSha256)
        {
            if (upstreamCommit != UpstreamCommit || expectedSha256 == null || expectedSha256.Length != 64)
                throw new ArgumentException("Pinned source commit and independently recorded input SHA256 required.");
            if (source == null || !source.CanSeek || !source.CanRead || source.Length > 512L*1024*1024)
                throw new ArgumentException("Seekable bounded snapshot required.");
            source.Position=0;
            using(var sha=SHA256.Create())
            {
                string hash=BitConverter.ToString(sha.ComputeHash(source)).Replace("-", "").ToLowerInvariant();
                if(!string.Equals(hash,expectedSha256,StringComparison.Ordinal)) throw new InvalidDataException("Input SHA256 mismatch.");
            }
            source.Position=0;
            using(var reader=new BinaryReader(source,Encoding.UTF8,true))
            {
                if(Encoding.ASCII.GetString(reader.ReadBytes(8))!="SMSPH001") throw new InvalidDataException("Unknown sphere snapshot ABI.");
                uint n=reader.ReadUInt32(), q=reader.ReadUInt32(), total=reader.ReadUInt32();
                long expectedBytes=20L+16L*n+16L*q+4L*(q+1L)+4L*total;
                if(n>16776960 || q>65535 || total>int.MaxValue || source.Length!=expectedBytes || total>(long)n*q)
                    throw new InvalidDataException("Malformed snapshot dimensions.");
                var output=new ArborXSnapshot{Points=new SourcePoint[n],Spheres=new SourceSphere[q],ReferenceMembership=new uint[q][]};
                var validIds=new System.Collections.Generic.HashSet<uint>();
                for(int i=0;i<n;i++)
                {
                    var p=new SourcePoint(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle(),reader.ReadUInt32());
                    SphereWorkloadDomain.ValidateCoordinate(p.X);SphereWorkloadDomain.ValidateCoordinate(p.Y);SphereWorkloadDomain.ValidateCoordinate(p.Z);
                    if(!validIds.Add(p.Id))throw new InvalidDataException("Duplicate source IDs.");output.Points[i]=p;
                }
                for(int i=0;i<q;i++)
                {
                    var s=new SourceSphere(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
                    SphereWorkloadDomain.ValidateCoordinate(s.X);SphereWorkloadDomain.ValidateCoordinate(s.Y);SphereWorkloadDomain.ValidateCoordinate(s.Z);SphereWorkloadDomain.ValidateCoordinate(s.Radius);
                    if(s.Radius<=0)throw new InvalidDataException("Pinned ArborX make_intersects workload requires positive radius.");output.Spheres[i]=s;
                }
                var offsets=new uint[q+1];
                for(int i=0;i<offsets.Length;i++)
                {
                    offsets[i]=reader.ReadUInt32();
                    if(offsets[i]>total || (i==0 && offsets[i]!=0) || (i>0 && offsets[i]<offsets[i-1]))throw new InvalidDataException("Malformed complete CSR offsets.");
                }
                if(offsets[q]!=total)throw new InvalidDataException("Missing reference membership.");
                for(int i=0;i<q;i++)
                {
                    output.ReferenceMembership[i]=new uint[offsets[i+1]-offsets[i]];
                    var seen=new System.Collections.Generic.HashSet<uint>();
                    for(int j=0;j<output.ReferenceMembership[i].Length;j++)
                    {
                        uint id=reader.ReadUInt32();if(!validIds.Contains(id)||!seen.Add(id))throw new InvalidDataException("Invalid reference membership ID.");
                        output.ReferenceMembership[i][j]=id;
                    }
                }
                return output;
            }
        }
    }
}
