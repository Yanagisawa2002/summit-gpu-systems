// Inert command-recording doubles. No Unity DLL, native plugin, device, or clock.
using System;
using System.Collections.Generic;
namespace UnityEngine
{
    public sealed class GraphicsBuffer : IDisposable
    {
        [Flags]public enum Target { Raw=1,Structured=2 }
        public readonly Target target;public readonly int count,stride;
        public bool Disposed;
        public GraphicsBuffer(Target t,int n,int s){target=t;count=n;stride=s;}
        public void Dispose(){Disposed=true;}
    }
    public sealed class ComputeShader
    {
        readonly Dictionary<string,int> kernels=new Dictionary<string,int>();
        public bool Supported=true;public uint Threads=256;
        public ComputeShader(params string[] names){for(int i=0;i<names.Length;i++)kernels.Add(names[i],i);}
        public bool HasKernel(string name)=>kernels.ContainsKey(name);
        public int FindKernel(string name)=>kernels[name];
        public bool IsSupported(int kernel)=>Supported;
        public void GetKernelThreadGroupSizes(int kernel,out uint x,out uint y,out uint z){x=Threads;y=z=1;}
    }
    public static class Resources
    {
        public static object Value;
        public static T Load<T>(string path)where T:class=>Value as T;
    }
    public static class SystemInfo
    {
        public static bool supportsComputeShaders=true;
        public static Rendering.GraphicsDeviceType graphicsDeviceType=Rendering.GraphicsDeviceType.Direct3D12;
    }
}
namespace UnityEngine.Rendering
{
    public enum GraphicsDeviceType{Null,Direct3D12}
    public sealed class CommandBuffer
    {
        public sealed class Dispatch
        {
            public UnityEngine.ComputeShader Shader;public int Kernel,X;
            public Dictionary<string,int> Constants;
            public Dictionary<string,UnityEngine.GraphicsBuffer> Buffers;
        }
        readonly Dictionary<string,int> constants=new Dictionary<string,int>();
        readonly Dictionary<(UnityEngine.ComputeShader,int),Dictionary<string,UnityEngine.GraphicsBuffer>> buffers=new Dictionary<(UnityEngine.ComputeShader,int),Dictionary<string,UnityEngine.GraphicsBuffer>>();
        public readonly List<Dispatch> Dispatches=new List<Dispatch>();
        public void SetComputeIntParam(UnityEngine.ComputeShader shader,string name,int value){constants[name]=value;}
        public void SetComputeBufferParam(UnityEngine.ComputeShader shader,int kernel,string name,UnityEngine.GraphicsBuffer buffer)
        {var key=(shader,kernel);if(!buffers.ContainsKey(key))buffers[key]=new Dictionary<string,UnityEngine.GraphicsBuffer>();buffers[key][name]=buffer;}
        public void DispatchCompute(UnityEngine.ComputeShader shader,int kernel,int x,int y,int z)
        {
            if(y!=1||z!=1)throw new Exception("Unexpected dispatch shape");
            Dispatches.Add(new Dispatch{Shader=shader,Kernel=kernel,X=x,Constants=new Dictionary<string,int>(constants),Buffers=new Dictionary<string,UnityEngine.GraphicsBuffer>(buffers[(shader,kernel)])});
        }
    }
}
