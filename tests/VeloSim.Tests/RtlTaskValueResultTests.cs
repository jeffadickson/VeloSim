using System.Numerics;
using NUnit.Framework;

namespace VeloSim.Tests;

/// <summary>Value-result task arguments sample inputs before simultaneous copy-out.</summary>
[NonParallelizable]
public class RtlTaskValueResultTests
{
    [TestCase("logic[3:0] x=18",13),TestCase("logic signed[3:0] x=-1",4),TestCase("logic[3:0] x=-1",52)]
    public Task ConstantDefaultsUseFormalWidthAndSignedness(string formal,int expectedWidth)
    {
        string source="module dut(input logic[7:0] a,output logic[31:0] q);function automatic integer f(input "+formal+",input integer y=7);begin f=x*3+y;end endfunction logic[f()-1:0] w;assign w=a;assign q=w;endmodule";
        int[] inputs=[0,1,37,255,128,0];
        int mask=expectedWidth>=8?255:(1<<expectedWidth)-1;
        return RtlIndexedConformanceTests.Qualify("constant-default-formal-conversion-"+expectedWidth,source,
            [new("a",8,"input"),new("q",32,"output"),new("w",expectedWidth,"internal")],
            inputs.Select(n=>new Dictionary<string,string>{{"a",Convert.ToString(n,2).PadLeft(8,'0')}}).ToList(),
            sdk:true,selectedProbe:false,
            expectedOutputs:inputs.Select(n=>new Dictionary<string,string>{{"q",Convert.ToString(n&mask,2).PadLeft(32,'0')}}).ToList());
    }

    [TestCase(0,14),TestCase(1,13),TestCase(2,16),TestCase(3,14),TestCase(4,11),TestCase(5,14)]
    public Task ConstantFunctionDefaultsDetermineTheActualPackedWidth(int shape,int expectedWidth)
    {
        // Assignment-to-function syntax exercises the upstream constant evaluator;
        // return-statement-only fixtures can reject before reaching this path.
        string arguments=shape switch {0 or 5=>",5",1=>"2,",2=>"",3=>".y(5)",_=>"2,.y(5)"};
        string defaultX=shape==5?"BASE+1":"3";
        string source="module dut(input logic[7:0] a,output logic[31:0] q);localparam integer BASE=2;function automatic integer f(input integer x="+defaultX+",input integer y=7);begin f=x*3+y;end endfunction localparam integer WIDTH=f("+arguments+");logic[WIDTH-1:0] w;assign w=a;assign q=w;endmodule";
        int[] inputs=[0,1,37,255,128,0];
        return RtlIndexedConformanceTests.Qualify("constant-default-packed-width-"+shape,source,
            [new("a",8,"input"),new("q",32,"output"),new("w",expectedWidth,"internal")],
            inputs.Select(n=>new Dictionary<string,string>{{"a",Convert.ToString(n,2).PadLeft(8,'0')}}).ToList(),
            sdk:true,selectedProbe:false,useVerilator:shape is 3 or 4,
            expectedOutputs:inputs.Select(n=>new Dictionary<string,string>{{"q",Convert.ToString(n,2).PadLeft(32,'0')}}).ToList());
    }

    [TestCase(false),TestCase(true)]
    public Task MultipleLeadingDefaultsSurviveCommentsAndLineBreaks(bool task)
    {
        string sub=task?"task automatic run(output logic[7:0] result,input logic[7:0] x=3,input logic[7:0] y=7,input logic[7:0] z=11);result=x+2*y+3*z;endtask ":"function automatic logic[7:0] run(input logic[7:0] x=3,input logic[7:0] y=7,input logic[7:0] z=11);return x+2*y+3*z;endfunction ";
        string args="\n/* first omitted */ ,\n/* second omitted */ ,a\n";
        string body=task?"always_comb run(q,"+args+");":"always_comb q=run("+args+");";
        string source="module dut(input logic[7:0] a,output logic[7:0] q);"+sub+body+"endmodule";
        string Bits(int n)=>Convert.ToString(n&255,2).PadLeft(8,'0');
        int[] inputs=[0,1,37,255,128,0];
        return RtlIndexedConformanceTests.Qualify("multiple-leading-input-defaults-"+task,source,[new("a",8,"input"),new("q",8,"output")],
            inputs.Select(n=>new Dictionary<string,string>{{"a",Bits(n)}}).ToList(),sdk:true,selectedProbe:false,
            expectedOutputs:inputs.Select(n=>new Dictionary<string,string>{{"q",Bits(17+3*n)}}).ToList());
    }

    [TestCase("unknown"),TestCase("duplicate"),TestCase("missing"),TestCase("output_hole"),TestCase("constant_missing"),TestCase("constant_call_default"),Category("RealFifo")]
    public async Task InvalidSubroutineArgumentsFailClosed(string mode)
    {
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires pinned frontend.");
        string declaration=mode=="output_hole"?"task automatic run(output logic[7:0] result,input logic[7:0] x=3);result=x;endtask ":"function automatic logic[7:0] run(input logic[7:0] x=3,input logic[7:0] y"+(mode=="missing"?"":"=7")+");return x+y;endfunction ";
        string call=mode switch {"unknown"=>"q=run(.typo(a));","duplicate"=>"q=run(.x(a),.x(5));","missing"=>"q=run();",_=>"run(,a);"};
        string source="module dut(input logic[7:0] a,output logic[7:0] q);"+declaration+"always_comb "+call+"endmodule";
        if(mode.StartsWith("constant_",StringComparison.Ordinal))
            source="module dut(output logic[7:0] q);function automatic integer helper();begin helper=3;end endfunction function automatic integer f(input integer x"+(mode=="constant_missing"?"":"=helper()")+");begin f=x;end endfunction localparam integer WIDTH=f();logic[WIDTH-1:0] w;assign w=0;assign q=w;endmodule";
        var root=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"native/rtl/runtime.h")))root=root.Parent;
        if(root is null)throw new DirectoryNotFoundException("Checkout required.");
        string parent=Path.Combine(root.FullName,"artifacts");if(!Directory.Exists(parent))Directory.CreateDirectory(parent);
        string path=Path.Combine(parent,"invalid-subroutine-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        string file=Path.Combine(path,"design.sv"),ir=Path.Combine(path,"design.json");await File.WriteAllTextAsync(file,source);
        var runner=new ProcessRunner();Elaboration frontend;
        try{frontend=await new SurelogFrontend(runner).ElaborateAsync([file],Path.Combine(path,"surelog"),"dut",workers:2);}
        catch(InvalidOperationException error)when(error.Message.Contains("argument",StringComparison.OrdinalIgnoreCase))
        {
            await File.WriteAllTextAsync(Path.Combine(path,"upstream-rejection.txt"),error.Message);
            // Surelog may serialize a database even after an ERROR. Such output
            // must not carry successful constant-argument provenance.
            if(mode.StartsWith("constant_",StringComparison.Ordinal))
                foreach(string database in Directory.GetFiles(Path.Combine(path,"surelog"),"surelog.uhdm",SearchOption.AllDirectories))
                {
                    var rejected=await runner.RunAsync(Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL")??throw new IOException("Exporter required."),[database,ir,"dut"],path,TimeSpan.FromMinutes(2));
                    await File.WriteAllTextAsync(Path.Combine(path,"failed-database-rejection.txt"),rejected.StandardError);
                    Assert.That(rejected.ExitCode,Is.Not.Zero);
                    Assert.That(rejected.StandardError,Does.Contain("constant-safe argument provenance"));
                }
            return;
        }
        var result=await runner.RunAsync(Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL")??throw new IOException("Exporter required."),[frontend.UhdmPath,ir,"dut"],path,TimeSpan.FromMinutes(2));
        await File.WriteAllTextAsync(Path.Combine(path,"diagnostic.txt"),result.StandardError);
        Assert.That(result.ExitCode,Is.Not.Zero);
        Assert.That(mode.StartsWith("constant_",StringComparison.Ordinal),Is.False,"Unsupported constant defaults must be rejected by the frontend before width elaboration.");
        Assert.That(result.StandardError,Does.Contain(mode is "unknown" or "duplicate"?"invalid named or positional":mode=="missing"?"requires an explicit default":"missing task input"));
    }

    [TestCase(false,0),TestCase(false,1),TestCase(false,2),TestCase(true,0),TestCase(true,1),TestCase(true,2)]
    public Task NamedInputDefaultsKeepOmittedSlots(bool task,int shape)
    {
        string sub=task?"task automatic run(output logic[7:0] result,input logic[7:0] x=3,input logic[7:0] y=7);result=x*3+y;endtask ":"function automatic logic[7:0] run(input logic[7:0] x=3,input logic[7:0] y=7);return x*3+y;endfunction ";
        string args=shape==0?".y(5)":shape==1?".x(a)":".x()";
        string body=task?"always_comb run(.result(q),"+args+");":"always_comb q=run("+args+");";
        string source="module dut(input logic[7:0] a,output logic[7:0] q);"+sub+body+"endmodule";
        string Bits(int n)=>Convert.ToString(n&255,2).PadLeft(8,'0');
        int[] inputs=[0,1,37,255,128,0];
        return RtlIndexedConformanceTests.Qualify("named-input-defaults-"+task+"-"+shape,source,[new("a",8,"input"),new("q",8,"output")],
            inputs.Select(n=>new Dictionary<string,string>{{"a",Bits(n)}}).ToList(),sdk:true,selectedProbe:false,
            expectedOutputs:inputs.Select(n=>new Dictionary<string,string>{{"q",Bits(shape==0?14:shape==1?n*3+7:16)}}).ToList(),useVerilator:true);
    }

    [TestCase(false),TestCase(true)]
    public Task LeadingPositionalArgumentsCanPrecedeNamedArguments(bool task)
    {
        string sub=task?"task automatic run(output logic[7:0] result,input logic[7:0] x=3,input logic[7:0] y=7);result=x*3+y;endtask ":"function automatic logic[7:0] run(input logic[7:0] x=3,input logic[7:0] y=7);return x*3+y;endfunction ";
        string source="module dut(input logic[7:0] a,output logic[7:0] q);"+sub+(task?"always_comb run(q,a,.y(5));":"always_comb q=run(a,.y(5));")+"endmodule";
        string Bits(int n)=>Convert.ToString(n&255,2).PadLeft(8,'0');
        int[] inputs=[0,1,37,255,128,0];
        return RtlIndexedConformanceTests.Qualify("mixed-subroutine-arguments-"+task,source,[new("a",8,"input"),new("q",8,"output")],
            inputs.Select(n=>new Dictionary<string,string>{{"a",Bits(n)}}).ToList(),sdk:true,selectedProbe:false,
            expectedOutputs:inputs.Select(n=>new Dictionary<string,string>{{"q",Bits(n*3+5)}}).ToList(),useVerilator:true);
    }

    [TestCase(false,0),TestCase(false,1),TestCase(false,2),TestCase(true,0),TestCase(true,1),TestCase(true,2)]
    public Task InputDefaultHolesKeepTheirArgumentPositions(bool task,int shape)
    {
        string sub=task?"task automatic run(output logic[7:0] result,input logic[7:0] x=3,input logic[7:0] y=7);result=x*3+y;endtask ":"function automatic logic[7:0] run(input logic[7:0] x=3,input logic[7:0] y=7);return x*3+y;endfunction ";
        string args=shape==0?",5":shape==1?"a,":",";
        string body=task?"always_comb run(q,"+args+");":"always_comb q=run("+args+");";
        string source="module dut(input logic[7:0] a,output logic[7:0] q);"+sub+body+"endmodule";
        string Bits(int n)=>Convert.ToString(n&255,2).PadLeft(8,'0');
        int[] inputs=[0,1,37,255,128,0];
        return RtlIndexedConformanceTests.Qualify("subroutine-default-holes-"+task+"-"+shape,source,[new("a",8,"input"),new("q",8,"output")],
            inputs.Select(n=>new Dictionary<string,string>{{"a",Bits(n)}}).ToList(),sdk:true,selectedProbe:false,
            expectedOutputs:inputs.Select(n=>new Dictionary<string,string>{{"q",Bits(shape==0?14:shape==1?n*3+7:16)}}).ToList());
    }

    [TestCase(false),TestCase(true)]
    public Task DefaultFunctionCallsAreEvaluatedOnlyWhenUsed(bool supplied)
    {
        string source="module dut(input logic[7:0] a,output logic[7:0] q);function automatic logic[7:0] inc(input logic[7:0] x);return x+1;endfunction function automatic logic[7:0] outer(input logic[7:0] x=inc(3));return x+1;endfunction always_comb q=outer("+(supplied?"a":"")+");endmodule";
        string Bits(int n)=>Convert.ToString(n&255,2).PadLeft(8,'0');
        int[] inputs=[0,1,254,255,37,0];
        return RtlIndexedConformanceTests.Qualify("default-function-call-"+supplied,source,[new("a",8,"input"),new("q",8,"output")],
            inputs.Select(n=>new Dictionary<string,string>{{"a",Bits(n)}}).ToList(),sdk:true,selectedProbe:false,
            expectedOutputs:inputs.Select(n=>new Dictionary<string,string>{{"q",Bits(supplied?n+1:5)}}).ToList());
    }

    [Test]
    public Task NestedTaskUsesTrailingInputDefaults()
    {
        const string source="module dut(input logic[7:0] a,output logic[7:0] q);task automatic inner(output logic[7:0] result,input logic[7:0] x,input logic[7:0] y=7);result=x*3+y;endtask task automatic outer(input logic[7:0] x,output logic[7:0] result);inner(result,x);endtask always_comb outer(a,q);endmodule";
        string Bits(int n)=>Convert.ToString(n&255,2).PadLeft(8,'0');
        int[] inputs=[0,1,37,255,128,0];
        return RtlIndexedConformanceTests.Qualify("nested-task-default",source,[new("a",8,"input"),new("q",8,"output")],
            inputs.Select(n=>new Dictionary<string,string>{{"a",Bits(n)}}).ToList(),sdk:true,selectedProbe:false,
            expectedOutputs:inputs.Select(n=>new Dictionary<string,string>{{"q",Bits(n*3+7)}}).ToList());
    }

    [TestCase(false),TestCase(true)]
    public Task NamedArgumentsAreNormalizedByTheFrontend(bool task)
    {
        string sub=task?"task automatic run(output logic[7:0] result,input logic[7:0] x,input logic[7:0] y);result=x*3+y;endtask ":"function automatic logic[7:0] run(input logic[7:0] x,input logic[7:0] y);return x*3+y;endfunction ";
        string body=task?"always_comb run(.y(5),.result(q),.x(a));":"always_comb q=run(.y(5),.x(a));";
        string source="module dut(input logic[7:0] a,output logic[7:0] q);"+sub+body+"endmodule";
        string Bits(int n)=>Convert.ToString(n&255,2).PadLeft(8,'0');
        int[] inputs=[0,1,37,255,128,0];
        return RtlIndexedConformanceTests.Qualify("named-subroutine-arguments-"+task,source,[new("a",8,"input"),new("q",8,"output")],
            inputs.Select(n=>new Dictionary<string,string>{{"a",Bits(n)}}).ToList(),sdk:true,selectedProbe:false,
            expectedOutputs:inputs.Select(n=>new Dictionary<string,string>{{"q",Bits(n*3+5)}}).ToList(),useVerilator:true);
    }

    [TestCase(false,0),TestCase(false,1),TestCase(false,2),TestCase(true,0),TestCase(true,1),TestCase(true,2)]
    public Task TrailingInputDefaultsPreserveProvidedArguments(bool task,int supplied)
    {
        string sub=task?"task automatic run(output logic[7:0] result,input logic[7:0] x=3,input logic[7:0] y=7);result=x*3+y;endtask ":"function automatic logic[7:0] run(input logic[7:0] x=3,input logic[7:0] y=7);return x*3+y;endfunction ";
        string args=supplied==0?"":supplied==1?"a":"a,5";
        string body=task?"always_comb run(q"+(args.Length>0?","+args:"")+");":"always_comb q=run("+args+");";
        string source="module dut(input logic[7:0] a,output logic[7:0] q);"+sub+body+"endmodule";
        string Bits(int n)=>Convert.ToString(n&255,2).PadLeft(8,'0');
        int[] inputs=[0,1,37,255,128,0];
        return RtlIndexedConformanceTests.Qualify("subroutine-trailing-defaults-"+task+"-"+supplied,source,
            [new("a",8,"input"),new("q",8,"output")],inputs.Select(n=>new Dictionary<string,string>{{"a",Bits(n)}}).ToList(),sdk:true,selectedProbe:false,
            expectedOutputs:inputs.Select(n=>new Dictionary<string,string>{{"q",Bits((supplied==0?3:n)*3+(supplied<2?7:5))}}).ToList());
    }

    [Test]
    public Task DefaultArgumentsReadTheirDeclarationScopeOnEachCall()
    {
        // Explicit changing actual guarantees another invocation. Task-hidden
        // reads are not a portable always_comb sensitivity stimulus. The caller's
        // shadow remains deliberately different from the module's default value.
        const string source="module dut(input logic[7:0] a,output logic[7:0] q);wire[7:0] fallback;assign fallback=a;function automatic logic[7:0] value(input logic[7:0] x=fallback);return x+1;endfunction task automatic outer(input logic[7:0] fallback,output logic[7:0] y);y=value();endtask always_comb outer(a^8'd200,q);endmodule";
        string Bits(int n)=>Convert.ToString(n&255,2).PadLeft(8,'0');
        int[] inputs=[0,9,255,37,0];
        return RtlIndexedConformanceTests.Qualify("default-declaration-scope",source,[new("a",8,"input"),new("fallback",8,"internal"),new("q",8,"output")],
            inputs.Select(n=>new Dictionary<string,string>{{"a",Bits(n)}}).ToList(),sdk:true,selectedProbe:false,
            expectedOutputs:inputs.Select(n=>new Dictionary<string,string>{{"q",Bits(n+1)}}).ToList());
    }

    [Test]
    public Task LocalInitializerMayCallAnotherAutomaticFunction()
    {
        const string source="module dut(input logic[7:0] a,output logic[7:0] q);function automatic logic[7:0] inc(input logic[7:0] x);return x+1;endfunction function automatic logic[7:0] outer(input logic[7:0] x);logic[7:0] local_value=inc(x);return local_value+1;endfunction always_comb q=outer(a);endmodule";
        string Bits(int n)=>Convert.ToString(n&255,2).PadLeft(8,'0');
        int[] inputs=[0,1,254,255,37,0];
        return RtlIndexedConformanceTests.Qualify("initializer-nested-function",source,[new("a",8,"input"),new("q",8,"output")],
            inputs.Select(n=>new Dictionary<string,string>{{"a",Bits(n)}}).ToList(),sdk:true,selectedProbe:false,
            expectedOutputs:inputs.Select(n=>new Dictionary<string,string>{{"q",Bits(n+2)}}).ToList());
    }

    [Test]
    public Task TaskReturnSkipsTheRemainingContinuation()
    {
        const string source="module dut(input logic[7:0] a,output logic[7:0] q);task automatic run(input logic[7:0] x,output logic[7:0] y);begin if(x==0)begin y=7;return;end y=x+1;end endtask always_comb run(a,q);endmodule";
        string Bits(int n)=>Convert.ToString(n&255,2).PadLeft(8,'0');
        int[] inputs=[0,1,255,37,0];
        return RtlIndexedConformanceTests.Qualify("task-early-return",source,[new("a",8,"input"),new("q",8,"output")],
            inputs.Select(n=>new Dictionary<string,string>{{"a",Bits(n)}}).ToList(),sdk:true,selectedProbe:false,
            expectedOutputs:inputs.Select(n=>new Dictionary<string,string>{{"q",Bits(n==0?7:n+1)}}).ToList(),useVerilator:true);
    }

    [TestCase(false),TestCase(true)]
    public Task AutomaticLocalInitializersRunOnEveryCall(bool task)
    {
        string sub=task?"task automatic run(input logic[7:0] x,output logic[7:0] y);logic[7:0] temp=x+1;begin y=temp;end endtask":"function automatic logic[7:0] run(input logic[7:0] x);logic[7:0] temp=x+1;begin return temp;end endfunction";
        string source="module dut(input logic[7:0] a,output logic[7:0] q);"+sub+" "+(task?"always_comb run(a,q);":"always_comb q=run(a);")+"endmodule";
        string Bits(int n)=>Convert.ToString(n&255,2).PadLeft(8,'0');
        int[] inputs=[0,255,37,1,128,0];
        return RtlIndexedConformanceTests.Qualify("automatic-local-initializer",source,
            [new("a",8,"input"),new("q",8,"output")],inputs.Select(n=>new Dictionary<string,string>{{"a",Bits(n)}}).ToList(),
            sdk:true,selectedProbe:false,expectedOutputs:inputs.Select(n=>new Dictionary<string,string>{{"q",Bits(n+1)}}).ToList());
    }

    [TestCase(8,false), TestCase(128,false), TestCase(8,true), TestCase(128,true)]
    public Task InoutCopiesInBeforeCombinedOutputsCommit(int width,bool nested)
    {
        string source=$"module dut(input logic[{width-1}:0]a,output logic[{width-1}:0]q,r);task automatic transform(input logic[{width-1}:0]original,inout logic[{width-1}:0]value,output logic[{width-1}:0]other);begin value=value+1;other=~original;end endtask always_comb begin q=a;transform(q,q,r);end endmodule";
        if(nested)source=source.Replace("always_comb begin q=a;transform(q,q,r);end",$"task automatic outer(input logic[{width-1}:0]x,output logic[{width-1}:0]y,z);begin y=x;transform(y,y,z);end endtask always_comb outer(a,q,r);",StringComparison.Ordinal);
        string Bits(BigInteger value) => string.Concat(Enumerable.Range(0,width).Reverse().Select(bit => ((value>>bit)&1)==0?'0':'1'));
        var mask=(BigInteger.One<<width)-1;
        BigInteger[] numbers=[0,1,mask,BigInteger.One<<(width-1),(BigInteger.One<<Math.Min(width-1,64))+3];
        var vectors=numbers.Select(n=>new Dictionary<string,string>{{"a",Bits(n)}}).ToList();
        var expected=numbers.Select(n=>new Dictionary<string,string>{{"q",Bits((n+1)&mask)},{"r",Bits(n^mask)}}).ToList();
        return RtlIndexedConformanceTests.Qualify("task-value-result-"+width,source,
            [new("a",width,"input"),new("q",width,"output"),new("r",width,"output")],vectors,
            sdk:true,selectedProbe:false,expectedOutputs:expected);
    }

    [Test,Category("RealFifo")]
    public async Task NestedTaskCannotInvalidateAStaticallyProvenLoopCounter()
    {
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires pinned frontend.");
        var root=new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"native/rtl/runtime.h")))root=root.Parent;
        if(root is null)throw new DirectoryNotFoundException("Checkout required.");
        string parent=Path.Combine(root.FullName,"artifacts");if(!Directory.Exists(parent))Directory.CreateDirectory(parent);
        string path=Path.Combine(parent,"task-counter-reject-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
        string file=Path.Combine(path,"design.sv"),ir=Path.Combine(path,"design.json");
        await File.WriteAllTextAsync(file,"module dut(output logic[7:0] q);task automatic bump(inout int v);v=v+1;endtask task automatic run(output logic[7:0] result);int i;begin result=0;for(i=0;i<2;i=i+1)bump(i);end endtask always_comb run(q);endmodule");
        var runner=new ProcessRunner();var frontend=await new SurelogFrontend(runner).ElaborateAsync([file],Path.Combine(path,"surelog"),"dut",workers:2);
        var result=await runner.RunAsync(Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL")??throw new IOException("Exporter required."),[frontend.UhdmPath,ir,"dut"],path,TimeSpan.FromMinutes(2));
        await File.WriteAllTextAsync(Path.Combine(path,"diagnostic.txt"),result.StandardError);
        Assert.That(result.ExitCode,Is.Not.Zero);
        Assert.That(result.StandardError,Does.Contain("nested task cannot mutate a proven loop counter"));
    }
}
