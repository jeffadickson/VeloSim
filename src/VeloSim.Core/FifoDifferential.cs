using System.Globalization;
using System.Text;
using System.Text.Json;
namespace VeloSim;

public sealed record FifoDrive(bool ResetN, bool Push, bool Pop, FourStateWord Data, string Phase);
public sealed record FifoDifference(string Engine, int Cycle, string Signal, string Expected, string Actual);
public sealed record FifoRun(string Status, string ReportPath, int Cycles, int Width, int Depth);
public static class FifoStimulus
{
    public static IReadOnlyDictionary<string, int> Coverage(IReadOnlyList<FifoDrive> drives, int depth)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal); int occupancy = 0, writes = 0, reads = 0;
        void Hit(string name) => result[name] = result.GetValueOrDefault(name) + 1;
        foreach (var d in drives)
        {
            bool full = occupancy == depth, empty = occupancy == 0;
            if (!d.ResetN) { Hit("reset"); if (!empty) Hit("reset_nonempty"); occupancy = writes = reads = 0; continue; }
            if (d.Push && full) Hit("push_on_full"); if (d.Pop && empty) Hit("pop_on_empty");
            if (d.Push && d.Pop) Hit(full ? "simultaneous_full" : empty ? "simultaneous_empty" : "simultaneous_middle");
            if (d.Push && !full) { occupancy++; Hit("accepted_push"); if (++writes % depth == 0) Hit("write_wrap"); if (d.Data.XMask != 0) Hit("unknown_data_write"); if (d.Data.ZMask != 0) Hit("high_impedance_data_write"); }
            if (d.Pop && !empty) { occupancy--; Hit("accepted_pop"); if (++reads % depth == 0) Hit("read_wrap"); }
        }
        return result;
    }
    public static IReadOnlyList<FifoDrive> Generate(int width, int depth, uint seed, int randomCycles)
    {
        if (width is < 1 or > 64 || depth is < 2 or > 256 || (depth & (depth - 1)) != 0 || randomCycles is < 0 or > 10000) throw new ArgumentException("FIFO requires widths1..64, power-of-two depths2..256 and0..10000 random cycles.");
        var result = new List<FifoDrive>(); uint state = seed == 0 ? 0x6D2B79F5 : seed;
        uint Next() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; }
        ulong mask = new FourStateWord(width).Mask; FourStateWord Data() => new(width, (((ulong)Next() << 32) | Next()) & mask);
        void Add(bool reset, bool push, bool pop, string phase, FourStateWord? data = null) => result.Add(new(reset, push, pop, data ?? Data(), phase));
        Add(false, true, true, "reset-dominance"); Add(true, false, true, "underflow"); Add(true, true, true, "simultaneous-empty"); Add(true, false, true, "drain-single");
        for (int i = 0; i < depth; i++) Add(true, true, false, "fill");
        Add(true, true, false, "overflow"); Add(true, true, true, "simultaneous-full"); Add(true, true, true, "simultaneous-middle");
        for (int i = 0; i < depth * 3; i++) { Add(true, false, true, "wrap-pop"); Add(true, true, false, "wrap-push"); }
        Add(false, true, true, "reset-while-nonempty");
        Add(true, true, false, "unknown-data", new(width, 0, mask)); Add(true, true, false, "high-impedance-data", new(width, 0, 0, mask));
        for (int i = 0; i < depth + 1; i++) Add(true, false, true, "drain-four-state");
        for (int i = 0; i < randomCycles; i++) { var flags = Next(); Add((flags & 31) != 0, (flags & 32) != 0, (flags & 64) != 0, "seeded"); }
        return result.AsReadOnly();
    }
}
public sealed class FifoDifferential(IProcessRunner? runner = null)
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(3);
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> ParseTrace(string text, RtlDesign design, int cycles)
    {
        var signals = design.Signals.Where(s => s.Direction == "output").ToArray(); var trace = new List<IReadOnlyDictionary<string, string>>();
        foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("VS_RTL", StringComparison.Ordinal)))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != signals.Length + 2 || parts[0] != "VS_RTL" || parts[1] != (trace.Count + 1).ToString(CultureInfo.InvariantCulture)) throw new FormatException("Malformed FIFO trace dimensions/order.");
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < signals.Length; i++) { var value = parts[i + 2].ToLowerInvariant(); if (value.Length != signals[i].Width || value.Any(c => c is not ('0' or '1' or 'x' or 'z'))) throw new FormatException("FIFO trace width/digit mismatch."); values.Add(signals[i].Name, value); }
            trace.Add(values);
        }
        if (trace.Count != cycles) throw new FormatException("Incomplete or extra FIFO trace cycles."); return trace.AsReadOnly();
    }
    public static IReadOnlyList<FifoDifference> CheckQueue(IReadOnlyList<FifoDrive> drives, IReadOnlyList<IReadOnlyDictionary<string, string>> trace, int depth, string engine)
    {
        if (trace.Count != drives.Count) throw new ArgumentException("Queue checker trace length differs.");
        var queue = new Queue<FourStateWord>(); var differences = new List<FifoDifference>();
        for (int cycle = 0; cycle < drives.Count; cycle++)
        {
            var drive = drives[cycle]; bool full = queue.Count == depth, empty = queue.Count == 0;
            if (!drive.ResetN) queue.Clear();
            else { if (drive.Push && !full) queue.Enqueue(drive.Data); if (drive.Pop && !empty) queue.Dequeue(); }
            void Compare(string signal, string expected) { if (trace[cycle][signal] != expected) differences.Add(new(engine, cycle + 1, signal, expected, trace[cycle][signal])); }
            Compare("full", queue.Count == depth ? "1" : "0"); Compare("empty", queue.Count == 0 ? "1" : "0");
            // The FIFO exposes retained/uninitialized storage when empty. The
            // queue checks valid data; the external trace comparison checks dout even when empty.
            if (queue.Count > 0) Compare("dout", FourStateCodec.FormatBinary(queue.Peek()));
        }
        return differences.AsReadOnly();
    }
    public static void ValidateInterface(RtlDesign design)
    {
        var expected = new Dictionary<string, (string Direction, int Width)>(StringComparer.Ordinal) { ["clk"] = ("input", 1), ["rst_n"] = ("input", 1), ["push"] = ("input", 1), ["pop"] = ("input", 1), ["din"] = ("input", 0), ["dout"] = ("output", 0), ["full"] = ("output", 1), ["empty"] = ("output", 1) };
        var ports = design.Signals.Where(s => s.Direction != "internal").ToArray();
        if (design.Clock != "clk" || design.Memories.Count != 1 || ports.Length != expected.Count || ports.Any(s => !expected.TryGetValue(s.Name, out var e) || s.Direction != e.Direction || e.Width != 0 && s.Width != e.Width)) throw new IrValidationException("FIFO workflow requires the documented clk/rst_n/push/pop/din/dout/full/empty interface and one memory.");
        int width = design.Signals[design.SignalId("din")].Width; var memory = design.Memories[0];
        if (design.Signals[design.SignalId("dout")].Width != width || memory.Width != width || (memory.Depth & (memory.Depth - 1)) != 0) throw new IrValidationException("FIFO data widths must match and depth must be a power of two.");
    }
    private static string Testbench(RtlDesign design, string top, IReadOnlyList<FifoDrive> drives)
    {
        var b = new StringBuilder("module velosim_fifo_tb;\n"); foreach (var signal in design.Signals.Where(s => s.Direction != "internal")) b.AppendLine($"logic [{signal.Width - 1}:0] {signal.Name};");
        b.AppendLine(top + " dut(" + string.Join(',', design.Signals.Where(s => s.Direction != "internal").Select(s => $".{s.Name}({s.Name})")) + ");\ninitial begin clk=0;");
        for (int i = 0; i < drives.Count; i++)
        {
            var d = drives[i]; b.AppendLine($"rst_n=1'b{(d.ResetN ? 1 : 0)};push=1'b{(d.Push ? 1 : 0)};pop=1'b{(d.Pop ? 1 : 0)};din={d.Data.Width}'b{FourStateCodec.FormatBinary(d.Data)};#1;clk=1;#1;");
            var outputs = design.Signals.Where(s => s.Direction == "output").ToArray(); b.AppendLine($"$display(\"VS_RTL {i + 1} {string.Join(' ', outputs.Select(_ => "%b"))}\",{string.Join(',', outputs.Select(s => s.Name))});clk=0;#1;");
        }
        return b.AppendLine("$finish;end endmodule").ToString();
    }
    public async Task<FifoRun> RunAsync(string source, string top, string outputDirectory, IReadOnlyList<int> workers, int jobs = 4, uint seed = 42, int randomCycles = 1000, string? exporter = null, string? surelog = null, CancellationToken cancellationToken = default)
    {
        if (!RtlDesign.Identifier(top) || jobs is < 1 or > 256 || workers.Count is < 1 or > 16 || workers.Distinct().Count() != workers.Count || workers.Any(w => w is < 1 or > 256) || randomCycles is < 0 or > 10000) throw new ArgumentException("Invalid FIFO workflow arguments.");
        cancellationToken.ThrowIfCancellationRequested(); var output = WordEvidenceLimits.FreshOutput(outputDirectory); Directory.CreateDirectory(output); source = Path.GetFullPath(source);
        exporter = ArtifactMetadata.ResolveExecutable(exporter ?? Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL") ?? "velosim-uhdm-rtl", Environment.CurrentDirectory) ?? exporter;
        if (surelog is not null) surelog = ArtifactMetadata.ResolveExecutable(surelog, Environment.CurrentDirectory) ?? surelog;
        var recording = new RecordingProcessRunner(runner ?? new ProcessRunner()); var sourceHashes = ArtifactMetadata.SourceClosure([source]);
        async Task<ProcessResult> Run(string exe, IReadOnlyList<string> args, string cwd) { var result = await recording.RunAsync(exe, args, cwd, Deadline, cancellationToken).ConfigureAwait(false); if (result.ExitCode != 0) throw new IOException($"{exe} failed ({result.ExitCode}): {result.StandardError}"); return result; }
        var elaboration = await new SurelogFrontend(recording).ElaborateAsync([source], Path.Combine(output, "surelog"), top, surelog, jobs, cancellationToken: cancellationToken).ConfigureAwait(false);
        var irPath = Path.Combine(output, "design.rtl.json"); await Run(exporter ?? Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL") ?? "velosim-uhdm-rtl", [elaboration.UhdmPath, irPath, top], output).ConfigureAwait(false);
        var design = RtlDesign.Parse(await File.ReadAllTextAsync(irPath, cancellationToken).ConfigureAwait(false)); ValidateInterface(design); int width = design.Signals[design.SignalId("din")].Width, depth = design.Memories[0].Depth;
        var drives = FifoStimulus.Generate(width, depth, seed, randomCycles); var nativeDir = Path.Combine(output, "native"); Directory.CreateDirectory(nativeDir);
        var runtime = FindRuntime(); File.Copy(runtime, Path.Combine(nativeDir, "runtime.h")); await File.WriteAllTextAsync(Path.Combine(nativeDir, "design.cpp"), design.EmitCpp(), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(nativeDir, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.20)\nproject(VeloSimRtl LANGUAGES CXX)\nset(CMAKE_CXX_STANDARD 20)\nadd_executable(velosim_rtl design.cpp)\n", cancellationToken).ConfigureAwait(false);
        var compiledInputs = new[] { "runtime.h", "design.cpp", "CMakeLists.txt" }.Select(name => ArtifactMetadata.Fingerprint(Path.Combine(nativeDir, name))).ToArray();
        var stimulus = Path.Combine(output, "stimulus.txt");
        string Value(FifoDrive drive, string name) => name switch { "rst_n" => drive.ResetN ? "1" : "0", "push" => drive.Push ? "1" : "0", "pop" => drive.Pop ? "1" : "0", "din" => FourStateCodec.FormatBinary(drive.Data), _ => throw new IrValidationException("Unsupported input.") };
        await File.WriteAllLinesAsync(stimulus, drives.Select(d => string.Join(' ', design.Signals.Where(s => s.Direction == "input" && s.Name != design.Clock).Select(s => Value(d, s.Name)))), cancellationToken).ConfigureAwait(false);
        var tb = Path.Combine(output, "reference.sv"); await File.WriteAllTextAsync(tb, Testbench(design, top, drives), cancellationToken).ConfigureAwait(false);
        var traces = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, string>>>(StringComparer.Ordinal); var errors = new Dictionary<string, string>(StringComparer.Ordinal); var differences = new List<FifoDifference>(); var compilerTools = new List<FileFingerprint>(); var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        bool BackendFailure(Exception e) => e is IOException or ArgumentException or FormatException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception;
        try
        {
            await Run("cmake", ["-S", nativeDir, "-B", Path.Combine(nativeDir, "build"), "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release"], output).ConfigureAwait(false);
            var cache = File.ReadAllLines(Path.Combine(nativeDir, "build", "CMakeCache.txt"));
            foreach (string key in new[] { "CMAKE_CXX_COMPILER:", "CMAKE_MAKE_PROGRAM:" })
            {
                string tool = cache.Single(line => line.StartsWith(key, StringComparison.Ordinal)).Split('=', 2)[1]; compilerTools.Add(ArtifactMetadata.Fingerprint(tool));
                var version = await Run(tool, ["--version"], output).ConfigureAwait(false); versions[tool] = version.StandardOutput + version.StandardError;
            }
            await Run("cmake", ["--build", Path.Combine(nativeDir, "build"), "--parallel", jobs.ToString(CultureInfo.InvariantCulture)], output).ConfigureAwait(false);
            foreach (int count in workers) { var result = await Run(Path.Combine(nativeDir, "build", "velosim_rtl"), [stimulus, count.ToString(CultureInfo.InvariantCulture)], output).ConfigureAwait(false); traces.Add("velosim-" + count, ParseTrace(result.StandardOutput, design, drives.Count)); }
        }
        catch (Exception e) when (BackendFailure(e)) { errors.Add("native", e.Message); }
        var compiled = Path.Combine(output, "reference.vvp");
        try { await Run("iverilog", ["-g2012", "-s", "velosim_fifo_tb", "-o", compiled, source, tb], output).ConfigureAwait(false); var result = await Run("vvp", [compiled], output).ConfigureAwait(false); traces.Add("icarus", ParseTrace(result.StandardOutput, design, drives.Count)); }
        catch (Exception e) when (BackendFailure(e)) { errors.Add("icarus", e.Message); }
        foreach (var (engine, trace) in traces) differences.AddRange(CheckQueue(drives, trace, depth, engine));
        if (traces.TryGetValue("icarus", out var reference)) foreach (var (engine, trace) in traces.Where(t => t.Key != "icarus")) for (int i = 0; i < trace.Count; i++) foreach (var signal in reference[i].Keys) if (reference[i][signal] != trace[i][signal]) differences.Add(new(engine + ":icarus", i + 1, signal, reference[i][signal], trace[i][signal]));
        foreach (var file in sourceHashes) if (ArtifactMetadata.Fingerprint(file.Path).Sha256 != file.Sha256) errors.TryAdd("source_changed", "Input source changed during execution.");
        foreach (var file in compiledInputs.Concat(compilerTools)) if (ArtifactMetadata.Fingerprint(file.Path).Sha256 != file.Sha256) errors.TryAdd("build_input_changed", "Compiled input or compiler changed during execution.");
        string status = differences.Count > 0 ? "fail" : errors.Count > 0 || traces.Count != workers.Count + 1 ? "incomplete" : "pass";
        var artifacts = new List<FileFingerprint>(sourceHashes) { ArtifactMetadata.Fingerprint(elaboration.UhdmPath), ArtifactMetadata.Fingerprint(irPath), ArtifactMetadata.Fingerprint(stimulus), ArtifactMetadata.Fingerprint(tb) }; artifacts.AddRange(compiledInputs);
        foreach (var file in new[] { compiled, Path.Combine(nativeDir, "build", "velosim_rtl") }) if (File.Exists(file)) artifacts.Add(ArtifactMetadata.Fingerprint(file));
        var tools = recording.Commands.Select(c => ArtifactMetadata.ResolveExecutable(c.Executable, c.WorkingDirectory)).OfType<string>().Distinct().Select(ArtifactMetadata.Fingerprint).Concat(compilerTools).DistinctBy(f => f.Path).ToArray();
        var report = Path.Combine(output, "fifo-report.json"); using var stream = new MemoryStream(); using (var w = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            w.WriteStartObject(); w.WriteString("schema", "velosim.fifo.differential.v1"); w.WriteString("status", status); w.WriteString("source_mode", "actual_RTL_Surelog_UHDM_structural_IR_native_Cpp_compilation"); w.WriteString("execution_scope", "one_posedge_process_NBA_unpacked_memory_acyclic_continuous_logic"); w.WriteNumber("effective_sequential_processes", 1); w.WriteString("worker_scope", "requested_configurations_single_process_executes_serially_no_parallel_speedup_claim"); w.WriteNumber("width", width); w.WriteNumber("depth", depth); w.WriteNumber("cycles", drives.Count); w.WriteNumber("seed", seed); w.WriteNumber("build_jobs", jobs);
            w.WriteStartObject("scenario_coverage"); foreach (var (scenario, count) in FifoStimulus.Coverage(drives, depth)) w.WriteNumber(scenario, count); w.WriteEndObject();
            w.WriteStartArray("stimulus"); foreach (var d in drives) { w.WriteStartObject(); w.WriteBoolean("rst_n", d.ResetN); w.WriteBoolean("push", d.Push); w.WriteBoolean("pop", d.Pop); w.WriteString("din", FourStateCodec.FormatBinary(d.Data)); w.WriteString("phase", d.Phase); w.WriteEndObject(); } w.WriteEndArray();
            w.WriteStartObject("traces"); foreach (var (engine, trace) in traces) { w.WriteStartArray(engine); foreach (var row in trace) { w.WriteStartObject(); foreach (var (signal, value) in row) w.WriteString(signal, value); w.WriteEndObject(); } w.WriteEndArray(); } w.WriteEndObject();
            w.WriteStartObject("errors"); foreach (var (engine, message) in errors) w.WriteString(engine, message); w.WriteEndObject();
            w.WriteStartArray("differences"); foreach (var d in differences.Take(1000)) { w.WriteStartObject(); w.WriteString("engine", d.Engine); w.WriteNumber("cycle", d.Cycle); w.WriteString("signal", d.Signal); w.WriteString("expected", d.Expected); w.WriteString("actual", d.Actual); w.WriteEndObject(); } w.WriteEndArray(); w.WriteNumber("total_differences", differences.Count);
            new ArtifactMetadata(artifacts, null, recording.Commands.ToArray(), versions, tools).Write(w); w.WriteEndObject();
        }
        await File.WriteAllTextAsync(report, Encoding.UTF8.GetString(stream.ToArray()) + "\n", cancellationToken).ConfigureAwait(false); return new(status, report, drives.Count, width, depth);
    }
    private static string FindRuntime()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent) { var file = Path.Combine(directory.FullName, "native", "rtl", "runtime.h"); if (File.Exists(file)) return file; }
        throw new IOException("Run FIFO native compilation from a VeloSim checkout containing native/rtl/runtime.h.");
    }
}
