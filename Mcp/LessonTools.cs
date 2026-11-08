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

    [McpServerTool(Name = "schematic_move_part", UseStructuredContent = true)]
    [Description("Move an existing part to explicit sheet coordinates without changing connections.")]
    public CommandResult Move(Guid documentId, long revision, string partId, int x, int y,
        CancellationToken cancellationToken) =>
        session.Execute(new MovePart(new(documentId, revision), partId, x, y), cancellationToken);

    [McpServerTool(Name = "schematic_rotate_part", UseStructuredContent = true)]
    [Description("Rotate an existing part clockwise by 90 degrees, preserving terminal identity.")]
    public CommandResult Rotate(Guid documentId, long revision, string partId, CancellationToken cancellationToken) =>
        session.Execute(new RotatePart(new(documentId, revision), partId), cancellationToken);

    [McpServerTool(Name = "schematic_mirror_part", UseStructuredContent = true)]
    [Description("Toggle a part's reflection in its local vertical axis.")]
    public CommandResult Mirror(Guid documentId, long revision, string partId, CancellationToken cancellationToken) =>
        session.Execute(new MirrorPart(new(documentId, revision), partId), cancellationToken);

    [McpServerTool(Name = "schematic_duplicate_part", UseStructuredContent = true)]
    [Description("Copy the part's value and orientation without its wires.")]
    public CommandResult Duplicate(Guid documentId, long revision, string partId, int x, int y,
        CancellationToken cancellationToken) =>
        session.Execute(new DuplicatePart(new(documentId, revision), partId, x, y), cancellationToken);

    [McpServerTool(Name = "schematic_undo_edit", UseStructuredContent = true)]
    [Description("Undo the most recent retained edit and advance the document revision.")]
    public CommandResult Undo(Guid documentId, long revision, CancellationToken cancellationToken) =>
        session.Execute(new UndoEdit(new(documentId, revision)), cancellationToken);

    [McpServerTool(Name = "schematic_redo_edit", UseStructuredContent = true)]
    [Description("Redo the most recent undone edit and advance the document revision.")]
    public CommandResult Redo(Guid documentId, long revision, CancellationToken cancellationToken) =>
        session.Execute(new RedoEdit(new(documentId, revision)), cancellationToken);

    [McpServerTool(Name = "schematic_check_circuit", ReadOnly = true, UseStructuredContent = true)]
    [Description("Check the current schematic for missing ground, dangling terminals and supported topology.")]
    public CommandResult Check(CancellationToken cancellationToken) =>
        session.Execute(new CheckCircuit(), cancellationToken);

    [McpServerTool(Name = "schematic_set_net_label", UseStructuredContent = true)]
    [Description("Set or clear a case-insensitive electrical net label on one terminal.")]
    public CommandResult Label(Guid documentId, long revision, Pin terminal, string? name,
        CancellationToken cancellationToken) =>
        session.Execute(new SetNetLabel(new(documentId, revision), terminal, name), cancellationToken);

    [McpServerTool(Name = "schematic_set_wire_route", UseStructuredContent = true)]
    [Description("Replace the ordered bends of a wire without changing its terminal endpoints.")]
    public CommandResult Route(Guid documentId, long revision, Wire wire, SheetPoint[] bends,
        CancellationToken cancellationToken) =>
        session.Execute(new SetWireRoute(new(documentId, revision), wire, bends), cancellationToken);

    [McpServerTool(Name = "schematic_delete_wire", UseStructuredContent = true)]
    [Description("Remove a wire, its route and its incident junctions.")]
    public CommandResult RemoveWire(Guid documentId, long revision, Wire wire,
        CancellationToken cancellationToken) =>
        session.Execute(new DeleteWire(new(documentId, revision), wire), cancellationToken);

    [McpServerTool(Name = "schematic_join_routes", UseStructuredContent = true)]
    [Description("Connect two wires at an explicit bend shared by their routes.")]
    public CommandResult Join(Guid documentId, long revision, Wire first, Wire second, SheetPoint at,
        CancellationToken cancellationToken) =>
        session.Execute(new JoinRoutes(new(documentId, revision), first, second, at), cancellationToken);

    [McpServerTool(Name = "schematic_delete_junction", UseStructuredContent = true)]
    [Description("Remove an explicit electrical junction while preserving the wire routes.")]
    public CommandResult RemoveJunction(Guid documentId, long revision, Guid junctionId,
        CancellationToken cancellationToken) =>
        session.Execute(new DeleteJunction(new(documentId, revision), junctionId), cancellationToken);

    [McpServerTool(Name = "schematic_load_document", UseStructuredContent = true)]
    [Description("Validate and replace the current sheet with a versioned JSON document.")]
    public CommandResult Load(Guid documentId, long revision, string document,
        CancellationToken cancellationToken) =>
        session.Execute(new LoadDocument(new(documentId, revision), document), cancellationToken);

    [McpServerTool(Name = "schematic_save_document", ReadOnly = true, UseStructuredContent = true)]
    [Description("Export the current sheet as versioned JSON for client-controlled storage.")]
    public CommandResult Save(CancellationToken cancellationToken) =>
        session.Execute(new SaveDocument(), cancellationToken);

}