using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed class IrValidationException(string message) : ArgumentException(message);
public sealed record IrSignal(string Name, FourStateWord Initial);
public enum IrEdge : byte { Posedge, Negedge }
public enum IrOperation : byte { Copy, Increment, ResetIncrement, SvIfResetIncrement, SvIncrement, SvIfResetIncrementStrict }
public sealed record IrProcess(string Clock, string Destination, string Source, string Reset, IrEdge Edge, IrOperation Operation);

public sealed class IrDesign
{
    internal static readonly string[] OperationNames = ["copy", "increment", "reset_increment", "sv_if_reset_increment", "sv_increment", "sv_if_reset_increment_strict"];
    public IReadOnlyList<IrSignal> Signals { get; }
    public IReadOnlyList<IrProcess> Processes { get; }

    public IrDesign(IEnumerable<IrSignal> signals, IEnumerable<IrProcess> processes)
    {
        var sigs = signals.ToArray(); var procs = processes.ToArray();
        if (sigs.Length == 0) throw new IrValidationException("At least one signal required.");
        var names = new Dictionary<string, IrSignal>(StringComparer.Ordinal);
        foreach (var s in sigs)
            if (string.IsNullOrEmpty(s.Name) || s.Initial.Width is < 1 or > 64 || !names.TryAdd(s.Name, s))
                throw new IrValidationException("Invalid or duplicate signal.");
        var writers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in procs)
        {
            if (new[] { p.Clock, p.Destination, p.Source, p.Reset }.Any(n => !names.ContainsKey(n)))
                throw new IrValidationException("Unknown process signal.");
            if (names[p.Clock].Initial.Width != 1 || !Enum.IsDefined(p.Edge) || !Enum.IsDefined(p.Operation))
                throw new IrValidationException("Unsupported process edge, clock or operation.");
            if (p.Operation is IrOperation.ResetIncrement or IrOperation.SvIfResetIncrement or IrOperation.SvIfResetIncrementStrict && names[p.Reset].Initial.Width != 1)
                throw new IrValidationException("Reset must be scalar for reset operations.");
            if (!writers.Add(p.Destination)) throw new IrValidationException("Multiple process writers.");
        }
        Signals = Array.AsReadOnly(sigs); Processes = Array.AsReadOnly(procs);
    }

    public static IrDesign Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonShape.Fields(root, ["format", "signals", "processes"]);
            if (root.GetProperty("format").GetString() != "velosim.ir.v1") throw new IrValidationException("Unsupported IR format.");
            var signals = new List<IrSignal>();
            foreach (var s in root.GetProperty("signals").EnumerateArray())
            {
                JsonShape.Fields(s, ["name", "width"], ["value", "xmask", "zmask"]);
                ulong Read(string name) => s.TryGetProperty(name, out var v) ? v.GetUInt64() : 0;
                signals.Add(new(s.GetProperty("name").GetString()!, new(s.GetProperty("width").GetInt32(), Read("value"), Read("xmask"), Read("zmask"))));
            }
            var processes = new List<IrProcess>();
            foreach (var p in root.GetProperty("processes").EnumerateArray())
            {
                JsonShape.Fields(p, ["clock", "destination", "source", "reset", "edge", "operation"]);
                string S(string n) => p.GetProperty(n).GetString()!;
                var edge = S("edge") switch { "posedge" => IrEdge.Posedge, "negedge" => IrEdge.Negedge, _ => throw new IrValidationException("Unknown edge.") };
                var op = Array.IndexOf(OperationNames, S("operation"));
                if (op < 0) throw new IrValidationException("Unknown operation.");
                processes.Add(new(S("clock"), S("destination"), S("source"), S("reset"), edge, (IrOperation)op));
            }
            return new(signals, processes);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException && ex is not IrValidationException)
        { throw new IrValidationException("Malformed IR: " + ex.Message); }
    }

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            w.WriteStartObject(); w.WriteString("format", "velosim.ir.v1"); w.WriteStartArray("signals");
            foreach (var s in Signals)
            {
                w.WriteStartObject(); w.WriteString("name", s.Name); w.WriteNumber("width", s.Initial.Width);
                w.WriteNumber("value", s.Initial.Value); w.WriteNumber("xmask", s.Initial.XMask); w.WriteNumber("zmask", s.Initial.ZMask); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteStartArray("processes");
            foreach (var p in Processes)
            {
                w.WriteStartObject(); w.WriteString("clock", p.Clock); w.WriteString("destination", p.Destination); w.WriteString("source", p.Source); w.WriteString("reset", p.Reset);
                w.WriteString("edge", p.Edge == IrEdge.Posedge ? "posedge" : "negedge"); w.WriteString("operation", OperationNames[(int)p.Operation]); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}

internal static class JsonShape
{
    public static void Fields(JsonElement element, string[] required, string[]? optional = null)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new IrValidationException("Expected JSON object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in element.EnumerateObject())
            if (!names.Add(p.Name) || (!required.Contains(p.Name) && !(optional?.Contains(p.Name) ?? false)))
                throw new IrValidationException("Unexpected or duplicate field: " + p.Name);
        if (required.Any(n => !names.Contains(n))) throw new IrValidationException("Required field missing.");
    }
}
