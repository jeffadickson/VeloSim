using System.Globalization;

namespace VeloSim;

public static class ToolCli
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, IProcessRunner? runner = null, CancellationToken cancellationToken = default)
    {
        var structured = args.Length > 0 && args[0] == "--json-errors"; if (structured) args = args[1..];
        using var capture = new ErrorCapture(); int result;
        try { cancellationToken.ThrowIfCancellationRequested(); result = await RunCoreAsync(args, output, capture, runner, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException ex) { WriteError(capture, ex); result = 130; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { WriteError(capture, ex); result = 1; }
        if (result == 0) return 0;
        var message = capture.ToString().Trim(); if (message.Length == 0) message = result == 130 ? "Command canceled." : "Command completed with failing results.";
        if (!structured) { error.WriteLine(message); return result; }
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("schema", "velosim.cli.error.v1"); writer.WriteString("command", args.FirstOrDefault() ?? ""); writer.WriteNumber("exit_code", result);
            writer.WriteString("category", result == 130 ? "Canceled" : capture.Cause is null ? "CommandFailure" : RegressionSuiteReport.Classify(capture.Cause).ToString()); writer.WriteString("message", message); writer.WriteEndObject();
        }
        error.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray())); return result;
    }
    private sealed class ErrorCapture : StringWriter { public Exception? Cause { get; set; } }
    private static void WriteError(TextWriter writer, Exception exception) { if (writer is ErrorCapture capture) capture.Cause = exception; writer.WriteLine(exception.Message); }
    private static async Task<int> RunCoreAsync(string[] args, TextWriter output, TextWriter error, IProcessRunner? runner, CancellationToken cancellationToken)
    {
        if (args.Length > 0 && args[0] is "scheduler-inspect" or "scheduler-replay" or "scheduler-reduce")
        {
            bool inspect = args[0] == "scheduler-inspect";
            if (inspect && args.Length != 2 || !inspect && (args.Length != 4 || args[2] != "--output-dir")) throw new ArgumentException("Usage: scheduler-inspect <report.json> | scheduler-replay|scheduler-reduce <report.json> --output-dir <directory>");
            if (new FileInfo(args[1]).Length > 64 * 1024 * 1024) throw new ArgumentException("Scheduler report exceeds 64 MiB inspection/replay limit.");
            var json = await File.ReadAllTextAsync(args[1], cancellationToken).ConfigureAwait(false);
            if (inspect) { var report = SchedulerReportReader.Parse(json); output.WriteLine($"{report.Status} steps={report.Fixture.Drives.Count} workers={string.Join(',', report.RequestedWorkers)}; {SchedulerReportReader.ReplayAvailability(report)}"); return 0; }
            if (args[0] == "scheduler-reduce") { var path = await SchedulerReproduction.SavePrefixAsync(SchedulerReportReader.Parse(json), args[3], cancellationToken).ConfigureAwait(false); output.WriteLine("first-mismatch prefix fixture: " + path); return 0; }
            var input = SchedulerReplay.Validate(json); var replay = await SchedulerReplay.RunAsync(input, args[3], runner, cancellationToken).ConfigureAwait(false); output.WriteLine(replay.Status); return replay.Status == "pass" ? 0 : 1;
        }
        if (args.Length > 0 && args[0] is "scheduler-differential" or "scheduler-conformance" or "scheduler-generate")
        {
            bool differential = args[0] == "scheduler-differential", generate = args[0] == "scheduler-generate";
            if (differential && args.Length < 2) throw new ArgumentException("Scheduler fixture path required.");
            string? destination = null, compiler = null, runtime = null; int[] workers = [1, 2, 4, 8]; int steps = 500; uint seed = 42;
            for (int i = differential ? 2 : 1; i < args.Length; i += 2)
            {
                if (i + 1 == args.Length) throw new ArgumentException("Incomplete scheduler workflow option.");
                switch (args[i])
                {
                    case "--output-dir" when !generate: destination = args[i + 1]; break; case "--output" when generate: destination = args[i + 1]; break;
                    case "--iverilog" when !generate: compiler = args[i + 1]; break; case "--vvp" when !generate: runtime = args[i + 1]; break;
                    case "--workers" when !generate: workers = args[i + 1].Split(',').Select(s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : throw new ArgumentException("Invalid worker count.")).ToArray(); break;
                    case "--steps" when generate: if (!int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out steps)) throw new ArgumentException("Invalid step count."); break;
                    case "--seed" when generate: if (!uint.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out seed)) throw new ArgumentException("Invalid seed."); break;
                    default: throw new ArgumentException("Unknown scheduler workflow option: " + args[i]);
                }
            }
            if (destination is null) throw new ArgumentException("Scheduler workflow output path required.");
            if (generate) { await WriteNewAsync(destination, SchedulerStimulus.Generate(seed, steps).ToJson(), cancellationToken).ConfigureAwait(false); output.WriteLine(Path.GetFullPath(destination)); return 0; }
            if (differential)
            {
                if (new FileInfo(args[1]).Length > ConformanceLimits.MaxFixtureCharacters) throw new ArgumentException("Scheduler fixture exceeds input size limit.");
                var fixture = SchedulerFixture.Parse(await File.ReadAllTextAsync(args[1], cancellationToken).ConfigureAwait(false));
                var report = await new SchedulerDifferential(runner).RunAsync(fixture, workers, destination, compiler, runtime, cancellationToken).ConfigureAwait(false); output.WriteLine(report.Status + " " + Path.Combine(Path.GetFullPath(destination), "scheduler-report.json")); return report.Status == "pass" ? 0 : 1;
            }
            var results = await SchedulerConformance.RunAsync(workers, destination, runner, compiler, runtime, cancellationToken).ConfigureAwait(false); foreach (var result in results) output.WriteLine(result.Status + " " + result.Name); return results.All(r => r.Status == "pass") ? 0 : 1;
        }
        if (args.Length > 0 && args[0] == "matrix-inspect")
        {
            if (args.Length != 2) throw new ArgumentException("Usage: matrix-inspect <matrix.json>");
            var report = ElaborationMatrixReport.Parse(await File.ReadAllTextAsync(args[1], cancellationToken).ConfigureAwait(false)); output.WriteLine($"{report.Status} runs={report.Records.Count} elaboration-only"); return 0;
        }
        if (args.Length > 0 && args[0] == "elaboration-matrix")
        {
            string? config = null, root = null, directory = null, surelog = null; int[]? workers = null;
            for (int i = 1; i < args.Length; i += 2)
            {
                if (i + 1 == args.Length) throw new ArgumentException("Incomplete matrix option.");
                switch (args[i])
                {
                    case "--config": config = Path.GetFullPath(args[i + 1]); break; case "--root": root = Path.GetFullPath(args[i + 1]); break; case "--output-dir": directory = args[i + 1]; break; case "--surelog": surelog = args[i + 1]; break;
                    case "--workers": workers = args[i + 1].Split(',').Select(s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : throw new ArgumentException("Invalid matrix worker count.")).ToArray(); break;
                    default: throw new ArgumentException("Unknown matrix option: " + args[i]);
                }
            }
            if (directory is null || config is not null && root is not null) throw new ArgumentException("Matrix requires --output-dir and either --config or --root.");
            var plan = config is null ? ElaborationMatrixPlan.Default(root ?? Environment.CurrentDirectory, workers) : ElaborationMatrixPlan.Parse(await File.ReadAllTextAsync(config, cancellationToken).ConfigureAwait(false), Path.GetDirectoryName(config)!);
            if (config is not null && workers is not null) plan = new(plan.Name, plan.Cases, workers);
            var report = await new ElaborationMatrix(runner).RunAsync(plan, directory, surelog, cancellationToken).ConfigureAwait(false);
            foreach (var record in report.Records) output.WriteLine($"{record.Status} {record.Case} workers={record.Workers} seconds={record.ElapsedSeconds.ToString("F3", CultureInfo.InvariantCulture)}");
            output.WriteLine(Path.Combine(Path.GetFullPath(directory), "matrix.json")); return report.Status == "canceled" ? 130 : report.Status == "pass" ? 0 : 1;
        }
        if (args.Length > 0 && args[0] is "word-check" or "word-generate")
        {
            if (args.Length < 2) throw new ArgumentException("Usage: word-check <fixture.json> [--report PATH] | word-generate <ir.json> --output PATH [--steps N] [--seed N] [--workers 1,4] [--four-state]");
            if (new FileInfo(args[1]).Length > ConformanceLimits.MaxFixtureCharacters) throw new ArgumentException("Input file exceeds word workflow size limit.");
            if (args[0] == "word-check")
            {
                if (args.Length != 2 && (args.Length != 4 || args[2] != "--report")) throw new ArgumentException("Usage: word-check <fixture.json> [--report PATH]");
                if (args.Length == 4 && File.Exists(args[3])) throw new IOException("Report already exists.");
                var fixture = WordFixture.Parse(await File.ReadAllTextAsync(args[1], cancellationToken).ConfigureAwait(false));
                var results = WordFixtureRunner.Run(fixture, cancellationToken: cancellationToken); var report = WordFixtureRunner.ReportJson(results);
                if (args.Length == 2) output.Write(report);
                else { await WriteNewAsync(args[3], report, cancellationToken).ConfigureAwait(false); output.WriteLine(Path.GetFullPath(args[3])); }
                return results.All(r => r.Failure is null) ? 0 : 1;
            }
            string? destination = null; int steps = 100; uint seed = 42; bool fourState = false; int[] workers = [1];
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--four-state") { fourState = true; continue; }
                var option = args[i]; if (++i == args.Length) throw new ArgumentException("Incomplete generation option.");
                switch (option)
                {
                    case "--output": destination = args[i]; break;
                    case "--steps": if (!int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out steps)) throw new ArgumentException("Invalid step count."); break;
                    case "--seed": if (!uint.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out seed)) throw new ArgumentException("Invalid seed."); break;
                    case "--workers": workers = args[i].Split(',').Select(s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : throw new ArgumentException("Invalid worker count.")).ToArray(); break;
                    default: throw new ArgumentException("Unknown generation option: " + option);
                }
            }
            if (destination is null) throw new ArgumentException("Output fixture path required.");
            var design = IrDesign.Parse(await File.ReadAllTextAsync(args[1], cancellationToken).ConfigureAwait(false));
            var generated = IrStimulus.Generate(design, steps, seed, fourState, workers, cancellationToken);
            await WriteNewAsync(destination, generated.ToJson(), cancellationToken).ConfigureAwait(false); output.WriteLine(Path.GetFullPath(destination)); return 0;
        }
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
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException) { WriteError(error, ex); return 1; }
        }
        if (args.Length > 0 && args[0] is "regression" or "suite")
        {
            try
            {
                if (args.Length < 4) throw new ArgumentException("Usage: regression|suite <manifest.json> --output-dir <directory> [--surelog PATH] [--normalizer PATH]");
                string? directory = null, surelog = null, normalizer = null; string[]? include = null, exclude = null; int shardIndex = 0, shardCount = 1;
                for (int i = 2; i < args.Length; i += 2)
                {
                    if (i + 1 == args.Length) throw new ArgumentException("Incomplete option.");
                    switch (args[i])
                    {
                        case "--output-dir": directory = args[i + 1]; break; case "--surelog": surelog = args[i + 1]; break; case "--normalizer": normalizer = args[i + 1]; break;
                        case "--include" when args[0] == "suite": include = args[i + 1].Split(','); break; case "--exclude" when args[0] == "suite": exclude = args[i + 1].Split(','); break;
                        case "--shard-index" when args[0] == "suite": if (!int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out shardIndex)) throw new ArgumentException("Invalid shard index."); break;
                        case "--shard-count" when args[0] == "suite": if (!int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out shardCount)) throw new ArgumentException("Invalid shard count."); break;
                        default: throw new ArgumentException("Unknown option: " + args[i]);
                    }
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
                var suite = SuiteSelector.Select(RegressionSuiteManifest.Parse(json, Path.GetDirectoryName(manifestPath)!), include, exclude, shardIndex, shardCount);
                var report = await new RegressionSuiteRunner(new RegressionExecutor(runner)).RunAsync(suite, directory, surelog, normalizer, cancellationToken).ConfigureAwait(false);
                output.WriteLine($"{report.Status} cases={report.Cases.Count} " + Path.Combine(Path.GetFullPath(directory), "suite-report.json")); return report.Status == "canceled" ? 130 : report.Passed ? 0 : 1;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException) { WriteError(error, ex); return 1; }
        }
        if (args.Length > 0 && args[0] is "inspect-report" or "replay")
        {
            try
            {
                if (args[0] == "inspect-report" && args.Length != 2 || args[0] == "replay" && (args.Length != 4 || args[2] != "--output-dir")) throw new ArgumentException("Usage: inspect-report <report.json> | replay <report.json> --output-dir <directory>");
                var json = await File.ReadAllTextAsync(args[1], cancellationToken).ConfigureAwait(false);
                if (args[0] == "inspect-report") { var inspection = DifferentialReportInspector.Inspect(json); output.WriteLine($"{inspection.Status} seed={inspection.Seed} cycles={inspection.Cycles} workers={inspection.Workers}; {inspection.ReplayMessage}"); return 0; }
                var replay = DifferentialReplay.Validate(json);
                var report = await DifferentialReplay.RunAsync(replay, args[3], runner, cancellationToken).ConfigureAwait(false); output.WriteLine(report.Status); return report.Status == "pass" ? 0 : 1;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or TimeoutException or DllNotFoundException or EntryPointNotFoundException) { WriteError(error, ex); return 1; }
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
            catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception) { WriteError(error, ex); return 1; }
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
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { WriteError(error, ex); return 1; }
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
        { WriteError(error, ex); return 1; }
    }
    private static async Task WriteNewAsync(string path, string content, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(Path.GetFullPath(path), FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(stream); await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
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
        { WriteError(error, ex); return 1; }
    }
}
