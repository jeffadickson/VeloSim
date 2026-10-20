using NUnit.Framework;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VeloSim.Tests;

public class WordReplayTests
{
    private static string Temp() { var d = Path.Combine(Path.GetTempPath(), "velosim-word-replay-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
    private static WordFixture Fixture() => new(new IrDesign([new("clk", new(1)), new("rst", new(1, 1)), new("a", new(8)), new("b", new(8)), new("q", new(8)), new("history", new(8))], [new("clk", "q", "a", "rst", IrEdge.Posedge, IrOperation.UnsignedAdd, "b"), new("clk", "history", "q", "rst", IrEdge.Posedge, IrOperation.Copy)]), [new Dictionary<string, FourStateWord> { ["clk"] = new(1), ["a"] = new(8, 3), ["b"] = new(8, 2) }, new Dictionary<string, FourStateWord> { ["clk"] = new(1, 1) }], [1, 4]);
    private sealed class ReferenceRunner(WordFixture fixture) : IProcessRunner
    {
        public bool Mismatch, FailCompiler;
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> args, string cwd, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (args.SequenceEqual(["-V"])) return Task.FromResult(new ProcessResult(0, "version", ""));
            if (args.Contains("-g2012")) { if (FailCompiler) return Task.FromResult(new ProcessResult(9, "", "compiler failure")); File.WriteAllText(args[args.ToList().IndexOf("-o") + 1], "reference"); return Task.FromResult(new ProcessResult(0, "", "")); }
            var output = new StringBuilder($"VS_WORD_HEADER {fixture.Design.Signals.Count} {fixture.Design.Processes.Count} {fixture.Drives.Count}\n"); for (int signal = 0; signal < fixture.Design.Signals.Count; signal++) output.AppendLine($"VS_WORD_SIGNAL {signal} {fixture.Design.Signals[signal].Initial.Width}");
            var rows = WordHdlTrace.Oracle(fixture); for (int step = 0; step < rows.Count; step++) { output.AppendLine($"VS_WORD_STEP {step} {rows[step].Tick} {rows[step].ActivatedProcesses}"); for (int signal = 0; signal < rows[step].Values.Count; signal++) output.AppendLine($"VS_WORD_VALUE {step} {signal} {(Mismatch && step == 0 && signal == 4 ? "11111111" : rows[step].Values[signal])}"); }
            return Task.FromResult(new ProcessResult(0, output.ToString(), ""));
        }
    }
    [Test] public async Task WordStructuralReaderRoundTripsAndRejectsStatusAndOracleContradictions()
    {
        var directory = Temp();
        try
        {
            var report = await new WordDifferential(new ReferenceRunner(Fixture())).RunAsync(Fixture(), Path.Combine(directory, "run")); var json = report.ToJson(); Assert.That(WordReportReader.Parse(json).ToJson(), Is.EqualTo(json));
            var node = JsonNode.Parse(json)!; node["status"] = "fail"; Assert.Throws<IrValidationException>(() => WordReportReader.Parse(node.ToJsonString())); node = JsonNode.Parse(json)!; node["oracle"]![1]!["values"]![4] = "11111111"; Assert.Throws<IrValidationException>(() => WordReportReader.Parse(node.ToJsonString()));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task StrictWordReplayBindsFixtureIrSourceArtifactHashAndToolRoles()
    {
        var directory = Temp();
        try
        {
            var report = await new WordDifferential(new ReferenceRunner(Fixture())).RunAsync(Fixture(), Path.Combine(directory, "run")); var json = report.ToJson(); Assert.That(WordReplay.Validate(json).Fixture.ToJson(), Is.EqualTo(Fixture().ToJson()));
            var node = JsonNode.Parse(json)!; node["toolchain"]!["compiler"] = report.Toolchain.Runtime; Assert.Throws<IrValidationException>(() => WordReplay.Validate(node.ToJsonString()));
            var ir = report.Metadata.Sources.Single(s => Path.GetFileName(s.Path) == "design.ir.json"); File.AppendAllText(ir.Path, "\n"); Assert.Throws<IOException>(() => WordReplay.Validate(json));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task WordInspectAcceptsIncompleteEvidenceWithoutRequiringCurrentArtifactFiles()
    {
        var directory = Temp();
        try
        {
            var run = Path.Combine(directory, "run"); var report = await new WordDifferential(new ReferenceRunner(Fixture()) { FailCompiler = true }).RunAsync(Fixture(), run); var json = report.ToJson(); Directory.Delete(run, true); var path = Path.Combine(directory, "report.json"); File.WriteAllText(path, json); var output = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["word-inspect", path], output, new StringWriter()), Is.Zero); Assert.That(output.ToString(), Does.StartWith("incomplete").And.Contain("replay unavailable")); Assert.Throws<IrValidationException>(() => WordReplay.Validate(json));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task WordReplayCliPreservesPinnedToolsFixtureAndAllNativeWorkers()
    {
        var directory = Temp();
        try
        {
            var report = await new WordDifferential(new ReferenceRunner(Fixture())).RunAsync(Fixture(), Path.Combine(directory, "original")); var path = Path.Combine(directory, "original", "word-differential-report.json"); var output = Path.Combine(directory, "replay"); var error = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["word-replay", path, "--output-dir", output], new StringWriter(), error, new ReferenceRunner(Fixture())), Is.Zero, error.ToString()); var replay = WordReportReader.Parse(File.ReadAllText(Path.Combine(output, "word-differential-report.json"))); Assert.That(replay.Toolchain, Is.EqualTo(report.Toolchain)); Assert.That(WordReportReader.TraceEqual(replay.Native[4], report.Native[4]), Is.True);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task WordPrefixReproductionRetainsExactCustomToolsAndExplicitPrefixOnlyClaim()
    {
        var directory = Temp();
        try
        {
            var report = await new WordDifferential(new ReferenceRunner(Fixture()) { Mismatch = true }).RunAsync(Fixture(), Path.Combine(directory, "original")); Assert.That(report.FirstMismatchPrefixSteps, Is.EqualTo(1)); var custom = report with { Toolchain = new("/custom compiler/iverilog", "/custom runtime/vvp") }; var output = Path.Combine(directory, "prefix"); var path = await WordReproduction.SavePrefixAsync(custom, output); Assert.That(WordFixture.Parse(File.ReadAllText(path)).Drives.Count, Is.EqualTo(1));
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "reproduction.json"))); Assert.That(json.RootElement.GetProperty("arguments").EnumerateArray().Select(e => e.GetString()), Does.Contain(custom.Toolchain.Compiler).And.Contain(custom.Toolchain.Runtime)); Assert.That(json.RootElement.GetProperty("reduction_kind").GetString(), Is.EqualTo("first_mismatch_prefix_not_general_delta_debugging"));
        }
        finally { Directory.Delete(directory, true); }
    }
}
