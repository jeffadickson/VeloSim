#:property PublishAot=false
#:project ../Core/SchematicLab.Core.csproj
#:project ../Solver/SchematicLab.Solver.csproj
using System.Diagnostics;
using System.Globalization;
using SchematicLab.Core;
using SchematicLab.Solver;

if (args.Length != 3 || !File.Exists(args[0]) || !File.Exists(args[1]) || !Directory.Exists(args[2]))
    throw new ArgumentException("Pass ngspice, the native solver and an existing artifact directory.");

var directory = Path.GetFullPath(args[2]);
var deck = """
    Mixed RLC and controlled-source reference
    V1 in 0 DC 1 AC 1 30 PULSE(0 1 0 10u 10u 10m 20m)
    R1 in rl 100
    L1 rl filter 10m
    C1 filter 0 1u
    E1 e 0 filter 0 2
    G2 0 sum filter 0 1m
    F1 0 sum V1 .5
    H1 h 0 V1 30
    R2 e sum 1k
    R3 h sum 1k
    R4 sum 0 1k
    I1 0 sum DC .2m AC .2m 90 PULSE(0 .2m 0 20u 10u 10m 20m)
    .control
    set wr_singlescale
    set numdgt=16
    op
    wrdata dc.dat v(sum)
    ac lin 1 100 100
    wrdata ac-low.dat v(sum)
    ac lin 1 1000 1000
    wrdata ac-high.dat v(sum)
    tran 2u 3m 0 2u
    wrdata transient.dat v(sum)
    quit
    .endc
    .end
    """;
await File.WriteAllTextAsync(Path.Combine(directory, "mixed.cir"), deck);
var info = new ProcessStartInfo(Path.GetFullPath(args[0]))
{
    WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
    RedirectStandardOutput = true, RedirectStandardError = true
};
info.ArgumentList.Add("-b");
info.ArgumentList.Add("mixed.cir");
using var process = Process.Start(info) ?? throw new IOException("Unable to launch ngspice.");
var stdout = process.StandardOutput.ReadToEndAsync();
var stderr = process.StandardError.ReadToEndAsync();
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try { await process.WaitForExitAsync(deadline.Token); }
catch (OperationCanceledException)
{
    process.Kill(entireProcessTree: true);
    throw;
}
await File.WriteAllTextAsync(Path.Combine(directory, "ngspice.log"), await stdout + await stderr);
if (process.ExitCode != 0) throw new IOException($"ngspice exited with {process.ExitCode}.");

var solver = new NativeRcSolver(Path.GetFullPath(args[1]));
var session = new LessonSession();
session.Execute(new LoadBridgeDemo(session.Version));
foreach (var analysis in Enum.GetValues<BridgeAnalysis>())
{
    var result = await session.ExecuteBridgeAsync(new RunBridge(session.Version, BridgeDemo.Options(analysis)),
        solver.SolveCircuitAsync);
    var run = result.Bridge ?? throw new InvalidOperationException($"{analysis}: {result.Message}");
    var referenceFiles = analysis == BridgeAnalysis.Ac ? new[] { "ac-low.dat", "ac-high.dat" }
        : [analysis.ToString().ToLowerInvariant() + ".dat"];
    var reference = referenceFiles.SelectMany(file => File.ReadLines(Path.Combine(directory, file)))
        .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(number => double.Parse(number, CultureInfo.InvariantCulture)).ToArray()).ToArray();
    if (reference.Length == 0) throw new InvalidDataException("ngspice returned no reference data.");
    if (analysis == BridgeAnalysis.Ac && !run.Samples.Select(s => s.Axis).SequenceEqual(reference.Select(r => r[0])))
        throw new InvalidDataException("AC frequency grids differ.");
    if (analysis == BridgeAnalysis.Transient && (reference[0][0] != 0 ||
        Math.Abs(reference[^1][0] - run.Samples[^1].Axis) > 1e-12))
        throw new InvalidDataException("Transient domains differ.");

    var maximum = run.Samples.Select((sample, index) =>
    {
        if (analysis == BridgeAnalysis.Transient)
            return Math.Abs(sample.Real - Interpolate(reference, sample.Axis));
        var row = reference[index];
        return Math.Max(Math.Abs(sample.Real - row[1]),
            analysis == BridgeAnalysis.Ac ? Math.Abs(sample.Imaginary - row[2]) : 0);
    }).Max();
    var tolerance = analysis == BridgeAnalysis.Transient ? 5e-4 : 1e-9;
    Console.WriteLine(FormattableString.Invariant(
        $"{analysis}: native {run.Samples.Count}, ngspice {reference.Length}, max component error {maximum:G9} V, tolerance {tolerance:G9} V."));
    if (!double.IsFinite(maximum) || maximum > tolerance) Environment.ExitCode = 1;
}

static double Interpolate(double[][] points, double time)
{
    var index = Array.FindIndex(points, point => point[0] >= time);
    if (index == 0) return points[0][1];
    if (index < 0) return points[^1][1];
    var first = points[index - 1];
    var last = points[index];
    return first[1] + (last[1] - first[1]) * (time - first[0]) / (last[0] - first[0]);
}
