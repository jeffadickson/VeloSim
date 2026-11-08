namespace SchematicLab.Core;

public enum BridgeAnalysis { Dc, Ac, Transient }

/// <summary>AC excitation in polar units. Omitted independent sources have zero AC amplitude.</summary>
public sealed record SourcePhasor(string Id, double Magnitude, double PhaseDegrees);
/// <summary>Independent-source pulse levels in volts or amperes and times in seconds.</summary>
public readonly record struct WavePoint(double TimeSeconds, double Value);
public sealed record SourcePwl(string Id, WavePoint[] Points);
public sealed record SourceSine(string Id, double Offset, double Amplitude, double FrequencyHz,
    double DelaySeconds = 0, double DampingPerSecond = 0, double PhaseDegrees = 0);
public sealed record SourceExponential(string Id, double Initial, double Pulsed, double RiseDelaySeconds,
    double RiseTauSeconds, double FallDelaySeconds, double FallTauSeconds);
public sealed record SourcePulse(string Id, double Low, double High, double Delay, double Rise,
    double Fall, double Width, double Period);
/// <summary>Effective diode conduction and optional depletion/diffusion parameters in SI units.</summary>
public sealed record DiodeBinding(string Id, double SaturationAmps, double ThermalVolts,
    double JunctionFarads = 0, double JunctionVolts = 1, double Grading = .5,
    double ForwardCoefficient = .5, double TransitSeconds = 0);
/// <summary>Effective MOS level 1 channel parameters in SI units. Beta includes W/L.</summary>
public sealed record Mos1Binding(string Id, double ThresholdVolts, double Beta, double Gamma, double Phi, double Lambda,
    double GateSourceFarads = 0, double GateDrainFarads = 0, double GateBulkFarads = 0,
    double DrainBodyFarads = 0, double SourceBodyFarads = 0, double BodyVolts = .8,
    double BodyGrading = .5, double ForwardCoefficient = .5);
/// <summary>Effective junction storage in farads, volts and seconds.</summary>
public sealed record BjtJunction(double Farads, double Volts, double Grading, double ForwardCoefficient, double TransitSeconds);
/// <summary>Effective parameters for the bounded three-terminal BJT profile.</summary>
public sealed record BjtBinding(string Id, double SaturationAmps, double ForwardBeta, double ReverseBeta,
    double ForwardThermalVolts, double ReverseThermalVolts, BjtJunction? BaseEmitter = null,
    BjtJunction? BaseCollector = null);
/// <summary>Bulk BSIM3 3.3.0 effective geometry in SI, with NCH and NSUB in cm⁻³.</summary>
public sealed record Bsim3Binding(string Id, double Tox, double Nch, double Nsub, double ThresholdVolts,
    double Width, double Length, double DrainPerimeter = 0, double SourcePerimeter = 0,
    double DrainArea = 0, double SourceArea = 0);
public sealed record CurrentControl(string Id, string VoltageSourceId);
public sealed record BridgeOptions(BridgeAnalysis Analysis, string ProbeId, double[]? FrequenciesHz = null,
    double StopSeconds = .005, double StepSeconds = .00001, SourcePhasor[]? Phasors = null,
    SourcePulse[]? Pulses = null, CurrentControl[]? Controls = null, DiodeBinding[]? Diodes = null, Mos1Binding[]? Mos1 = null,
    BjtBinding[]? Bjts = null, Bsim3Binding[]? Bsim3 = null,
    SourcePwl[]? Pwl = null, SourceSine[]? Sines = null, SourceExponential[]? Exponentials = null, string[]? FdSoi = null, bool UseSourceContinuation = false);

public sealed record RunBridge(DocumentVersion Expected, BridgeOptions Options) : DocumentCommand(Expected);
public sealed record LoadBridgeDemo(DocumentVersion Expected) : DocumentCommand(Expected);
public sealed record LoadSoiDemo(DocumentVersion Expected) : DocumentCommand(Expected);
public sealed record LoadBsim3Demo(DocumentVersion Expected) : DocumentCommand(Expected);
public sealed record LoadBjtDemo(DocumentVersion Expected) : DocumentCommand(Expected);
public sealed record LoadModelDemo(DocumentVersion Expected) : DocumentCommand(Expected);
public sealed record CircuitElement(Kind Kind, string Id, int P, int N, double Value, int Cp = 0, int Cn = 0,
    string? ControlId = null, int Gate = 0, int Bulk = 0, int Base = 0);
/// <summary>Bounded circuit transport with explicit ground and an observable node.</summary>
public sealed record CircuitJob(string[] Nodes, CircuitElement[] Elements, string ProbeNode, BridgeOptions Options);
public sealed record BridgeSample(double Axis, double Real, double Imaginary = 0);
public sealed record BridgeRun(DocumentVersion Version, BridgeAnalysis Analysis, string ProbeId,
    IReadOnlyList<BridgeSample> Samples);

public sealed partial class Board
{
    internal CircuitJob? CompileBridge(BridgeOptions options)
    {
        var soi = parts.Where(part => part.Kind == Kind.NmosSoi).ToArray();
        if (soi.Any(part => options.Analysis != BridgeAnalysis.Dc || options.FdSoi?.Contains(part.Id) != true ||
            wires.Any(wire => wire.A == new Pin(part.Id, 4) || wire.B == new Pin(part.Id, 4)) ||
            labels.Any(label => label.Terminal == new Pin(part.Id, 4)))) return null;

        var grounds = parts.Where(part => part.Kind == Kind.Ground).Select(part => new Pin(part.Id, 0)).ToArray();
        var probe = new Pin(options.ProbeId, 0);
        if (grounds.Length == 0 || !parts.Any(part => part.Id == probe.Part && part.Kind == Kind.Probe)) return null;
        var nodes = new List<string> { "0" };
        var assignments = new Dictionary<Pin, int>();
        foreach (var ground in grounds)
            foreach (var terminal in TraceNet(ground)!.Terminals)
                assignments[terminal] = 0;

        foreach (var part in parts)
        for (var index = 0; index < part.PinCount; index++)
        {
            if (part.Kind == Kind.NmosSoi && index == 4) continue; // Pinned four-terminal profile has no body-contact port.
            var pin = new Pin(part.Id, index);
            if (assignments.ContainsKey(pin)) continue;
            var net = TraceNet(pin)!;
            if (net.Terminals.Count == 1 || nodes.Count == 128) return null;
            foreach (var terminal in net.Terminals) assignments.Add(terminal, nodes.Count);
            nodes.Add($"n{nodes.Count}");
        }

        var elements = new List<CircuitElement>();
        foreach (var part in parts)
        {
            if (part.Kind is Kind.Ground or Kind.Probe) continue;
            if (part.Kind is not (Kind.Resistor or Kind.Capacitor or Kind.Inductor or Kind.Supply or
                Kind.Current or Kind.PulseVoltage or Kind.Vcvs or Kind.Vccs or Kind.Ccvs or Kind.Cccs or Kind.Diode or Kind.Nmos or Kind.Pmos or Kind.Npn or Kind.Pnp or Kind.NmosSoi))
                return null;
            var bulkMos = part.Kind is Kind.Nmos or Kind.Pmos;
            var mos = bulkMos || part.Kind == Kind.NmosSoi;
            var bipolar = part.Kind is Kind.Npn or Kind.Pnp;
            if (bipolar && options.Bjts?.Any(binding => binding.Id == part.Id) != true) return null;
            if (bulkMos && options.Mos1?.Any(binding => binding.Id == part.Id) != true &&
                options.Bsim3?.Any(binding => binding.Id == part.Id) != true) return null;
            var p = assignments[new(part.Id, 0)];
            var n = assignments[new(part.Id, mos || bipolar ? 2 : 1)];
            if (p == n || !ValidPartValue(part.Kind, part.Value)) return null;
            var control = options.Controls?.SingleOrDefault(binding => binding.Id == part.Id)?.VoltageSourceId;
            if (part.Kind is Kind.Ccvs or Kind.Cccs && control is null) return null;
            var cp = !mos && part.PinCount == 4 ? assignments[new(part.Id, 2)] : 0;
            var cn = !mos && part.PinCount == 4 ? assignments[new(part.Id, 3)] : 0;
            var gate = mos ? assignments[new(part.Id, 1)] : 0;
            var bulk = mos ? assignments[new(part.Id, 3)] : 0;
            var baseNode = bipolar ? assignments[new(part.Id, 1)] : 0;
            elements.Add(new(part.Kind, part.Id, p, n, part.Value, cp, cn, control, gate, bulk, baseNode));
        }
        if (nodes.Count + 2 * elements.Count(element => element.Kind is Kind.Npn or Kind.Pnp) > 128) return null;
        return new(nodes.ToArray(), elements.ToArray(), nodes[assignments[probe]], options);
    }
}

public sealed partial class LessonSession
{
    /// <summary>Capture topology and analysis settings before releasing the session lock for execution.</summary>
    public async Task<CommandResult> ExecuteBridgeAsync(RunBridge command,
        Func<CircuitJob, CancellationToken, Task<IReadOnlyList<BridgeSample>>> solve, CancellationToken cancellation = default)
    {
        CircuitJob job;
        lock (gate)
        {
            if (cancellation.IsCancellationRequested) return Result(CommandCode.Cancelled, "Command cancelled.");
            if (command.Expected.DocumentId != documentId) return Result(CommandCode.WrongDocument, "The command targets a different document.");
            if (command.Expected.Revision != revision) return Result(CommandCode.StaleRevision, "The document changed.");
            if (command.Options is not { } options)
                return Result(CommandCode.InvalidInput, "Supply analysis options.");
            options = options with { FrequenciesHz = options.FrequenciesHz?.ToArray(), Phasors = options.Phasors?.ToArray(),
                Pulses = options.Pulses?.ToArray(), Controls = options.Controls?.ToArray(),
                Diodes = options.Diodes?.ToArray(), Mos1 = options.Mos1?.ToArray(), Bjts = options.Bjts?.ToArray(), Bsim3 = options.Bsim3?.ToArray(),
                Pwl = options.Pwl?.Select(wave => wave is null ? null! : wave with { Points = wave.Points?.ToArray()! }).ToArray(),
                Sines = options.Sines?.ToArray(), Exponentials = options.Exponentials?.ToArray(), FdSoi = options.FdSoi?.ToArray() };
            if (!ValidBridgeOptions(options))
                return Result(CommandCode.InvalidInput, "Check the probe, analysis settings, and source bindings.");
            var compiled = board.CompileBridge(options);
            if (compiled is null) return Result(CommandCode.UnsupportedCircuit,
                "Use supported parts and connected terminals. Check the model binding, terminal connections and supported analyses.");
            job = compiled;
        }

        try
        {
            var samples = (await solve(job, cancellation)).ToArray();
            if (cancellation.IsCancellationRequested) return new(CommandCode.Cancelled, command.Expected, "Command cancelled.");
            if (!ValidBridgeSamples(job.Options, samples))
                return new(CommandCode.SolverFailed, command.Expected, "The solver returned an incomplete or invalid trace.");
            return new(CommandCode.Ok, command.Expected, "Circuit analysis completed.",
                Bridge: new(command.Expected, job.Options.Analysis, job.Options.ProbeId, Array.AsReadOnly(samples)));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return new(CommandCode.Cancelled, command.Expected, "Command cancelled.");
        }
        catch (RcSolverException error)
        {
            return new(CommandCode.SolverFailed, command.Expected, error.Message);
        }
    }

    private bool ValidBridgeOptions(BridgeOptions options)
    {
        if (options.ProbeId is not { Length: > 0 and <= 12 } || !Enum.IsDefined(options.Analysis) ||
            options.UseSourceContinuation && options.Analysis != BridgeAnalysis.Dc) return false;
        var frequencies = options.FrequenciesHz ?? [];
        if (options.Analysis == BridgeAnalysis.Ac && (frequencies.Length is < 1 or > 128 ||
            frequencies.Any(f => f is <= 0 or > 1e12 || !double.IsFinite(f)) ||
            frequencies.Zip(frequencies.Skip(1)).Any(pair => pair.First >= pair.Second))) return false;
        if (options.Analysis == BridgeAnalysis.Transient &&
            (!Positive(options.StopSeconds) || !Positive(options.StepSeconds) ||
             options.StopSeconds / options.StepSeconds > 4096 || options.StepSeconds > options.StopSeconds)) return false;

        var phasors = options.Phasors ?? [];
        var pulses = options.Pulses ?? [];
        var controls = options.Controls ?? [];
        var diodes = options.Diodes ?? [];
        var mos = options.Mos1 ?? [];
        var bjts = options.Bjts ?? [];
        var bulk = options.Bsim3 ?? [];
        var fd = options.FdSoi ?? [];
        return fd.Length <= 64 && fd.All(id => id is not null && FindPart(id)?.Kind == Kind.NmosSoi) &&
            fd.Distinct().Count() == fd.Length &&
            bulk.Length <= 64 && bulk.All(b => b is not null && FindPart(b.Id)?.Kind is Kind.Nmos or Kind.Pmos &&
            Positive(b.Tox) && Positive(b.Nch) && Positive(b.Nsub) && double.IsFinite(b.ThresholdVolts) &&
            Positive(b.Width) && Positive(b.Length) && Nonnegative(b.DrainPerimeter) && Nonnegative(b.SourcePerimeter) &&
            Nonnegative(b.DrainArea) && Nonnegative(b.SourceArea)) &&
            bulk.Select(b => b.Id).Distinct().Count() == bulk.Length &&
            bjts.Length <= 64 && bjts.All(b => b is not null && FindPart(b.Id)?.Kind is Kind.Npn or Kind.Pnp &&
            Positive(b.SaturationAmps) && Positive(b.ForwardBeta) && Positive(b.ReverseBeta) &&
            Positive(b.ForwardThermalVolts) && Positive(b.ReverseThermalVolts) &&
            Positive(b.SaturationAmps / b.ForwardBeta) && Positive(b.SaturationAmps / b.ReverseBeta) &&
            ValidJunction(b.BaseEmitter) && ValidJunction(b.BaseCollector)) &&
            bjts.Select(b => b.Id).Distinct().Count() == bjts.Length &&
            diodes.Length <= 64 && mos.Length <= 64 &&
            diodes.All(d => d is not null && FindPart(d.Id)?.Kind == Kind.Diode &&
                Positive(d.SaturationAmps) && Positive(d.ThermalVolts) && Nonnegative(d.JunctionFarads) &&
                Positive(d.JunctionVolts) && d.Grading is >= 0 and < 1 &&
                d.ForwardCoefficient is >= 0 and < 1 && Nonnegative(d.TransitSeconds)) &&
            mos.All(m => m is not null && FindPart(m.Id)?.Kind is Kind.Nmos or Kind.Pmos &&
                double.IsFinite(m.ThresholdVolts) && Positive(m.Beta) && Positive(m.Phi) &&
                Nonnegative(m.Gamma) && Nonnegative(m.Lambda) && Nonnegative(m.GateSourceFarads) &&
                Nonnegative(m.GateDrainFarads) && Nonnegative(m.GateBulkFarads) && Nonnegative(m.DrainBodyFarads) &&
                Nonnegative(m.SourceBodyFarads) && Positive(m.BodyVolts) && m.BodyGrading is >= 0 and < 1 &&
                m.ForwardCoefficient is >= 0 and < 1) &&
            diodes.Select(d => d.Id).Distinct().Count() == diodes.Length &&
            mos.Select(m => m.Id).Concat(bulk.Select(b => b.Id)).Distinct().Count() == mos.Length + bulk.Length &&
            phasors.Length <= 64 && pulses.Length <= 64 && controls.Length <= 64 &&
            phasors.All(p => p is not null && Independent(p.Id) && double.IsFinite(p.Magnitude) &&
                p.Magnitude >= 0 && double.IsFinite(p.PhaseDegrees)) &&
            pulses.All(p => p is not null && Independent(p.Id) && double.IsFinite(p.Low) && double.IsFinite(p.High) &&
                double.IsFinite(p.Delay) && p.Delay >= 0 && Positive(p.Rise) && Positive(p.Fall) &&
                Positive(p.Width) && Positive(p.Period) && p.Rise + p.Width + p.Fall <= p.Period) &&
            controls.All(c => c is not null && FindPart(c.Id)?.Kind is Kind.Ccvs or Kind.Cccs &&
                FindPart(c.VoltageSourceId)?.Kind is Kind.Supply or Kind.PulseVoltage) &&
            phasors.Select(p => p.Id).Distinct().Count() == phasors.Length &&
            ValidWaveforms(options, pulses) &&
            controls.Select(c => c.Id).Distinct().Count() == controls.Length;
    }

    private static bool ValidJunction(BjtJunction? junction) => junction is null ||
        Nonnegative(junction.Farads) && Positive(junction.Volts) && junction.Grading is >= 0 and < 1 &&
        junction.ForwardCoefficient is >= 0 and < 1 && Nonnegative(junction.TransitSeconds);

    private bool ValidWaveforms(BridgeOptions options, SourcePulse[] pulses)
    {
        var pwl = options.Pwl ?? [];
        var sines = options.Sines ?? [];
        var exp = options.Exponentials ?? [];
        if (pwl.Length + sines.Length + exp.Length + pulses.Length > 64) return false;
        return pwl.All(w => w is { Points.Length: >= 1 and <= 256 } && Independent(w.Id) &&
                w.Points.All(p => Nonnegative(p.TimeSeconds) && double.IsFinite(p.Value)) &&
                w.Points.Zip(w.Points.Skip(1)).All(pair => pair.First.TimeSeconds < pair.Second.TimeSeconds)) &&
            sines.All(w => w is not null && Independent(w.Id) && double.IsFinite(w.Offset) &&
                double.IsFinite(w.Amplitude) && Positive(w.FrequencyHz) && Nonnegative(w.DelaySeconds) &&
                Nonnegative(w.DampingPerSecond) && double.IsFinite(w.PhaseDegrees)) &&
            exp.All(w => w is not null && Independent(w.Id) && double.IsFinite(w.Initial) &&
                double.IsFinite(w.Pulsed) && Nonnegative(w.RiseDelaySeconds) && Positive(w.RiseTauSeconds) &&
                Nonnegative(w.FallDelaySeconds) && w.FallDelaySeconds >= w.RiseDelaySeconds && Positive(w.FallTauSeconds)) &&
            pulses.Select(w => w.Id).Concat(pwl.Select(w => w.Id)).Concat(sines.Select(w => w.Id))
                .Concat(exp.Select(w => w.Id)).Distinct().Count() == pulses.Length + pwl.Length + sines.Length + exp.Length;
    }

    private bool Independent(string id) => FindPart(id)?.Kind is Kind.Supply or Kind.PulseVoltage or Kind.Current;
    private static bool Nonnegative(double value) => value >= 0 && double.IsFinite(value);
    private static bool Positive(double value) => value > 0 && double.IsFinite(value);

    private static bool ValidBridgeSamples(BridgeOptions options, BridgeSample[] samples)
    {
        if (samples.Length is < 1 or > 8193 || samples.Any(sample => sample is null || !double.IsFinite(sample.Axis) ||
            !double.IsFinite(sample.Real) || !double.IsFinite(sample.Imaginary))) return false;
        return options.Analysis switch
        {
            BridgeAnalysis.Dc => samples is [{ Axis: 0, Imaginary: 0 }],
            BridgeAnalysis.Ac => samples.Select(sample => sample.Axis).SequenceEqual(options.FrequenciesHz!),
            _ => samples.Length >= 2 && samples[0].Axis == 0 &&
                Math.Abs(samples[^1].Axis - options.StopSeconds) <= options.StopSeconds * 1e-9 &&
                samples.All(sample => sample.Imaginary == 0) &&
                samples.Zip(samples.Skip(1)).All(pair => pair.First.Axis < pair.Second.Axis)
        };
    }
}
