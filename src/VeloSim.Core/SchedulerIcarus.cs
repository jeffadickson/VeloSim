namespace VeloSim;

public sealed record SchedulerIcarusResult(string SourcePath, IReadOnlyList<SchedulerStep> Trace);
public sealed class SchedulerIcarus(IProcessRunner? runner = null)
{
    private readonly IProcessRunner runner = runner ?? new ProcessRunner();
    public async Task<SchedulerIcarusResult> RunAsync(SchedulerFixture fixture, string outputDirectory, string? compiler = null, string? runtime = null, CancellationToken cancellationToken = default)
    {
        var source = SchedulerSystemVerilog.Emit(fixture); var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Scheduler reference output must be empty."); Directory.CreateDirectory(output);
        var path = Path.Combine(output, "scheduler.sv"); var executable = Path.Combine(output, "scheduler.vvp");
        await File.WriteAllTextAsync(path, source, cancellationToken).ConfigureAwait(false);
        var compilation = await runner.RunAsync(compiler ?? "iverilog", ["-g2012", "-s", "velosim_scheduler_tb", "-o", executable, path], output, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false); IcarusReference.RequireSuccess(compilation, "Scheduler Icarus compilation");
        if (!File.Exists(executable)) throw new IOException("Scheduler reference compiler did not produce an executable.");
        var result = await runner.RunAsync(runtime ?? "vvp", [executable], output, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false); IcarusReference.RequireSuccess(result, "Scheduler Icarus execution");
        return new(path, SchedulerTraceParser.Parse(result.StandardOutput, fixture));
    }
}
