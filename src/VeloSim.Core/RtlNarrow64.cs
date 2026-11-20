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
        if (e.TryGetProperty("args", out var args))
        {
            foreach (var a in args.EnumerateArray())
                if (!ExpressionFits64(a)) return false;
        }
        return true;
    }

    private bool StatementFits64(JsonElement e)
    {
        if (e.TryGetProperty("target", out var t) && !ExpressionFits64(t)) return false;
        if (e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object && !ExpressionFits64(v)) return false;
        if (e.TryGetProperty("condition", out var c) && c.ValueKind == JsonValueKind.Object && !ExpressionFits64(c)) return false;
        if (e.TryGetProperty("count", out var cnt) && cnt.ValueKind == JsonValueKind.Object && !ExpressionFits64(cnt)) return false;
        if (Kind(e) == "case")
        {
            foreach (var item in e.GetProperty("items").EnumerateArray())
            {
                var match = item.GetProperty("match");
                if (match.ValueKind != JsonValueKind.Null && !ExpressionFits64(match)) return false;
            }
        }
        return true;
    }

    private bool CanUseNarrow64(bool useFastEvent)
    {
        if (!useFastEvent || Timed || CombinationalOnly) return false;
        if (Signals.Any(s => s.Width > 64)) return false;
        if (Memories.Any(m => m.Width > 64 || m.Associative || m.Queue || m.Dynamic || m.Dimensions is not null)) return false;
        if (SettledSteps.Any(step => !(Kind(step) == "blocking" && Kind(step.GetProperty("target")) == "ref"))) return false;
        return SettledSteps.Append(Body).SelectMany(Statements).All(StatementFits64);
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
        var args = e.GetProperty("args").EnumerateArray().ToArray();
        int width = Math.Max(context, ExpressionWidth(e));
        return kind switch
        {
            "castsigned" or "castunsigned" => Extend(Expression64(args[0])),
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

    private void Statement64(StringBuilder code, JsonElement e, ref int unique, bool trackBudget)
    {
        if (trackBudget) code.AppendLine("execution_step(execution_budget);");
        switch (Kind(e))
        {
            case "while":
            case "do":
                if (Kind(e) == "do") code.AppendLine("do{");
                else code.AppendLine($"while(vsrtl::w64::yes({Expression64(e.GetProperty("condition"))})){{");
                code.AppendLine("execution_step(execution_budget);");
                Statement64(code, e.GetProperty("body"), ref unique, trackBudget);
                code.AppendLine(Kind(e) == "do" ? $"}}while(vsrtl::w64::yes({Expression64(e.GetProperty("condition"))}));" : "}");
                break;
            case "runtime_repeat":
                string runtimeLoop = "runtime_repeat" + unique++;
                code.AppendLine($"for(unsigned {runtimeLoop}=repeat_count(vsrtl::w64::from({Expression64(e.GetProperty("count"))}),{(ExpressionSigned(e.GetProperty("count")) ? "true" : "false")});{runtimeLoop}>0;--{runtimeLoop}){{");
                code.AppendLine("execution_step(execution_budget);");
                Statement64(code, e.GetProperty("body"), ref unique, trackBudget);
                code.AppendLine("}");
                break;
            case "repeat":
                string loop = "repeat" + unique++;
                code.AppendLine($"for(int {loop}=0;{loop}<{e.GetProperty("count").GetInt32()};++{loop}){{");
                Statement64(code, e.GetProperty("body"), ref unique, trackBudget);
                code.AppendLine("}");
                break;
            case "block":
                foreach (var item in e.GetProperty("items").EnumerateArray()) Statement64(code, item, ref unique, trackBudget);
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
                code.AppendLine($"if(vsrtl::w64::yes({Expression64(e.GetProperty("condition"))})){{");
                Statement64(code, e.GetProperty("then"), ref unique, trackBudget);
                code.AppendLine("}else{");
                Statement64(code, e.GetProperty("else"), ref unique, trackBudget);
                code.AppendLine("}");
                break;
            case "case":
                var label = "case" + unique++;
                var items = e.GetProperty("items").EnumerateArray().ToArray();
                int caseWidth = Math.Max(ExpressionWidth(e.GetProperty("value")), items.Where(i => i.GetProperty("match").ValueKind != JsonValueKind.Null).Select(i => ExpressionWidth(i.GetProperty("match"))).DefaultIfEmpty(1).Max());
                bool caseSigned = ExpressionSigned(e.GetProperty("value")) && items.Where(i => i.GetProperty("match").ValueKind != JsonValueKind.Null).All(i => ExpressionSigned(i.GetProperty("match")));
                code.AppendLine($"{{auto {label}={Expression64(e.GetProperty("value"), caseWidth, caseSigned)};");
                bool first = true;
                foreach (var item in items.Where(i => i.GetProperty("match").ValueKind != JsonValueKind.Null))
                {
                    code.AppendLine($"{(first ? "if" : "else if")}(vsrtl::w64::exact({label},{Expression64(item.GetProperty("match"), caseWidth, caseSigned)})){{");
                    Statement64(code, item.GetProperty("body"), ref unique, trackBudget);
                    code.AppendLine("}");
                    first = false;
                }
                var defaultItem = items.FirstOrDefault(i => i.GetProperty("match").ValueKind == JsonValueKind.Null);
                if (defaultItem.ValueKind != JsonValueKind.Undefined)
                {
                    code.AppendLine(first ? "{" : "else{");
                    Statement64(code, defaultItem.GetProperty("body"), ref unique, trackBudget);
                    code.AppendLine("}");
                }
                code.AppendLine("}");
                break;
        }
    }

    private string EmitNarrow64ModelHeader(string top, IReadOnlyDictionary<string, string> names, int[] seqIds, bool hasFixedMemWrites, bool hasRuntimeLoops, bool canThrow)
    {
        string className = "V" + top;
        bool hasInit = Statements(Initialization).Any(st => Kind(st) is not "block");
        var procs = Kind(Body) == "parallel" ? Body.GetProperty("items").EnumerateArray().ToArray() : [Body];
        var eventIds = EventSignals.Select(SignalId).ToArray();

        var code = new StringBuilder("#pragma once\n#include \"external_model.h\"\nusing namespace vsrtl;\n");
        code.AppendLine($"class {className} final : public vsrtl::Model {{");
        if (hasRuntimeLoops || hasInit) code.AppendLine("size_t execution_budget=20000;");
        code.AppendLine($"vsrtl::w64::W64 _vs_prev[{Signals.Count}];");
        code.AppendLine($"vsrtl::w64::W64 _vs_next[{Signals.Count}];");
        if (hasFixedMemWrites) code.AppendLine("vsrtl::w64::MemLog64<16> _vs_mlog;");
        if (hasInit) code.AppendLine("std::vector<AssociativeStorage>assoc;");
        code.AppendLine("inline void settle(){");
        foreach (var step in SettledSteps)
        {
            int id = SignalId(Name(step.GetProperty("target")));
            var rhs = step.GetProperty("value");
            int vw = ExpressionWidth(rhs);
            bool exactWidth = vw == Signals[id].Width && !(ExpressionSigned(rhs) && Signals[id].Width > vw);
            code.AppendLine($"vsrtl::w64::store(s[{id}],{StoreWord64(Expression64(rhs, Signals[id].Width), Signals[id], exactWidth ? Signals[id].Width : -1)});");
        }
        code.AppendLine("}\nvoid evaluate() override {");
        if (hasRuntimeLoops) code.AppendLine("execution_budget=20000;");
        if (canThrow)
        {
            code.AppendLine($"auto saved_s=s;auto saved_mem=mem;vsrtl::w64::W64 saved_prev[{Signals.Count}];for(int i=0;i<{Signals.Count};++i)saved_prev[i]=_vs_prev[i];try{{");
        }
        code.AppendLine("settle();");
        for (int p = 0; p < procs.Length; ++p)
        {
            string cond = string.Join("||", procs[p].GetProperty("events").EnumerateArray().Select(trigger =>
            {
                int id = SignalId(trigger.GetProperty("signal").GetString()!);
                return $"vsrtl::w64::edge(_vs_prev[{id}],vsrtl::w64::to(s[{id}]),{(trigger.GetProperty("edge").GetString() == "posedge" ? "true" : "false")})";
            }));
            code.AppendLine($"const bool _vs_ev{p}={cond};");
        }
        string anyEv = string.Join("||", Enumerable.Range(0, procs.Length).Select(p => $"_vs_ev{p}"));
        code.AppendLine($"if({anyEv}){{");
        if (seqIds.Length > 0)
            code.AppendLine(string.Concat(seqIds.Select(id => $"_vs_next[{id}]=vsrtl::w64::to(s[{id}]);")));
        int unique = 0;
        for (int p = 0; p < procs.Length; ++p)
        {
            if (procs.Length > 1) code.AppendLine($"if(_vs_ev{p}){{");
            Statement64(code, procs[p].GetProperty("body"), ref unique, hasRuntimeLoops);
            if (procs.Length > 1) code.AppendLine("}");
        }
        if (seqIds.Length > 0)
            code.AppendLine(string.Concat(seqIds.Select(id => $"vsrtl::w64::store(s[{id}],_vs_next[{id}]);")));
        if (hasFixedMemWrites)
            code.AppendLine("_vs_mlog.commit(mem);");
        code.AppendLine("settle();\n}");
        foreach (int id in eventIds)
            code.AppendLine($"_vs_prev[{id}]=vsrtl::w64::to(s[{id}]);");
        if (canThrow)
        {
            code.AppendLine($"\n}}catch(...){{s=std::move(saved_s);mem=std::move(saved_mem);for(int i=0;i<{Signals.Count};++i)_vs_prev[i]=saved_prev[i];{(hasFixedMemWrites ? "_vs_mlog.count=0;_vs_mlog.overflow.clear();" : "")}throw;}}");
        }
        code.AppendLine("}\npublic:");
        // Non-virtual inline fast-path lets the C++ compiler inline evaluate() directly into host loops.
        code.AppendLine("inline void eval(){");
        code.AppendLine("if(fast_eval_ok&&owner_thread==std::this_thread::get_id())[[likely]]{");
        code.AppendLine("fast_eval_ok=false;fast_evaluating=true;fast_dirty=false;");
        if (canThrow)
            code.AppendLine($"try{{this->{className}::evaluate();}}catch(...){{fast_evaluating=false;fast_dirty=true;fast_eval_ok=fast_eligible;throw;}}");
        else
            code.AppendLine($"this->{className}::evaluate();");
        code.AppendLine("fast_evaluating=false;fast_eval_ok=fast_eligible;return;}");
        code.AppendLine("evalSlow();}");
        foreach (var signal in Signals.Where(s => s.Direction != "internal")) code.AppendLine($"vsrtl::Port {names[signal.Name]};");
        code.Append($"{className}():vsrtl::Model(\"{top}\",{{");
        code.AppendJoin(',', Signals.Select(s => $"{{\"{s.SourcePath ?? s.Name}\",{s.Width},{(s.Direction == "input" ? "vpiInput" : s.Direction == "output" ? "vpiOutput" : "vpiNoDirection")},{(s.Signed ? "true" : "false")},{(s.TwoState ? "true" : "false")}}}"));
        code.Append($"}},{SignalId(Clock!)})");
        foreach (var signal in Signals.Where(s => s.Direction != "internal")) code.Append($",{names[signal.Name]}(*this,{SignalId(signal.Name)})");
        code.AppendLine($"{{{(hasInit ? $"assoc.resize({Memories.Count});" : "")}mem={{");
        code.AppendJoin(',', Memories.Select(m => $"std::vector<W>({m.Depth},{(m.TwoState ? $"word(0,0,0,{m.Width})" : $"unknown({m.Width})")})"));
        code.AppendLine("};");
        if (hasInit) EmitInitialization(code);
        code.AppendLine($"for(int i=0;i<{Signals.Count};++i)_vs_prev[i]=vsrtl::w64::unknown(1);");
        code.AppendLine($"_vs_prev[{SignalId(Clock!)}]=vsrtl::w64::lit(0ULL,1);");
        foreach (var signal in Signals.Where(s => s.TwoState && s.Width == 1))
            code.AppendLine($"_vs_prev[{SignalId(signal.Name)}]=vsrtl::w64::lit(0ULL,1);");
        return code.AppendLine("}\n};").ToString();
    }
}
