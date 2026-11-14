namespace SchematicLab.Core;

/// <summary>Identifies the exact document state on which an edit or analysis is based.</summary>
public readonly record struct DocumentVersion(Guid DocumentId, long Revision);

public enum CommandCode { Ok, InvalidInput, RequiredInput, WrongDocument, StaleRevision, Cancelled, NotFound, LimitReached, UnsupportedCircuit, SolverFailed, LayoutLocked }

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
    IReadOnlyList<Wire> Wires, double? Probe, IReadOnlyList<NetLabel>? Labels = null,
    IReadOnlyList<WireRoute>? Routes = null, IReadOnlyList<Junction>? Junctions = null);
public sealed record SweepSample(double Input, double Probe);
public sealed record SweepRun(Guid Id, DocumentVersion Version, IReadOnlyList<SweepSample> Samples);
public sealed record CatalogEntry(Kind Kind, PartDefinition Definition);
public sealed record CommandResult(CommandCode Code, DocumentVersion Version, string Message,
    Part? Part = null, CircuitSnapshot? Circuit = null, SweepRun? Run = null,
    IReadOnlyList<CatalogEntry>? Catalog = null, TransientRun? Transient = null, IReadOnlyList<CircuitFinding>? Findings = null, string? Document = null, NetHighlight? Net = null, LessonView? View = null, AnalysisHistory? History = null, BridgeRun? Bridge = null, IReadOnlyDictionary<BridgeField, string>? AnalysisFields = null, IReadOnlyList<NamedModel>? Models = null, ChildSheet? Child = null, InteractionState? Interaction = null);

public sealed record MovePart(DocumentVersion Expected, string PartId, int X, int Y) : DocumentCommand(Expected);
public sealed record RotatePart(DocumentVersion Expected, string PartId) : DocumentCommand(Expected);
public sealed record MirrorPart(DocumentVersion Expected, string PartId) : DocumentCommand(Expected);
public sealed record DuplicatePart(DocumentVersion Expected, string PartId, int X, int Y) : DocumentCommand(Expected);
public sealed record UndoEdit(DocumentVersion Expected) : DocumentCommand(Expected);
public sealed record RedoEdit(DocumentVersion Expected) : DocumentCommand(Expected);
public sealed record ArmMove(string PartId, bool Duplicate = false) : LessonCommand;

public sealed record CheckCircuit : LessonCommand;
public sealed record FocusTerminal(Pin Terminal) : LessonCommand;
public enum FindingCode { MissingGround, DanglingTerminal, UnsupportedTopology }
public sealed record CircuitFinding(FindingCode Code, bool Blocking, string Message, Pin? Terminal = null);
