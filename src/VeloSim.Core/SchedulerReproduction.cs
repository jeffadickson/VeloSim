using System.Text;
using System.Text.Json;

namespace VeloSim;

public static class SchedulerReproduction
{
    public static async Task<string> SavePrefixAsync(SchedulerDifferentialReport report, string outputDirectory, CancellationToken cancellationToken = default)
    {
        if (report.FirstMismatchPrefixSteps is not { } count) throw new ArgumentException("Scheduler report has no mismatch prefix.");
        var output = Path.GetFullPath(outputDirectory); if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Prefix output must be empty."); Directory.CreateDirectory(output);
        var prefix = new SchedulerFixture(report.Fixture.SignalCount, report.Fixture.Processes, report.Fixture.Drives.Take(count), report.Fixture.Observed); var fixturePath = Path.Combine(output, "first-mismatch.fixture.json");
        await File.WriteAllTextAsync(fixturePath, prefix.ToJson(), cancellationToken).ConfigureAwait(false);
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteString("schema", "velosim.scheduler.reproduction.v1"); writer.WriteString("reduction_kind", "first_mismatch_prefix_not_general_delta_debugging"); writer.WriteNumber("prefix_steps", count); writer.WriteString("command", "scheduler-differential");
            writer.WriteStartArray("arguments"); foreach (var argument in new[] { fixturePath, "--output-dir", Path.Combine(output, "rerun"), "--workers", string.Join(',', report.RequestedWorkers) }) writer.WriteStringValue(argument); writer.WriteEndArray();
            writer.WriteString("fixture_sha256", ArtifactMetadata.Fingerprint(fixturePath).Sha256); writer.WriteEndObject();
        }
        await File.WriteAllTextAsync(Path.Combine(output, "reproduction.json"), Encoding.UTF8.GetString(stream.ToArray()) + "\n", cancellationToken).ConfigureAwait(false); return fixturePath;
    }
}
