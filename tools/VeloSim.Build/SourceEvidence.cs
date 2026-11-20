using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public static class SourceEvidence
{
    public static IReadOnlyDictionary<string, string> Capture(string root)
    {
        root = Path.GetFullPath(root); var paths = new List<string>();
        HashSet<string> extensions = [with(StringComparer.Ordinal), ".cs", ".csproj", ".cpp", ".c", ".h", ".map", ".sv", ".svh", ".json", ".cmake", ".props", ".targets", ".config"];
        void Walk(DirectoryInfo directory)
        {
            if (directory.LinkTarget is not null) throw new IOException("Coverage source directories cannot be symbolic links.");
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo child) { if (child.Name is not ("bin" or "obj" or "__pycache__" or "TestResults")) Walk(child); }
                else if (entry is FileInfo file && (extensions.Contains(file.Extension) || file.Name is "CMakeLists.txt" or "Makefile")) paths.Add(file.FullName);
            }
        }
        // File-based qualification apps live beside the build-tool project and must invalidate stale evidence too.
        foreach (var relative in new[] { "src", "native", "tests", "tools", "examples" }) { var path = Path.Combine(root, relative); if (Directory.Exists(path)) Walk(new(path)); }
        paths.AddRange(new[] { "CMakeLists.txt", "Makefile", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.config", "coverage.runsettings", "VeloSim.slnx", ".config/dotnet-tools.json" }.Select(p => Path.Combine(root, p)).Where(File.Exists));
        if (paths.Any(p => new FileInfo(p).LinkTarget is not null)) throw new IOException("Coverage source files cannot be symbolic links.");
        return paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToDictionary(p => Path.GetRelativePath(root, p).Replace('\\', '/'), p => Convert.ToHexStringLower(SHA256.HashData(Path.GetFileName(p) == "packages.lock.json" ? DependencySnapshot(p) : File.ReadAllBytes(p))), StringComparer.Ordinal);
    }
    public static byte[] DependencySnapshot(string path)
    {
        // NativeAOT restore adds compiler-only runtime entries; ordinary build
        // removes them. Keep application dependency hashes stable across that
        // restore-mode change while retaining every noncompiler dependency.
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject().Where(p => p.Name != "dependencies").OrderBy(p => p.Name, StringComparer.Ordinal)) { writer.WritePropertyName(property.Name); property.Value.WriteTo(writer); }
            writer.WriteStartObject("dependencies");
            foreach (var framework in document.RootElement.GetProperty("dependencies").EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            {
                var packages = framework.Value.EnumerateObject().Where(p => p.Name != "Microsoft.DotNet.ILCompiler" && !(p.Name.StartsWith("runtime.", StringComparison.Ordinal) && p.Name.EndsWith(".Microsoft.DotNet.ILCompiler", StringComparison.Ordinal))).OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                if (packages.Length == 0 && framework.Name.Contains('/')) continue;
                writer.WriteStartObject(framework.Name); foreach (var package in packages) { writer.WritePropertyName(package.Name); package.Value.WriteTo(writer); } writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static string Digest(IReadOnlyDictionary<string, string> sources)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, hash) in sources.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (hash.Length != 64 || hash.Any(c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))) throw new IOException("Invalid source fingerprint.");
            digest.AppendData(Encoding.UTF8.GetBytes(path + "\n")); digest.AppendData(Convert.FromHexString(hash));
        }
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }
    public static void Save(string path, IReadOnlyDictionary<string, string> sources)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using (var w = new Utf8JsonWriter(file, new() { Indented = true }))
        {
            w.WriteStartObject(); w.WriteString("format", "velosim.coverage.sources.v1"); w.WriteString("scope", "repository_first_party_inputs_compiler_normalized_locks_excludes_generated_files_and_toolchain"); w.WriteString("source_sha256", Digest(sources));
            w.WriteStartArray("sources"); foreach (var (filePath, hash) in sources.OrderBy(p => p.Key, StringComparer.Ordinal)) { w.WriteStartObject(); w.WriteString("path", filePath); w.WriteString("sha256", hash); w.WriteEndObject(); } w.WriteEndArray(); w.WriteEndObject();
        }
        file.WriteByte((byte)'\n');
    }
    public static void VerifyCurrent(string root, string manifest)
    {
        if (new FileInfo(manifest).LinkTarget is not null) throw new IOException("Coverage source manifest cannot be a link.");
        if (new FileInfo(manifest).Length > 16 * 1024 * 1024) throw new IOException("Coverage source manifest exceeds size limit.");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest)); var r = document.RootElement;
        CoverageEvidence.Fields(r, "format", "scope", "source_sha256", "sources");
        if (r.GetProperty("format").GetString() != "velosim.coverage.sources.v1" || r.GetProperty("scope").GetString() != "repository_first_party_inputs_compiler_normalized_locks_excludes_generated_files_and_toolchain") throw new IOException("Unsupported coverage source manifest.");
        var recorded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in r.GetProperty("sources").EnumerateArray())
        {
            CoverageEvidence.Fields(entry, "path", "sha256");
            if (!recorded.TryAdd(entry.GetProperty("path").GetString() ?? throw new IOException("Missing source path."), entry.GetProperty("sha256").GetString() ?? throw new IOException("Missing source hash."))) throw new IOException("Duplicate coverage source path.");
        }
        if (Digest(recorded) != r.GetProperty("source_sha256").GetString() || Digest(Capture(root)) != Digest(recorded)) throw new IOException("Coverage source inputs changed; run make coverage again.");
    }
}
