using System.Numerics;
using System.Text;

namespace VeloSim.Uhdm;

public sealed partial class UhdmRtlLowerer
{
    private readonly Dictionary<string, UhdmNode?> originalExpressions = new(StringComparer.Ordinal);
    private readonly HashSet<UhdmNode> originalVisited = new(ReferenceEqualityComparer.Instance);

    private static bool UnsizedDecimal(UhdmNode? h)
    {
        if (TypeOf(h) != UhdmVpi.VpiConstant) return false;
        string text = h!.GetString(UhdmVpi.VpiDecompile);
        if (text.Length == 0) return false;
        foreach (char c in text)
            if (c is not ((>= '0' and <= '9') or '_')) return false;
        return true;
    }

    internal string ConstantBits(UhdmNode obj, bool plainDecimal = false) =>
        ConstantBits(obj.GetInt(UhdmVpi.VpiSize), obj.GetFieldString("VpiValue"), obj.GetString(UhdmVpi.VpiDecompile), plainDecimal);

    internal string ConstantBits(long rawWidth, string raw, string literal = "", bool plainDecimal = false)
    {
        long wLong = plainDecimal ? 32L : rawWidth;
        if (wLong < 1 && literal is "'0" or "'1" or "'x" or "'z") wLong = 1;
        Need(wLong >= 1 && wLong <= limits.MaxWordBits, "constant width exceeds limit");
        int w = (int)wLong;

        Need(raw.Length <= limits.MaxWordBits * 2, "constant payload budget exceeded");
        int colon = raw.IndexOf(':');
        Need(colon >= 0, "constant payload tag required");
        string tag = raw[..colon];
        string value = raw[(colon + 1)..].Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        Need(value.Length > 0, "empty constant payload");

        if (tag is "INT" or "UINT" or "DEC")
        {
            if (value is "x" or "z") return new string(value[0], w);
            bool negative = value[0] == '-';
            int start = negative ? 1 : 0;
            Need(start < value.Length, "invalid decimal constant");
            BigInteger number = BigInteger.Zero;
            BigInteger maxWord = (BigInteger.One << Math.Max(256, limits.MaxWordBits)) - 1;
            for (int i = start; i < value.Length; i++)
            {
                Need(value[i] is >= '0' and <= '9', "invalid decimal digit");
                uint digit = (uint)(value[i] - '0');
                Need(number <= (maxWord - digit) / 10, "decimal constant exceeds256bits");
                number = number * 10 + digit;
            }
            if (plainDecimal)
                Need(!negative && number <= 2147483647, "large unsized decimal requires an explicit sized literal");
            if (negative)
                number = -number;
            var sb = new StringBuilder(w);
            for (int bit = w - 1; bit >= 0; bit--)
                sb.Append(((number >> bit) & BigInteger.One) != 0 ? '1' : '0');
            return sb.ToString();
        }

        int group = tag is "BIN" or "SCAL" ? 1 : tag == "OCT" ? 3 : tag == "HEX" ? 4 : 0;
        Need(group != 0, "unsupported constant payload tag");
        var bitsOut = new StringBuilder(value.Length * group);
        foreach (char c in value)
        {
            if (c is 'x' or 'z')
            {
                bitsOut.Append(c, group);
            }
            else
            {
                int digit = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : -1;
                Need(digit >= 0 && digit < (1 << group), "invalid based constant digit");
                for (int bit = group - 1; bit >= 0; bit--)
                    bitsOut.Append((digit & (1 << bit)) != 0 ? '1' : '0');
            }
        }
        string result = bitsOut.ToString();
        if (result.Length < w)
        {
            char fill = result[0] is 'x' or 'z' ? result[0] : '0';
            result = new string(fill, w - result.Length) + result;
        }
        if (result.Length > w)
            result = result[^w..];
        return result;
    }

    private string Bits(UhdmNode? h)
    {
        Need(TypeOf(h) == UhdmVpi.VpiConstant, "constant required");
        return ConstantBits(h!, UnsizedDecimal(h));
    }

    private static string ExpressionLocation(UhdmNode? h)
    {
        if (h is null || h.LineNo == 0 || h.ColumnNo == 0) return string.Empty;
        UhdmNode? parent = h;
        string file = string.Empty;
        for (int depth = 0; parent is not null && depth < 128; depth++)
        {
            file = parent.File;
            if (file.Length > 0) break;
            var next = parent.Parent;
            if (ReferenceEquals(next, parent)) break;
            parent = next;
        }
        if (file.Length == 0) return string.Empty;
        return $"{file}:{h.LineNo}:{h.ColumnNo}:{h.EndLineNo}:{h.EndColumnNo}";
    }

    private static readonly int[] OriginalSingleRelations =
    [
        UhdmVpi.VpiStmt, UhdmVpi.VpiCondition, UhdmVpi.VpiElseStmt, UhdmVpi.VpiLhs, UhdmVpi.VpiRhs,
        UhdmVpi.VpiIndex, UhdmVpi.VpiBaseExpr, UhdmVpi.VpiWidthExpr, UhdmVpi.VpiLeftRange, UhdmVpi.VpiRightRange,
        UhdmVpi.VpiHighConn, UhdmVpi.VpiLowConn, UhdmVpi.VpiExpr
    ];

    private static readonly int[] OriginalManyRelations =
    [
        UhdmVpi.VpiModule, UhdmVpi.VpiTaskFunc, UhdmVpi.VpiPort, UhdmVpi.VpiVariables, UhdmVpi.VpiReg,
        UhdmVpi.VpiNet, UhdmVpi.VpiProcess, UhdmVpi.VpiContAssign, UhdmVpi.VpiParamAssign, UhdmVpi.VpiStmt,
        UhdmVpi.VpiOperand, UhdmVpi.VpiArgument, UhdmVpi.VpiExpr, UhdmVpi.VpiCaseItem, UhdmVpi.VpiGenScopeArray,
        UhdmVpi.VpiGenScope, UhdmVpi.VpiGenStmt
    ];

    private void CollectOriginalExpressions(UhdmNode? h, int depth = 0)
    {
        if (h is null || !originalVisited.Add(h)) return;
        Need(depth < 128 && originalVisited.Count <= 100_000, "original syntax traversal budget exceeded");

        if (h.VpiType == UhdmVpi.VpiHierPath)
        {
            foreach (var element in h.GetFieldList("Path_elems"))
                CollectOriginalExpressions(element, depth + 1);
        }

        if (h.VpiType is UhdmVpi.VpiOperation or UhdmVpi.VpiRefObj or UhdmVpi.VpiSysFuncCall or UhdmVpi.VpiConstant)
        {
            string key = ExpressionLocation(h);
            if (key.Length > 0)
            {
                if (!originalExpressions.TryGetValue(key, out var existing))
                {
                    originalExpressions[key] = h;
                }
                else if (!ReferenceEquals(existing, h))
                {
                    bool same = false;
                    if (h.VpiType == UhdmVpi.VpiConstant && existing is not null && existing.VpiType == UhdmVpi.VpiConstant)
                    {
                        same = h.GetInt(UhdmVpi.VpiSize) == existing.GetInt(UhdmVpi.VpiSize)
                            && h.GetFieldString("VpiValue") == existing.GetFieldString("VpiValue")
                            && h.GetInt(UhdmVpi.VpiConstType) == existing.GetInt(UhdmVpi.VpiConstType)
                            && h.GetString(UhdmVpi.VpiDecompile) == existing.GetString(UhdmVpi.VpiDecompile)
                            && h.GetInt(UhdmVpi.VpiSigned) == existing.GetInt(UhdmVpi.VpiSigned);
                    }
                    if (!same) originalExpressions[key] = null;
                }
            }
        }

        foreach (int rel in OriginalSingleRelations)
            CollectOriginalExpressions(h.One(rel), depth + 1);
        foreach (int rel in OriginalManyRelations)
            foreach (var child in h.Many(rel))
                CollectOriginalExpressions(child, depth + 1);
    }

    private UhdmNode? OriginalExpression(UhdmNode? h)
    {
        if (TypeOf(h) != UhdmVpi.VpiConstant) return null;
        string key = ExpressionLocation(h);
        if (!originalExpressions.TryGetValue(key, out var found)) return null;
        Need(found is not null, "ambiguous original expression source span");
        return ReferenceEquals(found, h) ? null : found;
    }
}
