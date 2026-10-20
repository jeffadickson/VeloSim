using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed record ElaborationMatrixCase(string Name, string Top, IReadOnlyList<string> Sources);
public sealed class ElaborationMatrixPlan
{
    public string Name { get; }
    public IReadOnlyList<ElaborationMatrixCase> Cases { get; }
    public IReadOnlyList<int> Workers { get; }
    public int RunCount => Cases.Count * Workers.Count;
    public ElaborationMatrixPlan(string name, IEnumerable<ElaborationMatrixCase> cases, IEnumerable<int> workers)
    {
        var entries = cases.Take(65).Select(c => c with { Sources = Array.AsReadOnly(c.Sources.Select(Path.GetFullPath).ToArray()) }).ToArray(); var counts = workers.Take(257).ToArray();
        if (string.IsNullOrWhiteSpace(name) || entries.Length is < 1 or > 64 || counts.Length is < 1 or > 256 || (long)entries.Length * counts.Length > 1024 || counts.Distinct().Count() != counts.Length || counts.Any(w => w is < 1 or > 256)) throw new IrValidationException("Invalid elaboration matrix dimensions.");
        if (entries.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length || entries.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Name.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '-')) || c.Sources.Count == 0 || c.Sources.Distinct(StringComparer.Ordinal).Count() != c.Sources.Count)) throw new IrValidationException("Invalid matrix case name or sources.");
        foreach (var entry in entries) CounterTestbench.Generate(entry.Top, 8, [0]);
        Name = name; Cases = Array.AsReadOnly(entries); Workers = Array.AsReadOnly(counts);
    }
    public static ElaborationMatrixPlan Default(string root, IReadOnlyList<int>? workers = null) => new("surelog-fixtures", [new("counter", "slang_counter", [Path.Combine(root, "examples", "slang_counter.sv")]), new("fifo", "surelog_fifo", [Path.Combine(root, "examples", "surelog_fifo.sv")]), new("multiclock", "multiclock", [Path.Combine(root, "examples", "multiclock.sv")])], workers ?? [1, 2, 4, 8]);
    public static ElaborationMatrixPlan Parse(string json, string baseDirectory)
    {
        try
        {
            using var document = JsonDocument.Parse(json); var root = document.RootElement; JsonShape.Fields(root, ["format", "name", "cases", "workers"]);
            if (root.GetProperty("format").GetString() != "velosim.elaboration.matrix.v1") throw new IrValidationException("Unsupported elaboration matrix plan.");
            var cases = new List<ElaborationMatrixCase>();
            foreach (var entry in root.GetProperty("cases").EnumerateArray())
            {
                JsonShape.Fields(entry, ["name", "top", "sources"]);
                cases.Add(new(entry.GetProperty("name").GetString()!, entry.GetProperty("top").GetString()!, entry.GetProperty("sources").EnumerateArray().Select(s => Path.GetFullPath(s.GetString()!, baseDirectory)).ToArray()));
            }
            return new(root.GetProperty("name").GetString()!, cases, root.GetProperty("workers").EnumerateArray().Select(w => w.GetInt32()));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException && ex is not IrValidationException) { throw new IrValidationException("Invalid matrix plan: " + ex.Message); }
    }
    public string ToJson()
    {
        using var stream = new MemoryStream(); using (var w = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            w.WriteStartObject(); w.WriteString("format", "velosim.elaboration.matrix.v1"); w.WriteString("name", Name); w.WriteStartArray("cases");
            foreach (var c in Cases) { w.WriteStartObject(); w.WriteString("name", c.Name); w.WriteString("top", c.Top); w.WriteStartArray("sources"); foreach (var source in c.Sources) w.WriteStringValue(source); w.WriteEndArray(); w.WriteEndObject(); }
            w.WriteEndArray(); w.WriteStartArray("workers"); foreach (var count in Workers) w.WriteNumberValue(count); w.WriteEndArray(); w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
