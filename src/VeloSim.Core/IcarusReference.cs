using System.Globalization;

namespace VeloSim;

public sealed class IcarusReference(IProcessRunner? runner = null)
{
    private readonly IProcessRunner runner = runner ?? new ProcessRunner();
    public async Task<IReadOnlyList<TraceSample>> RunAsync(IReadOnlyList<string> sources, string testbenchTop, string outputDirectory, int cycles, int width, string? compiler = null, string? runtime = null, CancellationToken cancellationToken = default, bool requireWidthHeader = false)
    {
        if (sources.Count == 0 || sources.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(testbenchTop)) throw new ArgumentException("Reference sources and top required.");
        if (cycles < 1 || width is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(cycles));
        var directory = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(directory); var executable = Path.Combine(directory, "simulation.vvp");
        if (File.Exists(executable)) throw new IOException("Reference artifact already exists.");
        var args = new List<string> { "-g2012", "-s", testbenchTop, "-o", executable }; args.AddRange(sources.Select(Path.GetFullPath));
        var compilation = await runner.RunAsync(compiler ?? "iverilog", args, directory, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        RequireSuccess(compilation, "Icarus compilation");
        if (!File.Exists(executable)) throw new IOException("Icarus produced no executable.");
        var execution = await runner.RunAsync(runtime ?? "vvp", [executable], directory, TimeSpan.FromSeconds(90), cancellationToken).ConfigureAwait(false);
        RequireSuccess(execution, "Icarus simulation"); return ParseOutput(execution.StandardOutput, cycles, width, requireWidthHeader);
    }
    public static IReadOnlyList<TraceSample> ParseOutput(string output, int cycles, int width, bool requireWidthHeader = false)
    {
        if (cycles < 1 || width is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(cycles));
        var samples = new List<TraceSample>(); bool sawWidth = false;
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("VS_WIDTH", StringComparison.Ordinal))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (sawWidth || samples.Count > 0 || fields.Length != 2 || fields[0] != "VS_WIDTH" || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var actualWidth) || actualWidth != width) throw new FormatException("Reference width handshake mismatch; expected " + width.ToString(CultureInfo.InvariantCulture) + ".");
                sawWidth = true; continue;
            }
            if (!line.StartsWith("VS_RESULT", StringComparison.Ordinal)) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || parts[0] != "VS_RESULT" || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index != samples.Count || parts[2].Length != width || parts[2].Any(c => c is not ('0' or '1' or 'x' or 'X' or 'z' or 'Z'))) throw new FormatException("Invalid VS_RESULT row: " + line);
            samples.Add(new(index, parts[2].ToLowerInvariant()));
        }
        if (requireWidthHeader && !sawWidth) throw new FormatException("Missing VS_WIDTH reference handshake.");
        if (samples.Count != cycles) throw new FormatException($"Expected {cycles} reference samples, found {samples.Count}.");
        return samples.AsReadOnly();
    }
    internal static void RequireSuccess(ProcessResult result, string stage) { if (result.ExitCode != 0) throw new InvalidOperationException($"{stage} failed ({result.ExitCode}): {result.StandardError}\n{result.StandardOutput}"); }
}
