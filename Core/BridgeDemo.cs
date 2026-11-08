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

    internal static Board Build()
    {
        var board = new Board();
        (Kind Kind, int X, int Y, double Value, string[] Nets)[] parts =
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
        ];
        foreach (var entry in parts)
        {
            var part = board.Place(entry.Kind, entry.X, entry.Y);
            board.Replace(part with { Value = entry.Value });
            for (var i = 0; i < entry.Nets.Length; i++) board.Label(new(part.Id, i), entry.Nets[i]);
        }
        return board;
    }
}
