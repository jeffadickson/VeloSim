using System.Globalization;
using System.Text.Json;

namespace SchematicLab.Core;

public sealed partial class Board
{
    private const int MaxDocumentLength = 2 * 1024 * 1024;
    private readonly List<NetLabel> labels = [];
    private readonly List<WireRoute> routes = [];
    private readonly List<Junction> junctions = [];

    public IReadOnlyList<NetLabel> Labels => labels.AsReadOnly();
    public IReadOnlyList<WireRoute> Routes => routes.AsReadOnly();
    public IReadOnlyList<Junction> Junctions => junctions.AsReadOnly();

    internal bool Label(Pin pin, string? name)
    {
        if (!Exists(pin) || (name is not null && !ValidLabel(name))) return false;
        labels.RemoveAll(label => label.Terminal == pin);
        if (name is not null) labels.Add(new(pin, name));
        return true;
    }

    private static bool ValidLabel(string? name) => name is { Length: > 0 and <= 48 } &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    internal bool Route(Wire wire, IReadOnlyList<SheetPoint>? bends)
    {
        if (!wires.Contains(wire) || bends is null || bends.Count > 16 ||
            bends.Any(point => !point.OnSheet) || bends.Distinct().Count() != bends.Count ||
            junctions.Any(j => (j.First == wire || j.Second == wire) && !bends.Contains(j.At)))
            return false;

        routes.RemoveAll(route => route.Wire == wire);
        if (bends.Count > 0) routes.Add(new(wire, Array.AsReadOnly(bends.ToArray())));
        return true;
    }

    internal bool Join(Wire first, Wire second, SheetPoint at) =>
        AddJunction(new(Guid.NewGuid(), first, second, at));

    private bool AddJunction(Junction junction)
    {
        if (junction.Id == Guid.Empty || junction.First == junction.Second || junctions.Count >= 256 ||
            junctions.Any(j => j.Id == junction.Id || (j.At == junction.At &&
                (j.First == junction.First && j.Second == junction.Second ||
                 j.First == junction.Second && j.Second == junction.First))) ||
            !HasBend(junction.First, junction.At) || !HasBend(junction.Second, junction.At))
            return false;

        junctions.Add(junction);
        return true;
    }

    private bool HasBend(Wire wire, SheetPoint point) =>
        routes.Any(route => route.Wire == wire && route.Bends.Contains(point));

    internal bool RemoveJunction(Guid id) => junctions.RemoveAll(j => j.Id == id) != 0;

    internal bool RemoveWire(Wire wire)
    {
        if (!wires.Remove(wire)) return false;
        routes.RemoveAll(route => route.Wire == wire);
        junctions.RemoveAll(j => j.First == wire || j.Second == wire);
        return true;
    }

    // Coordinates have no electrical effect. Each junction supplies an edge between its two wires.
    private IEnumerable<Pin> Neighbors(Pin pin)
    {
        foreach (var wire in wires)
        {
            if (wire.A == pin) yield return wire.B;
            if (wire.B == pin) yield return wire.A;
        }

        var name = labels.Find(label => label.Terminal == pin)?.Name;
        if (name is not null)
            foreach (var label in labels)
                if (label.Terminal != pin && string.Equals(label.Name, name, StringComparison.OrdinalIgnoreCase))
                    yield return label.Terminal;

        foreach (var junction in junctions)
        {
            if (junction.First.A == pin) yield return junction.Second.A;
            if (junction.Second.A == pin) yield return junction.First.A;
        }
    }

    internal NetHighlight? TraceNet(Pin anchor)
    {
        if (!Exists(anchor)) return null;
        var visited = new HashSet<Pin> { anchor };
        var pending = new Queue<Pin>();
        pending.Enqueue(anchor);
        while (pending.TryDequeue(out var pin))
            foreach (var neighbor in Neighbors(pin))
                if (visited.Add(neighbor)) pending.Enqueue(neighbor);
        return new(anchor, Array.AsReadOnly(visited.ToArray()),
            Array.AsReadOnly(wires.Where(wire => visited.Contains(wire.A)).ToArray()),
            Array.AsReadOnly(junctions.Where(j => visited.Contains(j.First.A)).Select(j => j.Id).ToArray()));
    }

    internal string Save(SavedSession session) => JsonSerializer.Serialize(
        new SheetDocument(2, parts.ToArray(), wires.ToArray(), labels.ToArray(),
            routes.ToArray(), junctions.ToArray(), next, session), SheetJson.Default.SheetDocument);

    internal static Board? Load(string? json, out SavedSession? session)
    {
        session = null;
        if (json is null || json.Length > MaxDocumentLength) return null;
        SheetDocument? document;
        try { document = JsonSerializer.Deserialize(json, SheetJson.Default.SheetDocument); }
        catch (JsonException) { return null; }
        if (document is not { SchemaVersion: 1 or 2, Parts.Length: <= 64, Wires.Length: <= 320,
            Labels.Length: <= 320, Routes.Length: <= 320, Junctions.Length: <= 256, Next.Count: <= 32 })
            return null;

        var candidate = new Board();
        foreach (var (prefix, count) in document.Next)
        {
            if (count is < 1 or >= 100_000_000 ||
                !Enum.GetValues<Kind>().Any(kind => PartCatalog.Get(kind).Prefix == prefix))
                return null;
            candidate.next.Add(prefix, count);
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in document.Parts)
        {
            if (part is null || !Enum.IsDefined(part.Kind) || part.Id is not { Length: > 0 and <= 12 } ||
                !ids.Add(part.Id) || !new SheetPoint(part.X, part.Y).OnSheet ||
                part.QuarterTurns is < 0 or > 3 || (!ValidPartValue(part.Kind, part.Value) && !(part.Kind == Kind.Inductor && part.Value == 0)))
                return null;
            var prefix = PartCatalog.Get(part.Kind).Prefix;
            if (!part.Id.StartsWith(prefix, StringComparison.Ordinal) ||
                !int.TryParse(part.Id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
                number <= 0 || number > candidate.next.GetValueOrDefault(prefix) ||
                part.Id != prefix + number.ToString(CultureInfo.InvariantCulture))
                return null;
            candidate.parts.Add(part);
        }

        var seenWires = new HashSet<Wire>();
        foreach (var wire in document.Wires)
        {
            if (!candidate.Exists(wire.A) || !candidate.Exists(wire.B) || wire.A == wire.B ||
                !seenWires.Add(wire) || !seenWires.Add(new(wire.B, wire.A)))
                return null;
            candidate.wires.Add(wire);
        }

        var labeled = new HashSet<Pin>();
        foreach (var label in document.Labels)
            if (label is null || !labeled.Add(label.Terminal) || !ValidLabel(label.Name) ||
                !candidate.Label(label.Terminal, label.Name))
                return null;

        var routed = new HashSet<Wire>();
        foreach (var route in document.Routes)
            if (route is null || route.Bends is not { Count: > 0 } ||
                !routed.Add(route.Wire) || !candidate.Route(route.Wire, route.Bends))
                return null;

        foreach (var junction in document.Junctions)
            if (junction is null || !candidate.AddJunction(junction))
                return null;
        if ((document.SchemaVersion == 2) != (document.Session is not null)) return null;
        session = document.Session;
        return candidate;
    }
}
