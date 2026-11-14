namespace SchematicLab.Core;

/// <summary>Trusted lesson-author policy. Reader documents cannot grant unlock permission.</summary>
public sealed record LessonInteractionPolicy(bool InitiallyLocked = false, bool ReaderCanUnlock = true);

/// <summary>Changes layout mode at an exact document revision; parameter changes remain available.</summary>
public sealed record SetLayoutLocked(DocumentVersion Expected, bool Locked) : DocumentCommand(Expected);
public sealed record ReadInteractionPolicy : LessonCommand;

/// <summary>Current interaction mode and the immutable author permission.</summary>
public sealed record InteractionState(bool LayoutLocked, bool ReaderCanUnlock);

/// <summary>Small trusted bootstrap profile shared by browser hosting and stdio MCP.</summary>
public sealed record LessonDeployment(LessonInteractionPolicy Policy, bool RcExample = false)
{
    /// <summary>Creates a session only from a complete bootstrap, including deserialized profiles.</summary>
    public LessonSession CreateSession()
    {
        ArgumentNullException.ThrowIfNull(Policy);
        return new(Policy, RcExample);
    }

    /// <summary>Parses explicit deployment modes; unknown configuration fails closed at startup.</summary>
    public static LessonDeployment Parse(string? mode, string? example) => new(mode switch
    {
        null or "" or "editor" => new(),
        "reader-lock" => new(true, true),
        "author-locked" => new(true, false),
        _ => throw new ArgumentException("SCHEMATIC_LAYOUT_MODE must be editor, reader-lock or author-locked.")
    }, example switch
    {
        null or "" or "divider" => false,
        "rc" => true,
        _ => throw new ArgumentException("SCHEMATIC_LESSON must be divider or rc.")
    });
}

public sealed partial class LessonSession
{
    private readonly LessonInteractionPolicy interactionPolicy;
    private bool layoutLocked;

    /// <summary>Creates an editable session unless trusted author policy locks its layout.</summary>
    public LessonSession(LessonInteractionPolicy? policy = null, bool rcExample = false)
    {
        interactionPolicy = policy ?? new();
        // A deployment that forbids unlocking must not accidentally start editable.
        layoutLocked = interactionPolicy.InitiallyLocked || !interactionPolicy.ReaderCanUnlock;
        if (rcExample) board = Board.ChargingCircuit();
    }

    public InteractionState Interaction { get { lock (gate) return new(layoutLocked, interactionPolicy.ReaderCanUnlock); } }

    // Default refusal makes newly introduced actions require an explicit policy
    // decision. Controls names select electrical connections, not numeric values.
    private bool AllowedWhileLocked(LessonCommand command) => command switch
    {
        SetLayoutLocked or ReadInteractionPolicy or SetPartValue or RunSweep or ReadCircuit or
        ReadCatalog or ReadRun or SelectPart or CancelGesture or SetDraftValue or SetCatalogFilter or
        ToggleCatalog or CheckCircuit or FocusTerminal or HighlightNet or ReadView or ReadAnalysisHistory or
        SaveDocument or ReadChildSheet or ReadNamedModels or ReadAnalysisFields => true,
        UndoEdit => undo.Last?.Value.ParameterOnly == true,
        RedoEdit => redo.Last?.Value.ParameterOnly == true,
        SetBridgeDraft draft => draft.Field != BridgeField.Controls,
        ApplyAnalysisFields fields => fields.Fields is not null &&
            (!fields.Fields.TryGetValue(BridgeField.Controls, out var controls) || controls == BridgeDraft[BridgeField.Controls]),
        _ => false
    };
}
