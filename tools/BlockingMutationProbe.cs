#:project ../src/VeloSim.Core/VeloSim.csproj
// C# file-based qualification app. Run: dotnet run --file tools/BlockingMutationProbe.cs -- <checkout>
// Mutations are confined to a copied Core project; the working source and built solution are never replaced.
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VeloSim;

if(args.Length>1)throw new ArgumentException("Usage: BlockingMutationProbe [checkout]");
string root=Path.GetFullPath(args.Length==1?args[0]:Environment.CurrentDirectory);
string source=Path.Combine(root,"src/VeloSim.Core/RtlDesign.cs");
if(!File.Exists(source))throw new ArgumentException("A VeloSim checkout is required.");
string output=Path.Combine(root,"artifacts/requirements-200/mutations",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff",CultureInfo.InvariantCulture));
Directory.CreateDirectory(output);
string core=Path.Combine(output,"Core"),probe=Path.Combine(output,"Tests");
CopySources(Path.GetDirectoryName(source)!,core);Directory.CreateDirectory(probe);
File.Copy(Path.Combine(root,"Directory.Build.props"),Path.Combine(output,"Directory.Build.props"));
// This isolated project reuses actual test sources and dependency pins, avoiding a separate test oracle.
var packages=XDocument.Load(Path.Combine(root,"tests/VeloSim.Tests/VeloSim.Tests.csproj")).Descendants("PackageReference").Select(element=>new XElement(element));
string[] testFiles=["RtlRequirements200Tests.cs","RtlBlockingRobustnessTests.cs","RtlIndexedConformanceTests.cs"];
foreach(string file in testFiles)File.Copy(Path.Combine(root,"tests/VeloSim.Tests",file),Path.Combine(probe,file));
var project=new XDocument(new XElement("Project",new XAttribute("Sdk","Microsoft.NET.Sdk"),
    new XElement("PropertyGroup",new XElement("AssemblyName","VeloSim.Tests"),new XElement("IsTestProject","true"),new XElement("IsPackable","false"),new XElement("TestingPlatformDotnetTestSupport","false")),
    new XElement("ItemGroup",packages,new XElement("ProjectReference",new XAttribute("Include",Path.Combine(core,"VeloSim.csproj"))))));
string projectFile=Path.Combine(probe,"Probe.csproj");project.Save(projectFile);
string original=File.ReadAllText(source),copy=Path.Combine(core,"RtlDesign.cs");
string baselineHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
var mutations=new[]{
    new Mutation("omit-word-visibility","""code.AppendLine($"s[{id}]=next[{id}];");""","""code.AppendLine("/* omitted word visibility */");""","TestCategory=Req200_001","SEMANTIC_MISMATCH iteration 001",1),
    new Mutation("omit-memory-visibility","""code.AppendLine($"mem[{id}]=nextMem[{id}];");""","""code.AppendLine("/* omitted memory visibility */");""","TestCategory=Req200_101","SEMANTIC_MISMATCH iteration 101",101),
    new Mutation("omit-memory-dependency","""yield return Name(e); foreach (var n in References(e.GetProperty("index")))""","""foreach (var n in References(e.GetProperty("index")))""","TestCategory=Req200_123","ISOLATION_GUARD iteration 123",0),
    new Mutation("disable-mixed-kind-guard","if(blocking.Overlaps(nonblocking))","if(blocking.Overlaps(nonblocking) && Environment.GetEnvironmentVariable(\"VELOSIM_MUTANT_KEEP_GUARD\")==\"1\")","FullyQualifiedName~MixedAssignmentKindsAreRejectedByProperty","MIXED_KIND_GUARD",0)
};
string controlFilter=string.Join('|',mutations.Select(mutation=>mutation.Filter));
var results=new List<Observation>();
try{
    var control=await Run("control",controlFilter);results.Add(control);
    if(control.ExitCode!=0||control.Passed!=4||control.Failed!=0)throw new InvalidOperationException("Mutation control must pass all four selected tests.");
    foreach(var mutation in mutations){
        if(original.Split(mutation.Before,StringSplitOptions.None).Length!=2)throw new InvalidOperationException("Mutation target drift: "+mutation.Name);
        string changed=original.Replace(mutation.Before,mutation.After,StringComparison.Ordinal);
        File.WriteAllText(copy,changed);File.WriteAllText(Path.Combine(output,mutation.Name+".cs"),changed);
        var result=await Run(mutation.Name,mutation.Filter);
        bool semantic=result.Failure.Contains(mutation.FailureMarker,StringComparison.Ordinal);
        if(mutation.NativeCase!=0)semantic&=NativeFailureEvidence(Path.Combine(output,mutation.Name,"native",$"{mutation.NativeCase:000}"));
        else if(mutation.Name=="omit-memory-dependency")semantic&=Regex.IsMatch(result.Failure,@"But was:\s*null");
        // FsCheck.Fluent represents a false Boolean as this exact assertion exception.
        // Other exception types/messages indicate a broken probe, not a semantic kill.
        else semantic&=result.Failure.Contains("Falsifiable",StringComparison.Ordinal)&&
            Regex.IsMatch(result.Failure,@"with exception:\r?\nSystem\.Exception: Expected true, got false\.(?:\r?\n|$)");
        result=result with{SemanticFailure=semantic};results.Add(result);
        // Compile failures, timeouts, missing TRX and zero-test runs are invalid probes, never kills.
        if(result.ExitCode==0||result.Failed!=1||result.Total!=1||!semantic)throw new InvalidOperationException("Mutant survived or probe was invalid: "+mutation.Name);
    }
}finally{
    File.WriteAllText(copy,original);
    using var report=File.Create(Path.Combine(output,"report.json"));using var json=new Utf8JsonWriter(report,new JsonWriterOptions{Indented=true});
    json.WriteStartObject();json.WriteString("scope","Four focused mutations; not a whole-program mutation score");json.WriteString("source_sha256",baselineHash);json.WriteStartArray("runs");
    foreach(var result in results){json.WriteStartObject();json.WriteString("name",result.Name);json.WriteNumber("exit_code",result.ExitCode);json.WriteNumber("total",result.Total);json.WriteNumber("passed",result.Passed);json.WriteNumber("failed",result.Failed);json.WriteBoolean("semantic_failure",result.SemanticFailure);json.WriteEndObject();}
    json.WriteEndArray();json.WriteEndObject();
}
Console.WriteLine("Mutation evidence: "+output);return 0;

async Task<Observation> Run(string name,string filter){
    string resultDirectory=Path.Combine(output,name);Directory.CreateDirectory(resultDirectory);
    string[] arguments=["test",projectFile,"-c","Release","--filter",filter,"--results-directory",resultDirectory,"--logger","trx;LogFileName=results.trx"];
    Environment.SetEnvironmentVariable("VELOSIM_NATIVE_DIR",Path.Combine(root,"artifacts/native/lib"));Environment.SetEnvironmentVariable("VELOSIM_REAL_FIFO","1");Environment.SetEnvironmentVariable("VELOSIM_MUTANT_KEEP_GUARD",null);Environment.SetEnvironmentVariable("VELOSIM_REQ200_EVIDENCE_ROOT",Path.Combine(resultDirectory,"native"));
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
static bool NativeFailureEvidence(string directory){
    if(!File.Exists(Path.Combine(directory,"status.json"))||!File.Exists(Path.Combine(directory,"commands.log")))return false;
    using var status=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"status.json")));if(status.RootElement.GetProperty("status").GetString()!="failed")return false;
    var commands=File.ReadAllLines(Path.Combine(directory,"commands.log")).Select(line=>JsonDocument.Parse(line)).ToArray();
    try{return commands.Length>=2&&commands.All(command=>command.RootElement.GetProperty("ExitCode").GetInt32()==0)&&Path.GetFileName(commands[^1].RootElement.GetProperty("exe").GetString())=="requirement";}finally{foreach(var command in commands)command.Dispose();}
}

// Skip build products while preserving the Core project's source layout and dependency lock.
static void CopySources(string from,string to){
    Directory.CreateDirectory(to);foreach(string file in Directory.EnumerateFiles(from))File.Copy(file,Path.Combine(to,Path.GetFileName(file)));
    foreach(string directory in Directory.EnumerateDirectories(from))if(Path.GetFileName(directory) is not("bin" or "obj"))CopySources(directory,Path.Combine(to,Path.GetFileName(directory)));
}
record Mutation(string Name,string Before,string After,string Filter,string FailureMarker,int NativeCase);
record Observation(string Name,int ExitCode,int Total,int Passed,int Failed,string Failure,bool SemanticFailure=false);
