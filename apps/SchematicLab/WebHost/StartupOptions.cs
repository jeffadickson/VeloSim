using System.Globalization;
using SchematicLab.Solver;

namespace SchematicLab.WebHost;

/// <summary>Parses bounded CLI input without publishing server state or launching dialogs.</summary>
public static class StartupOptions
{
    /// <summary>Historical loopback port used by the lesson and MCP/browser examples.</summary>
    public const int DefaultPort = 5087;

    /// <summary>Parses CLI overrides, environment values, then installed-relative defaults.</summary>
    /// <param name="arguments">Up to 64 arguments, each no longer than 4096 characters.</param>
    /// <param name="applicationDirectory">Absolute installed application directory, never the working directory.</param>
    /// <param name="webRoot">Optional legacy SCHEMATIC_WEB_ROOT value.</param>
    /// <param name="solverPath">Optional legacy SPICE_SOLVER_PATH value.</param>
    /// <returns>A serve, help or expected-error command; no native process is started.</returns>
    public static StartupCommand Parse(IReadOnlyList<string?>? arguments, string? applicationDirectory,
        string? webRoot = null, string? solverPath = null)
    {
        if (arguments is null || arguments.Any(argument => argument is null))
            return new InvalidSchematicStartup("invalid_arguments", "Arguments must contain non-null strings.");
        if (arguments.Count > 64 || arguments.Any(argument => argument!.Length > 4096))
            return new InvalidSchematicStartup("argument_budget", "At most 64 arguments of 4096 characters are supported.");
        if (arguments.Count == 1 && arguments[0] is "--help" or "-h") return new ShowSchematicHelp();
        if (applicationDirectory is null || !Path.IsPathFullyQualified(applicationDirectory))
            return new InvalidSchematicStartup("invalid_application_directory", "Application directory must be absolute.");

        // Constructor arguments retain the comparer when using C# 15 collection syntax.
        HashSet<string> seen = [with(StringComparer.Ordinal)];
        var port = DefaultPort;
        for (var i = 0; i < arguments.Count; i++)
        {
            var option = arguments[i]!;
            if (option is not ("--port" or "--web-root" or "--solver"))
                return new InvalidSchematicStartup("unknown_option", "Supported options: --port, --web-root, --solver, --help.");
            if (!seen.Add(option)) return new InvalidSchematicStartup("duplicate_option", "An option may appear only once.");
            if (++i == arguments.Count) return new InvalidSchematicStartup("missing_value", "Each configuration option requires a value.");
            var value = arguments[i]!;
            switch (option)
            {
                case "--port":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1024 or > 65535)
                        return new InvalidSchematicStartup("invalid_port", "Port must be an integer in 1024..65535.");
                    break;
                case "--web-root":
                    if (string.IsNullOrWhiteSpace(value)) return new InvalidSchematicStartup("invalid_web_root", "An explicit browser path cannot be empty.");
                    webRoot = value; break;
                case "--solver":
                    if (string.IsNullOrWhiteSpace(value)) return new InvalidSchematicStartup("invalid_solver", "An explicit solver path cannot be empty.");
                    solverPath = value; break;
            }
        }

        try
        {
            var root = string.IsNullOrWhiteSpace(webRoot) ? Path.Combine(applicationDirectory, "wwwroot") : webRoot;
            if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root) || !File.Exists(Path.Combine(root, "index.html")))
                return new InvalidSchematicStartup("invalid_web_root", "Browser assets need an absolute directory containing index.html.");
            root = Path.GetFullPath(root);
            if (string.IsNullOrWhiteSpace(solverPath))
            {
                solverPath = SolverLocation.FindPackaged(applicationDirectory);
            }
            else if (!Path.IsPathFullyQualified(solverPath) || !File.Exists(solverPath))
                return new InvalidSchematicStartup("invalid_solver", "The explicitly selected solver must be an existing absolute file.");
            return new ServeSchematic(new(root, solverPath is null ? null : Path.GetFullPath(solverPath), port));
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return new InvalidSchematicStartup("invalid_path", "A deployment path is invalid or inaccessible.");
        }
    }
}
