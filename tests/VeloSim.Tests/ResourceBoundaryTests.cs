using NUnit.Framework;
namespace VeloSim.Tests;

public class ResourceBoundaryTests
{
    [Test] public async Task TimeoutKillsDescendantsWhoseParentAlreadyExited()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Linux process-group integration.");
        var file = Path.Combine(Path.GetTempPath(), "velosim-child-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.ThrowsAsync<ProcessTimeoutException>(async () => await new ProcessRunner().RunAsync("/bin/sh", ["-c", "(while :; do sleep 1; done)& echo $! > \"$1\"; exit 0", "test", file], Path.GetTempPath(), TimeSpan.FromMilliseconds(300)));
            int pid = int.Parse(File.ReadAllText(file).Trim());
            for (int i = 0; i < 50; i++)
            {
                string status = File.Exists($"/proc/{pid}/stat") ? File.ReadAllText($"/proc/{pid}/stat") : "";
                if (status.Length == 0 || status.Contains(") Z ", StringComparison.Ordinal)) return;
                await Task.Delay(20);
            }
            Assert.Fail("Orphan descendant remained alive after process-group timeout cleanup.");
        }
        finally { File.Delete(file); }
    }
    [Test] public async Task ProcessOutputExactlyAtLimitIsComplete()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Linux shell integration.");
        var result = await new ProcessRunner(4).RunAsync("/bin/sh", ["-c", "printf abcd; printf efgh >&2"], Path.GetTempPath(), TimeSpan.FromSeconds(5));
        Assert.That(result.StandardOutput, Is.EqualTo("abcd")); Assert.That(result.StandardError, Is.EqualTo("efgh"));
    }
    [Test] public void OutputFloodStopsPromptlyAndRetainsBoundedDiagnostics()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Linux shell integration.");
        var runner = new RecordingProcessRunner(new ProcessRunner(128)); var watch = System.Diagnostics.Stopwatch.StartNew();
        var error = Assert.ThrowsAsync<ProcessOutputLimitException>(async () => await runner.RunAsync("/bin/sh", ["-c", "printf diagnostic >&2; while :; do printf 1234567890; done"], Path.GetTempPath(), TimeSpan.FromSeconds(10)))!;
        Assert.That(watch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5))); Assert.That(error.StandardOutput.Length, Is.EqualTo(128));
        Assert.That(runner.Commands.Single().StandardOutput, Is.EqualTo(error.StandardOutput)); Assert.That(runner.Commands.Single().Failure, Does.Contain("output limit"));
    }
    [Test] public void StderrFloodIsBoundedAndInvalidDeadlinesNeverStartAProcess()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Linux shell integration.");
        var runner = new ProcessRunner(16);
        var error = Assert.ThrowsAsync<ProcessOutputLimitException>(async () => await runner.RunAsync("/bin/sh", ["-c", "while :; do printf error >&2; done"], Path.GetTempPath(), TimeSpan.FromSeconds(10)))!;
        Assert.That(error.StandardError.Length, Is.EqualTo(16));
        Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await runner.RunAsync("/no-such-tool", [], Path.GetTempPath(), TimeSpan.FromDays(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessRunner(0));
        Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(async () => await runner.RunAsync("/no-such-tool", [], Path.GetTempPath(), TimeSpan.FromSeconds(1)));
    }
    [Test] public void SchedulerBudgetsWholeStateAndWorkerMatrix()
    {
        var drives = Enumerable.Range(0, 101).Select(_ => (IReadOnlyDictionary<int, ScalarValue>)new Dictionary<int, ScalarValue>()).ToArray();
        Assert.Throws<IrValidationException>(() => new SchedulerFixture(1_000_000, [], drives, [0]));
        var allowed = new SchedulerFixture(500_000, [], drives, [0]);
        Assert.Throws<IrValidationException>(() => SchedulerRegression.Run(allowed, [1, 2]));
    }
    [Test] public void WordConformanceBudgetsProcessEvaluationSeparately()
    {
        var design = new IrDesign([new("clk", new(1)), new("rst", new(1)), new("q", new(8))], [new("clk", "q", "q", "rst", IrEdge.Posedge, IrOperation.Copy)]);
        IReadOnlyList<IReadOnlyDictionary<string, FourStateWord>> drives = [new Dictionary<string, FourStateWord>(), new Dictionary<string, FourStateWord>()];
        Assert.Throws<ArgumentException>(() => new ConformanceLimits(MaxProcessEvaluations: 1).Validate(design, drives, [1]));
        var oracle = new IrOracle(design); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => oracle.Drive(drives[0], cancellation.Token)); Assert.That(oracle.Ticks, Is.Zero);
    }
    [Test] public void IncludeClosureHandlesCyclesAndRejectsExcessiveDepth()
    {
        var root = Path.Combine(Path.GetTempPath(), "velosim-includes-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.sv"), "`include \"b.sv\"\n"); File.WriteAllText(Path.Combine(root, "b.sv"), "`include \"a.sv\"\n");
            Assert.That(ArtifactMetadata.SourceClosure([Path.Combine(root, "a.sv")]).Count, Is.EqualTo(2));
            for (int i = 0; i < 131; i++) File.WriteAllText(Path.Combine(root, $"s{i}.sv"), i == 130 ? "module last; endmodule" : $"`include \"s{i + 1}.sv\"\n");
            Assert.Throws<IOException>(() => ArtifactMetadata.SourceClosure([Path.Combine(root, "s0.sv")]));
        }
        finally { Directory.Delete(root, true); }
    }
}
