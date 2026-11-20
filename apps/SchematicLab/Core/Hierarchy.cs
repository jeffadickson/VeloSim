namespace SchematicLab.Core;

/// <summary>Saved child topology and the interface part IDs bound to the cell's left and right pins.</summary>
public sealed record ChildSheet(string Document, string LeftPort, string RightPort);
public sealed record AttachChildSheet(DocumentVersion Expected, string CellId, ChildSheet Child) : DocumentCommand(Expected);
public sealed record ReadChildSheet(string CellId) : LessonCommand;
public sealed record LoadHierarchyDemo(DocumentVersion Expected) : DocumentCommand(Expected);

public sealed partial class Board
{
    private const int MaximumChildDepth = 3;
    private const int MaximumChildren = 8;
    private readonly Dictionary<string, (ChildSheet Definition, Board Topology)> childSheets = [];

    internal ChildSheet? ChildOf(string? id) => id is null ? null : childSheets.GetValueOrDefault(id).Definition;
    internal bool CanDuplicateChild(string id) => !childSheets.ContainsKey(id) || childSheets.Count < MaximumChildren;

    internal bool AttachChild(string id, ChildSheet? definition, int depth = 0)
    {
        if (depth >= MaximumChildDepth || definition is not
            { Document.Length: > 0 and <= 32768, LeftPort.Length: > 0 and <= 12, RightPort.Length: > 0 and <= 12 } ||
            definition.LeftPort == definition.RightPort || !parts.Any(p => p.Id == id && p.Kind == Kind.CellInstance) ||
            (childSheets.Count == MaximumChildren && !childSheets.ContainsKey(id))) return false;

        var child = Load(definition.Document, out _, depth + 1);
        if (child is null || child.parts.Any(p => p.Kind is not
                (Kind.Resistor or Kind.Capacitor or Kind.Inductor or Kind.Ground or Kind.InterfacePort or Kind.CellInstance)) ||
            child.parts.Count(p => p.Kind == Kind.InterfacePort) != 2 ||
            child.parts.Any(p => p.Kind == Kind.CellInstance && !child.childSheets.ContainsKey(p.Id)) ||
            !ConnectedPort(definition.LeftPort) || !ConnectedPort(definition.RightPort)) return false;

        childSheets[id] = (definition, child);
        return true;

        bool ConnectedPort(string port) => child.parts.Any(p => p.Id == port && p.Kind == Kind.InterfacePort) &&
            child.TraceNet(new(port, 0))!.Terminals.Count > 1;
    }

    // Take electrical nets from each board independently. Coordinates and identical names in
    // different instances never create connectivity. Virtual terminals only join those nets.
    private Board? FlattenHierarchy()
    {
        var graph = new Board();
        if (!Append(this, "")) return null;

        var flattened = new Board();
        flattened.parts.AddRange(graph.parts.Where(p => p.Kind is not (Kind.InterfacePort or Kind.CellInstance)));
        var physicalIds = flattened.parts.Select(p => p.Id).ToHashSet();
        var visited = new HashSet<Pin>();
        foreach (var part in flattened.parts)
        for (var i = 0; i < part.PinCount; i++)
        {
            var pin = new Pin(part.Id, i);
            if (visited.Contains(pin)) continue;
            var net = graph.TraceNet(pin)!.Terminals;
            visited.UnionWith(net);
            foreach (var terminal in net.Where(p => p != pin && physicalIds.Contains(p.Part)))
                flattened.wires.Add(new(pin, terminal));
        }
        // The four-terminal SOI profile also rejects a label on the unused body pin.
        flattened.labels.AddRange(labels.Where(l => parts.Any(p =>
            p.Id == l.Terminal.Part && p.Kind == Kind.NmosSoi && l.Terminal.Index == 4)));
        return flattened;

        bool Append(Board source, string prefix)
        {
            if (graph.parts.Count + source.parts.Count > 128) return false;
            graph.parts.AddRange(source.parts.Select(p => p with { Id = prefix + p.Id }));
            Pin Scoped(Pin pin) => new(prefix + pin.Part, pin.Index);
            var seen = new HashSet<Pin>();
            foreach (var part in source.parts)
            for (var i = 0; i < part.PinCount; i++)
            {
                var pin = new Pin(part.Id, i);
                if (seen.Contains(pin)) continue;
                var net = source.TraceNet(pin)!.Terminals;
                seen.UnionWith(net);
                foreach (var terminal in net.Where(p => p != pin))
                    graph.wires.Add(new(Scoped(pin), Scoped(terminal)));
            }

            foreach (var instance in source.parts.Where(p => p.Kind == Kind.CellInstance))
            {
                if (!source.childSheets.TryGetValue(instance.Id, out var entry) ||
                    source.TraceNet(new(instance.Id, 0))!.Terminals.Count == 1 ||
                    source.TraceNet(new(instance.Id, 1))!.Terminals.Count == 1) return false;
                var path = prefix + instance.Id + "_";
                if (!Append(entry.Topology, path)) return false;
                graph.wires.Add(new(new(prefix + instance.Id, 0), new(path + entry.Definition.LeftPort, 0)));
                graph.wires.Add(new(new(prefix + instance.Id, 1), new(path + entry.Definition.RightPort, 0)));
            }
            return true;
        }
    }

    internal static Board HierarchyDemo(SavedSession session)
    {
        var leaf = new Board();
        var left = leaf.Place(Kind.InterfacePort, 90, 120);
        var right = leaf.Place(Kind.InterfacePort, 410, 120);
        var resistor = leaf.Place(Kind.Resistor, 250, 120);
        leaf.Connect(new(left.Id, 0), new(resistor.Id, 0));
        leaf.Connect(new(right.Id, 0), new(resistor.Id, 1));

        var middle = new Board();
        var input = middle.Place(Kind.InterfacePort, 90, 120);
        var output = middle.Place(Kind.InterfacePort, 410, 120);
        var inner = middle.Place(Kind.CellInstance, 250, 120);
        middle.AttachChild(inner.Id, new(leaf.Save(session), left.Id, right.Id));
        middle.Connect(new(input.Id, 0), new(inner.Id, 0));
        middle.Connect(new(output.Id, 0), new(inner.Id, 1));

        var parent = Divider();
        parent.Remove("R1");
        parent.SetValue("R2", 3000);
        var cell = parent.Place(Kind.CellInstance, 350, 75);
        parent.AttachChild(cell.Id, new(middle.Save(session), input.Id, output.Id));
        parent.Connect(new("V1", 0), new(cell.Id, 0));
        parent.Connect(new(cell.Id, 1), new("R2", 0));
        parent.Connect(new(cell.Id, 1), new("P1", 0));
        return parent;
    }
}
