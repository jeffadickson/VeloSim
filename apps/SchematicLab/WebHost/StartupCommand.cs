namespace SchematicLab.WebHost;

/// <summary>Validated deployment paths and localhost port; never a public bind address.</summary>
/// <param name="WebRoot">Absolute browser asset directory containing index.html.</param>
/// <param name="SolverPath">Explicit or packaged native solver, or null when unavailable.</param>
/// <param name="Port">Local TCP port in the inclusive range 1024..65535.</param>
public sealed record SchematicHostOptions(string WebRoot, string? SolverPath, int Port);

/// <summary>Starts the local schematic using a completely validated configuration.</summary>
/// <param name="Options">Immutable validated startup settings.</param>
public sealed record ServeSchematic(SchematicHostOptions Options);

/// <summary>Displays usage without requiring deployed assets or native tooling.</summary>
public sealed record ShowSchematicHelp;

/// <summary>Expected startup refusal, before any listener or solver process starts.</summary>
/// <param name="Code">Stable machine-readable failure identifier.</param>
/// <param name="Message">Actionable local diagnostic.</param>
public sealed record InvalidSchematicStartup(string Code, string Message);

/// <summary>Finite startup outcomes; C# 15 checks consumer switches for exhaustiveness.</summary>
public union StartupCommand(ServeSchematic, ShowSchematicHelp, InvalidSchematicStartup);
