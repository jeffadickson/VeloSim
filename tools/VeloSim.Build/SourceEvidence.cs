using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public static class SourceEvidence
{
    public static IReadOnlyDictionary<string, string> Capture(string root)
    {
        root = Path.GetFullPath(root); var paths = new List<string>();
        var extensions = new HashSet<string>([".cs", ".csproj", ".cpp", ".c", ".h", ".map", ".sv", ".svh", ".json", ".cmake", ".props", ".targets", ".config"], StringComparer.Ordinal);
        void Walk(DirectoryInfo directory)
        {
            if (directory.LinkTarget is not null) throw new IOException("Coverage source directories cannot be symbolic links.");
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo child) { if (child.Name is not ("bin" or "obj" or "__pycache__" or "TestResults")) Walk(child); }
                else if (entry is FileInfo file && (extensions.Contains(file.Extension) || file.Name is "CMakeLists.txt" or "Makefile")) paths.Add(file.FullName);
            }
        }
        foreach (var relative in new[] { "src", "native", "tests", "tools/VeloSim.Build", "examples" }) { var path = Path.Combine(root, relative); if (Directory.Exists(path)) Walk(new(path)); }
        paths.AddRange(new[] { "CMakeLists.txt", "Makefile", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json", "NuGet.config", "coverage.runsettings", "VeloSim.slnx", ".config/dotnet-tools.json" }.Select(p => Path.Combine(root, p)).Where(File.Exists));
        return paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToDictionary(p => Path.GetRelativePath(root, p).Replace('\\', '/'), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant(), StringComparer.Ordinal);
    }
    public static string Digest(IReadOnlyDictionary<string, string> sources)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, hash) in sources.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (hash.Length != 64 || hash.Any(c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))) throw new IOException("Invalid source fingerprint.");
            digest.AppendData(Encoding.UTF8.GetBytes(path + "\n")); digest.AppendData(Convert.FromHexString(hash));
        }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }
    public static void Save(string path, IReadOnlyDictionary<string, string> sources)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            w.WriteStartObject(); w.WriteString("format", "velosim.coverage.sources.v1"); w.WriteString("scope", "repository_first_party_inputs_excludes_generated_files_and_toolchain"); w.WriteString("source_sha256", Digest(sources));
            w.WriteStartArray("sources"); foreach (var (file, hash) in sources.OrderBy(p => p.Key, StringComparer.Ordinal)) { w.WriteStartObject(); w.WriteString("path", file); w.WriteString("sha256", hash); w.WriteEndObject(); } w.WriteEndArray(); w.WriteEndObject();
        }
        File.WriteAllText(path, Encoding.UTF8.GetString(stream.ToArray()) + "\n");
    }
    public static void VerifyCurrent(string root, string manifest)
    {
        if (new FileInfo(manifest).Length > 16 * 1024 * 1024) throw new IOException("Coverage source manifest exceeds size limit.");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest)); var r = document.RootElement;
        if (r.GetProperty("format").GetString() != "velosim.coverage.sources.v1" || r.GetProperty("scope").GetString() != "repository_first_party_inputs_excludes_generated_files_and_toolchain") throw new IOException("Unsupported coverage source manifest.");
        var recorded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in r.GetProperty("sources").EnumerateArray()) if (!recorded.TryAdd(entry.GetProperty("path").GetString() ?? throw new IOException("Missing source path."), entry.GetProperty("sha256").GetString() ?? throw new IOException("Missing source hash."))) throw new IOException("Duplicate coverage source path.");
        if (Digest(recorded) != r.GetProperty("source_sha256").GetString() || Digest(Capture(root)) != Digest(recorded)) throw new IOException("Coverage source inputs changed; run make coverage again.");
    }
}
