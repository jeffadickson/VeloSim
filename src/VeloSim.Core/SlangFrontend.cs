using System.Text.Json;

namespace VeloSim;

public sealed record SlangSummary(string? RootKind, IReadOnlyList<string> TopInstances);
public sealed class SlangFrontend(IProcessRunner? runner = null)
{
    private readonly IProcessRunner runner = runner ?? new ProcessRunner();
    public async Task<JsonDocument> CompileAsync(IReadOnlyList<string> sources, string? top = null, string? executable = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (sources.Count == 0 || sources.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Sources required.");
        var directory = Path.Combine(Path.GetTempPath(), "velosim-slang-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "ast.json");
            var args = new List<string> { "--std", "1800-2023", "--ast-json", path, "--ast-json-source-info" };
            if (top is not null) { if (string.IsNullOrWhiteSpace(top)) throw new ArgumentException("Invalid top."); args.AddRange(["--top", top]); }
            args.AddRange(sources.Select(Path.GetFullPath));
            var result = await runner.RunAsync(executable ?? Environment.GetEnvironmentVariable("VELOSIM_SLANG") ?? "slang", args, directory, timeout ?? TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Slang failed ({result.ExitCode}): {result.StandardError}\n{result.StandardOutput}");
            var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
            try { Summarize(document.RootElement); return document; } catch { document.Dispose(); throw; }
        }
        finally { Directory.Delete(directory, true); }
    }
    public static SlangSummary Summarize(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("design", out var design) || design.ValueKind != JsonValueKind.Object) throw new IrValidationException("Slang AST requires design object.");
        var names = new List<string>();
        if (design.TryGetProperty("members", out var members))
            foreach (var member in members.EnumerateArray())
                if (member.ValueKind == JsonValueKind.Object && member.TryGetProperty("kind", out var kind) && kind.GetString() == "Instance") names.Add(member.GetProperty("name").GetString() ?? throw new IrValidationException("Instance name required."));
        return new(design.TryGetProperty("kind", out var k) ? k.GetString() : null, names.AsReadOnly());
    }
}
