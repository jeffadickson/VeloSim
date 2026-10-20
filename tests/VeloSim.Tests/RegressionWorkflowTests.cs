using NUnit.Framework;

namespace VeloSim.Tests;

public class RegressionWorkflowTests
{
    private static string Root
    {
        get { var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); while (d is not null && !File.Exists(Path.Combine(d.FullName, "examples", "counter.normalized.json"))) d = d.Parent; return d!.FullName; }
    }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "velosim-workflow-" + Guid.NewGuid().ToString("N"));
    private static RegressionManifest Manifest() => new("counter", "slang_counter", [Path.Combine(Root, "examples", "slang_counter.sv")], "clk", "count", "rst_n", 8, TraceComparison.Parse("0 00000000\n1 00000001\n2 00000010", 8), [1, 4]);
    private sealed class FakeRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (executable == "surelog") File.WriteAllText(Path.Combine(workingDirectory, "surelog.uhdm"), "binary");
            else if (executable == "normalize") File.Copy(Path.Combine(Root, "examples", "counter.normalized.json"), arguments[1]);
            else if (executable == "iverilog") File.WriteAllText(arguments[4], "compiled");
            else if (executable == "vvp") return Task.FromResult(new ProcessResult(0, "VS_RESULT 0 00000000\nVS_RESULT 1 00000001\nVS_RESULT 2 00000010\n", ""));
            else throw new InvalidOperationException("Unexpected command " + executable);
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }
    [Test] public async Task ValidationAndLowerCliFailClosedAndDoNotOverwriteOutput()
    {
        var path = Path.GetTempFileName(); File.Delete(path);
        try
        {
            var output = new StringWriter(); var error = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["lower", Path.Combine(Root, "examples", "counter.normalized.json"), "--output", path], output, error), Is.Zero);
            Assert.That(await ToolCli.RunAsync(["validate-ir", path], output, error), Is.Zero);
            Assert.That(await ToolCli.RunAsync(["lower", Path.Combine(Root, "examples", "counter.normalized.json"), "--output", path], output, error), Is.EqualTo(1));
            File.WriteAllText(path, "{}"); Assert.That(await ToolCli.RunAsync(["validate-ir", path], output, error), Is.EqualTo(1));
        }
        finally { File.Delete(path); }
    }
    [Test] public void ManifestRoundTripResolvesPathsAndRejectsInvalidMatricesAndCycles()
    {
        var json = Manifest().ToJson(); var parsed = RegressionManifest.Parse(json, Root);
        Assert.That(parsed.Sources, Is.EqualTo(Manifest().Sources)); Assert.That(parsed.Expected, Is.EqualTo(Manifest().Expected));
        Assert.Throws<IrValidationException>(() => RegressionManifest.Parse(json.Replace("\"format\":", "\"unexpected\":0,\"format\":"), Root));
        Assert.Throws<IrValidationException>(() => RegressionManifest.Parse(json.Replace("00000000", "invalid"), Root));
        var duplicate = Manifest() with { WorkerCounts = [1, 1] }; Assert.Throws<IrValidationException>(() => RegressionManifest.Parse(duplicate.ToJson(), Root));
    }
    [Test] public async Task CompiledRegressionExecutesLoweredDesignAndChecksDeclaredWidth()
    {
        var directory = Temp();
        try
        {
            var result = await new CompiledRegression(new FakeRunner()).RunAsync(Manifest(), directory, "surelog", "normalize");
            Assert.That(result.Passed, Is.True); Assert.That(result.Results.Count, Is.EqualTo(2));
            Assert.That(File.Exists(result.IrPath), Is.True);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task IcarusReferenceRunsCompilerAndRuntimeAndRejectsMalformedResults()
    {
        var directory = Temp();
        try
        {
            var samples = await new IcarusReference(new FakeRunner()).RunAsync(["reference.sv"], "tb", directory, 3, 8);
            Assert.That(samples, Is.EqualTo(Manifest().Expected));
            Assert.Throws<FormatException>(() => IcarusReference.ParseOutput("VS_RESULT 0 0\nVS_RESULT 0 1", 2, 8));
            Assert.Throws<FormatException>(() => IcarusReference.ParseOutput("VS_RESULT 0 q", 1, 8));
            Assert.Throws<FormatException>(() => IcarusReference.ParseOutput("VS_RESULT 0 0", 2, 8));
            foreach (var shortBits in new[] { "0", "x", "z" }) Assert.Throws<FormatException>(() => IcarusReference.ParseOutput("VS_RESULT 0 " + shortBits, 1, 8));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task HdlDifferentialUsesCompiledSourceAndIndependentReferenceTrace()
    {
        var directory = Temp();
        try
        {
            var result = await new HdlDifferential(new FakeRunner()).RunAsync(Manifest(), ["reference.sv"], "tb", directory, "surelog", "normalize");
            Assert.That(result.Passed, Is.True); Assert.That(result.SourceMode, Is.EqualTo("surelog_hdl_compilation"));
            Assert.That(result.Reference, Is.EqualTo(Manifest().Expected));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test, Explicit("Requires installed Surelog/UHDM normalizer and Icarus."), Category("RealHdl")]
    public async Task RealHdlDifferentialCompilesSameRtlWithSurelogAndIcarus()
    {
        var directory = Temp(); Directory.CreateDirectory(directory);
        try
        {
            var tb = Path.Combine(directory, "reference.sv");
            await File.WriteAllTextAsync(tb, """
                module reference_tb;
                  reg clk=0; reg rst_n=0; wire [7:0] count;
                  slang_counter dut(clk,rst_n,count);
                  initial begin
                    for(integer cycle=0;cycle<3;cycle=cycle+1) begin
                      rst_n=(cycle!=0); #1 clk=1; #1 $display("VS_RESULT %0d %08b",cycle,count); #1 clk=0;
                    end
                    $finish;
                  end
                endmodule
                """);
            var manifest = Manifest();
            var result = await new HdlDifferential().RunAsync(manifest, [.. manifest.Sources, tb], "reference_tb", Path.Combine(directory, "results"));
            Assert.That(result.Passed, Is.True);
            Assert.That(result.Reference, Is.EqualTo(manifest.Expected));
        }
        finally { Directory.Delete(directory, true); }
    }
}
