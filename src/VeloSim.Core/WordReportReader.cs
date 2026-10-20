using System.Globalization;
using System.Text.Json;

namespace VeloSim;

public static class WordReportReader
{
    public static bool TraceEqual(IReadOnlyList<WordHdlStep> a, IReadOnlyList<WordHdlStep> b) => a.Count == b.Count && !a.Where((step, index) => step.Tick != b[index].Tick || step.ActivatedProcesses != b[index].ActivatedProcesses || !step.Values.SequenceEqual(b[index].Values)).Any();
    public static WordDifferentialReport Parse(string json, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested(); if (json.Length > WordEvidenceLimits.MaxReportCharacters) throw new IrValidationException("Word report exceeds text budget."); using var document = JsonDocument.Parse(json); var root = document.RootElement;
            JsonShape.Fields(root, ["schema", "status", "source_mode", "fixture", "oracle", "icarus", "native", "errors", "differences", "toolchain", "metadata"], ["reduction_kind", "first_mismatch_prefix_steps"]);
            if (root.GetProperty("schema").GetString() != "velosim.word.differential.v1" || root.GetProperty("source_mode").GetString() != "generated_strict_SystemVerilog_fixture_not_frontend_lowering") throw new IrValidationException("Unsupported word differential semantics.");
            var fixture = WordFixture.Parse(root.GetProperty("fixture").GetRawText()); WordSystemVerilog.Validate(fixture); WordEvidenceLimits.Preflight(root, fixture);
            IReadOnlyList<WordHdlStep> Trace(JsonElement data)
            {
                var trace = new List<WordHdlStep>();
                foreach (var row in data.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested(); JsonShape.Fields(row, ["tick", "activated_processes", "values"]); var tick = row.GetProperty("tick").GetUInt64(); var active = row.GetProperty("activated_processes").GetInt32(); var values = row.GetProperty("values").EnumerateArray().Select(e => e.GetString()!).ToArray();
                    if (tick != (ulong)trace.Count + 1 || active < 0 || active > fixture.Design.Processes.Count || values.Length != fixture.Design.Signals.Count || values.Where((value, i) => value is null || value.Length != fixture.Design.Signals[i].Initial.Width || value.Any(c => c is not ('0' or '1' or 'x' or 'z'))).Any()) throw new IrValidationException("Invalid word differential trace row."); trace.Add(new(tick, active, values));
                }
                if (trace.Count != fixture.Drives.Count) throw new IrValidationException("Incomplete word differential trace."); return trace.AsReadOnly();
            }
            Dictionary<string, string> Strings(JsonElement data)
            {
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var entry in data.EnumerateObject()) if (entry.Value.GetString() is not { Length: > 0 } value || !result.TryAdd(entry.Name, value)) throw new IrValidationException("Invalid or duplicate report string entry."); return result;
            }
            FileFingerprint Fingerprint(JsonElement data)
            {
                JsonShape.Fields(data, ["path", "sha256"]); var path = data.GetProperty("path").GetString(); var hash = data.GetProperty("sha256").GetString();
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || hash is null || hash.Length != 64 || hash.Any(c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))) throw new IrValidationException("Invalid word artifact fingerprint."); return new(path, hash);
            }
            var oracle = Trace(root.GetProperty("oracle")); if (!TraceEqual(oracle, WordHdlTrace.Oracle(fixture, cancellationToken))) throw new IrValidationException("Recorded word oracle differs from fixture semantics.");
            var reference = root.GetProperty("icarus").ValueKind == JsonValueKind.Null ? null : Trace(root.GetProperty("icarus")); var native = new Dictionary<int, IReadOnlyList<WordHdlStep>>();
            foreach (var entry in root.GetProperty("native").EnumerateObject()) if (!int.TryParse(entry.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var worker) || entry.Name != worker.ToString(CultureInfo.InvariantCulture) || !fixture.Workers.Contains(worker) || !native.TryAdd(worker, Trace(entry.Value))) throw new IrValidationException("Invalid native word worker result.");
            var errors = Strings(root.GetProperty("errors"));
            foreach (var error in errors.Keys)
            {
                if (error == "icarus") { if (reference is not null) throw new IrValidationException("Reference has contradictory success and failure evidence."); }
                else if (error is not ("native_metadata" or "canceled") && (!error.StartsWith("native-", StringComparison.Ordinal) || !int.TryParse(error.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out var worker) || error != "native-" + worker.ToString(CultureInfo.InvariantCulture) || !fixture.Workers.Contains(worker) || native.ContainsKey(worker))) throw new IrValidationException("Invalid native word failure evidence.");
            }
            var toolchain = root.GetProperty("toolchain"); JsonShape.Fields(toolchain, ["compiler", "runtime"]); var compiler = toolchain.GetProperty("compiler").GetString(); var runtime = toolchain.GetProperty("runtime").GetString(); if (string.IsNullOrWhiteSpace(compiler) || string.IsNullOrWhiteSpace(runtime)) throw new IrValidationException("Invalid word toolchain configuration.");
            var metadata = root.GetProperty("metadata"); JsonShape.Fields(metadata, ["source_hash_scope", "sources", "native_library", "tool_versions", "tool_binaries", "commands"]); if (metadata.GetProperty("source_hash_scope").GetString() != "explicit_sources_and_recursive_literal_relative_includes") throw new IrValidationException("Unsupported word metadata scope.");
            var sources = metadata.GetProperty("sources").EnumerateArray().Select(Fingerprint).ToArray(); var binaries = metadata.GetProperty("tool_binaries").EnumerateArray().Select(Fingerprint).ToArray();
            if (sources.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count() != sources.Length || binaries.Select(f => f.Path).Distinct(StringComparer.Ordinal).Count() != binaries.Length) throw new IrValidationException("Duplicate word artifact paths.");
            var commands = new List<RecordedCommand>();
            foreach (var entry in metadata.GetProperty("commands").EnumerateArray())
            {
                JsonShape.Fields(entry, ["executable", "arguments", "working_directory", "exit_code", "stdout", "stderr", "failure"]); var executable = entry.GetProperty("executable").GetString(); var cwd = entry.GetProperty("working_directory").GetString(); var stdout = entry.GetProperty("stdout").GetString(); var stderr = entry.GetProperty("stderr").GetString();
                if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(cwd) || !Path.IsPathRooted(cwd) || stdout is null || stderr is null) throw new IrValidationException("Invalid word command evidence.");
                var exit = entry.GetProperty("exit_code").ValueKind == JsonValueKind.Null ? (int?)null : entry.GetProperty("exit_code").GetInt32(); var failure = entry.GetProperty("failure").GetString();
                if (exit is null && string.IsNullOrWhiteSpace(failure) || exit is not null && failure is not null) throw new IrValidationException("Contradictory word command completion evidence.");
                commands.Add(new(executable, entry.GetProperty("arguments").EnumerateArray().Select(e => e.GetString() ?? throw new IrValidationException("Null command argument.")).ToArray(), cwd, exit, stdout, stderr, failure));
            }
            var library = metadata.GetProperty("native_library"); var report = new WordDifferentialReport(fixture, oracle, reference, native, errors, new(sources, library.ValueKind == JsonValueKind.Null ? null : Fingerprint(library), commands, Strings(metadata.GetProperty("tool_versions")), binaries), new(compiler, runtime));
            var recorded = new List<WordDifference>(); foreach (var entry in root.GetProperty("differences").EnumerateArray()) { JsonShape.Fields(entry, ["comparison", "step", "signal", "expected", "actual"]); recorded.Add(new(entry.GetProperty("comparison").GetString()!, entry.GetProperty("step").GetInt32(), entry.GetProperty("signal").GetString()!, entry.GetProperty("expected").GetString()!, entry.GetProperty("actual").GetString()!)); }
            if (root.GetProperty("status").GetString() != report.Status || !recorded.SequenceEqual(report.Differences)) throw new IrValidationException("Word report conclusions disagree with traces/checkpoints.");
            if (root.TryGetProperty("reduction_kind", out var reduction) && reduction.GetString() != "first_mismatch_prefix_not_general_delta_debugging" || root.TryGetProperty("first_mismatch_prefix_steps", out var prefix) && (prefix.ValueKind == JsonValueKind.Null ? (int?)null : prefix.GetInt32()) != report.FirstMismatchPrefixSteps) throw new IrValidationException("Invalid word prefix metadata."); return report;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException && ex is not IrValidationException) { throw new IrValidationException("Invalid word report: " + ex.Message); }
    }
    public static string ReplayAvailability(WordDifferentialReport report) => report.Reference is null || report.Fixture.Workers.Any(w => !report.Native.ContainsKey(w)) || report.Metadata.NativeLibrary is null ? "replay unavailable: backend results or native fingerprint missing" : "replay prerequisites present; replay verifies current artifacts and tool roles";
}
