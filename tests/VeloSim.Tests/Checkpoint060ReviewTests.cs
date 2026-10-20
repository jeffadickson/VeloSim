using NUnit.Framework;
using System.Collections;
using System.Text.Json;

namespace VeloSim.Tests;

public class Checkpoint060ReviewTests
{
    private sealed class LongTrace : IReadOnlyList<TraceSample>
    {
        public int Count => 1_000_000;
        public TraceSample this[int index] => new(index, "00000000");
        public IEnumerator<TraceSample> GetEnumerator() => Enumerable.Range(0, Count).Select(i => this[i]).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    [Test] public void NativeRegressionCancellationInterruptsLongSimulationBeforeReportingPass()
    {
        var design = new IrDesign([new("clk", new(1)), new("rst_n", new(1)), new("count", new(8))], [new("clk", "count", "count", "rst_n", IrEdge.Posedge, IrOperation.SvIfResetIncrementStrict)]);
        var expected = new LongTrace(); var test = new RegressionCase("long", design, "clk", "count", expected.Count, expected, "rst_n");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        Assert.Throws<OperationCanceledException>(() => RegressionRunner.Run(test, [1, 4], cancellation.Token));
    }
    private sealed class MalformedRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string cwd, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (arguments.Count == 1 && arguments[0] is "--version" or "-V") return Task.FromResult(new ProcessResult(0, "test version", ""));
            switch (Path.GetFileName(executable))
            {
                case "surelog": File.WriteAllText(Path.Combine(cwd, "surelog.uhdm"), "db"); break;
                case "velosim-uhdm-normalize":
                    var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
                    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "examples", "counter.normalized.json"))) directory = directory.Parent;
                    File.Copy(Path.Combine(directory!.FullName, "examples", "counter.normalized.json"), arguments[1]); break;
                case "iverilog": File.WriteAllText(arguments[arguments.ToList().IndexOf("-o") + 1], "exe"); break;
                case "vvp": return Task.FromResult(new ProcessResult(0, "VS_RESULT not-a-cycle q", ""));
            }
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }
    [Test] public async Task MalformedReferenceTraceBecomesRecordedEngineErrorAndStructuredCliFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "velosim-malformed-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "counter.sv"); File.WriteAllText(source, "module counter; endmodule");
            var report = await new CounterDifferential(new MalformedRunner()).RunAsync([source], "counter", Path.Combine(directory, "run"), cycles: 2, engines: ["velosim", "icarus"]);
            Assert.That(report.Status, Is.EqualTo("incomplete")); Assert.That(report.Errors["icarus"], Does.Contain("Invalid VS_RESULT"));
            var error = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["--json-errors", "differential", source, "--top", "counter", "--output-dir", Path.Combine(directory, "cli"), "--cycles", "2", "--engines", "velosim,icarus"], new StringWriter(), error, new MalformedRunner()), Is.EqualTo(1));
            using var json = JsonDocument.Parse(error.ToString()); Assert.That(json.RootElement.GetProperty("category").GetString(), Is.EqualTo("CommandFailure"));
        }
        finally { Directory.Delete(directory, true); }
    }
}
