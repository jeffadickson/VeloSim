using NUnit.Framework;
using System.Text;
using System.Text.Json.Nodes;

namespace VeloSim.Tests;

public class WordWorkflowHardeningTests
{
    private static string Temp() { var path = Path.Combine(Path.GetTempPath(), "velosim-word-hardening-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static WordFixture Fixture() => WordSeededStimulus.Generate(17, 8, 8, 8, [1, 4]);
    private sealed class Runner(CancellationTokenSource? cancel = null, bool cancelVersion = false) : IProcessRunner
    {
        public int Calls;
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string cwd, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (cancel is not null && (cancelVersion ? arguments.SequenceEqual(["-V"]) : arguments.Contains("-g2012"))) { cancel.Cancel(); cancellationToken.ThrowIfCancellationRequested(); }
            if (arguments.SequenceEqual(["-V"])) return Task.FromResult(new ProcessResult(0, "version", ""));
            if (arguments.Contains("-g2012")) { File.WriteAllText(arguments[arguments.ToList().IndexOf("-o") + 1], "compiled"); return Task.FromResult(new ProcessResult(0, "", "")); }
            var fixture = WordFixture.Parse(File.ReadAllText(Path.Combine(Directory.GetParent(cwd)!.FullName, "fixture.json"))); var rows = WordHdlTrace.Oracle(fixture);
            var output = new StringBuilder($"VS_WORD_HEADER {fixture.Design.Signals.Count} {fixture.Design.Processes.Count} {fixture.Drives.Count}\n");
            for (int signal = 0; signal < fixture.Design.Signals.Count; signal++) output.AppendLine($"VS_WORD_SIGNAL {signal} {fixture.Design.Signals[signal].Initial.Width}");
            for (int step = 0; step < rows.Count; step++) { output.AppendLine($"VS_WORD_STEP {step} {rows[step].Tick} {rows[step].ActivatedProcesses}"); for (int signal = 0; signal < rows[step].Values.Count; signal++) output.AppendLine($"VS_WORD_VALUE {step} {signal} {rows[step].Values[signal]}"); }
            return Task.FromResult(new ProcessResult(0, output.ToString(), ""));
        }
    }
    [Test] public async Task ReportPreflightRejectsOversizedTextAndRowsBeforeOracleEvaluation()
    {
        Assert.Throws<IrValidationException>(() => WordReportReader.Parse(new string(' ', WordEvidenceLimits.MaxReportCharacters + 1)));
        var manySignals = new IrDesign(Enumerable.Range(0, 100).Select(i => new IrSignal("s" + i, new(1))), [new("s0", "s1", "s2", "s3", IrEdge.Posedge, IrOperation.Copy)]);
        var manySteps = new WordFixture(manySignals, Enumerable.Range(0, 100_000).Select(_ => (IReadOnlyDictionary<string, FourStateWord>)new Dictionary<string, FourStateWord>()), [1]);
        Assert.Throws<ArgumentException>(() => WordSystemVerilog.Validate(manySteps));
        var directory = Temp(); try { var report = await new WordDifferential(new Runner()).RunAsync(Fixture(), Path.Combine(directory, "run")); var node = JsonNode.Parse(report.ToJson())!; ((JsonArray)node["oracle"]!).Add(node["oracle"]![0]!.DeepClone()); Assert.Throws<IrValidationException>(() => WordReportReader.Parse(node.ToJsonString())); var commands = (JsonArray)node["metadata"]!["commands"]!; while (commands.Count <= WordEvidenceLimits.MaxCommands) commands.Add(commands[0]!.DeepClone()); Assert.Throws<IrValidationException>(() => WordReportReader.Parse(node.ToJsonString())); }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task TypedReportsCannotSerializeMalformedBitsOrUnexpectedWorkersAsPass()
    {
        var directory = Temp(); try { var report = await new WordDifferential(new Runner()).RunAsync(Fixture(), Path.Combine(directory, "run")); var rows = report.Native[1].ToArray(); rows[0] = rows[0] with { Values = ["x"] }; var malformed = report with { Native = new Dictionary<int, IReadOnlyList<WordHdlStep>> { [1] = rows } }; Assert.Throws<IrValidationException>(() => malformed.ToJson()); var extra = report with { Native = new Dictionary<int, IReadOnlyList<WordHdlStep>> { [2] = report.Native[1] } }; Assert.Throws<IrValidationException>(() => _ = extra.Status); }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task ReportRejectsDuplicateEngineRolesAndContradictoryCommandEvidence()
    {
        var directory = Temp(); try { var report = await new WordDifferential(new Runner()).RunAsync(Fixture(), Path.Combine(directory, "run")); var compact = JsonNode.Parse(report.ToJson())!.ToJsonString(); Assert.Throws<IrValidationException>(() => WordReportReader.Parse(compact.Replace("\"errors\":{}", "\"errors\":{\"canceled\":\"one\",\"canceled\":\"two\"}"))); var node = JsonNode.Parse(compact)!; node["metadata"]!["commands"]![0]!["failure"] = "contradictory"; Assert.Throws<IrValidationException>(() => WordReportReader.Parse(node.ToJsonString())); node = JsonNode.Parse(compact)!; node["errors"]!["native-01"] = "unavailable"; Assert.Throws<IrValidationException>(() => WordReportReader.Parse(node.ToJsonString())); }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task MissingCompilerPersistsInspectableIncompleteReportWithNativeEvidence()
    {
        var directory = Temp(); try { var report = await new WordDifferential().RunAsync(Fixture(), Path.Combine(directory, "run"), Path.Combine(directory, "missing-iverilog")); Assert.That(report.Status, Is.EqualTo("incomplete")); Assert.That(report.Errors["icarus"], Does.StartWith("backend unavailable:")); Assert.That(report.Native.Keys, Is.EquivalentTo(new[] { 1, 4 })); Assert.That(WordReportReader.Parse(File.ReadAllText(Path.Combine(directory, "run", "word-differential-report.json"))).Status, Is.EqualTo("incomplete")); }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task CancellationPersistsCompletedBackendEvidenceAndRecordedCanceledCommand()
    {
        var directory = Temp(); try
        {
            using var cancel = new CancellationTokenSource(); var report = await new WordDifferential(new Runner(cancel)).RunAsync(Fixture(), Path.Combine(directory, "run"), cancellationToken: cancel.Token);
            Assert.That(report.Status, Is.EqualTo("incomplete")); Assert.That(report.Native.Count, Is.EqualTo(2)); Assert.That(report.Errors.ContainsKey("canceled"), Is.True); Assert.That(report.Metadata.Commands.Single().Failure, Is.Not.Null); Assert.That(WordReportReader.Parse(report.ToJson()).Status, Is.EqualTo("incomplete"));
            using var versionCancel = new CancellationTokenSource(); var completed = await new WordDifferential(new Runner(versionCancel, true)).RunAsync(Fixture(), Path.Combine(directory, "version"), cancellationToken: versionCancel.Token); Assert.That(completed.Reference, Is.Not.Null); Assert.That(completed.Status, Is.EqualTo("incomplete")); Assert.That(WordReportReader.Parse(completed.ToJson()).Reference, Is.Not.Null);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task CheckpointEnumerationIsBoundedButWrongExpectedValuesProduceFailEvidence()
    {
        var fixture = Fixture(); int enumerated = 0;
        IEnumerable<WordCheckpoint> Infinite() { while (true) { enumerated++; yield return new(0, new Dictionary<string, FourStateWord> { ["q0"] = new(8) }, null); } }
        Assert.Throws<IrValidationException>(() => new WordFixture(fixture.Design, fixture.Drives, fixture.Workers, Infinite())); Assert.That(enumerated, Is.EqualTo(fixture.Drives.Count + 1));
        var wrong = new WordFixture(fixture.Design, fixture.Drives, fixture.Workers, [new(0, new Dictionary<string, FourStateWord> { ["q0"] = new(8, 255) }, null)]); var directory = Temp();
        try { var report = await new WordDifferential().RunAsync(wrong, Path.Combine(directory, "run"), Path.Combine(directory, "missing")); Assert.That(report.Status, Is.EqualTo("fail")); Assert.That(report.Errors.ContainsKey("icarus"), Is.True); Assert.That(WordReportReader.Parse(report.ToJson()).Differences.Any(d => d.Comparison == "checkpoint:oracle"), Is.True); }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task OutputCollisionAndSymbolicLinkPreflightRunsBeforeToolInvocation()
    {
        var directory = Temp(); var runner = new Runner(); try
        {
            var file = Path.Combine(directory, "occupied"); File.WriteAllText(file, "preserve"); Assert.ThrowsAsync<IOException>(() => new WordDifferential(runner).RunAsync(Fixture(), file)); Assert.That(File.ReadAllText(file), Is.EqualTo("preserve"));
            var target = Path.Combine(directory, "target"); Directory.CreateDirectory(target); var link = Path.Combine(directory, "linked"); Directory.CreateSymbolicLink(link, target); Assert.ThrowsAsync<IOException>(() => new WordIcarus(runner).RunAsync(Fixture(), Path.Combine(link, "child"))); Assert.That(Directory.EnumerateFileSystemEntries(target), Is.Empty); Assert.That(runner.Calls, Is.Zero);
            await Task.CompletedTask;
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public void SeededWordFixturesAreStableDiverseAndCoverStrictOperationsAndKnownActivations()
    {
        var first = WordSeededStimulus.Generate(1234, 128, 64, 8, [1, 4]); var second = WordSeededStimulus.Generate(1234, 128, 64, 8, [1, 4]); Assert.That(first.ToJson(), Is.EqualTo(second.ToJson())); Assert.That(first.ToJson(), Is.Not.EqualTo(WordSeededStimulus.Generate(1235, 128, 64, 8, [1, 4]).ToJson())); Assert.That(first.Design.Processes.Select(p => p.Operation).Distinct().Count(), Is.EqualTo(10)); Assert.That(first.Drives.Any(d => d.Values.Any(v => v.XMask != 0 && v.ZMask != 0)), Is.True); Assert.That(WordHdlTrace.Oracle(first).Any(s => s.ActivatedProcesses >= 10), Is.True);
    }
    [Test] public async Task SeededConformanceCliPersistsSeedDimensionsAndExactCustomToolRoles()
    {
        var directory = Temp(); try { var output = Path.Combine(directory, "run"); var error = new StringWriter(); Assert.That(await ToolCli.RunAsync(["word-seeded-conformance", "--output-dir", output, "--seed", "4294967295", "--steps", "8", "--workers", "1,4", "--repetitions", "2", "--iverilog", "/custom compiler", "--vvp", "/custom runtime"], new StringWriter(), error, new Runner()), Is.Zero, error.ToString()); var node = JsonNode.Parse(File.ReadAllText(Path.Combine(output, "word-seeded-conformance-report.json")))!; Assert.That(node["seed"]!.GetValue<uint>(), Is.EqualTo(uint.MaxValue)); Assert.That(((JsonArray)node["cases"]!).Count, Is.EqualTo(5)); var report = WordReportReader.Parse(File.ReadAllText(node["cases"]![0]!["report_path"]!.GetValue<string>())); Assert.That(report.Toolchain.Compiler, Is.EqualTo("/custom compiler")); Assert.That(((JsonArray)node["cases"]![0]!["determinism"]!).Count, Is.EqualTo(4));
            var invalidError = new StringWriter(); Assert.That(await ToolCli.RunAsync(["--json-errors", "word-seeded-conformance", "--steps", "999999999999999999999"], new StringWriter(), invalidError), Is.EqualTo(1)); Assert.That(JsonNode.Parse(invalidError.ToString())!["schema"]!.GetValue<string>(), Is.EqualTo("velosim.cli.error.v1")); }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public void RepeatedNativeWorkersMatchIndependentOracleForEveryFullSeededTrace()
    {
        var fixture = WordSeededStimulus.Generate(0xABCDEF, 128, 64, 64, [1, 4, 8]); var checks = WordDeterminism.Run(fixture, 3); Assert.That(checks.Count, Is.EqualTo(9)); Assert.That(checks.All(c => c.MatchesOracle && c.MatchesFirstRun), Is.True); Assert.Throws<ArgumentException>(() => WordDeterminism.Run(fixture, 17)); using var canceled = new CancellationTokenSource(); canceled.Cancel(); Assert.Throws<OperationCanceledException>(() => WordDeterminism.Run(fixture, 2, canceled.Token));
    }
}
