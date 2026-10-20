using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed record RegressionSuiteCase(string Name, string ManifestPath);
public sealed record RegressionSuiteManifest(string Name, IReadOnlyList<RegressionSuiteCase> Cases, int MaxParallelCases, int JobBudget)
{
    public SuiteSelection? Selection { get; init; }
    public static RegressionSuiteManifest Parse(string json, string baseDirectory)
    {
        try
        {
            using var document = JsonDocument.Parse(json); var root = document.RootElement;
            JsonShape.Fields(root, ["format", "name", "cases", "max_parallel_cases", "job_budget"]);
            if (root.GetProperty("format").GetString() != "velosim.suite.v1") throw new IrValidationException("Unsupported suite format.");
            var name = root.GetProperty("name").GetString(); var parallel = root.GetProperty("max_parallel_cases").GetInt32(); var budget = root.GetProperty("job_budget").GetInt32();
            var cases = new List<RegressionSuiteCase>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in root.GetProperty("cases").EnumerateArray())
            {
                JsonShape.Fields(entry, ["name", "manifest"]); var caseName = entry.GetProperty("name").GetString(); var path = entry.GetProperty("manifest").GetString();
                if (string.IsNullOrWhiteSpace(caseName) || caseName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')) || !names.Add(caseName) || string.IsNullOrWhiteSpace(path)) throw new IrValidationException("Invalid or duplicate suite case.");
                cases.Add(new(caseName, Path.GetFullPath(path, baseDirectory)));
            }
            if (string.IsNullOrWhiteSpace(name) || cases.Count is < 1 or > 256 || parallel is < 1 or > 256 || budget is < 1 or > 256 || parallel > budget) throw new IrValidationException("Invalid suite resource limits.");
            return new(name, cases.AsReadOnly(), parallel, budget);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException && ex is not IrValidationException) { throw new IrValidationException("Invalid suite manifest: " + ex.Message); }
    }
}

public interface IRegressionExecutor
{
    Task<CompiledRegressionResult> RunAsync(RegressionManifest manifest, string outputDirectory, string? surelog, string? normalizer, CancellationToken cancellationToken);
}
public sealed class RegressionExecutor(IProcessRunner? runner = null) : IRegressionExecutor
{
    public Task<CompiledRegressionResult> RunAsync(RegressionManifest manifest, string outputDirectory, string? surelog, string? normalizer, CancellationToken cancellationToken) => new CompiledRegression(runner).RunAsync(manifest, outputDirectory, surelog, normalizer, cancellationToken);
}
public enum RegressionFailureCategory { None, TraceMismatch, BackendUnavailable, Timeout, InvalidInput, ArtifactFailure, ToolFailure, Canceled }
public sealed record SuiteCaseResult(string Name, string OutputDirectory, CompiledRegressionResult? Result, RegressionFailureCategory FailureCategory, string? Error)
{
    public bool Passed => FailureCategory == RegressionFailureCategory.None && Result?.Passed == true;
}
public sealed record RegressionSuiteReport(string Name, int JobBudget, int MaxParallelCases, IReadOnlyList<SuiteCaseResult> Cases)
{
    public SuiteSelection? Selection { get; init; }
    public bool Passed => Cases.Count > 0 && Cases.All(c => c.Passed);
    public string Status => Cases.Any(c => c.FailureCategory == RegressionFailureCategory.Canceled) ? "canceled" : Passed ? "pass" : "fail";
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            w.WriteStartObject(); w.WriteString("format", "velosim.suite.report.v1"); w.WriteString("name", Name); w.WriteString("status", Status); w.WriteNumber("job_budget", JobBudget); w.WriteNumber("max_parallel_cases", MaxParallelCases);
            if (Selection is not null)
            {
                w.WriteStartObject("selection"); w.WriteNumber("original_case_count", Selection.OriginalCaseCount); w.WriteNumber("shard_index", Selection.ShardIndex); w.WriteNumber("shard_count", Selection.ShardCount);
                w.WriteStartArray("include"); foreach (var pattern in Selection.Include) w.WriteStringValue(pattern); w.WriteEndArray(); w.WriteStartArray("exclude"); foreach (var pattern in Selection.Exclude) w.WriteStringValue(pattern); w.WriteEndArray(); w.WriteEndObject();
            }
            w.WriteStartArray("cases"); foreach (var c in Cases)
            {
                w.WriteStartObject(); w.WriteString("name", c.Name); w.WriteString("status", c.FailureCategory == RegressionFailureCategory.Canceled ? "canceled" : c.Passed ? "pass" : "fail"); w.WriteString("output_directory", c.OutputDirectory); w.WriteString("failure_category", c.FailureCategory.ToString()); w.WriteString("error", c.Error);
                if (c.Result is not null)
                {
                    w.WriteString("ir_path", c.Result.IrPath); w.WriteStartArray("worker_results");
                    foreach (var r in c.Result.Results) { w.WriteStartObject(); w.WriteNumber("workers", r.Workers); w.WriteBoolean("passed", r.Mismatch is null); if (r.Mismatch is not null) { w.WriteNumber("first_mismatch_index", r.Mismatch.Index); w.WriteString("expected", r.Mismatch.Expected?.Bits); w.WriteString("actual", r.Mismatch.Actual?.Bits); } w.WriteEndObject(); }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
    public static RegressionFailureCategory Classify(Exception exception) => exception switch
    {
        DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or System.ComponentModel.Win32Exception => RegressionFailureCategory.BackendUnavailable,
        TimeoutException => RegressionFailureCategory.Timeout,
        ArgumentException or FormatException => RegressionFailureCategory.InvalidInput,
        IOException or UnauthorizedAccessException => RegressionFailureCategory.ArtifactFailure,
        _ => RegressionFailureCategory.ToolFailure
    };
}
public sealed class RegressionSuiteRunner(IRegressionExecutor? executor = null)
{
    private readonly IRegressionExecutor executor = executor ?? new RegressionExecutor();
    public async Task<RegressionSuiteReport> RunAsync(RegressionSuiteManifest suite, string outputDirectory, string? surelog = null, string? normalizer = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (suite.Cases.Count is < 1 or > 256 || suite.JobBudget is < 1 or > 256 || suite.MaxParallelCases is < 1 or > 256 || suite.MaxParallelCases > suite.JobBudget) throw new ArgumentException("Invalid suite resource budget.");
        if (suite.Cases.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != suite.Cases.Count || suite.Cases.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Name.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('_' or '-')))) throw new ArgumentException("Invalid suite case name.");
        var manifests = new List<RegressionManifest>();
        foreach (var c in suite.Cases)
        {
            var path = Path.GetFullPath(c.ManifestPath);
            var manifest = RegressionManifest.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false), Path.GetDirectoryName(path)!);
            if (manifest.WorkerCounts.Any(w => w > suite.JobBudget)) throw new ArgumentException($"Case {c.Name} exceeds total job budget {suite.JobBudget}.");
            manifests.Add(manifest);
        }
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Suite output must be empty.");
        Directory.CreateDirectory(output);
        var results = new SuiteCaseResult[suite.Cases.Count];
        async Task<SuiteCaseResult> Execute(int index)
        {
            var c = suite.Cases[index];
            var path = Path.Combine(output, c.Name);
            try
            {
                var result = await executor.RunAsync(manifests[index], path, surelog, normalizer, cancellationToken).ConfigureAwait(false);
                return new(c.Name, path, result, result.Passed ? RegressionFailureCategory.None : RegressionFailureCategory.TraceMismatch, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return new(c.Name, path, null, RegressionFailureCategory.Canceled, "Canceled while running."); }
            catch (Exception ex) { return new(c.Name, path, null, RegressionSuiteReport.Classify(ex), ex.Message); }
        }
        var running = new Dictionary<Task<SuiteCaseResult>, (int Index, int Cost)>(); int next = 0, available = suite.JobBudget;
        while (next < suite.Cases.Count || running.Count > 0)
        {
            while (!cancellationToken.IsCancellationRequested && next < suite.Cases.Count && running.Count < suite.MaxParallelCases && manifests[next].WorkerCounts.Max() <= available)
            {
                var cost = manifests[next].WorkerCounts.Max(); available -= cost; running.Add(Execute(next), (next, cost)); next++;
            }
            if (cancellationToken.IsCancellationRequested)
                while (next < suite.Cases.Count) { var c = suite.Cases[next]; results[next++] = new(c.Name, Path.Combine(output, c.Name), null, RegressionFailureCategory.Canceled, "Canceled before starting."); }
            if (running.Count == 0) break;
            var completed = await Task.WhenAny(running.Keys).ConfigureAwait(false); var slot = running[completed];
            results[slot.Index] = await completed.ConfigureAwait(false); available += slot.Cost; running.Remove(completed);
        }
        var report = new RegressionSuiteReport(suite.Name, suite.JobBudget, suite.MaxParallelCases, Array.AsReadOnly(results)) { Selection = suite.Selection };
        // Persist completed and canceled outcomes even after caller cancellation.
        await File.WriteAllTextAsync(Path.Combine(output, "suite-report.json"), report.ToJson(), CancellationToken.None).ConfigureAwait(false); return report;
    }
}
