using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed record RegressionManifest(string Name, string Top, IReadOnlyList<string> Sources, string Clock, string Observed, string? ActiveLowReset, int Width, IReadOnlyList<TraceSample> Expected, IReadOnlyList<int> WorkerCounts)
{
    public static RegressionManifest Parse(string json, string baseDirectory)
    {
        try
        {
            using var doc = JsonDocument.Parse(json); var r = doc.RootElement;
            JsonShape.Fields(r, ["format", "name", "top", "sources", "clock", "observed", "width", "expected", "workers"], ["activeLowReset"]);
            if (r.GetProperty("format").GetString() != "velosim.regression.v1") throw new IrValidationException("Unsupported regression manifest.");
            string S(string name) => r.GetProperty(name).GetString() is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value) ? value : throw new IrValidationException("Required manifest string: " + name);
            var sources = r.GetProperty("sources").EnumerateArray().Select(e => e.GetString() is { Length: > 0 } s ? Path.GetFullPath(s, baseDirectory) : throw new IrValidationException("Invalid source path.")).ToArray();
            var width = r.GetProperty("width").GetInt32();
            var expected = TraceComparison.Parse(S("expected"), width);
            if (expected.Where((e, i) => e.Cycle != i).Any()) throw new IrValidationException("Expected cycles must start at zero and be contiguous.");
            var workers = r.GetProperty("workers").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            if (sources.Length == 0 || sources.Distinct(StringComparer.Ordinal).Count() != sources.Length || workers.Length == 0 || workers.Distinct().Count() != workers.Length || workers.Any(w => w is < 1 or > 256)) throw new IrValidationException("Invalid manifest source or worker list.");
            var reset = r.TryGetProperty("activeLowReset", out _) ? S("activeLowReset") : null;
            return new(S("name"), S("top"), Array.AsReadOnly(sources), S("clock"), S("observed"), reset, width, expected, Array.AsReadOnly(workers));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException && ex is not IrValidationException) { throw new IrValidationException("Invalid regression manifest: " + ex.Message); }
    }
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            w.WriteStartObject(); w.WriteString("format", "velosim.regression.v1"); w.WriteString("name", Name); w.WriteString("top", Top);
            w.WriteStartArray("sources"); foreach (var s in Sources) w.WriteStringValue(s); w.WriteEndArray();
            w.WriteString("clock", Clock); w.WriteString("observed", Observed); if (ActiveLowReset is not null) w.WriteString("activeLowReset", ActiveLowReset);
            w.WriteNumber("width", Width); w.WriteString("expected", string.Join('\n', Expected.Select(e => $"{e.Cycle} {e.Bits}")) + "\n");
            w.WriteStartArray("workers"); foreach (var n in WorkerCounts) w.WriteNumberValue(n); w.WriteEndArray(); w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
