using NUnit.Framework;

namespace VeloSim.Tests;

public class IntegrationTests
{
    private static IrDesign Counter() => new([new("clk", new(1)), new("rst_n", new(1)), new("count", new(8))], [new("clk", "count", "count", "rst_n", IrEdge.Posedge, IrOperation.SvIfResetIncrementStrict)]);
    [Test] public void NativeEngineReadsAndOwnsItsLifetime()
    {
        var sim = new Simulation(Counter());
        Assert.That(sim.Read("count"), Is.EqualTo(new FourStateWord(8)));
        sim.Dispose(); sim.Dispose();
        Assert.Throws<ObjectDisposedException>(() => sim.Read("count"));
    }
    [Test] public void InvalidDriveIsAtomicAndValidDriveActivatesProcess()
    {
        using var sim = new Simulation(Counter());
        Assert.Throws<ArgumentException>(() => sim.Drive(new Dictionary<string, FourStateWord> { ["clk"] = new(1, 1), ["count"] = new(1) }));
        Assert.That(sim.Read("clk").Value, Is.Zero);
        Assert.That(sim.Drive(new Dictionary<string, FourStateWord> { ["rst_n"] = new(1, 1), ["clk"] = new(1, 1) }, 4), Is.EqualTo(1));
        Assert.That(sim.Read("count").Value, Is.EqualTo(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => sim.Drive(new Dictionary<string, FourStateWord>(), 0));
    }
    [Test] public void SimulationCliRunsCounterAndRejectsBadOptions()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, Counter().ToJson());
            var output = new StringWriter(); var error = new StringWriter();
            Assert.That(ToolCli.Run(["simulate", file, "--cycles", "3", "--release-reset"], output, error), Is.Zero, error.ToString());
            Assert.That(output.ToString(), Does.Contain("cycle 3 rst_n=(1,1,0,0) count=(8,2,0,0)"));
            Assert.That(ToolCli.Run(["simulate", file, "--threads", "0"], output, error), Is.EqualTo(1));
            Assert.That(ToolCli.Run(["simulate", file, "--typo"], output, error), Is.EqualTo(1));
        }
        finally { File.Delete(file); }
    }
    [Test] public async Task ProcessRunnerKeepsArgumentsLiteralAndCapturesBothStreams()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Linux toolchain integration test.");
        var runner = new ProcessRunner();
        var result = await runner.RunAsync("/bin/sh", ["-c", "printf '%s' \"$1\"; printf diagnostic >&2; exit 7", "test", "spaces; $(literal)"], Path.GetTempPath(), TimeSpan.FromSeconds(5));
        Assert.That(result.ExitCode, Is.EqualTo(7)); Assert.That(result.StandardOutput, Is.EqualTo("spaces; $(literal)")); Assert.That(result.StandardError, Is.EqualTo("diagnostic"));
        Assert.ThrowsAsync<ProcessTimeoutException>(async () => await runner.RunAsync("/bin/sleep", ["20"], Path.GetTempPath(), TimeSpan.FromMilliseconds(50)));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await runner.RunAsync("/bin/sleep", ["20"], Path.GetTempPath(), TimeSpan.FromSeconds(10), canceled.Token));
        Assert.ThrowsAsync<ProcessTimeoutException>(async () => await runner.RunAsync("/bin/sh", ["-c", "(sleep 1)& exit 0"], Path.GetTempPath(), TimeSpan.FromMilliseconds(50)));
        Assert.ThrowsAsync<OperationCanceledException>(async () => await runner.RunAsync("/nonexistent/no-start", [], Path.GetTempPath(), TimeSpan.FromSeconds(1), canceled.Token));
    }
    private sealed class FakeRunner(Func<string, IReadOnlyList<string>, string, ProcessResult> action) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult(action(executable, arguments, workingDirectory));
    }
    [Test] public async Task SurelogAdapterConstructsArgumentsAndRequiresFreshUniqueArtifact()
    {
        var dir = Path.Combine(Path.GetTempPath(), "velosim-" + Guid.NewGuid().ToString("N"));
        try
        {
            var frontend = new SurelogFrontend(new FakeRunner((exe, args, cwd) =>
            {
                Assert.That(exe, Is.EqualTo("surelog-test")); Assert.That(args, Does.Contain("TOP=1").Or.Contain("+define+TOP=1"));
                Assert.That(args.Last(), Does.EndWith("source with spaces.sv"));
                Directory.CreateDirectory(Path.Combine(cwd, "slpp_all")); File.WriteAllText(Path.Combine(cwd, "slpp_all", "surelog.uhdm"), "database");
                return new(0, "okay", "");
            }));
            var result = await frontend.ElaborateAsync(["source with spaces.sv"], dir, "counter", "surelog-test", 4, defines: ["TOP=1"]);
            Assert.That(File.Exists(result.UhdmPath), Is.True);
            Assert.ThrowsAsync<IOException>(async () => await frontend.ElaborateAsync(["input.sv"], dir));
            Assert.ThrowsAsync<ArgumentException>(async () => await frontend.ElaborateAsync(["input.sv"], dir, defines: ["bad macro"]));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
