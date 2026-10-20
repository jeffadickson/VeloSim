using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed record SchedulerConformanceCase(string Name, SchedulerFixture Fixture);
public sealed record SchedulerConformanceResult(string Name, string Status, string ReportPath);
public static class SchedulerConformance
{
    public static IReadOnlyList<SchedulerConformanceCase> Cases()
    {
        var nba = new SchedulerFixture(4, [new(0, 2, 1, 1, IrEdge.Posedge, ScalarOperation.Copy), new(0, 3, 2, 2, IrEdge.Posedge, ScalarOperation.Copy)], [new Dictionary<int, ScalarValue> { [0] = ScalarValue.Zero, [1] = ScalarValue.One, [2] = ScalarValue.Zero, [3] = ScalarValue.Zero }, new Dictionary<int, ScalarValue> { [0] = ScalarValue.One }, new Dictionary<int, ScalarValue> { [0] = ScalarValue.Zero, [1] = ScalarValue.Zero }, new Dictionary<int, ScalarValue> { [0] = ScalarValue.One }], [2, 3]);
        var edges = new SchedulerFixture(4, [new(0, 2, 1, 1, IrEdge.Posedge, ScalarOperation.Copy), new(0, 3, 1, 1, IrEdge.Negedge, ScalarOperation.Copy)], new[] { ScalarValue.Zero, ScalarValue.X, ScalarValue.One, ScalarValue.Z, ScalarValue.Zero, ScalarValue.One }.Select(value => (IReadOnlyDictionary<int, ScalarValue>)new Dictionary<int, ScalarValue> { [0] = value, [1] = ScalarValue.Z }), [2, 3]);
        var drives = new List<IReadOnlyDictionary<int, ScalarValue>>();
        foreach (var a in Enum.GetValues<ScalarValue>()) foreach (var b in Enum.GetValues<ScalarValue>()) { drives.Add(new Dictionary<int, ScalarValue> { [0] = ScalarValue.Zero, [1] = a, [2] = b }); drives.Add(new Dictionary<int, ScalarValue> { [0] = ScalarValue.One }); }
        var gates = new SchedulerFixture(8, Enum.GetValues<ScalarOperation>().Select(operation => new SchedulerProcess(0, 3 + (int)operation, 1, 2, IrEdge.Posedge, operation)), drives, [3, 4, 5, 6, 7]);
        return [new("nba-precommit", nba), new("unknown-edges", edges), new("gate-truth-tables", gates), new("seeded-multiclock", SchedulerStimulus.Generate(0xC0C07B, 500))];
    }
    public static async Task<IReadOnlyList<SchedulerConformanceResult>> RunAsync(IReadOnlyList<int> workers, string outputDirectory, IProcessRunner? runner = null, string? compiler = null, string? runtime = null, CancellationToken cancellationToken = default)
    {
        if (workers.Count == 0 || workers.Distinct().Count() != workers.Count || workers.Any(w => w is < 1 or > 256)) throw new ArgumentException("Invalid conformance worker matrix.");
        var output = Path.GetFullPath(outputDirectory); if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Conformance output must be empty."); Directory.CreateDirectory(output);
        var results = new List<SchedulerConformanceResult>();
        foreach (var test in Cases())
        {
            cancellationToken.ThrowIfCancellationRequested(); var directory = Path.Combine(output, test.Name);
            var report = await new SchedulerDifferential(runner).RunAsync(test.Fixture, workers, directory, compiler, runtime, cancellationToken).ConfigureAwait(false); results.Add(new(test.Name, report.Status, Path.Combine(directory, "scheduler-report.json")));
        }
        using var stream = new MemoryStream(); using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteString("schema", "velosim.scheduler.conformance.v1"); writer.WriteString("status", results.Any(r => r.Status == "fail") ? "fail" : results.Any(r => r.Status != "pass") ? "incomplete" : "pass"); writer.WriteStartArray("cases");
            foreach (var result in results) { writer.WriteStartObject(); writer.WriteString("name", result.Name); writer.WriteString("status", result.Status); writer.WriteString("report_path", result.ReportPath); writer.WriteEndObject(); } writer.WriteEndArray(); writer.WriteEndObject();
        }
        await File.WriteAllTextAsync(Path.Combine(output, "conformance-report.json"), Encoding.UTF8.GetString(stream.ToArray()) + "\n", cancellationToken).ConfigureAwait(false); return results.AsReadOnly();
    }
}
