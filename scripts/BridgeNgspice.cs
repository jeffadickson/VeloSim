#:property PublishAot=false
#:project ../Core/SchematicLab.Core.csproj
#:project ../Solver/SchematicLab.Solver.csproj
using System.Diagnostics;
using System.Globalization;
using SchematicLab.Core;
using SchematicLab.Solver;

if (args.Length is not (3 or 4) || !File.Exists(args[0]) || !File.Exists(args[1]) || !Directory.Exists(args[2]))
    throw new ArgumentException("Pass ngspice, the native solver, an empty artifact directory and optionally mixed, diode or mos1.");

var directory = Path.GetFullPath(args[2]);
if (Directory.EnumerateFileSystemEntries(directory).Any())
    throw new ArgumentException("Use an empty artifact directory to prevent stale oracle data.");
var fixture = args.Length == 3 ? "mixed" : args[3];
if (fixture is not ("mixed" or "diode" or "diode-charge" or "mos1" or "mos1-charge" or "bjt" or "bsim3" or "waveforms" or "exponential" or "sine" or "fd-soi" or "mos1-high" or "hierarchy")) throw new ArgumentException("Unknown fixture.");
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
if (fixture == "hierarchy")
{
    deck = """
        Nested resistor cell reference
        V1 in 0 DC 5 AC 1 PULSE(0 5 0 100u 100u 10m 20m)
        X1 in out outer
        R2 out 0 3k
        .subckt outer a b
        X1 a b inner
        .ends
        .subckt inner a b
        R1 a b 1k
        .ends
        .control
        set wr_singlescale
        set numdgt=16
        op
        wrdata dc.dat v(out)
        ac lin 1 100 100
        wrdata ac-low.dat v(out)
        ac lin 1 1000 1000
        wrdata ac-high.dat v(out)
        tran 10u 5m 0 10u
        wrdata transient.dat v(out)
        quit
        .endc
        .end
        """;
}
else if (fixture is "waveforms" or "exponential" or "sine")
{
    var voltage = fixture switch
    {
        "waveforms" => "PWL(0 0 20u 1 3m 1)",
        "exponential" => "EXP(0 1 20u 100u 1m 200u)",
        _ => "SIN(0 1 1k 0 100)"
    };
    deck = $"""
        Source waveform through a divider
        V1 in 0 {voltage}
        R1 in sum 1k
        R2 sum 0 1k
        .control
        set wr_singlescale
        set numdgt=16
        tran .25u 3m 0 .25u
        wrdata transient.dat v(sum)
        quit
        .endc
        .end
        """;
}
else if (fixture != "mixed")
{
    var observable = fixture.StartsWith("diode", StringComparison.Ordinal) ? "diode" : "drain";
    deck = """
        Diode and MOS1 conduction reference
        V1 vdd 0 1
        V2 gate 0 DC 1.5 AC 1 PULSE(1.2 1.6 0 20u 20u 5m 10m)
        V3 vd 0 DC .8 AC 1 PULSE(.2 1 0 20u 20u 5m 10m)
        R1 vdd drain 1k
        R2 vd diode 1k
        M1 drain gate 0 0 MN W=10u L=10u
        D1 diode 0 JD
        .model MN NMOS(LEVEL=1 VTO=.7 KP=1m LAMBDA=.02 GAMMA=0 PHI=.6 IS=1e-30)
        .model JD D(IS=1e-14 N=1 RS=0 TT=0 CJO=0)
        .temp 27
        .options tnom=27
        """ + Environment.NewLine +
        deck[deck.IndexOf(".control", StringComparison.Ordinal)..]
            .Replace("v(sum)", $"v({observable})", StringComparison.Ordinal)
            .Replace("tran 2u 3m 0 2u", "tran .025u 100u 0 .025u", StringComparison.Ordinal);
    if (fixture is "mos1-charge" or "mos1-high")
        deck = deck.Replace("IS=1e-30)", "IS=1e-30 CGSO=1e-5 CGDO=2e-5 CGBO=5e-6 CBD=3e-10 CBS=4e-10 PB=.8 MJ=.5 FC=.5)", StringComparison.Ordinal);
    if (fixture == "mos1-high")
        deck = deck.Replace("V1 vdd 0 1", "V1 vdd 0 3", StringComparison.Ordinal)
            .Replace("V3 vd 0 DC .8", "V3 vd 0 DC 1", StringComparison.Ordinal);
    if (fixture == "diode-charge")
        deck = deck.Replace("TT=0 CJO=0", "TT=10n CJO=2n VJ=.7 M=.5 FC=.5", StringComparison.Ordinal);
}
if (fixture == "bjt")
{
    deck = """
        Three-terminal NPN conduction reference
        V1 vdd 0 2
        V2 drive 0 DC .65 AC 1 PULSE(.55 .68 0 20u 20u 5m 10m)
        R1 vdd collector 1k
        R2 drive base 500
        Q1 collector base 0 QN
        .model QN NPN(IS=1e-15 BF=100 BR=1 NF=1 NR=1 CJE=300p VJE=.75 MJE=.33 CJC=200p VJC=.75 MJC=.33 FC=.5 TF=1n TR=2n)
        .temp 27
        .options tnom=27
        """ + Environment.NewLine + deck[deck.IndexOf(".control", StringComparison.Ordinal)..]
            .Replace("v(drain)", "v(collector)", StringComparison.Ordinal);
}
if (fixture == "bsim3")
{
    deck = """
        Loaded bulk BSIM3 3.3.0 gate ramp
        V1 vdd 0 .9
        V2 gate 0 DC .8 AC 1 PULSE(.8 .9 0 1n 1n 5n 10n)
        R1 vdd drain 1k
        C1 drain 0 20f
        M1 drain gate 0 0 MN W=1u L=.35u PD=1.5u PS=1.5u AD=1.5p AS=1.5p
        .model MN NMOS(LEVEL=49 VERSION=3.3.0 TOX=3.5n NCH=2.4e17 NSUB=5e16 VTH0=.15)
        .temp 27
        .options method=gear maxord=1
        """ + Environment.NewLine + deck[deck.IndexOf(".control", StringComparison.Ordinal)..]
            .Replace("ac lin 1 100 100", "ac lin 1 1meg 1meg", StringComparison.Ordinal)
            .Replace("ac lin 1 1000 1000", "ac lin 1 1g 1g", StringComparison.Ordinal)
            .Replace("tran .025u 100u 0 .025u", "tran 1p 1n 0 1p", StringComparison.Ordinal);
}
if (fixture == "fd-soi")
{
    var card = Environment.GetEnvironmentVariable("SCHEMATIC_SOI_NGSPICE_CARD");
    if (card is null || !Path.IsPathFullyQualified(card) || !File.Exists(card))
        throw new ArgumentException("Set SCHEMATIC_SOI_NGSPICE_CARD to the pinned FD model card.");
    var bytes = await File.ReadAllBytesAsync(card);
    if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) !=
        "8CB77650FD65C5712F9C2CFAFBC974F5B49DA1C1411AD9CC910E17965CDA6491")
        throw new InvalidDataException("The FD reference card does not match the pinned bytes.");
    await File.WriteAllBytesAsync(Path.Combine(directory, "nmosfd.mod"), bytes);
    deck = """
        Loaded pinned FD SOI
        V1 vdd 0 .8
        V2 gate 0 .8
        V3 back 0 -1
        R1 vdd drain 1k
        M1 drain gate 0 back n1 w=10u l=.25u
        .option gmin=1e-25 itl1=500 noacct
        .include nmosfd.mod
        .control
        set wr_singlescale
        set numdgt=16
        op
        wrdata dc.dat v(drain)
        quit
        .endc
        .end
        """;
}
await File.WriteAllTextAsync(Path.Combine(directory, "reference.cir"), deck);
var info = new ProcessStartInfo(Path.GetFullPath(args[0]))
{
    WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
    RedirectStandardOutput = true, RedirectStandardError = true
};
info.ArgumentList.Add("-b");
info.ArgumentList.Add("reference.cir");
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
session.Execute(fixture switch
{
    "waveforms" or "exponential" or "sine" => new ResetLesson(session.Version),
    "hierarchy" => new LoadHierarchyDemo(session.Version),
    "mixed" => new LoadBridgeDemo(session.Version),
    "bjt" => new LoadBjtDemo(session.Version),
    "fd-soi" => new LoadSoiDemo(session.Version),
    "bsim3" => new LoadBsim3Demo(session.Version),
    _ => new LoadModelDemo(session.Version)
});
if (fixture == "mos1-high")
{
    session.Execute(new SetPartValue(session.Version, "V1", 3));
    session.Execute(new SetPartValue(session.Version, "V3", 1));
}
foreach (var analysis in fixture switch
{
    "waveforms" or "exponential" or "sine" => [BridgeAnalysis.Transient],
    "fd-soi" or "mos1-high" => [BridgeAnalysis.Dc],
    _ => Enum.GetValues<BridgeAnalysis>()
})
{
    var options = fixture switch
    {
        "hierarchy" => new BridgeOptions(analysis, "P1", analysis == BridgeAnalysis.Ac ? [100, 1000] : null,
            Phasors: analysis == BridgeAnalysis.Ac ? [new("V1", 1, 0)] : null,
            Pulses: analysis == BridgeAnalysis.Transient ? [new("V1", 0, 5, 0, .0001, .0001, .01, .02)] : null),
        "mixed" => BridgeDemo.Options(analysis),
        "waveforms" or "exponential" or "sine" => BridgeDemo.WaveformOptions(fixture),
        "bjt" => BridgeDemo.BjtOptions(analysis),
        "fd-soi" => BridgeDemo.SoiOptions(),
        "bsim3" => BridgeDemo.Bsim3Options(analysis),
        _ => BridgeDemo.ModelOptions(analysis, fixture.StartsWith("diode", StringComparison.Ordinal) ? "P1" : "P2")
    };
    if (options.Mos1 is not null && fixture is not ("mos1-charge" or "mos1-high"))
        options = options with { Mos1 = [options.Mos1![0] with
        {
            GateSourceFarads = 0, GateDrainFarads = 0, GateBulkFarads = 0, DrainBodyFarads = 0, SourceBodyFarads = 0
        }] };
    if (fixture is "diode" or "mos1" or "mos1-charge" or "mos1-high")
        options = options with { Diodes = [options.Diodes![0] with { JunctionFarads = 0, TransitSeconds = 0 }] };
    if (fixture == "mos1-high") options = options with { UseSourceContinuation = true };
    var result = await session.ExecuteBridgeAsync(new RunBridge(session.Version, options),
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
    var tolerance = analysis == BridgeAnalysis.Transient ? 5e-4 : fixture == "mixed" ? 1e-9 : 1e-5;
    Console.WriteLine(FormattableString.Invariant(
        $"{fixture} {analysis}: native {run.Samples.Count}, ngspice {reference.Length}, max component error {maximum:G9} V, tolerance {tolerance:G9} V."));
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
