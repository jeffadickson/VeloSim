using System.Numerics;
using System.Text;
using System.Text.Json;

namespace VeloSim.Uhdm;

/// <summary>
/// Pure .NET 11 port of <c>native/uhdm/normalize.cpp</c>.
/// Lowers a single <c>always_ff @(posedge clk)</c> reset/increment counter in UHDM into
/// <c>velosim.uhdm.normalized.v1</c> JSON consumed by <see cref="NormalizedUhdm.Lower"/>.
/// </summary>
public static class UhdmNormalizer
{
    public static string NormalizeFile(string uhdmPath, string top)
    {
        var db = UhdmDatabase.Load(uhdmPath);
        return Normalize(db, top);
    }

    public static void NormalizeFileTo(string uhdmPath, string outputPath, string top)
    {
        string json = NormalizeFile(uhdmPath, top);
        if (Path.GetDirectoryName(Path.GetFullPath(outputPath)) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);
        File.WriteAllText(outputPath, json, new UTF8Encoding(false));
    }

    public static string Normalize(UhdmDatabase database, string top)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (string.IsNullOrWhiteSpace(top))
            throw new InvalidOperationException("top not found");

        var designs = database.Designs;
        Check(designs.Count == 1, "expected one UHDM design");
        var design = designs[0];
        Check(design.VpiType == UhdmVpi.VpiDesign, "not a UHDM design");

        var modules = design.Many(UhdmVpi.UhdmtopModules);
        Check(modules.Count == 1, "expected exactly one top module");

        UhdmNode? mod = null;
        string workTop = "work@" + top;
        foreach (var h in modules)
        {
            if (h.Name == top || h.Name == workTop)
            {
                Check(mod is null, "duplicate top");
                mod = h;
            }
        }
        Check(mod is not null, "top not found");
        Check(mod!.Many(UhdmVpi.VpiModule).Count == 0, "nested modules unsupported");

        var procs = mod.Many(UhdmVpi.VpiProcess);
        Check(procs.Count == 1, "expected one process");
        Check(mod.Many(UhdmVpi.VpiContAssign).Count == 0, "continuous assignments unsupported");

        var proc = ParseProcess(procs[0]);
        var widths = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int[] relations = [UhdmVpi.VpiPort, UhdmVpi.VpiNet, UhdmVpi.VpiReg, UhdmVpi.VpiVariables];

        foreach (int relation in relations)
        {
            foreach (var h in mod.Many(relation))
            {
                string n = h.Name;
                if (n.Length == 0) continue;

                UhdmNode signal = h;
                if (relation == UhdmVpi.VpiPort)
                {
                    var connection = h.One(UhdmVpi.VpiLowConn);
                    var actual = connection?.One(UhdmVpi.VpiActual);
                    Check(actual is not null, "port has no resolved signal: " + n);
                    signal = actual!;
                }

                int bits = EvaluateSignalWidth(signal, mod);
                Check(bits is >= 1 and <= 64, "signal width outside 1..64: " + n);
                if (widths.TryGetValue(n, out int existing))
                    Check(existing == bits, "conflicting widths");
                else
                    widths[n] = bits;
            }
        }

        foreach (string n in new[] { proc.Clk, proc.Rst, proc.Dst })
            Check(widths.ContainsKey(n), "process signal not declared: " + n);
        Check(widths[proc.Clk] == 1 && widths[proc.Rst] == 1, "clock and reset must be scalar");
        Check(widths.Count == 3, "extra signals not yet supported");

        return EmitNormalizedJson(widths, proc);
    }

    private readonly record struct Proc(string Clk, string Rst, string Dst);
    private readonly record struct Assign(string Target, UhdmNode Rhs);

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    private static UhdmNode Sole(IReadOnlyList<UhdmNode> items, string what)
    {
        Check(items.Count == 1, "expected exactly one " + what);
        return items[0];
    }

    private static UhdmNode? Unbox(UhdmNode? stmt)
    {
        for (int depth = 0; depth < 4 && stmt is not null; depth++)
        {
            if (stmt.VpiType != UhdmVpi.VpiBegin && stmt.VpiType != UhdmVpi.VpiNamedBegin)
                break;
            stmt = Sole(stmt.Many(UhdmVpi.VpiStmt), "statement inside begin");
        }
        return stmt;
    }

    private static string Reference(UhdmNode? expr)
    {
        Check(expr is not null, "missing reference");
        string s = expr!.Name;
        Check(s.Length > 0, "expression is not a named signal reference");
        Check(expr.VpiType != UhdmVpi.VpiOperation && expr.VpiType != UhdmVpi.VpiConstant,
            "expression is not a simple reference");
        return s;
    }

    private static string ExtractConstantBits(UhdmNode expr, string missingMessage)
    {
        Check(expr.VpiType == UhdmVpi.VpiConstant, missingMessage);
        string raw = expr.GetFieldString("VpiValue");
        Check(raw.Length > 0, "constant value unavailable");
        int colon = raw.IndexOf(':');
        Check(colon > 0, "constant value unavailable");
        string tag = raw[..colon];
        string payload = raw[(colon + 1)..].Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
        Check(payload.Length > 0, "empty constant");

        if (tag is "BIN" or "SCAL")
            return payload;
        if (tag == "HEX")
        {
            var sb = new StringBuilder(payload.Length * 4);
            foreach (char c in payload)
            {
                if (c is 'x' or 'z') { sb.Append(c, 4); continue; }
                int d = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : -1;
                Check(d >= 0, "constant value unavailable");
                for (int b = 3; b >= 0; b--) sb.Append(((d >> b) & 1) != 0 ? '1' : '0');
            }
            return sb.ToString();
        }
        if (tag == "OCT")
        {
            var sb = new StringBuilder(payload.Length * 3);
            foreach (char c in payload)
            {
                if (c is 'x' or 'z') { sb.Append(c, 3); continue; }
                int d = c is >= '0' and <= '7' ? c - '0' : -1;
                Check(d >= 0, "constant value unavailable");
                for (int b = 2; b >= 0; b--) sb.Append(((d >> b) & 1) != 0 ? '1' : '0');
            }
            return sb.ToString();
        }
        if (tag is "INT" or "UINT" or "DEC")
        {
            if (payload is "x" or "z") return payload;
            Check(BigInteger.TryParse(payload, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var num),
                "constant value unavailable");
            int size = (int)expr.GetInt(UhdmVpi.VpiSize);
            if (size <= 0) size = 1;
            if (num == 0) return new string('0', size);
            var sb = new StringBuilder(size);
            for (int b = size - 1; b >= 0; b--)
                sb.Append(((num >> b) & 1) != 0 ? '1' : '0');
            return sb.ToString();
        }
        throw new InvalidOperationException("constant value unavailable");
    }

    private static bool IsZeroConst(UhdmNode expr)
    {
        string bits = ExtractConstantBits(expr, "expected zero constant");
        foreach (char c in bits)
            if (c != '0') return false;
        return true;
    }

    private static bool IsOneConst(UhdmNode expr)
    {
        string bits = ExtractConstantBits(expr, "expected increment constant");
        if (bits[^1] != '1') return false;
        for (int i = 0; i + 1 < bits.Length; i++)
            if (bits[i] != '0') return false;
        return true;
    }

    private static Assign ParseNba(UhdmNode? stmt)
    {
        stmt = Unbox(stmt);
        Check(stmt is not null && stmt.VpiType == UhdmVpi.VpiAssignment, "expected procedural assignment");
        Check(stmt!.GetInt(UhdmVpi.VpiBlocking) == 0, "blocking assignment unsupported");
        var lhs = stmt.One(UhdmVpi.VpiLhs);
        var rhs = stmt.One(UhdmVpi.VpiRhs);
        Check(lhs is not null && rhs is not null, "assignment missing lhs/rhs");
        return new Assign(Reference(lhs), rhs!);
    }

    private static Proc ParseProcess(UhdmNode h)
    {
        Check(h.VpiType == UhdmVpi.VpiAlways, "expected always process");
        Check(h.GetInt(UhdmVpi.VpiAlwaysType) == UhdmVpi.VpiAlwaysFF, "only always_ff supported");
        var ev = Unbox(h.One(UhdmVpi.VpiStmt));
        Check(ev is not null && ev.VpiType == UhdmVpi.VpiEventControl, "expected event control");
        var edge = ev!.One(UhdmVpi.VpiCondition);
        Check(edge is not null && edge.VpiType == UhdmVpi.VpiOperation && edge.GetInt(UhdmVpi.VpiOpType) == UhdmVpi.VpiPosedgeOp,
            "only posedge supported");
        string clk = Reference(Sole(edge!.Many(UhdmVpi.VpiOperand), "posedge operand"));

        var body = Unbox(ev.One(UhdmVpi.VpiStmt));
        Check(body is not null && body.VpiType == UhdmVpi.VpiIfElse, "expected if/else");
        var cond = body!.One(UhdmVpi.VpiCondition);
        Check(cond is not null && cond.VpiType == UhdmVpi.VpiOperation && cond.GetInt(UhdmVpi.VpiOpType) == UhdmVpi.VpiNotOp,
            "expected !rst_n");
        string rst = Reference(Sole(cond!.Many(UhdmVpi.VpiOperand), "reset operand"));

        var yes = ParseNba(body.One(UhdmVpi.VpiStmt));
        var no = ParseNba(body.One(UhdmVpi.VpiElseStmt));
        Check(yes.Target == no.Target, "branch destinations differ");
        Check(IsZeroConst(yes.Rhs), "expected zero reset assignment");
        Check(no.Rhs.VpiType == UhdmVpi.VpiOperation && no.Rhs.GetInt(UhdmVpi.VpiOpType) == UhdmVpi.VpiAddOp,
            "expected addition");
        var operands = no.Rhs.Many(UhdmVpi.VpiOperand);
        Check(operands.Count == 2, "expected binary addition");
        Check(Reference(operands[0]) == yes.Target && IsOneConst(operands[1]), "expected count + 1");
        return new Proc(clk, rst, yes.Target);
    }

    internal static int EvaluateSignalWidth(UhdmNode signal, UhdmNode mod, int depth = 0)
    {
        if (depth > 16) return 0;

        // Follow Actual if reference.
        if (signal.VpiType is UhdmVpi.VpiRefObj or UhdmVpi.VpiRefVar)
        {
            var actual = signal.One(UhdmVpi.VpiActual);
            if (actual is not null && !ReferenceEquals(actual, signal))
                return EvaluateSignalWidth(actual, mod, depth + 1);
        }

        // Check direct ranges on the signal first (e.g. logic_net / reg / logic_var).
        var directRanges = signal.Many(UhdmVpi.VpiRange);
        if (directRanges.Count > 0)
            return EvaluateRangesProduct(directRanges, mod, depth + 1);

        // Check typespec.
        var tsRef = signal.One(UhdmVpi.VpiTypespec);
        var ts = UnwrapTypespec(tsRef, depth + 1);
        if (ts is not null)
        {
            int atom = ts.VpiType switch
            {
                UhdmVpi.VpiByteTypespec => 8,
                UhdmVpi.VpiShortIntTypespec => 16,
                UhdmVpi.VpiIntTypespec or UhdmVpi.VpiIntegerTypespec => 32,
                UhdmVpi.VpiLongIntTypespec or UhdmVpi.VpiTimeTypespec => 64,
                _ => 0
            };
            if (atom > 0) return atom;

            var tsRanges = ts.Many(UhdmVpi.VpiRange);
            if (tsRanges.Count > 0)
                return EvaluateRangesProduct(tsRanges, mod, depth + 1);
            if (ts.VpiType is UhdmVpi.VpiLogicTypespec or UhdmVpi.VpiBitTypespec)
                return 1;
        }

        // Fallback to VpiSize if populated, else scalar 1 for logic/reg/net/bit.
        long vpiSize = signal.GetInt(UhdmVpi.VpiSize);
        if (vpiSize > 0) return (int)Math.Min(vpiSize, int.MaxValue);
        if (signal.VpiType is UhdmVpi.VpiNet or UhdmVpi.VpiReg or UhdmVpi.VpiLogicVar or UhdmVpi.VpiBitVar)
            return 1;
        return 0;
    }

    private static UhdmNode? UnwrapTypespec(UhdmNode? spec, int depth)
    {
        if (spec is null || depth > 16) return null;
        if (spec.VpiType == UhdmVpi.VpiRefTypespec)
            return UnwrapTypespec(spec.One(UhdmVpi.VpiActual), depth + 1);
        if (spec.VpiType == UhdmVpi.VpiEnumTypespec)
            return UnwrapTypespec(spec.One(UhdmVpi.VpiBaseTypespec), depth + 1);
        return spec;
    }

    private static int EvaluateRangesProduct(IReadOnlyList<UhdmNode> ranges, UhdmNode mod, int depth)
    {
        long product = 1;
        foreach (var range in ranges)
        {
            if (!TryEvalConstInt(range.One(UhdmVpi.VpiLeftRange), mod, depth + 1, out long left) ||
                !TryEvalConstInt(range.One(UhdmVpi.VpiRightRange), mod, depth + 1, out long right))
                return 0;
            long span = Math.Abs(left - right) + 1;
            if (span <= 0 || product * span > 65536) return 0;
            product *= span;
        }
        return (int)product;
    }

    private static bool TryEvalConstInt(UhdmNode? expr, UhdmNode mod, int depth, out long value)
    {
        value = 0;
        if (expr is null || depth > 32) return false;
        if (expr.VpiType == UhdmVpi.VpiConstant)
        {
            string raw = expr.GetFieldString("VpiValue");
            int colon = raw.IndexOf(':');
            if (colon <= 0) return false;
            string tag = raw[..colon];
            string payload = raw[(colon + 1)..].Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
            if (tag is "INT" or "UINT" or "DEC")
                return long.TryParse(payload, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out value);
            if (tag is "BIN" or "SCAL")
            {
                if (payload.Any(c => c is not ('0' or '1'))) return false;
                value = Convert.ToInt64(payload, 2);
                return true;
            }
            if (tag == "HEX")
            {
                if (payload.Any(c => c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))) return false;
                value = Convert.ToInt64(payload, 16);
                return true;
            }
            return false;
        }
        if (expr.VpiType == UhdmVpi.VpiOperation)
        {
            var ops = expr.Many(UhdmVpi.VpiOperand);
            long op = expr.GetInt(UhdmVpi.VpiOpType);
            if (ops.Count == 1 && TryEvalConstInt(ops[0], mod, depth + 1, out long a))
            {
                if (op == UhdmVpi.VpiMinusOp) { value = -a; return true; }
                if (op == UhdmVpi.VpiPlusOp) { value = a; return true; }
            }
            if (ops.Count == 2 && TryEvalConstInt(ops[0], mod, depth + 1, out long l) && TryEvalConstInt(ops[1], mod, depth + 1, out long r))
            {
                switch (op)
                {
                    case UhdmVpi.VpiAddOp: value = l + r; return true;
                    case UhdmVpi.VpiSubOp: value = l - r; return true;
                    case UhdmVpi.VpiMultOp: value = l * r; return true;
                    case UhdmVpi.VpiDivOp when r != 0: value = l / r; return true;
                }
            }
            return false;
        }
        if (expr.VpiType is UhdmVpi.VpiRefObj or UhdmVpi.VpiParameter)
        {
            var param = expr.VpiType == UhdmVpi.VpiParameter ? expr : expr.One(UhdmVpi.VpiActual);
            if (param is not null && param.VpiType == UhdmVpi.VpiParameter)
            {
                foreach (var pa in mod.Many(UhdmVpi.VpiParamAssign))
                {
                    var lhs = pa.One(UhdmVpi.VpiLhs);
                    if (ReferenceEquals(lhs, param) || (lhs is not null && lhs.Name == param.Name))
                        return TryEvalConstInt(pa.One(UhdmVpi.VpiRhs), mod, depth + 1, out value);
                }
                string pv = param.GetFieldString("VpiValue");
                if (pv.Length > 0)
                {
                    int c = pv.IndexOf(':');
                    if (c > 0 && long.TryParse(pv[(c + 1)..], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out value))
                        return true;
                }
            }
        }
        return false;
    }

    private static string EmitNormalizedJson(SortedDictionary<string, int> widths, Proc p)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("format", "velosim.uhdm.normalized.v1");
            w.WriteStartArray("signals");
            foreach (var (n, width) in widths)
            {
                w.WriteStartObject();
                w.WriteString("name", n);
                w.WriteNumber("width", width);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("processes");
            w.WriteStartObject();
            w.WriteString("kind", "always_ff");

            w.WriteStartObject("event");
            w.WriteString("edge", "posedge");
            w.WriteString("signal", p.Clk);
            w.WriteEndObject();

            w.WriteStartObject("body");
            w.WriteString("kind", "if");

            w.WriteStartObject("condition");
            w.WriteString("kind", "not");
            w.WriteStartObject("operand");
            w.WriteString("kind", "ref");
            w.WriteString("name", p.Rst);
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteStartObject("then");
            w.WriteString("kind", "nba");
            w.WriteString("target", p.Dst);
            w.WriteStartObject("value");
            w.WriteString("kind", "const");
            w.WriteNumber("value", 0);
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteStartObject("else");
            w.WriteString("kind", "nba");
            w.WriteString("target", p.Dst);
            w.WriteStartObject("value");
            w.WriteString("kind", "add");
            w.WriteStartObject("lhs");
            w.WriteString("kind", "ref");
            w.WriteString("name", p.Dst);
            w.WriteEndObject();
            w.WriteStartObject("rhs");
            w.WriteString("kind", "const");
            w.WriteNumber("value", 1);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();

            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }
}
