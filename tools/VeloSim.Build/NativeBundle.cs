using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public static class NativeBundle
{
    private static readonly string[] Required = ["VeloSim.Tools", "VeloSim.Build", "VeloSim.Bench", "libvelosim_ir.so", "libvelosim_scheduler.so", "include/velosim.h", "libvelosim_vpi.so", "include/velosim/runtime.h", "include/velosim/external_model.h", "include/velosim/vpi_user.h", "examples/counter.ir.json", "README.txt", "build.txt"];
    public static void Create(string directory, IReadOnlyDictionary<string, string> inputs, string revision, string buildMetadata)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (Directory.Exists(directory)) throw new IOException("Bundle directory already exists.");
        if (inputs.ContainsKey("manifest.json") || inputs.ContainsKey("build.txt")) throw new IOException("Reserved bundle input name.");
        foreach (var (relative, source) in inputs) { SafePath(directory, relative); if (!File.Exists(source) || new FileInfo(source).LinkTarget is not null) throw new IOException("Missing or linked bundle input: " + source); }
        if (Required.Where(p => p != "build.txt").Any(p => !inputs.ContainsKey(p))) throw new IOException("Bundle inputs omit required files.");
        Directory.CreateDirectory(directory);
        foreach (var (relative, source) in inputs)
        {
            var path = SafePath(directory, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.Copy(source, path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, File.GetUnixFileMode(source));
        }
        File.WriteAllText(Path.Combine(directory, "build.txt"), buildMetadata);
        var files = Walk(directory).Order(StringComparer.Ordinal).ToArray();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            writer.WriteStartObject(); writer.WriteString("format", "velosim.native.bundle.v2"); writer.WriteString("runtime", "linux-x64"); writer.WriteString("source_revision", revision);
            writer.WriteStartArray("files");
            foreach (var relative in files)
            {
                var path = SafePath(directory, relative); writer.WriteStartObject(); writer.WriteString("path", relative); writer.WriteNumber("bytes", new FileInfo(path).Length); writer.WriteString("sha256", Digest(path));
                writer.WriteBoolean("executable", !OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        File.WriteAllText(Path.Combine(directory, "manifest.json"), Encoding.UTF8.GetString(stream.ToArray()) + "\n");
        Validate(directory);
    }
    public static void Validate(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var manifest = Path.Combine(directory, "manifest.json");
        if (new DirectoryInfo(directory).LinkTarget is not null || new FileInfo(manifest).LinkTarget is not null) throw new IOException("Bundle paths cannot be links.");
        if (new FileInfo(manifest).Length > 8 * 1024 * 1024) throw new IOException("Bundle manifest exceeds size limit.");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var root = document.RootElement;
        RequireFields(root, ["format", "runtime", "source_revision", "files"]);
        if (root.GetProperty("format").GetString() != "velosim.native.bundle.v2" || root.GetProperty("runtime").GetString() != "linux-x64" || string.IsNullOrWhiteSpace(root.GetProperty("source_revision").GetString())) throw new IOException("Unsupported bundle metadata.");
        var actual = Walk(directory).ToHashSet(StringComparer.Ordinal);
        var expected = new HashSet<string>(StringComparer.Ordinal) { "manifest.json" };
        if (root.GetProperty("files").GetArrayLength() > 4096) throw new IOException("Bundle manifest has too many files.");
        foreach (var entry in root.GetProperty("files").EnumerateArray())
        {
            RequireFields(entry, ["path", "bytes", "sha256", "executable"]);
            var relative = entry.GetProperty("path").GetString() ?? throw new IOException("Missing bundle path.");
            var path = SafePath(directory, relative);
            if (!expected.Add(relative) || !File.Exists(path) || new FileInfo(path).Length != entry.GetProperty("bytes").GetInt64() || Digest(path) != entry.GetProperty("sha256").GetString()) throw new IOException("Bundle integrity mismatch: " + relative);
            if (!OperatingSystem.IsWindows() && entry.GetProperty("executable").GetBoolean() != ((File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0)) throw new IOException("Bundle executable mode mismatch: " + relative);
        }
        if (!expected.SetEquals(actual)) throw new IOException("Bundle file set differs from manifest.");
        if (Required.Any(p => !expected.Contains(p))) throw new IOException("Bundle manifest omits required files.");
    }
    public static void Archive(string directory, string destination)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)); destination = Path.GetFullPath(destination);
        if (destination.StartsWith(directory + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Archive destination must be outside its input directory.");
        Validate(directory);
        using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
        using var compressed = new GZipStream(file, CompressionLevel.SmallestSize);
        TarFile.CreateFromDirectory(directory, compressed, includeBaseDirectory: true);
    }
    private static string Digest(string path)
    {
        using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }
    private static void RequireFields(JsonElement element, string[] names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object) throw new IOException("Bundle metadata must be an object.");
        foreach (var field in element.EnumerateObject()) if (!names.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name)) throw new IOException("Unknown or duplicate bundle metadata field.");
        if (seen.Count != names.Length) throw new IOException("Missing bundle metadata field.");
    }
    private static string SafePath(string root, string relative)
    {
        if (relative.Length == 0 || relative.Contains('\\') || relative.Contains(':') || Path.IsPathRooted(relative) || relative.Split('/').Any(p => p is "" or "." or "..")) throw new IOException("Unsafe bundle path: " + relative);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) throw new IOException("Bundle path escapes root.");
        return path;
    }
    private static IEnumerable<string> Walk(string root)
    {
        int count = 0;
        IEnumerable<string> Visit(DirectoryInfo directory, int depth)
        {
            if (depth > 16) throw new IOException("Bundle directory depth exceeds limit.");
            if (directory.LinkTarget is not null) throw new IOException("Bundle directories cannot be links.");
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (++count > 4096) throw new IOException("Bundle file count exceeds limit.");
                if (entry.LinkTarget is not null) throw new IOException("Bundle files cannot be links.");
                if (entry is DirectoryInfo child) { foreach (var path in Visit(child, depth + 1)) yield return path; }
                else if (entry is FileInfo file) yield return Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
            }
        }
        return Visit(new DirectoryInfo(root), 0);
    }
}
