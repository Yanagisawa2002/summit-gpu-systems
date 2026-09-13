using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.GpuPrimitives
{
    /// <summary>Explicit SoA Structured<->Raw boundary conversion for real SUMMIT
    /// consumers. Both copies and external reset/scan belong to the total cost.
    /// Caller owns the supplied HLSL consumer and fences all uses before disposal.</summary>
    public sealed class GpuHlslStructuredScanBridge : IDisposable
    {
        readonly GpuHlslScanConsumer consumer;
        readonly ComputeShader shader;
        readonly int toRaw,toStructured;
        GraphicsBuffer rawInput,rawOutput;
        bool disposed;
        public int Capacity {get;}
        public long ScratchBytes=>8L*Capacity+consumer.ScratchBytes;
        public GpuHlslStructuredScanBridge(GpuHlslScanConsumer consumer,int capacity)
        {
            this.consumer=consumer??throw new ArgumentNullException(nameof(consumer));
            if(capacity<1||capacity>consumer.Capacity)throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity=capacity;
            shader=Resources.Load<ComputeShader>("GpuPrimitives/HlslRawScanBridge");
            if(shader==null)throw new InvalidOperationException("Raw conversion shader missing.");
            toRaw=shader.FindKernel("ToRaw");toStructured=shader.FindKernel("ToStructured");
            if(!shader.IsSupported(toRaw)||!shader.IsSupported(toStructured))throw new NotSupportedException("Raw conversion kernels unavailable.");
            try{rawInput=new GraphicsBuffer(GraphicsBuffer.Target.Raw,capacity,4);rawOutput=new GraphicsBuffer(GraphicsBuffer.Target.Raw,capacity,4);}
            catch{Dispose();throw;}
        }
        public void RecordExclusiveScan(CommandBuffer commands,GraphicsBuffer input,GraphicsBuffer output,int count)
        {
            if(disposed)throw new ObjectDisposedException(nameof(GpuHlslStructuredScanBridge));
            if(commands==null)throw new ArgumentNullException(nameof(commands));
            if(count<0||count>Capacity)throw new ArgumentOutOfRangeException(nameof(count));
            Validate(input,count);Validate(output,count);
            if(input==output)throw new ArgumentException("Scan input/output must not alias.");
            if(count==0)return;
            commands.SetComputeIntParam(shader,"_Count",count);
            commands.SetComputeBufferParam(shader,toRaw,"_StructuredInput",input);
            commands.SetComputeBufferParam(shader,toRaw,"_RawOutput",rawInput);
            commands.DispatchCompute(shader,toRaw,(count+255)/256,1,1);
            consumer.RecordExclusiveScan(commands,rawInput,rawOutput,count);
            commands.SetComputeBufferParam(shader,toStructured,"_RawInput",rawOutput);
            commands.SetComputeBufferParam(shader,toStructured,"_StructuredOutput",output);
            commands.DispatchCompute(shader,toStructured,(count+255)/256,1,1);
        }
        static void Validate(GraphicsBuffer b,int count)
        {if(b==null||b.stride!=4||(b.target&GraphicsBuffer.Target.Structured)==0||b.count<Math.Max(count,1))throw new ArgumentException("Structured uint input/output required.");}
        public void Dispose(){if(disposed)return;disposed=true;rawInput?.Dispose();rawOutput?.Dispose();}
    }
}
