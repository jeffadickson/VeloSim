using System.Text;
using System.Text.Json;
using NUnit.Framework;

namespace VeloSim.Tests;

/// <summary>Actual binary queue equivalence; Verilator is not a four-state reference.</summary>
[NonParallelizable]
public class VerilatorQueueConformanceTests
{
    [Test,Category("VerilatorConformance")]
    public async Task EmptyBitQueuePopsMatchVerilatorWithoutGuards()
    {
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_VERILATOR")!="1")Assert.Ignore("Opt in to installed Verilator and Surelog with VELOSIM_REAL_VERILATOR=1");
        var root=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"native/rtl/runtime.h")))root=root.Parent;
        if(root is null)throw new IOException("Checkout missing");
        string parent=Environment.GetEnvironmentVariable("VELOSIM_RTL_EVIDENCE_ROOT")??Path.Combine(root.FullName,"artifacts");
        if(!Directory.Exists(parent))throw new DirectoryNotFoundException("Create the evidence parent before running this test");
        string path=Path.Combine(parent,"verilator-empty-bit-queue-"+Guid.NewGuid().ToString("N"));
        if(!Directory.Exists(path))Directory.CreateDirectory(path);
        string source=Path.Combine(path,"design.sv"),tb=Path.Combine(path,"tb.sv");
        // Deliberately no size guard: the empty-pop result itself is the contract.
        const string hdl="module dut(input logic clk,input logic[1:0]op,input logic[7:0]a,output logic[7:0]q);bit[7:0]m[$];always @(posedge clk)begin case(op)0:q=m.pop_front();1:q=m.pop_back();2:m.push_back(a);3:begin m.delete();q=8'hff;end endcase end endmodule";
        File.WriteAllText(source,hdl);
        int[] operations=[0,1,2,0,1,2,3,1,0],payloads=[0,0,165,0,0,90,0,0,0],expected=[0,0,0,165,0,0,255,0,0];
        string Bits(int value,int width)=>Convert.ToString(value,2).PadLeft(width,'0');
        var bench=new StringBuilder("module queue_tb;logic clk=0;logic[1:0]op;logic[7:0]a;wire[7:0]q;dut dut_instance(.*);initial begin $display(\"VS_WIDTH 8\");\n");
        for(int i=0;i<operations.Length;i++)bench.AppendLine($"clk=0;op=2'd{operations[i]};a=8'd{payloads[i]};#1;clk=1;#1;$display(\"VS_RESULT {i} %08b\",q);");
        File.WriteAllText(tb,bench.AppendLine("$finish;end endmodule").ToString());
        var inputs=new[]{ArtifactMetadata.Fingerprint(source),ArtifactMetadata.Fingerprint(tb)};
        var runner=new RecordingProcessRunner(new ProcessRunner());
        async Task<ProcessResult> Run(string tool,params string[] arguments){
            string exe=ArtifactMetadata.ResolveExecutable(tool,path)??throw new IOException("Required tool missing: "+tool);
            var result=await runner.RunAsync(exe,arguments,path,TimeSpan.FromMinutes(3));
            Assert.That(result.ExitCode,Is.Zero,result.StandardOutput+result.StandardError);return result;
        }
        try{
            var version=await Run("verilator","--version");
            var frontend=await new SurelogFrontend(runner).ElaborateAsync([source],Path.Combine(path,"surelog"),"dut",executable:Environment.GetEnvironmentVariable("VELOSIM_SURELOG")??"surelog",workers:4);
            string ir=Path.Combine(path,"design.json"),exporter=Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL")??throw new IOException("Set VELOSIM_UHDM_RTL to the current exporter");
            await Run(exporter,frontend.UhdmPath,ir,"dut");
            var design=RtlDesign.Parse(File.ReadAllText(ir));
            Assert.That(design.Memories.Single(),Is.EqualTo(new RtlMemory("m",8,1,TwoState:true,Queue:true)));
            File.WriteAllText(Path.Combine(path,"design.cpp"),design.EmitCpp());
            File.Copy(Path.Combine(root.FullName,"native/rtl/runtime.h"),Path.Combine(path,"runtime.h"));
            File.WriteAllText(Path.Combine(path,"CMakeLists.txt"),"cmake_minimum_required(VERSION 3.20)\nproject(queue_native LANGUAGES CXX)\nset(CMAKE_CXX_STANDARD 20)\nadd_executable(queue_native design.cpp)\n");
            // Input order follows the validated declaration inventory, not source spelling.
            var ports=design.Signals.Where(s=>s.Direction=="input"&&s.Name!="clk").ToArray();
            string stimulus=Path.Combine(path,"stimulus.txt");
            File.WriteAllLines(stimulus,operations.Select((op,i)=>string.Join(' ',ports.Select(s=>s.Name=="op"?Bits(op,2):Bits(payloads[i],8)))));
            await Run("cmake","-S",path,"-B",Path.Combine(path,"build"),"-G","Ninja","-DCMAKE_BUILD_TYPE=Release");
            await Run("cmake","--build",Path.Combine(path,"build"),"--parallel","4");
            var native=await Run(Path.Combine(path,"build/queue_native"),stimulus,"1");
            var nativeRows=FifoDifferential.ParseTrace(native.StandardOutput,design,operations.Length);
            var reference=await new VerilatorReference(runner).RunAsync([source,tb],"queue_tb",Path.Combine(path,"verilator"),operations.Length,8,4,requireWidthHeader:true);
            var oracle=expected.Select((value,i)=>new TraceSample(i,Bits(value,8))).ToArray();
            Assert.That(reference,Is.EqualTo(oracle),"Verilator must return zero for both empty bit-queue pops");
            Assert.That(nativeRows.Select((row,i)=>new TraceSample(i,row["q"])),Is.EqualTo(oracle),"VeloSim must match the same independent expected history");
            foreach(var input in inputs)Assert.That(ArtifactMetadata.Fingerprint(input.Path),Is.EqualTo(input),"Reference engines must consume unchanged source files");
            File.WriteAllText(Path.Combine(path,"native.txt"),native.StandardOutput);
            File.WriteAllText(Path.Combine(path,"reference.json"),JsonSerializer.Serialize(reference));
            var tools=runner.Commands.Select(c=>ArtifactMetadata.ResolveExecutable(c.Executable,c.WorkingDirectory)).OfType<string>().Distinct().Select(ArtifactMetadata.Fingerprint).ToArray();
            File.WriteAllText(Path.Combine(path,"report.json"),JsonSerializer.Serialize(new{status="pass",scope="binary_bit_queue_equivalence_not_four_state_conformance",rows=operations.Length,sourceInputs=inputs,tools,verilatorVersion=version.StandardOutput,buildJobs=4,expected=oracle,commands=runner.Commands},new JsonSerializerOptions{WriteIndented=true}));
        }finally{File.WriteAllText(Path.Combine(path,"commands.json"),JsonSerializer.Serialize(runner.Commands));}
    }
}
