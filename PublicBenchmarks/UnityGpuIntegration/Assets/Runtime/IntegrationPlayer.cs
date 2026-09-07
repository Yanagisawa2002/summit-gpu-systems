using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Summit.GpuPrimitives;
using Summit.GpuSensorPipeline;
using Summit.GpuTimestamps;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Pipeline=Summit.GpuSensorPipeline.GpuSensorPipeline;

namespace Summit.PublicIntegration
{
    [Serializable] public sealed class IntegrationConfig
    {
        public string mode="oracle",scenario="sparse-low-change",output,oracle,sourceSha;
        public uint seed=920071;public int frames=384,warmup=64,blocks=1,processReplicate;
        public string[] arms={"old-full"};public bool screenshot;
    }
    [Serializable] public sealed class NativeTiming
    {
        public string status="pending";public ulong token,beginTicks,endTicks,frequency;
        public int sourceFrame,resultFrame;public double milliseconds=-1;
    }
    [Serializable] public sealed class IntegrationFrame
    {
        public int frame,unityFrame,activeCount,changedSlots,queryCount=9,drawVertices=262144,overlayVertices=54,drawCalls=2,historyDispatches=1,indexRecordedDispatches,queryRecordedDispatches;
        public bool measured;public long cpuStartTicks,qpcStart,qpcEnd,uploadedBytes,recordAllocatedBytes;
        public double engineCadenceMs,recordCpuMs;public int gc0,gc1,gc2;
        public NativeTiming sceneGpu=new NativeTiming(),indexGpu=new NativeTiming(),queryGpu=new NativeTiming();
    }
    [Serializable] public sealed class EngineTiming
    {
        public ulong frameStartTimestamp,cpuTimePresentCalled,cpuTimeFrameComplete;
        public double cpuFrameMs,mainThreadMs,renderThreadMs,presentWaitMs,gpuFrameMs;
        public int observedUnityFrame;
    }
    [Serializable] public sealed class IntegrationArm
    {
        public string arm,startedUtc,endedUtc,queryBackend,indexBackend,oracleSha256;
        public int block,position,frameCount,verifiedDigestWords;public long residentBytes,allocatedBefore,allocatedAfter;
        public double setupMilliseconds,asyncDrainMilliseconds;
        public bool verified;public IntegrationFrame[] frames;public List<ContentEvent> contentEvents=new List<ContentEvent>();
    }
    [Serializable] public sealed class ProcessFrame
    {
        public int unityFrame;public long qpc;public double engineIntervalMs;public string phase;
    }
    [Serializable] public sealed class IntegrationResult
    {
        public int schemaVersion=1,processId;public string status="running",error,startedUtc,endedUtc,buildGuid,unityVersion,device,graphicsApi;
        public bool development,formalPerformanceEvidence,osPresentationAvailable=false;
        public string firstScreenMetric="Engine first rendered frame proxy; OS first-present unavailable";
        public string nativeScope="Main D3D12 queue: explicit scene clear + index + nine queries + digest history + actual draw; full engine GPU separately from FrameTimingManager";
        public string engineScope="All process Update intervals retained, plus complete per-arm windows and predeclared warmup exclusion for steady comparisons; not OS displayed cadence";
        public double firstRenderedEngineMilliseconds=-1;public ulong engineCpuTimerFrequency;public long qpcFrequency,stopwatchFrequency;
        public IntegrationConfig config;public List<IntegrationArm> runs=new List<IntegrationArm>();
        public List<EngineTiming> engineTimings=new List<EngineTiming>();
        public List<ProcessFrame> processFrames=new List<ProcessFrame>();
    }
    public sealed class IntegrationPlayer:MonoBehaviour
    {
        const int N=IntegrationFixture.Capacity;
        IntegrationConfig config;IntegrationResult result;Camera cameraView;Material material;
        GpuTimestampSession timestamps;ComputeShader historyShader;int historyKernel;
        readonly FrameTiming[] frameTimings=new FrameTiming[16];readonly HashSet<ulong> seenEngineFrames=new HashSet<ulong>();
        readonly List<PendingTiming> pending=new List<PendingTiming>(2048);
        double lastProcessFrame;string phase="startup";ulong nextTag=1;
        struct PendingTiming {public GpuTimestampToken token;public NativeTiming result;}
        [DllImport("kernel32.dll")] static extern bool QueryPerformanceCounter(out long ticks);
        [DllImport("kernel32.dll")] static extern bool QueryPerformanceFrequency(out long frequency);
        static long Qpc(){if(!QueryPerformanceCounter(out long ticks))throw new Exception("QPC unavailable");return ticks;}
        static readonly int SamplesId=Shader.PropertyToID("_Samples"),ActiveId=Shader.PropertyToID("_Active"),DigestsId=Shader.PropertyToID("_Digests");
        static readonly int[][] Orders={new[]{0,1,3,2},new[]{1,2,0,3},new[]{2,3,1,0},new[]{3,0,2,1}};
        void Awake()
        {
            Application.runInBackground=true;Application.targetFrameRate=-1;QualitySettings.vSyncCount=0;
            var args=Environment.GetCommandLineArgs();int at=Array.IndexOf(args,"-integration-config");
            if(at<0||at+1>=args.Length)throw new Exception("Provide -integration-config JSON path");
            config=JsonUtility.FromJson<IntegrationConfig>(File.ReadAllText(args[at+1]));
            if(config.frames<384||config.frames>1024||config.warmup<0||config.warmup>=config.frames||config.blocks<1||config.blocks>4)throw new Exception("Invalid bounded config");
            Directory.CreateDirectory(config.output);
            result=new IntegrationResult{processId=Process.GetCurrentProcess().Id,startedUtc=DateTime.UtcNow.ToString("O"),
                config=config,buildGuid=Application.buildGUID,unityVersion=Application.unityVersion,device=SystemInfo.graphicsDeviceName,
                graphicsApi=SystemInfo.graphicsDeviceType.ToString(),development=UnityEngine.Debug.isDebugBuild,
                formalPerformanceEvidence=config.mode=="formal"&&!UnityEngine.Debug.isDebugBuild&&!Application.isEditor,
                engineCpuTimerFrequency=FrameTimingManager.GetCpuTimerFrequency()};
            if(!QueryPerformanceFrequency(out result.qpcFrequency))throw new Exception("QPC frequency unavailable");
            result.stopwatchFrequency=Stopwatch.Frequency;
            Save();lastProcessFrame=Time.realtimeSinceStartupAsDouble;
        }
        IEnumerator Start()
        {
            var routine=Run();
            while(true)
            {
                bool next;
                try{next=routine.MoveNext();}
                catch(Exception e){result.status="failed";result.error=e.ToString();Save();UnityEngine.Debug.LogException(e);Application.Quit(2);yield break;}
                if(!next)break;yield return routine.Current;
            }
            result.status="completed";result.endedUtc=DateTime.UtcNow.ToString("O");Save();Application.Quit(0);
        }
        void Update()
        {
            if(result==null)return;
            double now=Time.realtimeSinceStartupAsDouble;
            result.processFrames.Add(new ProcessFrame{unityFrame=Time.frameCount,qpc=Qpc(),engineIntervalMs=(now-lastProcessFrame)*1000,phase=phase});
            lastProcessFrame=now;
            FrameTimingManager.CaptureFrameTimings();
            uint count=FrameTimingManager.GetLatestTimings((uint)frameTimings.Length,frameTimings);
            for(int i=0;i<count;i++)
            {
                var t=frameTimings[i];if(t.frameStartTimestamp==0||!seenEngineFrames.Add(t.frameStartTimestamp))continue;
                result.engineTimings.Add(new EngineTiming{frameStartTimestamp=t.frameStartTimestamp,cpuTimePresentCalled=t.cpuTimePresentCalled,
                    cpuTimeFrameComplete=t.cpuTimeFrameComplete,cpuFrameMs=t.cpuFrameTime,mainThreadMs=t.cpuMainThreadFrameTime,
                    renderThreadMs=t.cpuRenderThreadFrameTime,presentWaitMs=t.cpuMainThreadPresentWaitTime,gpuFrameMs=t.gpuFrameTime,observedUnityFrame=Time.frameCount});
            }
        }
        IEnumerator Run()
        {
            string[] known={"old-full","new-full","old-incremental","new-incremental"};
            if(config.arms==null||config.arms.Length==0||config.arms.Distinct().Count()!=config.arms.Length||config.arms.Any(a=>!known.Contains(a)))throw new Exception("Invalid nonempty arm array");
            if(config.mode!="oracle"&&config.mode!="validate"&&config.mode!="formal")throw new Exception("Unknown run mode");
            if(config.mode=="formal"&&!result.formalPerformanceEvidence)throw new Exception("Formal evidence requires a non-Development standalone Player");
            if(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D12)throw new Exception("D3D12 required");
            if(!GpuTimestampSession.TryCreate(out timestamps,out var support))throw new Exception("Native timestamp support: "+support.Message);
            material=new Material(Resources.Load<Shader>("IntegrationParticles"));
            historyShader=Resources.Load<ComputeShader>("IntegrationHistory");historyKernel=historyShader.FindKernel("StoreHistory");
            cameraView=new GameObject("Deterministic Camera").AddComponent<Camera>();cameraView.clearFlags=CameraClearFlags.SolidColor;
            cameraView.backgroundColor=new Color(0.015f,0.025f,0.055f);cameraView.nearClipPlane=0.03f;cameraView.farClipPlane=250;
            using(var initialize=new CommandBuffer()){timestamps.RecordFrequencyInitialization(initialize);Graphics.ExecuteCommandBuffer(initialize);}
            yield return null;
            for(int block=0;block<config.blocks;block++)
            {
                int[] order=config.arms.Length==4?Orders[(block+config.processReplicate)%4]:Enumerable.Range(0,config.arms.Length).ToArray();
                for(int p=0;p<order.Length;p++)
                {
                    var armRoutine=RunArm(config.arms[order[p]],block,p);
                    while(armRoutine.MoveNext())yield return armRoutine.Current;
                }
            }
            timestamps.Dispose();timestamps=null;
            phase="engine-timing-drain";for(int i=0;i<8;i++)yield return null;
        }
        IEnumerator RunArm(string arm,int block,int position)
        {
            phase="setup:"+arm;double setupAt=Time.realtimeSinceStartupAsDouble;
            bool incremental=arm.EndsWith("incremental",StringComparison.Ordinal);
            var backend=GpuSensorQueryBackend.CellSerial;
            if(arm.StartsWith("new",StringComparison.Ordinal)&&!Enum.TryParse("BatchedPointScanWave",out backend))throw new Exception("Improved query API has not been integrated");
            var report=new IntegrationArm{arm=arm,block=block,position=position,startedUtc=DateTime.UtcNow.ToString("O"),
                queryBackend=backend.ToString(),indexBackend=incremental?"incremental-gpu-driven":"full-direct-waveops",frameCount=config.frames,frames=new IntegrationFrame[config.frames],
                allocatedBefore=Profiler.GetTotalAllocatedMemoryLong()};
            for(int i=0;i<report.frames.Length;i++)report.frames[i]=new IntegrationFrame{frame=i,measured=i>=config.warmup,
                indexRecordedDispatches=incremental?13:11,queryRecordedDispatches=arm.StartsWith("new",StringComparison.Ordinal)?3:2};
            result.runs.Add(report);
            var data=IntegrationFixture.Create(config.scenario,config.seed);var active=new uint[N];
            for(int i=0;i<N;i++)active[i]=config.scenario==IntegrationFixture.Scenarios[2]&&i>=N-2*IntegrationFixture.ContentSlots?0u:1u;
            var content=new IntegrationContent(e=>report.contentEvents.Add(e));
            uint activeCount=(uint)(config.scenario==IntegrationFixture.Scenarios[2]?N-2*IntegrationFixture.ContentSlots:N);
            var expected=config.mode=="oracle"?new GpuSensorQueryDigest[config.frames*10]:ReadHistory(config.oracle);
            if(expected.Length!=config.frames*10)throw new Exception("Oracle trajectory length mismatch");
            using(var input=new GraphicsBuffer(GraphicsBuffer.Target.Structured,N,16))
            using(var flags=new GraphicsBuffer(GraphicsBuffer.Target.Structured,N,4))
            using(var history=new GraphicsBuffer(GraphicsBuffer.Target.Structured,config.frames*10,16))
            using(var index=incremental?null:new GpuSensorFullRebuildIndex(N,GpuPrimitiveBackend.WaveOps))
            using(var updated=incremental?new GpuSensorIncrementalIndex(N,0,executionMode:GpuSensorIndexExecutionMode.GpuDriven):null)
            using(var consumer=new Pipeline(N,9,GpuPrimitiveBackend.Portable,false,queryBackend:backend,queryIndexEntryCapacity:N*3))
            using(var commands=new CommandBuffer{name="PublicIntegration/Frame"})
            {
                consumer.SetQueries(IntegrationFixture.Queries());history.SetData(new GpuSensorQueryDigest[config.frames*10]);
                input.SetData(data);flags.SetData(active);
                var properties=new MaterialPropertyBlock();properties.SetBuffer(SamplesId,input);properties.SetBuffer(ActiveId,flags);properties.SetBuffer(DigestsId,consumer.QueryDigests);
                report.residentBytes=(incremental?updated.ResidentBytes:index.ResidentBytes)+consumer.ResidentBytes+(long)N*20+(long)config.frames*160;
                cameraView.AddCommandBuffer(CameraEvent.BeforeForwardOpaque,commands);
                report.setupMilliseconds=(Time.realtimeSinceStartupAsDouble-setupAt)*1000;
                for(int frame=0;frame<config.frames;frame++)
                {
                    phase=arm+":frame-"+frame;var row=report.frames[frame];row.unityFrame=Time.frameCount;row.cpuStartTicks=Stopwatch.GetTimestamp();row.qpcStart=Qpc();
                    double frameStart=Time.realtimeSinceStartupAsDouble;PollNative();
                    long allocation=GC.GetAllocatedBytesForCurrentThread();long recordBegin=Stopwatch.GetTimestamp();
                    row.changedSlots=IntegrationFixture.Advance(config.scenario,data,active,frame);
                    if(config.scenario==IntegrationFixture.Scenarios[2]){row.changedSlots+=content.Advance(frame,data,active,config.seed);activeCount=(uint)((int)activeCount+content.ActiveDelta);}
                    row.activeCount=(int)activeCount;
                    if(row.changedSlots>0){input.SetData(data);flags.SetData(active);row.uploadedBytes=(long)N*20;}
                    if(config.mode=="oracle")
                    {
                        var oracle=IntegrationFixture.Oracle(data,active);Array.Copy(oracle,0,expected,frame*10,9);
                        if(oracle[0].Count!=activeCount)throw new Exception("Cached live count disagrees with independent oracle");
                        expected[frame*10+9]=new GpuSensorQueryDigest((uint)frame,activeCount,N,(uint)row.changedSlots);
                    }
                    float angle=(float)frame/config.frames*Mathf.PI*0.65f;
                    float radius=config.scenario==IntegrationFixture.Scenarios[1]?5:100;
                    cameraView.transform.position=new Vector3(Mathf.Sin(angle)*radius,radius*0.35f,Mathf.Cos(angle)*radius);
                    cameraView.transform.LookAt(Vector3.zero);
                    properties.SetTexture("_Palette",content.CurrentTexture);
                    commands.Clear();var whole=Begin(commands,row.sceneGpu);commands.ClearRenderTarget(true,true,cameraView.backgroundColor);
                    var indexToken=Begin(commands,row.indexGpu);
                    if(incremental)updated.RecordUpdate(commands,input,flags);else index.RecordUpdate(commands,input,flags);
                    End(commands,indexToken);
                    var queryToken=Begin(commands,row.queryGpu);
                    consumer.RecordExternalIndexQueries(commands,incremental?updated.Samples:input,
                        incremental?updated.BinOffsets:index.BinOffsets,incremental?updated.BinnedIds:index.BinnedIds,N,9,0);End(commands,queryToken);
                    commands.SetComputeBufferParam(historyShader,historyKernel,"_Digests",consumer.QueryDigests);
                    commands.SetComputeBufferParam(historyShader,historyKernel,"_History",history);
                    commands.SetComputeIntParam(historyShader,"_Frame",frame);commands.SetComputeIntParam(historyShader,"_ActiveCount",(int)activeCount);
                    commands.SetComputeIntParam(historyShader,"_DrawCount",N);commands.SetComputeIntParam(historyShader,"_UpdateCount",row.changedSlots);
                    commands.DispatchCompute(historyShader,historyKernel,1,1,1);
                    commands.DrawProcedural(Matrix4x4.identity,material,0,MeshTopology.Points,N,1,properties);
                    commands.DrawProcedural(Matrix4x4.identity,material,1,MeshTopology.Triangles,54,1,properties);End(commands,whole);
                    row.recordCpuMs=(Stopwatch.GetTimestamp()-recordBegin)*1000.0/Stopwatch.Frequency;
                    row.recordAllocatedBytes=GC.GetAllocatedBytesForCurrentThread()-allocation;
                    row.gc0=GC.CollectionCount(0);row.gc1=GC.CollectionCount(1);row.gc2=GC.CollectionCount(2);
                    yield return new WaitForEndOfFrame();
                    if(config.scenario==IntegrationFixture.Scenarios[2])content.MarkRendered(frame);
                    if(result.firstRenderedEngineMilliseconds<0)result.firstRenderedEngineMilliseconds=Time.realtimeSinceStartupAsDouble*1000;
                    if(config.screenshot&&config.mode!="formal"&&frame==config.frames-1)
                    {
                        var picture=ScreenCapture.CaptureScreenshotAsTexture();
                        File.WriteAllBytes(Path.Combine(config.output,config.scenario+".png"),picture.EncodeToPNG());Destroy(picture);
                    }
                    commands.Clear();yield return null;
                    row.engineCadenceMs=(Time.realtimeSinceStartupAsDouble-frameStart)*1000;
                    row.qpcEnd=Qpc();
                }
                cameraView.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque,commands);
                phase="asynchronous-verification-drain";double drain=Time.realtimeSinceStartupAsDouble;
                var request=AsyncGPUReadback.Request(history);
                while(!request.done||pending.Count>0)
                {
                    if(Time.realtimeSinceStartupAsDouble-drain>60)throw new Exception("Bounded asynchronous completion timeout");
                    PollNative();content.Poll(config.frames);yield return null;
                }
                if(request.hasError)throw new Exception("Asynchronous history readback failed");
                var actual=request.GetData<GpuSensorQueryDigest>().ToArray();
                WriteHistory(Path.Combine(config.output,$"{block}-{position}-{arm}.history.bin"),actual);
                if(!actual.SequenceEqual(expected))
                {
                    int bad=-1;for(int i=0;i<actual.Length;i++)if(!actual[i].Equals(expected[i])){bad=i;break;}
                    throw new Exception("Oracle/history mismatch at word "+bad+" (frame "+bad/10+")");
                }
                if(config.mode=="oracle")WriteHistory(config.oracle,expected);
                using(var sha=SHA256.Create())report.oracleSha256=BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(config.oracle))).Replace("-","").ToLowerInvariant();
                report.verified=true;report.verifiedDigestWords=actual.Length;report.asyncDrainMilliseconds=(Time.realtimeSinceStartupAsDouble-drain)*1000;
                content.BeginCleanup(config.frames);
                while(content.Pending>0){content.Poll(config.frames);if(Time.realtimeSinceStartupAsDouble-drain>60)throw new Exception("Content drain timeout");yield return null;}
                report.allocatedAfter=Profiler.GetTotalAllocatedMemoryLong();report.endedUtc=DateTime.UtcNow.ToString("O");
            }
            phase="arm-report";Save();yield return null;
        }
        GpuTimestampToken Begin(CommandBuffer commands,NativeTiming output)
        {
            var status=timestamps.Acquire(nextTag++,GpuTimestampSampleFlags.None,Time.frameCount,out var token);
            if(status!=GpuTimestampStatus.Ready)throw new Exception("Native timestamp acquisition: "+status);
            timestamps.GetScope(token).RecordBegin(commands);pending.Add(new PendingTiming{token=token,result=output});return token;
        }
        void End(CommandBuffer commands,GpuTimestampToken token)
        {timestamps.GetScope(token).RecordEnd(commands);if(timestamps.MarkSubmitted(token)!=GpuTimestampStatus.Ready)throw new Exception("Timestamp submit failed");}
        void PollNative()
        {
            for(int i=pending.Count-1;i>=0;i--)
            {
                var p=pending[i];var status=timestamps.TryConsume(p.token,Time.frameCount,out var timing);
                if(status==GpuTimestampStatus.Pending)continue;
                if(status!=GpuTimestampStatus.Ready)throw new Exception("Native GPU result: "+status);
                p.result.status="Ready";p.result.token=timing.Token.Value;p.result.beginTicks=timing.BeginTicks;p.result.endTicks=timing.EndTicks;
                p.result.frequency=timing.TimestampFrequency;p.result.milliseconds=timing.ElapsedMilliseconds;p.result.sourceFrame=timing.SourceFrame;p.result.resultFrame=timing.ResultFrame;
                pending.RemoveAt(i);
            }
        }
        static void WriteHistory(string path,GpuSensorQueryDigest[] values)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            using(var writer=new BinaryWriter(File.Create(path)))foreach(var v in values){writer.Write(v.Count);writer.Write(v.XorHash);writer.Write(v.SumHash0);writer.Write(v.SumHash1);}
        }
        static GpuSensorQueryDigest[] ReadHistory(string path)
        {
            using(var reader=new BinaryReader(File.OpenRead(path)))
            {var values=new GpuSensorQueryDigest[reader.BaseStream.Length/16];for(int i=0;i<values.Length;i++)values[i]=new GpuSensorQueryDigest(reader.ReadUInt32(),reader.ReadUInt32(),reader.ReadUInt32(),reader.ReadUInt32());return values;}
        }
        void Save(){if(result!=null)File.WriteAllText(Path.Combine(config.output,"result.json"),JsonUtility.ToJson(result,true));}
        void OnDestroy(){timestamps?.Dispose();if(material!=null)Destroy(material);}
    }
}
