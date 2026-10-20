namespace VeloSim;

public static class FourStateArithmetic
{
    public static FourStateWord Increment(FourStateWord operand, int width, bool strictSystemVerilog = false)
    {
        if (operand.Width is < 1 or > 64 || width is < 1 or > 64) throw new ArgumentException("Invalid arithmetic word width.");
        var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        if (strictSystemVerilog && !operand.IsKnown) return new(width, 0, mask);
        var source = FourStateCodec.Resize(operand, width); ulong value = 0, x = 0;
        // Sets of possible binary carries and input bits; this oracle never calls native arithmetic.
        var carries = new HashSet<int> { 1 };
        for (int bit = 0; bit < width; bit++)
        {
            var input = FourStateCodec.BitAt(source, bit);
            var inputs = input switch { ScalarValue.Zero => new[] { 0 }, ScalarValue.One => [1], _ => [0, 1] };
            var sums = new HashSet<int>(); var nextCarries = new HashSet<int>();
            foreach (var a in inputs) foreach (var carry in carries) { sums.Add((a + carry) % 2); nextCarries.Add((a + carry) / 2); }
            if (sums.Count == 2) x |= 1UL << bit; else if (sums.Contains(1)) value |= 1UL << bit;
            carries = nextCarries;
        }
        return new(width, value, x);
    }
    public static FourStateWord ResetIncrement(FourStateWord operand, FourStateWord activeLowReset, int width, bool proceduralIf, bool strictSystemVerilog)
    {
        if (activeLowReset.Width != 1) throw new ArgumentException("Reset must be scalar.");
        var next = Increment(operand, width, strictSystemVerilog); var reset = FourStateCodec.BitAt(activeLowReset, 0);
        if (reset == ScalarValue.Zero) return new(width);
        if (reset == ScalarValue.One || proceduralIf) return next;
        return new(width, 0, next.Value | next.XMask | next.ZMask);
    }
}
