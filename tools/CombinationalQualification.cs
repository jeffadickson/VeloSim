#:project ../src/VeloSim.Core/VeloSim.csproj
#:property NuGetLockFilePath=CombinationalQualification.packages.lock.json
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using VeloSim;
// A native C# checkpoint controller. Each invocation gets immutable, distinct evidence.
if(args.Length is <3 or >4)throw new ArgumentException("usage: CombinationalQualification <root> <first> <last> [functions100]");
// The optional mode preserves the original200-case workflow while qualifying the newer100-case contract.
bool functions=args.Length==4&&args[3]=="functions100";if(args.Length==4&&!functions)throw new ArgumentException("unknown qualification mode");
int maximum=functions?100:200;string batch=functions?"functions-100":"comb-subroutines-200",category=functions?"Functions100":"Comb200",fixture=functions?"functions100":"comb200";
string root=Path.GetFullPath(args[0]);int first=int.Parse(args[1]),last=int.Parse(args[2]);
if(first<1||last>maximum||first>last)throw new ArgumentOutOfRangeException(nameof(args),$"Require 1 <= first <= last <= {maximum}");
string evidence=Path.Combine(root,"artifacts",batch,"checkpoints",$"{first:000}-{last:000}-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(evidence);
string[] InputFiles()=>new[]{"src/VeloSim.Core","native","tests/VeloSim.Tests","tools"}.SelectMany(dir=>Directory.EnumerateFiles(Path.Combine(root,dir),"*",SearchOption.AllDirectories)).Where(file=>Path.GetExtension(file) is ".cs" or ".cpp" or ".h" or ".csproj").Where(file=>!file.Replace('\\','/').Contains("/obj/")&&!file.Replace('\\','/').Contains("/bin/")).Order(StringComparer.Ordinal).ToArray();
var snapshot=InputFiles().ToDictionary(file=>file,file=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
void VerifySources(){if(!snapshot.Keys.Order(StringComparer.Ordinal).SequenceEqual(InputFiles()))throw new IOException("Source file set changed during checkpoint");foreach(var pair in snapshot)if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Key)))!=pair.Value)throw new IOException("Source changed during checkpoint: "+pair.Key);}using(var file=File.Create(Path.Combine(evidence,"sources.json")))using(var json=new Utf8JsonWriter(file)){json.WriteStartObject();foreach(var pair in snapshot)json.WriteString(pair.Key,pair.Value);json.WriteEndObject();}
async Task Run(string executable,string[] arguments,string log){
 Environment.SetEnvironmentVariable("VELOSIM_REAL_FIFO","1");Environment.SetEnvironmentVariable("VELOSIM_RTL_EVIDENCE_ROOT",$"/home/jeffd/.local/share/VeloSim/{fixture}/frontend");Environment.SetEnvironmentVariable("VELOSIM_JOBS","4");
 var result=await new ProcessRunner(4*1024*1024).RunAsync(executable,arguments,root,TimeSpan.FromMinutes(30));
 await File.WriteAllTextAsync(Path.Combine(evidence,log),result.StandardOutput+result.StandardError);Console.WriteLine($"{log}: exit {result.ExitCode}");if(result.ExitCode!=0)throw new InvalidOperationException(result.StandardOutput+result.StandardError);
}
await Run("dotnet",["build","tests/VeloSim.Tests","-c","Release","-m:4"],"initial-build.log");
await Run("cmake",["--build","artifacts/with-uhdm","--target","velosim-uhdm-rtl","--parallel","4"],"initial-exporter-build.log");VerifySources();
for(int begin=first;begin<=last;){
 int end=Math.Min(last,Math.Min(((begin-1)/5+1)*5,((begin-1)/10+1)*10));string tag=$"{begin:000}-{end:000}",trx=$"cases-{tag}.trx";
 string filter=string.Join('|',Enumerable.Range(begin,end-begin+1).Select(id=>$"TestCategory={category}_{id:000}"));
 await Run("dotnet",["test","tests/VeloSim.Tests","-c","Release","--no-build","--filter",filter,"--results-directory",evidence,"--logger",$"trx;LogFileName={trx}"],$"cases-{tag}.log");
 var document=XDocument.Load(Path.Combine(evidence,trx));var counters=document.Descendants().Single(e=>e.Name.LocalName=="Counters");int count=end-begin+1;
 if((int?)counters.Attribute("total")!=count||(int?)counters.Attribute("passed")!=count||(int?)counters.Attribute("executed")!=count)throw new InvalidOperationException("Incomplete checkpoint: "+counters);
 if(end%10==0){await Run("make",["build","JOBS=4"],$"build-{end:000}.log");await Run("cmake",["--build","artifacts/with-uhdm","--target","velosim-uhdm-rtl","--parallel","4"],$"exporter-build-{end:000}.log");}
 VerifySources();
 await File.WriteAllTextAsync(Path.Combine(evidence,$"checkpoint-{end:000}.json"),$$"""{"first":{{begin}},"last":{{end}},"passed":{{count}},"build":{{(end%10==0?"true":"false")}},"trx":"{{trx}}","utc":"{{DateTime.UtcNow:O}}"}""");begin=end+1;
}
Console.WriteLine("Checkpoint evidence: "+evidence);
