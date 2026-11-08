using System.Text.Json;
using SchematicLab.Core;

namespace SchematicLab.Solver;

public sealed partial class NativeRcSolver
{
    /// <summary>Run a bounded general circuit through the same versioned native transport as the RC lesson.</summary>
    public async Task<IReadOnlyList<BridgeSample>> SolveCircuitAsync(CircuitJob job, CancellationToken cancellation)
    {
        if (job is not { Nodes.Length: >= 1 and <= 128, Elements.Length: >= 1 and <= 64,
            ProbeNode.Length: >= 1 and <= 12, Options: not null } ||
            !job.Nodes.Contains(job.ProbeNode) || !Enum.IsDefined(job.Options.Analysis) ||
            job.Elements.Any(element => element is null || !double.IsFinite(element.Value)) ||
            job.Options.Analysis == BridgeAnalysis.Ac && job.Options.FrequenciesHz is not { Length: >= 1 and <= 128 })
            throw new RcSolverException("Invalid circuit job.");

        var nodes = job.Nodes.ToList();
        var elements = job.Elements.SelectMany(element => Elements(element, job.Options, nodes)).ToArray();
        if (nodes.Count > 128) throw new RcSolverException("The lowered circuit exceeds the native node budget.");
        var request = JsonSerializer.Serialize(new
        {
            schema_version = "1", case_id = "schematic-circuit", backend = "lu",
            circuit = new { nodes, elements },
            analysis = Analysis(job)
        });
        return await ExchangeAsync(request, json => ParseCircuit(json, job), 32 * 1024 * 1024, cancellation);
    }

    private static IEnumerable<Dictionary<string, object>> Elements(CircuitElement element, BridgeOptions options, List<string> nodes)
    {
        if (element.Kind is Kind.Npn or Kind.Pnp)
        {
            foreach (var primitive in BjtElements(element, options, nodes)) yield return primitive;
            yield break;
        }
        yield return Element(element, options);
        if (element.Kind is not (Kind.Nmos or Kind.Pmos)) yield break;
        var model = FindModel(options.Mos1, element.Id, binding => binding.Id)!;
        var overlap = new (string Suffix, int Other, double Farads)[]
        {
            ("_qgs", element.N, model.GateSourceFarads), ("_qgd", element.P, model.GateDrainFarads),
            ("_qgb", element.Bulk, model.GateBulkFarads)
        };
        foreach (var (suffix, other, farads) in overlap)
            if (farads > 0 && element.Gate != other)
                yield return new() { ["kind"] = "capacitor", ["id"] = element.Id + suffix,
                    ["p"] = element.Gate, ["n"] = other, ["farads"] = farads };

        var body = new (string Suffix, int Other, double Farads)[]
        {
            ("_qbd", element.P, model.DrainBodyFarads), ("_qbs", element.N, model.SourceBodyFarads)
        };
        foreach (var (suffix, other, farads) in body)
        {
            if (farads == 0 || element.Bulk == other) continue;
            var (p, n) = element.Kind == Kind.Pmos ? (other, element.Bulk) : (element.Bulk, other);
            yield return new() { ["kind"] = "junction_storage", ["id"] = element.Id + suffix,
                ["p"] = p, ["n"] = n, ["model"] = new
                {
                    saturation_amps = 1e-14, thermal_volts = 8.617333262145e-5 * 300.15,
                    junction_farads = farads, junction_volts = model.BodyVolts, grading = model.BodyGrading,
                    forward_coefficient = model.ForwardCoefficient, transit_seconds = 0
                } };
        }
    }

    // The same Ebers–Moll primitive composition as the native deck frontend.
    // The private sense branches carry conduction current, leaving room for separate storage.
    private static IEnumerable<Dictionary<string, object>> BjtElements(CircuitElement element,
        BridgeOptions options, List<string> nodes)
    {
        var model = FindModel(options.Bjts, element.Id, b => b.Id)
            ?? throw new RcSolverException("BJT requires a model binding.");
        var pnp = element.Kind == Kind.Pnp;
        var be = nodes.Count;
        nodes.Add(element.Id + "_be");
        var bc = nodes.Count;
        nodes.Add(element.Id + "_bc");
        var beSense = element.Id + "_be_sense";
        var bcSense = element.Id + "_bc_sense";
        yield return Primitive("voltage", beSense, pnp ? element.N : element.Base, be, "volts", 0d);
        yield return Primitive("voltage", bcSense, pnp ? element.P : element.Base, bc, "volts", 0d);
        var forward = Primitive("diode", element.Id + "_be", be, pnp ? element.Base : element.N,
            "saturation_amps", model.SaturationAmps / model.ForwardBeta);
        forward["thermal_volts"] = model.ForwardThermalVolts;
        yield return forward;
        var reverse = Primitive("diode", element.Id + "_bc", bc, pnp ? element.Base : element.P,
            "saturation_amps", model.SaturationAmps / model.ReverseBeta);
        reverse["thermal_volts"] = model.ReverseThermalVolts;
        yield return reverse;
        var collector = Primitive("cccs", element.Id + "_f", pnp ? element.N : element.P,
            pnp ? element.P : element.N, "gain", model.ForwardBeta);
        collector["control_id"] = beSense;
        yield return collector;
        var emitter = Primitive("cccs", element.Id + "_r", pnp ? element.P : element.N,
            pnp ? element.N : element.P, "gain", model.ReverseBeta);
        emitter["control_id"] = bcSense;
        yield return emitter;
    }

    private static Dictionary<string, object> Primitive(string kind, string id, int p, int n, string parameter, double value) =>
        new() { ["kind"] = kind, ["id"] = id, ["p"] = p, ["n"] = n, [parameter] = value };

    private static Dictionary<string, object> Element(CircuitElement element, BridgeOptions options)
    {
        if (element.Kind is Kind.Nmos or Kind.Pmos)
        {
            var binding = FindModel(options.Mos1, element.Id, model => model.Id)
                ?? throw new RcSolverException("MOS level 1 requires a model binding.");
            return new()
            {
                ["kind"] = "mos1_channel", ["id"] = element.Id, ["p"] = element.P, ["n"] = element.N,
                ["gate"] = element.Gate, ["bulk"] = element.Bulk,
                ["model"] = new
                {
                    p_channel = element.Kind == Kind.Pmos, threshold_volts = binding.ThresholdVolts,
                    beta_amps_per_volt_squared = binding.Beta, body_effect_sqrt_volts = binding.Gamma,
                    surface_potential_volts = binding.Phi, channel_length_modulation_per_volt = binding.Lambda
                }
            };
        }
        var (kind, parameter) = element.Kind switch
        {
            Kind.Resistor => ("resistor", "ohms"),
            Kind.Capacitor => ("capacitor", "farads"),
            Kind.Inductor => ("inductor", "henries"),
            Kind.Supply or Kind.PulseVoltage => ("voltage", "volts"),
            Kind.Current => ("current", "amps"),
            Kind.Vcvs => ("vcvs", "gain"),
            Kind.Vccs => ("vccs", "siemens"),
            Kind.Ccvs => ("ccvs", "ohms"),
            Kind.Cccs => ("cccs", "gain"),
            Kind.Diode => ("diode", "saturation_amps"),
            _ => throw new RcSolverException("The circuit contains an unsupported element.")
        };
        var result = new Dictionary<string, object>
        {
            ["kind"] = kind, ["id"] = element.Id, ["p"] = element.P, ["n"] = element.N,
            [parameter] = element.Kind switch { Kind.Diode => 1e-14, Kind.PulseVoltage => 0, _ => element.Value }
        };
        if (element.Kind is Kind.Vccs or Kind.Vcvs)
        {
            result["cp"] = element.Cp;
            result["cn"] = element.Cn;
        }
        if (element.Kind is Kind.Cccs or Kind.Ccvs)
            result["control_id"] = element.ControlId ?? throw new RcSolverException("A current control is missing.");
        if (element.Kind == Kind.Diode)
        {
            var binding = FindModel(options.Diodes, element.Id, model => model.Id);
            if (binding is { } d && (d.JunctionFarads != 0 || d.TransitSeconds != 0))
            {
                result.Remove("saturation_amps");
                result["kind"] = "spice_diode";
                result["model"] = new
                {
                    saturation_amps = d.SaturationAmps, thermal_volts = d.ThermalVolts,
                    junction_farads = d.JunctionFarads, junction_volts = d.JunctionVolts,
                    grading = d.Grading, forward_coefficient = d.ForwardCoefficient, transit_seconds = d.TransitSeconds
                };
            }
            else
            {
                result["saturation_amps"] = binding?.SaturationAmps ?? 1e-14;
                result["thermal_volts"] = binding?.ThermalVolts ?? 8.617333262145e-5 * 300.15;
            }
        }
        return result;
    }

    private static T? FindModel<T>(T[]? models, string id, Func<T, string> identify) where T : class =>
        models?.FirstOrDefault(model => model is not null && identify(model) == id);

    private static object Analysis(CircuitJob job)
    {
        var options = job.Options;
        if (options.Analysis == BridgeAnalysis.Dc) return new { kind = "operating_point" };
        if (options.Analysis == BridgeAnalysis.Ac)
        {
            var phasors = options.Phasors ?? [];
            if (phasors.Any(p => p is null)) throw new RcSolverException("Invalid source phasor.");
            return new
            {
                kind = "ac", frequencies_hz = options.FrequenciesHz,
                source_phasors = job.Elements.Where(e => e.Kind is Kind.Supply or Kind.PulseVoltage or Kind.Current)
                    .Select(e =>
                    {
                        var p = Array.Find(phasors, p => p.Id == e.Id);
                        var angle = Math.IEEERemainder(p?.PhaseDegrees ?? 0, 360) * Math.PI / 180;
                        var magnitude = p?.Magnitude ?? 0;
                        return new { id = e.Id, real = magnitude * Math.Cos(angle), imag = magnitude * Math.Sin(angle) };
                    }).ToArray()
            };
        }

        var pulses = options.Pulses ?? [];
        if (pulses.Any(p => p is null)) throw new RcSolverException("Invalid source pulse.");
        var defaults = job.Elements.Where(e => e.Kind == Kind.PulseVoltage && !pulses.Any(p => p.Id == e.Id))
            .Select(e => new SourcePulse(e.Id, 0, e.Value, 0, options.StepSeconds, options.StepSeconds,
                options.StopSeconds, 4 * options.StopSeconds));
        return new
        {
            kind = "transient_configured",
            options = new
            {
                stop_s = options.StopSeconds, max_step_s = options.StepSeconds, max_steps = 8192,
                // The native nonlinear profile is restricted to backward Euler.
                method = job.Elements.Any(e => e.Kind is Kind.Diode or Kind.Nmos or Kind.Pmos or Kind.Npn or Kind.Pnp)
                    ? "backward_euler" : "trapezoidal",
                sources = pulses.Concat(defaults).Select(p => new { id = p.Id, waveform = new
                {
                    kind = "pulse", low = p.Low, high = p.High, delay_s = p.Delay,
                    rise_s = p.Rise, fall_s = p.Fall, width_s = p.Width, period_s = p.Period
                } }).ToArray()
            }
        };
    }

    private static IReadOnlyList<BridgeSample> ParseCircuit(string json, CircuitJob job)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("schema_version").GetString() != "1" ||
            root.GetProperty("case_id").GetString() != "schematic-circuit" ||
            root.GetProperty("status").GetString() != "succeeded" || !root.GetProperty("complete").GetBoolean())
            throw new RcSolverException("The native solver returned an incomplete run.");

        var signal = $"v({job.ProbeNode})";
        var names = root.GetProperty("signals").EnumerateArray().ToArray();
        var index = Array.FindIndex(names, name => name.GetProperty("id").GetString() == signal &&
            name.GetProperty("unit").GetString() == "V");
        if (index < 0 && job.ProbeNode != "0") throw new RcSolverException("The probe voltage is missing.");
        if (job.Options.Analysis == BridgeAnalysis.Ac)
            return root.GetProperty("ac_samples").EnumerateArray().Select(sample =>
            {
                var frequency = sample.GetProperty("frequency_hz").GetDouble();
                if (index < 0) return new BridgeSample(frequency, 0);
                var value = sample.GetProperty("values")[index];
                return new BridgeSample(frequency, value.GetProperty("real").GetDouble(), value.GetProperty("imag").GetDouble());
            }).ToArray();
        return root.GetProperty("samples").EnumerateArray().Select(sample =>
            new BridgeSample(sample.GetProperty("time_s").GetDouble(),
                index < 0 ? 0 : sample.GetProperty("values")[index].GetDouble())).ToArray();
    }
}
