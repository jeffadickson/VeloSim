using System.Text.Json;
using System.Text.Json.Nodes;
using FsCheck;
using FsCheck.Fluent;
using NUnit.Framework;
namespace VeloSim.Tests;

/// <summary>Dynamic lifetimes are tested through real HDL, emitted SDKs and strict IR guards.</summary>
[NonParallelizable]
public class RtlDynamicArrayTests
{
    private static object Ref(string name)=>new{kind="ref",name};
    private static object Count(int value)=>new{kind="const",bits=Convert.ToString(value,2).PadLeft(32,'0'),signed=true};
    internal static string Fixture()=>JsonSerializer.Serialize(new{format="velosim.rtl.v1",signals=new[]{new{name="clk",width=1,direction="input"},new{name="q",width=32,direction="output"}},memories=new[]{new{name="m",width=8,depth=1,dynamic=true}},continuous=Array.Empty<object>(),clock="clk",body=new{kind="block",items=new object[]{new{kind="allocate",name="m",count=Count(2),preserve=false},new{kind="nba",target=Ref("q"),value=new{kind="size",name="m"}}}}});

    [Test]
    public void AllocationIsOwnedTypedAndInitiallyEmpty()
    {
        var design=RtlDesign.Parse(Fixture());Assert.That(design.Memories.Single().Dynamic,Is.True);
        foreach(string emitted in new[]{design.EmitCpp(),design.EmitModelHeader("dut")}){
            Assert.That(emitted,Does.Contain("std::vector<W>(0,"));Assert.That(emitted,Does.Contain("dynamic_allocate("));
        }
    }

    [TestCase(0),TestCase(1),TestCase(2),TestCase(3),TestCase(4),TestCase(5),TestCase(6),TestCase(7)]
    public void MalformedLifetimesFailClosed(int fault)
    {
        var node=JsonNode.Parse(Fixture())!;var memory=node["memories"]![0]!;var allocation=node["body"]!["items"]![0]!;
        switch(fault){case 0:memory["dynamic"]=false;break;case 1:memory["depth"]=2;break;case 2:memory["lower"]=1;break;case 3:memory["dimensions"]=new JsonArray();break;case 4:allocation["preserve"]="yes";break;case 5:allocation["count"]=null;break;case 6:allocation["name"]="missing";break;case 7:allocation["extra"]=true;break;}
        Assert.That(()=>RtlDesign.Parse(node.ToJsonString()),Throws.TypeOf<IrValidationException>());
    }

    [Test]
    public void DynamicMetadataPropertyRejectsEveryNoncanonicalDepth()
    {
        Prop.ForAll<int>(depth=>{var node=JsonNode.Parse(Fixture())!;node["memories"]![0]!["depth"]=depth;try{RtlDesign.Parse(node.ToJsonString());return depth==1;}catch(IrValidationException){return depth!=1;}})
            .Check(Config.QuickThrowOnFailure.WithMaxTest(500).WithReplay(20261101,864013));
    }

    [TestCase("extra"),TestCase("index"),TestCase("signed-index"),TestCase("shape-form")]
    public void StorageSchemaAndAddressValidationCannotBeSkipped(string fault)
    {
        var node=JsonNode.Parse(Fixture())!;
        node["body"]!["items"]![1]!["value"]=JsonSerializer.SerializeToNode(new{kind="index",name="m",index=Count(0)});
        if(fault=="extra")node["memories"]![0]!["extra"]=true;
        if(fault=="index")node["body"]!["items"]![1]!["value"]!["index"]=JsonSerializer.SerializeToNode(Ref("missing"));
        if(fault=="signed-index")node["body"]!["items"]![1]!["value"]!["index"]=JsonSerializer.SerializeToNode(new{kind="const",bits="1",signed=true});
        if(fault=="shape-form"){node["body"]!["items"]![1]!["value"]!.AsObject().Remove("index");node["body"]!["items"]![1]!["value"]!["indices"]=new JsonArray(JsonSerializer.SerializeToNode(Count(0)));}
        Assert.That(()=>RtlDesign.Parse(node.ToJsonString()),Throws.TypeOf<IrValidationException>());
    }

    [Test]
    public void OptionalStorageMetadataAndFixedShapesRemainTyped()
    {
        var node=JsonNode.Parse(Fixture())!;var memory=node["memories"]![0]!;
        memory["signed"]=true;memory["two_state"]=true;memory["lower"]=0;
        var dynamic=RtlDesign.Parse(node.ToJsonString()).Memories.Single();Assert.That(dynamic.Signed&&dynamic.TwoState&&dynamic.Dynamic,Is.True);
        memory["dynamic"]=false;memory["depth"]=2;memory["dimensions"]=new JsonArray(new JsonObject{["left"]=2,["right"]=3});
        node["body"]=JsonSerializer.SerializeToNode(new{kind="nba",target=Ref("q"),value=new{kind="index",name="m",indices=new[]{Count(2)}}});
        var design=RtlDesign.Parse(node.ToJsonString());Assert.That(design.Memories.Single().Dimensions,Is.EqualTo(new[]{new RtlDimension(2,3)}));
        Assert.That(design.EmitCpp(),Does.Contain("array_index("));
        memory["dimensions"]![0]!["extra"]=1;Assert.That(()=>RtlDesign.Parse(node.ToJsonString()),Throws.TypeOf<IrValidationException>());
    }

    [Test]
    public void DynamicElementsCannotMixNbaWithLifetimeChanges()
    {
        var node=JsonNode.Parse(Fixture())!;node["body"]!["items"]!.AsArray().Add(JsonSerializer.SerializeToNode(new{kind="nba",target=new{kind="index",name="m",index=Count(0)},value=Count(1)}));
        Assert.That(()=>RtlDesign.Parse(node.ToJsonString()),Throws.TypeOf<IrValidationException>().With.Message.Contains("blocking"));
    }

    [Test]
    public void LifetimeChangesCannotRaceOtherProcessesOrCombinationalReaders()
    {
        var node=JsonNode.Parse(Fixture())!;var items=node["body"]!["items"]!.AsArray();
        node["body"]=JsonSerializer.SerializeToNode(new{kind="parallel",items=new[]{new{kind="block",items=new[]{items[0]!.DeepClone()}},new{kind="block",items=new[]{items[1]!.DeepClone()}}}});
        Assert.That(()=>RtlDesign.Parse(node.ToJsonString()),Throws.TypeOf<IrValidationException>().With.Message.Contains("isolated"));
        node=JsonNode.Parse(Fixture())!;node["combinational"]=new JsonArray(node["body"]!["items"]![0]!.DeepClone());
        Assert.That(()=>RtlDesign.Parse(node.ToJsonString()),Throws.TypeOf<IrValidationException>().With.Message.Contains("Combinational"));
    }

    [Test]
    public void StartupAllocationCannotDependOnHostInputs()
    {
        var node=JsonNode.Parse(Fixture())!;node["initialization"]=node["body"]!["items"]![0]!.DeepClone();node["initialization"]!["count"]=JsonSerializer.SerializeToNode(Ref("clk"));
        Assert.That(()=>RtlDesign.Parse(node.ToJsonString()),Throws.TypeOf<IrValidationException>().With.Message.Contains("Initialization cannot access"));
    }

    [TestCase(false,8),TestCase(true,8),TestCase(false,256)]
    public Task AllocationResizeAccessAndDeleteMatchOriginalHdl(bool twoState,int width)
    {
        string source=$"module dut(input logic clk,input logic[2:0]op,input logic[8:0]n,i,input logic[{width-1}:0]a,output logic[{width-1}:0]q,output int sz);{(twoState?"bit":"logic")}[{width-1}:0]m[];initial begin m=new[2];m[0]=1;end always @(posedge clk)begin case(op)0:m=new[n];1:m=new[n](m);2:m.delete();3:m[i]=a;4:begin m=new[n](m);m=new[257];end default:begin end endcase {(twoState?"if(i<m.size())q<=m[i];else q<=0;":"q<=m[i];")}sz<=m.size();end endmodule";
        var vectors=new List<Dictionary<string,string>>();var expected=new List<Dictionary<string,string>>();var cells=new List<string>{new string('0',width-1)+"1",new string(twoState?'0':'x',width)};
        string Bits(int value,int size)=>Convert.ToString(value,2).PadLeft(size,'0');
        void Step(int op,int count,int index,string? payload=null,bool unknownIndex=false){
            string a=payload??new string('1',width),initial=new(twoState?'0':'x',width);
            vectors.Add(new(){["op"]=Bits(op,3),["n"]=Bits(count,9),["i"]=unknownIndex?new string('x',9):Bits(index,9),["a"]=a});
            if(op==0)cells=Enumerable.Repeat(initial,count).ToList();
            if(op==1){while(cells.Count<count)cells.Add(initial);if(cells.Count>count)cells.RemoveRange(count,cells.Count-count);}
            if(op==2)cells.Clear();
            if(op==3&&!unknownIndex&&index<cells.Count)cells[index]=twoState?a.Replace('x','0').Replace('z','0'):a;
            expected.Add(new(){["q"]=!unknownIndex&&index<cells.Count?cells[index]:initial,["sz"]=Bits(cells.Count,32)});
        }
        Step(5,0,0);Step(1,4,0);Step(3,0,3);Step(1,2,0);Step(1,4,3);Step(0,1,0);Step(3,0,0,string.Concat(Enumerable.Range(0,width).Select(i=>"01xz"[i%4])));Step(5,0,0);Step(3,0,9);Step(5,0,0,null,true);Step(2,0,0);Step(1,0,0);Step(0,256,255);Step(3,0,255);Step(1,256,255);Step(1,1,0);Step(2,0,0);
        const string rollback="probe.op=0;probe.n=4;probe.i=3;probe.a=7;probe.clk=0;probe.eval();probe.clk=1;probe.eval();probe.op=3;probe.clk=0;probe.eval();probe.clk=1;probe.eval();auto saved=probe.q.bits();probe.op=4;probe.n=1;probe.clk=0;probe.eval();probe.clk=1;bool rejected=false;try{probe.eval();}catch(const std::length_error&){rejected=true;}if(!rejected||probe.q.bits()!=saved||probe.sz.bits()!=\"00000000000000000000000000000100\")return 81;probe.op=5;probe.eval();if(probe.sz.bits()!=\"00000000000000000000000000000100\"||probe.q.bits()!=saved)return 82;";
        return RtlIndexedConformanceTests.Qualify($"dynamic-{twoState}-{width}",source,[new("clk",1,"input"),new("op",3,"input"),new("n",9,"input"),new("i",9,"input"),new("a",width,"input"),new("q",width,"output"),new("sz",32,"output",Signed:true,TwoState:true)],vectors,sdk:true,selectedProbe:false,sdkProbe:rollback,expectedOutputs:expected,expectedMemories:[new("m",width,1,TwoState:twoState,Dynamic:true)]);
    }
}
