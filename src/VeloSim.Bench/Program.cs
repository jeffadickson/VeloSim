using System.Globalization;
using VeloSim;

if (args.Length == 1 && args[0] == "--help") { Console.WriteLine("VeloSim.Bench [--processes N] [--cycles N] [--width N] [--workers 1,2,4] [--repeat N] [--warmup N] [--report FILE]. Measures the managed drive API plus native execution; verifies identical results across workers."); return 0; }
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    int processes = 4, cycles = 100, width = 64, repetitions = 3, warmups = 1; int[] workers = [1, 2, 4]; string? reportPath = null;
    for (int i = 0; i < args.Length; i++)
    {
        var option = args[i]; if (++i == args.Length) throw new ArgumentException("Incomplete benchmark option.");
        if (option == "--report") { reportPath = args[i]; continue; }
        if (option == "--workers") { workers = args[i].Split(',').Select(s => int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray(); continue; }
        int value = int.Parse(args[i], NumberStyles.None, CultureInfo.InvariantCulture);
        switch (option) { case "--processes": processes = value; break; case "--cycles": cycles = value; break; case "--width": width = value; break; case "--repeat": repetitions = value; break; case "--warmup": warmups = value; break; default: throw new ArgumentException("Unknown benchmark option: " + option); }
    }
    if (reportPath is not null && File.Exists(reportPath)) throw new IOException("Benchmark report already exists.");
    var report = NativeBenchmark.Run(new(processes, cycles, width, repetitions, warmups, workers), cancellation.Token);
    if (reportPath is null) Console.Write(report.ToJson());
    else { using var file = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write); using var writer = new StreamWriter(file); writer.Write(report.ToJson()); Console.WriteLine(report.Status + " " + Path.GetFullPath(reportPath)); }
    return report.Status == "pass" ? 0 : report.Status == "canceled" ? 130 : 1;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Benchmark canceled."); return 130; }
catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or IOException or UnauthorizedAccessException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
{ Console.Error.WriteLine(ex.Message); return 1; }
