namespace VeloSim;

public readonly record struct FourStateWord
{
    public int Width { get; }
    public ulong Value { get; }
    public ulong XMask { get; }
    public ulong ZMask { get; }
    public ulong Mask => Width == 64 ? ulong.MaxValue : (1UL << Width) - 1;
    public bool IsKnown => (XMask | ZMask) == 0;

    public FourStateWord(int width, ulong value = 0, ulong xmask = 0, ulong zmask = 0)
    {
        if (width is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(width));
        var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
        if (((value | xmask | zmask) & ~mask) != 0 || (xmask & zmask) != 0)
            throw new ArgumentException("Invalid four-state value or overlapping masks.");
        Width = width; Value = value; XMask = xmask; ZMask = zmask;
    }
}
