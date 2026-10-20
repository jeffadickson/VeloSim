using NUnit.Framework;

namespace VeloSim.Tests;

public class SchedulerTests
{
    [Test, NonParallelizable] public void SchedulerOwnsNativeLifetimeAndPinsResolvedModuleAcrossEnvironmentChanges()
    {
        using var scheduler = new ScalarScheduler(2); var library = ScalarScheduler.NativeLibraryPath;
        var previous = Environment.GetEnvironmentVariable("VELOSIM_NATIVE_DIR");
        try
        {
            Environment.SetEnvironmentVariable("VELOSIM_NATIVE_DIR", "/nonexistent/changed-after-load");
            Assert.That(scheduler.Read(0), Is.EqualTo(ScalarValue.X)); Assert.That(scheduler.Ticks, Is.Zero);
            Assert.That(ScalarScheduler.NativeLibraryPath, Is.EqualTo(library));
            scheduler.Dispose(); scheduler.Dispose(); Assert.Throws<ObjectDisposedException>(() => scheduler.Read(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ScalarScheduler(0));
        }
        finally { Environment.SetEnvironmentVariable("VELOSIM_NATIVE_DIR", previous); }
    }
    [Test] public void SchedulerRegistrationAndDrivesValidateAtomicallyAndTrackTicks()
    {
        using var scheduler = new ScalarScheduler(4);
        scheduler.AddProcess(new(0, 2, 1, 1, IrEdge.Posedge, ScalarOperation.Copy));
        Assert.Throws<ArgumentException>(() => scheduler.AddProcess(new(0, 2, 1, 1, IrEdge.Posedge, ScalarOperation.Not)));
        Assert.Throws<ArgumentException>(() => scheduler.AddProcess(new(0, 3, 1, 1, IrEdge.Posedge, (ScalarOperation)5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.AddProcess(new(0, 3, 1, 4, IrEdge.Posedge, ScalarOperation.Copy)));
        scheduler.Drive(new Dictionary<int, ScalarValue> { [0] = ScalarValue.Zero, [1] = ScalarValue.One });
        var ticks = scheduler.Ticks;
        Assert.Throws<ArgumentException>(() => scheduler.Drive(new Dictionary<int, ScalarValue> { [0] = ScalarValue.One, [1] = (ScalarValue)4 }));
        Assert.That(scheduler.Read(0), Is.EqualTo(ScalarValue.Zero)); Assert.That(scheduler.Ticks, Is.EqualTo(ticks));
        Assert.That(scheduler.Drive(new Dictionary<int, ScalarValue> { [0] = ScalarValue.One }, 4), Is.EqualTo(1)); Assert.That(scheduler.Read(2), Is.EqualTo(ScalarValue.One));
        Assert.That(scheduler.Ticks, Is.EqualTo(ticks + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Drive(new Dictionary<int, ScalarValue>(), 257));
    }
    [Test] public void IndependentScalarTruthTablesAndEdgesAgreeWithNativeKernel()
    {
        foreach (var operation in Enum.GetValues<ScalarOperation>())
        {
            using var scheduler = new ScalarScheduler(5);
            scheduler.AddProcess(new(0, 3, 1, 2, IrEdge.Posedge, operation)); scheduler.AddProcess(new(0, 4, 2, 1, IrEdge.Posedge, ScalarOperation.Copy));
            foreach (var a in Enum.GetValues<ScalarValue>()) foreach (var b in Enum.GetValues<ScalarValue>())
            {
                scheduler.Drive(new Dictionary<int, ScalarValue> { [0] = ScalarValue.Zero, [1] = a, [2] = b }, 4);
                Assert.That(scheduler.Drive(new Dictionary<int, ScalarValue> { [0] = ScalarValue.One }, 4), Is.EqualTo(2));
                Assert.That(scheduler.Read(3), Is.EqualTo(ScalarLogic.Evaluate(operation, a, b)), $"{operation} {a} {b}");
            }
        }
        foreach (var edge in Enum.GetValues<IrEdge>()) foreach (var before in Enum.GetValues<ScalarValue>()) foreach (var after in Enum.GetValues<ScalarValue>())
        {
            using var scheduler = new ScalarScheduler(3); scheduler.AddProcess(new(0, 2, 1, 1, edge, ScalarOperation.Copy));
            scheduler.Drive(new Dictionary<int, ScalarValue> { [0] = before, [1] = ScalarValue.One });
            Assert.That(scheduler.Drive(new Dictionary<int, ScalarValue> { [0] = after }), Is.EqualTo(ScalarLogic.IsEdge(edge, before, after) ? 1 : 0), $"{edge} {before}->{after}");
        }
    }
    private const string FixtureJson = """{"format":"velosim.scheduler.v1","signal_count":4,"processes":[{"clock":0,"destination":2,"a":1,"b":1,"edge":"posedge","operation":"copy"},{"clock":0,"destination":3,"a":2,"b":2,"edge":"posedge","operation":"copy"}],"drives":[[{"signal":0,"value":"0"},{"signal":1,"value":"1"},{"signal":2,"value":"0"},{"signal":3,"value":"0"}],[{"signal":0,"value":"1"}],[{"signal":0,"value":"0"},{"signal":1,"value":"0"}],[{"signal":0,"value":"1"}]],"observed":[2,3]}""";
    [Test] public async Task SchedulerFixtureCliRunsTraceAndRejectsDuplicateUpdatesBeforeExecution()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, FixtureJson); var output = new StringWriter(); var error = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["scheduler", file, "--workers", "4"], output, error), Is.Zero, error.ToString());
            Assert.That(output.ToString(), Does.Contain("workers=4 tick=2 active=2 values=10").And.Contain("workers=4 tick=4 active=2 values=01"));
            Assert.That(await ToolCli.RunAsync(["scheduler-check", file, "--workers", "1,4,8"], output, error), Is.Zero, error.ToString());
            Assert.Throws<IrValidationException>(() => SchedulerFixture.Parse(FixtureJson.Replace("\"signal\":1,\"value\":\"1\"", "\"signal\":0,\"value\":\"1\"")));
            Assert.Throws<IrValidationException>(() => SchedulerFixture.Parse(FixtureJson.Replace("\"signal_count\":4", "\"signal_count\":4,\"unexpected\":1")));
        }
        finally { File.Delete(file); }
    }
    [Test] public void SchedulerRegressionChecksNbaMulticlockUnknownEdgesAndThreadDeterminism()
    {
        var nba = SchedulerRegression.Run(SchedulerFixture.Parse(FixtureJson), [1, 2, 4, 8, 16]);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => SchedulerRegression.Oracle(SchedulerFixture.Parse(FixtureJson), canceled.Token));
        Assert.That(nba.All(r => r.Mismatch is null), Is.True); Assert.That(nba.All(r => r.Trace[1].ActivatedProcesses == 2 && r.Trace[1].Values == "10" && r.Trace[3].Values == "01"), Is.True);
        var drives = new List<IReadOnlyDictionary<int, ScalarValue>>
        {
            new Dictionary<int, ScalarValue> { [0] = ScalarValue.Zero, [1] = ScalarValue.One, [2] = ScalarValue.One, [3] = ScalarValue.Zero },
            new Dictionary<int, ScalarValue> { [0] = ScalarValue.One, [1] = ScalarValue.Zero },
            new Dictionary<int, ScalarValue> { [0] = ScalarValue.Zero, [1] = ScalarValue.One },
            new Dictionary<int, ScalarValue> { [0] = ScalarValue.Z, [1] = ScalarValue.X }
        };
        uint state = 0xC0C07B;
        for (int i = 0; i < 500; i++)
        {
            var update = new Dictionary<int, ScalarValue>();
            for (int signal = 0; signal < 4; signal++) { state ^= state << 13; state ^= state >> 17; state ^= state << 5; update.Add(signal, (ScalarValue)(state % 4)); }
            drives.Add(update);
        }
        var fixture = new SchedulerFixture(6, [new(0, 4, 2, 2, IrEdge.Posedge, ScalarOperation.Copy), new(1, 5, 3, 3, IrEdge.Negedge, ScalarOperation.Copy)], drives, [4, 5]);
        var results = SchedulerRegression.Run(fixture, [1, 2, 4, 8, 16]);
        Assert.That(results.All(r => r.Mismatch is null), Is.True);
        Assert.That(results[0].Trace[1].ActivatedProcesses, Is.EqualTo(2)); Assert.That(results[0].Trace[3].ActivatedProcesses, Is.EqualTo(2));
        Assert.That(results[0].Trace[1].Values, Is.EqualTo("10"));
        Assert.That(results.All(r => r.Trace.SequenceEqual(results[0].Trace)), Is.True);
    }
}
