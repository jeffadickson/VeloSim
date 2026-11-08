using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchematicLab.Core;

/// <summary>A drawing coordinate in SVG user units.</summary>
public readonly record struct SheetPoint(int X, int Y)
{
    internal bool OnSheet => X is >= 0 and <= 640 && Y is >= 0 and <= 370;
}

public sealed record NetLabel(Pin Terminal, string Name);
public sealed record WireRoute(Wire Wire, IReadOnlyList<SheetPoint> Bends);
public sealed record Junction(Guid Id, Wire First, Wire Second, SheetPoint At);

public sealed record SetNetLabel(DocumentVersion Expected, Pin Terminal, string? Name) : DocumentCommand(Expected);
public sealed record SetWireRoute(DocumentVersion Expected, Wire Wire, IReadOnlyList<SheetPoint> Bends) : DocumentCommand(Expected);
public sealed record DeleteWire(DocumentVersion Expected, Wire Wire) : DocumentCommand(Expected);
public sealed record JoinRoutes(DocumentVersion Expected, Wire First, Wire Second, SheetPoint At) : DocumentCommand(Expected);
public sealed record DeleteJunction(DocumentVersion Expected, Guid JunctionId) : DocumentCommand(Expected);
public sealed record SaveDocument : LessonCommand;
public sealed record LoadDocument(DocumentVersion Expected, string Json) : DocumentCommand(Expected);

/// <summary>Browser form state for wiring tools. Stored separately from document history.</summary>
public sealed record WiringDraft(Pin? Terminal = null, string Name = "", Wire? First = null,
    Wire? Second = null, string Bends = "");
public sealed record UpdateWiringDraft(WiringDraft Draft) : LessonCommand;

internal sealed record SheetDocument(int SchemaVersion, Part[] Parts, Wire[] Wires,
    NetLabel[] Labels, WireRoute[] Routes, Junction[] Junctions, Dictionary<string, int> Next, SavedSession? Session = null);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SheetDocument))]
internal partial class SheetJson : JsonSerializerContext;
