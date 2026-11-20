using NUnit.Framework;
using System.Text.Json;
namespace VeloSim.Tests;

/// <summary>Nested controls must not invalidate the enclosing loop's proven trip count.</summary>
[NonParallelizable]
public class RtlNestedLoopSafetyTests
{
    [TestCase("for(i=0;i<1;i=i+1)n=~n;")]
    [TestCase("for(j=0;j<1;i++)begin j=j+1;n=~n;end")]
    public async Task NestedControlsCannotMutateOuterCounter(string nested){
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires actual frontend");
        string root=Root(),path=Path.Combine(Environment.GetEnvironmentVariable("VELOSIM_RTL_EVIDENCE_ROOT")??Path.Combine(root,"artifacts"),"nested-counter-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);string source=Path.Combine(path,"design.sv");
        File.WriteAllText(source,$"module dut(input logic[7:0]a,output logic[7:0]q);function automatic logic[7:0]f(input logic[7:0]x);logic[31:0]i,j;logic[7:0]n;begin n=x;for(i=0;i<3;i=i+1)begin {nested} end return n;end endfunction always_comb q=f(a);endmodule");
        var runner=new RecordingProcessRunner(new ProcessRunner());var elaboration=await new SurelogFrontend(runner).ElaborateAsync([source],Path.Combine(path,"surelog"),"dut",workers:4);
        var result=await runner.RunAsync(Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL")??Path.Combine(root,"artifacts/with-uhdm/bin/velosim-uhdm-rtl"),[elaboration.UhdmPath,Path.Combine(path,"design.json"),"dut"],path,TimeSpan.FromMinutes(3));
        Assert.That(result.ExitCode,Is.EqualTo(1),result.StandardError);Assert.That(result.StandardError,Does.Contain("for body cannot mutate its counter"));File.WriteAllText(Path.Combine(path,"report.json"),JsonSerializer.Serialize(new{status="pass",nested,source=ArtifactMetadata.Fingerprint(source),commands=runner.Commands,result.StandardError}));
    }
    [Test]
    public Task IndependentNestedCountersMatchIcarus(){
        const string source="module dut(input logic[7:0]a,output logic[7:0]q);function automatic logic[7:0]f(input logic[7:0]x);logic[31:0]i,j;logic[7:0]n;begin n=x;for(i=0;i<2;i=i+1)for(j=0;j<3;j=j+1)n=~n;return n;end endfunction always_comb q=f(a);endmodule";
        var vectors=Enumerable.Range(0,16).Select(row=>new Dictionary<string,string>{["a"]=string.Concat(Enumerable.Range(0,8).Select(bit=>"01xz"[(row+bit)%4]))}).ToList();
        return RtlIndexedConformanceTests.Qualify("independent-nested-loops",source,[new("a",8,"input"),new("q",8,"output")],vectors,sdk:true,selectedProbe:false);
    }
    static string Root(){for(var directory=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);directory is not null;directory=directory.Parent)if(File.Exists(Path.Combine(directory.FullName,"native/uhdm/rtl.cpp")))return directory.FullName;throw new IOException("Checkout missing");}
}