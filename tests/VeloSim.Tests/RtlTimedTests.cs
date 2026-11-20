using System.Text.Json;
using NUnit.Framework;

namespace VeloSim.Tests;

/// <summary>Explicit-time SDK execution, checked against the original HDL and an independent trace.</summary>
[NonParallelizable]
public class RtlTimedTests
{
    [TestCase("condition"),TestCase("count"),TestCase("amount"),TestCase("index"),TestCase("base"),TestCase("constant_count")]
    public void TimedHazardsIncludeControlAndAddressReads(string field)
    {
        const string read="""{"kind":"ref","name":"r"}""";
        const string write="""{"kind":"blocking","target":{"kind":"ref","name":"q"},"value":{"kind":"const","bits":"1"}}""";
        string body=field switch {
            "condition"=>"{\"kind\":\"wait\",\"condition\":"+read+",\"body\":"+write+"}",
            "count"=>"{\"kind\":\"runtime_repeat\",\"count\":"+read+",\"body\":"+write+"}",
            "constant_count"=>"{\"kind\":\"repeat\",\"count\":2,\"body\":"+write+"}",
            "amount"=>"{\"kind\":\"delay\",\"amount\":"+read+",\"scale\":1,\"body\":"+write+"}",
            _=>write.Replace("\"target\":{\"kind\":\"ref\",\"name\":\"q\"}","\"target\":{\"kind\":\""+(field=="index"?"bitselect":"indexedselect")+"\",\"value\":{\"kind\":\"ref\",\"name\":\"q\"},\""+field+"\":"+read+(field=="base"?",\"width\":1,\"descending\":false":"")+"}",StringComparison.Ordinal)
        };
        string writer=write.Replace("\"name\":\"q\"","\"name\":\"r\"",StringComparison.Ordinal);
        string json="{\"format\":\"velosim.rtl.v1\",\"signals\":[{\"name\":\"q\",\"width\":8,\"direction\":\"output\"},{\"name\":\"r\",\"width\":8,\"direction\":\"output\"}],\"memories\":[],\"continuous\":[],\"clock\":null,\"body\":{\"kind\":\"block\",\"items\":[]},\"timed\":[{\"kind\":\"always_star\",\"body\":"+body+"},"+writer+"]}";
        var design=RtlDesign.Parse(json);
        Assert.That(design.TimingDiagnostics.Any(d=>d.StartsWith("VS-RACE-RW: r:",StringComparison.Ordinal)),Is.EqualTo(field!="constant_count"));
        Assert.That(design.EmitModelHeader("dut").Contains("{1,HdlScheduler::Edge::change}",StringComparison.Ordinal),Is.EqualTo(field!="constant_count"));
    }

    [Test]
    public void ImplicitSensitivityValidatesEvenUnreachableBodies()
    {
        const string json="""{"format":"velosim.rtl.v1","signals":[{"name":"q","width":8,"direction":"output"}],"memories":[],"continuous":[],"clock":null,"body":{"kind":"block","items":[]},"timed":[{"kind":"always_star","body":{"kind":"blocking","target":{"kind":"ref","name":"q"},"value":{"kind":"const","bits":"1"}}}]}""";
        Assert.That(RtlDesign.Parse(json).EmitModelHeader("dut"),Does.Not.Contain("engine.blocking("));
        Assert.That(()=>RtlDesign.Parse(json.Replace("\"kind\":\"blocking\"","\"kind\":\"unsupported\"",StringComparison.Ordinal)),Throws.TypeOf<IrValidationException>());
        Assert.That(RtlDesign.Parse(json.Replace("\"kind\":\"const\",\"bits\":\"1\"","\"kind\":\"ref\",\"name\":\"q\"",StringComparison.Ordinal)).EmitModelHeader("dut"),Does.Contain("engine.watch(frame,"));
    }

    [TestCase(false),TestCase(true),Category("RealFifo")]
    public async Task RuntimeIntraAssignmentDelaySamplesValueAndDelay(bool nonblocking)
    {
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires pinned frontend and references.");
        string source="module dut(input logic[7:0] data,output logic[7:0] q);timeunit 1ns;timeprecision 1ns;logic[7:0] d;initial begin q=0;d=7;q"+(nonblocking?"<=":"=")+"#d data;end initial begin #1 d=2;end endmodule";
        string[] expected=Enumerable.Range(0,10).Select(t=>$"T {t} {(t<7?"00000000":"00000010")}").ToArray();
        await Qualify(source,"data","q",["q"],expected);
    }

    [TestCase(false),TestCase(true)]
    public void TimedRaceDiagnosticsIdentifyPotentialHazards(bool fork)
    {
        const string write="""{"kind":"blocking","target":{"kind":"ref","name":"q"},"value":{"kind":"const","bits":"1"}}""";
        const string read="""{"kind":"blocking","target":{"kind":"ref","name":"r"},"value":{"kind":"ref","name":"q"}}""";
        string processes=fork?"{\"kind\":\"fork\",\"join\":\"all\",\"items\":["+write+","+read+","+write+"]}":write+","+read+","+write;
        string json="{\"format\":\"velosim.rtl.v1\",\"signals\":[{\"name\":\"q\",\"width\":8,\"direction\":\"output\"},{\"name\":\"r\",\"width\":8,\"direction\":\"output\"}],\"memories\":[],\"continuous\":[],\"clock\":null,\"body\":{\"kind\":\"block\",\"items\":[]},\"timed\":["+processes+"]}";
        var diagnostics=RtlDesign.Parse(json).TimingDiagnostics;
        Assert.That(diagnostics,Has.Some.StartsWith("VS-RACE-WW: q:"));
        Assert.That(diagnostics,Has.Some.StartsWith("VS-RACE-RW: q:"));
        Assert.That(diagnostics,Has.None.StartsWith("VS-RACE-RW: r:"));
        var single=RtlDesign.Parse(json.Replace(processes,write,StringComparison.Ordinal));
        Assert.That(single.TimingDiagnostics,Is.Empty);
    }

    [Test,Category("RealFifo")]
    public async Task ImplicitSensitivityStartsOnChangesAndRearms()
    {
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires pinned native frontend and references.");
        const string source="module dut(input logic[7:0] data,output logic[7:0] q);timeunit 1ns;timeprecision 1ns;always @* q=data;endmodule";
        // Inputs are deposited before processes start, like static testbench
        // initialization. @* waits for a later change; always_comb runs once.
        string[] expected=Enumerable.Range(0,10).Select(t=>$"T {t} {(t<5?"xxxxxxxx":"00001001")}").ToArray();
        await Qualify(source,"data","q",["q"],expected);
    }

    [Test,Category("RealFifo")]
    public async Task EmptyImplicitSensitivityDoesNotBehaveLikeAlwaysComb()
    {
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires pinned native frontend and references.");
        const string source="module dut(output logic[7:0] q,seen);timeunit 1ns;timeprecision 1ns;initial #1;always @* q=7;always_comb seen=9;endmodule";
        string[] expected=Enumerable.Range(0,10).Select(t=>$"T {t} xxxxxxxx 00001001").ToArray();
        await Qualify(source,"","q,seen",["q","seen"],expected);
    }

    private static string Root()
    {
        for (var root = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); root is not null; root = root.Parent)
            if (File.Exists(Path.Combine(root.FullName, "native/rtl/hdl_scheduler.h"))) return root.FullName;
        throw new DirectoryNotFoundException("VeloSim checkout required.");
    }

    [TestCase(false), TestCase(true), Category("RealFifo")]
    public async Task InternalClockNbaAndCombinationalReactivationMatchOriginalSource(bool useLatch)
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned native frontend and Icarus.");
        const string prefix = "module dut(output logic[7:0] q,seen,zero,comb);timeunit 1ns;timeprecision 1ns;logic clk;initial begin clk=0;q=0;seen=0;zero=0;#0 zero=q;end always #2 clk=~clk;always @(posedge clk)begin q<=q+1;seen<=q;end ";
        string source = prefix + (useLatch ? "always_latch if(clk)comb=q+1;" : "always_comb comb=q+1;") + " endmodule";
        string[] expected = Enumerable.Range(0, 10).Select(t =>
        {
            int q = (t + 2) / 4, seen = Math.Max(0, q - 1), comb = useLatch && t < 2 ? -1 : q + 1;
            string Bits(int n) => n < 0 ? "xxxxxxxx" : Convert.ToString(n, 2).PadLeft(8, '0');
            return $"T {t} {Bits(q)} {Bits(seen)} 00000000 {Bits(comb)}";
        }).ToArray();
        await Qualify(source, "", "q,seen,zero,comb", ["q", "seen", "zero", "comb"], expected);
    }

    [Test, Category("RealFifo")]
    public async Task FutureHostDrivesDoNotLeakIntoOlderSuspendedReads()
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned native frontend and Icarus.");
        const string source = "module dut(input logic[7:0] data,output logic[7:0] early,late);timeunit 1ns;timeprecision 1ns;initial begin early=0;late=0;#3 early=data;#4 late=data;end endmodule";
        string[] expected = Enumerable.Range(0, 10).Select(t => $"T {t} {(t < 3 ? "00000000" : "00000010")} {(t < 7 ? "00000000" : "00001001")}").ToArray();
        await Qualify(source, "data", "early,late", ["early", "late"], expected);
    }

    [Test, Category("RealFifo")]
    public async Task IntraAssignmentDelaysSampleBeforeSuspension()
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned native frontend and Icarus.");
        const string source = "module dut(input logic[7:0] data,output logic[7:0] q,seen);timeunit 1ns;timeprecision 1ns;initial begin q=0;seen=0;q<=#3 data;seen=#7 data;end endmodule";
        string[] expected = Enumerable.Range(0,10).Select(t => $"T {t} {(t<3?"00000000":"00000010")} {(t<7?"00000000":"00000010")}").ToArray();
        await Qualify(source,"data","q,seen",["q","seen"],expected);
    }

    [Test, Category("RealFifo")]
    public async Task WaitAndNestedEventResumeAcrossMultipleInitialProcesses()
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned native frontend and Icarus.");
        const string source = "module dut(output logic[7:0] q,seen);timeunit 1ns;timeprecision 1ns;logic clk;initial clk=0;always #1 clk=~clk;initial begin q=0;seen=0;wait(clk)q=1;@(negedge clk)seen=2;end endmodule";
        string[] expected = Enumerable.Range(0,10).Select(t => $"T {t} {(t<1?"00000000":"00000001")} {(t<2?"00000000":"00000010")}").ToArray();
        await Qualify(source,"","q,seen",["q","seen"],expected);
    }

    [TestCase("all"),TestCase("any"),TestCase("none"),Category("RealFifo")]
    public async Task ForkJoinLifetimeMatchesOriginalSource(string join)
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned native frontend and Icarus.");
        string suffix=join=="all"?"join q=q+seen;":join=="any"?"join_any disable fork;q=q+1;":"join_none wait fork;q=q+seen;";
        string source="module dut(output logic[7:0] q,seen);timeunit 1ns;timeprecision 1ns;initial begin q=0;seen=0;fork #2 q=2;#4 seen=4;"+suffix+"end endmodule";
        string Bits(int n)=>Convert.ToString(n,2).PadLeft(8,'0');
        string[] expected=Enumerable.Range(0,10).Select(t=>{
            int q=t<2?0:join=="any"?3:t<4?2:6,seen=join=="any"||t<4?0:4;
            return $"T {t} {Bits(q)} {Bits(seen)}";
        }).ToArray();
        await Qualify(source,"","q,seen",["q","seen"],expected);
    }

    [Test,Category("RealFifo")]
    public async Task NamedEventsAreOccurrencesAndRearmAfterResume()
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned native frontend and Icarus.");
        const string source="module dut(output logic[7:0] q,seen);timeunit 1ns;timeprecision 1ns;event ready;initial begin q=0;seen=0;end initial begin #1 ->ready;#2 ->ready;end initial begin @(ready)q=1;@(ready)seen=2;end endmodule";
        string[] expected=Enumerable.Range(0,10).Select(t=>$"T {t} {(t<1?"00000000":"00000001")} {(t<3?"00000000":"00000010")}").ToArray();
        await Qualify(source,"","q,seen",["q","seen"],expected);
    }

    [Test,Category("RealFifo")]
    public async Task HierarchicalTimeUnitsConvertToGlobalPrecisionWithoutGuessing()
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned native frontend and Icarus.");
        const string source="module leaf(output logic[7:0] q);timeunit 10ns;timeprecision 1ns;initial begin q=0;#1 q=7;end endmodule module dut(output logic[7:0] q);timeunit 1ns;timeprecision 100ps;leaf child(.q(q));endmodule";
        string[] expected=Enumerable.Range(0,110).Select(t=>$"T {t} {(t<100?"00000000":"00000111")}").ToArray();
        await Qualify(source,"","q",["q"],expected,-10,"100ps","0.1");
    }

    [Test,Category("RealFifo")]
    public async Task DelayedMemoryUpdatesKeepTheirSampledAddress()
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned native frontend and Icarus.");
        const string source="module dut(input logic[7:0] data,output logic[7:0] early,late);timeunit 1ns;timeprecision 1ns;logic idx;logic[7:0] m[0:1];initial begin early=0;late=0;idx=0;m[0]=0;m[1]=0;m[idx]<=#3 data;idx=1;#4 begin early=m[0];late=m[1];end end endmodule";
        string[] expected=Enumerable.Range(0,10).Select(t=>$"T {t} {(t<4?"00000000":"00000010")} 00000000").ToArray();
        await Qualify(source,"data","early,late",["early","late"],expected);
    }

    [Test, Category("RealFifo")]
    public async Task RuntimeIntegerDelaySamplesItsValueAtSuspension()
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned native frontend and Icarus.");
        const string source="module dut(output logic[7:0] q);timeunit 1ns;timeprecision 1ns;logic[7:0] d;initial begin q=0;d=3;#d q=7;#d q=9;end initial begin #1 d=2;end endmodule";
        string[] expected=Enumerable.Range(0,10).Select(t=>$"T {t} {(t<3?"00000000":t<5?"00000111":"00001001")}").ToArray();
        await Qualify(source,"","q",["q"],expected);
    }

    [Test, Category("RealFifo")]
    public async Task MemoryChangesReactivateCombinationalReadsAndWaits()
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned native frontend and Icarus.");
        const string source="module dut(output logic[7:0] q,seen);timeunit 1ns;timeprecision 1ns;logic[7:0] m[0:1];initial begin m[0]=0;m[1]=0;seen=0;#2 m[1]<=7;end always_comb q=m[1];initial begin wait(m[1]==7)seen=9;end endmodule";
        string[] expected=Enumerable.Range(0,10).Select(t=>$"T {t} {(t<2?"00000000":"00000111")} {(t<2?"00000000":"00001001")}").ToArray();
        await Qualify(source,"","q,seen",["q","seen"],expected);
    }

    [Test, Category("RealFifo")]
    public async Task NonblockingNamedTriggerIsObservedByLaterActiveWaiter()
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned native frontend and Icarus.");
        const string source="module dut(output logic[7:0] q);timeunit 1ns;timeprecision 1ns;event ready;initial begin q=0;->>ready;@(ready)q=7;end endmodule";
        string[] expected=Enumerable.Range(0,10).Select(t=>$"T {t} 00000111").ToArray();
        await Qualify(source,"","q",["q"],expected,allowIcarusUnsupported:true,
            verilatorExpected:Enumerable.Range(0,10).Select(t=>$"T {t} 00000000").ToArray(),
            referenceDifferenceReason:"Pinned reference does not deliver the nonblocking event to the later waiter in this same-process fixture.");
    }

    [Test, Category("RealFifo")]
    public async Task RuntimeRepeatCountIsSampledBeforeTimedIterations()
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned native frontend and Icarus.");
        const string source="module dut(output logic[7:0] q);timeunit 1ns;timeprecision 1ns;logic[7:0] n;initial begin q=0;n=3;repeat(n)begin #1 q=q+1;end end initial begin #1 n=9;end endmodule";
        string[] expected=Enumerable.Range(0,10).Select(t=>$"T {t} {Convert.ToString(Math.Min(t,3),2).PadLeft(8,'0')}").ToArray();
        await Qualify(source,"","q",["q"],expected);
    }

    [TestCase("1.24",120), TestCase("1.25",130), TestCase("0.05",10), TestCase("0.049",0), Category("RealFifo")]
    public async Task DecimalDelayRoundsAtModulePrecisionBeforeGlobalConversion(string delay,int transition)
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned native frontend and Icarus.");
        string source="module leaf(output logic[7:0] q);timeunit 1ns;timeprecision 100ps;initial begin q=0;#"+delay+" q=7;end endmodule module dut(output logic[7:0] q);timeunit 1ns;timeprecision 10ps;leaf child(.q(q));endmodule";
        string[] expected=Enumerable.Range(0,140).Select(t=>$"T {t} {(t<transition?"00000000":"00000111")}").ToArray();
        // Pinned Verilator scales directly to the global precision in V3Timing;
        // preserve its disagreement rather than changing the module oracle.
        int referenceTransition=(int)decimal.Round(decimal.Parse(delay,System.Globalization.CultureInfo.InvariantCulture)*100,0,MidpointRounding.AwayFromZero);
        string[] referenceExpected=Enumerable.Range(0,140).Select(t=>$"T {t} {(t<referenceTransition?"00000000":"00000111")}").ToArray();
        await Qualify(source,"","q",["q"],expected,-11,"10ps","0.01",verilatorExpected:referenceExpected);
    }

    [Test,Category("RealFifo")]
    public async Task TimedForContinueRunsIncrementAndBreakSkipsIt()
    {
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires pinned frontend and references.");
        const string source="module dut(output logic[7:0] q);timeunit 1ns;timeprecision 1ns;logic[7:0] i;initial begin q=0;for(i=0;i<5;i=i+1)begin #1;if(i==1)continue;if(i==3)break;q=q+1;end end endmodule";
        string[] expected=Enumerable.Range(0,10).Select(t=>$"T {t} {(t<1?"00000000":t<3?"00000001":"00000010")}").ToArray();
        await Qualify(source,"","q",["q"],expected,allowIcarusUnsupported:true);
    }

    [Test,Category("RealFifo")]
    public async Task SimultaneousInternalClockEdgesSampleBeforeEitherNbaCommits()
    {
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires pinned frontend and references.");
        const string source="module dut(output logic[7:0] left,right,seen);timeunit 1ns;timeprecision 1ns;logic a,b;initial begin a=0;b=1;left=0;right=0;end always #2 a=~a;always #2 b=~b;always @(posedge a)left<=right+1;always @(negedge b)right<=left+1;always_comb seen=left+right;endmodule";
        string Bits(int n)=>Convert.ToString(n,2).PadLeft(8,'0');
        string[] expected=Enumerable.Range(0,10).Select(t=>{int n=(t+2)/4;return $"T {t} {Bits(n)} {Bits(n)} {Bits(2*n)}";}).ToArray();
        await Qualify(source,"","left,right,seen",["left","right","seen"],expected);
    }

    [Test,Category("RealFifo")]
    public async Task SourceAttributedRuntimeFailureRollsBackAndCanRecover()
    {
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires pinned frontend and references.");
        const string source="module dut(input logic[7:0] data,output logic[7:0] q);timeunit 1ns;timeprecision 1ns;initial begin q=1;#data q=2;end endmodule";
        const string before="try{m.eval();return 40;}catch(const std::runtime_error& error){if(std::string(error.what()).find(\"design.sv:1:\")==std::string::npos)return 41;}if(m.q.bits()!=\"xxxxxxxx\")return 42;";
        string[] expected=Enumerable.Range(0,10).Select(t=>$"T {t} {(t<2?"00000001":"00000010")}").ToArray();
        await Qualify(source,"data","q",["q"],expected,beforeLoop:before);
    }

    [Test,Category("RealFifo")]
    public async Task TimedProcessTaskSamplesInputsBefore256BitCopyOut()
    {
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires pinned frontend and references.");
        const string source="module dut(output logic[127:0] q,r);timeunit 1ns;timeprecision 1ns;task automatic transform(input logic[127:0] original,inout logic[127:0] value,output logic[127:0] other);begin value=value+1;other=~original;end endtask initial begin q=128'h80000000000000000000000000000000;r=0;#1 transform(q,q,r);end endmodule";
        string initial="1"+new string('0',127),incremented="1"+new string('0',126)+"1",complement="0"+new string('1',127);
        string[] expected=Enumerable.Range(0,10).Select(t=>$"T {t} {(t==0?initial:incremented)} {(t==0?new string('0',128):complement)}").ToArray();
        await Qualify(source,"","q,r",["q","r"],expected,outputWidth:128);
    }

    [TestCase(false),TestCase(true),Category("RealFifo")]
    public async Task DelayedConcatenationSamplesAllResultsBeforeSuspension(bool nonblocking)
    {
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires pinned frontend and references.");
        string source="module dut(input logic[7:0] data,output logic[7:0] q,r);timeunit 1ns;timeprecision 1ns;initial begin q=0;r=0;#1;{q,r}"+(nonblocking?"<=":"=")+"#6 {data,~data};end endmodule";
        string[] expected=Enumerable.Range(0,10).Select(t=>$"T {t} {(t<7?"00000000 00000000":"00000010 11111101")}").ToArray();
        string[]? referenceExpected=nonblocking?null:Enumerable.Range(0,10).Select(t=>$"T {t} {(t<7?"00000000":"00000010")} 00000000").ToArray();
        await Qualify(source,"data","q,r",["q","r"],expected,verilatorExpected:referenceExpected,
            referenceDifferenceReason:"Pinned reference loses the low destination in this delayed blocking-concatenation fixture; SDK and Icarus preserve the sampled complement.");
    }

    [Test,Category("RealFifo")]
    public async Task DelayedSelectedNbaSamplesAddressAndPreservesUnselectedUpdates()
    {
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires pinned frontend and references.");
        const string source="module dut(input logic[7:0] data,output logic[7:0] q);timeunit 1ns;timeprecision 1ns;logic[3:0] idx;initial begin q=0;idx=0;q[idx+:4]<=#7 data;#1 q[7:4]=4'ha;idx=4;end endmodule";
        string[] expected=Enumerable.Range(0,10).Select(t=>$"T {t} {(t==0?"00000000":t<7?"10100000":"10100010")}").ToArray();
        await Qualify(source,"data","q",["q"],expected);
    }

    [Test,Category("RealFifo")]
    public async Task PartialLatchWritesPreserveOtherBitsAndHoldWhenDisabled()
    {
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires pinned frontend and references.");
        const string source="module dut(input logic[7:0] data,output logic[7:0] q);timeunit 1ns;timeprecision 1ns;logic en;initial begin q=8'ha1;en=1;#4 en=0;end always_latch if(en)q[0]=data[0];endmodule";
        string[] expected=Enumerable.Range(0,10).Select(t=>$"T {t} 10100000").ToArray();
        await Qualify(source,"data","q",["q"],expected);
    }

    [Test,Category("RealFifo")]
    public async Task HostTriggeredNbaDoesNotPrecedeOtherActiveWorkAtSameTick()
    {
        if(Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO")!="1")Assert.Ignore("Requires pinned frontend and references.");
        const string source="module dut(input logic[7:0] data,output logic[7:0] q,seen);timeunit 1ns;timeprecision 1ns;initial begin q=0;seen=0;end initial begin #1;@(data)q<=data;end initial #5 seen=q;endmodule";
        string[] expected=Enumerable.Range(0,10).Select(t=>$"T {t} {(t<5?"00000000":"00001001")} 00000000").ToArray();
        await Qualify(source,"data","q,seen",["q","seen"],expected);
    }

    private static async Task Qualify(string source, string input, string outputList, string[] outputs, string[] expected,int precisionExponent=-9,string precision="1ns",string tick="1",bool allowIcarusUnsupported=false,string[]? verilatorExpected=null,string? beforeLoop=null,string referenceDifferenceReason="Pinned reference rounds directly to global precision; module-precision oracle retained.",int outputWidth=8)
    {
        string root = Root(), artifacts = Path.Combine(root, "artifacts");
        if (!Directory.Exists(artifacts)) Directory.CreateDirectory(artifacts);
        string directory = Path.Combine(artifacts, "timed-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var runner = new RecordingProcessRunner(new ProcessRunner());
        async Task<ProcessResult> Run(string tool, params string[] args)
        {
            var result = await runner.RunAsync(tool, args, directory, TimeSpan.FromMinutes(4));
            await File.WriteAllTextAsync(Path.Combine(directory,"commands.json"),JsonSerializer.Serialize(runner.Commands,new JsonSerializerOptions{WriteIndented=true}));
            await File.WriteAllTextAsync(Path.Combine(directory,$"command-{runner.Commands.Count}.json"),JsonSerializer.Serialize(new{tool,args,result.ExitCode,result.StandardOutput,result.StandardError},new JsonSerializerOptions{WriteIndented=true}));
            bool unsupported=allowIcarusUnsupported&&tool=="iverilog"&&(result.StandardError.Contains("break statements not supported",StringComparison.Ordinal)||result.StandardError.Contains("->> is not currently supported",StringComparison.Ordinal));
            Assert.That(result.ExitCode==0||unsupported, Is.True, result.StandardOutput + result.StandardError);
            return result;
        }
        string file = Path.Combine(directory, "design.sv"), ir = Path.Combine(directory, "design.json");
        await File.WriteAllTextAsync(file, source);
        var elaboration = await new SurelogFrontend(runner).ElaborateAsync([file], Path.Combine(directory, "surelog"), "dut", workers: 2);
        await Run(Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL") ?? throw new IOException("Exporter path required."), elaboration.UhdmPath, ir, "dut");
        var design = RtlDesign.Parse(await File.ReadAllTextAsync(ir));
        Assert.That(design.Timed, Is.True); Assert.That(design.TimePrecisionExponent, Is.EqualTo(precisionExponent));
        string sdk = Path.Combine(directory, "sdk"); await RtlExternalSdk.ExportAsync(ir, "dut", sdk);
        string host = "#include \"Vdut.h\"\n#include <iostream>\n#include <thread>\nint main(){Vdut m;bool rejected=false;std::thread wrong([&]{try{m.eventsPending();}catch(const std::runtime_error&){rejected=true;}});wrong.join();if(!rejected)return 31;"+beforeLoop+"for(unsigned t=0;t<"+expected.Length+";++t){if(t)m.timeInc(1);" +
            (input.Length == 0 ? "" : "m.data=(t<5?2:9);") + "m.eval();std::cout<<\"T \"<<t" +
            string.Concat(outputs.Select(o => "<<' '<<m." + design.ExternalPortNames()[o] + ".bits()")) + "<<'\\n';}m.final();}\n";
        string hostPath = Path.Combine(sdk, "host.cpp"); await File.WriteAllTextAsync(hostPath, host);
        string build = Path.Combine(directory, "sdk-build");
        await Run("cmake", "-S", sdk, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Debug", "-DVELOSIM_HOST_SOURCE=" + hostPath);
        await Run("cmake", "--build", build, "--parallel", "2");
        var actual = await Run(Path.Combine(build, "velosim_host"));
        string[] Lines(string text) => text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("T ", StringComparison.Ordinal)).ToArray();
        Assert.That(Lines(actual.StandardOutput), Is.EqualTo(expected));
        string declarations = string.Join(',', outputs), connections = string.Join(',', outputs.Select(o => "." + o + "(" + o + ")"));
        string tb = "module tb;timeunit 1ns;timeprecision "+precision+";wire["+(outputWidth-1)+":0] " + declarations + ";" +
            (input.Length == 0 ? "" : "reg[7:0] data=2;initial #5 data=9;") + "dut d(" + connections + (input.Length == 0 ? "" : ",.data(data)") + ");integer t;initial begin for(t=0;t<"+expected.Length+";t=t+1)begin $strobe(\"T %0d" + string.Concat(outputs.Select(_ => " %b")) + "\",t," + outputList + ");#"+tick+";end $finish;end endmodule";
        string testbench = Path.Combine(directory, "tb.sv"); await File.WriteAllTextAsync(testbench, tb);
        string vvp = Path.Combine(directory, "reference.vvp");var icarusCompile=await Run("iverilog", "-g2012", "-s", "tb", "-o", vvp, file, testbench);
        string? icarusOutput=null;
        if(icarusCompile.ExitCode==0){var reference=await Run("vvp",vvp);icarusOutput=reference.StandardOutput;Assert.That(Lines(icarusOutput),Is.EqualTo(expected));}
        else{await File.WriteAllTextAsync(Path.Combine(directory,"icarus-unavailable.txt"),icarusCompile.StandardError);Assert.That(Environment.GetEnvironmentVariable("VELOSIM_TIMED_VERILATOR"),Is.Not.Null,"A supported independent reference is required for this case.");}
        string? verilator = Environment.GetEnvironmentVariable("VELOSIM_TIMED_VERILATOR");
        // Verilator's two-state reference cannot qualify startup X values.
        if (verilator is not null && expected.All(line => !line.Contains('x')))
        {
            string referenceDirectory=Path.Combine(directory,"verilator");Directory.CreateDirectory(referenceDirectory);
            await File.WriteAllTextAsync(Path.Combine(referenceDirectory,"CMakeLists.txt"),VerilatorReference.GenerateCMake("tb",[file,testbench]));
            string referenceBuild=Path.Combine(referenceDirectory,"build");
            await Run("cmake","-S",referenceDirectory,"-B",referenceBuild,"-G","Ninja","-DCMAKE_BUILD_TYPE=Release","-DVERILATOR_BIN="+verilator);
            await Run("cmake","--build",referenceBuild,"--parallel","2");
            var modern=await Run(Path.Combine(referenceBuild,"reference"));
            Assert.That(Lines(modern.StandardOutput),Is.EqualTo(verilatorExpected??expected));
            if(verilatorExpected is not null)await File.WriteAllTextAsync(Path.Combine(directory,"verilator-disagreement.json"),JsonSerializer.Serialize(new{expected,referenceExpected=verilatorExpected,reason=referenceDifferenceReason},new JsonSerializerOptions{WriteIndented=true}));
            await File.WriteAllTextAsync(Path.Combine(directory,"verilator.trace"),modern.StandardOutput);
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "sdk.trace"), actual.StandardOutput);
        if(icarusOutput is not null)await File.WriteAllTextAsync(Path.Combine(directory, "icarus.trace"), icarusOutput);
        await File.WriteAllTextAsync(Path.Combine(directory, "sources.json"), JsonSerializer.Serialize(new { design = ArtifactMetadata.Fingerprint(file), testbench = ArtifactMetadata.Fingerprint(testbench), ir = ArtifactMetadata.Fingerprint(ir), expected,verilatorExpected }, new JsonSerializerOptions { WriteIndented = true }));
    }

    [Test]
    public void RepeatedLargeOriginsCannotExpandIntoUnboundedGeneratedCode()
    {
        // A legal diagnostic label is data, not a file to open. Nested repeats
        // amplify this tiny IR well before the instruction-count ceiling.
        string file=JsonSerializer.Serialize(new string('a',4096));
        string json="{\"format\":\"velosim.rtl.v1\",\"signals\":[{\"name\":\"q\",\"width\":8,\"direction\":\"output\"}],\"memories\":[],\"continuous\":[],\"clock\":null,\"body\":{\"kind\":\"block\",\"items\":[]},\"time_precision\":-9,\"timed\":[{\"kind\":\"repeat\",\"count\":256,\"body\":{\"kind\":\"repeat\",\"count\":256,\"body\":{\"kind\":\"blocking\",\"source\":{\"file\":"+file+",\"line\":1},\"target\":{\"kind\":\"ref\",\"name\":\"q\"},\"value\":{\"kind\":\"const\",\"bits\":\"1\"}}}}]}";
        var design=RtlDesign.Parse(json);
        Assert.That(()=>design.EmitModelHeader("dut"),Throws.TypeOf<IrValidationException>().With.Message.Contains("generated-code budget"));
    }

    [Test]
    [TestCase("0.145",-9,-11,-12,"150")]
    [TestCase("0.145",-9,-11,-15,"150000")]
    [TestCase("0.049",-9,-10,-11,"0")]
    [TestCase("0.05",-9,-10,-11,"10")]
    [TestCase("0",0,0,0,"0")]
    [TestCase("1.000000000000000000000000000",0,0,0,"1")]
    [TestCase("1e2",-9,-9,-10,"1000")]
    [TestCase("18446744073709551615",0,0,0,"18446744073709551615")]
    public void LiteralDelayQuantization(string literal,int unit,int precision,int global,string ticks)
    {
        string json=$$$"""
        {"format":"velosim.rtl.v1","signals":[{"name":"q","width":8,"direction":"output"}],"memories":[],"continuous":[],"clock":null,"body":{"kind":"block","items":[]},"time_precision":{{{global}}},"timed":[{"kind":"delay","literal":"{{{literal}}}","unit":{{{unit}}},"precision":{{{precision}}},"body":{"kind":"block","items":[]}}]}
        """;
        Assert.That(RtlDesign.Parse(json).EmitModelHeader("dut"),Does.Contain("Step::delay("+ticks+"ULL)"));
    }

    [Test]
    public void MalformedTimedMetadataFailsClosed()
    {
        const string json = """
        {"format":"velosim.rtl.v1","signals":[{"name":"q","width":8,"direction":"output"}],"memories":[],"continuous":[],"clock":null,"body":{"kind":"block","items":[]},"time_precision":-9,"timed":[{"kind":"delay","ticks":1,"body":{"kind":"blocking","target":{"kind":"ref","name":"q"},"value":{"kind":"const","bits":"1"}}}]}
        """;
        Assert.That(RtlDesign.Parse(json).EmitModelHeader("dut"), Does.Contain("HdlScheduler::Step::delay(1ULL)"));
        Assert.That(() => RtlDesign.Parse(json.Replace("\"ticks\":1", "\"ticks\":-1", StringComparison.Ordinal)), Throws.TypeOf<IrValidationException>());
        Assert.That(() => RtlDesign.Parse(json.Replace("\"time_precision\":-9", "\"time_precision\":9", StringComparison.Ordinal)), Throws.TypeOf<IrValidationException>());
        Assert.That(() => RtlDesign.Parse(json.Replace("\"kind\":\"delay\"", "\"kind\":\"fork\"", StringComparison.Ordinal)), Throws.TypeOf<IrValidationException>());
        Assert.That(() => RtlDesign.Parse(json).EmitCpp(), Throws.TypeOf<IrValidationException>());
        string located=json.Replace("\"kind\":\"delay\"","\"kind\":\"delay\",\"source\":{\"file\":\"design.sv\",\"line\":7,\"column\":3}",StringComparison.Ordinal);
        Assert.That(RtlDesign.Parse(located).EmitModelHeader("dut"),Does.Contain("frame.source="));
        Assert.That(()=>RtlDesign.Parse(located.Replace("\"line\":7","\"line\":0",StringComparison.Ordinal)),Throws.TypeOf<IrValidationException>());
        Assert.That(()=>RtlDesign.Parse(located.Replace("\"kind\":\"delay\"","\"source\":{\"file\":\"other.sv\",\"line\":1,\"column\":1},\"kind\":\"delay\"",StringComparison.Ordinal)),Throws.TypeOf<IrValidationException>());
        Assert.That(RtlDesign.Parse(located.Replace(",\"column\":3","",StringComparison.Ordinal)).EmitModelHeader("dut"),Does.Contain("frame.source="));
        foreach(string reserved in new[]{"scheduler","eventsPending","nextTimeSlot","execute"})
        {
            var design=RtlDesign.Parse(json.Replace("\"q\"","\""+reserved+"\"",StringComparison.Ordinal));
            Assert.That(design.ExternalPortNames()[reserved],Is.Not.EqualTo(reserved));
            Assert.That(design.EmitModelHeader("dut"),Does.Contain("vsrtl::Port "+design.ExternalPortNames()[reserved]+";"));
        }
        string runtime = json.Replace("\"ticks\":1", "\"amount\":{\"kind\":\"const\",\"bits\":\"11\"},\"scale\":10", StringComparison.Ordinal);
        Assert.That(RtlDesign.Parse(runtime).EmitModelHeader("dut"), Does.Contain("HdlScheduler::delayTicks("));
        foreach (string invalid in new[] {
            runtime.Replace("\"scale\":10", "\"scale\":0", StringComparison.Ordinal),
            runtime.Replace("\"scale\":10", "\"scale\":10,\"ticks\":1", StringComparison.Ordinal),
            runtime.Replace(",\"scale\":10", "", StringComparison.Ordinal),
            json.Replace("\"ticks\":1", "\"ticks\":1,\"scale\":10", StringComparison.Ordinal) })
            Assert.That(() => RtlDesign.Parse(invalid), Throws.TypeOf<IrValidationException>());
        string literal=json.Replace("\"ticks\":1","\"literal\":\"0.145\",\"unit\":-9,\"precision\":-11",StringComparison.Ordinal).Replace("\"time_precision\":-9","\"time_precision\":-12",StringComparison.Ordinal);
        Assert.That(RtlDesign.Parse(literal).EmitModelHeader("dut"),Does.Contain("Step::delay(150ULL)"));
        foreach(string invalid in new[]{literal.Replace("0.145","-0.145",StringComparison.Ordinal),literal.Replace("0.145","1ns",StringComparison.Ordinal),literal.Replace("0.145","0.144999999999999999999999999999999",StringComparison.Ordinal),literal.Replace("\"literal\":\"0.145\"","\"literal\":null",StringComparison.Ordinal),literal.Replace("\"precision\":-11","\"precision\":-13",StringComparison.Ordinal),literal.Replace("0.145","18446744073709551616",StringComparison.Ordinal)})
            Assert.That(()=>RtlDesign.Parse(invalid),Throws.TypeOf<IrValidationException>().With.Message.Not.Empty);
        string missing=json.Replace(",\"time_precision\":-9","",StringComparison.Ordinal).Replace("\"ticks\":1","\"literal\":\"0\",\"unit\":0,\"precision\":0",StringComparison.Ordinal);
        Assert.That(()=>RtlDesign.Parse(missing),Throws.TypeOf<IrValidationException>().With.Message.Not.Empty);
    }
}
