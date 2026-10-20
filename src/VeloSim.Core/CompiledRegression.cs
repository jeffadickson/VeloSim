namespace VeloSim;

public sealed record CompiledRegressionResult(string IrPath, IReadOnlyList<RegressionResult> Results)
{
    public bool Passed => Results.Count > 0 && Results.All(r => r.Mismatch is null);
}
public sealed class CompiledRegression(IProcessRunner? runner = null)
{
    public async Task<CompiledRegressionResult> RunAsync(RegressionManifest manifest, string outputDirectory, string? surelog = null, string? normalizer = null, CancellationToken cancellationToken = default)
    {
        var path = await new DesignPipeline(runner).CompileAsync(manifest.Sources, manifest.Top, outputDirectory, surelog, normalizer, manifest.WorkerCounts.Max(), cancellationToken: cancellationToken).ConfigureAwait(false);
        var design = IrDesign.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        var observed = design.Signals.FirstOrDefault(s => s.Name == manifest.Observed);
        if (observed is null || observed.Initial.Width != manifest.Width) throw new IrValidationException("Observed signal width differs from manifest.");
        var test = new RegressionCase(manifest.Name, design, manifest.Clock, manifest.Observed, manifest.Expected.Count, manifest.Expected, manifest.ActiveLowReset);
        return new(path, RegressionRunner.Run(test, manifest.WorkerCounts, cancellationToken));
    }
}
