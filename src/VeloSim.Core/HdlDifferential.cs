namespace VeloSim;

public sealed record HdlDifferentialResult(string SourceMode, string IrPath, IReadOnlyList<TraceSample> Reference, IReadOnlyList<RegressionResult> NativeResults)
{
    public bool Passed => NativeResults.Count > 0 && NativeResults.All(r => r.Mismatch is null);
}
public sealed class HdlDifferential(IProcessRunner? runner = null)
{
    public async Task<HdlDifferentialResult> RunAsync(RegressionManifest design, IReadOnlyList<string> referenceSources, string referenceTop, string outputDirectory, string? surelog = null, string? normalizer = null, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(directory);
        var reference = await new IcarusReference(runner).RunAsync(referenceSources, referenceTop, Path.Combine(directory, "icarus"), design.Expected.Count, design.Width, cancellationToken: cancellationToken).ConfigureAwait(false);
        var manifest = design with { Expected = reference };
        var native = await new CompiledRegression(runner).RunAsync(manifest, Path.Combine(directory, "velosim"), surelog, normalizer, cancellationToken).ConfigureAwait(false);
        return new("surelog_hdl_compilation", native.IrPath, reference, native.Results);
    }
}
