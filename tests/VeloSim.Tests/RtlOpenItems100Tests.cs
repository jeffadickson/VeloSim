using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace VeloSim.Tests;

public class RtlOpenItems100Tests
{
    [Test,Category("RealFifo")]
    public async Task HierarchicalFallingClockAliasMatchesIcarus(){const string source="module leaf(input logic clk,input logic[7:0]d,output logic[7:0]q);always @(negedge clk)q<=d;endmodule module dut(input logic clk,aux,input logic[7:0]a,output logic[7:0]q,r);always_ff @(posedge clk)r<=a;leaf u(.clk(aux),.d(r),.q(q));endmodule";var vectors=Enumerable.Range(0,24).Select(row=>new Dictionary<string,string>{["aux"]="001100xx11zz00xz10101001"[row].ToString(),["a"]=Convert.ToString(row*7,2).PadLeft(8,'0')}).ToList();await RtlIndexedConformanceTests.Qualify("hierarchy-falling-clock",source,[new("clk",1,"input"),new("aux",1,"input"),new("a",8,"input"),new("q",8,"output"),new("r",8,"output"),new("u.d",8,"internal"),new("u.q",8,"internal")],vectors,sdk:true,selectedProbe:false);}
    [Test,Category("RealFifo")]
    public async Task SignedTwoStateBlockingAndSelectedNbaMatchIcarus(){const string source="module dut(input logic clk,input bit signed[7:0]a,output bit signed[15:0]q,output bit[7:0]selected);bit signed[7:0]tmp;always_ff @(posedge clk)begin tmp=a;q<=tmp+16'sd1;selected<=a;selected[3:0]<=4'bx1z0;end endmodule";var vectors=Enumerable.Range(0,16).Select(row=>new Dictionary<string,string>{["a"]=string.Concat(Enumerable.Range(0,8).Select(bit=>"01xz"[(bit+row)%4]))}).ToList();await RtlIndexedConformanceTests.Qualify("signed-bit-blocking-selected",source,[new("clk",1,"input"),new("a",8,"input",Signed:true,TwoState:true),new("q",16,"output",Signed:true,TwoState:true),new("selected",8,"output",TwoState:true),new("tmp",8,"internal",Signed:true,TwoState:true)],vectors,sdk:true,selectedProbe:false);}
    [Test,Category("RealFifo")]
    public async Task PrimaryFallingEdgeDoesNotInventStartupTransition(){const string source="module dut(input logic clk,input logic[7:0]a,output logic[7:0]q);always @(negedge clk)q<=a;endmodule";var vectors=Enumerable.Range(0,8).Select(row=>new Dictionary<string,string>{["a"]=Convert.ToString(row*19,2).PadLeft(8,'0')}).ToList();await RtlIndexedConformanceTests.Qualify("primary-negedge-startup",source,[new("clk",1,"input"),new("a",8,"input"),new("q",8,"output")],vectors,sdk:true,selectedProbe:false);}
    [Test]
    public void NestedRepeatExecutionBudgetRejectsExplosion(){var node=Fixture();var body=node["body"]!["body"]!.DeepClone();for(int i=0;i<2;++i)body=new JsonObject{["kind"]="repeat",["count"]=256,["body"]=body};node["body"]!["body"]=body;Assert.That(Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(node.ToJsonString()))!.Message,Does.Contain("budget"));}
    [TestCase(-1),TestCase(257)]
    public void InvalidRepeatCountRejects(int count){var node=Fixture();node["body"]!["body"]=new JsonObject{["kind"]="repeat",["count"]=count,["body"]=node["body"]!["body"]!.DeepClone()};Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(node.ToJsonString()));}
    [TestCase(-1,1),TestCase(int.MaxValue,2),TestCase(0,257)]
    public void InvalidMemoryBoundsReject(int lower,int depth){var node=Fixture();node["memories"]!.AsArray().Add(new JsonObject{["name"]="m",["width"]=8,["depth"]=depth,["lower"]=lower});Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(node.ToJsonString()));}
    [TestCase(91),TestCase(92),TestCase(93),TestCase(94),TestCase(95),TestCase(96),TestCase(97),TestCase(98),TestCase(99),TestCase(100),Category("RealFifo")]
    public async Task Iterations091To100UnsupportedBoundaries(int iteration)
    {
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires pinned Surelog/UHDM.");
        string[] bodies=iteration switch {
            91=>["initial q=0;"],92=>["always_ff @(posedge clk) #1 q<=a;"],93=>["always_comb q=a;"],
            // Pure automatic calls are now qualified; preserve the unsupported static-lifetime boundary.
            94=>["function logic[7:0]f(input logic[7:0]x);f=x;endfunction always_ff @(posedge clk)q<=f(a);"],
            95=>["task automatic t(input logic[7:0]x);q<=x;endtask always @(posedge clk)t(a);"],
            96=>["typedef struct packed{logic[3:0]hi;logic[3:0]lo;} pair_t;pair_t s;always_ff @(posedge clk)begin s<=a;q<=s;end"],
            97=>["typedef union packed{logic[7:0]hi;logic[7:0]lo;} pair_t;pair_t s;always_ff @(posedge clk)begin s<=a;q<=s;end"],
            98=>["typedef enum logic[1:0]{A=0,B=1} state_t;state_t state;always_ff @(posedge clk)begin state<=A;q<={6'b0,state};end"],
            99=>["logic[7:0]m[0:1][0:1];always_ff @(posedge clk)begin m[0][0]<=a;q<=m[0][0];end"],
            _=>["logic[7:0]m[$];always @(posedge clk)begin m.push_back(a);q<=m[0];end","logic[7:0]m[];initial m=new[2];always_ff @(posedge clk)begin m[0]<=a;q<=m[0];end","logic[7:0]m[int];always @(posedge clk)begin m[0]=a;q<=m[0];end"]};
        var checkout=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);while(checkout is not null&&!File.Exists(Path.Combine(checkout.FullName,"native/rtl/runtime.h")))checkout=checkout.Parent;
        Assert.That(checkout,Is.Not.Null);string path=Path.Combine(checkout!.FullName,"artifacts","open-items-100","boundary-"+iteration+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        for(int variant=0;variant<bodies.Length;++variant){string source=Path.Combine(path,$"design{variant}.sv");File.WriteAllText(source,"module dut(input logic clk,input logic[7:0]a,output logic[7:0]q);"+bodies[variant]+" endmodule\n");var frontend=await new SurelogFrontend().ElaborateAsync([source],Path.Combine(path,"surelog"+variant),"dut",workers:4);string output=Path.Combine(path,$"design{variant}.json");var result=await new ProcessRunner().RunAsync(Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL")??Path.Combine(checkout.FullName,"artifacts/with-uhdm/bin/velosim-uhdm-rtl"),[frontend.UhdmPath,output,"dut"],path,TimeSpan.FromMinutes(1));if(result.ExitCode==0)Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(File.ReadAllText(output)),"Unsupported source must not execute silently");else Assert.That(result.ExitCode,Is.EqualTo(1),result.StandardError);}
        TestContext.Progress.WriteLine($"OPEN_ITEM_ITERATION {iteration:000} PASS unsupported boundary");
    }
    [TestCase(81,1),TestCase(82,2),TestCase(83,3),TestCase(84,7),TestCase(85,16),TestCase(86,31),TestCase(87,64),TestCase(88,128),TestCase(89,255),TestCase(90,256),Category("RealFifo")]
    public async Task Iterations081To090FixedArrayBounds(int iteration,int depth)
    {
        int width=iteration%3==0?128:iteration%3==1?8:65,lower=iteration%2==0?4:0,upper=lower+depth-1;bool descending=iteration%2==0;
        string source=$"module dut(input logic clk,input logic[31:0]i,input logic[{width-1}:0]a,output logic[{width-1}:0]q);logic[{width-1}:0]m[{(descending?upper:lower)}:{(descending?lower:upper)}];always_ff @(posedge clk)begin m[i]<=a;q<=m[i];end endmodule";
        var vectors=Enumerable.Range(0,32).Select(row=>new Dictionary<string,string>{["i"]=row==27?new('x',32):row==28?new('z',32):Convert.ToString(row is 24 or 25?upper:row==29?upper+1:row==30?0:lower+(row/2)%depth,2).PadLeft(32,'0'),["a"]=string.Concat(Enumerable.Range(0,width).Select(bit=>"01xz"[(bit+row)%4]))}).ToList();
        await RtlIndexedConformanceTests.Qualify($"open-{iteration}",source,[new("clk",1,"input"),new("i",32,"input"),new("a",width,"input"),new("q",width,"output")],vectors,sdk:true,selectedProbe:false);
        TestContext.Progress.WriteLine($"OPEN_ITEM_ITERATION {iteration:000} PASS fixed array depth {depth}");
    }
    [TestCase(71,1,8),TestCase(72,8,1),TestCase(73,7,16),TestCase(74,16,7),TestCase(75,64,65),TestCase(76,65,64,Category="GeneratedPortSizing"),TestCase(77,65,128,Category="GeneratedPortSizing"),TestCase(78,128,65,Category="GeneratedPortSizing"),TestCase(79,96,128,Category="GeneratedPortSizing"),TestCase(80,128,96,Category="GeneratedPortSizing"),Category("RealFifo")]
    public async Task Iterations071To080PortSizing(int iteration,int parentWidth,int childWidth)
    {
        bool parentSigned=iteration%2!=0,childSigned=iteration%3!=0,generated=iteration>=76;string ps=parentSigned?"signed":"",cs=childSigned?"signed":"",scope=generated?"g.u":"u";
        string source=$"module leaf #(parameter int W={childWidth})(input logic clk,input logic {cs}[W-1:0]a,output logic {cs}[W-1:0]q);always_ff @(posedge clk)q<=a;endmodule module dut(input logic clk,input logic {ps}[{parentWidth-1}:0]a,output logic[{parentWidth-1}:0]q);{(generated?"if(1)begin:g ":"")}leaf u(.clk(clk),.a(a),.q(q));{(generated?" end":"")} endmodule";
        var vectors=Enumerable.Range(0,16).Select(row=>new Dictionary<string,string>{["a"]=string.Concat(Enumerable.Range(0,parentWidth).Select(bit=>"01xz"[(bit+row)%4]))}).ToList();
        await RtlIndexedConformanceTests.Qualify($"open-{iteration}",source,[new("clk",1,"input"),new("a",parentWidth,"input",Signed:parentSigned),new("q",parentWidth,"output"),new(scope+".a",childWidth,"internal",Signed:childSigned),new(scope+".q",childWidth,"internal",Signed:childSigned)],vectors,sdk:true,selectedProbe:false);
        TestContext.Progress.WriteLine($"OPEN_ITEM_ITERATION {iteration:000} PASS sized instance ports");
    }
    [TestCase(61,1),TestCase(62,7),TestCase(63,8),TestCase(64,16),TestCase(65,31),TestCase(66,32),TestCase(67,64),TestCase(68,65),TestCase(69,96),TestCase(70,128),Category("RealFifo")]
    public async Task Iterations061To070TwoStateWords(int iteration,int width)
    {
        string source=$"module dut(input logic clk,mode,input bit[{width-1}:0]a,output bit[{width-1}:0]q,r);always_ff @(posedge clk)begin q<=mode?~a:({width}'bx);r<=q;end endmodule";
        var vectors=Enumerable.Range(0,24).Select(row=>new Dictionary<string,string>{["mode"]=row%2==0?"0":"1",["a"]=string.Concat(Enumerable.Range(0,width).Select(bit=>"01xz"[(bit+row)%4]))}).ToList();
        await RtlIndexedConformanceTests.Qualify($"open-{iteration}",source,[new("clk",1,"input"),new("mode",1,"input"),new("a",width,"input",TwoState:true),new("q",width,"output",TwoState:true),new("r",width,"output",TwoState:true)],vectors,sdk:true,selectedProbe:false);
        TestContext.Progress.WriteLine($"OPEN_ITEM_ITERATION {iteration:000} PASS two-state width {width}");
    }
    [TestCase(51),TestCase(52),TestCase(53),TestCase(54),TestCase(55),TestCase(56),Category("RealFifo")]
    public async Task Iterations051To056BlockingExecution(int iteration)
    {
        int width=iteration==52?65:iteration==53?128:8;
        string calculation=iteration==54?"if(mode)temp=~temp;":iteration==55?"case(mode)1'b0:temp=temp+1;default:temp=~temp;endcase":iteration==56?"repeat(3)temp=temp+1;":"temp=temp+1;";
        string source=$"module dut(input logic clk,mode,input logic[{width-1}:0]a,output logic[{width-1}:0]q,r);logic[{width-1}:0]temp;always_ff @(posedge clk)begin temp=a;{calculation} q<=temp;r<=q;end endmodule";
        var vectors=Enumerable.Range(0,24).Select(row=>new Dictionary<string,string>{["mode"]=row%2==0?"0":"1",["a"]=string.Concat(Enumerable.Range(0,width).Select(bit=>row<16?"01"[(bit+row)%2]:"01xz"[(bit+row)%4]))}).ToList();
        await RtlIndexedConformanceTests.Qualify($"open-{iteration}",source,[new("clk",1,"input"),new("mode",1,"input"),new("a",width,"input"),new("q",width,"output"),new("r",width,"output"),new("temp",width,"internal")],vectors,sdk:true,selectedProbe:false);
        TestContext.Progress.WriteLine($"OPEN_ITEM_ITERATION {iteration:000} PASS isolated blocking execution");
    }
    [TestCase(57),TestCase(58),TestCase(59),TestCase(60)]
    public void Iterations057To060BlockingBoundaries(int iteration)
    {
        var node=Fixture();var body=node["body"]!["body"]!;body["kind"]="blocking";
        if(iteration==57)node["body"]!["body"]=new JsonObject{["kind"]="block",["items"]=new JsonArray(body.DeepClone(),new JsonObject{["kind"]="nba",["target"]=new JsonObject{["kind"]="ref",["name"]="q"},["value"]=new JsonObject{["kind"]="ref",["name"]="a"}})};
        if(iteration is 58 or 60){node["signals"]!.AsArray().Add(new JsonObject{["name"]="r",["width"]=8,["direction"]="output"});var assignment=new JsonObject{["kind"]="nba",["target"]=new JsonObject{["kind"]="ref",["name"]="r"},["value"]=new JsonObject{["kind"]="ref",["name"]="q"}};if(iteration==58)node["body"]=new JsonObject{["kind"]="parallel",["items"]=new JsonArray(node["body"]!.DeepClone(),new JsonObject{["kind"]="process",["events"]=node["body"]!["events"]!.DeepClone(),["body"]=assignment})};else node["continuous"]!.AsArray().Add(new JsonObject{["target"]=assignment["target"]!.DeepClone(),["value"]=assignment["value"]!.DeepClone()});}
        if(iteration==59)body["target"]=new JsonObject{["kind"]="bitselect",["value"]=new JsonObject{["kind"]="ref",["name"]="a"},["index"]=new JsonObject{["kind"]="const",["bits"]="0"}};
        Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(node.ToJsonString()));TestContext.Progress.WriteLine($"OPEN_ITEM_ITERATION {iteration:000} PASS blocking boundary");
    }
    [TestCase(41,0),TestCase(42,1),TestCase(43,2),TestCase(44,3),TestCase(45,8),TestCase(46,16),TestCase(47,32),TestCase(48,64),TestCase(49,128),TestCase(50,256),Category("RealFifo")]
    public async Task Iterations041To050ConstantRepeat(int iteration,int count)
    {
        string source=$"module dut(input logic clk,rst,input logic[7:0]a,output logic[7:0]q,r);always_ff @(posedge clk)begin if(rst)q<=0;else begin q<=a;repeat({count})begin q<=q+8'd1;end end r<=q;end endmodule";
        var vectors=Enumerable.Range(0,12).Select(row=>new Dictionary<string,string>{["a"]=Convert.ToString(row*17,2).PadLeft(8,'0'),["rst"]=row==0?"1":"0"}).ToList();
        await RtlIndexedConformanceTests.Qualify($"open-{iteration}",source,[new("clk",1,"input"),new("rst",1,"input"),new("a",8,"input"),new("q",8,"output"),new("r",8,"output")],vectors,sdk:true,selectedProbe:false);
        TestContext.Progress.WriteLine($"OPEN_ITEM_ITERATION {iteration:000} PASS constant repeat count {count}");
    }
    [TestCase(31),TestCase(32),TestCase(33),TestCase(34),TestCase(35),TestCase(36),TestCase(37),TestCase(38),TestCase(39),TestCase(40),Category("RealFifo")]
    public async Task Iterations031To040AsynchronousControls(int iteration)
    {
        int width=iteration%3==0?128:iteration%3==1?8:65;bool positive=iteration%2==0,multiple=iteration>=36;
        string resetEdge=positive?"posedge":"negedge",condition=positive?"rst":"!rst";
        string source=$"module dut(input logic clk,rst,preset,input logic[{width-1}:0]a,output logic[{width-1}:0]q,r);always_ff @(posedge clk or {resetEdge} rst{(multiple?" or posedge preset":"")})if({condition}{(multiple?"||preset":"")})q<='0;else q<=a;always_ff @(posedge clk)r<=q;endmodule";
        var vectors=Enumerable.Range(0,24).Select(row=>new Dictionary<string,string>{["rst"]="001100xx11zz00xz10101001"[row].ToString(),["preset"]=row%5==0?"1":"0",["a"]=string.Concat(Enumerable.Range(0,width).Select(bit=>"01xz"[(bit+row/3)%4]))}).ToList();
        await RtlIndexedConformanceTests.Qualify($"open-{iteration}",source,[new("clk",1,"input"),new("rst",1,"input"),new("preset",1,"input"),new("a",width,"input"),new("q",width,"output"),new("r",width,"output")],vectors,sdk:true,selectedProbe:false);
        TestContext.Progress.WriteLine($"OPEN_ITEM_ITERATION {iteration:000} PASS asynchronous controls");
    }
    [TestCase(21,1),TestCase(22,7),TestCase(23,8),TestCase(24,16),TestCase(25,31),TestCase(26,32),TestCase(27,64),TestCase(28,65),TestCase(29,96),TestCase(30,128),Category("RealFifo")]
    public async Task Iterations021To030InputClockExecution(int iteration,int width)
    {
        string edge=iteration%2==0?"negedge":"posedge",keyword=iteration%3==0?"always":"always_ff";
        string source=$"module dut(input logic clk,aux,input logic[{width-1}:0]a,output logic[{width-1}:0]q,r);always_ff @(posedge clk)q<=a;{keyword} @({edge} aux)r<=q;endmodule";
        var vectors=Enumerable.Range(0,24).Select(row=>new Dictionary<string,string>{["aux"]="001100xx11zz00xz10101001"[row].ToString(),["a"]=string.Concat(Enumerable.Range(0,width).Select(bit=>"01xz"[(bit+row)%4]))}).ToList();
        await RtlIndexedConformanceTests.Qualify($"open-{iteration}",source,[new("clk",1,"input"),new("aux",1,"input"),new("a",width,"input"),new("q",width,"output"),new("r",width,"output")],vectors,sdk:true,selectedProbe:false);
        TestContext.Progress.WriteLine($"OPEN_ITEM_ITERATION {iteration:000} PASS input clock execution");
    }
    internal static JsonNode Fixture() => JsonNode.Parse(JsonSerializer.Serialize(new { format="velosim.rtl.v1",signals=new[]{new{name="clk",width=1,direction="input"},new{name="aux",width=1,direction="input"},new{name="a",width=8,direction="input"},new{name="q",width=8,direction="output"}},memories=Array.Empty<object>(),continuous=Array.Empty<object>(),clock="clk",body=new{kind="process",events=new[]{new{signal="clk",edge="posedge"}},body=new{kind="nba",target=new{kind="ref",name="q"},value=new{kind="ref",name="a"}}}}))!;
    [TestCase(1),TestCase(2),TestCase(3),TestCase(4),TestCase(5),TestCase(6),TestCase(7),TestCase(8),TestCase(9),TestCase(10)]
    public void Iterations001To010EventContract(int iteration)
    {
        var node=Fixture(); var process=node["body"]!;
        switch(iteration){
            case 2: process["events"]![0]!["edge"]="negedge";break;
            case 3: process["events"]![0]!["signal"]="aux";break;
            case 4: process["events"]!.AsArray().Add(new JsonObject{["signal"]="aux",["edge"]="negedge"});break;
            case 5: process["events"]=new JsonArray();break;
            case 6: process["events"]!.AsArray().Add(process["events"]![0]!.DeepClone());break;
            case 7: process["events"]![0]!["signal"]="a";break;
            case 8: process["events"]![0]!["signal"]="q";break;
            case 9: process["body"]=new JsonObject{["kind"]="block",["items"]=new JsonArray(process.DeepClone())};break;
            case 10: node["body"]=new JsonObject{["kind"]="parallel",["items"]=new JsonArray(process.DeepClone(),process["body"]!.DeepClone())};break;
        }
        if(iteration<=4)Assert.That(RtlDesign.Parse(node.ToJsonString()).EventDriven,Is.True);
        else Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(node.ToJsonString()));
        TestContext.Progress.WriteLine($"OPEN_ITEM_ITERATION {iteration:000} PASS event contract");
    }
}
