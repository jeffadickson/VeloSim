using System.ComponentModel;
using ModelContextProtocol.Server;
using SchematicLab.Core;
using SchematicLab.Solver;

[McpServerToolType]
internal sealed class LessonTools(LessonSession session, NativeRcSolver solver)
{
    [McpServerTool(Name = "schematic_read_circuit", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read the current circuit, voltage probe, document ID and revision.")]
    public CommandResult Read(CancellationToken cancellationToken) =>
        session.Execute(new ReadCircuit(), cancellationToken);

    [McpServerTool(Name = "schematic_read_catalog", ReadOnly = true, UseStructuredContent = true)]
    [Description("List the generic parts and their ordered zero-based terminals.")]
    public CommandResult Catalog(CancellationToken cancellationToken) =>
        session.Execute(new ReadCatalog(), cancellationToken);

    [McpServerTool(Name = "schematic_set_value", UseStructuredContent = true)]
    [Description("Set a resistor in ohms, a capacitor in farads or a supported source in volts at the expected revision.")]
    public CommandResult SetValue(Guid documentId, long revision, string partId, double value,
        CancellationToken cancellationToken) =>
        session.Execute(new SetPartValue(new(documentId, revision), partId, value), cancellationToken);

    [McpServerTool(Name = "schematic_place_part", UseStructuredContent = true)]
    [Description("Place a catalog part at explicit SVG sheet coordinates.")]
    public CommandResult Place(Guid documentId, long revision, Kind kind, int x, int y,
        CancellationToken cancellationToken) =>
        session.Execute(new PlacePart(new(documentId, revision), kind, x, y), cancellationToken);

    [McpServerTool(Name = "schematic_connect_pins", UseStructuredContent = true)]
    [Description("Wire two explicit terminals at the expected revision. Terminal indices start at zero.")]
    public CommandResult Connect(Guid documentId, long revision, string firstPart, int firstPin,
        string secondPart, int secondPin, CancellationToken cancellationToken) =>
        session.Execute(new ConnectPins(new(documentId, revision), new(firstPart, firstPin),
            new(secondPart, secondPin)), cancellationToken);

    [McpServerTool(Name = "schematic_delete_part", UseStructuredContent = true)]
    [Description("Delete the identified part and all its incident wires at the expected revision.")]
    public CommandResult Delete(Guid documentId, long revision, string partId,
        CancellationToken cancellationToken) =>
        session.Execute(new DeletePart(new(documentId, revision), partId), cancellationToken);

    [McpServerTool(Name = "schematic_reset_lesson", UseStructuredContent = true)]
    [Description("Replace the circuit with the built-in voltage divider at the expected revision.")]
    public CommandResult Reset(Guid documentId, long revision, CancellationToken cancellationToken) =>
        session.Execute(new ResetLesson(new(documentId, revision)), cancellationToken);

    [McpServerTool(Name = "schematic_run_sweep", UseStructuredContent = true)]
    [Description("Run the analytical divider sweep at the specified revision and retain its 21 samples.")]
    public CommandResult Run(Guid documentId, long revision, CancellationToken cancellationToken) =>
        session.Execute(new RunSweep(new(documentId, revision)), cancellationToken);

    [McpServerTool(Name = "schematic_read_run", ReadOnly = true, UseStructuredContent = true)]
    [Description("Read a retained run by ID, including its original document revision and samples.")]
    public CommandResult ReadRunById(Guid runId, CancellationToken cancellationToken) =>
        session.Execute(new ReadRun(runId), cancellationToken);

    [McpServerTool(Name = "schematic_load_rc", UseStructuredContent = true)]
    [Description("Load the built-in RC charging example at the expected revision.")]
    public CommandResult LoadChargingExample(Guid documentId, long revision, CancellationToken cancellationToken) =>
        session.Execute(new LoadRc(new(documentId, revision)), cancellationToken);

    [McpServerTool(Name = "schematic_run_transient", UseStructuredContent = true)]
    [Description("Run the native RC transient solver and retain the voltage samples of the specified probe.")]
    public Task<CommandResult> RunChargingTransient(Guid documentId, long revision, string probeId,
        CancellationToken cancellationToken) =>
        session.ExecuteTransientAsync(new RunTransient(new(documentId, revision), probeId), solver.SolveAsync, cancellationToken);
}
