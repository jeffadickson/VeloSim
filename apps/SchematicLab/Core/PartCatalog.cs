namespace SchematicLab.Core;

/// <summary>Electrical pin name and artwork anchor in symbol-local SVG units.</summary>
public sealed record Terminal(string Name, int X, int Y);

/// <summary>Stable part definition with ordered terminals and explicit lesson support.</summary>
public sealed record PartDefinition(string Name, string Category, string Prefix, string Artwork,
    IReadOnlyList<Terminal> Terminals, bool DividerSupported = false, double DefaultValue = 0, string? Mark = null);

/// <summary>Built-in generic symbols. Vendor model binding is a separate operation.</summary>
public static partial class PartCatalog
{
    private static readonly IReadOnlyList<Terminal> Two = Pins(new("Positive", 0, -32), new("Negative", 0, 32));
    private static readonly IReadOnlyList<Terminal> Bipolar = Pins(new("Collector", 12, -32), new("Base", -32, 0), new("Emitter", 12, 32));
    private static readonly IReadOnlyList<Terminal> Mos = Pins(new("Drain", 12, -32), new("Gate", -32, 0), new("Source", 12, 32), new("Bulk", 33, 0));
    private static readonly IReadOnlyList<Terminal> Fet = Pins(new("Drain", 12, -32), new("Gate", -32, 0), new("Source", 12, 32));
    private static readonly IReadOnlyList<Terminal> Controlled = Pins(new("Output +", 0, -32), new("Output −", 0, 32), new("Control +", -32, -11), new("Control −", -32, 11));
    private const string Circle = "M0 -32V-18M0 18V32M0 -18A18 18 0 1 1 0 18A18 18 0 1 1 0 -18";
    private const string BjtBody = "M-32 0H-9M-9 -17V17M-9 -9L12 -22V-32M-9 9L12 22V32";
    private const string MosBody = "M-32 0H-12M-12 -16V16M-5 -17V17M-5 -15H12V-32M-5 15H12V32M-5 0H33";
    private const string Diamond = "M0 -32V-20L18 0L0 20L-18 0L0 -20M0 20V32M-32 -11H-22M-32 11H-22";

    public static PartDefinition Get(Kind kind) => Definitions.TryGetValue(kind, out var definition)
        ? definition : Extended.Value[kind];

    /// <summary>SI unit for the editable primary parameter, if present.</summary>
    public static string? ValueUnit(Kind kind) => kind switch
    {
        Kind.Supply or Kind.PulseVoltage => "V",
        Kind.Current => "A",
        Kind.Resistor or Kind.Ccvs => "Ω",
        Kind.Capacitor or Kind.PolarizedCapacitor => "F",
        Kind.Inductor => "H",
        Kind.Vccs => "S",
        Kind.Vcvs or Kind.Cccs => "gain",
        _ => null
    };

    private static IReadOnlyList<Terminal> Pins(params Terminal[] terminals) => Array.AsReadOnly(terminals);

    private static readonly Dictionary<Kind, PartDefinition> Definitions = new()
    {
        [Kind.Supply] = new("DC voltage", "Sources", "V", Circle + "M-5 -7H5M0 -12V-2M-5 7H5", Two, true, 5),
        [Kind.Resistor] = new("Resistor", "Passives", "R", "M0 -32V-22L-8 -17L8 -10L-8 -3L8 4L-8 11L8 18L0 22V32", Two, true, 1000),
        [Kind.Ground] = new("Ground", "Sheet", "G", "M0 -20V0M-14 0H14M-9 5H9M-4 10H4", Pins(new Terminal("Reference", 0, -20)), true),
        [Kind.Probe] = new("Voltage probe", "Sheet", "P", "M-22 0H-10M-10 0L8 -10L8 10Z", Pins(new Terminal("Measured node", -22, 0)), true),
        [Kind.Capacitor] = new("Capacitor", "Passives", "C", "M0 -32V-5M-13 -5H13M-13 5H13M0 5V32", Two, DefaultValue: 1e-6),
        [Kind.PolarizedCapacitor] = new("Polarized capacitor", "Passives", "C",
            "M0 -32V-5M-13 -5H13M-13 8Q0 2 13 8M0 5V32M9 -19H17M13 -23V-15", Two, DefaultValue: 1e-6),
        [Kind.PulseVoltage] = new("Pulse voltage", "Sources", "V", Circle + "M-10 6H-5V-6H5V6H10", Two, DefaultValue: 5),
        [Kind.Inductor] = new("Inductor", "Passives", "L", "M0 -32V-20Q18 -15 0 -10Q18 -5 0 0Q18 5 0 10Q18 15 0 20V32", Two, DefaultValue: .01),
        [Kind.Current] = new("DC current", "Sources", "I", Circle + "M0 10V-10M-5 -4L0 -10L5 -4", Two),
        [Kind.Diode] = new("Diode", "Semiconductors", "D", "M0 -32V-10M-10 -10H10L0 9ZM-11 9H11M0 9V32",
            Pins(new("Anode", 0, -32), new("Cathode", 0, 32))),
        [Kind.Npn] = new("NPN BJT", "Semiconductors", "Q", BjtBody + "M4 12L10 21L0 20", Bipolar),
        [Kind.Pnp] = new("PNP BJT", "Semiconductors", "Q", BjtBody + "M3 20L-5 12L6 12", Bipolar),
        [Kind.Nmos] = new("NMOS", "Semiconductors", "M", MosBody + "M2 -4L-4 0L2 4", Mos),
        [Kind.Pmos] = new("PMOS", "Semiconductors", "M", MosBody + "M-2 -4L4 0L-2 4", Mos),
        [Kind.Njfet] = new("N-channel JFET", "Semiconductors", "J", "M-32 0H1M-6 -5L1 0L-6 5M1 -18V18M1 -13H12V-32M1 13H12V32", Fet),
        [Kind.Pjfet] = new("P-channel JFET", "Semiconductors", "J", "M-32 0H1M-3 -5L-10 0L-3 5M1 -18V18M1 -13H12V-32M1 13H12V32", Fet),
        [Kind.Vcvs] = new("Voltage-controlled voltage", "Sources", "E", Diamond + "M-4 -7H4M0 -11V-3M-4 7H4", Controlled, DefaultValue: 1),
        [Kind.Vccs] = new("Voltage-controlled current", "Sources", "G", Diamond + "M0 10V-10M-5 -4L0 -10L5 -4", Controlled, DefaultValue: .001),
        [Kind.Cccs] = new("Current-controlled current", "Sources", "F",
            "M0 -32V-20L18 0L0 20L-18 0L0 -20M0 20V32M0 10V-10M-5 -4L0 -10L5 -4", Two, DefaultValue: 1),
        [Kind.Ccvs] = new("Current-controlled voltage", "Sources", "H",
            "M0 -32V-20L18 0L0 20L-18 0L0 -20M0 20V32M-4 -7H4M0 -11V-3M-4 7H4", Two, DefaultValue: 1),
        [Kind.Switch] = new("Voltage-controlled switch", "Analog", "S", "M0 -32V-14M0 14V32M0 14L12 -12M-32 -11H-17M-32 11H-17", Controlled),
        [Kind.OpAmp] = new("Five-pin op amp", "Analog", "X", "M-32 -11H-14M-32 11H-14M-14 -20V20L20 0ZM20 0H32M0 -12V-32M0 12V32",
            Pins(new("Noninverting", -32, -11), new("Inverting", -32, 11), new("Positive rail", 0, -32), new("Negative rail", 0, 32), new("Output", 32, 0)))
    };
}
