namespace SchematicLab.Core;

public enum BridgeAnalysis { Dc, Ac, Transient }

/// <summary>AC excitation in polar units. Omitted independent sources have zero AC amplitude.</summary>
public sealed record SourcePhasor(string Id, double Magnitude, double PhaseDegrees);
/// <summary>Independent-source pulse levels in volts or amperes and times in seconds.</summary>
public sealed record SourcePulse(string Id, double Low, double High, double Delay, double Rise,
    double Fall, double Width, double Period);
/// <summary>Effective conduction-only diode parameters at the evaluation temperature.</summary>
public sealed record DiodeBinding(string Id, double SaturationAmps, double ThermalVolts);
/// <summary>Effective MOS level 1 channel parameters in SI units. Beta includes W/L.</summary>
public sealed record Mos1Binding(string Id, double ThresholdVolts, double Beta, double Gamma, double Phi, double Lambda);
public sealed record CurrentControl(string Id, string VoltageSourceId);
public sealed record BridgeOptions(BridgeAnalysis Analysis, string ProbeId, double[]? FrequenciesHz = null,
    double StopSeconds = .005, double StepSeconds = .00001, SourcePhasor[]? Phasors = null,
    SourcePulse[]? Pulses = null, CurrentControl[]? Controls = null, DiodeBinding[]? Diodes = null, Mos1Binding[]? Mos1 = null);

public sealed record RunBridge(DocumentVersion Expected, BridgeOptions Options) : DocumentCommand(Expected);
public sealed record LoadBridgeDemo(DocumentVersion Expected) : DocumentCommand(Expected);
public sealed record LoadModelDemo(DocumentVersion Expected) : DocumentCommand(Expected);
public sealed record CircuitElement(Kind Kind, string Id, int P, int N, double Value, int Cp = 0, int Cn = 0,
    string? ControlId = null, int Gate = 0, int Bulk = 0);
/// <summary>Bounded circuit transport with explicit ground and an observable node.</summary>
public sealed record CircuitJob(string[] Nodes, CircuitElement[] Elements, string ProbeNode, BridgeOptions Options);
public sealed record BridgeSample(double Axis, double Real, double Imaginary = 0);
public sealed record BridgeRun(DocumentVersion Version, BridgeAnalysis Analysis, string ProbeId,
    IReadOnlyList<BridgeSample> Samples);

public sealed partial class Board
{
    internal CircuitJob? CompileBridge(BridgeOptions options)
    {
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
                Kind.Current or Kind.PulseVoltage or Kind.Vcvs or Kind.Vccs or Kind.Ccvs or Kind.Cccs or Kind.Diode or Kind.Nmos or Kind.Pmos))
                return null;
            var mos = part.Kind is Kind.Nmos or Kind.Pmos;
            if (mos && options.Mos1?.Any(binding => binding.Id == part.Id) != true) return null;
            var p = assignments[new(part.Id, 0)];
            var n = assignments[new(part.Id, mos ? 2 : 1)];
            if (p == n || !ValidPartValue(part.Kind, part.Value)) return null;
            var control = options.Controls?.SingleOrDefault(binding => binding.Id == part.Id)?.VoltageSourceId;
            if (part.Kind is Kind.Ccvs or Kind.Cccs && control is null) return null;
            var cp = !mos && part.PinCount == 4 ? assignments[new(part.Id, 2)] : 0;
            var cn = !mos && part.PinCount == 4 ? assignments[new(part.Id, 3)] : 0;
            var gate = mos ? assignments[new(part.Id, 1)] : 0;
            var bulk = mos ? assignments[new(part.Id, 3)] : 0;
            elements.Add(new(part.Kind, part.Id, p, n, part.Value, cp, cn, control, gate, bulk));
        }
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
                Diodes = options.Diodes?.ToArray(), Mos1 = options.Mos1?.ToArray() };
            if (!ValidBridgeOptions(options))
                return Result(CommandCode.InvalidInput, "Check the probe, analysis settings, and source bindings.");
            var compiled = board.CompileBridge(options);
            if (compiled is null) return Result(CommandCode.UnsupportedCircuit,
                "Use supported parts and connected terminals. Current controls and MOS level 1 devices require explicit bindings.");
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
        if (options.ProbeId is not { Length: > 0 and <= 12 } || !Enum.IsDefined(options.Analysis)) return false;
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
        return diodes.Length <= 64 && mos.Length <= 64 &&
            diodes.All(d => d is not null && FindPart(d.Id)?.Kind == Kind.Diode &&
                Positive(d.SaturationAmps) && Positive(d.ThermalVolts)) &&
            mos.All(m => m is not null && FindPart(m.Id)?.Kind is Kind.Nmos or Kind.Pmos &&
                double.IsFinite(m.ThresholdVolts) && Positive(m.Beta) && Positive(m.Phi) &&
                double.IsFinite(m.Gamma) && m.Gamma >= 0 && double.IsFinite(m.Lambda) && m.Lambda >= 0) &&
            diodes.Select(d => d.Id).Distinct().Count() == diodes.Length &&
            mos.Select(m => m.Id).Distinct().Count() == mos.Length &&
            phasors.Length <= 64 && pulses.Length <= 64 && controls.Length <= 64 &&
            phasors.All(p => p is not null && Independent(p.Id) && double.IsFinite(p.Magnitude) &&
                p.Magnitude >= 0 && double.IsFinite(p.PhaseDegrees)) &&
            pulses.All(p => p is not null && Independent(p.Id) && double.IsFinite(p.Low) && double.IsFinite(p.High) &&
                double.IsFinite(p.Delay) && p.Delay >= 0 && Positive(p.Rise) && Positive(p.Fall) &&
                Positive(p.Width) && Positive(p.Period) && p.Rise + p.Width + p.Fall <= p.Period) &&
            controls.All(c => c is not null && FindPart(c.Id)?.Kind is Kind.Ccvs or Kind.Cccs &&
                FindPart(c.VoltageSourceId)?.Kind is Kind.Supply or Kind.PulseVoltage) &&
            phasors.Select(p => p.Id).Distinct().Count() == phasors.Length &&
            pulses.Select(p => p.Id).Distinct().Count() == pulses.Length &&
            controls.Select(c => c.Id).Distinct().Count() == controls.Length;
    }

    private bool Independent(string id) => FindPart(id)?.Kind is Kind.Supply or Kind.PulseVoltage or Kind.Current;
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
