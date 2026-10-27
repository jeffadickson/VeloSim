using NUnit.Framework;
using System.Text.Json;
namespace VeloSim.Tests;

/// <summary>Unsupported scheduling, type and container forms must fail explicitly.</summary>
[NonParallelizable]
public class RtlEvolutionRejectionTests
{
    [TestCase("stock-union","union tag provenance"),TestCase("racy","multiple initial blocks"),TestCase("input","Initialization cannot access"),TestCase("timing","unsupported statement type"),TestCase("mixed","mixed two/four-state"),TestCase("unpacked","only bounded packed"),TestCase("tagged","tag-aware storage"),TestCase("rank5","fixed arrays require"),TestCase("dynamic","initializer must"),TestCase("queue","container-lifetime"),TestCase("associative","container-lifetime")]
    public async Task UnsupportedFormsAreRejectedByTheActualPipeline(string form,string marker)
    {
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires pinned Surelog/UHDM");
        string body=form switch{
            "racy"=>"initial q=1;initial q=2;",
            "input"=>"initial q=a;",
            "timing"=>"initial begin #1;q=3;end ",
            "mixed"=>"typedef struct packed{bit b;logic c;}s_t;s_t s;assign q=0;",
            "unpacked"=>"typedef struct{logic[7:0]b;}s_t;s_t s;assign q=0;",
            "stock-union"=>"typedef union packed{logic[7:0]b;logic[7:0]c;}s_t;s_t s;assign q=0;",
            "tagged"=>"typedef union tagged packed{logic[7:0]b;logic[7:0]c;}s_t;s_t s;assign q=0;",
            "rank5"=>"logic[7:0]m[0:0][0:0][0:0][0:0][0:0];assign q=0;",
            "dynamic"=>"logic[7:0]m[],other[];initial m=new[2](other);assign q=0;",
            "queue"=>"logic[7:0]m[$];assign q=0;",
            _=>"logic[7:0]m[int];assign q=0;"};
        string source=$"module dut(input logic[7:0]a,output logic[7:0]q);{body}endmodule";
        string root=Root(),path=Path.Combine(Environment.GetEnvironmentVariable("VELOSIM_RTL_EVIDENCE_ROOT")??Path.Combine(root,"artifacts"),"rejection-"+form+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);string file=Path.Combine(path,"design.sv");File.WriteAllText(file,source);
        var runner=new RecordingProcessRunner(new ProcessRunner());var elaboration=await new SurelogFrontend(runner).ElaborateAsync([file],Path.Combine(path,"surelog"),"dut",executable:form=="stock-union"?"/opt/velosim/bin/surelog":null,workers:4);
        string ir=Path.Combine(path,"design.json");var result=await runner.RunAsync(Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL")??Path.Combine(root,"artifacts/with-uhdm/bin/velosim-uhdm-rtl"),[elaboration.UhdmPath,ir,"dut"],path,TimeSpan.FromMinutes(3));
        string stage;
        if(result.ExitCode==0){Assert.That(()=>RtlDesign.Parse(File.ReadAllText(ir)),Throws.TypeOf<IrValidationException>().With.Message.Contains(marker));stage="IR validation";}
        else{Assert.That(result.ExitCode,Is.EqualTo(1),result.StandardError);Assert.That(result.StandardError,Does.Contain(marker));stage="UHDM lowering";}
        File.WriteAllText(Path.Combine(path,"report.json"),JsonSerializer.Serialize(new{status="pass",form,stage,marker,source=ArtifactMetadata.Fingerprint(file),commands=runner.Commands}));
    }
    static string Root(){for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d is not null;d=d.Parent)if(File.Exists(Path.Combine(d.FullName,"native/rtl/runtime.h")))return d.FullName;throw new IOException("Checkout missing");}
}
