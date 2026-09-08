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
    [Serializable] public sealed class BoundaryConfig
    {
        public string mode="boundary",arm="scan",distribution="uniform",output,sourceSha;
        public int replicate,blocks=3,repeats=16,jobs=128;
        public uint seed=928301;
        public bool corruptOracle;
    }
    [Serializable] public sealed class BoundarySample
    {
        public string workload,arm;
        public int block,order,queries,repeats;
        public double recordMs,submitToReadbackMs;
        public long startTicks,endTicks;
        public bool verified,focused;
        public uint[] counts;
    }
    [Serializable] public sealed class DispatchOrder
    {
        public int job,routeMask,expectedRouteMask;
        public double arrivalMs,prepareMs,uploadMs,recordMs,submitMs,resultReadyMs,decisionMs,renderedDecisionMs=-1,deliveredMs=-1;
        public long submitTicks,readbackTicks,decisionTicks;
        public bool verified;
        public uint[] counts;
    }
    [Serializable] public sealed class BoundaryReport
    {
        public string status="preparing",error,device,api,unity,startedUtc,endedUtc;
        public string boundaryScope="Host-observed submit-to-readback time for 16 repeated query batches on a prebuilt index; no CPU upload or index build in this interval; not a hardware GPU timestamp";
        public string businessScope="Sensor snapshot arrival to route application, first Unity end-of-frame and simulated transport completion; includes CPU update/upload and required index build; not physical display latency";
        public BoundaryConfig config;
        public bool development,osPresentationAvailable=false,verified;
        public long frequency,startTicks;
        public int completed,delivered,unfocusedUpdates,updates,validationCases;
        public uint[] thresholds;
        public double finishMs,transportFinishMs;
        public List<BoundarySample> samples=new List<BoundarySample>();
        public List<DispatchOrder> orders=new List<DispatchOrder>();
    }
    public sealed class QueryBoundaryPlayer:MonoBehaviour
    {
        BoundaryConfig config;BoundaryReport report;
        GpuSensorSample[] samples;uint[] active,threshold;
        GpuSensorRangeQuery[] queries;
        GpuSensorQueryDigest[][] oracle;
        GpuSensorQueryDigest[] actualBusiness;
        GraphicsBuffer input,flags,queryBuffer,scanOutput;
        GpuSensorFullRebuildIndex index;
        GpuSensorParallelScanQuery scan;
        readonly Dictionary<string,Pipeline> pipelines=new Dictionary<string,Pipeline>();
        CommandBuffer commands;
        AsyncGPUReadbackRequest request;bool pending,failed,ready,businessStarted,done;
        int submitted,completed,delivered;long epoch;
        double[,] vehicleProgress;
        GUIStyle title,label,small;Font font;
        readonly WaitForEndOfFrame eof=new WaitForEndOfFrame();
        static long Tick()=>Stopwatch.GetTimestamp();
        double Ms(long t)=>(t-epoch)*1000.0/Stopwatch.Frequency;
        double Now=>Ms(Tick());
        void Awake()
        {
            try {
                var args=Environment.GetCommandLineArgs();int at=Array.IndexOf(args,"-boundary-config");
                config=JsonUtility.FromJson<BoundaryConfig>(File.ReadAllText(args[at+1]));
                if(config.mode!="validate"&&config.mode!="boundary"&&config.mode!="business")throw new ArgumentException("Unknown mode");
                if(Array.IndexOf(QueryBoundaryFixture.Arms,config.arm)<0||config.repeats!=16||config.blocks!=3||config.jobs!=128)throw new ArgumentException("Frozen protocol required");
                if(UnityEngine.Debug.isDebugBuild||Application.isEditor||SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D12||!SystemInfo.supportsAsyncGPUReadback)throw new Exception("Release D3D12 async readback required");
                Application.runInBackground=true;Application.targetFrameRate=-1;QualitySettings.vSyncCount=0;
                Directory.CreateDirectory(config.output);
                report=new BoundaryReport{config=config,device=SystemInfo.graphicsDeviceName,api=SystemInfo.graphicsDeviceType.ToString(),unity=Application.unityVersion,frequency=Stopwatch.Frequency};
                font=Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                title=Style(32);label=Style(23);small=Style(18);
                var camera=new GameObject("Dispatch dashboard").AddComponent<Camera>();camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.025f,.04f,.065f);camera.cullingMask=0;
                Save();
            } catch(Exception e) { Fail(e); }
        }
        GUIStyle Style(int size)=>new GUIStyle{font=font,fontSize=size,normal={textColor=Color.white}};
        IEnumerator Start()
        {
            if(failed)yield break;
            var routine=Run();
            while(true) { bool more;try{more=routine.MoveNext();}catch(Exception e){Fail(e);yield break;}if(!more)break;yield return routine.Current; }
        }
        IEnumerator Run()
        {
            if(config.mode=="validate") {
                var v=Validate();while(v.MoveNext())yield return v.Current;
                report.verified=true;Finish();yield break;
            }
            Allocate(QueryBoundaryFixture.N);
            if(config.mode=="boundary") {
                var b=Boundary();while(b.MoveNext())yield return b.Current;
                report.verified=true;Finish();yield break;
            }
            PrepareBusiness();
            // Equal warmup: four complete service submissions, including index
            // only for algorithms that require one. Never counted as orders.
            for(int i=0;i<4;i++) {
                commands.Clear();RecordService(config.arm);Graphics.ExecuteCommandBuffer(commands);
                request=AsyncGPUReadback.Request(Output(config.arm));pending=true;
                while(!request.done)yield return null;
                Check(request,oracle[0]);pending=false;
            }
            ready=true;report.status="ready";Save();File.WriteAllText(Path.Combine(config.output,"ready.signal"),"ready");
            double deadline=Time.realtimeSinceStartupAsDouble+90;
            while(!File.Exists(Path.Combine(config.output,"start.signal"))) {if(Time.realtimeSinceStartupAsDouble>deadline)throw new Exception("Start handshake timeout");yield return null;}
            epoch=Tick();report.startTicks=epoch;report.startedUtc=DateTime.UtcNow.ToString("O");report.status="running";businessStarted=true;
            while(!done&&!failed) {
                yield return eof;
                double now=Now;
                foreach(var order in report.orders)if(order.verified&&order.renderedDecisionMs<0)order.renderedDecisionMs=now;
            }
            if(failed)yield break;
            report.verified=true;Finish();
        }
        void Allocate(int n)
        {
            input=new GraphicsBuffer(GraphicsBuffer.Target.Structured,n,16);flags=new GraphicsBuffer(GraphicsBuffer.Target.Structured,n,4);
            queryBuffer=new GraphicsBuffer(GraphicsBuffer.Target.Structured,9,16);scanOutput=new GraphicsBuffer(GraphicsBuffer.Target.Structured,9,16);
            scan=new GpuSensorParallelScanQuery(n);index=new GpuSensorFullRebuildIndex(n,GpuPrimitiveBackend.WaveOps);
            commands=new CommandBuffer{name="QueryBoundary"};
            foreach(string arm in new[]{"cell","chunks","batch"}) {
                var backend=arm=="cell"?GpuSensorQueryBackend.CellSerial:arm=="chunks"?GpuSensorQueryBackend.PointChunksWave:GpuSensorQueryBackend.BatchedPointScanWave;
                pipelines[arm]=new Pipeline(n,9,GpuPrimitiveBackend.Portable,false,queryBackend:backend,queryIndexEntryCapacity:n*3);
            }
            active=new uint[n];for(int i=0;i<n;i++)active[i]=1;
        }
        void SetQueries(GpuSensorRangeQuery[] q)
        {
            queries=q;queryBuffer.SetData(q);
            foreach(var pipeline in pipelines.Values)pipeline.SetQueries(q);
        }
        GraphicsBuffer Output(string arm)=>arm=="scan"?scanOutput:pipelines[arm].QueryDigests;
        void RecordIndexed(string arm)=>pipelines[arm].RecordExternalIndexQueriesOnly(commands,input,index.BinOffsets,index.BinnedIds,samples.Length,queries.Length);
        void Query(string arm) { if(arm=="scan")scan.Record(commands,input,flags,queryBuffer,scanOutput,samples.Length,queries.Length);else RecordIndexed(arm); }
        void RecordService(string arm) { if(arm!="scan")index.RecordUpdate(commands,input,flags);Query(arm); }
        uint[] Check(AsyncGPUReadbackRequest req,GpuSensorQueryDigest[] expected)
        {
            if(req.hasError)throw new Exception("GPU readback failed");
            var data=req.GetData<GpuSensorQueryDigest>();var counts=new uint[expected.Length];
            for(int i=0;i<expected.Length;i++){if(!data[i].Equals(expected[i]))throw new Exception("GPU/oracle mismatch query "+i);counts[i]=data[i].Count;}
            return counts;
        }
        IEnumerator Boundary()
        {
            report.status="running";report.startedUtc=DateTime.UtcNow.ToString("O");
            for(int ci=0;ci<QueryBoundaryFixture.Cases.Length;ci++) {
                string name=QueryBoundaryFixture.Cases[(ci+config.replicate)%QueryBoundaryFixture.Cases.Length];
                samples=QueryBoundaryFixture.Samples(QueryBoundaryFixture.Distribution(name),config.seed);
                SetQueries(QueryBoundaryFixture.Queries(name));input.SetData(samples);flags.SetData(active);
                var expected=GpuSensorBenchmarkOracle.QueryAll(samples,samples.Length,queries,active);
                WriteDigests(name+"-oracle.bin",expected);
                commands.Clear();index.RecordUpdate(commands,input,flags);Graphics.ExecuteCommandBuffer(commands);
                request=AsyncGPUReadback.Request(index.BinOffsets);pending=true;while(!request.done)yield return null;if(request.hasError)throw new Exception("Index readback failed");pending=false;
                foreach(string arm in QueryBoundaryFixture.Arms) {
                    commands.Clear();for(int r=0;r<4;r++)Query(arm);Graphics.ExecuteCommandBuffer(commands);
                    request=AsyncGPUReadback.Request(Output(arm));pending=true;while(!request.done)yield return null;Check(request,expected);pending=false;
                }
                var captured=new List<GpuSensorQueryDigest>();
                for(int b=0;b<config.blocks;b++)for(int pos=0;pos<4;pos++) {
                    int offset=(config.replicate+b)%4;int ai=(offset+((config.replicate+b)%2==0?pos:3-pos))%4;
                    string arm=QueryBoundaryFixture.Arms[ai];
                    long begin=Tick();commands.Clear();for(int r=0;r<config.repeats;r++)Query(arm);
                    double recordMs=(Tick()-begin)*1000.0/Stopwatch.Frequency;
                    long start=Tick();Graphics.ExecuteCommandBuffer(commands);request=AsyncGPUReadback.Request(Output(arm));pending=true;
                    while(!request.done)yield return null;
                    long end=Tick();var counts=Check(request,expected);pending=false;
                    var data=request.GetData<GpuSensorQueryDigest>();for(int q=0;q<queries.Length;q++)captured.Add(data[q]);
                    report.samples.Add(new BoundarySample{workload=name,arm=arm,block=b,order=pos,queries=queries.Length,repeats=config.repeats,
                        recordMs=recordMs,startTicks=start,endTicks=end,submitToReadbackMs=(end-start)*1000.0/Stopwatch.Frequency,counts=counts,verified=true,focused=Application.isFocused});
                }
                WriteDigests(name+"-actual.bin",captured.ToArray());Save();
            }
        }
        IEnumerator Validate()
        {
            Allocate(513);SetQueries(GpuSensorBenchmarkFixtures.Queries());
            samples=QueryBoundaryFixture.Samples("hotspot",config.seed,513);
            for(int pass=0;pass<4;pass++) {
                for(int i=0;i<active.Length;i++)active[i]=pass==0?1u:pass==1?(i%3==0?0u:1u):pass==2?0u:(i==512?1u:0u);
                samples[512]=new GpuSensorSample(65535,65535,65535,uint.MaxValue);
                input.SetData(samples);flags.SetData(active);
                var expected=GpuSensorBenchmarkOracle.QueryAll(samples,513,queries,active);
                commands.Clear();index.RecordUpdate(commands,input,flags);Graphics.ExecuteCommandBuffer(commands);
                foreach(var arm in QueryBoundaryFixture.Arms) {
                    commands.Clear();Query(arm);Graphics.ExecuteCommandBuffer(commands);
                    request=AsyncGPUReadback.Request(Output(arm));pending=true;while(!request.done)yield return null;
                    Check(request,expected);pending=false;report.validationCases++;
                }
            }
            // Zero logical length must clear stale results without reading slots.
            commands.Clear();scan.Record(commands,input,flags,queryBuffer,scanOutput,0,queries.Length);Graphics.ExecuteCommandBuffer(commands);
            request=AsyncGPUReadback.Request(scanOutput);pending=true;while(!request.done)yield return null;
            Check(request,new GpuSensorQueryDigest[9]);pending=false;report.validationCases++;
            bool rejected=false;commands.Clear();try{scan.Record(commands,input,flags,queryBuffer,input,513,9);}catch(ArgumentException){rejected=true;}
            if(!rejected||commands.sizeInBytes!=0)throw new Exception("Aliasing not rejected before GPU commands");report.validationCases++;
        }
        void PrepareBusiness()
        {
            samples=QueryBoundaryFixture.Samples(config.distribution,config.seed);SetQueries(QueryBoundaryFixture.Zones());
            QueryBoundaryFixture.MoveCohort(samples,-1);
            var background=GpuSensorBenchmarkOracle.QueryAll(samples,samples.Length,queries,active);threshold=new uint[9];
            for(int q=0;q<9;q++)threshold[q]=background[q].Count+2048u;
            report.thresholds=threshold;actualBusiness=new GpuSensorQueryDigest[config.jobs*9];
            oracle=new GpuSensorQueryDigest[config.jobs][];vehicleProgress=new double[config.jobs,9];
            for(int j=0;j<config.jobs;j++) {
                QueryBoundaryFixture.MoveCohort(samples,j);oracle[j]=GpuSensorBenchmarkOracle.QueryAll(samples,samples.Length,queries,active);
                if(Mask(oracle[j])!=(1<<(j%9)))throw new Exception("Fixture must trigger exactly the visiting region");
                report.orders.Add(new DispatchOrder{job=j,arrivalMs=j*1000.0/30,expectedRouteMask=Mask(oracle[j])});
            }
            var flat=new List<GpuSensorQueryDigest>();foreach(var row in oracle)flat.AddRange(row);WriteDigests("business-oracle.bin",flat.ToArray());
            if(config.corruptOracle)oracle[1][0]=new GpuSensorQueryDigest(oracle[1][0].Count+1,0,0,0);
            QueryBoundaryFixture.MoveCohort(samples,0);input.SetData(samples);flags.SetData(active);
        }
        int Mask(GpuSensorQueryDigest[] values) {int mask=0;for(int q=0;q<9;q++)if(values[q].Count>threshold[q])mask|=1<<q;return mask;}
        void Update()
        {
            if(!businessStarted||done||failed)return;
            try{Step();}catch(Exception e){Fail(e);}
        }
        void Step()
        {
            report.updates++;if(!Application.isFocused)report.unfocusedUpdates++;
            if(Now>90000)throw new Exception("Business workload timeout");
            if(pending&&request.done) {
                var row=report.orders[completed];row.readbackTicks=Tick();row.resultReadyMs=Now;
                row.counts=Check(request,oracle[completed]);
                var returned=request.GetData<GpuSensorQueryDigest>();for(int q=0;q<9;q++)actualBusiness[completed*9+q]=returned[q];
                row.routeMask=0;for(int q=0;q<9;q++)if(row.counts[q]>threshold[q])row.routeMask|=1<<q;
                if(row.routeMask!=row.expectedRouteMask)throw new Exception("Route mismatch");
                row.verified=true;row.decisionTicks=Tick();row.decisionMs=Ms(row.decisionTicks);
                pending=false;completed++;report.completed=completed;
                if(completed==config.jobs)report.finishMs=row.decisionMs;
            }
            double now=Now;
            for(int j=0;j<completed;j++) {
                var row=report.orders[j];if(row.deliveredMs>=0)continue;
                bool arrived=true;
                for(int q=0;q<9;q++) {
                    // A transport cannot move until its own GPU-derived route is
                    // applied. Equal route lengths/speeds across all algorithms.
                    double duration=(row.routeMask&(1<<q))!=0?700:350;
                    vehicleProgress[j,q]=Math.Min(1,(now-row.decisionMs)/duration);
                    if(vehicleProgress[j,q]<1)arrived=false;
                }
                if(arrived){row.deliveredMs=now;delivered++;report.delivered=delivered;report.transportFinishMs=now;}
            }
            if(delivered==config.jobs) {done=true;return;}
            if(!pending&&submitted<config.jobs&&now>=report.orders[submitted].arrivalMs) {
                var row=report.orders[submitted];long t=Tick();QueryBoundaryFixture.MoveCohort(samples,submitted);row.prepareMs=(Tick()-t)*1000.0/Stopwatch.Frequency;
                t=Tick();input.SetData(samples);flags.SetData(active);row.uploadMs=(Tick()-t)*1000.0/Stopwatch.Frequency;
                t=Tick();commands.Clear();RecordService(config.arm);row.recordMs=(Tick()-t)*1000.0/Stopwatch.Frequency;
                row.submitTicks=Tick();row.submitMs=Ms(row.submitTicks);Graphics.ExecuteCommandBuffer(commands);
                request=AsyncGPUReadback.Request(Output(config.arm),queries.Length*16,0);pending=true;submitted++;
            }
        }
        void OnGUI()
        {
            if(report==null||title==null)return;
            GUI.matrix=Matrix4x4.Scale(new Vector3(Screen.width/1280f,Screen.height/720f,1));
            GUI.Label(new Rect(28,20,1220,60),"GPU occupancy -> route decision -> transport",title);
            GUI.Label(new Rect(28,74,1220,50),$"{config.mode} / {config.arm} / {config.distribution}   |   {report.status}",label);
            if(!businessStarted){GUI.Label(new Rect(28,126,1200,50),"Preparing independent correctness reference / query benchmark",label);return;}
            int due=Math.Min(config.jobs,(int)(Now*30/1000)+1);
            GUI.Label(new Rect(28,126,1220,40),$"Orders: {due} due   {completed} routed   {delivered} delivered   {due-completed} waiting for sensing",label);
            GUI.Label(new Rect(28,170,1220,35),"Red: occupied region / detour. Green: direct route. Carts wait for their own verified result.",small);
            for(int q=0;q<9;q++) {
                float y=226+q*46;
                Box(new Rect(240,y+10,900,3),Color.gray);
                GUI.Label(new Rect(28,y,200,38),"Region "+q,small);
                for(int j=Math.Max(0,due-18);j<due;j++) {
                    var row=report.orders[j];bool detour=row.verified&&(row.routeMask&(1<<q))!=0;
                    float progress=(float)vehicleProgress[j,q];float x=240+880*progress;
                    Box(new Rect(x,y+(detour?20:0)+(j%3)*3,12,9),!row.verified?Color.yellow:detour?new Color(1,.4f,.3f):Color.cyan);
                }
            }
            GUI.Label(new Rect(28,660,1220,40),"Simulated transport at original time. Engine-frame response measured; physical display timing unavailable.",small);
        }
        void Box(Rect rect,Color c){GUI.color=c;GUI.DrawTexture(rect,Texture2D.whiteTexture);GUI.color=Color.white;}
        void WriteDigests(string name,GpuSensorQueryDigest[] values)
        {
            using(var w=new BinaryWriter(File.Create(Path.Combine(config.output,name))))foreach(var v in values){w.Write(v.Count);w.Write(v.XorHash);w.Write(v.SumHash0);w.Write(v.SumHash1);}
        }
        void Save(){if(report!=null)File.WriteAllText(Path.Combine(config.output,"result.json"),JsonUtility.ToJson(report,true));}
        void Finish(){if(actualBusiness!=null)WriteDigests("business-actual.bin",actualBusiness);report.status="completed";report.endedUtc=DateTime.UtcNow.ToString("O");Save();File.WriteAllText(Path.Combine(config.output,"done.signal"),"completed");Application.Quit(0);}
        void Fail(Exception e){failed=true;UnityEngine.Debug.LogException(e);if(report!=null){report.status="failed";report.error=e.ToString();Save();}Application.Quit(2);}
        void OnDestroy()
        {
            if(pending)request.WaitForCompletion();commands?.Dispose();scan?.Dispose();index?.Dispose();foreach(var p in pipelines.Values)p.Dispose();
            input?.Dispose();flags?.Dispose();queryBuffer?.Dispose();scanOutput?.Dispose();
        }
    }
}
