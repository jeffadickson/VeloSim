using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VeloSim;

public sealed record FileFingerprint(string Path, string Sha256);
public sealed record RecordedCommand(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, int? ExitCode, string StandardOutput, string StandardError, string? Failure);
public sealed class RecordingProcessRunner(IProcessRunner inner) : IProcessRunner
{
    private readonly List<RecordedCommand> commands = [];
    public IReadOnlyList<RecordedCommand> Commands => commands.AsReadOnly();
    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        executable = ArtifactMetadata.ResolveExecutable(executable, workingDirectory) ?? executable;
        try
        {
            var result = await inner.RunAsync(executable, arguments, workingDirectory, timeout, cancellationToken).ConfigureAwait(false);
            commands.Add(new(executable, arguments.ToArray(), workingDirectory, result.ExitCode, result.StandardOutput, result.StandardError, null)); return result;
        }
        catch (Exception ex)
        {
            commands.Add(new(executable, arguments.ToArray(), workingDirectory, null, ex is ProcessTimeoutException timed ? timed.StandardOutput : "", ex is ProcessTimeoutException timedError ? timedError.StandardError : "", ex.Message)); throw;
        }
    }
}
public sealed record ArtifactMetadata(IReadOnlyList<FileFingerprint> Sources, FileFingerprint? NativeLibrary, IReadOnlyList<RecordedCommand> Commands, IReadOnlyDictionary<string, string> ToolVersions, IReadOnlyList<FileFingerprint>? ToolBinaries = null)
{
    public static string? ResolveExecutable(string executable, string? workingDirectory = null)
    {
        workingDirectory = System.IO.Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
        if (System.IO.Path.IsPathRooted(executable) || executable.Contains(System.IO.Path.DirectorySeparatorChar))
        { var candidate = System.IO.Path.GetFullPath(executable, workingDirectory); return File.Exists(candidate) ? candidate : null; }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            foreach (var suffix in OperatingSystem.IsWindows() ? new[] { "", ".exe", ".cmd", ".bat" } : new[] { "" })
            { var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, executable + suffix), workingDirectory); if (File.Exists(path)) return path; }
        }
        return null;
    }
    public static FileFingerprint Fingerprint(string path)
    {
        var absolute = System.IO.Path.GetFullPath(path); using var stream = File.OpenRead(absolute); return new(absolute, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }
    public static IReadOnlyList<FileFingerprint> SourceClosure(IEnumerable<string> sources)
    {
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal); var result = new List<FileFingerprint>();
        void Visit(string source)
        {
            var path = System.IO.Path.GetFullPath(source); if (!visited.Add(path)) return;
            result.Add(Fingerprint(path));
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.TrimStart(); if (!trimmed.StartsWith("`include", StringComparison.Ordinal)) continue;
                var match = Regex.Match(trimmed, "^`include\\s+\"([^\"]+)\"", RegexOptions.CultureInvariant);
                if (!match.Success) throw new IOException("Cannot fingerprint macro-based include in " + path);
                Visit(System.IO.Path.GetFullPath(match.Groups[1].Value, System.IO.Path.GetDirectoryName(path)!));
            }
        }
        foreach (var source in sources) Visit(source); return result.AsReadOnly();
    }
    public void Write(Utf8JsonWriter w)
    {
        void FileObject(FileFingerprint file) { w.WriteStartObject(); w.WriteString("path", file.Path); w.WriteString("sha256", file.Sha256); w.WriteEndObject(); }
        w.WriteStartObject("metadata"); w.WriteString("source_hash_scope", "explicit_sources_and_recursive_literal_relative_includes");
        w.WriteStartArray("sources"); foreach (var source in Sources) FileObject(source); w.WriteEndArray();
        w.WritePropertyName("native_library"); if (NativeLibrary is null) w.WriteNullValue(); else FileObject(NativeLibrary);
        w.WriteStartObject("tool_versions"); foreach (var (tool, version) in ToolVersions) w.WriteString(tool, version); w.WriteEndObject();
        w.WriteStartArray("tool_binaries"); foreach (var tool in ToolBinaries ?? []) FileObject(tool); w.WriteEndArray();
        w.WriteStartArray("commands"); foreach (var c in Commands)
        {
            w.WriteStartObject(); w.WriteString("executable", c.Executable); w.WriteString("working_directory", c.WorkingDirectory);
            w.WriteStartArray("arguments"); foreach (var a in c.Arguments) w.WriteStringValue(a); w.WriteEndArray();
            if (c.ExitCode.HasValue) w.WriteNumber("exit_code", c.ExitCode.Value); else w.WriteNull("exit_code");
            w.WriteString("stdout", c.StandardOutput); w.WriteString("stderr", c.StandardError); w.WriteString("failure", c.Failure); w.WriteEndObject();
        }
        w.WriteEndArray(); w.WriteEndObject();
    }
}
