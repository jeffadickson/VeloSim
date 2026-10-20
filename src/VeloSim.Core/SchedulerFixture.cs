using System.Text.Json;

namespace VeloSim;

public sealed class SchedulerFixture
{
    public int SignalCount { get; }
    public IReadOnlyList<SchedulerProcess> Processes { get; }
    public IReadOnlyList<IReadOnlyDictionary<int, ScalarValue>> Drives { get; }
    public IReadOnlyList<int> Observed { get; }
    public SchedulerFixture(int signalCount, IEnumerable<SchedulerProcess> processes, IEnumerable<IReadOnlyDictionary<int, ScalarValue>> drives, IEnumerable<int> observed)
    {
        if (signalCount is < 1 or > 1_000_000) throw new IrValidationException("Invalid scheduler signal count.");
        var procs = processes.ToArray(); var updates = drives.Select(d => (IReadOnlyDictionary<int, ScalarValue>)new System.Collections.ObjectModel.ReadOnlyDictionary<int, ScalarValue>(new Dictionary<int, ScalarValue>(d))).ToArray(); var outputs = observed.ToArray();
        bool Invalid(int id) => id < 0 || id >= signalCount;
        if (procs.Any(p => new[] { p.Clock, p.Destination, p.A, p.B }.Any(Invalid) || !Enum.IsDefined(p.Edge) || !Enum.IsDefined(p.Operation)) || procs.Select(p => p.Destination).Distinct().Count() != procs.Length) throw new IrValidationException("Invalid scheduler processes.");
        if (updates.Length is < 1 or > 1_000_000 || updates.Any(d => d.Any(e => Invalid(e.Key) || !Enum.IsDefined(e.Value)))) throw new IrValidationException("Invalid scheduler drive.");
        if (outputs.Length == 0 || outputs.Any(Invalid) || outputs.Distinct().Count() != outputs.Length || (long)updates.Length * outputs.Length > 10_000_000) throw new IrValidationException("Invalid scheduler observations or trace size.");
        SignalCount = signalCount; Processes = Array.AsReadOnly(procs); Drives = Array.AsReadOnly(updates); Observed = Array.AsReadOnly(outputs);
    }
    public static SchedulerFixture Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json); var root = document.RootElement; JsonShape.Fields(root, ["format", "signal_count", "processes", "drives", "observed"]);
            if (root.GetProperty("format").GetString() != "velosim.scheduler.v1") throw new IrValidationException("Unsupported scheduler fixture.");
            var processes = new List<SchedulerProcess>();
            foreach (var p in root.GetProperty("processes").EnumerateArray())
            {
                JsonShape.Fields(p, ["clock", "destination", "a", "b", "edge", "operation"]);
                var edge = p.GetProperty("edge").GetString() switch { "posedge" => IrEdge.Posedge, "negedge" => IrEdge.Negedge, _ => throw new IrValidationException("Invalid scheduler edge.") };
                var operation = p.GetProperty("operation").GetString() switch { "copy" => ScalarOperation.Copy, "and" => ScalarOperation.And, "or" => ScalarOperation.Or, "xor" => ScalarOperation.Xor, "not" => ScalarOperation.Not, _ => throw new IrValidationException("Invalid scheduler operation.") };
                processes.Add(new(p.GetProperty("clock").GetInt32(), p.GetProperty("destination").GetInt32(), p.GetProperty("a").GetInt32(), p.GetProperty("b").GetInt32(), edge, operation));
            }
            var drives = new List<IReadOnlyDictionary<int, ScalarValue>>();
            foreach (var drive in root.GetProperty("drives").EnumerateArray())
            {
                var values = new Dictionary<int, ScalarValue>();
                foreach (var update in drive.EnumerateArray())
                {
                    JsonShape.Fields(update, ["signal", "value"]); var text = update.GetProperty("value").GetString();
                    if (text?.Length != 1 || !values.TryAdd(update.GetProperty("signal").GetInt32(), ScalarLogic.Parse(text[0]))) throw new IrValidationException("Duplicate or invalid scheduler update.");
                }
                drives.Add(values);
            }
            return new(root.GetProperty("signal_count").GetInt32(), processes, drives, root.GetProperty("observed").EnumerateArray().Select(e => e.GetInt32()));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException && ex is not IrValidationException) { throw new IrValidationException("Invalid scheduler fixture: " + ex.Message); }
    }
    public IReadOnlyList<SchedulerStep> Run(int workers, CancellationToken cancellationToken = default)
    {
        if (workers is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(workers));
        cancellationToken.ThrowIfCancellationRequested(); using var scheduler = new ScalarScheduler(SignalCount);
        foreach (var process in Processes) scheduler.AddProcess(process);
        var steps = new List<SchedulerStep>();
        foreach (var drive in Drives)
        {
            cancellationToken.ThrowIfCancellationRequested(); var active = scheduler.Drive(drive, workers);
            steps.Add(new(scheduler.Ticks, active, new string(Observed.Select(id => ScalarLogic.Format(scheduler.Read(id))).ToArray())));
        }
        return steps.AsReadOnly();
    }
    public string ToJson()
    {
        using var stream = new MemoryStream(); using (var w = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            w.WriteStartObject(); w.WriteString("format", "velosim.scheduler.v1"); w.WriteNumber("signal_count", SignalCount); w.WriteStartArray("processes");
            foreach (var p in Processes) { w.WriteStartObject(); w.WriteNumber("clock", p.Clock); w.WriteNumber("destination", p.Destination); w.WriteNumber("a", p.A); w.WriteNumber("b", p.B); w.WriteString("edge", p.Edge == IrEdge.Posedge ? "posedge" : "negedge"); w.WriteString("operation", p.Operation.ToString().ToLowerInvariant()); w.WriteEndObject(); }
            w.WriteEndArray(); w.WriteStartArray("drives"); foreach (var drive in Drives) { w.WriteStartArray(); foreach (var (id, value) in drive) { w.WriteStartObject(); w.WriteNumber("signal", id); w.WriteString("value", ScalarLogic.Format(value).ToString()); w.WriteEndObject(); } w.WriteEndArray(); } w.WriteEndArray();
            w.WriteStartArray("observed"); foreach (var signal in Observed) w.WriteNumberValue(signal); w.WriteEndArray(); w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
public sealed record SchedulerStep(ulong Tick, int ActivatedProcesses, string Values);
