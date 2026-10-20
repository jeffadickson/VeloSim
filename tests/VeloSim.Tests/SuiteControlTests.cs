using NUnit.Framework;
using System.Text.Json;

namespace VeloSim.Tests;

public class SuiteControlTests
{
    private static string Temp() { var d = Path.Combine(Path.GetTempPath(), "velosim-control-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }
    private static RegressionSuiteManifest Suite(string directory, int[] costs)
    {
        var cases = new List<RegressionSuiteCase>();
        for (int i = 0; i < costs.Length; i++)
        {
            var name = "case" + i; var path = Path.Combine(directory, name + ".json");
            File.WriteAllText(path, new RegressionManifest(name, "counter", ["counter.sv"], "clk", "count", "rst_n", 8, [new(0, "00000000")], [costs[i]]).ToJson()); cases.Add(new(name, path));
        }
        return new("control", cases, 3, 4);
    }
    private sealed class BudgetExecutor : IRegressionExecutor
    {
        public CancellationTokenSource? CancelAfterFirst; private int jobs; public int PeakJobs; public int Calls;
        public async Task<CompiledRegressionResult> RunAsync(RegressionManifest manifest, string outputDirectory, string? surelog, string? normalizer, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls); var cost = manifest.WorkerCounts.Max(); var now = Interlocked.Add(ref jobs, cost); int old;
            do { old = PeakJobs; } while (now > old && Interlocked.CompareExchange(ref PeakJobs, now, old) != old);
            try { await Task.Delay(30, cancellationToken); if (manifest.Name == "case0") CancelAfterFirst?.Cancel(); return new("design.ir.json", [new(manifest.Name, cost, null, manifest.Expected)]); }
            finally { Interlocked.Add(ref jobs, -cost); }
        }
    }
    [Test] public async Task CancellationPersistsCompletedAndQueuedOutcomesInPartialReport()
    {
        var directory = Temp();
        try
        {
            using var cancel = new CancellationTokenSource(); var executor = new BudgetExecutor { CancelAfterFirst = cancel };
            var report = await new RegressionSuiteRunner(executor).RunAsync(Suite(directory, [1, 1, 1]) with { MaxParallelCases = 1 }, Path.Combine(directory, "results"), cancellationToken: cancel.Token);
            Assert.That(report.Status, Is.EqualTo("canceled")); Assert.That(report.Cases[0].Passed, Is.True); Assert.That(report.Cases.Skip(1).All(c => c.FailureCategory == RegressionFailureCategory.Canceled), Is.True); Assert.That(executor.Calls, Is.EqualTo(1));
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "results", "suite-report.json"))); Assert.That(json.RootElement.GetProperty("cases").GetArrayLength(), Is.EqualTo(3));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public void CaseFiltersPreserveOrderAndRejectAccidentalEmptySelection()
    {
        var suite = new RegressionSuiteManifest("filters", [new("fast-a", "a.json"), new("slow-b", "b.json"), new("fast-c", "c.json")], 1, 1);
        var selected = SuiteSelector.Select(suite, ["fast-*"], ["*-c"]);
        Assert.That(selected.Cases.Select(c => c.Name), Is.EqualTo(new[] { "fast-a" })); Assert.That(selected.Selection!.OriginalCaseCount, Is.EqualTo(3));
        Assert.Throws<ArgumentException>(() => SuiteSelector.Select(suite, ["typo*"])); Assert.Throws<ArgumentException>(() => SuiteSelector.Select(suite, ["../*"]));
    }
    [Test] public void StableShardsPartitionCasesWithoutReassignmentWhenOrderChanges()
    {
        var cases = Enumerable.Range(0, 64).Select(i => new RegressionSuiteCase("case" + i, i + ".json")).ToArray(); var suite = new RegressionSuiteManifest("shards", cases, 1, 1);
        var selected = Enumerable.Range(0, 4).SelectMany(index => SuiteSelector.Select(suite, shardIndex: index, shardCount: 4).Cases).ToArray();
        Assert.That(selected.Select(c => c.Name).Distinct().Count(), Is.EqualTo(cases.Length)); Assert.That(selected.Length, Is.EqualTo(cases.Length));
        Assert.That(SuiteSelector.AssignShard("CASE42", 4), Is.EqualTo(SuiteSelector.AssignShard("case42", 4)));
        var reversed = suite with { Cases = cases.Reverse().ToArray() }; Assert.That(SuiteSelector.Select(reversed, shardCount: 4).Cases.Select(c => c.Name).Order(), Is.EqualTo(SuiteSelector.Select(suite, shardCount: 4).Cases.Select(c => c.Name).Order()));
    }
    [Test] public async Task WeightedBudgetSchedulesLargeCasesWithoutOversubscribingAggregateWorkers()
    {
        var directory = Temp();
        try
        {
            var executor = new BudgetExecutor(); var report = await new RegressionSuiteRunner(executor).RunAsync(Suite(directory, [3, 1, 3, 4]), Path.Combine(directory, "results"));
            Assert.That(report.Passed, Is.True); Assert.That(executor.PeakJobs, Is.EqualTo(4)); Assert.That(executor.Calls, Is.EqualTo(4));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task StructuredCliErrorsExposeTypedCategoryAndCancellationExitCode()
    {
        var error = new StringWriter();
        Assert.That(await ToolCli.RunAsync(["--json-errors", "scheduler", "missing", "--workers", "999"], new StringWriter(), error), Is.EqualTo(1));
        using var invalid = JsonDocument.Parse(error.ToString()); Assert.That(invalid.RootElement.GetProperty("category").GetString(), Is.EqualTo("InvalidInput"));
        error.GetStringBuilder().Clear(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.That(await ToolCli.RunAsync(["--json-errors", "compile"], new StringWriter(), error, cancellationToken: cancel.Token), Is.EqualTo(130));
        using var canceled = JsonDocument.Parse(error.ToString()); Assert.That(canceled.RootElement.GetProperty("category").GetString(), Is.EqualTo("Canceled"));
    }
}
