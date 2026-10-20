namespace VeloSim;

public sealed record HdlRejectionCase(string Name, IReadOnlyList<string> Sources, string Top, string ExpectedDiagnostic);
public sealed record HdlRejectionResult(string Name, bool RejectedAsExpected, string Diagnostic, string OutputDirectory);
public sealed class HdlRejectionRunner(IProcessRunner? runner = null)
{
    public async Task<IReadOnlyList<HdlRejectionResult>> RunAsync(IReadOnlyList<HdlRejectionCase> cases, string outputDirectory, string? surelog = null, string? normalizer = null, CancellationToken cancellationToken = default)
    {
        if (cases.Count is < 1 or > 64 || cases.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != cases.Count || cases.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Name.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '-')) || c.Sources.Count == 0 || string.IsNullOrWhiteSpace(c.ExpectedDiagnostic))) throw new ArgumentException("Invalid HDL rejection cases.");
        var output = Path.GetFullPath(outputDirectory); if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Rejection run output must be empty."); Directory.CreateDirectory(output);
        var results = new List<HdlRejectionResult>();
        foreach (var test in cases)
        {
            cancellationToken.ThrowIfCancellationRequested(); var directory = Path.Combine(output, test.Name);
            try
            {
                await new DesignPipeline(runner).CompileAsync(test.Sources, test.Top, directory, surelog, normalizer, cancellationToken: cancellationToken).ConfigureAwait(false);
                results.Add(new(test.Name, false, "Design unexpectedly accepted.", directory));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
            {
                var semanticRejection = ex is IrValidationException || ex is InvalidOperationException && (ex.Message.StartsWith("Surelog failed (", StringComparison.Ordinal) || ex.Message.StartsWith("UHDM normalizer failed (", StringComparison.Ordinal));
                results.Add(new(test.Name, semanticRejection && ex.Message.Contains(test.ExpectedDiagnostic, StringComparison.OrdinalIgnoreCase), ex.Message, directory));
            }
        }
        return results.AsReadOnly();
    }
}
