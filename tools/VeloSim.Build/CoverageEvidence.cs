using System.Security.Cryptography;
using System.Text.Json;

public static class CoverageEvidence
{
    internal static void Fields(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new IOException("Coverage evidence requires an object.");
        var expected = names.ToHashSet(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject()) if (!expected.Remove(field.Name)) throw new IOException("Duplicate or unknown coverage evidence field: " + field.Name);
        if (expected.Count != 0) throw new IOException("Coverage evidence omits required fields.");
    }
    public static void Validate(string report, IEnumerable<string> nativeObjects, IEnumerable<string> managedObjects)
    {
        if (Path.GetFileName(report) != "Cobertura.xml") throw new IOException("Current coverage checks require the recorded Cobertura.xml report.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(report))!; var manifest = Path.Combine(directory, "evidence.json");
        if (new DirectoryInfo(directory).LinkTarget is not null || new FileInfo(manifest).LinkTarget is not null) throw new IOException("Coverage evidence directory and manifest cannot be links.");
        if (new FileInfo(manifest).Length > 1024 * 1024) throw new IOException("Coverage evidence exceeds 1 MiB limit.");
        using var document = JsonDocument.Parse(File.ReadAllText(manifest)); var root = document.RootElement;
        Fields(root, "format", "run_id", "jobs", "reports", "llvm_version", "native_objects", "managed_objects");
        if (root.GetProperty("format").GetString() != "velosim.coverage.evidence.v1" || string.IsNullOrWhiteSpace(root.GetProperty("run_id").GetString()) || root.GetProperty("jobs").GetInt32() is < 1 or > 256 || string.IsNullOrWhiteSpace(root.GetProperty("llvm_version").GetString())) throw new IOException("Unsupported coverage evidence metadata.");
        void Verify(JsonElement entries, IEnumerable<string> required, bool relative)
        {
            var expected = required.ToHashSet(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                Fields(entry, "path", "sha256"); var path = entry.GetProperty("path").GetString()!;
                if (!expected.Remove(path)) throw new IOException("Unexpected or duplicate coverage evidence file.");
                var file = relative ? Path.Combine(directory, path) : path;
                if (new FileInfo(file).LinkTarget is not null) throw new IOException("Coverage evidence files cannot be links.");
                using var input = File.OpenRead(file); var hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
                if (hash != entry.GetProperty("sha256").GetString()) throw new IOException("Coverage evidence file changed: " + path);
            }
            if (expected.Count != 0) throw new IOException("Coverage evidence omits required files.");
        }
        Verify(root.GetProperty("reports"), ["Cobertura.xml", "gate.json", "sources.json", "doctor.json"], true);
        Verify(root.GetProperty("native_objects"), nativeObjects, false); Verify(root.GetProperty("managed_objects"), managedObjects, false);
    }
}
