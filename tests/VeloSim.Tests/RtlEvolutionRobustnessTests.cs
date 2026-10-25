using FsCheck;
using FsCheck.Fluent;
using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace VeloSim.Tests;

/// <summary>Reproducible structural guards and copied-header semantic mutations.</summary>
[NonParallelizable]
public class RtlEvolutionRobustnessTests
{
    static string Root(){for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d is not null;d=d.Parent)if(File.Exists(Path.Combine(d.FullName,"native/rtl/runtime.h")))return d.FullName;throw new IOException("Checkout missing");}
    static object Ref(string name)=>new{kind="ref",name};
    static object Block(params object[] items)=>new{kind="block",items};
    static JsonNode Fixture()=>JsonNode.Parse(JsonSerializer.Serialize(new{format="velosim.rtl.v1",clock="clk",signals=new[]{new{name="clk",width=1,direction="input"},new{name="r",width=32,direction="input"},new{name="c",width=32,direction="input"},new{name="q",width=8,direction="output"}},memories=new[]{new{name="m",width=8,depth=6,dimensions=new[]{new{left=1,right=2},new{left=3,right=5}}}},continuous=Array.Empty<object>(),body=new{kind="nba",target=Ref("q"),value=new{kind="index",name="m",indices=new[]{Ref("r"),Ref("c")}}}}))!;
    static bool Rejects(JsonNode node){try{RtlDesign.Parse(node.ToJsonString());return false;}catch(IrValidationException){return true;}}

    [Test]
    public void MalformedShapeProperty()
    {
        Prop.ForAll<byte>(shape=>{var node=Fixture();var memory=node["memories"]![0]!;
            switch(shape%10){case 0:memory["depth"]=7;break;case 1:memory["dimensions"]![0]!["left"]=-1;break;case 2:memory["dimensions"]![1]!["right"]=int.MaxValue;break;case 3:memory["lower"]=1;break;case 4:node["body"]!["value"]!["indices"]!.AsArray().RemoveAt(0);break;case 5:node["body"]!["value"]!["index"]=JsonSerializer.SerializeToNode(Ref("r"));break;case 6:memory["dimensions"]![1]!["right"]=long.MaxValue;break;case 7:memory["dimensions"]![1]!["right"]=-1;break;case 8:memory.AsObject().Remove("dimensions");break;default:memory["dimensions"]=new JsonArray();break;}
            return Rejects(node);
        }).Label("EVOLUTION_SHAPE_GUARD").Check(Config.QuickThrowOnFailure.WithMaxTest(500).WithReplay(20261028,864013));
    }

    [Test]
    public void MalformedStartupAndLoopFuzz()
    {
        const int seed=20261029;var random=new Random(seed);
        for(int trial=0;trial<500;++trial){var node=Fixture();object init=random.Next(5) switch{0=>new{kind="runtime_repeat",count=Ref("r"),body=Block()},1=>new{kind="while",condition=Ref("c"),body=Block()},2=>new{kind="nba",target=Ref("q"),value=Ref("r")},3=>new{kind="do",condition=Array.Empty<object>(),body=Block()},_=>new{kind="runtime_repeat",count=-1,body=Block()}};node["initialization"]=JsonSerializer.SerializeToNode(init);Assert.That(Rejects(node),Is.True,$"seed {seed} trial {trial}");}
        TestContext.Progress.WriteLine($"EVOLUTION_FUZZ 500 trials seed {seed}");
    }

    [Test]
    public async Task FocusedCopiedRuntimeMutationsDetectSemanticFaults()
    {
        if(!OperatingSystem.IsLinux())Assert.Ignore("Native C++ qualification runs in Linux/WSL");
        string root=Root(),header=Path.Combine(root,"native/rtl/runtime.h"),original=File.ReadAllText(header),hash=ArtifactMetadata.Fingerprint(header).Sha256;
        string id=Guid.NewGuid().ToString("N"),workspace=Path.Combine("/home/jeffd/.local/share/VeloSim/procedural-types-arrays/native-mutations",id),evidence=Path.Combine(root,"artifacts/procedural-types-arrays/native-mutations",id);Directory.CreateDirectory(evidence);
        const string program="""
            #include "runtime.h"
            #include <iostream>
            using namespace vsrtl;
            int main(){
              std::cout<<text(arithmetic(parse(std::string(128,'1')),word(1,0,0,129),129,false))<<'\n';
              std::cout<<text(array_index({word(2,0,0,32),word(3,0,0,32)},{{1,2},{3,5}}))<<'\n';
              std::vector<W> memory;for(int i=0;i<6;++i)memory.push_back(word(20+i,0,0,8));
              std::cout<<text(read(memory,array_index({word(1,0,0,32),word(6,0,0,32)},{{1,2},{3,5}}),8))<<'\n';
              std::cout<<text(two_state(parse("xz10")))<<'\n';
            }
            """;
        string expected="1"+new string('0',128)+"\n"+new string('0',62)+"11\nxxxxxxxx\n0010\n";
        var faults=new[]{("carry","carry>>=64;}return a;}","carry=0;}return a;}"),("stride","offset=offset*size+uint64_t(value.v)-lower;","offset=offset+uint64_t(value.v)-lower;"),("coordinate","||value.v>Bits(upper)",""),("two-state","inline W two_state(W value){return word(value.v,0,0,value.w);}","inline W two_state(W value){return value;}")};
        var observations=new List<object>();
        async Task Run(string name,string text,bool control){
            Assert.That(ArtifactMetadata.Fingerprint(header).Sha256,Is.EqualTo(hash));string path=Path.Combine(workspace,name);Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"runtime.h"),text);File.WriteAllText(Path.Combine(path,"main.cpp"),program);File.WriteAllText(Path.Combine(path,"CMakeLists.txt"),"cmake_minimum_required(VERSION 3.20)\nproject(mutation LANGUAGES CXX)\nset(CMAKE_CXX_STANDARD 20)\nadd_executable(probe main.cpp)\n");
            var runner=new RecordingProcessRunner(new ProcessRunner());
            async Task<ProcessResult> Command(string exe,params string[] args){var result=await runner.RunAsync(exe,args,path,TimeSpan.FromMinutes(3));Assert.That(result.ExitCode,Is.Zero,result.StandardOutput+result.StandardError);return result;}
            await Command("cmake","-S",path,"-B",Path.Combine(path,"build"),"-G","Ninja","-DCMAKE_BUILD_TYPE=Release");await Command("cmake","--build",Path.Combine(path,"build"),"--parallel","4");var execution=await Command(Path.Combine(path,"build/probe"));
            if(control)Assert.That(execution.StandardOutput,Is.EqualTo(expected));else Assert.That(execution.StandardOutput,Is.Not.EqualTo(expected),"Semantic mutant survived "+name);
            File.WriteAllText(Path.Combine(evidence,name+".commands.json"),JsonSerializer.Serialize(runner.Commands));File.WriteAllText(Path.Combine(evidence,name+".actual.txt"),execution.StandardOutput);
            observations.Add(new{name,control,successful_commands=runner.Commands.All(c=>c.ExitCode==0),semantic_difference=execution.StandardOutput!=expected,header_sha256=ArtifactMetadata.Fingerprint(Path.Combine(path,"runtime.h")).Sha256,binary=ArtifactMetadata.Fingerprint(Path.Combine(path,"build/probe"))});
        }
        await Run("control",original,true);foreach(var fault in faults){Assert.That(original.Split(fault.Item2,StringSplitOptions.None).Length-1,Is.EqualTo(1),"Mutation target drift");await Run(fault.Item1,original.Replace(fault.Item2,fault.Item3,StringComparison.Ordinal),false);}
        Assert.That(ArtifactMetadata.Fingerprint(header).Sha256,Is.EqualTo(hash));File.WriteAllText(Path.Combine(evidence,"expected.txt"),expected);File.WriteAllText(Path.Combine(evidence,"report.json"),JsonSerializer.Serialize(new{scope="four focused native semantic faults; not a whole-program mutation score",root_header_sha256=hash,workspace,observations}));
    }
}
