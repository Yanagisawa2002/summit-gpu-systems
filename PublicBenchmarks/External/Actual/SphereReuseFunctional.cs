using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Summit.ExternalWorkloads;
using UnityEngine;
using UnityEngine.Rendering;

namespace Summit.ActualWorkloads
{
    // Untimed API fixtures, with explicit memberships. These do not generate
    // benchmark inputs or supply a CPU result in place of GPU execution.
    public static class SphereReuseFunctional
    {
        [Serializable] sealed class CheckResult { public string name,detail; public bool passed; }
        [Serializable] sealed class Results { public string scope="untimed real GPU API/lifetime fixtures"; public List<CheckResult> checks=new List<CheckResult>(); }
        static Results results;
        static string destination;
        static void Save(){File.WriteAllText(Path.Combine(destination,"api-checks.json"),JsonUtility.ToJson(results,true));}
        static void Pass(string name,string detail="") { results.checks.Add(new CheckResult{name=name,detail=detail,passed=true});Save(); }
        static void Require(bool value,string name) { if(!value)throw new InvalidOperationException(name);Pass(name); }
        static void Reject<T>(string name,Action action) where T:Exception
        {
            try { action(); } catch(T error) { Pass(name,error.Message);return; }
            throw new InvalidOperationException("Expected "+typeof(T).Name+": "+name);
        }
        static void Build(GpuSphereWorkloadAdapter adapter,CommandBuffer commands)
        {
            commands.Clear();adapter.RecordIndexBuild(commands);Graphics.ExecuteCommandBuffer(commands);adapter.CompleteIndexBuild();
            if(!adapter.IsIndexReady)throw new InvalidOperationException("Build not confirmed");commands.Clear();
        }
        static void Compare(GpuSphereWorkloadAdapter adapter,string name,uint[][] expected)
        {
            var offsets=new uint[expected.Length+1];adapter.Offsets.GetData(offsets,0,0,offsets.Length);
            if(offsets[0]!=0||offsets[offsets.Length-1]>adapter.Ids.count)throw new InvalidDataException("GPU CSR header");
            var ids=new uint[offsets[offsets.Length-1]];if(ids.Length>0)adapter.Ids.GetData(ids,0,0,ids.Length);
            using(var writer=new BinaryWriter(File.Create(Path.Combine(destination,name+".csr")))){
                writer.Write(Encoding.ASCII.GetBytes("SMCSR001"));writer.Write(expected.Length);writer.Write(ids.Length);
                foreach(uint n in offsets)writer.Write(n);foreach(uint n in ids)writer.Write(n);
            }
            uint prefix=0;
            for(int row=0;row<expected.Length;row++){
                if(offsets[row]!=prefix)throw new InvalidDataException(name+" offset "+row);
                prefix+=(uint)expected[row].Length;
                if(offsets[row+1]!=prefix)throw new InvalidDataException(name+" terminal offset "+row);
                var actual=ids.Skip((int)offsets[row]).Take(expected[row].Length).OrderBy(x=>x);
                if(!actual.SequenceEqual(expected[row].OrderBy(x=>x)))throw new InvalidDataException(name+" full IDs/multiplicity "+row);
            }
            if(prefix!=ids.Length)throw new InvalidDataException(name+" output length");
            Pass(name,"All offsets/IDs/multiplicity checked on actual GPU output");
        }
        static void Query(GpuSphereWorkloadAdapter adapter,CommandBuffer commands,SourceSphere[] queries,string name,params uint[][] expected)
        {
            adapter.UploadQueries(queries);commands.Clear();adapter.RecordQueries(commands);Graphics.ExecuteCommandBuffer(commands);Compare(adapter,name,expected);commands.Clear();
        }

        public static void Run(string output)
        {
            destination=output;results=new Results();Save();
            var domain=new SphereWorkloadDomain(-4,-4,-4,8);
            var changedDomain=new SphereWorkloadDomain(-8,-8,-8,16);
            var points=new[]{new SourcePoint(-2,0,0,10),new SourcePoint(0,0,0,20),new SourcePoint(0,0,0,21),new SourcePoint(2,0,0,30)};
            var near=new[]{new SourceSphere(-2,0,0,.25f),new SourceSphere(0,0,0,0)};
            var wide=new[]{new SourceSphere(2,0,0,.25f),new SourceSphere(0,0,0,3)};
            Reject<ArgumentOutOfRangeException>("zero-point-capacity",()=>new GpuSphereWorkloadAdapter(0,2,true));
            Reject<ArgumentOutOfRangeException>("worst-case-capacity-rejected-before-allocation",()=>new GpuSphereWorkloadAdapter(1000000,600,true));
            using(var commands=new CommandBuffer())using(var adapter=new GpuSphereWorkloadAdapter(5,2,true)){
                Reject<InvalidOperationException>("queries-before-points",()=>adapter.UploadQueries(near));
                Reject<InvalidOperationException>("build-before-points",()=>adapter.RecordIndexBuild(commands));
                Reject<InvalidOperationException>("confirm-without-build",()=>adapter.CompleteIndexBuild());
                adapter.UploadPoints(domain,points);adapter.UploadQueries(near);
                Reject<InvalidOperationException>("query-before-build",()=>adapter.RecordQueries(commands));
                Reject<ArgumentNullException>("null-build-command-buffer",()=>adapter.RecordIndexBuild(null));
                adapter.RecordIndexBuild(commands);
                Require(!adapter.IsIndexReady,"recording-is-not-completion");
                Reject<InvalidOperationException>("unsubmitted-build-not-confirmed",()=>adapter.CompleteIndexBuild());
                Reject<InvalidOperationException>("query-before-build-submission",()=>adapter.RecordQueries(commands));
                Graphics.ExecuteCommandBuffer(commands);adapter.CompleteIndexBuild();commands.Clear();
                adapter.UploadQueries(near);adapter.RecordQueries(commands);
                Reject<InvalidOperationException>("query-replacement-before-submission",()=>adapter.UploadQueries(wide));
                Reject<InvalidOperationException>("point-replacement-before-submission",()=>adapter.UploadPoints(domain,points));
                Graphics.ExecuteCommandBuffer(commands);Compare(adapter,"recorded-query-submission",new[]{new uint[]{10},new uint[]{20,21}});commands.Clear();
                Query(adapter,commands,near,"duplicate-coordinates-inclusive-zero-radius",new uint[]{10},new uint[]{20,21});
                ulong generation=adapter.PointGeneration;
                Query(adapter,commands,wide,"changed-query-reuses-points",new uint[]{30},new uint[]{10,20,21,30});
                Require(adapter.PointGeneration==generation&&adapter.IsIndexReady,"query-change-keeps-point-generation");
                Query(adapter,commands,new[]{wide[1]},"smaller-query-batch-tail",new uint[]{10,20,21,30});
                Reject<ArgumentOutOfRangeException>("oversize-query-batch",()=>adapter.UploadQueries(new SourceSphere[3]));
                Require(adapter.IsIndexReady,"invalid-query-keeps-valid-index");
                Reject<InvalidOperationException>("rejected-query-invalidates-old-batch",()=>adapter.RecordQueries(commands));
                Reject<ArgumentException>("nonfinite-query",()=>adapter.UploadQueries(new[]{new SourceSphere(float.NaN,0,0,1)}));
                Query(adapter,commands,wide,"query-recovery-without-rebuild",new uint[]{30},new uint[]{10,20,21,30});
                adapter.UploadPoints(changedDomain,points);
                Require(!adapter.IsIndexReady&&adapter.PointGeneration!=generation,"domain-replacement-invalidates-index");
                Reject<InvalidOperationException>("domain-change-requires-new-build",()=>adapter.RecordQueries(commands));
                Build(adapter,commands);Query(adapter,commands,near,"same-points-new-domain",new uint[]{10},new uint[]{20,21});
                adapter.UploadPoints(changedDomain,new[]{new SourcePoint(2,0,0,50)});Build(adapter,commands);
                Query(adapter,commands,wide,"shrinking-points-clears-removed-memberships",new uint[]{50},new uint[]{50});
                Query(adapter,commands,Array.Empty<SourceSphere>(),"empty-query-batch",Array.Empty<uint[]>());
                adapter.UploadPoints(changedDomain,Array.Empty<SourcePoint>());Build(adapter,commands);
                Query(adapter,commands,wide,"empty-points",Array.Empty<uint>(),Array.Empty<uint>());
                Reject<ArgumentException>("duplicate-point-ids",()=>adapter.UploadPoints(domain,new[]{new SourcePoint(0,0,0,1),new SourcePoint(1,0,0,1)}));
                Require(!adapter.IsIndexReady,"rejected-points-invalidate-index");
                Reject<InvalidOperationException>("no-stale-queries-after-bad-points",()=>adapter.UploadQueries(near));
                Reject<ArgumentException>("out-of-domain-points",()=>adapter.UploadPoints(new SphereWorkloadDomain(-1,-1,-1,2),points));
                Reject<ArgumentOutOfRangeException>("oversize-points",()=>adapter.UploadPoints(domain,new SourcePoint[6]));
                adapter.Upload(domain,points,near);adapter.Record(commands);Graphics.ExecuteCommandBuffer(commands);
                Compare(adapter,"legacy-convenience",new[]{new uint[]{10},new uint[]{20,21}});commands.Clear();
                adapter.Record(commands);Graphics.ExecuteCommandBuffer(commands);
                Compare(adapter,"legacy-repeat-record",new[]{new uint[]{10},new uint[]{20,21}});commands.Clear();
                Require(!adapter.IsIndexReady,"legacy-record-does-not-certify-reuse");
                Build(adapter,commands);Query(adapter,commands,wide,"legacy-to-prepared-after-completion",new uint[]{30},new uint[]{10,20,21,30});
                // A second build must not inherit the first build's completed
                // stamp when its commands are discarded without submission.
                adapter.RecordIndexBuild(commands);commands.Clear();
                Reject<InvalidOperationException>("discarded-rebuild-does-not-reuse-old-stamp",()=>adapter.CompleteIndexBuild());
                Require(!adapter.IsIndexReady,"discarded-rebuild-invalidates-readiness");
                adapter.Dispose();adapter.Dispose();
                Reject<ObjectDisposedException>("disposed-upload",()=>adapter.UploadPoints(domain,points));
                Reject<ObjectDisposedException>("disposed-query",()=>adapter.RecordQueries(commands));
                Reject<ObjectDisposedException>("disposed-completion",()=>adapter.CompleteIndexBuild());
            }
            // Fresh ownership after cancellation/disposal; no old GPU state can
            // become this instance's certified index.
            using(var commands=new CommandBuffer())using(var adapter=new GpuSphereWorkloadAdapter(5,2,true)){
                adapter.UploadPoints(domain,points);Build(adapter,commands);
                Query(adapter,commands,near,"fresh-owner-after-disposal",new uint[]{10},new uint[]{20,21});
            }
            Pass("all-fixtures-completed");
        }
    }
}
