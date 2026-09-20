using System;
using System.Collections.Generic;
using Summit.VisibleTiles;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

static class Program
{
    static int assertions;
    static void Check(bool condition) { ++assertions; if(!condition) throw new Exception("Assertion failed "+assertions); }
    static void Reject(Action f) { bool rejected=false; try { f(); } catch(ArgumentException) {rejected=true;} Check(rejected); }
    static void Main()
    {
        foreach(string file in Directory.GetFiles("PublicBenchmarks/VisibleTiles/Source", "*.cs"))
        {
            var tree=CSharpSyntaxTree.ParseText(File.ReadAllText(file), new CSharpParseOptions(LanguageVersion.CSharp9));
            Check(!tree.GetDiagnostics().Any(d=>d.Severity==DiagnosticSeverity.Error));
            foreach(var y in tree.GetRoot().DescendantNodes().OfType<YieldStatementSyntax>())
                Check(!y.Ancestors().Any(n=>n is CatchClauseSyntax || n is FinallyClauseSyntax));
        }
        // Host files are syntax-checked, NOT compiled against Unity assemblies.
        P3 back=new P3(0,0,-10), front=new P3(0,0,18);
        foreach(int n in new[]{0,1,127,128,129,257}) foreach(bool sparse in new[]{false,true})
        {
            Fixture f=Fixture.Create(n,sparse);
            bool[] union=f.Visibility(new[]{back,front},front,true);
            bool[] reversed=f.Visibility(new[]{front,back},front,true);
            for(int i=0;i<n;++i) { Check(union[i]); Check(union[i]==reversed[i]); }
            foreach(int tile in new[]{0,32,64}) foreach(bool flip in new[]{false,true})
            {
                Reject(()=>f.RequireCapacity(f.RequiredWords(tile)-1,tile));
                foreach(P3[] views in new[]{new[]{front},new[]{back},new[]{back,front},Array.Empty<P3>()})
                {
                    bool[] visible=f.Visibility(views,front,true);
                    uint[] expected=f.Expected(visible,flip);
                    var output=new List<uint>();
                    // Reverse cluster order to exercise nondeterministic wave reservation order.
                    for(int i=n-1;i>=0;--i) if(visible[i])
                    {
                        if(tile==0)
                        {
                            for(int j=0;j<f.Counts[i];j+=3)
                            { int s=f.Starts[i]+j; output.Add(f.Indices[s]); output.Add(f.Indices[s+(flip?2:1)]); output.Add(f.Indices[s+(flip?1:2)]); }
                        }
                        else for(int j=0;j<f.Counts[i];j+=tile*3)
                        { output.Add((uint)(f.Starts[i]+j)); output.Add((uint)Math.Min(tile*3,f.Counts[i]-j)); }
                    }
                    uint[] decoded=f.Decode(output.ToArray(),(uint)f.ExpectedDrawCount(visible,tile),tile,flip);
                    Fixture.RequireEqualTriangles(expected,decoded); Check(decoded.Length==expected.Length);
                }
            }
        }
        Fixture one=Fixture.Create(1,false);
        Check(one.Visibility(new[]{back,new P3(0,0,0)},front,true)[0]);
        Check(one.Visibility(new[]{back},front,false)[0]);
        Check(!one.Visibility(new[]{back},front,true)[0]);
        Check(one.Visibility(Array.Empty<P3>(),front,true)[0]);
        Reject(()=>one.Visibility(new P3[9],front,true));
        Reject(()=>one.Decode(new uint[]{0,4},96,32,false));
        Reject(()=>one.Decode(new uint[]{uint.MaxValue,3},96,32,false));
        Reject(()=>one.Decode(new uint[]{0,0},96,32,false));
        Reject(()=>one.Decode(new uint[]{0,3},95,32,false));
        Reject(()=>one.Decode(Array.Empty<uint>(),3,0,false));
        bool membershipRejected=false;
        try { Fixture.RequireEqualTriangles(new uint[]{0,1,2},new uint[]{0,2,1}); }
        catch(InvalidOperationException) { membershipRejected=true; } Check(membershipRejected);
        Console.WriteLine("PASS "+assertions+" actual C# fixture/oracle assertions; no GPU execution.");
    }
}
