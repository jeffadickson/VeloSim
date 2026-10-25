using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace VeloSim.Tests;

/// <summary>Stable numbered qualification for bounded combinational processes and pure automatic calls.</summary>
[NonParallelizable]
public class RtlCombinational200Tests
{
    static readonly int[] Widths=[1,2,4,7,8,16,31,32,33,63,64,65,80,96,127,128,3,9,17,48];
    public static IEnumerable<TestCaseData> Cases(){for(int id=1;id<=200;id++)yield return new TestCaseData(id).SetName($"CombinationalIteration{id:000}").SetCategory($"Comb200_{id:000}");}
    /// <summary>Repetition must see each preceding blocking value in the same combinational invocation.</summary>
    [Test,Category("RealFifo")]
    public async Task CombinationalRepeatSettlesBeforeEdge(){
        const string source="module dut(input logic clk,input logic[7:0]a,output logic[7:0]q,r);always_comb begin r=a;repeat(3)r=~r;end always @(posedge clk)q<=r;endmodule";
        var vectors=new[]{"00000000","11111111","10101010","01010101","xz01xz01","zzzzzzzz","xxxxxxxx","10000000"}.Select(bits=>new Dictionary<string,string>{["a"]=bits}).ToList();
        const string probe="probe.a=0;probe.clk=0;probe.eval();if(probe.r.bits()!=\"11111111\"||probe.q.bits()!=\"xxxxxxxx\")return 30;probe.a=255;probe.eval();if(probe.r.bits()!=\"00000000\"||probe.q.bits()!=\"xxxxxxxx\")return 31;probe.clk=1;probe.eval();if(probe.q.bits()!=\"00000000\")return 32;";
        await RtlIndexedConformanceTests.Qualify("comb-repeat",source,[new("clk",1,"input"),new("a",8,"input"),new("q",8,"output"),new("r",8,"output")],vectors,sdk:true,selectedProbe:false,sdkProbe:probe);
    }
    [TestCaseSource(nameof(Cases))]
    public async Task Qualify(int id)
    {
        if(id<=120)await Frontend(id);
        else if(id<=160)Reject(id);
        else if(id<=180)await Conversion(id);
        else await RejectFrontend(id);
        TestContext.Progress.WriteLine($"COMBINATIONAL_ITERATION {id:000} PASS seed {20261025+id}");
    }
    static async Task Frontend(int id)
    {
        int w=Widths[(id-1)%20],group=(id-1)/20;string range=$"[{w-1}:0]";
        string declaration="",logic="";bool temp=false;
        switch(group){
            case 0:
                temp=true;logic="always_comb r=q^t; always_comb t=a^b; always @(posedge clk)q<=t;";break;
            case 1:
                temp=true;logic="always_comb begin t=a;if(c)t=b;end always_comb begin r=t;case(c)1'b0:r=~t;default:r=t;endcase end always @(posedge clk)q<=r;";break;
            case 2:
                declaration=$"function automatic logic {range} f(input logic {range} x,y,input logic flag);logic {range} n;begin n=x;if(flag)n=y;f=~n;end endfunction";
                logic="always_comb r=f(b,a,c);always @(posedge clk)q<=f(a,b,c);";break;
            case 3:
                int fw=Math.Max(1,w/2);declaration=$"function automatic logic signed [{fw-1}:0] f(input logic signed [{fw-1}:0] x);begin f=x+{fw}'b1;end endfunction function automatic logic {range} g(input logic {range} x);begin g=f(~x);end endfunction";
                logic="always_comb r=g(f(b));always @(posedge clk)q<=g(f(a));";break;
            case 4:
                declaration=$"task automatic transform(input logic {range} x,y,input logic flag,output logic {range} result);logic {range} n;begin n=x;if(flag)n=y;result=~n;end endtask";
                logic="always_comb transform(a,b,c,r);always @(posedge clk)q<=r;";break;
            case 5:
                declaration=$"task automatic transform(input logic {range} x,y,output logic {range} result);begin result=x^y;end endtask";
                if(w<=64){declaration=$"logic {range} t;task automatic transform(input logic {range} x,y,output logic {range} result,other);begin result=x^y;other=~x;end endtask";logic="always_comb transform(a,b,r,t);always @(posedge clk)begin q=a;u=b;transform(q,u,q,u);end";}else logic="always_comb transform(a,b,r);always @(posedge clk)transform(b,a,q);";break;
        }
        string source=$"module dut(input logic clk,c,input logic {range} a,b,output logic {range} q,r);"+(temp?$"logic {range} t;":"")+declaration+" "+logic+" endmodule";
        if(group==5&&w<=64)source=source.Replace($"output logic {range} q,r)",$"output logic {range} q,r,u)",StringComparison.Ordinal);
        var expected=new List<RtlSignal>{new("clk",1,"input"),new("c",1,"input"),new("a",w,"input"),new("b",w,"input"),new("q",w,"output"),new("r",w,"output")};if(temp)expected.Add(new("t",w,"internal"));if(group==5&&w<=64){expected.Add(new("t",w,"internal"));expected.Add(new("u",w,"output"));}
        var random=new Random(20261025+id);string Bits(int row,int offset)=>string.Concat(Enumerable.Range(0,w).Select(bit=>row<4?"01xz"[(row+offset)%4]:"01xz"[random.Next(row<20?2:4)]));
        var vectors=Enumerable.Range(0,36).Select(row=>new Dictionary<string,string>{["a"]=Bits(row,0),["b"]=Bits(row,1),["c"]="01xz"[row%4].ToString()}).ToList();
        string zeros=new('0',w),ones=new('1',w),unknown=new('x',w);
        string output=group==0?unknown:group is 2 or 3?zeros:ones;
        string changed=group is 2 or 3?ones:zeros;
        string change=group is 2 or 3?$"probe.b.bits(\"{zeros}\");":$"probe.a.bits(\"{ones}\");";
        string qAfter=group is 2 or 3?ones:zeros;
        string probe=$"probe.a.bits(\"{zeros}\");probe.b.bits(\"{ones}\");probe.c=0;probe.clk=0;probe.eval();if(probe.q.bits()!=\"{unknown}\"||probe.r.bits()!=\"{output}\")return 20;";
        if(group==0)probe+=$"for(size_t i=0;i<probe.signals().size();i++)if(std::string(probe.signals()[i].name)==\"t\"&&text(probe.get(i))!=\"{ones}\")return 21;";
        probe+=change+$"probe.eval();if(probe.q.bits()!=\"{unknown}\"||probe.r.bits()!=\"{(group==0?unknown:changed)}\")return 22;probe.clk=1;probe.eval();if(probe.q.bits()!=\"{qAfter}\")return 23;auto saved=probe.q.bits();probe.eval();if(probe.q.bits()!=saved)return 24;";
        if(group==0)probe+=$"if(probe.r.bits()!=\"{zeros}\")return 25;";
        await RtlIndexedConformanceTests.Qualify($"comb200-{id:000}",source,expected.ToArray(),vectors,sdk:true,selectedProbe:false,sdkProbe:probe);
    }
    static object Ref(string n)=>new{kind="ref",name=n};
    static object Write(string n,object value,string kind="blocking")=>new{kind,target=Ref(n),value};
    static object Block(params object[] items)=>new{kind="block",items};
    static string Ir(object[] comb,object? body=null,object[]? continuous=null,int width=8)=>JsonSerializer.Serialize(new{
        format="velosim.rtl.v1",clock="clk",signals=new[]{new{name="clk",width=1,direction="input"},new{name="a",width,direction="input"},new{name="b",width,direction="input"},new{name="q",width,direction="output"},new{name="r",width,direction="output"}},memories=Array.Empty<object>(),continuous=continuous??[],combinational=comb,body=body??Block()});
    static void Reject(int id){
        int variant=(id-121)%10;object q=Write("q",Ref("a")),r=Write("r",Ref("b"));
        object[] comb=variant switch{
            0=>[Block(Write("r",Ref("q")),q)], // read before write
            1=>[new{kind="if",condition=Ref("a"),then=q,@else=Block()},r],
            2=>[Write("q",Ref("r")),Write("r",Ref("q"))],
            3=>[q,Write("q",Ref("b")),r],
            4=>[Write("q",Ref("a"),"nba"),r],
            5=>[Write("q",Ref("clk")),r],
            6=>[new{kind="repeat",count=0,body=q},r],
            7=>[Write("a",Ref("b")),q,r],
            8=>[new{kind="case",value=Ref("a"),items=new[]{new{match=(object)new{kind="const",bits="00000000"},body=q}}},r],
            _=>[q,r]};
        bool reverse=id is >=131 and <=140;int[] negativeWidths=[1,16,31,32,33,63,64,65,127,128];
        if(reverse)comb=comb.Reverse().ToArray();
        string json=Ir(comb,variant==9?Write("q",Ref("b"),"nba"):null,width:reverse?negativeWidths[variant]:8);
        // Distinct structural corruptions vary the exact unsupported boundary without accepting parser crashes.
        if(id>=141){var node=JsonNode.Parse(json)!;if(id%2==0)node["combinational"]![0]!["unexpected"]=true;else node["signals"]![3]!["width"]=id;json=node.ToJsonString();}
        var error=Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(json));
        if(id<141){string[] diagnostics=["before definite","every path","feedback","Conflicting","untimed blocking","Clock-dependent","every path","writable declared","every path","Conflicting"];Assert.That(error!.Message,Does.Contain(diagnostics[variant]));}
    }
    static async Task Conversion(int id){
        int w=Widths[id-161];var conversion=new{kind="convert",value=Ref("a"),width=w,signed=id%2==0,two_state=id%3==0};
        var design=RtlDesign.Parse(Ir([Write("q",conversion),Write("r",Ref("q"))]));
        // Conversion behavior is executed below against the independent HDL simulator.
        var malformed=JsonNode.Parse(design.Json)!;malformed["combinational"]![0]!["value"]!["width"]=id%2==0?0:129;
        Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(malformed.ToJsonString()));        string scalar=id%3==0?"bit":"logic";
        string source=$"module dut(input logic clk,input logic[127:0]a,b,output logic[127:0]q,r);function automatic logic signed[127:0] f(input {scalar} signed[{w-1}:0]x);{scalar}[{w-1}:0]n;begin n=x+{w}'b1;f=$signed(n);end endfunction always_comb r=f(b);always @(posedge clk)q<=f(a);endmodule";
        var random=new Random(20261025+id);string Bits(int row)=>row<4?new("01xz"[row],128):string.Concat(Enumerable.Range(0,128).Select(_=>"01xz"[random.Next(row<20?2:4)]));
        var vectors=Enumerable.Range(0,24).Select(row=>new Dictionary<string,string>{["a"]=Bits(row),["b"]=Bits((row+1)%24)}).ToList();
        await RtlIndexedConformanceTests.Qualify($"comb200-{id:000}-conversion",source,[new("clk",1,"input"),new("a",128,"input"),new("b",128,"input"),new("q",128,"output"),new("r",128,"output")],vectors,sdk:true,selectedProbe:false);
    }
    static string Root(){for(var d=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);d!=null;d=d.Parent)if(File.Exists(Path.Combine(d.FullName,"native/rtl/runtime.h")))return d.FullName;throw new IOException("Checkout missing");}
    [Test,Category("RealFifo")]
    public Task CrossScopeCallsAreRejected()=>RejectFrontend(186,2);
    [Test,Category("RealFifo")]
    public Task SubroutineLocalParameterCaptureResolvesOwnDeclaration()=>RtlIndexedConformanceTests.Qualify("localparam-shadow", "module dut(input logic clk,input logic[7:0]a,output logic[7:0]q);parameter int P=1;function automatic logic[7:0]f(input logic[7:0]x);localparam int P=9;return x+P;endfunction always @(posedge clk)q<=f(a);endmodule",[new("clk",1,"input"),new("a",8,"input"),new("q",8,"output")],[new(){["a"]="00000000"},new(){["a"]="11111000"}],sdk:true,selectedProbe:false);
    static async Task RejectFrontend(int id,int captureMode=0){
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires Surelog/UHDM");
        string[] bodies=[
            "always_comb begin q<=a;end",
            "always_comb if(c)q=a;",
            "always_comb begin q=r;r=a;end",
            "always_comb q=r;always_comb r=q;",
            "always_comb q=a;always @(posedge clk)q<=b;",
            "function automatic logic[7:0] f(input logic[7:0]x);f=a;endfunction always_comb q=f(b);",
            "function logic[7:0] f(input logic[7:0]x);f=x;endfunction always_comb q=f(b);",
            "task automatic t(input logic[7:0]x,output logic[7:0]y);#1;y=x;endtask always_comb t(a,q);",
            "task automatic t(inout logic[7:0]x);x=~x;endtask always_comb t(q);",
            "task automatic t(input logic[7:0]x,output logic[7:0]y,z);y=x;z=~x;endtask always_comb t(a,q,q);"
        ];
        if(id>=191)bodies=[
            "function automatic logic[7:0]f(input logic[7:0]x);begin f=x;f[0]=0;end endfunction always_comb q=f(a);",
            "function automatic logic[7:0]f(input logic[7:0]x);if(x)f=x;endfunction always_comb q=f(a);",
            "function automatic logic[7:0]f(input logic[7:0]x);f=f(x);endfunction always_comb q=f(a);",
            "function automatic logic[7:0]f(input logic[7:0]x);static logic[7:0]n;begin n=n+1;f=n;end endfunction always_comb q=f(a);",
            "function automatic logic[7:0]f(input logic[7:0]x);begin r=x;f=x;end endfunction always_comb q=f(a);",
            "function automatic logic[7:0]f(output logic[7:0]x);begin x=0;f=x;end endfunction always_comb q=f(a);",
            "function automatic logic[7:0]f(input logic[7:0]x);return;endfunction always_comb q=f(a);",
            "task automatic t(input logic[7:0]x,output logic[7:0]y);if(x)y=x;endtask always_comb t(a,q);",
            "function automatic logic[7:0]f(input logic[7:0]x);logic[7:0]n;f=n;endfunction always_comb q=f(a);",
            "function automatic logic[7:0]f(input logic[7:0]x);begin f=x;"+string.Concat(Enumerable.Repeat("f=f+f;",20))+"end endfunction always_comb q=f(a);"
        ];
        int variant=(id-181)%10;string text="module dut(input logic clk,c,input logic[7:0]a,b,output logic[7:0]q,r);"+" "+bodies[variant]+" "+(id<191&&variant is 2 or 3?"":"always_comb r=b;")+" always @(posedge clk)begin end endmodule";
        if(captureMode==1)text="module dut(input logic clk,c,input logic[7:0]a,b,output logic[7:0]q,r);parameter int P=1;function automatic logic[7:0]f(input logic[7:0]x);localparam int P=9;f=x+P;endfunction always_comb r=b;always @(posedge clk)q<=f(a);endmodule";
        if(captureMode==2)text="module helper;parameter int P=9;function automatic logic[7:0]f(input logic[7:0]x);f=x+P;endfunction endmodule module dut(input logic clk,c,input logic[7:0]a,b,output logic[7:0]q,r);parameter int P=1;helper u();always_comb r=b;always @(posedge clk)q<=u.f(a);endmodule";
        string root=Root(),path=Path.Combine(Environment.GetEnvironmentVariable("VELOSIM_RTL_EVIDENCE_ROOT")??Path.Combine(root,"artifacts"),$"comb200-reject-{id}-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);string source=Path.Combine(path,"design.sv");File.WriteAllText(source,text);
        var runner=new RecordingProcessRunner(new ProcessRunner());var elaboration=await new SurelogFrontend(runner).ElaborateAsync([source],Path.Combine(path,"surelog"),"dut",executable:Environment.GetEnvironmentVariable("VELOSIM_SURELOG")??"surelog",workers:4);
        var result=await runner.RunAsync(Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL")??Path.Combine(root,"artifacts/with-uhdm/bin/velosim-uhdm-rtl"),[elaboration.UhdmPath,Path.Combine(path,"design.json"),"dut"],path,TimeSpan.FromMinutes(3));
        string[] diagnostics=["untimed blocking","every path","before definite","feedback","Conflicting","captured global","only automatic","unsupported pure subroutine statement","input formals and task outputs","aliased task outputs","whole local","every path","recursive","local read before assignment","not bound to a local","input formals and task outputs","function return requires assigned result or expression","every path","local read before assignment","normalized RTL"];
        string expectedDiagnostic=captureMode switch{1=>"subroutine parameter captures",2=>"unsupported reference type 5000",_=>diagnostics[id-181]};bool rejected=result.ExitCode==1&&result.StandardError.Contains("RTL lowering rejected:",StringComparison.Ordinal);
        if(rejected)Assert.That(result.StandardError,Does.Contain(expectedDiagnostic));
        if(result.ExitCode==0){var failure=Assert.Throws<IrValidationException>(()=>RtlDesign.Parse(File.ReadAllText(Path.Combine(path,"design.json"))));Assert.That(failure!.Message,Does.Contain(expectedDiagnostic));rejected=true;}
        Assert.That(rejected,Is.True,result.StandardOutput+result.StandardError);
        File.WriteAllText(Path.Combine(path,"report.json"),JsonSerializer.Serialize(new{status="pass",id,captureMode,source=ArtifactMetadata.Fingerprint(source),commands=runner.Commands,result.StandardError,result.ExitCode}));
    }
}
