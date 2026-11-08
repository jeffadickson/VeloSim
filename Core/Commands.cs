namespace SchematicLab.Core;

/// <summary>Identifies the exact document state on which an edit or analysis is based.</summary>
public readonly record struct DocumentVersion(Guid DocumentId, long Revision);

public enum CommandCode { Ok, InvalidInput, RequiredInput, WrongDocument, StaleRevision, Cancelled, NotFound, LimitReached, UnsupportedCircuit, SolverFailed }

/// <summary>The command type identifies its stable action. The version is mandatory for document changes.</summary>
public abstract record LessonCommand;
public abstract record DocumentCommand(DocumentVersion Expected) : LessonCommand;
public sealed record PlacePart(DocumentVersion Expected, Kind Kind, int X, int Y) : DocumentCommand(Expected);
public sealed record SetPartValue(DocumentVersion Expected, string PartId, double Value) : DocumentCommand(Expected);
public sealed record DeletePart(DocumentVersion Expected, string PartId) : DocumentCommand(Expected);
public sealed record ConnectPins(DocumentVersion Expected, Pin First, Pin Second) : DocumentCommand(Expected);
public sealed record ResetLesson(DocumentVersion Expected) : DocumentCommand(Expected);
public sealed record RunSweep(DocumentVersion Expected) : DocumentCommand(Expected);
public sealed record ReadCircuit : LessonCommand;
public sealed record ReadCatalog : LessonCommand;
public sealed record ReadRun(Guid RunId) : LessonCommand;
public sealed record SelectPart(string PartId) : LessonCommand;
public sealed record ArmPlacement(Kind Kind) : LessonCommand;
public sealed record ArmPin(Pin Pin) : LessonCommand;
public sealed record CancelGesture : LessonCommand;
public sealed record SetDraftValue(string Value) : LessonCommand;
public sealed record SetCatalogFilter(string Value) : LessonCommand;
public sealed record ToggleCatalog : LessonCommand;

public sealed record CircuitSnapshot(DocumentVersion Version, IReadOnlyList<Part> Parts,
    IReadOnlyList<Wire> Wires, double? Probe);
public sealed record SweepSample(double Input, double Probe);
public sealed record SweepRun(Guid Id, DocumentVersion Version, IReadOnlyList<SweepSample> Samples);
public sealed record CatalogEntry(Kind Kind, PartDefinition Definition);
public sealed record CommandResult(CommandCode Code, DocumentVersion Version, string Message,
    Part? Part = null, CircuitSnapshot? Circuit = null, SweepRun? Run = null,
    IReadOnlyList<CatalogEntry>? Catalog = null, TransientRun? Transient = null);
