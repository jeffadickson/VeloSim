using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed class ElaborationMatrixReport
{
    public ElaborationMatrixPlan Plan { get; }
    public string Executable { get; }
    public FileFingerprint? Tool { get; }
    public string ToolVersion { get; }
    public IReadOnlyList<MatrixElaborationRecord> Records { get; }
    public string Status => Records.Any(r => r.Status == "canceled") ? "canceled" : Records.All(r => r.Status == "pass") ? "pass" : "fail";
    public ElaborationMatrixReport(ElaborationMatrixPlan plan, string executable, FileFingerprint? tool, string toolVersion, IReadOnlyList<MatrixElaborationRecord> records)
    {
        var expected = plan.Cases.SelectMany(c => plan.Workers.Select(w => (c.Name, c.Top, Workers: w))).ToHashSet();
        if (records.Count != expected.Count || records.Select(r => (r.Case, r.Top, r.Workers)).Distinct().Count() != records.Count || records.Any(r => !expected.Contains((r.Case, r.Top, r.Workers)) || r.Status is not ("pass" or "fail" or "canceled") || !double.IsFinite(r.ElapsedSeconds) || r.ElapsedSeconds < 0 || !Path.IsPathRooted(r.OutputDirectory) || r.Sources.Count == 0 || r.Status == "pass" && (r.Uhdm is null || r.UhdmBytes is null or <= 0 || r.Error is not null || r.ErrorCategory is not null) || r.Status != "pass" && (string.IsNullOrWhiteSpace(r.Error) || string.IsNullOrWhiteSpace(r.ErrorCategory)))) throw new IrValidationException("Invalid or incomplete matrix records.");
        static bool Invalid(FileFingerprint file) => string.IsNullOrWhiteSpace(file.Path) || !Path.IsPathRooted(file.Path) || string.IsNullOrEmpty(file.Sha256) || file.Sha256.Length != 64 || file.Sha256.Any(c => !char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'));
        if (string.IsNullOrWhiteSpace(executable) || toolVersion is null || tool is not null && Invalid(tool) || records.Any(r => r.Sources.Any(Invalid) || r.Sources.Select(s => s.Path).Distinct(StringComparer.Ordinal).Count() != r.Sources.Count || r.Uhdm is not null && Invalid(r.Uhdm))) throw new IrValidationException("Invalid matrix fingerprint.");
        foreach (var record in records)
        {
            var entry = plan.Cases.First(c => c.Name == record.Case);
            if (entry.Sources.Any(s => !record.Sources.Any(f => f.Path == s)) || record.Commands.Any(c => string.IsNullOrWhiteSpace(c.Executable) || !Path.IsPathRooted(c.WorkingDirectory) || c.StandardOutput is null || c.StandardError is null || c.Arguments.Any(a => a is null))) throw new IrValidationException("Invalid matrix source or command evidence.");
            if (record.Status == "pass")
            {
                bool Option(IReadOnlyList<string> args, string option, string value) { for (int i = 0; i + 1 < args.Count; i++) if (args[i] == option && args[i + 1] == value) return true; return false; }
                if (!record.Commands.Any(c => c.ExitCode == 0 && c.Arguments.Contains("-elabuhdm") && Option(c.Arguments, "-top", record.Top) && Option(c.Arguments, "-mt", record.Workers.ToString(System.Globalization.CultureInfo.InvariantCulture)) && entry.Sources.All(c.Arguments.Contains))) throw new IrValidationException("Passing matrix record lacks successful elaboration command.");
                var relative = Path.GetRelativePath(record.OutputDirectory, record.Uhdm!.Path);
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new IrValidationException("UHDM artifact lies outside run directory.");
            }
            else if (record.Uhdm is not null || record.UhdmBytes is not null) throw new IrValidationException("Failed matrix record cannot claim a completed artifact.");
        }
        Plan = plan; Executable = executable; Tool = tool; ToolVersion = toolVersion; Records = Array.AsReadOnly(records.ToArray());
    }
    public string ToJson()
    {
        using var stream = new MemoryStream(); using (var w = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            void Fingerprint(FileFingerprint file) { w.WriteStartObject(); w.WriteString("path", file.Path); w.WriteString("sha256", file.Sha256); w.WriteEndObject(); }
            w.WriteStartObject(); w.WriteString("format", "velosim.elaboration.matrix.report.v1"); w.WriteString("scope", "elaboration_only_not_simulation_correctness"); w.WriteString("status", Status); w.WritePropertyName("plan"); w.WriteRawValue(Plan.ToJson()); w.WriteString("executable", Executable); w.WritePropertyName("tool"); if (Tool is null) w.WriteNullValue(); else Fingerprint(Tool); w.WriteString("tool_version", ToolVersion);
            w.WriteStartArray("records"); foreach (var r in Records)
            {
                w.WriteStartObject(); w.WriteString("case", r.Case); w.WriteString("top", r.Top); w.WriteNumber("workers", r.Workers); w.WriteString("status", r.Status); w.WriteString("output_directory", r.OutputDirectory); w.WriteNumber("elapsed_seconds", r.ElapsedSeconds);
                w.WriteStartArray("sources"); foreach (var file in r.Sources) Fingerprint(file); w.WriteEndArray(); w.WritePropertyName("uhdm"); if (r.Uhdm is null) w.WriteNullValue(); else Fingerprint(r.Uhdm);
                if (r.UhdmBytes is { } size) w.WriteNumber("uhdm_bytes", size); else w.WriteNull("uhdm_bytes"); w.WriteString("error_category", r.ErrorCategory); w.WriteString("error", r.Error);
                w.WriteStartArray("commands"); foreach (var command in r.Commands)
                {
                    w.WriteStartObject(); w.WriteString("executable", command.Executable); w.WriteStartArray("arguments"); foreach (var a in command.Arguments) w.WriteStringValue(a); w.WriteEndArray(); w.WriteString("working_directory", command.WorkingDirectory);
                    if (command.ExitCode is { } exit) w.WriteNumber("exit_code", exit); else w.WriteNull("exit_code"); w.WriteString("stdout", command.StandardOutput); w.WriteString("stderr", command.StandardError); w.WriteString("failure", command.Failure); w.WriteEndObject();
                }
                w.WriteEndArray(); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
    public static ElaborationMatrixReport Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json); var root = document.RootElement;
            JsonShape.Fields(root, ["format", "scope", "status", "plan", "executable", "tool", "tool_version", "records"]);
            if (root.GetProperty("format").GetString() != "velosim.elaboration.matrix.report.v1" || root.GetProperty("scope").GetString() != "elaboration_only_not_simulation_correctness") throw new IrValidationException("Unsupported matrix report semantics.");
            FileFingerprint Fingerprint(JsonElement file) { JsonShape.Fields(file, ["path", "sha256"]); return new(file.GetProperty("path").GetString()!, file.GetProperty("sha256").GetString()!); }
            var records = new List<MatrixElaborationRecord>();
            foreach (var record in root.GetProperty("records").EnumerateArray())
            {
                JsonShape.Fields(record, ["case", "top", "workers", "status", "output_directory", "elapsed_seconds", "sources", "uhdm", "uhdm_bytes", "error_category", "error", "commands"]);
                var commands = new List<RecordedCommand>();
                foreach (var command in record.GetProperty("commands").EnumerateArray())
                {
                    JsonShape.Fields(command, ["executable", "arguments", "working_directory", "exit_code", "stdout", "stderr", "failure"]);
                    commands.Add(new(command.GetProperty("executable").GetString()!, command.GetProperty("arguments").EnumerateArray().Select(a => a.GetString() ?? throw new IrValidationException("Invalid command argument.")).ToArray(), command.GetProperty("working_directory").GetString()!, command.GetProperty("exit_code").ValueKind == JsonValueKind.Null ? null : command.GetProperty("exit_code").GetInt32(), command.GetProperty("stdout").GetString()!, command.GetProperty("stderr").GetString()!, command.GetProperty("failure").GetString()));
                }
                var artifact = record.GetProperty("uhdm"); var bytes = record.GetProperty("uhdm_bytes");
                records.Add(new(record.GetProperty("case").GetString()!, record.GetProperty("top").GetString()!, record.GetProperty("workers").GetInt32(), record.GetProperty("status").GetString()!, record.GetProperty("output_directory").GetString()!, record.GetProperty("elapsed_seconds").GetDouble(), record.GetProperty("sources").EnumerateArray().Select(Fingerprint).ToArray(), artifact.ValueKind == JsonValueKind.Null ? null : Fingerprint(artifact), bytes.ValueKind == JsonValueKind.Null ? null : bytes.GetInt64(), commands, record.GetProperty("error_category").GetString(), record.GetProperty("error").GetString()));
            }
            var tool = root.GetProperty("tool");
            var report = new ElaborationMatrixReport(ElaborationMatrixPlan.Parse(root.GetProperty("plan").GetRawText(), Environment.CurrentDirectory), root.GetProperty("executable").GetString()!, tool.ValueKind == JsonValueKind.Null ? null : Fingerprint(tool), root.GetProperty("tool_version").GetString()!, records);
            if (report.Status != root.GetProperty("status").GetString()) throw new IrValidationException("Matrix status differs from record results.");
            return report;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException && ex is not IrValidationException) { throw new IrValidationException("Invalid matrix report: " + ex.Message); }
    }
}
