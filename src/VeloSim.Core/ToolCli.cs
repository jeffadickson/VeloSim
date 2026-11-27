using System.Globalization;

namespace VeloSim;

public static class ToolCli
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, IProcessRunner? runner = null, CancellationToken cancellationToken = default)
    {
        var structured = args.Length > 0 && args[0] == "--json-errors"; if (structured) args = args[1..];
        using var capture = new ErrorCapture(); int result;
        try { cancellationToken.ThrowIfCancellationRequested(); if (CliOptions.TryHelp(args, output)) return 0; result = await RunCoreAsync(args, output, capture, runner, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException ex) { WriteError(capture, ex); result = 130; }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { WriteError(capture, ex); result = 1; }
        if (result == 0) return 0;
        var message = capture.ToString().Trim(); if (message.Length == 0) message = result == 130 ? "Command canceled." : "Command completed with failing results.";
        if (!structured) { error.WriteLine(message); return result; }
        var contract = new CliErrorContract("velosim.cli.error.v1", args.FirstOrDefault() ?? "", result,
            result == 130 ? "Canceled" : capture.Cause is null ? "CommandFailure" : RegressionSuiteReport.Classify(capture.Cause).ToString(), message);
        error.WriteLine(System.Text.Json.JsonSerializer.Serialize(contract, CliJsonContext.Default.CliErrorContract)); return result;
    }
    private sealed class ErrorCapture : StringWriter { public Exception? Cause { get; set; } }
    private static void WriteError(TextWriter writer, Exception exception) { if (writer is ErrorCapture capture) capture.Cause = exception; writer.WriteLine(exception.Message); }
    private static async Task<int> RunCoreAsync(string[] args, TextWriter output, TextWriter error, IProcessRunner? runner, CancellationToken cancellationToken)
    {
        if (args.Length > 0 && args[0] == "rtl-export")
        {
            if (args.Length < 6) throw new ArgumentException("Usage: rtl-export <design.rtl.json> --top <name> --output-dir <empty-directory> [--target cpp|csharp|rust] [--two-state] [--vpi]");
            var options = CliOptions.Parse(args, 2);
            string? top = options.String("--top"), directory = options.String("--output-dir"), targetText = options.String("--target");
            if (top is null || directory is null) throw new ArgumentException("Usage: rtl-export <design.rtl.json> --top <name> --output-dir <empty-directory> [--target cpp|csharp|rust] [--two-state] [--vpi]");
            var target = (targetText ?? "cpp").ToLowerInvariant() switch
            {
                "cpp" or "c++" => RtlTargetLanguage.Cpp,
                "csharp" or "cs" or "c#" => RtlTargetLanguage.CSharp,
                "rust" or "rs" => RtlTargetLanguage.Rust,
                _ => throw new ArgumentException("Unsupported rtl-export --target (expected cpp, csharp, or rust): " + targetText)
            };
            var stateMode = options.Flag("--two-state") ? RtlStateMode.TwoState : RtlStateMode.FourState;
            var limits = options.Flag("--large-design") ? RtlDesignLimits.Large : RtlDesignLimits.Bounded;
            bool enableCoverage = options.Flag("--coverage");
            bool enableVpi = options.Flag("--vpi");
            int threads = options.Int("--threads", 1);
            int lanes = options.Int("--lanes", 1);
            output.WriteLine(await RtlExternalSdk.ExportAsync(args[1], top, directory, cancellationToken, stateMode, target, limits, enableCoverage, threads, enableVpi, lanes).ConfigureAwait(false)); return 0;
        }
        if (args.Length > 0 && args[0] is "lint" or "--lint-only")
            return await RunLintAsync(args, output, runner, cancellationToken).ConfigureAwait(false);
        if (args.Length > 0 && args[0] == "slang-lower")
        {
            var options = CliOptions.Parse(args, 1);
            string? top = options.String("--top"), outputPath = options.String("--output"), slangExe = options.String("--slang"), astJsonPath = options.String("--ast-json");
            if (top is null || outputPath is null)
                throw new ArgumentException("Usage: slang-lower [<file.sv> ... | --ast-json <ast.json>] --top <module> --output <design.rtl.json> [--slang <path>]");
            string irJson = astJsonPath is not null
                ? SlangAstLowerer.LowerAstJson(await File.ReadAllTextAsync(astJsonPath, cancellationToken).ConfigureAwait(false), top)
                : await SlangAstLowerer.LowerSystemVerilogAsync(options.Sources, top, slangExe, cancellationToken).ConfigureAwait(false);
            string fullOut = Path.GetFullPath(outputPath);
            if (Path.GetDirectoryName(fullOut) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(fullOut, irJson, cancellationToken).ConfigureAwait(false);
            output.WriteLine(fullOut);
            return 0;
        }
        if (args.Length > 0 && args[0] == "uhdm-inspect")
        {
            if (args.Length < 2) throw new ArgumentException("Usage: uhdm-inspect <design.uhdm> [--output <inventory.json>]");
            var options = CliOptions.Parse(args, 2);
            string json = Uhdm.UhdmInspector.InspectFile(args[1]);
            if (options.String("--output") is { } outPath)
            {
                string fullOut = Path.GetFullPath(outPath);
                if (Path.GetDirectoryName(fullOut) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(fullOut, json, cancellationToken).ConfigureAwait(false);
                output.WriteLine(fullOut);
            }
            else
            {
                output.Write(json);
            }
            return 0;
        }
        if (args.Length > 0 && args[0] is "uhdm-normalize" or "uhdm-rtl")
        {
            bool structural = args[0] == "uhdm-rtl";
            if (args.Length < 2) throw new ArgumentException($"Usage: {args[0]} <design.uhdm> --top <module> --output <file.json>{(structural ? " [--large-design]" : "")}");
            var options = CliOptions.Parse(args, 2);
            string? top = options.String("--top"), outputPath = options.String("--output");
            if (top is null || outputPath is null)
                throw new ArgumentException($"Usage: {args[0]} <design.uhdm> --top <module> --output <file.json>{(structural ? " [--large-design]" : "")}");
            string fullOut = Path.GetFullPath(outputPath);
            if (structural)
            {
                var limits = options.Flag("--large-design") ? Uhdm.UhdmRtlLimits.Large : Uhdm.UhdmRtlLimits.FromEnvironment();
                Uhdm.UhdmRtlLowerer.LowerFileTo(args[1], fullOut, top, limits);
            }
            else
            {
                Uhdm.UhdmNormalizer.NormalizeFileTo(args[1], fullOut, top);
            }
            output.WriteLine(fullOut);
            return 0;
        }
        if (args.Length > 0 && args[0] == "coverage-merge")
        {
            if (args.Length < 3) throw new ArgumentException("Usage: coverage-merge <output.json> <input1.json> [<input2.json> ...]");
            output.WriteLine(RtlCoverageMerge.MergeFiles(args[1], args[2..]));
            return 0;
        }
        if (args.Length > 0 && args[0] == "fifo-differential")
        {
            if (args.Length < 2) throw new ArgumentException("FIFO source required.");
            string top = "surelog_fifo"; string? directory = null, exporter = null, surelog = null; int jobs = 4, cycles = 1000; uint seed = 42; int[] workers = [1, 4, 8];
var options = CliOptions.Parse(args, 2);
            top = options.String("--top") ?? top; directory = options.String("--output-dir"); exporter = options.String("--exporter"); surelog = options.String("--surelog");
            jobs = options.Int("--build-jobs", jobs); cycles = options.Int("--cycles", cycles); seed = options.UInt("--seed", seed); workers = options.Workers(workers);
            if (directory is null) throw new ArgumentException("FIFO output directory required.");
            var result = await new FifoDifferential(runner).RunAsync(args[1], top, directory, workers, jobs, seed, cycles, exporter, surelog, cancellationToken).ConfigureAwait(false); output.WriteLine($"{result.Status} width={result.Width} depth={result.Depth} cycles={result.Cycles} {result.ReportPath}"); return result.Status == "pass" ? 0 : 1;
        }
        if (args.Length > 0 && args[0] == "word-seeded-conformance")
        {
            string? destination = null, compiler = null, runtime = null; uint seed = 1; int steps = 128, repetitions = 2; int[] workers = [1, 4];
var options = CliOptions.Parse(args, 1);
            destination = options.String("--output-dir"); compiler = options.String("--iverilog"); runtime = options.String("--vvp");
            seed = options.UInt("--seed", seed); steps = options.Int("--steps", steps); repetitions = options.Int("--repetitions", repetitions); workers = options.Workers(workers);
            if (destination is null) throw new ArgumentException("Seeded word output directory required.");
            var status = await WordSeededConformance.RunAsync(destination, seed, steps, workers, repetitions, runner, compiler, runtime, cancellationToken).ConfigureAwait(false); output.WriteLine(status + " " + Path.Combine(Path.GetFullPath(destination), "word-seeded-conformance-report.json")); return status == "pass" ? 0 : 1;
        }
        if (args.Length > 0 && args[0] is "word-inspect" or "word-replay" or "word-reduce")
        {
            bool inspect = args[0] == "word-inspect";
            if (inspect && args.Length != 2 || !inspect && (args.Length != 4 || args[2] != "--output-dir")) throw new ArgumentException("Usage: word-inspect <report.json> | word-replay|word-reduce <report.json> --output-dir <directory>");
            if (new FileInfo(args[1]).Length > 64 * 1024 * 1024) throw new ArgumentException("Word report exceeds 64 MiB inspection/replay limit."); var json = await File.ReadAllTextAsync(args[1], cancellationToken).ConfigureAwait(false);
            if (inspect) { var report = WordReportReader.Parse(json, cancellationToken); output.WriteLine($"{report.Status} steps={report.Fixture.Drives.Count} workers={string.Join(',', report.Fixture.Workers)}; {WordReportReader.ReplayAvailability(report)}"); return 0; }
            if (args[0] == "word-reduce") { output.WriteLine("first-mismatch prefix fixture: " + await WordReproduction.SavePrefixAsync(WordReportReader.Parse(json, cancellationToken), args[3], cancellationToken).ConfigureAwait(false)); return 0; }
            var replay = await WordReplay.RunAsync(WordReplay.Validate(json, cancellationToken), args[3], runner, cancellationToken).ConfigureAwait(false); output.WriteLine(replay.Status); return replay.Status == "pass" ? 0 : 1;
        }
        if (args.Length > 0 && args[0] is "word-differential" or "word-conformance")
        {
            bool differential = args[0] == "word-differential"; if (differential && args.Length < 2) throw new ArgumentException("Word fixture path required.");
            string? destination = null, compiler = null, runtime = null; int[] workers = [1, 4, 8];
var options = CliOptions.Parse(args, differential ? 2 : 1);
            destination = options.String("--output-dir"); compiler = options.String("--iverilog"); runtime = options.String("--vvp");
            if (!differential) workers = options.Workers(workers);
            if (destination is null) throw new ArgumentException("Word HDL output directory required.");
            if (differential)
            {
                if (new FileInfo(args[1]).Length > ConformanceLimits.MaxFixtureCharacters) throw new ArgumentException("Word fixture exceeds input limit.");
                var fixture = WordFixture.Parse(await File.ReadAllTextAsync(args[1], cancellationToken).ConfigureAwait(false)); var report = await new WordDifferential(runner).RunAsync(fixture, destination, compiler, runtime, cancellationToken).ConfigureAwait(false); output.WriteLine(report.Status + " " + Path.Combine(Path.GetFullPath(destination), "word-differential-report.json")); return report.Status == "pass" ? 0 : 1;
            }
            var results = await WordConformance.RunAsync(destination, workers, runner, compiler, runtime, cancellationToken).ConfigureAwait(false); output.WriteLine($"{(results.All(r => r.Status == "pass") ? "pass" : "fail")} cases={results.Count} " + Path.Combine(Path.GetFullPath(destination), "word-conformance-report.json")); return results.All(r => r.Status == "pass") ? 0 : 1;
        }
        if (args.Length > 0 && args[0] is "word-boundaries" or "word-operation-conformance")
        {
            bool boundaries = args[0] == "word-boundaries"; if (boundaries && args.Length < 2) throw new ArgumentException("IR path required.");
            string? destination = null; int[] workers = [1, 4];
var options = CliOptions.Parse(args, boundaries ? 2 : 1);
            destination = options.String(boundaries ? "--output" : "--output-dir"); workers = options.Workers(workers);
            if (destination is null) throw new ArgumentException("Output path required.");
            if (boundaries)
            {
                if (new FileInfo(args[1]).Length > ConformanceLimits.MaxFixtureCharacters) throw new ArgumentException("IR file exceeds input limit.");
                var design = IrDesign.Parse(await File.ReadAllTextAsync(args[1], cancellationToken).ConfigureAwait(false)); var fixture = IrStimulus.GenerateBoundaries(design, workers); await WriteNewAsync(destination, fixture.ToJson(), cancellationToken).ConfigureAwait(false); output.WriteLine(Path.GetFullPath(destination)); return 0;
            }
            var results = IrOperationConformance.Run(destination, workers, cancellationToken); output.WriteLine($"{(results.All(r => r.Passed) ? "pass" : "fail")} cases={results.Count} " + Path.Combine(Path.GetFullPath(destination), "word-operations-report.json")); return results.All(r => r.Passed) ? 0 : 1;
        }
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
var options = CliOptions.Parse(args, differential ? 2 : 1);
            destination = options.String(generate ? "--output" : "--output-dir");
            if (generate) { steps = options.Int("--steps", steps); seed = options.UInt("--seed", seed); }
            else { compiler = options.String("--iverilog"); runtime = options.String("--vvp"); workers = options.Workers(workers); }
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
var options = CliOptions.Parse(args, 1);
            if (options.String("--config") is { } configPath) config = Path.GetFullPath(configPath);
            if (options.String("--root") is { } rootPath) root = Path.GetFullPath(rootPath);
            directory = options.String("--output-dir"); surelog = options.String("--surelog");
            if (options.String("--workers") is not null) workers = options.Workers([]);
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
var options = CliOptions.Parse(args, 2);
            destination = options.String("--output"); steps = options.Int("--steps", steps); seed = options.UInt("--seed", seed);
            workers = options.Workers(workers); fourState = options.Flag("--four-state");
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
var options = CliOptions.Parse(args, 2);
                directory = options.String("--output-dir"); surelog = options.String("--surelog"); normalizer = options.String("--normalizer");
                if (args[0] == "suite") { include = options.String("--include")?.Split(','); exclude = options.String("--exclude")?.Split(','); shardIndex = options.Int("--shard-index", shardIndex); shardCount = options.Int("--shard-count", shardCount); }
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
var options = CliOptions.Parse(args, 1);
                top = options.String("--top"); directory = options.String("--output-dir"); surelog = options.String("--surelog"); normalizer = options.String("--normalizer");
                seed = options.UInt("--seed", seed); cycles = options.Int("--cycles", cycles); workers = options.Int("--threads", workers); jobs = options.Int("--build-jobs", jobs);
                if (options.String("--engines") is { } engineList) engines = engineList.Split(','); sources.AddRange(options.Sources);
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
        if (args.Length == 0 || args[0] is not ("compile" or "compile-rtl")) return Run(args, output, error);
        try
        {
            bool structural = args[0] == "compile-rtl";
            string? top = null, directory = null, surelog = null, normalizer = null; int workers = 1;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var sources = new List<string>();
            if (structural)
            {
                for (int i = 1; i < args.Length; i++)
                {
                    if (args[i] == "--") { sources.AddRange(args.Skip(i + 1)); break; }
                    if (!args[i].StartsWith("--", StringComparison.Ordinal)) { sources.Add(args[i]); continue; }
                    var option = args[i];
                    if (!seen.Add(option)) throw new ArgumentException("Duplicate compile-rtl option: " + option);
                    if (option == "--exporter") option = "--normalizer";
                    else if (option == "--normalizer") throw new ArgumentException("compile-rtl requires --exporter, not --normalizer.");
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
            }
            else
            {
                var options = CliOptions.Parse(args, 1);
                top = options.String("--top"); directory = options.String("--output-dir"); surelog = options.String("--surelog"); normalizer = options.String("--normalizer");
                workers = options.Int("--threads", workers); sources.AddRange(options.Sources);
            }
            if (top is null || directory is null) throw new ArgumentException("Compile requires --top and --output-dir.");
            var pipeline = new DesignPipeline(runner);
            output.WriteLine(structural
                ? await pipeline.CompileRtlAsync(sources, top, directory, surelog, normalizer, workers, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await pipeline.CompileAsync(sources, top, directory, surelog, normalizer, workers, cancellationToken: cancellationToken).ConfigureAwait(false));
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        { WriteError(error, ex); return 1; }
    }
    private static async Task<int> RunLintAsync(string[] args, TextWriter output, IProcessRunner? runner, CancellationToken cancellationToken)
    {
        bool wall = false, werror = false, noFatal = false, slangOnly = false, largeDesign = false, jsonOutput = false, sarifOutput = false, listRules = false, noConfig = false, warnUnusedWaivers = false, stopOnPhaseError = false, checkRules = false;
        string? top = null, slangExe = null, astJsonPath = null, outputPath = null, configPath = null, baselinePath = null, writeBaselinePath = null, initConfigPath = null, sdcPath = null, dumpFsmPath = null, dumpCdcPath = null, emitCdcSvaPath = null, initRulePath = null, emitCameoPreludePath = null;
        LintProfile? profile = null;
        IReadOnlySet<LintPhase>? phases = null;
        LintGroupBy groupBy = LintGroupBy.None;
        long? cameoMaxSteps = null;
        var ruleScripts = new List<string>();
        var enabledRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var disabledRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errorRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includeDirs = new List<string>();
        var defines = new List<string>();
        var sources = new List<string>();

        const string usage = "Usage: lint [<file.rtl.json|file.sv|file.uhdm> ... | --ast-json <ast.json>] [--top <module>] [--profile default|wall|signoff|fourstate|do254|starc|iso26262|lowrisc] [--phase rtl,fsm,cdc,style|all] [--stop-on-phase-error] [--sdc <file.sdc>] [--rules <rules.cameo>] [--cameo-max-steps <N>] [--group-by none|rule|signal|file|phase] [--dump-fsm <path.json>] [--dump-cdc <path.json>] [--emit-cdc-sva <path.sv>] [--config <velosim-lint.json>|--no-config] [-Wall] [-Werror] [-Wno-fatal] [-W<RULE>] [-Wno-<RULE>] [-Werror-<RULE>] [--baseline <file>] [--write-baseline <file>] [--warn-unused-waivers] [--lint-only] [--slang-only] [--slang <path>] [-I <dir>] [-D <macro>] [--large-design] [--json|--sarif] [--output <path>] | lint --list-rules [--rules <rules.cameo>] [--json] | lint --check-rules --rules <rules.cameo> | lint --init-config <path> | lint --init-rule <path.cameo> | lint --emit-cameo-prelude <path.d.tl>";

        for (int i = 1; i < args.Length; i++)
        {
            string tok = args[i];
            if (tok == "--")
            {
                sources.AddRange(args.Skip(i + 1));
                break;
            }
            if (!tok.StartsWith('-'))
            {
                sources.Add(tok);
                continue;
            }

            if (tok is "-Wall" or "--Wall" or "-Weverything" or "--Weverything") { wall = true; continue; }
            if (tok is "-Werror" or "--Werror") { werror = true; continue; }
            if (tok is "-Wno-fatal" or "--Wno-fatal") { noFatal = true; continue; }
            if (tok is "--lint-only") { continue; }
            if (tok is "--slang-only") { slangOnly = true; continue; }
            if (tok is "--large-design") { largeDesign = true; continue; }
            if (tok is "--json") { jsonOutput = true; continue; }
            if (tok is "--sarif") { sarifOutput = true; continue; }
            if (tok is "--list-rules") { listRules = true; continue; }
            if (tok is "--no-config") { noConfig = true; continue; }
            if (tok is "--warn-unused-waivers") { warnUnusedWaivers = true; continue; }
            if (tok is "--stop-on-phase-error") { stopOnPhaseError = true; continue; }
            if (tok is "--check-rules") { checkRules = true; continue; }

            if (tok is "--top" or "--slang" or "--ast-json" or "--output" or "--incdir" or "-I" or "--define" or "-D"
                or "--profile" or "--config" or "--baseline" or "--write-baseline" or "--init-config"
                or "--phase" or "--sdc" or "--group-by" or "--dump-fsm" or "--dump-cdc" or "--emit-cdc-sva"
                or "--rules" or "--cameo-max-steps" or "--init-rule" or "--emit-cameo-prelude")
            {
                if (++i >= args.Length) throw new ArgumentException("Incomplete lint option: " + tok);
                switch (tok)
                {
                    case "--top": top = args[i]; break;
                    case "--slang": slangExe = args[i]; break;
                    case "--ast-json": astJsonPath = args[i]; break;
                    case "--output": outputPath = args[i]; break;
                    case "--profile": profile = LintProfiles.Parse(args[i]); break;
                    case "--config": configPath = args[i]; break;
                    case "--baseline": baselinePath = args[i]; break;
                    case "--write-baseline": writeBaselinePath = args[i]; break;
                    case "--init-config": initConfigPath = args[i]; break;
                    case "--phase": phases = LintPhases.ParseList(args[i]); break;
                    case "--sdc": sdcPath = args[i]; break;
                    case "--group-by": groupBy = ParseLintGroupBy(args[i]); break;
                    case "--dump-fsm": dumpFsmPath = args[i]; break;
                    case "--dump-cdc": dumpCdcPath = args[i]; break;
                    case "--emit-cdc-sva": emitCdcSvaPath = args[i]; break;
                    case "--rules": ruleScripts.Add(args[i]); break;
                    case "--init-rule": initRulePath = args[i]; break;
                    case "--emit-cameo-prelude": emitCameoPreludePath = args[i]; break;
                    case "--cameo-max-steps":
                        if (!long.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out long steps) || steps <= 0)
                            throw new ArgumentException("Invalid --cameo-max-steps value (expected positive integer): " + args[i]);
                        cameoMaxSteps = steps;
                        break;
                    case "--incdir":
                    case "-I": includeDirs.Add(args[i]); break;
                    case "--define":
                    case "-D": defines.Add(args[i]); break;
                }
                continue;
            }

            if (tok.StartsWith("-I", StringComparison.Ordinal) && tok.Length > 2)
            {
                includeDirs.Add(tok[2..]);
                continue;
            }
            if (tok.StartsWith("-D", StringComparison.Ordinal) && tok.Length > 2)
            {
                defines.Add(tok[2..]);
                continue;
            }

            string normalized = tok.StartsWith("--W", StringComparison.Ordinal) ? tok[1..] : tok;
            if (normalized.StartsWith("-Wno-", StringComparison.Ordinal) && normalized.Length > 5)
            {
                disabledRules.Add(normalized[5..]);
                continue;
            }
            if ((normalized.StartsWith("-Werror-", StringComparison.Ordinal) || normalized.StartsWith("-Werror=", StringComparison.Ordinal)) && normalized.Length > 8)
            {
                errorRules.Add(normalized[8..]);
                continue;
            }
            if (normalized.StartsWith("-W", StringComparison.Ordinal) && normalized.Length > 2)
            {
                enabledRules.Add(normalized[2..]);
                continue;
            }

            throw new ArgumentException("Unknown lint option: " + tok + ". " + usage);
        }

        if (jsonOutput && sarifOutput)
            throw new ArgumentException("Cannot combine --json and --sarif output formats.");

        if (initRulePath is not null)
        {
            string fullRule = Path.GetFullPath(initRulePath);
            if (Path.GetDirectoryName(fullRule) is { Length: > 0 } ruleDir) Directory.CreateDirectory(ruleDir);
            await File.WriteAllTextAsync(fullRule, CameoLintRulePack.DefaultRuleTemplate() + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            output.WriteLine(fullRule);
            return 0;
        }

        if (emitCameoPreludePath is not null)
        {
            string fullPre = Path.GetFullPath(emitCameoPreludePath);
            if (Path.GetDirectoryName(fullPre) is { Length: > 0 } preDir) Directory.CreateDirectory(preDir);
            await File.WriteAllTextAsync(fullPre, CameoLintRulePack.PreludeDeclaration + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            output.WriteLine(fullPre);
            return 0;
        }

        LintConfig? config = null;
        if (configPath is not null)
        {
            config = LintConfig.LoadFile(configPath);
        }
        else if (!noConfig && !listRules && initConfigPath is null)
        {
            string? discovered = LintConfig.Discover(astJsonPath ?? sources.FirstOrDefault());
            if (discovered is not null) config = LintConfig.LoadFile(discovered);
        }

        var allRuleScripts = new List<string>();
        if (config?.RuleScripts is { Count: > 0 } cfgScripts) allRuleScripts.AddRange(cfgScripts);
        allRuleScripts.AddRange(ruleScripts);

        if (checkRules)
        {
            if (allRuleScripts.Count == 0)
                throw new ArgumentException("--check-rules requires at least one --rules <file.cameo> script or config rule_scripts.");
            int totalErrors = 0;
            foreach (string scriptPath in allRuleScripts)
            {
                var errs = CameoLintRulePack.CheckFile(scriptPath);
                if (errs.Count == 0)
                {
                    output.WriteLine($"ok: {scriptPath}");
                }
                else
                {
                    totalErrors += errs.Count;
                    foreach (var err in errs)
                        output.WriteLine($"error: {err}");
                }
            }
            return totalErrors == 0 ? 0 : 1;
        }

        if (listRules)
        {
            List<IRtlLintRule>? extraRules = null;
            if (allRuleScripts.Count > 0)
            {
                extraRules = [];
                long steps = cameoMaxSteps ?? config?.CameoMaxSteps ?? CameoLintRulePack.DefaultMaxSteps;
                foreach (string scriptPath in allRuleScripts)
                    extraRules.AddRange(CameoLintRulePack.LoadFile(scriptPath, steps).Rules);
            }
            string catalog = jsonOutput ? LintRuleCatalog.FormatRulesJson(extraRules) + Environment.NewLine : LintRuleCatalog.FormatRulesText(extraRules);
            await output.WriteAsync(catalog.AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        if (initConfigPath is not null)
        {
            string fullCfg = Path.GetFullPath(initConfigPath);
            if (Path.GetDirectoryName(fullCfg) is { Length: > 0 } cfgDir) Directory.CreateDirectory(cfgDir);
            await File.WriteAllTextAsync(fullCfg, LintConfig.DefaultTemplateJson() + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            output.WriteLine(fullCfg);
            return 0;
        }

        if (astJsonPath is null && sources.Count == 0)
            throw new ArgumentException(usage);

        LintBaseline? baseline = baselinePath is not null ? LintBaseline.LoadFile(baselinePath) : null;
        SdcConstraints? sdc = sdcPath is not null ? SdcConstraints.LoadFile(sdcPath) : null;

        var lintOptions = new LintOptions(
            Wall: wall,
            WarningsAsErrors: werror,
            NoFatal: noFatal,
            EnabledRules: enabledRules.Count > 0 ? enabledRules : null,
            DisabledRules: disabledRules.Count > 0 ? disabledRules : null,
            ErrorRules: errorRules.Count > 0 ? errorRules : null,
            LargeDesign: largeDesign,
            SlangOnly: slangOnly,
            IncludeDirectories: includeDirs.Count > 0 ? includeDirs : null,
            Defines: defines.Count > 0 ? defines : null,
            Profile: profile,
            Config: config,
            Baseline: baseline,
            WarnUnusedWaivers: warnUnusedWaivers,
            Phases: phases,
            StopOnPhaseError: stopOnPhaseError,
            Sdc: sdc,
            HasExplicitSdc: sdc is not null,
            GroupBy: groupBy,
            CameoRuleScripts: ruleScripts.Count > 0 ? ruleScripts : null,
            CameoMaxSteps: cameoMaxSteps);

        LintReport report;
        if (astJsonPath is not null)
        {
            string astJson = await File.ReadAllTextAsync(astJsonPath, cancellationToken).ConfigureAwait(false);
            string irJson = SlangAstLowerer.LowerAstJson(astJson, top);
            report = RtlLinter.LintJson(irJson, lintOptions, astJsonPath);
        }
        else if (sources.Count == 1 && sources[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            string irJson = await File.ReadAllTextAsync(sources[0], cancellationToken).ConfigureAwait(false);
            report = RtlLinter.LintJson(irJson, lintOptions, sources[0]);
        }
        else if (sources.Count == 1 && sources[0].EndsWith(".uhdm", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(top))
                throw new ArgumentException("Linting a .uhdm file requires --top <module>.");
            var uhdmLimits = largeDesign ? Uhdm.UhdmRtlLimits.Large : Uhdm.UhdmRtlLimits.FromEnvironment();
            try
            {
                string irJson = Uhdm.UhdmRtlLowerer.LowerFile(sources[0], top, uhdmLimits);
                report = RtlLinter.LintJson(irJson, lintOptions, sources[0]);
            }
            catch (InvalidOperationException ex)
            {
                report = RtlLinter.Finalize(
                    [new LintDiagnostic("UHDMAUDIT", LintSeverity.Error, ex.Message, "uhdm", new LintLocation(File: sources[0]))],
                    lintOptions);
            }
        }
        else
        {
            var slangLinter = new SlangLinter(runner);
            report = await slangLinter.LintSystemVerilogAsync(sources, lintOptions, top, slangExe, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (writeBaselinePath is not null)
        {
            string fullBase = Path.GetFullPath(writeBaselinePath);
            if (Path.GetDirectoryName(fullBase) is { Length: > 0 } baseDir) Directory.CreateDirectory(baseDir);
            await File.WriteAllTextAsync(fullBase, LintBaseline.FromReport(report).ToJson() + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }

        if (dumpFsmPath is not null)
        {
            string fullFsm = Path.GetFullPath(dumpFsmPath);
            if (Path.GetDirectoryName(fullFsm) is { Length: > 0 } fsmDir) Directory.CreateDirectory(fsmDir);
            await File.WriteAllTextAsync(fullFsm, report.FormatFsmDumpJson() + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }

        if (dumpCdcPath is not null)
        {
            string fullCdc = Path.GetFullPath(dumpCdcPath);
            if (Path.GetDirectoryName(fullCdc) is { Length: > 0 } cdcDir) Directory.CreateDirectory(cdcDir);
            await File.WriteAllTextAsync(fullCdc, report.FormatCdcDumpJson() + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }

        if (emitCdcSvaPath is not null)
        {
            string fullSva = Path.GetFullPath(emitCdcSvaPath);
            if (Path.GetDirectoryName(fullSva) is { Length: > 0 } svaDir) Directory.CreateDirectory(svaDir);
            string sva = (report.Cdc ?? CdcAnalysisResult.Empty).GenerateSvaAssertions(top ?? "velosim_cdc");
            await File.WriteAllTextAsync(fullSva, sva, cancellationToken).ConfigureAwait(false);
        }

        string rendered = sarifOutput
            ? report.ToSarif() + Environment.NewLine
            : jsonOutput
                ? report.ToJson() + Environment.NewLine
                : report.FormatText(groupBy);
        if (outputPath is not null)
        {
            string fullOut = Path.GetFullPath(outputPath);
            if (Path.GetDirectoryName(fullOut) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(fullOut, rendered, cancellationToken).ConfigureAwait(false);
        }
        await output.WriteAsync(rendered.AsMemory(), cancellationToken).ConfigureAwait(false);
        return report.ExitCode;
    }

    private static LintGroupBy ParseLintGroupBy(string value) => value.Trim().ToLowerInvariant() switch
    {
        "none" or "flat" => LintGroupBy.None,
        "rule" or "code" => LintGroupBy.Rule,
        "signal" or "net" => LintGroupBy.Signal,
        "file" => LintGroupBy.File,
        "phase" => LintGroupBy.Phase,
        _ => throw new ArgumentException($"Unknown --group-by mode '{value}' (expected: none, rule, signal, file, phase).")
    };
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
var options = CliOptions.Parse(args, 2);
            cycles = options.Int("--cycles", cycles); workers = options.Int("--threads", workers); release = options.Flag("--release-reset");
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
