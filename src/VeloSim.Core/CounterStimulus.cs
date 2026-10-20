using System.Globalization;

namespace VeloSim;

public static class CounterStimulus
{
    // Fixed xorshift32 protocol: reproducible across .NET runtime versions.
    public static IReadOnlyList<int> Create(uint seed, int cycles)
    {
        if (cycles is < 1 or > 10_000_000) throw new ArgumentOutOfRangeException(nameof(cycles));
        var state = seed == 0 ? 0x6D2B79F5u : seed; var result = new int[cycles];
        for (int i = 1; i < cycles; i++) { state ^= state << 13; state ^= state >> 17; state ^= state << 5; result[i] = state % 19 == 0 ? 0 : 1; }
        return Array.AsReadOnly(result);
    }
    public static string ToText(IReadOnlyList<int> resets)
    {
        Validate(resets); return string.Join('\n', resets.Select(n => n.ToString(CultureInfo.InvariantCulture))) + "\n";
    }
    internal static void Validate(IReadOnlyList<int> resets)
    { if (resets.Count == 0 || resets[0] != 0 || resets.Any(n => n is not (0 or 1))) throw new ArgumentException("Stimulus requires binary resets beginning asserted."); }
    public static IReadOnlyList<TraceSample> RunNative(IrDesign design, IReadOnlyList<int> resets, int workers, CancellationToken cancellationToken = default)
    {
        Validate(resets);
        cancellationToken.ThrowIfCancellationRequested();
        var contract = CounterDesignContract.Inspect(design);
        foreach (var name in new[] { "clk", "rst_n", "count" }) if (!design.Signals.Any(s => s.Name == name)) throw new ArgumentException("Counter signal missing: " + name);
        if (design.Signals.First(s => s.Name == "clk").Initial.Width != 1 || design.Signals.First(s => s.Name == "rst_n").Initial.Width != 1) throw new ArgumentException("Counter inputs must be scalar.");
        using var sim = new Simulation(design); var samples = new List<TraceSample>();
        for (int i = 0; i < resets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sim.Drive(new Dictionary<string, FourStateWord> { ["clk"] = new(1), ["rst_n"] = new(1, (ulong)resets[i]) }, workers);
            sim.Drive(new Dictionary<string, FourStateWord> { ["clk"] = new(1, 1) }, workers);
            var value = sim.Read("count"); var bits = new char[value.Width];
            if (value.Width != contract.Width) throw new InvalidOperationException("Native observed width differs from counter contract.");
            for (int bit = 0; bit < value.Width; bit++) { var mask = 1UL << bit; bits[value.Width - 1 - bit] = (value.ZMask & mask) != 0 ? 'z' : (value.XMask & mask) != 0 ? 'x' : (value.Value & mask) != 0 ? '1' : '0'; }
            samples.Add(new(i, new(bits)));
        }
        return samples.AsReadOnly();
    }
}
