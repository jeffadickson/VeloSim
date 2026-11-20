namespace SchematicLab.Core;

public static partial class PartCatalog
{
    private static readonly IReadOnlyList<Terminal> Horizontal = Pins(new("Positive", -33, 0), new("Negative", 33, 0));
    private static readonly IReadOnlyList<Terminal> InOut = Pins(new("Input", -33, 0), new("Output", 33, 0));
    private static readonly IReadOnlyList<Terminal> WindingPins = Pins(new("Primary 1", -33, -12), new("Primary 2", -33, 12),
        new("Secondary 1", 33, -12), new("Secondary 2", 33, 12));
    private static readonly IReadOnlyList<Terminal> SensePins = Pins(new("Output +", -33, -12), new("Output −", -33, 12),
        new("Sense +", 33, -12), new("Sense −", 33, 12));
    private static readonly IReadOnlyList<Terminal> LinePins = Pins(new("Near +", -33, -12), new("Near −", -33, 12),
        new("Far +", 33, -12), new("Far −", 33, 12));
    private static readonly IReadOnlyList<Terminal> CoupledLinePins = Pins(new("Near 1", -33, -14), new("Near 2", -33, 0),
        new("Near reference", -33, 14), new("Far 1", 33, -14), new("Far 2", 33, 0), new("Far reference", 33, 14));
    private static readonly IReadOnlyList<Terminal> Soi = Pins(new("Drain", -33, -14), new("Gate", -33, 0),
        new("Source", -33, 14), new("Back gate", 33, -10), new("Body contact", 33, 10));
    private static readonly IReadOnlyList<Terminal> Substrate = Pins(new("Collector", -33, -12), new("Base", -33, 0),
        new("Emitter", -33, 12), new("Substrate", 33, 0));
    private static readonly IReadOnlyList<Terminal> Thermal = Pins(new("Collector", -33, -12), new("Base", -33, 0),
        new("Emitter", -33, 12), new("Substrate", 33, -10), new("Thermal", 33, 10));

    private static IReadOnlyList<Terminal> Inputs(string a, string b, string output) =>
        Pins(new(a, -33, -10), new(b, -33, 10), new(output, 33, 0));

    private static PartDefinition Wave(string name, string mark, bool current) =>
        new(name, "Sources", current ? "I" : "V", Circle, Two, Mark: mark);

    // Original block artwork keeps every lead attached to its declared pin.
    // The compact mark identifies the type even when a family shares geometry.
    private static PartDefinition Block(string name, string category, string prefix, string mark,
        IReadOnlyList<Terminal>? terminals = null)
    {
        terminals ??= [];
        var leads = terminals.Select(pin => pin.X is <= -25 or >= 25
            ? FormattableString.Invariant($"M{pin.X} {pin.Y}H{Math.Sign(pin.X) * 21}")
            : FormattableString.Invariant($"M{pin.X} {pin.Y}V{Math.Sign(pin.Y) * 18}"));
        return new(name, category, prefix, "M-21 -18H21V18H-21Z" + string.Concat(leads), terminals, Mark: mark);
    }

    // Lazy construction runs after both partial declarations' shared geometry has initialized.
    private static readonly Lazy<Dictionary<Kind, PartDefinition>> Extended = new(() => new()
    {
        [Kind.AcVoltage] = Wave("AC voltage", "AC", false),
        [Kind.AcCurrent] = Wave("AC current", "AC", true),
        [Kind.SineVoltage] = Wave("Sinusoidal voltage", "SIN", false),
        [Kind.SineCurrent] = Wave("Sinusoidal current", "SIN", true),
        [Kind.PwlVoltage] = Wave("Piecewise linear voltage", "PWL", false),
        [Kind.PwlCurrent] = Wave("Piecewise linear current", "PWL", true),
        [Kind.PwlFileVoltage] = Wave("File-backed PWL voltage", "FILE", false),
        [Kind.PwlFileCurrent] = Wave("File-backed PWL current", "FILE", true),
        [Kind.ExpVoltage] = Wave("Exponential voltage", "EXP", false),
        [Kind.ExpCurrent] = Wave("Exponential current", "EXP", true),
        [Kind.FmVoltage] = Wave("Frequency-modulated voltage", "FM", false),
        [Kind.FmCurrent] = Wave("Frequency-modulated current", "FM", true),
        [Kind.AmVoltage] = Wave("Amplitude-modulated voltage", "AM", false),
        [Kind.AmCurrent] = Wave("Amplitude-modulated current", "AM", true),
        [Kind.PatternVoltage] = Wave("Pattern voltage", "PAT", false),
        [Kind.PatternCurrent] = Wave("Pattern current", "PAT", true),
        [Kind.NoiseVoltage] = Wave("Noise voltage", "NOISE", false),
        [Kind.NoiseCurrent] = Wave("Noise current", "NOISE", true),
        [Kind.RandomVoltage] = Wave("Random voltage", "RND", false),
        [Kind.RandomCurrent] = Wave("Random current", "RND", true),
        [Kind.PulseCurrent] = Wave("Pulse current", "PULSE", true),
        [Kind.Transformer] = Block("Transformer", "Passives", "T", "XFMR", WindingPins),
        [Kind.CoupledInductor] = Block("Coupled inductor directive", "Passives", "K", "K"),
        [Kind.SaturableInductor] = Block("Saturable inductor", "Passives", "L", "LSAT", Horizontal),
        [Kind.Memristor] = Block("Memristor", "Passives", "MR", "MEM", Horizontal),
        [Kind.Nmesfet] = Block("N-channel MESFET", "Semiconductors", "Z", "MES", Fet),
        [Kind.Pmesfet] = Block("P-channel MESFET", "Semiconductors", "Z", "MES", Fet),
        [Kind.Nvdmos] = Block("N-channel VDMOS", "Semiconductors", "M", "VDMOS", Mos),
        [Kind.Pvdmos] = Block("P-channel VDMOS", "Semiconductors", "M", "VDMOS", Mos),
        [Kind.NmosSoi] = Block("NMOS SOI", "Semiconductors", "M", "SOI", Soi),
        [Kind.PmosSoi] = Block("PMOS SOI", "Semiconductors", "M", "SOI", Soi),
        [Kind.NpnSubstrate] = Block("NPN BJT with substrate", "Semiconductors", "Q", "SUB", Substrate),
        [Kind.NpnThermal] = Block("NPN electrothermal BJT", "Semiconductors", "Q", "THERM", Thermal),
        [Kind.PnpSubstrate] = Block("PNP BJT with substrate", "Semiconductors", "Q", "SUB", Substrate),
        [Kind.PnpThermal] = Block("PNP electrothermal BJT", "Semiconductors", "Q", "THERM", Thermal),
        [Kind.IdealOpAmp] = Block("Ideal op amp", "Analog", "E", "OP", Inputs("Noninverting", "Inverting", "Output")),
        [Kind.BehavioralSource] = Block("Behavioral source", "Sources", "B", "B", Two),
        [Kind.CurrentSwitch] = Block("Current-controlled switch", "Analog", "W", "ISW", SensePins),
        [Kind.ExpressionSwitch] = Block("Expression-controlled switch", "Analog", "S", "SW", Horizontal),
        [Kind.CurrentSenseCccs] = Block("Current-controlled current with sense terminals", "Sources", "F", "F", SensePins),
        [Kind.CurrentSenseCcvs] = Block("Current-controlled voltage with sense terminals", "Sources", "H", "H", SensePins),
        [Kind.TransmissionLine] = Block("Lossless transmission line", "Transmission", "T", "TL", LinePins),
        [Kind.LossyTransmissionLine] = Block("Lossy transmission line", "Transmission", "O", "LOSS", LinePins),
        [Kind.CoupledTransmissionLine] = Block("Coupled transmission line", "Transmission", "P", "CPL", CoupledLinePins),
        [Kind.RfPort] = Block("RF port", "Transmission", "P", "RF", Two),
        [Kind.LoopProbe] = Block("Loop probe", "Sheet", "V", "LOOP", Horizontal),
        [Kind.InterfacePort] = Block("Interface port", "Sheet", "P", "PORT", Pins(new Terminal("Port", -33, 0))),
        [Kind.CellInstance] = Block("Two-port child sheet", "Sheet", "X", "CELL", Horizontal),
        [Kind.Gain] = Block("Analog gain", "Mixed signal", "A", "GAIN", InOut),
        [Kind.Limiter] = Block("Limiter", "Mixed signal", "A", "LIM", InOut),
        [Kind.Integrator] = Block("Integrator", "Mixed signal", "A", "INT", InOut),
        [Kind.Differentiator] = Block("Differentiator", "Mixed signal", "A", "DIFF", InOut),
        [Kind.Summer] = Block("Analog summer", "Mixed signal", "A", "SUM", Inputs("Input 1", "Input 2", "Output")),
        [Kind.Multiplier] = Block("Analog multiplier", "Mixed signal", "A", "MULT", Inputs("Input 1", "Input 2", "Output")),
        [Kind.Divider] = Block("Analog divider", "Mixed signal", "A", "DIV", Inputs("Input 1", "Input 2", "Output")),
        [Kind.Inverter] = Block("Inverter", "Digital", "A", "NOT", InOut),
        [Kind.Buffer] = Block("Digital buffer", "Digital", "A", "BUF", InOut),
        [Kind.AdcBridge] = Block("Analog-to-digital bridge", "Digital", "A", "ADC", InOut),
        [Kind.DacBridge] = Block("Digital-to-analog bridge", "Digital", "A", "DAC", InOut),
        [Kind.AndGate] = Block("AND gate", "Digital", "A", "AND", Inputs("A", "B", "Output")),
        [Kind.OrGate] = Block("OR gate", "Digital", "A", "OR", Inputs("A", "B", "Output")),
        [Kind.NandGate] = Block("NAND gate", "Digital", "A", "NAND", Inputs("A", "B", "Output")),
        [Kind.NorGate] = Block("NOR gate", "Digital", "A", "NOR", Inputs("A", "B", "Output")),
        [Kind.XorGate] = Block("XOR gate", "Digital", "A", "XOR", Inputs("A", "B", "Output")),
        [Kind.Tristate] = Block("Tri-state buffer", "Digital", "A", "TRI", Pins(new("Input", -33, 0), new("Enable", 0, -32), new("Output", 33, 0))),
        [Kind.DFlipFlop] = Block("D flip-flop", "Digital", "A", "DFF", Pins(new("D", -33, -10), new("Clock", -33, 10), new("Q", 33, -10), new("Q bar", 33, 10))),
        [Kind.JkFlipFlop] = Block("JK flip-flop", "Digital", "A", "JKFF", Pins(new("J", -33, -12), new("K", -33, 12), new("Clock", -33, 0), new("Q", 33, -12), new("Q bar", 33, 12))),
        [Kind.SrLatch] = Block("SR latch", "Digital", "A", "SR", Pins(new("Set", -33, -10), new("Reset", -33, 10), new("Enable", 0, -32), new("Q", 33, -10), new("Q bar", 33, 10))),
    });
}
