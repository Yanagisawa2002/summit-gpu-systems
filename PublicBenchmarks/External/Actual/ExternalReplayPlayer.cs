using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Summit.ExternalWorkloads;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

namespace Summit.ActualWorkloads
{
    // Actual external snapshot replay host. No generator, native-algorithm copy,
    // autotuning, automatic profile promotion or scene-benchmark substitution.
    public sealed class ExternalReplayPlayer : MonoBehaviour
    {
        [Serializable] public sealed class Config { public string kind,manifest,expectedManifestSha256,output,sourceCommit; public bool validateOnly; }
        [Serializable] public sealed class InputFile { public string name,path,sha256,kind; }
        [Serializable] public sealed class Manifest { public InputFile[] files; public string cabanaCommit,arborxCommit; }
        [Serializable] public sealed class Row { public string name,phase,checksum; public int step,queries,ids; public bool verified; public double uploadMs,submitReadbackMs,consumeMs,hostWallMs; }
        [Serializable] public sealed class Report { public string sourceCommit,manifestSha256,kind,measurementScope,unityVersion,buildGuid,deviceName,deviceVersion,cpu,status,error; public int vendorId,deviceId,logicalCpuCount; public List<Row> rows=new List<Row>(); }
        sealed class Csr { public uint[] offsets,ids; }
        sealed class LinkedInput { public LinkedCellWorkloadContract contract; public LinkedCellPoint[] points; public Csr reference; public string name; }
        Config config;Report report;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot(){new GameObject("External full-CSR replay").AddComponent<ExternalReplayPlayer>();}
        IEnumerator Start()
        {
            yield return null;yield return null;
            try {
                var args=Environment.GetCommandLineArgs();int at=Array.IndexOf(args,"-actual-config");
                if(at<0||at+1>=args.Length)throw new ArgumentException("-actual-config is required");
                config=JsonUtility.FromJson<Config>(File.ReadAllText(args[at+1]));
                if(Directory.Exists(config.output))throw new IOException("Output exists; preserve the prior run.");
                Directory.CreateDirectory(config.output);
                report=new Report{sourceCommit=config.sourceCommit,manifestSha256=Hash(config.manifest),kind=config.kind,
                    measurementScope=config.validateOnly?"correctness only; no measured samples":"host wall: staging through complete CSR consumption; no GPU/frame/presentation timer",
                    unityVersion=Application.unityVersion,buildGuid=Application.buildGUID,deviceName=SystemInfo.graphicsDeviceName,
                    deviceVersion=SystemInfo.graphicsDeviceVersion,cpu=SystemInfo.processorType,vendorId=SystemInfo.graphicsDeviceVendorID,
                    deviceId=SystemInfo.graphicsDeviceID,logicalCpuCount=SystemInfo.processorCount,status="running"};
                if(report.manifestSha256!=config.expectedManifestSha256)throw new InvalidDataException("Frozen input manifest SHA mismatch");
                if(!SystemInfo.supportsComputeShaders||SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D12)throw new NotSupportedException("Actual D3D12 compute required; no CPU fallback in this comparison");
                var manifest=JsonUtility.FromJson<Manifest>(File.ReadAllText(config.manifest));
                foreach(var file in manifest.files)if(Hash(file.path)!=file.sha256)throw new InvalidDataException("Input SHA mismatch: "+file.name);
                if(config.kind=="cabana")RunLinked(manifest);else if(config.kind=="arborx")RunSphere(manifest);else throw new ArgumentException("Unknown workload");
                report.status="completed";Save();Debug.Log("PASS actual GPU full CSR: "+config.kind);Application.Quit(0);
            }catch(Exception error){
                Debug.LogException(error);
                if(report!=null){report.status="failed";report.error=error.ToString();Save();}
                Application.Quit(2);
            }
        }
        void Save(){File.WriteAllText(Path.Combine(config.output,"result.json"),JsonUtility.ToJson(report,true));}
        static string Hash(string path){using(var sha=SHA256.Create())using(var file=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();}
        static double Ms(long a,long b)=>(b-a)*1000.0/Stopwatch.Frequency;
        long Tick()=>config.validateOnly?0:Stopwatch.GetTimestamp();
        static ulong Consume(Csr csr){unchecked {ulong sum=0;for(int row=0;row+1<csr.offsets.Length;row++)for(uint i=csr.offsets[row];i<csr.offsets[row+1];i++)sum+=((ulong)(row+1)*0x9e3779b1UL)^((ulong)csr.ids[i]+1);return sum;}}
        static void Equal(Csr actual,Csr expected)
        {
            if(!actual.offsets.SequenceEqual(expected.offsets)||actual.ids.Length!=expected.ids.Length)throw new InvalidDataException("Full CSR offsets differ");
            var a=(uint[])actual.ids.Clone();var e=(uint[])expected.ids.Clone();
            for(int q=0;q+1<actual.offsets.Length;q++){int start=checked((int)actual.offsets[q]),count=checked((int)(actual.offsets[q+1]-actual.offsets[q]));Array.Sort(a,start,count);Array.Sort(e,start,count);}
            if(!a.SequenceEqual(e))throw new InvalidDataException("Full CSR IDs/multiplicity differ");
        }
        static Csr Readback(GraphicsBuffer offsets,GraphicsBuffer ids,int rows)
        {
            var result=new Csr{offsets=new uint[rows+1]};offsets.GetData(result.offsets,0,0,rows+1);
            if(result.offsets[0]!=0||result.offsets[rows]>ids.count)throw new InvalidDataException("GPU CSR bound/header failure");
            for(int i=1;i<=rows;i++)if(result.offsets[i]<result.offsets[i-1])throw new InvalidDataException("GPU CSR offsets not monotone");
            result.ids=new uint[result.offsets[rows]];if(result.ids.Length>0)ids.GetData(result.ids,0,0,result.ids.Length);return result;
        }
        void Record(string name,int step,Csr actual,Csr expected,long start,long uploaded,long readback,long end,ulong checksum,double? uploadOverride=null,double? queueOverride=null)
        {
            string phase=config.validateOnly?"validation":step<0?"warmup":"measured";
            // Retain raw actual GPU output before equality can fail.
            using(var writer=new BinaryWriter(File.Create(Path.Combine(config.output,name+"-"+phase+"-"+step+".csr")))){
                writer.Write(Encoding.ASCII.GetBytes("SMCSR001"));writer.Write(actual.offsets.Length-1);writer.Write(actual.ids.Length);
                foreach(uint value in actual.offsets)writer.Write(value);foreach(uint value in actual.ids)writer.Write(value);
            }
            var row=new Row{name=name,phase=phase,step=step,queries=actual.offsets.Length-1,ids=actual.ids.Length,checksum=checksum.ToString(CultureInfo.InvariantCulture),
                uploadMs=config.validateOnly?-1:uploadOverride??Ms(start,uploaded),submitReadbackMs=config.validateOnly?-1:queueOverride??Ms(uploaded,readback),
                consumeMs=config.validateOnly?-1:Ms(readback,end),hostWallMs=config.validateOnly?-1:Ms(start,end)};
            report.rows.Add(row);Save();Equal(actual,expected);row.verified=true;Save();
        }
        static LinkedInput ReadLinked(InputFile file)
        {
            using(var reader=new BinaryReader(File.OpenRead(file.path))){
                if(Encoding.ASCII.GetString(reader.ReadBytes(8))!="SMLCL001")throw new InvalidDataException("Linked snapshot ABI");
                int n=reader.ReadInt32(),bins=reader.ReadInt32();if(n<1||n>1000||bins<1||bins>1000)throw new InvalidDataException("Unexpected frozen default case");
                var lo=new double[3];var hi=new double[3];var width=new double[3];foreach(var array in new[]{lo,hi,width})for(int i=0;i<3;i++)array[i]=reader.ReadDouble();
                var value=new LinkedInput{name=file.name,contract=new LinkedCellWorkloadContract(lo,hi,width),points=new LinkedCellPoint[n],reference=new Csr{offsets=new uint[bins+1],ids=new uint[n]}};
                if(value.contract.BinCount!=bins)throw new InvalidDataException("Native/SUMMIT grid shape differs");
                for(int i=0;i<n;i++)value.points[i]=new LinkedCellPoint(reader.ReadDouble(),reader.ReadDouble(),reader.ReadDouble(),reader.ReadUInt32());
                for(int i=0;i<=bins;i++)value.reference.offsets[i]=reader.ReadUInt32();for(int i=0;i<n;i++)value.reference.ids[i]=reader.ReadUInt32();
                if(reader.BaseStream.Position!=reader.BaseStream.Length)throw new InvalidDataException("Trailing input bytes");return value;
            }
        }
        void RunLinked(Manifest manifest)
        {
            if(manifest.cabanaCommit!="dd6bd7ccbb28974f81c365ff6b1fd1f3a080b802")throw new InvalidDataException("Native source pin mismatch");
            var groups=manifest.files.Where(f=>f.kind=="cabana").Select(ReadLinked).GroupBy(f=>f.name.Substring(0,f.name.LastIndexOf("-t",StringComparison.Ordinal))).OrderBy(g=>g.Key).ToArray();
            if(groups.Length!=4)throw new InvalidDataException("Expected all four native default cases");
            foreach(var group in groups){var inputs=group.OrderBy(f=>f.name).ToArray();if(inputs.Length!=10)throw new InvalidDataException("Expected all ten original iterations");
                using(var adapter=new GpuLinkedCellWorkloadAdapter(inputs[0].contract,inputs[0].points.Length,true))using(var commands=new CommandBuffer()){
                    for(int step=config.validateOnly?0:-10;step<10;step++){
                        var input=inputs[step<0?0:step];commands.Clear();long start=Tick();adapter.Upload(input.points);long uploaded=Tick();adapter.Record(commands);Graphics.ExecuteCommandBuffer(commands);
                        var actual=Readback(adapter.Offsets,adapter.Ids,input.contract.BinCount);long readback=Tick();ulong checksum=Consume(actual);long end=Tick();
                        Record(group.Key,step,actual,input.reference,start,uploaded,readback,end,checksum);
                    }
                }
            }
        }
        void RunSphere(Manifest manifest)
        {
            var file=manifest.files.Single(f=>f.kind=="arborx");ArborXSnapshot input;
            using(var stream=File.OpenRead(file.path))input=ArborXSnapshot.Read(stream,manifest.arborxCommit,file.sha256);
            if(input.Points.Length!=50000||input.Spheres.Length!=20000)throw new InvalidDataException("Native default size changed");
            var expected=new Csr{offsets=new uint[20001],ids=input.ReferenceMembership.SelectMany(x=>x).ToArray()};
            for(int q=0;q<20000;q++)expected.offsets[q+1]=expected.offsets[q]+(uint)input.ReferenceMembership[q].Length;
            const int batchSize=128;var batches=new List<SourceSphere[]>();for(int q=0;q<20000;q+=batchSize)batches.Add(input.Spheres.Skip(q).Take(Math.Min(batchSize,20000-q)).ToArray());
            double extent=Math.Ceiling(Math.Pow(input.Points.Length,1.0/3.0));var domain=new SphereWorkloadDomain(-extent,-extent,-extent,2*extent);
            using(var adapter=new GpuSphereWorkloadAdapter(50000,batchSize,true))using(var commands=new CommandBuffer()){
                for(int step=config.validateOnly?0:-10;step<(config.validateOnly?1:10);step++){
                    long start=Tick();double upload=0,queue=0;var actual=new Csr{offsets=new uint[20001]};var allIds=new List<uint>();int row=0;
                    foreach(var batch in batches){commands.Clear();long a=Tick();adapter.Upload(domain,input.Points,batch);long b=Tick();adapter.Record(commands);Graphics.ExecuteCommandBuffer(commands);var current=Readback(adapter.Offsets,adapter.Ids,batch.Length);long c=Tick();upload+=Ms(a,b);queue+=Ms(b,c);
                        uint baseId=(uint)allIds.Count;for(int i=0;i<batch.Length;i++)actual.offsets[row+i+1]=baseId+current.offsets[i+1];row+=batch.Length;allIds.AddRange(current.ids);
                    }
                    actual.ids=allIds.ToArray();long readback=Tick();ulong checksum=Consume(actual);long end=Tick();
                    Record("arborx-default",step,actual,expected,start,start,readback,end,checksum,upload,queue);
                }
            }
        }
    }
}
