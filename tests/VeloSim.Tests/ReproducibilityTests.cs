using NUnit.Framework;

namespace VeloSim.Tests;

public class ReproducibilityTests
{
    private static string Temp() { var path = Path.Combine(Path.GetTempPath(), "velosim-repro-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static CounterDifferentialReport Fixture(string directory)
    {
        var source = Path.Combine(directory, "counter.sv"); File.WriteAllText(source, "module counter; endmodule");
        var tb = Path.Combine(directory, "reference.sv"); var stimulus = Path.Combine(directory, "stimulus.txt"); var resets = CounterStimulus.Create(42, 2);
        File.WriteAllText(tb, CounterTestbench.Generate("counter", 8, resets)); File.WriteAllText(stimulus, CounterStimulus.ToText(resets));
        var ir = Path.Combine(directory, "design.ir.json");
        File.WriteAllText(ir, new IrDesign([new("clk", new(1)), new("rst_n", new(1)), new("count", new(8))], [new("clk", "count", "count", "rst_n", IrEdge.Posedge, IrOperation.SvIfResetIncrementStrict)]).ToJson());
        return new(42, 2, 1, ["velosim", "icarus"], new Dictionary<string, IReadOnlyList<TraceSample>> { ["velosim"] = [new(0, "00000000"), new(1, "00000001")], ["icarus"] = [new(0, "00000000"), new(1, "00000001")] }, new Dictionary<string, string>())
        {
            IrArtifact = ArtifactMetadata.Fingerprint(ir), Specification = CounterSpecification.Generate(resets, 8),
            Configuration = new("counter", [source], 2, "/bin/true", "/bin/true", new Dictionary<string, string> { ["iverilog"] = "/bin/echo", ["vvp"] = "/bin/false" }),
            Metadata = new([ArtifactMetadata.Fingerprint(source), ArtifactMetadata.Fingerprint(tb), ArtifactMetadata.Fingerprint(stimulus)], ArtifactMetadata.Fingerprint(Simulation.NativeLibraryPath), [new("/bin/true", ["-elabuhdm", "-mt", "1", "-top", "counter", source], directory, 0, "", "", null), new("/bin/true", ["db.uhdm", Path.Combine(directory, "normalized.json"), "counter"], directory, 0, "", "", null), new("/bin/echo", ["-g2012", "-s", "velosim_reference_tb", "-o", "simulation.vvp"], directory, 0, "", "", null), new("/bin/false", ["simulation.vvp"], directory, 0, "", "", null)], new Dictionary<string, string>(), [ArtifactMetadata.Fingerprint("/bin/true"), ArtifactMetadata.Fingerprint("/bin/echo"), ArtifactMetadata.Fingerprint("/bin/false")])
        };
    }
    [Test] public void MetadataHashesTransitiveIncludesAndActualLoadedNativeLibrary()
    {
        var directory = Temp();
        try
        {
            var source = Path.Combine(directory, "root.sv"); var include = Path.Combine(directory, "types.svh");
            File.WriteAllText(source, "`include \"types.svh\"\n"); File.WriteAllText(include, "typedef logic bit_t;\n");
            var closure = ArtifactMetadata.SourceClosure([source]); Assert.That(closure.Count, Is.EqualTo(2));
            var before = closure[1].Sha256; File.AppendAllText(include, "//change\n"); Assert.That(ArtifactMetadata.Fingerprint(include).Sha256, Is.Not.EqualTo(before));
            Assert.That(Path.GetFileName(Simulation.NativeLibraryPath), Does.Contain("velosim_ir")); Assert.That(ArtifactMetadata.Fingerprint(Simulation.NativeLibraryPath).Sha256.Length, Is.EqualTo(64));
            File.WriteAllText(source, "`include MACRO\n"); Assert.Throws<IOException>(() => ArtifactMetadata.SourceClosure([source]));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public void ReplayValidationRejectsChangedSourceAndTamperedConfiguration()
    {
        var directory = Temp();
        try
        {
            var report = Fixture(directory); var json = report.ToJson(); var replay = DifferentialReplay.Validate(json);
            Assert.That(replay.Cycles, Is.EqualTo(2)); Assert.That(replay.Configuration.BuildJobs, Is.EqualTo(2));
            Assert.Throws<IrValidationException>(() => DifferentialReplay.Validate((report with { Cycles = 3 }).ToJson()));
            Assert.Throws<IrValidationException>(() => DifferentialReplay.Validate((report with { Configuration = report.Configuration! with { Normalizer = "/bin/false" } }).ToJson()));
            Assert.Throws<IrValidationException>(() => DifferentialReplay.Validate((report with { Configuration = report.Configuration! with { ToolchainPaths = new Dictionary<string, string> { ["iverilog"] = "/bin/false", ["vvp"] = "/bin/echo" } } }).ToJson()));
            Assert.Throws<IrValidationException>(() => DifferentialReplay.Validate(json.Replace("two_state_reference_not_four_state_conformance", "four_state_conformance")));
            Assert.Throws<IrValidationException>(() => DifferentialReplay.Validate(json.Replace("\"differences\": []", "\"differences\": [{\"engines\":\"velosim:icarus\",\"cycle\":0,\"expected\":\"00000000\",\"actual\":\"00000001\"}]")));
            File.AppendAllText(report.Configuration!.Sources[0], "//changed"); Assert.Throws<IOException>(() => DifferentialReplay.Validate(json));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public void FailureReductionReportsFirstMismatchPrefixWithoutClaimingDeltaDebugging()
    {
        var directory = Temp();
        try
        {
            var report = Fixture(directory) with { Results = new Dictionary<string, IReadOnlyList<TraceSample>> { ["velosim"] = [new(0, "00000000"), new(1, "00000001")], ["icarus"] = [new(0, "00000000"), new(1, "00000010")] } };
            Assert.That(report.FirstMismatchPrefixCycles, Is.EqualTo(2)); Assert.That(report.Status, Is.EqualTo("fail"));
            Assert.That(report.ToJson(), Does.Contain("first_mismatch_prefix_not_general_delta_debugging"));
            Assert.That(CounterStimulus.ToText(CounterStimulus.Create(report.Seed, report.Cycles).Take(report.FirstMismatchPrefixCycles!.Value).ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, Is.EqualTo(2));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task InspectionCliVerifiesArtifactsAndReportsMalformedInput()
    {
        var directory = Temp();
        try
        {
            var report = Fixture(directory); var path = Path.Combine(directory, "report.json"); File.WriteAllText(path, report.ToJson());
            var output = new StringWriter(); var error = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["inspect-report", path], output, error), Is.Zero, error.ToString()); Assert.That(output.ToString(), Does.Contain("pass seed=42 cycles=2 workers=1"));
            File.WriteAllText(path, "{}"); Assert.That(await ToolCli.RunAsync(["inspect-report", path], output, error), Is.EqualTo(1));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task TimeoutRetainsDiagnosticsAndNativeStimulusHonorsCancellation()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Linux process test.");
        var runner = new ProcessRunner();
        var ex = Assert.ThrowsAsync<ProcessTimeoutException>(async () => await runner.RunAsync("/bin/sh", ["-c", "printf stage-started; printf failure-detail >&2; sleep 1"], Path.GetTempPath(), TimeSpan.FromMilliseconds(100)));
        Assert.That(ex!.StandardOutput, Does.Contain("stage-started")); Assert.That(ex.StandardError, Does.Contain("failure-detail"));
        var recording = new RecordingProcessRunner(runner);
        Assert.ThrowsAsync<ProcessTimeoutException>(async () => await recording.RunAsync("/bin/sh", ["-c", "printf retained; sleep 1"], Path.GetTempPath(), TimeSpan.FromMilliseconds(100)));
        Assert.That(recording.Commands[0].StandardOutput, Does.Contain("retained"));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var design = new IrDesign([new("clk", new(1)), new("rst_n", new(1)), new("count", new(8))], []);
        Assert.Throws<OperationCanceledException>(() => CounterStimulus.RunNative(design, [0], 1, canceled.Token));
        await Task.CompletedTask;
    }
}
