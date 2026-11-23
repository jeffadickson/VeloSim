using System.Text;
using System.Text.Json;
namespace VeloSim;

public sealed partial class RtlDesign
{
    private bool ExpressionFits64(JsonElement e)
    {
        string kind = Kind(e);
        if (kind == "convert") return false;
        if (kind == "size") return true;
        if (kind == "exists") return ExpressionFits64(e.GetProperty("index"));
        if (ExpressionWidth(e) > 64) return false;
        if (kind == "index") return MemoryIndices(e).All(ExpressionFits64);
        if (kind is "bitselect" or "partselect" or "indexedselect")
        {
            if (!ExpressionFits64(e.GetProperty("value"))) return false;
            if (e.TryGetProperty("index", out var idx) && !ExpressionFits64(idx)) return false;
            if (e.TryGetProperty("base", out var basis) && !ExpressionFits64(basis)) return false;
            return true;
        }
        if (kind == "urandom_range")
        {
            if (!ExpressionFits64(e.GetProperty("max"))) return false;
            if (e.TryGetProperty("min", out var minEl) && !ExpressionFits64(minEl)) return false;
            return true;
        }
        if (kind == "inside")
        {
            if (!ExpressionFits64(e.GetProperty("value"))) return false;
            foreach (var it in e.GetProperty("items").EnumerateArray())
            {
                if (it.TryGetProperty("lo", out var loEl) && it.TryGetProperty("hi", out var hiEl))
                {
                    if (!ExpressionFits64(loEl) || !ExpressionFits64(hiEl)) return false;
                }
                else if (!ExpressionFits64(it)) return false;
            }
            return true;
        }
        if (e.TryGetProperty("args", out var args))
        {
            foreach (var a in args.EnumerateArray())
                if (!ExpressionFits64(a)) return false;
        }
        return true;
    }

    private bool StatementFits64(JsonElement e)
    {
        if (e.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.Object && !ExpressionFits64(t)) return false;
        if (e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object && !ExpressionFits64(v)) return false;
        if (e.TryGetProperty("condition", out var c) && c.ValueKind == JsonValueKind.Object && !ExpressionFits64(c)) return false;
        if (e.TryGetProperty("count", out var cnt) && cnt.ValueKind == JsonValueKind.Object && !ExpressionFits64(cnt)) return false;
        if (e.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Object && !ExpressionFits64(ix)) return false;
        if (e.TryGetProperty("amount", out var amt) && amt.ValueKind == JsonValueKind.Object && !ExpressionFits64(amt)) return false;
        if (Kind(e) == "case")
        {
            foreach (var item in e.GetProperty("items").EnumerateArray())
            {
                foreach (var match in CaseItemMatches(item))
                    if (!ExpressionFits64(match)) return false;
            }
        }
        if (Kind(e) is "display" or "fatal" && e.TryGetProperty("args", out var dArgs))
        {
            foreach (var da in dArgs.EnumerateArray())
                if (!ExpressionFits64(da)) return false;
        }
        return true;
    }

    private bool VerificationFits64()
    {
        foreach (var cg in Covergroups)
            foreach (var cp in cg.Points)
                if (!ExpressionFits64(cp.Value)) return false;
        foreach (var p in Properties)
        {
            if (p.DisableIff is { } dis && !ExpressionFits64(dis)) return false;
            if (p.Antecedent is { } ant && !ExpressionFits64(ant)) return false;
            if (!ExpressionFits64(p.Consequent)) return false;
        }
        return true;
    }

    private bool CanUseNarrow64(bool useFastEvent)
    {
        if (!useFastEvent || Timed || CombinationalOnly) return false;
        if (Signals.Any(s => s.Width > 64)) return false;
        if (Memories.Any(m => m.Width > 64 || m.Associative || m.Queue || m.Dynamic || m.Dimensions is not null)) return false;
        if (SettledSteps.Any(step => !(Kind(step) == "blocking" && Kind(step.GetProperty("target")) == "ref"))) return false;
        return VerificationFits64() && SettledSteps.Append(Body).SelectMany(Statements).All(StatementFits64);
    }

    private static string CppMaskHex(int w) => w >= 64 ? "~0ULL" : (w <= 0 ? "0ULL" : $"0x{((1UL << w) - 1UL):x}ULL");

    private static string EmitConst64(string bits)
    {
        ulong a = 0, b = 0;
        foreach (char c in bits)
        {
            a <<= 1; b <<= 1;
            if (c == '1') a |= 1;
            else if (c == 'x') { a |= 1; b |= 1; }
            else if (c == 'z') b |= 1;
        }
        return b == 0 ? $"vsrtl::w4::lit(0x{a:x}ULL)" : $"vsrtl::w4::lit(0x{a:x}ULL,0x{b:x}ULL)";
    }

    private string MemoryIndex64(JsonElement access, int id)
    {
        string idx = Expression64(access.GetProperty("index"));
        return Memories[id].LowerBound == 0 ? idx : $"vsrtl::w4::memidx({idx},{Memories[id].LowerBound})";
    }

    private string Expression64(JsonElement e, int context = 0, bool? forceSigned = null)
    {
        string kind = Kind(e);
        int ew = ExpressionWidth(e);
        bool signed = forceSigned ?? ExpressionSigned(e);
        string Extend(string value) => signed && context > ew ? $"vsrtl::w4::sresize({value},{ew},{CppMaskHex(ew)},{CppMaskHex(context)})" : value;
        if (kind == "ref") return Extend($"_vs_s[{SignalId(Name(e))}]");
        if (kind == "const") return Extend(EmitConst64(e.GetProperty("bits").GetString()!));
        if (kind == "index")
        {
            int id = MemoryId(Name(e));
            return Extend($"vsrtl::w4::read_flat(_vs_m{id},{Memories[id].Depth},{MemoryIndex64(e, id)},{CppMaskHex(Memories[id].Width)})");
        }
        if (kind == "bitselect")
        {
            var valEl = e.GetProperty("value");
            return $"vsrtl::w4::selbit({Expression64(valEl)},{ExpressionWidth(valEl)},{Expression64(e.GetProperty("index"))})";
        }
        if (kind == "partselect")
        {
            var valEl = e.GetProperty("value");
            return $"vsrtl::w4::selpart({Expression64(valEl)},{ExpressionWidth(valEl)},{e.GetProperty("left").GetInt32()},{e.GetProperty("right").GetInt32()})";
        }
        if (kind == "indexedselect")
        {
            var valEl = e.GetProperty("value");
            return $"vsrtl::w4::selidx({Expression64(valEl)},{ExpressionWidth(valEl)},{Expression64(e.GetProperty("base"))},{e.GetProperty("width").GetInt32()},{(e.GetProperty("descending").GetBoolean() ? "true" : "false")})";
        }
        if (kind == "test_plusargs") return Extend($"vsrtl::w4::lit(testPlusargs({CppStringLiteral(e.GetProperty("prefix").GetString()!)}))");
        if (kind == "urandom") return Extend("vsrtl::w4::lit(nextUrandom())");
        if (kind == "random") return Extend("vsrtl::w4::lit(uint32_t(nextRandom()))");
        if (kind == "urandom_range")
        {
            string maxExpr = $"uint32_t(vsrtl::w4::two_state({Expression64(e.GetProperty("max"))},~0ULL).a)";
            string minExpr = e.TryGetProperty("min", out var minEl) ? $"uint32_t(vsrtl::w4::two_state({Expression64(minEl)},~0ULL).a)" : "0u";
            return Extend($"vsrtl::w4::lit(nextUrandomRange({maxExpr},{minExpr}))");
        }
        if (kind == "resolve")
        {
            int nk = ParseNetKind(e.GetProperty("net").GetString()!);
            int rw = Math.Max(context, ew);
            var drvEls = e.GetProperty("args").EnumerateArray().ToArray();
            if (drvEls.Length == 2)
                return Extend($"vsrtl::w4::resolve_net2({nk},{Expression64(drvEls[0], rw)},{Expression64(drvEls[1], rw)},{CppMaskHex(rw)})");
            string drivers = string.Join(",", drvEls.Select(d => Expression64(d, rw)));
            return Extend($"vsrtl::w4::resolve_net({nk},{{{drivers}}},{CppMaskHex(rw)})");
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
                    string loCmp = cs ? $"vsrtl::w4::scmp({Expression64(valEl, cw, true)},{Expression64(loEl, cw, true)},{cw},3)" : $"vsrtl::w4::cmp({Expression64(valEl, cw, false)},{Expression64(loEl, cw, false)},3)";
                    string hiCmp = cs ? $"vsrtl::w4::scmp({Expression64(valEl, cw, true)},{Expression64(hiEl, cw, true)},{cw},1)" : $"vsrtl::w4::cmp({Expression64(valEl, cw, false)},{Expression64(hiEl, cw, false)},1)";
                    parts.Add($"vsrtl::w4::land({loCmp},{hiCmp})");
                }
                else
                {
                    int cw = Math.Max(vw, ExpressionWidth(it));
                    bool cs = vSigned && ExpressionSigned(it);
                    parts.Add($"vsrtl::w4::wildeq({Expression64(valEl, cw, cs)},{Expression64(it, cw, cs)},{CppMaskHex(cw)})");
                }
            }
            string acc = parts[0];
            for (int i = 1; i < parts.Count; ++i) acc = $"vsrtl::w4::lor({acc},{parts[i]})";
            return acc;
        }
        if (kind == "past")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            int ticks = e.TryGetProperty("ticks", out var tEl) ? tEl.GetInt32() : 1;
            return Extend($"_vs_past_{sid}[{ticks - 1}]");
        }
        if (kind == "rose")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            return $"vsrtl::w4::lit(uint64_t(vsrtl::w4::edge(_vs_past_{sid}[0],_vs_s[{sid}],true)))";
        }
        if (kind == "fell")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            return $"vsrtl::w4::lit(uint64_t(vsrtl::w4::edge(_vs_past_{sid}[0],_vs_s[{sid}],false)))";
        }
        if (kind == "stable")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            return $"vsrtl::w4::lit(uint64_t(vsrtl::w4::exact(_vs_s[{sid}],_vs_past_{sid}[0])))";
        }
        if (kind == "dpi_call")
        {
            string callArgs = string.Join(",", e.GetProperty("args").EnumerateArray().Select(a => $"vsrtl::w4::two_state({Expression64(a)},~0ULL).a"));
            return Extend($"vsrtl::w4::lit(uint64_t(::{Name(e)}({callArgs}))&{CppMaskHex(e.GetProperty("width").GetInt32())})");
        }
        var args = e.GetProperty("args").EnumerateArray().ToArray();
        int width = Math.Max(context, ew);
        string wm = CppMaskHex(width);
        return kind switch
        {
            "castsigned" or "castunsigned" => Extend(Expression64(args[0])),
            "countones" => Extend($"vsrtl::w4::countones({Expression64(args[0])},{CppMaskHex(ExpressionWidth(args[0]))})"),
            "onehot" => $"vsrtl::w4::onehot({Expression64(args[0])},{CppMaskHex(ExpressionWidth(args[0]))})",
            "onehot0" => $"vsrtl::w4::onehot0({Expression64(args[0])},{CppMaskHex(ExpressionWidth(args[0]))})",
            "isunknown" => $"vsrtl::w4::isunknown({Expression64(args[0])},{CppMaskHex(ExpressionWidth(args[0]))})",
            "clog2" => Extend($"vsrtl::w4::clog2({Expression64(args[0])},{CppMaskHex(ExpressionWidth(args[0]))})"),
            "add" or "sub" => $"vsrtl::w4::arith({Expression64(args[0], width, signed)},{Expression64(args[1], width, signed)},{wm},{(kind == "sub" ? "true" : "false")})",
            "bitnot" => $"vsrtl::w4::bnot({Expression64(args[0], width, signed)},{wm})",
            "neg" => $"vsrtl::w4::arith(vsrtl::w4::lit(0ULL),{Expression64(args[0], width, signed)},{wm},true)",
            "pos" => $"vsrtl::w4::resize({Expression64(args[0], width, signed)},{wm})",
            "mul" or "div" or "mod" => signed
                ? $"vsrtl::w4::sproduct({Expression64(args[0], width, true)},{Expression64(args[1], width, true)},{width},{wm},{(kind == "mul" ? 0 : kind == "div" ? 1 : 2)})"
                : $"vsrtl::w4::product({Expression64(args[0], width, false)},{Expression64(args[1], width, false)},{wm},{(kind == "mul" ? 0 : kind == "div" ? 1 : 2)})",
            "cond" => $"vsrtl::w4::cond({Expression64(args[0])},{Expression64(args[1], width, signed)},{Expression64(args[2], width, signed)},{wm})",
            "ashr" when signed => $"vsrtl::w4::sshift({Expression64(args[0], width, true)},{Expression64(args[1])},{width},{wm})",
            "shl" or "shr" or "ashr" => $"vsrtl::w4::shift({Expression64(args[0], width, signed)},{Expression64(args[1])},{wm},{(kind == "shl" ? "false" : "true")})",
            "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor" => $"vsrtl::w4::red({Expression64(args[0])},{CppMaskHex(ExpressionWidth(args[0]))},{(kind == "redand" ? 0 : kind == "rednand" ? 1 : kind == "redor" ? 2 : kind == "rednor" ? 3 : kind == "redxor" ? 4 : 5)})",
            "bitxnor" => $"vsrtl::w4::bnot(vsrtl::w4::bbin({Expression64(args[0], width, signed)},{Expression64(args[1], width, signed)},{wm},2),{wm})",
            "bitand" or "bitor" or "bitxor" => $"vsrtl::w4::bbin({Expression64(args[0], width, signed)},{Expression64(args[1], width, signed)},{wm},{(kind == "bitand" ? 0 : kind == "bitor" ? 1 : 2)})",
            "not" => $"vsrtl::w4::lnot({Expression64(args[0])})",
            "and" => $"vsrtl::w4::land({Expression64(args[0])},{Expression64(args[1])})",
            "or" => $"vsrtl::w4::lor({Expression64(args[0])},{Expression64(args[1])})",
            "eq" or "neq" => (kind == "neq" ? "vsrtl::w4::lnot(" : "") + $"vsrtl::w4::eq({Expression64(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{Expression64(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))})" + (kind == "neq" ? ")" : ""),
            "wildeq" or "wildneq" => $"vsrtl::w4::{(kind == "wildneq" ? "wildneq" : "wildeq")}({Expression64(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{Expression64(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{CppMaskHex(args.Max(ExpressionWidth))})",
            "caseeq" or "caseneq" => $"vsrtl::w4::lit(uint64_t({(kind == "caseneq" ? "!" : "")}vsrtl::w4::exact({Expression64(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{Expression64(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))})))",
            "lt" or "le" or "gt" or "ge" => args.All(ExpressionSigned)
                ? $"vsrtl::w4::scmp({Expression64(args[0], args.Max(ExpressionWidth), true)},{Expression64(args[1], args.Max(ExpressionWidth), true)},{args.Max(ExpressionWidth)},{(kind == "lt" ? 0 : kind == "le" ? 1 : kind == "gt" ? 2 : 3)})"
                : $"vsrtl::w4::cmp({Expression64(args[0], args.Max(ExpressionWidth), false)},{Expression64(args[1], args.Max(ExpressionWidth), false)},{(kind == "lt" ? 0 : kind == "le" ? 1 : kind == "gt" ? 2 : 3)})",
            "concat" => $"vsrtl::w4::concat({Expression64(args[0])},{Expression64(args[1])},{ExpressionWidth(args[1])})",
            _ => throw new IrValidationException("Unsupported narrow64 expression.")
        };
    }

    private static string StoreWord64(string value, RtlSignal signal, int exprWidth = -1)
    {
        string m = CppMaskHex(signal.Width);
        return signal.TwoState
            ? $"vsrtl::w4::two_state({value},{m})"
            : (exprWidth == signal.Width ? value : $"vsrtl::w4::resize({value},{m})");
    }

    private void WordWrite64(StringBuilder code, JsonElement target, string value, int exprWidth = -1)
    {
        if (Kind(target) == "ref")
        {
            int id = SignalId(Name(target));
            code.AppendLine($"_vs_next{id}={StoreWord64(value, Signals[id], exprWidth)};");
            return;
        }
        int selectedId = SignalId(Name(target.GetProperty("value"))), width = ExpressionWidth(target);
        string basis = Kind(target) == "bitselect" ? Expression64(target.GetProperty("index")) : Kind(target) == "indexedselect" ? Expression64(target.GetProperty("base")) : $"vsrtl::w4::lit({target.GetProperty("right").GetInt32()}ULL)";
        bool descending = Kind(target) == "indexedselect" && target.GetProperty("descending").GetBoolean();
        string deposited = $"vsrtl::w4::depidx(_vs_next{selectedId},{Signals[selectedId].Width},{basis},{width},{(descending ? "true" : "false")},{value})";
        code.AppendLine($"_vs_next{selectedId}={(Signals[selectedId].TwoState ? $"vsrtl::w4::two_state({deposited},{CppMaskHex(Signals[selectedId].Width)})" : deposited)};");
    }

    private void Statement64(StringBuilder code, JsonElement e, ref int unique, bool trackBudget, List<(string Id, string Kind)>? coverage = null)
    {
        if (trackBudget) code.AppendLine("execution_step(execution_budget);");
        switch (Kind(e))
        {
            case "while":
            case "do":
                if (Kind(e) == "do") code.AppendLine("do{");
                else code.AppendLine($"while(vsrtl::w4::yes({Expression64(e.GetProperty("condition"))})){{");
                code.AppendLine("execution_step(execution_budget);");
                Statement64(code, e.GetProperty("body"), ref unique, trackBudget, coverage);
                code.AppendLine(Kind(e) == "do" ? $"}}while(vsrtl::w4::yes({Expression64(e.GetProperty("condition"))}));" : "}");
                break;
            case "runtime_repeat":
            {
                string runtimeLoop = "runtime_repeat" + unique++;
                var cntEl = e.GetProperty("count");
                code.AppendLine($"for(unsigned {runtimeLoop}=vsrtl::w4::repeat_count({Expression64(cntEl)},{ExpressionWidth(cntEl)},{(ExpressionSigned(cntEl) ? "true" : "false")});{runtimeLoop}>0;--{runtimeLoop}){{");
                code.AppendLine("execution_step(execution_budget);");
                Statement64(code, e.GetProperty("body"), ref unique, trackBudget, coverage);
                code.AppendLine("}");
                break;
            }
            case "repeat":
                string loop = "repeat" + unique++;
                code.AppendLine($"for(int {loop}=0;{loop}<{e.GetProperty("count").GetInt32()};++{loop}){{");
                Statement64(code, e.GetProperty("body"), ref unique, trackBudget, coverage);
                code.AppendLine("}");
                break;
            case "block":
                foreach (var item in e.GetProperty("items").EnumerateArray()) Statement64(code, item, ref unique, trackBudget, coverage);
                break;
            case "nba":
                var target = e.GetProperty("target");
                if (Kind(target) == "concat")
                {
                    int total = ExpressionWidth(target), remaining = total;
                    string rhs = "nba" + unique++;
                    code.AppendLine($"{{auto {rhs}=vsrtl::w4::resize({Expression64(e.GetProperty("value"), total)},{CppMaskHex(total)});");
                    foreach (var leaf in TargetLeaves(target))
                    {
                        int width = ExpressionWidth(leaf);
                        remaining -= width;
                        WordWrite64(code, leaf, $"vsrtl::w4::selpart({rhs},{total},{remaining + width - 1},{remaining})", width);
                    }
                    code.AppendLine("}");
                }
                else if (Kind(target) is "ref" or "bitselect" or "partselect" or "indexedselect")
                {
                    int tw = ExpressionWidth(target);
                    var valEl = e.GetProperty("value");
                    int vw = ExpressionWidth(valEl);
                    bool exactWidth = vw == tw && !(ExpressionSigned(valEl) && tw > vw);
                    WordWrite64(code, target, Expression64(valEl, tw), exactWidth ? tw : -1);
                }
                else
                {
                    int id = MemoryId(Name(target));
                    string mm = CppMaskHex(Memories[id].Width);
                    var value = Expression64(e.GetProperty("value"), Memories[id].Width);
                    if (Memories[id].TwoState) value = $"vsrtl::w4::two_state({value},{mm})";
                    code.AppendLine($"_vs_mlog.push({id},{Memories[id].Depth},{MemoryIndex64(target, id)},{value},{mm});");
                }
                break;
            case "if":
            {
                int ifId = unique++;
                int thenCov = RegisterCoveragePoint(coverage, $"if.{ifId}.then", "branch");
                int elseCov = RegisterCoveragePoint(coverage, $"if.{ifId}.else", "branch");
                code.AppendLine($"if(vsrtl::w4::yes({Expression64(e.GetProperty("condition"))})){{");
                if (thenCov >= 0) code.AppendLine($"++_vs_cov[{thenCov}];");
                Statement64(code, e.GetProperty("then"), ref unique, trackBudget, coverage);
                code.AppendLine("}else{");
                if (elseCov >= 0) code.AppendLine($"++_vs_cov[{elseCov}];");
                Statement64(code, e.GetProperty("else"), ref unique, trackBudget, coverage);
                code.AppendLine("}");
                break;
            }
            case "assert":
                code.AppendLine($"vsrtl::w4::check_assert({Expression64(e.GetProperty("condition"))},{CppStringLiteral(e.TryGetProperty("message", out var am) ? am.GetString()! : "")});");
                break;
            case "finish":
                code.AppendLine("finish();");
                break;
            case "fatal":
            {
                int fCode = e.TryGetProperty("code", out var fc) ? fc.GetInt32() : 1;
                if (e.TryGetProperty("format", out var ff))
                {
                    string fmt = CppStringLiteral(ff.GetString()!);
                    string argList = e.TryGetProperty("args", out var fa)
                        ? string.Join(",", fa.EnumerateArray().Select(a => $"vsrtl::w4::from({Expression64(a)},{ExpressionWidth(a)})"))
                        : "";
                    code.AppendLine($"fatal({fCode},vsrtl::format_display({fmt},{{{argList}}},false));");
                }
                else
                {
                    code.AppendLine($"fatal({fCode});");
                }
                break;
            }
            case "sample_covergroup":
                code.AppendLine(EmitCppSampleCovergroupStmt(e, ex => Expression64(ex), true, false));
                break;
            case "display":
            {
                string fmt = CppStringLiteral(e.GetProperty("format").GetString()!);
                bool nl = !e.TryGetProperty("newline", out var nlProp) || nlProp.GetBoolean();
                string argList = e.TryGetProperty("args", out var dArgs)
                    ? string.Join(",", dArgs.EnumerateArray().Select(a => $"vsrtl::w4::from({Expression64(a)},{ExpressionWidth(a)})"))
                    : "";
                code.AppendLine($"emitDisplay(vsrtl::format_display({fmt},{{{argList}}},{(nl ? "true" : "false")}));");
                break;
            }
            case "value_plusargs":
            {
                string fmt = CppStringLiteral(e.GetProperty("format").GetString()!);
                int tid = SignalId(e.GetProperty("target").GetString()!);
                string tm = CppMaskHex(Signals[tid].Width);
                code.AppendLine($"{{W _vs_vp=vsrtl::w4::from(_vs_next{tid},{Signals[tid].Width});uint32_t _vs_vpm=valuePlusargs({fmt},_vs_vp);if(_vs_vpm)_vs_next{tid}={StoreWord64($"vsrtl::w4::to(_vs_vp,{tm})", Signals[tid])};");
                if (e.TryGetProperty("matched", out var vpMatched) && vpMatched.ValueKind != JsonValueKind.Null)
                {
                    int mid = SignalId(vpMatched.GetString()!);
                    code.AppendLine($"_vs_next{mid}={StoreWord64("vsrtl::w4::lit(_vs_vpm)", Signals[mid])};");
                }
                code.AppendLine("}");
                break;
            }
            case "force":
            {
                int tid = SignalId(e.GetProperty("target").GetString()!);
                code.AppendLine($"forceSignal({tid},vsrtl::w4::from({StoreWord64(Expression64(e.GetProperty("value")), Signals[tid])},{Signals[tid].Width}));");
                break;
            }
            case "release":
            {
                int tid = SignalId(e.GetProperty("target").GetString()!);
                code.AppendLine($"releaseSignal({tid});");
                break;
            }
            case "case":
            {
                int caseId = unique++;
                var label = "case" + caseId;
                var items = e.GetProperty("items").EnumerateArray().ToArray();
                string caseKind = CaseKind(e);
                var allMatches = items.SelectMany(CaseItemMatches).ToArray();
                int caseWidth = Math.Max(ExpressionWidth(e.GetProperty("value")), allMatches.Select(ExpressionWidth).DefaultIfEmpty(1).Max());
                bool caseSigned = ExpressionSigned(e.GetProperty("value")) && allMatches.All(ExpressionSigned);
                string cm = CppMaskHex(caseWidth);
                code.AppendLine($"{{auto {label}={Expression64(e.GetProperty("value"), caseWidth, caseSigned)};");
                bool first = true;
                int armIdx = 0;
                foreach (var item in items.Where(i => !IsCaseDefault(i)))
                {
                    var ms = CaseItemMatches(item);
                    string cond = string.Join("||", ms.Select(m => caseKind switch
                    {
                        "casez" => $"vsrtl::w4::casez_match({label},{Expression64(m, caseWidth, caseSigned)},{cm})",
                        "casex" => $"vsrtl::w4::casex_match({label},{Expression64(m, caseWidth, caseSigned)},{cm})",
                        _ => $"vsrtl::w4::exact({label},{Expression64(m, caseWidth, caseSigned)})"
                    }));
                    int armCov = RegisterCoveragePoint(coverage, $"case.{caseId}.arm.{armIdx++}", "case_arm");
                    code.AppendLine($"{(first ? "if" : "else if")}({cond}){{");
                    if (armCov >= 0) code.AppendLine($"++_vs_cov[{armCov}];");
                    Statement64(code, item.GetProperty("body"), ref unique, trackBudget, coverage);
                    code.AppendLine("}");
                    first = false;
                }
                var defaultItem = items.FirstOrDefault(IsCaseDefault);
                if (defaultItem.ValueKind != JsonValueKind.Undefined)
                {
                    int defCov = RegisterCoveragePoint(coverage, $"case.{caseId}.default", "case_default");
                    code.AppendLine(first ? "{" : "else{");
                    if (defCov >= 0) code.AppendLine($"++_vs_cov[{defCov}];");
                    Statement64(code, defaultItem.GetProperty("body"), ref unique, trackBudget, coverage);
                    code.AppendLine("}");
                }
                code.AppendLine("}");
                break;
            }
        }
    }

    private string EmitNarrow64ModelHeader(string top, IReadOnlyDictionary<string, string> names, int[] seqIds, bool hasFixedMemWrites, bool hasRuntimeLoops, bool canThrow, IReadOnlyList<(string Name, int ArgCount)> dpiCalls, bool enableCoverage, int threads = 1)
    {
        string className = "V" + top;
        bool hasInit = Statements(Initialization).Any(st => Kind(st) is not "block");
        var procs = Kind(Body) == "parallel" ? Body.GetProperty("items").EnumerateArray().ToArray() : [Body];
        var eventIds = EventSignals.Select(SignalId).ToArray();
        var coverage = enableCoverage ? new List<(string Id, string Kind)>() : null;
        bool canReenter = dpiCalls.Count > 0 || Statements(Body).Any(st => Kind(st) is "display" or "finish" or "fatal" or "force" or "release");

        void EmitStep64(StringBuilder sb, JsonElement step)
        {
            int id = SignalId(Name(step.GetProperty("target")));
            var rhs = step.GetProperty("value");
            int vw = ExpressionWidth(rhs);
            bool exactWidth = vw == Signals[id].Width && !(ExpressionSigned(rhs) && Signals[id].Width > vw);
            sb.AppendLine($"_vs_s[{id}]={StoreWord64(Expression64(rhs, Signals[id].Width), Signals[id], exactWidth ? Signals[id].Width : -1)};");
        }

        var bodyCode = new StringBuilder();
        bodyCode.AppendLine("inline void settle(){");
        if (threads <= 1)
        {
            foreach (var step in SettledSteps) EmitStep64(bodyCode, step);
        }
        else
        {
            foreach (var level in SettledLevels)
            {
                if (level.Count < 2) { foreach (var step in level) EmitStep64(bodyCode, step); continue; }
                bodyCode.AppendLine($"_vs_pool.parallel_for({level.Count},[&](size_t _lvl_i){{switch(_lvl_i){{");
                for (int idx = 0; idx < level.Count; ++idx)
                {
                    bodyCode.Append($"case {idx}:{{");
                    EmitStep64(bodyCode, level[idx]);
                    bodyCode.AppendLine("}break;");
                }
                bodyCode.AppendLine("}});");
            }
        }
        bodyCode.AppendLine("}\nvoid syncToBase() const override {");
        bodyCode.AppendLine($"for(size_t i=0;i<{Signals.Count};++i)vsrtl::w4::store(s[i],_vs_s[i]);");
        bodyCode.AppendLine("}\nvoid syncFromBase() override {");
        for (int i = 0; i < Signals.Count; ++i)
            bodyCode.AppendLine($"_vs_s[{i}]=vsrtl::w4::to(s[{i}],{CppMaskHex(Signals[i].Width)});");
        bodyCode.AppendLine("}\nvoid evaluate() override {");
        if (dpiCalls.Count > 0) bodyCode.AppendLine("struct _VsDpiScope{svScope prev;explicit _VsDpiScope(svScope s):prev(svSetScope(s)){}~_VsDpiScope(){svSetScope(prev);}} _vs_dpi_scope(static_cast<svScope>(this));");
        if (hasRuntimeLoops) bodyCode.AppendLine("execution_budget=20000;");
        if (canThrow)
        {
            bodyCode.Append($"vsrtl::w4::W4 saved_s[{Signals.Count}];std::memcpy(saved_s,_vs_s,sizeof(_vs_s));");
            bodyCode.Append($"vsrtl::w4::W4 saved_prev[{Signals.Count}];std::memcpy(saved_prev,_vs_prev,sizeof(_vs_prev));");
            for (int i = 0; i < Memories.Count; ++i)
                bodyCode.Append($"vsrtl::w4::W4 saved_m{i}[{Memories[i].Depth}];std::memcpy(saved_m{i},_vs_m{i},sizeof(_vs_m{i}));");
            bodyCode.AppendLine("try{");
        }
        bodyCode.AppendLine("settle();");
        EmitCppPropertyEvaluation(bodyCode, ex => Expression64(ex), ex => $"vsrtl::w4::yes({ex})", sid => $"_vs_s[{sid}]", (cid, edge) => $"vsrtl::w4::edge(_vs_pclk_{cid},_vs_s[{cid}],{(edge == "posedge" ? "true" : "false")})", true, false);
        for (int p = 0; p < procs.Length; ++p)
        {
            string cond = string.Join("||", procs[p].GetProperty("events").EnumerateArray().Select(trigger =>
            {
                int id = SignalId(trigger.GetProperty("signal").GetString()!);
                return $"vsrtl::w4::edge(_vs_prev[{id}],_vs_s[{id}],{(trigger.GetProperty("edge").GetString() == "posedge" ? "true" : "false")})";
            }));
            bodyCode.AppendLine($"const bool _vs_ev{p}={cond};");
        }
        string anyEv = string.Join("||", Enumerable.Range(0, procs.Length).Select(p => $"_vs_ev{p}"));
        bodyCode.AppendLine($"if({anyEv}){{");
        if (seqIds.Length > 0)
            bodyCode.AppendLine(string.Concat(seqIds.Select(id => $"vsrtl::w4::W4 _vs_next{id}=_vs_s[{id}];")));
        int unique = 0;
        for (int p = 0; p < procs.Length; ++p)
        {
            if (procs.Length > 1) bodyCode.AppendLine($"if(_vs_ev{p}){{");
            Statement64(bodyCode, procs[p].GetProperty("body"), ref unique, hasRuntimeLoops, coverage);
            if (procs.Length > 1) bodyCode.AppendLine("}");
        }
        if (seqIds.Length > 0)
            bodyCode.AppendLine(string.Concat(seqIds.Select(id => $"_vs_s[{id}]=_vs_next{id};")));
        if (hasFixedMemWrites)
            bodyCode.AppendLine("_vs_mlog.commit_flat(_vs_mptrs);");
        bodyCode.AppendLine("settle();\n}");
        foreach (int id in eventIds)
            bodyCode.AppendLine($"_vs_prev[{id}]=_vs_s[{id}];");
        if (canThrow)
        {
            bodyCode.Append("\n}catch(...){std::memcpy(_vs_s,saved_s,sizeof(_vs_s));std::memcpy(_vs_prev,saved_prev,sizeof(_vs_prev));");
            for (int i = 0; i < Memories.Count; ++i)
                bodyCode.Append($"std::memcpy(_vs_m{i},saved_m{i},sizeof(_vs_m{i}));");
            if (hasFixedMemWrites) bodyCode.Append("_vs_mlog.count=0;_vs_mlog.overflow.clear();");
            bodyCode.AppendLine("throw;}");
        }
        bodyCode.AppendLine("}\nvoid saveExtraState(std::vector<uint8_t>& out) const override {");
        bodyCode.AppendLine("out.clear();");
        bodyCode.AppendLine("{const auto* p=reinterpret_cast<const uint8_t*>(_vs_prev);out.insert(out.end(),p,p+sizeof(_vs_prev));}");
        for (int i = 0; i < Memories.Count; ++i)
            bodyCode.AppendLine($"{{const auto* p=reinterpret_cast<const uint8_t*>(_vs_m{i});out.insert(out.end(),p,p+sizeof(_vs_m{i}));}}");
        bodyCode.AppendLine("}\nvoid restoreExtraState(const uint8_t* data,size_t len) override {");
        bodyCode.AppendLine("size_t off=0;");
        bodyCode.AppendLine("if(off+sizeof(_vs_prev)>len)throw std::runtime_error(\"checkpoint W4 state size mismatch\");std::memcpy(_vs_prev,data+off,sizeof(_vs_prev));off+=sizeof(_vs_prev);");
        for (int i = 0; i < Memories.Count; ++i)
            bodyCode.AppendLine($"if(off+sizeof(_vs_m{i})>len)throw std::runtime_error(\"checkpoint W4 memory size mismatch\");std::memcpy(_vs_m{i},data+off,sizeof(_vs_m{i}));off+=sizeof(_vs_m{i});");
        bodyCode.AppendLine("if(off!=len)throw std::runtime_error(\"checkpoint W4 extra trailing bytes\");");
        bodyCode.AppendLine("}");
        if (Memories.Count > 0)
        {
            bodyCode.AppendLine("bool loadDerivedMemory(size_t mem_id,const std::string& path,bool hex,size_t start_addr,size_t end_addr,size_t& loaded) override {");
            bodyCode.AppendLine("switch(mem_id){");
            for (int i = 0; i < Memories.Count; ++i)
                bodyCode.AppendLine($"case {i}: loaded=vsrtl::load_mem_file_flat(_vs_m{i},{Memories[i].Depth},{Memories[i].Width},path,hex,start_addr,end_addr,{(Memories[i].TwoState ? "true" : "false")});return true;");
            bodyCode.AppendLine("default: return false;}}");
            bodyCode.AppendLine($"size_t memoryCount() const override {{ return {Memories.Count}; }}");
            bodyCode.AppendLine("size_t memoryDepth(size_t mem_id) const override { switch(mem_id){");
            for (int i = 0; i < Memories.Count; ++i) bodyCode.AppendLine($"case {i}: return {Memories[i].Depth};");
            bodyCode.AppendLine("default: return 0; }}");
            bodyCode.AppendLine("int memoryWidth(size_t mem_id) const override { switch(mem_id){");
            for (int i = 0; i < Memories.Count; ++i) bodyCode.AppendLine($"case {i}: return {Memories[i].Width};");
            bodyCode.AppendLine("default: return 0; }}");
            bodyCode.AppendLine("W readMemoryWord(size_t mem_id,size_t addr) const override { checkThread(); switch(mem_id){");
            for (int i = 0; i < Memories.Count; ++i) bodyCode.AppendLine($"case {i}: if(addr>={Memories[i].Depth})throw std::out_of_range(\"memory word address out of range\");return vsrtl::w4::from(_vs_m{i}[addr],{Memories[i].Width});");
            bodyCode.AppendLine("default: throw std::out_of_range(\"memory word address out of range\"); }}");
            bodyCode.AppendLine("void writeMemoryWord(size_t mem_id,size_t addr,W value) override { checkThread(); switch(mem_id){");
            for (int i = 0; i < Memories.Count; ++i)
            {
                string mm = CppMaskHex(Memories[i].Width);
                bodyCode.AppendLine($"case {i}: if(addr>={Memories[i].Depth})throw std::out_of_range(\"memory word address out of range\");_vs_m{i}[addr]={(Memories[i].TwoState ? $"vsrtl::w4::two_state(vsrtl::w4::to(value,{mm}),{mm})" : $"vsrtl::w4::to(value,{mm})")};markDirty();return;");
            }
            bodyCode.AppendLine("default: throw std::out_of_range(\"memory word address out of range\"); }}");
        }
        bodyCode.AppendLine("public:");
        EmitCoveragePointsOverride(bodyCode, coverage);
        // Non-virtual inline fast-path lets the C++ compiler inline evaluate() directly into host loops.
        bodyCode.AppendLine("inline void eval(){");
        bodyCode.AppendLine("if(fast_eval_ok&&isOwnerThreadFast())[[likely]]{");
        if (!canReenter)
        {
            bodyCode.AppendLine("fast_dirty=false;");
            if (canThrow)
                bodyCode.AppendLine($"try{{this->{className}::evaluate();}}catch(...){{fast_dirty=true;throw;}}");
            else
                bodyCode.AppendLine($"this->{className}::evaluate();");
            bodyCode.AppendLine("return;}");
        }
        else
        {
            bodyCode.AppendLine("fast_eval_ok=false;fast_evaluating=true;fast_dirty=false;");
            if (canThrow)
                bodyCode.AppendLine($"try{{this->{className}::evaluate();}}catch(...){{fast_evaluating=false;fast_dirty=true;fast_eval_ok=fast_eligible;throw;}}");
            else
                bodyCode.AppendLine($"this->{className}::evaluate();");
            bodyCode.AppendLine("fast_evaluating=false;if(!fast_eligible)[[unlikely]]applyForces();fast_eval_ok=fast_eligible;return;}");
        }
        bodyCode.AppendLine("evalSlow();}");
        foreach (var signal in Signals.Where(s => s.Direction != "internal")) bodyCode.AppendLine($"vsrtl::Port {names[signal.Name]};");
        bodyCode.Append($"{className}():vsrtl::Model(\"{top}\",{{");
        bodyCode.AppendJoin(',', Signals.Select(s => $"{{\"{s.SourcePath ?? s.Name}\",{s.Width},{(s.Direction == "input" ? "vpiInput" : s.Direction == "output" ? "vpiOutput" : "vpiNoDirection")},{(s.Signed ? "true" : "false")},{(s.TwoState ? "true" : "false")}}}"));
        bodyCode.Append($"}},{SignalId(Clock!)})");
        foreach (var signal in Signals.Where(s => s.Direction != "internal")) bodyCode.Append($",{names[signal.Name]}(*this,{SignalId(signal.Name)},&_vs_s[{SignalId(signal.Name)}])");
        bodyCode.AppendLine("{");
        bodyCode.AppendLine("initFastThreadTag();");
        for (int i = 0; i < Memories.Count; ++i)
        {
            string initWord = Memories[i].TwoState ? "vsrtl::w4::lit(0ULL)" : $"vsrtl::w4::unknown({CppMaskHex(Memories[i].Width)})";
            bodyCode.AppendLine($"for(int j=0;j<{Memories[i].Depth};++j)_vs_m{i}[j]={initWord};");
        }
        if (hasFixedMemWrites)
        {
            for (int i = 0; i < Memories.Count; ++i)
                bodyCode.AppendLine($"_vs_mptrs[{i}]=_vs_m{i};");
        }
        if (hasInit)
        {
            bodyCode.AppendLine($"assoc.resize({Memories.Count});mem={{");
            bodyCode.AppendJoin(',', Memories.Select(m => $"std::vector<W>({m.Depth},{(m.TwoState ? $"word(0,0,0,{m.Width})" : $"unknown({m.Width})")})"));
            bodyCode.AppendLine("};");
            EmitInitialization(bodyCode);
            for (int i = 0; i < Memories.Count; ++i)
                bodyCode.AppendLine($"for(int j=0;j<{Memories[i].Depth};++j)_vs_m{i}[j]=vsrtl::w4::to(mem[{i}][j],{CppMaskHex(Memories[i].Width)});");
            bodyCode.AppendLine("mem.clear();");
        }
        bodyCode.AppendLine("syncFromBase();");
        bodyCode.AppendLine($"for(int i=0;i<{Signals.Count};++i)_vs_prev[i]=vsrtl::w4::unknown(1ULL);");
        bodyCode.AppendLine($"_vs_prev[{SignalId(Clock!)}]=vsrtl::w4::lit(0ULL);");
        foreach (var signal in Signals.Where(s => s.TwoState && s.Width == 1))
            bodyCode.AppendLine($"_vs_prev[{SignalId(signal.Name)}]=vsrtl::w4::lit(0ULL);");
        EmitCppCovergroupRegistration(bodyCode);
        EmitCppPropertyInit(bodyCode, true, false);
        if (enableCoverage) bodyCode.AppendLine("enableToggleCoverage(true);");
        bodyCode.AppendLine("}");
        bodyCode.AppendLine($"std::unique_ptr<{className}> fork() const {{ auto c = std::make_unique<{className}>(); c->vsrtl::Model::restore(this->vsrtl::Model::snapshot()); return c; }}");
        bodyCode.AppendLine("};");

        var code = new StringBuilder("#pragma once\n#include \"external_model.h\"\n" + (threads > 1 ? "#include \"thread_pool.h\"\n" : "") + "using namespace vsrtl;\n");
        EmitDpiPrototypes(code, dpiCalls);
        code.AppendLine($"class {className} final : public vsrtl::Model {{");
        if (threads > 1) code.AppendLine($"vsrtl::ThreadPool _vs_pool{{{threads}}};");
        if (hasRuntimeLoops || hasInit) code.AppendLine("size_t execution_budget=20000;");
        code.AppendLine($"vsrtl::w4::W4 _vs_s[{Signals.Count}];");
        code.AppendLine($"vsrtl::w4::W4 _vs_prev[{Signals.Count}];");
        for (int i = 0; i < Memories.Count; ++i)
            code.AppendLine($"vsrtl::w4::W4 _vs_m{i}[{Memories[i].Depth}];");
        if (hasFixedMemWrites)
        {
            code.AppendLine($"vsrtl::w4::W4* _vs_mptrs[{Memories.Count}];");
            code.AppendLine("vsrtl::w4::MemLog4<16> _vs_mlog;");
        }
        if (hasInit) code.AppendLine("std::vector<AssociativeStorage>assoc;");
        if (coverage is { Count: > 0 }) code.AppendLine($"uint64_t _vs_cov[{coverage.Count}]{{}};");
        EmitCppPropertyMembers(code, true, false);
        code.Append(bodyCode);
        return code.ToString();
    }
}
