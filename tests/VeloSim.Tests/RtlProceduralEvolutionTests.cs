using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace VeloSim.Tests;

/// <summary>Same-original-HDL startup and input-controlled procedural loop regressions.</summary>
[NonParallelizable]
public class RtlProceduralEvolutionTests
{
    [TestCase(1),TestCase(2),TestCase(3),TestCase(4),TestCase(5),TestCase(6),TestCase(7),TestCase(8),TestCase(9),TestCase(10)]
    public Task InitializationAndRuntimeLoopsMatchOriginalHdl(int id)
    {
        var expected=new List<RtlSignal>{new("a",8,"input"),new("q",8,"output")};
        string source,probe="";
        if(id==1){source="module dut(input logic[7:0]a,output logic[7:0]q);initial q=8'h35;endmodule";probe="if(probe.q.bits()!=\"00110101\")return 21;probe.eval();probe.timeInc(3);probe.eval();if(probe.q.bits()!=\"00110101\")return 22;";}
        else if(id==2||id==3){source=$"module dut(input logic clk,input logic[7:0]a,output logic[7:0]q{(id==2?"=8'h80":"")});{(id==3?"initial begin q=3;q=q+2;end ":"")}always @(posedge clk)q<=q+1;endmodule";expected.Add(new("clk",1,"input"));}
        else if(id==4){source="module dut(input logic clk,input logic[7:0]a,output logic[7:0]q);logic[31:0]i;logic[7:0]m[0:7];initial begin for(i=0;i<8;i++)m[i]=i+8'd10;end always @(posedge clk)q<=m[a];endmodule";expected.Add(new("clk",1,"input"));expected.Add(new("i",32,"internal"));}
        else if(id==10){source="module dut(input logic[7:0]a,output logic[7:0]q);logic[7:0]v=8'hf0;initial begin q=v;repeat(3)q=~q;end endmodule";expected.Add(new("v",8,"internal"));}
        else {
            expected.Add(new("n",4,"input"));bool clocked=id is 8 or 9;
            string loop=id switch{5=>"for(i=0;i<n;i++)q=q+1;",6=>"i=0;while(i<n)begin q=q+1;i++;end",7=>"i=0;do begin q=q+1;i++;end while(i<n);",8=>"repeat(n)q=~q;",_=>"for(i=0;i<n;i++)q<=a+i;"};
            if(id!=8)expected.Add(new("i",32,"internal"));if(clocked)expected.Add(new("clk",1,"input"));
            source=$"module dut({(clocked?"input logic clk,":"")}input logic[7:0]a,input logic[3:0]n,output logic[7:0]q);{(id!=8?"logic[31:0]i;":"")}always {(clocked?"@(posedge clk)":"_comb")} begin q{(id==9?"<=":"=")}a;{loop} end endmodule";
            // Keep always_comb one lexical token; no source rewrite is performed by the simulator.
            source=source.Replace("always _comb","always_comb",StringComparison.Ordinal);
        }
        var vectors=Enumerable.Range(0,id==8?10:12).Select(row=>new Dictionary<string,string>{["a"]=Convert.ToString(row,2).PadLeft(8,'0'),["n"]=row<10?Convert.ToString(row%8,2).PadLeft(4,'0'):new string(row==10?'x':'z',4)}).ToList();
        return RtlIndexedConformanceTests.Qualify("procedural-"+id,source,expected.ToArray(),vectors,sdk:true,selectedProbe:false,sdkProbe:probe);
    }

    static object Ref(string n)=>new{kind="ref",name=n};
    static object Constant(string bits)=>new{kind="const",bits};
    static object Block(params object[] items)=>new{kind="block",items};
    static object Set(string n,object value,string kind="blocking")=>new{kind,target=Ref(n),value};
    static string Fixture(object initialization,object? body=null)=>JsonSerializer.Serialize(new{format="velosim.rtl.v1",clock="clk",signals=new[]{new{name="clk",width=1,direction="input"},new{name="a",width=8,direction="input"},new{name="q",width=8,direction="output"}},memories=Array.Empty<object>(),continuous=Array.Empty<object>(),initialization,body=body??Set("q",Ref("a"),"nba")});

    [Test]
    public void EmptyInitializationRejectsUnknownFields()
    {
        Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(Fixture(new{kind="block",items=Array.Empty<object>(),unexpected=true})));
    }

    [TestCase("nba"),TestCase("input"),TestCase("event")]
    public void UnsafeStartupIsRejected(string mode)
    {
        object init=mode=="nba"?Set("q",Constant("1"),"nba"):mode=="input"?Set("q",Ref("a")):new{kind="process",events=new[]{new{signal="clk",edge="posedge"}},body=Set("q",Constant("1"))};
        Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(Fixture(init)));
    }

    [Test]
    public void LoopCountReadersParticipateInBlockingIsolation()
    {
        var node=JsonNode.Parse(Fixture(Block()))!;
        node["body"]=JsonSerializer.SerializeToNode(new{kind="parallel",items=new object[]{Set("q",Ref("a")),new{kind="runtime_repeat",count=Ref("q"),body=Block()}}});
        Assert.That(()=>RtlDesign.Parse(node.ToJsonString()),Throws.TypeOf<IrValidationException>().With.Message.Contains("isolated"));
    }

    [Test]
    public Task ExplicitEventBudgetMatchesStandaloneAndSdk()
    {
        const string source="module dut(input logic clk,input logic[7:0]a,output logic[7:0]q,t);logic[31:0]i;always_comb begin t=a;i=0;while(i<1200)begin t=~t;i=i+1;end end always @(negedge clk)q<=a;endmodule";
        var vectors=Enumerable.Range(0,4).Select(row=>new Dictionary<string,string>{["a"]=Convert.ToString(row,2).PadLeft(8,'0')}).ToList();
        return RtlIndexedConformanceTests.Qualify("event-loop-budget",source,[new("clk",1,"input"),new("a",8,"input"),new("q",8,"output"),new("t",8,"output"),new("i",32,"internal")],vectors,sdk:true,selectedProbe:false);
    }

    [Test]
    public Task SingleClockRuntimeLoopMatchesSdk()
    {
        // Clock-dependent combinational logic is rejected separately; this tests
        // the supported single-clock path with a substantial runtime loop.
        const string source="module dut(input logic clk,input logic[7:0]a,output logic[7:0]q,t);logic[31:0]i;always_comb begin t=a;i=0;while(i<1200)begin t=~t;i=i+1;end end always @(posedge clk)q<=a;endmodule";
        var vectors=Enumerable.Range(0,3).Select(row=>new Dictionary<string,string>{["a"]=Convert.ToString(row,2).PadLeft(8,'0')}).ToList();
        return RtlIndexedConformanceTests.Qualify("single-clock-loop-budget",source,[new("clk",1,"input"),new("a",8,"input"),new("q",8,"output"),new("t",8,"output"),new("i",32,"internal")],vectors,sdk:true,selectedProbe:false);
    }
    [Test]
    public Task BudgetFailureRestoresModelStateAndEdgeHistory()
    {
        const string source="module dut(input logic clk,input logic[7:0]a,input logic[15:0]n,output logic[7:0]q,r);logic[31:0]i;logic[7:0]m[0:1];initial begin q=0;m[0]=3;m[1]=4;end always_comb r=a;always @(posedge clk)begin q<=m[0];i=0;while(i<n)begin m[0]=a;i++;end end endmodule";
        var vectors=Enumerable.Range(0,6).Select(row=>new Dictionary<string,string>{["a"]=Convert.ToString(row,2).PadLeft(8,'0'),["n"]=Convert.ToString(row%3,2).PadLeft(16,'0')}).ToList();
        const string probe="probe.a=0;probe.n=0;probe.clk=0;probe.eval();probe.a=85;probe.n=65535;probe.clk=1;bool caught=false;try{probe.eval();}catch(const std::runtime_error& e){caught=std::string(e.what()).find(\"execution budget\")!=std::string::npos;}if(!caught||probe.q.bits()!=\"00000000\"||probe.r.bits()!=\"00000000\")return 31;probe.n=0;probe.eval();if(probe.q.bits()!=\"00000011\"||probe.r.bits()!=\"01010101\")return 32;";
        return RtlIndexedConformanceTests.Qualify("loop-budget-rollback",source,[new("clk",1,"input"),new("a",8,"input"),new("n",16,"input"),new("q",8,"output"),new("r",8,"output"),new("i",32,"internal")],vectors,sdk:true,selectedProbe:false,sdkProbe:probe);
    }

    [Test]
    public Task InstanceOutputInitializersUseTheirOwnParameterOverrides()
    {
        const string source="module leaf #(parameter int P=1)(input logic clk,output logic[7:0]q=P);always @(posedge clk)q<=q+1;endmodule module dut(input logic clk,output logic[7:0]q,r);leaf #(.P(3))u1(clk,q);leaf #(.P(9))u2(clk,r);endmodule";
        var vectors=Enumerable.Range(0,4).Select(_=>new Dictionary<string,string>()).ToList();var expected=Enumerable.Range(0,4).Select(row=>new Dictionary<string,string>{["q"]=Convert.ToString(4+row,2).PadLeft(8,'0'),["r"]=Convert.ToString(10+row,2).PadLeft(8,'0')}).ToList();
        return RtlIndexedConformanceTests.Qualify("instance-startup",source,[new("clk",1,"input"),new("q",8,"output"),new("r",8,"output"),new("u1.q",8,"internal"),new("u2.q",8,"internal")],vectors,sdk:true,selectedProbe:false,expectedOutputs:expected);
    }
}
