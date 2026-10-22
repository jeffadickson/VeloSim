using NUnit.Framework;
namespace VeloSim.Tests;

public class RtlHierarchyTests
{
    [Test,Category("RealFifo")]
    public async Task UnsupportedConnectionsAndGlobalDriverConflictsFailClosed()
    {
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires pinned frontend.");
        var root=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"native/rtl/runtime.h")))root=root.Parent;
        Assert.That(root,Is.Not.Null);string directory=Path.Combine(root!.FullName,"artifacts","hierarchy-rejections-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        const string source="module leaf(input logic clk,input logic[7:0]a,output logic[7:0]q);always_ff @(posedge clk)q<=a;endmodule module dut(input logic clk,clk2,input logic[7:0]a,output logic[7:0]q,s);leaf u(.clk(clk),.a(a),.q(q));always_ff @(posedge clk)s<=a;endmodule";
        var variants=new Dictionary<string,string>{["different-clock"]=source.Replace("u(.clk(clk)","u(.clk(clk2)"),["inout"]=source.Replace("module leaf(input logic clk,input logic[7:0]a","module leaf(input logic clk,inout wire[7:0]a"),["output-select"]=source.Replace(".q(q)",".q(q[7:0])"),["width-mismatch"]=source.Replace(".a(a)",".a({a,a})"),["multiple-drivers"]=source.Replace("s<=a;","begin s<=a;q<=a;end "),["feedback"]=source.Replace("always_ff @(posedge clk)q<=a;","assign q=a;").Replace(".a(a)",".a(q)")};
        foreach(var (tag,text) in variants){string path=Path.Combine(directory,tag+".sv"),json=Path.Combine(directory,tag+".json");File.WriteAllText(path,text);var elaboration=await new SurelogFrontend().ElaborateAsync([path],Path.Combine(directory,tag),"dut",workers:4);var result=await new ProcessRunner().RunAsync(Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL")??Path.Combine(root.FullName,"artifacts/with-uhdm/bin/velosim-uhdm-rtl"),[elaboration.UhdmPath,json,"dut"],root.FullName,TimeSpan.FromSeconds(30));if(result.ExitCode==0)Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(File.ReadAllText(json)),tag);else Assert.That(result.StandardError,Does.Contain("rejected"),tag);}
    }
    [TestCase(false),TestCase(true),Category("RealFifo")]
    public async Task ParameterizedSiblingPipelineMatchesSameSourceIcarus(bool generated)
    {
        string source="module leaf #(parameter int W=8)(input logic clk,rst_n,enable,input logic[W-1:0]a,output logic[W-1:0]q);always_ff @(posedge clk)if(!rst_n)q<='0;else if(enable)q<=a;endmodule module dut(input logic clk,rst_n,enable,input logic[63:0]a,i,output logic[7:0]q,snapshot,output logic[15:0]r);leaf #(.W(8))u0(.clk(clk),.rst_n(rst_n),.enable(enable),.a(a[7:0]),.q(q));leaf #(.W(16))u1(.clk(clk),.rst_n(rst_n),.enable(enable),.a({8'b0,q}),.q(r));always_ff @(posedge clk)if(!rst_n)snapshot<='0;else snapshot<=r[7:0];endmodule";
        if(generated)source=source.Replace("leaf #(.W(8))", "if(1) begin:g leaf #(.W(8))").Replace("always_ff @(posedge clk)if(!rst_n)snapshot", "end always_ff @(posedge clk)if(!rst_n)snapshot");
        var expected=new List<RtlSignal>{new("clk",1,"input"),new("rst_n",1,"input"),new("enable",1,"input"),new("a",64,"input"),new("i",64,"input"),new("q",8,"output"),new("r",16,"output"),new("snapshot",8,"output")};
        foreach(var (scope,width) in new[]{("u0",8),("u1",16)}){string prefix=(generated?"g.":"")+scope;expected.Add(new(prefix+".a",width,"internal"));expected.Add(new(prefix+".q",width,"internal"));expected.Add(new(prefix+".rst_n",1,"internal"));expected.Add(new(prefix+".enable",1,"internal"));}
        var vectors=Enumerable.Range(0,40).Select(row=>new Dictionary<string,string>{["rst_n"]=row%17==0?"0":"1",["enable"]=row%7==0?"0":"1",["a"]=string.Concat(Enumerable.Range(0,64).Select(bit=>"01xz"[(bit+row/3)%4])),["i"]=new('0',64)}).ToList();
        await RtlIndexedConformanceTests.Qualify("hierarchy-siblings-"+generated,source,expected.ToArray(),vectors,sdk:true,selectedProbe:false);
    }
}
