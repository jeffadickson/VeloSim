using NUnit.Framework;
using System.Text;
using System.Text.Json;

namespace VeloSim.Tests;

public class WordDifferentialTests
{
    private static string Temp() { var d = Path.Combine(Path.GetTempPath(), "velosim-word-hdl-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
    private static WordFixture Fixture(IrOperation operation = IrOperation.UnsignedAdd, int source = 8, int destination = 8)
    {
        var design = new IrDesign([new("clk", new(1)), new("rst", new(1, 1)), new("a", new(source)), new("b", new(1)), new("q", new(destination)), new("history", new(destination))], [new("clk", "q", "a", "rst", IrEdge.Posedge, operation, "b"), new("clk", "history", "q", "rst", IrEdge.Posedge, IrOperation.Copy)]);
        return new(design, [new Dictionary<string, FourStateWord> { ["a"] = new(source, 3), ["b"] = new(1, 1), ["clk"] = new(1) }, new Dictionary<string, FourStateWord> { ["clk"] = new(1, 1) }], [1, 4], [new(1, new Dictionary<string, FourStateWord> { ["history"] = new(destination) }, 2)]);
    }
    private static string Trace(WordFixture fixture, bool mismatch = false)
    {
        var rows = WordHdlTrace.Oracle(fixture); var output = new StringBuilder($"VS_WORD_HEADER {fixture.Design.Signals.Count} {fixture.Design.Processes.Count} {fixture.Drives.Count}\n");
        for (int signal = 0; signal < fixture.Design.Signals.Count; signal++) output.AppendLine($"VS_WORD_SIGNAL {signal} {fixture.Design.Signals[signal].Initial.Width}");
        for (int step = 0; step < rows.Count; step++)
        {
            output.AppendLine($"VS_WORD_STEP {step} {rows[step].Tick} {rows[step].ActivatedProcesses}");
            for (int signal = 0; signal < rows[step].Values.Count; signal++) output.AppendLine($"VS_WORD_VALUE {step} {signal} {(mismatch && step == 0 && signal == 4 ? new string('1', fixture.Design.Signals[signal].Initial.Width) : rows[step].Values[signal])}");
        }
        return output.ToString();
    }
    private sealed class ReferenceRunner(WordFixture? fixture = null, IReadOnlyDictionary<string, WordFixture>? fixtures = null) : IProcessRunner
    {
        public bool Mismatch, FailCompiler; public int Compilations; private readonly Dictionary<string, string> outputs = [];
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string cwd, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (arguments.SequenceEqual(["-V"])) return Task.FromResult(new ProcessResult(0, "Icarus test-version", ""));
            if (arguments.Contains("-g2012"))
            {
                Compilations++; if (FailCompiler) return Task.FromResult(new ProcessResult(9, "", "compiler failure"));
                var target = arguments[arguments.ToList().IndexOf("-o") + 1]; File.WriteAllText(target, "word-reference"); var current = fixture ?? fixtures![new DirectoryInfo(cwd).Parent!.Name]; outputs.Add(target, Trace(current, Mismatch)); return Task.FromResult(new ProcessResult(0, "", ""));
            }
            return Task.FromResult(new ProcessResult(0, outputs[arguments[0]], ""));
        }
    }
    [Test] public void StrictWordValidatorRejectsExperimentalArithmeticAndGeneratedClockFeedback()
    {
        foreach (var operation in new[] { IrOperation.Increment, IrOperation.ResetIncrement, IrOperation.SvIfResetIncrement }) Assert.Throws<ArgumentException>(() => WordSystemVerilog.Validate(Fixture(operation)));
        var fixture = Fixture(); var processes = fixture.Design.Processes.Select((p, i) => i == 0 ? p with { Destination = "clk" } : p).ToArray();
        Assert.Throws<ArgumentException>(() => WordSystemVerilog.Validate(new WordFixture(new IrDesign(fixture.Design.Signals, processes), fixture.Drives, fixture.Workers)));
    }
    [Test] public void ExpressionEmitterResizesBitwiseBeforeUseAndKeepsOriginalArithmeticWidth()
    {
        var bitwise = WordSystemVerilog.ProcessBody(Fixture(IrOperation.And, 64, 8).Design, 0); var arithmetic = WordSystemVerilog.ProcessBody(Fixture(IrOperation.UnsignedAdd, 64, 8).Design, 0);
        Assert.That(bitwise, Does.Contain("reg [7:0] a0,b0").And.Contain("a0=s2;b0=s3").And.Contain("s4 <= a0 & b0"));
        Assert.That(arithmetic, Does.Contain("reg [63:0] a0,b0").And.Contain("s4 <= a0 + b0")); Assert.That(bitwise, Does.Not.Contain("assign "));
    }
    [Test] public void AtomicWordTestbenchInitializesFourStateValuesBeforeEnablingNbaProcesses()
    {
        var fixture = Fixture(); var signals = fixture.Design.Signals.Select(s => s.Name == "q" ? new IrSignal(s.Name, FourStateCodec.ParseBinary("10xz01zx")) : s).ToArray();
        var source = WordSystemVerilog.Emit(new WordFixture(new IrDesign(signals, fixture.Design.Processes), fixture.Drives, fixture.Workers));
        Assert.That(source, Does.Contain("reg ready=0").And.Contain("s4=8'b10xz01zx").And.Contain("#1; ready=1").And.Contain("s5 <= s4").And.Contain("VS_WORD_SIGNAL 4 8"));
    }
    [Test] public void WordTraceParserBindsSignalOrderWidthsTicksAndActivationCounts()
    {
        var fixture = Fixture(); var output = Trace(fixture); var parsed = WordHdlTrace.Parse(output, fixture); Assert.That(parsed[1].Values[4], Is.EqualTo("00000100"));
        Assert.Throws<FormatException>(() => WordHdlTrace.Parse(output.Replace("VS_WORD_SIGNAL 4 8", "VS_WORD_SIGNAL 4 64"), fixture));
        Assert.Throws<FormatException>(() => WordHdlTrace.Parse(output.Replace("VS_WORD_STEP 0 1", "VS_WORD_STEP 0 2"), fixture)); Assert.Throws<FormatException>(() => WordHdlTrace.Parse(output + output, fixture));
    }
    [Test] public async Task FreshIcarusAdapterCompilesGeneratedWordFixtureAndRejectsStaleArtifacts()
    {
        var directory = Temp();
        try
        {
            var runner = new ReferenceRunner(Fixture()); var output = Path.Combine(directory, "run"); var trace = await new WordIcarus(runner).RunAsync(Fixture(), output); Assert.That(trace[1].ActivatedProcesses, Is.EqualTo(2)); Assert.That(File.Exists(Path.Combine(output, "word.vvp")), Is.True);
            Assert.ThrowsAsync<IOException>(async () => await new WordIcarus(runner).RunAsync(Fixture(), output)); Assert.That(runner.Compilations, Is.EqualTo(1));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task DifferentialComparesFullNativeWorkerTracesOracleExternalAndCheckpoints()
    {
        var directory = Temp();
        try
        {
            var fixture = Fixture(); var correct = await new WordDifferential(new ReferenceRunner(fixture)).RunAsync(fixture, Path.Combine(directory, "correct")); Assert.That(correct.Status, Is.EqualTo("pass"));
            var wrong = await new WordDifferential(new ReferenceRunner(fixture) { Mismatch = true }).RunAsync(fixture, Path.Combine(directory, "wrong")); Assert.That(wrong.Status, Is.EqualTo("fail")); Assert.That(wrong.Differences.Select(d => d.Comparison), Does.Contain("oracle:icarus").And.Contain("icarus:native-4"));
            var badCheckpoint = new WordFixture(fixture.Design, fixture.Drives, fixture.Workers, [new(1, new Dictionary<string, FourStateWord> { ["q"] = new(8, 9) }, null)]); var report = await new WordDifferential(new ReferenceRunner(badCheckpoint)).RunAsync(badCheckpoint, Path.Combine(directory, "checkpoint")); Assert.That(report.Differences.Any(d => d.Comparison == "checkpoint:icarus"), Is.True);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task WordReportRetainsActualSourceNativeAndToolEvidenceAndIncompleteStatus()
    {
        var directory = Temp();
        try
        {
            var fixture = Fixture(); var report = await new WordDifferential(new ReferenceRunner(fixture) { FailCompiler = true }).RunAsync(fixture, Path.Combine(directory, "incomplete")); Assert.That(report.Status, Is.EqualTo("incomplete"));
            Assert.That(report.Metadata.Sources.All(f => ArtifactMetadata.Fingerprint(f.Path).Sha256 == f.Sha256), Is.True); Assert.That(Path.GetFileName(report.Metadata.NativeLibrary!.Path), Does.Contain("velosim_ir"));
            using var json = JsonDocument.Parse(report.ToJson()); Assert.That(json.RootElement.GetProperty("source_mode").GetString(), Does.Contain("not_frontend_lowering")); Assert.That(json.RootElement.GetProperty("icarus").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task WordDifferentialCliRunsVersionedFixtureAndWritesReviewableReport()
    {
        var directory = Temp();
        try
        {
            var fixture = Fixture(); var path = Path.Combine(directory, "fixture.json"); File.WriteAllText(path, fixture.ToJson()); var output = Path.Combine(directory, "run"); var error = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["word-differential", path, "--output-dir", output], new StringWriter(), error, new ReferenceRunner(fixture)), Is.Zero, error.ToString()); Assert.That(File.Exists(Path.Combine(output, "word-differential-report.json")), Is.True);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public void ConformanceGeneratorIncludesAllStrictOpsWidthsResetStatesAndKnownMulticlockNba()
    {
        var cases = WordConformance.Cases([1, 4]); Assert.That(cases.Count, Is.EqualTo(52)); Assert.That(cases.Any(c => c.Name == "unsigned_multiply-64-1-8"), Is.True); Assert.That(cases.Any(c => c.Name == "procedural-reset-zero-one-x-z"), Is.True);
        var mixed = cases.Single(c => c.Name == "seeded-multiclock-nba").Fixture; var trace = WordHdlTrace.Oracle(mixed); Assert.That(trace[1].ActivatedProcesses, Is.EqualTo(2)); Assert.That(trace[1].Values[5], Is.EqualTo("00000101")); Assert.That(trace[1].Values[6], Is.EqualTo("00000000")); foreach (var test in cases) WordSystemVerilog.Validate(test.Fixture);
    }
    [Test] public async Task CompiledWordConformanceBatchPreserves52IndependentCaseReports()
    {
        var directory = Temp();
        try
        {
            var fixtures = WordConformance.Cases([1]).ToDictionary(c => c.Name, c => c.Fixture); var output = Path.Combine(directory, "run"); var reports = await WordConformance.RunAsync(output, [1], new ReferenceRunner(fixtures: fixtures)); Assert.That(reports.Count, Is.EqualTo(52)); Assert.That(reports.All(r => r.Status == "pass" && File.Exists(r.ReportPath)), Is.True); Assert.That(File.Exists(Path.Combine(output, "word-conformance-report.json")), Is.True);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test, Explicit("Requires installed Icarus Verilog."), Category("RealHdl")]
    public async Task ActualIcarusWordConformanceMatchesNativeAndOracleForAll52Cases()
    {
        var directory = Temp();
        try { Assert.That((await WordConformance.RunAsync(Path.Combine(directory, "run"), [1, 4, 8])).All(r => r.Status == "pass"), Is.True); }
        finally { Directory.Delete(directory, true); }
    }
}
