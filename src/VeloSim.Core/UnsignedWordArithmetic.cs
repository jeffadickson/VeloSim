using System.Numerics;

namespace VeloSim;

public static class UnsignedWordArithmetic
{
    public static FourStateWord Evaluate(IrOperation operation, FourStateWord a, FourStateWord b, int destinationWidth)
    {
        if (a.Width is < 1 or > 64 || b.Width is < 1 or > 64 || destinationWidth is < 1 or > 64 || operation is not (IrOperation.UnsignedAdd or IrOperation.UnsignedSubtract or IrOperation.UnsignedMultiply)) throw new ArgumentException("Invalid unsigned arithmetic operands or operation.");
        var mask = destinationWidth == 64 ? ulong.MaxValue : (1UL << destinationWidth) - 1;
        // SystemVerilog arithmetic uncertainty is tested on the original operands, including bits later truncated away.
        if (!a.IsKnown || !b.IsKnown) return new(destinationWidth, 0, mask);
        var left = new BigInteger(a.Value); var right = new BigInteger(b.Value); var modulus = BigInteger.One << destinationWidth;
        var result = operation switch { IrOperation.UnsignedAdd => left + right, IrOperation.UnsignedSubtract => left - right, IrOperation.UnsignedMultiply => left * right, _ => throw new ArgumentException("Unsupported unsigned arithmetic.") };
        var residue = result % modulus; if (residue.Sign < 0) residue += modulus;
        return new(destinationWidth, (ulong)residue);
    }
}
