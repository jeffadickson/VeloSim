using NUnit.Framework;
using System.Text.Json;

namespace VeloSim.Tests;

public class PipelineTests
{
    private static string Normalized
    {
        get
        {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "examples", "counter.normalized.json"))) dir = dir.Parent;
            return File.ReadAllText(Path.Combine(dir!.FullName, "examples", "counter.normalized.json"));
        }
    }
    private sealed class FakeRunner(Func<string, IReadOnlyList<string>, string, ProcessResult> action) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(action(executable, arguments, workingDirectory));
    }
    private static FakeRunner PipelineRunner() => new((exe, args, cwd) =>
    {
        if (exe == "surelog-test") { File.WriteAllText(Path.Combine(cwd, "surelog.uhdm"), "binary"); }
        else { Assert.That(exe, Is.EqualTo("normalize-test")); Assert.That(args[2], Is.EqualTo("counter")); File.WriteAllText(args[1], Normalized); }
        return new(0, "", "");
    });
    [Test] public async Task PipelineLowersNormalizerOutputAndRefusesStaleResults()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var pipeline = new DesignPipeline(PipelineRunner());
            var path = await pipeline.CompileAsync(["counter.sv"], "counter", dir, "surelog-test", "normalize-test");
            Assert.That(IrDesign.Parse(File.ReadAllText(path)).Signals[2].Initial.XMask, Is.EqualTo(255));
            Assert.ThrowsAsync<IOException>(async () => await pipeline.CompileAsync(["counter.sv"], "counter", dir));
        }
        finally { Directory.Delete(dir, true); }
    }
    [Test] public async Task SlangElaboratorParsesDesignAndCleansTemporaryDirectory()
    {
        string? directory = null;
        var frontend = new SlangFrontend(new FakeRunner((exe, args, cwd) =>
        {
            directory = cwd; Assert.That(args, Does.Contain("--top"));
            File.WriteAllText(args[args.ToList().IndexOf("--ast-json") + 1], """{"design":{"kind":"Root","members":[{"kind":"Instance","name":"counter"},{"kind":"Package","name":"ignored"}]}}""");
            return new(0, "", "");
        }));
        using var document = await frontend.CompileAsync(["input.sv"], "counter");
        Assert.That(SlangFrontend.Summarize(document.RootElement).TopInstances, Is.EqualTo(new[] { "counter" }));
        Assert.That(Directory.Exists(directory), Is.False);
        using var bad = JsonDocument.Parse("{}"); Assert.Throws<IrValidationException>(() => SlangFrontend.Summarize(bad.RootElement));
    }
    [Test] public async Task CompileCliWritesValidatedIrAndReportsMissingArguments()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var output = new StringWriter(); var error = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["compile", "counter.sv", "--top", "counter", "--output-dir", dir, "--surelog", "surelog-test", "--normalizer", "normalize-test"], output, error, PipelineRunner()), Is.Zero, error.ToString());
            Assert.That(File.Exists(output.ToString().Trim()), Is.True);
            Assert.That(await ToolCli.RunAsync(["compile", "counter.sv", "--threads", "no"], output, error), Is.EqualTo(1));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Test] public void TraceParsingNormalizesFourStateAndReportsMissingOrDifferingSamples()
    {
        var expected = TraceComparison.Parse("0 0X\n1 1Z\n", 2);
        Assert.That(expected[0].Bits, Is.EqualTo("0x"));
        Assert.That(TraceComparison.Compare(expected, TraceComparison.Parse("0 0x\n1 10", 2))!.Index, Is.EqualTo(1));
        Assert.That(TraceComparison.Compare(expected, expected.Take(1).ToArray())!.Actual, Is.Null);
        Assert.Throws<FormatException>(() => TraceComparison.Parse("0 00\n0 01", 2));
        Assert.Throws<FormatException>(() => TraceComparison.Parse("0 0q", 2));
    }
    [Test] public void RegressionMatrixRunsFreshNativeEnginesAndDetectsWrongExpectedResult()
    {
        var design = NormalizedUhdm.Lower(Normalized);
        var expected = TraceComparison.Parse("0 00000000\n1 00000001\n2 00000010", 8);
        var test = new RegressionCase("counter", design, "clk", "count", 3, expected, "rst_n");
        var results = RegressionRunner.Run(test, [1, 4]);
        Assert.That(results.All(r => r.Mismatch is null), Is.True);
        var wrong = test with { Expected = TraceComparison.Parse("0 00000001\n1 00000001\n2 00000010", 8) };
        Assert.That(RegressionRunner.Run(wrong, [1])[0].Mismatch!.Index, Is.Zero);
    }
    [Test] public void Review020ResetAndUnknownInitialStateRegressions()
    {
        var design = NormalizedUhdm.Lower(Normalized);
        Assert.Throws<IrValidationException>(() => new IrDesign(design.Signals.Select(s => s.Name == "rst_n" ? new IrSignal(s.Name, new(2)) : s), design.Processes));
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, design.ToJson()); var output = new StringWriter();
            Assert.That(ToolCli.Run(["simulate", file, "--cycles", "2", "--release-reset"], output, new StringWriter()), Is.Zero);
            Assert.That(output.ToString(), Does.Contain("count=(8,0,0,0)").And.Contain("count=(8,1,0,0)"));
        }
        finally { File.Delete(file); }
    }
}
