#:project ../src/VeloSim.Core/VeloSim.csproj
// C# file-based qualification app. Run: dotnet run --file tools/CombinationalMutationProbe.cs -- <checkout>
// Mutations are confined to a copied Core project; the working source and built solution are never replaced.
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VeloSim;

if(args.Length>1)throw new ArgumentException("Usage: CombinationalMutationProbe [checkout]");
string root=Path.GetFullPath(args.Length==1?args[0]:Environment.CurrentDirectory);
string source=Path.Combine(root,"src/VeloSim.Core/RtlDesign.cs");
if(!File.Exists(source))throw new ArgumentException("A VeloSim checkout is required.");
string output=Path.Combine(root,"artifacts/functions-100/core-mutations",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff",CultureInfo.InvariantCulture));
Directory.CreateDirectory(output);
string core=Path.Combine(output,"Core"),probe=Path.Combine(output,"Tests");
CopySources(Path.GetDirectoryName(source)!,core);Directory.CreateDirectory(probe);
File.Copy(Path.Combine(root,"Directory.Build.props"),Path.Combine(output,"Directory.Build.props"));
// This isolated project reuses actual test sources and dependency pins, avoiding a separate test oracle.
var packages=XDocument.Load(Path.Combine(root,"tests/VeloSim.Tests/VeloSim.Tests.csproj")).Descendants("PackageReference").Select(element=>new XElement(element));
string[] testFiles=["RtlCombinational200Tests.cs","RtlCombinationalRobustnessTests.cs","RtlIndexedConformanceTests.cs","RtlFunctions100Tests.cs"];
foreach(string file in testFiles)File.Copy(Path.Combine(root,"tests/VeloSim.Tests",file),Path.Combine(probe,file));
var project=new XDocument(new XElement("Project",new XAttribute("Sdk","Microsoft.NET.Sdk"),
    new XElement("PropertyGroup",new XElement("AssemblyName","VeloSim.Tests"),new XElement("IsTestProject","true"),new XElement("IsPackable","false"),new XElement("TestingPlatformDotnetTestSupport","false")),
    new XElement("ItemGroup",packages,new XElement("ProjectReference",new XAttribute("Include",Path.Combine(core,"VeloSim.csproj"))))));
string projectFile=Path.Combine(probe,"Probe.csproj");project.Save(projectFile);
string original=File.ReadAllText(source),copy=Path.Combine(core,"RtlDesign.cs");
string baselineHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
var frozen=new[]{"src/VeloSim.Core/RtlDesign.cs","src/VeloSim.Core/RtlCombinational.cs","src/VeloSim.Core/RtlConversion.cs","tests/VeloSim.Tests/RtlCombinational200Tests.cs","tests/VeloSim.Tests/RtlCombinationalRobustnessTests.cs","tests/VeloSim.Tests/RtlIndexedConformanceTests.cs","tests/VeloSim.Tests/RtlFunctions100Tests.cs"}.ToDictionary(file=>file,file=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,file)))));
void VerifyFrozen(){foreach(var pair in frozen)if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,pair.Key))))!=pair.Value)throw new IOException("Source changed during mutations: "+pair.Key);}
var mutations=new[]{
 new Mutation("disable-clockless-body-guard","RtlDesign.cs","if (CombinationalOnly && (Kind(Body)","if (CombinationalOnly && Environment.GetEnvironmentVariable(\"VELOSIM_MUTANT_KEEP_GUARD\")==\"1\" && (Kind(Body)","FullyQualifiedName~HiddenClocklessBodyProperty","CLOCKLESS_EMPTY_BODY",0),
 new Mutation("omit-settling","RtlDesign.cs","EmitSettle(code);","code.AppendLine(\"/* omitted settle */\");","TestCategory=Comb200_001","RTL_TRACE_MISMATCH",1),
 new Mutation("disable-definite-assignment","RtlCombinational.cs","if(!owned.SetEquals(assigned))","if(!owned.SetEquals(assigned)&&Environment.GetEnvironmentVariable(\"VELOSIM_MUTANT_KEEP_GUARD\")==\"1\")","FullyQualifiedName~MissingBranchAssignmentProperty","COMB_DEFINITE_ASSIGNMENT",0),
 new Mutation("disable-read-before-write","RtlCombinational.cs","if(References(expression).Any(n=>owned.Contains(n)&&!assigned.Contains(n)))","if(References(expression).Any(n=>owned.Contains(n)&&!assigned.Contains(n))&&Environment.GetEnvironmentVariable(\"VELOSIM_MUTANT_KEEP_GUARD\")==\"1\")","FullyQualifiedName~SelfReadBeforeWriteProperty","COMB_READ_BEFORE_WRITE",0),
 new Mutation("corrupt-second-copy-out","RtlDesign.cs","""WordWrite(code, leaf, $"select_part({rhs},{remaining + width - 1},{remaining})");""","""WordWrite(code, leaf, remaining==0?$"unknown({width})":$"select_part({rhs},{remaining + width - 1},{remaining})");""","TestCategory=Comb200_101","RTL_TRACE_MISMATCH",101),
 new Mutation("omit-conversion","RtlConversion.cs","return e.GetProperty(\"two_state\").GetBoolean()?$\"two_state({value})\":value;","return Expression(e.GetProperty(\"value\"));","TestCategory=Comb200_161","RTL_TRACE_MISMATCH",161)
};string controlFilter=string.Join('|',mutations.Select(mutation=>mutation.Filter));
var results=new List<Observation>();
try{
    var control=await Run("control",controlFilter);results.Add(control);
    if(control.ExitCode!=0||control.Passed!=mutations.Length||control.Failed!=0)throw new InvalidOperationException("Mutation control must pass all selected tests.");
    foreach(var mutation in mutations){VerifyFrozen();
        foreach(var file in new[]{"RtlDesign.cs","RtlCombinational.cs","RtlConversion.cs"})File.Copy(Path.Combine(root,"src/VeloSim.Core",file),Path.Combine(core,file),true);
        string text=File.ReadAllText(Path.Combine(core,mutation.File));
        int occurrences=text.Split(mutation.Before,StringSplitOptions.None).Length-1;
        if(occurrences!=(mutation.Name=="omit-settling"?2:1))throw new InvalidOperationException("Mutation target drift: "+mutation.Name);
        string changed=text.Replace(mutation.Before,mutation.After,StringComparison.Ordinal);File.WriteAllText(Path.Combine(core,mutation.File),changed);File.WriteAllText(Path.Combine(output,mutation.Name+".cs"),changed);
        var result=await Run(mutation.Name,mutation.Filter);
        bool semantic=result.Failure.Contains(mutation.FailureMarker,StringComparison.Ordinal);
        if(mutation.NativeCase!=0)semantic&=NativeFailureEvidence(result.Failure);
        else semantic&=result.Failure.Contains("Falsifiable",StringComparison.Ordinal)&&Regex.IsMatch(result.Failure,@"with exception:\r?\nSystem\.Exception: Expected true, got false\.(?:\r?\n|$)");        result=result with{SemanticFailure=semantic};results.Add(result);
        // Compile failures, timeouts, missing TRX and zero-test runs are invalid probes, never kills.
        if(result.ExitCode==0||result.Failed!=1||result.Total!=1||!semantic)throw new InvalidOperationException("Mutant survived or probe was invalid: "+mutation.Name);
    }
}finally{VerifyFrozen();
    foreach(var file in new[]{"RtlDesign.cs","RtlCombinational.cs","RtlConversion.cs"})File.Copy(Path.Combine(root,"src/VeloSim.Core",file),Path.Combine(core,file),true);
    using var report=File.Create(Path.Combine(output,"report.json"));using var json=new Utf8JsonWriter(report,new JsonWriterOptions{Indented=true});
    json.WriteStartObject();json.WriteString("scope","Six focused mutations; not a whole-program mutation score");json.WriteString("source_sha256",baselineHash);json.WriteStartObject("source_hashes");foreach(var file in new[]{"RtlDesign.cs","RtlCombinational.cs","RtlConversion.cs"})json.WriteString(file,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,"src/VeloSim.Core",file)))));json.WriteEndObject();json.WriteStartObject("frozen_inputs");foreach(var pair in frozen)json.WriteString(pair.Key,pair.Value);json.WriteEndObject();json.WriteStartArray("runs");
    foreach(var result in results){json.WriteStartObject();json.WriteString("name",result.Name);json.WriteNumber("exit_code",result.ExitCode);json.WriteNumber("total",result.Total);json.WriteNumber("passed",result.Passed);json.WriteNumber("failed",result.Failed);json.WriteBoolean("semantic_failure",result.SemanticFailure);json.WriteEndObject();}
    json.WriteEndArray();json.WriteEndObject();
}
Console.WriteLine("Mutation evidence: "+output);return 0;

async Task<Observation> Run(string name,string filter){
    string resultDirectory=Path.Combine(output,name);Directory.CreateDirectory(resultDirectory);
    string[] arguments=["test",projectFile,"-c","Release","-m:4","--filter",filter,"--results-directory",resultDirectory,"--logger","trx;LogFileName=results.trx"];
    Environment.SetEnvironmentVariable("VELOSIM_NATIVE_DIR",Path.Combine(root,"artifacts/native/lib"));Environment.SetEnvironmentVariable("VELOSIM_REAL_FIFO","1");Environment.SetEnvironmentVariable("VELOSIM_MUTANT_KEEP_GUARD",null);Environment.SetEnvironmentVariable("VELOSIM_RTL_EVIDENCE_ROOT",Path.Combine("/home/jeffd/.local/share/VeloSim/comb200/mutations",Path.GetFileName(output),name));
    Console.WriteLine("Mutation probe: "+name);
    // Reuse the qualified process-group cleanup, output cap and stream-drain deadline.
    // This deadline includes restoring and compiling the isolated copied projects, not just test execution.
    var child=await new ProcessRunner(4*1024*1024).RunAsync("dotnet",arguments,root,TimeSpan.FromMinutes(20));
    using(var command=File.Create(Path.Combine(resultDirectory,"command.json")))using(var json=new Utf8JsonWriter(command)){
        json.WriteStartObject();json.WriteString("executable","dotnet");json.WriteStartArray("arguments");foreach(string argument in arguments)json.WriteStringValue(argument);json.WriteEndArray();json.WriteEndObject();
    }
    File.WriteAllText(Path.Combine(resultDirectory,"output.log"),child.StandardOutput+child.StandardError);
    var trx=XDocument.Load(Path.Combine(resultDirectory,"results.trx"));var counters=trx.Descendants().Single(element=>element.Name.LocalName=="Counters");
    int Count(string attribute)=>int.Parse(counters.Attribute(attribute)!.Value,CultureInfo.InvariantCulture);
    string failure=string.Join('\n',trx.Descendants().Where(element=>element.Name.LocalName=="ErrorInfo").SelectMany(element=>element.Elements().Where(child=>child.Name.LocalName=="Message")).Select(element=>element.Value));
    var observation=new Observation(name,child.ExitCode,Count("total"),Count("passed"),Count("failed"),failure);Console.WriteLine($"{name}: exit={observation.ExitCode}, passed={observation.Passed}, failed={observation.Failed}");return observation;
}

// A native semantic kill requires successful compilation/execution, then a marked comparison failure.
static bool NativeFailureEvidence(string failure){
 var match=Regex.Match(failure,@"RTL_TRACE_MISMATCH ([^\r\n]+)");if(!match.Success)return false;string path=match.Groups[1].Value.Trim();
 string commands=Path.Combine(path,"commands.json");if(!File.Exists(commands))return false;
 using var document=JsonDocument.Parse(File.ReadAllText(commands));var list=document.RootElement.EnumerateArray().ToArray();
 return list.Length>3&&list.All(item=>item.GetProperty("ExitCode").GetInt32()==0)&&Path.GetFileName(list[^1].GetProperty("Executable").GetString())=="indexed";
}
// Skip build products while preserving the Core project's source layout and dependency lock.
static void CopySources(string from,string to){
    Directory.CreateDirectory(to);foreach(string file in Directory.EnumerateFiles(from))File.Copy(file,Path.Combine(to,Path.GetFileName(file)));
    foreach(string directory in Directory.EnumerateDirectories(from))if(Path.GetFileName(directory) is not("bin" or "obj"))CopySources(directory,Path.Combine(to,Path.GetFileName(directory)));
}
record Mutation(string Name,string File,string Before,string After,string Filter,string FailureMarker,int NativeCase);
record Observation(string Name,int ExitCode,int Total,int Passed,int Failed,string Failure,bool SemanticFailure=false);
