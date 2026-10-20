using System.Text;
using System.Text.Json;

namespace VeloSim;

public static class WordReproduction
{
    public static async Task<string> SavePrefixAsync(WordDifferentialReport report, string outputDirectory, CancellationToken cancellationToken = default)
    {
        if (report.FirstMismatchPrefixSteps is not { } count) throw new ArgumentException("Word report has no mismatch prefix.");
        var output = Path.GetFullPath(outputDirectory); if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Word prefix output must be empty."); Directory.CreateDirectory(output);
        var prefix = new WordFixture(report.Fixture.Design, report.Fixture.Drives.Take(count), report.Fixture.Workers, report.Fixture.Checkpoints.Where(c => c.Step < count)); var path = Path.Combine(output, "first-mismatch.fixture.json");
        await File.WriteAllTextAsync(path, prefix.ToJson(), cancellationToken).ConfigureAwait(false);
        using var stream = new MemoryStream(); using (var w = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            w.WriteStartObject(); w.WriteString("schema", "velosim.word.reproduction.v1"); w.WriteString("reduction_kind", "first_mismatch_prefix_not_general_delta_debugging"); w.WriteNumber("prefix_steps", count); w.WriteString("command", "word-differential");
            w.WriteStartArray("arguments"); foreach (var argument in new[] { path, "--output-dir", Path.Combine(output, "rerun"), "--iverilog", report.Toolchain.Compiler, "--vvp", report.Toolchain.Runtime }) w.WriteStringValue(argument); w.WriteEndArray(); w.WriteString("fixture_sha256", ArtifactMetadata.Fingerprint(path).Sha256); w.WriteEndObject();
        }
        await File.WriteAllTextAsync(Path.Combine(output, "reproduction.json"), Encoding.UTF8.GetString(stream.ToArray()) + "\n", cancellationToken).ConfigureAwait(false); return path;
    }
}
