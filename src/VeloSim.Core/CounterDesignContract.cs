namespace VeloSim;

public sealed record CounterDesignContract(int Width)
{
    public static CounterDesignContract Inspect(IrDesign design)
    {
        var signals = design.Signals.ToDictionary(s => s.Name, StringComparer.Ordinal);
        if (!signals.TryGetValue("clk", out var clock) || clock.Initial.Width != 1 || !signals.TryGetValue("rst_n", out var reset) || reset.Initial.Width != 1 || !signals.TryGetValue("count", out var count)) throw new IrValidationException("Counter differential requires scalar clk/rst_n and count output.");
        var writers = design.Processes.Where(p => p.Destination == "count").ToArray();
        if (writers.Length != 1 || writers[0] is not { Clock: "clk", Reset: "rst_n", Source: "count", Edge: IrEdge.Posedge, Operation: IrOperation.SvIfResetIncrementStrict }) throw new IrValidationException("Counter differential requires count <= !rst_n ? 0 : count + 1 on posedge clk with strict SystemVerilog arithmetic.");
        return new(count.Initial.Width);
    }
}
public static class CounterSpecification
{
    public static IReadOnlyList<TraceSample> Generate(IReadOnlyList<int> resets, int width)
    {
        CounterStimulus.Validate(resets); if (width is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(width));
        ulong value = 0; var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1; var result = new List<TraceSample>();
        for (int i = 0; i < resets.Count; i++) { value = resets[i] == 0 ? 0 : unchecked(value + 1) & mask; result.Add(new(i, FourStateCodec.FormatBinary(new(width, value)))); }
        return result.AsReadOnly();
    }
}
