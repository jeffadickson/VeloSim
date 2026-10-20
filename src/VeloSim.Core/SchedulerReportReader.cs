using System.Globalization;
using System.Text.Json;

namespace VeloSim;

public static class SchedulerReportReader
{
    public static SchedulerDifferentialReport Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
            JsonShape.Fields(root, ["schema", "status", "scope", "fixture", "requested_workers", "oracle", "icarus", "native", "errors", "differences", "metadata"], ["reduction_kind", "first_mismatch_prefix_steps", "toolchain"]);
            if (root.GetProperty("schema").GetString() != "velosim.scheduler.differential.v1" || root.GetProperty("scope").GetString() != "generated_four_state_SystemVerilog_edge_and_NBA_fixture_not_full_IEEE_scheduler") throw new IrValidationException("Unsupported scheduler report semantics.");
            var fixture = SchedulerFixture.Parse(root.GetProperty("fixture").GetRawText()); SchedulerSystemVerilog.Validate(fixture);
            var workers = root.GetProperty("requested_workers").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            if (workers.Length is < 1 or > 256 || workers.Distinct().Count() != workers.Length || workers.Any(w => w is < 1 or > 256) || (long)workers.Length * fixture.Drives.Count * fixture.Observed.Count > 10_000_000) throw new IrValidationException("Invalid scheduler report resource dimensions.");
            IReadOnlyList<SchedulerStep> Trace(JsonElement rows)
            {
                var result = new List<SchedulerStep>();
                foreach (var row in rows.EnumerateArray())
                {
                    JsonShape.Fields(row, ["tick", "activated_processes", "values"]); var tick = row.GetProperty("tick").GetUInt64(); var active = row.GetProperty("activated_processes").GetInt32(); var values = row.GetProperty("values").GetString();
                    if (tick != (ulong)result.Count + 1 || active < 0 || active > fixture.Processes.Count || values is null || values.Length != fixture.Observed.Count || values.Any(c => c is not ('0' or '1' or 'x' or 'z'))) throw new IrValidationException("Invalid scheduler report trace.");
                    result.Add(new(tick, active, values));
                }
                if (result.Count != fixture.Drives.Count) throw new IrValidationException("Incomplete scheduler report trace."); return result.AsReadOnly();
            }
            Dictionary<string, string> Strings(JsonElement data)
            {
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var entry in data.EnumerateObject()) if (entry.Value.GetString() is not { Length: > 0 } value || !result.TryAdd(entry.Name, value)) throw new IrValidationException("Invalid or duplicate report string entry."); return result;
            }
            FileFingerprint Fingerprint(JsonElement data)
            {
                JsonShape.Fields(data, ["path", "sha256"]); var path = data.GetProperty("path").GetString(); var hash = data.GetProperty("sha256").GetString();
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || hash is null || hash.Length != 64 || hash.Any(c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))) throw new IrValidationException("Invalid scheduler fingerprint."); return new(path, hash);
            }
            var oracle = Trace(root.GetProperty("oracle"));
            if (!oracle.SequenceEqual(SchedulerRegression.Oracle(fixture))) throw new IrValidationException("Recorded oracle differs from fixture semantics.");
            var native = new Dictionary<int, IReadOnlyList<SchedulerStep>>();
            foreach (var entry in root.GetProperty("native").EnumerateObject()) if (!int.TryParse(entry.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var worker) || worker.ToString(CultureInfo.InvariantCulture) != entry.Name || !workers.Contains(worker) || !native.TryAdd(worker, Trace(entry.Value))) throw new IrValidationException("Invalid scheduler native worker result.");
            var reference = root.GetProperty("icarus").ValueKind == JsonValueKind.Null ? null : Trace(root.GetProperty("icarus")); var errors = Strings(root.GetProperty("errors"));
            foreach (var name in errors.Keys)
                if (name == "icarus" && reference is not null || name.StartsWith("native-", StringComparison.Ordinal) && int.TryParse(name.AsSpan(7), out var count) && native.ContainsKey(count)) throw new IrValidationException("Backend has both success and failure evidence.");
            var metadata = root.GetProperty("metadata"); JsonShape.Fields(metadata, ["source_hash_scope", "sources", "native_library", "tool_versions", "tool_binaries", "commands"]);
            if (metadata.GetProperty("source_hash_scope").GetString() != "explicit_sources_and_recursive_literal_relative_includes") throw new IrValidationException("Unsupported scheduler evidence scope.");
            var commands = new List<RecordedCommand>();
            foreach (var entry in metadata.GetProperty("commands").EnumerateArray())
            {
                JsonShape.Fields(entry, ["executable", "arguments", "working_directory", "exit_code", "stdout", "stderr", "failure"]);
                var executable = entry.GetProperty("executable").GetString(); var cwd = entry.GetProperty("working_directory").GetString(); var stdout = entry.GetProperty("stdout").GetString(); var stderr = entry.GetProperty("stderr").GetString();
                if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(cwd) || !Path.IsPathRooted(cwd) || stdout is null || stderr is null) throw new IrValidationException("Invalid scheduler command evidence.");
                commands.Add(new(executable, entry.GetProperty("arguments").EnumerateArray().Select(e => e.GetString() ?? throw new IrValidationException("Null command argument.")).ToArray(), cwd, entry.GetProperty("exit_code").ValueKind == JsonValueKind.Null ? null : entry.GetProperty("exit_code").GetInt32(), stdout, stderr, entry.GetProperty("failure").GetString()));
            }
            var sources = metadata.GetProperty("sources").EnumerateArray().Select(Fingerprint).ToArray(); var binaries = metadata.GetProperty("tool_binaries").EnumerateArray().Select(Fingerprint).ToArray();
            if (sources.Select(s => s.Path).Distinct(StringComparer.Ordinal).Count() != sources.Length || binaries.Select(s => s.Path).Distinct(StringComparer.Ordinal).Count() != binaries.Length) throw new IrValidationException("Duplicate fingerprint paths.");
            var library = metadata.GetProperty("native_library"); var evidence = new ArtifactMetadata(sources, library.ValueKind == JsonValueKind.Null ? null : Fingerprint(library), commands, Strings(metadata.GetProperty("tool_versions")), binaries);
            SchedulerToolchain? toolchain = null;
            if (root.TryGetProperty("toolchain", out var configured))
            {
                JsonShape.Fields(configured, ["compiler", "runtime"]); var compiler = configured.GetProperty("compiler").GetString(); var runtime = configured.GetProperty("runtime").GetString();
                if (string.IsNullOrWhiteSpace(compiler) || string.IsNullOrWhiteSpace(runtime)) throw new IrValidationException("Invalid scheduler toolchain configuration."); toolchain = new(compiler, runtime);
            }
            var report = new SchedulerDifferentialReport(fixture, workers, oracle, reference, native, errors, evidence) { Toolchain = toolchain };
            if (report.Status != root.GetProperty("status").GetString()) throw new IrValidationException("Scheduler report status contradicts traces.");
            var recorded = new List<SchedulerDifferentialMismatch>();
            foreach (var entry in root.GetProperty("differences").EnumerateArray())
            {
                JsonShape.Fields(entry, ["comparison", "step", "expected", "actual"]); var index = entry.GetProperty("step").GetInt32();
                SchedulerStep One(JsonElement data) { var rows = data.EnumerateArray().ToArray(); if (rows.Length != 1) throw new IrValidationException("Difference must contain one trace step."); JsonShape.Fields(rows[0], ["tick", "activated_processes", "values"]); return new(rows[0].GetProperty("tick").GetUInt64(), rows[0].GetProperty("activated_processes").GetInt32(), rows[0].GetProperty("values").GetString()!); }
                recorded.Add(new(entry.GetProperty("comparison").GetString()!, index, One(entry.GetProperty("expected")), One(entry.GetProperty("actual"))));
            }
            if (!recorded.SequenceEqual(report.Differences)) throw new IrValidationException("Scheduler report differences contradict traces.");
            if (root.TryGetProperty("reduction_kind", out var reduction) && reduction.GetString() != "first_mismatch_prefix_not_general_delta_debugging" || root.TryGetProperty("first_mismatch_prefix_steps", out var prefix) && (prefix.ValueKind == JsonValueKind.Null ? (int?)null : prefix.GetInt32()) != report.FirstMismatchPrefixSteps) throw new IrValidationException("Invalid scheduler prefix reduction evidence.");
            return report;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException && ex is not IrValidationException) { throw new IrValidationException("Invalid scheduler report: " + ex.Message); }
    }
    public static string ReplayAvailability(SchedulerDifferentialReport report) => report.Reference is null || report.RequestedWorkers.Any(w => !report.Native.ContainsKey(w)) || report.Metadata.NativeLibrary is null || report.Toolchain is null ? "replay unavailable: backend results, native fingerprint, or toolchain configuration missing" : "replay prerequisites present; replay verifies current artifacts and tool roles";
}
