using System.Text;
using System.Text.Json;
namespace VeloSim;

public sealed partial class RtlDesign
{
    private bool ExpressionFits64(JsonElement e)
    {
        string kind = Kind(e);
        if (kind is "convert" or "size" or "exists") return false;
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

    private static string EmitConst64(string bits)
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
        if ((x | z) == 0) return $"vsrtl::w64::lit(0x{v:x}ULL,{w})";
        return $"vsrtl::w64::lit(0x{v:x}ULL,0x{x:x}ULL,0x{z:x}ULL,{w})";
    }

    private string MemoryIndex64(JsonElement access, int id)
    {
        string idx = Expression64(access.GetProperty("index"));
        return Memories[id].LowerBound == 0 ? idx : $"vsrtl::w64::memidx({idx},{Memories[id].LowerBound})";
    }

    private string Expression64(JsonElement e, int context = 0, bool? forceSigned = null)
    {
        string kind = Kind(e);
        bool signed = forceSigned ?? ExpressionSigned(e);
        string Extend(string value) => signed && context > ExpressionWidth(e) ? $"vsrtl::w64::sresize({value},{context})" : value;
        if (kind == "ref") return Extend($"vsrtl::w64::to(s[{SignalId(Name(e))}])");
        if (kind == "const") return Extend(EmitConst64(e.GetProperty("bits").GetString()!));
        if (kind == "index")
        {
            int id = MemoryId(Name(e));
            return Extend($"vsrtl::w64::read(mem[{id}],{MemoryIndex64(e, id)},{Memories[id].Width})");
        }
        if (kind == "bitselect") return $"vsrtl::w64::selbit({Expression64(e.GetProperty("value"))},{Expression64(e.GetProperty("index"))})";
        if (kind == "partselect") return $"vsrtl::w64::selpart({Expression64(e.GetProperty("value"))},{e.GetProperty("left").GetInt32()},{e.GetProperty("right").GetInt32()})";
        if (kind == "indexedselect") return $"vsrtl::w64::selidx({Expression64(e.GetProperty("value"))},{Expression64(e.GetProperty("base"))},{e.GetProperty("width").GetInt32()},{(e.GetProperty("descending").GetBoolean() ? "true" : "false")})";
        if (kind == "test_plusargs") return Extend($"vsrtl::w64::lit(testPlusargs({CppStringLiteral(e.GetProperty("prefix").GetString()!)}),32)");
        if (kind == "urandom") return Extend("vsrtl::w64::lit(nextUrandom(),32)");
        if (kind == "random") return Extend("vsrtl::w64::lit(uint32_t(nextRandom()),32)");
        if (kind == "urandom_range")
        {
            string maxExpr = $"uint32_t(({Expression64(e.GetProperty("max"))}).v)";
            string minExpr = e.TryGetProperty("min", out var minEl) ? $"uint32_t(({Expression64(minEl)}).v)" : "0u";
            return Extend($"vsrtl::w64::lit(nextUrandomRange({maxExpr},{minExpr}),32)");
        }
        if (kind == "resolve")
        {
            int nk = ParseNetKind(e.GetProperty("net").GetString()!);
            int rw = Math.Max(context, ExpressionWidth(e));
            string drivers = string.Join(",", e.GetProperty("args").EnumerateArray().Select(d => Expression64(d, rw)));
            return Extend($"vsrtl::w64::resolve_net({nk},{{{drivers}}},{rw})");
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
                    string cmp = cs ? "vsrtl::w64::scmp" : "vsrtl::w64::cmp";
                    parts.Add($"vsrtl::w64::land({cmp}({Expression64(valEl, cw, cs)},{Expression64(loEl, cw, cs)},3),{cmp}({Expression64(valEl, cw, cs)},{Expression64(hiEl, cw, cs)},1))");
                }
                else
                {
                    int cw = Math.Max(vw, ExpressionWidth(it));
                    bool cs = vSigned && ExpressionSigned(it);
                    parts.Add($"vsrtl::w64::wildeq({Expression64(valEl, cw, cs)},{Expression64(it, cw, cs)})");
                }
            }
            string acc = parts[0];
            for (int i = 1; i < parts.Count; ++i) acc = $"vsrtl::w64::lor({acc},{parts[i]})";
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
            return $"vsrtl::w64::lit(uint64_t(vsrtl::w64::edge(vsrtl::w64::selbit(_vs_past_{sid}[0],vsrtl::w64::lit(0ULL,1)),vsrtl::w64::selbit(vsrtl::w64::to(s[{sid}]),vsrtl::w64::lit(0ULL,1)),true)),1)";
        }
        if (kind == "fell")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            return $"vsrtl::w64::lit(uint64_t(vsrtl::w64::edge(vsrtl::w64::selbit(_vs_past_{sid}[0],vsrtl::w64::lit(0ULL,1)),vsrtl::w64::selbit(vsrtl::w64::to(s[{sid}]),vsrtl::w64::lit(0ULL,1)),false)),1)";
        }
        if (kind == "stable")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            return $"vsrtl::w64::lit(uint64_t(vsrtl::w64::exact(vsrtl::w64::to(s[{sid}]),_vs_past_{sid}[0])),1)";
        }
        if (kind == "dpi_call")
        {
            string callArgs = string.Join(",", e.GetProperty("args").EnumerateArray().Select(a => $"({Expression64(a)}).v"));
            return Extend($"vsrtl::w64::word(::{Name(e)}({callArgs}),0ULL,0ULL,{e.GetProperty("width").GetInt32()})");
        }
        var args = e.GetProperty("args").EnumerateArray().ToArray();
        int width = Math.Max(context, ExpressionWidth(e));
        return kind switch
        {
            "castsigned" or "castunsigned" => Extend(Expression64(args[0])),
            "countones" => Extend($"vsrtl::w64::countones({Expression64(args[0])})"),
            "onehot" => $"vsrtl::w64::onehot({Expression64(args[0])})",
            "onehot0" => $"vsrtl::w64::onehot0({Expression64(args[0])})",
            "isunknown" => $"vsrtl::w64::isunknown({Expression64(args[0])})",
            "clog2" => Extend($"vsrtl::w64::clog2({Expression64(args[0])})"),
            "add" or "sub" => $"vsrtl::w64::arith({Expression64(args[0], width, signed)},{Expression64(args[1], width, signed)},{width},{(kind == "sub" ? "true" : "false")})",
            "bitnot" => $"vsrtl::w64::bnot({Expression64(args[0], width, signed)},{width})",
            "neg" => $"vsrtl::w64::arith(vsrtl::w64::lit(0ULL,{width}),{Expression64(args[0], width, signed)},{width},true)",
            "pos" => $"vsrtl::w64::resize({Expression64(args[0], width, signed)},{width})",
            "mul" or "div" or "mod" => $"vsrtl::w64::{(signed ? "sproduct" : "product")}({Expression64(args[0], width, signed)},{Expression64(args[1], width, signed)},{width},{(kind == "mul" ? 0 : kind == "div" ? 1 : 2)})",
            "cond" => $"vsrtl::w64::cond({Expression64(args[0])},{Expression64(args[1], width, signed)},{Expression64(args[2], width, signed)},{width})",
            "ashr" when signed => $"vsrtl::w64::sshift({Expression64(args[0], width, true)},{Expression64(args[1])},{width})",
            "shl" or "shr" or "ashr" => $"vsrtl::w64::shift({Expression64(args[0], width, signed)},{Expression64(args[1])},{width},{(kind == "shl" ? "false" : "true")})",
            "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor" => $"vsrtl::w64::red({Expression64(args[0])},{(kind == "redand" ? 0 : kind == "rednand" ? 1 : kind == "redor" ? 2 : kind == "rednor" ? 3 : kind == "redxor" ? 4 : 5)})",
            "bitxnor" => $"vsrtl::w64::bnot(vsrtl::w64::bbin({Expression64(args[0], width, signed)},{Expression64(args[1], width, signed)},{width},2),{width})",
            "bitand" or "bitor" or "bitxor" => $"vsrtl::w64::bbin({Expression64(args[0], width, signed)},{Expression64(args[1], width, signed)},{width},{(kind == "bitand" ? 0 : kind == "bitor" ? 1 : 2)})",
            "not" => $"vsrtl::w64::lnot({Expression64(args[0])})",
            "and" => $"vsrtl::w64::land({Expression64(args[0])},{Expression64(args[1])})",
            "or" => $"vsrtl::w64::lor({Expression64(args[0])},{Expression64(args[1])})",
            "eq" or "neq" => (kind == "neq" ? "vsrtl::w64::lnot(" : "") + $"vsrtl::w64::eq({Expression64(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{Expression64(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))})" + (kind == "neq" ? ")" : ""),
            "wildeq" or "wildneq" => $"vsrtl::w64::{(kind == "wildneq" ? "wildneq" : "wildeq")}({Expression64(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{Expression64(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))})",
            "caseeq" or "caseneq" => $"vsrtl::w64::W64{{uint64_t({(kind == "caseneq" ? "!" : "")}vsrtl::w64::exact({Expression64(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{Expression64(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))})),0,0,1}}",
            "lt" or "le" or "gt" or "ge" => $"vsrtl::w64::{(args.All(ExpressionSigned) ? "scmp" : "cmp")}({Expression64(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{Expression64(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{(kind == "lt" ? 0 : kind == "le" ? 1 : kind == "gt" ? 2 : 3)})",
            "concat" => $"vsrtl::w64::concat({Expression64(args[0])},{Expression64(args[1])})",
            _ => throw new IrValidationException("Unsupported narrow64 expression.")
        };
    }

    private static string StoreWord64(string value, RtlSignal signal, int exprWidth = -1) =>
        signal.TwoState ? $"vsrtl::w64::two_state(vsrtl::w64::resize({value},{signal.Width}))" : (exprWidth == signal.Width ? value : $"vsrtl::w64::resize({value},{signal.Width})");

    private void WordWrite64(StringBuilder code, JsonElement target, string value, int exprWidth = -1)
    {
        if (Kind(target) == "ref")
        {
            int id = SignalId(Name(target));
            code.AppendLine($"_vs_next[{id}]={StoreWord64(value, Signals[id], exprWidth)};");
            return;
        }
        int selectedId = SignalId(Name(target.GetProperty("value"))), width = ExpressionWidth(target);
        string basis = Kind(target) == "bitselect" ? Expression64(target.GetProperty("index")) : Kind(target) == "indexedselect" ? Expression64(target.GetProperty("base")) : $"vsrtl::w64::lit({target.GetProperty("right").GetInt32()}ULL,64)";
        bool descending = Kind(target) == "indexedselect" && target.GetProperty("descending").GetBoolean();
        string deposited = $"vsrtl::w64::depidx(_vs_next[{selectedId}],{basis},{width},{(descending ? "true" : "false")},{value})";
        code.AppendLine($"_vs_next[{selectedId}]={(Signals[selectedId].TwoState ? $"vsrtl::w64::two_state({deposited})" : deposited)};");
    }

    private void Statement64(StringBuilder code, JsonElement e, ref int unique, bool trackBudget, List<(string Id, string Kind)>? coverage = null)
    {
        if (trackBudget) code.AppendLine("execution_step(execution_budget);");
        switch (Kind(e))
        {
            case "while":
            case "do":
                if (Kind(e) == "do") code.AppendLine("do{");
                else code.AppendLine($"while(vsrtl::w64::yes({Expression64(e.GetProperty("condition"))})){{");
                code.AppendLine("execution_step(execution_budget);");
                Statement64(code, e.GetProperty("body"), ref unique, trackBudget, coverage);
                code.AppendLine(Kind(e) == "do" ? $"}}while(vsrtl::w64::yes({Expression64(e.GetProperty("condition"))}));" : "}");
                break;
            case "runtime_repeat":
                string runtimeLoop = "runtime_repeat" + unique++;
                code.AppendLine($"for(unsigned {runtimeLoop}=repeat_count(vsrtl::w64::from({Expression64(e.GetProperty("count"))}),{(ExpressionSigned(e.GetProperty("count")) ? "true" : "false")});{runtimeLoop}>0;--{runtimeLoop}){{");
                code.AppendLine("execution_step(execution_budget);");
                Statement64(code, e.GetProperty("body"), ref unique, trackBudget, coverage);
                code.AppendLine("}");
                break;
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
                    code.AppendLine($"{{auto {rhs}=vsrtl::w64::resize({Expression64(e.GetProperty("value"), total)},{total});");
                    foreach (var leaf in TargetLeaves(target))
                    {
                        int width = ExpressionWidth(leaf);
                        remaining -= width;
                        WordWrite64(code, leaf, $"vsrtl::w64::selpart({rhs},{remaining + width - 1},{remaining})", width);
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
                    var value = Expression64(e.GetProperty("value"), Memories[id].Width);
                    if (Memories[id].TwoState) value = $"vsrtl::w64::two_state({value})";
                    code.AppendLine($"_vs_mlog.push({id},mem[{id}].size(),{MemoryIndex64(target, id)},{value},{Memories[id].Width});");
                }
                break;
            case "if":
            {
                int ifId = unique++;
                int thenCov = RegisterCoveragePoint(coverage, $"if.{ifId}.then", "branch");
                int elseCov = RegisterCoveragePoint(coverage, $"if.{ifId}.else", "branch");
                code.AppendLine($"if(vsrtl::w64::yes({Expression64(e.GetProperty("condition"))})){{");
                if (thenCov >= 0) code.AppendLine($"++_vs_cov[{thenCov}];");
                Statement64(code, e.GetProperty("then"), ref unique, trackBudget, coverage);
                code.AppendLine("}else{");
                if (elseCov >= 0) code.AppendLine($"++_vs_cov[{elseCov}];");
                Statement64(code, e.GetProperty("else"), ref unique, trackBudget, coverage);
                code.AppendLine("}");
                break;
            }
            case "assert":
                code.AppendLine($"vsrtl::w64::check_assert({Expression64(e.GetProperty("condition"))},{CppStringLiteral(e.TryGetProperty("message", out var am) ? am.GetString()! : "")});");
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
                        ? string.Join(",", fa.EnumerateArray().Select(a => $"vsrtl::w64::from({Expression64(a)})"))
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
                    ? string.Join(",", dArgs.EnumerateArray().Select(a => $"vsrtl::w64::from({Expression64(a)})"))
                    : "";
                code.AppendLine($"emitDisplay(vsrtl::format_display({fmt},{{{argList}}},{(nl ? "true" : "false")}));");
                break;
            }
            case "value_plusargs":
            {
                string fmt = CppStringLiteral(e.GetProperty("format").GetString()!);
                int tid = SignalId(e.GetProperty("target").GetString()!);
                code.AppendLine($"{{W _vs_vp=vsrtl::w64::from(_vs_next[{tid}]);uint32_t _vs_vpm=valuePlusargs({fmt},_vs_vp);if(_vs_vpm)_vs_next[{tid}]={StoreWord64("vsrtl::w64::to(_vs_vp)", Signals[tid])};");
                if (e.TryGetProperty("matched", out var vpMatched) && vpMatched.ValueKind != JsonValueKind.Null)
                {
                    int mid = SignalId(vpMatched.GetString()!);
                    code.AppendLine($"_vs_next[{mid}]={StoreWord64("vsrtl::w64::lit(_vs_vpm,32)", Signals[mid])};");
                }
                code.AppendLine("}");
                break;
            }
            case "force":
            {
                int tid = SignalId(e.GetProperty("target").GetString()!);
                code.AppendLine($"forceSignal({tid},vsrtl::w64::from({StoreWord64(Expression64(e.GetProperty("value")), Signals[tid])}));");
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
                string matcher = CaseKind(e) switch { "casez" => "vsrtl::w64::casez_match", "casex" => "vsrtl::w64::casex_match", _ => "vsrtl::w64::exact" };
                var allMatches = items.SelectMany(CaseItemMatches).ToArray();
                int caseWidth = Math.Max(ExpressionWidth(e.GetProperty("value")), allMatches.Select(ExpressionWidth).DefaultIfEmpty(1).Max());
                bool caseSigned = ExpressionSigned(e.GetProperty("value")) && allMatches.All(ExpressionSigned);
                code.AppendLine($"{{auto {label}={Expression64(e.GetProperty("value"), caseWidth, caseSigned)};");
                bool first = true;
                int armIdx = 0;
                foreach (var item in items.Where(i => !IsCaseDefault(i)))
                {
                    var ms = CaseItemMatches(item);
                    string cond = string.Join("||", ms.Select(m => $"{matcher}({label},{Expression64(m, caseWidth, caseSigned)})"));
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

        void EmitStep64(StringBuilder sb, JsonElement step)
        {
            int id = SignalId(Name(step.GetProperty("target")));
            var rhs = step.GetProperty("value");
            int vw = ExpressionWidth(rhs);
            bool exactWidth = vw == Signals[id].Width && !(ExpressionSigned(rhs) && Signals[id].Width > vw);
            sb.AppendLine($"vsrtl::w64::store(s[{id}],{StoreWord64(Expression64(rhs, Signals[id].Width), Signals[id], exactWidth ? Signals[id].Width : -1)});");
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
        bodyCode.AppendLine("}\nvoid evaluate() override {");
        if (dpiCalls.Count > 0) bodyCode.AppendLine("struct _VsDpiScope{svScope prev;explicit _VsDpiScope(svScope s):prev(svSetScope(s)){}~_VsDpiScope(){svSetScope(prev);}} _vs_dpi_scope(static_cast<svScope>(this));");
        if (hasRuntimeLoops) bodyCode.AppendLine("execution_budget=20000;");
        if (canThrow)
        {
            bodyCode.AppendLine($"auto saved_s=s;auto saved_mem=mem;vsrtl::w64::W64 saved_prev[{Signals.Count}];for(int i=0;i<{Signals.Count};++i)saved_prev[i]=_vs_prev[i];try{{");
        }
        bodyCode.AppendLine("settle();");
        EmitCppPropertyEvaluation(bodyCode, ex => Expression64(ex), ex => $"vsrtl::w64::yes({ex})", sid => $"vsrtl::w64::to(s[{sid}])", (cid, edge) => $"vsrtl::w64::edge(_vs_pclk_{cid},vsrtl::w64::to(s[{cid}]),{(edge == "posedge" ? "true" : "false")})", true, false);
        for (int p = 0; p < procs.Length; ++p)
        {
            string cond = string.Join("||", procs[p].GetProperty("events").EnumerateArray().Select(trigger =>
            {
                int id = SignalId(trigger.GetProperty("signal").GetString()!);
                return $"vsrtl::w64::edge(_vs_prev[{id}],vsrtl::w64::to(s[{id}]),{(trigger.GetProperty("edge").GetString() == "posedge" ? "true" : "false")})";
            }));
            bodyCode.AppendLine($"const bool _vs_ev{p}={cond};");
        }
        string anyEv = string.Join("||", Enumerable.Range(0, procs.Length).Select(p => $"_vs_ev{p}"));
        bodyCode.AppendLine($"if({anyEv}){{");
        if (seqIds.Length > 0)
            bodyCode.AppendLine(string.Concat(seqIds.Select(id => $"_vs_next[{id}]=vsrtl::w64::to(s[{id}]);")));
        int unique = 0;
        for (int p = 0; p < procs.Length; ++p)
        {
            if (procs.Length > 1) bodyCode.AppendLine($"if(_vs_ev{p}){{");
            Statement64(bodyCode, procs[p].GetProperty("body"), ref unique, hasRuntimeLoops, coverage);
            if (procs.Length > 1) bodyCode.AppendLine("}");
        }
        if (seqIds.Length > 0)
            bodyCode.AppendLine(string.Concat(seqIds.Select(id => $"vsrtl::w64::store(s[{id}],_vs_next[{id}]);")));
        if (hasFixedMemWrites)
            bodyCode.AppendLine("_vs_mlog.commit(mem);");
        bodyCode.AppendLine("settle();\n}");
        foreach (int id in eventIds)
            bodyCode.AppendLine($"_vs_prev[{id}]=vsrtl::w64::to(s[{id}]);");
        if (canThrow)
        {
            bodyCode.AppendLine($"\n}}catch(...){{s=std::move(saved_s);mem=std::move(saved_mem);for(int i=0;i<{Signals.Count};++i)_vs_prev[i]=saved_prev[i];{(hasFixedMemWrites ? "_vs_mlog.count=0;_vs_mlog.overflow.clear();" : "")}throw;}}");
        }
        bodyCode.AppendLine("}\nvoid saveExtraState(std::vector<uint8_t>& out) const override {");
        bodyCode.AppendLine($"const size_t n=sizeof(_vs_prev);out.resize(n);std::memcpy(out.data(),_vs_prev,n);");
        bodyCode.AppendLine("}\nvoid restoreExtraState(const uint8_t* data,size_t len) override {");
        bodyCode.AppendLine($"if(len!=sizeof(_vs_prev))throw std::runtime_error(\"checkpoint W64 state size mismatch\");std::memcpy(_vs_prev,data,len);");
        bodyCode.AppendLine("}\npublic:");
        EmitCoveragePointsOverride(bodyCode, coverage);
        // Non-virtual inline fast-path lets the C++ compiler inline evaluate() directly into host loops.
        bodyCode.AppendLine("inline void eval(){");
        bodyCode.AppendLine("if(fast_eval_ok&&owner_thread==std::this_thread::get_id())[[likely]]{");
        bodyCode.AppendLine("fast_eval_ok=false;fast_evaluating=true;fast_dirty=false;");
        if (canThrow)
            bodyCode.AppendLine($"try{{this->{className}::evaluate();}}catch(...){{fast_evaluating=false;fast_dirty=true;fast_eval_ok=fast_eligible;throw;}}");
        else
            bodyCode.AppendLine($"this->{className}::evaluate();");
        bodyCode.AppendLine("fast_evaluating=false;if(!fast_eligible)[[unlikely]]applyForces();fast_eval_ok=fast_eligible;return;}");
        bodyCode.AppendLine("evalSlow();}");
        foreach (var signal in Signals.Where(s => s.Direction != "internal")) bodyCode.AppendLine($"vsrtl::Port {names[signal.Name]};");
        bodyCode.Append($"{className}():vsrtl::Model(\"{top}\",{{");
        bodyCode.AppendJoin(',', Signals.Select(s => $"{{\"{s.SourcePath ?? s.Name}\",{s.Width},{(s.Direction == "input" ? "vpiInput" : s.Direction == "output" ? "vpiOutput" : "vpiNoDirection")},{(s.Signed ? "true" : "false")},{(s.TwoState ? "true" : "false")}}}"));
        bodyCode.Append($"}},{SignalId(Clock!)})");
        foreach (var signal in Signals.Where(s => s.Direction != "internal")) bodyCode.Append($",{names[signal.Name]}(*this,{SignalId(signal.Name)})");
        bodyCode.AppendLine($"{{{(hasInit ? $"assoc.resize({Memories.Count});" : "")}mem={{");
        bodyCode.AppendJoin(',', Memories.Select(m => $"std::vector<W>({m.Depth},{(m.TwoState ? $"word(0,0,0,{m.Width})" : $"unknown({m.Width})")})"));
        bodyCode.AppendLine("};");
        if (hasInit) EmitInitialization(bodyCode);
        bodyCode.AppendLine($"for(int i=0;i<{Signals.Count};++i)_vs_prev[i]=vsrtl::w64::unknown(1);");
        bodyCode.AppendLine($"_vs_prev[{SignalId(Clock!)}]=vsrtl::w64::lit(0ULL,1);");
        foreach (var signal in Signals.Where(s => s.TwoState && s.Width == 1))
            bodyCode.AppendLine($"_vs_prev[{SignalId(signal.Name)}]=vsrtl::w64::lit(0ULL,1);");
        EmitCppCovergroupRegistration(bodyCode);
        EmitCppPropertyInit(bodyCode, true, false);
        if (enableCoverage) bodyCode.AppendLine("enableToggleCoverage(true);");
        bodyCode.AppendLine("}\n};");

        var code = new StringBuilder("#pragma once\n#include \"external_model.h\"\n" + (threads > 1 ? "#include \"thread_pool.h\"\n" : "") + "using namespace vsrtl;\n");
        EmitDpiPrototypes(code, dpiCalls);
        code.AppendLine($"class {className} final : public vsrtl::Model {{");
        if (threads > 1) code.AppendLine($"vsrtl::ThreadPool _vs_pool{{{threads}}};");
        if (hasRuntimeLoops || hasInit) code.AppendLine("size_t execution_budget=20000;");
        code.AppendLine($"vsrtl::w64::W64 _vs_prev[{Signals.Count}];");
        code.AppendLine($"vsrtl::w64::W64 _vs_next[{Signals.Count}];");
        if (hasFixedMemWrites) code.AppendLine("vsrtl::w64::MemLog64<16> _vs_mlog;");
        if (hasInit) code.AppendLine("std::vector<AssociativeStorage>assoc;");
        if (coverage is { Count: > 0 }) code.AppendLine($"uint64_t _vs_cov[{coverage.Count}]{{}};");
        EmitCppPropertyMembers(code, true, false);
        code.Append(bodyCode);
        return code.ToString();
    }
}
