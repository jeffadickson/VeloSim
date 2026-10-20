using NUnit.Framework;
using System.Text.Json;

namespace VeloSim.Tests;
public class NativeBenchmarkTests
{
    [Test] public void BenchmarkPlanRejectsSingleProcessAndOversizedWorkBeforeNativeAllocation()
    {
        var plan = new BenchmarkPlan(2, 8, 8, 1, 0, [1, 4]); plan.Validate();
        Assert.Throws<ArgumentException>(() => (plan with { Processes = 1 }).Validate());
        Assert.Throws<ArgumentException>(() => (plan with { Processes = 256, Cycles = 100_000 }).Validate());
        Assert.Throws<ArgumentException>(() => (plan with { Workers = [1, 1] }).Validate());
    }
    [Test] public void BenchmarkStatisticsUseMedianAndNearestRankP95WithoutOverflow()
    {
        var stats = BenchmarkStatistics.From([4, 1, 2, 3]); Assert.That(stats.Median, Is.EqualTo(2.5)); Assert.That(stats.P95, Is.EqualTo(4));
        Assert.That(BenchmarkStatistics.From([double.MaxValue, double.MaxValue]).Median, Is.EqualTo(double.MaxValue));
        Assert.Throws<ArgumentException>(() => BenchmarkStatistics.From([double.NaN])); Assert.Throws<ArgumentException>(() => BenchmarkStatistics.From([0]));
    }
    [Test] public void BenchmarkUsesMultipleActiveProcessesAndVerifiesRolloverAcrossWorkerCounts()
    {
        var report = NativeBenchmark.Run(new(4, 257, 8, 2, 1, [1, 4]));
        Assert.That(report.Status, Is.EqualTo("pass")); Assert.That(report.Cells.Select(c => c.Checksum).Distinct().Count(), Is.EqualTo(1));
        Assert.That(report.Cells.All(c => c.Seconds.Count == 2 && c.Seconds.All(v => v > 0)), Is.True);
        Assert.That(report.NativeProfile, Is.AnyOf("debug", "optimized"));
    }
    [Test] public void BenchmarkReportCarriesHostAndNativeIdentityAndExplicitMeasurementScope()
    {
        var report = NativeBenchmark.Run(new(2, 8, 64, 1, 0, [1, 2])); using var json = JsonDocument.Parse(report.ToJson());
        Assert.That(json.RootElement.GetProperty("native_sha256").GetString()!.Length, Is.EqualTo(64));
        Assert.That(json.RootElement.GetProperty("host_sha256").GetString()!.Length, Is.EqualTo(64));
        Assert.That(json.RootElement.GetProperty("measurement_scope").GetString(), Does.Contain("managed_drive_api"));
        Assert.That(report.ManagedAssemblies.Any(f => Path.GetFileName(f.Path) == "VeloSim.dll" && f.Sha256.Length == 64), Is.True);
        Assert.That(json.RootElement.GetProperty("cells")[0].GetProperty("relative_median_to_one_worker").GetDouble(), Is.EqualTo(1));
    }
    [Test] public void BenchmarkCancellationPreservesPartialWorkerOutcomes()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); Assert.Throws<OperationCanceledException>(() => NativeBenchmark.Run(new(2, 8, 8, 1, 0, [1]), canceled.Token));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        var report = NativeBenchmark.Run(new(2, 100_000, 8, 1, 0, [2, 4]), deadline.Token);
        Assert.That(report.Status, Is.EqualTo("canceled")); Assert.That(report.Cells.Count, Is.EqualTo(2)); Assert.That(report.Cells.All(c => c.Status == "canceled"), Is.True);
    }
}
