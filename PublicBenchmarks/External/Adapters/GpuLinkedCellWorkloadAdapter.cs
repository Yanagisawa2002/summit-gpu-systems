using System;
using Summit.GpuDirectBinning;
using Summit.GpuPrimitives;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.ExternalWorkloads
{
    public sealed class GpuLinkedCellWorkloadAdapter : IDisposable
    {
        readonly LinkedCellWorkloadContract contract;
        readonly int capacity;
        GpuDirectSpatialBinner binner;
        GraphicsBuffer keys, sourceIds;
        public GraphicsBuffer Counts { get; private set; }
        public GraphicsBuffer Offsets { get; private set; }
        public GraphicsBuffer Ids { get; private set; }
        public GraphicsBuffer Diagnostics { get; private set; }
        int count;
        bool disposed, uploaded;
        public GpuLinkedCellWorkloadAdapter(LinkedCellWorkloadContract contract, int capacity, bool allowUnmeasured)
        {
            if(!allowUnmeasured) throw new InvalidOperationException("Cabana grouping adapter is opt-in and Unmeasured.");
            this.contract=contract??throw new ArgumentNullException(nameof(contract));
            if(capacity<1 || capacity>GpuPrimitives.GpuPrimitives.MaxElementCount) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.capacity=capacity;
            try
            {
                binner=new GpuDirectSpatialBinner(capacity,contract.BinCount,emitProfilerMarkers:false);
                keys=Buffer(capacity);sourceIds=Buffer(capacity);Counts=Buffer(contract.BinCount);
                Offsets=Buffer(contract.BinCount+1);Ids=Buffer(capacity);Diagnostics=Buffer(2);
            }
            catch{Dispose();throw;}
        }
        // Caller must fence the previous snapshot before Upload or Dispose.
        public void Upload(LinkedCellPoint[] points)
        {
            Check();uploaded=false;
            if(points==null || points.Length>capacity) throw new ArgumentException("Complete particle snapshot exceeds capacity.");
            var encoded=contract.Encode(points);var ids=new uint[points.Length];
            for(int i=0;i<points.Length;i++)ids[i]=points[i].Id;
            if(points.Length>0){keys.SetData(encoded);sourceIds.SetData(ids);}
            count=points.Length;uploaded=true;
        }
        public void Record(CommandBuffer commands)
        {
            Check();if(!uploaded)throw new InvalidOperationException("Validated source snapshot required.");
            binner.Record(commands,keys,sourceIds,Counts,Offsets,Ids,Diagnostics,count,contract.BinCount,GpuPrimitiveBackend.Portable);
        }
        static GraphicsBuffer Buffer(int n)=>new GraphicsBuffer(GraphicsBuffer.Target.Structured,n,4);
        void Check(){if(disposed)throw new ObjectDisposedException(nameof(GpuLinkedCellWorkloadAdapter));}
        public void Dispose(){if(disposed)return;disposed=true;binner?.Dispose();keys?.Dispose();sourceIds?.Dispose();Counts?.Dispose();Offsets?.Dispose();Ids?.Dispose();Diagnostics?.Dispose();}
    }
}
