using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VeloSim;
public sealed record BenchmarkPlan(int Processes, int Cycles, int Width, int Repetitions, int Warmups, IReadOnlyList<int> Workers)
{
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Workers);
        if (Processes is < 2 or > 256 || Cycles is < 1 or > 100_000 || Width is < 1 or > 64 || Repetitions is < 1 or > 50 || Warmups is < 0 or > 10 || Workers.Count is < 1 or > 16 || Workers.Distinct().Count() != Workers.Count || Workers.Any(w => w is < 1 or > 256)) throw new ArgumentException("Invalid benchmark dimensions.");
        if ((long)Processes * Cycles * (Repetitions + Warmups) * Workers.Count > 2_000_000) throw new ArgumentException("Benchmark work budget exceeded.");
    }
}
public sealed record BenchmarkStatistics(double Minimum, double Median, double P95, double Maximum)
{
    public static BenchmarkStatistics From(IReadOnlyList<double> values)
    {
        if (values.Count == 0 || values.Any(v => !double.IsFinite(v) || v <= 0)) throw new ArgumentException("Measurements must be finite and positive.");
        var ordered = values.Order().ToArray(); var middle = ordered.Length / 2;
        return new(ordered[0], ordered.Length % 2 == 0 ? ordered[middle - 1] / 2 + ordered[middle] / 2 : ordered[middle], ordered[(int)Math.Ceiling(0.95 * ordered.Length) - 1], ordered[^1]);
    }
}
public sealed record BenchmarkCell(int Workers, string Status, IReadOnlyList<double> Seconds, string? Checksum, string? Error);
public sealed record BenchmarkReport(BenchmarkPlan Plan, string NativePath, string NativeHash, string NativeProfile, string HostPath, string HostHash, int LogicalProcessors, bool DynamicCodeSupported, IReadOnlyList<BenchmarkCell> Cells)
{
    public IReadOnlyList<FileFingerprint> ManagedAssemblies { get; init; } = [];
    public string Status => Cells.Any(c => c.Status == "canceled") ? "canceled" : Cells.All(c => c.Status == "pass") ? "pass" : "fail";
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            w.WriteStartObject(); w.WriteString("format", "velosim.native.benchmark.v1"); w.WriteString("status", Status);
            w.WriteString("measurement_scope", "managed_drive_api_native_kernel_and_activation_checks; setup_and_final_checksum_excluded");
            w.WriteString("native_path", NativePath); w.WriteString("native_sha256", NativeHash); w.WriteString("native_profile", NativeProfile); w.WriteString("host_path", HostPath); w.WriteString("host_sha256", HostHash);
            w.WriteNumber("logical_processors", LogicalProcessors); w.WriteBoolean("dynamic_code_supported", DynamicCodeSupported);
            w.WriteStartArray("managed_assemblies"); foreach (var assembly in ManagedAssemblies) { w.WriteStartObject(); w.WriteString("path", assembly.Path); w.WriteString("sha256", assembly.Sha256); w.WriteEndObject(); } w.WriteEndArray();
            w.WriteStartObject("plan"); w.WriteNumber("processes", Plan.Processes); w.WriteNumber("cycles", Plan.Cycles); w.WriteNumber("width", Plan.Width); w.WriteNumber("repetitions", Plan.Repetitions); w.WriteNumber("warmups", Plan.Warmups); w.WriteStartArray("workers"); foreach (var worker in Plan.Workers) w.WriteNumberValue(worker); w.WriteEndArray(); w.WriteEndObject();
            var baseline = Cells.FirstOrDefault(c => c.Workers == 1 && c.Status == "pass"); double? baselineMedian = baseline is null ? null : BenchmarkStatistics.From(baseline.Seconds).Median;
            w.WriteStartArray("cells"); foreach (var c in Cells)
            {
                w.WriteStartObject(); w.WriteNumber("workers", c.Workers); w.WriteString("status", c.Status); w.WriteString("checksum", c.Checksum); w.WriteString("error", c.Error);
                w.WriteStartArray("seconds"); foreach (var value in c.Seconds) w.WriteNumberValue(value); w.WriteEndArray();
                if (c.Status == "pass")
                {
                    var stats = BenchmarkStatistics.From(c.Seconds); w.WriteNumber("minimum_seconds", stats.Minimum); w.WriteNumber("median_seconds", stats.Median); w.WriteNumber("p95_seconds", stats.P95); w.WriteNumber("maximum_seconds", stats.Maximum);
                    if (baselineMedian is { } one) w.WriteNumber("relative_median_to_one_worker", one / stats.Median);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
public static class NativeBenchmark
{
    public static BenchmarkReport Run(BenchmarkPlan plan, CancellationToken cancellationToken = default)
    {
        plan.Validate(); plan = plan with { Workers = Array.AsReadOnly(plan.Workers.ToArray()) }; cancellationToken.ThrowIfCancellationRequested();
        var native = Simulation.NativeLibraryPath; var profile = Simulation.NativeBuildProfile;
        var host = Environment.ProcessPath ?? throw new IOException("Host executable path unavailable.");
        static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
        string nativeHash = Hash(native), hostHash = Hash(host);
        var implementation = new List<FileFingerprint>();
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            var entry = Environment.GetCommandLineArgs().FirstOrDefault();
            foreach (var file in new[] { Path.Combine(AppContext.BaseDirectory, "VeloSim.dll"), entry is null ? "" : Path.GetFullPath(entry) }.Where(File.Exists).Distinct(StringComparer.Ordinal))
                implementation.Add(new(Path.GetFullPath(file), Hash(file)));
            if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && !implementation.Any(f => Path.GetFileName(f.Path) == "VeloSim.dll")) throw new IOException("Managed benchmark implementation fingerprint unavailable.");
        }
        var signals = new List<IrSignal> { new("clk", new(1)), new("reset", new(1, 1)) };
        var processes = new List<IrProcess>();
        for (int i = 0; i < plan.Processes; i++) { var name = "v" + i; signals.Add(new(name, new(plan.Width))); processes.Add(new("clk", name, name, "reset", IrEdge.Posedge, IrOperation.SvIncrement)); }
        var design = new IrDesign(signals, processes); var cells = new List<BenchmarkCell>(); string? commonChecksum = null;
        Span<byte> checksumBytes = stackalloc byte[8];
        foreach (var workers in plan.Workers)
        {
            var samples = new List<double>(); string? checksum = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int repetition = -plan.Warmups; repetition < plan.Repetitions; repetition++)
                {
                    using var simulation = new Simulation(design);
                    var low = new Dictionary<string, FourStateWord> { ["clk"] = new(1) }; var high = new Dictionary<string, FourStateWord> { ["clk"] = new(1, 1) };
                    long start = Stopwatch.GetTimestamp();
                    for (int cycle = 0; cycle < plan.Cycles; cycle++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (simulation.Drive(low, workers) != 0 || simulation.Drive(high, workers) != plan.Processes) throw new InvalidOperationException("Benchmark activation count differs from workload.");
                    }
                    double elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
                    if (simulation.Ticks != (ulong)plan.Cycles * 2) throw new InvalidOperationException("Benchmark tick count differs from workload.");
                    ulong mask = plan.Width == 64 ? ulong.MaxValue : (1UL << plan.Width) - 1; var expected = new FourStateWord(plan.Width, (ulong)plan.Cycles & mask);
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    foreach (var signal in signals.Skip(2))
                    {
                        var actual = simulation.Read(signal.Name); if (actual != expected) throw new InvalidOperationException("Benchmark result differs from independent expected counter.");
                        BinaryPrimitives.WriteUInt64LittleEndian(checksumBytes, actual.Value); digest.AppendData(checksumBytes);
                    }
                    checksum = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
                    if (commonChecksum is not null && commonChecksum != checksum) throw new InvalidOperationException("Benchmark worker checksums differ."); commonChecksum = checksum;
                    if (repetition >= 0) samples.Add(elapsed);
                }
                cells.Add(new(workers, "pass", samples.AsReadOnly(), checksum, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { cells.Add(new(workers, "canceled", samples.AsReadOnly(), checksum, "Canceled during benchmark.")); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException) { cells.Add(new(workers, "fail", samples.AsReadOnly(), checksum, ex.Message)); }
        }
        return new(plan, native, nativeHash, profile, host, hostHash, Environment.ProcessorCount, RuntimeFeature.IsDynamicCodeSupported, cells.AsReadOnly()) { ManagedAssemblies = implementation.AsReadOnly() };
    }
}
