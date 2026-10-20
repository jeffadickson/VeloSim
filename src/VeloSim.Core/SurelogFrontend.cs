namespace VeloSim;

public sealed record Elaboration(string UhdmPath, IReadOnlyList<string> Arguments, ProcessResult Result);
public sealed class SurelogFrontend(IProcessRunner? runner = null)
{
    private readonly IProcessRunner runner = runner ?? new ProcessRunner();
    public async Task<Elaboration> ElaborateAsync(IReadOnlyList<string> sources, string outputDirectory, string? top = null, string? executable = null, int workers = 1, IReadOnlyList<string>? includeDirectories = null, IReadOnlyList<string>? defines = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0 || sources.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Source files required.");
        if (workers is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(workers));
        if (defines?.Any(d => string.IsNullOrWhiteSpace(d) || d.Any(char.IsWhiteSpace)) == true) throw new ArgumentException("Invalid macro definition.");
        var output = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(output);
        if (Directory.EnumerateFiles(output, "surelog.uhdm", SearchOption.AllDirectories).Any()) throw new IOException("Output directory contains an existing UHDM artifact.");
        var args = new List<string> { "-parse", "-elabuhdm", "-sverilog", "-mt", workers.ToString(System.Globalization.CultureInfo.InvariantCulture), "-odir", output };
        if (top is not null) { if (string.IsNullOrWhiteSpace(top)) throw new ArgumentException("Top name required."); args.AddRange(["-top", top]); }
        foreach (var d in includeDirectories ?? []) args.Add("+incdir+" + Path.GetFullPath(d));
        foreach (var d in defines ?? []) args.Add("+define+" + d);
        args.AddRange(sources.Select(Path.GetFullPath));
        var result = await runner.RunAsync(executable ?? Environment.GetEnvironmentVariable("VELOSIM_SURELOG") ?? "surelog", args, output, timeout ?? TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Surelog failed ({result.ExitCode}): {result.StandardError}\n{result.StandardOutput}");
        var artifacts = Directory.GetFiles(output, "surelog.uhdm", SearchOption.AllDirectories);
        if (artifacts.Length != 1) throw new IOException($"Expected one UHDM artifact, found {artifacts.Length}.");
        return new(artifacts[0], args.AsReadOnly(), result);
    }
}
