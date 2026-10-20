using NUnit.Framework;
using System.Text.Json;

namespace VeloSim.Tests;

public class SuiteTests
{
    private static string Temp() { var directory = Path.Combine(Path.GetTempPath(), "velosim-suite-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); return directory; }
    private static string Root { get { var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); while (d is not null && !File.Exists(Path.Combine(d.FullName, "examples", "counter.normalized.json"))) d = d.Parent; return d!.FullName; } }
    private static RegressionManifest Case(string name) => new(name, "counter", ["counter.sv"], "clk", "count", "rst_n", 8, TraceComparison.Parse("0 00000000\n1 00000001", 8), [1]);
    private static RegressionSuiteManifest Suite(string directory, int cases = 3)
    {
        var entries = new List<RegressionSuiteCase>();
        for (int i = 0; i < cases; i++) { var name = "case" + i; var path = Path.Combine(directory, name + ".json"); File.WriteAllText(path, Case(name).ToJson()); entries.Add(new(name, path)); }
        return new("suite", entries, 2, 4);
    }
    [Test] public void NinjaCMakeQuotesSpacesAndBracketTerminatorsAndRejectsListInjection()
    {
        var script = VerilatorReference.GenerateCMake("tb", ["/source with spaces/a]]b.sv"]);
        Assert.That(script, Does.Contain("[=[/source with spaces/a]]b.sv]=]"));
        Assert.That(script, Does.Contain("find_package(verilator REQUIRED)"));
        Assert.Throws<ArgumentException>(() => VerilatorReference.GenerateCMake("tb", ["/tmp/one;two.sv"]));
    }
    private sealed class CliRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string cwd, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (executable == "surelog-test") File.WriteAllText(Path.Combine(cwd, "surelog.uhdm"), "binary");
            else File.Copy(Path.Combine(Root, "examples", "counter.normalized.json"), arguments[1]);
            return Task.FromResult(new ProcessResult(0, "", ""));
        }
    }
    [Test] public async Task RegressionCliRunsManifestWorkerMatrixAndReturnsFailureForMismatch()
    {
        var directory = Temp();
        try
        {
            var path = Path.Combine(directory, "case.json"); File.WriteAllText(path, Case("counter").ToJson()); var output = new StringWriter(); var error = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["regression", path, "--output-dir", Path.Combine(directory, "pass"), "--surelog", "surelog-test", "--normalizer", "normalize-test"], output, error, new CliRunner()), Is.Zero, error.ToString());
            Assert.That(output.ToString(), Does.Contain("pass counter workers=1"));
            var wrong = Case("wrong") with { Expected = TraceComparison.Parse("0 00000001\n1 00000001", 8) }; File.WriteAllText(path, wrong.ToJson());
            Assert.That(await ToolCli.RunAsync(["regression", path, "--output-dir", Path.Combine(directory, "fail"), "--surelog", "surelog-test", "--normalizer", "normalize-test"], output, error, new CliRunner()), Is.EqualTo(1));
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class Executor : IRegressionExecutor
    {
        private int active; public int Peak; public int Calls; public bool FailSecond;
        public async Task<CompiledRegressionResult> RunAsync(RegressionManifest manifest, string outputDirectory, string? surelog, string? normalizer, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls); var now = Interlocked.Increment(ref active);
            int observed; do { observed = Peak; } while (now > observed && Interlocked.CompareExchange(ref Peak, now, observed) != observed);
            try
            {
                await Task.Delay(50, cancellationToken);
                if (FailSecond && manifest.Name == "case1") throw new TimeoutException("elaboration timed out");
                return new(Path.Combine(outputDirectory, "design.ir.json"), [new(manifest.Name, 1, null, manifest.Expected)]);
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
    [Test] public async Task SuiteManifestRejectsDuplicateCasesAndEnforcesTotalJobBudgetBeforeExecution()
    {
        var json = """{"format":"velosim.suite.v1","name":"suite","cases":[{"name":"case","manifest":"case.json"},{"name":"CASE","manifest":"other.json"}],"max_parallel_cases":2,"job_budget":4}""";
        Assert.Throws<IrValidationException>(() => RegressionSuiteManifest.Parse(json, Path.GetTempPath()));
        var directory = Temp();
        try
        {
            var suite = Suite(directory); var executor = new Executor();
            Assert.ThrowsAsync<ArgumentException>(async () => await new RegressionSuiteRunner(executor).RunAsync(suite with { JobBudget = 1 }, Path.Combine(directory, "results")));
            Assert.That(executor.Calls, Is.Zero);
            var manifestPath = suite.Cases[0].ManifestPath; File.WriteAllText(manifestPath, (Case("case0") with { WorkerCounts = [5] }).ToJson());
            Assert.ThrowsAsync<ArgumentException>(async () => await new RegressionSuiteRunner(executor).RunAsync(suite, Path.Combine(directory, "results"))); Assert.That(executor.Calls, Is.Zero);
            await Task.CompletedTask;
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task SuiteRunnerBoundsConcurrentCasesAndKeepsManifestOrder()
    {
        var directory = Temp();
        try
        {
            var suite = Suite(directory, 5); var executor = new Executor();
            var report = await new RegressionSuiteRunner(executor).RunAsync(suite, Path.Combine(directory, "results"));
            Assert.That(report.Passed, Is.True); Assert.That(executor.Peak, Is.EqualTo(2)); Assert.That(report.Cases.Select(c => c.Name), Is.EqualTo(suite.Cases.Select(c => c.Name)));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task SuiteReportPreservesCaseFailuresWithoutLosingCompletedCases()
    {
        var directory = Temp();
        try
        {
            var report = await new RegressionSuiteRunner(new Executor { FailSecond = true }).RunAsync(Suite(directory), Path.Combine(directory, "results"));
            Assert.That(report.Passed, Is.False); Assert.That(report.Cases.Count(c => c.Passed), Is.EqualTo(2)); Assert.That(report.Cases[1].FailureCategory, Is.EqualTo(RegressionFailureCategory.Timeout));
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "results", "suite-report.json"))); Assert.That(json.RootElement.GetProperty("status").GetString(), Is.EqualTo("fail"));
            Assert.That(RegressionSuiteReport.Classify(new DllNotFoundException()), Is.EqualTo(RegressionFailureCategory.BackendUnavailable)); Assert.That(RegressionSuiteReport.Classify(new IOException()), Is.EqualTo(RegressionFailureCategory.ArtifactFailure));
        }
        finally { Directory.Delete(directory, true); }
    }
}
