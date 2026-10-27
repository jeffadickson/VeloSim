#:property NuGetLockFilePath=ContainerEventMutationProbe.packages.lock.json
// Runs compiled semantic mutations in isolated copies; never edits live headers.
// Requires Linux clang++-18. Compile failures are probe errors, not detected faults.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

if(args.Length is <1 or >2||args.Length==2&&args[1] is not ("dynamic" or "queue"))throw new ArgumentException("Usage: ContainerEventMutationProbe <checkout> [dynamic|queue]");
bool dynamicMode=args.Length==2&&args[1]=="dynamic",queueMode=args.Length==2&&args[1]=="queue";
string root=Path.GetFullPath(args[0]);
if(!Directory.Exists(root))throw new DirectoryNotFoundException(root);
string output=Path.Combine(root,queueMode?"artifacts/queue-feature/mutations":dynamicMode?"artifacts/dynamic-array/mutations":"artifacts/container-events/mutations",Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output);
var mutations=queueMode?new (string Name,string File,string Before,string After)[]{
 ("queue-pop-direction","runtime.h","W value=front?memory.front():memory.back();","W value=front?memory.back():memory.front();"),
 ("queue-capacity-off-by-one","runtime.h","if(memory.size()>=256)throw","if(memory.size()>256)throw"),
 ("queue-append-lost","runtime.h","if(index.v==memory.size())queue_push(memory,value,width,is_two_state,false);","if(index.v==memory.size())return;"),
 ("queue-two-state-push","runtime.h","value=resize(value,width);if(is_two_state)value=two_state(value);","value=resize(value,width);")
}:dynamicMode?new (string Name,string File,string Before,string After)[]{
 ("lost-resize-prefix","runtime.h","if(preserve)for(size_t i=0;","if(false)for(size_t i=0;"),
 ("capacity-off-by-one","runtime.h","if(count.v>256)throw","if(count.v>257)throw"),
 ("allocation-not-published","runtime.h","memory.swap(next);","/* missing allocation publication */"),
 ("two-state-invalid-read","runtime.h","if(index.x||index.z||index.v>=memory.size())return is_two_state?word(0,0,0,width):unknown(width);","if(index.x||index.z||index.v>=memory.size())return unknown(width);")
}:new (string Name,string File,string Before,string After)[]{
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
 string testSource=queueMode?"rtl_queue_tests.cpp":dynamicMode?"rtl_runtime_tests.cpp":"container_event_tests.cpp";
 File.Copy(Path.Combine(root,"tests/native",testSource),Path.Combine(copy,"tests/native",testSource));
 if(dynamicMode)foreach(string header in Directory.GetFiles(Path.Combine(root,"tests/native"),"*.h"))File.Copy(header,Path.Combine(copy,"tests/native",Path.GetFileName(header)));
 await Run("clang++-18",["-std=c++20","-O0","-g","tests/native/"+testSource,"-o","probe"],copy,name+"-compile",true);
 var run=await Run(Path.Combine(copy,"probe"),[],copy,name+"-run",false);
 bool detected=run.Code!=0&&run.Text.Contains(queueMode?"queue assertion failed":dynamicMode?"RTL runtime assertion failed":"container/event assertion failed",StringComparison.Ordinal);
 results.Add((name,run.Code,detected));
 // Explicit JSON writing keeps this helper compatible with NativeAOT.
 using(var stream=File.Create(Path.Combine(output,"report.json")))using(var json=new Utf8JsonWriter(stream,new JsonWriterOptions{Indented=true})) {
  json.WriteStartObject();json.WriteNumber("seed",20261030);json.WriteString("scope",queueMode?"Four compiled queue semantic faults; not exhaustive":dynamicMode?"Four dynamic allocation semantic faults; not exhaustive":"Five focused semantic faults; not exhaustive");
  json.WriteStartObject("headers");foreach(string header in new[]{"runtime.h","containers.h","timed_events.h"})json.WriteString(header,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,"native/rtl",header)))));json.WriteEndObject();
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
