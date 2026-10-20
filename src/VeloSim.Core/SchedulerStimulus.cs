namespace VeloSim;

public static class SchedulerStimulus
{
    public static SchedulerFixture Generate(uint seed, int steps)
    {
        if (steps is < 2 or > 100_000) throw new ArgumentOutOfRangeException(nameof(steps));
        uint state = seed == 0 ? 0x6D2B79F5u : seed;
        ScalarValue Next() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return (ScalarValue)(state % 4); }
        var drives = new List<IReadOnlyDictionary<int, ScalarValue>>
        {
            new Dictionary<int, ScalarValue> { [0] = ScalarValue.Zero, [1] = ScalarValue.One, [2] = ScalarValue.One, [3] = ScalarValue.Zero },
            new Dictionary<int, ScalarValue> { [0] = ScalarValue.One, [1] = ScalarValue.Zero }
        };
        for (int step = 2; step < steps; step++) drives.Add(step % 19 == 0 ? new Dictionary<int, ScalarValue>() : new Dictionary<int, ScalarValue> { [0] = Next(), [1] = Next(), [2] = Next(), [3] = Next() });
        return new(9, [new(0, 4, 2, 3, IrEdge.Posedge, ScalarOperation.Copy), new(0, 5, 4, 3, IrEdge.Posedge, ScalarOperation.And), new(1, 6, 2, 3, IrEdge.Negedge, ScalarOperation.Or), new(1, 7, 2, 3, IrEdge.Negedge, ScalarOperation.Xor), new(0, 8, 2, 3, IrEdge.Posedge, ScalarOperation.Not)], drives, [4, 5, 6, 7, 8]);
    }
}
