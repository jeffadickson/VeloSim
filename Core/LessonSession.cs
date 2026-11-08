using System.Globalization;

namespace SchematicLab.Core;

/// <summary>Owns one in-memory document. All commands run serially against this session.</summary>
public sealed class LessonSession
{
    private readonly object gate = new();
    private readonly Guid documentId = Guid.NewGuid();
    private readonly Queue<SweepRun> runs = new();
    private Board board = Board.Divider();
    private long revision;
    public DocumentVersion Version { get { lock (gate) return new(documentId, revision); } }
    public IReadOnlyList<Part> Parts { get { lock (gate) return Array.AsReadOnly(board.Parts.ToArray()); } }
    public IReadOnlyList<Wire> Wires { get { lock (gate) return Array.AsReadOnly(board.Wires.ToArray()); } }
    public double? Probe { get { lock (gate) return board.Output(); } }

    public string? Selected { get; private set; }
    public string DraftValue { get; private set; } = "";
    public DocumentVersion DraftVersion { get; private set; }
    public Pin? PendingPin { get; private set; }
    public DocumentVersion PendingVersion { get; private set; }
    public Kind? Placement { get; private set; }
    public string Filter { get; private set; } = "";
    public bool CatalogOpen { get; private set; }

    public CommandResult Execute(LessonCommand command, CancellationToken cancellation = default)
    {
        lock (gate)
        {
            if (cancellation.IsCancellationRequested) return Result(CommandCode.Cancelled, "Command cancelled.");
            if (command is DocumentCommand edit)
            {
                if (edit.Expected.DocumentId != documentId)
                    return Result(CommandCode.WrongDocument, "The command targets a different document.");
                if (edit.Expected.Revision != revision)
                    return Result(CommandCode.StaleRevision, "The document changed. Reselect the part or retry with the current revision.");
            }

            switch (command)
            {
                case ReadCircuit:
                    return Result(CommandCode.Ok, "Circuit inspected.") with
                    {
                        Circuit = new(Version, Parts, Wires, Probe)
                    };
                case ReadCatalog:
                    return Result(CommandCode.Ok, "Catalog inspected.") with
                    {
                        Catalog = Array.AsReadOnly(Enum.GetValues<Kind>().Select(k => new CatalogEntry(k, PartCatalog.Get(k))).ToArray())
                    };
                case ReadRun query:
                    var retained = runs.FirstOrDefault(run => run.Id == query.RunId);
                    return retained is null ? Result(CommandCode.NotFound, "Run not retained.")
                        : Result(CommandCode.Ok, "Run retrieved.") with { Run = retained };
                case RunSweep:
                    var points = board.Sweep();
                    if (points.Count == 0)
                        return Result(CommandCode.UnsupportedCircuit, "Connect a valid divider to plot the probe voltage.");
                    var run = new SweepRun(Guid.NewGuid(), Version,
                        Array.AsReadOnly(points.Select(p => new SweepSample(p.Input, p.Probe)).ToArray()));
                    if (runs.Count == 16) runs.Dequeue();
                    runs.Enqueue(run);
                    return Result(CommandCode.Ok, "Sweep completed.") with { Run = run };
                case PlacePart place:
                    if (!Enum.IsDefined(place.Kind) || place.X is < 0 or > 640 || place.Y is < 0 or > 370 ||
                        board.Parts.Any(p => Math.Abs(p.X - place.X) < 64 && Math.Abs(p.Y - place.Y) < 55))
                        return Result(CommandCode.InvalidInput, "Choose an unoccupied position on the sheet.");
                    if (board.Parts.Count >= 64) return Result(CommandCode.LimitReached, "The sheet allows 64 parts.");
                    var part = board.Place(place.Kind, place.X, place.Y);
                    revision++;
                    Select(part);
                    return Result(CommandCode.Ok, $"Placed {part.Id}.") with { Part = part };
                case SetPartValue change:
                    if (!board.SetValue(change.PartId, change.Value))
                        return Result(CommandCode.InvalidInput, "Select an editable part and enter a finite value greater than zero.");
                    revision++;
                    if (Selected == change.PartId) DraftVersion = Version;
                    return Result(CommandCode.Ok, $"Updated {change.PartId}.");
                case DeletePart delete:
                    if (!board.Remove(delete.PartId)) return Result(CommandCode.NotFound, "Part not found.");
                    if (Selected == delete.PartId) ClearSelection();
                    ClearGesture();
                    revision++;
                    return Result(CommandCode.Ok, $"Deleted {delete.PartId} and its incident wires.");
                case ConnectPins connect:
                    ClearGesture();
                    if (!board.Connect(connect.First, connect.Second))
                        return Result(CommandCode.InvalidInput, "Choose two valid terminals on different nets.");
                    revision++;
                    return Result(CommandCode.Ok, "Wire added.");
                case ResetLesson:
                    board = Board.Divider();
                    ClearSelection();
                    ClearGesture();
                    revision++;
                    return Result(CommandCode.Ok, "Example restored.");
                case SelectPart select:
                    var target = board.Parts.FirstOrDefault(p => p.Id == select.PartId);
                    if (target is null) return Result(CommandCode.NotFound, "Part not found.");
                    Select(target);
                    return Result(CommandCode.Ok, $"Selected {target.Id}.");
                case ArmPlacement arm:
                    if (!Enum.IsDefined(arm.Kind)) return Result(CommandCode.InvalidInput, "Unknown part type.");
                    ClearGesture();
                    Placement = arm.Kind;
                    return Result(CommandCode.Ok, $"Choose a placement point for {arm.Kind}.");
                case ArmPin arm:
                    if (!board.Connected(arm.Pin, arm.Pin))
                        return Result(CommandCode.InvalidInput, "Choose a valid terminal.");
                    ClearGesture();
                    PendingPin = arm.Pin;
                    PendingVersion = Version;
                    return Result(CommandCode.Ok, "Choose a second terminal.");
                case CancelGesture:
                    ClearGesture();
                    return Result(CommandCode.Ok, "Tool cancelled.");
                case SetDraftValue draft when draft.Value is { Length: <= 128 }:
                    DraftValue = draft.Value;
                    return Result(CommandCode.Ok, "Draft updated.");
                case SetCatalogFilter filter when filter.Value is { Length: <= 128 }:
                    Filter = filter.Value;
                    return Result(CommandCode.Ok, "Catalog filtered.");
                case ToggleCatalog:
                    CatalogOpen = !CatalogOpen;
                    return Result(CommandCode.Ok, "Catalog visibility changed.");
                default:
                    return Result(CommandCode.InvalidInput, "Invalid command or argument.");
            }
        }
    }

    private CommandResult Result(CommandCode code, string message) => new(code, Version, message);

    private void Select(Part part)
    {
        Selected = part.Id;
        DraftValue = part.Value.ToString(CultureInfo.InvariantCulture);
        DraftVersion = Version;
        ClearGesture();
    }

    private void ClearSelection()
    {
        Selected = null;
        DraftValue = "";
    }

    private void ClearGesture()
    {
        PendingPin = null;
        Placement = null;
    }
}
