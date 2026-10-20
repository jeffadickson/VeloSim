using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed record WordCheckpoint(int Step, IReadOnlyDictionary<string, FourStateWord> Signals, int? ActivatedProcesses);
public sealed class WordFixture
{
    public IrDesign Design { get; }
    public IReadOnlyList<IReadOnlyDictionary<string, FourStateWord>> Drives { get; }
    public IReadOnlyList<int> Workers { get; }
    public IReadOnlyList<WordCheckpoint> Checkpoints { get; }
    public WordFixture(IrDesign design, IEnumerable<IReadOnlyDictionary<string, FourStateWord>> drives, IEnumerable<int> workers, IEnumerable<WordCheckpoint>? checkpoints = null)
    {
        Design = design ?? throw new ArgumentNullException(nameof(design));
        var raw = drives.Take(ConformanceLimits.Default.MaxSteps + 1).ToArray(); var counts = workers.Take(257).ToArray();
        ConformanceLimits.Default.Validate(design, raw, counts);
        var updates = raw.Select(d => (IReadOnlyDictionary<string, FourStateWord>)new ReadOnlyDictionary<string, FourStateWord>(new Dictionary<string, FourStateWord>(d, StringComparer.Ordinal))).ToArray();
        var points = (checkpoints ?? []).Take(updates.Length + 1).ToArray();
        if (points.Length > updates.Length || points.Any(c => c is null || c.Signals is null || c.Signals.Count > design.Signals.Count)) throw new IrValidationException("Fixture checkpoint budget exceeded.");
        var expected = points.Select(c => c with { Signals = new ReadOnlyDictionary<string, FourStateWord>(new Dictionary<string, FourStateWord>(c.Signals, StringComparer.Ordinal)) }).ToArray();
        var names = design.Signals.ToDictionary(s => s.Name, s => s.Initial.Width, StringComparer.Ordinal);
        if (expected.Select(c => c.Step).Distinct().Count() != expected.Length || expected.Any(c => c.Step < 0 || c.Step >= updates.Length || c.ActivatedProcesses < 0 || c.ActivatedProcesses > design.Processes.Count || c.Signals.Count == 0 && c.ActivatedProcesses is null || c.Signals.Any(s => !names.TryGetValue(s.Key, out var width) || width != s.Value.Width))) throw new IrValidationException("Invalid fixture checkpoint.");
        Drives = Array.AsReadOnly(updates); Workers = Array.AsReadOnly(counts); Checkpoints = Array.AsReadOnly(expected);
    }
    public static WordFixture Parse(string json)
    {
        if (json.Length > ConformanceLimits.MaxFixtureCharacters) throw new IrValidationException("Word fixture exceeds text size limit.");
        try
        {
            using var document = JsonDocument.Parse(json); var root = document.RootElement;
            JsonShape.Fields(root, ["format", "design", "drives", "workers"], ["checkpoints"]);
            var format = root.GetProperty("format").GetString();
            if (format is not ("velosim.word.fixture.v1" or "velosim.word.fixture.v2")) throw new IrValidationException("Unsupported word fixture.");
            var design = IrDesign.Parse(root.GetProperty("design").GetRawText());
            if (format == "velosim.word.fixture.v1" && design.Format != "velosim.ir.v1" || format == "velosim.word.fixture.v2" && design.Format != "velosim.ir.v2") throw new IrValidationException("Word fixture and embedded IR versions differ.");
            IReadOnlyDictionary<string, FourStateWord> Values(JsonElement element)
            {
                if (element.ValueKind != JsonValueKind.Object) throw new IrValidationException("Expected signal drive object.");
                var values = new Dictionary<string, FourStateWord>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    var text = property.Value.GetString();
                    if (text is null || !values.TryAdd(property.Name, FourStateCodec.ParseBinary(text))) throw new IrValidationException("Duplicate or invalid word signal.");
                }
                return values;
            }
            var drives = root.GetProperty("drives").EnumerateArray().Select(Values).ToArray();
            var workers = root.GetProperty("workers").EnumerateArray().Select(v => v.GetInt32()).ToArray();
            var checkpoints = new List<WordCheckpoint>();
            if (root.TryGetProperty("checkpoints", out var points))
                foreach (var point in points.EnumerateArray())
                {
                    JsonShape.Fields(point, ["step", "signals"], ["activated_processes"]);
                    checkpoints.Add(new(point.GetProperty("step").GetInt32(), Values(point.GetProperty("signals")), point.TryGetProperty("activated_processes", out var active) ? active.GetInt32() : null));
                }
            return new(design, drives, workers, checkpoints);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException && ex is not IrValidationException) { throw new IrValidationException("Invalid word fixture: " + ex.Message); }
    }
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            void Values(IReadOnlyDictionary<string, FourStateWord> values) { writer.WriteStartObject(); foreach (var (name, word) in values) writer.WriteString(name, FourStateCodec.FormatBinary(word)); writer.WriteEndObject(); }
            writer.WriteStartObject(); writer.WriteString("format", Design.Format == "velosim.ir.v2" ? "velosim.word.fixture.v2" : "velosim.word.fixture.v1"); writer.WritePropertyName("design"); writer.WriteRawValue(Design.ToJson());
            writer.WriteStartArray("drives"); foreach (var drive in Drives) Values(drive); writer.WriteEndArray(); writer.WriteStartArray("workers"); foreach (var count in Workers) writer.WriteNumberValue(count); writer.WriteEndArray();
            writer.WriteStartArray("checkpoints"); foreach (var checkpoint in Checkpoints)
            {
                writer.WriteStartObject(); writer.WriteNumber("step", checkpoint.Step); writer.WritePropertyName("signals"); Values(checkpoint.Signals); if (checkpoint.ActivatedProcesses is { } active) writer.WriteNumber("activated_processes", active); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
