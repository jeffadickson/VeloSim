namespace SchematicLab.Core;

/// <summary>Known adapter identities exposed by the bounded schematic bridge, not model families.</summary>
public enum SolverBackend { Lu, Qr, Faer }

/// <summary>Selects a CPU adapter at an exact revision; changing it invalidates earlier run freshness.</summary>
public sealed record SetSolverBackend(DocumentVersion Expected, SolverBackend Backend) : DocumentCommand(Expected);
/// <summary>Discovers the bounded adapter choices, selected identity and known analysis capabilities.</summary>
public sealed record ReadSolverBackends : LessonCommand;
/// <summary>Known request/response identities and complex-AC capability; this is not hardware discovery.</summary>
public sealed record SolverProviderInfo(SolverBackend Backend, string RequestName, string ProviderId, bool SupportsAc);

/// <summary>Stable engine request names and expected response identities.</summary>
public static class SolverBackendNames
{
    /// <summary>Returns true for known complex-capable adapters; real-only QR and unknown values return false.</summary>
    public static bool SupportsAc(this SolverBackend backend) => backend is SolverBackend.Lu or SolverBackend.Faer;
    /// <summary>Returns an immutable snapshot of the three adapters exposed by this bridge.</summary>
    public static IReadOnlyList<SolverProviderInfo> Providers() => Array.AsReadOnly(Enum.GetValues<SolverBackend>()
        .Select(backend => new SolverProviderInfo(backend, backend.WireName(), backend.ProviderId(), backend.SupportsAc())).ToArray());
    /// <summary>Maps a validated selection to the exact native schema request name.</summary>
    public static string WireName(this SolverBackend backend) => backend switch
    {
        SolverBackend.Lu => "lu", SolverBackend.Qr => "qr", SolverBackend.Faer => "faer",
        _ => throw new ArgumentOutOfRangeException(nameof(backend))
    };

    /// <summary>Returns the required manifest identity; accepting an alias here could conceal fallback.</summary>
    public static string ProviderId(this SolverBackend backend) => backend switch
    {
        SolverBackend.Lu => "nalgebra_dense_lu_reference", SolverBackend.Qr => "nalgebra_dense_qr_reference",
        SolverBackend.Faer => "faer_sparse_lu", _ => throw new ArgumentOutOfRangeException(nameof(backend))
    };

    /// <summary>Accepts only canonical lower-case wire names, without case folding or silent defaulting.</summary>
    public static bool TryParse(string? text, out SolverBackend backend)
    {
        backend = text switch { "lu" => SolverBackend.Lu, "qr" => SolverBackend.Qr, "faer" => SolverBackend.Faer, _ => (SolverBackend)(-1) };
        return Enum.IsDefined(backend);
    }
}

public sealed partial class LessonSession
{
    /// <summary>Selection inherited by RC and by general analyses without an explicit per-run override.</summary>
    public SolverBackend SelectedBackend { get { lock (gate) return BridgeDraft.Backend; } }

    private CommandResult ApplyFields(IReadOnlyDictionary<BridgeField, string>? fields)
    {
        if (fields is null || !BridgeDraft.ValidFields(fields)) return Result(CommandCode.InvalidInput, "Invalid analysis settings.");
        var changed = fields.TryGetValue(BridgeField.Backend, out var next) && next != BridgeDraft[BridgeField.Backend];
        var before = changed ? board.Copy() : null;
        var saved = changed ? CaptureSession() : null;
        var structural = fields.TryGetValue(BridgeField.Controls, out var controls) && controls != BridgeDraft[BridgeField.Controls];
        BridgeDraft.Apply(fields);
        if (changed) RecordEdit(before!, saved, parameterOnly: !structural);
        return Result(CommandCode.Ok, "Analysis settings applied.") with { Backend = BridgeDraft.Backend };
    }
}
