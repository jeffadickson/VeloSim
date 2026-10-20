using System.Globalization;

namespace VeloSim;

public static class ToolCli
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, IProcessRunner? runner = null, CancellationToken cancellationToken = default)
    {
        if (args.Length > 0 && args[0] is "scheduler" or "scheduler-check")
        {
            try
            {
                if (args.Length != 2 && args.Length != 4 || args.Length == 4 && args[2] != "--workers") throw new ArgumentException("Usage: scheduler|scheduler-check <fixture.json> [--workers 1,4,8]");
                var counts = args.Length == 2 ? new[] { 1 } : args[3].Split(',').Select(s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : throw new ArgumentException("Invalid worker count.")).ToArray();
                if (counts.Length == 0 || counts.Any(w => w is < 1 or > 256) || counts.Distinct().Count() != counts.Length) throw new ArgumentException("Invalid worker matrix.");
                var fixture = SchedulerFixture.Parse(await File.ReadAllTextAsync(args[1], cancellationToken).ConfigureAwait(false));
                if (args[0] == "scheduler-check")
                {
                    var results = SchedulerRegression.Run(fixture, counts, cancellationToken);
                    foreach (var result in results) output.WriteLine($"{(result.Mismatch is null ? "pass" : "fail")} workers={result.Workers}" + (result.Mismatch is null ? "" : $" first_mismatch={result.Mismatch.Step}"));
                    return results.All(r => r.Mismatch is null) ? 0 : 1;
                }
                foreach (var workers in counts) foreach (var step in fixture.Run(workers, cancellationToken)) output.WriteLine($"workers={workers} tick={step.Tick} active={step.ActivatedProcesses} values={step.Values}");
                return 0;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) { error.WriteLine(ex.Message); return 1; }
        }
        if (args.Length > 0 && args[0] is "regression" or "suite")
        {
            try
            {
                if (args.Length < 4) throw new ArgumentException("Usage: regression|suite <manifest.json> --output-dir <directory> [--surelog PATH] [--normalizer PATH]");
                string? directory = null, surelog = null, normalizer = null;
                for (int i = 2; i < args.Length; i += 2)
                {
                    if (i + 1 == args.Length) throw new ArgumentException("Incomplete option.");
                    switch (args[i]) { case "--output-dir": directory = args[i + 1]; break; case "--surelog": surelog = args[i + 1]; break; case "--normalizer": normalizer = args[i + 1]; break; default: throw new ArgumentException("Unknown option: " + args[i]); }
                }
                if (directory is null) throw new ArgumentException("Output directory required.");
                var manifestPath = Path.GetFullPath(args[1]); var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                if (args[0] == "regression")
                {
                    var manifest = RegressionManifest.Parse(json, Path.GetDirectoryName(manifestPath)!);
                    var result = await new CompiledRegression(runner).RunAsync(manifest, directory, surelog, normalizer, cancellationToken).ConfigureAwait(false);
                    foreach (var r in result.Results) output.WriteLine($"{(r.Mismatch is null ? "pass" : "fail")} {r.Name} workers={r.Workers}" + (r.Mismatch is null ? "" : $" first_mismatch={r.Mismatch.Index}"));
                    return result.Passed ? 0 : 1;
                }
                var suite = RegressionSuiteManifest.Parse(json, Path.GetDirectoryName(manifestPath)!);
                var report = await new RegressionSuiteRunner(new RegressionExecutor(runner)).RunAsync(suite, directory, surelog, normalizer, cancellationToken).ConfigureAwait(false);
                output.WriteLine($"{(report.Passed ? "pass" : "fail")} cases={report.Cases.Count} " + Path.Combine(Path.GetFullPath(directory), "suite-report.json")); return report.Passed ? 0 : 1;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException) { error.WriteLine(ex.Message); return 1; }
        }
        if (args.Length > 0 && args[0] is "inspect-report" or "replay")
        {
            try
            {
                if (args[0] == "inspect-report" && args.Length != 2 || args[0] == "replay" && (args.Length != 4 || args[2] != "--output-dir")) throw new ArgumentException("Usage: inspect-report <report.json> | replay <report.json> --output-dir <directory>");
                var replay = DifferentialReplay.Validate(await File.ReadAllTextAsync(args[1], cancellationToken).ConfigureAwait(false));
                if (args[0] == "inspect-report") { output.WriteLine($"{replay.PreviousStatus} seed={replay.Seed} cycles={replay.Cycles} workers={replay.Workers}"); return 0; }
                var report = await DifferentialReplay.RunAsync(replay, args[3], runner, cancellationToken).ConfigureAwait(false); output.WriteLine(report.Status); return report.Status == "pass" ? 0 : 1;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or TimeoutException or DllNotFoundException or EntryPointNotFoundException) { error.WriteLine(ex.Message); return 1; }
        }
        if (args.Length > 0 && args[0] == "differential")
        {
            try
            {
                string? top = null, directory = null, surelog = null, normalizer = null; uint seed = 42; int cycles = 100, workers = 1, jobs = 1;
                string[] engines = ["velosim", "icarus", "verilator"]; var sources = new List<string>();
                for (int i = 1; i < args.Length; i++)
                {
                    if (args[i] == "--") { sources.AddRange(args.Skip(i + 1)); break; }
                    if (!args[i].StartsWith("--", StringComparison.Ordinal)) { sources.Add(args[i]); continue; }
                    var option = args[i];
                    if (option is not ("--top" or "--output-dir" or "--surelog" or "--normalizer" or "--seed" or "--cycles" or "--threads" or "--build-jobs" or "--engines") || ++i == args.Length) throw new ArgumentException("Unknown or incomplete differential option: " + option);
                    switch (option)
                    {
                        case "--top": top = args[i]; break; case "--output-dir": directory = args[i]; break;
                        case "--surelog": surelog = args[i]; break; case "--normalizer": normalizer = args[i]; break;
                        case "--engines": engines = args[i].Split(','); break;
                        case "--seed": if (!uint.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out seed)) throw new ArgumentException("Invalid seed."); break;
                        default:
                            if (!int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n)) throw new ArgumentException("Invalid integer for " + option);
                            if (option == "--cycles") cycles = n; else if (option == "--threads") workers = n; else jobs = n;
                            break;
                    }
                }
                if (top is null || directory is null) throw new ArgumentException("Differential requires --top and --output-dir.");
                var report = await new CounterDifferential(runner).RunAsync(sources, top, directory, seed, cycles, workers, jobs, engines, surelog, normalizer, cancellationToken).ConfigureAwait(false);
                output.WriteLine(report.Status + " " + Path.Combine(Path.GetFullPath(directory), "report.json"));
                return report.Status == "pass" ? 0 : 1;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { error.WriteLine(ex.Message); return 1; }
        }
        if (args.Length > 0 && args[0] is "validate-ir" or "lower")
        {
            try
            {
                if (args[0] == "validate-ir")
                {
                    if (args.Length != 2) throw new ArgumentException("Usage: validate-ir <ir.json>");
                    var design = IrDesign.Parse(await File.ReadAllTextAsync(args[1], cancellationToken).ConfigureAwait(false));
                    output.WriteLine($"valid signals={design.Signals.Count} processes={design.Processes.Count}");
                }
                else
                {
                    if (args.Length != 4 || args[2] != "--output") throw new ArgumentException("Usage: lower <normalized.json> --output <ir.json>");
                    var design = NormalizedUhdm.Lower(await File.ReadAllTextAsync(args[1], cancellationToken).ConfigureAwait(false));
                    var path = Path.GetFullPath(args[3]);
                    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                    using var writer = new StreamWriter(stream);
                    await writer.WriteAsync(design.ToJson().AsMemory(), cancellationToken).ConfigureAwait(false);
                    output.WriteLine(path);
                }
                return 0;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { error.WriteLine(ex.Message); return 1; }
        }
        if (args.Length == 0 || args[0] != "compile") return Run(args, output, error);
        try
        {
            string? top = null, directory = null, surelog = null, normalizer = null; int workers = 1;
            var sources = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--") { sources.AddRange(args.Skip(i + 1)); break; }
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) { sources.Add(args[i]); continue; }
                var option = args[i];
                if (option is not ("--top" or "--output-dir" or "--surelog" or "--normalizer" or "--threads") || ++i == args.Length) throw new ArgumentException("Unknown or incomplete compile option: " + option);
                switch (option)
                {
                    case "--top": top = args[i]; break;
                    case "--output-dir": directory = args[i]; break;
                    case "--surelog": surelog = args[i]; break;
                    case "--normalizer": normalizer = args[i]; break;
                    case "--threads": if (!int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out workers)) throw new ArgumentException("Invalid thread count."); break;
                }
            }
            if (top is null || directory is null) throw new ArgumentException("Compile requires --top and --output-dir.");
            output.WriteLine(await new DesignPipeline(runner).CompileAsync(sources, top, directory, surelog, normalizer, workers, cancellationToken: cancellationToken).ConfigureAwait(false));
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        { error.WriteLine(ex.Message); return 1; }
    }
    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            if (args.Length < 2 || args[0] != "simulate") throw new ArgumentException("Usage: velosim simulate <ir.json> [--cycles N] [--threads N] [--release-reset]");
            int cycles = 5, workers = 1; bool release = false;
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--release-reset") { release = true; continue; }
                if (args[i] is not ("--cycles" or "--threads") || i + 1 == args.Length) throw new ArgumentException("Unknown or incomplete option: " + args[i]);
                var option = args[i++]; if (!int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n)) throw new ArgumentException("Expected positive integer.");
                if (option == "--cycles") cycles = n; else workers = n;
            }
            if (cycles is < 1 or > 10_000_000 || workers is < 1 or > 256) throw new ArgumentException("Cycles must be 1..10000000 and threads 1..256.");
            var design = IrDesign.Parse(File.ReadAllText(args[1]));
            if (!design.Signals.Any(s => s.Name == "clk" && s.Initial.Width == 1)) throw new ArgumentException("Simulation command requires scalar clk.");
            if (release && !design.Signals.Any(s => s.Name == "rst_n" && s.Initial.Width == 1)) throw new ArgumentException("Release reset requires scalar rst_n.");
            using var sim = new Simulation(design);
            if (release) sim.Drive(new Dictionary<string, FourStateWord> { ["rst_n"] = new(1) }, workers);
            for (int cycle = 0; cycle < cycles; cycle++)
            {
                if (release && cycle == 1) sim.Drive(new Dictionary<string, FourStateWord> { ["rst_n"] = new(1, 1) }, workers);
                sim.Drive(new Dictionary<string, FourStateWord> { ["clk"] = new(1) }, workers);
                sim.Drive(new Dictionary<string, FourStateWord> { ["clk"] = new(1, 1) }, workers);
                output.Write("cycle " + (cycle + 1).ToString(CultureInfo.InvariantCulture));
                foreach (var s in design.Signals.Where(s => s.Name != "clk")) { var word = sim.Read(s.Name); output.Write($" {s.Name}=({word.Width},{word.Value},{word.XMask},{word.ZMask})"); }
                output.WriteLine();
            }
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        { error.WriteLine(ex.Message); return 1; }
    }
}
