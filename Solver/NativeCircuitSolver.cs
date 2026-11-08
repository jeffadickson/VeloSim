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

        var request = JsonSerializer.Serialize(new
        {
            schema_version = "1", case_id = "schematic-circuit", backend = "lu",
            circuit = new { nodes = job.Nodes, elements = job.Elements.Select(element => Element(element, job.Options)).ToArray() },
            analysis = Analysis(job)
        });
        return await ExchangeAsync(request, json => ParseCircuit(json, job), 32 * 1024 * 1024, cancellation);
    }

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
                method = job.Elements.Any(e => e.Kind is Kind.Diode or Kind.Nmos or Kind.Pmos)
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
