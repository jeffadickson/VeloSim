using System.Text;
using System.Text.Json;
namespace VeloSim;

/// <summary>Simulation value-state mode for emitted C++ RTL models. FourState is the IEEE default; TwoState is an opt-in high-speed 0/1 mode.</summary>
public enum RtlStateMode
{
    FourState = 0,
    TwoState = 1
}

public sealed partial class RtlDesign
{
    private bool CanUseTwoState64(bool useFastEvent)
    {
        if (Timed) return false;
        if (!CombinationalOnly && !useFastEvent) return false;
        if (Signals.Any(s => s.Width > 64)) return false;
        if (Memories.Any(m => m.Width > 64 || m.Associative || m.Queue || m.Dynamic || m.Dimensions is not null)) return false;
        if (SettledSteps.Any(step => !(Kind(step) == "blocking" && Kind(step.GetProperty("target")) == "ref"))) return false;
        return VerificationFits64() && SettledSteps.Append(Body).SelectMany(Statements).All(StatementFits64);
    }

    private static string EmitConst2(string bits)
    {
        ulong v = 0;
        foreach (char c in bits)
        {
            v <<= 1;
            if (c == '1') v |= 1;
        }
        return $"0x{v:x}ULL";
    }

    private string MemoryIndex2(JsonElement access, int id)
    {
        string idx = Expression2(access.GetProperty("index"));
        return Memories[id].LowerBound == 0 ? idx : $"vsrtl::w2::memidx({idx},{Memories[id].LowerBound})";
    }

    private string Expression2(JsonElement e, int context = 0, bool? forceSigned = null)
    {
        string kind = Kind(e);
        int ew = ExpressionWidth(e);
        bool signed = forceSigned ?? ExpressionSigned(e);
        string Extend(string value) => signed && context > ew ? $"vsrtl::w2::sresize({value},{ew},{context})" : value;
        if (kind == "ref") return Extend($"_vs_s[{SignalId(Name(e))}]");
        if (kind == "const") return Extend(EmitConst2(e.GetProperty("bits").GetString()!));
        if (kind == "index")
        {
            int id = MemoryId(Name(e));
            return Extend($"vsrtl::w2::read_flat(_vs_m{id},{Memories[id].Depth},{MemoryIndex2(e, id)})");
        }
        if (kind == "bitselect")
        {
            var valEl = e.GetProperty("value");
            return $"vsrtl::w2::selbit({Expression2(valEl)},{ExpressionWidth(valEl)},{Expression2(e.GetProperty("index"))})";
        }
        if (kind == "partselect")
        {
            var valEl = e.GetProperty("value");
            return $"vsrtl::w2::selpart({Expression2(valEl)},{ExpressionWidth(valEl)},{e.GetProperty("left").GetInt32()},{e.GetProperty("right").GetInt32()})";
        }
        if (kind == "indexedselect")
        {
            var valEl = e.GetProperty("value");
            return $"vsrtl::w2::selidx({Expression2(valEl)},{ExpressionWidth(valEl)},{Expression2(e.GetProperty("base"))},{e.GetProperty("width").GetInt32()},{(e.GetProperty("descending").GetBoolean() ? "true" : "false")})";
        }
        if (kind == "test_plusargs") return Extend($"uint64_t(testPlusargs({CppStringLiteral(e.GetProperty("prefix").GetString()!)}))");
        if (kind == "urandom") return Extend("uint64_t(nextUrandom())");
        if (kind == "random") return Extend("uint64_t(uint32_t(nextRandom()))");
        if (kind == "urandom_range")
        {
            string maxExpr = $"uint32_t({Expression2(e.GetProperty("max"))})";
            string minExpr = e.TryGetProperty("min", out var minEl) ? $"uint32_t({Expression2(minEl)})" : "0u";
            return Extend($"uint64_t(nextUrandomRange({maxExpr},{minExpr}))");
        }
        if (kind == "resolve")
        {
            int nk = ParseNetKind(e.GetProperty("net").GetString()!);
            int rw = Math.Max(context, ew);
            var drvEls = e.GetProperty("args").EnumerateArray().ToArray();
            if (drvEls.Length == 2)
                return Extend($"vsrtl::w2::resolve_pair({Expression2(drvEls[0], rw)},{Expression2(drvEls[1], rw)},{nk},{rw})");
            string drivers = string.Join(",", drvEls.Select(d => Expression2(d, rw)));
            return Extend($"vsrtl::w2::resolve_net({nk},{{{drivers}}},{rw})");
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
                        ? $"vsrtl::w2::scmp({Expression2(valEl, cw, true)},{Expression2(loEl, cw, true)},{cw},3)"
                        : $"vsrtl::w2::cmp({Expression2(valEl, cw, false)},{Expression2(loEl, cw, false)},3)";
                    string hiC = cs
                        ? $"vsrtl::w2::scmp({Expression2(valEl, cw, true)},{Expression2(hiEl, cw, true)},{cw},1)"
                        : $"vsrtl::w2::cmp({Expression2(valEl, cw, false)},{Expression2(hiEl, cw, false)},1)";
                    parts.Add($"vsrtl::w2::land({loC},{hiC})");
                }
                else
                {
                    int cw = Math.Max(vw, ExpressionWidth(it));
                    bool cs = vSigned && ExpressionSigned(it);
                    parts.Add($"vsrtl::w2::eq({Expression2(valEl, cw, cs)},{Expression2(it, cw, cs)})");
                }
            }
            string acc = parts[0];
            for (int i = 1; i < parts.Count; ++i) acc = $"vsrtl::w2::lor({acc},{parts[i]})";
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
            return $"uint64_t(vsrtl::w2::edge(_vs_past_{sid}[0]&1ULL,_vs_s[{sid}]&1ULL,true))";
        }
        if (kind == "fell")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            return $"uint64_t(vsrtl::w2::edge(_vs_past_{sid}[0]&1ULL,_vs_s[{sid}]&1ULL,false))";
        }
        if (kind == "stable")
        {
            int sid = SignalId(e.GetProperty("signal").GetString()!);
            return $"uint64_t(_vs_s[{sid}]==_vs_past_{sid}[0])";
        }
        if (kind == "dpi_call")
        {
            string callArgs = string.Join(",", e.GetProperty("args").EnumerateArray().Select(a => Expression2(a)));
            return Extend($"vsrtl::w2::resize(::{Name(e)}({callArgs}),{e.GetProperty("width").GetInt32()})");
        }
        var args = e.GetProperty("args").EnumerateArray().ToArray();
        int width = Math.Max(context, ew);
        return kind switch
        {
            "castsigned" or "castunsigned" => Extend(Expression2(args[0])),
            "countones" => Extend($"vsrtl::w2::countones({Expression2(args[0])},{ExpressionWidth(args[0])})"),
            "onehot" => $"vsrtl::w2::onehot({Expression2(args[0])},{ExpressionWidth(args[0])})",
            "onehot0" => $"vsrtl::w2::onehot0({Expression2(args[0])},{ExpressionWidth(args[0])})",
            "isunknown" => "0ULL",
            "clog2" => Extend($"vsrtl::w2::clog2({Expression2(args[0])},{ExpressionWidth(args[0])})"),
            "add" or "sub" => $"vsrtl::w2::arith({Expression2(args[0], width, signed)},{Expression2(args[1], width, signed)},{width},{(kind == "sub" ? "true" : "false")})",
            "bitnot" => $"vsrtl::w2::bnot({Expression2(args[0], width, signed)},{width})",
            "neg" => $"vsrtl::w2::arith(0ULL,{Expression2(args[0], width, signed)},{width},true)",
            "pos" => $"vsrtl::w2::resize({Expression2(args[0], width, signed)},{width})",
            "mul" or "div" or "mod" => $"vsrtl::w2::{(signed ? "sproduct" : "product")}({Expression2(args[0], width, signed)},{Expression2(args[1], width, signed)},{width},{(kind == "mul" ? 0 : kind == "div" ? 1 : 2)})",
            "cond" => $"vsrtl::w2::cond({Expression2(args[0])},{Expression2(args[1], width, signed)},{Expression2(args[2], width, signed)},{width})",
            "ashr" when signed => $"vsrtl::w2::sshift({Expression2(args[0], width, true)},{Expression2(args[1])},{width})",
            "shl" or "shr" or "ashr" => $"vsrtl::w2::shift({Expression2(args[0], width, signed)},{Expression2(args[1])},{width},{(kind == "shl" ? "false" : "true")})",
            "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor" => $"vsrtl::w2::red({Expression2(args[0])},{ExpressionWidth(args[0])},{(kind == "redand" ? 0 : kind == "rednand" ? 1 : kind == "redor" ? 2 : kind == "rednor" ? 3 : kind == "redxor" ? 4 : 5)})",
            "bitxnor" => $"vsrtl::w2::bnot(vsrtl::w2::bbin({Expression2(args[0], width, signed)},{Expression2(args[1], width, signed)},{width},2),{width})",
            "bitand" or "bitor" or "bitxor" => $"vsrtl::w2::bbin({Expression2(args[0], width, signed)},{Expression2(args[1], width, signed)},{width},{(kind == "bitand" ? 0 : kind == "bitor" ? 1 : 2)})",
            "not" => $"vsrtl::w2::lnot({Expression2(args[0])})",
            "and" => $"vsrtl::w2::land({Expression2(args[0])},{Expression2(args[1])})",
            "or" => $"vsrtl::w2::lor({Expression2(args[0])},{Expression2(args[1])})",
            "eq" or "neq" or "caseeq" or "caseneq" or "wildeq" or "wildneq" =>
                (kind is "neq" or "caseneq" or "wildneq" ? "vsrtl::w2::lnot(" : "")
                + $"vsrtl::w2::eq({Expression2(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{Expression2(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))})"
                + (kind is "neq" or "caseneq" or "wildneq" ? ")" : ""),
            "lt" or "le" or "gt" or "ge" =>
                args.All(ExpressionSigned)
                    ? $"vsrtl::w2::scmp({Expression2(args[0], args.Max(ExpressionWidth), true)},{Expression2(args[1], args.Max(ExpressionWidth), true)},{args.Max(ExpressionWidth)},{(kind == "lt" ? 0 : kind == "le" ? 1 : kind == "gt" ? 2 : 3)})"
                    : $"vsrtl::w2::cmp({Expression2(args[0], args.Max(ExpressionWidth), false)},{Expression2(args[1], args.Max(ExpressionWidth), false)},{(kind == "lt" ? 0 : kind == "le" ? 1 : kind == "gt" ? 2 : 3)})",
            "concat" => $"vsrtl::w2::concat({Expression2(args[0])},{Expression2(args[1])},{ExpressionWidth(args[1])})",
            _ => throw new IrValidationException("Unsupported two-state expression.")
        };
    }

    private static string StoreWord2(string value, RtlSignal signal, int exprWidth = -1) =>
        exprWidth == signal.Width ? value : $"vsrtl::w2::resize({value},{signal.Width})";

    private void WordWrite2(StringBuilder code, JsonElement target, string value, int exprWidth = -1)
    {
        if (Kind(target) == "ref")
        {
            int id = SignalId(Name(target));
            code.AppendLine($"_vs_next[{id}]={StoreWord2(value, Signals[id], exprWidth)};");
            return;
        }
        int selectedId = SignalId(Name(target.GetProperty("value"))), width = ExpressionWidth(target);
        string basis = Kind(target) == "bitselect"
            ? Expression2(target.GetProperty("index"))
            : Kind(target) == "indexedselect"
                ? Expression2(target.GetProperty("base"))
                : $"{target.GetProperty("right").GetInt32()}ULL";
        bool descending = Kind(target) == "indexedselect" && target.GetProperty("descending").GetBoolean();
        code.AppendLine($"_vs_next[{selectedId}]=vsrtl::w2::depidx(_vs_next[{selectedId}],{Signals[selectedId].Width},{basis},{width},{(descending ? "true" : "false")},{value});");
    }

    private void Statement2(StringBuilder code, JsonElement e, ref int unique, bool trackBudget, List<(string Id, string Kind)>? coverage = null)
    {
        if (trackBudget) code.AppendLine("execution_step(execution_budget);");
        switch (Kind(e))
        {
            case "while":
            case "do":
                if (Kind(e) == "do") code.AppendLine("do{");
                else code.AppendLine($"while(vsrtl::w2::yes({Expression2(e.GetProperty("condition"))})){{");
                code.AppendLine("execution_step(execution_budget);");
                Statement2(code, e.GetProperty("body"), ref unique, trackBudget, coverage);
                code.AppendLine(Kind(e) == "do" ? $"}}while(vsrtl::w2::yes({Expression2(e.GetProperty("condition"))}));" : "}");
                break;
            case "runtime_repeat":
                string runtimeLoop = "runtime_repeat" + unique++;
                var cntEl = e.GetProperty("count");
                code.AppendLine($"for(unsigned {runtimeLoop}=repeat_count(vsrtl::w2::from({Expression2(cntEl)},{ExpressionWidth(cntEl)}),{(ExpressionSigned(cntEl) ? "true" : "false")});{runtimeLoop}>0;--{runtimeLoop}){{");
                code.AppendLine("execution_step(execution_budget);");
                Statement2(code, e.GetProperty("body"), ref unique, trackBudget, coverage);
                code.AppendLine("}");
                break;
            case "repeat":
                string loop = "repeat" + unique++;
                code.AppendLine($"for(int {loop}=0;{loop}<{e.GetProperty("count").GetInt32()};++{loop}){{");
                Statement2(code, e.GetProperty("body"), ref unique, trackBudget, coverage);
                code.AppendLine("}");
                break;
            case "block":
                foreach (var item in e.GetProperty("items").EnumerateArray()) Statement2(code, item, ref unique, trackBudget, coverage);
                break;
            case "nba":
                var target = e.GetProperty("target");
                if (Kind(target) == "concat")
                {
                    int total = ExpressionWidth(target), remaining = total;
                    string rhs = "nba" + unique++;
                    code.AppendLine($"{{uint64_t {rhs}=vsrtl::w2::resize({Expression2(e.GetProperty("value"), total)},{total});");
                    foreach (var leaf in TargetLeaves(target))
                    {
                        int width = ExpressionWidth(leaf);
                        remaining -= width;
                        WordWrite2(code, leaf, $"vsrtl::w2::selpart({rhs},{total},{remaining + width - 1},{remaining})", width);
                    }
                    code.AppendLine("}");
                }
                else if (Kind(target) is "ref" or "bitselect" or "partselect" or "indexedselect")
                {
                    int tw = ExpressionWidth(target);
                    var valEl = e.GetProperty("value");
                    int vw = ExpressionWidth(valEl);
                    bool exactWidth = vw == tw && !(ExpressionSigned(valEl) && tw > vw);
                    WordWrite2(code, target, Expression2(valEl, tw), exactWidth ? tw : -1);
                }
                else
                {
                    int id = MemoryId(Name(target));
                    var value = Expression2(e.GetProperty("value"), Memories[id].Width);
                    code.AppendLine($"_vs_mlog.push({id},{Memories[id].Depth},{MemoryIndex2(target, id)},{value},{Memories[id].Width});");
                }
                break;
            case "if":
            {
                int ifId = unique++;
                int thenCov = RegisterCoveragePoint(coverage, $"if.{ifId}.then", "branch");
                int elseCov = RegisterCoveragePoint(coverage, $"if.{ifId}.else", "branch");
                code.AppendLine($"if(vsrtl::w2::yes({Expression2(e.GetProperty("condition"))})){{");
                if (thenCov >= 0) code.AppendLine($"++_vs_cov[{thenCov}];");
                Statement2(code, e.GetProperty("then"), ref unique, trackBudget, coverage);
                code.AppendLine("}else{");
                if (elseCov >= 0) code.AppendLine($"++_vs_cov[{elseCov}];");
                Statement2(code, e.GetProperty("else"), ref unique, trackBudget, coverage);
                code.AppendLine("}");
                break;
            }
            case "assert":
                code.AppendLine($"vsrtl::w2::check_assert({Expression2(e.GetProperty("condition"))},{CppStringLiteral(e.TryGetProperty("message", out var am) ? am.GetString()! : "")});");
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
                        ? string.Join(",", fa.EnumerateArray().Select(a => $"vsrtl::w2::from({Expression2(a)},{ExpressionWidth(a)})"))
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
                code.AppendLine(EmitCppSampleCovergroupStmt(e, ex => Expression2(ex), false, true));
                break;
            case "display":
            {
                string fmt = CppStringLiteral(e.GetProperty("format").GetString()!);
                bool nl = !e.TryGetProperty("newline", out var nlProp) || nlProp.GetBoolean();
                string argList = e.TryGetProperty("args", out var dArgs)
                    ? string.Join(",", dArgs.EnumerateArray().Select(a => $"vsrtl::w2::from({Expression2(a)},{ExpressionWidth(a)})"))
                    : "";
                code.AppendLine($"emitDisplay(vsrtl::format_display({fmt},{{{argList}}},{(nl ? "true" : "false")}));");
                break;
            }
            case "value_plusargs":
            {
                string fmt = CppStringLiteral(e.GetProperty("format").GetString()!);
                int tid = SignalId(e.GetProperty("target").GetString()!);
                code.AppendLine($"{{W _vs_vp=vsrtl::w2::from(_vs_next[{tid}],{Signals[tid].Width});uint32_t _vs_vpm=valuePlusargs({fmt},_vs_vp);if(_vs_vpm)_vs_next[{tid}]={StoreWord2("vsrtl::w2::to(_vs_vp)", Signals[tid])};");
                if (e.TryGetProperty("matched", out var vpMatched) && vpMatched.ValueKind != JsonValueKind.Null)
                {
                    int mid = SignalId(vpMatched.GetString()!);
                    code.AppendLine($"_vs_next[{mid}]={StoreWord2("uint64_t(_vs_vpm)", Signals[mid])};");
                }
                code.AppendLine("}");
                break;
            }
            case "force":
            {
                int tid = SignalId(e.GetProperty("target").GetString()!);
                code.AppendLine($"forceSignal({tid},vsrtl::w2::from({StoreWord2(Expression2(e.GetProperty("value")), Signals[tid])},{Signals[tid].Width}));");
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
                var allMatches = items.SelectMany(CaseItemMatches).ToArray();
                int caseWidth = Math.Max(ExpressionWidth(e.GetProperty("value")), allMatches.Select(ExpressionWidth).DefaultIfEmpty(1).Max());
                bool caseSigned = ExpressionSigned(e.GetProperty("value")) && allMatches.All(ExpressionSigned);
                code.AppendLine($"{{uint64_t {label}={Expression2(e.GetProperty("value"), caseWidth, caseSigned)};");
                bool first = true;
                int armIdx = 0;
                foreach (var item in items.Where(i => !IsCaseDefault(i)))
                {
                    var ms = CaseItemMatches(item);
                    string cond = string.Join("||", ms.Select(m => $"{label}=={Expression2(m, caseWidth, caseSigned)}"));
                    int armCov = RegisterCoveragePoint(coverage, $"case.{caseId}.arm.{armIdx++}", "case_arm");
                    code.AppendLine($"{(first ? "if" : "else if")}({cond}){{");
                    if (armCov >= 0) code.AppendLine($"++_vs_cov[{armCov}];");
                    Statement2(code, item.GetProperty("body"), ref unique, trackBudget, coverage);
                    code.AppendLine("}");
                    first = false;
                }
                var defaultItem = items.FirstOrDefault(IsCaseDefault);
                if (defaultItem.ValueKind != JsonValueKind.Undefined)
                {
                    int defCov = RegisterCoveragePoint(coverage, $"case.{caseId}.default", "case_default");
                    code.AppendLine(first ? "{" : "else{");
                    if (defCov >= 0) code.AppendLine($"++_vs_cov[{defCov}];");
                    Statement2(code, defaultItem.GetProperty("body"), ref unique, trackBudget, coverage);
                    code.AppendLine("}");
                }
                code.AppendLine("}");
                break;
            }
        }
    }

    private string EmitTwoState64ModelHeader(string top, IReadOnlyDictionary<string, string> names, int[] seqIds, bool hasFixedMemWrites, bool hasRuntimeLoops, bool canThrow, IReadOnlyList<(string Name, int ArgCount)> dpiCalls, bool enableCoverage, int threads = 1)
    {
        string className = "V" + top;
        bool hasInit = Statements(Initialization).Any(st => Kind(st) is not "block");
        var procs = CombinationalOnly
            ? Array.Empty<JsonElement>()
            : (Kind(Body) == "parallel" ? Body.GetProperty("items").EnumerateArray().ToArray() : [Body]);
        var eventIds = CombinationalOnly ? Array.Empty<int>() : EventSignals.Select(SignalId).ToArray();
        var coverage = enableCoverage ? new List<(string Id, string Kind)>() : null;

        void EmitStep2(StringBuilder sb, JsonElement step)
        {
            int id = SignalId(Name(step.GetProperty("target")));
            var rhs = step.GetProperty("value");
            int vw = ExpressionWidth(rhs);
            bool exactWidth = vw == Signals[id].Width && !(ExpressionSigned(rhs) && Signals[id].Width > vw);
            sb.AppendLine($"_vs_s[{id}]={StoreWord2(Expression2(rhs, Signals[id].Width), Signals[id], exactWidth ? Signals[id].Width : -1)};");
        }

        bool canReenter = dpiCalls.Count > 0 || Statements(Body).Any(st => Kind(st) is "display" or "finish" or "fatal" or "force" or "release");
        bool combPureInternal = !CombinationalOnly && !canReenter && !canThrow && !hasRuntimeLoops
            && Properties.Count == 0 && PastDepths.Count == 0
            && SettledSteps.Count > 0
            && SettledSteps.All(s => Kind(s) == "blocking" && CsExprIsPureInternal(s.GetProperty("value")));

        var bodyCode = new StringBuilder();
        bodyCode.AppendLine("inline void settle(){");
        if (threads <= 1)
        {
            foreach (var step in SettledSteps) EmitStep2(bodyCode, step);
        }
        else
        {
            foreach (var level in SettledLevels)
            {
                if (level.Count < 32) { foreach (var step in level) EmitStep2(bodyCode, step); continue; }
                bodyCode.AppendLine($"_vs_pool.parallel_for({level.Count},[&](size_t _lvl_i){{switch(_lvl_i){{");
                for (int idx = 0; idx < level.Count; ++idx)
                {
                    bodyCode.Append($"case {idx}:{{");
                    EmitStep2(bodyCode, level[idx]);
                    bodyCode.AppendLine("}break;");
                }
                bodyCode.AppendLine("}});");
            }
        }
        bodyCode.AppendLine("}\nvoid syncToBase() const override {");
        bodyCode.AppendLine($"for(size_t i=0;i<{Signals.Count};++i)vsrtl::w2::store(s[i],_vs_s[i]);");
        bodyCode.AppendLine("}\nvoid syncFromBase() override {");
        bodyCode.AppendLine($"for(size_t i=0;i<{Signals.Count};++i)_vs_s[i]=vsrtl::w2::to(s[i]);");
        if (combPureInternal) bodyCode.AppendLine("_vs_need_settle=true;");
        bodyCode.AppendLine(combPureInternal ? "}\ninline void evaluateCore(bool _vs_force_settle){" : "}\nvoid evaluate() override {");
        if (dpiCalls.Count > 0) bodyCode.AppendLine("struct _VsDpiScope{svScope prev;explicit _VsDpiScope(svScope s):prev(svSetScope(s)){}~_VsDpiScope(){svSetScope(prev);}} _vs_dpi_scope(static_cast<svScope>(this));");
        if (hasRuntimeLoops) bodyCode.AppendLine("execution_budget=20000;");
        if (canThrow)
        {
            bodyCode.Append($"uint64_t saved_s[{Signals.Count}];std::memcpy(saved_s,_vs_s,sizeof(_vs_s));");
            if (!CombinationalOnly) bodyCode.Append($"uint64_t saved_prev[{Signals.Count}];std::memcpy(saved_prev,_vs_prev,sizeof(_vs_prev));");
            for (int i = 0; i < Memories.Count; ++i)
                bodyCode.Append($"uint64_t saved_m{i}[{Memories[i].Depth}];std::memcpy(saved_m{i},_vs_m{i},sizeof(_vs_m{i}));");
            bodyCode.AppendLine("try{");
        }
        if (combPureInternal)
            bodyCode.AppendLine("if(_vs_force_settle||_vs_need_settle)[[unlikely]]{settle();_vs_need_settle=false;}");
        else
            bodyCode.AppendLine("settle();");
        EmitCppPropertyEvaluation(bodyCode, ex => Expression2(ex), ex => $"vsrtl::w2::yes({ex})", sid => $"_vs_s[{sid}]", (cid, edge) => $"vsrtl::w2::edge(_vs_pclk_{cid},_vs_s[{cid}],{(edge == "posedge" ? "true" : "false")})", false, true);
        if (!CombinationalOnly)
        {
            for (int p = 0; p < procs.Length; ++p)
            {
                string cond = string.Join("||", procs[p].GetProperty("events").EnumerateArray().Select(trigger =>
                {
                    int id = SignalId(trigger.GetProperty("signal").GetString()!);
                    return $"vsrtl::w2::edge(_vs_prev[{id}],_vs_s[{id}],{(trigger.GetProperty("edge").GetString() == "posedge" ? "true" : "false")})";
                }));
                bodyCode.AppendLine($"const bool _vs_ev{p}={cond};");
            }
            string anyEv = string.Join("||", Enumerable.Range(0, procs.Length).Select(p => $"_vs_ev{p}"));
            bodyCode.AppendLine($"if({anyEv}){{");
            if (seqIds.Length > 0)
                bodyCode.AppendLine(string.Concat(seqIds.Select(id => $"_vs_next[{id}]=_vs_s[{id}];")));
            int unique = 0;
            for (int p = 0; p < procs.Length; ++p)
            {
                if (procs.Length > 1) bodyCode.AppendLine($"if(_vs_ev{p}){{");
                Statement2(bodyCode, procs[p].GetProperty("body"), ref unique, hasRuntimeLoops, coverage);
                if (procs.Length > 1) bodyCode.AppendLine("}");
            }
            if (seqIds.Length > 0)
                bodyCode.AppendLine(string.Concat(seqIds.Select(id => $"_vs_s[{id}]=_vs_next[{id}];")));
            if (hasFixedMemWrites)
                bodyCode.AppendLine("_vs_mlog.commit(_vs_mptrs);");
            bodyCode.AppendLine("settle();\n}");
            foreach (int id in eventIds)
                bodyCode.AppendLine($"_vs_prev[{id}]=_vs_s[{id}];");
        }
        if (canThrow)
        {
            bodyCode.Append("\n}catch(...){std::memcpy(_vs_s,saved_s,sizeof(_vs_s));");
            if (!CombinationalOnly) bodyCode.Append("std::memcpy(_vs_prev,saved_prev,sizeof(_vs_prev));");
            for (int i = 0; i < Memories.Count; ++i)
                bodyCode.Append($"std::memcpy(_vs_m{i},saved_m{i},sizeof(_vs_m{i}));");
            if (hasFixedMemWrites) bodyCode.Append("_vs_mlog.count=0;_vs_mlog.overflow.clear();");
            bodyCode.AppendLine("throw;}");
        }
        if (combPureInternal)
            bodyCode.AppendLine("}\nvoid evaluate() override { evaluateCore(true); }");
        else
            bodyCode.AppendLine("}");
        bodyCode.AppendLine("void saveExtraState(std::vector<uint8_t>& out) const override {");
        bodyCode.AppendLine("out.clear();");
        if (!CombinationalOnly)
            bodyCode.AppendLine($"{{const auto* p=reinterpret_cast<const uint8_t*>(_vs_prev);out.insert(out.end(),p,p+sizeof(_vs_prev));}}");
        for (int i = 0; i < Memories.Count; ++i)
            bodyCode.AppendLine($"{{const auto* p=reinterpret_cast<const uint8_t*>(_vs_m{i});out.insert(out.end(),p,p+sizeof(_vs_m{i}));}}");
        bodyCode.AppendLine("}\nvoid restoreExtraState(const uint8_t* data,size_t len) override {");
        bodyCode.AppendLine("size_t off=0;");
        if (!CombinationalOnly)
            bodyCode.AppendLine("if(off+sizeof(_vs_prev)>len)throw std::runtime_error(\"checkpoint W2 state size mismatch\");std::memcpy(_vs_prev,data+off,sizeof(_vs_prev));off+=sizeof(_vs_prev);");
        for (int i = 0; i < Memories.Count; ++i)
            bodyCode.AppendLine($"if(off+sizeof(_vs_m{i})>len)throw std::runtime_error(\"checkpoint W2 memory size mismatch\");std::memcpy(_vs_m{i},data+off,sizeof(_vs_m{i}));off+=sizeof(_vs_m{i});");
        bodyCode.AppendLine("if(off!=len)throw std::runtime_error(\"checkpoint W2 extra trailing bytes\");");
        if (combPureInternal) bodyCode.AppendLine("_vs_need_settle=true;");
        bodyCode.AppendLine("}");
        if (Memories.Count > 0)
        {
            bodyCode.AppendLine("bool loadDerivedMemory(size_t mem_id,const std::string& path,bool hex,size_t start_addr,size_t end_addr,size_t& loaded) override {");
            bodyCode.AppendLine("switch(mem_id){");
            for (int i = 0; i < Memories.Count; ++i)
                bodyCode.AppendLine($"case {i}: loaded=vsrtl::load_mem_file_flat(_vs_m{i},{Memories[i].Depth},{Memories[i].Width},path,hex,start_addr,end_addr);{(combPureInternal ? "_vs_need_settle=true;" : "")}return true;");
            bodyCode.AppendLine("default: return false;}}");
            bodyCode.AppendLine($"size_t memoryCount() const override {{ return {Memories.Count}; }}");
            bodyCode.AppendLine("size_t memoryDepth(size_t mem_id) const override { switch(mem_id){");
            for (int i = 0; i < Memories.Count; ++i) bodyCode.AppendLine($"case {i}: return {Memories[i].Depth};");
            bodyCode.AppendLine("default: return 0; }}");
            bodyCode.AppendLine("int memoryWidth(size_t mem_id) const override { switch(mem_id){");
            for (int i = 0; i < Memories.Count; ++i) bodyCode.AppendLine($"case {i}: return {Memories[i].Width};");
            bodyCode.AppendLine("default: return 0; }}");
            bodyCode.AppendLine("W readMemoryWord(size_t mem_id,size_t addr) const override { checkThread(); switch(mem_id){");
            for (int i = 0; i < Memories.Count; ++i) bodyCode.AppendLine($"case {i}: if(addr>={Memories[i].Depth})throw std::out_of_range(\"memory word address out of range\");return vsrtl::w2::from(_vs_m{i}[addr],{Memories[i].Width});");
            bodyCode.AppendLine("default: throw std::out_of_range(\"memory word address out of range\"); }}");
            bodyCode.AppendLine("void writeMemoryWord(size_t mem_id,size_t addr,W value) override { checkThread(); switch(mem_id){");
            for (int i = 0; i < Memories.Count; ++i) bodyCode.AppendLine($"case {i}: if(addr>={Memories[i].Depth})throw std::out_of_range(\"memory word address out of range\");_vs_m{i}[addr]=vsrtl::w2::resize(value.v.limb[0],{Memories[i].Width});{(combPureInternal ? "_vs_need_settle=true;" : "")}markDirty();return;");
            bodyCode.AppendLine("default: throw std::out_of_range(\"memory word address out of range\"); }}");
        }
        bodyCode.AppendLine("public:");
        EmitCoveragePointsOverride(bodyCode, coverage);
        // Non-virtual inline fast-path lets GCC/Clang inline evaluate() directly into host loops.
        bodyCode.AppendLine("inline void eval(){");
        bodyCode.AppendLine("if(fast_eval_ok&&isOwnerThreadFast())[[likely]]{");
        if (!canReenter)
        {
            bodyCode.AppendLine("fast_dirty=false;");
            if (canThrow)
                bodyCode.AppendLine($"try{{this->{className}::evaluate();}}catch(...){{fast_dirty=true;throw;}}");
            else if (combPureInternal)
                bodyCode.AppendLine($"this->{className}::evaluateCore(false);");
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
        // All signals carry two_state=true so Model::Model(), Model::put(), Port, and VPI agree on 2-state semantics.
        bodyCode.AppendJoin(',', Signals.Select(s => $"{{\"{s.SourcePath ?? s.Name}\",{s.Width},{(s.Direction == "input" ? "vpiInput" : s.Direction == "output" ? "vpiOutput" : "vpiNoDirection")},{(s.Signed ? "true" : "false")},true}}"));
        bodyCode.Append($"}},{(CombinationalOnly ? -1 : SignalId(Clock!))})");
        foreach (var signal in Signals.Where(s => s.Direction != "internal")) bodyCode.Append($",{names[signal.Name]}(*this,{SignalId(signal.Name)},&_vs_s[{SignalId(signal.Name)}])");
        bodyCode.AppendLine("{");
        bodyCode.AppendLine("initFastThreadTag();");
        if (hasFixedMemWrites)
        {
            for (int i = 0; i < Memories.Count; ++i)
                bodyCode.AppendLine($"_vs_mptrs[{i}]=_vs_m{i};");
        }
        if (hasInit)
        {
            bodyCode.AppendLine($"assoc.resize({Memories.Count});mem={{");
            bodyCode.AppendJoin(',', Memories.Select(m => $"std::vector<W>({m.Depth},word(0,0,0,{m.Width}))"));
            bodyCode.AppendLine("};");
            EmitInitialization(bodyCode);
            bodyCode.AppendLine($"for(size_t i=0;i<s.size();++i)s[i]=two_state(s[i]);");
            for (int i = 0; i < Memories.Count; ++i)
                bodyCode.AppendLine($"for(int j=0;j<{Memories[i].Depth};++j)_vs_m{i}[j]=mem[{i}][j].v.limb[0];");
            bodyCode.AppendLine("mem.clear();");
        }
        bodyCode.AppendLine("syncFromBase();");
        EmitCppCovergroupRegistration(bodyCode);
        EmitCppPropertyInit(bodyCode, false, true);
        if (enableCoverage) bodyCode.AppendLine("enableToggleCoverage(true);");
        bodyCode.AppendLine("}");
        bodyCode.AppendLine($"std::unique_ptr<{className}> fork() const {{ auto c = std::make_unique<{className}>(); c->vsrtl::Model::restore(this->vsrtl::Model::snapshot()); return c; }}");
        bodyCode.AppendLine("};");

        var code = new StringBuilder("#pragma once\n#include \"external_model.h\"\n" + (threads > 1 ? "#include \"thread_pool.h\"\n" : "") + "using namespace vsrtl;\n");
        EmitDpiPrototypes(code, dpiCalls);
        code.AppendLine($"class {className} final : public vsrtl::Model {{");
        if (threads > 1) code.AppendLine($"vsrtl::ThreadPool _vs_pool{{{threads}}};");
        if (hasRuntimeLoops || hasInit) code.AppendLine("size_t execution_budget=20000;");
        if (combPureInternal) code.AppendLine("bool _vs_need_settle=true;");
        code.AppendLine($"uint64_t _vs_s[{Signals.Count}]{{}};");
        if (!CombinationalOnly)
        {
            code.AppendLine($"uint64_t _vs_prev[{Signals.Count}]{{}};");
            code.AppendLine($"uint64_t _vs_next[{Signals.Count}]{{}};");
        }
        for (int i = 0; i < Memories.Count; ++i)
            code.AppendLine($"uint64_t _vs_m{i}[{Memories[i].Depth}]{{}};");
        if (hasFixedMemWrites)
        {
            code.AppendLine($"uint64_t* _vs_mptrs[{Memories.Count}];");
            code.AppendLine("vsrtl::w2::MemLog2<16> _vs_mlog;");
        }
        if (hasInit) code.AppendLine("std::vector<AssociativeStorage>assoc;");
        if (coverage is { Count: > 0 }) code.AppendLine($"uint64_t _vs_cov[{coverage.Count}]{{}};");
        EmitCppPropertyMembers(code, false, true);
        code.Append(bodyCode);
        return code.ToString();
    }
}
