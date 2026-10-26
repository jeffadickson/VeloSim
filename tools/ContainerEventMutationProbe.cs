#:property NuGetLockFilePath=ContainerEventMutationProbe.packages.lock.json
// Runs compiled semantic mutations in isolated copies; never edits live headers.
// Requires Linux clang++-18. Compile failures are probe errors, not detected faults.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

if(args.Length!=1)throw new ArgumentException("Usage: ContainerEventMutationProbe <checkout>");
string root=Path.GetFullPath(args[0]);
string output=Path.Combine(root,"artifacts/container-events/mutations",Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output);
var mutations=new (string Name,string File,string Before,string After)[]{
 ("dynamic-prefix","containers.h","if(preserve)std::copy_n","if(false)std::copy_n"),
 ("queue-direction","containers.h","W value=front?cells_.front():cells_.back();","W value=front?cells_.back():cells_.front();"),
 ("associative-key","containers.h","cells_.insert_or_assign(key,copy.read(0));","cells_.insert_or_assign(Bits(0),copy.read(0));"),
 ("event-order","timed_events.h","Key{time_+delay,delta,region,serial_}","Key{time_+delay,delta,region,UINT64_MAX-serial_}"),
 ("event-budget","timed_events.h","if(!remaining_)throw","if(false)throw")
};
var results=new List<(string Name,int Code,bool Detected)>();
foreach(var mutation in new[]{("control","","","")}.Concat(mutations)) {
 var (name,file,before,after)=mutation;
 string copy=Path.Combine(output,name);Directory.CreateDirectory(Path.Combine(copy,"native/rtl"));Directory.CreateDirectory(Path.Combine(copy,"tests/native"));
 foreach(string header in new[]{"runtime.h","containers.h","timed_events.h"}) {
  string source=File.ReadAllText(Path.Combine(root,"native/rtl",header));
  if(header==file){if(source.Split(before,StringSplitOptions.None).Length!=2)throw new InvalidOperationException("Mutation target drift: "+name);source=source.Replace(before,after,StringComparison.Ordinal);}
  File.WriteAllText(Path.Combine(copy,"native/rtl",header),source);
 }
 File.Copy(Path.Combine(root,"tests/native/container_event_tests.cpp"),Path.Combine(copy,"tests/native/container_event_tests.cpp"));
 await Run("clang++-18",["-std=c++20","-O0","-g","tests/native/container_event_tests.cpp","-o","probe"],copy,name+"-compile",true);
 var run=await Run(Path.Combine(copy,"probe"),[],copy,name+"-run",false);
 bool detected=run.Code!=0&&run.Text.Contains("container/event assertion failed",StringComparison.Ordinal);
 results.Add((name,run.Code,detected));
 // Explicit JSON writing keeps this helper compatible with NativeAOT.
 using(var stream=File.Create(Path.Combine(output,"report.json")))using(var json=new Utf8JsonWriter(stream,new JsonWriterOptions{Indented=true})) {
  json.WriteStartObject();json.WriteNumber("seed",20261030);json.WriteString("scope","Five focused semantic faults; not exhaustive");
  json.WriteStartObject("headers");foreach(string header in new[]{"containers.h","timed_events.h"})json.WriteString(header,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,"native/rtl",header)))));json.WriteEndObject();
  json.WriteStartArray("results");foreach(var result in results){json.WriteStartObject();json.WriteString("name",result.Name);json.WriteNumber("exit_code",result.Code);json.WriteBoolean("detected",result.Detected);json.WriteEndObject();}json.WriteEndArray();json.WriteEndObject();
 }
 if(name=="control"?run.Code!=0:!detected)throw new InvalidOperationException("Invalid control or surviving mutant: "+name);
}
Console.WriteLine(output);

async Task<(int Code,string Text)> Run(string tool,string[] arguments,string cwd,string label,bool compile) {
 var info=new ProcessStartInfo(tool){WorkingDirectory=cwd,RedirectStandardOutput=true,RedirectStandardError=true};
 foreach(string argument in arguments)info.ArgumentList.Add(argument);
 using var process=Process.Start(info)??throw new InvalidOperationException("Could not start "+tool);
 var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
 using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
 try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){process.Kill(true);throw new TimeoutException(label);}
 string text=await stdout+await stderr;File.WriteAllText(Path.Combine(output,label+".log"),text);
 if(compile&&process.ExitCode!=0)throw new InvalidOperationException("Compilation failed: "+label);
 return (process.ExitCode,text);
}
