using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuPrimitives
{
    /// <summary>Raw-buffer recorder for the ACTUAL HLSL wave-tiled asset. Does not
    /// alias a local SUMMIT variant under an external identity or change Auto.</summary>
    public sealed class GpuHlslScanConsumer : IDisposable
    {
        readonly ComputeShader shader;
        readonly GraphicsBuffer scratch;
        readonly int reset,scan;
        readonly int capacity;
        bool disposed;
        public HlslScanArtifact Artifact { get; }
        public int Capacity=>capacity;
        public int ScratchBytes=>Artifact.ScratchBytes(capacity);
        GpuHlslScanConsumer(HlslScanArtifact artifact,ComputeShader shader,int capacity,int reset,int scan)
        {
            Artifact=artifact;this.shader=shader;this.capacity=capacity;this.reset=reset;this.scan=scan;
            scratch=new GraphicsBuffer(GraphicsBuffer.Target.Raw,artifact.ScratchBytes(capacity)/4,4);
        }
        // The imported shader must be returned by the build-time verified catalog
        // for this artifact. See the Editor factory; arbitrary profile paths are never loaded.
        public static bool TryCreate(HlslScanArtifact artifact,ComputeShader installedShader,HlslScanSelection selection,
            string runtimeDeviceDriverId,int capacity,bool allowUnmeasured,out GpuHlslScanConsumer consumer,out string reason)
        {
            consumer=null;
            if(!allowUnmeasured){reason="unmeasured-opt-in-required";return false;}
            if(artifact==null){reason="verified-deployment-missing";return false;}
            artifact.ScratchBytes(capacity);
            if(!artifact.Accepts(selection,runtimeDeviceDriverId,out reason))return false;
            if(!SystemInfo.supportsComputeShaders||SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D12)
            {reason="d3d12-compute-required";return false;}
            if(installedShader==null||!installedShader.HasKernel("ResetWaveTiledState")||!installedShader.HasKernel("SinglePassScanWaveTiled"))
            {reason="actual-hlsl-shader-missing";return false;}
            int reset=installedShader.FindKernel("ResetWaveTiledState"),scan=installedShader.FindKernel("SinglePassScanWaveTiled");
            if(!installedShader.IsSupported(reset)||!installedShader.IsSupported(scan))
            {reason="sm66-wave32-import-not-supported";return false;}
            foreach(int kernel in new[]{reset,scan})
            {
                installedShader.GetKernelThreadGroupSizes(kernel,out uint x,out uint y,out uint z);
                if(x!=256||y!=1||z!=1){reason="compiled-thread-shape-mismatch";return false;}
            }
            consumer=new GpuHlslScanConsumer(artifact,installedShader,capacity,reset,scan);
            reason="explicit-unmeasured-hlsl-scan";return true;
        }
        // No submission, readback, profiling, growth, or managed/GPU allocation here.
        // Caller owns queue ordering and must finish previous uses before Dispose.
        public void RecordExclusiveScan(CommandBuffer commands,GraphicsBuffer input,GraphicsBuffer output,int count)
        {
            if(disposed)throw new ObjectDisposedException(nameof(GpuHlslScanConsumer));
            if(commands==null)throw new ArgumentNullException(nameof(commands));
            if(count<0||count>capacity)throw new ArgumentOutOfRangeException(nameof(count));
            Validate(input,count);Validate(output,count);
            if(input==output||input==scratch||output==scratch)throw new ArgumentException("Distinct Raw buffers required.");
            if(count==0)return;
            int partitions=(count+HlslScanArtifact.TileSize-1)/HlslScanArtifact.TileSize;
            commands.SetComputeIntParam(shader,"ElementCount",count);
            commands.SetComputeIntParam(shader,"ElementsPerBlock",HlslScanArtifact.TileSize);
            commands.SetComputeIntParam(shader,"LogicalBlockCount",partitions);
            commands.SetComputeBufferParam(shader,reset,"Output0",scratch);
            commands.DispatchCompute(shader,reset,1,1,1);
            commands.SetComputeBufferParam(shader,scan,"Input0",input);
            commands.SetComputeBufferParam(shader,scan,"Output0",output);
            commands.SetComputeBufferParam(shader,scan,"Output1",scratch);
            commands.DispatchCompute(shader,scan,HlslScanArtifact.ScanGroups(count),1,1);
        }
        static void Validate(GraphicsBuffer b,int count)
        {
            if(b==null||b.stride!=4||(b.target&GraphicsBuffer.Target.Raw)==0||b.count<Math.Max(1,count))
                throw new ArgumentException("HLSL scan requires Raw uint32 buffers; Structured storage needs an explicit conversion path.");
        }
        public void Dispose(){if(disposed)return;disposed=true;scratch.Dispose();}
    }
}
