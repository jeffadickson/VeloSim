using NUnit.Framework;
using System.Text.Json.Nodes;

namespace VeloSim.Tests;

public class ElaborationMatrixTests
{
    private static string Temp() { var directory = Path.Combine(Path.GetTempPath(), "velosim-matrix-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); return directory; }
    private static ElaborationMatrixPlan Plan(string directory)
    {
        var source = Path.Combine(directory, "counter.sv"); File.WriteAllText(source, "`include \"types.svh\"\nmodule counter; endmodule\n"); File.WriteAllText(Path.Combine(directory, "types.svh"), "typedef logic data_t;\n");
        return new("matrix", [new("counter", "counter", [source]), new("alternate", "counter", [source])], [1, 2]);
    }
    private sealed class MatrixRunner : IProcessRunner
    {
        public readonly List<string> ElaborationDirectories = []; public bool FailWorkerTwo;
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string cwd, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (arguments.SequenceEqual(["--version"])) return Task.FromResult(new ProcessResult(0, "Surelog test-version", ""));
            ElaborationDirectories.Add(cwd); Assert.That(Directory.EnumerateFiles(cwd, "surelog.uhdm", SearchOption.AllDirectories), Is.Empty);
            var count = arguments[arguments.ToList().IndexOf("-mt") + 1];
            if (FailWorkerTwo && count == "2") return Task.FromResult(new ProcessResult(9, "failure-stdout", "failure-stderr"));
            var artifact = Path.Combine(cwd, "slpp_all"); Directory.CreateDirectory(artifact); File.WriteAllText(Path.Combine(artifact, "surelog.uhdm"), "database-worker-" + count);
            return Task.FromResult(new ProcessResult(0, "elaborated", ""));
        }
    }
    [Test] public void MatrixPlanResolvesAllSourcesAndRejectsDuplicateWorkersAndUnsafeCaseNames()
    {
        var directory = Temp();
        try
        {
            var plan = Plan(directory); Assert.That(ElaborationMatrixPlan.Parse(plan.ToJson(), directory).ToJson(), Is.EqualTo(plan.ToJson())); Assert.That(plan.RunCount, Is.EqualTo(4));
            Assert.Throws<IrValidationException>(() => new ElaborationMatrixPlan("bad", plan.Cases, [1, 1]));
            Assert.Throws<IrValidationException>(() => new ElaborationMatrixPlan("bad", [new("../escape", "counter", plan.Cases[0].Sources)], [1]));
            Assert.Throws<IrValidationException>(() => ElaborationMatrixPlan.Parse(plan.ToJson().Replace("\"workers\":", "\"extra\":0,\"workers\":"), directory));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task MatrixElaboratesFreshCaseWorkerDirectoriesAndRejectsRerunArtifacts()
    {
        var directory = Temp();
        try
        {
            var plan = Plan(directory); var runner = new MatrixRunner(); var output = Path.Combine(directory, "results");
            var report = await new ElaborationMatrix(runner).RunAsync(plan, output, "/bin/true");
            Assert.That(report.Status, Is.EqualTo("pass")); Assert.That(runner.ElaborationDirectories.Distinct().Count(), Is.EqualTo(4));
            Assert.That(report.Records.All(r => File.Exists(r.Uhdm!.Path)), Is.True);
            Assert.ThrowsAsync<IOException>(async () => await new ElaborationMatrix(runner).RunAsync(plan, output, "/bin/true")); Assert.That(runner.ElaborationDirectories.Count, Is.EqualTo(4));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task MatrixEvidenceRetainsHashesDurationAndFailureStreamsWithoutSimulationClaims()
    {
        var directory = Temp();
        try
        {
            var report = await new ElaborationMatrix(new MatrixRunner { FailWorkerTwo = true }).RunAsync(Plan(directory), Path.Combine(directory, "results"), "/bin/true");
            Assert.That(report.Status, Is.EqualTo("fail")); Assert.That(report.Tool!.Sha256.Length, Is.EqualTo(64)); Assert.That(report.ToolVersion, Does.Contain("test-version"));
            var passed = report.Records.First(r => r.Status == "pass"); var failed = report.Records.First(r => r.Status == "fail");
            Assert.That(passed.Sources.Count, Is.EqualTo(2)); Assert.That(passed.UhdmBytes, Is.GreaterThan(0)); Assert.That(passed.ElapsedSeconds, Is.GreaterThanOrEqualTo(0));
            Assert.That(failed.Uhdm, Is.Null); Assert.That(failed.Commands[0].StandardError, Is.EqualTo("failure-stderr")); Assert.That(failed.ErrorCategory, Is.EqualTo("ToolFailure")); Assert.That(report.ToJson(), Does.Contain("elaboration_only_not_simulation_correctness"));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task StrictMatrixReportRoundTripsAndRejectsContradictoryStatusesAndMissingRuns()
    {
        var directory = Temp();
        try
        {
            var report = await new ElaborationMatrix(new MatrixRunner()).RunAsync(Plan(directory), Path.Combine(directory, "results"), "/bin/true"); var json = report.ToJson();
            Assert.That(ElaborationMatrixReport.Parse(json).ToJson(), Is.EqualTo(json));
            var node = JsonNode.Parse(json)!; node["status"] = "fail"; Assert.Throws<IrValidationException>(() => ElaborationMatrixReport.Parse(node.ToJsonString()));
            node = JsonNode.Parse(json)!; node["records"]!.AsArray().RemoveAt(0); Assert.Throws<IrValidationException>(() => ElaborationMatrixReport.Parse(node.ToJsonString()));
            node = JsonNode.Parse(json)!; node["records"]![0]!["sources"]![0]!["sha256"] = null; Assert.Throws<IrValidationException>(() => ElaborationMatrixReport.Parse(node.ToJsonString()));
            node = JsonNode.Parse(json)!; node["records"]![0]!["commands"]![0]!["executable"] = "/bin/false"; Assert.Throws<IrValidationException>(() => ElaborationMatrixReport.Parse(node.ToJsonString()));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task MatrixCliLoadsConfigOverridesWorkerPlanAndInspectsSavedReport()
    {
        var directory = Temp();
        try
        {
            var config = Path.Combine(directory, "matrix-config.json"); File.WriteAllText(config, Plan(directory).ToJson()); var output = Path.Combine(directory, "results"); var text = new StringWriter(); var error = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["elaboration-matrix", "--config", config, "--output-dir", output, "--workers", "1", "--surelog", "/bin/true"], text, error, new MatrixRunner()), Is.Zero, error.ToString());
            Assert.That(ElaborationMatrixReport.Parse(File.ReadAllText(Path.Combine(output, "matrix.json"))).Records.Count, Is.EqualTo(2));
            Assert.That(await ToolCli.RunAsync(["matrix-inspect", Path.Combine(output, "matrix.json")], text, error), Is.Zero); Assert.That(text.ToString(), Does.Contain("pass runs=2 elaboration-only"));
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class VersionCancellation(CancellationTokenSource cancellation) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default) { cancellation.Cancel(); cancellationToken.ThrowIfCancellationRequested(); throw new AssertionException("Cancellation expected."); }
    }
    [Test] public async Task VersionProbeCancellationPersistsAllCanceledMatrixRows()
    {
        var directory = Temp();
        try
        {
            using var canceled = new CancellationTokenSource(); var output = Path.Combine(directory, "results");
            var report = await new ElaborationMatrix(new VersionCancellation(canceled)).RunAsync(Plan(directory), output, "/bin/true", canceled.Token);
            Assert.That(report.Status, Is.EqualTo("canceled")); Assert.That(report.Records.Count, Is.EqualTo(4)); Assert.That(report.Records.All(r => r.Status == "canceled"), Is.True); Assert.That(File.Exists(Path.Combine(output, "matrix.json")), Is.True);
        }
        finally { Directory.Delete(directory, true); }
    }
}
