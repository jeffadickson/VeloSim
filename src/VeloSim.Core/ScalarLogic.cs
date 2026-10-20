namespace VeloSim;

// Independent table oracle for scalar IEEE four-state bitwise logic and edge classification.
public static class ScalarLogic
{
    private static readonly string[] AndTable = ["0000", "01xx", "0xxx", "0xxx"];
    private static readonly string[] OrTable = ["01xx", "1111", "x1xx", "x1xx"];
    private static readonly string[] XorTable = ["01xx", "10xx", "xxxx", "xxxx"];
    private static readonly string[] RisingTable = ["0111", "0000", "0100", "0100"];
    private static readonly string[] FallingTable = ["0000", "1011", "1000", "1000"];
    public static ScalarValue Parse(char value) => value switch { '0' => ScalarValue.Zero, '1' => ScalarValue.One, 'x' or 'X' => ScalarValue.X, 'z' or 'Z' => ScalarValue.Z, _ => throw new ArgumentException("Invalid four-state digit.") };
    public static char Format(ScalarValue value) => value switch { ScalarValue.Zero => '0', ScalarValue.One => '1', ScalarValue.X => 'x', ScalarValue.Z => 'z', _ => throw new ArgumentException("Invalid scalar value.") };
    public static ScalarValue Evaluate(ScalarOperation operation, ScalarValue a, ScalarValue b)
    {
        if (!Enum.IsDefined(operation) || !Enum.IsDefined(a) || !Enum.IsDefined(b)) throw new ArgumentException("Invalid scalar logic input.");
        return operation switch
        {
            ScalarOperation.Copy => a,
            ScalarOperation.Not => Parse("10xx"[(int)a]),
            ScalarOperation.And => Parse(AndTable[(int)a][(int)b]),
            ScalarOperation.Or => Parse(OrTable[(int)a][(int)b]),
            ScalarOperation.Xor => Parse(XorTable[(int)a][(int)b]),
            _ => throw new ArgumentException("Invalid operation.")
        };
    }
    public static bool IsEdge(IrEdge edge, ScalarValue before, ScalarValue after)
    {
        if (!Enum.IsDefined(edge) || !Enum.IsDefined(before) || !Enum.IsDefined(after)) throw new ArgumentException("Invalid edge input.");
        return (edge == IrEdge.Posedge ? RisingTable : FallingTable)[(int)before][(int)after] == '1';
    }
}
