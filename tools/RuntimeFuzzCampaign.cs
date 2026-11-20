#:property NuGetLockFilePath=RuntimeFuzzCampaign.packages.lock.json
// NativeAOT-compatible controller. All campaigns compile a frozen copy on the
// Linux filesystem; changing the checkout cannot change an executing campaign.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
if(args.Length!=3||!int.TryParse(args[2],out int seconds)||seconds is <1 or >43200)throw new ArgumentException("Usage: RuntimeFuzzCampaign <checkout> <new-output-directory> <seconds-per-domain:1..43200>");
string root=Path.GetFullPath(args[0]),output=Path.GetFullPath(args[1]);
if(!Directory.Exists(root)||!File.Exists(Path.Combine(root,"tools/fuzz/runtime_fuzz.cpp")))throw new DirectoryNotFoundException(root);
if(Directory.Exists(output))throw new IOException("Campaign output must be new: "+output);
if(!Directory.Exists(Path.GetDirectoryName(output)))throw new DirectoryNotFoundException("Create/verify output parent first.");
Directory.CreateDirectory(output);string snapshot=Path.Combine(output,"source");
string[] sources=["tools/fuzz/runtime_fuzz.cpp","tools/fuzz/CMakeLists.txt","native/rtl/runtime.h","native/rtl/containers.h","native/rtl/timed_events.h"];
foreach(string relative in sources){string target=Path.Combine(snapshot,relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(Path.Combine(root,relative),target);}
var commands=new List<(string Tool,string[] Args,int Code)>();var outcomes=new List<(int Domain,int Exit,int Corpus)>();
await Run("clang++-18",["--version"],output,"compiler",120);
await Run("cmake",["-S",Path.Combine(snapshot,"tools/fuzz"),"-B",Path.Combine(output,"build"),"-G","Ninja","-DCMAKE_CXX_COMPILER=clang++-18"],output,"configure",120);
await Run("cmake",["--build",Path.Combine(output,"build"),"--parallel","4"],output,"build",600);
// Independent processes/corpora give four bounded workers; no shared mutable
// corpus or compiler output exists between domains.
var tasks=Enumerable.Range(0,4).Select(async domain=>{
 string corpus=Path.Combine(output,"corpus-"+domain);Directory.CreateDirectory(corpus);
 var random=new Random(20261031+domain);for(int seed=0;seed<12;++seed){byte[] bytes=new byte[seed==0?0:64+seed*64];random.NextBytes(bytes);File.WriteAllBytes(Path.Combine(corpus,"seed-"+seed),bytes);}
 string crashes=Path.Combine(output,"crashes-"+domain);Directory.CreateDirectory(crashes);
 int code=await Run(Path.Combine(output,"build/fuzz_"+domain),[corpus,"-seed="+(20261031+domain),"-max_total_time="+seconds,"-max_len=4096","-timeout=10","-rss_limit_mb=1024","-print_final_stats=1","-artifact_prefix="+crashes+"/"],output,"fuzz-"+domain,seconds+90,false);
 lock(outcomes)outcomes.Add((domain,code,Directory.GetFiles(corpus).Length));
 // Replay only successful corpora. Failed campaigns preserve their crash input
 // and logs for explicit triage, never report their partial coverage as passing.
 if(code==0)await Run(Path.Combine(output,"build/replay_"+domain),[corpus],output,"replay-"+domain,180,true,new(){["LLVM_PROFILE_FILE"]=Path.Combine(output,"replay-"+domain+".profraw")});
}).ToArray();
try{await Task.WhenAll(tasks);}finally{Save();}
var profiles=Directory.GetFiles(output,"replay-*.profraw");
if(profiles.Length==4){
 await Run("llvm-profdata-18",["merge","-sparse",..profiles,"-o",Path.Combine(output,"coverage.profdata")],output,"merge",120);
 var coverageArgs=new List<string>{"report",Path.Combine(output,"build/replay_0"),"-instr-profile="+Path.Combine(output,"coverage.profdata")};
 for(int i=1;i<4;++i){coverageArgs.Add("-object");coverageArgs.Add(Path.Combine(output,"build/replay_"+i));}
 coverageArgs.AddRange(new[]{"runtime.h","containers.h","timed_events.h"}.Select(h=>Path.Combine(snapshot,"native/rtl",h)));
 await Run("llvm-cov-18",coverageArgs.ToArray(),output,"coverage",120);
 coverageArgs[0]="export";await Run("llvm-cov-18",coverageArgs.ToArray(),output,"coverage-json",120);
}
Save();Console.WriteLine(output);return outcomes.Count==4&&outcomes.All(x=>x.Exit==0)?0:1;

void Save(){
 using var file=File.Create(Path.Combine(output,"report.json"));using var json=new Utf8JsonWriter(file,new(){Indented=true});
 json.WriteStartObject();json.WriteNumber("seconds_per_domain",seconds);json.WriteString("scope","Four native runtime domains; no HDL frontend fuzzing or proof");
 json.WriteStartObject("source_sha256");foreach(string path in sources)json.WriteString(path,Hash(Path.Combine(snapshot,path)));json.WriteEndObject();
 json.WriteStartArray("domains");foreach(var result in outcomes.OrderBy(r=>r.Domain)){json.WriteStartObject();json.WriteNumber("domain",result.Domain);json.WriteNumber("exit",result.Exit);json.WriteNumber("corpus_files",result.Corpus);json.WriteNumber("seed",20261031+result.Domain);json.WriteString("binary_sha256",Hash(Path.Combine(output,"build/fuzz_"+result.Domain)));json.WriteEndObject();}json.WriteEndArray();
 json.WriteStartArray("commands");lock(commands)foreach(var command in commands){json.WriteStartObject();json.WriteString("tool",command.Tool);json.WriteNumber("exit",command.Code);json.WriteStartArray("args");foreach(string argument in command.Args)json.WriteStringValue(argument);json.WriteEndArray();json.WriteEndObject();}json.WriteEndArray();json.WriteEndObject();
}
static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
async Task<int> Run(string tool,string[] arguments,string cwd,string label,int timeout,bool requireSuccess=true,Dictionary<string,string>? environment=null){
 var info=new ProcessStartInfo(tool){WorkingDirectory=cwd,RedirectStandardOutput=true,RedirectStandardError=true};foreach(string arg in arguments)info.ArgumentList.Add(arg);
 if(environment is not null)foreach(var pair in environment)info.Environment[pair.Key]=pair.Value;
 info.Environment["ASAN_OPTIONS"]="detect_leaks=1";info.Environment["UBSAN_OPTIONS"]="halt_on_error=1:print_stacktrace=1";
 using var process=Process.Start(info)??throw new IOException(tool);var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
 using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
 try{await process.WaitForExitAsync(cancellation.Token);}catch(OperationCanceledException){process.Kill(true);throw new TimeoutException(label);}
 File.WriteAllText(Path.Combine(output,label+".stdout.log"),await stdout);File.WriteAllText(Path.Combine(output,label+".stderr.log"),await stderr);
 lock(commands)commands.Add((tool,arguments,process.ExitCode));
 if(requireSuccess&&process.ExitCode!=0)throw new InvalidOperationException(label+" failed: "+process.ExitCode);
 return process.ExitCode;
}
