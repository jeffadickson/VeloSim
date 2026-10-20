using System.Diagnostics;

namespace VeloSim;

public sealed record MatrixElaborationRecord(string Case, string Top, int Workers, string Status, string OutputDirectory, double ElapsedSeconds, IReadOnlyList<FileFingerprint> Sources, FileFingerprint? Uhdm, long? UhdmBytes, IReadOnlyList<RecordedCommand> Commands, string? ErrorCategory, string? Error);
public sealed class ElaborationMatrix(IProcessRunner? runner = null)
{
    private readonly IProcessRunner runner = runner ?? new ProcessRunner();
    public async Task<ElaborationMatrixReport> RunAsync(ElaborationMatrixPlan plan, string outputDirectory, string? surelog = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Matrix output must be empty; each run requires fresh elaboration artifacts.");
        var sources = plan.Cases.ToDictionary(c => c.Name, c => ArtifactMetadata.SourceClosure(c.Sources), StringComparer.Ordinal);
        Directory.CreateDirectory(output);
        var executable = ArtifactMetadata.ResolveExecutable(surelog ?? Environment.GetEnvironmentVariable("VELOSIM_SURELOG") ?? "surelog", output) ?? surelog ?? Environment.GetEnvironmentVariable("VELOSIM_SURELOG") ?? "surelog";
        FileFingerprint? tool = File.Exists(executable) ? ArtifactMetadata.Fingerprint(executable) : null; string version;
        var versionDirectory = Path.Combine(Path.GetTempPath(), "velosim-matrix-version-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(versionDirectory);
        try
        {
            try { var result = await runner.RunAsync(executable, ["--version"], versionDirectory, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); version = $"exit={result.ExitCode}\n{result.StandardOutput}\n{result.StandardError}"; }
            catch (Exception ex) when (ex is IOException or TimeoutException or System.ComponentModel.Win32Exception) { version = "unavailable: " + ex.Message; }
        }
        finally { Directory.Delete(versionDirectory, true); }
        var records = new List<MatrixElaborationRecord>();
        foreach (var entry in plan.Cases) foreach (var count in plan.Workers)
        {
            var destination = Path.Combine(output, entry.Name, "workers-" + count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (cancellationToken.IsCancellationRequested)
            {
                records.Add(new(entry.Name, entry.Top, count, "canceled", destination, 0, sources[entry.Name], null, null, [], "Canceled", "Canceled before elaboration.")); continue;
            }
            var recording = new RecordingProcessRunner(runner); var stopwatch = Stopwatch.StartNew();
            try
            {
                var elaboration = await new SurelogFrontend(recording).ElaborateAsync(entry.Sources, destination, entry.Top, executable, count, cancellationToken: cancellationToken).ConfigureAwait(false);
                var size = new FileInfo(elaboration.UhdmPath).Length;
                if (size == 0) throw new IOException("Surelog produced an empty UHDM database.");
                records.Add(new(entry.Name, entry.Top, count, "pass", destination, stopwatch.Elapsed.TotalSeconds, sources[entry.Name], ArtifactMetadata.Fingerprint(elaboration.UhdmPath), size, recording.Commands.ToArray(), null, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { records.Add(new(entry.Name, entry.Top, count, "canceled", destination, stopwatch.Elapsed.TotalSeconds, sources[entry.Name], null, null, recording.Commands.ToArray(), "Canceled", "Canceled during elaboration.")); }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { records.Add(new(entry.Name, entry.Top, count, "fail", destination, stopwatch.Elapsed.TotalSeconds, sources[entry.Name], null, null, recording.Commands.ToArray(), RegressionSuiteReport.Classify(ex).ToString(), ex.Message)); }
        }
        var report = new ElaborationMatrixReport(plan, executable, tool, version, records.AsReadOnly());
        await File.WriteAllTextAsync(Path.Combine(output, "matrix.json"), report.ToJson(), CancellationToken.None).ConfigureAwait(false); return report;
    }
}
