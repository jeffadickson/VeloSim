using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed record SchedulerDifferentialMismatch(string Comparison, int Step, SchedulerStep Expected, SchedulerStep Actual);
public sealed record SchedulerToolchain(string Compiler, string Runtime);
public sealed record SchedulerDifferentialReport(SchedulerFixture Fixture, IReadOnlyList<int> RequestedWorkers, IReadOnlyList<SchedulerStep> Oracle, IReadOnlyList<SchedulerStep>? Reference, IReadOnlyDictionary<int, IReadOnlyList<SchedulerStep>> Native, IReadOnlyDictionary<string, string> Errors, ArtifactMetadata Metadata)
{
    public SchedulerToolchain? Toolchain { get; init; }
    public IReadOnlyList<SchedulerDifferentialMismatch> Differences
    {
        get
        {
            var differences = new List<SchedulerDifferentialMismatch>();
            void Compare(string name, IReadOnlyList<SchedulerStep> expected, IReadOnlyList<SchedulerStep> actual)
            {
                if (expected.Count != actual.Count) throw new IrValidationException("Scheduler differential trace length mismatch.");
                for (int i = 0; i < expected.Count; i++) if (expected[i] != actual[i]) { differences.Add(new(name, i, expected[i], actual[i])); break; }
            }
            if (Reference is not null) Compare("oracle:icarus", Oracle, Reference);
            foreach (var (workers, trace) in Native) { Compare("oracle:native-" + workers, Oracle, trace); if (Reference is not null) Compare("icarus:native-" + workers, Reference, trace); }
            return differences.AsReadOnly();
        }
    }
    public string Status => Differences.Count > 0 ? "fail" : Reference is null || Errors.Count > 0 || RequestedWorkers.Any(w => !Native.ContainsKey(w)) ? "incomplete" : "pass";
    public int? FirstMismatchPrefixSteps => Differences.Count == 0 ? null : Differences.Min(d => d.Step) + 1;
    public string ToJson()
    {
        using var stream = new MemoryStream(); using (var w = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            void Trace(IReadOnlyList<SchedulerStep> trace) { w.WriteStartArray(); foreach (var step in trace) { w.WriteStartObject(); w.WriteNumber("tick", step.Tick); w.WriteNumber("activated_processes", step.ActivatedProcesses); w.WriteString("values", step.Values); w.WriteEndObject(); } w.WriteEndArray(); }
            w.WriteStartObject(); w.WriteString("schema", "velosim.scheduler.differential.v1"); w.WriteString("status", Status); w.WriteString("scope", "generated_four_state_SystemVerilog_edge_and_NBA_fixture_not_full_IEEE_scheduler"); w.WriteString("reduction_kind", "first_mismatch_prefix_not_general_delta_debugging"); if (FirstMismatchPrefixSteps is { } prefix) w.WriteNumber("first_mismatch_prefix_steps", prefix); else w.WriteNull("first_mismatch_prefix_steps"); w.WritePropertyName("fixture"); w.WriteRawValue(Fixture.ToJson()); w.WriteStartArray("requested_workers"); foreach (var worker in RequestedWorkers) w.WriteNumberValue(worker); w.WriteEndArray();
            w.WritePropertyName("oracle"); Trace(Oracle); w.WritePropertyName("icarus"); if (Reference is null) w.WriteNullValue(); else Trace(Reference);
            w.WriteStartObject("native"); foreach (var (workers, trace) in Native) { w.WritePropertyName(workers.ToString(System.Globalization.CultureInfo.InvariantCulture)); Trace(trace); } w.WriteEndObject();
            w.WriteStartObject("errors"); foreach (var (engine, error) in Errors) w.WriteString(engine, error); w.WriteEndObject();
            if (Toolchain is not null) { w.WriteStartObject("toolchain"); w.WriteString("compiler", Toolchain.Compiler); w.WriteString("runtime", Toolchain.Runtime); w.WriteEndObject(); }
            w.WriteStartArray("differences"); foreach (var difference in Differences) { w.WriteStartObject(); w.WriteString("comparison", difference.Comparison); w.WriteNumber("step", difference.Step); w.WritePropertyName("expected"); Trace([difference.Expected]); w.WritePropertyName("actual"); Trace([difference.Actual]); w.WriteEndObject(); } w.WriteEndArray(); Metadata.Write(w); w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
public sealed class SchedulerDifferential(IProcessRunner? runner = null)
{
    public async Task<SchedulerDifferentialReport> RunAsync(SchedulerFixture fixture, IReadOnlyList<int> workers, string outputDirectory, string? compiler = null, string? runtime = null, CancellationToken cancellationToken = default)
    {
        SchedulerSystemVerilog.Validate(fixture);
        if (workers.Count is < 1 or > 256 || workers.Distinct().Count() != workers.Count || workers.Any(w => w is < 1 or > 256) || (long)workers.Count * fixture.Drives.Count * fixture.Observed.Count > 10_000_000) throw new ArgumentException("Invalid scheduler differential worker or trace budget.");
        cancellationToken.ThrowIfCancellationRequested(); var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Scheduler differential output must be empty."); Directory.CreateDirectory(output);
        var input = Path.Combine(output, "fixture.json"); await File.WriteAllTextAsync(input, fixture.ToJson(), cancellationToken).ConfigureAwait(false);
        var recording = new RecordingProcessRunner(runner ?? new ProcessRunner()); var native = new Dictionary<int, IReadOnlyList<SchedulerStep>>(); var errors = new Dictionary<string, string>();
        var oracle = SchedulerRegression.Oracle(fixture, cancellationToken); IReadOnlyList<SchedulerStep>? reference = null;
        bool Failure(Exception ex) => ex is ArgumentException or IOException or FormatException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;
        foreach (var count in workers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { native.Add(count, fixture.Run(count, cancellationToken)); } catch (Exception ex) when (Failure(ex)) { errors.Add("native-" + count, ex.Message); }
        }
        var resolvedCompiler = ArtifactMetadata.ResolveExecutable(compiler ?? "iverilog", output) ?? compiler ?? "iverilog"; var resolvedRuntime = ArtifactMetadata.ResolveExecutable(runtime ?? "vvp", output) ?? runtime ?? "vvp";
        try { reference = (await new SchedulerIcarus(recording).RunAsync(fixture, Path.Combine(output, "icarus"), resolvedCompiler, resolvedRuntime, cancellationToken).ConfigureAwait(false)).Trace; }
        catch (Exception ex) when (Failure(ex)) { errors.Add("icarus", ex.Message); }
        var versions = new Dictionary<string, string>();
        var probeDirectory = Path.Combine(Path.GetTempPath(), "velosim-scheduler-version-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(probeDirectory);
        try
        {
            foreach (var executable in new[] { resolvedCompiler, resolvedRuntime }.Distinct())
                try { var version = await recording.RunAsync(executable, ["-V"], probeDirectory, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); versions.Add(executable, "exit=" + version.ExitCode + "\n" + version.StandardOutput + "\n" + version.StandardError); }
                catch (Exception ex) when (Failure(ex)) { versions.Add(executable, "unavailable: " + ex.Message); }
        }
        finally { Directory.Delete(probeDirectory, true); }
        var source = Path.Combine(output, "icarus", "scheduler.sv"); FileFingerprint? library = null;
        if (native.Count > 0) try { library = ArtifactMetadata.Fingerprint(ScalarScheduler.NativeLibraryPath); } catch (Exception ex) when (Failure(ex)) { errors.Add("native_metadata", ex.Message); }
        var tools = recording.Commands.Select(c => ArtifactMetadata.ResolveExecutable(c.Executable, c.WorkingDirectory)).OfType<string>().Distinct().Select(ArtifactMetadata.Fingerprint).ToArray();
        var compiled = Path.Combine(output, "icarus", "scheduler.vvp");
        var metadata = new ArtifactMetadata([ArtifactMetadata.Fingerprint(input), .. File.Exists(source) ? new[] { ArtifactMetadata.Fingerprint(source) } : [], .. File.Exists(compiled) ? new[] { ArtifactMetadata.Fingerprint(compiled) } : []], library, recording.Commands.ToArray(), versions, tools);
        var report = new SchedulerDifferentialReport(fixture, workers.ToArray(), oracle, reference, native, errors, metadata) { Toolchain = new(resolvedCompiler, resolvedRuntime) };
        if (report.FirstMismatchPrefixSteps is not null) await SchedulerReproduction.SavePrefixAsync(report, Path.Combine(output, "first-mismatch-prefix"), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "scheduler-report.json"), report.ToJson(), cancellationToken).ConfigureAwait(false); return report;
    }
}
