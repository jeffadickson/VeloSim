namespace VeloSim;

public static class FourStateCodec
{
    public static ScalarValue BitAt(FourStateWord word, int bit)
    {
        if (word.Width is < 1 or > 64 || bit < 0 || bit >= word.Width) throw new ArgumentOutOfRangeException(nameof(bit));
        var mask = 1UL << bit; return (word.ZMask & mask) != 0 ? ScalarValue.Z : (word.XMask & mask) != 0 ? ScalarValue.X : (word.Value & mask) != 0 ? ScalarValue.One : ScalarValue.Zero;
    }
    public static string FormatBinary(FourStateWord word)
    {
        if (word.Width is < 1 or > 64) throw new ArgumentException("Invalid four-state word.");
        return new string(Enumerable.Range(0, word.Width).Reverse().Select(bit => ScalarLogic.Format(BitAt(word, bit))).ToArray());
    }
    public static FourStateWord ParseBinary(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length is < 1 or > 64) throw new ArgumentException("Binary word must contain 1..64 digits.");
        ulong value = 0, x = 0, z = 0;
        foreach (var digit in text)
        {
            value <<= 1; x <<= 1; z <<= 1;
            switch (ScalarLogic.Parse(digit)) { case ScalarValue.One: value |= 1; break; case ScalarValue.X: x |= 1; break; case ScalarValue.Z: z |= 1; break; }
        }
        return new(text.Length, value, x, z);
    }
    public static FourStateWord Resize(FourStateWord word, int width)
    {
        if (word.Width is < 1 or > 64 || width is < 1 or > 64) throw new ArgumentException("Invalid resize width.");
        var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        return new(width, word.Value & mask & ~(word.XMask | word.ZMask), word.XMask & mask, word.ZMask & mask);
    }
}
