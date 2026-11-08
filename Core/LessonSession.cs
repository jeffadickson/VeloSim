using System.Globalization;

namespace SchematicLab.Core;

/// <summary>Owns one in-memory document. All commands run serially against this session.</summary>
public sealed partial class LessonSession
{
    private readonly object gate = new();
    private readonly Guid documentId = Guid.NewGuid();
    private readonly Queue<SweepRun> runs = new();
    private readonly Queue<TransientRun> transients = new();
    private const int HistoryLimit = 32;
    private readonly LinkedList<EditState> undo = new();
    private readonly LinkedList<EditState> redo = new();
    public bool CanUndo { get { lock (gate) return undo.Count > 0; } }
    public bool CanRedo { get { lock (gate) return redo.Count > 0; } }
    private Board board = Board.Divider();
    private long revision;
    public DocumentVersion Version { get { lock (gate) return new(documentId, revision); } }
    public IReadOnlyList<Part> Parts { get { lock (gate) return Array.AsReadOnly(board.Parts.ToArray()); } }
    public IReadOnlyList<Wire> Wires { get { lock (gate) return Array.AsReadOnly(board.Wires.ToArray()); } }
    public double? Probe { get { lock (gate) return board.Output(); } }

    public IReadOnlyList<NetLabel> Labels { get { lock (gate) return Array.AsReadOnly(board.Labels.ToArray()); } }
    public IReadOnlyList<WireRoute> Routes { get { lock (gate) return Array.AsReadOnly(board.Routes.ToArray()); } }
    public IReadOnlyList<Junction> Junctions { get { lock (gate) return Array.AsReadOnly(board.Junctions.ToArray()); } }
    public BridgeDraft BridgeDraft { get; } = new();
    public WiringDraft Wiring { get; private set; } = new();
    public Pin? FocusedTerminal { get; private set; }
    public string? Selected { get; private set; }
    public string DraftValue { get; private set; } = "";
    public DocumentVersion DraftVersion { get; private set; }
    public Pin? PendingPin { get; private set; }
    public DocumentVersion PendingVersion { get; private set; }
    public Kind? Placement { get; private set; }
    public ArmMove? MoveGesture { get; private set; }
    public DocumentVersion MoveVersion { get; private set; }
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

            // Successful edits record the prior board. Failed edits preserve both history stacks.
            var before = command is PlacePart or SetPartValue or DeletePart or ConnectPins or
                LoadRc or LoadBridgeDemo or LoadModelDemo or LoadBjtDemo or LoadBsim3Demo or LoadSoiDemo or ResetLesson or MovePart or RotatePart or MirrorPart or DuplicatePart or
                SetNetLabel or SetWireRoute or DeleteWire or JoinRoutes or DeleteJunction or LoadDocument ? board.Copy() : null;
            switch (command)
            {
                case SaveDocument:
                    return Result(CommandCode.Ok, "Document serialized.") with { Document = board.Save(CaptureSession()) };
                case LoadDocument load:
                    var loaded = Board.Load(load.Json, out var saved);
                    if (loaded is null || (saved is not null && !ValidateSession(loaded, saved)))
                        return Result(CommandCode.InvalidInput, "The saved sheet is invalid or uses an unsupported format.");
                    var previous = CaptureSession();
                    board = loaded;
                    RecordEdit(before!, previous);
                    ApplySession(saved ?? EmptySession);
                    return Result(CommandCode.Ok, "Document reloaded.");
                case SetNetLabel label:
                    if (!board.Label(label.Terminal, label.Name))
                        return Result(CommandCode.InvalidInput, "Choose a valid terminal and a name of up to 48 letters, digits, dots, hyphens or underscores.");
                    RecordEdit(before!);
                    return Result(CommandCode.Ok, "Net label updated.");
                case SetWireRoute route:
                    if (!board.Route(route.Wire, route.Bends))
                        return Result(CommandCode.InvalidInput, "Use up to 16 distinct sheet points and preserve all junction points.");
                    RecordEdit(before!);
                    return Result(CommandCode.Ok, "Wire route updated.");
                case DeleteWire wire:
                    if (!board.RemoveWire(wire.Wire)) return Result(CommandCode.NotFound, "Wire not found.");
                    Wiring = new();
                    RecordEdit(before!);
                    return Result(CommandCode.Ok, "Wire and its junctions removed.");
                case JoinRoutes join:
                    if (!board.Join(join.First, join.Second, join.At))
                        return Result(CommandCode.InvalidInput, "Select two distinct wires with a bend at the junction point.");
                    RecordEdit(before!);
                    return Result(CommandCode.Ok, "Junction added.");
                case DeleteJunction junction:
                    if (!board.RemoveJunction(junction.JunctionId))
                        return Result(CommandCode.NotFound, "Junction not found.");
                    RecordEdit(before!);
                    return Result(CommandCode.Ok, "Junction removed.");
                case UpdateWiringDraft wiring when wiring.Draft is { Name.Length: <= 48, Bends.Length: <= 256 }:
                    Wiring = wiring.Draft;
                    return Result(CommandCode.Ok, "Wiring selection updated.");
                case DefineNamedModel definition:
                    return DefineModel(definition.Model);
                case ApplyNamedModel binding:
                    return BindModel(binding.Name, binding.PartId);
                case ReadNamedModels:
                    return Result(CommandCode.Ok, "Model library read.") with { Models = namedModels.Values.ToArray() };
                case RemoveNamedModel removal:
                    return removal.Name is not null && namedModels.Remove(removal.Name)
                        ? Result(CommandCode.Ok, "Model removed.")
                        : Result(CommandCode.NotFound, "Model not found.");
                case ReadAnalysisFields:
                    return Result(CommandCode.Ok, "Analysis settings read.") with { AnalysisFields = BridgeDraft.Snapshot() };
                case ApplyAnalysisFields settings:
                    return BridgeDraft.Apply(settings.Fields) ? Result(CommandCode.Ok, "Analysis settings applied.")
                        : Result(CommandCode.InvalidInput, "Invalid analysis settings.");
                case SetBridgeDraft draftChange:
                    return BridgeDraft.Set(draftChange.Field, draftChange.Text)
                        ? Result(CommandCode.Ok, "Analysis draft updated.")
                        : Result(CommandCode.InvalidInput, "Invalid analysis draft.");
                case HighlightNet highlight:
                    var net = highlight.Terminal is Pin anchor ? board.TraceNet(anchor) : null;
                    if (highlight.Terminal is not null && net is null)
                        return Result(CommandCode.NotFound, "Terminal not found.");
                    highlightAnchor = highlight.Terminal;
                    return Result(CommandCode.Ok, net is null ? "Net highlight cleared." : "Net highlighted.") with { Net = net };
                case ReadView:
                    return Result(CommandCode.Ok, "View inspected.") with { View = View, Net = HighlightedNet };
                case ReadAnalysisHistory:
                    return Result(CommandCode.Ok, "History inspected.") with { History = CaptureHistory() };
                case CheckCircuit:
                    return Result(CommandCode.Ok, "Circuit checked.") with { Findings = board.Check() };
                case FocusTerminal focus:
                    if (!board.Connected(focus.Terminal, focus.Terminal))
                        return Result(CommandCode.NotFound, "Terminal not found.");
                    Select(FindPart(focus.Terminal.Part)!);
                    FocusedTerminal = focus.Terminal;
                    return Result(CommandCode.Ok, "Terminal highlighted.");
                case ReadCircuit:
                    return Result(CommandCode.Ok, "Circuit inspected.") with
                    {
                        Circuit = new(Version, Parts, Wires, Probe, Labels, Routes, Junctions)
                    };
                case ReadCatalog:
                    return Result(CommandCode.Ok, "Catalog inspected.") with
                    {
                        Catalog = Array.AsReadOnly(Enum.GetValues<Kind>().Select(k => new CatalogEntry(k, PartCatalog.Get(k))).ToArray())
                    };
                case ReadRun query:
                    var transient = transients.FirstOrDefault(run => run.Id == query.RunId);
                    if (transient is not null) return Result(CommandCode.Ok, "Run retrieved.") with { Transient = transient };
                    var retained = runs.FirstOrDefault(run => run.Id == query.RunId);
                    return retained is null ? Result(CommandCode.NotFound, "Run not retained.")
                        : Result(CommandCode.Ok, "Run retrieved.") with { Run = retained };
                case RunSweep:
                    var points = board.Sweep();
                    if (points.Count == 0)
                        return Result(CommandCode.UnsupportedCircuit, "Connect a valid divider to plot the probe voltage.") with { Findings = board.Check() };
                    var run = new SweepRun(Guid.NewGuid(), Version,
                        Array.AsReadOnly(points.Select(p => new SweepSample(p.Input, p.Probe)).ToArray()));
                    if (runs.Count == RunLimit) runs.Dequeue();
                    runs.Enqueue(run);
                    return Result(CommandCode.Ok, "Sweep completed.") with { Run = run };
                case PlacePart place:
                    if (!Enum.IsDefined(place.Kind) || place.X is < 0 or > 640 || place.Y is < 0 or > 370 ||
                        board.Parts.Any(p => Math.Abs(p.X - place.X) < 64 && Math.Abs(p.Y - place.Y) < 55))
                        return Result(CommandCode.InvalidInput, "Choose an unoccupied position on the sheet.");
                    if (board.Parts.Count >= 64) return Result(CommandCode.LimitReached, "The sheet allows 64 parts.");
                    var part = board.Place(place.Kind, place.X, place.Y);
                    RecordEdit(before!);
                    Select(part);
                    return Result(CommandCode.Ok, $"Placed {part.Id}.") with { Part = part };
                case SetPartValue change:
                    if (!board.SetValue(change.PartId, change.Value))
                        return Result(CommandCode.InvalidInput, "Select an editable part and enter a finite value in its allowed range.");
                    RecordEdit(before!);
                    if (Selected == change.PartId) DraftVersion = Version;
                    return Result(CommandCode.Ok, $"Updated {change.PartId}.");
                case DeletePart delete:
                    if (!board.Remove(delete.PartId)) return Result(CommandCode.NotFound, "Part not found.");
                    if (Selected == delete.PartId) ClearSelection();
                    ClearGesture();
                    RecordEdit(before!);
                    return Result(CommandCode.Ok, $"Deleted {delete.PartId} and its incident wires.");
                case ConnectPins connect:
                    ClearGesture();
                    if (!board.Connect(connect.First, connect.Second))
                        return Result(CommandCode.InvalidInput, "Choose two valid terminals on different nets.");
                    RecordEdit(before!);
                    return Result(CommandCode.Ok, "Wire added.");
                case LoadSoiDemo:
                    highlightAnchor = null;
                    board = BridgeDemo.BuildSoi();
                    BridgeDraft.LoadSoi();
                    Wiring = new();
                    ClearSelection();
                    ClearGesture();
                    RecordEdit(before!);
                    return Result(CommandCode.Ok, "Pinned FD-SOI DC example loaded.");
                case LoadBsim3Demo:
                case LoadBjtDemo:
                    var bsim3 = command is LoadBsim3Demo;
                    highlightAnchor = null;
                    board = bsim3 ? BridgeDemo.BuildBsim3() : BridgeDemo.BuildBjt();
                    if (bsim3) BridgeDraft.LoadBsim3();
                    else BridgeDraft.LoadBjt();
                    Wiring = new();
                    ClearSelection();
                    ClearGesture();
                    RecordEdit(before!);
                    return Result(CommandCode.Ok, bsim3 ? "Bulk BSIM3 example loaded." : "BJT example loaded.");
                case LoadBridgeDemo:
                case LoadModelDemo:
                    var models = command is LoadModelDemo;
                    highlightAnchor = null;
                    board = models ? BridgeDemo.BuildModels() : BridgeDemo.Build();
                    BridgeDraft.LoadDemo(models);
                    Wiring = new();
                    ClearSelection();
                    ClearGesture();
                    RecordEdit(before!);
                    return Result(CommandCode.Ok, models ? "Semiconductor example loaded." : "Mixed RLC example loaded.");
                case LoadRc:
                    highlightAnchor = null;
                    board = Board.ChargingCircuit();
                    Wiring = new();
                    ClearSelection();
                    ClearGesture();
                    RecordEdit(before!);
                    return Result(CommandCode.Ok, "RC charging example loaded.");
                case ResetLesson:
                    highlightAnchor = null;
                    board = Board.Divider();
                    Wiring = new();
                    ClearSelection();
                    ClearGesture();
                    RecordEdit(before!);
                    return Result(CommandCode.Ok, "Example restored.");
                case MovePart move:
                    var moving = FindPart(move.PartId);
                    if (moving is null) return Result(CommandCode.NotFound, "Part not found.");
                    if (!PositionAvailable(move.X, move.Y, moving.Id))
                        return Result(CommandCode.InvalidInput, "Choose an unoccupied position on the sheet.");
                    board.Replace(moving with { X = move.X, Y = move.Y });
                    RecordEdit(before!);
                    Select(FindPart(moving.Id)!);
                    return Result(CommandCode.Ok, $"Moved {moving.Id}.");
                case RotatePart rotate:
                    var rotating = FindPart(rotate.PartId);
                    if (rotating is null) return Result(CommandCode.NotFound, "Part not found.");
                    var rotated = rotating with { QuarterTurns = (rotating.QuarterTurns + 1) % 4 };
                    board.Replace(rotated);
                    RecordEdit(before!);
                    Select(rotated);
                    return Result(CommandCode.Ok, $"Rotated {rotated.Id} clockwise.");
                case MirrorPart mirror:
                    var mirroring = FindPart(mirror.PartId);
                    if (mirroring is null) return Result(CommandCode.NotFound, "Part not found.");
                    var mirrored = mirroring with { Mirrored = !mirroring.Mirrored };
                    board.Replace(mirrored);
                    RecordEdit(before!);
                    Select(mirrored);
                    return Result(CommandCode.Ok, $"Mirrored {mirrored.Id}.");
                case DuplicatePart duplicate:
                    var original = FindPart(duplicate.PartId);
                    if (original is null) return Result(CommandCode.NotFound, "Part not found.");
                    if (board.Parts.Count >= 64) return Result(CommandCode.LimitReached, "The sheet allows 64 parts.");
                    if (!PositionAvailable(duplicate.X, duplicate.Y))
                        return Result(CommandCode.InvalidInput, "Choose an unoccupied position on the sheet.");
                    var copy = board.Duplicate(original, duplicate.X, duplicate.Y);
                    RecordEdit(before!);
                    Select(copy);
                    return Result(CommandCode.Ok, $"Duplicated {original.Id} as {copy.Id}.") with { Part = copy };
                case UndoEdit:
                    return Restore(undo, redo, "Edit undone.");
                case RedoEdit:
                    return Restore(redo, undo, "Edit redone.");
                case ArmMove armMove:
                    if (FindPart(armMove.PartId) is null) return Result(CommandCode.NotFound, "Part not found.");
                    ClearGesture();
                    MoveGesture = armMove;
                    MoveVersion = Version;
                    return Result(CommandCode.Ok, "Choose a destination on the sheet.");
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

    /// <summary>Capture a circuit revision under lock, then run the solver without holding it.</summary>
    public async Task<CommandResult> ExecuteTransientAsync(RunTransient command,
        Func<RcInput, CancellationToken, Task<IReadOnlyList<TransientSample>>> solve,
        CancellationToken cancellation = default)
    {
        RcInput input;
        long generation;
        lock (gate)
        {
            if (cancellation.IsCancellationRequested) return Result(CommandCode.Cancelled, "Command cancelled.");
            if (command.Expected.DocumentId != documentId)
                return Result(CommandCode.WrongDocument, "The command targets a different document.");
            if (command.Expected.Revision != revision)
                return Result(CommandCode.StaleRevision, "The document changed. Run the current revision.");
            if (board.ChargingInput(command.ProbeId) is not { } charging)
                return Result(CommandCode.UnsupportedCircuit, "Wire the selected probe to a valid RC charging circuit.") with { Findings = board.Check() };
            input = charging;
            generation = sessionGeneration;
        }

        IReadOnlyList<TransientSample> samples;
        try
        {
            samples = Array.AsReadOnly((await solve(input, cancellation)).ToArray());
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return Result(CommandCode.Cancelled, "Command cancelled.");
        }
        catch (RcSolverException error)
        {
            return new(CommandCode.SolverFailed, command.Expected, error.Message);
        }

        lock (gate)
        {
            if (cancellation.IsCancellationRequested) return Result(CommandCode.Cancelled, "Command cancelled.");
            if (generation != sessionGeneration)
                return Result(CommandCode.StaleRevision, "The document was replaced while analysis was running.");
            if (!IsComplete(input, samples))
                return new(CommandCode.SolverFailed, command.Expected, "The solver returned an incomplete waveform.");
            var run = new TransientRun(Guid.NewGuid(), command.Expected, command.ProbeId, input, samples);
            if (transients.Count == RunLimit) transients.Dequeue();
            transients.Enqueue(run);
            return new(CommandCode.Ok, command.Expected, "RC transient completed.", Transient: run);
        }
    }

    private static bool IsComplete(RcInput input, IReadOnlyList<TransientSample> samples)
    {
        if (samples.Count is < 2 or > 1025 || samples[0].Seconds != 0 ||
            Math.Abs(samples[^1].Seconds - input.StopSeconds) > input.StopSeconds * 1e-9)
            return false;
        var previous = -1d;
        foreach (var sample in samples)
        {
            if (!double.IsFinite(sample.Seconds) || !double.IsFinite(sample.Volts) || sample.Seconds <= previous)
                return false;
            previous = sample.Seconds;
        }
        return true;
    }

    private Part? FindPart(string id) => board.Parts.FirstOrDefault(part => part.Id == id);

    private bool PositionAvailable(int x, int y, string? except = null) =>
        x is >= 0 and <= 640 && y is >= 0 and <= 370 &&
        !board.Parts.Any(part => part.Id != except && Math.Abs(part.X - x) < 64 && Math.Abs(part.Y - y) < 55);

    private void RecordEdit(Board before, SavedSession? saved = null)
    {
        if (undo.Count == HistoryLimit) undo.RemoveFirst();
        undo.AddLast(new EditState(before, saved));
        redo.Clear();
        revision++;
        if (highlightAnchor is Pin pin && !board.Connected(pin, pin)) highlightAnchor = null;
    }

    private CommandResult Restore(LinkedList<EditState> source, LinkedList<EditState> destination, string message)
    {
        if (source.Last is not { } snapshot) return Result(CommandCode.NotFound, "No edit is available.");
        destination.AddLast(new EditState(board, snapshot.Value.Session is null ? null : CaptureSession()));
        board = snapshot.Value.Board;
        Wiring = new();
        source.RemoveLast();
        revision++;
        ClearSelection();
        ClearGesture();
        if (snapshot.Value.Session is { } saved) ApplySession(saved);
        else if (highlightAnchor is Pin pin && !board.Connected(pin, pin)) highlightAnchor = null;
        return Result(CommandCode.Ok, message);
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
        FocusedTerminal = null;
        MoveGesture = null;
        PendingPin = null;
        Placement = null;
    }
}
