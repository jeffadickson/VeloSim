using NUnit.Framework;
using System.Text.Json;

namespace VeloSim.Tests;

public class CounterDifferentialTests
{
    [Test] public void SeededStimulusIsStableBinaryAndBeginsWithReset()
    {
        var resets = CounterStimulus.Create(42, 100);
        Assert.That(resets, Is.EqualTo(CounterStimulus.Create(42, 100)));
        Assert.That(resets[0], Is.Zero); Assert.That(resets.All(n => n is 0 or 1), Is.True);
        Assert.That(resets.Count(n => n == 0), Is.GreaterThan(1));
        Assert.That(CounterStimulus.ToText([0, 1]), Is.EqualTo("0\n1\n"));
        Assert.Throws<ArgumentException>(() => CounterStimulus.ToText([1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => CounterStimulus.Create(1, 0));
    }
    [Test] public void GeneratedTestbenchInstantiatesSameDutAndRejectsCodeInjection()
    {
        var tb = CounterTestbench.Generate("counter", 8, [0, 1, 0]);
        Assert.That(tb, Does.Contain("counter dut(.clk(clk),.rst_n(rst_n),.count(count))"));
        Assert.That(tb, Does.Contain("VS_RESULT 2 %8b"));
        Assert.Throws<ArgumentException>(() => CounterTestbench.Generate("counter; endmodule", 8, [0]));
    }
    private sealed class VerilatorRunner : IProcessRunner
    {
        public int Calls;
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> args, string cwd, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (executable == "cmake" && args.Contains("-S"))
            {
                Assert.That(args, Does.Contain("Ninja")); return Task.FromResult(new ProcessResult(0, "", ""));
            }
            if (executable == "cmake" && args.Contains("--build"))
            {
                Assert.That(args[args.ToList().IndexOf("--parallel") + 1], Is.EqualTo("3"));
                var objects = args[1]; Directory.CreateDirectory(objects); File.WriteAllText(Path.Combine(objects, "reference"), "binary"); return Task.FromResult(new ProcessResult(0, "", ""));
            }
            return Task.FromResult(new ProcessResult(0, "VS_RESULT 0 00000000", ""));
        }
    }
    [Test] public async Task VerilatorUsesExplicitBoundedBuildJobsAndRejectsBeforeSpawn()
    {
        var runner = new VerilatorRunner(); var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var samples = await new VerilatorReference(runner).RunAsync(["counter.sv"], "tb", directory, 1, 8, 3);
            Assert.That(samples[0].Bits, Is.EqualTo("00000000")); Assert.That(runner.Calls, Is.EqualTo(3));
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await new VerilatorReference(runner).RunAsync(["counter.sv"], "tb", directory, 1, 8, 257));
            Assert.That(runner.Calls, Is.EqualTo(3));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Test] public void ReportDistinguishesIncompleteMismatchAndTwoStateSemantics()
    {
        var results = new Dictionary<string, IReadOnlyList<TraceSample>> { ["velosim"] = [new(0, "0")], ["icarus"] = [new(0, "1")] };
        var report = new CounterDifferentialReport(42, 1, 1, ["velosim", "icarus"], results, new Dictionary<string, string>()) { ObservedWidth = 1 };
        Assert.That(report.Status, Is.EqualTo("fail")); Assert.That(report.Differences["velosim:icarus"].Index, Is.Zero);
        var missing = report with { RequestedEngines = ["velosim", "icarus", "verilator"] }; Assert.That(missing.Status, Is.EqualTo("fail"));
        var incomplete = missing with { Results = new Dictionary<string, IReadOnlyList<TraceSample>> { ["velosim"] = [new(0, "0")] } }; Assert.That(incomplete.Status, Is.EqualTo("incomplete"));
        using var json = JsonDocument.Parse(report.ToJson()); Assert.That(json.RootElement.GetProperty("verilator_semantics").GetString(), Does.Contain("not_four_state_conformance"));
    }
    private sealed class NoSpawn : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default) => throw new AssertionException("Unexpected process launch.");
    }
    [Test] public async Task DifferentialCliRejectsInvalidJobBudgetAndUnsupportedEngineBeforeSpawn()
    {
        var error = new StringWriter();
        Assert.That(await ToolCli.RunAsync(["differential", "counter.sv", "--top", "counter", "--output-dir", "unused", "--build-jobs", "257"], new StringWriter(), error, new NoSpawn()), Is.EqualTo(1));
        Assert.That(await ToolCli.RunAsync(["differential", "counter.sv", "--top", "counter", "--output-dir", "unused", "--engines", "velosim,unknown"], new StringWriter(), error, new NoSpawn()), Is.EqualTo(1));
    }
}
