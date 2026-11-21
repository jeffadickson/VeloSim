using System.Text;
using System.Text.Json;
namespace VeloSim;

/// <summary>Target language emitted by RtlExternalSdk / rtl-export.</summary>
public enum RtlTargetLanguage
{
    Cpp = 0,
    CSharp = 1,
    Rust = 2
}

public sealed partial class RtlDesign
{
    internal bool CanUsePortable64(RtlStateMode stateMode)
    {
        if (Timed) return false;
        if (CollectDpiCalls().Count > 0 || Covergroups.Count > 0) return false;
        bool hasBlocking = Statements(Body).Any(e => Kind(e) is "blocking" or "allocate" or "delete" or "queue_push" or "queue_pop" or "assoc_traverse");
        bool hasContainers = Memories.Any(m => m.Associative || m.Queue || m.Dynamic);
        bool useFastEvent = EventDriven && !hasBlocking && !hasContainers;
        if (!CombinationalOnly && !useFastEvent) return false;
        if (Statements(Initialization).Any(st => Kind(st) is not "block")) return false;
        if (Signals.Any(s => s.Width > 64)) return false;
        if (Memories.Any(m => m.Width > 64 || m.Associative || m.Queue || m.Dynamic || m.Dimensions is not null)) return false;
        if (SettledSteps.Any(step => !(Kind(step) == "blocking" && Kind(step.GetProperty("target")) == "ref"))) return false;
        return VerificationFits64() && SettledSteps.Append(Body).SelectMany(Statements).All(StatementFits64);
    }

    internal IReadOnlyDictionary<string, string> CSharpPortNames()
    {
        var reserved = (
            "abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while Eval Final Time TimeInc OpenVcd OpenVcdGz DumpVcd CloseVcd SetPlusargs AddPlusarg TestPlusargs ValuePlusargs SetRandomSeed RandomSeed NextUrandom NextUrandomRange NextRandom ForceSignal ReleaseSignal IsForced Fatal ExitCode PropertyPasses PropertyFailures PropertyVacuous SaveCheckpoint RestoreCheckpoint W64 W64Ops W2Ops VcdWriter PlusargStore SplitMix64Rng CheckpointIO"
        ).Split(' ').ToHashSet(StringComparer.Ordinal);
        var used = Signals.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var signal in Signals.Where(s => s.Direction != "internal"))
        {
            string name = signal.Name;
            if (reserved.Contains(name) || name.StartsWith('_'))
            {
                name = "port_" + name.TrimStart('_');
                while (used.Contains(name) || reserved.Contains(name)) name = "port_" + name;
            }
            used.Add(name);
            result.Add(signal.Name, name);
        }
        return result;
    }

    private static string EmitCsConst64(string bits)
    {
        int w = bits.Length;
        ulong v = 0, x = 0, z = 0;
        foreach (char c in bits)
        {
            v <<= 1; x <<= 1; z <<= 1;
            if (c == '1') v |= 1;
            else if (c == 'x') x |= 1;
            else if (c == 'z') z |= 1;
        }
        return (x | z) == 0
            ? $"new W64(0x{v:x}UL,0UL,0UL,{w})"
            : $"new W64(0x{v:x}UL,0x{x:x}UL,0x{z:x}UL,{w})";
    }

    private static string EmitCsConst2(string bits)
    {
        ulong v = 0;
        foreach (char c in bits)
        {
            v <<= 1;
            if (c == '1') v |= 1;
        }
        return $"0x{v:x}UL";
    }

    private string CsMemIdx(JsonElement access, int id, bool twoState)
    {
        string idx = twoState ? CsExpr2(access.GetProperty("index")) : CsExpr64(access.GetProperty("index"));
        if (Memories[id].LowerBound == 0) return idx;
        return twoState ? $"W2Ops.MemIdx({idx},{Memories[id].LowerBound})" : $"W64Ops.MemIdx({idx},{Memories[id].LowerBound})";
    }

    private string CsExpr64(JsonElement e, int context = 0, bool? forceSigned = null)
    {
        string kind = Kind(e);
        bool signed = forceSigned ?? ExpressionSigned(e);
        string Extend(string val) => signed && context > ExpressionWidth(e) ? $"W64Ops.SResize({val},{context})" : val;
        if (kind == "ref") return Extend($"_s{SignalId(Name(e))}");
        if (kind == "const") return Extend(EmitCsConst64(e.GetProperty("bits").GetString()!));
        if (kind == "index")
        {
            int id = MemoryId(Name(e));
            return Extend($"W64Ops.Read(_m{id},{CsMemIdx(e, id, false)},{Memories[id].Width})");
        }
        if (kind == "bitselect") return $"W64Ops.SelBit({CsExpr64(e.GetProperty("value"))},{CsExpr64(e.GetProperty("index"))})";
        if (kind == "partselect") return $"W64Ops.SelPart({CsExpr64(e.GetProperty("value"))},{e.GetProperty("left").GetInt32()},{e.GetProperty("right").GetInt32()})";
        if (kind == "indexedselect") return $"W64Ops.SelIdx({CsExpr64(e.GetProperty("value"))},{CsExpr64(e.GetProperty("base"))},{e.GetProperty("width").GetInt32()},{(e.GetProperty("descending").GetBoolean() ? "true" : "false")})";
        if (kind == "test_plusargs") return Extend($"new W64(TestPlusargs({CppStringLiteral(e.GetProperty("prefix").GetString()!)}),0UL,0UL,32)");
        if (kind == "urandom") return Extend("new W64(NextUrandom(),0UL,0UL,32)");
        if (kind == "random") return Extend("new W64((uint)NextRandom(),0UL,0UL,32)");
        if (kind == "urandom_range")
        {
            string maxExpr = $"(uint)({CsExpr64(e.GetProperty("max"))}).V";
            string minExpr = e.TryGetProperty("min", out var minEl) ? $"(uint)({CsExpr64(minEl)}).V" : "0u";
            return Extend($"new W64(NextUrandomRange({maxExpr},{minExpr}),0UL,0UL,32)");
        }
        if (kind == "resolve")
        {
            int nk = ParseNetKind(e.GetProperty("net").GetString()!);
            int rw = Math.Max(context, ExpressionWidth(e));
            string drivers = string.Join(", ", e.GetProperty("args").EnumerateArray().Select(d => CsExpr64(d, rw)));
            return Extend($"W64Ops.ResolveNet({nk}, [{drivers}], {rw})");
        }
        if (kind == "inside")
        {
            var valEl = e.GetProperty("value");
            bool vSigned = ExpressionSigned(valEl);
            int vw = ExpressionWidth(valEl);
            var parts = new List<string>();
            foreach (var it in e.GetProperty("items").EnumerateArray())
            {
                if (it.TryGetProperty("lo", out var loEl) && it.TryGetProperty("hi", out var hiEl))
                {
                    int cw = Math.Max(vw, Math.Max(ExpressionWidth(loEl), ExpressionWidth(hiEl)));
                    bool cs = vSigned && ExpressionSigned(loEl) && ExpressionSigned(hiEl);
                    string cmp = cs ? "W64Ops.SCmp" : "W64Ops.Cmp";
                    parts.Add($"W64Ops.LAnd({cmp}({CsExpr64(valEl, cw, cs)},{CsExpr64(loEl, cw, cs)},3),{cmp}({CsExpr64(valEl, cw, cs)},{CsExpr64(hiEl, cw, cs)},1))");
                }
                else
                {
                    int cw = Math.Max(vw, ExpressionWidth(it));
                    bool cs = vSigned && ExpressionSigned(it);
                    parts.Add($"W64Ops.WildEq({CsExpr64(valEl, cw, cs)},{CsExpr64(it, cw, cs)})");
                }
            }
            string acc = parts[0];
            for (int i = 1; i < parts.Count; ++i) acc = $"W64Ops.LOr({acc},{parts[i]})";
            return acc;
        }
        if (kind == "past")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            int ticks = e.TryGetProperty("ticks", out var tEl) ? tEl.GetInt32() : 1;
            return Extend($"_past_{sid}[{ticks - 1}]");
        }
        if (kind == "rose")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            return $"new W64(W64Ops.Edge(W64Ops.SelBit(_past_{sid}[0],new W64(0UL,0UL,0UL,1)),W64Ops.SelBit(_s{sid},new W64(0UL,0UL,0UL,1)),true)?1UL:0UL,0UL,0UL,1)";
        }
        if (kind == "fell")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            return $"new W64(W64Ops.Edge(W64Ops.SelBit(_past_{sid}[0],new W64(0UL,0UL,0UL,1)),W64Ops.SelBit(_s{sid},new W64(0UL,0UL,0UL,1)),false)?1UL:0UL,0UL,0UL,1)";
        }
        if (kind == "stable")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            return $"new W64(W64Ops.Exact(_s{sid},_past_{sid}[0])?1UL:0UL,0UL,0UL,1)";
        }
        var args = e.GetProperty("args").EnumerateArray().ToArray();
        int width = Math.Max(context, ExpressionWidth(e));
        return kind switch
        {
            "castsigned" or "castunsigned" => Extend(CsExpr64(args[0])),
            "countones" => Extend($"W64Ops.CountOnes({CsExpr64(args[0])})"),
            "onehot" => $"W64Ops.OneHot({CsExpr64(args[0])})",
            "onehot0" => $"W64Ops.OneHot0({CsExpr64(args[0])})",
            "isunknown" => $"W64Ops.IsUnknown({CsExpr64(args[0])})",
            "clog2" => Extend($"W64Ops.CLog2({CsExpr64(args[0])})"),
            "add" or "sub" => $"W64Ops.Arith({CsExpr64(args[0], width, signed)},{CsExpr64(args[1], width, signed)},{width},{(kind == "sub" ? "true" : "false")})",
            "bitnot" => $"W64Ops.BNot({CsExpr64(args[0], width, signed)},{width})",
            "neg" => $"W64Ops.Arith(new W64(0UL,0UL,0UL,{width}),{CsExpr64(args[0], width, signed)},{width},true)",
            "pos" => $"W64Ops.Resize({CsExpr64(args[0], width, signed)},{width})",
            "mul" or "div" or "mod" => $"W64Ops.{(signed ? "SProduct" : "Product")}({CsExpr64(args[0], width, signed)},{CsExpr64(args[1], width, signed)},{width},{(kind == "mul" ? 0 : kind == "div" ? 1 : 2)})",
            "cond" => $"W64Ops.Cond({CsExpr64(args[0])},{CsExpr64(args[1], width, signed)},{CsExpr64(args[2], width, signed)},{width})",
            "ashr" when signed => $"W64Ops.SShift({CsExpr64(args[0], width, true)},{CsExpr64(args[1])},{width})",
            "shl" or "shr" or "ashr" => $"W64Ops.Shift({CsExpr64(args[0], width, signed)},{CsExpr64(args[1])},{width},{(kind == "shl" ? "false" : "true")})",
            "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor" => $"W64Ops.Red({CsExpr64(args[0])},{(kind == "redand" ? 0 : kind == "rednand" ? 1 : kind == "redor" ? 2 : kind == "rednor" ? 3 : kind == "redxor" ? 4 : 5)})",
            "bitxnor" => $"W64Ops.BNot(W64Ops.BBin({CsExpr64(args[0], width, signed)},{CsExpr64(args[1], width, signed)},{width},2),{width})",
            "bitand" or "bitor" or "bitxor" => $"W64Ops.BBin({CsExpr64(args[0], width, signed)},{CsExpr64(args[1], width, signed)},{width},{(kind == "bitand" ? 0 : kind == "bitor" ? 1 : 2)})",
            "not" => $"W64Ops.LNot({CsExpr64(args[0])})",
            "and" => $"W64Ops.LAnd({CsExpr64(args[0])},{CsExpr64(args[1])})",
            "or" => $"W64Ops.LOr({CsExpr64(args[0])},{CsExpr64(args[1])})",
            "eq" or "neq" => (kind == "neq" ? "W64Ops.LNot(" : "") + $"W64Ops.Eq({CsExpr64(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{CsExpr64(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))})" + (kind == "neq" ? ")" : ""),
            "wildeq" or "wildneq" => $"W64Ops.{(kind == "wildneq" ? "WildNeq" : "WildEq")}({CsExpr64(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{CsExpr64(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))})",
            "caseeq" or "caseneq" => $"new W64({(kind == "caseneq" ? "!" : "")}W64Ops.Exact({CsExpr64(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{CsExpr64(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))})?1UL:0UL,0UL,0UL,1)",
            "lt" or "le" or "gt" or "ge" => $"W64Ops.{(args.All(ExpressionSigned) ? "SCmp" : "Cmp")}({CsExpr64(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{CsExpr64(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{(kind == "lt" ? 0 : kind == "le" ? 1 : kind == "gt" ? 2 : 3)})",
            "concat" => $"W64Ops.Concat({CsExpr64(args[0])},{CsExpr64(args[1])})",
            _ => throw new IrValidationException("Unsupported C# expression.")
        };
    }

    private string CsExpr2(JsonElement e, int context = 0, bool? forceSigned = null)
    {
        string kind = Kind(e);
        int ew = ExpressionWidth(e);
        bool signed = forceSigned ?? ExpressionSigned(e);
        string Extend(string val) => signed && context > ew ? $"W2Ops.SResize({val},{ew},{context})" : val;
        if (kind == "ref") return Extend($"_s{SignalId(Name(e))}");
        if (kind == "const") return Extend(EmitCsConst2(e.GetProperty("bits").GetString()!));
        if (kind == "index")
        {
            int id = MemoryId(Name(e));
            return Extend($"W2Ops.Read(_m{id},{CsMemIdx(e, id, true)})");
        }
        if (kind == "bitselect")
        {
            var valEl = e.GetProperty("value");
            return $"W2Ops.SelBit({CsExpr2(valEl)},{ExpressionWidth(valEl)},{CsExpr2(e.GetProperty("index"))})";
        }
        if (kind == "partselect")
        {
            var valEl = e.GetProperty("value");
            return $"W2Ops.SelPart({CsExpr2(valEl)},{ExpressionWidth(valEl)},{e.GetProperty("left").GetInt32()},{e.GetProperty("right").GetInt32()})";
        }
        if (kind == "indexedselect")
        {
            var valEl = e.GetProperty("value");
            return $"W2Ops.SelIdx({CsExpr2(valEl)},{ExpressionWidth(valEl)},{CsExpr2(e.GetProperty("base"))},{e.GetProperty("width").GetInt32()},{(e.GetProperty("descending").GetBoolean() ? "true" : "false")})";
        }
        if (kind == "test_plusargs") return Extend($"(ulong)TestPlusargs({CppStringLiteral(e.GetProperty("prefix").GetString()!)})");
        if (kind == "urandom") return Extend("(ulong)NextUrandom()");
        if (kind == "random") return Extend("(ulong)(uint)NextRandom()");
        if (kind == "urandom_range")
        {
            string maxExpr = $"(uint)({CsExpr2(e.GetProperty("max"))})";
            string minExpr = e.TryGetProperty("min", out var minEl) ? $"(uint)({CsExpr2(minEl)})" : "0u";
            return Extend($"(ulong)NextUrandomRange({maxExpr},{minExpr})");
        }
        if (kind == "resolve")
        {
            int nk = ParseNetKind(e.GetProperty("net").GetString()!);
            int rw = Math.Max(context, ew);
            string drivers = string.Join(", ", e.GetProperty("args").EnumerateArray().Select(d => CsExpr2(d, rw)));
            return Extend($"W2Ops.ResolveNet({nk}, [{drivers}], {rw})");
        }
        if (kind == "inside")
        {
            var valEl = e.GetProperty("value");
            bool vSigned = ExpressionSigned(valEl);
            int vw = ExpressionWidth(valEl);
            var parts = new List<string>();
            foreach (var it in e.GetProperty("items").EnumerateArray())
            {
                if (it.TryGetProperty("lo", out var loEl) && it.TryGetProperty("hi", out var hiEl))
                {
                    int cw = Math.Max(vw, Math.Max(ExpressionWidth(loEl), ExpressionWidth(hiEl)));
                    bool cs = vSigned && ExpressionSigned(loEl) && ExpressionSigned(hiEl);
                    string loC = cs
                        ? $"W2Ops.SCmp({CsExpr2(valEl, cw, true)},{CsExpr2(loEl, cw, true)},{cw},3)"
                        : $"W2Ops.Cmp({CsExpr2(valEl, cw, false)},{CsExpr2(loEl, cw, false)},3)";
                    string hiC = cs
                        ? $"W2Ops.SCmp({CsExpr2(valEl, cw, true)},{CsExpr2(hiEl, cw, true)},{cw},1)"
                        : $"W2Ops.Cmp({CsExpr2(valEl, cw, false)},{CsExpr2(hiEl, cw, false)},1)";
                    parts.Add($"W2Ops.LAnd({loC},{hiC})");
                }
                else
                {
                    int cw = Math.Max(vw, ExpressionWidth(it));
                    bool cs = vSigned && ExpressionSigned(it);
                    parts.Add($"W2Ops.Eq({CsExpr2(valEl, cw, cs)},{CsExpr2(it, cw, cs)})");
                }
            }
            string acc = parts[0];
            for (int i = 1; i < parts.Count; ++i) acc = $"W2Ops.LOr({acc},{parts[i]})";
            return acc;
        }
        if (kind == "past")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            int ticks = e.TryGetProperty("ticks", out var tEl) ? tEl.GetInt32() : 1;
            return Extend($"_past_{sid}[{ticks - 1}]");
        }
        if (kind == "rose")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            return $"(W2Ops.Edge(_past_{sid}[0]&1UL,_s{sid}&1UL,true)?1UL:0UL)";
        }
        if (kind == "fell")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            return $"(W2Ops.Edge(_past_{sid}[0]&1UL,_s{sid}&1UL,false)?1UL:0UL)";
        }
        if (kind == "stable")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            return $"(_s{sid}==_past_{sid}[0]?1UL:0UL)";
        }
        var args = e.GetProperty("args").EnumerateArray().ToArray();
        int width = Math.Max(context, ew);
        return kind switch
        {
            "castsigned" or "castunsigned" => Extend(CsExpr2(args[0])),
            "countones" => Extend($"W2Ops.CountOnes({CsExpr2(args[0])},{ExpressionWidth(args[0])})"),
            "onehot" => $"W2Ops.OneHot({CsExpr2(args[0])},{ExpressionWidth(args[0])})",
            "onehot0" => $"W2Ops.OneHot0({CsExpr2(args[0])},{ExpressionWidth(args[0])})",
            "isunknown" => "0UL",
            "clog2" => Extend($"W2Ops.CLog2({CsExpr2(args[0])},{ExpressionWidth(args[0])})"),
            "add" or "sub" => $"W2Ops.Arith({CsExpr2(args[0], width, signed)},{CsExpr2(args[1], width, signed)},{width},{(kind == "sub" ? "true" : "false")})",
            "bitnot" => $"W2Ops.BNot({CsExpr2(args[0], width, signed)},{width})",
            "neg" => $"W2Ops.Arith(0UL,{CsExpr2(args[0], width, signed)},{width},true)",
            "pos" => $"W2Ops.Resize({CsExpr2(args[0], width, signed)},{width})",
            "mul" or "div" or "mod" => $"W2Ops.{(signed ? "SProduct" : "Product")}({CsExpr2(args[0], width, signed)},{CsExpr2(args[1], width, signed)},{width},{(kind == "mul" ? 0 : kind == "div" ? 1 : 2)})",
            "cond" => $"W2Ops.Cond({CsExpr2(args[0])},{CsExpr2(args[1], width, signed)},{CsExpr2(args[2], width, signed)},{width})",
            "ashr" when signed => $"W2Ops.SShift({CsExpr2(args[0], width, true)},{CsExpr2(args[1])},{width})",
            "shl" or "shr" or "ashr" => $"W2Ops.Shift({CsExpr2(args[0], width, signed)},{CsExpr2(args[1])},{width},{(kind == "shl" ? "false" : "true")})",
            "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor" => $"W2Ops.Red({CsExpr2(args[0])},{ExpressionWidth(args[0])},{(kind == "redand" ? 0 : kind == "rednand" ? 1 : kind == "redor" ? 2 : kind == "rednor" ? 3 : kind == "redxor" ? 4 : 5)})",
            "bitxnor" => $"W2Ops.BNot(W2Ops.BBin({CsExpr2(args[0], width, signed)},{CsExpr2(args[1], width, signed)},{width},2),{width})",
            "bitand" or "bitor" or "bitxor" => $"W2Ops.BBin({CsExpr2(args[0], width, signed)},{CsExpr2(args[1], width, signed)},{width},{(kind == "bitand" ? 0 : kind == "bitor" ? 1 : 2)})",
            "not" => $"W2Ops.LNot({CsExpr2(args[0])})",
            "and" => $"W2Ops.LAnd({CsExpr2(args[0])},{CsExpr2(args[1])})",
            "or" => $"W2Ops.LOr({CsExpr2(args[0])},{CsExpr2(args[1])})",
            "eq" or "neq" or "caseeq" or "caseneq" or "wildeq" or "wildneq" =>
                (kind is "neq" or "caseneq" or "wildneq" ? "W2Ops.LNot(" : "")
                + $"W2Ops.Eq({CsExpr2(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{CsExpr2(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))})"
                + (kind is "neq" or "caseneq" or "wildneq" ? ")" : ""),
            "lt" or "le" or "gt" or "ge" =>
                args.All(ExpressionSigned)
                    ? $"W2Ops.SCmp({CsExpr2(args[0], args.Max(ExpressionWidth), true)},{CsExpr2(args[1], args.Max(ExpressionWidth), true)},{args.Max(ExpressionWidth)},{(kind == "lt" ? 0 : kind == "le" ? 1 : kind == "gt" ? 2 : 3)})"
                    : $"W2Ops.Cmp({CsExpr2(args[0], args.Max(ExpressionWidth), false)},{CsExpr2(args[1], args.Max(ExpressionWidth), false)},{(kind == "lt" ? 0 : kind == "le" ? 1 : kind == "gt" ? 2 : 3)})",
            "concat" => $"W2Ops.Concat({CsExpr2(args[0])},{CsExpr2(args[1])},{ExpressionWidth(args[1])})",
            _ => throw new IrValidationException("Unsupported C# two-state expression.")
        };
    }

    private string CsStore(string val, RtlSignal signal, bool twoState, int exprWidth = -1)
    {
        if (twoState) return exprWidth == signal.Width ? val : $"W2Ops.Resize({val},{signal.Width})";
        return signal.TwoState
            ? $"W64Ops.TwoState(W64Ops.Resize({val},{signal.Width}))"
            : (exprWidth == signal.Width ? val : $"W64Ops.Resize({val},{signal.Width})");
    }

    private void CsWordWrite(StringBuilder code, JsonElement target, string value, bool twoState, int exprWidth = -1)
    {
        if (Kind(target) == "ref")
        {
            int id = SignalId(Name(target));
            code.AppendLine($"_next{id}={CsStore(value, Signals[id], twoState, exprWidth)};");
            return;
        }
        int sid = SignalId(Name(target.GetProperty("value"))), width = ExpressionWidth(target);
        bool desc = Kind(target) == "indexedselect" && target.GetProperty("descending").GetBoolean();
        if (twoState)
        {
            string basis = Kind(target) == "bitselect" ? CsExpr2(target.GetProperty("index"))
                : Kind(target) == "indexedselect" ? CsExpr2(target.GetProperty("base"))
                : $"{target.GetProperty("right").GetInt32()}UL";
            code.AppendLine($"_next{sid}=W2Ops.DepIdx(_next{sid},{Signals[sid].Width},{basis},{width},{(desc ? "true" : "false")},{value});");
        }
        else
        {
            string basis = Kind(target) == "bitselect" ? CsExpr64(target.GetProperty("index"))
                : Kind(target) == "indexedselect" ? CsExpr64(target.GetProperty("base"))
                : $"new W64({target.GetProperty("right").GetInt32()}UL,0UL,0UL,64)";
            string dep = $"W64Ops.DepIdx(_next{sid},{basis},{width},{(desc ? "true" : "false")},{value})";
            code.AppendLine($"_next{sid}={(Signals[sid].TwoState ? $"W64Ops.TwoState({dep})" : dep)};");
        }
    }

    private void CsStmt(StringBuilder code, JsonElement e, ref int unique, bool twoState, bool trackBudget)
    {
        if (trackBudget) code.AppendLine("W64Ops.Step(ref _budget);");
        switch (Kind(e))
        {
            case "while":
            case "do":
                string cond = twoState ? $"W2Ops.Yes({CsExpr2(e.GetProperty("condition"))})" : $"W64Ops.Yes({CsExpr64(e.GetProperty("condition"))})";
                if (Kind(e) == "do") code.AppendLine("do{");
                else code.AppendLine($"while({cond}){{");
                code.AppendLine("W64Ops.Step(ref _budget);");
                CsStmt(code, e.GetProperty("body"), ref unique, twoState, trackBudget);
                code.AppendLine(Kind(e) == "do" ? $"}}while({cond});" : "}");
                break;
            case "runtime_repeat":
                string rl = "rrep" + unique++;
                var cntEl = e.GetProperty("count");
                string cnt = twoState
                    ? $"W2Ops.RepeatCount({CsExpr2(cntEl)},{ExpressionWidth(cntEl)},{(ExpressionSigned(cntEl) ? "true" : "false")})"
                    : $"W64Ops.RepeatCount({CsExpr64(cntEl)},{(ExpressionSigned(cntEl) ? "true" : "false")})";
                code.AppendLine($"for(uint {rl}={cnt};{rl}>0;--{rl}){{");
                code.AppendLine("W64Ops.Step(ref _budget);");
                CsStmt(code, e.GetProperty("body"), ref unique, twoState, trackBudget);
                code.AppendLine("}");
                break;
            case "repeat":
                string lp = "rep" + unique++;
                code.AppendLine($"for(int {lp}=0;{lp}<{e.GetProperty("count").GetInt32()};++{lp}){{");
                CsStmt(code, e.GetProperty("body"), ref unique, twoState, trackBudget);
                code.AppendLine("}");
                break;
            case "block":
                foreach (var item in e.GetProperty("items").EnumerateArray()) CsStmt(code, item, ref unique, twoState, trackBudget);
                break;
            case "nba":
                var target = e.GetProperty("target");
                if (Kind(target) == "concat")
                {
                    int total = ExpressionWidth(target), remaining = total;
                    string rhs = "nba" + unique++;
                    code.AppendLine(twoState
                        ? $"{{ulong {rhs}=W2Ops.Resize({CsExpr2(e.GetProperty("value"), total)},{total});"
                        : $"{{W64 {rhs}=W64Ops.Resize({CsExpr64(e.GetProperty("value"), total)},{total});");
                    foreach (var leaf in TargetLeaves(target))
                    {
                        int width = ExpressionWidth(leaf);
                        remaining -= width;
                        string part = twoState
                            ? $"W2Ops.SelPart({rhs},{total},{remaining + width - 1},{remaining})"
                            : $"W64Ops.SelPart({rhs},{remaining + width - 1},{remaining})";
                        CsWordWrite(code, leaf, part, twoState, width);
                    }
                    code.AppendLine("}");
                }
                else if (Kind(target) is "ref" or "bitselect" or "partselect" or "indexedselect")
                {
                    int tw = ExpressionWidth(target);
                    var valEl = e.GetProperty("value");
                    int vw = ExpressionWidth(valEl);
                    bool exactWidth = vw == tw && !(ExpressionSigned(valEl) && tw > vw);
                    string expr = twoState ? CsExpr2(valEl, tw) : CsExpr64(valEl, tw);
                    CsWordWrite(code, target, expr, twoState, exactWidth ? tw : -1);
                }
                else
                {
                    int id = MemoryId(Name(target));
                    if (twoState)
                        code.AppendLine($"_mlog.Push({id},_m{id}.Length,{CsMemIdx(target, id, true)},{CsExpr2(e.GetProperty("value"), Memories[id].Width)},{Memories[id].Width});");
                    else
                    {
                        string v = CsExpr64(e.GetProperty("value"), Memories[id].Width);
                        if (Memories[id].TwoState) v = $"W64Ops.TwoState({v})";
                        code.AppendLine($"_mlog.Push({id},_m{id}.Length,{CsMemIdx(target, id, false)},{v},{Memories[id].Width});");
                    }
                }
                break;
            case "if":
                string ifCond = twoState ? $"W2Ops.Yes({CsExpr2(e.GetProperty("condition"))})" : $"W64Ops.Yes({CsExpr64(e.GetProperty("condition"))})";
                code.AppendLine($"if({ifCond}){{");
                CsStmt(code, e.GetProperty("then"), ref unique, twoState, trackBudget);
                code.AppendLine("}else{");
                CsStmt(code, e.GetProperty("else"), ref unique, twoState, trackBudget);
                code.AppendLine("}");
                break;
            case "assert":
            {
                string condExpr = twoState ? $"W2Ops.Yes({CsExpr2(e.GetProperty("condition"))})" : $"W64Ops.Yes({CsExpr64(e.GetProperty("condition"))})";
                string msg = CppStringLiteral(e.TryGetProperty("message", out var am) ? am.GetString()! : "");
                code.AppendLine($"if (!{condExpr}) throw new InvalidOperationException(\"RTL assertion failed: \" + {msg});");
                break;
            }
            case "finish":
                code.AppendLine("_finished = true;");
                break;
            case "fatal":
            {
                int fCode = e.TryGetProperty("code", out var fc) ? fc.GetInt32() : 1;
                if (e.TryGetProperty("format", out var ff))
                {
                    string fmt = CppStringLiteral(ff.GetString()!);
                    string argList = e.TryGetProperty("args", out var fa)
                        ? string.Join(", ", fa.EnumerateArray().Select(a => twoState ? $"new W64({CsExpr2(a)},0UL,0UL,{ExpressionWidth(a)})" : CsExpr64(a)))
                        : "";
                    code.AppendLine($"Fatal({fCode}, W64Ops.FormatDisplay({fmt}, [{argList}], false));");
                }
                else
                {
                    code.AppendLine($"Fatal({fCode});");
                }
                break;
            }
            case "display":
            {
                string fmt = CppStringLiteral(e.GetProperty("format").GetString()!);
                bool nl = !e.TryGetProperty("newline", out var nlProp) || nlProp.GetBoolean();
                string argList = e.TryGetProperty("args", out var dArgs)
                    ? string.Join(", ", dArgs.EnumerateArray().Select(a => twoState ? $"new W64({CsExpr2(a)},0UL,0UL,{ExpressionWidth(a)})" : CsExpr64(a)))
                    : "";
                code.AppendLine($"EmitDisplay(W64Ops.FormatDisplay({fmt}, [{argList}], {(nl ? "true" : "false")}));");
                break;
            }
            case "value_plusargs":
            {
                string fmt = CppStringLiteral(e.GetProperty("format").GetString()!);
                int tid = SignalId(e.GetProperty("target").GetString()!);
                var tsig = Signals[tid];
                code.AppendLine($"{{W64 _vp = {(twoState ? $"new W64(_next{tid},0UL,0UL,{tsig.Width})" : $"_next{tid}")}; uint _vpm = ValuePlusargs({fmt}, ref _vp); if (_vpm != 0u) _next{tid} = {CsStore(twoState ? "_vp.V" : "_vp", tsig, twoState)};");
                if (e.TryGetProperty("matched", out var vpMatched) && vpMatched.ValueKind != JsonValueKind.Null)
                {
                    int mid = SignalId(vpMatched.GetString()!);
                    code.AppendLine($"_next{mid} = {CsStore(twoState ? "(ulong)_vpm" : "new W64(_vpm,0UL,0UL,32)", Signals[mid], twoState)};");
                }
                code.AppendLine("}");
                break;
            }
            case "force":
            {
                int tid = SignalId(e.GetProperty("target").GetString()!);
                var tsig = Signals[tid];
                string valExpr = twoState
                    ? $"new W64({CsStore(CsExpr2(e.GetProperty("value")), tsig, true)},0UL,0UL,{tsig.Width})"
                    : CsStore(CsExpr64(e.GetProperty("value")), tsig, false);
                code.AppendLine($"ForceSignal({tid}, {valExpr});");
                break;
            }
            case "release":
            {
                int tid = SignalId(e.GetProperty("target").GetString()!);
                code.AppendLine($"ReleaseSignal({tid});");
                break;
            }
            case "case":
            {
                string lbl = "cs" + unique++;
                var items = e.GetProperty("items").EnumerateArray().ToArray();
                string matcher = CaseKind(e) switch { "casez" => "W64Ops.CasezMatch", "casex" => "W64Ops.CasexMatch", _ => "W64Ops.Exact" };
                var allMatches = items.SelectMany(CaseItemMatches).ToArray();
                int cw = Math.Max(ExpressionWidth(e.GetProperty("value")), allMatches.Select(ExpressionWidth).DefaultIfEmpty(1).Max());
                bool csSigned = ExpressionSigned(e.GetProperty("value")) && allMatches.All(ExpressionSigned);
                code.AppendLine(twoState
                    ? $"{{ulong {lbl}={CsExpr2(e.GetProperty("value"), cw, csSigned)};"
                    : $"{{W64 {lbl}={CsExpr64(e.GetProperty("value"), cw, csSigned)};");
                bool first = true;
                foreach (var item in items.Where(i => !IsCaseDefault(i)))
                {
                    var ms = CaseItemMatches(item);
                    string cmp = twoState
                        ? string.Join(" || ", ms.Select(m => $"{lbl}=={CsExpr2(m, cw, csSigned)}"))
                        : string.Join(" || ", ms.Select(m => $"{matcher}({lbl},{CsExpr64(m, cw, csSigned)})"));
                    code.AppendLine($"{(first ? "if" : "else if")}({cmp}){{");
                    CsStmt(code, item.GetProperty("body"), ref unique, twoState, trackBudget);
                    code.AppendLine("}");
                    first = false;
                }
                var defItem = items.FirstOrDefault(IsCaseDefault);
                if (defItem.ValueKind != JsonValueKind.Undefined)
                {
                    code.AppendLine(first ? "{" : "else{");
                    CsStmt(code, defItem.GetProperty("body"), ref unique, twoState, trackBudget);
                    code.AppendLine("}");
                }
                code.AppendLine("}");
                break;
            }
        }
    }

    /// <summary>Emits a standalone C# model class using register-resident W64 (4-state) or ulong (2-state) storage.</summary>
    public string EmitCSharpModel(string top, RtlStateMode stateMode = RtlStateMode.FourState, string @namespace = "VeloSim.Generated")
    {
        if (!Identifier(top) || ("V" + top).Contains("__", StringComparison.Ordinal)) throw new ArgumentException("Invalid external model top name.", nameof(top));
        if (!CanUsePortable64(stateMode))
            throw new IrValidationException("C# target currently supports non-timed <=64-bit event-driven and combinational designs without initialization blocks or dynamic, queue, associative, or multidimensional arrays.");
        bool twoState = stateMode == RtlStateMode.TwoState;
        string className = "V" + top;
        var names = CSharpPortNames();
        if (names.Values.Contains(className)) throw new ArgumentException("Port conflicts with generated class name.", nameof(top));
        string wordType = twoState ? "ulong" : "W64";
        bool hasRuntimeLoops = StatementsHaveRuntimeLoops(SettledSteps.Append(Body));
        bool canThrow = CanEvaluateThrow();
        var procs = CombinationalOnly ? Array.Empty<JsonElement>() : (Kind(Body) == "parallel" ? Body.GetProperty("items").EnumerateArray().ToArray() : [Body]);
        var eventIds = CombinationalOnly ? Array.Empty<int>() : EventSignals.Select(SignalId).ToArray();
        int[] seqIds = CombinationalOnly ? [] : Statements(Body).Where(e => Kind(e) == "nba" && Kind(e.GetProperty("target")) != "index")
            .SelectMany(e => TargetLeaves(e.GetProperty("target"))).Select(t => SignalId(TargetOwner(t)))
            .Concat(Statements(Body).Where(e => Kind(e) == "value_plusargs").SelectMany(e =>
                e.TryGetProperty("matched", out var m) && m.ValueKind != JsonValueKind.Null
                    ? new[] { SignalId(e.GetProperty("target").GetString()!), SignalId(m.GetString()!) }
                    : new[] { SignalId(e.GetProperty("target").GetString()!) }))
            .Distinct().Order().ToArray();
        bool hasFixedMemWrites = !CombinationalOnly && Statements(Body).Any(e => Kind(e) == "nba" && Kind(e.GetProperty("target")) == "index");

        var code = new StringBuilder();
        code.AppendLine("// <auto-generated/>");
        code.AppendLine("#nullable enable");
        code.AppendLine("using System;");
        code.AppendLine("using System.Collections.Generic;");
        code.AppendLine("using System.Runtime.CompilerServices;");
        code.AppendLine($"namespace {@namespace};");
        code.AppendLine();
        code.AppendLine($"public sealed class {className}");
        code.AppendLine("{");
        if (hasRuntimeLoops) code.AppendLine("    private int _budget = 20000;");
        code.AppendLine("    private ulong _time;");
        code.AppendLine("    private bool _finished = false;");
        code.AppendLine("    private int _exitCode = 0;");
        code.AppendLine("    private VcdWriter? _vcd;");
        code.AppendLine("    private readonly PlusargStore _plusargs = new();");
        code.AppendLine("    private SplitMix64Rng _rng = new();");
        code.AppendLine($"    private Dictionary<int, {wordType}>? _forced;");
        code.AppendLine("    public bool GotFinish => _finished;");
        code.AppendLine("    public int ExitCode => _exitCode;");
        code.AppendLine("    public void Fatal(int code, string message = \"\") { _exitCode = code; _finished = true; throw new InvalidOperationException(string.IsNullOrEmpty(message) ? $\"RTL $fatal({code})\" : $\"RTL $fatal({code}): {message}\"); }");
        code.AppendLine("    public Action<string>? DisplaySink { get; set; }");
        code.AppendLine("    private void EmitDisplay(string text) { if (DisplaySink is not null) DisplaySink(text); else Console.Write(text); }");
        code.AppendLine("    public void SetPlusargs(IEnumerable<string> args) => _plusargs.Set(args);");
        code.AppendLine("    public void AddPlusarg(string arg) => _plusargs.Add(arg);");
        code.AppendLine("    public uint TestPlusargs(string prefix) => _plusargs.Test(prefix);");
        code.AppendLine("    public uint ValuePlusargs(string spec, ref W64 output) => _plusargs.Value(spec, ref output);");
        code.AppendLine("    public void SetRandomSeed(ulong seed) => _rng.SetSeed(seed);");
        code.AppendLine("    public ulong RandomSeed => _rng.State;");
        code.AppendLine("    public uint NextUrandom() => _rng.NextU32();");
        code.AppendLine("    public uint NextUrandomRange(uint maxVal, uint minVal = 0u) => _rng.NextRange(maxVal, minVal);");
        code.AppendLine("    public int NextRandom() => _rng.NextRandom();");
        if (Properties.Count > 0)
        {
            code.AppendLine($"    public ulong[] PropertyPasses {{ get; }} = new ulong[{Properties.Count}];");
            code.AppendLine($"    public ulong[] PropertyFailures {{ get; }} = new ulong[{Properties.Count}];");
            code.AppendLine($"    public ulong[] PropertyVacuous {{ get; }} = new ulong[{Properties.Count}];");
            for (int i = 0; i < Properties.Count; ++i)
                if (Properties[i].Delay > 0)
                    code.AppendLine($"    private readonly bool[] _propPipe_{i} = new bool[{Properties[i].Delay}];");
        }
        foreach (var (sid, depth) in PastDepths.OrderBy(kv => kv.Key))
            code.AppendLine($"    private readonly {wordType}[] _past_{sid} = new {wordType}[{depth}];");
        foreach (int cid in PropertyClockSignalIds())
            code.AppendLine($"    private {wordType} _pclk_{cid};");
        code.AppendLine("    public bool IsForced(int signalId) => _forced is not null && _forced.ContainsKey(signalId);");
        code.AppendLine("    public void ForceSignal(int signalId, W64 value)");
        code.AppendLine("    {");
        code.AppendLine("        _forced ??= new();");
        code.AppendLine("        switch (signalId)");
        code.AppendLine("        {");
        for (int i = 0; i < Signals.Count; ++i)
        {
            var sig = Signals[i];
            if (twoState)
                code.AppendLine($"            case {i}: {{ ulong fv = W2Ops.Resize(value.V, {sig.Width}); _forced[{i}] = fv; _s{i} = fv; break; }}");
            else
                code.AppendLine($"            case {i}: {{ W64 fv = {(sig.TwoState ? $"W64Ops.TwoState(W64Ops.Resize(value, {sig.Width}))" : $"W64Ops.Resize(value, {sig.Width})")}; _forced[{i}] = fv; _s{i} = fv; break; }}");
        }
        code.AppendLine("            default: throw new ArgumentOutOfRangeException(nameof(signalId));");
        code.AppendLine("        }");
        code.AppendLine("    }");
        code.AppendLine("    public void ReleaseSignal(int signalId) { _forced?.Remove(signalId); }");
        code.AppendLine("    private void ApplyForces()");
        code.AppendLine("    {");
        code.AppendLine("        if (_forced is null || _forced.Count == 0) return;");
        code.AppendLine("        foreach (var kv in _forced)");
        code.AppendLine("        {");
        code.AppendLine("            switch (kv.Key)");
        code.AppendLine("            {");
        for (int i = 0; i < Signals.Count; ++i)
            code.AppendLine($"                case {i}: _s{i} = kv.Value; break;");
        code.AppendLine("            }");
        code.AppendLine("        }");
        code.AppendLine("    }");
        for (int i = 0; i < Signals.Count; ++i)
            code.AppendLine($"    private {wordType} _s{i};");
        foreach (int id in eventIds)
            code.AppendLine($"    private {wordType} _prev{id};");
        foreach (int id in seqIds)
            code.AppendLine($"    private {wordType} _next{id};");
        for (int i = 0; i < Memories.Count; ++i)
            code.AppendLine($"    private readonly {wordType}[] _m{i} = new {wordType}[{Memories[i].Depth}];");
        if (hasFixedMemWrites)
        {
            code.AppendLine($"    private readonly {wordType}[][] _mems;");
            code.AppendLine($"    private readonly {(twoState ? "MemLog2" : "MemLog64")} _mlog = new();");
        }
        code.AppendLine();
        code.AppendLine($"    public {className}()");
        code.AppendLine("    {");
        for (int i = 0; i < Signals.Count; ++i)
        {
            var sig = Signals[i];
            if (twoState) code.AppendLine($"        _s{i} = 0UL;");
            else if (sig.Name == Clock || sig.TwoState) code.AppendLine($"        _s{i} = new W64(0UL,0UL,0UL,{sig.Width});");
            else code.AppendLine($"        _s{i} = W64Ops.Unknown({sig.Width});");
        }
        foreach (int id in eventIds)
        {
            var sig = Signals[id];
            if (twoState) code.AppendLine($"        _prev{id} = 0UL;");
            else if (sig.Name == Clock || sig.TwoState) code.AppendLine($"        _prev{id} = new W64(0UL,0UL,0UL,1);");
            else code.AppendLine($"        _prev{id} = W64Ops.Unknown(1);");
        }
        for (int i = 0; i < Memories.Count; ++i)
        {
            var mem = Memories[i];
            if (!twoState)
                code.AppendLine($"        Array.Fill(_m{i}, {(mem.TwoState ? $"new W64(0UL,0UL,0UL,{mem.Width})" : $"W64Ops.Unknown({mem.Width})")});");
        }
        foreach (var (sid, depth) in PastDepths.OrderBy(kv => kv.Key))
        {
            var sig = Signals[sid];
            string initVal = twoState ? "0UL" : (sig.TwoState ? $"new W64(0UL,0UL,0UL,{sig.Width})" : $"W64Ops.Unknown({sig.Width})");
            code.AppendLine($"        Array.Fill(_past_{sid}, {initVal});");
        }
        foreach (int cid in PropertyClockSignalIds())
            code.AppendLine(twoState ? $"        _pclk_{cid} = 0UL;" : $"        _pclk_{cid} = new W64(0UL,0UL,0UL,1);");
        if (hasFixedMemWrites)
        {
            code.Append("        _mems = [");
            code.AppendJoin(", ", Enumerable.Range(0, Memories.Count).Select(i => $"_m{i}"));
            code.AppendLine("];");
        }
        code.AppendLine("    }");
        code.AppendLine();
        code.AppendLine("    public ulong Time => _time;");
        code.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)] public void TimeInc(ulong delta) => _time += delta;");
        code.AppendLine("    public void Final() { CloseVcd(); }");
        code.AppendLine("    private static readonly (string Name, int Width)[] _signalSchema = [");
        code.Append("        ");
        code.AppendJoin(", ", Signals.Select(s => $"({CppStringLiteral(s.SourcePath ?? s.Name)}, {s.Width})"));
        code.AppendLine();
        code.AppendLine("    ];");
        code.AppendLine("    public void OpenVcd(string path, string timescale = \"1ns\", string[]? filterPrefixes = null)");
        code.AppendLine("    {");
        code.AppendLine("        CloseVcd();");
        code.AppendLine($"        _vcd = new VcdWriter(path, \"{top}\", _signalSchema, timescale, false, filterPrefixes);");
        code.AppendLine("    }");
        code.AppendLine("    public void OpenVcdGz(string path, string timescale = \"1ns\", string[]? filterPrefixes = null)");
        code.AppendLine("    {");
        code.AppendLine("        CloseVcd();");
        code.AppendLine($"        _vcd = new VcdWriter(path, \"{top}\", _signalSchema, timescale, true, filterPrefixes);");
        code.AppendLine("    }");
        code.AppendLine("    public void DumpVcd()");
        code.AppendLine("    {");
        code.AppendLine("        if (_vcd is null) return;");
        code.Append("        _vcd.Dump(_time, [");
        code.AppendJoin(", ", Signals.Select((s, idx) => twoState ? $"new W64(_s{idx}, 0UL, 0UL, {s.Width})" : $"_s{idx}"));
        code.AppendLine("]);");
        code.AppendLine("    }");
        code.AppendLine("    public void CloseVcd() { _vcd?.Dispose(); _vcd = null; }");
        // Persistent binary checkpoint with schema hash and FNV-1a integrity verification.
        code.AppendLine($"    private static ulong CheckpointSchemaHash() => CheckpointIO.SchemaHash(\"{top}\", {(twoState ? "true" : "false")}, _signalSchema, [{string.Join(", ", Memories.Select(m => $"({m.Depth}, {m.Width})"))}]);");
        code.AppendLine("    public void SaveCheckpoint(string path)");
        code.AppendLine("    {");
        code.Append("        W64[] sigs = [");
        code.AppendJoin(", ", Signals.Select((s, idx) => twoState ? $"new W64(_s{idx}, 0UL, 0UL, {s.Width})" : $"_s{idx}"));
        code.AppendLine("];");
        code.Append("        W64[] prevs = [");
        code.AppendJoin(", ", eventIds.Select(id => twoState ? $"new W64(_prev{id}, 0UL, 0UL, 1)" : $"_prev{id}"));
        code.AppendLine("];");
        code.AppendLine($"        W64[][] mems = new W64[{Memories.Count}][];");
        for (int i = 0; i < Memories.Count; ++i)
        {
            if (twoState)
                code.AppendLine($"        mems[{i}] = Array.ConvertAll(_m{i}, v => new W64(v, 0UL, 0UL, {Memories[i].Width}));");
            else
                code.AppendLine($"        mems[{i}] = _m{i};");
        }
        code.AppendLine("        CheckpointIO.Save(path, CheckpointSchemaHash(), _time, _rng.State, sigs, prevs, mems);");
        code.AppendLine("    }");
        code.AppendLine("    public void RestoreCheckpoint(string path)");
        code.AppendLine("    {");
        code.AppendLine($"        var (t, rngState, sigs, prevs, mems) = CheckpointIO.Load(path, CheckpointSchemaHash(), {Signals.Count}, {eventIds.Length}, [{string.Join(", ", Memories.Select(m => m.Depth))}]);");
        code.AppendLine("        _time = t; _rng.State = rngState;");
        for (int i = 0; i < Signals.Count; ++i)
            code.AppendLine(twoState ? $"        _s{i} = W2Ops.Resize(sigs[{i}].V, {Signals[i].Width});" : $"        _s{i} = sigs[{i}];");
        for (int i = 0; i < eventIds.Length; ++i)
            code.AppendLine(twoState ? $"        _prev{eventIds[i]} = prevs[{i}].V & 1UL;" : $"        _prev{eventIds[i]} = prevs[{i}];");
        for (int i = 0; i < Memories.Count; ++i)
        {
            if (twoState)
                code.AppendLine($"        for (int j = 0; j < _m{i}.Length; ++j) _m{i}[j] = W2Ops.Resize(mems[{i}][j].V, {Memories[i].Width});");
            else
                code.AppendLine($"        Array.Copy(mems[{i}], _m{i}, _m{i}.Length);");
        }
        code.AppendLine("    }");
        code.AppendLine();
        foreach (var sig in Signals.Where(s => s.Direction != "internal"))
        {
            int id = SignalId(sig.Name);
            string prop = names[sig.Name];
            ulong mask = sig.Width >= 64 ? ulong.MaxValue : ((1UL << sig.Width) - 1UL);
            if (sig.Direction == "input")
            {
                if (twoState)
                {
                    code.AppendLine($"    public ulong {prop} {{ [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _s{id}; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => _s{id} = value & 0x{mask:x}UL; }}");
                    code.AppendLine($"    public W64 Get_{prop}_Word() => new W64(_s{id}, 0UL, 0UL, {sig.Width});");
                    code.AppendLine($"    public void Set_{prop}_Word(W64 value) => _s{id} = value.V & ~(value.X | value.Z) & 0x{mask:x}UL;");
                    code.AppendLine($"    public string Get_{prop}_Bits() => W64Ops.Text(new W64(_s{id}, 0UL, 0UL, {sig.Width}));");
                    code.AppendLine($"    public void Set_{prop}_Bits(string bits) {{ var w = W64Ops.Parse(bits, {sig.Width}); _s{id} = w.V & ~(w.X | w.Z) & 0x{mask:x}UL; }}");
                }
                else
                {
                    code.AppendLine($"    public ulong {prop} {{ [MethodImpl(MethodImplOptions.AggressiveInlining)] get => W64Ops.ToUInt64(_s{id}); [MethodImpl(MethodImplOptions.AggressiveInlining)] set => _s{id} = new W64(value & 0x{mask:x}UL, 0UL, 0UL, {sig.Width}); }}");
                    code.AppendLine($"    public W64 Get_{prop}_Word() => _s{id};");
                    code.AppendLine($"    public void Set_{prop}_Word(W64 value) => _s{id} = {(sig.TwoState ? $"W64Ops.TwoState(W64Ops.Resize(value, {sig.Width}))" : $"W64Ops.Resize(value, {sig.Width})")};");
                    code.AppendLine($"    public string Get_{prop}_Bits() => W64Ops.Text(_s{id});");
                    code.AppendLine($"    public void Set_{prop}_Bits(string bits) => Set_{prop}_Word(W64Ops.Parse(bits, {sig.Width}));");
                }
            }
            else
            {
                if (twoState)
                {
                    code.AppendLine($"    public ulong {prop} {{ [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _s{id}; }}");
                    code.AppendLine($"    public W64 Get_{prop}_Word() => new W64(_s{id}, 0UL, 0UL, {sig.Width});");
                    code.AppendLine($"    public string Get_{prop}_Bits() => W64Ops.Text(new W64(_s{id}, 0UL, 0UL, {sig.Width}));");
                }
                else
                {
                    code.AppendLine($"    public ulong {prop} {{ [MethodImpl(MethodImplOptions.AggressiveInlining)] get => W64Ops.ToUInt64(_s{id}); }}");
                    code.AppendLine($"    public W64 Get_{prop}_Word() => _s{id};");
                    code.AppendLine($"    public string Get_{prop}_Bits() => W64Ops.Text(_s{id});");
                }
            }
        }
        code.AppendLine();
        code.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        code.AppendLine("    private void Settle()");
        code.AppendLine("    {");
        foreach (var step in SettledSteps)
        {
            int id = SignalId(Name(step.GetProperty("target")));
            var rhs = step.GetProperty("value");
            int vw = ExpressionWidth(rhs);
            bool exactWidth = vw == Signals[id].Width && !(ExpressionSigned(rhs) && Signals[id].Width > vw);
            string expr = twoState ? CsExpr2(rhs, Signals[id].Width) : CsExpr64(rhs, Signals[id].Width);
            code.AppendLine($"        _s{id} = {CsStore(expr, Signals[id], twoState, exactWidth ? Signals[id].Width : -1)};");
        }
        code.AppendLine("    }");
        code.AppendLine();
        code.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        code.AppendLine("    public void Eval()");
        code.AppendLine("    {");
        if (hasRuntimeLoops) code.AppendLine("        _budget = 20000;");
        if (canThrow)
        {
            for (int i = 0; i < Signals.Count; ++i) code.AppendLine($"        var sv_s{i} = _s{i};");
            foreach (int id in eventIds) code.AppendLine($"        var sv_p{id} = _prev{id};");
            for (int i = 0; i < Memories.Count; ++i) code.AppendLine($"        var sv_m{i} = ({wordType}[])_m{i}.Clone();");
            code.AppendLine("        try {");
        }
        code.AppendLine("        if (_forced is { Count: > 0 }) ApplyForces();");
        code.AppendLine("        Settle();");
        code.AppendLine("        if (_forced is { Count: > 0 }) ApplyForces();");
        if (Properties.Count > 0 || PastDepths.Count > 0)
        {
            for (int i = 0; i < Properties.Count; ++i)
            {
                var p = Properties[i];
                int cid = SignalId(p.Clock);
                string edgeCond = twoState
                    ? $"W2Ops.Edge(_pclk_{cid}, _s{cid}, {(p.Edge == "posedge" ? "true" : "false")})"
                    : $"W64Ops.Edge(_pclk_{cid}, _s{cid}, {(p.Edge == "posedge" ? "true" : "false")})";
                code.AppendLine($"        if ({edgeCond}) {{");
                if (p.DisableIff is { } dis)
                {
                    string disCond = twoState ? $"W2Ops.Yes({CsExpr2(dis)})" : $"W64Ops.Yes({CsExpr64(dis)})";
                    code.AppendLine($"            if ({disCond}) {{");
                    if (p.Delay > 0) code.AppendLine($"                Array.Clear(_propPipe_{i});");
                    code.AppendLine("            } else {");
                }
                string antCond = p.Antecedent is { } ant
                    ? (twoState ? $"W2Ops.Yes({CsExpr2(ant)})" : $"W64Ops.Yes({CsExpr64(ant)})")
                    : "true";
                string consCond = twoState ? $"W2Ops.Yes({CsExpr2(p.Consequent)})" : $"W64Ops.Yes({CsExpr64(p.Consequent)})";
                string failMsg = CppStringLiteral(string.IsNullOrEmpty(p.Message) ? $"SVA property '{p.Name}' failed" : $"SVA property '{p.Name}' failed: {p.Message}");
                if (p.Delay == 0)
                {
                    code.AppendLine($"            if ({antCond}) {{ if ({consCond}) {{ ++PropertyPasses[{i}]; }} else {{ ++PropertyFailures[{i}]; {(p.Kind == "assert" ? $"throw new InvalidOperationException({failMsg});" : "")} }} }}");
                    if (p.Kind != "cover") code.AppendLine($"            else {{ ++PropertyVacuous[{i}]; }}");
                }
                else
                {
                    code.AppendLine($"            if (_propPipe_{i}[{p.Delay - 1}]) {{ if ({consCond}) {{ ++PropertyPasses[{i}]; }} else {{ ++PropertyFailures[{i}]; {(p.Kind == "assert" ? $"throw new InvalidOperationException({failMsg});" : "")} }} }}");
                    for (int k = p.Delay - 1; k > 0; --k)
                        code.AppendLine($"            _propPipe_{i}[{k}] = _propPipe_{i}[{k - 1}];");
                    code.AppendLine($"            bool _ant_{i} = {antCond}; _propPipe_{i}[0] = _ant_{i};");
                    if (p.Kind != "cover") code.AppendLine($"            if (!_ant_{i}) ++PropertyVacuous[{i}];");
                }
                if (p.DisableIff is not null) code.AppendLine("            }");
                code.AppendLine("        }");
            }
            if (PastDepths.Count > 0)
            {
                int pastCid = Clock is not null ? SignalId(Clock) : SignalId(Properties[0].Clock);
                string pastEdge = Properties.FirstOrDefault(p => SignalId(p.Clock) == pastCid)?.Edge ?? "posedge";
                string pEdge = twoState
                    ? $"W2Ops.Edge(_pclk_{pastCid}, _s{pastCid}, {(pastEdge == "posedge" ? "true" : "false")})"
                    : $"W64Ops.Edge(_pclk_{pastCid}, _s{pastCid}, {(pastEdge == "posedge" ? "true" : "false")})";
                code.AppendLine($"        if ({pEdge}) {{");
                foreach (var (sid, depth) in PastDepths.OrderBy(kv => kv.Key))
                {
                    for (int k = depth - 1; k > 0; --k)
                        code.AppendLine($"            _past_{sid}[{k}] = _past_{sid}[{k - 1}];");
                    code.AppendLine($"            _past_{sid}[0] = _s{sid};");
                }
                code.AppendLine("        }");
            }
            foreach (int cid in PropertyClockSignalIds())
                code.AppendLine($"        _pclk_{cid} = _s{cid};");
        }
        if (!CombinationalOnly)
        {
            for (int p = 0; p < procs.Length; ++p)
            {
                string cond = string.Join(" || ", procs[p].GetProperty("events").EnumerateArray().Select(tr =>
                {
                    int id = SignalId(tr.GetProperty("signal").GetString()!);
                    bool pos = tr.GetProperty("edge").GetString() == "posedge";
                    return twoState
                        ? $"W2Ops.Edge(_prev{id}, _s{id}, {(pos ? "true" : "false")})"
                        : $"W64Ops.Edge(_prev{id}, _s{id}, {(pos ? "true" : "false")})";
                }));
                code.AppendLine($"        bool ev{p} = {cond};");
            }
            string anyEv = string.Join(" || ", Enumerable.Range(0, procs.Length).Select(p => $"ev{p}"));
            code.AppendLine($"        if ({anyEv})");
            code.AppendLine("        {");
            foreach (int id in seqIds) code.AppendLine($"            _next{id} = _s{id};");
            int unique = 0;
            for (int p = 0; p < procs.Length; ++p)
            {
                if (procs.Length > 1) code.AppendLine($"            if (ev{p}) {{");
                CsStmt(code, procs[p].GetProperty("body"), ref unique, twoState, hasRuntimeLoops);
                if (procs.Length > 1) code.AppendLine("            }");
            }
            foreach (int id in seqIds) code.AppendLine($"            _s{id} = _next{id};");
            if (hasFixedMemWrites) code.AppendLine("            _mlog.Commit(_mems);");
            code.AppendLine("            Settle();");
            code.AppendLine("            if (_forced is { Count: > 0 }) ApplyForces();");
            code.AppendLine("        }");
            foreach (int id in eventIds) code.AppendLine($"        _prev{id} = _s{id};");
        }
        if (canThrow)
        {
            code.AppendLine("        } catch {");
            for (int i = 0; i < Signals.Count; ++i) code.AppendLine($"            _s{i} = sv_s{i};");
            foreach (int id in eventIds) code.AppendLine($"            _prev{id} = sv_p{id};");
            for (int i = 0; i < Memories.Count; ++i) code.AppendLine($"            Array.Copy(sv_m{i}, _m{i}, _m{i}.Length);");
            if (hasFixedMemWrites) code.AppendLine("            _mlog.Clear();");
            code.AppendLine("            throw;");
            code.AppendLine("        }");
        }
        code.AppendLine("    }");
        code.AppendLine("}");
        return code.ToString();
    }

    internal static string EmitCSharpRuntime(string @namespace = "VeloSim.Generated") => $$"""
// <auto-generated/>
#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace {{@namespace}};

public readonly struct W64
{
    public readonly ulong V;
    public readonly ulong X;
    public readonly ulong Z;
    public readonly int W;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public W64(ulong v, ulong x, ulong z, int w) { V = v; X = x; Z = z; W = w; }
}

public static class W64Ops
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Mask(int w) => w >= 64 ? ulong.MaxValue : (w <= 0 ? 0UL : ((1UL << w) - 1UL));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 Unknown(int w) => new W64(0UL, Mask(w), 0UL, w);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ToUInt64(W64 a)
    {
        if ((a.X | a.Z) != 0UL) throw new InvalidOperationException("integer conversion of X/Z port");
        return a.V;
    }

    public static W64 Parse(string bits, int width)
    {
        if (bits is null || bits.Length != width) throw new ArgumentException("port binary width mismatch");
        ulong v = 0, x = 0, z = 0;
        foreach (char c in bits)
        {
            v <<= 1; x <<= 1; z <<= 1;
            switch (c)
            {
                case '0': break;
                case '1': v |= 1UL; break;
                case 'x': case 'X': x |= 1UL; break;
                case 'z': case 'Z': z |= 1UL; break;
                default: throw new ArgumentException("invalid 4-state bit character");
            }
        }
        return new W64(v, x, z, width);
    }

    public static string Text(W64 a)
    {
        char[] buf = new char[a.W];
        for (int i = 0; i < a.W; ++i)
        {
            int bit = a.W - 1 - i;
            if (((a.Z >> bit) & 1UL) != 0) buf[i] = 'z';
            else if (((a.X >> bit) & 1UL) != 0) buf[i] = 'x';
            else buf[i] = (((a.V >> bit) & 1UL) != 0) ? '1' : '0';
        }
        return new string(buf);
    }

    public static string FormatDisplay(string fmt, W64[] args, bool newline)
    {
        var sb = new System.Text.StringBuilder(fmt.Length + 32);
        int argIdx = 0;
        for (int i = 0; i < fmt.Length; ++i)
        {
            if (fmt[i] != '%' || i + 1 >= fmt.Length) { sb.Append(fmt[i]); continue; }
            if (fmt[i + 1] == '%') { sb.Append('%'); ++i; continue; }
            bool trim = false;
            if (fmt[i + 1] == '0' && i + 2 < fmt.Length) { trim = true; ++i; }
            char spec = fmt[++i];
            if (argIdx >= args.Length) { sb.Append('%').Append(spec); continue; }
            W64 a = args[argIdx++];
            if (spec is 'h' or 'H' or 'x' or 'X')
            {
                string s = ((a.X | a.Z) != 0) ? "x" : a.V.ToString("x");
                if (!trim) { int digits = (a.W + 3) / 4; if (s.Length < digits) s = s.PadLeft(digits, '0'); }
                sb.Append(s);
            }
            else if (spec is 'b' or 'B')
            {
                string s = Text(a);
                if (trim) s = s.TrimStart('0') is { Length: > 0 } t ? t : "0";
                sb.Append(s);
            }
            else if (spec is 'd' or 'D' or 'u' or 'U')
                sb.Append((a.X | a.Z) != 0 ? "x" : a.V.ToString());
            else sb.Append('%').Append(spec);
        }
        if (newline) sb.Append('\n');
        return sb.ToString();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Step(ref int budget)
    {
        if (budget == 0) throw new InvalidOperationException("runtime loop execution budget exceeded");
        --budget;
    }

    public static uint RepeatCount(W64 a, bool isSigned)
    {
        if ((a.X | a.Z) != 0UL) return 0U;
        if (isSigned && a.W > 0 && ((a.V >> (a.W - 1)) & 1UL) != 0) return 0U;
        if (a.V > 20000UL) throw new InvalidOperationException("runtime repeat count exceeds budget");
        return (uint)a.V;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 Resize(W64 a, int w)
    {
        ulong m = Mask(w);
        return new W64(a.V & m, a.X & m, a.Z & m, w);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 TwoState(W64 a) => new W64(a.V & Mask(a.W), 0UL, 0UL, a.W);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 SResize(W64 a, int w)
    {
        if (w <= a.W) return Resize(a, w);
        ulong fill = Mask(w) & ~Mask(a.W);
        int top = a.W - 1;
        ulong sv = ((a.V >> top) & 1UL) != 0 ? fill : 0UL;
        ulong sx = ((a.X >> top) & 1UL) != 0 ? fill : 0UL;
        ulong sz = ((a.Z >> top) & 1UL) != 0 ? fill : 0UL;
        return new W64(a.V | sv, a.X | sx, a.Z | sz, w);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LogicBit(W64 a) => ((a.X | a.Z) & 1UL) != 0 ? -1 : (int)(a.V & 1UL);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Edge(W64 prev, W64 curr, bool pos)
    {
        int p = LogicBit(prev), c = LogicBit(curr);
        return pos ? ((p == 0 && c != 0) || (p < 0 && c == 1)) : ((p == 1 && c != 1) || (p < 0 && c == 0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Truth(W64 a) => a.V != 0UL ? 1 : ((a.X | a.Z) != 0UL ? -1 : 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Yes(W64 a) => a.V != 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 LNot(W64 a)
    {
        int t = Truth(a);
        return t < 0 ? new W64(0UL, 1UL, 0UL, 1) : new W64(t == 0 ? 1UL : 0UL, 0UL, 0UL, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 LAnd(W64 a, W64 b)
    {
        int x = Truth(a), y = Truth(b);
        return (x == 0 || y == 0) ? new W64(0UL, 0UL, 0UL, 1) : (x == 1 && y == 1) ? new W64(1UL, 0UL, 0UL, 1) : new W64(0UL, 1UL, 0UL, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 LOr(W64 a, W64 b)
    {
        int x = Truth(a), y = Truth(b);
        return (x == 1 || y == 1) ? new W64(1UL, 0UL, 0UL, 1) : (x == 0 && y == 0) ? new W64(0UL, 0UL, 0UL, 1) : new W64(0UL, 1UL, 0UL, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 BNot(W64 a, int w)
    {
        ulong m = Mask(w);
        ulong u = (a.X | a.Z) & m;
        return new W64((~a.V) & (~u) & m, u, 0UL, w);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 BBin(W64 a, W64 b, int w, int op)
    {
        ulong m = Mask(w);
        ulong au = (a.X | a.Z) & m, bu = (b.X | b.Z) & m;
        ulong av = a.V & (~au) & m, bv = b.V & (~bu) & m;
        if (op == 2) { ulong ou = au | bu; return new W64((av ^ bv) & (~ou), ou, 0UL, w); }
        ulong az = ~(av | au), bz = ~(bv | bu);
        if (op == 0) { ulong ov = av & bv; return new W64(ov, ~(az | bz | ov) & m, 0UL, w); }
        { ulong ov = av | bv; return new W64(ov, ~(ov | (az & bz)) & m, 0UL, w); }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Exact(W64 a, W64 b) => a.V == b.V && a.X == b.X && a.Z == b.Z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CasezMatch(W64 a, W64 b)
    {
        int w = a.W > b.W ? a.W : b.W;
        ulong care = ~(a.Z | b.Z) & Mask(w);
        return (((a.V ^ b.V) | (a.X ^ b.X)) & care) == 0UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CasexMatch(W64 a, W64 b)
    {
        int w = a.W > b.W ? a.W : b.W;
        ulong care = ~(a.X | a.Z | b.X | b.Z) & Mask(w);
        return ((a.V ^ b.V) & care) == 0UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 WildEq(W64 a, W64 b)
    {
        int w = a.W > b.W ? a.W : b.W;
        ulong care = ~(b.X | b.Z) & Mask(w);
        ulong au = (a.X | a.Z) & care;
        ulong known = care & ~au;
        if (((a.V ^ b.V) & known) != 0UL) return new W64(0UL, 0UL, 0UL, 1);
        return au != 0UL ? new W64(0UL, 1UL, 0UL, 1) : new W64(1UL, 0UL, 0UL, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 WildNeq(W64 a, W64 b) => LNot(WildEq(a, b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 Eq(W64 a, W64 b)
    {
        if (((a.X | a.Z) | (b.X | b.Z)) != 0UL) return new W64(0UL, 1UL, 0UL, 1);
        return new W64(a.V == b.V ? 1UL : 0UL, 0UL, 0UL, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 Cmp(W64 a, W64 b, int op)
    {
        if (((a.X | a.Z) | (b.X | b.Z)) != 0UL) return new W64(0UL, 1UL, 0UL, 1);
        bool r = op == 0 ? a.V < b.V : op == 1 ? a.V <= b.V : op == 2 ? a.V > b.V : a.V >= b.V;
        return new W64(r ? 1UL : 0UL, 0UL, 0UL, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 SCmp(W64 a, W64 b, int op)
    {
        if (((a.X | a.Z) | (b.X | b.Z)) != 0UL) return new W64(0UL, 1UL, 0UL, 1);
        long sa = W2Ops.SExt(a.V, a.W), sb = W2Ops.SExt(b.V, b.W);
        bool r = op == 0 ? sa < sb : op == 1 ? sa <= sb : op == 2 ? sa > sb : sa >= sb;
        return new W64(r ? 1UL : 0UL, 0UL, 0UL, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 Arith(W64 a, W64 b, int w, bool sub)
    {
        ulong m = Mask(w);
        if (((a.X | a.Z) | (b.X | b.Z)) != 0UL) return new W64(0UL, m, 0UL, w);
        ulong r = unchecked(sub ? (a.V - b.V) : (a.V + b.V)) & m;
        return new W64(r, 0UL, 0UL, w);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 Shift(W64 a, W64 b, int w, bool right)
    {
        ulong m = Mask(w);
        if ((b.X | b.Z) != 0UL) return new W64(0UL, m, 0UL, w);
        if (b.V >= 64UL) return new W64(0UL, 0UL, 0UL, w);
        int sh = (int)b.V;
        if (right) return new W64((a.V & m) >> sh, (a.X & m) >> sh, (a.Z & m) >> sh, w);
        return new W64((a.V << sh) & m, (a.X << sh) & m, (a.Z << sh) & m, w);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 SShift(W64 a, W64 b, int w)
    {
        ulong m = Mask(w);
        if ((b.X | b.Z) != 0UL) return new W64(0UL, m, 0UL, w);
        int top = w - 1;
        ulong sv = (a.V >> top) & 1UL, sx = (a.X >> top) & 1UL, sz = (a.Z >> top) & 1UL;
        if (b.V >= 64UL) return new W64(sv != 0 ? m : 0UL, sx != 0 ? m : 0UL, sz != 0 ? m : 0UL, w);
        int sh = (int)b.V;
        ulong fill = sh == 0 ? 0UL : (m & ~(m >> sh));
        return new W64((((a.V & m) >> sh) | (sv != 0 ? fill : 0UL)) & m,
                       (((a.X & m) >> sh) | (sx != 0 ? fill : 0UL)) & m,
                       (((a.Z & m) >> sh) | (sz != 0 ? fill : 0UL)) & m, w);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 Red(W64 a, int op)
    {
        ulong m = Mask(a.W);
        ulong u = (a.X | a.Z) & m;
        ulong v = a.V & (~u) & m;
        if (op == 0 || op == 1)
        {
            ulong zeros = (~(v | u)) & m;
            if (zeros != 0UL) return new W64(op == 1 ? 1UL : 0UL, 0UL, 0UL, 1);
            if (u != 0UL) return new W64(0UL, 1UL, 0UL, 1);
            return new W64(op == 0 ? 1UL : 0UL, 0UL, 0UL, 1);
        }
        if (op == 2 || op == 3)
        {
            if (v != 0UL) return new W64(op == 2 ? 1UL : 0UL, 0UL, 0UL, 1);
            if (u != 0UL) return new W64(0UL, 1UL, 0UL, 1);
            return new W64(op == 3 ? 1UL : 0UL, 0UL, 0UL, 1);
        }
        if (u != 0UL) return new W64(0UL, 1UL, 0UL, 1);
        bool odd = (BitOperations.PopCount(v) & 1) != 0;
        return new W64((odd ^ (op == 5)) ? 1UL : 0UL, 0UL, 0UL, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 Product(W64 a, W64 b, int w, int op)
    {
        ulong m = Mask(w);
        if (((a.X | a.Z) | (b.X | b.Z)) != 0UL) return new W64(0UL, m, 0UL, w);
        ulong av = a.V & m, bv = b.V & m;
        if (op == 0) return new W64(unchecked(av * bv) & m, 0UL, 0UL, w);
        if (bv == 0UL) return new W64(0UL, m, 0UL, w);
        return new W64((op == 1 ? (av / bv) : (av % bv)) & m, 0UL, 0UL, w);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 SProduct(W64 a, W64 b, int w, int op)
    {
        ulong m = Mask(w);
        if (((a.X | a.Z) | (b.X | b.Z)) != 0UL) return new W64(0UL, m, 0UL, w);
        long sa = W2Ops.SExt(a.V, w), sb = W2Ops.SExt(b.V, w);
        if (op == 0) return new W64(unchecked((ulong)sa * (ulong)sb) & m, 0UL, 0UL, w);
        if (sb == 0L) return new W64(0UL, m, 0UL, w);
        if (sa == long.MinValue && sb == -1L) return new W64(op == 1 ? ((ulong)sa & m) : 0UL, 0UL, 0UL, w);
        return new W64((ulong)(op == 1 ? (sa / sb) : (sa % sb)) & m, 0UL, 0UL, w);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 Cond(W64 c, W64 a, W64 b, int w)
    {
        int t = Truth(c);
        if (t == 1) return Resize(a, w);
        if (t == 0) return Resize(b, w);
        ulong m = Mask(w);
        ulong au = (a.X | a.Z) & m, bu = (b.X | b.Z) & m;
        ulong av = a.V & (~au) & m, bv = b.V & (~bu) & m;
        ulong u = (au | bu | (av ^ bv)) & m;
        return new W64(av & (~u), u, 0UL, w);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 Concat(W64 a, W64 b)
    {
        int bw = b.W;
        ulong bm = Mask(bw);
        if (bw >= 64) return new W64(b.V & bm, b.X & bm, b.Z & bm, a.W + bw);
        return new W64((a.V << bw) | (b.V & bm), (a.X << bw) | (b.X & bm), (a.Z << bw) | (b.Z & bm), a.W + bw);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 SelBit(W64 a, W64 idx)
    {
        if ((idx.X | idx.Z) != 0UL || idx.V >= (ulong)a.W) return new W64(0UL, 1UL, 0UL, 1);
        int i = (int)idx.V;
        return new W64((a.V >> i) & 1UL, (a.X >> i) & 1UL, (a.Z >> i) & 1UL, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 SelPart(W64 a, int left, int right)
    {
        int n = left - right + 1;
        ulong m = Mask(n);
        if (right >= a.W) return new W64(0UL, m, 0UL, n);
        int avail = a.W - right;
        ulong am = Mask(avail < n ? avail : n);
        return new W64((a.V >> right) & am, ((a.X >> right) & am) | (m & ~am), (a.Z >> right) & am, n);
    }

    public static W64 SelIdx(W64 a, W64 b, int width, bool desc)
    {
        if ((b.X | b.Z) != 0UL) return Unknown(width);
        if (b.V > 2147483647UL) throw new ArgumentOutOfRangeException(nameof(b), "known indexed select base exceeds qualified range 0..INT32_MAX");
        long lo = desc ? (long)b.V - (width - 1) : (long)b.V;
        long srcLo = lo < 0 ? 0 : lo;
        long srcHi = (lo + width) < a.W ? (lo + width) : a.W;
        if (srcLo >= srcHi) return Unknown(width);
        int count = (int)(srcHi - srcLo);
        int dstLo = (int)(srcLo - lo);
        int sLo = (int)srcLo;
        ulong cm = Mask(count);
        ulong dstMask = cm << dstLo;
        return new W64(((a.V >> sLo) & cm) << dstLo, (((a.X >> sLo) & cm) << dstLo) | (Mask(width) & ~dstMask), ((a.Z >> sLo) & cm) << dstLo, width);
    }

    public static W64 DepIdx(W64 before, W64 b, int width, bool desc, W64 value)
    {
        if ((b.X | b.Z) != 0UL) return before;
        if (b.V > 2147483647UL) throw new ArgumentOutOfRangeException(nameof(b), "known selected write base exceeds qualified range 0..INT32_MAX");
        value = Resize(value, width);
        long lo = desc ? (long)b.V - (width - 1) : (long)b.V;
        long dstLo = lo < 0 ? 0 : lo;
        long dstHi = (lo + width) < before.W ? (lo + width) : before.W;
        if (dstLo >= dstHi) return before;
        int count = (int)(dstHi - dstLo);
        int srcLo = (int)(dstLo - lo);
        int dLo = (int)dstLo;
        ulong cm = Mask(count);
        ulong dmask = cm << dLo;
        return new W64((before.V & ~dmask) | (((value.V >> srcLo) & cm) << dLo),
                       (before.X & ~dmask) | (((value.X >> srcLo) & cm) << dLo),
                       (before.Z & ~dmask) | (((value.Z >> srcLo) & cm) << dLo), before.W);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 MemIdx(W64 index, int lower)
    {
        if ((index.X | index.Z) != 0UL || index.V < (ulong)lower) return new W64(0UL, 1UL, 0UL, 1);
        return new W64(index.V - (ulong)lower, 0UL, 0UL, index.W);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 Read(W64[] m, W64 idx, int w)
    {
        if ((idx.X | idx.Z) != 0UL || idx.V >= (ulong)m.Length) return Unknown(w);
        return m[(int)idx.V];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 CountOnes(W64 a)
    {
        ulong ones = a.V & ~a.X & ~a.Z & Mask(a.W);
        return new W64((ulong)BitOperations.PopCount(ones), 0UL, 0UL, 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 OneHot(W64 a) => new W64(CountOnes(a).V == 1UL ? 1UL : 0UL, 0UL, 0UL, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 OneHot0(W64 a) => new W64(CountOnes(a).V <= 1UL ? 1UL : 0UL, 0UL, 0UL, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 IsUnknown(W64 a) => new W64(((a.X | a.Z) & Mask(a.W)) != 0UL ? 1UL : 0UL, 0UL, 0UL, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 CLog2(W64 a)
    {
        ulong m = Mask(a.W);
        if (((a.X | a.Z) & m) != 0UL) return new W64(0UL, 0UL, 0UL, 32);
        ulong x = a.V & m;
        if (x <= 1UL) return new W64(0UL, 0UL, 0UL, 32);
        return new W64((ulong)(64 - BitOperations.LeadingZeroCount(x - 1UL)), 0UL, 0UL, 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 ResolvePair(W64 aIn, W64 bIn, int netKind, int w)
    {
        ulong m = Mask(w);
        W64 a = aIn.W == w ? aIn : Resize(aIn, w);
        W64 b = bIn.W == w ? bIn : Resize(bIn, w);
        ulong va = a.V & m, xa = a.X & m, za = a.Z & m;
        ulong vb = b.V & m, xb = b.X & m, zb = b.Z & m;
        ulong a0 = ~(va | xa | za) & m;
        ulong b0 = ~(vb | xb | zb) & m;
        ulong zOut = za & zb;
        ulong vOut, o0;
        if (netKind == 1)
        {
            o0 = (a0 | b0) & m;
            vOut = (~o0) & ((va & (vb | zb)) | (za & vb)) & m;
        }
        else if (netKind == 2)
        {
            vOut = (va | vb) & m;
            o0 = (~vOut) & ((a0 & (b0 | zb)) | (za & b0)) & m;
        }
        else
        {
            vOut = ((va & (vb | zb)) | (za & vb)) & m;
            o0 = ((a0 & (b0 | zb)) | (za & b0)) & m;
        }
        ulong xOut = ~(zOut | vOut | o0) & m;
        return new W64(vOut, xOut, zOut, w);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 ResolveFinalize(W64 acc, int netKind)
    {
        ulong m = Mask(acc.W);
        if (netKind == 3) return new W64(acc.V, acc.X, 0UL, acc.W);
        if (netKind == 4) return new W64((acc.V | acc.Z) & m, acc.X, 0UL, acc.W);
        return acc;
    }

    public static W64 ResolveNet(int netKind, ReadOnlySpan<W64> drivers, int w)
    {
        if (drivers.Length == 0) return ResolveFinalize(new W64(0UL, 0UL, Mask(w), w), netKind);
        W64 acc = drivers[0].W == w ? drivers[0] : Resize(drivers[0], w);
        for (int i = 1; i < drivers.Length; ++i) acc = ResolvePair(acc, drivers[i], netKind, w);
        return ResolveFinalize(acc, netKind);
    }
}

public static class W2Ops
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Mask(int w) => w >= 64 ? ulong.MaxValue : (w <= 0 ? 0UL : ((1UL << w) - 1UL));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong CountOnes(ulong a, int w) => (ulong)BitOperations.PopCount(a & Mask(w));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong OneHot(ulong a, int w) => CountOnes(a, w) == 1UL ? 1UL : 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong OneHot0(ulong a, int w) => CountOnes(a, w) <= 1UL ? 1UL : 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong CLog2(ulong a, int w)
    {
        ulong x = a & Mask(w);
        if (x <= 1UL) return 0UL;
        return (ulong)(64 - BitOperations.LeadingZeroCount(x - 1UL));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long SExt(ulong a, int w)
    {
        ulong m = Mask(w);
        a &= m;
        if (w < 64 && (a & (1UL << (w - 1))) != 0UL) a |= ~m;
        return unchecked((long)a);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Resize(ulong a, int w) => a & Mask(w);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong SResize(ulong a, int aw, int w) => unchecked((ulong)SExt(a, aw)) & Mask(w);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Edge(ulong prev, ulong curr, bool pos) => pos ? (((prev & 1UL) == 0UL) && ((curr & 1UL) != 0UL)) : (((prev & 1UL) != 0UL) && ((curr & 1UL) == 0UL));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Yes(ulong a) => a != 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong LNot(ulong a) => a == 0UL ? 1UL : 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong LAnd(ulong a, ulong b) => (a != 0UL && b != 0UL) ? 1UL : 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong LOr(ulong a, ulong b) => (a != 0UL || b != 0UL) ? 1UL : 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong BNot(ulong a, int w) => (~a) & Mask(w);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong BBin(ulong a, ulong b, int w, int op) => (op == 0 ? (a & b) : op == 1 ? (a | b) : (a ^ b)) & Mask(w);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Eq(ulong a, ulong b) => a == b ? 1UL : 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Cmp(ulong a, ulong b, int op) => (op == 0 ? a < b : op == 1 ? a <= b : op == 2 ? a > b : a >= b) ? 1UL : 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong SCmp(ulong a, ulong b, int w, int op)
    {
        long sa = SExt(a, w), sb = SExt(b, w);
        return (op == 0 ? sa < sb : op == 1 ? sa <= sb : op == 2 ? sa > sb : sa >= sb) ? 1UL : 0UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Arith(ulong a, ulong b, int w, bool sub) => unchecked(sub ? (a - b) : (a + b)) & Mask(w);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Shift(ulong a, ulong b, int w, bool right)
    {
        if (b >= 64UL) return 0UL;
        ulong m = Mask(w);
        return (right ? ((a & m) >> (int)b) : (a << (int)b)) & m;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong SShift(ulong a, ulong b, int w)
    {
        ulong m = Mask(w);
        long sa = SExt(a, w);
        if (b >= 64UL) return sa < 0L ? m : 0UL;
        return unchecked((ulong)(sa >> (int)b)) & m;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Red(ulong a, int w, int op)
    {
        ulong m = Mask(w);
        ulong v = a & m;
        if (op == 0 || op == 1) return ((v == m) ^ (op == 1)) ? 1UL : 0UL;
        if (op == 2 || op == 3) return ((v != 0UL) ^ (op == 3)) ? 1UL : 0UL;
        bool odd = (BitOperations.PopCount(v) & 1) != 0;
        return (odd ^ (op == 5)) ? 1UL : 0UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Product(ulong a, ulong b, int w, int op)
    {
        ulong m = Mask(w);
        ulong av = a & m, bv = b & m;
        if (op == 0) return unchecked(av * bv) & m;
        if (bv == 0UL) return 0UL;
        return (op == 1 ? (av / bv) : (av % bv)) & m;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong SProduct(ulong a, ulong b, int w, int op)
    {
        ulong m = Mask(w);
        long sa = SExt(a, w), sb = SExt(b, w);
        if (op == 0) return unchecked((ulong)sa * (ulong)sb) & m;
        if (sb == 0L) return 0UL;
        if (sa == long.MinValue && sb == -1L) return op == 1 ? ((ulong)sa & m) : 0UL;
        return unchecked((ulong)(op == 1 ? (sa / sb) : (sa % sb))) & m;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Cond(ulong c, ulong t, ulong f, int w) => (c != 0UL ? t : f) & Mask(w);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Concat(ulong a, ulong b, int bw) => (bw >= 64 ? 0UL : (a << bw)) | (b & Mask(bw));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong SelBit(ulong a, int aw, ulong idx) => idx < (ulong)aw ? ((a >> (int)idx) & 1UL) : 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong SelPart(ulong a, int aw, int left, int right) => right >= aw ? 0UL : (((a & Mask(aw)) >> right) & Mask(left - right + 1));

    public static ulong SelIdx(ulong a, int aw, ulong b, int width, bool desc)
    {
        if (b > 2147483647UL) throw new ArgumentOutOfRangeException(nameof(b), "known indexed select base exceeds qualified range 0..INT32_MAX");
        long lo = desc ? (long)b - (width - 1) : (long)b;
        long srcLo = lo < 0 ? 0 : lo;
        long srcHi = (lo + width) < aw ? (lo + width) : aw;
        if (srcLo >= srcHi) return 0UL;
        int count = (int)(srcHi - srcLo);
        int dstLo = (int)(srcLo - lo);
        ulong cm = Mask(count);
        return (((a & Mask(aw)) >> (int)srcLo) & cm) << dstLo;
    }

    public static ulong DepIdx(ulong before, int tw, ulong b, int width, bool desc, ulong value)
    {
        if (b > 2147483647UL) throw new ArgumentOutOfRangeException(nameof(b), "known selected write base exceeds qualified range 0..INT32_MAX");
        value &= Mask(width);
        long lo = desc ? (long)b - (width - 1) : (long)b;
        long dstLo = lo < 0 ? 0 : lo;
        long dstHi = (lo + width) < tw ? (lo + width) : tw;
        if (dstLo >= dstHi) return before;
        int count = (int)(dstHi - dstLo);
        int srcLo = (int)(dstLo - lo);
        int dLo = (int)dstLo;
        ulong cm = Mask(count);
        ulong dmask = cm << dLo;
        return (before & ~dmask) | (((value >> srcLo) & cm) << dLo);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong MemIdx(ulong index, int lower) => index >= (ulong)lower ? (index - (ulong)lower) : ulong.MaxValue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Read(ulong[] m, ulong idx) => idx < (ulong)m.Length ? m[(int)idx] : 0UL;

    public static uint RepeatCount(ulong a, int w, bool isSigned)
    {
        if (isSigned && w > 0 && ((a >> (w - 1)) & 1UL) != 0) return 0U;
        if (a > 20000UL) throw new InvalidOperationException("runtime repeat count exceeds budget");
        return (uint)a;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ResolvePair(ulong a, ulong b, int netKind, int w)
    {
        ulong m = Mask(w);
        return (netKind == 1 ? (a & b) : (a | b)) & m;
    }

    public static ulong ResolveNet(int netKind, ReadOnlySpan<ulong> drivers, int w)
    {
        ulong m = Mask(w);
        if (drivers.Length == 0) return netKind == 4 ? m : 0UL;
        ulong acc = drivers[0] & m;
        for (int i = 1; i < drivers.Length; ++i) acc = ResolvePair(acc, drivers[i], netKind, w);
        return acc;
    }
}

public sealed class MemLog64
{
    private readonly int[] _mem = new int[16];
    private readonly int[] _idx = new int[16];
    private readonly W64[] _val = new W64[16];
    private List<(int mem, int idx, W64 val)>? _overflow;
    private int _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(int memId, int memLen, W64 idx, W64 val, int w)
    {
        if ((idx.X | idx.Z) != 0UL || idx.V >= (ulong)memLen) return;
        W64 v = W64Ops.Resize(val, w);
        if (_count < 16) { _mem[_count] = memId; _idx[_count] = (int)idx.V; _val[_count] = v; ++_count; }
        else (_overflow ??= new()).Add((memId, (int)idx.V, v));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Commit(W64[][] mems)
    {
        for (int i = 0; i < _count; ++i) mems[_mem[i]][_idx[i]] = _val[i];
        if (_overflow is { Count: > 0 }) { foreach (var e in _overflow) mems[e.mem][e.idx] = e.val; _overflow.Clear(); }
        _count = 0;
    }

    public void Clear() { _count = 0; _overflow?.Clear(); }
}

public sealed class MemLog2
{
    private readonly int[] _mem = new int[16];
    private readonly int[] _idx = new int[16];
    private readonly ulong[] _val = new ulong[16];
    private List<(int mem, int idx, ulong val)>? _overflow;
    private int _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(int memId, int memLen, ulong idx, ulong val, int w)
    {
        if (idx >= (ulong)memLen) return;
        ulong v = val & W2Ops.Mask(w);
        if (_count < 16) { _mem[_count] = memId; _idx[_count] = (int)idx; _val[_count] = v; ++_count; }
        else (_overflow ??= new()).Add((memId, (int)idx, v));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Commit(ulong[][] mems)
    {
        for (int i = 0; i < _count; ++i) mems[_mem[i]][_idx[i]] = _val[i];
        if (_overflow is { Count: > 0 }) { foreach (var e in _overflow) mems[e.mem][e.idx] = e.val; _overflow.Clear(); }
        _count = 0;
    }

    public void Clear() { _count = 0; _overflow?.Clear(); }
}

public sealed class VcdWriter : IDisposable
{
    private readonly System.IO.StreamWriter _writer;
    private readonly int[] _sigIds;
    private readonly string[] _codes;
    private readonly W64[] _last;
    private bool _initial;
    private bool _disposed;

    private static bool MatchesSelector(string full, string sel) =>
        full == sel || (full.Length > sel.Length && full.StartsWith(sel, StringComparison.Ordinal) && full[sel.Length] == '.');

    public VcdWriter(string path, string topName, (string Name, int Width)[] signals, string timescale = "1ns", bool gzip = false, string[]? filterPrefixes = null)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("VCD output path must not be empty.", nameof(path));
        if (string.IsNullOrEmpty(timescale)) throw new ArgumentException("VCD timescale must not be empty.", nameof(timescale));
        var selected = new List<int>(signals.Length);
        if (filterPrefixes is null || filterPrefixes.Length == 0)
        {
            for (int i = 0; i < signals.Length; ++i) selected.Add(i);
        }
        else
        {
            var matched = new bool[filterPrefixes.Length];
            for (int k = 0; k < filterPrefixes.Length; ++k)
                if (string.IsNullOrEmpty(filterPrefixes[k])) throw new ArgumentException("VCD trace selector must not be empty.", nameof(filterPrefixes));
            for (int i = 0; i < signals.Length; ++i)
            {
                bool hit = false;
                for (int k = 0; k < filterPrefixes.Length; ++k)
                {
                    if (MatchesSelector(signals[i].Name, filterPrefixes[k])) { matched[k] = true; hit = true; }
                }
                if (hit) selected.Add(i);
            }
            for (int k = 0; k < filterPrefixes.Length; ++k)
                if (!matched[k]) throw new ArgumentException($"VCD trace selector matched no signals: {filterPrefixes[k]}");
        }

        System.IO.Stream raw = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read);
        System.IO.Stream stream = gzip ? new System.IO.Compression.GZipStream(raw, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: false) : raw;
        _writer = new System.IO.StreamWriter(stream, new System.Text.UTF8Encoding(false));
        _sigIds = selected.ToArray();
        _codes = new string[_sigIds.Length];
        _last = new W64[_sigIds.Length];
        _writer.Write("$version VeloSim $end\n");
        _writer.Write($"$timescale {timescale} $end\n");
        _writer.Write($"$scope module {(string.IsNullOrEmpty(topName) ? "top" : topName)} $end\n");
        for (int idx = 0; idx < _sigIds.Length; ++idx)
        {
            int i = _sigIds[idx];
            string code = MakeCode(idx);
            _codes[idx] = code;
            _last[idx] = W64Ops.Unknown(signals[i].Width);
            string leaf = signals[i].Name.Replace('.', '_');
            _writer.Write(signals[i].Width > 1
                ? $"$var wire {signals[i].Width} {code} {leaf} [{signals[i].Width - 1}:0] $end\n"
                : $"$var wire 1 {code} {leaf} $end\n");
        }
        _writer.Write("$upscope $end\n$enddefinitions $end\n");
    }

    private static string MakeCode(int index)
    {
        var sb = new System.Text.StringBuilder(4);
        int n = index;
        do { sb.Append((char)(33 + (n % 94))); n /= 94; } while (n > 0);
        return sb.ToString();
    }

    private void WriteVal(W64 val, string code)
    {
        if (val.W == 1)
        {
            char ch = (val.Z & 1UL) != 0 ? 'z' : (val.X & 1UL) != 0 ? 'x' : (val.V & 1UL) != 0 ? '1' : '0';
            _writer.Write(ch);
            _writer.Write(code);
            _writer.Write('\n');
            return;
        }
        _writer.Write('b');
        _writer.Write(W64Ops.Text(val));
        _writer.Write(' ');
        _writer.Write(code);
        _writer.Write('\n');
    }

    public void Dump(ulong time, W64[] values)
    {
        if (_disposed) return;
        if (!_initial)
        {
            _writer.Write('#');
            _writer.Write(time);
            _writer.Write("\n$dumpvars\n");
            for (int idx = 0; idx < _sigIds.Length; ++idx)
            {
                int sid = _sigIds[idx];
                if (sid >= values.Length) continue;
                _last[idx] = values[sid];
                WriteVal(values[sid], _codes[idx]);
            }
            _writer.Write("$end\n");
            _initial = true;
            return;
        }
        bool wroteTime = false;
        for (int idx = 0; idx < _sigIds.Length; ++idx)
        {
            int sid = _sigIds[idx];
            if (sid >= values.Length) continue;
            if (W64Ops.Exact(values[sid], _last[idx])) continue;
            if (!wroteTime) { _writer.Write('#'); _writer.Write(time); _writer.Write('\n'); wroteTime = true; }
            _last[idx] = values[sid];
            WriteVal(values[sid], _codes[idx]);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.Flush();
        _writer.Dispose();
    }
}

public sealed class PlusargStore
{
    private readonly List<string> _args = new();
    public void Set(IEnumerable<string> args)
    {
        _args.Clear();
        foreach (var raw in args) Add(raw);
    }
    public void Add(string arg) => _args.Add(arg.StartsWith('+') ? arg[1..] : arg);
    public uint Test(string prefix)
    {
        string p = prefix.StartsWith('+') ? prefix[1..] : prefix;
        foreach (var a in _args) if (a.StartsWith(p, StringComparison.Ordinal)) return 1u;
        return 0u;
    }
    public uint Value(string spec, ref W64 output)
    {
        string s = spec.StartsWith('+') ? spec[1..] : spec;
        int pct = s.IndexOf('%');
        if (pct < 0 || pct + 1 >= s.Length) return 0u;
        string prefix = s[..pct];
        int fi = pct + 1;
        if (s[fi] == '0' && fi + 1 < s.Length) ++fi;
        char fmt = char.ToLowerInvariant(s[fi]);
        foreach (var a in _args)
        {
            if (!a.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string clean = a[prefix.Length..].Replace("_", "");
            if (clean.Length == 0) continue;
            if (TryParse(clean, fmt, output.W, out W64 parsed)) { output = parsed; return 1u; }
        }
        return 0u;
    }
    private static bool TryParse(string s, char fmt, int width, out W64 value)
    {
        value = default;
        ulong m = W64Ops.Mask(width);
        if (fmt is 'd' or 'u')
        {
            bool neg = fmt == 'd' && s[0] == '-';
            string digits = (neg || s[0] == '+') ? s[1..] : s;
            if (!ulong.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong v)) return false;
            if (neg) v = unchecked((ulong)(-(long)v));
            value = new W64(v & m, 0UL, 0UL, width);
            return true;
        }
        int shift = fmt is 'h' or 'x' ? 4 : fmt == 'o' ? 3 : fmt == 'b' ? 1 : 0;
        if (shift == 0) return false;
        ulong nib = (1UL << shift) - 1UL;
        ulong vv = 0, xx = 0, zz = 0;
        foreach (char raw in s)
        {
            char c = char.ToLowerInvariant(raw);
            vv <<= shift; xx <<= shift; zz <<= shift;
            if (c == 'x') xx |= nib;
            else if (c == 'z') zz |= nib;
            else
            {
                int d = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : -1;
                if (d < 0 || d >= (1 << shift)) return false;
                vv |= (uint)d;
            }
        }
        value = new W64(vv & ~(xx | zz) & m, xx & ~zz & m, zz & m, width);
        return true;
    }
}

public struct SplitMix64Rng
{
    public ulong State;
    public uint LcgState;
    public SplitMix64Rng() { State = 0x9E3779B97F4A7C15UL; LcgState = 0x9E3779B9u; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetSeed(ulong seed)
    {
        State = seed;
        LcgState = (uint)(seed ^ (seed >> 32));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint NextU32()
    {
        ulong z = State += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (uint)z;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint NextRange(uint maxVal, uint minVal)
    {
        if (maxVal < minVal) (maxVal, minVal) = (minVal, maxVal);
        ulong span = (ulong)maxVal - minVal + 1UL;
        uint r = NextU32();
        if (span >= 0x100000000UL) return r;
        return (uint)(minVal + (r % span));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int NextRandom()
    {
        LcgState = unchecked(69069u * LcgState + 1u);
        return unchecked((int)LcgState);
    }
}

public static class CheckpointIO
{
    private const uint Magic = 0x31435356u; // "VSC1"
    private const uint Version = 1u;

    private static ulong FnvMix(ulong h, ulong v)
    {
        for (int i = 0; i < 8; ++i) { h ^= (v >> (i * 8)) & 0xFFUL; h *= 1099511628211UL; }
        return h;
    }
    private static ulong FnvBytes(byte[] data)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < data.Length; ++i) { h ^= data[i]; h *= 1099511628211UL; }
        return h;
    }
    public static ulong SchemaHash(string top, bool twoState, (string Name, int Width)[] signals, (int Depth, int Width)[] memories)
    {
        ulong h = 14695981039346656037UL;
        foreach (char c in top) h = FnvMix(h, c);
        h = FnvMix(h, twoState ? 2UL : 4UL);
        h = FnvMix(h, (ulong)signals.Length);
        foreach (var (name, width) in signals)
        {
            foreach (char c in name) h = FnvMix(h, c);
            h = FnvMix(h, (ulong)width);
        }
        h = FnvMix(h, (ulong)memories.Length);
        foreach (var (depth, width) in memories) { h = FnvMix(h, (ulong)depth); h = FnvMix(h, (ulong)width); }
        return h;
    }
    public static void Save(string path, ulong schemaHash, ulong time, ulong rngState, W64[] sigs, W64[] prevs, W64[][] mems)
    {
        using var payloadMs = new System.IO.MemoryStream();
        using (var bw = new System.IO.BinaryWriter(payloadMs, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write(time);
            bw.Write(rngState);
            bw.Write(sigs.Length);
            foreach (var w in sigs) { bw.Write(w.V); bw.Write(w.X); bw.Write(w.Z); bw.Write(w.W); }
            bw.Write(prevs.Length);
            foreach (var w in prevs) { bw.Write(w.V); bw.Write(w.X); bw.Write(w.Z); bw.Write(w.W); }
            bw.Write(mems.Length);
            foreach (var m in mems)
            {
                bw.Write(m.Length);
                foreach (var w in m) { bw.Write(w.V); bw.Write(w.X); bw.Write(w.Z); bw.Write(w.W); }
            }
        }
        byte[] payload = payloadMs.ToArray();
        ulong checksum = FnvBytes(payload);
        using var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None);
        using var hw = new System.IO.BinaryWriter(fs);
        hw.Write(Magic);
        hw.Write(Version);
        hw.Write(schemaHash);
        hw.Write((ulong)payload.Length);
        hw.Write(checksum);
        hw.Write(payload);
    }
    public static (ulong Time, ulong RngState, W64[] Sigs, W64[] Prevs, W64[][] Mems) Load(string path, ulong expectedSchema, int expectedSigs, int expectedPrevs, int[] expectedMemDepths)
    {
        byte[] raw = System.IO.File.ReadAllBytes(path);
        if (raw.Length < 32) throw new System.IO.InvalidDataException("Checkpoint file is truncated.");
        using var ms = new System.IO.MemoryStream(raw);
        using var br = new System.IO.BinaryReader(ms);
        if (br.ReadUInt32() != Magic) throw new System.IO.InvalidDataException("Invalid checkpoint magic.");
        if (br.ReadUInt32() != Version) throw new System.IO.InvalidDataException("Unsupported checkpoint version.");
        if (br.ReadUInt64() != expectedSchema) throw new System.IO.InvalidDataException("Checkpoint schema hash mismatch.");
        ulong payloadLen = br.ReadUInt64();
        ulong expectedCsum = br.ReadUInt64();
        if (payloadLen != (ulong)(raw.Length - 32)) throw new System.IO.InvalidDataException("Checkpoint payload length mismatch.");
        byte[] payload = br.ReadBytes((int)payloadLen);
        if (FnvBytes(payload) != expectedCsum) throw new System.IO.InvalidDataException("Checkpoint checksum mismatch.");
        using var pms = new System.IO.MemoryStream(payload);
        using var pr = new System.IO.BinaryReader(pms);
        ulong time = pr.ReadUInt64();
        ulong rngState = pr.ReadUInt64();
        int nSigs = pr.ReadInt32();
        if (nSigs != expectedSigs) throw new System.IO.InvalidDataException("Checkpoint signal count mismatch.");
        var sigs = new W64[nSigs];
        for (int i = 0; i < nSigs; ++i) sigs[i] = new W64(pr.ReadUInt64(), pr.ReadUInt64(), pr.ReadUInt64(), pr.ReadInt32());
        int nPrevs = pr.ReadInt32();
        if (nPrevs != expectedPrevs) throw new System.IO.InvalidDataException("Checkpoint event count mismatch.");
        var prevs = new W64[nPrevs];
        for (int i = 0; i < nPrevs; ++i) prevs[i] = new W64(pr.ReadUInt64(), pr.ReadUInt64(), pr.ReadUInt64(), pr.ReadInt32());
        int nMems = pr.ReadInt32();
        if (nMems != expectedMemDepths.Length) throw new System.IO.InvalidDataException("Checkpoint memory count mismatch.");
        var mems = new W64[nMems][];
        for (int i = 0; i < nMems; ++i)
        {
            int depth = pr.ReadInt32();
            if (depth != expectedMemDepths[i]) throw new System.IO.InvalidDataException("Checkpoint memory depth mismatch.");
            mems[i] = new W64[depth];
            for (int j = 0; j < depth; ++j) mems[i][j] = new W64(pr.ReadUInt64(), pr.ReadUInt64(), pr.ReadUInt64(), pr.ReadInt32());
        }
        return (time, rngState, sigs, prevs, mems);
    }
}
""";
}
