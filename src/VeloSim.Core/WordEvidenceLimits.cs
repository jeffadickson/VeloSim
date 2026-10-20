using System.Text.Json;

namespace VeloSim;

public static class WordEvidenceLimits
{
    public const int MaxReportCharacters = 64 * 1024 * 1024;
    public const int MaxTraceRows = 1_000_000;
    public const int MaxTraceValues = 10_000_000;
    public const int MaxCommands = 16;
    public const int MaxCommandCharacters = 16 * 1024 * 1024;
    // Check dimensions before materializing trace lists or recomputing the oracle.
    internal static void Preflight(JsonElement root, WordFixture fixture)
    {
        long rows = 0, values = 0;
        void Trace(JsonElement trace)
        {
            if (trace.ValueKind != JsonValueKind.Array || trace.GetArrayLength() != fixture.Drives.Count) throw new IrValidationException("Word report trace dimensions differ from fixture.");
            rows += trace.GetArrayLength(); values += (long)trace.GetArrayLength() * fixture.Design.Signals.Count;
            if (rows > MaxTraceRows || values > MaxTraceValues) throw new IrValidationException("Word report exceeds trace row/value budget.");
        }
        Trace(root.GetProperty("oracle")); if (root.GetProperty("icarus").ValueKind != JsonValueKind.Null) Trace(root.GetProperty("icarus"));
        var native = root.GetProperty("native"); if (native.ValueKind != JsonValueKind.Object || native.EnumerateObject().Count() > fixture.Workers.Count) throw new IrValidationException("Word report exceeds native engine budget.");
        foreach (var entry in native.EnumerateObject()) Trace(entry.Value);
        var metadata = root.GetProperty("metadata"); var commands = metadata.GetProperty("commands");
        if (commands.ValueKind != JsonValueKind.Array || commands.GetArrayLength() > MaxCommands) throw new IrValidationException("Word report exceeds command count budget.");
        foreach (var command in commands.EnumerateArray())
        {
            var arguments = command.GetProperty("arguments");
            if (arguments.ValueKind != JsonValueKind.Array || arguments.GetArrayLength() > 256) throw new IrValidationException("Word command exceeds argument budget.");
            foreach (var name in new[] { "stdout", "stderr" }) if (command.GetProperty(name).GetString() is not { } text || text.Length > MaxCommandCharacters) throw new IrValidationException("Word command exceeds output evidence budget.");
        }
    }
    public static string FreshOutput(string path)
    {
        var absolute = Path.GetFullPath(path);
        for (DirectoryInfo? current = new(absolute); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Word output cannot traverse a symbolic link or reparse point.");
        if (File.Exists(absolute) || Directory.Exists(absolute) && Directory.EnumerateFileSystemEntries(absolute).Any()) throw new IOException("Word output must be an empty directory.");
        return absolute;
    }
}
