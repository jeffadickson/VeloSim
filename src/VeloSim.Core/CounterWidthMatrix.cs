using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed record CounterWidthCase(string Name, IReadOnlyList<string> Sources, string Top, int ExpectedWidth);
public sealed record CounterWidthResult(string Name, int Workers, int ExpectedWidth, int? ActualWidth, string Status, string ReportPath);
public sealed class CounterWidthMatrix(IProcessRunner? runner = null)
{
    public async Task<IReadOnlyList<CounterWidthResult>> RunAsync(IReadOnlyList<CounterWidthCase> cases, IReadOnlyList<int> workers, string outputDirectory, int cycles = 100, int buildJobs = 1, IReadOnlyList<string>? engines = null, string? surelog = null, string? normalizer = null, CancellationToken cancellationToken = default)
    {
        if (cases.Count is < 1 or > 64 || workers.Count == 0 || cases.Count * workers.Count > 256 || workers.Distinct().Count() != workers.Count || workers.Any(w => w is < 1 or > 256) || buildJobs is < 1 or > 256 || cycles is < 1 or > 1_000_000) throw new ArgumentException("Invalid width matrix resource dimensions.");
        if (cases.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != cases.Count || cases.Any(c => c.ExpectedWidth is < 1 or > 64 || string.IsNullOrWhiteSpace(c.Name) || c.Name.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '-')) || c.Sources.Count == 0)) throw new ArgumentException("Invalid width matrix cases.");
        var output = Path.GetFullPath(outputDirectory); if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Width matrix output must be empty."); Directory.CreateDirectory(output);
        var results = new List<CounterWidthResult>();
        foreach (var test in cases) foreach (var count in workers)
        {
            cancellationToken.ThrowIfCancellationRequested(); var destination = Path.Combine(output, test.Name, "workers-" + count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var report = await new CounterDifferential(runner).RunAsync(test.Sources, test.Top, destination, cycles: cycles, workers: count, buildJobs: buildJobs, engines: engines, surelog: surelog, normalizer: normalizer, cancellationToken: cancellationToken).ConfigureAwait(false);
            results.Add(new(test.Name, count, test.ExpectedWidth, report.ObservedWidth, report.Status == "pass" && report.ObservedWidth == test.ExpectedWidth ? "pass" : "fail", Path.Combine(destination, "report.json")));
        }
        using var stream = new MemoryStream(); using (var w = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            w.WriteStartObject(); w.WriteString("schema", "velosim.counter.width.matrix.v1"); w.WriteString("status", results.All(r => r.Status == "pass") ? "pass" : "fail"); w.WriteStartArray("results");
            foreach (var result in results) { w.WriteStartObject(); w.WriteString("case", result.Name); w.WriteNumber("workers", result.Workers); w.WriteNumber("expected_width", result.ExpectedWidth); if (result.ActualWidth is { } width) w.WriteNumber("actual_width", width); else w.WriteNull("actual_width"); w.WriteString("status", result.Status); w.WriteString("report_path", result.ReportPath); w.WriteEndObject(); }
            w.WriteEndArray(); w.WriteEndObject();
        }
        await File.WriteAllTextAsync(Path.Combine(output, "width-matrix.json"), Encoding.UTF8.GetString(stream.ToArray()) + "\n", cancellationToken).ConfigureAwait(false); return results.AsReadOnly();
    }
}
