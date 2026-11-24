using System.Security.Cryptography;
using System.Text.Json;

namespace VeloSim;

public sealed partial class DesignPipeline
{
    /// <summary>Retains structural exporter bytes and source provenance. Does not lower or qualify simulator semantics.</summary>
    public async Task<string> CompileRtlAsync(IReadOnlyList<string> sources, string top, string outputDirectory,
        string? surelog = null, string? exporter = null, int workers = 1, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(sources);
        if (!RtlDesign.Identifier(top)) throw new ArgumentException("Structural top must be an identifier.");
        if (sources.Count == 0 || sources.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Source files required.");
        if (workers is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(workers));
        string Tool(string command) => command.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0 ? Path.GetFullPath(command) : command;
        string? envExporter = Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL");
        string? explicitExporter = exporter;
        surelog = Tool(surelog ?? Environment.GetEnvironmentVariable("VELOSIM_SURELOG") ?? "surelog");
        exporter = Tool(exporter ?? envExporter ?? "velosim-uhdm-rtl");
        var explicitSources = sources.Select(Path.GetFullPath).ToArray();
        var inputs = ArtifactMetadata.SourceClosure(explicitSources);
        string directory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new IOException("Structural compilation output directory must be empty.");
        Directory.CreateDirectory(directory);
        string pending = Path.Combine(directory, "exported.rtl.json");
        string artifact = Path.Combine(directory, "design.rtl.json");
        string manifest = Path.Combine(directory, "compile-rtl.json");
        string pendingManifest = Path.Combine(directory, "compile-rtl.pending.json");
        var elaboration = await new SurelogFrontend(runner).ElaborateAsync(explicitSources,
            Path.Combine(directory, "surelog"), top, surelog, workers, timeout: timeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        bool useBuiltin = IsBuiltinUhdmTool(exporter)
            || (!customRunner && explicitExporter is null && envExporter is null && ArtifactMetadata.ResolveExecutable(exporter, directory) is null);
        if (useBuiltin)
        {
            exporter = "builtin";
            Uhdm.UhdmRtlLowerer.LowerFileTo(elaboration.UhdmPath, pending, top);
        }
        else
        {
            var result = await runner.RunAsync(exporter, [elaboration.UhdmPath, pending, top], directory,
                timeout ?? TimeSpan.FromMinutes(3), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Structural UHDM exporter failed ({result.ExitCode}): {result.StandardError}\n{result.StandardOutput}");
        }
        if (!File.Exists(pending)) throw new IOException("Structural exporter did not produce output.");
        if (new FileInfo(pending).Length > 4 * 1024 * 1024) throw new IrValidationException("Structural output exceeds 4 MiB.");
        byte[] bytes = await File.ReadAllBytesAsync(pending, cancellationToken).ConfigureAwait(false);
        ValidateStructuralEnvelope(bytes);
        var current = ArtifactMetadata.SourceClosure(explicitSources);
        if (!inputs.SequenceEqual(current)) throw new IOException("Source changed during structural compilation.");
        using var metadata = new MemoryStream();
        using (var json = new Utf8JsonWriter(metadata, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("schema", "velosim.compile-rtl.v1"); json.WriteString("format", "velosim.rtl.v1"); json.WriteString("top", top);
            json.WriteString("source_hash_scope", "explicit_sources_and_recursive_literal_relative_includes");
            json.WriteStartArray("sources");
            foreach (var source in inputs)
            {
                json.WriteStartObject(); json.WriteString("path", source.Path); json.WriteString("sha256", source.Sha256); json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteString("surelog", surelog); json.WriteString("exporter", exporter); json.WriteNumber("threads", workers);
            json.WriteString("artifact", "design.rtl.json");
            json.WriteString("artifact_sha256", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            json.WriteEndObject();
        }
        // The manifest is the completion marker; no successful artifact is published before validation.
        try
        {
            using (var stream = new FileStream(pendingManifest, FileMode.CreateNew, FileAccess.Write))
                await stream.WriteAsync(metadata.ToArray(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pending, artifact);
            File.Move(pendingManifest, manifest);
        }
        catch
        {
            File.Delete(artifact); File.Delete(manifest);
            throw;
        }
        return artifact;
    }

    // This is a transport envelope check. The exporter owns source-subset rejection;
    // consumers independently validate semantics. Preserve fields the simulation parser cannot consume.
    private static void ValidateStructuralEnvelope(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new IrValidationException("Structural output must be an object.");
            CheckDuplicates(root);
            if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != "velosim.rtl.v1")
                throw new IrValidationException("Structural exporter must produce velosim.rtl.v1.");
            JsonShape.Fields(root, ["format", "signals", "memories", "continuous", "clock", "body"], ["initialization", "combinational"]);
            foreach (string field in new[] { "signals", "memories", "continuous" })
                if (root.GetProperty(field).ValueKind != JsonValueKind.Array) throw new IrValidationException("Structural " + field + " must be an array.");
            if (root.GetProperty("signals").GetArrayLength() == 0 ||
                root.GetProperty("clock").ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(root.GetProperty("clock").GetString()) ||
                root.GetProperty("body").ValueKind != JsonValueKind.Object ||
                root.TryGetProperty("initialization", out var initial) && initial.ValueKind != JsonValueKind.Object ||
                root.TryGetProperty("combinational", out var comb) && comb.ValueKind != JsonValueKind.Array)
                throw new IrValidationException("Malformed structural RTL envelope.");
        }
        catch (JsonException ex) { throw new IrValidationException("Malformed structural RTL JSON: " + ex.Message); }
    }

    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new IrValidationException("Duplicate structural field: " + property.Name);
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicates(item);
    }
}
