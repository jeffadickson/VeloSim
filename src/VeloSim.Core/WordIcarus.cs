namespace VeloSim;

public sealed class WordIcarus(IProcessRunner? runner = null)
{
    private readonly IProcessRunner runner = runner ?? new ProcessRunner();
    public async Task<IReadOnlyList<WordHdlStep>> RunAsync(WordFixture fixture, string outputDirectory, string? compiler = null, string? runtime = null, CancellationToken cancellationToken = default)
    {
        var source = WordSystemVerilog.Emit(fixture); var directory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("Word reference output must be empty."); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "word.sv"); var executable = Path.Combine(directory, "word.vvp"); await File.WriteAllTextAsync(path, source, cancellationToken).ConfigureAwait(false);
        var compilation = await runner.RunAsync(compiler ?? "iverilog", ["-g2012", "-s", "velosim_word_tb", "-o", executable, path], directory, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false); IcarusReference.RequireSuccess(compilation, "Word Icarus compilation");
        if (!File.Exists(executable)) throw new IOException("Word compiler produced no reference executable.");
        var execution = await runner.RunAsync(runtime ?? "vvp", [executable], directory, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false); IcarusReference.RequireSuccess(execution, "Word Icarus execution"); return WordHdlTrace.Parse(execution.StandardOutput, fixture);
    }
}
