namespace VeloSim;

public sealed partial class DesignPipeline(IProcessRunner? runner = null)
{
    private readonly bool customRunner = runner is not null;
    private readonly IProcessRunner runner = runner ?? new ProcessRunner();

    internal static bool IsBuiltinUhdmTool(string? tool) =>
        tool is not null && tool.Equals("builtin", StringComparison.OrdinalIgnoreCase);

    public async Task<string> CompileAsync(IReadOnlyList<string> sources, string top, string outputDirectory, string? surelog = null, string? normalizer = null, int workers = 1, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(top)) throw new ArgumentException("Top name required.");
        var directory = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(directory);
        var normalized = Path.Combine(directory, "normalized.json"); var ir = Path.Combine(directory, "design.ir.json");
        if (File.Exists(normalized) || File.Exists(ir)) throw new IOException("Pipeline output artifacts already exist.");
        var elaboration = await new SurelogFrontend(runner).ElaborateAsync(sources, Path.Combine(directory, "surelog"), top, surelog, workers, timeout: timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        string? envNormalizer = Environment.GetEnvironmentVariable("VELOSIM_UHDM_NORMALIZER");
        string effectiveNormalizer = normalizer ?? envNormalizer ?? "velosim-uhdm-normalize";
        bool useBuiltin = IsBuiltinUhdmTool(effectiveNormalizer)
            || (!customRunner && normalizer is null && envNormalizer is null && ArtifactMetadata.ResolveExecutable(effectiveNormalizer, directory) is null);
        if (useBuiltin)
        {
            Uhdm.UhdmNormalizer.NormalizeFileTo(elaboration.UhdmPath, normalized, top);
        }
        else
        {
            var result = await runner.RunAsync(effectiveNormalizer, [elaboration.UhdmPath, normalized, top], directory, timeout ?? TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new InvalidOperationException($"UHDM normalizer failed ({result.ExitCode}): {result.StandardError}\n{result.StandardOutput}");
        }
        var json = await File.ReadAllTextAsync(normalized, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                document.RootElement.TryGetProperty("format", out var format) &&
                format.ValueKind == System.Text.Json.JsonValueKind.String && format.GetString() == "velosim.rtl.v1")
                throw new IrValidationException("Structural velosim.rtl.v1 requires compile-rtl --exporter <velosim-uhdm-rtl>; compile expects legacy normalized UHDM.");
        }
        catch (System.Text.Json.JsonException) { /* Legacy lowerer owns malformed-input diagnostics. */ }
        var design = NormalizedUhdm.Lower(json);
        await File.WriteAllTextAsync(ir, design.ToJson(), cancellationToken).ConfigureAwait(false);
        return ir;
    }
}
