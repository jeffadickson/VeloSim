using NUnit.Framework;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VeloSim.Tests;

public class CounterWidthTests
{
    private static string Temp() { var d = Path.Combine(Path.GetTempPath(), "velosim-width-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
    private static string Root { get { var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); while (d is not null && !File.Exists(Path.Combine(d.FullName, "examples", "counter.normalized.json"))) d = d.Parent; return d!.FullName; } }
    private static IrDesign Counter(int width) => new([new("clk", new(1)), new("rst_n", new(1)), new("count", new(width))], [new("clk", "count", "count", "rst_n", IrEdge.Posedge, IrOperation.SvIfResetIncrementStrict)]);
    private static string Source(string directory, int width) { var path = Path.Combine(directory, "counter" + width + ".sv"); File.WriteAllText(path, $"module counter{width}(input logic clk,rst_n,output logic [{width - 1}:0] count); always_ff @(posedge clk) if(!rst_n) count <= '0; else count <= count + 1'b1; endmodule"); return path; }
    private sealed class WidthRunner : IProcessRunner
    {
        public bool FailFrontend, WrongCounterContract; public readonly List<(string Executable, string[] Arguments)> Calls = []; private readonly Dictionary<string, string> outputs = [];
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string cwd, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            var args = arguments.ToArray(); var name = Path.GetFileName(executable); Calls.Add((name, args));
            if (args.Length == 1 && args[0] is "--version" or "-V") return Task.FromResult(new ProcessResult(0, "test-version", ""));
            if (name == "surelog")
            {
                if (FailFrontend || args[Array.IndexOf(args, "-top") + 1] == "wrongtop") return Task.FromResult(new ProcessResult(9, "", "missing top"));
                var source = args.Last(); var match = Regex.Match(File.ReadAllText(source), @"logic \[(\d+):0\]"); File.WriteAllText(Path.Combine(cwd, "surelog.uhdm"), (int.Parse(match.Groups[1].Value) + 1).ToString());
            }
            else if (name == "velosim-uhdm-normalize")
            {
                var width = int.Parse(File.ReadAllText(args[0])); var tree = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "examples", "counter.normalized.json")))!; tree["signals"]![2]!["width"] = width;
                File.WriteAllText(args[1], WrongCounterContract ? tree.ToJsonString().Replace("count", "other") : tree.ToJsonString());
            }
            else if (name == "iverilog")
            {
                var tb = File.ReadAllText(args.Last()); var width = int.Parse(Regex.Match(tb, @"wire \[(\d+):0\]").Groups[1].Value) + 1;
                var resets = Regex.Matches(tb, @"rst_n=1'b([01])").Select(m => int.Parse(m.Groups[1].Value)).ToArray();
                var artifact = args[Array.IndexOf(args, "-o") + 1]; File.WriteAllText(artifact, "compiled"); outputs[artifact] = "VS_WIDTH " + width + "\n" + string.Join('\n', CounterSpecification.Generate(resets, width).Select(s => $"VS_RESULT {s.Cycle} {s.Bits}"));
            }
            else if (name == "vvp") return Task.FromResult(new ProcessResult(0, outputs[args[0]], ""));
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }
    [Test] public void LoweredCounterContractInfersOneThrough64BitsAndRejectsDifferentLogic()
    {
        Assert.That(CounterDesignContract.Inspect(Counter(1)).Width, Is.EqualTo(1)); Assert.That(CounterDesignContract.Inspect(Counter(64)).Width, Is.EqualTo(64));
        var design = Counter(16); Assert.Throws<IrValidationException>(() => CounterDesignContract.Inspect(new IrDesign(design.Signals, [design.Processes[0] with { Edge = IrEdge.Negedge }])));
    }
    [Test] public async Task CompilationFailureGatesReferencesBeforeCreatingWidthDependentTestbench()
    {
        var directory = Temp();
        try
        {
            var runner = new WidthRunner { FailFrontend = true }; var output = Path.Combine(directory, "results");
            var report = await new CounterDifferential(runner).RunAsync([Source(directory, 16)], "counter16", output, engines: ["velosim", "icarus"]);
            Assert.That(report.ObservedWidth, Is.Null); Assert.That(report.Status, Is.EqualTo("incomplete")); Assert.That(File.Exists(Path.Combine(output, "reference.sv")), Is.False);
            Assert.That(runner.Calls.Any(c => c.Arguments.Contains("-g2012")), Is.False); Assert.That(report.FailureStages["icarus"], Is.EqualTo("dependency_skipped"));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public void GeneratedTestbenchAssertsPortWidthsAndAnnouncesNegotiatedWidth()
    {
        var tb = CounterTestbench.Generate("counter64", 64, [0, 1]);
        Assert.That(tb, Does.Contain("wire [63:0] count").And.Contain("$bits(dut.count)!=64").And.Contain("VS_WIDTH %0d").And.Contain("%64b"));
    }
    [Test] public void ReferenceWidthHandshakeRetainsFull64BitUnknownValuesAndRejectsMismatch()
    {
        var bits = new string('x', 63) + "z"; Assert.That(IcarusReference.ParseOutput("VS_WIDTH 64\nVS_RESULT 0 " + bits, 1, 64, true)[0].Bits, Is.EqualTo(bits));
        Assert.Throws<FormatException>(() => IcarusReference.ParseOutput("VS_WIDTH 16\nVS_RESULT 0 " + bits, 1, 64, true)); Assert.Throws<FormatException>(() => IcarusReference.ParseOutput("VS_RESULT 0 " + bits, 1, 64, true));
    }
    [Test] public void IndependentCounterSpecificationHandlesRolloverAndResetAcrossWidths()
    {
        Assert.That(CounterSpecification.Generate([0, 1, 1, 1], 1).Select(s => s.Bits), Is.EqualTo(new[] { "0", "1", "0", "1" }));
        var samples = CounterSpecification.Generate([0, 1, 1, 0, 1], 64); Assert.That(samples[2].Bits, Is.EqualTo(new string('0', 62) + "10")); Assert.That(samples[3].Bits, Is.EqualTo(new string('0', 64)));
    }
    [Test] public void WidthAwareReportsRejectTruncatedResultsAndVersionSchemaExplicitly()
    {
        var report = new CounterDifferentialReport(1, 1, 1, ["velosim", "icarus"], new Dictionary<string, IReadOnlyList<TraceSample>> { ["velosim"] = [new(0, "0000000000000000")] }, new Dictionary<string, string>()) { ObservedWidth = 16 };
        Assert.That(report.ToJson(), Does.Contain("velosim.diff.v3").And.Contain("\"observed_width\": 16"));
        Assert.Throws<IrValidationException>(() => (report with { ObservedWidth = 64 }).ToJson());
    }
    [Test] public async Task WidthReplayBindsNegotiatedWidthToFingerprintOfCompiledIr()
    {
        var directory = Temp();
        try
        {
            var report = await new CounterDifferential(new WidthRunner()).RunAsync([Source(directory, 16)], "counter16", Path.Combine(directory, "results"), cycles: 3, engines: ["velosim", "icarus"]);
            Assert.That(report.Status, Is.EqualTo("pass")); Assert.That(DifferentialReplay.Validate(report.ToJson()).Cycles, Is.EqualTo(3));
            var node = JsonNode.Parse(report.ToJson())!; node["observed_width"] = 64; Assert.Throws<IrValidationException>(() => DifferentialReplay.Validate(node.ToJsonString()));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task CounterContractFailureIsDistinguishedFromFrontendAndReferenceErrors()
    {
        var directory = Temp();
        try
        {
            var report = await new CounterDifferential(new WidthRunner { WrongCounterContract = true }).RunAsync([Source(directory, 16)], "counter16", Path.Combine(directory, "results"), cycles: 3, engines: ["velosim", "icarus"]);
            Assert.That(report.FailureStages["velosim"], Is.EqualTo("counter_contract")); Assert.That(report.Errors["velosim"], Does.Contain("count output"));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task HdlRejectionRunnerRequiresExpectedDiagnosticAndFlagsUnexpectedAcceptance()
    {
        var directory = Temp();
        try
        {
            var cases = new[] { new HdlRejectionCase("wide", [Source(directory, 128)], "counter128", "width"), new HdlRejectionCase("wrongtop", [Source(directory, 16)], "wrongtop", "Surelog failed"), new HdlRejectionCase("accepted", [Source(directory, 64)], "counter64", "unsupported") };
            var results = await new HdlRejectionRunner(new WidthRunner()).RunAsync(cases, Path.Combine(directory, "results")); Assert.That(results.Select(r => r.RejectedAsExpected), Is.EqualTo(new[] { true, true, false }));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test, Explicit("Requires actual Surelog and UHDM normalizer."), Category("RealHdl")]
    public async Task RealUnsupported128BitBlockingAssignmentsAndWrongTopAreRejected()
    {
        var directory = Temp();
        try
        {
            var wide = Source(directory, 128); var good = Source(directory, 16); var blocking = Path.Combine(directory, "blocking.sv");
            File.WriteAllText(blocking, "module blocking(input logic clk,rst_n,output logic [7:0] count); always_ff @(posedge clk) begin if(!rst_n) count = '0; else count = count + 1'b1; end endmodule");
            var cases = new[] { new HdlRejectionCase("wide", [wide], "counter128", "width"), new HdlRejectionCase("blocking", [blocking], "blocking", "blocking assignment"), new HdlRejectionCase("wrongtop", [good], "missing_top", "top") };
            var results = await new HdlRejectionRunner().RunAsync(cases, Path.Combine(directory, "results"));
            Assert.That(results.All(r => r.RejectedAsExpected), Is.True, string.Join('\n', results.Select(r => r.Name + ": " + r.Diagnostic)));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task WidthMatrixChecksDeclared16And64BitContractsAcrossWorkersWithSeparateReports()
    {
        var directory = Temp();
        try
        {
            var cases = new[] { new CounterWidthCase("width16", [Source(directory, 16)], "counter16", 16), new CounterWidthCase("width64", [Source(directory, 64)], "counter64", 64) };
            var output = Path.Combine(directory, "results"); var results = await new CounterWidthMatrix(new WidthRunner()).RunAsync(cases, [1, 4], output, cycles: 5, engines: ["velosim", "icarus"]);
            Assert.That(results.Count, Is.EqualTo(4)); Assert.That(results.All(r => r.Status == "pass" && File.Exists(r.ReportPath)), Is.True); Assert.That(File.Exists(Path.Combine(output, "width-matrix.json")), Is.True);
        }
        finally { Directory.Delete(directory, true); }
    }
}
