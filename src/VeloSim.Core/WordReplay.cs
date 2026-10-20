namespace VeloSim;

public sealed record WordReplayInput(WordFixture Fixture, string Compiler, string Runtime);
public static class WordReplay
{
    public static WordReplayInput Validate(string json, CancellationToken cancellationToken = default)
    {
        var report = WordReportReader.Parse(json, cancellationToken);
        if (report.Reference is null || report.Fixture.Workers.Any(w => !report.Native.ContainsKey(w)) || report.Metadata.NativeLibrary is null) throw new IrValidationException("Word replay requires complete backend traces and native library evidence.");
        if (!Path.IsPathRooted(report.Toolchain.Compiler) || !Path.IsPathRooted(report.Toolchain.Runtime)) throw new IrValidationException("Replay toolchain paths must be absolute.");
        void Check(FileFingerprint file) { cancellationToken.ThrowIfCancellationRequested(); if (ArtifactMetadata.Fingerprint(file.Path).Sha256 != file.Sha256) throw new IOException("Word replay artifact changed: " + file.Path); }
        foreach (var source in report.Metadata.Sources) Check(source); foreach (var tool in report.Metadata.ToolBinaries ?? []) Check(tool); Check(report.Metadata.NativeLibrary);
        if (ArtifactMetadata.Fingerprint(Simulation.NativeLibraryPath).Sha256 != report.Metadata.NativeLibrary.Sha256) throw new IOException("Loaded native IR library differs from word replay evidence.");
        var tools = (report.Metadata.ToolBinaries ?? []).ToDictionary(f => f.Path, StringComparer.Ordinal); var sources = report.Metadata.Sources.ToDictionary(f => f.Path, StringComparer.Ordinal);
        string Executable(RecordedCommand command)
        {
            var resolved = ArtifactMetadata.ResolveExecutable(command.Executable, command.WorkingDirectory);
            if (resolved is null || !tools.ContainsKey(resolved)) throw new IOException("Word replay executable resolution changed: " + command.Executable); return resolved;
        }
        foreach (var command in report.Metadata.Commands.Where(c => c.ExitCode.HasValue)) Executable(command);
        var compilations = report.Metadata.Commands.Where(c => c.ExitCode == 0 && c.Arguments.Count == 6 && c.Arguments[0] == "-g2012" && c.Arguments[1] == "-s" && c.Arguments[2] == "velosim_word_tb" && c.Arguments[3] == "-o").ToArray();
        if (compilations.Length != 1) throw new IrValidationException("Word replay requires one successful compilation stage."); var compilation = compilations[0];
        if (Executable(compilation) != report.Toolchain.Compiler || !tools.ContainsKey(report.Toolchain.Runtime)) throw new IrValidationException("Word compiler/runtime role does not match configured fingerprinted tools.");
        var sourcePath = Path.GetFullPath(compilation.Arguments[5], compilation.WorkingDirectory); var compiledPath = Path.GetFullPath(compilation.Arguments[4], compilation.WorkingDirectory);
        if (!sources.ContainsKey(sourcePath) || !sources.ContainsKey(compiledPath)) throw new IrValidationException("Word replay lacks generated source or compiled reference fingerprint.");
        if (File.ReadAllText(sourcePath) != WordSystemVerilog.Emit(report.Fixture)) throw new IrValidationException("Word emitted reference source differs from fixture or replay implementation.");
        var fixtures = report.Metadata.Sources.Where(f => Path.GetFileName(f.Path) == "fixture.json").ToArray(); var designs = report.Metadata.Sources.Where(f => Path.GetFileName(f.Path) == "design.ir.json").ToArray();
        if (fixtures.Length != 1 || designs.Length != 1 || WordFixture.Parse(File.ReadAllText(fixtures[0].Path)).ToJson() != report.Fixture.ToJson() || IrDesign.Parse(File.ReadAllText(designs[0].Path)).ToJson() != report.Fixture.Design.ToJson()) throw new IrValidationException("Word fixture or IR differs from its fingerprinted artifact.");
        var executions = report.Metadata.Commands.Where(c => c.ExitCode == 0 && c.Arguments.Count == 1 && Path.GetFullPath(c.Arguments[0], c.WorkingDirectory) == compiledPath).ToArray();
        if (executions.Length != 1 || Executable(executions[0]) != report.Toolchain.Runtime) throw new IrValidationException("Word runtime role is not bound to the compiled reference artifact.");
        if (!WordReportReader.TraceEqual(WordHdlTrace.Parse(executions[0].StandardOutput, report.Fixture), report.Reference)) throw new IrValidationException("Word reference trace differs from recorded runtime output.");
        return new(report.Fixture, report.Toolchain.Compiler, report.Toolchain.Runtime);
    }
    public static Task<WordDifferentialReport> RunAsync(WordReplayInput input, string outputDirectory, IProcessRunner? runner = null, CancellationToken cancellationToken = default) => new WordDifferential(runner).RunAsync(input.Fixture, outputDirectory, input.Compiler, input.Runtime, cancellationToken);
}
