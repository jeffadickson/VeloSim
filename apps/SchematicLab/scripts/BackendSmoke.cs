#:property PublishAot=false
#:project ../Core/SchematicLab.Core.csproj
#:project ../Solver/SchematicLab.Solver.csproj
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using SchematicLab.Core;
using SchematicLab.Solver;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || !File.Exists(args[0]))
    throw new ArgumentException("Supply an absolute trusted native solver path.");
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var token = deadline.Token;
var native = new NativeRcSolver(args[0]);
var evidence = new List<object>();
foreach (var backend in Enum.GetValues<SolverBackend>())
{
    var session = new LessonSession(rcExample: true);
    Require(session.Execute(new SetSolverBackend(session.Version, backend)).Code == CommandCode.Ok, "Selection refused.");
    var rc = await session.ExecuteTransientAsync(new(session.Version, "P1"), native.SolveAsync, token);
    Require(rc is { Code: CommandCode.Ok, Transient: not null }, rc.Message);
    var waveform = rc.Transient!;
    var rcError = waveform.Samples.Max(point => Math.Abs(point.Volts - Charging(point.Seconds,
        waveform.Input.TimeConstant, waveform.Input.RiseSeconds, waveform.Input.Volts)));
    Require(rcError <= .02, "RC voltage error exceeds existing 0.02 V bound.");
    evidence.Add(new { backend, provider = backend.ProviderId(), analysis = "RC transient", samples = waveform.Samples.Count, max_absolute_volts = rcError });

    foreach (var analysis in Enum.GetValues<BridgeAnalysis>())
    {
        var options = new BridgeOptions(analysis, "P1", FrequenciesHz: [100, 1000],
            StopSeconds: .005, StepSeconds: .00001, Phasors: [new("V1", 1, 0)],
            Pulses: [new("V1", 0, 5, 0, .00001, .00001, .01, .02)]);
        var result = await session.ExecuteBridgeAsync(new(session.Version, options), native.SolveCircuitAsync, token);
        if (backend == SolverBackend.Qr && analysis == BridgeAnalysis.Ac)
        {
            Require(result.Code == CommandCode.UnsupportedCircuit && result.Bridge is null, "Unsupported QR AC did not refuse explicitly.");
            evidence.Add(new { backend, analysis = "Ac", status = "unsupported_no_fallback" });
            continue;
        }
        Require(result is { Code: CommandCode.Ok, Bridge: not null }, result.Message);
        Require(result.Bridge!.Backend == backend, "Effective provider was not retained.");
        var max = 0d;
        foreach (var point in result.Bridge.Samples)
        {
            if (analysis == BridgeAnalysis.Ac)
            {
                var reference = Complex.One / new Complex(1, 2 * Math.PI * point.Axis * .001);
                var error = Complex.Abs(new Complex(point.Real, point.Imaginary) - reference);
                Require(error <= 1e-6 + 1e-4 * reference.Magnitude, "AC disagrees with the analytical RC transfer function.");
                max = Math.Max(max, error);
            }
            else
            {
                // The PULSE source's DC bias is its zero low level; the 5 V
                // high level applies only after its transient ramp.
                var reference = analysis == BridgeAnalysis.Dc ? 0 : Charging(point.Axis, .001, .00001, 5);
                var error = Math.Abs(point.Real - reference);
                Require(error <= (analysis == BridgeAnalysis.Dc ? 1e-6 : .02), $"{backend}/{analysis} at {point.Axis}: actual={point.Real:R}, expected={reference:R}, error={error:R}.");
                max = Math.Max(max, error);
            }
        }
        evidence.Add(new { backend, provider = backend.ProviderId(), analysis = analysis.ToString(), samples = result.Bridge.Samples.Count, max_absolute_volts = max });
    }
}
Console.WriteLine(JsonSerializer.Serialize(new { native_sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(args[0]))), runs = evidence }, new JsonSerializerOptions { WriteIndented = true }));

// Independent closed-form response to the solver fixture's finite linear ramp.
static double Charging(double seconds, double tau, double rise, double volts) => seconds <= rise
    ? volts / rise * (seconds - tau * (1 - Math.Exp(-seconds / tau)))
    : volts * (1 - tau / rise * (1 - Math.Exp(-rise / tau)) * Math.Exp(-(seconds - rise) / tau));

static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
