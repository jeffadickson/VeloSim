namespace VeloSim;

public sealed record SchedulerReplayInput(SchedulerFixture Fixture, IReadOnlyList<int> Workers, string Compiler, string Runtime);
public static class SchedulerReplay
{
    public static SchedulerReplayInput Validate(string json)
    {
        var report = SchedulerReportReader.Parse(json);
        if (report.Reference is null || report.RequestedWorkers.Any(w => !report.Native.ContainsKey(w)) || report.Metadata.NativeLibrary is null || report.Toolchain is null) throw new IrValidationException("Replay requires complete backend traces, native library evidence, and toolchain configuration.");
        if (!Path.IsPathRooted(report.Toolchain.Compiler) || !Path.IsPathRooted(report.Toolchain.Runtime)) throw new IrValidationException("Replay toolchain must use effective absolute paths.");
        void Check(FileFingerprint file)
        {
            if (ArtifactMetadata.Fingerprint(file.Path).Sha256 != file.Sha256) throw new IOException("Scheduler replay artifact changed: " + file.Path);
        }
        foreach (var file in report.Metadata.Sources) Check(file); foreach (var file in report.Metadata.ToolBinaries ?? []) Check(file); Check(report.Metadata.NativeLibrary);
        if (ArtifactMetadata.Fingerprint(ScalarScheduler.NativeLibraryPath).Sha256 != report.Metadata.NativeLibrary.Sha256) throw new IOException("Loaded scheduler library differs from report.");
        var tools = (report.Metadata.ToolBinaries ?? []).ToDictionary(f => f.Path, StringComparer.Ordinal); var sources = report.Metadata.Sources.ToDictionary(f => f.Path, StringComparer.Ordinal);
        string Executable(RecordedCommand command)
        {
            var resolved = ArtifactMetadata.ResolveExecutable(command.Executable, command.WorkingDirectory);
            if (resolved is null || !tools.ContainsKey(resolved)) throw new IOException("Scheduler replay executable resolution changed: " + command.Executable);
            return resolved;
        }
        foreach (var command in report.Metadata.Commands.Where(c => c.ExitCode.HasValue)) Executable(command);
        var compilations = report.Metadata.Commands.Where(c => c.ExitCode == 0 && c.Arguments.Count == 6 && c.Arguments[0] == "-g2012" && c.Arguments[1] == "-s" && c.Arguments[2] == "velosim_scheduler_tb" && c.Arguments[3] == "-o").ToArray();
        if (compilations.Length != 1) throw new IrValidationException("Replay requires one unambiguous successful scheduler compilation stage.");
        var compilation = compilations[0]; var compiler = Executable(compilation);
        if (compiler != report.Toolchain.Compiler || !tools.ContainsKey(report.Toolchain.Runtime)) throw new IrValidationException("Compiler role differs from configured fingerprinted toolchain.");
        var source = Path.GetFullPath(compilation.Arguments[5], compilation.WorkingDirectory); var artifact = Path.GetFullPath(compilation.Arguments[4], compilation.WorkingDirectory);
        if (!sources.ContainsKey(source) || !sources.ContainsKey(artifact)) throw new IrValidationException("Replay lacks generated source or compiled reference fingerprint.");
        if (File.ReadAllText(source) != SchedulerSystemVerilog.Emit(report.Fixture)) throw new IrValidationException("Generated reference source differs from fixture or replay implementation.");
        var fixtureFiles = report.Metadata.Sources.Where(f => Path.GetFileName(f.Path) == "fixture.json").ToArray();
        if (fixtureFiles.Length != 1 || SchedulerFixture.Parse(File.ReadAllText(fixtureFiles[0].Path)).ToJson() != report.Fixture.ToJson()) throw new IrValidationException("Recorded scheduler fixture differs from fingerprinted fixture artifact.");
        var executions = report.Metadata.Commands.Where(c => c.ExitCode == 0 && c.Arguments.Count == 1 && Path.GetFullPath(c.Arguments[0], c.WorkingDirectory) == artifact).ToArray();
        if (executions.Length != 1) throw new IrValidationException("Replay requires one successful runtime stage bound to the compiled reference artifact.");
        var execution = executions[0]; var runtime = Executable(execution);
        if (runtime != report.Toolchain.Runtime) throw new IrValidationException("Runtime role differs from configured fingerprinted toolchain.");
        if (!SchedulerTraceParser.Parse(execution.StandardOutput, report.Fixture).SequenceEqual(report.Reference)) throw new IrValidationException("Reference trace differs from recorded runtime output.");
        return new(report.Fixture, report.RequestedWorkers.ToArray(), compiler, runtime);
    }
    public static Task<SchedulerDifferentialReport> RunAsync(SchedulerReplayInput input, string outputDirectory, IProcessRunner? runner = null, CancellationToken cancellationToken = default) => new SchedulerDifferential(runner).RunAsync(input.Fixture, input.Workers, outputDirectory, input.Compiler, input.Runtime, cancellationToken);
}
