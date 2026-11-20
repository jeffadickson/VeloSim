using System.Text.Json;
using System.Text.Json.Nodes;
using FsCheck;
using FsCheck.Fluent;
using NUnit.Framework;

namespace VeloSim.Tests;

/// <summary>Bounded queue lifetimes, strict IR guards and same-input HDL comparisons.</summary>
[NonParallelizable]
public class RtlQueueTests
{
    private static object Ref(string name)=>new{kind="ref",name};
    private static object Const(string bits)=>new{kind="const",bits};
    private static JsonNode Fixture()=>JsonSerializer.SerializeToNode(new{
        format="velosim.rtl.v1",clock="clk",
        signals=new[]{new{name="clk",width=1,direction="input"},new{name="q",width=8,direction="output"},new{name="sz",width=32,direction="output"}},
        memories=new[]{new{name="m",width=8,depth=1,queue=true}},continuous=Array.Empty<object>(),
        body=new{kind="block",items=new object[]{new{kind="queue_push",name="m",front=false,value=Const("10101010")},new{kind="queue_pop",name="m",front=true,target=Ref("q")},new{kind="nba",target=Ref("sz"),value=new{kind="size",name="m"}}}}
    })!;

    [Test]
    public void QueueStorageStartsEmptyAndUsesBothEmitters()
    {
        var design=RtlDesign.Parse(Fixture().ToJsonString());
        Assert.That(design.Memories.Single().Queue,Is.True);
        foreach(string source in new[]{design.EmitCpp(),design.EmitModelHeader("dut")}){
            Assert.That(source,Does.Contain("std::vector<W>(0,"));
            Assert.That(source,Does.Contain("queue_push("));Assert.That(source,Does.Contain("queue_pop("));
        }
    }

    [Test]
    public void DiscardedPopsAndTypedResultsRemainExplicit()
    {
        var node=Fixture();var memory=node["memories"]![0]!;memory["signed"]=true;memory["two_state"]=true;
        var design=RtlDesign.Parse(node.ToJsonString());
        Assert.That(design.EmitCpp(),Does.Contain("signed_resize(queue_value"));
        Assert.That(design.EmitCpp(),Does.Contain(",8,true,false)"));
        node["body"]!["items"]![1]!.AsObject().Remove("target");
        node["initialization"]=JsonSerializer.SerializeToNode(new{kind="blocking",target=Ref("q"),value=Const("0")});
        Assert.That(RtlDesign.Parse(node.ToJsonString()).EmitCpp(),Does.Contain("(void)queue_value"));
    }

    [TestCase(0),TestCase(1),TestCase(2),TestCase(3),TestCase(4),TestCase(5),TestCase(6),TestCase(7),TestCase(8)]
    public void InvalidQueueContractsFailClosed(int fault)
    {
        var node=Fixture();var memory=node["memories"]![0]!;var push=node["body"]!["items"]![0]!;var pop=node["body"]!["items"]![1]!;
        switch(fault){
            case 0:memory["dynamic"]=true;break;
            case 1:memory["depth"]=2;break;
            case 2:memory["lower"]=1;break;
            case 3:memory["dimensions"]=new JsonArray();break;
            case 4:push["front"]="yes";break;
            case 5:memory["queue"]=false;break;
            case 6:pop["target"]=JsonSerializer.SerializeToNode(Ref("clk"));break;
            case 7:push["extra"]=1;break;
            case 8:push["kind"]="allocate";push.AsObject().Remove("front");push.AsObject().Remove("value");push["count"]=JsonSerializer.SerializeToNode(Const("1"));push["preserve"]=false;break;
        }
        Assert.That(()=>RtlDesign.Parse(node.ToJsonString()),Throws.TypeOf<IrValidationException>());
    }

    [Test]
    public void QueueMetadataDepthPropertyIsCanonical()
    {
        Prop.ForAll<int>(depth=>{var node=Fixture();node["memories"]![0]!["depth"]=depth;try{RtlDesign.Parse(node.ToJsonString());return depth==1;}catch(IrValidationException){return depth!=1;}})
            .Check(Config.QuickThrowOnFailure.WithMaxTest(100).WithReplay(20261102,864013));
    }

    [TestCase("process"),TestCase("combinational"),TestCase("mixed"),TestCase("nba-element"),TestCase("initial-input")]
    public void QueueSideEffectsCannotEscapeOrderedOwnership(string fault)
    {
        var node=Fixture();var items=node["body"]!["items"]!.AsArray();
        if(fault=="process")node["body"]=JsonSerializer.SerializeToNode(new{kind="parallel",items=new object[]{new{kind="block",items=new[]{items[0]!.DeepClone(),items[1]!.DeepClone()}},items[2]!.DeepClone()}});
        if(fault=="combinational")node["combinational"]=new JsonArray(items[0]!.DeepClone());
        if(fault=="mixed")items.Add(JsonSerializer.SerializeToNode(new{kind="nba",target=Ref("q"),value=Const("1")}));
        if(fault=="nba-element")items.Add(JsonSerializer.SerializeToNode(new{kind="nba",target=new{kind="index",name="m",index=Const("0")},value=Const("1")}));
        if(fault=="initial-input")node["initialization"]=JsonSerializer.SerializeToNode(new{kind="delete",name="m",index=Ref("clk")});
        Assert.That(()=>RtlDesign.Parse(node.ToJsonString()),Throws.TypeOf<IrValidationException>());
    }

    [TestCase(0),TestCase(1),TestCase(2),TestCase(3)]
    public async Task UnsupportedQueueHdlIsRejectedExplicitly(int fault)
    {
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires pinned frontend");
        string declaration=fault==0?"logic[7:0]m[$:3];":"logic[7:0]m[$];";
        string operation=fault switch{0=>"m.push_back(a);q=m.pop_front();",1=>"q=m.pop_front()+a;",2=>"q<=m.pop_front();",_=>"m.insert(0,a);q=a;"};
        string source=$"module dut(input logic clk,input logic[7:0]a,output logic[7:0]q);{declaration}always @(posedge clk)begin {operation}end endmodule";
        string root=Environment.GetEnvironmentVariable("VELOSIM_RTL_EVIDENCE_ROOT")??throw new IOException("Set native evidence root");
        if(!Directory.Exists(root))throw new IOException("Evidence root missing");
        string path=Path.Combine(root,"queue-reject-"+fault+"-"+Guid.NewGuid().ToString("N"));
        if(!Directory.Exists(path))Directory.CreateDirectory(path);
        string file=Path.Combine(path,"design.sv");File.WriteAllText(file,source);
        var runner=new RecordingProcessRunner(new ProcessRunner());
        var elaboration=await new SurelogFrontend(runner).ElaborateAsync([file],Path.Combine(path,"surelog"),"dut",executable:Environment.GetEnvironmentVariable("VELOSIM_SURELOG")??"surelog",workers:4);
        string exporter=Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL")??throw new IOException("Set exporter path");
        var result=await runner.RunAsync(exporter,[elaboration.UhdmPath,Path.Combine(path,"design.json"),"dut"],path,TimeSpan.FromMinutes(2));
        File.WriteAllText(Path.Combine(path,"report.json"),JsonSerializer.Serialize(new{source=ArtifactMetadata.Fingerprint(file),commands=runner.Commands,result.ExitCode,result.StandardError}));
        Assert.That(result.ExitCode,Is.EqualTo(1));Assert.That(result.StandardError,Does.Contain("RTL lowering rejected:"));
    }

    [TestCase(false,8,false),TestCase(true,8,false),TestCase(false,128,true)]
    public Task QueueMethodsMatchOriginalHdlAndIndependentHistory(bool twoState,int width,bool signed)
    {
        int resultWidth=signed?width+8:width;
        // Icarus returns X for an empty bit-queue pop; guard that reference limitation while native tests check default zero directly.
        string frontPop=twoState?"if(m.size()>0)q=m.pop_front();else q=0;":"q=m.pop_front();",backPop=twoState?"if(m.size()>0)q=m.pop_back();else q=0;":"q=m.pop_back();";
        string source=$"module dut(input logic clk,input logic[3:0]op,input logic[8:0]i,input logic[{width-1}:0]a,output logic[{resultWidth-1}:0]q,output int sz);{(twoState?"bit":"logic")} {(signed?"signed":"")}[{width-1}:0]m[$];initial m.push_back(1);always @(posedge clk)begin case(op)0:m.push_back(a);1:m.push_front(a);2:begin {frontPop}end 3:begin {backPop}end 4:m[i]=a;5:m.delete(i);6:m.delete();7:begin q=m.pop_front();repeat(256)m.push_back(a);end default:q=m[i];endcase sz<=m.size();end endmodule";
        var vectors=new List<Dictionary<string,string>>();var expected=new List<Dictionary<string,string>>();
        string Bits(int value,int count)=>Convert.ToString(value,2).PadLeft(count,'0');
        var cells=new List<string>{Bits(1,width)};string observed=new('x',resultWidth),empty=new(twoState?'0':'x',width);
        string Extend(string value)=>value.PadLeft(resultWidth,signed?value[0]:'0');
        void Step(int op,int index=0,string? payload=null,bool unknown=false){
            string value=payload??new string('1',width);vectors.Add(new(){["op"]=Bits(op,4),["i"]=unknown?new string('x',9):Bits(index,9),["a"]=value});
            if(twoState)value=value.Replace('x','0').Replace('z','0');
            switch(op){
                case 0:cells.Add(value);break;case 1:cells.Insert(0,value);break;
                case 2:case 3:int cell=op==2?0:cells.Count-1;observed=Extend(cells.Count==0?empty:cells[cell]);if(cells.Count>0)cells.RemoveAt(cell);break;
                case 4:if(!unknown&&index<=cells.Count){if(index==cells.Count)cells.Add(value);else cells[index]=value;}break;
                case 5:if(!unknown&&index<cells.Count)cells.RemoveAt(index);break;
                case 6:cells.Clear();break;
                default:observed=Extend(!unknown&&index<cells.Count?cells[index]:empty);break;
            }
            expected.Add(new(){["q"]=observed,["sz"]=Bits(cells.Count,32)});
        }
        Step(8);Step(0);Step(1,payload:Bits(5,width));Step(2);Step(3);Step(4,1);Step(8,1);Step(5,0);Step(8);Step(4,9);Step(5,9);Step(4,0,payload:string.Concat(Enumerable.Range(0,width).Select(n=>"01xz"[n%4])));Step(8);Step(4,0,unknown:true);Step(5,0,unknown:true);Step(8);Step(6);Step(2);Step(3);Step(4,0,payload:Bits(9,width));Step(8);Step(6);
        // A failed evaluation first pops, then fills and overflows. Same-edge
        // retry must see the original queue, outputs and edge history restored.
        const string rollback="probe.op=0;probe.a=7;probe.clk=0;probe.eval();probe.clk=1;probe.eval();probe.op=8;probe.i=0;probe.clk=0;probe.eval();probe.clk=1;probe.eval();auto saved=probe.q.bits();auto saved_size=probe.sz.bits();probe.op=7;probe.clk=0;probe.eval();probe.clk=1;bool rejected=false;try{probe.eval();}catch(const std::length_error&){rejected=true;}if(!rejected||probe.q.bits()!=saved||probe.sz.bits()!=saved_size)return 91;probe.op=8;probe.eval();if(probe.q.bits()!=saved||probe.sz.bits()!=saved_size)return 92;";
        return RtlIndexedConformanceTests.Qualify($"queue-{twoState}-{width}-{signed}",source,[new("clk",1,"input"),new("op",4,"input"),new("i",9,"input"),new("a",width,"input"),new("q",resultWidth,"output"),new("sz",32,"output",Signed:true,TwoState:true)],vectors,sdk:true,selectedProbe:false,sdkProbe:rollback,expectedOutputs:expected,expectedMemories:[new("m",width,1,Signed:signed,TwoState:twoState,Queue:true)]);
    }
}
