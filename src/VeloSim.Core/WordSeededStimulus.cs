namespace VeloSim;

public static class WordSeededStimulus
{
    public static WordFixture Generate(uint seed, int steps, int sourceWidth, int destinationWidth, IReadOnlyList<int> workers, CancellationToken cancellationToken = default)
    {
        if (steps is < 2 or > 4096 || sourceWidth is < 1 or > 64 || destinationWidth is < 1 or > 64) throw new ArgumentException("Seeded word stimulus requires 2..4096 steps and widths 1..64.");
        cancellationToken.ThrowIfCancellationRequested();
        var signals = new List<IrSignal> { new("clk_a", new(1)), new("clk_b", new(1, 1)), new("rst", new(1, 1)), new("a", new(sourceWidth)), new("b", new(sourceWidth == 64 ? 1 : sourceWidth)) };
        var processes = new List<IrProcess>();
        var operations = new[] { IrOperation.Copy, IrOperation.SvIncrement, IrOperation.SvIfResetIncrementStrict, IrOperation.And, IrOperation.Or, IrOperation.Xor, IrOperation.Not, IrOperation.UnsignedAdd, IrOperation.UnsignedSubtract, IrOperation.UnsignedMultiply };
        for (int i = 0; i < operations.Length; i++) { signals.Add(new("q" + i, new(destinationWidth))); processes.Add(new(i % 2 == 0 ? "clk_a" : "clk_b", "q" + i, "a", "rst", i % 2 == 0 ? IrEdge.Posedge : IrEdge.Negedge, operations[i], "b")); }
        signals.Add(new("history", new(destinationWidth))); processes.Add(new("clk_a", "history", "q7", "rst", IrEdge.Posedge, IrOperation.Copy));
        var design = new IrDesign(signals, processes); uint state = seed == 0 ? 0x6D2B79F5u : seed;
        uint Next() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; }
        FourStateWord Word(int width, bool fourState)
        {
            ulong value = 0, x = 0, z = 0;
            for (int bit = 0; bit < width; bit++) switch (Next() % (fourState ? 4u : 2u)) { case 1: value |= 1UL << bit; break; case 2: x |= 1UL << bit; break; case 3: z |= 1UL << bit; break; }
            return new(width, value, x, z);
        }
        var drives = new List<IReadOnlyDictionary<string, FourStateWord>>();
        for (int step = 0; step < steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested(); bool unknown = step % 8 >= 4;
            // Known-clock phases guarantee real activation and known arithmetic; random phases cover X/Z edges.
            drives.Add(new Dictionary<string, FourStateWord> { ["clk_a"] = unknown ? Word(1, true) : new(1, (ulong)(step % 2)), ["clk_b"] = unknown ? Word(1, true) : new(1, (ulong)(1 - step % 2)), ["rst"] = unknown ? Word(1, true) : new(1, 1), ["a"] = Word(sourceWidth, unknown), ["b"] = Word(sourceWidth == 64 ? 1 : sourceWidth, unknown) });
        }
        var fixture = new WordFixture(design, drives, workers); WordSystemVerilog.Validate(fixture); return fixture;
    }
}
