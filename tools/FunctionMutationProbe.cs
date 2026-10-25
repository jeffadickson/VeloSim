#:project ../src/VeloSim.Core/VeloSim.csproj
#:property NuGetLockFilePath=FunctionMutationProbe.packages.lock.json
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using VeloSim;

// Exporter faults are confined to copied sources; the production exporter is unchanged.
// Rebuild and hash test binaries before control. Infrastructure failures never count as kills.
if(args.Length!=1)throw new ArgumentException("usage: FunctionMutationProbe <checkout>");
string root=Path.GetFullPath(args[0]),run=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff");
string evidence=Path.Combine(root,"artifacts/functions-100/mutations",run);
string workspace=Path.Combine("/home/jeffd/.local/share/VeloSim/functions100/mutations",run);
Directory.CreateDirectory(evidence);Directory.CreateDirectory(workspace);
string sources=Path.Combine(workspace,"source");Directory.CreateDirectory(sources);
foreach(string file in Directory.GetFiles(Path.Combine(root,"native/uhdm")))File.Copy(file,Path.Combine(sources,Path.GetFileName(file)));
string header=Path.Combine(sources,"rtl_subroutines.h"),original=File.ReadAllText(header);
var frozen=Directory.GetFiles(Path.Combine(root,"native/uhdm")).Concat(Directory.GetFiles(Path.Combine(root,"native/rtl"))).Concat(Directory.GetFiles(Path.Combine(root,"src/VeloSim.Core"),"*.cs")).Concat(Directory.GetFiles(Path.Combine(root,"tests/VeloSim.Tests"),"*.cs")).Concat(new[]{"src/VeloSim.Core/VeloSim.csproj","tests/VeloSim.Tests/VeloSim.Tests.csproj","Directory.Build.props","Directory.Build.targets","Directory.Packages.props","global.json","NuGet.config","coverage.runsettings"}.Select(p=>Path.Combine(root,p)).Where(File.Exists)).ToDictionary(p=>p,Hash);
var binaries=new Dictionary<string,string>();
void Verify(){foreach(var pair in frozen)if(Hash(pair.Key)!=pair.Value)throw new IOException("Source changed: "+pair.Key);}
string build=Path.Combine(workspace,"build"),binary=Path.Combine(build,"velosim-uhdm-rtl");
var runner=new ProcessRunner(4*1024*1024);var observations=new List<object>();
var mutations=new[]{
    new Mutation("return-falls-through","need(call_locals.at(\"$return\").assigned,\"function return requires assigned result or expression\");return;","need(call_locals.at(\"$return\").assigned,\"function return requires assigned result or expression\");continue;",24),
    new Mutation("repeat-off-by-one","std::vector<H> expanded(size_t(count->bits),one(h,vpiStmt));","std::vector<H> expanded(size_t(count->bits)+1,one(h,vpiStmt));",43),
    new Mutation("erase-captured-constant","if(b.size()>size_t(w))b=b.substr(b.size()-w);","b=std::string(b.size(),'0');if(b.size()>size_t(w))b=b.substr(b.size()-w);",76)
};
try{
    Verify();await Command("dotnet",["build","tests/VeloSim.Tests","-c","Release","-m:4"],"test-build");Verify();
    foreach(string path in new[]{"tests/VeloSim.Tests/bin/Release/net10.0/VeloSim.Tests.dll","tests/VeloSim.Tests/bin/Release/net10.0/VeloSim.dll"})binaries.Add(Path.Combine(root,path),Hash(Path.Combine(root,path)));
    await Command("cmake",["-S",sources,"-B",build,"-G","Ninja","-DCMAKE_BUILD_TYPE=Release","-DCMAKE_PREFIX_PATH=/opt/velosim"],"configure");
    await Compile("control");
    await Test("control",string.Join('|',mutations.Select(m=>$"TestCategory=Functions100_{m.Case:000}")),3,false);
    foreach(var mutation in mutations){
        Verify();if(original.Split(mutation.Before,StringSplitOptions.None).Length!=2)throw new IOException("Mutation target drift: "+mutation.Name);
        string changed=original.Replace(mutation.Before,mutation.After,StringComparison.Ordinal);File.WriteAllText(header,changed);File.WriteAllText(Path.Combine(evidence,mutation.Name+".h"),changed);
        await Compile(mutation.Name);await Test(mutation.Name,$"TestCategory=Functions100_{mutation.Case:000}",1,true);
    }
}finally{
    Verify();File.WriteAllText(header,original);
    // Explicit writer keeps the helper compatible with NativeAOT without reflection serialization.
    using var file=File.Create(Path.Combine(evidence,"report.json"));using var json=new Utf8JsonWriter(file,new(){Indented=true});
    json.WriteStartObject();json.WriteString("scope","Three focused exporter faults; no whole-program mutation score");json.WriteString("workspace",workspace);json.WriteStartObject("frozen_inputs");foreach(var pair in frozen)json.WriteString(pair.Key,pair.Value);json.WriteEndObject();json.WriteStartObject("loaded_binaries");foreach(var pair in binaries)json.WriteString(pair.Key,pair.Value);json.WriteEndObject();json.WriteStartArray("runs");foreach(var item in observations.Cast<Observation>()){json.WriteStartObject();json.WriteString("name",item.Name);json.WriteNumber("total",item.Total);json.WriteNumber("passed",item.Passed);json.WriteNumber("failed",item.Failed);json.WriteBoolean("semantic_failure",item.Semantic);json.WriteString("binary_sha256",item.Binary);json.WriteString("header_sha256",item.Header);json.WriteEndObject();}json.WriteEndArray();json.WriteEndObject();
}
Console.WriteLine("Mutation evidence: "+evidence);
static string Hash(string file)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
async Task Command(string executable,string[] arguments,string tag){
    var result=await runner.RunAsync(executable,arguments,root,TimeSpan.FromMinutes(20));File.WriteAllText(Path.Combine(evidence,tag+".log"),result.StandardOutput+result.StandardError);
    if(result.ExitCode!=0)throw new IOException("Infrastructure failure: "+tag+" "+result.StandardOutput+result.StandardError);
}
async Task Compile(string tag){await Command("cmake",["--build",build,"--target","velosim-uhdm-rtl","--parallel","4"],tag+"-build");File.Copy(binary,Path.Combine(evidence,tag+"-exporter"),true);}
async Task Test(string name,string filter,int expected,bool mutant){
    Verify();foreach(var pair in binaries)if(Hash(pair.Key)!=pair.Value)throw new IOException("Measured test binary changed: "+pair.Key);
    Environment.SetEnvironmentVariable("VELOSIM_REAL_FIFO","1");Environment.SetEnvironmentVariable("VELOSIM_UHDM_RTL",binary);string fixtures=Path.Combine(workspace,name);Environment.SetEnvironmentVariable("VELOSIM_RTL_EVIDENCE_ROOT",fixtures);
    string results=Path.Combine(evidence,name);Directory.CreateDirectory(results);
    var result=await runner.RunAsync("dotnet",["test","tests/VeloSim.Tests","-c","Release","--no-build","--filter",filter,"--results-directory",results,"--logger","trx;LogFileName=results.trx"],root,TimeSpan.FromMinutes(20));File.WriteAllText(Path.Combine(results,"output.log"),result.StandardOutput+result.StandardError);
    var trx=XDocument.Load(Path.Combine(results,"results.trx"));var counters=trx.Descendants().Single(e=>e.Name.LocalName=="Counters");int total=(int)counters.Attribute("total")!,passed=(int)counters.Attribute("passed")!,failed=(int)counters.Attribute("failed")!;
    bool semantic=trx.ToString().Contains("RTL_TRACE_MISMATCH",StringComparison.Ordinal);
    if(mutant){
        var commands=Directory.GetFiles(fixtures,"commands.json",SearchOption.AllDirectories);if(commands.Length!=1)throw new IOException("Missing native fixture command evidence");
        using var doc=JsonDocument.Parse(File.ReadAllText(commands[0]));var entries=doc.RootElement.EnumerateArray().ToArray();semantic&=entries.All(e=>e.GetProperty("ExitCode").GetInt32()==0)&&entries[^1].GetProperty("Executable").GetString()!.EndsWith("/build/indexed",StringComparison.Ordinal);
    }
    observations.Add(new Observation(name,total,passed,failed,semantic,Hash(binary),Hash(header)));
    if(total!=expected||(int)counters.Attribute("executed")! != expected||(!mutant&&(result.ExitCode!=0||passed!=expected||failed!=0))||(mutant&&(result.ExitCode==0||failed!=1||!semantic)))throw new IOException("Invalid semantic mutation observation: "+name);
    Console.WriteLine($"{name}: {passed} passed, {failed} failed, semantic {semantic}");
}
record Mutation(string Name,string Before,string After,int Case);
record Observation(string Name,int Total,int Passed,int Failed,bool Semantic,string Binary,string Header);