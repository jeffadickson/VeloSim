using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed record WordDifference(string Comparison, int Step, string Signal, string Expected, string Actual);
public sealed record WordDifferentialReport(WordFixture Fixture, IReadOnlyList<WordHdlStep> Oracle, IReadOnlyList<WordHdlStep>? Reference, IReadOnlyDictionary<int, IReadOnlyList<WordHdlStep>> Native, IReadOnlyDictionary<string, string> Errors, ArtifactMetadata Metadata, SchedulerToolchain Toolchain)
{
    public IReadOnlyList<WordDifference> Differences
    {
        get
        {
            var differences = new List<WordDifference>();
            void Compare(string name, IReadOnlyList<WordHdlStep> expected, IReadOnlyList<WordHdlStep> actual)
            {
                if (expected.Count != Fixture.Drives.Count || actual.Count != expected.Count) throw new IrValidationException("Word differential trace step count mismatch.");
                for (int step = 0; step < expected.Count; step++)
                {
                    var e = expected[step]; var a = actual[step];
                    if (e.Values.Count != Fixture.Design.Signals.Count || a.Values.Count != e.Values.Count) throw new IrValidationException("Word differential signal count mismatch.");
                    if (e.Tick != a.Tick) { differences.Add(new(name, step, "$ticks", e.Tick.ToString(CultureInfo.InvariantCulture), a.Tick.ToString(CultureInfo.InvariantCulture))); return; }
                    if (e.ActivatedProcesses != a.ActivatedProcesses) { differences.Add(new(name, step, "$active", e.ActivatedProcesses.ToString(CultureInfo.InvariantCulture), a.ActivatedProcesses.ToString(CultureInfo.InvariantCulture))); return; }
                    for (int signal = 0; signal < e.Values.Count; signal++) if (e.Values[signal] != a.Values[signal]) { differences.Add(new(name, step, Fixture.Design.Signals[signal].Name, e.Values[signal], a.Values[signal])); return; }
                }
            }
            void Checkpoints(string name, IReadOnlyList<WordHdlStep> trace)
            {
                var ids = Fixture.Design.Signals.Select((s, index) => (s.Name, index)).ToDictionary(s => s.Name, s => s.index, StringComparer.Ordinal);
                foreach (var checkpoint in Fixture.Checkpoints)
                {
                    var actual = trace[checkpoint.Step];
                    if (checkpoint.ActivatedProcesses is { } active && active != actual.ActivatedProcesses) differences.Add(new("checkpoint:" + name, checkpoint.Step, "$active", active.ToString(CultureInfo.InvariantCulture), actual.ActivatedProcesses.ToString(CultureInfo.InvariantCulture)));
                    foreach (var (signal, expected) in checkpoint.Signals) if (FourStateCodec.FormatBinary(expected) != actual.Values[ids[signal]]) differences.Add(new("checkpoint:" + name, checkpoint.Step, signal, FourStateCodec.FormatBinary(expected), actual.Values[ids[signal]]));
                }
            }
            Checkpoints("oracle", Oracle);
            if (Reference is not null) { Compare("oracle:icarus", Oracle, Reference); Checkpoints("icarus", Reference); }
            foreach (var (workers, trace) in Native) { Compare("oracle:native-" + workers, Oracle, trace); if (Reference is not null) Compare("icarus:native-" + workers, Reference, trace); Checkpoints("native-" + workers, trace); }
            return differences.AsReadOnly();
        }
    }
    public string Status => Differences.Count > 0 ? "fail" : Reference is null || Errors.Count > 0 || Fixture.Workers.Any(w => !Native.ContainsKey(w)) ? "incomplete" : "pass";
    public int? FirstMismatchPrefixSteps => Differences.Count == 0 ? null : Differences.Min(d => d.Step) + 1;
    public string ToJson()
    {
        using var stream = new MemoryStream(); using (var w = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            void Trace(IReadOnlyList<WordHdlStep> trace) { w.WriteStartArray(); foreach (var step in trace) { w.WriteStartObject(); w.WriteNumber("tick", step.Tick); w.WriteNumber("activated_processes", step.ActivatedProcesses); w.WriteStartArray("values"); foreach (var value in step.Values) w.WriteStringValue(value); w.WriteEndArray(); w.WriteEndObject(); } w.WriteEndArray(); }
            w.WriteStartObject(); w.WriteString("schema", "velosim.word.differential.v1"); w.WriteString("status", Status); w.WriteString("source_mode", "generated_strict_SystemVerilog_fixture_not_frontend_lowering"); w.WriteString("reduction_kind", "first_mismatch_prefix_not_general_delta_debugging"); if (FirstMismatchPrefixSteps is { } prefix) w.WriteNumber("first_mismatch_prefix_steps", prefix); else w.WriteNull("first_mismatch_prefix_steps"); w.WritePropertyName("fixture"); w.WriteRawValue(Fixture.ToJson()); w.WritePropertyName("oracle"); Trace(Oracle); w.WritePropertyName("icarus"); if (Reference is null) w.WriteNullValue(); else Trace(Reference);
            w.WriteStartObject("native"); foreach (var (workers, trace) in Native) { w.WritePropertyName(workers.ToString(CultureInfo.InvariantCulture)); Trace(trace); } w.WriteEndObject(); w.WriteStartObject("errors"); foreach (var (engine, error) in Errors) w.WriteString(engine, error); w.WriteEndObject();
            w.WriteStartArray("differences"); foreach (var difference in Differences) { w.WriteStartObject(); w.WriteString("comparison", difference.Comparison); w.WriteNumber("step", difference.Step); w.WriteString("signal", difference.Signal); w.WriteString("expected", difference.Expected); w.WriteString("actual", difference.Actual); w.WriteEndObject(); } w.WriteEndArray();
            w.WriteStartObject("toolchain"); w.WriteString("compiler", Toolchain.Compiler); w.WriteString("runtime", Toolchain.Runtime); w.WriteEndObject(); Metadata.Write(w); w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
public sealed class WordDifferential(IProcessRunner? runner = null)
{
    public async Task<WordDifferentialReport> RunAsync(WordFixture fixture, string outputDirectory, string? compiler = null, string? runtime = null, CancellationToken cancellationToken = default)
    {
        WordSystemVerilog.Validate(fixture); cancellationToken.ThrowIfCancellationRequested(); var directory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("Word differential output must be empty."); Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "fixture.json"); var ir = Path.Combine(directory, "design.ir.json"); await File.WriteAllTextAsync(input, fixture.ToJson(), cancellationToken).ConfigureAwait(false); await File.WriteAllTextAsync(ir, fixture.Design.ToJson(), cancellationToken).ConfigureAwait(false);
        var recording = new RecordingProcessRunner(runner ?? new ProcessRunner()); var errors = new Dictionary<string, string>(); var native = new Dictionary<int, IReadOnlyList<WordHdlStep>>(); var oracle = WordHdlTrace.Oracle(fixture, cancellationToken);
        bool Failure(Exception ex) => ex is ArgumentException or IOException or FormatException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;
        foreach (var workers in fixture.Workers) { cancellationToken.ThrowIfCancellationRequested(); try { native.Add(workers, WordHdlTrace.Native(fixture, workers, cancellationToken)); } catch (Exception ex) when (Failure(ex)) { errors.Add("native-" + workers, ex.Message); } }
        var executable = ArtifactMetadata.ResolveExecutable(compiler ?? "iverilog", directory) ?? compiler ?? "iverilog"; var interpreter = ArtifactMetadata.ResolveExecutable(runtime ?? "vvp", directory) ?? runtime ?? "vvp"; IReadOnlyList<WordHdlStep>? reference = null;
        try { reference = await new WordIcarus(recording).RunAsync(fixture, Path.Combine(directory, "icarus"), executable, interpreter, cancellationToken).ConfigureAwait(false); } catch (Exception ex) when (Failure(ex)) { errors.Add("icarus", ex.Message); }
        var versions = new Dictionary<string, string>(); var probe = Path.Combine(Path.GetTempPath(), "velosim-word-version-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(probe);
        try
        {
            foreach (var tool in new[] { executable, interpreter }.Distinct())
                try { var version = await recording.RunAsync(tool, ["-V"], probe, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); versions.Add(tool, "exit=" + version.ExitCode + "\n" + version.StandardOutput + "\n" + version.StandardError); } catch (Exception ex) when (Failure(ex)) { versions.Add(tool, "unavailable: " + ex.Message); }
        }
        finally { Directory.Delete(probe, true); }
        FileFingerprint? library = null; if (native.Count > 0) try { library = ArtifactMetadata.Fingerprint(Simulation.NativeLibraryPath); } catch (Exception ex) when (Failure(ex)) { errors.Add("native_metadata", ex.Message); }
        var sources = new List<FileFingerprint> { ArtifactMetadata.Fingerprint(input), ArtifactMetadata.Fingerprint(ir) };
        foreach (var name in new[] { "word.sv", "word.vvp" }) { var path = Path.Combine(directory, "icarus", name); if (File.Exists(path)) sources.Add(ArtifactMetadata.Fingerprint(path)); }
        var binaries = recording.Commands.Select(c => ArtifactMetadata.ResolveExecutable(c.Executable, c.WorkingDirectory)).OfType<string>().Distinct().Select(ArtifactMetadata.Fingerprint).ToArray();
        var report = new WordDifferentialReport(fixture, oracle, reference, native, errors, new(sources, library, recording.Commands.ToArray(), versions, binaries), new(executable, interpreter));
        if (report.FirstMismatchPrefixSteps is not null) await WordReproduction.SavePrefixAsync(report, Path.Combine(directory, "first-mismatch-prefix"), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "word-differential-report.json"), report.ToJson(), cancellationToken).ConfigureAwait(false); return report;
    }
}
