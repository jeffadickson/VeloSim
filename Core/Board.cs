namespace SchematicLab.Core;

/// <summary>Types in the first lesson's electrical catalog.</summary>
public enum Kind
{
    Supply, Resistor, Ground, Probe, Capacitor, Inductor, Current, Diode,
    Npn, Pnp, Nmos, Pmos, Njfet, Pjfet, Vcvs, Vccs, Switch, OpAmp, PulseVoltage
}

/// <summary>Stable electrical endpoint, independent of artwork and screen position.</summary>
public readonly record struct Pin(string Part, int Index);

/// <summary>A part's sheet coordinates use SVG user units. Values are volts or ohms.</summary>
public sealed record Part(string Id, Kind Kind, int X, int Y, double Value, int QuarterTurns = 0, bool Mirrored = false)
{
    public int PinCount => PartCatalog.Get(Kind).Terminals.Count;

    /// <summary>Transforms the drawing anchor without changing the terminal index.</summary>
    public (int X, int Y) PinPosition(int index)
    {
        var terminal = PartCatalog.Get(Kind).Terminals[index];
        var x = Mirrored ? -terminal.X : terminal.X;
        var y = terminal.Y;
        var (dx, dy) = QuarterTurns switch
        {
            1 => (-y, x), 2 => (-x, -y), 3 => (y, -x), _ => (x, y)
        };
        return (X + dx, Y + dy);
    }
}

/// <summary>A wire joins two explicit electrical endpoints.</summary>
public readonly record struct Wire(Pin A, Pin B);

/// <summary>Owns the editable lesson and evaluates its supported electrical topology.</summary>
public sealed partial class Board
{
    private readonly List<Part> parts = [];
    private readonly List<Wire> wires = [];
    private readonly Dictionary<string, int> next = [];

    public IReadOnlyList<Part> Parts => parts.AsReadOnly();
    public IReadOnlyList<Wire> Wires => wires.AsReadOnly();

    public Part Place(Kind kind, int x, int y)
    {
        var definition = PartCatalog.Get(kind);
        var number = next.GetValueOrDefault(definition.Prefix) + 1;
        next[definition.Prefix] = number;
        var part = new Part(definition.Prefix + number, kind, x, y, definition.DefaultValue);
        parts.Add(part);
        return part;
    }

    // Parts and wires are immutable values. Preserve allocator state across history.
    internal Board Copy()
    {
        var copy = new Board();
        copy.parts.AddRange(parts);
        copy.wires.AddRange(wires);
        copy.routes.AddRange(routes);
        copy.labels.AddRange(labels);
        copy.junctions.AddRange(junctions);
        foreach (var entry in next) copy.next.Add(entry.Key, entry.Value);
        return copy;
    }

    internal void Replace(Part part) => parts[parts.FindIndex(p => p.Id == part.Id)] = part;

    internal Part Duplicate(Part original, int x, int y)
    {
        var copy = Place(original.Kind, x, y) with
        {
            Value = original.Value, QuarterTurns = original.QuarterTurns, Mirrored = original.Mirrored
        };
        Replace(copy);
        return copy;
    }

    public bool Remove(string id)
    {
        if (parts.RemoveAll(p => p.Id == id) == 0) return false;
        foreach (var wire in wires.Where(w => w.A.Part == id || w.B.Part == id).ToArray()) RemoveWire(wire);
        labels.RemoveAll(label => label.Terminal.Part == id);
        return true;
    }

    public bool SetValue(string id, double value)
    {
        var index = parts.FindIndex(p => p.Id == id && p.Kind is Kind.Supply or Kind.Resistor or Kind.Capacitor or Kind.PulseVoltage);
        if (index < 0 || !double.IsFinite(value) || value <= 0) return false;
        parts[index] = parts[index] with { Value = value };
        return true;
    }

    public bool Connect(Pin a, Pin b)
    {
        if (wires.Count >= 320 || !Exists(a) || !Exists(b) || Connected(a, b)) return false;
        wires.Add(new(a, b));
        return true;
    }

    public bool Connected(Pin a, Pin b)
    {
        if (!Exists(a) || !Exists(b)) return false;
        var visited = new HashSet<Pin> { a };
        var pending = new Queue<Pin>();
        pending.Enqueue(a);
        while (pending.TryDequeue(out var pin))
        {
            if (pin == b) return true;
            foreach (var neighbor in Neighbors(pin))
                if (visited.Add(neighbor)) pending.Enqueue(neighbor);
        }
        return false;
    }

    internal IReadOnlyList<CircuitFinding> Check()
    {
        var findings = new List<CircuitFinding>();
        if (!parts.Any(part => part.Kind == Kind.Ground))
            findings.Add(new(FindingCode.MissingGround, true, "Place ground and connect the reference terminal."));
        foreach (var part in parts)
        {
            for (var index = 0; index < part.PinCount; index++)
            {
                var pin = new Pin(part.Id, index);
                if (Neighbors(pin).Any()) continue;
                var name = PartCatalog.Get(part.Kind).Terminals[index].Name;
                findings.Add(new(FindingCode.DanglingTerminal, false, $"Connect {part.Id} · {name}.", pin));
            }
        }

        if (FindDivider() is null && !parts.Any(part => part.Kind == Kind.Probe && ChargingInput(part.Id) is not null))
            findings.Add(new(FindingCode.UnsupportedTopology, true, "Complete a supported divider or RC charging circuit."));
        return findings.AsReadOnly();
    }

    private bool Exists(Pin pin) => parts.Any(p => p.Id == pin.Part && pin.Index >= 0 && pin.Index < p.PinCount);

    /// <summary>Returns volts only for an unloaded, electrically complete divider.</summary>
    public double? Output() => FindDivider() is { } divider ? divider.Input * divider.Ratio : null;

    /// <summary>Samples the unloaded divider from zero to the configured supply, in volts.</summary>
    public IReadOnlyList<(double Input, double Probe)> Sweep()
    {
        if (FindDivider() is not { } divider) return [];
        return Enumerable.Range(0, 21).Select(step =>
        {
            var input = divider.Input * (step / 20d);
            return (input, input * divider.Ratio);
        }).ToArray();
    }

    private (double Input, double Ratio)? FindDivider()
    {
        if (parts.Count != 5) return null;
        var supply = parts.FirstOrDefault(p => p.Kind == Kind.Supply);
        var ground = parts.FirstOrDefault(p => p.Kind == Kind.Ground);
        var probe = parts.FirstOrDefault(p => p.Kind == Kind.Probe);
        var resistors = parts.Where(p => p.Kind == Kind.Resistor).ToArray();
        if (supply is null || ground is null || probe is null || resistors.Length != 2) return null;
        var high = new Pin(supply.Id, 0);
        var low = new Pin(ground.Id, 0);
        var output = new Pin(probe.Id, 0);
        if (!Connected(low, new(supply.Id, 1)) || Connected(high, low) ||
            Connected(high, output) || Connected(low, output)) return null;
        foreach (var upper in resistors)
        {
            var lower = resistors.First(p => p != upper);
            if (!Spans(upper, high, output) || !Spans(lower, output, low)) continue;
            // Scaling the ratio avoids overflow when both resistances are large.
            return (supply.Value, 1 / (1 + upper.Value / lower.Value));
        }
        return null;
    }

    private bool Spans(Part part, Pin a, Pin b) =>
        (Connected(new(part.Id, 0), a) && Connected(new(part.Id, 1), b)) ||
        (Connected(new(part.Id, 1), a) && Connected(new(part.Id, 0), b));

    public static Board Divider()
    {
        var board = new Board();
        board.Place(Kind.Supply, 120, 160);
        board.Place(Kind.Resistor, 350, 75);
        board.Place(Kind.Resistor, 350, 215);
        board.Place(Kind.Ground, 350, 315);
        board.Place(Kind.Probe, 525, 145);
        board.Connect(new("V1", 0), new("R1", 0));
        board.Connect(new("R1", 1), new("R2", 0));
        board.Connect(new("R1", 1), new("P1", 0));
        board.Connect(new("R2", 1), new("G1", 0));
        board.Connect(new("V1", 1), new("G1", 0));
        return board;
    }

    /// <summary>Resolve the selected voltage probe from electrical connectivity.</summary>
    internal RcInput? ChargingInput(string probeId)
    {
        if (parts.Count != 5) return null;
        var source = parts.Find(p => p.Kind == Kind.PulseVoltage);
        var resistor = parts.Find(p => p.Kind == Kind.Resistor);
        var capacitor = parts.Find(p => p.Kind == Kind.Capacitor);
        var ground = parts.Find(p => p.Kind == Kind.Ground);
        var probe = parts.Find(p => p.Kind == Kind.Probe && p.Id == probeId);
        if (source is null || resistor is null || capacitor is null || ground is null || probe is null)
            return null;

        var high = new Pin(source.Id, 0);
        var low = new Pin(ground.Id, 0);
        var output = new Pin(probe.Id, 0);
        if (!Connected(low, new(source.Id, 1)) || Connected(high, low) ||
            Connected(high, output) || Connected(low, output) ||
            !Spans(resistor, high, output) || !Spans(capacitor, output, low))
            return null;

        var input = new RcInput(resistor.Value, capacitor.Value, source.Value);
        return input.IsValid ? input : null;
    }

    internal static Board ChargingCircuit()
    {
        var board = new Board();
        board.Place(Kind.PulseVoltage, 120, 160);
        board.Place(Kind.Resistor, 350, 75);
        board.Place(Kind.Capacitor, 350, 215);
        board.Place(Kind.Ground, 350, 315);
        board.Place(Kind.Probe, 525, 145);
        board.Connect(new("V1", 0), new("R1", 0));
        board.Connect(new("R1", 1), new("C1", 0));
        board.Connect(new("R1", 1), new("P1", 0));
        board.Connect(new("C1", 1), new("G1", 0));
        board.Connect(new("V1", 1), new("G1", 0));
        return board;
    }
}
