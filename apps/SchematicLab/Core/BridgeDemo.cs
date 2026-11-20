namespace SchematicLab.Core;

/// <summary>Mixed linear reference lesson, with all four controlled-source types in the measured path.</summary>
public static class BridgeDemo
{
    public static BridgeOptions Options(BridgeAnalysis analysis) => new(analysis, "P1",
        FrequenciesHz: [100, 1000], StopSeconds: .003, StepSeconds: .000002,
        Phasors: [new("V1", 1, 30), new("I1", .0002, 90)],
        Pulses: [new("V1", 0, 1, 0, .00001, .00001, .01, .02),
                 new("I1", 0, .0002, 0, .00002, .00001, .01, .02)],
        Controls: [new("F1", "V1"), new("H1", "V1")]);

    public static BridgeOptions WaveformOptions(string profile) => new(BridgeAnalysis.Transient, "P1",
        StopSeconds: .003, StepSeconds: .000002,
        Pwl: profile == "waveforms" ? [new("V1", [new(0, 0), new(.00002, 1), new(.003, 1)])] : [],
        Exponentials: profile == "exponential" ? [new("V1", 0, 1, .00002, .0001, .001, .0002)] : [],
        Sines: profile == "sine" ? [new("V1", 0, 1, 1000, 0, 100)] : []);

    public static BridgeOptions ModelOptions(BridgeAnalysis analysis, string probeId) => new(analysis, probeId,
        FrequenciesHz: [100, 1000], StopSeconds: .0001, StepSeconds: .00000005,
        Phasors: [new("V2", 1, 0), new("V3", 1, 0)],
        Pulses: [new("V2", 1.2, 1.6, 0, .00002, .00002, .005, .01),
                 new("V3", .2, 1, 0, .00002, .00002, .005, .01)],
        Diodes: [new("D1", 1e-14, 8.617333262145e-5 * 300.15, 2e-9, .7, .5, .5, 1e-8)],
        Mos1: [new("M1", .7, .001, 0, .6, .02, 1e-10, 2e-10, 5e-11, 3e-10, 4e-10)]);

    /// <summary>Independent MOS1 fixture with 1 mm square effective geometry and 10 nm oxide.</summary>
    public static BridgeOptions MeyerOptions(BridgeAnalysis analysis) => ModelOptions(analysis, "P2") with
    {
        StopSeconds = .00002,
        Diodes = [new("D1", 1e-14, 8.617333262145e-5 * 300.15)],
        Mos1 = [new("M1", .7, .001, 0, .6, .02, OxideFarads: 3.45314379969e-9)]
    };

    // The BJT reference deck uses the simulator's documented 27 C nominal convention.
    private const double ThermalVolts = 1.38064852e-23 / 1.6021766208e-19 * 300.15;

    public static BridgeOptions BjtOptions(BridgeAnalysis analysis) => new(analysis, "P1",
        FrequenciesHz: [100, 1000], StopSeconds: .0001, StepSeconds: .00000005,
        Phasors: [new("V2", 1, 0)], Pulses: [new("V2", .55, .68, 0, .00002, .00002, .005, .01)],
        Bjts: [new("Q1", 1e-15, 100, 1, ThermalVolts, ThermalVolts,
            new(3e-10, .75, .33, .5, 1e-9), new(2e-10, .75, .33, .5, 2e-9))]);

    public static BridgeOptions SoiOptions() => new(BridgeAnalysis.Dc, "P1", FdSoi: ["M1"]);

    internal static Board BuildSoi() => Build(
    [
        (Kind.Ground, 310, 330, 0, ["0"]),
        (Kind.Supply, 85, 85, .8, ["supply", "0"]),
        (Kind.Supply, 85, 185, .8, ["gate", "0"]),
        (Kind.Supply, 85, 285, -1, ["back-gate", "0"]),
        (Kind.Resistor, 300, 90, 1000, ["supply", "drain"]),
        (Kind.NmosSoi, 315, 225, 0, ["drain", "gate", "0", "back-gate"]),
        (Kind.Probe, 520, 150, 0, ["drain"])
    ]);

    public static BridgeOptions Bsim3Options(BridgeAnalysis analysis) => new(analysis, "P1",
        FrequenciesHz: [1e6, 1e9], StopSeconds: 1e-9, StepSeconds: 2e-12,
        Phasors: [new("V2", 1, 0)], Pulses: [new("V2", .8, .9, 0, 1e-9, 1e-9, 5e-9, 1e-8)],
        Bsim3: [new("M1", 3.5e-9, 2.4e17, 5e16, .15, 1e-6, .35e-6, 1.5e-6, 1.5e-6, 1.5e-12, 1.5e-12)]);

    internal static Board BuildBsim3() => Build(
    [
        (Kind.Ground, 310, 330, 0, ["0"]),
        (Kind.Supply, 85, 95, .9, ["supply", "0"]),
        (Kind.Supply, 85, 235, .8, ["gate", "0"]),
        (Kind.Resistor, 300, 90, 1000, ["supply", "drain"]),
        (Kind.Nmos, 315, 225, 0, ["drain", "gate", "0", "0"]),
        (Kind.Capacitor, 450, 235, 2e-14, ["drain", "0"]),
        (Kind.Probe, 520, 150, 0, ["drain"])
    ]);

    internal static Board BuildBjt() => Build(
    [
        (Kind.Ground, 310, 330, 0, ["0"]),
        (Kind.Supply, 85, 95, 2, ["supply", "0"]),
        (Kind.Supply, 85, 235, .65, ["drive", "0"]),
        (Kind.Resistor, 190, 235, 500, ["drive", "base"]),
        (Kind.Resistor, 300, 90, 1000, ["supply", "collector"]),
        (Kind.Npn, 315, 225, 0, ["collector", "base", "0"]),
        (Kind.Probe, 470, 160, 0, ["collector"])
    ]);

    internal static Board BuildModels() => Build(
    [
        (Kind.Ground, 320, 330, 0, ["0"]),
        (Kind.Supply, 90, 100, 1, ["drain-supply", "0"]),
        (Kind.Supply, 90, 235, 1.5, ["gate", "0"]),
        (Kind.Supply, 385, 105, .8, ["diode-supply", "0"]),
        (Kind.Resistor, 220, 95, 1000, ["drain-supply", "drain"]),
        (Kind.Resistor, 490, 95, 1000, ["diode-supply", "diode"]),
        (Kind.Nmos, 235, 235, 0, ["drain", "gate", "0", "0"]),
        (Kind.Diode, 485, 240, 0, ["diode", "0"]),
        (Kind.Probe, 575, 160, 0, ["diode"]),
        (Kind.Probe, 310, 165, 0, ["drain"])
    ]);

    internal static Board Build() => Build(
        [
            (Kind.Ground, 310, 345, 0, ["0"]),
            (Kind.Supply, 65, 160, 1, ["in", "0"]),
            (Kind.Resistor, 160, 70, 100, ["in", "rl"]),
            (Kind.Inductor, 265, 70, .01, ["rl", "filter"]),
            (Kind.Capacitor, 365, 70, 1e-6, ["filter", "0"]),
            (Kind.Vcvs, 465, 70, 2, ["e", "0", "filter", "0"]),
            (Kind.Vccs, 465, 185, .001, ["0", "sum", "filter", "0"]),
            (Kind.Cccs, 185, 185, .5, ["0", "sum"]),
            (Kind.Ccvs, 275, 185, 30, ["h", "0"]),
            (Kind.Resistor, 385, 185, 1000, ["e", "sum"]),
            (Kind.Resistor, 385, 280, 1000, ["h", "sum"]),
            (Kind.Resistor, 485, 280, 1000, ["sum", "0"]),
            (Kind.Current, 95, 275, .0002, ["0", "sum"]),
            (Kind.Probe, 575, 175, 0, ["sum"])
        ]);

    private static Board Build((Kind Kind, int X, int Y, double Value, string[] Nets)[] parts)
    {
        var board = new Board();
        foreach (var entry in parts)
        {
            var part = board.Place(entry.Kind, entry.X, entry.Y);
            board.Replace(part with { Value = entry.Value });
            for (var i = 0; i < entry.Nets.Length; i++) board.Label(new(part.Id, i), entry.Nets[i]);
        }
        return board;
    }
}
