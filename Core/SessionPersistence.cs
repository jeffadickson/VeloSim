namespace SchematicLab.Core;

/// <summary>Finds or clears the connected net without editing the circuit.</summary>
public sealed record HighlightNet(Pin? Terminal) : LessonCommand;
public sealed record ReadView : LessonCommand;
public sealed record ReadAnalysisHistory : LessonCommand;

/// <summary>Terminals, physical wires and explicit junctions in one electrical net.</summary>
public sealed record NetHighlight(Pin Anchor, IReadOnlyList<Pin> Terminals,
    IReadOnlyList<Wire> Wires, IReadOnlyList<Guid> Junctions);

/// <summary>Persistent presentation choices. Pending gestures and unsaved field drafts are transient.</summary>
public sealed record LessonView(string? Selected, bool CatalogOpen, string Filter, Pin? Highlight);

/// <summary>Completed runs in retention order, with IDs that correspond to the current board.</summary>
public sealed record AnalysisHistory(IReadOnlyList<SweepRun> Sweeps, IReadOnlyList<TransientRun> Transients,
    IReadOnlyList<Guid> Current);

internal sealed record SavedSession(LessonView View, AnalysisHistory History);

public sealed partial class LessonSession
{
    private const int RunLimit = 16;
    private static readonly SavedSession EmptySession = new(new(null, false, "", null), new([], [], []));
    private sealed record EditState(Board Board, SavedSession? Session);
    private Pin? highlightAnchor;
    private long sessionGeneration;
    private long restoredRevision = -1;
    private HashSet<Guid> restoredCurrent = [];

    public LessonView View
    {
        get { lock (gate) return new(Selected, CatalogOpen, Filter, highlightAnchor); }
    }

    public NetHighlight? HighlightedNet
    {
        get { lock (gate) return highlightAnchor is Pin pin ? board.TraceNet(pin) : null; }
    }

    /// <summary>Tests freshness separately from the run's immutable original document version.</summary>
    public bool IsCurrent(Guid id, DocumentVersion version)
    {
        lock (gate) return version == Version || (revision == restoredRevision && restoredCurrent.Contains(id));
    }

    public AnalysisHistory History { get { lock (gate) return CaptureSession().History; } }

    private SavedSession CaptureSession() => new(View, new(
        Array.AsReadOnly(runs.ToArray()), Array.AsReadOnly(transients.ToArray()),
        Array.AsReadOnly(runs.Where(run => IsCurrent(run.Id, run.Version)).Select(run => run.Id)
            .Concat(transients.Where(run => IsCurrent(run.Id, run.Version)).Select(run => run.Id)).ToArray())));

    private void ApplySession(SavedSession saved)
    {
        sessionGeneration++;
        Wiring = new();
        ClearGesture();
        ClearSelection();
        if (saved.View.Selected is { } id) Select(FindPart(id)!);
        CatalogOpen = saved.View.CatalogOpen;
        Filter = saved.View.Filter;
        highlightAnchor = saved.View.Highlight;

        runs.Clear();
        foreach (var run in saved.History.Sweeps)
            runs.Enqueue(run with { Samples = Array.AsReadOnly(run.Samples.ToArray()) });
        transients.Clear();
        foreach (var run in saved.History.Transients)
            transients.Enqueue(run with { Samples = Array.AsReadOnly(run.Samples.ToArray()) });
        restoredCurrent = saved.History.Current.ToHashSet();
        restoredRevision = revision;
    }

    private static bool ValidateSession(Board candidate, SavedSession saved)
    {
        if (saved is not { View.Filter.Length: <= 128, History.Sweeps.Count: <= RunLimit,
            History.Transients.Count: <= RunLimit, History.Current.Count: <= 2 * RunLimit }) return false;
        if (saved.View.Selected is { } selected && !candidate.Parts.Any(part => part.Id == selected)) return false;
        if (saved.View.Highlight is Pin pin && !candidate.Connected(pin, pin)) return false;

        var current = saved.History.Current.ToHashSet();
        if (current.Count != saved.History.Current.Count) return false;
        var ids = new HashSet<Guid>();
        foreach (var run in saved.History.Sweeps)
        {
            if (run is not { Samples.Count: 21 } || !ValidIdentity(run.Id, run.Version, ids) ||
                run.Samples.Any(point => point is null || !double.IsFinite(point.Input) || !double.IsFinite(point.Probe)) ||
                run.Samples[^1].Input <= 0) return false;
            if (current.Contains(run.Id) && !candidate.Sweep().Select(p => new SweepSample(p.Input, p.Probe))
                .SequenceEqual(run.Samples)) return false;
        }

        foreach (var run in saved.History.Transients)
        {
            if (run is not { ProbeId.Length: > 0 and <= 12, Input.IsValid: true, Samples: not null } ||
                !ValidIdentity(run.Id, run.Version, ids) || run.Samples.Any(point => point is null) ||
                !IsComplete(run.Input, run.Samples)) return false;
            if (current.Contains(run.Id) && candidate.ChargingInput(run.ProbeId) != run.Input) return false;
        }

        return current.IsSubsetOf(ids);
    }

    private static bool ValidIdentity(Guid id, DocumentVersion version, HashSet<Guid> ids) =>
        id != Guid.Empty && version.DocumentId != Guid.Empty && version.Revision >= 0 && ids.Add(id);
}
