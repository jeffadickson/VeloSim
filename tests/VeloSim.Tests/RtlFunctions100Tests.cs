using FsCheck;
using FsCheck.Fluent;
using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace VeloSim.Tests;

/// <summary>One hundred stable cases for clockless models, control flow and constant captures.</summary>
[NonParallelizable]
public class RtlFunctions100Tests
{
    static readonly int[] Widths=[1,2,4,7,8,16,31,32,33,63,64,65,80,96,127,128,3,9,17,48];
    public static IEnumerable<TestCaseData> Cases(){for(int id=1;id<=100;id++)yield return new TestCaseData(id).SetName($"FunctionIteration{id:000}").SetCategory($"Functions100_{id:000}");}
    [TestCaseSource(nameof(Cases))]
    public async Task Qualify(int id)
    {
        if(id<=80)await Positive(id);else if(id<=90)RejectIr(id);else await RejectHdl(id);
        TestContext.Progress.WriteLine($"FUNCTION_ITERATION {id:000} PASS seed {20261026+id}");
    }
    static async Task Positive(int id)
    {
        int w=Widths[(id-1)%20];string range=$"[{w-1}:0]";string declarations="",logic="",input="a";
        bool clocked=id is >30 and <=40;
        if(id<=20){input=id%4==0?"clk":"a";logic=$"always_comb q=~r;always_comb r={input};";}
        else if(id<=40){declarations=$"function automatic logic {range} f(input logic {range} x,input logic flag);begin if(flag)return ~x;f=x;return f;f='0;end endfunction";logic=clocked?"always_comb r=f(a,c);always @(posedge clk)q<=f(~a,c);":"always_comb begin q=f(a,c);r=f(~a,~c);end";}
        else if(id<=60){
            int count=(id-41)%5;
            string action=id>50?"if(flag)return n;n=~n;":"n=~n;";
            string loop=id%2==0?$"for(i=0;i<{count};{(id%4==0?"i++":"i=i+1")})begin {action} end":$"repeat({count})begin {action} end";
            declarations=$"function automatic logic {range} f(input logic {range} x,input logic flag);logic {range} n;logic[31:0]i;begin n=x;{loop} if(flag)return n;return ~n;end endfunction";
            logic="always_comb begin q=f(a,c);r=f(~a,~c);end";
        }
        else if(id<=75){
            string parameter=id%2==0?"parameter int P=3;":"parameter int P=1;";
            string local=id%2==0?"":"localparam int P=9;";
            declarations=parameter+$"function automatic logic {range} f(input logic {range} x);{local}begin return x+P;end endfunction";
            logic="always_comb begin q=f(a);r=~f(~a);end";
        }
        if(id>75){
            declarations=$"module cell_impl #(parameter int P=1)(input logic {range} a,output logic {range} q);function automatic logic {range} f(input logic {range} x);return x+P;endfunction always_comb q=f(a);endmodule ";
            string source=declarations+$"module dut(input logic {range} a,output logic {range} q,r);cell_impl #(.P(3))u1(a,q);cell_impl #(.P(9))u2(a,r);endmodule";
            var expected=new[]{new RtlSignal("a",w,"input"),new("q",w,"output"),new("r",w,"output"),new("u1.a",w,"internal"),new("u1.q",w,"internal"),new("u2.a",w,"internal"),new("u2.q",w,"internal")};
            await RtlIndexedConformanceTests.Qualify($"functions100-{id:000}",source,expected,Vectors(id,w,"a",false),sdk:true,selectedProbe:false);return;
        }
        string ports=clocked?"input logic clk,c,":id>20?"input logic c,":"";
        string text=$"module dut({ports}input logic {range} {input},output logic {range} q,r);{declarations} {logic} endmodule";
        var signals=new List<RtlSignal>{new(input,w,"input"),new("q",w,"output"),new("r",w,"output")};if(id>20)signals.Add(new("c",1,"input"));if(clocked)signals.Add(new("clk",1,"input"));
        string probe="";
        if(id<=20)probe=$"probe.{input}.bits(\"{new string('0',w)}\");probe.eval();if(probe.q.bits()!=\"{new string('1',w)}\"||probe.r.bits()!=\"{new string('0',w)}\")return 40;probe.{input}.bits(\"{new string('1',w)}\");probe.eval();probe.eval();if(probe.q.bits()!=\"{new string('0',w)}\"||probe.r.bits()!=\"{new string('1',w)}\")return 41;probe.timeInc(2);probe.eval();if(probe.q.bits()!=\"{new string('0',w)}\")return 42;";
        await RtlIndexedConformanceTests.Qualify($"functions100-{id:000}",text,signals.ToArray(),Vectors(id,w,input,id>20),sdk:true,selectedProbe:false,sdkProbe:probe);
    }
    static List<Dictionary<string,string>> Vectors(int id,int width,string input,bool flag){
        var random=new Random(20261026+id);
        return Enumerable.Range(0,24).Select(row=>{var vector=new Dictionary<string,string>{[input]=string.Concat(Enumerable.Range(0,width).Select(bit=>row<4?"01xz"[row]:"01xz"[random.Next(row<16?2:4)]))};if(flag)vector["c"]="01xz"[row%4].ToString();return vector;}).ToList();
    }
    /// <summary>Protect signed trip counts, context sizing and loop-scope int declarations.</summary>
    [TestCase("int i;", "for(i=-1;i<1;i++)n=~n;")]
    [TestCase("logic[31:0]i;", "for(i=2'b11+2'b01;i<5;i=i+1)n=~n;")]
    [TestCase("logic[1:0]i;", "for(i=2'b11+2'b01;i<1;i=i+1)n=~n;")]
    [TestCase("", "for(int i=0;i<3;i++)n=~n;")]
    [TestCase("logic[31:0]i;", "for(i=(2'sb11+2'sb00)+32'd0;i<4;i=i+1)n=~n;")]
    [TestCase("logic[31:0]i;", "for(i=32'd0+(2'sb11+2'sb00);i<4;i=i+1)n=~n;")]
    public Task TypedLoopControl(string counter,string loop)=>RtlIndexedConformanceTests.Qualify("typed-loop",$"module dut(input logic[7:0]a,output logic[7:0]q);function automatic logic[7:0]f(input logic[7:0]x);logic[7:0]n;{counter} begin n=x;{loop} return n;end endfunction always_comb q=f(a);endmodule",[new("a",8,"input"),new("q",8,"output")],Vectors(301,8,"a",false),sdk:true,selectedProbe:false);
    [Test]
    public Task ConstantOnlyFirstEval()=>RtlIndexedConformanceTests.Qualify("constant-only","module dut(output logic[7:0]q);always_comb q=8'ha5;endmodule",[new("q",8,"output")],[new(),new()],sdk:true,selectedProbe:false,sdkProbe:"probe.eval();if(probe.q.bits()!=\"10100101\")return 50;probe.timeInc(5);probe.eval();if(probe.q.bits()!=\"10100101\")return 51;");
    [Test]
    public Task SignedWideParameter()=>RtlIndexedConformanceTests.Qualify("signed-parameter","module dut(input logic[127:0]a,output logic[127:0]q);parameter logic signed[127:0]P=-128'sd3;function automatic logic[127:0]f(input logic[127:0]x);return x+P;endfunction always_comb q=f(a);endmodule",[new("a",128,"input"),new("q",128,"output")],Vectors(302,128,"a",false),sdk:true,selectedProbe:false);
    [Test]
    public Task ExplicitReturnKeepsAssignedValue()=>RtlIndexedConformanceTests.Qualify("bare-return","module dut(input logic[7:0]a,input logic c,output logic[7:0]q);function automatic logic[7:0]f(input logic[7:0]x,input logic flag);begin f=x;if(flag)return f;f=~x;return f;f='0;end endfunction always_comb q=f(a,c);endmodule",[new("a",8,"input"),new("c",1,"input"),new("q",8,"output")],Vectors(303,8,"a",true),sdk:true,selectedProbe:false);
    [Test]
    public void HiddenClocklessBodyProperty(){
        Prop.ForAll<byte,bool>((size,reverse)=>{
            int width=1+size%128;object empty=new{kind="block",items=Array.Empty<object>()};object hidden=reverse?new{kind="repeat",count=0,body=empty}:new{kind="block",items=new[]{empty}};
            string json=JsonSerializer.Serialize(new{format="velosim.rtl.v1",clock=(string?)null,signals=new[]{new{name="clk",width,direction="input"},new{name="q",width,direction="output"}},memories=Array.Empty<object>(),continuous=new[]{new{target=new{kind="ref",name="q"},value=new{kind="ref",name="clk"}}},body=hidden});
            try{RtlDesign.Parse(json);return false;}catch(IrValidationException error){return error.Message.Contains("Clockless designs require an empty sequential body",StringComparison.Ordinal);}
        }).Label("CLOCKLESS_EMPTY_BODY").Check(Config.QuickThrowOnFailure.WithMaxTest(500).WithReplay(20261027,864013));
    }
    [Test]
    public void MalformedClocklessFuzz(){
        const int seed=20261026;var random=new Random(seed);
        for(int trial=0;trial<500;trial++){
            var node=JsonNode.Parse("""{"format":"velosim.rtl.v1","clock":null,"signals":[{"name":"a","width":1,"direction":"input"},{"name":"q","width":1,"direction":"output"}],"memories":[],"continuous":[{"target":{"kind":"ref","name":"q"},"value":{"kind":"ref","name":"a"}}],"body":{"kind":"block","items":[]}}""")!;
            int shape=random.Next(6);switch(shape){case 0:node["clock"]=random.Next();break;case 1:node["body"]=null;break;case 2:node["body"]!["items"]=null;break;case 3:node["body"]!["extra"]=true;break;case 4:node.AsObject().Remove("clock");break;default:node["clock"]=new JsonArray();break;}
            string json=node.ToJsonString();Assert.That(()=>RtlDesign.Parse(json),Throws.TypeOf<IrValidationException>(),$"seed {seed} trial {trial} shape {shape} counterexample {json}");
        }
    }
    [Test]
    public Task ModuleAndLocalShadowAreBothObservable()=>RtlIndexedConformanceTests.Qualify("module-local-shadow","module dut(input logic[7:0]a,output logic[7:0]q,r);parameter int P=1;function automatic logic[7:0]f(input logic[7:0]x);localparam int P=9;return x+P;endfunction always_comb begin q=f(a);r=a+P;end endmodule",[new("a",8,"input"),new("q",8,"output"),new("r",8,"output")],Vectors(304,8,"a",false),sdk:true,selectedProbe:false);
    [Test]
    public Task InstanceOverridesAndNestedShadowBothObservable(){
        const string source="module cell_impl #(parameter int P=1)(input logic[7:0]a,output logic[7:0]q,r);function automatic logic[7:0]g(input logic[7:0]x);return x+P;endfunction function automatic logic[7:0]f(input logic[7:0]x);localparam int P=9;return g(x)+P;endfunction always_comb begin q=a+P;r=f(a);end endmodule module dut(input logic[7:0]a,output logic[7:0]q,r,s,t);cell_impl #(.P(3))u1(a,q,r);cell_impl #(.P(5))u2(a,s,t);endmodule";
        var signals=new List<RtlSignal>{new("a",8,"input"),new("q",8,"output"),new("r",8,"output"),new("s",8,"output"),new("t",8,"output")};foreach(string instance in new[]{"u1","u2"})foreach(string port in new[]{"a","q","r"})signals.Add(new(instance+"."+port,8,"internal"));
        return RtlIndexedConformanceTests.Qualify("nested-instance-shadow",source,signals.ToArray(),Vectors(305,8,"a",false),sdk:true,selectedProbe:false);
    }
    [Test]
    public Task ParameterCoordinatesRemainFileScoped(){
        const string source="module dut(input logic[7:0]a,output logic[7:0]q,r);alpha u1(a,q);beta u2(a,r);endmodule";
        var files=new Dictionary<string,string>{["alpha.sv"]="module alpha(input logic[7:0]a,output logic[7:0]q);parameter int P=3;function automatic logic[7:0]f(input logic[7:0]x);return x+P;endfunction always_comb q=f(a);endmodule",["beta.sv"]="module beta (input logic[7:0]a,output logic[7:0]q);parameter int P=9;function automatic logic[7:0]f(input logic[7:0]x);return x+P;endfunction always_comb q=f(a);endmodule"};
        var signals=new List<RtlSignal>{new("a",8,"input"),new("q",8,"output"),new("r",8,"output")};foreach(string instance in new[]{"u1","u2"})foreach(string port in new[]{"a","q"})signals.Add(new(instance+"."+port,8,"internal"));
        return RtlIndexedConformanceTests.Qualify("file-scoped-parameters",source,signals.ToArray(),Vectors(306,8,"a",false),sdk:true,selectedProbe:false,extraSources:files);
    }
    [Test]
    public Task ShadowedParameterTypesKeepTheirOwnSizing()=>RtlIndexedConformanceTests.Qualify("shadow-parameter-sizing","module dut(input logic[127:0]a,output logic[127:0]q,r);parameter logic[7:0]P=8'hff;function automatic logic signed[127:0]f(input logic[127:0]x);localparam logic signed[15:0]P=-16'sd2;return P;endfunction always_comb begin q=f(a);r=P+128'b1;end endmodule",[new("a",128,"input"),new("q",128,"output"),new("r",128,"output")],Vectors(307,128,"a",false),sdk:true,selectedProbe:false);
    [Test]
    public Task ScopedLoopLocalsDoNotEscapeEarlyReturn()=>RtlIndexedConformanceTests.Qualify("scoped-loop-return","module dut(input logic[7:0]a,input logic c,output logic[7:0]q);function automatic logic[7:0]f(input logic[7:0]x,input logic flag);logic[7:0]n;begin n=x;if(flag)return n;for(int i=0;i<3;i++)n=~n;return n;end endfunction always_comb q=f(a,c);endmodule",[new("a",8,"input"),new("c",1,"input"),new("q",8,"output")],Vectors(308,8,"a",true),sdk:true,selectedProbe:false);
    [TestCase("P[8]")]
    [TestCase("P[9:8]")]
    [TestCase("P[8+:2]")]
    public async Task ParameterSelectionsFailClosed(string expression){
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires actual frontend");
        string path=Path.Combine(Environment.GetEnvironmentVariable("VELOSIM_RTL_EVIDENCE_ROOT")??Path.Combine(Root(),"artifacts"),"parameter-select-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);string file=Path.Combine(path,"design.sv");
        File.WriteAllText(file,$"module dut(input logic[7:0]a,output logic[7:0]q);parameter logic[15:8]P=8'ha5;function automatic logic[7:0]f(input logic[7:0]x);return x^{expression};endfunction always_comb q=f(a);endmodule");
        var runner=new RecordingProcessRunner(new ProcessRunner());var elaboration=await new SurelogFrontend(runner).ElaborateAsync([file],Path.Combine(path,"surelog"),"dut",workers:4);
        var result=await runner.RunAsync(Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL")??Path.Combine(Root(),"artifacts/with-uhdm/bin/velosim-uhdm-rtl"),[elaboration.UhdmPath,Path.Combine(path,"design.json"),"dut"],path,TimeSpan.FromMinutes(3));
        Assert.That(result.ExitCode,Is.EqualTo(1),result.StandardError);Assert.That(result.StandardError,Does.Contain("parameter selections unsupported"));File.WriteAllText(Path.Combine(path,"report.json"),JsonSerializer.Serialize(new{status="pass",expression,source=ArtifactMetadata.Fingerprint(file),commands=runner.Commands,result.StandardError}));
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task CompoundParameterValuesFailClosed(bool overridden){
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires actual frontend");
        const string value="128'h1000000000000000000000001+128'h1000000000000000000000001";
        string source=overridden?$"module leaf #(parameter logic[127:0]P=1)(input logic[127:0]a,output logic[127:0]q);function automatic logic[127:0]f(input logic[127:0]x);return x+P;endfunction always_comb q=f(a);endmodule module dut(input logic[127:0]a,output logic[127:0]q);leaf #(.P({value}))u(a,q);endmodule":$"module dut(input logic[127:0]a,output logic[127:0]q);parameter logic[127:0]P={value};function automatic logic[127:0]f(input logic[127:0]x);return x+P;endfunction always_comb q=f(a);endmodule";
        string path=Path.Combine(Environment.GetEnvironmentVariable("VELOSIM_RTL_EVIDENCE_ROOT")??Path.Combine(Root(),"artifacts"),"compound-parameter-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);string file=Path.Combine(path,"design.sv");File.WriteAllText(file,source);
        var runner=new RecordingProcessRunner(new ProcessRunner());var elaboration=await new SurelogFrontend(runner).ElaborateAsync([file],Path.Combine(path,"surelog"),"dut",workers:4);
        var result=await runner.RunAsync(Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL")??Path.Combine(Root(),"artifacts/with-uhdm/bin/velosim-uhdm-rtl"),[elaboration.UhdmPath,Path.Combine(path,"design.json"),"dut"],path,TimeSpan.FromMinutes(3));
        Assert.That(result.ExitCode,Is.EqualTo(1),result.StandardError);Assert.That(result.StandardError,Does.Contain("parameter must be materialized or a unary literal"));File.WriteAllText(Path.Combine(path,"report.json"),JsonSerializer.Serialize(new{status="pass",overridden,source=ArtifactMetadata.Fingerprint(file),commands=runner.Commands,result.StandardError}));
    }
    /// <summary>Practical bounded loops read formal bits without losing their declaration identity.</summary>
    [TestCase(8,0,8)]
    [TestCase(128,63,3)]
    [TestCase(128,64,8)]
    [TestCase(128,127,1)]
    public Task BoundedParityReadsFormalBits(int width,int start,int count)=>RtlIndexedConformanceTests.Qualify("bounded-parity",$"module dut(input logic[{width-1}:0]a,output logic q);function automatic logic f(input logic[{width-1}:0]x);logic n;logic[31:0]i;begin n=1'b0;for(i={start};i<{start+count};i++)n=n^x[i];return n;end endfunction always_comb q=f(a);endmodule",[new("a",width,"input"),new("q",1,"output")],Vectors(309+start,width,"a",false),sdk:true,selectedProbe:false);
    static string Root(){for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d is not null;d=d.Parent)if(File.Exists(Path.Combine(d.FullName,"native/uhdm/rtl.cpp")))return d.FullName;throw new IOException("Checkout missing");}
    static void RejectIr(int id){
        // Clockless IR may only contain an actually empty sequential body, even
        // when a disguised process/repeat would perform no writes at runtime.
        var node=JsonNode.Parse("""{"format":"velosim.rtl.v1","clock":null,"signals":[{"name":"a","width":1,"direction":"input"},{"name":"q","width":1,"direction":"output"}],"memories":[],"continuous":[{"target":{"kind":"ref","name":"q"},"value":{"kind":"ref","name":"a"}}],"body":{"kind":"block","items":[]}}""")!;
        int variant=(id-81)%5;
        node["body"]=JsonNode.Parse(variant switch{0=>"""{"kind":"block","items":[{"kind":"block","items":[]}]}""",1=>"""{"kind":"repeat","count":0,"body":{"kind":"block","items":[]}}""",2=>"""{"kind":"parallel","items":[]}""",3=>"""{"kind":"if","condition":{"kind":"ref","name":"a"},"then":{"kind":"block","items":[]},"else":{"kind":"block","items":[]}}""",_=>"""{"kind":"process","events":[{"signal":"a","edge":"posedge"}],"body":{"kind":"block","items":[]}}"""});
        Assert.That(Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(node.ToJsonString()))!.Message,Does.Contain("Clockless designs require an empty sequential body"));
    }
    static async Task RejectHdl(int id){
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires actual frontend");
        string body=((id-91)%5) switch{
            0=>"logic[31:0]i;begin for(i=0;i<3;i=i)f=x;end",
            1=>"logic[31:0]i;begin for(i=0;i<x;i=i+1)f=x;end",
            2=>"logic[31:0]i;begin for(i=0;i<3;i=i+1)begin i=2;f=x;end end",
            3=>"begin repeat(x)f=x;end",
            _=>"begin repeat(257)f=x;end"};
        if(id>=96)body=id switch{96=>"localparam logic[7:0]P=8'bx;begin return x+P;end",97=>"return;",98=>"logic[31:0]i,limit;begin limit=3;for(i=0;i<limit;i++)begin limit=0;f=x;end end",99=>"logic[1:0]i;begin for(i=0;i<4;i++)f=x;end",_=>"begin repeat(256)repeat(256)f=~x;end"};
        string source=$"module dut(input logic[7:0]a,output logic[7:0]q);function automatic logic[7:0]f(input logic[7:0]x);{body} endfunction always_comb q=f(a);endmodule";
        string path=Path.Combine(Environment.GetEnvironmentVariable("VELOSIM_RTL_EVIDENCE_ROOT")??Path.Combine(Root(),"artifacts"),$"functions100-reject-{id}-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);string file=Path.Combine(path,"design.sv");File.WriteAllText(file,source);
        var runner=new RecordingProcessRunner(new ProcessRunner());var elaboration=await new SurelogFrontend(runner).ElaborateAsync([file],Path.Combine(path,"surelog"),"dut",workers:4);
        var result=await runner.RunAsync(Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL")??Path.Combine(Root(),"artifacts/with-uhdm/bin/velosim-uhdm-rtl"),[elaboration.UhdmPath,Path.Combine(path,"design.json"),"dut"],path,TimeSpan.FromMinutes(3));
        string diagnostic=((id-91)%5) switch{0=>"progress",1=>"counter and constants",2=>"mutate its counter",_=>"known count0..256"};
        if(id>=96)diagnostic=id switch{96=>"captured parameter must have a known value",97=>"function return requires assigned result or expression",98=>"counter and constants",99=>"progress",_=>"statement budget"};
        Assert.That(result.ExitCode,Is.EqualTo(1),result.StandardOutput+result.StandardError);Assert.That(result.StandardError,Does.Contain(diagnostic));
        File.WriteAllText(Path.Combine(path,"report.json"),JsonSerializer.Serialize(new{status="pass",id,source=ArtifactMetadata.Fingerprint(file),commands=runner.Commands,result.StandardError}));
    }
}