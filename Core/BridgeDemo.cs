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

    public static BridgeOptions ModelOptions(BridgeAnalysis analysis, string probeId) => new(analysis, probeId,
        FrequenciesHz: [100, 1000], StopSeconds: .0001, StepSeconds: .00000005,
        Phasors: [new("V2", 1, 0), new("V3", 1, 0)],
        Pulses: [new("V2", 1.2, 1.6, 0, .00002, .00002, .005, .01),
                 new("V3", .2, 1, 0, .00002, .00002, .005, .01)],
        Diodes: [new("D1", 1e-14, 8.617333262145e-5 * 300.15, 2e-9, .7, .5, .5, 1e-8)],
        Mos1: [new("M1", .7, .001, 0, .6, .02)]);

    internal static Board BuildModels() => Build(
    [
        (Kind.Ground, 320, 330, 0, ["0"]),
        (Kind.Supply, 90, 100, 3, ["drain-supply", "0"]),
        (Kind.Supply, 90, 235, 1.5, ["gate", "0"]),
        (Kind.Supply, 385, 105, 1, ["diode-supply", "0"]),
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
