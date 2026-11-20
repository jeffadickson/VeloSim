namespace SchematicLab.Solver;

/// <summary>Finds the optional trusted executable in a local standalone distribution.</summary>
public static class SolverLocation
{
    /// <summary>Finds the installed solver without consulting current directory or PATH.</summary>
    /// <param name="applicationDirectory">Absolute application directory.</param>
    /// <returns>The packaged solver path, or null when native analysis is unavailable.</returns>
    /// <exception cref="ArgumentException">The application directory is not absolute.</exception>
    public static string? FindPackaged(string applicationDirectory)
    {
        if (!Path.IsPathFullyQualified(applicationDirectory))
            throw new ArgumentException("Application directory must be absolute.", nameof(applicationDirectory));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        var name = OperatingSystem.IsWindows() ? "circuit-simulator.exe" : "circuit-simulator";
        var candidate = Path.Combine(root, "solver", name);
        if (File.Exists(candidate)) return candidate;

        // The independently published MCP host has its own runtime directory.
        // Only a marked schematic bundle can supply its parent-relative solver.
        if (Path.GetFileName(root).Equals("mcp", StringComparison.OrdinalIgnoreCase) &&
            Path.GetDirectoryName(root) is { } parent && File.Exists(Path.Combine(parent, "schematic-bundle.json")))
        {
            candidate = Path.Combine(parent, "solver", name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
