using NUnit.Framework;
using System.Text.Json;

namespace VeloSim.Tests;

public class SchedulerHdlTests
{
    private static SchedulerFixture Fixture() => SchedulerConformance.Cases()[0].Fixture;
    private static string Temp() { var d = Path.Combine(Path.GetTempPath(), "velosim-scheduler-hdl-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
    private static string Output(SchedulerFixture fixture, bool mismatch = false)
    {
        var trace = SchedulerRegression.Oracle(fixture).ToArray();
        if (mismatch) trace[0] = trace[0] with { Values = new string(trace[0].Values[0] == '0' ? '1' : '0', fixture.Observed.Count) };
        return $"VS_SCHED_HEADER {fixture.SignalCount} {fixture.Processes.Count} {fixture.Observed.Count} {fixture.Drives.Count}\n" + string.Join('\n', trace.Select((row, i) => $"VS_SCHED {i} {row.Tick} {row.ActivatedProcesses} {row.Values}"));
    }
    private sealed class ReferenceRunner(SchedulerFixture? fixture = null) : IProcessRunner
    {
        public bool Mismatch, FailCompiler; public int Compilations;
        private readonly Dictionary<string, string> traces = [];
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string cwd, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (arguments.SequenceEqual(["-V"])) return Task.FromResult(new ProcessResult(0, "Icarus-test-version", ""));
            if (arguments.Contains("-g2012"))
            {
                Compilations++; if (FailCompiler) return Task.FromResult(new ProcessResult(9, "compile stdout", "compile rejected"));
                var target = arguments[arguments.ToList().IndexOf("-o") + 1]; File.WriteAllText(target, "reference-binary");
                var current = fixture ?? SchedulerConformance.Cases().First(c => c.Name == new DirectoryInfo(cwd).Parent!.Name).Fixture; traces.Add(target, Output(current, Mismatch));
                return Task.FromResult(new ProcessResult(0, "", ""));
            }
            return Task.FromResult(new ProcessResult(0, traces[arguments[0]], ""));
        }
    }
    [Test] public void ScalarSystemVerilogExpressionsUseIndependentFourStateBitwiseOperators()
    {
        var p = new SchedulerProcess(0, 3, 1, 2, IrEdge.Posedge, ScalarOperation.Copy);
        Assert.That(Enum.GetValues<ScalarOperation>().Select(op => SchedulerSystemVerilog.Expression(p with { Operation = op })), Is.EqualTo(new[] { "s1", "s1 & s2", "s1 | s2", "s1 ^ s2", "~s1" }));
        Assert.Throws<ArgumentException>(() => SchedulerSystemVerilog.Expression(p with { Operation = (ScalarOperation)9 }));
    }
    [Test] public void AtomicTestbenchHasStartupGuardNbaAssignmentsAndRejectsClockFeedback()
    {
        var source = SchedulerSystemVerilog.Emit(Fixture());
        Assert.That(source, Does.Contain("reg ready=0").And.Contain("#1; ready=1").And.Contain("if (ready) begin active=active+1; s2 <= s1"));
        Assert.That(source, Does.Contain("s0=1'b0;\ns1=1'b1;").And.Contain("#1; $display(\"VS_SCHED"));
        var generatedClock = new SchedulerFixture(3, [new(0, 1, 2, 2, IrEdge.Posedge, ScalarOperation.Copy), new(1, 2, 0, 0, IrEdge.Posedge, ScalarOperation.Copy)], [new Dictionary<int, ScalarValue>()], [2]);
        Assert.Throws<ArgumentException>(() => SchedulerSystemVerilog.Emit(generatedClock));
    }
    [Test] public void SchedulerTraceParserRequiresHeaderExactTicksActivationBoundsAndFullValues()
    {
        var fixture = Fixture(); var output = Output(fixture); Assert.That(SchedulerTraceParser.Parse(output, fixture), Is.EqualTo(SchedulerRegression.Oracle(fixture)));
        Assert.Throws<FormatException>(() => SchedulerTraceParser.Parse(output.Replace("VS_SCHED 0 1", "VS_SCHED 0 2"), fixture));
        Assert.Throws<FormatException>(() => SchedulerTraceParser.Parse(output + "\n" + output, fixture));
        Assert.Throws<FormatException>(() => SchedulerTraceParser.Parse(string.Join('\n', output.Split('\n').Skip(1)), fixture));
    }
    [Test] public async Task IcarusSchedulerAdapterCompilesFreshSourceAndChecksTraceContract()
    {
        var directory = Temp();
        try
        {
            var runner = new ReferenceRunner(Fixture()); var result = await new SchedulerIcarus(runner).RunAsync(Fixture(), Path.Combine(directory, "run"));
            Assert.That(File.ReadAllText(result.SourcePath), Does.Contain("velosim_scheduler_tb")); Assert.That(result.Trace[1].Values, Is.EqualTo("10"));
            Assert.ThrowsAsync<IOException>(async () => await new SchedulerIcarus(runner).RunAsync(Fixture(), Path.Combine(directory, "run"))); Assert.That(runner.Compilations, Is.EqualTo(1));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task IndependentReferenceIsComparedWithEachNativeWorkerAndManagedOracle()
    {
        var directory = Temp();
        try
        {
            var fixture = Fixture(); var correct = await new SchedulerDifferential(new ReferenceRunner(fixture)).RunAsync(fixture, [1, 4], Path.Combine(directory, "correct")); Assert.That(correct.Status, Is.EqualTo("pass"));
            var wrong = await new SchedulerDifferential(new ReferenceRunner(fixture) { Mismatch = true }).RunAsync(fixture, [1, 4], Path.Combine(directory, "wrong"));
            Assert.That(wrong.Status, Is.EqualTo("fail")); Assert.That(wrong.Differences.Select(d => d.Comparison), Does.Contain("oracle:icarus").And.Contain("icarus:native-4"));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task SchedulerProvenanceHashesActualFixtureSourceLibraryAndToolCommands()
    {
        var directory = Temp();
        try
        {
            var report = await new SchedulerDifferential(new ReferenceRunner(Fixture())).RunAsync(Fixture(), [1], Path.Combine(directory, "run"));
            Assert.That(report.Metadata.Sources.Count, Is.EqualTo(3)); Assert.That(report.Metadata.Sources.All(file => file.Sha256 == ArtifactMetadata.Fingerprint(file.Path).Sha256), Is.True);
            Assert.That(Path.GetFileName(report.Metadata.NativeLibrary!.Path), Does.Contain("velosim_scheduler")); Assert.That(report.Metadata.Commands.Any(c => c.Arguments.Contains("-g2012")), Is.True); Assert.That(report.Metadata.ToolBinaries!.Count, Is.EqualTo(2));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task MissingIndependentReferenceProducesIncompleteReportWhileKnownMismatchWins()
    {
        var directory = Temp();
        try
        {
            var report = await new SchedulerDifferential(new ReferenceRunner(Fixture()) { FailCompiler = true }).RunAsync(Fixture(), [1, 4], Path.Combine(directory, "run"));
            Assert.That(report.Status, Is.EqualTo("incomplete")); Assert.That(report.Errors["icarus"], Does.Contain("compile rejected"));
            using var json = JsonDocument.Parse(report.ToJson()); Assert.That(json.RootElement.GetProperty("icarus").ValueKind, Is.EqualTo(JsonValueKind.Null));
            var trace = report.Oracle.ToArray(); trace[0] = trace[0] with { Values = "11" }; var mismatch = report with { Reference = trace }; Assert.That(mismatch.Status, Is.EqualTo("fail"));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task SchedulerDifferentialCliSavesTypedReportForCompiledFixtureWorkflow()
    {
        var directory = Temp();
        try
        {
            var file = Path.Combine(directory, "fixture.json"); File.WriteAllText(file, Fixture().ToJson()); var output = Path.Combine(directory, "run"); var text = new StringWriter(); var error = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["scheduler-differential", file, "--output-dir", output, "--workers", "1,4"], text, error, new ReferenceRunner(Fixture())), Is.Zero, error.ToString());
            Assert.That(text.ToString(), Does.StartWith("pass ")); Assert.That(File.Exists(Path.Combine(output, "scheduler-report.json")), Is.True);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public void SeededMulticlockFixtureIsReproducibleAndActivatesAllFiveProcesses()
    {
        var first = SchedulerStimulus.Generate(1234, 500); Assert.That(first.ToJson(), Is.EqualTo(SchedulerStimulus.Generate(1234, 500).ToJson()));
        Assert.That(SchedulerFixture.Parse(first.ToJson()).ToJson(), Is.EqualTo(first.ToJson()));
        var expected = SchedulerRegression.Oracle(first); Assert.That(expected[1].ActivatedProcesses, Is.EqualTo(5)); Assert.That(expected[1].Values, Is.EqualTo("10110")); Assert.That(first.Drives.Any(d => d.Count == 0), Is.True); Assert.That(first.Drives.SelectMany(d => d.Values).Contains(ScalarValue.Z), Is.True);
    }
    [Test] public async Task BuiltInConformanceBatchKeepsSeparateCaseReportsAndCompleteIndex()
    {
        var directory = Temp();
        try
        {
            var cases = await SchedulerConformance.RunAsync([1, 4], Path.Combine(directory, "run"), new ReferenceRunner()); Assert.That(cases.Count, Is.EqualTo(4)); Assert.That(cases.All(c => c.Status == "pass" && File.Exists(c.ReportPath)), Is.True); Assert.That(File.Exists(Path.Combine(directory, "run", "conformance-report.json")), Is.True);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test, Explicit("Requires installed Icarus Verilog."), Category("RealHdl")]
    public async Task ActualIcarusConformanceCoversNbaUnknownEdgesTruthTablesAndSeededMulticlock()
    {
        var directory = Temp();
        try { Assert.That((await SchedulerConformance.RunAsync([1, 2, 4, 8], Path.Combine(directory, "run"))).All(c => c.Status == "pass"), Is.True); }
        finally { Directory.Delete(directory, true); }
    }
}
