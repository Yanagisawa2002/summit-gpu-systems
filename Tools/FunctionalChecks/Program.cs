using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Summit.GpuTimestamps;
using Summit.GpuPrimitives;
using Summit.ExternalWorkloads;

static class Program
{
    static int checks;
    static void Check(bool condition, string description)
    { checks++; if (!condition) throw new Exception(description); }
    static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { checks++; return; } throw new Exception("Expected " + typeof(T).Name); }
    static void Main()
    {
        ObservationChecks(); CounterChecks(); SphereChecks(); LinkedCellChecks(); LifecycleChecks(); HlslArtifactChecks(); SnapshotChecks();
        Console.WriteLine($"PASS: {checks} deterministic functional assertions; no timers, counters, GPU, or benchmark executed.");
    }
    static void ObservationChecks()
    {
        var source = new ObservationSource("synthetic-source", "build,\"line\n2", "fake-driver", "PIX-CSV-fixture", "qpc", "main", 5);
        var native = new Observation(source, "7", ObservationScope.ExplicitGpuWork, ObservationUnit.Milliseconds,
            ObservationState.Available, 0.25, "", 3, 8, 4, 5, 4);
        var missing = new Observation(source, "7", ObservationScope.EngineGpu, ObservationUnit.Milliseconds,
            ObservationState.Unavailable, null, "not captured", 3, 8);
        var ledger = new ObservationLedger(); ledger.Drain(new ReplayObservationCollector(new[] { native, missing }));
        Check(ledger.Rows.Count() == 2 && missing.Value == null, "Partial GPU never fills engine GPU");
        Throws<ArgumentException>(() => ledger.Add(native));
        Throws<ArgumentException>(() => new Observation(source, "0", ObservationScope.EngineGpu, ObservationUnit.Bytes, ObservationState.Available, 1, "", 0, 0));
        Throws<ArgumentException>(() => new Observation(source, "0", ObservationScope.EngineGpu, ObservationUnit.Milliseconds, ObservationState.Unavailable, 0, "missing", 0, 0));
        Throws<ArgumentException>(() => new Observation(source, "0", ObservationScope.EngineGpu, ObservationUnit.Milliseconds, ObservationState.Available, double.NaN, "", 0, 0));
        var output = new StringWriter(); ObservationCsv.Write(output, ledger.Rows);
        var replay = ObservationCsv.Read(new StringReader(output.ToString())).ToArray();
        Check(replay.Length == 2 && replay[0].Source.BuildId == source.BuildId && replay[0].SourceFrame == 3 && replay[0].ObservedFrame == 8 && replay[1].Value == null, "Lossless CSV with delayed results/escaping/missing values");
        Throws<FormatException>(() => ObservationCsv.Read(new StringReader("bad\n")).ToArray());
        Throws<FormatException>(() => ObservationCsv.Read(new StringReader(ObservationCsv.Header + "\n\"unterminated")).ToArray());
        var anchors = new[] { new FrameAnchor(8, 100), new FrameAnchor(9, 200), new FrameAnchor(10, 300) };
        Check(ObservationCorrelation.FirstSubsequentUpdate(190, "qpc", 1000, "qpc", 1000, anchors) == 9, "Attribute to source interval");
        Check(ObservationCorrelation.FirstSubsequentUpdate(200, "qpc", 1000, "qpc", 1000, anchors) == 9, "Inclusive subsequent Update");
        Check(ObservationCorrelation.FirstSubsequentUpdate(99, "qpc", 1000, "qpc", 1000, anchors) == -1, "No fabricated first window");
        Check(ObservationCorrelation.FirstSubsequentUpdate(190, "stopwatch", 1000, "qpc", 1000, anchors) == -1, "Epoch mismatch");
        Check(ObservationCorrelation.FirstSubsequentUpdate(190, "qpc", 100, "qpc", 1000, anchors) == -1, "Frequency mismatch");
        Check(ObservationCorrelation.FirstSubsequentUpdate(190, "qpc", 1000, "qpc", 1000, new[] { anchors[1], anchors[0] }) == -1, "Unordered source anchors rejected");
    }
    sealed class FakeCounter : IManagedAllocationCounter
    {
        public long Bytes; public int ThreadId { get; set; } = 1;
        public bool Supported = true; public string Scope { get; set; } = AllocationCounterCapability.CurrentThreadScope;
        public bool TryRead(out long bytes) { bytes = Bytes; return Supported; }
    }
    static void CounterChecks()
    {
        var fake = new FakeCounter();
        var noControl = AllocationCounterCapability.Probe(fake, () => { }, 1024);
        Check(!noControl.Available && noControl.Delta(fake, 0) == -1, "Lying zero counter unavailable");
        var yes = AllocationCounterCapability.Probe(fake, () => fake.Bytes += 1024, 1024);
        Check(yes.Available && yes.Delta(fake, fake.Bytes) == 0, "True zero allowed only after positive control");
        fake.ThreadId++;
        Check(yes.Delta(fake, 0) == -1, "Worker allocations cannot be treated as current-thread coverage");
        var reset = AllocationCounterCapability.Probe(fake, () => fake.Bytes = 0, 1);
        Check(!reset.Available, "Counter reset invalidates capability");
        fake.Scope = "process-native";
        bool invoked = false;
        Check(!AllocationCounterCapability.Probe(fake, () => invoked = true, 1).Available && !invoked, "Wrong-scope control not invoked");
    }
    static void SphereChecks()
    {
        var domain = new SphereWorkloadDomain(-8, -8, -8, 16);
        var points = new[] { new SourcePoint(0,0,0,7), new SourcePoint(1,0,0,8), new SourcePoint(1,0,0,9), new SourcePoint(1,1,0,10), new SourcePoint(-8,0,0,11), new SourcePoint(8,0,0,12), new SourcePoint(1e-24f,0,0,13) };
        var spheres = new[] { new SourceSphere(0,0,0,1), new SourceSphere(0,0,0,0), new SourceSphere(0,0,0,-1), new SourceSphere(20,0,0,1), new SourceSphere(0,0,0,8), new SourceSphere(9,0,0,1) };
        var expected = spheres.Select(s => points.Where(p => SphereWorkloadContract.Intersects(p,s)).Select(p=>p.Id).ToArray()).ToArray();
        var actual = SphereWorkloadContract.Filter(domain, points, spheres);
        SphereWorkloadContract.RequireFullMembership(expected, actual); Check(true, "Inclusive float sphere, duplicates, zero/negative radius, outside center");
        Check(actual[0].Contains(8u) && actual[0].Contains(9u) && !actual[0].Contains(10u), "Exact filter removes AABB false positives");
        Check(actual[1].Contains(13u), "Float squared-distance underflow has conservative broad phase");
        Check(SphereWorkloadContract.Filter(domain, Array.Empty<SourcePoint>(), spheres).All(x=>x.Length==0), "Empty input complete CSR");
        Throws<ArgumentException>(() => SphereWorkloadContract.Encode(domain, new[] {points[0], points[0]}));
        Throws<ArgumentException>(() => domain.Encode(new SourcePoint(9,0,0,0)));
        Throws<ArgumentException>(() => domain.Encode(new SourcePoint(float.NaN,0,0,0)));
        Throws<ArgumentException>(() => domain.Bounds(new SourceSphere(0,0,0,float.PositiveInfinity)));
        Throws<ArgumentException>(() => new SphereWorkloadDomain(0,0,0,0));
        Throws<ArgumentException>(() => SphereWorkloadContract.RequireFullMembership(new[] {new uint[]{1,2}}, new[] {new uint[]{1,1}}));
        // Fixed small adversarial cases around every grid boundary; no workload generator,
        // timings, scale sweep, or comparison of algorithms' performance is present.
        for (int cell=0; cell<64; cell++)
        {
            float x=(float)(-8+16.0*cell/64);
            var p=new[] {new SourcePoint(x,0,0,0),new SourcePoint(MathF.BitIncrement(x),0,0,1),new SourcePoint(MathF.BitDecrement(x),0,0,2)};
            if(p.Any(v=>v.X < -8 || v.X > 8)) continue;
            var s=new[]{new SourceSphere(x,0,0,0),new SourceSphere(x+0.5f,0,0,0.5f)};
            SphereWorkloadContract.RequireFullMembership(s.Select(v=>p.Where(w=>SphereWorkloadContract.Intersects(w,v)).Select(w=>w.Id).ToArray()).ToArray(), SphereWorkloadContract.Filter(domain,p,s));
            Check(true,"Quantization boundary "+cell);
        }
    }
    static void LifecycleChecks()
    {
        var epoch = new ConsumerEpoch(); ulong a = epoch.Submit(); ulong b = epoch.Submit();
        epoch.Invalidate(); Throws<InvalidOperationException>(epoch.RequireDrained);
        Check(!epoch.Complete(b), "Canceled result cannot be consumed");
        Check(!epoch.Complete(a), "Out-of-order completion still drains resources");
        epoch.RequireDrained(); Throws<ArgumentException>(() => epoch.Complete(a));
        Check(epoch.Complete(epoch.Submit()), "New generation result can be consumed");
    }
    static void LinkedCellChecks()
    {
        var grid=new LinkedCellWorkloadContract(new double[]{0,0,0},new double[]{10,10,10},new double[]{3,3,3});
        Check(grid.BinCount==27,"Cabana floors requested cells and adjusts actual width");
        Check(grid.Key(new LinkedCellPoint(3.2,0,0,0))==0,"Do not bin using requested width after adjustment");
        Check(grid.Key(new LinkedCellPoint(4,0,0,0))==9 && grid.Key(new LinkedCellPoint(0,0,4,0))==1,"Cabana z-fast cardinal IDs");
        Check(grid.Key(new LinkedCellPoint(10,10,10,0))==26,"Outer endpoint belongs to last cell");
        var rows=grid.Membership(new[]{new LinkedCellPoint(0,0,0,2),new LinkedCellPoint(0,0,0,7),new LinkedCellPoint(10,10,10,9)});
        Check(rows[0].SequenceEqual(new uint[]{2,7}) && rows[26].SequenceEqual(new uint[]{9}),"Full original membership including duplicate coordinates");
        Throws<ArgumentException>(()=>grid.Key(new LinkedCellPoint(-0.1,0,0,0)));
        Throws<ArgumentException>(()=>grid.Encode(new[]{new LinkedCellPoint(0,0,0,7),new LinkedCellPoint(4,0,0,7)}));
        Throws<ArgumentException>(()=>new LinkedCellWorkloadContract(new double[]{0,0,0},new double[]{1,1,1},new double[]{2,2,2}));
    }
    static void HlslArtifactChecks()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root!=null&&!Directory.Exists(Path.Combine(root.FullName,"Integrations/HlslKernelPipeline/Artifact~")))root=root.Parent;
        if(root==null)throw new Exception("Cannot find checked-in HLSL artifact.");
        string path=Path.Combine(root.FullName,"Integrations/HlslKernelPipeline/Artifact~");
        var manifest=JsonSerializer.Deserialize<HlslScanManifest>(File.ReadAllText(Path.Combine(path,"consumer-manifest.json")),new JsonSerializerOptions{IncludeFields=true});
        const string commit="274e5077455e6c08959a274a84379dfc4e1345dd";
        const string assets="117f226d4f83c9a19155d03f2d6179e55db676e19fc58c76ccb1aaef836b6f16";
        var artifact=HlslScanArtifact.Verify(manifest,f=>File.ReadAllBytes(Path.Combine(path,f)),commit,assets);
        Check(artifact.ScratchBytes(1)==20&&artifact.ScratchBytes(4097)==32,"Capacity-based external scratch includes complete status/sums");
        Check(HlslScanArtifact.ScanGroups(0)==0&&HlslScanArtifact.ScanGroups(4097)==2&&HlslScanArtifact.ScanGroups(HlslScanArtifact.MaxCount)==256,"Empty skips, tail and resident group bound");
        Throws<ArgumentOutOfRangeException>(()=>HlslScanArtifact.ScanGroups(-1));
        Throws<InvalidDataException>(()=>HlslScanArtifact.Verify(manifest,f=>new byte[]{0},commit,assets));
        Throws<InvalidDataException>(()=>HlslScanArtifact.Verify(manifest,f=>File.ReadAllBytes(Path.Combine(path,f)),new string('0',40),assets));
        var request=new HlslScanSelection{SourceCommit=commit,AssetSha256=assets,KernelAbi=HlslScanArtifact.Abi,VariantId=HlslScanArtifact.Variant,DeviceDriverId="fixture-device/driver"};
        Check(artifact.Accepts(request,"fixture-device/driver",out _),"Exact externally verified artifact selection");
        Check(!artifact.Accepts(request,"different-driver",out _),"Driver invalidates external selection");
        request.AssetSha256=new string('0',64);
        Check(!artifact.Accepts(request,"fixture-device/driver",out _),"Profile cannot self-attest a different asset");
        request.AssetSha256=assets;
        HlslRecordingChecks(artifact,request);
        manifest.files[0].path="../untrusted";
        Throws<InvalidDataException>(()=>HlslScanArtifact.Verify(manifest,f=>throw new Exception("No read expected"),commit,assets));
    }
    static void HlslRecordingChecks(HlslScanArtifact artifact,HlslScanSelection request)
    {
        var shader=new UnityEngine.ComputeShader("ResetWaveTiledState","SinglePassScanWaveTiled");
        Check(!GpuHlslScanConsumer.TryCreate(artifact,shader,request,request.DeviceDriverId,4097,false,out _,out _),"Unmeasured selection requires explicit opt-in");
        shader.Supported=false;
        Check(!GpuHlslScanConsumer.TryCreate(artifact,shader,request,request.DeviceDriverId,4097,true,out _,out _),"Unsupported imported kernel refuses external path");
        shader.Supported=true;
        Check(GpuHlslScanConsumer.TryCreate(artifact,shader,request,request.DeviceDriverId,4097,true,out var consumer,out _),"Verified raw recorder construction");
        using(consumer)
        using(var input=new UnityEngine.GraphicsBuffer(UnityEngine.GraphicsBuffer.Target.Raw,4097,4))
        using(var output=new UnityEngine.GraphicsBuffer(UnityEngine.GraphicsBuffer.Target.Raw,4097,4))
        {
            var commands=new UnityEngine.Rendering.CommandBuffer();
            consumer.RecordExclusiveScan(commands,input,output,0);Check(commands.Dispatches.Count==0,"Empty scan leaves sentinels and scratch untouched");
            consumer.RecordExclusiveScan(commands,input,output,4097);
            Check(commands.Dispatches.Count==2&&commands.Dispatches[0].Kernel==0&&commands.Dispatches[1].Kernel==1,"Actual reset before scan");
            Check(commands.Dispatches[0].X==1&&commands.Dispatches[1].X==2&&commands.Dispatches[1].Constants["LogicalBlockCount"]==2,"Tail partition dispatch");
            Check(ReferenceEquals(commands.Dispatches[1].Buffers["Input0"],input)&&ReferenceEquals(commands.Dispatches[1].Buffers["Output0"],output)&&ReferenceEquals(commands.Dispatches[0].Buffers["Output0"],commands.Dispatches[1].Buffers["Output1"]),"Actual Raw ABI bindings and shared scratch");
            consumer.RecordExclusiveScan(commands,input,output,1);
            Check(commands.Dispatches[2].Constants["LogicalBlockCount"]==1&&commands.Dispatches[3].X==1,"Repeat invocation resets actual current logical count");
            Throws<ArgumentException>(()=>consumer.RecordExclusiveScan(commands,input,input,1));
            Throws<ArgumentOutOfRangeException>(()=>consumer.RecordExclusiveScan(commands,input,output,4098));
            using var structured=new UnityEngine.GraphicsBuffer(UnityEngine.GraphicsBuffer.Target.Structured,4097,4);
            using var structuredOutput=new UnityEngine.GraphicsBuffer(UnityEngine.GraphicsBuffer.Target.Structured,4097,4);
            Throws<ArgumentException>(()=>consumer.RecordExclusiveScan(commands,structured,structuredOutput,1));
            UnityEngine.Resources.Value=new UnityEngine.ComputeShader("ToRaw","ToStructured");
            using(var bridge=new GpuHlslStructuredScanBridge(consumer,4097))
            {
                var bridged=new UnityEngine.Rendering.CommandBuffer();bridge.RecordExclusiveScan(bridged,structured,structuredOutput,5);
                Check(bridged.Dispatches.Count==4&&ReferenceEquals(bridged.Dispatches[0].Buffers["_StructuredInput"],structured)&&ReferenceEquals(bridged.Dispatches[3].Buffers["_StructuredOutput"],structuredOutput),"Real Structured consumer records both conversions plus reset/scan");
                Check(bridge.ScratchBytes==4097L*8+32,"Conversion buffers included in external scratch budget");
            }
        }
        Throws<ObjectDisposedException>(()=>consumer.RecordExclusiveScan(new UnityEngine.Rendering.CommandBuffer(),null,null,0));
    }
    static void SnapshotChecks()
    {
        using var bytes=new MemoryStream();
        using(var writer=new BinaryWriter(bytes,System.Text.Encoding.UTF8,true))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("SMSPH001"));writer.Write(1u);writer.Write(1u);writer.Write(1u);
            writer.Write(0f);writer.Write(0f);writer.Write(0f);writer.Write(99u);
            writer.Write(0f);writer.Write(0f);writer.Write(0f);writer.Write(1f);
            writer.Write(0u);writer.Write(1u);writer.Write(99u);
        }
        string hash=HlslScanArtifact.Sha256(bytes.ToArray());
        var snapshot=ArborXSnapshot.Read(bytes,ArborXSnapshot.UpstreamCommit,hash);
        Check(snapshot.Points[0].Id==99&&snapshot.ReferenceMembership[0].SequenceEqual(new uint[]{99}),"Upstream snapshot preserves original IDs and complete CSR");
        Throws<ArgumentException>(()=>ArborXSnapshot.Read(bytes,"not-pinned",hash));
        Throws<InvalidDataException>(()=>ArborXSnapshot.Read(bytes,ArborXSnapshot.UpstreamCommit,new string('0',64)));
        var malformed=bytes.ToArray();malformed[malformed.Length-1]=1;
        Throws<InvalidDataException>(()=>ArborXSnapshot.Read(new MemoryStream(malformed),ArborXSnapshot.UpstreamCommit,HlslScanArtifact.Sha256(malformed)));
    }
}
