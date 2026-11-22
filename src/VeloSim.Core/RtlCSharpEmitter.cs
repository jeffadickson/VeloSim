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
            "abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while Eval Final Finish Time TimeInc OpenVcd OpenVcdGz DumpVcd CloseVcd SetPlusargs AddPlusarg TestPlusargs ValuePlusargs Plusargs SetRandomSeed RandomSeed NextUrandom NextUrandomRange NextRandom ForceSignal ReleaseSignal IsForced Fatal GotFinish ExitCode PropertyPasses PropertyFailures PropertyVacuous SaveCheckpoint RestoreCheckpoint ModelName Signals Memories SignalByName GetSignal PutSignal ReadMemory WriteMemory W4 W4Ops MemLog4 W64 W64Ops W2Ops VcdWriter PlusargStore SplitMix64Rng CheckpointIO IVpiModel VpiDirection VpiSignalInfo VpiMemoryInfo VpiCbReason VpiCbHandle VpiCbEvent VpiLookup VpiContext VpiSession"
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

    private static string CsMaskHex(int w) =>
        w >= 64 ? "ulong.MaxValue" : (w <= 0 ? "0UL" : $"0x{((1UL << w) - 1UL):x}UL");

    private static string EmitCsConst4(string bits)
    {
        ulong a = 0, b = 0;
        foreach (char c in bits)
        {
            a <<= 1; b <<= 1;
            if (c == '1') a |= 1;
            else if (c == 'x') { a |= 1; b |= 1; }
            else if (c == 'z') b |= 1;
        }
        return (a | b) == 0 ? "default" : $"new W4(0x{a:x}UL,0x{b:x}UL)";
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

    private static bool TryConstShift(JsonElement e, out int shift)
    {
        shift = 0;
        if (Kind(e) != "const") return false;
        string bits = e.GetProperty("bits").GetString()!;
        ulong v = 0;
        foreach (char c in bits)
        {
            if (c is not ('0' or '1')) return false;
            v = (v << 1) | (c == '1' ? 1UL : 0UL);
        }
        if (v >= 64UL) return false;
        shift = (int)v;
        return true;
    }

    private const int CsInlineMemMaxDepth = 4096;
    private bool CsIsInlineMem(int id) => Memories[id].Depth is >= 1 and <= CsInlineMemMaxDepth;

    private int CsMaxMemWrites(JsonElement e, int memId, bool inLoop, out bool hitInLoop)
    {
        hitInLoop = false;
        switch (Kind(e))
        {
            case "nba":
            {
                var t = e.GetProperty("target");
                if (Kind(t) == "index" && MemoryId(Name(t)) == memId) { hitInLoop = inLoop; return 1; }
                return 0;
            }
            case "block":
            case "parallel":
            {
                int sum = 0;
                foreach (var item in e.GetProperty("items").EnumerateArray())
                {
                    sum += CsMaxMemWrites(item, memId, inLoop, out bool sub);
                    hitInLoop |= sub;
                }
                return sum;
            }
            case "process":
                return CsMaxMemWrites(e.GetProperty("body"), memId, inLoop, out hitInLoop);
            case "if":
            {
                int a = CsMaxMemWrites(e.GetProperty("then"), memId, inLoop, out bool sa);
                int b = CsMaxMemWrites(e.GetProperty("else"), memId, inLoop, out bool sb);
                hitInLoop = sa || sb;
                return Math.Max(a, b);
            }
            case "case":
            {
                int max = 0;
                foreach (var item in e.GetProperty("items").EnumerateArray())
                {
                    int c = CsMaxMemWrites(item.GetProperty("body"), memId, inLoop, out bool sc);
                    if (c > max) max = c;
                    hitInLoop |= sc;
                }
                return max;
            }
            case "while":
            case "do":
            case "repeat":
            case "runtime_repeat":
                return CsMaxMemWrites(e.GetProperty("body"), memId, true, out hitInLoop);
            default:
                return 0;
        }
    }

    private string CsMemIdx(JsonElement access, int id, bool twoState)
    {
        string idx = twoState ? CsExpr2(access.GetProperty("index")) : CsExpr64(access.GetProperty("index"));
        if (Memories[id].LowerBound == 0) return idx;
        return twoState ? $"W2Ops.MemIdx({idx},{Memories[id].LowerBound})" : $"W4Ops.MemIdx({idx},{Memories[id].LowerBound})";
    }

    private bool CsExprIsPureInternal(JsonElement e)
    {
        string k = Kind(e);
        if (k == "ref") return Signals[SignalId(Name(e))].Direction != "input";
        if (k is "urandom" or "urandom_range" or "random" or "test_plusargs" or "past" or "rose" or "fell" or "stable") return false;
        if (e.TryGetProperty("value", out var v) && !CsExprIsPureInternal(v)) return false;
        if (e.TryGetProperty("index", out var ix) && !CsExprIsPureInternal(ix)) return false;
        if (e.TryGetProperty("base", out var bs) && !CsExprIsPureInternal(bs)) return false;
        if (e.TryGetProperty("args", out var args))
            foreach (var a in args.EnumerateArray()) if (!CsExprIsPureInternal(a)) return false;
        if (e.TryGetProperty("items", out var items))
            foreach (var it in items.EnumerateArray())
            {
                if (it.TryGetProperty("lo", out var lo) && !CsExprIsPureInternal(lo)) return false;
                if (it.TryGetProperty("hi", out var hi) && !CsExprIsPureInternal(hi)) return false;
                if (!it.TryGetProperty("lo", out _) && !CsExprIsPureInternal(it)) return false;
            }
        return true;
    }

    private static bool CsHasSideEffect(JsonElement e)
    {
        string k = Kind(e);
        if (k is "urandom" or "urandom_range" or "random") return true;
        if (e.TryGetProperty("value", out var v) && CsHasSideEffect(v)) return true;
        if (e.TryGetProperty("index", out var ix) && CsHasSideEffect(ix)) return true;
        if (e.TryGetProperty("base", out var bs) && CsHasSideEffect(bs)) return true;
        if (e.TryGetProperty("args", out var args))
            foreach (var a in args.EnumerateArray()) if (CsHasSideEffect(a)) return true;
        return false;
    }

    private string CsCond64(JsonElement e)
    {
        string k = Kind(e);
        if (k == "not")
        {
            var a = e.GetProperty("args")[0];
            return $"W4Ops.IsZero({CsExpr64(a)})";
        }
        if (k is "and" or "or")
        {
            var args = e.GetProperty("args");
            var a = args[0];
            var b = args[1];
            if (!CsHasSideEffect(b))
                return $"({CsCond64(a)}{(k == "and" ? "&&" : "||")}{CsCond64(b)})";
        }
        if (k is "eq" or "neq" or "caseeq" or "caseneq")
        {
            var args = e.GetProperty("args");
            var a = args[0];
            var b = args[1];
            int cw = Math.Max(ExpressionWidth(a), ExpressionWidth(b));
            bool cs = ExpressionSigned(a) && ExpressionSigned(b);
            string sa = CsExpr64(a, cw, cs), sb = CsExpr64(b, cw, cs);
            return k switch
            {
                "eq" => $"W4Ops.EqYes({sa},{sb})",
                "neq" => $"W4Ops.NeqYes({sa},{sb})",
                "caseeq" => $"W4Ops.Exact({sa},{sb})",
                _ => $"!W4Ops.Exact({sa},{sb})"
            };
        }
        return $"W4Ops.Yes({CsExpr64(e)})";
    }

    private string CsCond2(JsonElement e)
    {
        string k = Kind(e);
        if (k == "not")
        {
            var a = e.GetProperty("args")[0];
            return $"({CsExpr2(a)}==0UL)";
        }
        if (k is "and" or "or")
        {
            var args = e.GetProperty("args");
            var a = args[0];
            var b = args[1];
            if (!CsHasSideEffect(b))
                return $"({CsCond2(a)}{(k == "and" ? "&&" : "||")}{CsCond2(b)})";
        }
        if (k is "eq" or "neq" or "caseeq" or "caseneq")
        {
            var args = e.GetProperty("args");
            var a = args[0];
            var b = args[1];
            int cw = Math.Max(ExpressionWidth(a), ExpressionWidth(b));
            bool cs = ExpressionSigned(a) && ExpressionSigned(b);
            string op = k is "eq" or "caseeq" ? "==" : "!=";
            return $"({CsExpr2(a, cw, cs)}{op}{CsExpr2(b, cw, cs)})";
        }
        if (k is "lt" or "le" or "gt" or "ge")
        {
            var args = e.GetProperty("args");
            var a = args[0];
            var b = args[1];
            if (!(ExpressionSigned(a) && ExpressionSigned(b)))
            {
                int cw = Math.Max(ExpressionWidth(a), ExpressionWidth(b));
                string op = k switch { "lt" => "<", "le" => "<=", "gt" => ">", _ => ">=" };
                return $"({CsExpr2(a, cw, false)}{op}{CsExpr2(b, cw, false)})";
            }
        }
        return $"({CsExpr2(e)}!=0UL)";
    }

    private string CsExpr64(JsonElement e, int context = 0, bool? forceSigned = null)
    {
        string kind = Kind(e);
        int ew = ExpressionWidth(e);
        bool signed = forceSigned ?? ExpressionSigned(e);
        string Extend(string val) => signed && context > ew ? $"W4Ops.SResize({val},{ew},{CsMaskHex(ew)},{CsMaskHex(context)})" : val;
        if (kind == "ref") return Extend($"_s{SignalId(Name(e))}");
        if (kind == "const") return Extend(EmitCsConst4(e.GetProperty("bits").GetString()!));
        if (kind == "index")
        {
            int id = MemoryId(Name(e));
            return Extend($"_ReadMem{id}({CsMemIdx(e, id, false)})");
        }
        if (kind == "bitselect")
        {
            var valEl = e.GetProperty("value");
            var idxEl = e.GetProperty("index");
            int aw = ExpressionWidth(valEl);
            if (TryConstShift(idxEl, out int bi) && bi < aw)
                return bi == 0 ? $"W4Ops.Resize({CsExpr64(valEl)},0x1UL)" : $"W4Ops.SelPartFast({CsExpr64(valEl)},{bi},0x1UL)";
            return $"W4Ops.SelBit({CsExpr64(valEl)},{aw},{CsExpr64(idxEl)})";
        }
        if (kind == "partselect")
        {
            var valEl = e.GetProperty("value");
            int aw = ExpressionWidth(valEl), left = e.GetProperty("left").GetInt32(), right = e.GetProperty("right").GetInt32();
            if (right >= 0 && left < aw)
            {
                string m = CsMaskHex(left - right + 1);
                return right == 0
                    ? $"W4Ops.Resize({CsExpr64(valEl)},{m})"
                    : $"W4Ops.SelPartFast({CsExpr64(valEl)},{right},{m})";
            }
            return $"W4Ops.SelPart({CsExpr64(valEl)},{aw},{left},{right})";
        }
        if (kind == "indexedselect")
        {
            var valEl = e.GetProperty("value");
            return $"W4Ops.SelIdx({CsExpr64(valEl)},{ExpressionWidth(valEl)},{CsExpr64(e.GetProperty("base"))},{e.GetProperty("width").GetInt32()},{(e.GetProperty("descending").GetBoolean() ? "true" : "false")})";
        }
        if (kind == "test_plusargs") return Extend($"new W4(TestPlusargs({CppStringLiteral(e.GetProperty("prefix").GetString()!)}),0UL)");
        if (kind == "urandom") return Extend("new W4(NextUrandom(),0UL)");
        if (kind == "random") return Extend("new W4((uint)NextRandom(),0UL)");
        if (kind == "urandom_range")
        {
            string maxExpr = $"(uint)({CsExpr64(e.GetProperty("max"))}).A";
            string minExpr = e.TryGetProperty("min", out var minEl) ? $"(uint)({CsExpr64(minEl)}).A" : "0u";
            return Extend($"new W4(NextUrandomRange({maxExpr},{minExpr}),0UL)");
        }
        if (kind == "resolve")
        {
            int nk = ParseNetKind(e.GetProperty("net").GetString()!);
            int rw = Math.Max(context, ew);
            var drvEls = e.GetProperty("args").EnumerateArray().ToArray();
            if (drvEls.Length == 2)
                return Extend($"W4Ops.ResolveNet2({nk},{CsExpr64(drvEls[0], rw)},{CsExpr64(drvEls[1], rw)},{CsMaskHex(rw)})");
            string drivers = string.Join(", ", drvEls.Select(d => CsExpr64(d, rw)));
            return Extend($"W4Ops.ResolveNet({nk},[{drivers}],{CsMaskHex(rw)})");
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
                    string loCmp = cs ? $"W4Ops.SCmp({CsExpr64(valEl, cw, true)},{CsExpr64(loEl, cw, true)},{cw},3)" : $"W4Ops.Cmp({CsExpr64(valEl, cw, false)},{CsExpr64(loEl, cw, false)},3)";
                    string hiCmp = cs ? $"W4Ops.SCmp({CsExpr64(valEl, cw, true)},{CsExpr64(hiEl, cw, true)},{cw},1)" : $"W4Ops.Cmp({CsExpr64(valEl, cw, false)},{CsExpr64(hiEl, cw, false)},1)";
                    parts.Add($"W4Ops.LAnd({loCmp},{hiCmp})");
                }
                else
                {
                    int cw = Math.Max(vw, ExpressionWidth(it));
                    bool cs = vSigned && ExpressionSigned(it);
                    parts.Add($"W4Ops.WildEq({CsExpr64(valEl, cw, cs)},{CsExpr64(it, cw, cs)},{CsMaskHex(cw)})");
                }
            }
            string acc = parts[0];
            for (int i = 1; i < parts.Count; ++i) acc = $"W4Ops.LOr({acc},{parts[i]})";
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
            return $"new W4(W4Ops.Edge(_past_{sid}[0],_s{sid},true)?1UL:0UL,0UL)";
        }
        if (kind == "fell")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            return $"new W4(W4Ops.Edge(_past_{sid}[0],_s{sid},false)?1UL:0UL,0UL)";
        }
        if (kind == "stable")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            return $"new W4(W4Ops.Exact(_s{sid},_past_{sid}[0])?1UL:0UL,0UL)";
        }
        var args = e.GetProperty("args").EnumerateArray().ToArray();
        int width = Math.Max(context, ew);
        string wm = CsMaskHex(width);
        return kind switch
        {
            "castsigned" or "castunsigned" => Extend(CsExpr64(args[0])),
            "countones" => Extend($"W4Ops.CountOnes({CsExpr64(args[0])},{CsMaskHex(ExpressionWidth(args[0]))})"),
            "onehot" => $"W4Ops.OneHot({CsExpr64(args[0])},{CsMaskHex(ExpressionWidth(args[0]))})",
            "onehot0" => $"W4Ops.OneHot0({CsExpr64(args[0])},{CsMaskHex(ExpressionWidth(args[0]))})",
            "isunknown" => $"W4Ops.IsUnknown({CsExpr64(args[0])},{CsMaskHex(ExpressionWidth(args[0]))})",
            "clog2" => Extend($"W4Ops.CLog2({CsExpr64(args[0])},{CsMaskHex(ExpressionWidth(args[0]))})"),
            "add" or "sub" => $"W4Ops.Arith({CsExpr64(args[0], width, signed)},{CsExpr64(args[1], width, signed)},{wm},{(kind == "sub" ? "true" : "false")})",
            "bitnot" => $"W4Ops.BNot({CsExpr64(args[0], width, signed)},{wm})",
            "neg" => $"W4Ops.Arith(default,{CsExpr64(args[0], width, signed)},{wm},true)",
            "pos" => $"W4Ops.Resize({CsExpr64(args[0], width, signed)},{wm})",
            "mul" or "div" or "mod" => $"W4Ops.{(signed ? "SProduct" : "Product")}({CsExpr64(args[0], width, signed)},{CsExpr64(args[1], width, signed)},{width},{wm},{(kind == "mul" ? 0 : kind == "div" ? 1 : 2)})",
            "cond" => $"W4Ops.Cond({CsExpr64(args[0])},{CsExpr64(args[1], width, signed)},{CsExpr64(args[2], width, signed)},{wm})",
            "ashr" when signed => $"W4Ops.SShift({CsExpr64(args[0], width, true)},{CsExpr64(args[1])},{width},{wm})",
            "shl" or "shr" or "ashr" => $"W4Ops.Shift({CsExpr64(args[0], width, signed)},{CsExpr64(args[1])},{wm},{(kind == "shl" ? "false" : "true")})",
            "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor" => $"W4Ops.Red({CsExpr64(args[0])},{CsMaskHex(ExpressionWidth(args[0]))},{(kind == "redand" ? 0 : kind == "rednand" ? 1 : kind == "redor" ? 2 : kind == "rednor" ? 3 : kind == "redxor" ? 4 : 5)})",
            "bitxnor" => $"W4Ops.BNot(W4Ops.BBin({CsExpr64(args[0], width, signed)},{CsExpr64(args[1], width, signed)},{wm},2),{wm})",
            "bitand" or "bitor" or "bitxor" => $"W4Ops.BBin({CsExpr64(args[0], width, signed)},{CsExpr64(args[1], width, signed)},{wm},{(kind == "bitand" ? 0 : kind == "bitor" ? 1 : 2)})",
            "not" => $"W4Ops.LNot({CsExpr64(args[0])})",
            "and" => $"W4Ops.LAnd({CsExpr64(args[0])},{CsExpr64(args[1])})",
            "or" => $"W4Ops.LOr({CsExpr64(args[0])},{CsExpr64(args[1])})",
            "eq" or "neq" => (kind == "neq" ? "W4Ops.LNot(" : "") + $"W4Ops.Eq({CsExpr64(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{CsExpr64(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))})" + (kind == "neq" ? ")" : ""),
            "wildeq" or "wildneq" => $"W4Ops.{(kind == "wildneq" ? "WildNeq" : "WildEq")}({CsExpr64(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{CsExpr64(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{CsMaskHex(args.Max(ExpressionWidth))})",
            "caseeq" or "caseneq" => $"new W4({(kind == "caseneq" ? "!" : "")}W4Ops.Exact({CsExpr64(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{CsExpr64(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))})?1UL:0UL,0UL)",
            "lt" or "le" or "gt" or "ge" => args.All(ExpressionSigned)
                ? $"W4Ops.SCmp({CsExpr64(args[0], args.Max(ExpressionWidth), true)},{CsExpr64(args[1], args.Max(ExpressionWidth), true)},{args.Max(ExpressionWidth)},{(kind == "lt" ? 0 : kind == "le" ? 1 : kind == "gt" ? 2 : 3)})"
                : $"W4Ops.Cmp({CsExpr64(args[0], args.Max(ExpressionWidth), false)},{CsExpr64(args[1], args.Max(ExpressionWidth), false)},{(kind == "lt" ? 0 : kind == "le" ? 1 : kind == "gt" ? 2 : 3)})",
            "concat" => $"W4Ops.Concat({CsExpr64(args[0])},{CsExpr64(args[1])},{ExpressionWidth(args[1])},{CsMaskHex(ExpressionWidth(args[1]))})",
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
            return Extend($"_ReadMem{id}({CsMemIdx(e, id, true)})");
        }
        if (kind == "bitselect")
        {
            var valEl = e.GetProperty("value");
            var idxEl = e.GetProperty("index");
            int aw = ExpressionWidth(valEl);
            if (TryConstShift(idxEl, out int bi) && bi < aw)
                return bi == 0 ? $"(({CsExpr2(valEl)})&0x1UL)" : $"((({CsExpr2(valEl)})>>{bi})&0x1UL)";
            return $"W2Ops.SelBit({CsExpr2(valEl)},{aw},{CsExpr2(idxEl)})";
        }
        if (kind == "partselect")
        {
            var valEl = e.GetProperty("value");
            int aw = ExpressionWidth(valEl), left = e.GetProperty("left").GetInt32(), right = e.GetProperty("right").GetInt32();
            if (right >= 0 && left < aw)
            {
                string m = CsMaskHex(left - right + 1);
                return right == 0 ? $"(({CsExpr2(valEl)})&{m})" : $"((({CsExpr2(valEl)})>>{right})&{m})";
            }
            return $"W2Ops.SelPart({CsExpr2(valEl)},{aw},{left},{right})";
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
            return $"(((_past_{sid}[0]&1UL)==0UL&&(_s{sid}&1UL)!=0UL)?1UL:0UL)";
        }
        if (kind == "fell")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            return $"(((_past_{sid}[0]&1UL)!=0UL&&(_s{sid}&1UL)==0UL)?1UL:0UL)";
        }
        if (kind == "stable")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            return $"(_s{sid}==_past_{sid}[0]?1UL:0UL)";
        }
        var args = e.GetProperty("args").EnumerateArray().ToArray();
        int width = Math.Max(context, ew);
        string maskExpr = CsMaskHex(width);
        string MaskWrap(string inner) => width >= 64 ? $"({inner})" : $"(({inner})&{maskExpr})";
        return kind switch
        {
            "castsigned" or "castunsigned" => Extend(CsExpr2(args[0])),
            "countones" => Extend($"W2Ops.CountOnes({CsExpr2(args[0])},{ExpressionWidth(args[0])})"),
            "onehot" => $"W2Ops.OneHot({CsExpr2(args[0])},{ExpressionWidth(args[0])})",
            "onehot0" => $"W2Ops.OneHot0({CsExpr2(args[0])},{ExpressionWidth(args[0])})",
            "isunknown" => "0UL",
            "clog2" => Extend($"W2Ops.CLog2({CsExpr2(args[0])},{ExpressionWidth(args[0])})"),
            "add" => MaskWrap($"unchecked({CsExpr2(args[0], width, signed)}+{CsExpr2(args[1], width, signed)})"),
            "sub" => MaskWrap($"unchecked({CsExpr2(args[0], width, signed)}-{CsExpr2(args[1], width, signed)})"),
            "bitnot" => MaskWrap($"~({CsExpr2(args[0], width, signed)})"),
            "neg" => MaskWrap($"unchecked(0UL-{CsExpr2(args[0], width, signed)})"),
            "pos" => MaskWrap(CsExpr2(args[0], width, signed)),
            "mul" when !signed => MaskWrap($"unchecked({CsExpr2(args[0], width, false)}*{CsExpr2(args[1], width, false)})"),
            "mul" or "div" or "mod" => $"W2Ops.{(signed ? "SProduct" : "Product")}({CsExpr2(args[0], width, signed)},{CsExpr2(args[1], width, signed)},{width},{(kind == "mul" ? 0 : kind == "div" ? 1 : 2)})",
            "cond" => $"W2Ops.Cond({CsExpr2(args[0])},{CsExpr2(args[1], width, signed)},{CsExpr2(args[2], width, signed)},{width})",
            "ashr" when signed => $"W2Ops.SShift({CsExpr2(args[0], width, true)},{CsExpr2(args[1])},{width})",
            "shl" when TryConstShift(args[1], out int shL) => MaskWrap($"({CsExpr2(args[0], width, signed)}<<{shL})"),
            "shr" or "ashr" when TryConstShift(args[1], out int shR) => $"({MaskWrap(CsExpr2(args[0], width, signed))}>>{shR})",
            "shl" or "shr" or "ashr" => $"W2Ops.Shift({CsExpr2(args[0], width, signed)},{CsExpr2(args[1])},{width},{(kind == "shl" ? "false" : "true")})",
            "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor" => $"W2Ops.Red({CsExpr2(args[0])},{ExpressionWidth(args[0])},{(kind == "redand" ? 0 : kind == "rednand" ? 1 : kind == "redor" ? 2 : kind == "rednor" ? 3 : kind == "redxor" ? 4 : 5)})",
            "bitxnor" => MaskWrap($"~({CsExpr2(args[0], width, signed)}^{CsExpr2(args[1], width, signed)})"),
            "bitand" => MaskWrap($"{CsExpr2(args[0], width, signed)}&{CsExpr2(args[1], width, signed)}"),
            "bitor" => MaskWrap($"{CsExpr2(args[0], width, signed)}|{CsExpr2(args[1], width, signed)}"),
            "bitxor" => MaskWrap($"{CsExpr2(args[0], width, signed)}^{CsExpr2(args[1], width, signed)}"),
            "not" => $"({CsExpr2(args[0])}==0UL?1UL:0UL)",
            "and" => $"(({CsExpr2(args[0])}!=0UL&&{CsExpr2(args[1])}!=0UL)?1UL:0UL)",
            "or" => $"(({CsExpr2(args[0])}!=0UL||{CsExpr2(args[1])}!=0UL)?1UL:0UL)",
            "eq" or "neq" or "caseeq" or "caseneq" or "wildeq" or "wildneq" =>
                $"({CsExpr2(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))}{(kind is "neq" or "caseneq" or "wildneq" ? "!=" : "==")}{CsExpr2(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))}?1UL:0UL)",
            "lt" or "le" or "gt" or "ge" =>
                args.All(ExpressionSigned)
                    ? $"W2Ops.SCmp({CsExpr2(args[0], args.Max(ExpressionWidth), true)},{CsExpr2(args[1], args.Max(ExpressionWidth), true)},{args.Max(ExpressionWidth)},{(kind == "lt" ? 0 : kind == "le" ? 1 : kind == "gt" ? 2 : 3)})"
                    : $"W2Ops.Cmp({CsExpr2(args[0], args.Max(ExpressionWidth), false)},{CsExpr2(args[1], args.Max(ExpressionWidth), false)},{(kind == "lt" ? 0 : kind == "le" ? 1 : kind == "gt" ? 2 : 3)})",
            "concat" => ExpressionWidth(args[1]) >= 64
                ? CsExpr2(args[1])
                : $"(({CsExpr2(args[0])}<<{ExpressionWidth(args[1])})|({CsExpr2(args[1])}&{CsMaskHex(ExpressionWidth(args[1]))}))",
            _ => throw new IrValidationException("Unsupported C# two-state expression.")
        };
    }

    private static string CsStore(string val, RtlSignal signal, bool twoState, int exprWidth = -1)
    {
        if (twoState)
        {
            if (exprWidth == signal.Width || signal.Width >= 64) return val;
            return $"(({val})&{CsMaskHex(signal.Width)})";
        }
        string sm = CsMaskHex(signal.Width);
        return signal.TwoState
            ? $"W4Ops.TwoState({val},{sm})"
            : (exprWidth == signal.Width ? val : $"W4Ops.Resize({val},{sm})");
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
                : $"new W4({target.GetProperty("right").GetInt32()}UL,0UL)";
            string dep = $"W4Ops.DepIdx(_next{sid},{Signals[sid].Width},{basis},{width},{(desc ? "true" : "false")},{value})";
            code.AppendLine($"_next{sid}={(Signals[sid].TwoState ? $"W4Ops.TwoState({dep},{CsMaskHex(Signals[sid].Width)})" : dep)};");
        }
    }

    private void CsStmt(StringBuilder code, JsonElement e, ref int unique, bool twoState, bool trackBudget, HashSet<int> singleWriteMems)
    {
        if (trackBudget) code.AppendLine("W64Ops.Step(ref _budget);");
        switch (Kind(e))
        {
            case "while":
            case "do":
                string cond = twoState ? CsCond2(e.GetProperty("condition")) : CsCond64(e.GetProperty("condition"));
                if (Kind(e) == "do") code.AppendLine("do{");
                else code.AppendLine($"while({cond}){{");
                code.AppendLine("W64Ops.Step(ref _budget);");
                CsStmt(code, e.GetProperty("body"), ref unique, twoState, trackBudget, singleWriteMems);
                code.AppendLine(Kind(e) == "do" ? $"}}while({cond});" : "}");
                break;
            case "runtime_repeat":
                string rl = "rrep" + unique++;
                var cntEl = e.GetProperty("count");
                string cnt = twoState
                    ? $"W2Ops.RepeatCount({CsExpr2(cntEl)},{ExpressionWidth(cntEl)},{(ExpressionSigned(cntEl) ? "true" : "false")})"
                    : $"W4Ops.RepeatCount({CsExpr64(cntEl)},{ExpressionWidth(cntEl)},{(ExpressionSigned(cntEl) ? "true" : "false")})";
                code.AppendLine($"for(uint {rl}={cnt};{rl}>0;--{rl}){{");
                code.AppendLine("W64Ops.Step(ref _budget);");
                CsStmt(code, e.GetProperty("body"), ref unique, twoState, trackBudget, singleWriteMems);
                code.AppendLine("}");
                break;
            case "repeat":
                string lp = "rep" + unique++;
                code.AppendLine($"for(int {lp}=0;{lp}<{e.GetProperty("count").GetInt32()};++{lp}){{");
                CsStmt(code, e.GetProperty("body"), ref unique, twoState, trackBudget, singleWriteMems);
                code.AppendLine("}");
                break;
            case "block":
                foreach (var item in e.GetProperty("items").EnumerateArray()) CsStmt(code, item, ref unique, twoState, trackBudget, singleWriteMems);
                break;
            case "nba":
                var target = e.GetProperty("target");
                if (Kind(target) == "concat")
                {
                    int total = ExpressionWidth(target), remaining = total;
                    string rhs = "nba" + unique++;
                    code.AppendLine(twoState
                        ? $"{{ulong {rhs}=W2Ops.Resize({CsExpr2(e.GetProperty("value"), total)},{total});"
                        : $"{{W4 {rhs}=W4Ops.Resize({CsExpr64(e.GetProperty("value"), total)},{CsMaskHex(total)});");
                    foreach (var leaf in TargetLeaves(target))
                    {
                        int width = ExpressionWidth(leaf);
                        remaining -= width;
                        string part = twoState
                            ? $"W2Ops.SelPart({rhs},{total},{remaining + width - 1},{remaining})"
                            : $"W4Ops.SelPartFast({rhs},{remaining},{CsMaskHex(width)})";
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
                    int depth = Memories[id].Depth;
                    if (singleWriteMems.Contains(id))
                    {
                        if (twoState)
                        {
                            string v = CsExpr2(e.GetProperty("value"), Memories[id].Width);
                            string vm = Memories[id].Width >= 64 ? v : $"(({v})&{CsMaskHex(Memories[id].Width)})";
                            code.AppendLine($"{{ulong _mi={CsMemIdx(target, id, true)};if(_mi<{depth}UL){{_mw{id}_idx=(int)_mi;_mw{id}_val={vm};}}}}");
                        }
                        else
                        {
                            string mm = CsMaskHex(Memories[id].Width);
                            string v = CsExpr64(e.GetProperty("value"), Memories[id].Width);
                            v = Memories[id].TwoState ? $"W4Ops.TwoState({v},{mm})" : $"W4Ops.Resize({v},{mm})";
                            code.AppendLine($"{{W4 _mi={CsMemIdx(target, id, false)};if(_mi.B==0UL&&_mi.A<{depth}UL){{_mw{id}_idx=(int)_mi.A;_mw{id}_val={v};}}}}");
                        }
                    }
                    else if (twoState)
                        code.AppendLine($"_mlog.Push({id},{depth},{CsMemIdx(target, id, true)},{CsExpr2(e.GetProperty("value"), Memories[id].Width)},{Memories[id].Width});");
                    else
                    {
                        string v = CsExpr64(e.GetProperty("value"), Memories[id].Width);
                        string mm = CsMaskHex(Memories[id].Width);
                        if (Memories[id].TwoState) v = $"W4Ops.TwoState({v},{mm})";
                        code.AppendLine($"_mlog.Push({id},{depth},{CsMemIdx(target, id, false)},{v},{mm});");
                    }
                }
                break;
            case "if":
                string ifCond = twoState ? CsCond2(e.GetProperty("condition")) : CsCond64(e.GetProperty("condition"));
                code.AppendLine($"if({ifCond}){{");
                CsStmt(code, e.GetProperty("then"), ref unique, twoState, trackBudget, singleWriteMems);
                code.AppendLine("}else{");
                CsStmt(code, e.GetProperty("else"), ref unique, twoState, trackBudget, singleWriteMems);
                code.AppendLine("}");
                break;
            case "assert":
            {
                string condExpr = twoState ? CsCond2(e.GetProperty("condition")) : CsCond64(e.GetProperty("condition"));
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
                        ? string.Join(", ", fa.EnumerateArray().Select(a => twoState ? $"new W64({CsExpr2(a)},0UL,0UL,{ExpressionWidth(a)})" : $"({CsExpr64(a)}).ToW64({ExpressionWidth(a)})"))
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
                    ? string.Join(", ", dArgs.EnumerateArray().Select(a => twoState ? $"new W64({CsExpr2(a)},0UL,0UL,{ExpressionWidth(a)})" : $"({CsExpr64(a)}).ToW64({ExpressionWidth(a)})"))
                    : "";
                code.AppendLine($"EmitDisplay(W64Ops.FormatDisplay({fmt}, [{argList}], {(nl ? "true" : "false")}));");
                break;
            }
            case "value_plusargs":
            {
                string fmt = CppStringLiteral(e.GetProperty("format").GetString()!);
                int tid = SignalId(e.GetProperty("target").GetString()!);
                var tsig = Signals[tid];
                code.AppendLine($"{{W64 _vp = {(twoState ? $"new W64(_next{tid},0UL,0UL,{tsig.Width})" : $"_next{tid}.ToW64({tsig.Width})")}; uint _vpm = ValuePlusargs({fmt}, ref _vp); if (_vpm != 0u) _next{tid} = {CsStore(twoState ? "_vp.V" : $"W4.FromW64(_vp,{CsMaskHex(tsig.Width)})", tsig, twoState)};");
                if (e.TryGetProperty("matched", out var vpMatched) && vpMatched.ValueKind != JsonValueKind.Null)
                {
                    int mid = SignalId(vpMatched.GetString()!);
                    code.AppendLine($"_next{mid} = {CsStore(twoState ? "(ulong)_vpm" : "new W4(_vpm,0UL)", Signals[mid], twoState)};");
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
                    : $"({CsStore(CsExpr64(e.GetProperty("value")), tsig, false)}).ToW64({tsig.Width})";
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
                var allMatches = items.SelectMany(CaseItemMatches).ToArray();
                int cw = Math.Max(ExpressionWidth(e.GetProperty("value")), allMatches.Select(ExpressionWidth).DefaultIfEmpty(1).Max());
                string cwm = CsMaskHex(cw);
                string ck = CaseKind(e);
                bool csSigned = ExpressionSigned(e.GetProperty("value")) && allMatches.All(ExpressionSigned);
                code.AppendLine(twoState
                    ? $"{{ulong {lbl}={CsExpr2(e.GetProperty("value"), cw, csSigned)};"
                    : $"{{W4 {lbl}={CsExpr64(e.GetProperty("value"), cw, csSigned)};");
                bool first = true;
                foreach (var item in items.Where(i => !IsCaseDefault(i)))
                {
                    var ms = CaseItemMatches(item);
                    string cmp = twoState
                        ? string.Join(" || ", ms.Select(m => $"{lbl}=={CsExpr2(m, cw, csSigned)}"))
                        : string.Join(" || ", ms.Select(m => ck switch
                        {
                            "casez" => $"W4Ops.CasezMatch({lbl},{CsExpr64(m, cw, csSigned)},{cwm})",
                            "casex" => $"W4Ops.CasexMatch({lbl},{CsExpr64(m, cw, csSigned)},{cwm})",
                            _ => $"W4Ops.Exact({lbl},{CsExpr64(m, cw, csSigned)})"
                        }));
                    code.AppendLine($"{(first ? "if" : "else if")}({cmp}){{");
                    CsStmt(code, item.GetProperty("body"), ref unique, twoState, trackBudget, singleWriteMems);
                    code.AppendLine("}");
                    first = false;
                }
                var defItem = items.FirstOrDefault(IsCaseDefault);
                if (defItem.ValueKind != JsonValueKind.Undefined)
                {
                    code.AppendLine(first ? "{" : "else{");
                    CsStmt(code, defItem.GetProperty("body"), ref unique, twoState, trackBudget, singleWriteMems);
                    code.AppendLine("}");
                }
                code.AppendLine("}");
                break;
            }
        }
    }

    /// <summary>Emits a standalone C# model class using register-resident W4 (16-byte 4-state) or ulong (2-state) storage.</summary>
    public string EmitCSharpModel(string top, RtlStateMode stateMode = RtlStateMode.FourState, string @namespace = "VeloSim.Generated")
    {
        if (!Identifier(top) || ("V" + top).Contains("__", StringComparison.Ordinal)) throw new ArgumentException("Invalid external model top name.", nameof(top));
        if (!CanUsePortable64(stateMode))
            throw new IrValidationException("C# target currently supports non-timed <=64-bit event-driven and combinational designs without initialization blocks or dynamic, queue, associative, or multidimensional arrays.");
        bool twoState = stateMode == RtlStateMode.TwoState;
        string className = "V" + top;
        var names = CSharpPortNames();
        if (names.Values.Contains(className)) throw new ArgumentException("Port conflicts with generated class name.", nameof(top));
        string wordType = twoState ? "ulong" : "W4";
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
        var singleWriteMems = new HashSet<int>();
        bool needsMemLog = false;
        if (!CombinationalOnly)
        {
            for (int i = 0; i < Memories.Count; ++i)
            {
                int maxW = CsMaxMemWrites(Body, i, false, out bool inLoop);
                if (maxW == 1 && !inLoop) singleWriteMems.Add(i);
                else if (maxW > 0) needsMemLog = true;
            }
        }
        bool hasHeapMems = Enumerable.Range(0, Memories.Count).Any(i => !CsIsInlineMem(i));
        bool bodyUsesForces = Statements(Body).Any(e => Kind(e) is "force" or "release");
        bool combPureInternal = !CombinationalOnly && !bodyUsesForces && !canThrow && !hasRuntimeLoops
            && Properties.Count == 0 && PastDepths.Count == 0
            && SettledSteps.Count > 0
            && SettledSteps.All(s => CsExprIsPureInternal(s.GetProperty("value")));

        var code = new StringBuilder();
        code.AppendLine("// <auto-generated/>");
        code.AppendLine("#nullable enable");
        code.AppendLine("using System;");
        code.AppendLine("using System.Collections.Generic;");
        code.AppendLine("using System.Runtime.CompilerServices;");
        if (hasHeapMems) code.AppendLine("using System.Runtime.InteropServices;");
        code.AppendLine($"namespace {@namespace};");
        code.AppendLine();
        code.AppendLine($"public sealed class {className} : IVpiModel");
        code.AppendLine("{");
        if (hasRuntimeLoops) code.AppendLine("    private int _budget = 20000;");
        code.AppendLine("    private ulong _time;");
        code.AppendLine("    private bool _finished = false;");
        code.AppendLine("    private int _exitCode = 0;");
        code.AppendLine("    private VcdWriter? _vcd;");
        code.AppendLine("    private readonly PlusargStore _plusargs = new();");
        code.AppendLine("    private SplitMix64Rng _rng = new();");
        code.AppendLine($"    private Dictionary<int, {wordType}>? _forced;");
        code.AppendLine("    private bool _hasForces;");
        if (combPureInternal) code.AppendLine("    private bool _evalSlow = true;");
        code.AppendLine("    public bool GotFinish => _finished;");
        code.AppendLine("    public void Finish() => _finished = true;");
        code.AppendLine("    public IReadOnlyList<string> Plusargs => _plusargs.List;");
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
                    code.AppendLine($"    private ulong _propPipe_{i};");
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
        string markSlow = combPureInternal ? " _evalSlow = true;" : "";
        for (int i = 0; i < Signals.Count; ++i)
        {
            var sig = Signals[i];
            if (twoState)
                code.AppendLine($"            case {i}: {{ ulong fv = W2Ops.Resize(value.V, {sig.Width}); _forced[{i}] = fv; _s{i} = fv; _hasForces = true;{markSlow} break; }}");
            else
            {
                string sm = CsMaskHex(sig.Width);
                string fv = sig.TwoState ? $"W4Ops.TwoState(W4.FromW64(value,{sm}),{sm})" : $"W4.FromW64(value,{sm})";
                code.AppendLine($"            case {i}: {{ W4 fv = {fv}; _forced[{i}] = fv; _s{i} = fv; _hasForces = true;{markSlow} break; }}");
            }
        }
        code.AppendLine("            default: throw new ArgumentOutOfRangeException(nameof(signalId));");
        code.AppendLine("        }");
        code.AppendLine("    }");
        code.AppendLine($"    public void ReleaseSignal(int signalId) {{ if (_forced is not null) {{ _forced.Remove(signalId); _hasForces = _forced.Count > 0; }}{markSlow} }}");
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
        for (int i = 0; i < Memories.Count; ++i)
        {
            int depth = Memories[i].Depth;
            string mm = CsMaskHex(Memories[i].Width);
            if (CsIsInlineMem(i))
            {
                code.AppendLine($"    [InlineArray({depth})] private struct _MemBuf{i} {{ private {wordType} _e0; }}");
                code.AppendLine($"    private _MemBuf{i} _m{i};");
                if (twoState)
                {
                    code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] private ulong _ReadMem{i}(ulong idx) => idx < {depth}UL ? Unsafe.Add(ref _m{i}[0], (nint)idx) : 0UL;");
                    code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] private void _WriteMem{i}(int idx, ulong val) => Unsafe.Add(ref _m{i}[0], (nint)idx) = val;");
                }
                else
                {
                    code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] private W4 _ReadMem{i}(W4 idx) => (idx.B == 0UL && idx.A < {depth}UL) ? Unsafe.Add(ref _m{i}[0], (nint)idx.A) : new W4({mm}, {mm});");
                    code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] private void _WriteMem{i}(int idx, W4 val) => Unsafe.Add(ref _m{i}[0], (nint)idx) = val;");
                }
            }
            else
            {
                code.AppendLine($"    private readonly {wordType}[] _m{i} = new {wordType}[{depth}];");
                if (twoState)
                {
                    code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] private ulong _ReadMem{i}(ulong idx) => W2Ops.Read(_m{i}, idx);");
                    code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] private void _WriteMem{i}(int idx, ulong val) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_m{i}), (nint)idx) = val;");
                }
                else
                {
                    code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] private W4 _ReadMem{i}(W4 idx) => W4Ops.Read(_m{i}, idx, {mm});");
                    code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] private void _WriteMem{i}(int idx, W4 val) => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_m{i}), (nint)idx) = val;");
                }
            }
        }
        if (needsMemLog)
        {
            code.AppendLine($"    private readonly {(twoState ? "MemLog2" : "MemLog4")} _mlog = new();");
            code.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
            code.AppendLine("    private void _CommitMemLog()");
            code.AppendLine("    {");
            code.AppendLine("        int c = _mlog.Count;");
            code.AppendLine("        for (int i = 0; i < c; ++i)");
            code.AppendLine("        {");
            code.AppendLine("            switch (_mlog.MemAt(i))");
            code.AppendLine("            {");
            for (int i = 0; i < Memories.Count; ++i)
                code.AppendLine($"                case {i}: _WriteMem{i}(_mlog.IdxAt(i), _mlog.ValAt(i)); break;");
            code.AppendLine("            }");
            code.AppendLine("        }");
            code.AppendLine("        var ov = _mlog.Overflow;");
            code.AppendLine("        if (ov is { Count: > 0 })");
            code.AppendLine("        {");
            code.AppendLine("            foreach (var e in ov)");
            code.AppendLine("            {");
            code.AppendLine("                switch (e.mem)");
            code.AppendLine("                {");
            for (int i = 0; i < Memories.Count; ++i)
                code.AppendLine($"                    case {i}: _WriteMem{i}(e.idx, e.val); break;");
            code.AppendLine("                }");
            code.AppendLine("            }");
            code.AppendLine("            ov.Clear();");
            code.AppendLine("        }");
            code.AppendLine("        _mlog.ResetCount();");
            code.AppendLine("    }");
        }
        code.AppendLine();
        code.AppendLine($"    public {className}()");
        code.AppendLine("    {");
        for (int i = 0; i < Signals.Count; ++i)
        {
            var sig = Signals[i];
            if (twoState) code.AppendLine($"        _s{i} = 0UL;");
            else if (sig.Name == Clock || sig.TwoState) code.AppendLine($"        _s{i} = default;");
            else code.AppendLine($"        _s{i} = W4Ops.Unknown({CsMaskHex(sig.Width)});");
        }
        foreach (int id in eventIds)
        {
            var sig = Signals[id];
            if (twoState) code.AppendLine($"        _prev{id} = 0UL;");
            else if (sig.Name == Clock || sig.TwoState) code.AppendLine($"        _prev{id} = default;");
            else code.AppendLine($"        _prev{id} = new W4(1UL,1UL);");
        }
        for (int i = 0; i < Memories.Count; ++i)
        {
            var mem = Memories[i];
            if (!twoState && !mem.TwoState)
            {
                if (CsIsInlineMem(i))
                    code.AppendLine($"        for (int j = 0; j < {mem.Depth}; ++j) _m{i}[j] = W4Ops.Unknown({CsMaskHex(mem.Width)});");
                else
                    code.AppendLine($"        Array.Fill(_m{i}, W4Ops.Unknown({CsMaskHex(mem.Width)}));");
            }
        }
        foreach (var (sid, depth) in PastDepths.OrderBy(kv => kv.Key))
        {
            var sig = Signals[sid];
            if (!twoState && !sig.TwoState)
                code.AppendLine($"        Array.Fill(_past_{sid}, W4Ops.Unknown({CsMaskHex(sig.Width)}));");
        }
        foreach (int cid in PropertyClockSignalIds())
            code.AppendLine(twoState ? $"        _pclk_{cid} = 0UL;" : $"        _pclk_{cid} = default;");
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
        code.AppendJoin(", ", Signals.Select((s, idx) => twoState ? $"new W64(_s{idx}, 0UL, 0UL, {s.Width})" : $"_s{idx}.ToW64({s.Width})"));
        code.AppendLine("]);");
        code.AppendLine("    }");
        code.AppendLine("    public void CloseVcd() { _vcd?.Dispose(); _vcd = null; }");
        // Persistent binary checkpoint with schema hash and FNV-1a integrity verification.
        code.AppendLine($"    private static ulong CheckpointSchemaHash() => CheckpointIO.SchemaHash(\"{top}\", {(twoState ? "true" : "false")}, _signalSchema, [{string.Join(", ", Memories.Select(m => $"({m.Depth}, {m.Width})"))}]);");
        code.AppendLine("    public void SaveCheckpoint(string path)");
        code.AppendLine("    {");
        code.Append("        W64[] sigs = [");
        code.AppendJoin(", ", Signals.Select((s, idx) => twoState ? $"new W64(_s{idx}, 0UL, 0UL, {s.Width})" : $"_s{idx}.ToW64({s.Width})"));
        code.AppendLine("];");
        code.Append("        W64[] prevs = [");
        code.AppendJoin(", ", eventIds.Select(id => twoState ? $"new W64(_prev{id}, 0UL, 0UL, 1)" : $"_prev{id}.ToW64(1)"));
        code.AppendLine("];");
        code.AppendLine($"        W64[][] mems = new W64[{Memories.Count}][];");
        for (int i = 0; i < Memories.Count; ++i)
        {
            int depth = Memories[i].Depth;
            if (twoState)
                code.AppendLine($"        {{ var a = new W64[{depth}]; for (int j = 0; j < {depth}; ++j) a[j] = new W64(_m{i}[j], 0UL, 0UL, {Memories[i].Width}); mems[{i}] = a; }}");
            else
                code.AppendLine($"        {{ var a = new W64[{depth}]; for (int j = 0; j < {depth}; ++j) a[j] = _m{i}[j].ToW64({Memories[i].Width}); mems[{i}] = a; }}");
        }
        code.AppendLine("        CheckpointIO.Save(path, CheckpointSchemaHash(), _time, _rng.State, sigs, prevs, mems);");
        code.AppendLine("    }");
        code.AppendLine("    public void RestoreCheckpoint(string path)");
        code.AppendLine("    {");
        code.AppendLine($"        var (t, rngState, sigs, prevs, mems) = CheckpointIO.Load(path, CheckpointSchemaHash(), {Signals.Count}, {eventIds.Length}, [{string.Join(", ", Memories.Select(m => m.Depth))}]);");
        code.AppendLine("        _time = t; _rng.State = rngState;");
        for (int i = 0; i < Signals.Count; ++i)
            code.AppendLine(twoState ? $"        _s{i} = W2Ops.Resize(sigs[{i}].V, {Signals[i].Width});" : $"        _s{i} = W4.FromW64(sigs[{i}], {CsMaskHex(Signals[i].Width)});");
        for (int i = 0; i < eventIds.Length; ++i)
            code.AppendLine(twoState ? $"        _prev{eventIds[i]} = prevs[{i}].V & 1UL;" : $"        _prev{eventIds[i]} = W4.FromW64(prevs[{i}], 1UL);");
        for (int i = 0; i < Memories.Count; ++i)
        {
            int depth = Memories[i].Depth;
            if (twoState)
                code.AppendLine($"        for (int j = 0; j < {depth}; ++j) _m{i}[j] = W2Ops.Resize(mems[{i}][j].V, {Memories[i].Width});");
            else
                code.AppendLine($"        for (int j = 0; j < {depth}; ++j) _m{i}[j] = W4.FromW64(mems[{i}][j], {CsMaskHex(Memories[i].Width)});");
        }
        if (combPureInternal) code.AppendLine("        _evalSlow = true;");
        code.AppendLine("    }");
        code.AppendLine();
        foreach (var sig in Signals.Where(s => s.Direction != "internal"))
        {
            int id = SignalId(sig.Name);
            string prop = names[sig.Name];
            string sm = CsMaskHex(sig.Width);
            if (sig.Direction == "input")
            {
                if (twoState)
                {
                    code.AppendLine($"    public ulong {prop} {{ [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _s{id}; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => _s{id} = {(sig.Width >= 64 ? "value" : $"value & {sm}")}; }}");
                    code.AppendLine($"    public W64 Get_{prop}_Word() => new W64(_s{id}, 0UL, 0UL, {sig.Width});");
                    code.AppendLine($"    public void Set_{prop}_Word(W64 value) => _s{id} = value.V & ~(value.X | value.Z) & {sm};");
                    code.AppendLine($"    public string Get_{prop}_Bits() => W64Ops.Text(new W64(_s{id}, 0UL, 0UL, {sig.Width}));");
                    code.AppendLine($"    public void Set_{prop}_Bits(string bits) {{ var w = W64Ops.Parse(bits, {sig.Width}); _s{id} = w.V & ~(w.X | w.Z) & {sm}; }}");
                }
                else
                {
                    code.AppendLine($"    public ulong {prop} {{ [MethodImpl(MethodImplOptions.AggressiveInlining)] get => W4Ops.ToUInt64(_s{id}); [MethodImpl(MethodImplOptions.AggressiveInlining)] set => _s{id} = new W4({(sig.Width >= 64 ? "value" : $"value & {sm}")}, 0UL); }}");
                    code.AppendLine($"    public W64 Get_{prop}_Word() => _s{id}.ToW64({sig.Width});");
                    code.AppendLine($"    public void Set_{prop}_Word(W64 value) => _s{id} = {(sig.TwoState ? $"W4Ops.TwoState(W4.FromW64(value, {sm}), {sm})" : $"W4.FromW64(value, {sm})")};");
                    code.AppendLine($"    public string Get_{prop}_Bits() => W64Ops.Text(_s{id}.ToW64({sig.Width}));");
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
                    code.AppendLine($"    public ulong {prop} {{ [MethodImpl(MethodImplOptions.AggressiveInlining)] get => W4Ops.ToUInt64(_s{id}); }}");
                    code.AppendLine($"    public W64 Get_{prop}_Word() => _s{id}.ToW64({sig.Width});");
                    code.AppendLine($"    public string Get_{prop}_Bits() => W64Ops.Text(_s{id}.ToW64({sig.Width}));");
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

        bool canOutlineEdge = !CombinationalOnly && !bodyUsesForces && !canThrow && !hasRuntimeLoops
            && Properties.Count == 0 && PastDepths.Count == 0;

        string RenderSeqEdgeBody(bool withForces)
        {
            var sb = new StringBuilder();
            foreach (int id in seqIds) sb.AppendLine($"        {wordType} _next{id} = _s{id};");
            foreach (int id in singleWriteMems.Order())
            {
                sb.AppendLine($"        int _mw{id}_idx = -1;");
                sb.AppendLine($"        {wordType} _mw{id}_val = default;");
            }
            int unique = 0;
            for (int p = 0; p < procs.Length; ++p)
            {
                if (procs.Length > 1) sb.AppendLine($"        if (ev{p}) {{");
                CsStmt(sb, procs[p].GetProperty("body"), ref unique, twoState, hasRuntimeLoops, singleWriteMems);
                if (procs.Length > 1) sb.AppendLine("        }");
            }
            foreach (int id in seqIds) sb.AppendLine($"        _s{id} = _next{id};");
            foreach (int id in singleWriteMems.Order())
                sb.AppendLine($"        if ((uint)_mw{id}_idx < {Memories[id].Depth}U) _WriteMem{id}(_mw{id}_idx, _mw{id}_val);");
            if (needsMemLog) sb.AppendLine("        _CommitMemLog();");
            sb.AppendLine("        Settle();");
            if (withForces) sb.AppendLine("        if (_hasForces) ApplyForces();");
            return sb.ToString();
        }

        string fastSeqBody = canOutlineEdge ? RenderSeqEdgeBody(withForces: false) : "";
        bool outlineEdge = canOutlineEdge && fastSeqBody.Length > (twoState ? 2500 : 750);

        if (outlineEdge)
        {
            string evParams = procs.Length > 1 ? string.Join(", ", Enumerable.Range(0, procs.Length).Select(p => $"bool ev{p}")) : "";
            code.AppendLine($"    private void EvalEdge({evParams})");
            code.AppendLine("    {");
            code.Append(fastSeqBody);
            code.AppendLine("    }");
            code.AppendLine();
        }

        void EmitEvalBody(bool withForces, bool skipPreSettle, bool useOutlineEdge)
        {
            if (hasRuntimeLoops) code.AppendLine("        _budget = 20000;");
            if (canThrow)
            {
                for (int i = 0; i < Signals.Count; ++i) code.AppendLine($"        var sv_s{i} = _s{i};");
                foreach (int id in eventIds) code.AppendLine($"        var sv_p{id} = _prev{id};");
                for (int i = 0; i < Memories.Count; ++i)
                    code.AppendLine(CsIsInlineMem(i) ? $"        var sv_m{i} = _m{i};" : $"        var sv_m{i} = ({wordType}[])_m{i}.Clone();");
                code.AppendLine("        try {");
            }
            if (withForces) code.AppendLine("        if (_hasForces) ApplyForces();");
            if (!skipPreSettle) code.AppendLine("        Settle();");
            if (withForces) code.AppendLine("        if (_hasForces) ApplyForces();");
            if (Properties.Count > 0 || PastDepths.Count > 0)
            {
                for (int i = 0; i < Properties.Count; ++i)
                {
                    var p = Properties[i];
                    int cid = SignalId(p.Clock);
                    string edgeCond = twoState
                        ? (p.Edge == "posedge" ? $"((_pclk_{cid}&1UL)==0UL&&(_s{cid}&1UL)!=0UL)" : $"((_pclk_{cid}&1UL)!=0UL&&(_s{cid}&1UL)==0UL)")
                        : $"W4Ops.Edge(_pclk_{cid},_s{cid},{(p.Edge == "posedge" ? "true" : "false")})";
                    code.AppendLine($"        if ({edgeCond}) {{");
                    if (p.DisableIff is { } dis)
                    {
                        string disCond = twoState ? $"({CsExpr2(dis)}!=0UL)" : $"W4Ops.Yes({CsExpr64(dis)})";
                        code.AppendLine($"            if ({disCond}) {{");
                        if (p.Delay > 0) code.AppendLine($"                _propPipe_{i} = 0UL;");
                        code.AppendLine("            } else {");
                    }
                    string antCond = p.Antecedent is { } ant
                        ? (twoState ? $"({CsExpr2(ant)}!=0UL)" : $"W4Ops.Yes({CsExpr64(ant)})")
                        : "true";
                    string consCond = twoState ? $"({CsExpr2(p.Consequent)}!=0UL)" : $"W4Ops.Yes({CsExpr64(p.Consequent)})";
                    string failMsg = CppStringLiteral(string.IsNullOrEmpty(p.Message) ? $"SVA property '{p.Name}' failed" : $"SVA property '{p.Name}' failed: {p.Message}");
                    if (p.Delay == 0)
                    {
                        code.AppendLine($"            if ({antCond}) {{ if ({consCond}) {{ ++PropertyPasses[{i}]; }} else {{ ++PropertyFailures[{i}]; {(p.Kind == "assert" ? $"throw new InvalidOperationException({failMsg});" : "")} }} }}");
                        if (p.Kind != "cover") code.AppendLine($"            else {{ ++PropertyVacuous[{i}]; }}");
                    }
                    else
                    {
                        ulong dueMask = 1UL << (p.Delay - 1);
                        code.AppendLine($"            if ((_propPipe_{i} & 0x{dueMask:x}UL) != 0UL) {{ if ({consCond}) {{ ++PropertyPasses[{i}]; }} else {{ ++PropertyFailures[{i}]; {(p.Kind == "assert" ? $"throw new InvalidOperationException({failMsg});" : "")} }} }}");
                        code.AppendLine($"            bool _ant_{i} = {antCond}; _propPipe_{i} = (_propPipe_{i} << 1) | (_ant_{i} ? 1UL : 0UL);");
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
                        ? (pastEdge == "posedge" ? $"((_pclk_{pastCid}&1UL)==0UL&&(_s{pastCid}&1UL)!=0UL)" : $"((_pclk_{pastCid}&1UL)!=0UL&&(_s{pastCid}&1UL)==0UL)")
                        : $"W4Ops.Edge(_pclk_{pastCid},_s{pastCid},{(pastEdge == "posedge" ? "true" : "false")})";
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
                            ? (pos ? $"((_prev{id}&1UL)==0UL&&(_s{id}&1UL)!=0UL)" : $"((_prev{id}&1UL)!=0UL&&(_s{id}&1UL)==0UL)")
                            : $"W4Ops.Edge(_prev{id},_s{id},{(pos ? "true" : "false")})";
                    }));
                    code.AppendLine($"        bool ev{p} = {cond};");
                }
                string anyEv = string.Join(" || ", Enumerable.Range(0, procs.Length).Select(p => $"ev{p}"));
                if (useOutlineEdge)
                {
                    string evArgs = procs.Length > 1 ? string.Join(", ", Enumerable.Range(0, procs.Length).Select(p => $"ev{p}")) : "";
                    code.AppendLine($"        if ({anyEv}) EvalEdge({evArgs});");
                }
                else
                {
                    code.AppendLine($"        if ({anyEv})");
                    code.AppendLine("        {");
                    code.Append(!withForces && canOutlineEdge ? fastSeqBody : RenderSeqEdgeBody(withForces));
                    code.AppendLine("        }");
                }
                foreach (int id in eventIds) code.AppendLine($"        _prev{id} = _s{id};");
            }
            if (canThrow)
            {
                code.AppendLine("        } catch {");
                for (int i = 0; i < Signals.Count; ++i) code.AppendLine($"            _s{i} = sv_s{i};");
                foreach (int id in eventIds) code.AppendLine($"            _prev{id} = sv_p{id};");
                for (int i = 0; i < Memories.Count; ++i)
                    code.AppendLine(CsIsInlineMem(i) ? $"            _m{i} = sv_m{i};" : $"            Array.Copy(sv_m{i}, _m{i}, {Memories[i].Depth});");
                if (needsMemLog) code.AppendLine("            _mlog.Clear();");
                code.AppendLine("            throw;");
                code.AppendLine("        }");
            }
        }

        if (bodyUsesForces)
        {
            code.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
            code.AppendLine("    public void Eval()");
            code.AppendLine("    {");
            EmitEvalBody(withForces: true, skipPreSettle: false, useOutlineEdge: false);
            code.AppendLine("    }");
        }
        else
        {
            code.AppendLine("    [MethodImpl(MethodImplOptions.NoInlining)]");
            code.AppendLine("    private void EvalForced()");
            code.AppendLine("    {");
            EmitEvalBody(withForces: true, skipPreSettle: false, useOutlineEdge: false);
            if (combPureInternal) code.AppendLine("        _evalSlow = _hasForces;");
            code.AppendLine("    }");
            code.AppendLine();
            code.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
            code.AppendLine("    public void Eval()");
            code.AppendLine("    {");
            code.AppendLine($"        if ({(combPureInternal ? "_evalSlow" : "_hasForces")}) {{ EvalForced(); return; }}");
            EmitEvalBody(withForces: false, skipPreSettle: combPureInternal, useOutlineEdge: outlineEdge);
            code.AppendLine("    }");
        }
        code.AppendLine($"    public string ModelName => {CppStringLiteral(top)};");
        code.Append("    private static readonly VpiSignalInfo[] s_vpiSignals = [");
        code.AppendJoin(", ", Signals.Select((s, idx) =>
        {
            string dir = s.Direction == "input" ? "VpiDirection.Input" : s.Direction == "output" ? "VpiDirection.Output" : "VpiDirection.Internal";
            return $"new VpiSignalInfo({idx}, {CppStringLiteral(s.SourcePath ?? s.Name)}, {s.Width}, {dir}, {(s.Signed ? "true" : "false")}, {(twoState || s.TwoState ? "true" : "false")})";
        }));
        code.AppendLine("];");
        code.Append("    private static readonly VpiMemoryInfo[] s_vpiMemories = [");
        code.AppendJoin(", ", Memories.Select((m, idx) =>
            $"new VpiMemoryInfo({idx}, {CppStringLiteral(m.Name)}, {m.Depth}, {m.Width}, {(twoState || m.TwoState ? "true" : "false")})"));
        code.AppendLine("];");
        code.AppendLine("    public IReadOnlyList<VpiSignalInfo> Signals => s_vpiSignals;");
        code.AppendLine("    public IReadOnlyList<VpiMemoryInfo> Memories => s_vpiMemories;");
        code.AppendLine("    public int SignalByName(string name) => VpiLookup.SignalByName(ModelName, s_vpiSignals, name);");
        code.AppendLine("    public W64 GetSignal(int id) => id switch");
        code.AppendLine("    {");
        for (int i = 0; i < Signals.Count; ++i)
            code.AppendLine(twoState ? $"        {i} => new W64(_s{i}, 0UL, 0UL, {Signals[i].Width})," : $"        {i} => _s{i}.ToW64({Signals[i].Width}),");
        code.AppendLine("        _ => throw new ArgumentOutOfRangeException(nameof(id), \"Invalid signal id.\")");
        code.AppendLine("    };");
        code.AppendLine("    public void PutSignal(int id, W64 value)");
        code.AppendLine("    {");
        code.AppendLine("        if (_finished) throw new InvalidOperationException(\"Model is finished.\");");
        code.AppendLine("        if ((uint)id >= (uint)s_vpiSignals.Length) throw new ArgumentOutOfRangeException(nameof(id), \"Invalid signal id.\");");
        code.AppendLine("        if (s_vpiSignals[id].Direction != VpiDirection.Input) throw new InvalidOperationException(\"Only input ports may be deposited.\");");
        code.AppendLine("        if (IsForced(id)) return;");
        code.AppendLine("        switch (id)");
        code.AppendLine("        {");
        for (int i = 0; i < Signals.Count; ++i)
        {
            if (Signals[i].Direction != "input") continue;
            string m = CsMaskHex(Signals[i].Width);
            if (twoState)
                code.AppendLine($"            case {i}: _s{i} = value.V & ~(value.X | value.Z) & {m}; break;");
            else
                code.AppendLine($"            case {i}: _s{i} = {(Signals[i].TwoState ? $"new W4(value.V & ~(value.X | value.Z) & {m}, 0UL)" : $"W4.FromW64(in value, {m})")}; break;");
        }
        code.AppendLine("            default: throw new InvalidOperationException(\"Only input ports may be deposited.\");");
        code.AppendLine("        }");
        code.AppendLine("    }");
        code.AppendLine("    public W64 ReadMemory(int memId, int addr)");
        code.AppendLine("    {");
        code.AppendLine("        switch (memId)");
        code.AppendLine("        {");
        for (int i = 0; i < Memories.Count; ++i)
            code.AppendLine(twoState
                ? $"            case {i}: if ((uint)addr >= {Memories[i].Depth}U) throw new ArgumentOutOfRangeException(nameof(addr)); return new W64(_m{i}[addr], 0UL, 0UL, {Memories[i].Width});"
                : $"            case {i}: if ((uint)addr >= {Memories[i].Depth}U) throw new ArgumentOutOfRangeException(nameof(addr)); return _m{i}[addr].ToW64({Memories[i].Width});");
        code.AppendLine("            default: throw new ArgumentOutOfRangeException(nameof(memId), \"Invalid memory id.\");");
        code.AppendLine("        }");
        code.AppendLine("    }");
        code.AppendLine("    public void WriteMemory(int memId, int addr, W64 value)");
        code.AppendLine("    {");
        code.AppendLine("        if (_finished) throw new InvalidOperationException(\"Model is finished.\");");
        code.AppendLine("        switch (memId)");
        code.AppendLine("        {");
        for (int i = 0; i < Memories.Count; ++i)
        {
            string m = CsMaskHex(Memories[i].Width);
            code.AppendLine(twoState
                ? $"            case {i}: if ((uint)addr >= {Memories[i].Depth}U) throw new ArgumentOutOfRangeException(nameof(addr)); _m{i}[addr] = value.V & ~(value.X | value.Z) & {m}; break;"
                : $"            case {i}: if ((uint)addr >= {Memories[i].Depth}U) throw new ArgumentOutOfRangeException(nameof(addr)); _m{i}[addr] = {(Memories[i].TwoState ? $"new W4(value.V & ~(value.X | value.Z) & {m}, 0UL)" : $"W4.FromW64(in value, {m})")}; break;");
        }
        code.AppendLine("            default: throw new ArgumentOutOfRangeException(nameof(memId), \"Invalid memory id.\");");
        code.AppendLine("        }");
        if (combPureInternal) code.AppendLine("        _evalSlow = true;");
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
using System.Runtime.InteropServices;

namespace {{@namespace}};

/// <summary>16-byte IEEE 1800 aval/bval 4-state word (0=(0,0), 1=(1,0), Z=(0,1), X=(1,1)) promoted into two 64-bit CPU registers by RyuJIT.</summary>
public readonly struct W4
{
    public readonly ulong A;
    public readonly ulong B;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public W4(ulong a, ulong b) { A = a; B = b; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public W64 ToW64(int w) => new W64(A & ~B, A & B, ~A & B, w);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 FromW64(in W64 w, ulong mask) => new W4((w.V | w.X) & mask, (w.X | w.Z) & mask);
}

public static class W4Ops
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 Unknown(ulong m) => new W4(m, m);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ToUInt64(W4 a)
    {
        if (a.B != 0UL) throw new InvalidOperationException("integer conversion of X/Z port");
        return a.A;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint RepeatCount(W4 a, int w, bool isSigned)
    {
        if (a.B != 0UL) return 0U;
        if (isSigned && w > 0 && ((a.A >> (w - 1)) & 1UL) != 0) return 0U;
        if (a.A > 20000UL) throw new InvalidOperationException("runtime repeat count exceeds budget");
        return (uint)a.A;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 Resize(W4 a, ulong m) => new W4(a.A & m, a.B & m);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 TwoState(W4 a, ulong m) => new W4(a.A & ~a.B & m, 0UL);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 SResize(W4 a, int aw, ulong am, ulong wm)
    {
        if (wm <= am) return new W4(a.A & wm, a.B & wm);
        ulong fill = wm & ~am;
        int top = aw - 1;
        ulong sa = ((a.A >> top) & 1UL) != 0 ? fill : 0UL;
        ulong sb = ((a.B >> top) & 1UL) != 0 ? fill : 0UL;
        return new W4(a.A | sa, a.B | sb);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Edge(W4 prev, W4 curr, bool pos) => pos
        ? ((((~prev.A & ~prev.B) & (curr.A | curr.B)) | (prev.B & curr.A & ~curr.B)) & 1UL) != 0UL
        : ((((prev.A & ~prev.B) & (~curr.A | curr.B)) | (prev.B & ~curr.A & ~curr.B)) & 1UL) != 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Truth(W4 a) => (a.A & ~a.B) != 0UL ? 1 : (a.B != 0UL ? -1 : 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Yes(W4 a) => (a.A & ~a.B) != 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsZero(W4 a) => (a.A | a.B) == 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 LNot(W4 a)
    {
        ulong nt = (a.A & ~a.B) == 0UL ? 1UL : 0UL;
        return new W4(nt, a.B != 0UL ? nt : 0UL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 LAnd(W4 a, W4 b)
    {
        ulong nz = ((a.A | a.B) != 0UL && (b.A | b.B) != 0UL) ? 1UL : 0UL;
        ulong unk = ((a.A & ~a.B) != 0UL && (b.A & ~b.B) != 0UL) ? 0UL : nz;
        return new W4(nz, unk);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 LOr(W4 a, W4 b)
    {
        ulong nz = (a.A | a.B | b.A | b.B) != 0UL ? 1UL : 0UL;
        ulong unk = ((a.A & ~a.B) != 0UL || (b.A & ~b.B) != 0UL) ? 0UL : nz;
        return new W4(nz, unk);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 BNot(W4 a, ulong m)
    {
        ulong ob = a.B & m;
        return new W4(((~a.A) | ob) & m, ob);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 BBin(W4 a, W4 b, ulong m, int op)
    {
        if (op == 2)
        {
            ulong ob = (a.B | b.B) & m;
            return new W4(((a.A ^ b.A) | ob) & m, ob);
        }
        if (op == 0)
        {
            ulong o0 = ((~a.A & ~a.B) | (~b.A & ~b.B)) & m;
            ulong o1 = (a.A & ~a.B) & (b.A & ~b.B) & m;
            ulong ox = ~(o0 | o1) & m;
            return new W4((o1 | ox) & m, ox);
        }
        {
            ulong o1 = ((a.A & ~a.B) | (b.A & ~b.B)) & m;
            ulong o0 = (~a.A & ~a.B) & (~b.A & ~b.B) & m;
            ulong ox = ~(o0 | o1) & m;
            return new W4((o1 | ox) & m, ox);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Exact(W4 a, W4 b) => a.A == b.A && a.B == b.B;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CasezMatch(W4 a, W4 b, ulong m)
    {
        ulong care = ~((~a.A & a.B) | (~b.A & b.B)) & m;
        return (((a.A ^ b.A) | (a.B ^ b.B)) & care) == 0UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CasexMatch(W4 a, W4 b, ulong m)
    {
        ulong care = ~(a.B | b.B) & m;
        return ((a.A ^ b.A) & care) == 0UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 WildEq(W4 a, W4 b, ulong m)
    {
        ulong care = ~b.B & m;
        ulong au = a.B & care;
        ulong known = care & ~au;
        if (((a.A ^ b.A) & known) != 0UL) return default;
        return au != 0UL ? new W4(1UL, 1UL) : new W4(1UL, 0UL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 WildNeq(W4 a, W4 b, ulong m) => LNot(WildEq(a, b, m));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool EqYes(W4 a, W4 b) => (a.B | b.B) == 0UL && a.A == b.A;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool NeqYes(W4 a, W4 b) => ((a.A ^ b.A) & ~(a.B | b.B)) != 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 Eq(W4 a, W4 b)
    {
        ulong u = a.B | b.B;
        ulong eq = (((a.A ^ b.A) & ~u) == 0UL) ? 1UL : 0UL;
        return new W4(eq, u != 0UL ? eq : 0UL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 Cmp(W4 a, W4 b, int op)
    {
        if ((a.B | b.B) != 0UL) return new W4(1UL, 1UL);
        bool r = op == 0 ? a.A < b.A : op == 1 ? a.A <= b.A : op == 2 ? a.A > b.A : a.A >= b.A;
        return new W4(r ? 1UL : 0UL, 0UL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 SCmp(W4 a, W4 b, int w, int op)
    {
        if ((a.B | b.B) != 0UL) return new W4(1UL, 1UL);
        long sa = W2Ops.SExt(a.A, w), sb = W2Ops.SExt(b.A, w);
        bool r = op == 0 ? sa < sb : op == 1 ? sa <= sb : op == 2 ? sa > sb : sa >= sb;
        return new W4(r ? 1UL : 0UL, 0UL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 Arith(W4 a, W4 b, ulong m, bool sub)
    {
        if ((a.B | b.B) != 0UL) return new W4(m, m);
        ulong r = unchecked(sub ? (a.A - b.A) : (a.A + b.A)) & m;
        return new W4(r, 0UL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 Shift(W4 a, W4 b, ulong m, bool right)
    {
        if (b.B != 0UL) return new W4(m, m);
        if (b.A >= 64UL) return default;
        int sh = (int)b.A;
        if (right) return new W4((a.A & m) >> sh, (a.B & m) >> sh);
        return new W4((a.A << sh) & m, (a.B << sh) & m);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 SShift(W4 a, W4 b, int w, ulong m)
    {
        if (b.B != 0UL) return new W4(m, m);
        int top = w - 1;
        ulong sa = (a.A >> top) & 1UL, sb = (a.B >> top) & 1UL;
        if (b.A >= 64UL) return new W4(sa != 0 ? m : 0UL, sb != 0 ? m : 0UL);
        int sh = (int)b.A;
        ulong fill = sh == 0 ? 0UL : (m & ~(m >> sh));
        return new W4((((a.A & m) >> sh) | (sa != 0 ? fill : 0UL)) & m,
                      (((a.B & m) >> sh) | (sb != 0 ? fill : 0UL)) & m);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 Red(W4 a, ulong m, int op)
    {
        ulong u = a.B & m;
        ulong v = a.A & ~u & m;
        if (op == 0 || op == 1)
        {
            ulong zeros = ~(v | u) & m;
            if (zeros != 0UL) return new W4(op == 1 ? 1UL : 0UL, 0UL);
            if (u != 0UL) return new W4(1UL, 1UL);
            return new W4(op == 0 ? 1UL : 0UL, 0UL);
        }
        if (op == 2 || op == 3)
        {
            if (v != 0UL) return new W4(op == 2 ? 1UL : 0UL, 0UL);
            if (u != 0UL) return new W4(1UL, 1UL);
            return new W4(op == 3 ? 1UL : 0UL, 0UL);
        }
        if (u != 0UL) return new W4(1UL, 1UL);
        bool odd = (BitOperations.PopCount(v) & 1) != 0;
        return new W4((odd ^ (op == 5)) ? 1UL : 0UL, 0UL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 Product(W4 a, W4 b, int w, ulong m, int op)
    {
        if (((a.B | b.B) & m) != 0UL) return new W4(m, m);
        ulong av = a.A & m, bv = b.A & m;
        if (op == 0) return new W4(unchecked(av * bv) & m, 0UL);
        if (bv == 0UL) return new W4(m, m);
        return new W4((op == 1 ? (av / bv) : (av % bv)) & m, 0UL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 SProduct(W4 a, W4 b, int w, ulong m, int op)
    {
        if (((a.B | b.B) & m) != 0UL) return new W4(m, m);
        long sa = W2Ops.SExt(a.A, w), sb = W2Ops.SExt(b.A, w);
        if (op == 0) return new W4(unchecked((ulong)sa * (ulong)sb) & m, 0UL);
        if (sb == 0L) return new W4(m, m);
        if (sa == long.MinValue && sb == -1L) return new W4(op == 1 ? ((ulong)sa & m) : 0UL, 0UL);
        return new W4((ulong)(op == 1 ? (sa / sb) : (sa % sb)) & m, 0UL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 Cond(W4 c, W4 a, W4 b, ulong m)
    {
        int t = Truth(c);
        if (t == 1) return Resize(a, m);
        if (t == 0) return Resize(b, m);
        ulong diff = ((a.A ^ b.A) | (a.B ^ b.B)) & m;
        return new W4((a.A & m) | diff, (a.B & m) | diff);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 Concat(W4 a, W4 b, int bw)
    {
        ulong bm = W2Ops.Mask(bw);
        if (bw >= 64) return new W4(b.A & bm, b.B & bm);
        return new W4((a.A << bw) | (b.A & bm), (a.B << bw) | (b.B & bm));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 Concat(W4 a, W4 b, int bw, ulong bm) =>
        bw >= 64 ? new W4(b.A & bm, b.B & bm) : new W4((a.A << bw) | (b.A & bm), (a.B << bw) | (b.B & bm));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 SelBit(W4 a, int aw, W4 idx)
    {
        if (idx.B != 0UL || idx.A >= (ulong)aw) return new W4(1UL, 1UL);
        int i = (int)idx.A;
        return new W4((a.A >> i) & 1UL, (a.B >> i) & 1UL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 SelPartFast(W4 a, int right, ulong m) =>
        new W4((a.A >> right) & m, (a.B >> right) & m);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 SelPart(W4 a, int aw, int left, int right)
    {
        int n = left - right + 1;
        ulong m = W2Ops.Mask(n);
        if (right >= aw) return new W4(m, m);
        int avail = aw - right;
        ulong am = W2Ops.Mask(avail < n ? avail : n);
        ulong oob = m & ~am;
        return new W4(((a.A >> right) & am) | oob, ((a.B >> right) & am) | oob);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 SelIdx(W4 a, int aw, W4 b, int width, bool desc)
    {
        ulong wm = W2Ops.Mask(width);
        if (b.B != 0UL) return new W4(wm, wm);
        if (b.A > 2147483647UL) throw new ArgumentOutOfRangeException(nameof(b), "known indexed select base exceeds qualified range 0..INT32_MAX");
        long lo = desc ? (long)b.A - (width - 1) : (long)b.A;
        long srcLo = lo < 0 ? 0 : lo;
        long srcHi = (lo + width) < aw ? (lo + width) : aw;
        if (srcLo >= srcHi) return new W4(wm, wm);
        int count = (int)(srcHi - srcLo);
        int dstLo = (int)(srcLo - lo);
        int sLo = (int)srcLo;
        ulong cm = W2Ops.Mask(count);
        ulong dstMask = cm << dstLo;
        ulong oob = wm & ~dstMask;
        return new W4((((a.A >> sLo) & cm) << dstLo) | oob, (((a.B >> sLo) & cm) << dstLo) | oob);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 DepIdx(W4 before, int bw, W4 b, int width, bool desc, W4 value)
    {
        if (b.B != 0UL) return before;
        if (b.A > 2147483647UL) throw new ArgumentOutOfRangeException(nameof(b), "known selected write base exceeds qualified range 0..INT32_MAX");
        ulong wm = W2Ops.Mask(width);
        W4 val = new W4(value.A & wm, value.B & wm);
        long lo = desc ? (long)b.A - (width - 1) : (long)b.A;
        long dstLo = lo < 0 ? 0 : lo;
        long dstHi = (lo + width) < bw ? (lo + width) : bw;
        if (dstLo >= dstHi) return before;
        int count = (int)(dstHi - dstLo);
        int srcLo = (int)(dstLo - lo);
        int dLo = (int)dstLo;
        ulong cm = W2Ops.Mask(count);
        ulong dmask = cm << dLo;
        return new W4((before.A & ~dmask) | (((val.A >> srcLo) & cm) << dLo),
                      (before.B & ~dmask) | (((val.B >> srcLo) & cm) << dLo));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 MemIdx(W4 index, int lower)
    {
        if (index.B != 0UL || index.A < (ulong)lower) return new W4(1UL, 1UL);
        return new W4(index.A - (ulong)lower, 0UL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 Read(W4[] m, W4 idx, ulong wm)
    {
        if (idx.B != 0UL || idx.A >= (ulong)m.Length) return new W4(wm, wm);
        return Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(m), (nint)idx.A);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 CountOnes(W4 a, ulong m)
    {
        ulong ones = a.A & ~a.B & m;
        return new W4((ulong)BitOperations.PopCount(ones), 0UL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 OneHot(W4 a, ulong m) => new W4(BitOperations.PopCount(a.A & ~a.B & m) == 1 ? 1UL : 0UL, 0UL);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 OneHot0(W4 a, ulong m) => new W4(BitOperations.PopCount(a.A & ~a.B & m) <= 1 ? 1UL : 0UL, 0UL);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 IsUnknown(W4 a, ulong m) => new W4((a.B & m) != 0UL ? 1UL : 0UL, 0UL);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 CLog2(W4 a, ulong m)
    {
        if ((a.B & m) != 0UL) return default;
        ulong x = a.A & m;
        if (x <= 1UL) return default;
        return new W4((ulong)(64 - BitOperations.LeadingZeroCount(x - 1UL)), 0UL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 ResolvePair(W4 a, W4 b, int netKind, ulong m)
    {
        ulong va = a.A & ~a.B & m, za = ~a.A & a.B & m, a0 = ~a.A & ~a.B & m;
        ulong vb = b.A & ~b.B & m, zb = ~b.A & b.B & m, b0 = ~b.A & ~b.B & m;
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
        return new W4((vOut | xOut) & m, (xOut | zOut) & m);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 ResolveFinalize(W4 acc, int netKind, ulong m)
    {
        if (netKind == 3) return new W4(acc.A & m, (acc.A & acc.B) & m);
        if (netKind == 4) return new W4((acc.A | acc.B) & m, (acc.A & acc.B) & m);
        return acc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 ResolveNet2(int netKind, W4 a, W4 b, ulong m) =>
        ResolveFinalize(ResolvePair(a, b, netKind, m), netKind, m);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4 ResolveNet(int netKind, ReadOnlySpan<W4> drivers, ulong m)
    {
        if (drivers.Length == 0) return ResolveFinalize(new W4(0UL, m), netKind, m);
        W4 acc = Resize(drivers[0], m);
        for (int i = 1; i < drivers.Length; ++i) acc = ResolvePair(acc, drivers[i], netKind, m);
        return ResolveFinalize(acc, netKind, m);
    }
}

public sealed class MemLog4
{
    private readonly int[] _mem = new int[16];
    private readonly int[] _idx = new int[16];
    private readonly W4[] _val = new W4[16];
    private List<(int mem, int idx, W4 val)>? _overflow;
    private int _count;

    public int Count => _count;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int MemAt(int i) => _mem[i];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int IdxAt(int i) => _idx[i];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public W4 ValAt(int i) => _val[i];
    public List<(int mem, int idx, W4 val)>? Overflow => _overflow;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetCount() => _count = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(int memId, int memLen, W4 idx, W4 val, ulong m)
    {
        if (idx.B != 0UL || idx.A >= (ulong)memLen) return;
        W4 v = new W4(val.A & m, val.B & m);
        if (_count < 16) { _mem[_count] = memId; _idx[_count] = (int)idx.A; _val[_count] = v; ++_count; }
        else (_overflow ??= new()).Add((memId, (int)idx.A, v));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Commit(W4[][] mems)
    {
        for (int i = 0; i < _count; ++i) mems[_mem[i]][_idx[i]] = _val[i];
        if (_overflow is { Count: > 0 }) { foreach (var e in _overflow) mems[e.mem][e.idx] = e.val; _overflow.Clear(); }
        _count = 0;
    }

    public void Clear() { _count = 0; _overflow?.Clear(); }
}

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
    public static ulong ToUInt64(in W64 a)
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

    public static string Text(in W64 a)
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

    public static uint RepeatCount(in W64 a, bool isSigned)
    {
        if ((a.X | a.Z) != 0UL) return 0U;
        if (isSigned && a.W > 0 && ((a.V >> (a.W - 1)) & 1UL) != 0) return 0U;
        if (a.V > 20000UL) throw new InvalidOperationException("runtime repeat count exceeds budget");
        return (uint)a.V;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 Resize(in W64 a, int w)
    {
        ulong m = Mask(w);
        return new W64(a.V & m, a.X & m, a.Z & m, w);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 TwoState(in W64 a) => new W64(a.V & Mask(a.W), 0UL, 0UL, a.W);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 SResize(in W64 a, int w)
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
    private static int LogicBit(in W64 a) => ((a.X | a.Z) & 1UL) != 0 ? -1 : (int)(a.V & 1UL);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Edge(in W64 prev, in W64 curr, bool pos)
    {
        int p = LogicBit(prev), c = LogicBit(curr);
        return pos ? ((p == 0 && c != 0) || (p < 0 && c == 1)) : ((p == 1 && c != 1) || (p < 0 && c == 0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Truth(in W64 a) => a.V != 0UL ? 1 : ((a.X | a.Z) != 0UL ? -1 : 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Yes(in W64 a) => a.V != 0UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 LNot(in W64 a)
    {
        int t = Truth(a);
        return t < 0 ? new W64(0UL, 1UL, 0UL, 1) : new W64(t == 0 ? 1UL : 0UL, 0UL, 0UL, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 LAnd(in W64 a, in W64 b)
    {
        int x = Truth(a), y = Truth(b);
        return (x == 0 || y == 0) ? new W64(0UL, 0UL, 0UL, 1) : (x == 1 && y == 1) ? new W64(1UL, 0UL, 0UL, 1) : new W64(0UL, 1UL, 0UL, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 LOr(in W64 a, in W64 b)
    {
        int x = Truth(a), y = Truth(b);
        return (x == 1 || y == 1) ? new W64(1UL, 0UL, 0UL, 1) : (x == 0 && y == 0) ? new W64(0UL, 0UL, 0UL, 1) : new W64(0UL, 1UL, 0UL, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 BNot(in W64 a, int w)
    {
        ulong m = Mask(w);
        ulong u = (a.X | a.Z) & m;
        return new W64((~a.V) & (~u) & m, u, 0UL, w);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 BBin(in W64 a, in W64 b, int w, int op)
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
    public static bool Exact(in W64 a, in W64 b) => a.V == b.V && a.X == b.X && a.Z == b.Z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CasezMatch(in W64 a, in W64 b)
    {
        int w = a.W > b.W ? a.W : b.W;
        ulong care = ~(a.Z | b.Z) & Mask(w);
        return (((a.V ^ b.V) | (a.X ^ b.X)) & care) == 0UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CasexMatch(in W64 a, in W64 b)
    {
        int w = a.W > b.W ? a.W : b.W;
        ulong care = ~(a.X | a.Z | b.X | b.Z) & Mask(w);
        return ((a.V ^ b.V) & care) == 0UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 WildEq(in W64 a, in W64 b)
    {
        int w = a.W > b.W ? a.W : b.W;
        ulong care = ~(b.X | b.Z) & Mask(w);
        ulong au = (a.X | a.Z) & care;
        ulong known = care & ~au;
        if (((a.V ^ b.V) & known) != 0UL) return new W64(0UL, 0UL, 0UL, 1);
        return au != 0UL ? new W64(0UL, 1UL, 0UL, 1) : new W64(1UL, 0UL, 0UL, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 WildNeq(in W64 a, in W64 b) => LNot(WildEq(a, b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 Eq(in W64 a, in W64 b)
    {
        if (((a.X | a.Z) | (b.X | b.Z)) != 0UL) return new W64(0UL, 1UL, 0UL, 1);
        return new W64(a.V == b.V ? 1UL : 0UL, 0UL, 0UL, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 Cmp(in W64 a, in W64 b, int op)
    {
        if (((a.X | a.Z) | (b.X | b.Z)) != 0UL) return new W64(0UL, 1UL, 0UL, 1);
        bool r = op == 0 ? a.V < b.V : op == 1 ? a.V <= b.V : op == 2 ? a.V > b.V : a.V >= b.V;
        return new W64(r ? 1UL : 0UL, 0UL, 0UL, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 SCmp(in W64 a, in W64 b, int op)
    {
        if (((a.X | a.Z) | (b.X | b.Z)) != 0UL) return new W64(0UL, 1UL, 0UL, 1);
        long sa = W2Ops.SExt(a.V, a.W), sb = W2Ops.SExt(b.V, b.W);
        bool r = op == 0 ? sa < sb : op == 1 ? sa <= sb : op == 2 ? sa > sb : sa >= sb;
        return new W64(r ? 1UL : 0UL, 0UL, 0UL, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 Arith(in W64 a, in W64 b, int w, bool sub)
    {
        ulong m = Mask(w);
        if (((a.X | a.Z) | (b.X | b.Z)) != 0UL) return new W64(0UL, m, 0UL, w);
        ulong r = unchecked(sub ? (a.V - b.V) : (a.V + b.V)) & m;
        return new W64(r, 0UL, 0UL, w);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 Shift(in W64 a, in W64 b, int w, bool right)
    {
        ulong m = Mask(w);
        if ((b.X | b.Z) != 0UL) return new W64(0UL, m, 0UL, w);
        if (b.V >= 64UL) return new W64(0UL, 0UL, 0UL, w);
        int sh = (int)b.V;
        if (right) return new W64((a.V & m) >> sh, (a.X & m) >> sh, (a.Z & m) >> sh, w);
        return new W64((a.V << sh) & m, (a.X << sh) & m, (a.Z << sh) & m, w);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 SShift(in W64 a, in W64 b, int w)
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
    public static W64 Red(in W64 a, int op)
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
    public static W64 Product(in W64 a, in W64 b, int w, int op)
    {
        ulong m = Mask(w);
        if (((a.X | a.Z) | (b.X | b.Z)) != 0UL) return new W64(0UL, m, 0UL, w);
        ulong av = a.V & m, bv = b.V & m;
        if (op == 0) return new W64(unchecked(av * bv) & m, 0UL, 0UL, w);
        if (bv == 0UL) return new W64(0UL, m, 0UL, w);
        return new W64((op == 1 ? (av / bv) : (av % bv)) & m, 0UL, 0UL, w);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 SProduct(in W64 a, in W64 b, int w, int op)
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
    public static W64 Cond(in W64 c, in W64 a, in W64 b, int w)
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
    public static W64 Concat(in W64 a, in W64 b)
    {
        int bw = b.W;
        ulong bm = Mask(bw);
        if (bw >= 64) return new W64(b.V & bm, b.X & bm, b.Z & bm, a.W + bw);
        return new W64((a.V << bw) | (b.V & bm), (a.X << bw) | (b.X & bm), (a.Z << bw) | (b.Z & bm), a.W + bw);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 SelBit(in W64 a, in W64 idx)
    {
        if ((idx.X | idx.Z) != 0UL || idx.V >= (ulong)a.W) return new W64(0UL, 1UL, 0UL, 1);
        int i = (int)idx.V;
        return new W64((a.V >> i) & 1UL, (a.X >> i) & 1UL, (a.Z >> i) & 1UL, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 SelPart(in W64 a, int left, int right)
    {
        int n = left - right + 1;
        ulong m = Mask(n);
        if (right >= a.W) return new W64(0UL, m, 0UL, n);
        int avail = a.W - right;
        ulong am = Mask(avail < n ? avail : n);
        return new W64((a.V >> right) & am, ((a.X >> right) & am) | (m & ~am), (a.Z >> right) & am, n);
    }

    public static W64 SelIdx(in W64 a, in W64 b, int width, bool desc)
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

    public static W64 DepIdx(in W64 before, in W64 b, int width, bool desc, in W64 value)
    {
        if ((b.X | b.Z) != 0UL) return before;
        if (b.V > 2147483647UL) throw new ArgumentOutOfRangeException(nameof(b), "known selected write base exceeds qualified range 0..INT32_MAX");
        W64 val = Resize(value, width);
        long lo = desc ? (long)b.V - (width - 1) : (long)b.V;
        long dstLo = lo < 0 ? 0 : lo;
        long dstHi = (lo + width) < before.W ? (lo + width) : before.W;
        if (dstLo >= dstHi) return before;
        int count = (int)(dstHi - dstLo);
        int srcLo = (int)(dstLo - lo);
        int dLo = (int)dstLo;
        ulong cm = Mask(count);
        ulong dmask = cm << dLo;
        return new W64((before.V & ~dmask) | (((val.V >> srcLo) & cm) << dLo),
                       (before.X & ~dmask) | (((val.X >> srcLo) & cm) << dLo),
                       (before.Z & ~dmask) | (((val.Z >> srcLo) & cm) << dLo), before.W);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 MemIdx(in W64 index, int lower)
    {
        if ((index.X | index.Z) != 0UL || index.V < (ulong)lower) return new W64(0UL, 1UL, 0UL, 1);
        return new W64(index.V - (ulong)lower, 0UL, 0UL, index.W);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 Read(W64[] m, in W64 idx, int w)
    {
        if ((idx.X | idx.Z) != 0UL || idx.V >= (ulong)m.Length) return Unknown(w);
        return m[(int)idx.V];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 CountOnes(in W64 a)
    {
        ulong ones = a.V & ~a.X & ~a.Z & Mask(a.W);
        return new W64((ulong)BitOperations.PopCount(ones), 0UL, 0UL, 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 OneHot(in W64 a) => new W64(CountOnes(a).V == 1UL ? 1UL : 0UL, 0UL, 0UL, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 OneHot0(in W64 a) => new W64(CountOnes(a).V <= 1UL ? 1UL : 0UL, 0UL, 0UL, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 IsUnknown(in W64 a) => new W64(((a.X | a.Z) & Mask(a.W)) != 0UL ? 1UL : 0UL, 0UL, 0UL, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 CLog2(in W64 a)
    {
        ulong m = Mask(a.W);
        if (((a.X | a.Z) & m) != 0UL) return new W64(0UL, 0UL, 0UL, 32);
        ulong x = a.V & m;
        if (x <= 1UL) return new W64(0UL, 0UL, 0UL, 32);
        return new W64((ulong)(64 - BitOperations.LeadingZeroCount(x - 1UL)), 0UL, 0UL, 32);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 ResolvePair(in W64 aIn, in W64 bIn, int netKind, int w)
    {
        ulong m = Mask(w);
        ulong va = aIn.V & m, xa = aIn.X & m, za = aIn.Z & m;
        ulong vb = bIn.V & m, xb = bIn.X & m, zb = bIn.Z & m;
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
    public static W64 ResolveFinalize(in W64 acc, int netKind)
    {
        ulong m = Mask(acc.W);
        if (netKind == 3) return new W64(acc.V, acc.X, 0UL, acc.W);
        if (netKind == 4) return new W64((acc.V | acc.Z) & m, acc.X, 0UL, acc.W);
        return acc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W64 ResolveNet2(int netKind, in W64 a, in W64 b, int w) =>
        ResolveFinalize(ResolvePair(a, b, netKind, w), netKind);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
    public static ulong Read(ulong[] m, ulong idx) =>
        idx < (ulong)m.Length ? Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(m), (nint)idx) : 0UL;

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
    public void Push(int memId, int memLen, in W64 idx, in W64 val, int w)
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

    public int Count => _count;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int MemAt(int i) => _mem[i];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int IdxAt(int i) => _idx[i];
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ValAt(int i) => _val[i];
    public List<(int mem, int idx, ulong val)>? Overflow => _overflow;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetCount() => _count = 0;

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
    public IReadOnlyList<string> List => _args;
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
""" + EmitCSharpSafeVpiRuntime();
}
