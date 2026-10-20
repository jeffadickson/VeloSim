namespace VeloSim;

public static class FourStateLogic
{
    public static FourStateWord Evaluate(ScalarOperation operation, FourStateWord a, FourStateWord b)
    {
        if (a.Width is < 1 or > 64 || a.Width != b.Width) throw new ArgumentException("Bitwise word widths must match.");
        ulong value = 0, x = 0, z = 0;
        for (int bit = 0; bit < a.Width; bit++)
        {
            var result = ScalarLogic.Evaluate(operation, FourStateCodec.BitAt(a, bit), FourStateCodec.BitAt(b, bit));
            var mask = 1UL << bit;
            if (result == ScalarValue.One) value |= mask; else if (result == ScalarValue.X) x |= mask; else if (result == ScalarValue.Z) z |= mask;
        }
        return new(a.Width, value, x, z);
    }
}
