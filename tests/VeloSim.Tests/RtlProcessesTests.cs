using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace VeloSim.Tests;

public class RtlProcessesTests
{
    private static object Ref(string name)=>new{kind="ref",name};
    private static object Nba(string target,string value)=>new{kind="nba",target=Ref(target),value=Ref(value)};
    private static JsonNode Fixture()=>JsonNode.Parse(JsonSerializer.Serialize(new{format="velosim.rtl.v1",signals=new[]{new{name="clk",width=1,direction="input"},new{name="a",width=8,direction="input"},new{name="q",width=8,direction="output"},new{name="r",width=8,direction="output"}},memories=Array.Empty<object>(),continuous=Array.Empty<object>(),clock="clk",body=new{kind="parallel",items=new[]{Nba("q","a"),Nba("r","q")}}}))!;
    [Test]
    public void SharedClockProcessesEmitOneSnapshotAndCommit(){var design=RtlDesign.Parse(Fixture().ToJsonString());Assert.DoesNotThrow(()=>design.EmitCpp());Assert.DoesNotThrow(()=>design.EmitModelHeader("pipeline"));}
    [TestCase(false),TestCase(true)]
    public void ConflictingProcessWritersRejected(bool selected){var node=Fixture();node["body"]!["items"]![1]!["target"]=JsonSerializer.SerializeToNode(selected?new{kind="bitselect",value=Ref("q"),index=new{kind="const",bits="0"}}:(object)Ref("q"));Assert.That(Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(node.ToJsonString()))!.Message,Does.Contain("Multiple processes"));}
    [Test]
    public void NestedProcessGroupRejected(){var node=Fixture();node["body"]=new JsonObject{["kind"]="block",["items"]=new JsonArray(node["body"]!.DeepClone())};Assert.That(Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(node.ToJsonString()))!.Message,Does.Contain("root"));}
    [Test,Category("RealFifo")]
    public async Task ActualSharedClockPipelineMatchesIcarus()
    {
        const string source="module dut(input logic clk,rst_n,enable,input logic[63:0]a,i,output logic[7:0]q,r,snapshot);always_ff @(posedge clk)if(!rst_n)q<='0;else if(enable)q<=a;always_ff @(posedge clk)if(!rst_n)r<='0;else if(enable)r<=q;always_ff @(posedge clk)if(!rst_n)snapshot<='0;else snapshot<=r;endmodule";
        var ports=new RtlSignal[]{new("clk",1,"input"),new("rst_n",1,"input"),new("enable",1,"input"),new("a",64,"input"),new("i",64,"input"),new("q",8,"output"),new("r",8,"output"),new("snapshot",8,"output")};
        var vectors=Enumerable.Range(0,40).Select(row=>new Dictionary<string,string>{["rst_n"]=row%13==0?"0":"1",["enable"]=row%7==0?"0":"1",["a"]=string.Concat(Enumerable.Range(0,64).Select(bit=>"01xz"[(bit+row/4)%4])),["i"]=new('0',64)}).ToList();
        await RtlIndexedConformanceTests.Qualify("shared-clock-pipeline",source,ports,vectors,sdk:true,selectedProbe:false);
    }
}
