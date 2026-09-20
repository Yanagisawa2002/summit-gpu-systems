using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.VisibleTiles
{
    public sealed class VisibleTilesDemo : MonoBehaviour
    {
        const int Width=480, Height=320;
        static readonly string[] Arms={"ScalarAoS","ScalarCompact","WaveCompact","Tile32","Tile64"};
        static readonly string[] Kernels={"CullAndCompactBfp2Clusters","CullAndCompactBfp2ClustersCompact",
            "CullAndCompactBfp2ClustersWave","CullAndBuildBfp2VisibleTiles32","CullAndBuildBfp2VisibleTiles64"};
        sealed class Case
        {
            public string name; public int clusters=129; public bool sparse, flip, cone=true;
            public P3[] views; public P3 fallback=new P3(0,0,18);
        }
        [Serializable] sealed class Row
        {
            public string scenario, binding, arm, triangleSha256;
            public int visibleClusters, visibleIndices, drawVertices, logicalOutputBytes;
            public string[] outputImageSha256, referenceImageSha256;
        }
        [Serializable] sealed class Receipt
        {
            public string status="RUNNING", message="", sourceManifest, unity, buildGuid, gpu, backend, driver;
            public string timingStatus="Unavailable: correctness run with synchronous audit readbacks; not a benchmark.";
            public int expectedChecks;
            public List<Row> checks=new List<Row>();
        }
        ComputeShader scalar, wave;
        Material material;
        Camera[] cameras;
        readonly List<Texture2D> previews=new List<Texture2D>();
        Receipt receipt=new Receipt();
        string outputRoot, current="setup", gpuError="";
        bool quitAfter, ownsOutput;

        IEnumerator Start()
        {
            Application.runInBackground=true;
            Application.logMessageReceived+=OnLog;
            Exception failure=null;
            try
            {
                quitAfter=Environment.GetCommandLineArgs().Contains("-visible-tiles-validate");
                outputRoot=Argument("-visible-tiles-output");
                if(string.IsNullOrEmpty(outputRoot)) outputRoot=Path.Combine(Application.persistentDataPath,
                    "VisibleTiles",DateTime.UtcNow.ToString("yyyyMMddTHHmmss")+"-"+Guid.NewGuid().ToString("N"));
                outputRoot=Path.GetFullPath(outputRoot);
                if(Directory.Exists(outputRoot) || File.Exists(outputRoot)) throw new IOException("Output must be a NEW path.");
                Directory.CreateDirectory(outputRoot); ownsOutput=true;
                Setup();
            }
            catch(Exception e) { failure=e; }
            if(failure!=null) { Finish(failure); yield break; }
            Case[] cases=Cases(); receipt.expectedChecks=cases.Length*2*Arms.Length;
            foreach(Case c in cases) foreach(bool recorded in new[]{false,true})
            {
                try { RunCase(c,recorded); }
                catch(Exception e) { failure=e; }
                if(failure!=null) { Finish(failure); yield break; }
                yield return null;
            }
            Finish(null);
        }
        static string Argument(string key)
        {
            string[] a=Environment.GetCommandLineArgs(); int i=Array.IndexOf(a,key);
            if(i<0) return null;
            if(i+1==a.Length || a[i+1].StartsWith("-")) throw new ArgumentException("Missing value for "+key);
            return a[i+1];
        }
        void OnLog(string condition, string stack, LogType type)
        {
            if(type==LogType.Error || type==LogType.Exception || type==LogType.Assert) gpuError=condition;
        }
        void Setup()
        {
            if(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D12 || !SystemInfo.supportsComputeShaders)
                throw new NotSupportedException("This entry point requires Windows/D3D12 compute; no backend fallback is reported as PASS.");
            var source=Resources.Load<TextAsset>("staging");
            if(source==null) throw new InvalidOperationException("Run prepare.py before Unity import.");
            receipt.sourceManifest=source.text;
            receipt.unity=Application.unityVersion; receipt.buildGuid=Application.buildGUID;
            receipt.gpu=SystemInfo.graphicsDeviceName; receipt.backend=SystemInfo.graphicsDeviceType.ToString();
            receipt.driver=SystemInfo.graphicsDeviceVersion;
            scalar=Instantiate(Resources.Load<ComputeShader>("Culling/Bfp2GpuClusterCull"));
            wave=Instantiate(Resources.Load<ComputeShader>("Culling/Bfp2GpuClusterCullWave"));
            if(scalar==null || wave==null) throw new InvalidOperationException("Staged original compute shaders missing.");
            for(int a=0;a<Arms.Length;++a)
            {
                ComputeShader cs=a<2 ? scalar : wave;
                if(!cs.HasKernel(Kernels[a]) || !cs.IsSupported(cs.FindKernel(Kernels[a])))
                    throw new NotSupportedException("Unsupported kernel: "+Kernels[a]);
            }
            Shader shader=Resources.Load<Shader>("VisibleTilesDraw");
            if(shader==null || !shader.isSupported) throw new NotSupportedException("Draw shader unavailable.");
            material=new Material(shader);
            cameras=new Camera[2];
            for(int i=0;i<2;++i)
            {
                cameras[i]=new GameObject("Validation view "+i).AddComponent<Camera>();
                cameras[i].enabled=false; cameras[i].fieldOfView=60;
                cameras[i].aspect=(float)Width/Height; cameras[i].nearClipPlane=0.1f; cameras[i].farClipPlane=100;
                cameras[i].transform.position=new Vector3(0,0,i==0 ? -24 : 24);
                cameras[i].transform.LookAt(new Vector3(0,-0.5f,0));
            }
        }
        static Case[] Cases()
        {
            P3 front=new P3(0,0,18), back=new P3(0,0,-10);
            var eight=Enumerable.Repeat(back,8).ToArray(); eight[7]=front;
            return new[] {
                new Case{name="union-back-front",views=new[]{back,front}},
                new Case{name="union-reversed",views=new[]{front,back}},
                new Case{name="single-front",views=new[]{front}},
                new Case{name="single-back-empty",views=new[]{back}},
                new Case{name="all-rejected",views=new[]{back,new P3(0,0,-18)}},
                new Case{name="eighth-view",views=eight},
                new Case{name="low-visible",sparse=true,views=new[]{front}},
                new Case{name="zero-distance",clusters=1,views=new[]{back,new P3(0,0,0)}},
                new Case{name="empty-input",clusters=0,views=new[]{back,front}},
                new Case{name="legacy-fallback",views=Array.Empty<P3>()},
                new Case{name="cone-disabled",cone=false,views=new[]{back}},
                new Case{name="winding-flip",flip=true,views=new[]{back,front}}
            };
        }
        static ComputeBuffer Buffer<T>(T[] data, int stride) where T:struct
        {
            var b=new ComputeBuffer(Math.Max(1,data.Length),stride);
            if(data.Length>0) b.SetData(data); return b;
        }
        void RunCase(Case c, bool recorded)
        {
            Fixture f=Fixture.Create(c.clusters,c.sparse);
            bool[] visible=f.Visibility(c.views,c.fallback,c.cone);
            uint[] expected=f.Expected(visible,c.flip);
            using(var positions=Buffer(f.Positions,12))
            using(var indices=Buffer(f.Indices,4))
            using(var words=Buffer(f.Words,4))
            using(var compact=new ComputeBuffer(Math.Max(1,c.clusters*10),4))
            {
                Mesh reference=ReferenceMesh(f,expected);
                try
                {
                    for(int arm=0;arm<Arms.Length;++arm)
                    {
                        current=c.name+"/"+(recorded?"recorded":"direct")+"/"+Arms[arm];
                        int tile=arm==3 ? 32 : arm==4 ? 64 : 0;
                        int capacity=f.RequiredWords(tile); f.RequireCapacity(capacity,tile);
                        using(var output=new ComputeBuffer(capacity,4))
                        using(var args=new ComputeBuffer(4,4,ComputeBufferType.IndirectArguments))
                        using(var stats=new ComputeBuffer(4,4))
                        using(var cmd=new CommandBuffer { name="VisibleTiles correctness "+current })
                        {
                            // Nonzero sentinels exercise required clear behavior, including empty input.
                            output.SetData(Enumerable.Repeat(0xdeadbeefu,capacity).ToArray());
                            args.SetData(new uint[]{123,45,67,89}); stats.SetData(new uint[]{1,2,3,4});
                            var targets=new RenderTexture[4];
                            try
                            {
                                CommandBuffer binding=recorded ? cmd : null;
                                int clear=scalar.FindKernel("ClearBfp2Args");
                                SetBuffer(binding,scalar,clear,"_Bfp2DrawArgs",args);
                                SetBuffer(binding,scalar,clear,"_Bfp2Stats",stats); Dispatch(binding,scalar,clear,1);
                                int pack=scalar.FindKernel("PackBfp2CompactCullClusters");
                                SetBuffer(binding,scalar,pack,"_Bfp2ClusterWords",words);
                                SetBuffer(binding,scalar,pack,"_Bfp2CompactClusterWords",compact);
                                SetInt(binding,scalar,"_Bfp2ClusterCount",c.clusters);
                                Dispatch(binding,scalar,pack,Math.Max(1,(c.clusters+127)/128));
                                ComputeShader cs=arm<2 ? scalar : wave; int kernel=cs.FindKernel(Kernels[arm]);
                                BindCull(binding,cs,kernel,c,tile,arm,indices,words,compact,output,args,stats);
                                Dispatch(binding,cs,kernel,Math.Max(1,(c.clusters+127)/128));
                                for(int view=0;view<2;++view)
                                {
                                    Matrix4x4 clip=GL.GetGPUProjectionMatrix(cameras[view].projectionMatrix,true)*cameras[view].worldToCameraMatrix;
                                    var props=new MaterialPropertyBlock(); props.SetMatrix("_ClipFromWorld",clip);
                                    props.SetBuffer("_Positions",positions); props.SetBuffer("_SourceIndices",indices);
                                    props.SetBuffer("_VisibleOutput",output); props.SetFloat("_TileTriangles",tile);
                                    props.SetFloat("_FlipWinding",c.flip?1:0);
                                    targets[view*2]=Target(); cmd.SetRenderTarget(targets[view*2]); cmd.ClearRenderTarget(true,true,Color.black);
                                    cmd.DrawProceduralIndirect(Matrix4x4.identity,material,0,MeshTopology.Triangles,args,0,props);
                                    targets[view*2+1]=Target(); cmd.SetRenderTarget(targets[view*2+1]); cmd.ClearRenderTarget(true,true,Color.black);
                                    if(expected.Length>0) cmd.DrawMesh(reference,Matrix4x4.identity,material,0,1,props);
                                }
                                // Same graphics queue, real draws BEFORE any audit readback. No async queue.
                                Graphics.ExecuteCommandBuffer(cmd);
                                uint[] arguments=new uint[4], counters=new uint[4], raw=new uint[capacity];
                                args.GetData(arguments); stats.GetData(counters); output.GetData(raw);
                                int kept=visible.Count(v=>v);
                                if(arguments[0]!=(uint)f.ExpectedDrawCount(visible,tile) || arguments[1]!=1 || arguments[2]!=0 || arguments[3]!=0 ||
                                   counters[0]!=(uint)kept || counters[1]!=(uint)expected.Length || counters[2]!=(uint)(c.clusters-kept) || counters[3]!=0)
                                    throw new InvalidOperationException("Draw arguments/statistics disagree with independent oracle: "+current);
                                Fixture.RequireEqualTriangles(expected,f.Decode(raw,arguments[0],tile,c.flip));
                                string folder=Path.Combine(outputRoot,c.name,recorded?"recorded":"direct",Arms[arm]);
                                Directory.CreateDirectory(folder);
                                SaveWords(Path.Combine(folder,"output.bin"),raw); SaveWords(Path.Combine(folder,"args.bin"),arguments);
                                SaveWords(Path.Combine(folder,"stats.bin"),counters); SaveWords(Path.Combine(folder,"expected.bin"),expected);
                                var row=new Row{scenario=c.name,binding=recorded?"recorded":"direct",arm=Arms[arm],
                                    visibleClusters=kept,visibleIndices=expected.Length,drawVertices=(int)arguments[0],
                                    logicalOutputBytes=tile==0 ? expected.Length*4 : (int)(arguments[0]/(uint)(tile*3))*8,
                                    triangleSha256=Hash(Encoding.UTF8.GetBytes(string.Join("\n",Fixture.CanonicalTriangles(expected)))),
                                    outputImageSha256=new string[2],referenceImageSha256=new string[2]};
                                for(int view=0;view<2;++view)
                                {
                                    Texture2D actual=Read(targets[view*2]), golden=Read(targets[view*2+1]);
                                    try
                                    {
                                        byte[] image=actual.EncodeToPNG(), referenceImage=golden.EncodeToPNG();
                                        File.WriteAllBytes(Path.Combine(folder,"view"+view+".png"),image);
                                        File.WriteAllBytes(Path.Combine(folder,"view"+view+"-reference.png"),referenceImage);
                                        CheckPixels(actual.GetPixels32(),golden.GetPixels32(),expected.Length>0);
                                        row.outputImageSha256[view]=Hash(image); row.referenceImageSha256[view]=Hash(referenceImage);
                                        if(c.name=="union-back-front" && recorded && (arm==0 || arm==2 || arm==3))
                                        { previews.Add(actual); actual=null; }
                                    }
                                    finally { if(actual!=null) Destroy(actual); Destroy(golden); }
                                }
                                if(!string.IsNullOrEmpty(gpuError)) throw new InvalidOperationException("Unity error: "+gpuError);
                                receipt.checks.Add(row);
                            }
                            finally
                            {
                                // Blocking audit reads follow the complete submitted draw stream; buffers
                                // stay owned until then. No outstanding async readback or cross-queue use.
                                foreach(var rt in targets) if(rt!=null) { rt.Release(); Destroy(rt); }
                            }
                        }
                    }
                }
                finally { Destroy(reference); }
            }
        }
        static void SetBuffer(CommandBuffer cb,ComputeShader cs,int k,string n,ComputeBuffer b)
        { if(cb==null) cs.SetBuffer(k,n,b); else cb.SetComputeBufferParam(cs,k,n,b); }
        static void SetInt(CommandBuffer cb,ComputeShader cs,string n,int v)
        { if(cb==null) cs.SetInt(n,v); else cb.SetComputeIntParam(cs,n,v); }
        static void SetVectors(CommandBuffer cb,ComputeShader cs,string n,Vector4[] v)
        { if(cb==null) cs.SetVectorArray(n,v); else cb.SetComputeVectorArrayParam(cs,n,v); }
        static void Dispatch(CommandBuffer cb,ComputeShader cs,int k,int groups)
        { if(cb==null) cs.Dispatch(k,groups,1,1); else cb.DispatchCompute(cs,k,groups,1,1); }
        static void BindCull(CommandBuffer cb,ComputeShader cs,int k,Case c,int tile,int arm,
            ComputeBuffer indices,ComputeBuffer words,ComputeBuffer compact,ComputeBuffer output,ComputeBuffer args,ComputeBuffer stats)
        {
            if(tile==0) SetBuffer(cb,cs,k,"_Bfp2Indices",indices);
            SetBuffer(cb,cs,k,arm==0 ? "_Bfp2ClusterWords" : "_Bfp2CompactClusterWords",arm==0 ? words : compact);
            SetBuffer(cb,cs,k,tile==0 ? "_Bfp2VisibleIndices" : "_Bfp2VisibleTileWords",output);
            SetBuffer(cb,cs,k,"_Bfp2DrawArgs",args); SetBuffer(cb,cs,k,"_Bfp2Stats",stats);
            SetInt(cb,cs,tile==0 ? "_Bfp2MaxVisibleIndices" : "_Bfp2MaxVisibleTileWords",output.count);
            SetInt(cb,cs,"_Bfp2ClusterCount",c.clusters); SetInt(cb,cs,"_Bfp2CameraFrameCount",c.views.Length);
            var views=new Vector4[8]; for(int i=0;i<c.views.Length;++i) views[i]=new Vector4(c.views[i].x,c.views[i].y,c.views[i].z,0);
            SetVectors(cb,cs,"_Bfp2CameraPositionsOS",views);
            SetVectors(cb,cs,"_Bfp2FrustumPlanes",new Vector4[6]); SetVectors(cb,cs,"_Bfp2CameraFrustumPlanes",new Vector4[48]);
            SetVectors(cb,cs,"_Bfp2CameraScreenParams",new Vector4[8]);
            var fallback=new Vector4(c.fallback.x,c.fallback.y,c.fallback.z,0);
            if(cb==null) cs.SetVector("_Bfp2CameraPositionOS",fallback); else cb.SetComputeVectorParam(cs,"_Bfp2CameraPositionOS",fallback);
            SetInt(cb,cs,"_Bfp2EnableNormalConeCulling",c.cone?1:0);
            SetInt(cb,cs,"_Bfp2EnableFrustumCulling",0); SetInt(cb,cs,"_Bfp2EnableDistanceCulling",0);
            SetInt(cb,cs,"_Bfp2EnableScreenSizeCulling",0); SetInt(cb,cs,"_Bfp2FlipTriangleWinding",c.flip?1:0);
        }
        static Mesh ReferenceMesh(Fixture f,uint[] expected)
        {
            var m=new Mesh{indexFormat=IndexFormat.UInt32};
            var p=new Vector3[expected.Length]; var colors=new Color32[p.Length]; var ids=new int[p.Length];
            for(int i=0;i<p.Length;++i)
            {
                uint id=expected[i], tri=id/3; P3 v=f.Positions[id]; p[i]=new Vector3(v.x,v.y,v.z); ids[i]=i;
                colors[i]=new Color32((byte)((tri*17)%251+4),(byte)((tri*47)%251+4),(byte)((tri*97)%251+4),255);
            }
            m.vertices=p; m.colors32=colors;
            if(ids.Length>0) m.SetIndices(ids,MeshTopology.Triangles,0);
            return m;
        }
        static RenderTexture Target()
        {
            var t=new RenderTexture(Width,Height,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear)
                {antiAliasing=1,filterMode=FilterMode.Point};
            if(!t.Create()) { Destroy(t); throw new InvalidOperationException("RenderTexture creation failed."); } return t;
        }
        static Texture2D Read(RenderTexture t)
        {
            RenderTexture old=RenderTexture.active;
            var tex=new Texture2D(Width,Height,TextureFormat.RGBA32,false,true);
            try { RenderTexture.active=t; tex.ReadPixels(new Rect(0,0,Width,Height),0,0,false); tex.Apply(false); return tex; }
            catch { Destroy(tex); throw; }
            finally { RenderTexture.active=old; }
        }
        static void CheckPixels(Color32[] a,Color32[] b,bool nonempty)
        {
            if(a.Length!=b.Length) throw new InvalidOperationException("Image extent mismatch.");
            int foreground=0;
            for(int i=0;i<a.Length;++i)
            {
                if(b[i].r!=0 || b[i].g!=0 || b[i].b!=0) ++foreground;
                if(Math.Abs(a[i].r-b[i].r)>1 || Math.Abs(a[i].g-b[i].g)>1 || Math.Abs(a[i].b-b[i].b)>1 || Math.Abs(a[i].a-b[i].a)>1)
                    throw new InvalidOperationException("Reference image mismatch at pixel "+i);
            }
            if(nonempty && foreground==0) throw new InvalidOperationException("Nonempty fixture rendered only background.");
        }
        static void SaveWords(string path,uint[] data)
        { using(var stream=new FileStream(path,FileMode.CreateNew)) using(var writer=new BinaryWriter(stream)) foreach(uint v in data) writer.Write(v); }
        static string Hash(byte[] bytes)
        { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
        void Finish(Exception error)
        {
            if(error==null && receipt.checks.Count!=receipt.expectedChecks) error=new InvalidOperationException("Incomplete case matrix.");
            receipt.status=error==null ? "PASS" : "FAIL";
            receipt.message=error==null ? "All full-output and independent mesh-image checks passed." : current+": "+error;
            try
            {
                if(ownsOutput && outputRoot!=null && Directory.Exists(outputRoot))
                    using(var file=new StreamWriter(new FileStream(Path.Combine(outputRoot,"result.json"),FileMode.CreateNew)))
                        file.Write(JsonUtility.ToJson(receipt,true));
            }
            catch(Exception e) { receipt.status="FAIL"; receipt.message+="\nReceipt write failed: "+e; }
            Debug.Log(receipt.status+": "+receipt.message+"\nEvidence: "+outputRoot);
            if(quitAfter) Application.Quit(receipt.status=="PASS"?0:1);
        }
        void OnGUI()
        {
            GUI.Label(new Rect(12,8,Screen.width-24,60),"SUMMIT Visible Tiles | "+receipt.status+" | "+receipt.checks.Count+"/"+receipt.expectedChecks+
                " checks\nScalar / WaveCompact / Tile32; two views. Correctness demonstration, not an FPS comparison.");
            float width=(Screen.width-48)/3f, height=width*Height/Width;
            for(int i=0;i<previews.Count;++i) GUI.DrawTexture(new Rect(12+(i/2)*(width+12),76+(i%2)*(height+12),width,height),previews[i],ScaleMode.ScaleToFit);
            GUI.Label(new Rect(12,Screen.height-52,Screen.width-24,48),receipt.status=="FAIL" ? receipt.message : "Evidence: "+outputRoot);
        }
        void OnDestroy()
        {
            Application.logMessageReceived-=OnLog;
            foreach(var t in previews) if(t!=null) Destroy(t);
            if(cameras!=null) foreach(var c in cameras) if(c!=null) Destroy(c.gameObject);
            if(material!=null) Destroy(material); if(scalar!=null) Destroy(scalar); if(wave!=null) Destroy(wave);
        }
    }
}
