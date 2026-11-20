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
        bool hasBlocking = Statements(Body).Any(e => Kind(e) is "blocking" or "allocate" or "delete" or "queue_push" or "queue_pop" or "assoc_traverse");
        bool hasContainers = Memories.Any(m => m.Associative || m.Queue || m.Dynamic);
        bool useFastEvent = EventDriven && !hasBlocking && !hasContainers;
        if (!CombinationalOnly && !useFastEvent) return false;
        if (Statements(Initialization).Any(st => Kind(st) is not "block")) return false;
        if (Signals.Any(s => s.Width > 64)) return false;
        if (Memories.Any(m => m.Width > 64 || m.Associative || m.Queue || m.Dynamic || m.Dimensions is not null)) return false;
        if (SettledSteps.Any(step => !(Kind(step) == "blocking" && Kind(step.GetProperty("target")) == "ref"))) return false;
        return SettledSteps.Append(Body).SelectMany(Statements).All(StatementFits64);
    }

    internal IReadOnlyDictionary<string, string> CSharpPortNames()
    {
        var reserved = (
            "abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while Eval Final Time TimeInc W64 W64Ops W2Ops"
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
        var args = e.GetProperty("args").EnumerateArray().ToArray();
        int width = Math.Max(context, ExpressionWidth(e));
        return kind switch
        {
            "castsigned" or "castunsigned" => Extend(CsExpr64(args[0])),
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
        var args = e.GetProperty("args").EnumerateArray().ToArray();
        int width = Math.Max(context, ew);
        return kind switch
        {
            "castsigned" or "castunsigned" => Extend(CsExpr2(args[0])),
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
            "eq" or "neq" or "caseeq" or "caseneq" =>
                (kind is "neq" or "caseneq" ? "W2Ops.LNot(" : "")
                + $"W2Ops.Eq({CsExpr2(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{CsExpr2(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))})"
                + (kind is "neq" or "caseneq" ? ")" : ""),
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
            case "case":
                string lbl = "cs" + unique++;
                var items = e.GetProperty("items").EnumerateArray().ToArray();
                int cw = Math.Max(ExpressionWidth(e.GetProperty("value")), items.Where(i => i.GetProperty("match").ValueKind != JsonValueKind.Null).Select(i => ExpressionWidth(i.GetProperty("match"))).DefaultIfEmpty(1).Max());
                bool csSigned = ExpressionSigned(e.GetProperty("value")) && items.Where(i => i.GetProperty("match").ValueKind != JsonValueKind.Null).All(i => ExpressionSigned(i.GetProperty("match")));
                code.AppendLine(twoState
                    ? $"{{ulong {lbl}={CsExpr2(e.GetProperty("value"), cw, csSigned)};"
                    : $"{{W64 {lbl}={CsExpr64(e.GetProperty("value"), cw, csSigned)};");
                bool first = true;
                foreach (var item in items.Where(i => i.GetProperty("match").ValueKind != JsonValueKind.Null))
                {
                    string cmp = twoState
                        ? $"{lbl}=={CsExpr2(item.GetProperty("match"), cw, csSigned)}"
                        : $"W64Ops.Exact({lbl},{CsExpr64(item.GetProperty("match"), cw, csSigned)})";
                    code.AppendLine($"{(first ? "if" : "else if")}({cmp}){{");
                    CsStmt(code, item.GetProperty("body"), ref unique, twoState, trackBudget);
                    code.AppendLine("}");
                    first = false;
                }
                var defItem = items.FirstOrDefault(i => i.GetProperty("match").ValueKind == JsonValueKind.Null);
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
            .SelectMany(e => TargetLeaves(e.GetProperty("target"))).Select(t => SignalId(TargetOwner(t))).Distinct().Order().ToArray();
        bool hasFixedMemWrites = !CombinationalOnly && Statements(Body).Any(e => Kind(e) == "nba" && Kind(e.GetProperty("target")) == "index");

        var code = new StringBuilder();
        code.AppendLine("// <auto-generated/>");
        code.AppendLine("using System;");
        code.AppendLine("using System.Runtime.CompilerServices;");
        code.AppendLine($"namespace {@namespace};");
        code.AppendLine();
        code.AppendLine($"public sealed class {className}");
        code.AppendLine("{");
        if (hasRuntimeLoops) code.AppendLine("    private int _budget = 20000;");
        code.AppendLine("    private ulong _time;");
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
        code.AppendLine("    public void Final() { }");
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
        code.AppendLine("        Settle();");
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
}

public static class W2Ops
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Mask(int w) => w >= 64 ? ulong.MaxValue : (w <= 0 ? 0UL : ((1UL << w) - 1UL));

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
""";
}
