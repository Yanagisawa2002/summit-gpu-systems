using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Summit.GpuPrimitives;
using Summit.GpuSensorPipeline;
using UnityEngine;
using UnityEngine.Rendering;
using Pipeline=Summit.GpuSensorPipeline.GpuSensorPipeline;

namespace Summit.PublicIntegration
{
    [Serializable] public sealed class QueueConfig
    {
        public string arm="old-full",output,oracle,sourceSha;
        public string observationDeviceDriverId="unknown";
        public uint seed=928201;
        public int replicate;
    }
    [Serializable] public sealed class QueueJob
    {
        public int job,changedSlots,queryCount=9,activeCount=262144,readbackBytes=160;
        public long uploadedBytes,submitTicks,readbackObservedTicks,verifiedTicks;
        public double arrivalMs,submitMs,readbackObservedMs,resultReadyMs,latencyMs;
        public bool verified;
        public int sourceFrame=-1,observedFrame=-1;
    }
    [Serializable] public struct QueueObservation
    {
        public long ticks;public double elapsedMs,lastLatencyMs;
        public int unityFrame,released,submitted,completed,outstanding;
        public bool focused;
    }
    [Serializable] public sealed class QueueResult
    {
        public int schemaVersion=1,jobCount=384,arrivalRate=60,maxInFlight=1,warmupJobs=8,processId;
        public string status="preparing",error,device,graphicsApi,unityVersion,buildGuid,oracleSha256,startedUtc,endedUtc;
        public string completionScope="GPU result readback observed on host and 10 digest/metadata records verified; not exact GPU completion timestamp";
        public string latencyScope="Scheduled task release to verified result available; includes queue, CPU preparation, GPU execution, async 160-byte readback and host polling";
        public string arrivalScope="Predeclared finite synthetic producer: job i released at i/60 seconds; descriptors retained even if host is delayed";
        public bool development,verified,osPresentationAvailable=false,nativeGpuTimingAvailable=false;
        public long clockFrequency,startTicks,firstMarkerRepaintTicks,finishTicks;
        public double finishMs=-1;public int completed,maxOutstanding;
        public QueueConfig config;public QueueJob[] jobs=new QueueJob[384];
        public List<QueueObservation> observations=new List<QueueObservation>(131072);
    }

    // A separate finite producer/consumer experiment. Existing formal and paced
    // IntegrationPlayer routes do not execute this component.
    public sealed class QueueLatencyPlayer:MonoBehaviour
    {
        const int N=IntegrationFixture.Capacity,Count=384;
        QueueConfig config;QueueResult report;GpuSensorSample[] samples;uint[] active;
        GpuSensorQueryDigest[] expected,actual=new GpuSensorQueryDigest[Count*10];
        GraphicsBuffer input,flags,history;GpuSensorFullRebuildIndex index;Pipeline query;
        ComputeShader historyShader;int historyKernel;CommandBuffer jobCommands,drawCommands;
        Camera cameraView;Material material;AsyncGPUReadbackRequest request;
        bool pending,prepared,started,finished,failed;int submitted,completed,startFrame;
        double readyAt,finishHoldAt,lastLatency;long startTicks;
        GUIStyle title,label,small,number;Font font;
        static long Tick()=>Stopwatch.GetTimestamp();
        double Ms(long tick)=>(tick-startTicks)*1000.0/Stopwatch.Frequency;
        int Released(double ms)=>Math.Min(Count,Math.Max(0,(int)Math.Floor(ms*60/1000)+1));
        void Awake()
        {
            try
            {
                int at=Array.IndexOf(Environment.GetCommandLineArgs(),"-queue-config");
                config=JsonUtility.FromJson<QueueConfig>(File.ReadAllText(Environment.GetCommandLineArgs()[at+1]));
                if(config.arm!="old-full"&&config.arm!="new-full")throw new Exception("Unknown full-rebuild query arm");
                if(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D12||!SystemInfo.supportsAsyncGPUReadback)
                    throw new Exception("D3D12 with async GPU readback required");
                if(UnityEngine.Debug.isDebugBuild||Application.isEditor)throw new Exception("Release standalone required");
                Application.runInBackground=true;Application.targetFrameRate=-1;QualitySettings.vSyncCount=0;
                Directory.CreateDirectory(config.output);
                report=new QueueResult{config=config,processId=Process.GetCurrentProcess().Id,device=SystemInfo.graphicsDeviceName,
                    graphicsApi=SystemInfo.graphicsDeviceType.ToString(),unityVersion=Application.unityVersion,buildGuid=Application.buildGUID,
                    development=UnityEngine.Debug.isDebugBuild,clockFrequency=Stopwatch.Frequency};
                expected=ReadHistory(config.oracle);
                if(expected.Length!=Count*10)throw new Exception("384-frame original oracle required");
                using(var sha=SHA256.Create())report.oracleSha256=BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(config.oracle))).Replace("-","").ToLowerInvariant();
                for(int i=0;i<Count;i++)report.jobs[i]=new QueueJob{job=i,arrivalMs=i*1000.0/60};
                font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                title=Style(36,FontStyle.Bold);label=Style(25);small=Style(19);number=Style(42,FontStyle.Bold);
                Save();
            }
            catch(Exception e){Fail(e);}
        }
        GUIStyle Style(int size,FontStyle style=FontStyle.Normal)=>new GUIStyle{font=font,fontSize=size,fontStyle=style,normal={textColor=Color.white}};
        IEnumerator Start()
        {
            if(failed)yield break;
            IEnumerator routine=Prepare();
            while(true)
            {
                bool more;
                try{more=routine.MoveNext();}catch(Exception e){Fail(e);yield break;}
                if(!more)break;yield return routine.Current;
            }
            prepared=true;readyAt=Time.realtimeSinceStartupAsDouble;report.status="ready";Save();
            File.WriteAllText(Path.Combine(config.output,"capture-ready.txt"),DateTime.UtcNow.ToString("O"));
        }
        IEnumerator Prepare()
        {
            samples=IntegrationFixture.Create("hotspot-dynamic",config.seed);active=new uint[N];
            for(int i=0;i<N;i++)active[i]=1;
            input=new GraphicsBuffer(GraphicsBuffer.Target.Structured,N,16);
            flags=new GraphicsBuffer(GraphicsBuffer.Target.Structured,N,4);
            history=new GraphicsBuffer(GraphicsBuffer.Target.Structured,Count*10,16);
            input.SetData(samples);flags.SetData(active);history.SetData(actual);
            index=new GpuSensorFullRebuildIndex(N,GpuPrimitiveBackend.WaveOps);
            var backend=config.arm=="old-full"?GpuSensorQueryBackend.CellSerial:GpuSensorQueryBackend.BatchedPointScanWave;
            query=new Pipeline(N,9,GpuPrimitiveBackend.Portable,false,queryBackend:backend,queryIndexEntryCapacity:N*3);
            query.SetQueries(IntegrationFixture.Queries());
            historyShader=Resources.Load<ComputeShader>("IntegrationHistory");historyKernel=historyShader.FindKernel("StoreHistory");
            jobCommands=new CommandBuffer{name="QueueLatency/OneJob"};
            material=new Material(Resources.Load<Shader>("IntegrationParticles"));
            var background=new GameObject("Queue latency background").AddComponent<Camera>();
            background.clearFlags=CameraClearFlags.SolidColor;background.backgroundColor=new Color(.025f,.04f,.065f);background.cullingMask=0;background.depth=-1;
            cameraView=new GameObject("Queue latency point cloud").AddComponent<Camera>();
            cameraView.clearFlags=CameraClearFlags.SolidColor;cameraView.backgroundColor=new Color(.025f,.04f,.065f);
            cameraView.rect=new Rect(.025f,.47f,.43f,.34f);cameraView.nearClipPlane=.03f;cameraView.farClipPlane=250;
            cameraView.transform.position=new Vector3(0,1.75f,5);cameraView.transform.LookAt(Vector3.zero);
            var props=new MaterialPropertyBlock();props.SetBuffer("_Samples",input);props.SetBuffer("_Active",flags);props.SetBuffer("_Digests",query.QueryDigests);props.SetTexture("_Palette",Texture2D.whiteTexture);
            drawCommands=new CommandBuffer{name="QueueLatency/VisualConsumer"};
            drawCommands.DrawProcedural(Matrix4x4.identity,material,0,MeshTopology.Points,N,1,props);
            cameraView.AddCommandBuffer(CameraEvent.BeforeForwardOpaque,drawCommands);
            // Same explicit warmup in both arms. No workload job counts advance.
            for(int w=0;w<8;w++)
            {
                RecordJob(0,0);Graphics.ExecuteCommandBuffer(jobCommands);
                var warm=AsyncGPUReadback.Request(history,160,0);
                double begin=Time.realtimeSinceStartupAsDouble;
                while(!warm.done){if(Time.realtimeSinceStartupAsDouble-begin>30)throw new Exception("Warmup timeout");yield return null;}
                if(warm.hasError)throw new Exception("Warmup readback failed");
                var data=warm.GetData<GpuSensorQueryDigest>();
                for(int i=0;i<10;i++)if(!data[i].Equals(expected[i]))throw new Exception("Warmup oracle mismatch");
            }
        }
        void RecordJob(int job,int changed)
        {
            jobCommands.Clear();index.RecordUpdate(jobCommands,input,flags);
            query.RecordExternalIndexQueries(jobCommands,input,index.BinOffsets,index.BinnedIds,N,9,0);
            jobCommands.SetComputeBufferParam(historyShader,historyKernel,"_Digests",query.QueryDigests);
            jobCommands.SetComputeBufferParam(historyShader,historyKernel,"_History",history);
            jobCommands.SetComputeIntParam(historyShader,"_Frame",job);
            jobCommands.SetComputeIntParam(historyShader,"_ActiveCount",N);
            jobCommands.SetComputeIntParam(historyShader,"_DrawCount",N);
            jobCommands.SetComputeIntParam(historyShader,"_UpdateCount",changed);
            jobCommands.DispatchCompute(historyShader,historyKernel,1,1,1);
        }
        void Update()
        {
            if(failed||!prepared)return;
            try{Step();}catch(Exception e){Fail(e);}
        }
        void Step()
        {
            if(!started)
            {
                if(!File.Exists(Path.Combine(config.output,"capture-start.signal")))
                {if(Time.realtimeSinceStartupAsDouble-readyAt>60)throw new Exception("Start handshake timed out");return;}
                // The marker gets one repaint before the first workload submission.
                started=true;startTicks=Tick();startFrame=Time.frameCount;report.startTicks=startTicks;
                report.startedUtc=DateTime.UtcNow.ToString("O");report.status="running";
                return;
            }
            if(Time.frameCount<=startFrame)return;
            double elapsed=Ms(Tick());
            if(!finished&&elapsed>90000)throw new Exception("Fixed task deadline: 90 seconds");
            if(pending&&request.done)
            {
                var row=report.jobs[completed];row.readbackObservedTicks=Tick();row.readbackObservedMs=Ms(row.readbackObservedTicks);
                if(request.hasError)throw new Exception("Workload async readback failed");
                var data=request.GetData<GpuSensorQueryDigest>();
                if(data.Length!=10)throw new Exception("Readback shape mismatch");
                for(int j=0;j<10;j++)
                {
                    if(!data[j].Equals(expected[completed*10+j]))throw new Exception("Oracle mismatch job "+completed+" digest "+j);
                    actual[completed*10+j]=data[j];
                }
                row.verifiedTicks=Tick();row.resultReadyMs=Ms(row.verifiedTicks);row.latencyMs=row.resultReadyMs-row.arrivalMs;row.verified=true;row.observedFrame=Time.frameCount;
                lastLatency=row.latencyMs;pending=false;completed++;report.completed=completed;
                if(completed==Count)
                {
                    finished=true;report.finishTicks=row.verifiedTicks;report.finishMs=row.resultReadyMs;
                    report.verified=true;report.status="completed";report.endedUtc=DateTime.UtcNow.ToString("O");
                    WriteHistory(Path.Combine(config.output,"actual.history.bin"),actual);
                    Save();File.WriteAllText(Path.Combine(config.output,"work-completed.signal"),report.endedUtc);
                    finishHoldAt=Time.realtimeSinceStartupAsDouble;
                }
            }
            elapsed=Ms(Tick());int released=Released(elapsed);
            if(!finished&&!pending&&submitted<released)
            {
                var row=report.jobs[submitted];row.changedSlots=IntegrationFixture.Advance("hotspot-dynamic",samples,active,submitted);
                if(row.changedSlots>0){input.SetData(samples);flags.SetData(active);row.uploadedBytes=(long)N*20;}
                RecordJob(submitted,row.changedSlots);
                row.sourceFrame=Time.frameCount;row.submitTicks=Tick();row.submitMs=Ms(row.submitTicks);
                Graphics.ExecuteCommandBuffer(jobCommands);
                request=AsyncGPUReadback.Request(history,160,submitted*160);
                pending=true;submitted++;
            }
            int outstanding=released-completed;report.maxOutstanding=Math.Max(report.maxOutstanding,outstanding);
            report.observations.Add(new QueueObservation{ticks=Tick(),elapsedMs=elapsed,unityFrame=Time.frameCount,released=released,
                submitted=submitted,completed=completed,outstanding=outstanding,lastLatencyMs=lastLatency,focused=Application.isFocused});
            if(report.observations.Count>131000)throw new Exception("Observation storage bound exceeded");
            if(finished&&(File.Exists(Path.Combine(config.output,"capture-stop.signal"))||Time.realtimeSinceStartupAsDouble-finishHoldAt>60))
            {Save();Application.Quit(0);}
        }
        void OnGUI()
        {
            if(report==null||title==null||Event.current.type!=EventType.Repaint)return;
            GUI.matrix=Matrix4x4.Scale(new Vector3(Screen.width/1280f,Screen.height/720f,1));
            // Exactly one persistent white marker transition identifies task start.
            Box(new Rect(1224,8,40,24),started?Color.white:Color.black);
            if(started&&report.firstMarkerRepaintTicks==0)report.firstMarkerRepaintTicks=Tick();
            Color accent=config.arm=="old-full"?new Color(.35f,.66f,1):new Color(.98f,.65f,.27f);
            Text(28,18,(config.arm=="old-full"?"BASELINE  |  CellSerial":"CANDIDATE  |  BatchedPointScanWave"),title,accent);
            Text(28,66,"384 jobs  |  262,144 points  |  9 queries  |  60 scheduled jobs/s",label);
            Text(28,104,"Same full index rebuild. One in-flight job. No dropped or merged tasks.",small);
            double now=started?Ms(Tick()):0;int released=started?Released(now):0;
            Text(606,146,"GPU RESULT VERIFIED",label);
            Text(606,180,$"{completed:000} / 384",number,accent);
            Text(606,240,$"Outstanding: {released-completed:000}",label);
            Text(606,277,$"Queued: {released-submitted:000}   In flight: {submitted-completed}",label);
            Text(28,385,$"Real elapsed: {now/1000:0.000} s",label);
            Text(606,320,$"Last result latency: {lastLatency:0.00} ms",label);
            Text(606,359,finished?$"All results ready: {report.finishMs/1000:0.000} s":"All results ready: --",label,accent);
            Text(28,432,"TASKS   dark = future   orange = waiting   yellow = submitted   cyan = verified",small);
            for(int i=0;i<Count;i++)
            {
                Color c=i<completed?new Color(.2f,.85f,.85f):i<submitted?new Color(1,.85f,.25f):i<released?new Color(.9f,.43f,.15f):new Color(.16f,.2f,.26f);
                Box(new Rect(28+(i%32)*38,468+(i/32)*14,33,10),c);
            }
            Text(28,647,failed?"FAILED - no performance claim":finished?"ALL GPU RESULT DIGESTS MATCH ORIGINAL CPU ORACLE":prepared?started?"RUNNING - progress advances only after verified async GPU readback":"READY - waiting for task-start signal":"WARMUP - not counted as completed work",small,accent);
            Text(28,679,"Latency = scheduled release to verified readback on host. Task delivery, not display FPS.",small);
        }
        void Text(float x,float y,string s,GUIStyle style,Color? color=null)
        {GUI.color=color??Color.white;GUI.Label(new Rect(x,y,1220-x,60),s,style);GUI.color=Color.white;}
        void Box(Rect r,Color c){GUI.color=c;GUI.DrawTexture(r,Texture2D.whiteTexture);GUI.color=Color.white;}
        void Save(){if(report!=null){File.WriteAllText(Path.Combine(config.output,"result.json"),JsonUtility.ToJson(report,true));
            using(var writer=new StreamWriter(Path.Combine(config.output,"observations.csv")))Summit.GpuTimestamps.ObservationCsv.Write(writer,QueueObservations.Enumerate(report));}}
        void Fail(Exception e)
        {
            failed=true;if(report!=null){report.status="failed";report.error=e.ToString();Save();}
            UnityEngine.Debug.LogException(e);Application.Quit(2);
        }
        static GpuSensorQueryDigest[] ReadHistory(string path)
        {
            using(var r=new BinaryReader(File.OpenRead(path)))
            {var a=new GpuSensorQueryDigest[r.BaseStream.Length/16];for(int i=0;i<a.Length;i++)a[i]=new GpuSensorQueryDigest(r.ReadUInt32(),r.ReadUInt32(),r.ReadUInt32(),r.ReadUInt32());return a;}
        }
        static void WriteHistory(string path,GpuSensorQueryDigest[] values)
        {using(var w=new BinaryWriter(File.Create(path)))foreach(var x in values){w.Write(x.Count);w.Write(x.XorHash);w.Write(x.SumHash0);w.Write(x.SumHash1);}}
        void OnDestroy()
        {
            // Resource cleanup only, never a per-job wait in the measured path.
            if(pending)request.WaitForCompletion();
            if(cameraView!=null&&drawCommands!=null)cameraView.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque,drawCommands);
            jobCommands?.Dispose();drawCommands?.Dispose();query?.Dispose();index?.Dispose();history?.Dispose();flags?.Dispose();input?.Dispose();
            if(material!=null)Destroy(material);
        }
    }
}
