using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Summit.GpuPrimitives;
using Summit.GpuSensorPipeline;
using Summit.GpuTimestamps;
using Summit.PublicIntegration;
using UnityEngine;
using UnityEngine.Rendering;
using Pipeline=Summit.GpuSensorPipeline.GpuSensorPipeline;
namespace Summit.IndexCostDiagnostics
{
    [Serializable] public sealed class Config
    {
        public string scenario,output,oracle,sourceSha;
        public uint seed=927101;public int frames=384,warmup=64;public bool phases;
    }
    [Serializable] public sealed class Timing
    {
        public string name,status="pending";public ulong token,beginTicks,endTicks,frequency;
        public double gpuMs,recordCpuMs;public int sourceFrame,resultFrame;
        [NonSerialized] public long recordBegin;
    }
    [Serializable] public sealed class Frame
    {
        public int frame,unityFrame,activeCount,changedSlots,indexOrder,queryOrder;public bool measured;
        public long uploadedBytes,recordAllocatedBytes;
        public double pollCpuMs,traceCpuMs,contentCpuMs,uploadCpuMs,recordCpuMs,fullRecordCpuMs,incrementalRecordCpuMs,compactQueryRecordCpuMs,reservedQueryRecordCpuMs;
        public double validationRecordCpuMs,renderSubmitCpuMs,engineDiagnosticIntervalMs;
        public List<Timing> timing=new List<Timing>();public uint[] state,compactCsrCounters,reservedCsrCounters;
    }
    [Serializable] public sealed class Report
    {
        public string status="running",error,startedUtc,endedUtc,device,graphicsApi,unityVersion,buildGuid;
        public int pid;public bool development,formalPerformanceEvidence=false;
        public string scope="Diagnostic paired graph rendered explicitly into a 1280x720 target: both complete indices, compact/reserved queries over identical incremental Samples, six independent CSR-validation dispatches, history, original two draws. Not original scene/full-engine/presentation time.";
        public string counterScope="Dedicated GPU validation scans count all CSR live/Invalid slots and verify exact active membership, no duplicates, correct cells and snapshot equality every frame. These are not hardware counters nor query-specific slots-visited. Layout, ordering, reservation and consumer capacity differ together.";
        public string phaseScope="Phase-on adds native scopes around 13 incremental dispatch calls and full key/clear/count/scan/prepare/scatter. Empty indirect dispatch timings include markers, barriers and command processing. No marker subtraction or free compaction claim.";
        public Config config;public List<Frame> frames=new List<Frame>();public List<ContentEvent> contentEvents=new List<ContentEvent>();
        public double setupCpuMs,drainWallMs;public long fullResidentBytes,incrementalResidentBytes;
        public int verifiedFrames,verifiedQueryWords,verifiedExactMembershipFrames;
        public bool allocationCounterValid,deviceRemovalReasonAvailable=false;
        public long allocationProbeReportedBytes,allocationProbeHeapDelta;
        public string allocationCounterStatus,nativeTerminalStatus;
        public int consumedNative,pendingNative;
        public int renderWidth=1280,renderHeight=720,renderDepthBits=24,renderMsaa=1,startupVerifiedScopes;
        public string renderFormat;public bool cameraHdr=false,cameraMsaa=false;
    }
    public sealed class IndexCostPlayer:MonoBehaviour
    {
        const int N=262144;Config config;Report report;GpuTimestampSession timestamps;
        readonly List<Pending> pending=new List<Pending>();readonly Dictionary<string,Pending> phases=new Dictionary<string,Pending>();
        Frame row;ulong nextToken=1;Camera cameraView;Material material;ComputeShader counterShader;RenderTexture target;
        int clearKernel,membersKernel,seenKernel,historyKernel;
        byte[] allocationProbe;
        struct Pending {public GpuTimestampToken token;public Timing timing;}
        void Awake()
        {
            Application.runInBackground=true;Application.targetFrameRate=-1;QualitySettings.vSyncCount=0;
            var args=Environment.GetCommandLineArgs();int at=Array.IndexOf(args,"-index-cost-config");
            if(at<0)throw new Exception("Config required");
            config=JsonUtility.FromJson<Config>(File.ReadAllText(args[at+1]));
            if(config.frames!=384||config.warmup!=64||config.seed!=927101||(config.scenario!="hotspot-dynamic"&&config.scenario!="streaming-switch"))throw new Exception("Fixed diagnostic case mismatch");
            Directory.CreateDirectory(config.output);
            report=new Report{config=config,pid=Process.GetCurrentProcess().Id,startedUtc=DateTime.UtcNow.ToString("O"),
                device=SystemInfo.graphicsDeviceName,graphicsApi=SystemInfo.graphicsDeviceType.ToString(),unityVersion=Application.unityVersion,buildGuid=Application.buildGUID,development=UnityEngine.Debug.isDebugBuild};Save();
        }
        IEnumerator Start()
        {
            var routine=Run();
            while(true)
            {
                bool next;
                try{next=routine.MoveNext();}
                catch(Exception e){report.status="failed";report.error=e.ToString();report.pendingNative=pending.Count;report.nativeTerminalStatus=timestamps?.TerminalStatus.ToString();Save();UnityEngine.Debug.LogException(e);(routine as IDisposable)?.Dispose();Application.Quit(2);yield break;}
                if(!next)break;yield return routine.Current;
            }
            (routine as IDisposable)?.Dispose();report.status="complete";report.endedUtc=DateTime.UtcNow.ToString("O");Save();Application.Quit(0);
        }
        IEnumerator Run()
        {
            if(report.development||SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D12)throw new Exception("Release/DX12 required");
            if(!GpuTimestampSession.TryCreate(out timestamps,out var support))throw new Exception(support.Message);
            long probeBefore=GC.GetAllocatedBytesForCurrentThread(),heapBefore=GC.GetTotalMemory(false);
            allocationProbe=new byte[1024*1024];allocationProbe[0]=1;
            report.allocationProbeReportedBytes=GC.GetAllocatedBytesForCurrentThread()-probeBefore;
            report.allocationProbeHeapDelta=GC.GetTotalMemory(false)-heapBefore;
            report.allocationCounterValid=report.allocationProbeReportedBytes>=allocationProbe.Length;
            report.allocationCounterStatus=report.allocationCounterValid?"known-allocation-probe-passed":"unavailable: known 1MiB allocation not counted; row bytes=-1";
            GC.KeepAlive(allocationProbe);
            byte[] expected=File.ReadAllBytes(config.oracle);if(expected.Length!=384*160)throw new Exception("Existing matching full oracle required");
            var expectedWords=new uint[expected.Length/4];Buffer.BlockCopy(expected,0,expectedWords,0,expected.Length);
            var timer=Stopwatch.StartNew();
            var samples=IntegrationFixture.Create(config.scenario,config.seed);var active=new uint[N];
            int activeCount=config.scenario=="streaming-switch"?N-32768:N;
            for(int i=0;i<activeCount;i++)active[i]=1;
            var content=new IntegrationContent(e=>report.contentEvents.Add(e));
            material=new Material(Resources.Load<Shader>("IntegrationParticles"));
            counterShader=Resources.Load<ComputeShader>("IndexCostHistory");
            clearKernel=counterShader.FindKernel("ClearSeen");membersKernel=counterShader.FindKernel("ValidateMembers");seenKernel=counterShader.FindKernel("ValidateSeen");historyKernel=counterShader.FindKernel("StoreHistory");
            cameraView=new GameObject("Diagnostic Camera").AddComponent<Camera>();cameraView.clearFlags=CameraClearFlags.SolidColor;
            cameraView.backgroundColor=new Color(.015f,.025f,.055f);cameraView.nearClipPlane=.03f;cameraView.farClipPlane=250;
            target=new RenderTexture(1280,720,24,RenderTextureFormat.ARGB32);target.Create();
            cameraView.targetTexture=target;cameraView.enabled=false;cameraView.allowHDR=false;cameraView.allowMSAA=false;
            report.renderFormat=target.graphicsFormat.ToString();report.renderMsaa=target.antiAliasing;
            var backend=config.scenario=="hotspot-dynamic"?GpuSensorQueryBackend.CellSerial:GpuSensorQueryBackend.BatchedPointScanWave;
            using(var input=new GraphicsBuffer(GraphicsBuffer.Target.Structured,N,16))
            using(var flags=new GraphicsBuffer(GraphicsBuffer.Target.Structured,N,4))
            using(var history=new GraphicsBuffer(GraphicsBuffer.Target.Structured,384*112,4))
            using(var seen=new GraphicsBuffer(GraphicsBuffer.Target.Structured,N,4))
            using(var compactStats=new GraphicsBuffer(GraphicsBuffer.Target.Structured,8,4))
            using(var reservedStats=new GraphicsBuffer(GraphicsBuffer.Target.Structured,8,4))
            using(var full=new Full(config.phases,Marker))
            using(var inc=new Incremental(config.phases,Marker))
            using(var compactQuery=new Pipeline(N,9,GpuPrimitiveBackend.Portable,false,queryBackend:backend,queryIndexEntryCapacity:N*3))
            using(var reservedQuery=new Pipeline(N,9,GpuPrimitiveBackend.Portable,false,queryBackend:backend,queryIndexEntryCapacity:N*3))
            using(var commands=new CommandBuffer{name="IndexCosts/PairedDiagnostic"})
            {
                report.fullResidentBytes=full.Bytes;report.incrementalResidentBytes=inc.Bytes;
                input.SetData(samples);flags.SetData(active);history.SetData(new uint[384*112]);
                compactQuery.SetQueries(IntegrationFixture.Queries());reservedQuery.SetQueries(IntegrationFixture.Queries());
                var props=new MaterialPropertyBlock();props.SetBuffer("_Samples",input);props.SetBuffer("_Active",flags);props.SetBuffer("_Digests",reservedQuery.QueryDigests);
                timestamps.RecordFrequencyInitialization(commands);Graphics.ExecuteCommandBuffer(commands);commands.Clear();yield return null;
                cameraView.AddCommandBuffer(CameraEvent.BeforeForwardOpaque,commands);report.setupCpuMs=timer.Elapsed.TotalMilliseconds;
                for(int frame=0;frame<384;frame++)
                {
                    double frameStart=Time.realtimeSinceStartupAsDouble;
                    row=new Frame{frame=frame,unityFrame=Time.frameCount,measured=frame>=64,indexOrder=frame%2,queryOrder=(frame/2)%2};report.frames.Add(row);
                    timer.Restart();PollNative();row.pollCpuMs=timer.Elapsed.TotalMilliseconds;
                    timer.Restart();row.changedSlots=IntegrationFixture.Advance(config.scenario,samples,active,frame);row.traceCpuMs=timer.Elapsed.TotalMilliseconds;
                    timer.Restart();if(config.scenario=="streaming-switch"){row.changedSlots+=content.Advance(frame,samples,active,config.seed);activeCount+=content.ActiveDelta;}row.contentCpuMs=timer.Elapsed.TotalMilliseconds;
                    row.activeCount=activeCount;timer.Restart();
                    if(row.changedSlots>0){input.SetData(samples);flags.SetData(active);row.uploadedBytes=N*20L;}row.uploadCpuMs=timer.Elapsed.TotalMilliseconds;
                    float angle=(float)frame/384*Mathf.PI*.65f;float radius=config.scenario=="hotspot-dynamic"?5:100;
                    cameraView.transform.position=new Vector3(Mathf.Sin(angle)*radius,radius*.35f,Mathf.Cos(angle)*radius);cameraView.transform.LookAt(Vector3.zero);props.SetTexture("_Palette",content.CurrentTexture);
                    commands.Clear();timer.Restart();long allocation=GC.GetAllocatedBytesForCurrentThread();
                    var outer=Begin(commands,"diagnosticGraph");commands.ClearRenderTarget(true,true,cameraView.backgroundColor);
                    for(int order=0;order<2;order++)
                    {
                        bool isFull=(order+row.indexOrder)%2==0;
                        var token=Begin(commands,isFull?"fullIndex":"incrementalIndex");long before=Stopwatch.GetTimestamp();
                        if(isFull){full.Record(commands,input,flags);row.fullRecordCpuMs=Ms(Stopwatch.GetTimestamp()-before);}
                        else{inc.Record(commands,input,flags);row.incrementalRecordCpuMs=Ms(Stopwatch.GetTimestamp()-before);}End(commands,token);
                    }
                    for(int order=0;order<2;order++)
                    {
                        bool compact=(order+row.queryOrder)%2==0;var token=Begin(commands,compact?"compactQuery":"reservedQuery");long before=Stopwatch.GetTimestamp();
                        // Both consumers read the exact same incremental Samples buffer.
                        if(compact){compactQuery.RecordExternalIndexQueries(commands,inc.Samples,full.Offsets,full.Members,N,9,0);row.compactQueryRecordCpuMs=Ms(Stopwatch.GetTimestamp()-before);}
                        else{reservedQuery.RecordExternalIndexQueries(commands,inc.Samples,inc.Offsets,inc.Members,N,9,0);row.reservedQueryRecordCpuMs=Ms(Stopwatch.GetTimestamp()-before);}End(commands,token);
                    }
                    var validation=Begin(commands,"additionalExactCsrValidation");long vbegin=Stopwatch.GetTimestamp();
                    ValidateCsr(commands,inc.Samples,input,flags,full.Offsets,full.Members,seen,compactStats);
                    ValidateCsr(commands,inc.Samples,input,flags,inc.Offsets,inc.Members,seen,reservedStats);
                    counterShaderBindings(commands,historyKernel,history,compactQuery.QueryDigests,reservedQuery.QueryDigests,inc.State,compactStats,reservedStats);
                    commands.SetComputeIntParam(counterShader,"_Frame",frame);commands.SetComputeIntParam(counterShader,"_ActiveCount",activeCount);commands.SetComputeIntParam(counterShader,"_UpdateCount",row.changedSlots);
                    commands.DispatchCompute(counterShader,historyKernel,1,1,1);row.validationRecordCpuMs=Ms(Stopwatch.GetTimestamp()-vbegin);End(commands,validation);
                    var draw=Begin(commands,"originalTwoDraws");
                    commands.DrawProcedural(Matrix4x4.identity,material,0,MeshTopology.Points,N,1,props);
                    commands.DrawProcedural(Matrix4x4.identity,material,1,MeshTopology.Triangles,54,1,props);End(commands,draw);End(commands,outer);
                    row.recordAllocatedBytes=report.allocationCounterValid?GC.GetAllocatedBytesForCurrentThread()-allocation:-1;row.recordCpuMs=timer.Elapsed.TotalMilliseconds;
                    timer.Restart();cameraView.Render();row.renderSubmitCpuMs=timer.Elapsed.TotalMilliseconds;
                    yield return new WaitForEndOfFrame();if(config.scenario=="streaming-switch")content.MarkRendered(frame);
                    commands.Clear();yield return null;row.engineDiagnosticIntervalMs=(Time.realtimeSinceStartupAsDouble-frameStart)*1000;
                    if(frame==0)
                    {
                        double startup=Time.realtimeSinceStartupAsDouble;
                        while(pending.Count>0){PollNative();if(Time.realtimeSinceStartupAsDouble-startup>60)throw new Exception("First-frame execution verification failed; pending="+pending.Count);yield return null;}
                        report.startupVerifiedScopes=report.consumedNative;
                    }
                }
                cameraView.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque,commands);
                double drain=Time.realtimeSinceStartupAsDouble;var request=AsyncGPUReadback.Request(history);
                while(!request.done||pending.Count>0){if(Time.realtimeSinceStartupAsDouble-drain>60)throw new Exception("Drain timeout");PollNative();content.Poll(384);yield return null;}
                if(request.hasError)throw new Exception("History readback failed");var words=request.GetData<uint>().ToArray();
                byte[] raw=new byte[words.Length*4];Buffer.BlockCopy(words,0,raw,0,raw.Length);File.WriteAllBytes(Path.Combine(config.output,"history.bin"),raw);
                for(int f=0;f<384;f++)
                {
                    for(int i=0;i<40;i++)if(words[f*112+i]!=expectedWords[f*40+i]||words[f*112+40+i]!=expectedWords[f*40+i])throw new Exception("Per-frame compact/reserved/oracle mismatch "+f+" word "+i);
                    var rr=report.frames[f];rr.state=words.Skip(f*112+80).Take(16).ToArray();rr.compactCsrCounters=words.Skip(f*112+96).Take(8).ToArray();rr.reservedCsrCounters=words.Skip(f*112+104).Take(8).ToArray();
                    foreach(var counters in new[]{rr.compactCsrCounters,rr.reservedCsrCounters})
                    {
                        if(counters[0]!=rr.activeCount||counters[7]!=counters[0]+counters[1]||counters[7]>N*3)throw new Exception("Live/extent mismatch "+f);
                        for(int k=2;k<=6;k++)if(counters[k]!=0)throw new Exception("Exact CSR validation failure frame "+f+" counter "+k);
                    }
                    report.verifiedFrames++;report.verifiedQueryWords+=80;report.verifiedExactMembershipFrames++;
                }
                report.drainWallMs=(Time.realtimeSinceStartupAsDouble-drain)*1000;
                content.BeginCleanup(384);while(content.Pending>0){content.Poll(384);if(Time.realtimeSinceStartupAsDouble-drain>60)throw new Exception("Content drain timeout");yield return null;}
            }
            timestamps.Dispose();timestamps=null;
        }
        void ValidateCsr(CommandBuffer c,GraphicsBuffer samples,GraphicsBuffer input,GraphicsBuffer flags,GraphicsBuffer offsets,GraphicsBuffer members,GraphicsBuffer seen,GraphicsBuffer stats)
        {
            c.SetComputeIntParam(counterShader,"_Capacity",N);c.SetComputeIntParam(counterShader,"_EntryCapacity",members.count);
            foreach(int k in new[]{clearKernel,membersKernel,seenKernel})
            {
                c.SetComputeBufferParam(counterShader,k,"_Samples",samples);c.SetComputeBufferParam(counterShader,k,"_InputSamples",input);
                c.SetComputeBufferParam(counterShader,k,"_Active",flags);c.SetComputeBufferParam(counterShader,k,"_Offsets",offsets);c.SetComputeBufferParam(counterShader,k,"_Members",members);
                c.SetComputeBufferParam(counterShader,k,"_Seen",seen);c.SetComputeBufferParam(counterShader,k,"_Stats",stats);
                c.DispatchCompute(counterShader,k,(k==membersKernel?members.count:N)/256,1,1);
            }
        }
        void counterShaderBindings(CommandBuffer c,int k,GraphicsBuffer history,GraphicsBuffer compact,GraphicsBuffer reserved,GraphicsBuffer state,GraphicsBuffer cs,GraphicsBuffer rs)
        {
            c.SetComputeBufferParam(counterShader,k,"_History",history);c.SetComputeBufferParam(counterShader,k,"_CompactDigests",compact);c.SetComputeBufferParam(counterShader,k,"_ReservedDigests",reserved);
            c.SetComputeBufferParam(counterShader,k,"_IndexState",state);c.SetComputeBufferParam(counterShader,k,"_CompactStats",cs);c.SetComputeBufferParam(counterShader,k,"_ReservedStats",rs);
        }
        Pending Begin(CommandBuffer c,string name)
        {
            var timing=new Timing{name=name};row.timing.Add(timing);
            var status=timestamps.Acquire(nextToken++,GpuTimestampSampleFlags.None,Time.frameCount,out var token);
            if(status!=GpuTimestampStatus.Ready)throw new Exception("Timestamp acquire failed: "+status+"; pending="+pending.Count+"; consumed="+report.consumedNative+"; terminal="+timestamps.IsTerminal+"/"+timestamps.TerminalStatus+"; graphicsApi="+SystemInfo.graphicsDeviceType);
            timestamps.GetScope(token).RecordBegin(c);var item=new Pending{token=token,timing=timing};pending.Add(item);return item;
        }
        void End(CommandBuffer c,Pending item){timestamps.GetScope(item.token).RecordEnd(c);if(timestamps.MarkSubmitted(item.token)!=GpuTimestampStatus.Ready)throw new Exception("Timestamp submit failed");}
        void Marker(CommandBuffer c,string name,bool begin)
        {
            if(begin){var p=Begin(c,name);p.timing.recordBegin=Stopwatch.GetTimestamp();phases.Add(name,p);}
            else{var p=phases[name];p.timing.recordCpuMs=Ms(Stopwatch.GetTimestamp()-p.timing.recordBegin);End(c,p);phases.Remove(name);}
        }
        void PollNative()
        {
            for(int i=pending.Count-1;i>=0;i--){var p=pending[i];var status=timestamps.TryConsume(p.token,Time.frameCount,out var r);if(status==GpuTimestampStatus.Pending)continue;
                if(status!=GpuTimestampStatus.Ready)throw new Exception("Native timing: "+status);p.timing.status="Ready";p.timing.token=r.Token.Value;p.timing.beginTicks=r.BeginTicks;p.timing.endTicks=r.EndTicks;p.timing.frequency=r.TimestampFrequency;p.timing.gpuMs=r.ElapsedMilliseconds;p.timing.sourceFrame=r.SourceFrame;p.timing.resultFrame=r.ResultFrame;pending.RemoveAt(i);report.consumedNative++;}
        }
        static double Ms(long ticks)=>ticks*1000.0/Stopwatch.Frequency;
        void Save()=>File.WriteAllText(Path.Combine(config.output,"result.json"),JsonUtility.ToJson(report,true));
        void OnDestroy(){timestamps?.Dispose();if(material!=null)Destroy(material);if(target!=null){target.Release();Destroy(target);}}
        sealed class Full:IDisposable
        {
            readonly GpuSensorFullRebuildIndex original;readonly CostProfiledFullIndex profiled;
            public Full(bool phases,Action<CommandBuffer,string,bool> marker){if(phases){profiled=new CostProfiledFullIndex(N,GpuPrimitiveBackend.WaveOps);profiled.DiagnosticMarker=marker;}else original=new GpuSensorFullRebuildIndex(N,GpuPrimitiveBackend.WaveOps);}
            public GraphicsBuffer Offsets=>original?.BinOffsets??profiled.BinOffsets;public GraphicsBuffer Members=>original?.BinnedIds??profiled.BinnedIds;
            public long Bytes=>original?.ResidentBytes??profiled.ResidentBytes;
            public void Record(CommandBuffer c,GraphicsBuffer input,GraphicsBuffer flags){if(original!=null)original.RecordUpdate(c,input,flags);else profiled.RecordUpdate(c,input,flags);}
            public void Dispose(){original?.Dispose();profiled?.Dispose();}
        }
        sealed class Incremental:IDisposable
        {
            readonly GpuSensorIncrementalIndex original;readonly CostProfiledIncrementalIndex profiled;
            public Incremental(bool phases,Action<CommandBuffer,string,bool> marker){if(phases){profiled=new CostProfiledIncrementalIndex(N,executionMode:GpuSensorIndexExecutionMode.GpuDriven);profiled.DiagnosticMarker=marker;}else original=new GpuSensorIncrementalIndex(N,executionMode:GpuSensorIndexExecutionMode.GpuDriven);}
            public GraphicsBuffer Samples=>original?.Samples??profiled.Samples;public GraphicsBuffer Offsets=>original?.BinOffsets??profiled.BinOffsets;public GraphicsBuffer Members=>original?.BinnedIds??profiled.BinnedIds;public GraphicsBuffer State=>original?.Diagnostics??profiled.Diagnostics;
            public long Bytes=>original?.ResidentBytes??profiled.ResidentBytes;
            public void Record(CommandBuffer c,GraphicsBuffer input,GraphicsBuffer flags){if(original!=null)original.RecordUpdate(c,input,flags);else profiled.RecordUpdate(c,input,flags);}
            public void Dispose(){original?.Dispose();profiled?.Dispose();}
        }
    }
}
