using System.Text;
using System.Text.Json;
namespace VeloSim;

public sealed partial class RtlDesign
{
    internal IReadOnlyDictionary<string, string> RustPortNames()
    {
        var reserved = (
            "as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while abstract become box do final macro override priv try typeof unsized virtual yield new eval try_eval final_model time time_inc default open_vcd dump_vcd close_vcd set_plusargs add_plusarg test_plusargs value_plusargs set_random_seed random_seed next_urandom next_urandom_range force_signal release_signal is_forced save_checkpoint restore_checkpoint"
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

    private static string EmitRsConst64(string bits)
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
            ? $"W64::lit(0x{v:x}u64,{w})"
            : $"W64::lit4(0x{v:x}u64,0x{x:x}u64,0x{z:x}u64,{w})";
    }

    private static string EmitRsConst2(string bits)
    {
        ulong v = 0;
        foreach (char c in bits)
        {
            v <<= 1;
            if (c == '1') v |= 1;
        }
        return $"0x{v:x}u64";
    }

    private string RsMemIdx(JsonElement access, int id, bool twoState)
    {
        string idx = twoState ? RsExpr2(access.GetProperty("index")) : RsExpr64(access.GetProperty("index"));
        if (Memories[id].LowerBound == 0) return idx;
        return twoState ? $"w2::memidx({idx},{Memories[id].LowerBound})" : $"w64::memidx({idx},{Memories[id].LowerBound})";
    }

    private string RsExpr64(JsonElement e, int context = 0, bool? forceSigned = null)
    {
        string kind = Kind(e);
        bool signed = forceSigned ?? ExpressionSigned(e);
        string Extend(string val) => signed && context > ExpressionWidth(e) ? $"w64::sresize({val},{context})" : val;
        if (kind == "ref") return Extend($"self.s{SignalId(Name(e))}");
        if (kind == "const") return Extend(EmitRsConst64(e.GetProperty("bits").GetString()!));
        if (kind == "index")
        {
            int id = MemoryId(Name(e));
            return Extend($"w64::read(&self.m{id},{RsMemIdx(e, id, false)},{Memories[id].Width})");
        }
        if (kind == "bitselect") return $"w64::selbit({RsExpr64(e.GetProperty("value"))},{RsExpr64(e.GetProperty("index"))})";
        if (kind == "partselect") return $"w64::selpart({RsExpr64(e.GetProperty("value"))},{e.GetProperty("left").GetInt32()},{e.GetProperty("right").GetInt32()})";
        if (kind == "indexedselect") return $"w64::selidx({RsExpr64(e.GetProperty("value"))},{RsExpr64(e.GetProperty("base"))},{e.GetProperty("width").GetInt32()},{(e.GetProperty("descending").GetBoolean() ? "true" : "false")})?";
        if (kind == "test_plusargs") return Extend($"W64::lit(self.test_plusargs({CppStringLiteral(e.GetProperty("prefix").GetString()!)}) as u64, 32)");
        if (kind == "urandom") return Extend("W64::lit(self.next_urandom() as u64, 32)");
        if (kind == "urandom_range")
        {
            string maxExpr = $"({RsExpr64(e.GetProperty("max"))}).v as u32";
            string minExpr = e.TryGetProperty("min", out var minEl) ? $"({RsExpr64(minEl)}).v as u32" : "0u32";
            return Extend($"{{ let _rmax = {maxExpr}; let _rmin = {minExpr}; W64::lit(self.next_urandom_range(_rmax, _rmin) as u64, 32) }}");
        }
        var args = e.GetProperty("args").EnumerateArray().ToArray();
        int width = Math.Max(context, ExpressionWidth(e));
        return kind switch
        {
            "castsigned" or "castunsigned" => Extend(RsExpr64(args[0])),
            "countones" => Extend($"w64::countones({RsExpr64(args[0])})"),
            "onehot" => $"w64::onehot({RsExpr64(args[0])})",
            "onehot0" => $"w64::onehot0({RsExpr64(args[0])})",
            "isunknown" => $"w64::isunknown({RsExpr64(args[0])})",
            "clog2" => Extend($"w64::clog2({RsExpr64(args[0])})"),
            "add" or "sub" => $"w64::arith({RsExpr64(args[0], width, signed)},{RsExpr64(args[1], width, signed)},{width},{(kind == "sub" ? "true" : "false")})",
            "bitnot" => $"w64::bnot({RsExpr64(args[0], width, signed)},{width})",
            "neg" => $"w64::arith(W64::lit(0u64,{width}),{RsExpr64(args[0], width, signed)},{width},true)",
            "pos" => $"w64::resize({RsExpr64(args[0], width, signed)},{width})",
            "mul" or "div" or "mod" => $"w64::{(signed ? "sproduct" : "product")}({RsExpr64(args[0], width, signed)},{RsExpr64(args[1], width, signed)},{width},{(kind == "mul" ? 0 : kind == "div" ? 1 : 2)})",
            "cond" => $"w64::cond({RsExpr64(args[0])},{RsExpr64(args[1], width, signed)},{RsExpr64(args[2], width, signed)},{width})",
            "ashr" when signed => $"w64::sshift({RsExpr64(args[0], width, true)},{RsExpr64(args[1])},{width})",
            "shl" or "shr" or "ashr" => $"w64::shift({RsExpr64(args[0], width, signed)},{RsExpr64(args[1])},{width},{(kind == "shl" ? "false" : "true")})",
            "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor" => $"w64::red({RsExpr64(args[0])},{(kind == "redand" ? 0 : kind == "rednand" ? 1 : kind == "redor" ? 2 : kind == "rednor" ? 3 : kind == "redxor" ? 4 : 5)})",
            "bitxnor" => $"w64::bnot(w64::bbin({RsExpr64(args[0], width, signed)},{RsExpr64(args[1], width, signed)},{width},2),{width})",
            "bitand" or "bitor" or "bitxor" => $"w64::bbin({RsExpr64(args[0], width, signed)},{RsExpr64(args[1], width, signed)},{width},{(kind == "bitand" ? 0 : kind == "bitor" ? 1 : 2)})",
            "not" => $"w64::lnot({RsExpr64(args[0])})",
            "and" => $"w64::land({RsExpr64(args[0])},{RsExpr64(args[1])})",
            "or" => $"w64::lor({RsExpr64(args[0])},{RsExpr64(args[1])})",
            "eq" or "neq" => (kind == "neq" ? "w64::lnot(" : "") + $"w64::eq({RsExpr64(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{RsExpr64(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))})" + (kind == "neq" ? ")" : ""),
            "wildeq" or "wildneq" => $"w64::{(kind == "wildneq" ? "wildneq" : "wildeq")}({RsExpr64(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{RsExpr64(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))})",
            "caseeq" or "caseneq" => $"W64::lit(if {(kind == "caseneq" ? "!" : "")}w64::exact({RsExpr64(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{RsExpr64(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))}) {{ 1 }} else {{ 0 }},1)",
            "lt" or "le" or "gt" or "ge" => $"w64::{(args.All(ExpressionSigned) ? "scmp" : "cmp")}({RsExpr64(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{RsExpr64(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{(kind == "lt" ? 0 : kind == "le" ? 1 : kind == "gt" ? 2 : 3)})",
            "concat" => $"w64::concat({RsExpr64(args[0])},{RsExpr64(args[1])})",
            _ => throw new IrValidationException("Unsupported Rust expression.")
        };
    }

    private string RsExpr2(JsonElement e, int context = 0, bool? forceSigned = null)
    {
        string kind = Kind(e);
        int ew = ExpressionWidth(e);
        bool signed = forceSigned ?? ExpressionSigned(e);
        string Extend(string val) => signed && context > ew ? $"w2::sresize({val},{ew},{context})" : val;
        if (kind == "ref") return Extend($"self.s{SignalId(Name(e))}");
        if (kind == "const") return Extend(EmitRsConst2(e.GetProperty("bits").GetString()!));
        if (kind == "index")
        {
            int id = MemoryId(Name(e));
            return Extend($"w2::read(&self.m{id},{RsMemIdx(e, id, true)})");
        }
        if (kind == "bitselect")
        {
            var valEl = e.GetProperty("value");
            return $"w2::selbit({RsExpr2(valEl)},{ExpressionWidth(valEl)},{RsExpr2(e.GetProperty("index"))})";
        }
        if (kind == "partselect")
        {
            var valEl = e.GetProperty("value");
            return $"w2::selpart({RsExpr2(valEl)},{ExpressionWidth(valEl)},{e.GetProperty("left").GetInt32()},{e.GetProperty("right").GetInt32()})";
        }
        if (kind == "indexedselect")
        {
            var valEl = e.GetProperty("value");
            return $"w2::selidx({RsExpr2(valEl)},{ExpressionWidth(valEl)},{RsExpr2(e.GetProperty("base"))},{e.GetProperty("width").GetInt32()},{(e.GetProperty("descending").GetBoolean() ? "true" : "false")})?";
        }
        if (kind == "test_plusargs") return Extend($"(self.test_plusargs({CppStringLiteral(e.GetProperty("prefix").GetString()!)}) as u64)");
        if (kind == "urandom") return Extend("(self.next_urandom() as u64)");
        if (kind == "urandom_range")
        {
            string maxExpr = $"({RsExpr2(e.GetProperty("max"))}) as u32";
            string minExpr = e.TryGetProperty("min", out var minEl) ? $"({RsExpr2(minEl)}) as u32" : "0u32";
            return Extend($"{{ let _rmax = {maxExpr}; let _rmin = {minExpr}; self.next_urandom_range(_rmax, _rmin) as u64 }}");
        }
        var args = e.GetProperty("args").EnumerateArray().ToArray();
        int width = Math.Max(context, ew);
        return kind switch
        {
            "castsigned" or "castunsigned" => Extend(RsExpr2(args[0])),
            "countones" => Extend($"w2::countones({RsExpr2(args[0])},{ExpressionWidth(args[0])})"),
            "onehot" => $"w2::onehot({RsExpr2(args[0])},{ExpressionWidth(args[0])})",
            "onehot0" => $"w2::onehot0({RsExpr2(args[0])},{ExpressionWidth(args[0])})",
            "isunknown" => "0u64",
            "clog2" => Extend($"w2::clog2({RsExpr2(args[0])},{ExpressionWidth(args[0])})"),
            "add" or "sub" => $"w2::arith({RsExpr2(args[0], width, signed)},{RsExpr2(args[1], width, signed)},{width},{(kind == "sub" ? "true" : "false")})",
            "bitnot" => $"w2::bnot({RsExpr2(args[0], width, signed)},{width})",
            "neg" => $"w2::arith(0u64,{RsExpr2(args[0], width, signed)},{width},true)",
            "pos" => $"w2::resize({RsExpr2(args[0], width, signed)},{width})",
            "mul" or "div" or "mod" => $"w2::{(signed ? "sproduct" : "product")}({RsExpr2(args[0], width, signed)},{RsExpr2(args[1], width, signed)},{width},{(kind == "mul" ? 0 : kind == "div" ? 1 : 2)})",
            "cond" => $"w2::cond({RsExpr2(args[0])},{RsExpr2(args[1], width, signed)},{RsExpr2(args[2], width, signed)},{width})",
            "ashr" when signed => $"w2::sshift({RsExpr2(args[0], width, true)},{RsExpr2(args[1])},{width})",
            "shl" or "shr" or "ashr" => $"w2::shift({RsExpr2(args[0], width, signed)},{RsExpr2(args[1])},{width},{(kind == "shl" ? "false" : "true")})",
            "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor" => $"w2::red({RsExpr2(args[0])},{ExpressionWidth(args[0])},{(kind == "redand" ? 0 : kind == "rednand" ? 1 : kind == "redor" ? 2 : kind == "rednor" ? 3 : kind == "redxor" ? 4 : 5)})",
            "bitxnor" => $"w2::bnot(w2::bbin({RsExpr2(args[0], width, signed)},{RsExpr2(args[1], width, signed)},{width},2),{width})",
            "bitand" or "bitor" or "bitxor" => $"w2::bbin({RsExpr2(args[0], width, signed)},{RsExpr2(args[1], width, signed)},{width},{(kind == "bitand" ? 0 : kind == "bitor" ? 1 : 2)})",
            "not" => $"w2::lnot({RsExpr2(args[0])})",
            "and" => $"w2::land({RsExpr2(args[0])},{RsExpr2(args[1])})",
            "or" => $"w2::lor({RsExpr2(args[0])},{RsExpr2(args[1])})",
            "eq" or "neq" or "caseeq" or "caseneq" or "wildeq" or "wildneq" =>
                (kind is "neq" or "caseneq" or "wildneq" ? "w2::lnot(" : "")
                + $"w2::eq({RsExpr2(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned))},{RsExpr2(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned))})"
                + (kind is "neq" or "caseneq" or "wildneq" ? ")" : ""),
            "lt" or "le" or "gt" or "ge" =>
                args.All(ExpressionSigned)
                    ? $"w2::scmp({RsExpr2(args[0], args.Max(ExpressionWidth), true)},{RsExpr2(args[1], args.Max(ExpressionWidth), true)},{args.Max(ExpressionWidth)},{(kind == "lt" ? 0 : kind == "le" ? 1 : kind == "gt" ? 2 : 3)})"
                    : $"w2::cmp({RsExpr2(args[0], args.Max(ExpressionWidth), false)},{RsExpr2(args[1], args.Max(ExpressionWidth), false)},{(kind == "lt" ? 0 : kind == "le" ? 1 : kind == "gt" ? 2 : 3)})",
            "concat" => $"w2::concat({RsExpr2(args[0])},{RsExpr2(args[1])},{ExpressionWidth(args[1])})",
            _ => throw new IrValidationException("Unsupported Rust two-state expression.")
        };
    }

    private string RsStore(string val, RtlSignal signal, bool twoState, int exprWidth = -1)
    {
        if (twoState) return exprWidth == signal.Width ? val : $"w2::resize({val},{signal.Width})";
        return signal.TwoState
            ? $"w64::two_state(w64::resize({val},{signal.Width}))"
            : (exprWidth == signal.Width ? val : $"w64::resize({val},{signal.Width})");
    }

    private void RsWordWrite(StringBuilder code, JsonElement target, string value, bool twoState, int exprWidth = -1)
    {
        if (Kind(target) == "ref")
        {
            int id = SignalId(Name(target));
            code.AppendLine($"next{id} = {RsStore(value, Signals[id], twoState, exprWidth)};");
            return;
        }
        int sid = SignalId(Name(target.GetProperty("value"))), width = ExpressionWidth(target);
        bool desc = Kind(target) == "indexedselect" && target.GetProperty("descending").GetBoolean();
        if (twoState)
        {
            string basis = Kind(target) == "bitselect" ? RsExpr2(target.GetProperty("index"))
                : Kind(target) == "indexedselect" ? RsExpr2(target.GetProperty("base"))
                : $"{target.GetProperty("right").GetInt32()}u64";
            code.AppendLine($"next{sid} = w2::depidx(next{sid},{Signals[sid].Width},{basis},{width},{(desc ? "true" : "false")},{value})?;");
        }
        else
        {
            string basis = Kind(target) == "bitselect" ? RsExpr64(target.GetProperty("index"))
                : Kind(target) == "indexedselect" ? RsExpr64(target.GetProperty("base"))
                : $"W64::lit({target.GetProperty("right").GetInt32()}u64,64)";
            string dep = $"w64::depidx(next{sid},{basis},{width},{(desc ? "true" : "false")},{value})?";
            code.AppendLine($"next{sid} = {(Signals[sid].TwoState ? $"w64::two_state({dep})" : dep)};");
        }
    }

    private void RsStmt(StringBuilder code, JsonElement e, ref int unique, bool twoState, bool trackBudget)
    {
        if (trackBudget) code.AppendLine("w64::step(&mut budget)?;");
        switch (Kind(e))
        {
            case "while":
            case "do":
                string cond = twoState ? $"w2::yes({RsExpr2(e.GetProperty("condition"))})" : $"w64::yes({RsExpr64(e.GetProperty("condition"))})";
                if (Kind(e) == "do")
                {
                    code.AppendLine("loop {");
                    code.AppendLine("w64::step(&mut budget)?;");
                    RsStmt(code, e.GetProperty("body"), ref unique, twoState, trackBudget);
                    code.AppendLine($"if !({cond}) {{ break; }}");
                    code.AppendLine("}");
                }
                else
                {
                    code.AppendLine($"while {cond} {{");
                    code.AppendLine("w64::step(&mut budget)?;");
                    RsStmt(code, e.GetProperty("body"), ref unique, twoState, trackBudget);
                    code.AppendLine("}");
                }
                break;
            case "runtime_repeat":
                string rl = "rrep" + unique++;
                var cntEl = e.GetProperty("count");
                string cnt = twoState
                    ? $"w2::repeat_count({RsExpr2(cntEl)},{ExpressionWidth(cntEl)},{(ExpressionSigned(cntEl) ? "true" : "false")})?"
                    : $"w64::repeat_count({RsExpr64(cntEl)},{(ExpressionSigned(cntEl) ? "true" : "false")})?";
                code.AppendLine($"for _{rl} in 0..({cnt}) {{");
                code.AppendLine("w64::step(&mut budget)?;");
                RsStmt(code, e.GetProperty("body"), ref unique, twoState, trackBudget);
                code.AppendLine("}");
                break;
            case "repeat":
                string lp = "rep" + unique++;
                code.AppendLine($"for _{lp} in 0..{e.GetProperty("count").GetInt32()}usize {{");
                RsStmt(code, e.GetProperty("body"), ref unique, twoState, trackBudget);
                code.AppendLine("}");
                break;
            case "block":
                foreach (var item in e.GetProperty("items").EnumerateArray()) RsStmt(code, item, ref unique, twoState, trackBudget);
                break;
            case "nba":
                var target = e.GetProperty("target");
                if (Kind(target) == "concat")
                {
                    int total = ExpressionWidth(target), remaining = total;
                    string rhs = "nba" + unique++;
                    code.AppendLine(twoState
                        ? $"{{ let {rhs}: u64 = w2::resize({RsExpr2(e.GetProperty("value"), total)},{total});"
                        : $"{{ let {rhs}: W64 = w64::resize({RsExpr64(e.GetProperty("value"), total)},{total});");
                    foreach (var leaf in TargetLeaves(target))
                    {
                        int width = ExpressionWidth(leaf);
                        remaining -= width;
                        string part = twoState
                            ? $"w2::selpart({rhs},{total},{remaining + width - 1},{remaining})"
                            : $"w64::selpart({rhs},{remaining + width - 1},{remaining})";
                        RsWordWrite(code, leaf, part, twoState, width);
                    }
                    code.AppendLine("}");
                }
                else if (Kind(target) is "ref" or "bitselect" or "partselect" or "indexedselect")
                {
                    int tw = ExpressionWidth(target);
                    var valEl = e.GetProperty("value");
                    int vw = ExpressionWidth(valEl);
                    bool exactWidth = vw == tw && !(ExpressionSigned(valEl) && tw > vw);
                    string expr = twoState ? RsExpr2(valEl, tw) : RsExpr64(valEl, tw);
                    RsWordWrite(code, target, expr, twoState, exactWidth ? tw : -1);
                }
                else
                {
                    int id = MemoryId(Name(target));
                    if (twoState)
                        code.AppendLine($"self.mlog.push({id},{Memories[id].Depth},{RsMemIdx(target, id, true)},{RsExpr2(e.GetProperty("value"), Memories[id].Width)},{Memories[id].Width});");
                    else
                    {
                        string v = RsExpr64(e.GetProperty("value"), Memories[id].Width);
                        if (Memories[id].TwoState) v = $"w64::two_state({v})";
                        code.AppendLine($"self.mlog.push({id},{Memories[id].Depth},{RsMemIdx(target, id, false)},{v},{Memories[id].Width});");
                    }
                }
                break;
            case "if":
                string ifCond = twoState ? $"w2::yes({RsExpr2(e.GetProperty("condition"))})" : $"w64::yes({RsExpr64(e.GetProperty("condition"))})";
                code.AppendLine($"if {ifCond} {{");
                RsStmt(code, e.GetProperty("then"), ref unique, twoState, trackBudget);
                code.AppendLine("} else {");
                RsStmt(code, e.GetProperty("else"), ref unique, twoState, trackBudget);
                code.AppendLine("}");
                break;
            case "assert":
            {
                string condExpr = twoState ? $"w2::yes({RsExpr2(e.GetProperty("condition"))})" : $"w64::yes({RsExpr64(e.GetProperty("condition"))})";
                string msg = CppStringLiteral(e.TryGetProperty("message", out var am) ? am.GetString()! : "RTL assertion failed");
                code.AppendLine($"if !({condExpr}) {{ return Err({msg}); }}");
                break;
            }
            case "finish":
                code.AppendLine("self.finished = true;");
                break;
            case "display":
            {
                string fmt = CppStringLiteral(e.GetProperty("format").GetString()!);
                bool nl = !e.TryGetProperty("newline", out var nlProp) || nlProp.GetBoolean();
                string argList = e.TryGetProperty("args", out var dArgs)
                    ? string.Join(", ", dArgs.EnumerateArray().Select(a => twoState ? $"W64::lit({RsExpr2(a)},{ExpressionWidth(a)})" : RsExpr64(a)))
                    : "";
                code.AppendLine($"print!(\"{{}}\", w64::format_display({fmt}, &[{argList}], {(nl ? "true" : "false")}));");
                break;
            }
            case "value_plusargs":
            {
                string fmt = CppStringLiteral(e.GetProperty("format").GetString()!);
                int tid = SignalId(e.GetProperty("target").GetString()!);
                var tsig = Signals[tid];
                code.AppendLine($"{{ let mut _vp = {(twoState ? $"W64::lit(next{tid},{tsig.Width})" : $"next{tid}")}; let _vpm = self.value_plusargs({fmt}, &mut _vp); if _vpm != 0 {{ next{tid} = {RsStore(twoState ? "_vp.v" : "_vp", tsig, twoState)}; }}");
                if (e.TryGetProperty("matched", out var vpMatched) && vpMatched.ValueKind != JsonValueKind.Null)
                {
                    int mid = SignalId(vpMatched.GetString()!);
                    code.AppendLine($"next{mid} = {RsStore(twoState ? "(_vpm as u64)" : "W64::lit(_vpm as u64, 32)", Signals[mid], twoState)};");
                }
                code.AppendLine("}");
                break;
            }
            case "force":
            {
                int tid = SignalId(e.GetProperty("target").GetString()!);
                var tsig = Signals[tid];
                string valExpr = twoState
                    ? $"W64::lit({RsStore(RsExpr2(e.GetProperty("value")), tsig, true)},{tsig.Width})"
                    : RsStore(RsExpr64(e.GetProperty("value")), tsig, false);
                code.AppendLine($"{{ let _fv = {valExpr}; self.force_signal({tid}, _fv); }}");
                break;
            }
            case "release":
            {
                int tid = SignalId(e.GetProperty("target").GetString()!);
                code.AppendLine($"self.release_signal({tid});");
                break;
            }
            case "case":
            {
                string lbl = "cs" + unique++;
                var items = e.GetProperty("items").EnumerateArray().ToArray();
                string matcher = CaseKind(e) switch { "casez" => "w64::casez_match", "casex" => "w64::casex_match", _ => "w64::exact" };
                var allMatches = items.SelectMany(CaseItemMatches).ToArray();
                int cw = Math.Max(ExpressionWidth(e.GetProperty("value")), allMatches.Select(ExpressionWidth).DefaultIfEmpty(1).Max());
                bool csSigned = ExpressionSigned(e.GetProperty("value")) && allMatches.All(ExpressionSigned);
                code.AppendLine(twoState
                    ? $"{{ let {lbl}: u64 = {RsExpr2(e.GetProperty("value"), cw, csSigned)};"
                    : $"{{ let {lbl}: W64 = {RsExpr64(e.GetProperty("value"), cw, csSigned)};");
                bool first = true;
                foreach (var item in items.Where(i => !IsCaseDefault(i)))
                {
                    var ms = CaseItemMatches(item);
                    string cmp = twoState
                        ? string.Join(" || ", ms.Select(m => $"{lbl} == {RsExpr2(m, cw, csSigned)}"))
                        : string.Join(" || ", ms.Select(m => $"{matcher}({lbl},{RsExpr64(m, cw, csSigned)})"));
                    code.AppendLine($"{(first ? "if" : "else if")} {cmp} {{");
                    RsStmt(code, item.GetProperty("body"), ref unique, twoState, trackBudget);
                    code.AppendLine("}");
                    first = false;
                }
                var defItem = items.FirstOrDefault(IsCaseDefault);
                if (defItem.ValueKind != JsonValueKind.Undefined)
                {
                    code.AppendLine(first ? "{" : "else {");
                    RsStmt(code, defItem.GetProperty("body"), ref unique, twoState, trackBudget);
                    code.AppendLine("}");
                }
                code.AppendLine("}");
                break;
            }
        }
    }

    /// <summary>Emits a safe, zero-allocation Rust 2021 model module using W64 (4-state) or u64 (2-state) fields.</summary>
    public string EmitRustModel(string top, RtlStateMode stateMode = RtlStateMode.FourState)
    {
        if (!Identifier(top) || ("V" + top).Contains("__", StringComparison.Ordinal)) throw new ArgumentException("Invalid external model top name.", nameof(top));
        if (!CanUsePortable64(stateMode))
            throw new IrValidationException("Rust target currently supports non-timed <=64-bit event-driven and combinational designs without initialization blocks or dynamic, queue, associative, or multidimensional arrays.");
        bool twoState = stateMode == RtlStateMode.TwoState;
        string structName = "V" + top;
        var names = RustPortNames();
        if (names.Values.Contains(structName)) throw new ArgumentException("Port conflicts with generated struct name.", nameof(top));
        string wordType = twoState ? "u64" : "W64";
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
        code.AppendLine("#![forbid(unsafe_code)]");
        code.AppendLine("#![allow(non_camel_case_types, non_snake_case, unused_variables, unused_mut, unused_parens, unused_imports, dead_code, clippy::all)]");
        code.AppendLine("pub mod runtime;");
        code.AppendLine("use runtime::{checkpoint, w2, w64, MemLog2, MemLog64, PlusargStore, SplitMix64Rng, VcdSlot, W64};");
        code.AppendLine();
        code.AppendLine("#[derive(Clone)]");
        code.AppendLine($"pub struct {structName} {{");
        code.AppendLine("    time: u64,");
        code.AppendLine("    finished: bool,");
        code.AppendLine("    vcd: VcdSlot,");
        code.AppendLine("    plusargs: PlusargStore,");
        code.AppendLine("    rng: SplitMix64Rng,");
        code.AppendLine($"    forced: Vec<(usize, {wordType})>,");
        for (int i = 0; i < Signals.Count; ++i)
            code.AppendLine($"    s{i}: {wordType},");
        foreach (int id in eventIds)
            code.AppendLine($"    prev{id}: {wordType},");
        for (int i = 0; i < Memories.Count; ++i)
            code.AppendLine($"    m{i}: [{wordType}; {Memories[i].Depth}],");
        if (hasFixedMemWrites)
            code.AppendLine($"    mlog: {(twoState ? "MemLog2" : "MemLog64")},");
        code.AppendLine("}");
        code.AppendLine();
        code.AppendLine($"impl Default for {structName} {{");
        code.AppendLine("    fn default() -> Self { Self::new() }");
        code.AppendLine("}");
        code.AppendLine();
        code.AppendLine($"impl {structName} {{");
        code.AppendLine("    pub fn new() -> Self {");
        code.AppendLine("        Self {");
        code.AppendLine("            time: 0,");
        code.AppendLine("            finished: false,");
        code.AppendLine("            vcd: VcdSlot::new(),");
        code.AppendLine("            plusargs: PlusargStore::new(),");
        code.AppendLine("            rng: SplitMix64Rng::new(),");
        code.AppendLine("            forced: Vec::new(),");
        for (int i = 0; i < Signals.Count; ++i)
        {
            var sig = Signals[i];
            if (twoState) code.AppendLine($"            s{i}: 0u64,");
            else if (sig.Name == Clock || sig.TwoState) code.AppendLine($"            s{i}: W64::lit(0u64, {sig.Width}),");
            else code.AppendLine($"            s{i}: W64::unknown({sig.Width}),");
        }
        foreach (int id in eventIds)
        {
            var sig = Signals[id];
            if (twoState) code.AppendLine($"            prev{id}: 0u64,");
            else if (sig.Name == Clock || sig.TwoState) code.AppendLine($"            prev{id}: W64::lit(0u64, 1),");
            else code.AppendLine($"            prev{id}: W64::unknown(1),");
        }
        for (int i = 0; i < Memories.Count; ++i)
        {
            var mem = Memories[i];
            if (twoState) code.AppendLine($"            m{i}: [0u64; {mem.Depth}],");
            else code.AppendLine($"            m{i}: [{(mem.TwoState ? $"W64::lit(0u64, {mem.Width})" : $"W64::unknown({mem.Width})")}; {mem.Depth}],");
        }
        if (hasFixedMemWrites)
            code.AppendLine($"            mlog: {(twoState ? "MemLog2::new()" : "MemLog64::new()")},");
        code.AppendLine("        }");
        code.AppendLine("    }");
        code.AppendLine();
        code.AppendLine("    #[inline(always)] pub fn time(&self) -> u64 { self.time }");
        code.AppendLine("    #[inline(always)] pub fn time_inc(&mut self, delta: u64) { self.time = self.time.wrapping_add(delta); }");
        code.AppendLine("    #[inline(always)] pub fn final_model(&mut self) { self.close_vcd(); }");
        code.AppendLine("    #[inline(always)] pub fn got_finish(&self) -> bool { self.finished }");
        code.AppendLine("    pub fn set_plusargs<I, S>(&mut self, args: I) where I: IntoIterator<Item = S>, S: AsRef<str> { self.plusargs.set(args); }");
        code.AppendLine("    pub fn add_plusarg(&mut self, arg: &str) { self.plusargs.add(arg); }");
        code.AppendLine("    pub fn test_plusargs(&self, prefix: &str) -> u32 { self.plusargs.test(prefix) }");
        code.AppendLine("    pub fn value_plusargs(&self, spec: &str, out: &mut W64) -> u32 { self.plusargs.value(spec, out) }");
        code.AppendLine("    #[inline(always)] pub fn set_random_seed(&mut self, seed: u64) { self.rng.state = seed; }");
        code.AppendLine("    #[inline(always)] pub fn random_seed(&self) -> u64 { self.rng.state }");
        code.AppendLine("    #[inline(always)] pub fn next_urandom(&mut self) -> u32 { self.rng.next_u32() }");
        code.AppendLine("    #[inline(always)] pub fn next_urandom_range(&mut self, max_val: u32, min_val: u32) -> u32 { self.rng.next_range(max_val, min_val) }");
        code.AppendLine("    pub fn is_forced(&self, signal_id: usize) -> bool { self.forced.iter().any(|(id, _)| *id == signal_id) }");
        code.AppendLine("    pub fn force_signal(&mut self, signal_id: usize, value: W64) {");
        code.AppendLine("        let fv = match signal_id {");
        for (int i = 0; i < Signals.Count; ++i)
        {
            var sig = Signals[i];
            if (twoState)
                code.AppendLine($"            {i} => {{ let v = w2::resize(value.v, {sig.Width}); self.s{i} = v; v }}");
            else
                code.AppendLine($"            {i} => {{ let v = {(sig.TwoState ? $"w64::two_state(w64::resize(value, {sig.Width}))" : $"w64::resize(value, {sig.Width})")}; self.s{i} = v; v }}");
        }
        code.AppendLine("            _ => panic!(\"invalid signal_id for force_signal\"),");
        code.AppendLine("        };");
        code.AppendLine("        if let Some(entry) = self.forced.iter_mut().find(|(id, _)| *id == signal_id) { entry.1 = fv; } else { self.forced.push((signal_id, fv)); }");
        code.AppendLine("    }");
        code.AppendLine("    pub fn release_signal(&mut self, signal_id: usize) { self.forced.retain(|(id, _)| *id != signal_id); }");
        code.AppendLine("    #[inline(always)] fn apply_forces(&mut self) {");
        code.AppendLine("        if self.forced.is_empty() { return; }");
        code.AppendLine("        for idx in 0..self.forced.len() {");
        code.AppendLine("            let (sid, val) = self.forced[idx];");
        code.AppendLine("            match sid {");
        for (int i = 0; i < Signals.Count; ++i)
            code.AppendLine($"                {i} => self.s{i} = val,");
        code.AppendLine("                _ => {}");
        code.AppendLine("            }");
        code.AppendLine("        }");
        code.AppendLine("    }");
        code.Append("    const SIGNAL_SCHEMA: &'static [(&'static str, i32)] = &[");
        code.AppendJoin(", ", Signals.Select(s => $"({CppStringLiteral(s.SourcePath ?? s.Name)}, {s.Width})"));
        code.AppendLine("];");
        code.AppendLine("    pub fn open_vcd(&mut self, path: &str, timescale: &str) -> std::io::Result<()> {");
        code.AppendLine($"        self.vcd.open(path, \"{top}\", Self::SIGNAL_SCHEMA, timescale)");
        code.AppendLine("    }");
        code.AppendLine("    pub fn dump_vcd(&mut self) -> std::io::Result<()> {");
        code.Append("        self.vcd.dump(self.time, &[");
        code.AppendJoin(", ", Signals.Select((s, idx) => twoState ? $"W64::lit(self.s{idx}, {s.Width})" : $"self.s{idx}"));
        code.AppendLine("])");
        code.AppendLine("    }");
        code.AppendLine("    pub fn close_vcd(&mut self) { self.vcd.close(); }");
        code.AppendLine($"    fn checkpoint_schema_hash() -> u64 {{ checkpoint::schema_hash(\"{top}\", {(twoState ? "true" : "false")}, Self::SIGNAL_SCHEMA, &[{string.Join(", ", Memories.Select(m => $"({m.Depth}, {m.Width})"))}]) }}");
        code.AppendLine("    pub fn save_checkpoint(&self, path: &str) -> std::io::Result<()> {");
        code.Append("        let sigs: [W64; " + Signals.Count + "] = [");
        code.AppendJoin(", ", Signals.Select((s, idx) => twoState ? $"W64::lit(self.s{idx}, {s.Width})" : $"self.s{idx}"));
        code.AppendLine("];");
        code.Append("        let prevs: [W64; " + eventIds.Length + "] = [");
        code.AppendJoin(", ", eventIds.Select(id => twoState ? $"W64::lit(self.prev{id}, 1)" : $"self.prev{id}"));
        code.AppendLine("];");
        code.AppendLine($"        let mut mems: Vec<Vec<W64>> = Vec::with_capacity({Memories.Count});");
        for (int i = 0; i < Memories.Count; ++i)
        {
            if (twoState)
                code.AppendLine($"        mems.push(self.m{i}.iter().map(|&v| W64::lit(v, {Memories[i].Width})).collect());");
            else
                code.AppendLine($"        mems.push(self.m{i}.to_vec());");
        }
        code.AppendLine("        checkpoint::save(path, Self::checkpoint_schema_hash(), self.time, self.rng.state, &sigs, &prevs, &mems)");
        code.AppendLine("    }");
        code.AppendLine("    pub fn restore_checkpoint(&mut self, path: &str) -> std::io::Result<()> {");
        code.AppendLine($"        let (t, rng_state, sigs, prevs, mems) = checkpoint::load(path, Self::checkpoint_schema_hash(), {Signals.Count}, {eventIds.Length}, &[{string.Join(", ", Memories.Select(m => m.Depth))}])?;");
        code.AppendLine("        self.time = t; self.rng.state = rng_state;");
        for (int i = 0; i < Signals.Count; ++i)
            code.AppendLine(twoState ? $"        self.s{i} = w2::resize(sigs[{i}].v, {Signals[i].Width});" : $"        self.s{i} = sigs[{i}];");
        for (int i = 0; i < eventIds.Length; ++i)
            code.AppendLine(twoState ? $"        self.prev{eventIds[i]} = prevs[{i}].v & 1u64;" : $"        self.prev{eventIds[i]} = prevs[{i}];");
        for (int i = 0; i < Memories.Count; ++i)
        {
            if (twoState)
                code.AppendLine($"        for j in 0..self.m{i}.len() {{ self.m{i}[j] = w2::resize(mems[{i}][j].v, {Memories[i].Width}); }}");
            else
                code.AppendLine($"        self.m{i}.copy_from_slice(&mems[{i}]);");
        }
        code.AppendLine("        Ok(())");
        code.AppendLine("    }");
        code.AppendLine();
        foreach (var sig in Signals.Where(s => s.Direction != "internal"))
        {
            int id = SignalId(sig.Name);
            string port = names[sig.Name];
            ulong mask = sig.Width >= 64 ? ulong.MaxValue : ((1UL << sig.Width) - 1UL);
            if (sig.Direction == "input")
            {
                if (twoState)
                {
                    code.AppendLine($"    #[inline(always)] pub fn set_{port}(&mut self, value: u64) {{ self.s{id} = value & 0x{mask:x}u64; }}");
                    code.AppendLine($"    #[inline(always)] pub fn {port}(&self) -> u64 {{ self.s{id} }}");
                    code.AppendLine($"    #[inline(always)] pub fn try_{port}(&self) -> Result<u64, &'static str> {{ Ok(self.s{id}) }}");
                    code.AppendLine($"    #[inline(always)] pub fn {port}_w64(&self) -> W64 {{ W64::lit(self.s{id}, {sig.Width}) }}");
                    code.AppendLine($"    #[inline(always)] pub fn set_{port}_w64(&mut self, value: W64) {{ self.s{id} = value.v & !(value.x | value.z) & 0x{mask:x}u64; }}");
                    code.AppendLine($"    pub fn {port}_bits(&self) -> String {{ w64::text(W64::lit(self.s{id}, {sig.Width})) }}");
                    code.AppendLine($"    pub fn set_{port}_bits(&mut self, bits: &str) -> Result<(), &'static str> {{ let w = w64::parse(bits, {sig.Width})?; self.s{id} = w.v & !(w.x | w.z) & 0x{mask:x}u64; Ok(()) }}");
                }
                else
                {
                    code.AppendLine($"    #[inline(always)] pub fn set_{port}(&mut self, value: u64) {{ self.s{id} = W64::lit(value & 0x{mask:x}u64, {sig.Width}); }}");
                    code.AppendLine($"    #[inline(always)] pub fn {port}(&self) -> u64 {{ w64::to_u64(self.s{id}).expect(\"integer conversion of X/Z port\") }}");
                    code.AppendLine($"    #[inline(always)] pub fn try_{port}(&self) -> Result<u64, &'static str> {{ w64::to_u64(self.s{id}) }}");
                    code.AppendLine($"    #[inline(always)] pub fn {port}_w64(&self) -> W64 {{ self.s{id} }}");
                    code.AppendLine($"    #[inline(always)] pub fn set_{port}_w64(&mut self, value: W64) {{ self.s{id} = {(sig.TwoState ? $"w64::two_state(w64::resize(value, {sig.Width}))" : $"w64::resize(value, {sig.Width})")}; }}");
                    code.AppendLine($"    pub fn {port}_bits(&self) -> String {{ w64::text(self.s{id}) }}");
                    code.AppendLine($"    pub fn set_{port}_bits(&mut self, bits: &str) -> Result<(), &'static str> {{ let w = w64::parse(bits, {sig.Width})?; self.set_{port}_w64(w); Ok(()) }}");
                }
            }
            else
            {
                if (twoState)
                {
                    code.AppendLine($"    #[inline(always)] pub fn {port}(&self) -> u64 {{ self.s{id} }}");
                    code.AppendLine($"    #[inline(always)] pub fn try_{port}(&self) -> Result<u64, &'static str> {{ Ok(self.s{id}) }}");
                    code.AppendLine($"    #[inline(always)] pub fn {port}_w64(&self) -> W64 {{ W64::lit(self.s{id}, {sig.Width}) }}");
                    code.AppendLine($"    pub fn {port}_bits(&self) -> String {{ w64::text(W64::lit(self.s{id}, {sig.Width})) }}");
                }
                else
                {
                    code.AppendLine($"    #[inline(always)] pub fn {port}(&self) -> u64 {{ w64::to_u64(self.s{id}).expect(\"integer conversion of X/Z port\") }}");
                    code.AppendLine($"    #[inline(always)] pub fn try_{port}(&self) -> Result<u64, &'static str> {{ w64::to_u64(self.s{id}) }}");
                    code.AppendLine($"    #[inline(always)] pub fn {port}_w64(&self) -> W64 {{ self.s{id} }}");
                    code.AppendLine($"    pub fn {port}_bits(&self) -> String {{ w64::text(self.s{id}) }}");
                }
            }
        }
        code.AppendLine();
        code.AppendLine("    #[inline(always)]");
        code.AppendLine(canThrow
            ? "    fn settle(&mut self) -> Result<(), &'static str> {"
            : "    fn settle(&mut self) {");
        foreach (var step in SettledSteps)
        {
            int id = SignalId(Name(step.GetProperty("target")));
            var rhs = step.GetProperty("value");
            int vw = ExpressionWidth(rhs);
            bool exactWidth = vw == Signals[id].Width && !(ExpressionSigned(rhs) && Signals[id].Width > vw);
            string expr = twoState ? RsExpr2(rhs, Signals[id].Width) : RsExpr64(rhs, Signals[id].Width);
            code.AppendLine($"        self.s{id} = {RsStore(expr, Signals[id], twoState, exactWidth ? Signals[id].Width : -1)};");
        }
        if (canThrow) code.AppendLine("        Ok(())");
        code.AppendLine("    }");
        code.AppendLine();
        if (canThrow)
        {
            code.AppendLine("    fn eval_inner(&mut self) -> Result<(), &'static str> {");
            if (hasRuntimeLoops) code.AppendLine("        let mut budget: usize = 20000;");
            code.AppendLine("        self.apply_forces();");
            code.AppendLine("        self.settle()?;");
            code.AppendLine("        self.apply_forces();");
        }
        else
        {
            code.AppendLine("    #[inline(always)]");
            code.AppendLine("    pub fn eval(&mut self) {");
            code.AppendLine("        self.apply_forces();");
            code.AppendLine("        self.settle();");
            code.AppendLine("        self.apply_forces();");
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
                        ? $"w2::edge(self.prev{id}, self.s{id}, {(pos ? "true" : "false")})"
                        : $"w64::edge(self.prev{id}, self.s{id}, {(pos ? "true" : "false")})";
                }));
                code.AppendLine($"        let ev{p} = {cond};");
            }
            string anyEv = string.Join(" || ", Enumerable.Range(0, procs.Length).Select(p => $"ev{p}"));
            code.AppendLine($"        if {anyEv} {{");
            foreach (int id in seqIds) code.AppendLine($"            let mut next{id} = self.s{id};");
            int unique = 0;
            for (int p = 0; p < procs.Length; ++p)
            {
                if (procs.Length > 1) code.AppendLine($"            if ev{p} {{");
                RsStmt(code, procs[p].GetProperty("body"), ref unique, twoState, hasRuntimeLoops);
                if (procs.Length > 1) code.AppendLine("            }");
            }
            foreach (int id in seqIds) code.AppendLine($"            self.s{id} = next{id};");
            if (hasFixedMemWrites)
            {
                code.AppendLine("            for i in 0..self.mlog.count {");
                code.AppendLine("                let e = self.mlog.buf[i];");
                code.AppendLine("                match e.mem_id {");
                for (int m = 0; m < Memories.Count; ++m)
                    code.AppendLine($"                    {m} => self.m{m}[e.index as usize] = e.value,");
                code.AppendLine("                    _ => {}");
                code.AppendLine("                }");
                code.AppendLine("            }");
                code.AppendLine("            if !self.mlog.overflow.is_empty() {");
                code.AppendLine("                for e in self.mlog.overflow.drain(..) {");
                code.AppendLine("                    match e.mem_id {");
                for (int m = 0; m < Memories.Count; ++m)
                    code.AppendLine($"                        {m} => self.m{m}[e.index as usize] = e.value,");
                code.AppendLine("                        _ => {}");
                code.AppendLine("                    }");
                code.AppendLine("                }");
                code.AppendLine("            }");
                code.AppendLine("            self.mlog.count = 0;");
            }
            code.AppendLine(canThrow ? "            self.settle()?;" : "            self.settle();");
            code.AppendLine("            self.apply_forces();");
            code.AppendLine("        }");
            foreach (int id in eventIds) code.AppendLine($"        self.prev{id} = self.s{id};");
        }
        if (canThrow)
        {
            code.AppendLine("        Ok(())");
            code.AppendLine("    }");
            code.AppendLine();
            code.AppendLine("    pub fn try_eval(&mut self) -> Result<(), &'static str> {");
            code.AppendLine("        let vcd = self.vcd.take();");
            code.AppendLine("        let saved = self.clone();");
            code.AppendLine("        if let Err(err) = self.eval_inner() {");
            code.AppendLine("            *self = saved;");
            code.AppendLine("            self.vcd = vcd;");
            code.AppendLine("            return Err(err);");
            code.AppendLine("        }");
            code.AppendLine("        self.vcd = vcd;");
            code.AppendLine("        Ok(())");
            code.AppendLine("    }");
            code.AppendLine();
            code.AppendLine("    #[inline(always)]");
            code.AppendLine("    pub fn eval(&mut self) { self.try_eval().expect(\"VeloSim evaluation failed\"); }");
        }
        else
        {
            code.AppendLine("    }");
            code.AppendLine();
            code.AppendLine("    #[inline(always)]");
            code.AppendLine("    pub fn try_eval(&mut self) -> Result<(), &'static str> { self.eval(); Ok(()) }");
        }
        code.AppendLine("}");
        return code.ToString();
    }

    internal static string EmitRustRuntime() => """
// Auto-generated VeloSim Rust 2021 runtime (register-resident W64 4-state + W2 2-state).
#![allow(dead_code)]

#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub struct W64 {
    pub v: u64,
    pub x: u64,
    pub z: u64,
    pub w: i32,
}

impl W64 {
    #[inline(always)]
    pub const fn mask(w: i32) -> u64 {
        if w >= 64 {
            u64::MAX
        } else if w <= 0 {
            0
        } else {
            (1u64 << (w as u32)) - 1
        }
    }

    #[inline(always)]
    pub const fn lit(v: u64, w: i32) -> Self {
        Self { v: v & Self::mask(w), x: 0, z: 0, w }
    }

    #[inline(always)]
    pub const fn lit4(v: u64, x: u64, z: u64, w: i32) -> Self {
        let m = Self::mask(w);
        let zc = z & m;
        let xc = x & m & !zc;
        let vc = v & m & !(xc | zc);
        Self { v: vc, x: xc, z: zc, w }
    }

    #[inline(always)]
    pub const fn unknown(w: i32) -> Self {
        Self { v: 0, x: Self::mask(w), z: 0, w }
    }
}

pub mod w64 {
    use super::{w2, W64};

    #[inline(always)]
    pub const fn to_u64(a: W64) -> Result<u64, &'static str> {
        if (a.x | a.z) != 0 {
            Err("integer conversion of X/Z port")
        } else {
            Ok(a.v)
        }
    }

    pub fn parse(bits: &str, width: i32) -> Result<W64, &'static str> {
        if bits.len() != width as usize {
            return Err("port binary width mismatch");
        }
        let (mut v, mut x, mut z) = (0u64, 0u64, 0u64);
        for b in bits.bytes() {
            v <<= 1;
            x <<= 1;
            z <<= 1;
            match b {
                b'0' => {}
                b'1' => v |= 1,
                b'x' | b'X' => x |= 1,
                b'z' | b'Z' => z |= 1,
                _ => return Err("invalid 4-state bit character"),
            }
        }
        Ok(W64 { v, x, z, w: width })
    }

    pub fn text(a: W64) -> String {
        let mut s = String::with_capacity(a.w as usize);
        for i in 0..a.w {
            let bit = (a.w - 1 - i) as u32;
            if ((a.z >> bit) & 1) != 0 {
                s.push('z');
            } else if ((a.x >> bit) & 1) != 0 {
                s.push('x');
            } else if ((a.v >> bit) & 1) != 0 {
                s.push('1');
            } else {
                s.push('0');
            }
        }
        s
    }

    pub fn format_display(fmt: &str, args: &[W64], newline: bool) -> String {
        let mut out = String::with_capacity(fmt.len() + 32);
        let bytes = fmt.as_bytes();
        let mut i = 0usize;
        let mut arg_idx = 0usize;
        while i < bytes.len() {
            if bytes[i] != b'%' || i + 1 >= bytes.len() {
                out.push(bytes[i] as char);
                i += 1;
                continue;
            }
            if bytes[i + 1] == b'%' {
                out.push('%');
                i += 2;
                continue;
            }
            let mut trim = false;
            if bytes[i + 1] == b'0' && i + 2 < bytes.len() {
                trim = true;
                i += 1;
            }
            i += 1;
            let spec = bytes[i] as char;
            i += 1;
            if arg_idx >= args.len() {
                out.push('%');
                out.push(spec);
                continue;
            }
            let a = args[arg_idx];
            arg_idx += 1;
            match spec {
                'h' | 'H' | 'x' | 'X' => {
                    if (a.x | a.z) != 0 {
                        out.push('x');
                    } else if trim {
                        out.push_str(&format!("{:x}", a.v));
                    } else {
                        let digits = ((a.w + 3) / 4) as usize;
                        out.push_str(&format!("{:0width$x}", a.v, width = digits));
                    }
                }
                'b' | 'B' => {
                    let s = text(a);
                    if trim {
                        let t = s.trim_start_matches('0');
                        out.push_str(if t.is_empty() { "0" } else { t });
                    } else {
                        out.push_str(&s);
                    }
                }
                'd' | 'D' | 'u' | 'U' => {
                    if (a.x | a.z) != 0 { out.push('x'); } else { out.push_str(&format!("{}", a.v)); }
                }
                _ => { out.push('%'); out.push(spec); }
            }
        }
        if newline { out.push('\n'); }
        out
    }

    #[inline(always)]
    pub fn step(budget: &mut usize) -> Result<(), &'static str> {
        if *budget == 0 {
            return Err("runtime loop execution budget exceeded");
        }
        *budget -= 1;
        Ok(())
    }

    #[inline(always)]
    pub fn repeat_count(a: W64, is_signed: bool) -> Result<usize, &'static str> {
        if (a.x | a.z) != 0 {
            return Ok(0);
        }
        if is_signed && a.w > 0 && ((a.v >> ((a.w - 1) as u32)) & 1) != 0 {
            return Ok(0);
        }
        if a.v > 20000 {
            return Err("runtime repeat count exceeds budget");
        }
        Ok(a.v as usize)
    }

    #[inline(always)]
    pub const fn resize(a: W64, w: i32) -> W64 {
        let m = W64::mask(w);
        W64 { v: a.v & m, x: a.x & m, z: a.z & m, w }
    }

    #[inline(always)]
    pub const fn two_state(a: W64) -> W64 {
        W64 { v: a.v & W64::mask(a.w), x: 0, z: 0, w: a.w }
    }

    #[inline(always)]
    pub const fn sresize(a: W64, w: i32) -> W64 {
        if w <= a.w {
            return resize(a, w);
        }
        let fill = W64::mask(w) & !W64::mask(a.w);
        let top = (a.w - 1) as u32;
        let sv = if ((a.v >> top) & 1) != 0 { fill } else { 0 };
        let sx = if ((a.x >> top) & 1) != 0 { fill } else { 0 };
        let sz = if ((a.z >> top) & 1) != 0 { fill } else { 0 };
        W64 { v: a.v | sv, x: a.x | sx, z: a.z | sz, w }
    }

    #[inline(always)]
    const fn logic_bit(a: W64) -> i32 {
        if ((a.x | a.z) & 1) != 0 { -1 } else { (a.v & 1) as i32 }
    }

    #[inline(always)]
    pub const fn edge(prev: W64, curr: W64, pos: bool) -> bool {
        let p = logic_bit(prev);
        let c = logic_bit(curr);
        if pos {
            (p == 0 && c != 0) || (p < 0 && c == 1)
        } else {
            (p == 1 && c != 1) || (p < 0 && c == 0)
        }
    }

    #[inline(always)]
    pub const fn truth(a: W64) -> i32 {
        if a.v != 0 { 1 } else if (a.x | a.z) != 0 { -1 } else { 0 }
    }

    #[inline(always)]
    pub const fn yes(a: W64) -> bool {
        a.v != 0
    }

    #[inline(always)]
    pub const fn lnot(a: W64) -> W64 {
        let t = truth(a);
        if t < 0 { W64 { v: 0, x: 1, z: 0, w: 1 } } else { W64 { v: if t == 0 { 1 } else { 0 }, x: 0, z: 0, w: 1 } }
    }

    #[inline(always)]
    pub const fn land(a: W64, b: W64) -> W64 {
        let (x, y) = (truth(a), truth(b));
        if x == 0 || y == 0 {
            W64 { v: 0, x: 0, z: 0, w: 1 }
        } else if x == 1 && y == 1 {
            W64 { v: 1, x: 0, z: 0, w: 1 }
        } else {
            W64 { v: 0, x: 1, z: 0, w: 1 }
        }
    }

    #[inline(always)]
    pub const fn lor(a: W64, b: W64) -> W64 {
        let (x, y) = (truth(a), truth(b));
        if x == 1 || y == 1 {
            W64 { v: 1, x: 0, z: 0, w: 1 }
        } else if x == 0 && y == 0 {
            W64 { v: 0, x: 0, z: 0, w: 1 }
        } else {
            W64 { v: 0, x: 1, z: 0, w: 1 }
        }
    }

    #[inline(always)]
    pub const fn bnot(a: W64, w: i32) -> W64 {
        let m = W64::mask(w);
        let u = (a.x | a.z) & m;
        W64 { v: (!a.v) & (!u) & m, x: u, z: 0, w }
    }

    #[inline(always)]
    pub const fn bbin(a: W64, b: W64, w: i32, op: i32) -> W64 {
        let m = W64::mask(w);
        let au = (a.x | a.z) & m;
        let bu = (b.x | b.z) & m;
        let av = a.v & !au & m;
        let bv = b.v & !bu & m;
        if op == 2 {
            let ou = au | bu;
            return W64 { v: (av ^ bv) & !ou, x: ou, z: 0, w };
        }
        let az = !(av | au);
        let bz = !(bv | bu);
        if op == 0 {
            let ov = av & bv;
            W64 { v: ov, x: !(az | bz | ov) & m, z: 0, w }
        } else {
            let ov = av | bv;
            W64 { v: ov, x: !(ov | (az & bz)) & m, z: 0, w }
        }
    }

    #[inline(always)]
    pub const fn exact(a: W64, b: W64) -> bool {
        a.v == b.v && a.x == b.x && a.z == b.z
    }

    #[inline(always)]
    pub const fn casez_match(a: W64, b: W64) -> bool {
        let w = if a.w > b.w { a.w } else { b.w };
        let care = !(a.z | b.z) & W64::mask(w);
        (((a.v ^ b.v) | (a.x ^ b.x)) & care) == 0
    }

    #[inline(always)]
    pub const fn casex_match(a: W64, b: W64) -> bool {
        let w = if a.w > b.w { a.w } else { b.w };
        let care = !(a.x | a.z | b.x | b.z) & W64::mask(w);
        ((a.v ^ b.v) & care) == 0
    }

    #[inline(always)]
    pub const fn wildeq(a: W64, b: W64) -> W64 {
        let w = if a.w > b.w { a.w } else { b.w };
        let care = !(b.x | b.z) & W64::mask(w);
        let au = (a.x | a.z) & care;
        let known = care & !au;
        if ((a.v ^ b.v) & known) != 0 {
            W64 { v: 0, x: 0, z: 0, w: 1 }
        } else if au != 0 {
            W64 { v: 0, x: 1, z: 0, w: 1 }
        } else {
            W64 { v: 1, x: 0, z: 0, w: 1 }
        }
    }

    #[inline(always)]
    pub const fn wildneq(a: W64, b: W64) -> W64 {
        lnot(wildeq(a, b))
    }

    #[inline(always)]
    pub const fn countones(a: W64) -> W64 {
        let ones = a.v & !a.x & !a.z & W64::mask(a.w);
        W64 { v: ones.count_ones() as u64, x: 0, z: 0, w: 32 }
    }

    #[inline(always)]
    pub const fn onehot(a: W64) -> W64 {
        W64 { v: if countones(a).v == 1 { 1 } else { 0 }, x: 0, z: 0, w: 1 }
    }

    #[inline(always)]
    pub const fn onehot0(a: W64) -> W64 {
        W64 { v: if countones(a).v <= 1 { 1 } else { 0 }, x: 0, z: 0, w: 1 }
    }

    #[inline(always)]
    pub const fn isunknown(a: W64) -> W64 {
        W64 { v: if ((a.x | a.z) & W64::mask(a.w)) != 0 { 1 } else { 0 }, x: 0, z: 0, w: 1 }
    }

    #[inline(always)]
    pub const fn clog2(a: W64) -> W64 {
        let m = W64::mask(a.w);
        if ((a.x | a.z) & m) != 0 { return W64 { v: 0, x: 0, z: 0, w: 32 }; }
        let x = a.v & m;
        if x <= 1 { return W64 { v: 0, x: 0, z: 0, w: 32 }; }
        W64 { v: (64 - (x - 1).leading_zeros()) as u64, x: 0, z: 0, w: 32 }
    }

    #[inline(always)]
    pub const fn eq(a: W64, b: W64) -> W64 {
        if ((a.x | a.z) | (b.x | b.z)) != 0 {
            W64 { v: 0, x: 1, z: 0, w: 1 }
        } else {
            W64 { v: if a.v == b.v { 1 } else { 0 }, x: 0, z: 0, w: 1 }
        }
    }

    #[inline(always)]
    pub const fn cmp(a: W64, b: W64, op: i32) -> W64 {
        if ((a.x | a.z) | (b.x | b.z)) != 0 {
            return W64 { v: 0, x: 1, z: 0, w: 1 };
        }
        let r = match op { 0 => a.v < b.v, 1 => a.v <= b.v, 2 => a.v > b.v, _ => a.v >= b.v };
        W64 { v: if r { 1 } else { 0 }, x: 0, z: 0, w: 1 }
    }

    #[inline(always)]
    pub const fn scmp(a: W64, b: W64, op: i32) -> W64 {
        if ((a.x | a.z) | (b.x | b.z)) != 0 {
            return W64 { v: 0, x: 1, z: 0, w: 1 };
        }
        let sa = w2::sext(a.v, a.w);
        let sb = w2::sext(b.v, b.w);
        let r = match op { 0 => sa < sb, 1 => sa <= sb, 2 => sa > sb, _ => sa >= sb };
        W64 { v: if r { 1 } else { 0 }, x: 0, z: 0, w: 1 }
    }

    #[inline(always)]
    pub const fn arith(a: W64, b: W64, w: i32, sub: bool) -> W64 {
        let m = W64::mask(w);
        if ((a.x | a.z) | (b.x | b.z)) != 0 {
            return W64 { v: 0, x: m, z: 0, w };
        }
        let r = if sub { a.v.wrapping_sub(b.v) } else { a.v.wrapping_add(b.v) } & m;
        W64 { v: r, x: 0, z: 0, w }
    }

    #[inline(always)]
    pub const fn shift(a: W64, b: W64, w: i32, right: bool) -> W64 {
        let m = W64::mask(w);
        if (b.x | b.z) != 0 {
            return W64 { v: 0, x: m, z: 0, w };
        }
        if b.v >= 64 {
            return W64 { v: 0, x: 0, z: 0, w };
        }
        let sh = b.v as u32;
        if right {
            W64 { v: (a.v & m) >> sh, x: (a.x & m) >> sh, z: (a.z & m) >> sh, w }
        } else {
            W64 { v: (a.v << sh) & m, x: (a.x << sh) & m, z: (a.z << sh) & m, w }
        }
    }

    #[inline(always)]
    pub const fn sshift(a: W64, b: W64, w: i32) -> W64 {
        let m = W64::mask(w);
        if (b.x | b.z) != 0 {
            return W64 { v: 0, x: m, z: 0, w };
        }
        let top = (w - 1) as u32;
        let sv = (a.v >> top) & 1;
        let sx = (a.x >> top) & 1;
        let sz = (a.z >> top) & 1;
        if b.v >= 64 {
            return W64 { v: if sv != 0 { m } else { 0 }, x: if sx != 0 { m } else { 0 }, z: if sz != 0 { m } else { 0 }, w };
        }
        let sh = b.v as u32;
        let fill = if sh == 0 { 0 } else { m & !(m >> sh) };
        W64 {
            v: (((a.v & m) >> sh) | if sv != 0 { fill } else { 0 }) & m,
            x: (((a.x & m) >> sh) | if sx != 0 { fill } else { 0 }) & m,
            z: (((a.z & m) >> sh) | if sz != 0 { fill } else { 0 }) & m,
            w,
        }
    }

    #[inline(always)]
    pub const fn red(a: W64, op: i32) -> W64 {
        let m = W64::mask(a.w);
        let u = (a.x | a.z) & m;
        let v = a.v & !u & m;
        if op == 0 || op == 1 {
            let zeros = (!(v | u)) & m;
            if zeros != 0 { return W64 { v: if op == 1 { 1 } else { 0 }, x: 0, z: 0, w: 1 }; }
            if u != 0 { return W64 { v: 0, x: 1, z: 0, w: 1 }; }
            return W64 { v: if op == 0 { 1 } else { 0 }, x: 0, z: 0, w: 1 };
        }
        if op == 2 || op == 3 {
            if v != 0 { return W64 { v: if op == 2 { 1 } else { 0 }, x: 0, z: 0, w: 1 }; }
            if u != 0 { return W64 { v: 0, x: 1, z: 0, w: 1 }; }
            return W64 { v: if op == 3 { 1 } else { 0 }, x: 0, z: 0, w: 1 };
        }
        if u != 0 {
            return W64 { v: 0, x: 1, z: 0, w: 1 };
        }
        let odd = (v.count_ones() & 1) != 0;
        W64 { v: if odd ^ (op == 5) { 1 } else { 0 }, x: 0, z: 0, w: 1 }
    }

    #[inline(always)]
    pub const fn product(a: W64, b: W64, w: i32, op: i32) -> W64 {
        let m = W64::mask(w);
        if ((a.x | a.z) | (b.x | b.z)) != 0 {
            return W64 { v: 0, x: m, z: 0, w };
        }
        let av = a.v & m;
        let bv = b.v & m;
        if op == 0 {
            return W64 { v: av.wrapping_mul(bv) & m, x: 0, z: 0, w };
        }
        if bv == 0 {
            return W64 { v: 0, x: m, z: 0, w };
        }
        W64 { v: (if op == 1 { av / bv } else { av % bv }) & m, x: 0, z: 0, w }
    }

    #[inline(always)]
    pub const fn sproduct(a: W64, b: W64, w: i32, op: i32) -> W64 {
        let m = W64::mask(w);
        if ((a.x | a.z) | (b.x | b.z)) != 0 {
            return W64 { v: 0, x: m, z: 0, w };
        }
        let sa = w2::sext(a.v, w);
        let sb = w2::sext(b.v, w);
        if op == 0 {
            return W64 { v: (sa as u64).wrapping_mul(sb as u64) & m, x: 0, z: 0, w };
        }
        if sb == 0 {
            return W64 { v: 0, x: m, z: 0, w };
        }
        if sa == i64::MIN && sb == -1 {
            return W64 { v: if op == 1 { (sa as u64) & m } else { 0 }, x: 0, z: 0, w };
        }
        W64 { v: ((if op == 1 { sa / sb } else { sa % sb }) as u64) & m, x: 0, z: 0, w }
    }

    #[inline(always)]
    pub const fn cond(c: W64, a: W64, b: W64, w: i32) -> W64 {
        let t = truth(c);
        if t == 1 { return resize(a, w); }
        if t == 0 { return resize(b, w); }
        let m = W64::mask(w);
        let au = (a.x | a.z) & m;
        let bu = (b.x | b.z) & m;
        let av = a.v & !au & m;
        let bv = b.v & !bu & m;
        let u = (au | bu | (av ^ bv)) & m;
        W64 { v: av & !u, x: u, z: 0, w }
    }

    #[inline(always)]
    pub const fn concat(a: W64, b: W64) -> W64 {
        let bw = b.w;
        let bm = W64::mask(bw);
        if bw >= 64 {
            return W64 { v: b.v & bm, x: b.x & bm, z: b.z & bm, w: a.w + bw };
        }
        let s = bw as u32;
        W64 { v: (a.v << s) | (b.v & bm), x: (a.x << s) | (b.x & bm), z: (a.z << s) | (b.z & bm), w: a.w + bw }
    }

    #[inline(always)]
    pub const fn selbit(a: W64, idx: W64) -> W64 {
        if (idx.x | idx.z) != 0 || idx.v >= a.w as u64 {
            return W64 { v: 0, x: 1, z: 0, w: 1 };
        }
        let s = idx.v as u32;
        W64 { v: (a.v >> s) & 1, x: (a.x >> s) & 1, z: (a.z >> s) & 1, w: 1 }
    }

    #[inline(always)]
    pub const fn selpart(a: W64, left: i32, right: i32) -> W64 {
        let n = left - right + 1;
        let m = W64::mask(n);
        if right >= a.w {
            return W64 { v: 0, x: m, z: 0, w: n };
        }
        let avail = a.w - right;
        let am = W64::mask(if avail < n { avail } else { n });
        let s = right as u32;
        W64 { v: (a.v >> s) & am, x: ((a.x >> s) & am) | (m & !am), z: (a.z >> s) & am, w: n }
    }

    pub fn selidx(a: W64, b: W64, width: i32, desc: bool) -> Result<W64, &'static str> {
        if (b.x | b.z) != 0 {
            return Ok(W64::unknown(width));
        }
        if b.v > 2147483647 {
            return Err("known indexed select base exceeds qualified range 0..INT32_MAX");
        }
        let lo: i64 = if desc { (b.v as i64) - (width as i64 - 1) } else { b.v as i64 };
        let src_lo = lo.max(0);
        let src_hi = (lo + width as i64).min(a.w as i64);
        if src_lo >= src_hi {
            return Ok(W64::unknown(width));
        }
        let count = (src_hi - src_lo) as i32;
        let dst_lo = (src_lo - lo) as u32;
        let s_lo = src_lo as u32;
        let cm = W64::mask(count);
        let dst_mask = cm << dst_lo;
        Ok(W64 {
            v: ((a.v >> s_lo) & cm) << dst_lo,
            x: (((a.x >> s_lo) & cm) << dst_lo) | (W64::mask(width) & !dst_mask),
            z: ((a.z >> s_lo) & cm) << dst_lo,
            w: width,
        })
    }

    pub fn depidx(before: W64, b: W64, width: i32, desc: bool, value: W64) -> Result<W64, &'static str> {
        if (b.x | b.z) != 0 {
            return Ok(before);
        }
        if b.v > 2147483647 {
            return Err("known selected write base exceeds qualified range 0..INT32_MAX");
        }
        let val = resize(value, width);
        let lo: i64 = if desc { (b.v as i64) - (width as i64 - 1) } else { b.v as i64 };
        let dst_lo = lo.max(0);
        let dst_hi = (lo + width as i64).min(before.w as i64);
        if dst_lo >= dst_hi {
            return Ok(before);
        }
        let count = (dst_hi - dst_lo) as i32;
        let src_lo = (dst_lo - lo) as u32;
        let d_lo = dst_lo as u32;
        let cm = W64::mask(count);
        let dmask = cm << d_lo;
        Ok(W64 {
            v: (before.v & !dmask) | (((val.v >> src_lo) & cm) << d_lo),
            x: (before.x & !dmask) | (((val.x >> src_lo) & cm) << d_lo),
            z: (before.z & !dmask) | (((val.z >> src_lo) & cm) << d_lo),
            w: before.w,
        })
    }

    #[inline(always)]
    pub const fn memidx(index: W64, lower: i32) -> W64 {
        if (index.x | index.z) != 0 || index.v < lower as u64 {
            W64 { v: 0, x: 1, z: 0, w: 1 }
        } else {
            W64::lit(index.v - lower as u64, index.w)
        }
    }

    #[inline(always)]
    pub fn read(m: &[W64], idx: W64, w: i32) -> W64 {
        if (idx.x | idx.z) != 0 || idx.v >= m.len() as u64 {
            W64::unknown(w)
        } else {
            m[idx.v as usize]
        }
    }
}

pub mod w2 {
    use super::W64;

    #[inline(always)]
    pub const fn countones(a: u64, w: i32) -> u64 {
        (a & W64::mask(w)).count_ones() as u64
    }

    #[inline(always)]
    pub const fn onehot(a: u64, w: i32) -> u64 {
        if countones(a, w) == 1 { 1 } else { 0 }
    }

    #[inline(always)]
    pub const fn onehot0(a: u64, w: i32) -> u64 {
        if countones(a, w) <= 1 { 1 } else { 0 }
    }

    #[inline(always)]
    pub const fn clog2(a: u64, w: i32) -> u64 {
        let x = a & W64::mask(w);
        if x <= 1 { return 0; }
        (64 - (x - 1).leading_zeros()) as u64
    }

    #[inline(always)]
    pub const fn sext(a: u64, w: i32) -> i64 {
        let m = W64::mask(w);
        let mut v = a & m;
        if w < 64 && (v & (1u64 << ((w - 1) as u32))) != 0 {
            v |= !m;
        }
        v as i64
    }

    #[inline(always)]
    pub const fn resize(a: u64, w: i32) -> u64 { a & W64::mask(w) }

    #[inline(always)]
    pub const fn sresize(a: u64, aw: i32, w: i32) -> u64 { (sext(a, aw) as u64) & W64::mask(w) }

    #[inline(always)]
    pub const fn edge(prev: u64, curr: u64, pos: bool) -> bool {
        if pos { ((prev & 1) == 0) && ((curr & 1) != 0) } else { ((prev & 1) != 0) && ((curr & 1) == 0) }
    }

    #[inline(always)]
    pub const fn yes(a: u64) -> bool { a != 0 }

    #[inline(always)]
    pub const fn lnot(a: u64) -> u64 { if a == 0 { 1 } else { 0 } }

    #[inline(always)]
    pub const fn land(a: u64, b: u64) -> u64 { if a != 0 && b != 0 { 1 } else { 0 } }

    #[inline(always)]
    pub const fn lor(a: u64, b: u64) -> u64 { if a != 0 || b != 0 { 1 } else { 0 } }

    #[inline(always)]
    pub const fn bnot(a: u64, w: i32) -> u64 { (!a) & W64::mask(w) }

    #[inline(always)]
    pub const fn bbin(a: u64, b: u64, w: i32, op: i32) -> u64 {
        (match op { 0 => a & b, 1 => a | b, _ => a ^ b }) & W64::mask(w)
    }

    #[inline(always)]
    pub const fn eq(a: u64, b: u64) -> u64 { if a == b { 1 } else { 0 } }

    #[inline(always)]
    pub const fn cmp(a: u64, b: u64, op: i32) -> u64 {
        let r = match op { 0 => a < b, 1 => a <= b, 2 => a > b, _ => a >= b };
        if r { 1 } else { 0 }
    }

    #[inline(always)]
    pub const fn scmp(a: u64, b: u64, w: i32, op: i32) -> u64 {
        let sa = sext(a, w);
        let sb = sext(b, w);
        let r = match op { 0 => sa < sb, 1 => sa <= sb, 2 => sa > sb, _ => sa >= sb };
        if r { 1 } else { 0 }
    }

    #[inline(always)]
    pub const fn arith(a: u64, b: u64, w: i32, sub: bool) -> u64 {
        (if sub { a.wrapping_sub(b) } else { a.wrapping_add(b) }) & W64::mask(w)
    }

    #[inline(always)]
    pub const fn shift(a: u64, b: u64, w: i32, right: bool) -> u64 {
        if b >= 64 { return 0; }
        let m = W64::mask(w);
        let sh = b as u32;
        (if right { (a & m) >> sh } else { a << sh }) & m
    }

    #[inline(always)]
    pub const fn sshift(a: u64, b: u64, w: i32) -> u64 {
        let m = W64::mask(w);
        let sa = sext(a, w);
        if b >= 64 { return if sa < 0 { m } else { 0 }; }
        ((sa >> (b as u32)) as u64) & m
    }

    #[inline(always)]
    pub const fn red(a: u64, w: i32, op: i32) -> u64 {
        let m = W64::mask(w);
        let v = a & m;
        if op == 0 || op == 1 { return if (v == m) ^ (op == 1) { 1 } else { 0 }; }
        if op == 2 || op == 3 { return if (v != 0) ^ (op == 3) { 1 } else { 0 }; }
        let odd = (v.count_ones() & 1) != 0;
        if odd ^ (op == 5) { 1 } else { 0 }
    }

    #[inline(always)]
    pub const fn product(a: u64, b: u64, w: i32, op: i32) -> u64 {
        let m = W64::mask(w);
        let av = a & m;
        let bv = b & m;
        if op == 0 { return av.wrapping_mul(bv) & m; }
        if bv == 0 { return 0; }
        (if op == 1 { av / bv } else { av % bv }) & m
    }

    #[inline(always)]
    pub const fn sproduct(a: u64, b: u64, w: i32, op: i32) -> u64 {
        let m = W64::mask(w);
        let sa = sext(a, w);
        let sb = sext(b, w);
        if op == 0 { return (sa as u64).wrapping_mul(sb as u64) & m; }
        if sb == 0 { return 0; }
        if sa == i64::MIN && sb == -1 { return if op == 1 { (sa as u64) & m } else { 0 }; }
        ((if op == 1 { sa / sb } else { sa % sb }) as u64) & m
    }

    #[inline(always)]
    pub const fn cond(c: u64, t: u64, f: u64, w: i32) -> u64 {
        (if c != 0 { t } else { f }) & W64::mask(w)
    }

    #[inline(always)]
    pub const fn concat(a: u64, b: u64, bw: i32) -> u64 {
        (if bw >= 64 { 0 } else { a << (bw as u32) }) | (b & W64::mask(bw))
    }

    #[inline(always)]
    pub const fn selbit(a: u64, aw: i32, idx: u64) -> u64 {
        if idx < aw as u64 { (a >> (idx as u32)) & 1 } else { 0 }
    }

    #[inline(always)]
    pub const fn selpart(a: u64, aw: i32, left: i32, right: i32) -> u64 {
        if right >= aw { 0 } else { ((a & W64::mask(aw)) >> (right as u32)) & W64::mask(left - right + 1) }
    }

    pub fn selidx(a: u64, aw: i32, b: u64, width: i32, desc: bool) -> Result<u64, &'static str> {
        if b > 2147483647 { return Err("known indexed select base exceeds qualified range 0..INT32_MAX"); }
        let lo: i64 = if desc { (b as i64) - (width as i64 - 1) } else { b as i64 };
        let src_lo = lo.max(0);
        let src_hi = (lo + width as i64).min(aw as i64);
        if src_lo >= src_hi { return Ok(0); }
        let count = (src_hi - src_lo) as i32;
        let dst_lo = (src_lo - lo) as u32;
        let cm = W64::mask(count);
        Ok((((a & W64::mask(aw)) >> (src_lo as u32)) & cm) << dst_lo)
    }

    pub fn depidx(before: u64, tw: i32, b: u64, width: i32, desc: bool, value: u64) -> Result<u64, &'static str> {
        if b > 2147483647 { return Err("known selected write base exceeds qualified range 0..INT32_MAX"); }
        let val = value & W64::mask(width);
        let lo: i64 = if desc { (b as i64) - (width as i64 - 1) } else { b as i64 };
        let dst_lo = lo.max(0);
        let dst_hi = (lo + width as i64).min(tw as i64);
        if dst_lo >= dst_hi { return Ok(before); }
        let count = (dst_hi - dst_lo) as i32;
        let src_lo = (dst_lo - lo) as u32;
        let d_lo = dst_lo as u32;
        let cm = W64::mask(count);
        let dmask = cm << d_lo;
        Ok((before & !dmask) | (((val >> src_lo) & cm) << d_lo))
    }

    #[inline(always)]
    pub const fn memidx(index: u64, lower: i32) -> u64 {
        if index >= lower as u64 { index - lower as u64 } else { u64::MAX }
    }

    #[inline(always)]
    pub fn read(m: &[u64], idx: u64) -> u64 {
        if idx < m.len() as u64 { m[idx as usize] } else { 0 }
    }

    #[inline(always)]
    pub fn repeat_count(a: u64, w: i32, is_signed: bool) -> Result<usize, &'static str> {
        if is_signed && w > 0 && ((a >> ((w - 1) as u32)) & 1) != 0 { return Ok(0); }
        if a > 20000 { return Err("runtime repeat count exceeds budget"); }
        Ok(a as usize)
    }
}

#[derive(Copy, Clone)]
pub struct MemWrite64 {
    pub mem_id: u16,
    pub index: u32,
    pub value: W64,
}

#[derive(Clone)]
pub struct MemLog64 {
    pub buf: [MemWrite64; 16],
    pub overflow: Vec<MemWrite64>,
    pub count: usize,
}

impl MemLog64 {
    pub const fn new() -> Self {
        Self {
            buf: [MemWrite64 { mem_id: 0, index: 0, value: W64 { v: 0, x: 0, z: 0, w: 1 } }; 16],
            overflow: Vec::new(),
            count: 0,
        }
    }

    #[inline(always)]
    pub fn push(&mut self, mem_id: u16, mem_len: usize, idx: W64, val: W64, w: i32) {
        if (idx.x | idx.z) != 0 || idx.v >= mem_len as u64 { return; }
        let e = MemWrite64 { mem_id, index: idx.v as u32, value: w64::resize(val, w) };
        if self.count < 16 {
            self.buf[self.count] = e;
            self.count += 1;
        } else {
            self.overflow.push(e);
        }
    }
}

#[derive(Copy, Clone)]
pub struct MemWrite2 {
    pub mem_id: u16,
    pub index: u32,
    pub value: u64,
}

#[derive(Clone)]
pub struct MemLog2 {
    pub buf: [MemWrite2; 16],
    pub overflow: Vec<MemWrite2>,
    pub count: usize,
}

impl MemLog2 {
    pub const fn new() -> Self {
        Self {
            buf: [MemWrite2 { mem_id: 0, index: 0, value: 0 }; 16],
            overflow: Vec::new(),
            count: 0,
        }
    }

    #[inline(always)]
    pub fn push(&mut self, mem_id: u16, mem_len: usize, idx: u64, val: u64, w: i32) {
        if idx >= mem_len as u64 { return; }
        let e = MemWrite2 { mem_id, index: idx as u32, value: val & W64::mask(w) };
        if self.count < 16 {
            self.buf[self.count] = e;
            self.count += 1;
        } else {
            self.overflow.push(e);
        }
    }
}

pub struct VcdWriter {
    out: std::io::BufWriter<std::fs::File>,
    codes: Vec<String>,
    last: Vec<W64>,
    initial: bool,
}

impl VcdWriter {
    fn make_code(mut index: usize) -> String {
        let mut s = String::new();
        loop {
            s.push((33u8 + (index % 94) as u8) as char);
            index /= 94;
            if index == 0 { break; }
        }
        s
    }

    fn write_val(out: &mut std::io::BufWriter<std::fs::File>, val: W64, code: &str) -> std::io::Result<()> {
        use std::io::Write;
        if val.w == 1 {
            let ch = if (val.z & 1) != 0 { 'z' } else if (val.x & 1) != 0 { 'x' } else if (val.v & 1) != 0 { '1' } else { '0' };
            writeln!(out, "{}{}", ch, code)
        } else {
            writeln!(out, "b{} {}", w64::text(val), code)
        }
    }

    pub fn open(path: &str, top_name: &str, signals: &[(&str, i32)], timescale: &str) -> std::io::Result<Self> {
        use std::io::Write;
        let file = std::fs::File::create(path)?;
        let mut out = std::io::BufWriter::new(file);
        writeln!(out, "$version VeloSim $end")?;
        writeln!(out, "$timescale {} $end", timescale)?;
        writeln!(out, "$scope module {} $end", if top_name.is_empty() { "top" } else { top_name })?;
        let mut codes = Vec::with_capacity(signals.len());
        let mut last = Vec::with_capacity(signals.len());
        for (i, (name, width)) in signals.iter().enumerate() {
            let code = Self::make_code(i);
            let leaf = name.replace('.', "_");
            if *width > 1 {
                writeln!(out, "$var wire {} {} {} [{}:0] $end", width, code, leaf, width - 1)?;
            } else {
                writeln!(out, "$var wire 1 {} {} $end", code, leaf)?;
            }
            codes.push(code);
            last.push(W64::unknown(*width));
        }
        writeln!(out, "$upscope $end")?;
        writeln!(out, "$enddefinitions $end")?;
        Ok(Self { out, codes, last, initial: false })
    }

    pub fn dump(&mut self, time: u64, values: &[W64]) -> std::io::Result<()> {
        use std::io::Write;
        if !self.initial {
            writeln!(self.out, "#{}\n$dumpvars", time)?;
            for i in 0..self.codes.len().min(values.len()) {
                self.last[i] = values[i];
                Self::write_val(&mut self.out, values[i], &self.codes[i])?;
            }
            writeln!(self.out, "$end")?;
            self.initial = true;
            return Ok(());
        }
        let mut wrote_time = false;
        for i in 0..self.codes.len().min(values.len()) {
            if w64::exact(values[i], self.last[i]) { continue; }
            if !wrote_time {
                writeln!(self.out, "#{}", time)?;
                wrote_time = true;
            }
            self.last[i] = values[i];
            Self::write_val(&mut self.out, values[i], &self.codes[i])?;
        }
        Ok(())
    }

    pub fn flush(&mut self) -> std::io::Result<()> {
        use std::io::Write;
        self.out.flush()
    }
}

pub struct VcdSlot(Option<VcdWriter>);

impl Clone for VcdSlot {
    fn clone(&self) -> Self { Self(None) }
}

impl VcdSlot {
    pub const fn new() -> Self { Self(None) }
    pub fn take(&mut self) -> Self { Self(self.0.take()) }
    pub fn open(&mut self, path: &str, top_name: &str, signals: &[(&str, i32)], timescale: &str) -> std::io::Result<()> {
        self.close();
        self.0 = Some(VcdWriter::open(path, top_name, signals, timescale)?);
        Ok(())
    }
    pub fn dump(&mut self, time: u64, values: &[W64]) -> std::io::Result<()> {
        if let Some(w) = self.0.as_mut() { w.dump(time, values)?; }
        Ok(())
    }
    pub fn close(&mut self) {
        if let Some(mut w) = self.0.take() { let _ = w.flush(); }
    }
}

#[derive(Clone)]
pub struct PlusargStore {
    args: Vec<String>,
}

impl PlusargStore {
    pub fn new() -> Self { Self { args: Vec::new() } }
    pub fn set<I, S>(&mut self, args: I) where I: IntoIterator<Item = S>, S: AsRef<str> {
        self.args.clear();
        for a in args { self.add(a.as_ref()); }
    }
    pub fn add(&mut self, arg: &str) {
        self.args.push(arg.strip_prefix('+').unwrap_or(arg).to_string());
    }
    pub fn test(&self, prefix: &str) -> u32 {
        let p = prefix.strip_prefix('+').unwrap_or(prefix);
        if self.args.iter().any(|a| a.starts_with(p)) { 1 } else { 0 }
    }
    pub fn value(&self, spec: &str, out: &mut W64) -> u32 {
        let s = spec.strip_prefix('+').unwrap_or(spec);
        let pct = match s.find('%') { Some(i) if i + 1 < s.len() => i, _ => return 0 };
        let prefix = &s[..pct];
        let rest = &s[pct + 1..];
        let fmt_ch = match rest.as_bytes() {
            [b'0', c, ..] => (*c as char).to_ascii_lowercase(),
            [c, ..] => (*c as char).to_ascii_lowercase(),
            _ => return 0,
        };
        for a in &self.args {
            if let Some(raw) = a.strip_prefix(prefix) {
                let clean: String = raw.chars().filter(|&c| c != '_').collect();
                if clean.is_empty() { continue; }
                if let Some(parsed) = Self::parse_val(&clean, fmt_ch, out.w) {
                    *out = parsed;
                    return 1;
                }
            }
        }
        0
    }
    fn parse_val(s: &str, fmt: char, width: i32) -> Option<W64> {
        let m = W64::mask(width);
        if fmt == 'd' || fmt == 'u' {
            let (neg, digits) = if fmt == 'd' && s.starts_with('-') { (true, &s[1..]) }
                else if let Some(d) = s.strip_prefix('+') { (false, d) }
                else { (false, s) };
            let mut v = digits.parse::<u64>().ok()?;
            if neg { v = v.wrapping_neg(); }
            return Some(W64 { v: v & m, x: 0, z: 0, w: width });
        }
        let shift: u32 = match fmt { 'h' | 'x' => 4, 'o' => 3, 'b' => 1, _ => return None };
        let nib = (1u64 << shift) - 1;
        let (mut vv, mut xx, mut zz) = (0u64, 0u64, 0u64);
        for raw in s.chars() {
            let c = raw.to_ascii_lowercase();
            vv = vv.wrapping_shl(shift);
            xx = xx.wrapping_shl(shift);
            zz = zz.wrapping_shl(shift);
            if c == 'x' { xx |= nib; }
            else if c == 'z' { zz |= nib; }
            else {
                let d = c.to_digit(1u32 << shift)?;
                vv |= d as u64;
            }
        }
        let zv = zz & m;
        let xv = xx & !zv & m;
        Some(W64 { v: vv & !(xv | zv) & m, x: xv, z: zv, w: width })
    }
}

#[derive(Clone, Copy)]
pub struct SplitMix64Rng {
    pub state: u64,
}

impl SplitMix64Rng {
    pub const fn new() -> Self { Self { state: 0x9E3779B97F4A7C15u64 } }
    #[inline(always)]
    pub fn next_u32(&mut self) -> u32 {
        self.state = self.state.wrapping_add(0x9E3779B97F4A7C15u64);
        let mut z = self.state;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58476D1CE4E5B9u64);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D049BB133111EBu64);
        z ^= z >> 31;
        z as u32
    }
    #[inline(always)]
    pub fn next_range(&mut self, mut max_val: u32, mut min_val: u32) -> u32 {
        if max_val < min_val { std::mem::swap(&mut max_val, &mut min_val); }
        let span = (max_val as u64) - (min_val as u64) + 1u64;
        let r = self.next_u32();
        if span >= 0x100000000u64 { return r; }
        min_val + ((r as u64) % span) as u32
    }
}

pub mod checkpoint {
    use super::W64;
    use std::io::{Error, ErrorKind, Result};

    const MAGIC: u32 = 0x31525356; // "VSR1"
    const VERSION: u32 = 1;

    fn fnv_mix(mut h: u64, v: u64) -> u64 {
        for b in v.to_le_bytes() {
            h ^= b as u64;
            h = h.wrapping_mul(1099511628211u64);
        }
        h
    }
    fn fnv_bytes(data: &[u8]) -> u64 {
        let mut h: u64 = 14695981039346656037u64;
        for &b in data {
            h ^= b as u64;
            h = h.wrapping_mul(1099511628211u64);
        }
        h
    }
    pub fn schema_hash(top: &str, two_state: bool, signals: &[(&str, i32)], memories: &[(usize, i32)]) -> u64 {
        let mut h: u64 = 14695981039346656037u64;
        for b in top.bytes() { h = fnv_mix(h, b as u64); }
        h = fnv_mix(h, if two_state { 2 } else { 4 });
        h = fnv_mix(h, signals.len() as u64);
        for &(name, width) in signals {
            for b in name.bytes() { h = fnv_mix(h, b as u64); }
            h = fnv_mix(h, width as u64);
        }
        h = fnv_mix(h, memories.len() as u64);
        for &(depth, width) in memories {
            h = fnv_mix(h, depth as u64);
            h = fnv_mix(h, width as u64);
        }
        h
    }
    fn push_w64(buf: &mut Vec<u8>, w: W64) {
        buf.extend_from_slice(&w.v.to_le_bytes());
        buf.extend_from_slice(&w.x.to_le_bytes());
        buf.extend_from_slice(&w.z.to_le_bytes());
        buf.extend_from_slice(&w.w.to_le_bytes());
    }
    fn read_u32(data: &[u8], off: &mut usize) -> Result<u32> {
        if *off + 4 > data.len() { return Err(Error::new(ErrorKind::InvalidData, "truncated checkpoint")); }
        let v = u32::from_le_bytes([data[*off], data[*off+1], data[*off+2], data[*off+3]]);
        *off += 4;
        Ok(v)
    }
    fn read_i32(data: &[u8], off: &mut usize) -> Result<i32> {
        Ok(read_u32(data, off)? as i32)
    }
    fn read_u64(data: &[u8], off: &mut usize) -> Result<u64> {
        if *off + 8 > data.len() { return Err(Error::new(ErrorKind::InvalidData, "truncated checkpoint")); }
        let v = u64::from_le_bytes([data[*off], data[*off+1], data[*off+2], data[*off+3], data[*off+4], data[*off+5], data[*off+6], data[*off+7]]);
        *off += 8;
        Ok(v)
    }
    fn read_w64(data: &[u8], off: &mut usize) -> Result<W64> {
        let v = read_u64(data, off)?;
        let x = read_u64(data, off)?;
        let z = read_u64(data, off)?;
        let w = read_i32(data, off)?;
        Ok(W64 { v, x, z, w })
    }
    pub fn save(path: &str, schema: u64, time: u64, rng_state: u64, sigs: &[W64], prevs: &[W64], mems: &[Vec<W64>]) -> Result<()> {
        let mut payload = Vec::new();
        payload.extend_from_slice(&time.to_le_bytes());
        payload.extend_from_slice(&rng_state.to_le_bytes());
        payload.extend_from_slice(&(sigs.len() as u32).to_le_bytes());
        for &w in sigs { push_w64(&mut payload, w); }
        payload.extend_from_slice(&(prevs.len() as u32).to_le_bytes());
        for &w in prevs { push_w64(&mut payload, w); }
        payload.extend_from_slice(&(mems.len() as u32).to_le_bytes());
        for m in mems {
            payload.extend_from_slice(&(m.len() as u32).to_le_bytes());
            for &w in m { push_w64(&mut payload, w); }
        }
        let csum = fnv_bytes(&payload);
        let mut file = Vec::with_capacity(32 + payload.len());
        file.extend_from_slice(&MAGIC.to_le_bytes());
        file.extend_from_slice(&VERSION.to_le_bytes());
        file.extend_from_slice(&schema.to_le_bytes());
        file.extend_from_slice(&(payload.len() as u64).to_le_bytes());
        file.extend_from_slice(&csum.to_le_bytes());
        file.extend_from_slice(&payload);
        std::fs::write(path, file)
    }
    pub fn load(path: &str, expected_schema: u64, expected_sigs: usize, expected_prevs: usize, expected_mem_depths: &[usize]) -> Result<(u64, u64, Vec<W64>, Vec<W64>, Vec<Vec<W64>>)> {
        let raw = std::fs::read(path)?;
        if raw.len() < 32 { return Err(Error::new(ErrorKind::InvalidData, "checkpoint file truncated")); }
        let mut off = 0usize;
        if read_u32(&raw, &mut off)? != MAGIC { return Err(Error::new(ErrorKind::InvalidData, "invalid checkpoint magic")); }
        if read_u32(&raw, &mut off)? != VERSION { return Err(Error::new(ErrorKind::InvalidData, "unsupported checkpoint version")); }
        if read_u64(&raw, &mut off)? != expected_schema { return Err(Error::new(ErrorKind::InvalidData, "checkpoint schema mismatch")); }
        let payload_len = read_u64(&raw, &mut off)? as usize;
        let expected_csum = read_u64(&raw, &mut off)?;
        if payload_len != raw.len() - 32 { return Err(Error::new(ErrorKind::InvalidData, "checkpoint payload length mismatch")); }
        let payload = &raw[32..];
        if fnv_bytes(payload) != expected_csum { return Err(Error::new(ErrorKind::InvalidData, "checkpoint checksum mismatch")); }
        let mut poff = 0usize;
        let time = read_u64(payload, &mut poff)?;
        let rng_state = read_u64(payload, &mut poff)?;
        let n_sigs = read_u32(payload, &mut poff)? as usize;
        if n_sigs != expected_sigs { return Err(Error::new(ErrorKind::InvalidData, "checkpoint signal count mismatch")); }
        let mut sigs = Vec::with_capacity(n_sigs);
        for _ in 0..n_sigs { sigs.push(read_w64(payload, &mut poff)?); }
        let n_prevs = read_u32(payload, &mut poff)? as usize;
        if n_prevs != expected_prevs { return Err(Error::new(ErrorKind::InvalidData, "checkpoint event count mismatch")); }
        let mut prevs = Vec::with_capacity(n_prevs);
        for _ in 0..n_prevs { prevs.push(read_w64(payload, &mut poff)?); }
        let n_mems = read_u32(payload, &mut poff)? as usize;
        if n_mems != expected_mem_depths.len() { return Err(Error::new(ErrorKind::InvalidData, "checkpoint memory count mismatch")); }
        let mut mems = Vec::with_capacity(n_mems);
        for &exp_depth in expected_mem_depths {
            let depth = read_u32(payload, &mut poff)? as usize;
            if depth != exp_depth { return Err(Error::new(ErrorKind::InvalidData, "checkpoint memory depth mismatch")); }
            let mut m = Vec::with_capacity(depth);
            for _ in 0..depth { m.push(read_w64(payload, &mut poff)?); }
            mems.push(m);
        }
        Ok((time, rng_state, sigs, prevs, mems))
    }
}
""";
}
