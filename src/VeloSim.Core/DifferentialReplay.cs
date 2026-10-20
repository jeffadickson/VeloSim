using System.Text.Json;

namespace VeloSim;

public sealed record DifferentialReplayConfiguration(string Top, IReadOnlyList<string> Sources, int BuildJobs, string? Surelog, string? Normalizer, IReadOnlyDictionary<string, string>? ToolchainPaths = null)
{
    internal void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject("configuration"); w.WriteString("top", Top); w.WriteNumber("build_jobs", BuildJobs); w.WriteString("surelog", Surelog); w.WriteString("normalizer", Normalizer);
        w.WriteStartArray("sources"); foreach (var source in Sources) w.WriteStringValue(source); w.WriteEndArray();
        w.WriteStartObject("toolchain_paths"); foreach (var (tool, path) in ToolchainPaths ?? new Dictionary<string, string>()) w.WriteString(tool, path); w.WriteEndObject(); w.WriteEndObject();
    }
}
public sealed record DifferentialReplayInput(uint Seed, int Cycles, int Workers, IReadOnlyList<string> Engines, DifferentialReplayConfiguration Configuration, string PreviousStatus);
public static class DifferentialReplay
{
    public static DifferentialReplayInput Validate(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json); var root = document.RootElement;
            JsonShape.Fields(root, ["schema", "status", "velosim_source_mode", "verilator_semantics", "seed", "cycles", "workers", "reduction_kind", "first_mismatch_prefix_cycles", "requested_engines", "results", "errors", "differences", "metadata", "configuration", "observed_width", "ir_artifact", "counter_specification", "failure_stages"]);
            if (root.GetProperty("schema").GetString() != "velosim.diff.v3" || root.GetProperty("velosim_source_mode").GetString() != "surelog_hdl_compilation") throw new IrValidationException("Unsupported differential report; width-aware replay requires velosim.diff.v3.");
            if (root.GetProperty("verilator_semantics").GetString() != "two_state_reference_not_four_state_conformance" || root.GetProperty("reduction_kind").GetString() != "first_mismatch_prefix_not_general_delta_debugging") throw new IrValidationException("Invalid semantic or reduction label.");
            var seed = root.GetProperty("seed").GetUInt32(); var cycles = root.GetProperty("cycles").GetInt32(); var workers = root.GetProperty("workers").GetInt32();
            var width = root.GetProperty("observed_width").GetInt32(); if (width is < 1 or > 64) throw new IrValidationException("Unsupported replay width.");
            if (cycles is < 1 or > 10_000_000 || workers is < 1 or > 256) throw new IrValidationException("Invalid replay limits.");
            var engines = root.GetProperty("requested_engines").EnumerateArray().Select(e => e.GetString()!).ToArray();
            if (engines.Length < 2 || !engines.Contains("velosim") || engines.Distinct().Count() != engines.Length || engines.Any(e => e is not ("velosim" or "icarus" or "verilator"))) throw new IrValidationException("Invalid replay engines.");
            var configuration = root.GetProperty("configuration"); JsonShape.Fields(configuration, ["top", "sources", "build_jobs", "surelog", "normalizer", "toolchain_paths"]);
            var top = configuration.GetProperty("top").GetString()!; var jobs = configuration.GetProperty("build_jobs").GetInt32();
            var surelog = configuration.GetProperty("surelog").GetString(); var normalizer = configuration.GetProperty("normalizer").GetString();
            if (surelog is null || normalizer is null || !Path.IsPathRooted(surelog) || !Path.IsPathRooted(normalizer)) throw new IrValidationException("Replay requires effective absolute frontend tool paths.");
            var sources = configuration.GetProperty("sources").EnumerateArray().Select(e => e.GetString()!).ToArray();
            if (jobs is < 1 or > 256 || sources.Length == 0 || sources.Any(s => !Path.IsPathRooted(s))) throw new IrValidationException("Invalid replay source configuration.");
            CounterTestbench.Generate(top, width, [0]);
            var metadata = root.GetProperty("metadata"); JsonShape.Fields(metadata, ["source_hash_scope", "sources", "native_library", "tool_versions", "tool_binaries", "commands"]);
            if (metadata.GetProperty("source_hash_scope").GetString() != "explicit_sources_and_recursive_literal_relative_includes") throw new IrValidationException("Unsupported source hash scope.");
            var fingerprinted = new HashSet<string>(StringComparer.Ordinal);
            void Check(JsonElement file)
            {
                JsonShape.Fields(file, ["path", "sha256"]); var path = file.GetProperty("path").GetString()!; var hash = file.GetProperty("sha256").GetString();
                if (!Path.IsPathRooted(path) || !string.Equals(ArtifactMetadata.Fingerprint(path).Sha256, hash, StringComparison.Ordinal)) throw new IOException("Replay file changed: " + path);
                fingerprinted.Add(path);
            }
            var irArtifact = root.GetProperty("ir_artifact"); Check(irArtifact);
            var irPath = irArtifact.GetProperty("path").GetString()!;
            if (CounterDesignContract.Inspect(IrDesign.Parse(File.ReadAllText(irPath))).Width != width) throw new IrValidationException("Replay width differs from compiled IR artifact.");
            foreach (var source in metadata.GetProperty("sources").EnumerateArray()) Check(source);
            var stimulusFiles = fingerprinted.Where(p => Path.GetFileName(p) == "stimulus.txt").ToArray();
            var testbenchFiles = fingerprinted.Where(p => Path.GetFileName(p) == "reference.sv").ToArray();
            var resets = CounterStimulus.Create(seed, cycles);
            if (stimulusFiles.Length != 1 || File.ReadAllText(stimulusFiles[0]) != CounterStimulus.ToText(resets) || testbenchFiles.Length != 1 || File.ReadAllText(testbenchFiles[0]) != CounterTestbench.Generate(top, width, resets)) throw new IrValidationException("Replay stimulus or testbench differs from configuration.");
            foreach (var source in sources) if (!fingerprinted.Contains(source)) throw new IrValidationException("Replay source lacks fingerprint: " + source);
            foreach (var source in ArtifactMetadata.SourceClosure(sources)) if (!fingerprinted.Contains(source.Path)) throw new IrValidationException("Replay include lacks fingerprint: " + source.Path);
            if (metadata.GetProperty("native_library").ValueKind != JsonValueKind.Object) throw new IrValidationException("Replay requires native library fingerprint.");
            Check(metadata.GetProperty("native_library"));
            if (ArtifactMetadata.Fingerprint(Simulation.NativeLibraryPath).Sha256 != metadata.GetProperty("native_library").GetProperty("sha256").GetString()) throw new IOException("Loaded native library differs from replay.");
            var binaryPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binary in metadata.GetProperty("tool_binaries").EnumerateArray()) { Check(binary); binaryPaths.Add(binary.GetProperty("path").GetString()!); }
            foreach (var command in metadata.GetProperty("commands").EnumerateArray())
            {
                var executable = command.GetProperty("executable").GetString()!; var resolved = ArtifactMetadata.ResolveExecutable(executable, command.GetProperty("working_directory").GetString());
                if (command.GetProperty("exit_code").ValueKind != JsonValueKind.Null && (resolved is null || !binaryPaths.Contains(resolved))) throw new IOException("Replay executable resolution changed: " + executable);
            }
            bool HasStage(string tool, Func<string[], bool> argumentsMatch) => binaryPaths.Contains(tool) && metadata.GetProperty("commands").EnumerateArray().Any(c => ArtifactMetadata.ResolveExecutable(c.GetProperty("executable").GetString()!, c.GetProperty("working_directory").GetString()) == tool && c.GetProperty("exit_code").ValueKind == JsonValueKind.Number && c.GetProperty("exit_code").GetInt32() == 0 && argumentsMatch(c.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()!).ToArray()));
            bool Option(string[] arguments, string option, string value) { var index = Array.IndexOf(arguments, option); return index >= 0 && index + 1 < arguments.Length && arguments[index + 1] == value; }
            if (!HasStage(surelog, a => a.Contains("-elabuhdm") && Option(a, "-mt", workers.ToString(System.Globalization.CultureInfo.InvariantCulture)) && Option(a, "-top", top) && sources.All(a.Contains)) || !HasStage(normalizer, a => a.Length == 3 && Path.GetFileName(a[1]) == "normalized.json" && a[2] == top)) throw new IrValidationException("Replay frontend configuration is not bound to successful fingerprinted stage commands.");
            var toolchain = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tool in configuration.GetProperty("toolchain_paths").EnumerateObject())
            {
                var path = tool.Value.GetString()!;
                if (!Path.IsPathRooted(path) || !binaryPaths.Contains(path) || !toolchain.TryAdd(tool.Name, path)) throw new IrValidationException("Invalid replay toolchain binding.");
            }
            var requiredTools = new List<string>(); if (engines.Contains("icarus")) requiredTools.AddRange(["iverilog", "vvp"]); if (engines.Contains("verilator")) requiredTools.AddRange(["verilator", "cmake", "ninja", "c++"]);
            if (requiredTools.Any(tool => !toolchain.ContainsKey(tool)) || toolchain.Keys.Any(tool => !requiredTools.Contains(tool))) throw new IrValidationException("Replay toolchain is incomplete.");
            if (engines.Contains("icarus") && (!HasStage(toolchain["iverilog"], a => a.Contains("-g2012") && Option(a, "-s", "velosim_reference_tb") && Array.IndexOf(a, "-o") is var index && index >= 0 && index + 1 < a.Length && Path.GetFileName(a[index + 1]) == "simulation.vvp") || !HasStage(toolchain["vvp"], a => a.Length == 1 && Path.GetFileName(a[0]) == "simulation.vvp"))) throw new IrValidationException("Replay Icarus compiler/runtime role bindings differ from recorded stages.");
            if (engines.Contains("verilator") && (!HasStage(toolchain["cmake"], a => a.Contains("-S") && Option(a, "-G", "Ninja") && a.Contains("-DVERILATOR_BIN=" + toolchain["verilator"]) && a.Contains("-DCMAKE_MAKE_PROGRAM=" + toolchain["ninja"]) && a.Contains("-DCMAKE_CXX_COMPILER=" + toolchain["c++"])) || !HasStage(toolchain["cmake"], a => a.Contains("--build") && Option(a, "--parallel", jobs.ToString(System.Globalization.CultureInfo.InvariantCulture))))) throw new IrValidationException("Replay Ninja toolchain role bindings differ from recorded configuration/build stages.");
            var resultNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var engine in root.GetProperty("results").EnumerateObject())
            {
                if (!engines.Contains(engine.Name) || !resultNames.Add(engine.Name)) throw new IrValidationException("Invalid replay result engine.");
                var rows = engine.Value.EnumerateArray().Select(e => e.GetString()!).ToArray();
                if (rows.Length != cycles) throw new IrValidationException("Replay result length mismatch.");
                TraceComparison.Parse(string.Join('\n', rows.Select((r, i) => $"{i} {r}")), width);
            }
            var status = root.GetProperty("status").GetString(); if (status is not ("pass" or "fail" or "incomplete")) throw new IrValidationException("Invalid report status.");
            var traces = root.GetProperty("results").EnumerateObject().ToDictionary(e => e.Name, e => (IReadOnlyList<TraceSample>)e.Value.EnumerateArray().Select((row, i) => new TraceSample(i, row.GetString()!)).ToArray());
            var errors = root.GetProperty("errors").EnumerateObject().ToDictionary(e => e.Name, e => e.Value.GetString()!);
            var expectedSpecification = CounterSpecification.Generate(resets, width);
            var recordedSpecification = root.GetProperty("counter_specification").EnumerateArray().Select((e, i) => new TraceSample(i, e.GetString()!)).ToArray();
            if (!expectedSpecification.SequenceEqual(recordedSpecification)) throw new IrValidationException("Recorded counter specification differs from seed and observed width.");
            var stages = root.GetProperty("failure_stages").EnumerateObject().ToDictionary(e => e.Name, e => e.Value.GetString()!);
            if (stages.Any(s => !errors.ContainsKey(s.Key) || s.Value is not ("frontend_compilation" or "counter_contract" or "native_execution" or "dependency_skipped" or "reference_trace" or "reference_execution"))) throw new IrValidationException("Invalid failure stage evidence.");
            var computed = new CounterDifferentialReport(seed, cycles, workers, engines, traces, errors) { ObservedWidth = width, Specification = expectedSpecification };
            computed.ValidateTraceShape();
            if (computed.Status != status || (root.GetProperty("first_mismatch_prefix_cycles").ValueKind == JsonValueKind.Null ? (int?)null : root.GetProperty("first_mismatch_prefix_cycles").GetInt32()) != computed.FirstMismatchPrefixCycles) throw new IrValidationException("Report summary disagrees with recorded traces.");
            var differences = new Dictionary<string, TraceMismatch>();
            foreach (var entry in root.GetProperty("differences").EnumerateArray())
            {
                JsonShape.Fields(entry, ["engines", "cycle", "expected", "actual"]);
                var index = entry.GetProperty("cycle").GetInt32(); var pair = entry.GetProperty("engines").GetString()!;
                var expected = entry.GetProperty("expected").GetString(); var actual = entry.GetProperty("actual").GetString();
                if (!differences.TryAdd(pair, new(index, expected is null ? null : new(index, expected), actual is null ? null : new(index, actual)))) throw new IrValidationException("Duplicate difference entry.");
            }
            if (differences.Count != computed.Differences.Count || differences.Any(d => !computed.Differences.TryGetValue(d.Key, out var mismatch) || mismatch != d.Value)) throw new IrValidationException("Reported differences disagree with traces.");
            return new(seed, cycles, workers, engines, new(top, sources, jobs, surelog, normalizer, toolchain), status);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException && ex is not IrValidationException) { throw new IrValidationException("Invalid replay report: " + ex.Message); }
    }
    public static Task<CounterDifferentialReport> RunAsync(DifferentialReplayInput input, string outputDirectory, IProcessRunner? runner = null, CancellationToken cancellationToken = default) =>
        new CounterDifferential(runner).RunAsync(input.Configuration.Sources, input.Configuration.Top, outputDirectory, input.Seed, input.Cycles, input.Workers, input.Configuration.BuildJobs, input.Engines, input.Configuration.Surelog, input.Configuration.Normalizer, cancellationToken, input.Configuration.ToolchainPaths);
}
