namespace VeloSim;

public sealed class DesignPipeline(IProcessRunner? runner = null)
{
    private readonly IProcessRunner runner = runner ?? new ProcessRunner();
    public async Task<string> CompileAsync(IReadOnlyList<string> sources, string top, string outputDirectory, string? surelog = null, string? normalizer = null, int workers = 1, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(top)) throw new ArgumentException("Top name required.");
        var directory = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(directory);
        var normalized = Path.Combine(directory, "normalized.json"); var ir = Path.Combine(directory, "design.ir.json");
        if (File.Exists(normalized) || File.Exists(ir)) throw new IOException("Pipeline output artifacts already exist.");
        var elaboration = await new SurelogFrontend(runner).ElaborateAsync(sources, Path.Combine(directory, "surelog"), top, surelog, workers, timeout: timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        var result = await runner.RunAsync(normalizer ?? Environment.GetEnvironmentVariable("VELOSIM_UHDM_NORMALIZER") ?? "velosim-uhdm-normalize", [elaboration.UhdmPath, normalized, top], directory, timeout ?? TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException($"UHDM normalizer failed ({result.ExitCode}): {result.StandardError}\n{result.StandardOutput}");
        var design = NormalizedUhdm.Lower(await File.ReadAllTextAsync(normalized, cancellationToken).ConfigureAwait(false));
        await File.WriteAllTextAsync(ir, design.ToJson(), cancellationToken).ConfigureAwait(false);
        return ir;
    }
}
