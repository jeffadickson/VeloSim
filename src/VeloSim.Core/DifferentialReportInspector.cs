using System.Text.Json;

namespace VeloSim;

public sealed record DifferentialInspection(string Status, uint Seed, int Cycles, int Workers, int? ObservedWidth, IReadOnlyList<string> ReplayUnavailableReasons)
{
    public string ReplayMessage => ReplayUnavailableReasons.Count == 0 ? "replay prerequisites present; replay verifies current artifacts and tools" : "replay unavailable: " + string.Join("; ", ReplayUnavailableReasons);
}

public static class DifferentialReportInspector
{
    // Inspection checks recorded structure and conclusions. It does not read artifacts or load native libraries.
    public static DifferentialInspection Inspect(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json); var root = document.RootElement;
            JsonShape.Fields(root, ["schema", "status", "velosim_source_mode", "verilator_semantics", "seed", "cycles", "workers", "reduction_kind", "first_mismatch_prefix_cycles", "requested_engines", "results", "errors", "differences", "observed_width", "ir_artifact", "counter_specification", "failure_stages"], ["metadata", "configuration"]);
            if (root.GetProperty("schema").GetString() != "velosim.diff.v3" || root.GetProperty("velosim_source_mode").GetString() != "surelog_hdl_compilation" || root.GetProperty("verilator_semantics").GetString() != "two_state_reference_not_four_state_conformance" || root.GetProperty("reduction_kind").GetString() != "first_mismatch_prefix_not_general_delta_debugging") throw new IrValidationException("Unsupported differential report semantics.");
            var seed = root.GetProperty("seed").GetUInt32(); var cycles = root.GetProperty("cycles").GetInt32(); var workers = root.GetProperty("workers").GetInt32();
            int? width = root.GetProperty("observed_width").ValueKind == JsonValueKind.Null ? null : root.GetProperty("observed_width").GetInt32();
            if (cycles is < 1 or > 10_000_000 || workers is < 1 or > 256 || width is < 1 or > 64) throw new IrValidationException("Invalid report dimensions.");
            var engines = root.GetProperty("requested_engines").EnumerateArray().Select(e => e.GetString()!).ToArray();
            if (engines.Length < 2 || !engines.Contains("velosim") || engines.Distinct().Count() != engines.Length || engines.Any(e => e is not ("velosim" or "icarus" or "verilator"))) throw new IrValidationException("Invalid report engines.");
            var traces = new Dictionary<string, IReadOnlyList<TraceSample>>(StringComparer.Ordinal);
            foreach (var engine in root.GetProperty("results").EnumerateObject())
                if (!engines.Contains(engine.Name) || !traces.TryAdd(engine.Name, engine.Value.EnumerateArray().Select((row, i) => new TraceSample(i, row.GetString() ?? throw new IrValidationException("Null trace value."))).ToArray())) throw new IrValidationException("Invalid or duplicate result engine.");
            Dictionary<string, string> Strings(JsonElement element)
            {
                if (element.ValueKind != JsonValueKind.Object) throw new IrValidationException("Expected report dictionary.");
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var entry in element.EnumerateObject()) if (entry.Value.GetString() is not { Length: > 0 } value || !result.TryAdd(entry.Name, value)) throw new IrValidationException("Invalid or duplicate report dictionary entry.");
                return result;
            }
            var errors = Strings(root.GetProperty("errors")); var stages = Strings(root.GetProperty("failure_stages"));
            if (errors.Keys.Any(traces.ContainsKey) || stages.Any(s => !errors.ContainsKey(s.Key) || s.Value is not ("frontend_compilation" or "counter_contract" or "native_execution" or "dependency_skipped" or "reference_trace" or "reference_execution"))) throw new IrValidationException("Contradictory failure evidence.");
            IReadOnlyList<TraceSample>? specification = root.GetProperty("counter_specification").ValueKind == JsonValueKind.Null ? null : root.GetProperty("counter_specification").EnumerateArray().Select((row, i) => new TraceSample(i, row.GetString()!)).ToArray();
            if (specification is not null && (width is null || !specification.SequenceEqual(CounterSpecification.Generate(CounterStimulus.Create(seed, cycles), width.Value)))) throw new IrValidationException("Invalid recorded counter specification.");
            var computed = new CounterDifferentialReport(seed, cycles, workers, engines, traces, errors) { ObservedWidth = width, Specification = specification };
            computed.ValidateTraceShape();
            var status = root.GetProperty("status").GetString();
            int? prefix = root.GetProperty("first_mismatch_prefix_cycles").ValueKind == JsonValueKind.Null ? null : root.GetProperty("first_mismatch_prefix_cycles").GetInt32();
            if (status != computed.Status || prefix != computed.FirstMismatchPrefixCycles) throw new IrValidationException("Recorded report conclusions disagree with traces.");
            var differences = new Dictionary<string, TraceMismatch>(StringComparer.Ordinal);
            foreach (var entry in root.GetProperty("differences").EnumerateArray())
            {
                JsonShape.Fields(entry, ["engines", "cycle", "expected", "actual"]); var step = entry.GetProperty("cycle").GetInt32(); var expected = entry.GetProperty("expected").GetString(); var actual = entry.GetProperty("actual").GetString();
                if (!differences.TryAdd(entry.GetProperty("engines").GetString()!, new(step, expected is null ? null : new(step, expected), actual is null ? null : new(step, actual)))) throw new IrValidationException("Duplicate difference entry.");
            }
            if (differences.Count != computed.Differences.Count || differences.Any(d => !computed.Differences.TryGetValue(d.Key, out var mismatch) || mismatch != d.Value)) throw new IrValidationException("Recorded differences disagree with traces.");
            bool HasFingerprint(JsonElement file)
            {
                if (file.ValueKind == JsonValueKind.Null) return false;
                JsonShape.Fields(file, ["path", "sha256"]); var path = file.GetProperty("path").GetString(); var hash = file.GetProperty("sha256").GetString();
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || hash is null || hash.Length != 64 || hash.Any(c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))) throw new IrValidationException("Invalid artifact fingerprint.");
                return true;
            }
            var hasIr = HasFingerprint(root.GetProperty("ir_artifact")); var hasNative = false;
            if (root.TryGetProperty("metadata", out var metadata))
            {
                JsonShape.Fields(metadata, ["source_hash_scope", "sources", "native_library", "tool_versions", "tool_binaries", "commands"]);
                if (metadata.GetProperty("source_hash_scope").GetString() != "explicit_sources_and_recursive_literal_relative_includes") throw new IrValidationException("Unsupported source evidence scope.");
                foreach (var file in metadata.GetProperty("sources").EnumerateArray()) HasFingerprint(file);
                foreach (var file in metadata.GetProperty("tool_binaries").EnumerateArray()) HasFingerprint(file);
                hasNative = HasFingerprint(metadata.GetProperty("native_library"));
                _ = Strings(metadata.GetProperty("tool_versions"));
                foreach (var command in metadata.GetProperty("commands").EnumerateArray())
                {
                    JsonShape.Fields(command, ["executable", "arguments", "working_directory", "exit_code", "stdout", "stderr", "failure"]);
                    if (string.IsNullOrWhiteSpace(command.GetProperty("executable").GetString()) || !Path.IsPathRooted(command.GetProperty("working_directory").GetString()!)) throw new IrValidationException("Invalid recorded command.");
                    foreach (var argument in command.GetProperty("arguments").EnumerateArray()) if (argument.GetString() is null) throw new IrValidationException("Null command argument.");
                    if (command.GetProperty("exit_code").ValueKind != JsonValueKind.Null) _ = command.GetProperty("exit_code").GetInt32();
                    if (command.GetProperty("stdout").GetString() is null || command.GetProperty("stderr").GetString() is null) throw new IrValidationException("Null command output.");
                    _ = command.GetProperty("failure").GetString();
                }
            }
            bool hasConfig = root.TryGetProperty("configuration", out var configuration);
            if (hasConfig) JsonShape.Fields(configuration, ["top", "sources", "build_jobs", "surelog", "normalizer", "toolchain_paths"]);
            var reasons = new List<string>();
            if (width is null) reasons.Add("counter width unresolved"); if (!hasIr) reasons.Add("compiled IR fingerprint missing"); if (!hasNative) reasons.Add("native library fingerprint missing"); if (!hasConfig) reasons.Add("replay configuration missing"); if (engines.Any(e => !traces.ContainsKey(e))) reasons.Add("some requested engines did not execute");
            return new(status!, seed, cycles, workers, width, reasons.AsReadOnly());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException && ex is not IrValidationException) { throw new IrValidationException("Invalid differential report: " + ex.Message); }
    }
}
