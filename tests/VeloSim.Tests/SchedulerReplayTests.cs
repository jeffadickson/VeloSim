using NUnit.Framework;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VeloSim.Tests;

public class SchedulerReplayTests
{
    private static SchedulerFixture Fixture() => SchedulerConformance.Cases()[0].Fixture;
    private static string Temp() { var d = Path.Combine(Path.GetTempPath(), "velosim-scheduler-replay-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
    private sealed class ReferenceRunner(SchedulerFixture fixture) : IProcessRunner
    {
        public bool Mismatch, FailCompiler;
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string cwd, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (arguments.SequenceEqual(["-V"])) return Task.FromResult(new ProcessResult(0, "version", ""));
            if (arguments.Contains("-g2012"))
            {
                if (FailCompiler) return Task.FromResult(new ProcessResult(9, "", "compilation failed"));
                File.WriteAllText(arguments[arguments.ToList().IndexOf("-o") + 1], "reference"); return Task.FromResult(new ProcessResult(0, "", ""));
            }
            var trace = SchedulerRegression.Oracle(fixture).ToArray(); if (Mismatch) trace[0] = trace[0] with { Values = new string('1', fixture.Observed.Count) };
            return Task.FromResult(new ProcessResult(0, $"VS_SCHED_HEADER {fixture.SignalCount} {fixture.Processes.Count} {fixture.Observed.Count} {fixture.Drives.Count}\n" + string.Join('\n', trace.Select((row, index) => $"VS_SCHED {index} {row.Tick} {row.ActivatedProcesses} {row.Values}")), ""));
        }
    }
    [Test] public async Task StructuralSchedulerReaderRoundTripsAndChecksOracleStatusAndDifferences()
    {
        var directory = Temp();
        try
        {
            var report = await new SchedulerDifferential(new ReferenceRunner(Fixture())).RunAsync(Fixture(), [1, 4], Path.Combine(directory, "run")); var json = report.ToJson();
            Assert.That(SchedulerReportReader.Parse(json).ToJson(), Is.EqualTo(json)); var node = JsonNode.Parse(json)!; node["status"] = "fail"; Assert.Throws<IrValidationException>(() => SchedulerReportReader.Parse(node.ToJsonString()));
            node = JsonNode.Parse(json)!; node["oracle"]![1]!["values"] = "11"; Assert.Throws<IrValidationException>(() => SchedulerReportReader.Parse(node.ToJsonString()));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task StrictSchedulerReplayVerifiesArtifactHashesAndPinnedCompilerRuntimeRoles()
    {
        var directory = Temp();
        try
        {
            var report = await new SchedulerDifferential(new ReferenceRunner(Fixture())).RunAsync(Fixture(), [1, 4], Path.Combine(directory, "run")); var json = report.ToJson(); Assert.That(SchedulerReplay.Validate(json).Workers, Is.EqualTo(new[] { 1, 4 }));
            var node = JsonNode.Parse(json)!; var commands = node["metadata"]!["commands"]!.AsArray(); commands.First(c => c!["arguments"]!.AsArray().Any(a => a!.GetValue<string>() == "-g2012"))!["executable"] = report.Toolchain!.Runtime;
            Assert.Throws<IrValidationException>(() => SchedulerReplay.Validate(node.ToJsonString()));
            var source = report.Metadata.Sources.Single(f => Path.GetFileName(f.Path) == "scheduler.sv"); File.AppendAllText(source.Path, "// changed\n"); Assert.Throws<IOException>(() => SchedulerReplay.Validate(json));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task SchedulerInspectAcceptsIncompleteReportsWithoutCurrentArtifacts()
    {
        var directory = Temp();
        try
        {
            var output = Path.Combine(directory, "run"); var report = await new SchedulerDifferential(new ReferenceRunner(Fixture()) { FailCompiler = true }).RunAsync(Fixture(), [1], output); var json = report.ToJson(); Directory.Delete(output, true);
            var file = Path.Combine(directory, "report.json"); File.WriteAllText(file, json); var text = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["scheduler-inspect", file], text, new StringWriter()), Is.Zero); Assert.That(text.ToString(), Does.StartWith("incomplete").And.Contain("replay unavailable")); Assert.Throws<IrValidationException>(() => SchedulerReplay.Validate(json));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task SchedulerReplayCliRunsValidatedFixtureAndWorkerMatrixInFreshDirectory()
    {
        var directory = Temp();
        try
        {
            var report = await new SchedulerDifferential(new ReferenceRunner(Fixture())).RunAsync(Fixture(), [1, 4], Path.Combine(directory, "original")); var file = Path.Combine(directory, "original", "scheduler-report.json"); var output = Path.Combine(directory, "replay"); var error = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["scheduler-replay", file, "--output-dir", output], new StringWriter(), error, new ReferenceRunner(Fixture())), Is.Zero, error.ToString());
            var replay = SchedulerReportReader.Parse(File.ReadAllText(Path.Combine(output, "scheduler-report.json"))); Assert.That(replay.Native[4], Is.EqualTo(report.Native[4])); Assert.That(replay.Toolchain, Is.EqualTo(report.Toolchain));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task SchedulerMismatchPrefixProducesRunnableFixtureAndExplicitNonMinimalityMetadata()
    {
        var directory = Temp();
        try
        {
            var report = await new SchedulerDifferential(new ReferenceRunner(Fixture()) { Mismatch = true }).RunAsync(Fixture(), [1, 4], Path.Combine(directory, "original")); Assert.That(report.FirstMismatchPrefixSteps, Is.EqualTo(1));
            var path = await SchedulerReproduction.SavePrefixAsync(report, Path.Combine(directory, "prefix")); var prefix = SchedulerFixture.Parse(File.ReadAllText(path)); Assert.That(prefix.Drives.Count, Is.EqualTo(1));
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "prefix", "reproduction.json"))); Assert.That(metadata.RootElement.GetProperty("reduction_kind").GetString(), Is.EqualTo("first_mismatch_prefix_not_general_delta_debugging"));
            var rerun = await new SchedulerDifferential(new ReferenceRunner(prefix) { Mismatch = true }).RunAsync(prefix, [1, 4], Path.Combine(directory, "rerun")); Assert.That(rerun.Status, Is.EqualTo("fail")); Assert.That(rerun.FirstMismatchPrefixSteps, Is.EqualTo(1));
        }
        finally { Directory.Delete(directory, true); }
    }
}
