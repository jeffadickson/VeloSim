using System.Globalization;
namespace VeloSim;
public static class BenchmarkCli
{
    public static int Run(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        if (args.Length == 1 && args[0] == "--help") { output.WriteLine("VeloSim.Bench [--processes N] [--cycles N] [--width N] [--workers 1,2,4] [--repeat N] [--warmup N] [--report FILE]. Measures managed drive API plus native execution and verifies identical results."); return 0; }
        try
        {
            int processes = 4, cycles = 100, width = 64, repetitions = 3, warmups = 1; int[] workers = [1, 2, 4]; string? reportPath = null;
            var options = CliOptions.Benchmark(args);
            processes = options.Int("--processes", processes); cycles = options.Int("--cycles", cycles); width = options.Int("--width", width);
            repetitions = options.Int("--repeat", repetitions); warmups = options.Int("--warmup", warmups); workers = options.Workers(workers);
            if (options.String("--report") is { } reportFile) reportPath = Path.GetFullPath(reportFile);
            if (reportPath is not null && (File.Exists(reportPath) || Directory.Exists(reportPath) || !Directory.Exists(Path.GetDirectoryName(reportPath)))) throw new IOException("Benchmark report must be a new file in an existing directory.");
            var report = NativeBenchmark.Run(new(processes, cycles, width, repetitions, warmups, workers), cancellationToken);
            if (reportPath is null) output.Write(report.ToJson());
            else { using var file = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write); using var writer = new StreamWriter(file); writer.Write(report.ToJson()); output.WriteLine(report.Status + " " + reportPath); }
            return report.Status == "pass" ? 0 : report.Status == "canceled" ? 130 : 1;
        }
        catch (OperationCanceledException) { error.WriteLine("Benchmark canceled."); return 130; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or IOException or UnauthorizedAccessException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { error.WriteLine(ex.Message); return 1; }
    }
}
