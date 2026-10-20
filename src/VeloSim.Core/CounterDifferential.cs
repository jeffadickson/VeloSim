using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed record CounterDifferentialReport(uint Seed, int Cycles, int Workers, IReadOnlyList<string> RequestedEngines, IReadOnlyDictionary<string, IReadOnlyList<TraceSample>> Results, IReadOnlyDictionary<string, string> Errors)
{
    public ArtifactMetadata? Metadata { get; init; }
    public DifferentialReplayConfiguration? Configuration { get; init; }
    public int? FirstMismatchPrefixCycles => Differences.Count == 0 ? null : Differences.Values.Min(m => m.Index) + 1;
    public string Status => Differences.Count > 0 ? "fail" : Errors.Count > 0 || RequestedEngines.Any(e => !Results.ContainsKey(e)) || Results.Count < 2 ? "incomplete" : "pass";
    public IReadOnlyDictionary<string, TraceMismatch> Differences
    {
        get
        {
            var result = new Dictionary<string, TraceMismatch>(); var engines = Results.Keys.ToArray();
            for (int i = 0; i < engines.Length; i++) for (int j = i + 1; j < engines.Length; j++)
                if (TraceComparison.Compare(Results[engines[i]], Results[engines[j]]) is { } mismatch) result.Add(engines[i] + ":" + engines[j], mismatch);
            return result;
        }
    }
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            w.WriteStartObject(); w.WriteString("schema", "velosim.diff.v2"); w.WriteString("status", Status); w.WriteString("velosim_source_mode", "surelog_hdl_compilation");
            w.WriteString("verilator_semantics", "two_state_reference_not_four_state_conformance"); w.WriteNumber("seed", Seed); w.WriteNumber("cycles", Cycles); w.WriteNumber("workers", Workers);
            w.WriteString("reduction_kind", "first_mismatch_prefix_not_general_delta_debugging");
            if (FirstMismatchPrefixCycles.HasValue) w.WriteNumber("first_mismatch_prefix_cycles", FirstMismatchPrefixCycles.Value); else w.WriteNull("first_mismatch_prefix_cycles");
            w.WriteStartArray("requested_engines"); foreach (var e in RequestedEngines) w.WriteStringValue(e); w.WriteEndArray();
            w.WriteStartObject("results"); foreach (var (e, samples) in Results) { w.WriteStartArray(e); foreach (var s in samples) w.WriteStringValue(s.Bits); w.WriteEndArray(); } w.WriteEndObject();
            w.WriteStartObject("errors"); foreach (var (e, error) in Errors) w.WriteString(e, error); w.WriteEndObject();
            w.WriteStartArray("differences"); foreach (var (pair, mismatch) in Differences) { w.WriteStartObject(); w.WriteString("engines", pair); w.WriteNumber("cycle", mismatch.Index); w.WriteString("expected", mismatch.Expected?.Bits); w.WriteString("actual", mismatch.Actual?.Bits); w.WriteEndObject(); } w.WriteEndArray();
            Metadata?.Write(w); Configuration?.Write(w); w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
public sealed class CounterDifferential(IProcessRunner? runner = null)
{
    public async Task<CounterDifferentialReport> RunAsync(IReadOnlyList<string> sources, string top, string outputDirectory, uint seed = 42, int cycles = 100, int workers = 1, int buildJobs = 1, IReadOnlyList<string>? engines = null, string? surelog = null, string? normalizer = null, CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? toolchainPaths = null)
    {
        engines ??= ["velosim", "icarus", "verilator"];
        cancellationToken.ThrowIfCancellationRequested();
        if (workers is < 1 or > 256 || buildJobs is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(workers));
        if (engines.Count < 2 || engines.Distinct().Count() != engines.Count || engines.Any(e => e is not ("velosim" or "icarus" or "verilator")) || !engines.Contains("velosim")) throw new ArgumentException("Select VeloSim and at least one distinct reference engine.");
        var resets = CounterStimulus.Create(seed, cycles); var testbench = CounterTestbench.Generate(top, 8, resets);
        var sourceFingerprints = ArtifactMetadata.SourceClosure(sources);
        var recording = new RecordingProcessRunner(runner ?? new ProcessRunner());
        var directory = Path.GetFullPath(outputDirectory); Directory.CreateDirectory(directory);
        var effectiveSurelog = ArtifactMetadata.ResolveExecutable(surelog ?? Environment.GetEnvironmentVariable("VELOSIM_SURELOG") ?? "surelog", directory) ?? surelog ?? Environment.GetEnvironmentVariable("VELOSIM_SURELOG") ?? "surelog";
        var effectiveNormalizer = ArtifactMetadata.ResolveExecutable(normalizer ?? Environment.GetEnvironmentVariable("VELOSIM_UHDM_NORMALIZER") ?? "velosim-uhdm-normalize", directory) ?? normalizer ?? Environment.GetEnvironmentVariable("VELOSIM_UHDM_NORMALIZER") ?? "velosim-uhdm-normalize";
        var requiredTools = new List<string>(); if (engines.Contains("icarus")) requiredTools.AddRange(["iverilog", "vvp"]); if (engines.Contains("verilator")) requiredTools.AddRange(["verilator", "cmake", "ninja", "c++"]);
        var tools = requiredTools.ToDictionary(tool => tool, tool => ArtifactMetadata.ResolveExecutable(toolchainPaths?.GetValueOrDefault(tool) ?? tool, directory) ?? toolchainPaths?.GetValueOrDefault(tool) ?? tool);
        var tb = Path.Combine(directory, "reference.sv"); var stimulus = Path.Combine(directory, "stimulus.txt");
        if (File.Exists(tb) || File.Exists(stimulus)) throw new IOException("Differential artifacts already exist.");
        await File.WriteAllTextAsync(tb, testbench, cancellationToken).ConfigureAwait(false); await File.WriteAllTextAsync(stimulus, CounterStimulus.ToText(resets), cancellationToken).ConfigureAwait(false);
        var results = new Dictionary<string, IReadOnlyList<TraceSample>>(); var errors = new Dictionary<string, string>();
        foreach (var engine in engines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (engine == "velosim")
                {
                    var ir = await new DesignPipeline(recording).CompileAsync(sources, top, Path.Combine(directory, engine), effectiveSurelog, effectiveNormalizer, workers, cancellationToken: cancellationToken).ConfigureAwait(false);
                    var design = IrDesign.Parse(await File.ReadAllTextAsync(ir, cancellationToken).ConfigureAwait(false));
                    if (design.Signals.FirstOrDefault(s => s.Name == "count")?.Initial.Width != 8) throw new IrValidationException("Counter differential currently requires count width8.");
                    results.Add(engine, CounterStimulus.RunNative(design, resets, workers, cancellationToken));
                }
                else if (engine == "icarus") results.Add(engine, await new IcarusReference(recording).RunAsync([.. sources, tb], "velosim_reference_tb", Path.Combine(directory, engine), cycles, 8, tools["iverilog"], tools["vvp"], cancellationToken).ConfigureAwait(false));
                else results.Add(engine, await new VerilatorReference(recording).RunAsync([.. sources, tb], "velosim_reference_tb", Path.Combine(directory, engine), cycles, 8, buildJobs, tools["verilator"], cancellationToken, tools["cmake"], tools["ninja"], tools["c++"]).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { errors.Add(engine, ex.Message); }
        }
        var versions = new Dictionary<string, string>();
        var versionDirectory = Path.Combine(Path.GetTempPath(), "velosim-versions-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(versionDirectory);
        try
        {
            foreach (var (name, executable) in new Dictionary<string, string>(tools) { ["surelog"] = effectiveSurelog })
            {
                try { var version = await recording.RunAsync(executable, [name is "iverilog" or "vvp" ? "-V" : "--version"], versionDirectory, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); versions.Add(executable, $"exit={version.ExitCode}\n{version.StandardOutput}\n{version.StandardError}"); }
                catch (Exception ex) when (ex is IOException or TimeoutException or System.ComponentModel.Win32Exception) { versions.Add(executable, "unavailable: " + ex.Message); }
            }
        }
        finally { Directory.Delete(versionDirectory, true); }
        FileFingerprint? native = null;
        if (results.ContainsKey("velosim"))
        {
            try { native = ArtifactMetadata.Fingerprint(Simulation.NativeLibraryPath); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { errors.Add("native_metadata", ex.Message); }
        }
        var toolBinaries = recording.Commands.Select(c => ArtifactMetadata.ResolveExecutable(c.Executable, c.WorkingDirectory)).OfType<string>().Distinct().Select(ArtifactMetadata.Fingerprint).ToArray();
        var report = new CounterDifferentialReport(seed, cycles, workers, engines.ToArray(), results, errors)
        {
            Metadata = new([.. sourceFingerprints, ArtifactMetadata.Fingerprint(tb), ArtifactMetadata.Fingerprint(stimulus)], native, recording.Commands.ToArray(), versions, toolBinaries),
            Configuration = new(top, sources.Select(Path.GetFullPath).ToArray(), buildJobs, effectiveSurelog, effectiveNormalizer, tools)
        };
        if (report.FirstMismatchPrefixCycles is { } prefix) await File.WriteAllTextAsync(Path.Combine(directory, "first_mismatch_prefix.txt"), CounterStimulus.ToText(resets.Take(prefix).ToArray()), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), report.ToJson(), cancellationToken).ConfigureAwait(false); return report;
    }
}
