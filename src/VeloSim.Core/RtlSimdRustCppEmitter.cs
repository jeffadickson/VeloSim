using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed partial class RtlDesign
{
    private readonly record struct SimdLang(
        bool IsRust,
        int Lanes,
        bool TwoState,
        string SelfPrefix,
        string FieldSuffix,
        string U64Suffix,
        string U64Max,
        string LetMut,
        string LetConst)
    {
        public string Ops => TwoState ? "w2s" : "w4s";
        public string WordType => TwoState ? "Lane" : "W4xN";
        public string MaskHex(int w) => w >= 64 ? U64Max : (w <= 0 ? $"0{U64Suffix}" : $"0x{((1UL << w) - 1UL):x}{U64Suffix}");
        public string Sig(int id) => $"{SelfPrefix}s{id}{FieldSuffix}";
        public string Prev(int id) => $"{SelfPrefix}prev{id}{FieldSuffix}";
        public string MemRead(int id, string idx) => $"{SelfPrefix}read_mem_{id}({idx})";
        public string MemLogPush(int id, int depth, string mask, string idx, string val) =>
            $"{SelfPrefix}mem_log{FieldSuffix}.push({id}, {depth}, {mask}, {idx}, {val});";
        public string If(string cond) => IsRust ? $"if {cond} {{" : $"if ({cond}) {{";
        public string Const4(string bits)
        {
            ulong a = 0, b = 0;
            foreach (char c in bits)
            {
                a <<= 1; b <<= 1;
                if (c == '1') a |= 1;
                else if (c == 'x') { a |= 1; b |= 1; }
                else if (c == 'z') b |= 1;
            }
            if (a == 0 && b == 0) return "W4xN::zero()";
            return b == 0 ? $"W4xN::lit(0x{a:x}{U64Suffix})" : $"W4xN::splat(0x{a:x}{U64Suffix}, 0x{b:x}{U64Suffix})";
        }
        public string Const2(string bits)
        {
            ulong v = 0;
            foreach (char c in bits) { v <<= 1; if (c == '1') v |= 1; }
            return v == 0 ? "Lane::zero()" : $"Lane::splat(0x{v:x}{U64Suffix})";
        }
    }

    private string RcSimdStore(string expr, RtlSignal sig, SimdLang lang, int knownWidth = -1)
    {
        if (lang.TwoState)
        {
            if (sig.Width >= 64 || knownWidth == sig.Width) return expr;
            return $"w2s::mask({expr}, {lang.MaskHex(sig.Width)})";
        }
        string m = lang.MaskHex(sig.Width);
        if (sig.TwoState) return $"w4s::two_state({expr}, {m})";
        return knownWidth == sig.Width ? expr : $"w4s::resize({expr}, {m})";
    }

    private string RcSimdMemIdx(JsonElement access, int id, SimdLang lang)
    {
        string idx = RcSimdExpr(access.GetProperty("index"), lang);
        if (Memories[id].LowerBound == 0) return idx;
        return $"{lang.Ops}::mem_idx({idx}, {Memories[id].LowerBound})";
    }

    private string RcSimdExpr(JsonElement e, SimdLang lang, int context = 0, bool? forceSigned = null)
    {
        string ops = lang.Ops;
        string kind = Kind(e);
        int ew = ExpressionWidth(e);
        bool signed = forceSigned ?? ExpressionSigned(e);
        string Extend(string val) => signed && context > ew
            ? $"{ops}::sresize({val}, {ew}, {lang.MaskHex(ew)}, {lang.MaskHex(context)})"
            : val;

        if (kind == "ref") return Extend(lang.Sig(SignalId(Name(e))));
        if (kind == "const") return Extend(lang.TwoState ? lang.Const2(e.GetProperty("bits").GetString()!) : lang.Const4(e.GetProperty("bits").GetString()!));
        if (kind == "index")
        {
            int id = MemoryId(Name(e));
            return Extend(lang.MemRead(id, RcSimdMemIdx(e, id, lang)));
        }
        if (kind == "bitselect")
        {
            var valEl = e.GetProperty("value");
            var idxEl = e.GetProperty("index");
            int aw = ExpressionWidth(valEl);
            if (TryConstShift(idxEl, out int bi) && bi < aw)
                return bi == 0
                    ? (lang.TwoState ? $"w2s::mask({RcSimdExpr(valEl, lang)}, 0x1{lang.U64Suffix})" : $"w4s::resize({RcSimdExpr(valEl, lang)}, 0x1{lang.U64Suffix})")
                    : $"{ops}::sel_part_fast({RcSimdExpr(valEl, lang)}, {bi}, 0x1{lang.U64Suffix})";
            return $"{ops}::sel_bit({RcSimdExpr(valEl, lang)}, {aw}, {RcSimdExpr(idxEl, lang)})";
        }
        if (kind == "partselect")
        {
            var valEl = e.GetProperty("value");
            int aw = ExpressionWidth(valEl), left = e.GetProperty("left").GetInt32(), right = e.GetProperty("right").GetInt32();
            if (right >= 0 && left < aw)
            {
                string m = lang.MaskHex(left - right + 1);
                return right == 0
                    ? (lang.TwoState ? $"w2s::mask({RcSimdExpr(valEl, lang)}, {m})" : $"w4s::resize({RcSimdExpr(valEl, lang)}, {m})")
                    : $"{ops}::sel_part_fast({RcSimdExpr(valEl, lang)}, {right}, {m})";
            }
            return $"{ops}::sel_part({RcSimdExpr(valEl, lang)}, {aw}, {left}, {right})";
        }
        if (kind == "indexedselect")
        {
            var valEl = e.GetProperty("value");
            return $"{ops}::sel_idx({RcSimdExpr(valEl, lang)}, {ExpressionWidth(valEl)}, {RcSimdExpr(e.GetProperty("base"), lang)}, {e.GetProperty("width").GetInt32()}, {(e.GetProperty("descending").GetBoolean() ? "true" : "false")})";
        }
        if (kind == "resolve")
        {
            int nk = ParseNetKind(e.GetProperty("net").GetString()!);
            int rw = Math.Max(context, ew);
            var drvEls = e.GetProperty("args").EnumerateArray().ToArray();
            if (lang.TwoState)
            {
                string acc = $"w2s::mask({RcSimdExpr(drvEls[0], lang, rw)}, {lang.MaskHex(rw)})";
                for (int i = 1; i < drvEls.Length; ++i)
                    acc = nk == 1 ? $"w2s::bit_and({acc}, {RcSimdExpr(drvEls[i], lang, rw)}, {lang.MaskHex(rw)})"
                        : $"w2s::bit_or({acc}, {RcSimdExpr(drvEls[i], lang, rw)}, {lang.MaskHex(rw)})";
                return Extend(acc);
            }
            string a4 = $"w4s::resize({RcSimdExpr(drvEls[0], lang, rw)}, {lang.MaskHex(rw)})";
            for (int i = 1; i < drvEls.Length; ++i)
                a4 = $"w4s::resolve_pair({a4}, {RcSimdExpr(drvEls[i], lang, rw)}, {nk}, {lang.MaskHex(rw)})";
            return Extend($"w4s::resolve_finalize({a4}, {nk}, {lang.MaskHex(rw)})");
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
                    string loC = cs ? $"{ops}::scmp({RcSimdExpr(valEl, lang, cw, true)}, {RcSimdExpr(loEl, lang, cw, true)}, {cw}, 3)" : $"{ops}::cmp({RcSimdExpr(valEl, lang, cw, false)}, {RcSimdExpr(loEl, lang, cw, false)}, 3)";
                    string hiC = cs ? $"{ops}::scmp({RcSimdExpr(valEl, lang, cw, true)}, {RcSimdExpr(hiEl, lang, cw, true)}, {cw}, 1)" : $"{ops}::cmp({RcSimdExpr(valEl, lang, cw, false)}, {RcSimdExpr(hiEl, lang, cw, false)}, 1)";
                    parts.Add($"{ops}::land({loC}, {hiC})");
                }
                else
                {
                    int cw = Math.Max(vw, ExpressionWidth(it));
                    bool cs = vSigned && ExpressionSigned(it);
                    parts.Add($"{ops}::wild_eq({RcSimdExpr(valEl, lang, cw, cs)}, {RcSimdExpr(it, lang, cw, cs)}, {lang.MaskHex(cw)})");
                }
            }
            string acc = parts[0];
            for (int i = 1; i < parts.Count; ++i) acc = $"{ops}::lor({acc}, {parts[i]})";
            return acc;
        }

        var args = e.GetProperty("args").EnumerateArray().ToArray();
        int width = Math.Max(context, ew);
        string wm = lang.MaskHex(width);
        return kind switch
        {
            "castsigned" or "castunsigned" => Extend(RcSimdExpr(args[0], lang)),
            "countones" => Extend($"{ops}::countones({RcSimdExpr(args[0], lang)}, {lang.MaskHex(ExpressionWidth(args[0]))})"),
            "onehot" => $"{ops}::onehot({RcSimdExpr(args[0], lang)}, {lang.MaskHex(ExpressionWidth(args[0]))})",
            "onehot0" => $"{ops}::onehot0({RcSimdExpr(args[0], lang)}, {lang.MaskHex(ExpressionWidth(args[0]))})",
            "isunknown" => lang.TwoState ? "Lane::zero()" : $"w4s::isunknown({RcSimdExpr(args[0], lang)}, {lang.MaskHex(ExpressionWidth(args[0]))})",
            "clog2" => Extend($"{ops}::clog2({RcSimdExpr(args[0], lang)}, {lang.MaskHex(ExpressionWidth(args[0]))})"),
            "bitnot" => Extend($"{ops}::bit_not({RcSimdExpr(args[0], lang, width, signed)}, {wm})"),
            "neg" => Extend($"{ops}::neg({RcSimdExpr(args[0], lang, width, signed)}, {wm})"),
            "pos" => Extend(lang.TwoState ? $"w2s::mask({RcSimdExpr(args[0], lang, width, signed)}, {wm})" : $"w4s::resize({RcSimdExpr(args[0], lang, width, signed)}, {wm})"),
            "not" => $"{ops}::lnot({RcSimdExpr(args[0], lang)})",
            "bitand" => Extend($"{ops}::bit_and({RcSimdExpr(args[0], lang, width, signed)}, {RcSimdExpr(args[1], lang, width, signed)}, {wm})"),
            "bitor" => Extend($"{ops}::bit_or({RcSimdExpr(args[0], lang, width, signed)}, {RcSimdExpr(args[1], lang, width, signed)}, {wm})"),
            "bitxor" => Extend($"{ops}::bit_xor({RcSimdExpr(args[0], lang, width, signed)}, {RcSimdExpr(args[1], lang, width, signed)}, {wm})"),
            "bitxnor" => Extend($"{ops}::bit_xnor({RcSimdExpr(args[0], lang, width, signed)}, {RcSimdExpr(args[1], lang, width, signed)}, {wm})"),
            "and" => $"{ops}::land({RcSimdExpr(args[0], lang)}, {RcSimdExpr(args[1], lang)})",
            "or" => $"{ops}::lor({RcSimdExpr(args[0], lang)}, {RcSimdExpr(args[1], lang)})",
            "add" => Extend($"{ops}::add({RcSimdExpr(args[0], lang, width, signed)}, {RcSimdExpr(args[1], lang, width, signed)}, {wm})"),
            "sub" => Extend($"{ops}::sub({RcSimdExpr(args[0], lang, width, signed)}, {RcSimdExpr(args[1], lang, width, signed)}, {wm})"),
            "mul" => Extend(signed ? $"{ops}::sproduct({RcSimdExpr(args[0], lang, width, true)}, {RcSimdExpr(args[1], lang, width, true)}, {width}, 0)" : $"{ops}::product({RcSimdExpr(args[0], lang, width)}, {RcSimdExpr(args[1], lang, width)}, {wm}, 0)"),
            "div" => Extend(signed ? $"{ops}::sproduct({RcSimdExpr(args[0], lang, width, true)}, {RcSimdExpr(args[1], lang, width, true)}, {width}, 1)" : $"{ops}::product({RcSimdExpr(args[0], lang, width)}, {RcSimdExpr(args[1], lang, width)}, {wm}, 1)"),
            "mod" => Extend(signed ? $"{ops}::sproduct({RcSimdExpr(args[0], lang, width, true)}, {RcSimdExpr(args[1], lang, width, true)}, {width}, 2)" : $"{ops}::product({RcSimdExpr(args[0], lang, width)}, {RcSimdExpr(args[1], lang, width)}, {wm}, 2)"),
            "shl" or "ashl" => Extend(TryConstShift(args[1], out int kShl) ? $"{ops}::shl_c({RcSimdExpr(args[0], lang, width, signed)}, {kShl}, {wm})" : $"{ops}::shl({RcSimdExpr(args[0], lang, width, signed)}, {RcSimdExpr(args[1], lang)}, {wm})"),
            "shr" => Extend(TryConstShift(args[1], out int kShr) ? $"{ops}::shr_c({RcSimdExpr(args[0], lang, width, signed)}, {kShr}, {wm})" : $"{ops}::shr({RcSimdExpr(args[0], lang, width, signed)}, {RcSimdExpr(args[1], lang)}, {wm})"),
            "ashr" => Extend(signed ? $"{ops}::ashr({RcSimdExpr(args[0], lang, width, true)}, {RcSimdExpr(args[1], lang)}, {width}, {wm})" : $"{ops}::shr({RcSimdExpr(args[0], lang, width)}, {RcSimdExpr(args[1], lang)}, {wm})"),
            "eq" or "neq" or "caseeq" or "caseneq" or "wildeq" or "wildneq" => RcSimdEqExpr(kind, args, lang),
            "lt" or "le" or "gt" or "ge" => RcSimdCmpExpr(kind, args, lang),
            "redand" => $"{ops}::red({RcSimdExpr(args[0], lang)}, {lang.MaskHex(ExpressionWidth(args[0]))}, 0)",
            "rednand" => $"{ops}::red({RcSimdExpr(args[0], lang)}, {lang.MaskHex(ExpressionWidth(args[0]))}, 1)",
            "redor" => $"{ops}::red({RcSimdExpr(args[0], lang)}, {lang.MaskHex(ExpressionWidth(args[0]))}, 2)",
            "rednor" => $"{ops}::red({RcSimdExpr(args[0], lang)}, {lang.MaskHex(ExpressionWidth(args[0]))}, 3)",
            "redxor" => $"{ops}::red({RcSimdExpr(args[0], lang)}, {lang.MaskHex(ExpressionWidth(args[0]))}, 4)",
            "redxnor" => $"{ops}::red({RcSimdExpr(args[0], lang)}, {lang.MaskHex(ExpressionWidth(args[0]))}, 5)",
            "cond" => Extend($"{ops}::cond({RcSimdExpr(args[0], lang)}, {RcSimdExpr(args[1], lang, width, signed)}, {RcSimdExpr(args[2], lang, width, signed)}, {wm})"),
            "concat" => Extend(RcSimdConcatExpr(args, lang)),
            _ => throw new ArgumentException("Unsupported SIMD RTL expression kind: " + kind)
        };
    }

    private string RcSimdEqExpr(string kind, JsonElement[] args, SimdLang lang)
    {
        int cw = Math.Max(ExpressionWidth(args[0]), ExpressionWidth(args[1]));
        bool cs = ExpressionSigned(args[0]) && ExpressionSigned(args[1]);
        string a = RcSimdExpr(args[0], lang, cw, cs), b = RcSimdExpr(args[1], lang, cw, cs), cm = lang.MaskHex(cw);
        string ops = lang.Ops;
        return kind switch
        {
            "eq" => $"{ops}::eq({a}, {b}, {cm})",
            "neq" => $"{ops}::lnot({ops}::eq({a}, {b}, {cm}))",
            "caseeq" => $"{ops}::case_eq({a}, {b}, {cm})",
            "caseneq" => $"{ops}::lnot({ops}::case_eq({a}, {b}, {cm}))",
            "wildeq" => $"{ops}::wild_eq({a}, {b}, {cm})",
            _ => $"{ops}::lnot({ops}::wild_eq({a}, {b}, {cm}))"
        };
    }

    private string RcSimdCmpExpr(string kind, JsonElement[] args, SimdLang lang)
    {
        int cw = Math.Max(ExpressionWidth(args[0]), ExpressionWidth(args[1]));
        bool cs = ExpressionSigned(args[0]) && ExpressionSigned(args[1]);
        int op = kind switch { "lt" => 0, "le" => 1, "gt" => 2, _ => 3 };
        string a = RcSimdExpr(args[0], lang, cw, cs), b = RcSimdExpr(args[1], lang, cw, cs);
        return cs ? $"{lang.Ops}::scmp({a}, {b}, {cw}, {op})" : $"{lang.Ops}::cmp({a}, {b}, {op})";
    }

    private string RcSimdConcatExpr(JsonElement[] args, SimdLang lang)
    {
        string acc = RcSimdExpr(args[0], lang);
        for (int i = 1; i < args.Length; ++i)
        {
            int bw = ExpressionWidth(args[i]);
            acc = $"{lang.Ops}::concat({acc}, {RcSimdExpr(args[i], lang)}, {bw}, {lang.MaskHex(bw)})";
        }
        return acc;
    }

    private void EmitRcSimdWordWrite(StringBuilder sb, JsonElement target, string value, string indent, string activeMask, SimdLang lang, int knownWidth = -1)
    {
        int sid = SignalId(TargetOwner(target));
        string ops = lang.Ops;
        if (Kind(target) == "ref")
        {
            string v = RcSimdStore(value, Signals[sid], lang, knownWidth);
            sb.AppendLine($"{indent}_next{sid} = {ops}::sel({activeMask}, {v}, _next{sid});");
            return;
        }
        int width = Kind(target) == "bitselect" ? 1
            : (Kind(target) == "indexedselect" ? target.GetProperty("width").GetInt32()
            : target.GetProperty("left").GetInt32() - target.GetProperty("right").GetInt32() + 1);
        bool desc = Kind(target) == "indexedselect" && target.GetProperty("descending").GetBoolean();
        string basis = Kind(target) == "bitselect" ? RcSimdExpr(target.GetProperty("index"), lang)
            : Kind(target) == "indexedselect" ? RcSimdExpr(target.GetProperty("base"), lang)
            : (lang.TwoState ? $"Lane::splat({target.GetProperty("right").GetInt32()}{lang.U64Suffix})" : $"W4xN::lit({target.GetProperty("right").GetInt32()}{lang.U64Suffix})");
        string dep = $"{ops}::dep_idx(_next{sid}, {Signals[sid].Width}, {basis}, {width}, {(desc ? "true" : "false")}, {value})";
        if (!lang.TwoState && Signals[sid].TwoState) dep = $"w4s::two_state({dep}, {lang.MaskHex(Signals[sid].Width)})";
        sb.AppendLine($"{indent}_next{sid} = {ops}::sel({activeMask}, {dep}, _next{sid});");
    }

    private void EmitRcSimdStatement(StringBuilder sb, JsonElement s, string indent, string activeMask, SimdLang lang, ref int tmp)
    {
        string kind = Kind(s);
        string ops = lang.Ops;
        if (kind == "block")
        {
            foreach (var item in s.GetProperty("items").EnumerateArray())
                EmitRcSimdStatement(sb, item, indent, activeMask, lang, ref tmp);
            return;
        }
        if (kind == "repeat")
        {
            int count = s.GetProperty("count").GetInt32();
            int id = tmp++;
            sb.AppendLine(lang.IsRust ? $"{indent}for _rep{id} in 0..{count} {{" : $"{indent}for (int _rep{id} = 0; _rep{id} < {count}; ++_rep{id}) {{");
            EmitRcSimdStatement(sb, s.GetProperty("body"), indent + "    ", activeMask, lang, ref tmp);
            sb.AppendLine($"{indent}}}");
            return;
        }
        if (kind == "if")
        {
            int id = tmp++;
            string cond = $"_c{id}", tMask = $"_mt{id}", eMask = $"_me{id}";
            sb.AppendLine($"{indent}{lang.LetConst} {cond} = {ops}::yes_mask({RcSimdExpr(s.GetProperty("condition"), lang)});");
            sb.AppendLine($"{indent}{lang.LetConst} {tMask} = Lane::band({activeMask}, {cond});");
            var thenEl = s.GetProperty("then");
            var elseEl = s.GetProperty("else");
            bool hasThen = !(Kind(thenEl) == "block" && thenEl.GetProperty("items").GetArrayLength() == 0);
            bool hasElse = !(Kind(elseEl) == "block" && elseEl.GetProperty("items").GetArrayLength() == 0);
            if (hasThen)
            {
                sb.AppendLine($"{indent}{lang.If($"Lane::any({tMask})")}");
                EmitRcSimdStatement(sb, thenEl, indent + "    ", tMask, lang, ref tmp);
                sb.AppendLine($"{indent}}}");
            }
            if (hasElse)
            {
                sb.AppendLine($"{indent}{lang.LetConst} {eMask} = Lane::band({activeMask}, Lane::bnot({cond}));");
                sb.AppendLine($"{indent}{lang.If($"Lane::any({eMask})")}");
                EmitRcSimdStatement(sb, elseEl, indent + "    ", eMask, lang, ref tmp);
                sb.AppendLine($"{indent}}}");
            }
            return;
        }
        if (kind == "case")
        {
            int id = tmp++;
            var items = s.GetProperty("items").EnumerateArray().ToArray();
            var allMatches = items.SelectMany(CaseItemMatches).ToArray();
            int cw = Math.Max(ExpressionWidth(s.GetProperty("value")), allMatches.Select(ExpressionWidth).DefaultIfEmpty(1).Max());
            string cwm = lang.MaskHex(cw);
            string ck = CaseKind(s);
            bool csSigned = ExpressionSigned(s.GetProperty("value")) && allMatches.All(ExpressionSigned);
            string sv = $"_sv{id}", rem = $"_rem{id}";
            sb.AppendLine($"{indent}{lang.LetConst} {sv} = {RcSimdExpr(s.GetProperty("value"), lang, cw, csSigned)};");
            sb.AppendLine($"{indent}{lang.LetMut} {rem} = {activeMask};");
            string fn = ck == "casez" ? "casez_eq" : (ck == "casex" ? "casex_eq" : "case_eq");
            foreach (var item in items.Where(i => !IsCaseDefault(i)))
            {
                var ms = CaseItemMatches(item);
                string cond = "";
                foreach (var m in ms)
                {
                    string term = $"{ops}::yes_mask({ops}::{fn}({sv}, {RcSimdExpr(m, lang, cw, csSigned)}, {cwm}))";
                    cond = cond.Length == 0 ? term : $"Lane::bor({cond}, {term})";
                }
                int aid = tmp++;
                string hit = $"_cm{aid}";
                sb.AppendLine($"{indent}{lang.LetConst} {hit} = Lane::band({rem}, {cond});");
                sb.AppendLine($"{indent}{lang.If($"Lane::any({hit})")}");
                EmitRcSimdStatement(sb, item.GetProperty("body"), indent + "    ", hit, lang, ref tmp);
                sb.AppendLine($"{indent}    {rem} = Lane::band({rem}, Lane::bnot({hit}));");
                sb.AppendLine($"{indent}}}");
            }
            var defItem = items.FirstOrDefault(IsCaseDefault);
            if (defItem.ValueKind != JsonValueKind.Undefined)
            {
                sb.AppendLine($"{indent}{lang.If($"Lane::any({rem})")}");
                EmitRcSimdStatement(sb, defItem.GetProperty("body"), indent + "    ", rem, lang, ref tmp);
                sb.AppendLine($"{indent}}}");
            }
            return;
        }
        if (kind == "nba")
        {
            var target = s.GetProperty("target");
            string tk = Kind(target);
            if (tk == "concat")
            {
                int id = tmp++;
                int total = ExpressionWidth(target), remaining = total;
                string rhs = $"_nc{id}";
                string raw = RcSimdExpr(s.GetProperty("value"), lang, total);
                sb.AppendLine($"{indent}{lang.LetConst} {rhs} = {(lang.TwoState ? $"w2s::mask({raw}, {lang.MaskHex(total)})" : $"w4s::resize({raw}, {lang.MaskHex(total)})")};");
                foreach (var leaf in TargetLeaves(target))
                {
                    int w = ExpressionWidth(leaf);
                    remaining -= w;
                    string part = remaining == 0
                        ? (lang.TwoState ? $"w2s::mask({rhs}, {lang.MaskHex(w)})" : $"w4s::resize({rhs}, {lang.MaskHex(w)})")
                        : $"{ops}::sel_part_fast({rhs}, {remaining}, {lang.MaskHex(w)})";
                    EmitRcSimdWordWrite(sb, leaf, part, indent, activeMask, lang, w);
                }
                return;
            }
            if (tk is "ref" or "bitselect" or "partselect" or "indexedselect")
            {
                int tw = ExpressionWidth(target);
                var valEl = s.GetProperty("value");
                int vw = ExpressionWidth(valEl);
                bool exactWidth = vw == tw && !(ExpressionSigned(valEl) && tw > vw);
                string expr = RcSimdExpr(valEl, lang, tw);
                EmitRcSimdWordWrite(sb, target, expr, indent, activeMask, lang, exactWidth ? tw : -1);
                return;
            }
            int mid = MemoryId(Name(target));
            var mem = Memories[mid];
            string idx = RcSimdMemIdx(target, mid, lang);
            string rawVal = RcSimdExpr(s.GetProperty("value"), lang, mem.Width);
            string val = lang.TwoState
                ? (mem.Width >= 64 ? rawVal : $"w2s::mask({rawVal}, {lang.MaskHex(mem.Width)})")
                : (mem.TwoState ? $"w4s::two_state({rawVal}, {lang.MaskHex(mem.Width)})" : $"w4s::resize({rawVal}, {lang.MaskHex(mem.Width)})");
            sb.AppendLine($"{indent}{lang.MemLogPush(mid, mem.Depth, activeMask, idx, val)}");
            return;
        }
        throw new ArgumentException("Unsupported SIMD RTL statement kind: " + kind);
    }

    private string SimdInitValue(RtlSignal sig, SimdLang lang)
    {
        if (lang.TwoState) return "Lane::zero()";
        if (sig.Name == Clock || sig.TwoState) return "W4xN::zero()";
        string m = lang.MaskHex(sig.Width);
        return $"W4xN::splat({m}, {m})";
    }

    // =========================================================================
    // Rust Lockstep SIMD Emitter (#![forbid(unsafe_code)], 4/8 lanes)
    // =========================================================================

    public string EmitRustSimdModel(string top, int lanes, RtlStateMode stateMode = RtlStateMode.FourState)
    {
        ValidateSimdLanes(lanes);
        if (!Identifier(top) || ("V" + top).Contains("__", StringComparison.Ordinal)) throw new ArgumentException("Invalid external model top name.", nameof(top));
        if (!CanUseSimdLanes(stateMode))
            throw new IrValidationException("SIMD --lanes export currently supports non-timed <=64-bit event-driven and combinational designs without runtime loops, forces, SVA, or dynamic containers.");
        bool twoState = stateMode == RtlStateMode.TwoState;
        var lang = new SimdLang(true, lanes, twoState, "self.", "", "u64", "u64::MAX", "let mut", "let");
        var portNames = RustPortNames();
        var procs = CombinationalOnly ? Array.Empty<JsonElement>() : (Kind(Body) == "parallel" ? Body.GetProperty("items").EnumerateArray().ToArray() : [Body]);
        var eventIds = CombinationalOnly ? Array.Empty<int>() : EventSignals.Select(SignalId).ToArray();
        int[] seqIds = CombinationalOnly ? [] : Statements(Body).Where(e => Kind(e) == "nba" && Kind(e.GetProperty("target")) != "index")
            .SelectMany(e => TargetLeaves(e.GetProperty("target"))).Select(t => SignalId(TargetOwner(t)))
            .Distinct().Order().ToArray();
        bool combPureInternal = !CombinationalOnly && SettledSteps.Count > 0 && SettledSteps.All(s => CsExprIsPureInternal(s.GetProperty("value")));
        string wt = lang.WordType;
        string st = twoState ? "u64" : "W4";

        var sb = new StringBuilder(16384);
        sb.AppendLine("#![forbid(unsafe_code)]");
        sb.AppendLine("#![allow(dead_code, unused_variables, unused_mut, non_snake_case, non_camel_case_types, clippy::all)]");
        sb.AppendLine("pub mod simd_runtime;");
        sb.AppendLine("pub use simd_runtime::{LANES, Lane, W4, W4xN, w2s, w4s, SimdMemLog2, SimdMemLog4};");
        sb.AppendLine();
        sb.AppendLine($"pub struct V{top} {{");
        sb.AppendLine("    time: u64,");
        if (combPureInternal) sb.AppendLine("    comb_dirty: bool,");
        for (int i = 0; i < Signals.Count; ++i)
            sb.AppendLine($"    s{i}: {wt},");
        foreach (int id in eventIds)
            sb.AppendLine($"    prev{id}: {wt},");
        for (int i = 0; i < Memories.Count; ++i)
        {
            int total = Memories[i].Depth * lanes;
            bool heap = total > 4096;
            string ty = heap ? "Vec<u64>" : $"[u64; {total}]";
            sb.AppendLine($"    m{i}_a: {ty},");
            if (!twoState) sb.AppendLine($"    m{i}_b: {ty},");
        }
        if (Memories.Count > 0)
            sb.AppendLine($"    mem_log: {(twoState ? "SimdMemLog2" : "SimdMemLog4")},");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine($"impl Default for V{top} {{ fn default() -> Self {{ Self::new() }} }}");
        sb.AppendLine();
        sb.AppendLine($"impl V{top} {{");
        sb.AppendLine($"    pub const LANES: usize = {lanes};");
        sb.AppendLine();
        sb.AppendLine("    pub fn new() -> Self {");
        sb.AppendLine("        Self {");
        sb.AppendLine("            time: 0,");
        if (combPureInternal) sb.AppendLine("            comb_dirty: true,");
        for (int i = 0; i < Signals.Count; ++i)
            sb.AppendLine($"            s{i}: {SimdInitValue(Signals[i], lang)},");
        foreach (int id in eventIds)
        {
            var sig = Signals[id];
            string init = twoState ? "Lane::zero()" : (sig.Name == Clock || sig.TwoState ? "W4xN::zero()" : "W4xN::splat(1u64, 1u64)");
            sb.AppendLine($"            prev{id}: {init},");
        }
        for (int i = 0; i < Memories.Count; ++i)
        {
            var mem = Memories[i];
            int total = mem.Depth * lanes;
            bool heap = total > 4096;
            string initV = twoState || mem.TwoState ? "0u64" : lang.MaskHex(mem.Width);
            sb.AppendLine($"            m{i}_a: {(heap ? $"vec![{initV}; {total}]" : $"[{initV}; {total}]")},");
            if (!twoState) sb.AppendLine($"            m{i}_b: {(heap ? $"vec![{initV}; {total}]" : $"[{initV}; {total}]")},");
        }
        if (Memories.Count > 0)
            sb.AppendLine($"            mem_log: {(twoState ? "SimdMemLog2" : "SimdMemLog4")}::new(),");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    #[inline(always)] pub fn time(&self) -> u64 { self.time }");
        sb.AppendLine("    #[inline(always)] pub fn time_inc(&mut self, delta: u64) { self.time = self.time.wrapping_add(delta); }");
        sb.AppendLine("    pub fn final_model(&mut self) {}");
        sb.AppendLine();

        foreach (var sig in Signals.Where(s => s.Direction != "internal"))
        {
            int id = SignalId(sig.Name);
            string pn = portNames[sig.Name];
            string m = lang.MaskHex(sig.Width);
            if (sig.Direction == "input")
            {
                if (twoState)
                {
                    sb.AppendLine($"    #[inline(always)] pub fn set_{pn}(&mut self, lane: usize, value: u64) {{ self.s{id}.v[lane] = value & {m}; }}");
                    sb.AppendLine($"    #[inline(always)] pub fn set_{pn}_all(&mut self, value: u64) {{ self.s{id} = Lane::splat(value & {m}); }}");
                    sb.AppendLine($"    #[inline(always)] pub fn set_{pn}_vec(&mut self, value: Lane) {{ self.s{id} = w2s::mask(value, {m}); }}");
                }
                else
                {
                    string norm = sig.TwoState ? $"W4::lit((value.a & !value.b) & {m})" : $"W4::new(value.a & {m}, value.b & {m})";
                    string normVec = sig.TwoState ? $"w4s::two_state(value, {m})" : $"w4s::resize(value, {m})";
                    sb.AppendLine($"    #[inline(always)] pub fn set_{pn}(&mut self, lane: usize, value: W4) {{ let v = {norm}; self.s{id}.a.v[lane] = v.a; self.s{id}.b.v[lane] = v.b; }}");
                    sb.AppendLine($"    #[inline(always)] pub fn set_{pn}_u64(&mut self, lane: usize, value: u64) {{ self.s{id}.a.v[lane] = value & {m}; self.s{id}.b.v[lane] = 0; }}");
                    sb.AppendLine($"    #[inline(always)] pub fn set_{pn}_all(&mut self, value: W4) {{ let v = {norm}; self.s{id} = W4xN::splat(v.a, v.b); }}");
                    sb.AppendLine($"    #[inline(always)] pub fn set_{pn}_all_u64(&mut self, value: u64) {{ self.s{id} = W4xN::lit(value & {m}); }}");
                    sb.AppendLine($"    #[inline(always)] pub fn set_{pn}_vec(&mut self, value: W4xN) {{ self.s{id} = {normVec}; }}");
                }
            }
            if (twoState)
            {
                sb.AppendLine($"    #[inline(always)] pub fn get_{pn}(&self, lane: usize) -> u64 {{ self.s{id}.v[lane] }}");
                sb.AppendLine($"    #[inline(always)] pub fn get_{pn}_vec(&self) -> Lane {{ self.s{id} }}");
                sb.AppendLine($"    #[inline(always)] pub fn diverged_mask_{pn}(&self, golden_lane: usize) -> u32 {{ w2s::diverged_mask(self.s{id}, golden_lane) }}");
            }
            else
            {
                sb.AppendLine($"    #[inline(always)] pub fn get_{pn}(&self, lane: usize) -> W4 {{ W4::new(self.s{id}.a.v[lane], self.s{id}.b.v[lane]) }}");
                sb.AppendLine($"    #[inline(always)] pub fn get_{pn}_u64(&self, lane: usize) -> u64 {{ self.s{id}.a.v[lane] }}");
                sb.AppendLine($"    #[inline(always)] pub fn get_{pn}_vec(&self) -> W4xN {{ self.s{id} }}");
                sb.AppendLine($"    #[inline(always)] pub fn diverged_mask_{pn}(&self, golden_lane: usize) -> u32 {{ w4s::diverged_mask(self.s{id}, golden_lane) }}");
            }
        }
        sb.AppendLine();

        for (int i = 0; i < Memories.Count; ++i)
        {
            var mem = Memories[i];
            string m = lang.MaskHex(mem.Width);
            if (twoState)
            {
                sb.AppendLine($"    #[inline(always)] fn read_mem_{i}(&self, idx: Lane) -> Lane {{");
                sb.AppendLine("        let mut out = [0u64; LANES];");
                sb.AppendLine("        for l in 0..LANES {");
                sb.AppendLine($"            let ix = idx.v[l] as usize;");
                sb.AppendLine($"            out[l] = if ix < {mem.Depth} {{ self.m{i}_a[l * {mem.Depth} + ix] & {m} }} else {{ 0u64 }};");
                sb.AppendLine("        }");
                sb.AppendLine("        Lane { v: out }");
                sb.AppendLine("    }");
            }
            else
            {
                string oob = mem.TwoState ? "0u64" : m;
                sb.AppendLine($"    #[inline(always)] fn read_mem_{i}(&self, idx: W4xN) -> W4xN {{");
                sb.AppendLine("        let mut oa = [0u64; LANES]; let mut ob = [0u64; LANES];");
                sb.AppendLine("        for l in 0..LANES {");
                sb.AppendLine($"            let ix = idx.a.v[l] as usize;");
                sb.AppendLine($"            if idx.b.v[l] == 0 && ix < {mem.Depth} {{");
                sb.AppendLine($"                let pos = l * {mem.Depth} + ix;");
                sb.AppendLine($"                oa[l] = self.m{i}_a[pos] & {m}; ob[l] = self.m{i}_b[pos] & {m};");
                sb.AppendLine($"            }} else {{ oa[l] = {oob}; ob[l] = {oob}; }}");
                sb.AppendLine("        }");
                sb.AppendLine("        W4xN { a: Lane { v: oa }, b: Lane { v: ob } }");
                sb.AppendLine("    }");
            }
        }

        sb.AppendLine($"    pub fn memory_read(&self, mem_id: usize, lane: usize, index: usize) -> {st} {{");
        sb.AppendLine("        match mem_id {");
        for (int i = 0; i < Memories.Count; ++i)
        {
            var mem = Memories[i];
            string m = lang.MaskHex(mem.Width);
            if (twoState)
                sb.AppendLine($"            {i} => if index < {mem.Depth} {{ self.m{i}_a[lane * {mem.Depth} + index] & {m} }} else {{ 0u64 }},");
            else
                sb.AppendLine($"            {i} => if index < {mem.Depth} {{ let p = lane * {mem.Depth} + index; W4::new(self.m{i}_a[p] & {m}, self.m{i}_b[p] & {m}) }} else {{ W4::new({m}, {m}) }},");
        }
        sb.AppendLine($"            _ => {(twoState ? "0u64" : "W4::zero()")},");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine($"    pub fn memory_write(&mut self, mem_id: usize, lane: usize, index: usize, value: {st}) {{");
        sb.AppendLine("        match mem_id {");
        for (int i = 0; i < Memories.Count; ++i)
        {
            var mem = Memories[i];
            string m = lang.MaskHex(mem.Width);
            if (twoState)
                sb.AppendLine($"            {i} => if index < {mem.Depth} {{ self.m{i}_a[lane * {mem.Depth} + index] = value & {m};{(combPureInternal ? " self.comb_dirty = true;" : "")} }},");
            else
            {
                string va = mem.TwoState ? $"(value.a & !value.b) & {m}" : $"value.a & {m}";
                string vb = mem.TwoState ? "0u64" : $"value.b & {m}";
                sb.AppendLine($"            {i} => if index < {mem.Depth} {{ let p = lane * {mem.Depth} + index; self.m{i}_a[p] = {va}; self.m{i}_b[p] = {vb};{(combPureInternal ? " self.comb_dirty = true;" : "")} }},");
            }
        }
        sb.AppendLine("            _ => {}");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        sb.AppendLine("    #[inline(always)] fn settle(&mut self) {");
        foreach (var step in SettledSteps)
        {
            int id = SignalId(Name(step.GetProperty("target")));
            var rhs = step.GetProperty("value");
            int vw = ExpressionWidth(rhs);
            bool exactWidth = vw == Signals[id].Width && !(ExpressionSigned(rhs) && Signals[id].Width > vw);
            string expr = RcSimdExpr(rhs, lang, Signals[id].Width);
            sb.AppendLine($"        self.s{id} = {RcSimdStore(expr, Signals[id], lang, exactWidth ? Signals[id].Width : -1)};");
        }
        sb.AppendLine("    }");
        sb.AppendLine();

        EmitRcSimdEval(sb, lang, procs, eventIds, seqIds, combPureInternal);
        sb.AppendLine("}");
        return sb.ToString();
    }

    // =========================================================================
    // C++20 Lockstep SIMD Emitter (alignas(32/64) Lane, 4/8 lanes)
    // =========================================================================

    public string EmitCppSimdModelHeader(string top, int lanes, RtlStateMode stateMode = RtlStateMode.FourState)
    {
        ValidateSimdLanes(lanes);
        if (!Identifier(top) || ("V" + top).Contains("__", StringComparison.Ordinal)) throw new ArgumentException("Invalid external model top name.", nameof(top));
        if (!CanUseSimdLanes(stateMode))
            throw new IrValidationException("SIMD --lanes export currently supports non-timed <=64-bit event-driven and combinational designs without runtime loops, forces, SVA, or dynamic containers.");
        bool twoState = stateMode == RtlStateMode.TwoState;
        var lang = new SimdLang(false, lanes, twoState, "", "_", "ULL", "0xffffffffffffffffULL", "auto", "const auto");
        var portNames = ExternalPortNames();
        var procs = CombinationalOnly ? Array.Empty<JsonElement>() : (Kind(Body) == "parallel" ? Body.GetProperty("items").EnumerateArray().ToArray() : [Body]);
        var eventIds = CombinationalOnly ? Array.Empty<int>() : EventSignals.Select(SignalId).ToArray();
        int[] seqIds = CombinationalOnly ? [] : Statements(Body).Where(e => Kind(e) == "nba" && Kind(e.GetProperty("target")) != "index")
            .SelectMany(e => TargetLeaves(e.GetProperty("target"))).Select(t => SignalId(TargetOwner(t)))
            .Distinct().Order().ToArray();
        bool combPureInternal = !CombinationalOnly && SettledSteps.Count > 0 && SettledSteps.All(s => CsExprIsPureInternal(s.GetProperty("value")));
        string wt = lang.WordType;
        string st = twoState ? "uint64_t" : "W4";

        var sb = new StringBuilder(16384);
        sb.AppendLine("#pragma once");
        sb.AppendLine("#include \"simd_runtime.h\"");
        sb.AppendLine("#include <array>");
        sb.AppendLine("#include <cstddef>");
        sb.AppendLine("#include <cstdint>");
        sb.AppendLine("#include <vector>");
        sb.AppendLine();
        sb.AppendLine($"class V{top} {{");
        sb.AppendLine("public:");
        sb.AppendLine("    using Lane = velosim_simd::Lane;");
        sb.AppendLine("    using W4 = velosim_simd::W4;");
        sb.AppendLine("    using W4xN = velosim_simd::W4xN;");
        sb.AppendLine($"    static constexpr int kLanes = {lanes};");
        sb.AppendLine();
        sb.AppendLine($"    V{top}() {{");
        for (int i = 0; i < Signals.Count; ++i)
            sb.AppendLine($"        s{i}_ = {SimdInitValue(Signals[i], lang)};");
        foreach (int id in eventIds)
        {
            var sig = Signals[id];
            string init = twoState ? "Lane::zero()" : (sig.Name == Clock || sig.TwoState ? "W4xN::zero()" : "W4xN::splat(1ULL, 1ULL)");
            sb.AppendLine($"        prev{id}_ = {init};");
        }
        for (int i = 0; i < Memories.Count; ++i)
        {
            var mem = Memories[i];
            int total = mem.Depth * lanes;
            string initV = twoState || mem.TwoState ? "0ULL" : lang.MaskHex(mem.Width);
            if (total > 4096)
            {
                sb.AppendLine($"        m{i}_a_.assign({total}, {initV});");
                if (!twoState) sb.AppendLine($"        m{i}_b_.assign({total}, {initV});");
            }
            else
            {
                sb.AppendLine($"        m{i}_a_.fill({initV});");
                if (!twoState) sb.AppendLine($"        m{i}_b_.fill({initV});");
            }
        }
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    inline uint64_t time() const { return time_; }");
        sb.AppendLine("    inline void timeInc(uint64_t delta) { time_ += delta; }");
        sb.AppendLine("    inline void final() {}");
        sb.AppendLine();

        foreach (var sig in Signals.Where(s => s.Direction != "internal"))
        {
            int id = SignalId(sig.Name);
            string pn = portNames[sig.Name];
            string m = lang.MaskHex(sig.Width);
            if (sig.Direction == "input")
            {
                if (twoState)
                {
                    sb.AppendLine($"    inline void set_{pn}(int lane, uint64_t value) {{ s{id}_.v[lane] = value & {m}; }}");
                    sb.AppendLine($"    inline void set_{pn}_all(uint64_t value) {{ s{id}_ = Lane::splat(value & {m}); }}");
                    sb.AppendLine($"    inline void set_{pn}_vec(Lane value) {{ s{id}_ = velosim_simd::w2s::mask(value, {m}); }}");
                }
                else
                {
                    string norm = sig.TwoState ? $"W4::lit((value.a & ~value.b) & {m})" : $"W4::from_ab(value.a & {m}, value.b & {m})";
                    string normVec = sig.TwoState ? $"velosim_simd::w4s::two_state(value, {m})" : $"velosim_simd::w4s::resize(value, {m})";
                    sb.AppendLine($"    inline void set_{pn}(int lane, W4 value) {{ W4 v = {norm}; s{id}_.a.v[lane] = v.a; s{id}_.b.v[lane] = v.b; }}");
                    sb.AppendLine($"    inline void set_{pn}_u64(int lane, uint64_t value) {{ s{id}_.a.v[lane] = value & {m}; s{id}_.b.v[lane] = 0ULL; }}");
                    sb.AppendLine($"    inline void set_{pn}_all(W4 value) {{ W4 v = {norm}; s{id}_ = W4xN::splat(v.a, v.b); }}");
                    sb.AppendLine($"    inline void set_{pn}_all_u64(uint64_t value) {{ s{id}_ = W4xN::lit(value & {m}); }}");
                    sb.AppendLine($"    inline void set_{pn}_vec(W4xN value) {{ s{id}_ = {normVec}; }}");
                }
            }
            if (twoState)
            {
                sb.AppendLine($"    inline uint64_t get_{pn}(int lane) const {{ return s{id}_.v[lane]; }}");
                sb.AppendLine($"    inline Lane get_{pn}_vec() const {{ return s{id}_; }}");
                sb.AppendLine($"    inline uint32_t diverged_mask_{pn}(int golden_lane = 0) const {{ return velosim_simd::w2s::diverged_mask(s{id}_, golden_lane); }}");
            }
            else
            {
                sb.AppendLine($"    inline W4 get_{pn}(int lane) const {{ return W4::from_ab(s{id}_.a.v[lane], s{id}_.b.v[lane]); }}");
                sb.AppendLine($"    inline uint64_t get_{pn}_u64(int lane) const {{ return s{id}_.a.v[lane]; }}");
                sb.AppendLine($"    inline W4xN get_{pn}_vec() const {{ return s{id}_; }}");
                sb.AppendLine($"    inline uint32_t diverged_mask_{pn}(int golden_lane = 0) const {{ return velosim_simd::w4s::diverged_mask(s{id}_, golden_lane); }}");
            }
        }
        sb.AppendLine();

        sb.AppendLine($"    {st} memory_read(int mem_id, int lane, size_t index) const {{");
        sb.AppendLine("        switch (mem_id) {");
        for (int i = 0; i < Memories.Count; ++i)
        {
            var mem = Memories[i];
            string m = lang.MaskHex(mem.Width);
            if (twoState)
                sb.AppendLine($"            case {i}: return index < {mem.Depth} ? (m{i}_a_[static_cast<size_t>(lane) * {mem.Depth} + index] & {m}) : 0ULL;");
            else
                sb.AppendLine($"            case {i}: return index < {mem.Depth} ? W4::from_ab(m{i}_a_[static_cast<size_t>(lane) * {mem.Depth} + index] & {m}, m{i}_b_[static_cast<size_t>(lane) * {mem.Depth} + index] & {m}) : W4::from_ab({m}, {m});");
        }
        sb.AppendLine($"            default: return {(twoState ? "0ULL" : "W4::zero()")};");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine($"    void memory_write(int mem_id, int lane, size_t index, {st} value) {{");
        sb.AppendLine("        switch (mem_id) {");
        for (int i = 0; i < Memories.Count; ++i)
        {
            var mem = Memories[i];
            string m = lang.MaskHex(mem.Width);
            if (twoState)
                sb.AppendLine($"            case {i}: if (index < {mem.Depth}) {{ m{i}_a_[static_cast<size_t>(lane) * {mem.Depth} + index] = value & {m};{(combPureInternal ? " comb_dirty_ = true;" : "")} }} break;");
            else
            {
                string va = mem.TwoState ? $"(value.a & ~value.b) & {m}" : $"value.a & {m}";
                string vb = mem.TwoState ? "0ULL" : $"value.b & {m}";
                sb.AppendLine($"            case {i}: if (index < {mem.Depth}) {{ size_t p = static_cast<size_t>(lane) * {mem.Depth} + index; m{i}_a_[p] = {va}; m{i}_b_[p] = {vb};{(combPureInternal ? " comb_dirty_ = true;" : "")} }} break;");
            }
        }
        sb.AppendLine("            default: break;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        EmitRcSimdEval(sb, lang, procs, eventIds, seqIds, combPureInternal);
        sb.AppendLine();
        sb.AppendLine("private:");
        sb.AppendLine("    inline void settle() {");
        sb.AppendLine("        using namespace velosim_simd;");
        foreach (var step in SettledSteps)
        {
            int id = SignalId(Name(step.GetProperty("target")));
            var rhs = step.GetProperty("value");
            int vw = ExpressionWidth(rhs);
            bool exactWidth = vw == Signals[id].Width && !(ExpressionSigned(rhs) && Signals[id].Width > vw);
            string expr = RcSimdExpr(rhs, lang, Signals[id].Width);
            sb.AppendLine($"        s{id}_ = {RcSimdStore(expr, Signals[id], lang, exactWidth ? Signals[id].Width : -1)};");
        }
        sb.AppendLine("    }");

        for (int i = 0; i < Memories.Count; ++i)
        {
            var mem = Memories[i];
            string m = lang.MaskHex(mem.Width);
            if (twoState)
            {
                sb.AppendLine($"    inline Lane read_mem_{i}(Lane idx) const {{");
                sb.AppendLine("        Lane out{};");
                sb.AppendLine("        for (int l = 0; l < kLanes; ++l) {");
                sb.AppendLine($"            size_t ix = static_cast<size_t>(idx.v[l]);");
                sb.AppendLine($"            out.v[l] = ix < {mem.Depth} ? (m{i}_a_[static_cast<size_t>(l) * {mem.Depth} + ix] & {m}) : 0ULL;");
                sb.AppendLine("        }");
                sb.AppendLine("        return out;");
                sb.AppendLine("    }");
            }
            else
            {
                string oob = mem.TwoState ? "0ULL" : m;
                sb.AppendLine($"    inline W4xN read_mem_{i}(W4xN idx) const {{");
                sb.AppendLine("        W4xN out{};");
                sb.AppendLine("        for (int l = 0; l < kLanes; ++l) {");
                sb.AppendLine($"            size_t ix = static_cast<size_t>(idx.a.v[l]);");
                sb.AppendLine($"            if (idx.b.v[l] == 0ULL && ix < {mem.Depth}) {{");
                sb.AppendLine($"                size_t p = static_cast<size_t>(l) * {mem.Depth} + ix;");
                sb.AppendLine($"                out.a.v[l] = m{i}_a_[p] & {m}; out.b.v[l] = m{i}_b_[p] & {m};");
                sb.AppendLine($"            }} else {{ out.a.v[l] = {oob}; out.b.v[l] = {oob}; }}");
                sb.AppendLine("        }");
                sb.AppendLine("        return out;");
                sb.AppendLine("    }");
            }
        }

        sb.AppendLine("    uint64_t time_ = 0;");
        if (combPureInternal) sb.AppendLine("    bool comb_dirty_ = true;");
        for (int i = 0; i < Signals.Count; ++i)
            sb.AppendLine($"    {wt} s{i}_{{}};");
        foreach (int id in eventIds)
            sb.AppendLine($"    {wt} prev{id}_{{}};");
        for (int i = 0; i < Memories.Count; ++i)
        {
            int total = Memories[i].Depth * lanes;
            string ty = total > 4096 ? "std::vector<uint64_t>" : $"std::array<uint64_t, {total}>";
            sb.AppendLine($"    {ty} m{i}_a_{{}};");
            if (!twoState) sb.AppendLine($"    {ty} m{i}_b_{{}};");
        }
        if (Memories.Count > 0)
            sb.AppendLine($"    velosim_simd::{(twoState ? "SimdMemLog2" : "SimdMemLog4")} mem_log_{{}};");
        sb.AppendLine("};");
        return sb.ToString();
    }

    private void EmitRcSimdEval(StringBuilder sb, SimdLang lang, JsonElement[] procs, int[] eventIds, int[] seqIds, bool combPureInternal)
    {
        string self = lang.SelfPrefix;
        string suf = lang.FieldSuffix;
        string ops = lang.Ops;
        int tmp = 0;

        sb.AppendLine(lang.IsRust ? "    #[inline(always)] pub fn eval(&mut self) {" : "    inline void eval() {");
        if (!lang.IsRust) sb.AppendLine("        using namespace velosim_simd;");
        if (combPureInternal)
            sb.AppendLine($"        {lang.If($"{self}comb_dirty{suf}")} {self}settle(); {self}comb_dirty{suf} = false; }}");
        else
            sb.AppendLine($"        {self}settle();");

        if (CombinationalOnly)
        {
            sb.AppendLine("    }");
            return;
        }

        for (int p = 0; p < procs.Length; ++p)
        {
            string acc = "";
            foreach (var tr in procs[p].GetProperty("events").EnumerateArray())
            {
                int id = SignalId(tr.GetProperty("signal").GetString()!);
                bool pos = tr.GetProperty("edge").GetString() == "posedge";
                string term = $"{ops}::edge({lang.Prev(id)}, {lang.Sig(id)}, {(pos ? "true" : "false")})";
                acc = acc.Length == 0 ? term : $"Lane::bor({acc}, {term})";
            }
            sb.AppendLine($"        {lang.LetConst} _ev{p} = {acc};");
        }
        string anyEv = "_ev0";
        for (int p = 1; p < procs.Length; ++p) anyEv = $"Lane::bor({anyEv}, _ev{p})";

        sb.AppendLine($"        {lang.If($"Lane::any({anyEv})")}");
        foreach (int id in seqIds)
            sb.AppendLine($"            {lang.LetMut} _next{id} = {lang.Sig(id)};");
        if (Memories.Count > 0)
            sb.AppendLine($"            {self}mem_log{suf}.clear();");
        for (int p = 0; p < procs.Length; ++p)
        {
            sb.AppendLine($"            {lang.If($"Lane::any(_ev{p})")}");
            EmitRcSimdStatement(sb, procs[p].GetProperty("body"), "                ", $"_ev{p}", lang, ref tmp);
            sb.AppendLine("            }");
        }
        foreach (int id in seqIds)
            sb.AppendLine($"            {lang.Sig(id)} = _next{id};");
        if (Memories.Count > 0)
        {
            if (lang.IsRust)
            {
                sb.AppendLine("            for i in 0..self.mem_log.len() {");
                sb.AppendLine("                let mid = self.mem_log.mem_at(i); let mk = self.mem_log.mask_at(i); let ix = self.mem_log.idx_at(i); let vl = self.mem_log.val_at(i);");
                sb.AppendLine("                match mid {");
                for (int i = 0; i < Memories.Count; ++i)
                {
                    int d = Memories[i].Depth;
                    if (lang.TwoState)
                        sb.AppendLine($"                    {i} => for l in 0..LANES {{ if mk.v[l] != 0 {{ self.m{i}_a[l * {d} + ix.v[l] as usize] = vl.v[l]; }} }},");
                    else
                        sb.AppendLine($"                    {i} => for l in 0..LANES {{ if mk.v[l] != 0 {{ let p = l * {d} + ix.v[l] as usize; self.m{i}_a[p] = vl.a.v[l]; self.m{i}_b[p] = vl.b.v[l]; }} }},");
                }
                sb.AppendLine("                    _ => {}");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
            }
            else
            {
                sb.AppendLine("            for (size_t i = 0; i < mem_log_.size(); ++i) {");
                sb.AppendLine("                const auto& e = mem_log_.at(i);");
                sb.AppendLine("                switch (e.mem) {");
                for (int i = 0; i < Memories.Count; ++i)
                {
                    int d = Memories[i].Depth;
                    if (lang.TwoState)
                        sb.AppendLine($"                    case {i}: for (int l = 0; l < kLanes; ++l) {{ if (e.mask.v[l]) m{i}_a_[static_cast<size_t>(l) * {d} + static_cast<size_t>(e.idx.v[l])] = e.val.v[l]; }} break;");
                    else
                        sb.AppendLine($"                    case {i}: for (int l = 0; l < kLanes; ++l) {{ if (e.mask.v[l]) {{ size_t p = static_cast<size_t>(l) * {d} + static_cast<size_t>(e.idx.v[l]); m{i}_a_[p] = e.val.a.v[l]; m{i}_b_[p] = e.val.b.v[l]; }} }} break;");
                }
                sb.AppendLine("                    default: break;");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
            }
        }
        sb.AppendLine($"            {self}settle();");
        sb.AppendLine("        }");
        foreach (int id in eventIds)
            sb.AppendLine($"        {lang.Prev(id)} = {lang.Sig(id)};");
        sb.AppendLine("    }");
    }

    // =========================================================================
    // Rust Lockstep SIMD Runtime (src/simd_runtime.rs, #![forbid(unsafe_code)])
    // =========================================================================

    public static string EmitRustSimdRuntime(int lanes)
    {
        ValidateSimdLanes(lanes);
        int align = lanes == 8 ? 64 : 32;
        return $$"""
#![forbid(unsafe_code)]
#![allow(dead_code, clippy::all)]

pub const LANES: usize = {{lanes}};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct W4 {
    pub a: u64,
    pub b: u64,
}

impl W4 {
    #[inline(always)] pub const fn zero() -> Self { Self { a: 0, b: 0 } }
    #[inline(always)] pub const fn lit(v: u64) -> Self { Self { a: v, b: 0 } }
    #[inline(always)] pub const fn new(a: u64, b: u64) -> Self { Self { a, b } }
    #[inline(always)] pub const fn unknown(m: u64) -> Self { Self { a: m, b: m } }
}

#[repr(align({{align}}))]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Lane {
    pub v: [u64; LANES],
}

impl Default for Lane {
    #[inline(always)] fn default() -> Self { Self::zero() }
}

impl Lane {
    #[inline(always)] pub const fn zero() -> Self { Self { v: [0u64; LANES] } }
    #[inline(always)] pub const fn all() -> Self { Self { v: [u64::MAX; LANES] } }
    #[inline(always)] pub const fn splat(x: u64) -> Self { Self { v: [x; LANES] } }
    #[inline(always)] pub fn band(a: Self, b: Self) -> Self {
        let mut r = [0u64; LANES];
        for i in 0..LANES { r[i] = a.v[i] & b.v[i]; }
        Self { v: r }
    }
    #[inline(always)] pub fn bor(a: Self, b: Self) -> Self {
        let mut r = [0u64; LANES];
        for i in 0..LANES { r[i] = a.v[i] | b.v[i]; }
        Self { v: r }
    }
    #[inline(always)] pub fn bnot(a: Self) -> Self {
        let mut r = [0u64; LANES];
        for i in 0..LANES { r[i] = !a.v[i]; }
        Self { v: r }
    }
    #[inline(always)] pub fn any(a: Self) -> bool {
        let mut acc = 0u64;
        for i in 0..LANES { acc |= a.v[i]; }
        acc != 0
    }
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct W4xN {
    pub a: Lane,
    pub b: Lane,
}

impl W4xN {
    #[inline(always)] pub const fn zero() -> Self { Self { a: Lane::zero(), b: Lane::zero() } }
    #[inline(always)] pub const fn lit(v: u64) -> Self { Self { a: Lane::splat(v), b: Lane::zero() } }
    #[inline(always)] pub const fn splat(a: u64, b: u64) -> Self { Self { a: Lane::splat(a), b: Lane::splat(b) } }
}

#[inline(always)] fn mask_w(w: i32) -> u64 { if w <= 0 { 0 } else if w >= 64 { u64::MAX } else { (1u64 << w) - 1 } }
#[inline(always)] fn sext_u64(v: u64, w: i32) -> i64 { if w <= 0 { 0 } else if w >= 64 { v as i64 } else { ((v << (64 - w)) as i64) >> (64 - w) } }

pub mod w2s {
    use super::{Lane, LANES, mask_w, sext_u64};

    #[inline(always)] pub fn mask(a: Lane, m: u64) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = a.v[i] & m; } Lane { v: r } }
    #[inline(always)] pub fn sel(m: Lane, t: Lane, f: Lane) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = (t.v[i] & m.v[i]) | (f.v[i] & !m.v[i]); } Lane { v: r } }
    #[inline(always)] pub fn yes_mask(a: Lane) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = if a.v[i] != 0 { u64::MAX } else { 0 }; } Lane { v: r } }
    #[inline(always)] pub fn edge(prev: Lane, cur: Lane, posedge: bool) -> Lane {
        let mut r = [0u64; LANES];
        for i in 0..LANES {
            let hit = if posedge { (prev.v[i] & 1) == 0 && (cur.v[i] & 1) != 0 } else { (prev.v[i] & 1) != 0 && (cur.v[i] & 1) == 0 };
            r[i] = if hit { u64::MAX } else { 0 };
        }
        Lane { v: r }
    }
    #[inline(always)] pub fn diverged_mask(a: Lane, golden: usize) -> u32 {
        let g = a.v[golden];
        let mut m = 0u32;
        for i in 0..LANES { if a.v[i] != g { m |= 1u32 << i; } }
        m
    }
    #[inline(always)] pub fn sresize(a: Lane, from_w: i32, from_m: u64, to_m: u64) -> Lane {
        let mut r = [0u64; LANES];
        let sign = if from_w > 0 && from_w < 64 { 1u64 << (from_w - 1) } else { 0 };
        let fill = to_m & !from_m;
        for i in 0..LANES {
            let v = a.v[i] & from_m;
            r[i] = if sign != 0 && (v & sign) != 0 { (v | fill) & to_m } else { v & to_m };
        }
        Lane { v: r }
    }
    #[inline(always)] pub fn bit_not(a: Lane, m: u64) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = (!a.v[i]) & m; } Lane { v: r } }
    #[inline(always)] pub fn neg(a: Lane, m: u64) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = a.v[i].wrapping_neg() & m; } Lane { v: r } }
    #[inline(always)] pub fn lnot(a: Lane) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = if a.v[i] == 0 { 1 } else { 0 }; } Lane { v: r } }
    #[inline(always)] pub fn bit_and(a: Lane, b: Lane, m: u64) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = (a.v[i] & b.v[i]) & m; } Lane { v: r } }
    #[inline(always)] pub fn bit_or(a: Lane, b: Lane, m: u64) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = (a.v[i] | b.v[i]) & m; } Lane { v: r } }
    #[inline(always)] pub fn bit_xor(a: Lane, b: Lane, m: u64) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = (a.v[i] ^ b.v[i]) & m; } Lane { v: r } }
    #[inline(always)] pub fn bit_xnor(a: Lane, b: Lane, m: u64) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = (!(a.v[i] ^ b.v[i])) & m; } Lane { v: r } }
    #[inline(always)] pub fn land(a: Lane, b: Lane) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = if a.v[i] != 0 && b.v[i] != 0 { 1 } else { 0 }; } Lane { v: r } }
    #[inline(always)] pub fn lor(a: Lane, b: Lane) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = if a.v[i] != 0 || b.v[i] != 0 { 1 } else { 0 }; } Lane { v: r } }
    #[inline(always)] pub fn add(a: Lane, b: Lane, m: u64) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = a.v[i].wrapping_add(b.v[i]) & m; } Lane { v: r } }
    #[inline(always)] pub fn sub(a: Lane, b: Lane, m: u64) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = a.v[i].wrapping_sub(b.v[i]) & m; } Lane { v: r } }
    #[inline(always)] pub fn product(a: Lane, b: Lane, m: u64, op: i32) -> Lane {
        let mut r = [0u64; LANES];
        for i in 0..LANES {
            let x = a.v[i] & m; let y = b.v[i] & m;
            r[i] = match op { 0 => x.wrapping_mul(y) & m, 1 => if y == 0 { 0 } else { (x / y) & m }, _ => if y == 0 { 0 } else { (x % y) & m } };
        }
        Lane { v: r }
    }
    #[inline(always)] pub fn sproduct(a: Lane, b: Lane, w: i32, op: i32) -> Lane {
        let m = mask_w(w);
        let mut r = [0u64; LANES];
        for i in 0..LANES {
            let x = sext_u64(a.v[i], w); let y = sext_u64(b.v[i], w);
            r[i] = match op { 0 => (x.wrapping_mul(y) as u64) & m, 1 => if y == 0 { 0 } else { (x.wrapping_div(y) as u64) & m }, _ => if y == 0 { 0 } else { (x.wrapping_rem(y) as u64) & m } };
        }
        Lane { v: r }
    }
    #[inline(always)] pub fn shl_c(a: Lane, k: i32, m: u64) -> Lane {
        let mut r = [0u64; LANES];
        if k < 64 { for i in 0..LANES { r[i] = (a.v[i] << k) & m; } }
        Lane { v: r }
    }
    #[inline(always)] pub fn shr_c(a: Lane, k: i32, m: u64) -> Lane {
        let mut r = [0u64; LANES];
        if k < 64 { for i in 0..LANES { r[i] = ((a.v[i] & m) >> k) & m; } }
        Lane { v: r }
    }
    #[inline(always)] pub fn shl(a: Lane, b: Lane, m: u64) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = if b.v[i] >= 64 { 0 } else { (a.v[i] << b.v[i]) & m }; } Lane { v: r } }
    #[inline(always)] pub fn shr(a: Lane, b: Lane, m: u64) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = if b.v[i] >= 64 { 0 } else { ((a.v[i] & m) >> b.v[i]) & m }; } Lane { v: r } }
    #[inline(always)] pub fn ashr(a: Lane, b: Lane, w: i32, m: u64) -> Lane {
        let mut r = [0u64; LANES];
        for i in 0..LANES {
            let s = sext_u64(a.v[i], w);
            let k = if b.v[i] >= 63 { 63 } else { b.v[i] as u32 };
            r[i] = ((s >> k) as u64) & m;
        }
        Lane { v: r }
    }
    #[inline(always)] pub fn eq(a: Lane, b: Lane, m: u64) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = if (a.v[i] & m) == (b.v[i] & m) { 1 } else { 0 }; } Lane { v: r } }
    #[inline(always)] pub fn case_eq(a: Lane, b: Lane, m: u64) -> Lane { eq(a, b, m) }
    #[inline(always)] pub fn casez_eq(a: Lane, b: Lane, m: u64) -> Lane { eq(a, b, m) }
    #[inline(always)] pub fn casex_eq(a: Lane, b: Lane, m: u64) -> Lane { eq(a, b, m) }
    #[inline(always)] pub fn wild_eq(a: Lane, b: Lane, m: u64) -> Lane { eq(a, b, m) }
    #[inline(always)] pub fn cmp(a: Lane, b: Lane, op: i32) -> Lane {
        let mut r = [0u64; LANES];
        for i in 0..LANES {
            let hit = match op { 0 => a.v[i] < b.v[i], 1 => a.v[i] <= b.v[i], 2 => a.v[i] > b.v[i], _ => a.v[i] >= b.v[i] };
            r[i] = if hit { 1 } else { 0 };
        }
        Lane { v: r }
    }
    #[inline(always)] pub fn scmp(a: Lane, b: Lane, w: i32, op: i32) -> Lane {
        let mut r = [0u64; LANES];
        for i in 0..LANES {
            let x = sext_u64(a.v[i], w); let y = sext_u64(b.v[i], w);
            let hit = match op { 0 => x < y, 1 => x <= y, 2 => x > y, _ => x >= y };
            r[i] = if hit { 1 } else { 0 };
        }
        Lane { v: r }
    }
    #[inline(always)] pub fn cond(c: Lane, a: Lane, b: Lane, m: u64) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = (if c.v[i] != 0 { a.v[i] } else { b.v[i] }) & m; } Lane { v: r } }
    #[inline(always)] pub fn concat(a: Lane, b: Lane, bw: i32, bm: u64) -> Lane {
        let mut r = [0u64; LANES];
        if bw >= 64 { for i in 0..LANES { r[i] = b.v[i] & bm; } }
        else { for i in 0..LANES { r[i] = (a.v[i] << bw) | (b.v[i] & bm); } }
        Lane { v: r }
    }
    #[inline(always)] pub fn sel_part_fast(a: Lane, right: i32, m: u64) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = (a.v[i] >> right) & m; } Lane { v: r } }
    #[inline(always)] pub fn sel_part(a: Lane, aw: i32, left: i32, right: i32) -> Lane {
        if right >= aw { return Lane::zero(); }
        let n = left - right + 1;
        let am = mask_w((aw - right).min(n));
        let mut r = [0u64; LANES];
        for i in 0..LANES { r[i] = (a.v[i] >> right) & am; }
        Lane { v: r }
    }
    #[inline(always)] pub fn sel_bit(a: Lane, aw: i32, idx: Lane) -> Lane {
        let mut r = [0u64; LANES];
        for i in 0..LANES { r[i] = if idx.v[i] < aw as u64 { (a.v[i] >> idx.v[i]) & 1 } else { 0 }; }
        Lane { v: r }
    }
    #[inline(always)] pub fn sel_idx(a: Lane, aw: i32, b: Lane, width: i32, desc: bool) -> Lane {
        let mut r = [0u64; LANES];
        for i in 0..LANES {
            let lo = if desc { b.v[i] as i64 - (width - 1) as i64 } else { b.v[i] as i64 };
            let src_lo = lo.max(0);
            let src_hi = (lo + width as i64).min(aw as i64);
            if src_lo < src_hi {
                let count = (src_hi - src_lo) as i32;
                let dst_lo = (src_lo - lo) as u32;
                r[i] = ((a.v[i] >> src_lo as u32) & mask_w(count)) << dst_lo;
            }
        }
        Lane { v: r }
    }
    #[inline(always)] pub fn dep_idx(before: Lane, bw: i32, b: Lane, width: i32, desc: bool, val: Lane) -> Lane {
        let wm = mask_w(width);
        let mut r = [0u64; LANES];
        for i in 0..LANES {
            let mut cur = before.v[i];
            let lo = if desc { b.v[i] as i64 - (width - 1) as i64 } else { b.v[i] as i64 };
            let dst_lo = lo.max(0);
            let dst_hi = (lo + width as i64).min(bw as i64);
            if dst_lo < dst_hi {
                let count = (dst_hi - dst_lo) as i32;
                let src_lo = (dst_lo - lo) as u32;
                let d_lo = dst_lo as u32;
                let cm = mask_w(count);
                let dm = cm << d_lo;
                cur = (cur & !dm) | ((((val.v[i] & wm) >> src_lo) & cm) << d_lo);
            }
            r[i] = cur;
        }
        Lane { v: r }
    }
    #[inline(always)] pub fn mem_idx(idx: Lane, lower: i32) -> Lane {
        let lo = lower as u64;
        let mut r = [0u64; LANES];
        for i in 0..LANES { r[i] = if idx.v[i] < lo { u64::MAX } else { idx.v[i] - lo }; }
        Lane { v: r }
    }
    #[inline(always)] pub fn red(a: Lane, m: u64, op: i32) -> Lane {
        let mut r = [0u64; LANES];
        for i in 0..LANES {
            let v = a.v[i] & m;
            r[i] = match op {
                0 => if v == m { 1 } else { 0 },
                1 => if v == m { 0 } else { 1 },
                2 => if v != 0 { 1 } else { 0 },
                3 => if v != 0 { 0 } else { 1 },
                4 => (v.count_ones() & 1) as u64,
                _ => ((v.count_ones() & 1) ^ 1) as u64,
            };
        }
        Lane { v: r }
    }
    #[inline(always)] pub fn countones(a: Lane, m: u64) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = (a.v[i] & m).count_ones() as u64; } Lane { v: r } }
    #[inline(always)] pub fn onehot(a: Lane, m: u64) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = if (a.v[i] & m).count_ones() == 1 { 1 } else { 0 }; } Lane { v: r } }
    #[inline(always)] pub fn onehot0(a: Lane, m: u64) -> Lane { let mut r = [0u64; LANES]; for i in 0..LANES { r[i] = if (a.v[i] & m).count_ones() <= 1 { 1 } else { 0 }; } Lane { v: r } }
    #[inline(always)] pub fn clog2(a: Lane, m: u64) -> Lane {
        let mut r = [0u64; LANES];
        for i in 0..LANES { let x = a.v[i] & m; r[i] = if x <= 1 { 0 } else { (64 - (x - 1).leading_zeros()) as u64 }; }
        Lane { v: r }
    }
}

pub mod w4s {
    use super::{Lane, W4xN, LANES, mask_w, sext_u64, w2s};

    #[inline(always)] pub fn resize(a: W4xN, m: u64) -> W4xN { W4xN { a: w2s::mask(a.a, m), b: w2s::mask(a.b, m) } }
    #[inline(always)] pub fn two_state(a: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES];
        for i in 0..LANES { ra[i] = (a.a.v[i] & !a.b.v[i]) & m; }
        W4xN { a: Lane { v: ra }, b: Lane::zero() }
    }
    #[inline(always)] pub fn sel(m: Lane, t: W4xN, f: W4xN) -> W4xN { W4xN { a: w2s::sel(m, t.a, f.a), b: w2s::sel(m, t.b, f.b) } }
    #[inline(always)] pub fn yes_mask(a: W4xN) -> Lane {
        let mut r = [0u64; LANES];
        for i in 0..LANES { r[i] = if (a.a.v[i] & !a.b.v[i]) != 0 { u64::MAX } else { 0 }; }
        Lane { v: r }
    }
    #[inline(always)] pub fn edge(prev: W4xN, cur: W4xN, posedge: bool) -> Lane {
        let mut r = [0u64; LANES];
        for i in 0..LANES {
            let p0 = ((prev.a.v[i] | prev.b.v[i]) & 1) == 0;
            let p1 = ((prev.a.v[i] & !prev.b.v[i]) & 1) != 0;
            let c0 = ((cur.a.v[i] | cur.b.v[i]) & 1) == 0;
            let c1 = ((cur.a.v[i] & !cur.b.v[i]) & 1) != 0;
            let hit = if posedge { (p0 && !c0) || (!p1 && c1) } else { (p1 && !c1) || (!p0 && c0) };
            r[i] = if hit { u64::MAX } else { 0 };
        }
        Lane { v: r }
    }
    #[inline(always)] pub fn diverged_mask(a: W4xN, golden: usize) -> u32 {
        let ga = a.a.v[golden]; let gb = a.b.v[golden];
        let mut m = 0u32;
        for i in 0..LANES { if a.a.v[i] != ga || a.b.v[i] != gb { m |= 1u32 << i; } }
        m
    }
    #[inline(always)] pub fn sresize(a: W4xN, from_w: i32, from_m: u64, to_m: u64) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        let sign = if from_w > 0 && from_w < 64 { 1u64 << (from_w - 1) } else { 0 };
        let fill = to_m & !from_m;
        for i in 0..LANES {
            let va = a.a.v[i] & from_m; let vb = a.b.v[i] & from_m;
            ra[i] = if sign != 0 && (va & sign) != 0 { (va | fill) & to_m } else { va & to_m };
            rb[i] = if sign != 0 && (vb & sign) != 0 { (vb | fill) & to_m } else { vb & to_m };
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn bit_not(a: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES { let u = a.b.v[i] & m; ra[i] = ((!a.a.v[i]) | u) & m; rb[i] = u; }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn neg(a: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            let unk = if (a.b.v[i] & m) != 0 { m } else { 0 };
            ra[i] = (a.a.v[i].wrapping_neg() & m) | unk;
            rb[i] = unk;
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn lnot(a: W4xN) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            let is1 = ((a.a.v[i] & !a.b.v[i]) != 0) as u64;
            let not1 = is1 ^ 1;
            ra[i] = not1;
            rb[i] = ((a.b.v[i] != 0) as u64) & not1;
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn bit_and(a: W4xN, b: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            let u = (a.a.v[i] | a.b.v[i]) & (b.a.v[i] | b.b.v[i]) & (a.b.v[i] | b.b.v[i]) & m;
            ra[i] = ((a.a.v[i] & b.a.v[i]) | u) & m;
            rb[i] = u;
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn bit_or(a: W4xN, b: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            let one = (a.a.v[i] & !a.b.v[i]) | (b.a.v[i] & !b.b.v[i]);
            let ab = a.b.v[i] | b.b.v[i];
            ra[i] = (one | ab) & m;
            rb[i] = (ab & !one) & m;
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn bit_xor(a: W4xN, b: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES { let u = (a.b.v[i] | b.b.v[i]) & m; ra[i] = ((a.a.v[i] ^ b.a.v[i]) | u) & m; rb[i] = u; }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn bit_xnor(a: W4xN, b: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES { let u = (a.b.v[i] | b.b.v[i]) & m; ra[i] = ((!(a.a.v[i] ^ b.a.v[i])) | u) & m; rb[i] = u; }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn land(a: W4xN, b: W4xN) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            let a1 = ((a.a.v[i] & !a.b.v[i]) != 0) as u64;
            let b1 = ((b.a.v[i] & !b.b.v[i]) != 0) as u64;
            let a0 = ((a.a.v[i] | a.b.v[i]) == 0) as u64;
            let b0 = ((b.a.v[i] | b.b.v[i]) == 0) as u64;
            let is0 = a0 | b0;
            let is1 = a1 & b1;
            ra[i] = is0 ^ 1;
            rb[i] = (is0 | is1) ^ 1;
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn lor(a: W4xN, b: W4xN) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            let a1 = ((a.a.v[i] & !a.b.v[i]) != 0) as u64;
            let b1 = ((b.a.v[i] & !b.b.v[i]) != 0) as u64;
            let a0 = ((a.a.v[i] | a.b.v[i]) == 0) as u64;
            let b0 = ((b.a.v[i] | b.b.v[i]) == 0) as u64;
            let is1 = a1 | b1;
            let is0 = a0 & b0;
            ra[i] = is0 ^ 1;
            rb[i] = (is0 | is1) ^ 1;
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn add(a: W4xN, b: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            let unk = if ((a.b.v[i] | b.b.v[i]) & m) != 0 { m } else { 0 };
            ra[i] = (a.a.v[i].wrapping_add(b.a.v[i]) & m) | unk;
            rb[i] = unk;
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn sub(a: W4xN, b: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            let unk = if ((a.b.v[i] | b.b.v[i]) & m) != 0 { m } else { 0 };
            ra[i] = (a.a.v[i].wrapping_sub(b.a.v[i]) & m) | unk;
            rb[i] = unk;
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn product(a: W4xN, b: W4xN, m: u64, op: i32) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        if op == 0 {
            for i in 0..LANES {
                let unk = if ((a.b.v[i] | b.b.v[i]) & m) != 0 { m } else { 0 };
                ra[i] = (a.a.v[i].wrapping_mul(b.a.v[i]) & m) | unk;
                rb[i] = unk;
            }
            return W4xN { a: Lane { v: ra }, b: Lane { v: rb } };
        }
        for i in 0..LANES {
            let x = a.a.v[i] & m; let y = b.a.v[i] & m;
            if ((a.b.v[i] | b.b.v[i]) & m) != 0 || y == 0 { ra[i] = m; rb[i] = m; }
            else { ra[i] = (if op == 1 { x / y } else { x % y }) & m; rb[i] = 0; }
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn sproduct(a: W4xN, b: W4xN, w: i32, op: i32) -> W4xN {
        let m = mask_w(w);
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        if op == 0 {
            for i in 0..LANES {
                let unk = if ((a.b.v[i] | b.b.v[i]) & m) != 0 { m } else { 0 };
                ra[i] = (a.a.v[i].wrapping_mul(b.a.v[i]) & m) | unk;
                rb[i] = unk;
            }
            return W4xN { a: Lane { v: ra }, b: Lane { v: rb } };
        }
        for i in 0..LANES {
            let x = sext_u64(a.a.v[i], w); let y = sext_u64(b.a.v[i], w);
            if ((a.b.v[i] | b.b.v[i]) & m) != 0 || y == 0 { ra[i] = m; rb[i] = m; }
            else { ra[i] = (if op == 1 { x.wrapping_div(y) } else { x.wrapping_rem(y) } as u64) & m; rb[i] = 0; }
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn shl_c(a: W4xN, k: i32, m: u64) -> W4xN { W4xN { a: w2s::shl_c(a.a, k, m), b: w2s::shl_c(a.b, k, m) } }
    #[inline(always)] pub fn shr_c(a: W4xN, k: i32, m: u64) -> W4xN { W4xN { a: w2s::shr_c(a.a, k, m), b: w2s::shr_c(a.b, k, m) } }
    #[inline(always)] pub fn shl(a: W4xN, b: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            if b.b.v[i] != 0 { ra[i] = m; rb[i] = m; }
            else if b.a.v[i] >= 64 { ra[i] = 0; rb[i] = 0; }
            else { ra[i] = (a.a.v[i] << b.a.v[i]) & m; rb[i] = (a.b.v[i] << b.a.v[i]) & m; }
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn shr(a: W4xN, b: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            if b.b.v[i] != 0 { ra[i] = m; rb[i] = m; }
            else if b.a.v[i] >= 64 { ra[i] = 0; rb[i] = 0; }
            else { ra[i] = ((a.a.v[i] & m) >> b.a.v[i]) & m; rb[i] = ((a.b.v[i] & m) >> b.a.v[i]) & m; }
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn ashr(a: W4xN, b: W4xN, w: i32, m: u64) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            if b.b.v[i] != 0 { ra[i] = m; rb[i] = m; }
            else {
                let k = if b.a.v[i] >= 63 { 63 } else { b.a.v[i] as u32 };
                ra[i] = ((sext_u64(a.a.v[i], w) >> k) as u64) & m;
                rb[i] = ((sext_u64(a.b.v[i], w) >> k) as u64) & m;
            }
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn eq(a: W4xN, b: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            let u = (a.b.v[i] | b.b.v[i]) & m;
            let neq = ((((a.a.v[i] ^ b.a.v[i]) & !u) & m) != 0) as u64;
            let eq_or_u = neq ^ 1;
            ra[i] = eq_or_u;
            rb[i] = ((u != 0) as u64) & eq_or_u;
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn case_eq(a: W4xN, b: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES];
        for i in 0..LANES { ra[i] = if ((a.a.v[i] ^ b.a.v[i]) & m) == 0 && ((a.b.v[i] ^ b.b.v[i]) & m) == 0 { 1 } else { 0 }; }
        W4xN { a: Lane { v: ra }, b: Lane::zero() }
    }
    #[inline(always)] pub fn casez_eq(a: W4xN, b: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES];
        for i in 0..LANES {
            let dc = (((!a.a.v[i]) & a.b.v[i]) | ((!b.a.v[i]) & b.b.v[i])) & m;
            let cm = m & !dc;
            ra[i] = if ((a.a.v[i] ^ b.a.v[i]) & cm) == 0 && ((a.b.v[i] ^ b.b.v[i]) & cm) == 0 { 1 } else { 0 };
        }
        W4xN { a: Lane { v: ra }, b: Lane::zero() }
    }
    #[inline(always)] pub fn casex_eq(a: W4xN, b: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES];
        for i in 0..LANES {
            let cm = m & !(a.b.v[i] | b.b.v[i]);
            ra[i] = if ((a.a.v[i] ^ b.a.v[i]) & cm) == 0 { 1 } else { 0 };
        }
        W4xN { a: Lane { v: ra }, b: Lane::zero() }
    }
    #[inline(always)] pub fn wild_eq(a: W4xN, b: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            let care = m & !b.b.v[i];
            let au = a.b.v[i] & care;
            let neq = ((((a.a.v[i] ^ b.a.v[i]) & care) & !au) != 0) as u64;
            let eq_or_u = neq ^ 1;
            ra[i] = eq_or_u;
            rb[i] = ((au != 0) as u64) & eq_or_u;
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn cmp(a: W4xN, b: W4xN, op: i32) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            let unk = ((a.b.v[i] | b.b.v[i]) != 0) as u64;
            let hit = (match op { 0 => a.a.v[i] < b.a.v[i], 1 => a.a.v[i] <= b.a.v[i], 2 => a.a.v[i] > b.a.v[i], _ => a.a.v[i] >= b.a.v[i] }) as u64;
            ra[i] = hit | unk;
            rb[i] = unk;
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn scmp(a: W4xN, b: W4xN, w: i32, op: i32) -> W4xN {
        let m = mask_w(w);
        let flip = if w > 0 { 1u64 << (w - 1) } else { 0 };
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            let unk = ((a.b.v[i] | b.b.v[i]) != 0) as u64;
            let x = (a.a.v[i] & m) ^ flip;
            let y = (b.a.v[i] & m) ^ flip;
            let hit = (match op { 0 => x < y, 1 => x <= y, 2 => x > y, _ => x >= y }) as u64;
            ra[i] = hit | unk;
            rb[i] = unk;
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn cond(c: W4xN, a: W4xN, b: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            let is1 = if (c.a.v[i] & !c.b.v[i]) != 0 { u64::MAX } else { 0 };
            let is0 = if (c.a.v[i] | c.b.v[i]) == 0 { u64::MAX } else { 0 };
            let is_u = !(is1 | is0);
            let aa = a.a.v[i] & m; let ab = a.b.v[i] & m;
            let ba = b.a.v[i] & m; let bb = b.b.v[i] & m;
            let diff = ((aa ^ ba) | (ab ^ bb)) & m;
            ra[i] = (is1 & aa) | (is0 & ba) | (is_u & (aa | diff));
            rb[i] = (is1 & ab) | (is0 & bb) | (is_u & (ab | diff));
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn concat(a: W4xN, b: W4xN, bw: i32, bm: u64) -> W4xN { W4xN { a: w2s::concat(a.a, b.a, bw, bm), b: w2s::concat(a.b, b.b, bw, bm) } }
    #[inline(always)] pub fn sel_part_fast(a: W4xN, right: i32, m: u64) -> W4xN { W4xN { a: w2s::sel_part_fast(a.a, right, m), b: w2s::sel_part_fast(a.b, right, m) } }
    #[inline(always)] pub fn sel_part(a: W4xN, aw: i32, left: i32, right: i32) -> W4xN {
        let n = left - right + 1; let m = mask_w(n);
        if right >= aw { return W4xN::splat(m, m); }
        let am = mask_w((aw - right).min(n)); let oob = m & !am;
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES { ra[i] = ((a.a.v[i] >> right) & am) | oob; rb[i] = ((a.b.v[i] >> right) & am) | oob; }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn sel_bit(a: W4xN, aw: i32, idx: W4xN) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            if idx.b.v[i] != 0 || idx.a.v[i] >= aw as u64 { ra[i] = 1; rb[i] = 1; }
            else { ra[i] = (a.a.v[i] >> idx.a.v[i]) & 1; rb[i] = (a.b.v[i] >> idx.a.v[i]) & 1; }
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn sel_idx(a: W4xN, aw: i32, b: W4xN, width: i32, desc: bool) -> W4xN {
        let wm = mask_w(width);
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            if b.b.v[i] != 0 { ra[i] = wm; rb[i] = wm; continue; }
            let lo = if desc { b.a.v[i] as i64 - (width - 1) as i64 } else { b.a.v[i] as i64 };
            let src_lo = lo.max(0); let src_hi = (lo + width as i64).min(aw as i64);
            if src_lo >= src_hi { ra[i] = wm; rb[i] = wm; continue; }
            let count = (src_hi - src_lo) as i32; let dst_lo = (src_lo - lo) as u32; let s_lo = src_lo as u32;
            let cm = mask_w(count); let oob = wm & !(cm << dst_lo);
            ra[i] = (((a.a.v[i] >> s_lo) & cm) << dst_lo) | oob;
            rb[i] = (((a.b.v[i] >> s_lo) & cm) << dst_lo) | oob;
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn dep_idx(before: W4xN, bw: i32, b: W4xN, width: i32, desc: bool, val: W4xN) -> W4xN {
        let wm = mask_w(width);
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            ra[i] = before.a.v[i]; rb[i] = before.b.v[i];
            if b.b.v[i] != 0 { continue; }
            let lo = if desc { b.a.v[i] as i64 - (width - 1) as i64 } else { b.a.v[i] as i64 };
            let dst_lo = lo.max(0); let dst_hi = (lo + width as i64).min(bw as i64);
            if dst_lo >= dst_hi { continue; }
            let count = (dst_hi - dst_lo) as i32; let src_lo = (dst_lo - lo) as u32; let d_lo = dst_lo as u32;
            let cm = mask_w(count); let dm = cm << d_lo;
            ra[i] = (ra[i] & !dm) | ((((val.a.v[i] & wm) >> src_lo) & cm) << d_lo);
            rb[i] = (rb[i] & !dm) | ((((val.b.v[i] & wm) >> src_lo) & cm) << d_lo);
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn mem_idx(idx: W4xN, lower: i32) -> W4xN {
        let lo = lower as u64;
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            if idx.b.v[i] != 0 || idx.a.v[i] < lo { ra[i] = 1; rb[i] = 1; } else { ra[i] = idx.a.v[i] - lo; rb[i] = 0; }
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn red(a: W4xN, m: u64, op: i32) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            let u = a.b.v[i] & m; let v = a.a.v[i] & !u & m;
            if op == 0 || op == 1 {
                if (!(v | u) & m) != 0 { ra[i] = if op == 1 { 1 } else { 0 }; rb[i] = 0; }
                else if u != 0 { ra[i] = 1; rb[i] = 1; }
                else { ra[i] = if op == 0 { 1 } else { 0 }; rb[i] = 0; }
            } else if op == 2 || op == 3 {
                if v != 0 { ra[i] = if op == 2 { 1 } else { 0 }; rb[i] = 0; }
                else if u != 0 { ra[i] = 1; rb[i] = 1; }
                else { ra[i] = if op == 3 { 1 } else { 0 }; rb[i] = 0; }
            } else if u != 0 { ra[i] = 1; rb[i] = 1; }
            else { ra[i] = (((v.count_ones() & 1) != 0) ^ (op == 5)) as u64; rb[i] = 0; }
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn countones(a: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES];
        for i in 0..LANES { ra[i] = (a.a.v[i] & !a.b.v[i] & m).count_ones() as u64; }
        W4xN { a: Lane { v: ra }, b: Lane::zero() }
    }
    #[inline(always)] pub fn onehot(a: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES];
        for i in 0..LANES { ra[i] = if (a.a.v[i] & !a.b.v[i] & m).count_ones() == 1 { 1 } else { 0 }; }
        W4xN { a: Lane { v: ra }, b: Lane::zero() }
    }
    #[inline(always)] pub fn onehot0(a: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES];
        for i in 0..LANES { ra[i] = if (a.a.v[i] & !a.b.v[i] & m).count_ones() <= 1 { 1 } else { 0 }; }
        W4xN { a: Lane { v: ra }, b: Lane::zero() }
    }
    #[inline(always)] pub fn isunknown(a: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES];
        for i in 0..LANES { ra[i] = if (a.b.v[i] & m) != 0 { 1 } else { 0 }; }
        W4xN { a: Lane { v: ra }, b: Lane::zero() }
    }
    #[inline(always)] pub fn clog2(a: W4xN, m: u64) -> W4xN {
        let mut ra = [0u64; LANES];
        for i in 0..LANES {
            if (a.b.v[i] & m) != 0 { ra[i] = 0; }
            else { let x = a.a.v[i] & m; ra[i] = if x <= 1 { 0 } else { (64 - (x - 1).leading_zeros()) as u64 }; }
        }
        W4xN { a: Lane { v: ra }, b: Lane::zero() }
    }
    #[inline(always)] pub fn resolve_pair(a: W4xN, b: W4xN, nk: i32, m: u64) -> W4xN {
        let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
        for i in 0..LANES {
            let va = a.a.v[i] & !a.b.v[i] & m; let za = !a.a.v[i] & a.b.v[i] & m; let a0 = !a.a.v[i] & !a.b.v[i] & m;
            let vb = b.a.v[i] & !b.b.v[i] & m; let zb = !b.a.v[i] & b.b.v[i] & m; let b0 = !b.a.v[i] & !b.b.v[i] & m;
            let z_out = za & zb;
            let (v_out, o0) = if nk == 1 {
                let o0 = (a0 | b0) & m; ((!o0) & ((va & (vb | zb)) | (za & vb)) & m, o0)
            } else if nk == 2 {
                let v_out = (va | vb) & m; (v_out, (!v_out) & ((a0 & (b0 | zb)) | (za & b0)) & m)
            } else {
                (((va & (vb | zb)) | (za & vb)) & m, ((a0 & (b0 | zb)) | (za & b0)) & m)
            };
            let x_out = !(z_out | v_out | o0) & m;
            ra[i] = (v_out | x_out) & m; rb[i] = (x_out | z_out) & m;
        }
        W4xN { a: Lane { v: ra }, b: Lane { v: rb } }
    }
    #[inline(always)] pub fn resolve_finalize(acc: W4xN, nk: i32, m: u64) -> W4xN {
        if nk == 3 {
            let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
            for i in 0..LANES { ra[i] = acc.a.v[i] & m; rb[i] = (acc.a.v[i] & acc.b.v[i]) & m; }
            return W4xN { a: Lane { v: ra }, b: Lane { v: rb } };
        }
        if nk == 4 {
            let mut ra = [0u64; LANES]; let mut rb = [0u64; LANES];
            for i in 0..LANES { ra[i] = (acc.a.v[i] | acc.b.v[i]) & m; rb[i] = (acc.a.v[i] & acc.b.v[i]) & m; }
            return W4xN { a: Lane { v: ra }, b: Lane { v: rb } };
        }
        acc
    }
}

pub struct SimdMemLog2 { entries: Vec<(usize, Lane, Lane, Lane)> }
impl SimdMemLog2 {
    pub fn new() -> Self { Self { entries: Vec::with_capacity(8) } }
    #[inline(always)] pub fn len(&self) -> usize { self.entries.len() }
    #[inline(always)] pub fn mem_at(&self, i: usize) -> usize { self.entries[i].0 }
    #[inline(always)] pub fn mask_at(&self, i: usize) -> Lane { self.entries[i].1 }
    #[inline(always)] pub fn idx_at(&self, i: usize) -> Lane { self.entries[i].2 }
    #[inline(always)] pub fn val_at(&self, i: usize) -> Lane { self.entries[i].3 }
    #[inline(always)] pub fn push(&mut self, mem_id: usize, depth: usize, active: Lane, idx: Lane, val: Lane) {
        let mut ok = [0u64; LANES];
        let mut any = 0u64;
        for i in 0..LANES {
            if active.v[i] != 0 && (idx.v[i] as usize) < depth { ok[i] = u64::MAX; any = 1; }
        }
        if any != 0 { self.entries.push((mem_id, Lane { v: ok }, idx, val)); }
    }
    #[inline(always)] pub fn clear(&mut self) { self.entries.clear(); }
}

pub struct SimdMemLog4 { entries: Vec<(usize, Lane, Lane, W4xN)> }
impl SimdMemLog4 {
    pub fn new() -> Self { Self { entries: Vec::with_capacity(8) } }
    #[inline(always)] pub fn len(&self) -> usize { self.entries.len() }
    #[inline(always)] pub fn mem_at(&self, i: usize) -> usize { self.entries[i].0 }
    #[inline(always)] pub fn mask_at(&self, i: usize) -> Lane { self.entries[i].1 }
    #[inline(always)] pub fn idx_at(&self, i: usize) -> Lane { self.entries[i].2 }
    #[inline(always)] pub fn val_at(&self, i: usize) -> W4xN { self.entries[i].3 }
    #[inline(always)] pub fn push(&mut self, mem_id: usize, depth: usize, active: Lane, idx: W4xN, val: W4xN) {
        let mut ok = [0u64; LANES];
        let mut any = 0u64;
        for i in 0..LANES {
            if active.v[i] != 0 && idx.b.v[i] == 0 && (idx.a.v[i] as usize) < depth { ok[i] = u64::MAX; any = 1; }
        }
        if any != 0 { self.entries.push((mem_id, Lane { v: ok }, idx.a, val)); }
    }
    #[inline(always)] pub fn clear(&mut self) { self.entries.clear(); }
}
""";
    }

    // =========================================================================
    // C++20 Lockstep SIMD Runtime (simd_runtime.h)
    // =========================================================================

    public static string EmitCppSimdRuntimeHeader(int lanes)
    {
        ValidateSimdLanes(lanes);
        int align = lanes == 8 ? 64 : 32;
        return $$"""
#pragma once
#include <algorithm>
#include <bit>
#include <cstddef>
#include <cstdint>
#include <vector>

namespace velosim_simd {

inline constexpr int kLanes = {{lanes}};

struct W4 {
    uint64_t a = 0;
    uint64_t b = 0;
    static constexpr W4 zero() { return {0ULL, 0ULL}; }
    static constexpr W4 lit(uint64_t v) { return {v, 0ULL}; }
    static constexpr W4 from_ab(uint64_t a_, uint64_t b_) { return {a_, b_}; }
    static constexpr W4 unknown(uint64_t m) { return {m, m}; }
    constexpr bool operator==(const W4& o) const { return a == o.a && b == o.b; }
    constexpr bool operator!=(const W4& o) const { return !(*this == o); }
};

struct alignas({{align}}) Lane {
    uint64_t v[kLanes]{};
    static constexpr Lane zero() { return Lane{}; }
    static constexpr Lane all() {
        Lane r{};
        for (int i = 0; i < kLanes; ++i) r.v[i] = 0xffffffffffffffffULL;
        return r;
    }
    static constexpr Lane splat(uint64_t x) {
        Lane r{};
        for (int i = 0; i < kLanes; ++i) r.v[i] = x;
        return r;
    }
    static inline Lane band(Lane a, Lane b) {
        Lane r{};
        for (int i = 0; i < kLanes; ++i) r.v[i] = a.v[i] & b.v[i];
        return r;
    }
    static inline Lane bor(Lane a, Lane b) {
        Lane r{};
        for (int i = 0; i < kLanes; ++i) r.v[i] = a.v[i] | b.v[i];
        return r;
    }
    static inline Lane bnot(Lane a) {
        Lane r{};
        for (int i = 0; i < kLanes; ++i) r.v[i] = ~a.v[i];
        return r;
    }
    static inline bool any(Lane a) {
        uint64_t acc = 0;
        for (int i = 0; i < kLanes; ++i) acc |= a.v[i];
        return acc != 0;
    }
};

struct W4xN {
    Lane a{};
    Lane b{};
    static constexpr W4xN zero() { return {Lane::zero(), Lane::zero()}; }
    static constexpr W4xN lit(uint64_t v) { return {Lane::splat(v), Lane::zero()}; }
    static constexpr W4xN splat(uint64_t a_, uint64_t b_) { return {Lane::splat(a_), Lane::splat(b_)}; }
};

inline constexpr uint64_t mask_w(int w) { return w <= 0 ? 0ULL : (w >= 64 ? 0xffffffffffffffffULL : ((1ULL << w) - 1ULL)); }
inline constexpr int64_t sext_u64(uint64_t v, int w) {
    if (w <= 0) return 0;
    if (w >= 64) return static_cast<int64_t>(v);
    int sh = 64 - w;
    return static_cast<int64_t>(v << sh) >> sh;
}

namespace w2s {
inline Lane mask(Lane a, uint64_t m) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = a.v[i] & m; return r; }
inline Lane sel(Lane m, Lane t, Lane f) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = (t.v[i] & m.v[i]) | (f.v[i] & ~m.v[i]); return r; }
inline Lane yes_mask(Lane a) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = a.v[i] != 0ULL ? 0xffffffffffffffffULL : 0ULL; return r; }
inline Lane edge(Lane prev, Lane cur, bool posedge) {
    Lane r{};
    for (int i = 0; i < kLanes; ++i) {
        bool hit = posedge ? ((prev.v[i] & 1ULL) == 0ULL && (cur.v[i] & 1ULL) != 0ULL)
                           : ((prev.v[i] & 1ULL) != 0ULL && (cur.v[i] & 1ULL) == 0ULL);
        r.v[i] = hit ? 0xffffffffffffffffULL : 0ULL;
    }
    return r;
}
inline uint32_t diverged_mask(Lane a, int golden) {
    uint64_t g = a.v[golden]; uint32_t m = 0;
    for (int i = 0; i < kLanes; ++i) if (a.v[i] != g) m |= (1U << i);
    return m;
}
inline Lane sresize(Lane a, int from_w, uint64_t from_m, uint64_t to_m) {
    Lane r{};
    uint64_t sign = (from_w > 0 && from_w < 64) ? (1ULL << (from_w - 1)) : 0ULL;
    uint64_t fill = to_m & ~from_m;
    for (int i = 0; i < kLanes; ++i) { uint64_t v = a.v[i] & from_m; r.v[i] = (sign && (v & sign)) ? ((v | fill) & to_m) : (v & to_m); }
    return r;
}
inline Lane bit_not(Lane a, uint64_t m) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = (~a.v[i]) & m; return r; }
inline Lane neg(Lane a, uint64_t m) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = (0ULL - a.v[i]) & m; return r; }
inline Lane lnot(Lane a) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = a.v[i] == 0ULL ? 1ULL : 0ULL; return r; }
inline Lane bit_and(Lane a, Lane b, uint64_t m) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = (a.v[i] & b.v[i]) & m; return r; }
inline Lane bit_or(Lane a, Lane b, uint64_t m) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = (a.v[i] | b.v[i]) & m; return r; }
inline Lane bit_xor(Lane a, Lane b, uint64_t m) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = (a.v[i] ^ b.v[i]) & m; return r; }
inline Lane bit_xnor(Lane a, Lane b, uint64_t m) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = (~(a.v[i] ^ b.v[i])) & m; return r; }
inline Lane land(Lane a, Lane b) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = (a.v[i] && b.v[i]) ? 1ULL : 0ULL; return r; }
inline Lane lor(Lane a, Lane b) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = (a.v[i] || b.v[i]) ? 1ULL : 0ULL; return r; }
inline Lane add(Lane a, Lane b, uint64_t m) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = (a.v[i] + b.v[i]) & m; return r; }
inline Lane sub(Lane a, Lane b, uint64_t m) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = (a.v[i] - b.v[i]) & m; return r; }
inline Lane product(Lane a, Lane b, uint64_t m, int op) {
    Lane r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t x = a.v[i] & m, y = b.v[i] & m;
        r.v[i] = op == 0 ? ((x * y) & m) : (op == 1 ? (y ? ((x / y) & m) : 0ULL) : (y ? ((x % y) & m) : 0ULL));
    }
    return r;
}
inline Lane sproduct(Lane a, Lane b, int w, int op) {
    uint64_t m = mask_w(w); Lane r{};
    for (int i = 0; i < kLanes; ++i) {
        int64_t x = sext_u64(a.v[i], w), y = sext_u64(b.v[i], w);
        if (op == 0) r.v[i] = static_cast<uint64_t>(x * y) & m;
        else if (y == 0) r.v[i] = 0ULL;
        else if (x == INT64_MIN && y == -1) r.v[i] = op == 1 ? (static_cast<uint64_t>(x) & m) : 0ULL;
        else r.v[i] = static_cast<uint64_t>(op == 1 ? (x / y) : (x % y)) & m;
    }
    return r;
}
inline Lane shl_c(Lane a, int k, uint64_t m) { Lane r{}; if (k < 64) for (int i = 0; i < kLanes; ++i) r.v[i] = (a.v[i] << k) & m; return r; }
inline Lane shr_c(Lane a, int k, uint64_t m) { Lane r{}; if (k < 64) for (int i = 0; i < kLanes; ++i) r.v[i] = ((a.v[i] & m) >> k) & m; return r; }
inline Lane shl(Lane a, Lane b, uint64_t m) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = b.v[i] >= 64 ? 0ULL : ((a.v[i] << b.v[i]) & m); return r; }
inline Lane shr(Lane a, Lane b, uint64_t m) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = b.v[i] >= 64 ? 0ULL : (((a.v[i] & m) >> b.v[i]) & m); return r; }
inline Lane ashr(Lane a, Lane b, int w, uint64_t m) {
    Lane r{};
    for (int i = 0; i < kLanes; ++i) {
        int64_t s = sext_u64(a.v[i], w);
        int k = b.v[i] >= 63 ? 63 : static_cast<int>(b.v[i]);
        r.v[i] = static_cast<uint64_t>(s >> k) & m;
    }
    return r;
}
inline Lane eq(Lane a, Lane b, uint64_t m) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = ((a.v[i] & m) == (b.v[i] & m)) ? 1ULL : 0ULL; return r; }
inline Lane case_eq(Lane a, Lane b, uint64_t m) { return eq(a, b, m); }
inline Lane casez_eq(Lane a, Lane b, uint64_t m) { return eq(a, b, m); }
inline Lane casex_eq(Lane a, Lane b, uint64_t m) { return eq(a, b, m); }
inline Lane wild_eq(Lane a, Lane b, uint64_t m) { return eq(a, b, m); }
inline Lane cmp(Lane a, Lane b, int op) {
    Lane r{};
    for (int i = 0; i < kLanes; ++i) {
        bool hit = op == 0 ? (a.v[i] < b.v[i]) : op == 1 ? (a.v[i] <= b.v[i]) : op == 2 ? (a.v[i] > b.v[i]) : (a.v[i] >= b.v[i]);
        r.v[i] = hit ? 1ULL : 0ULL;
    }
    return r;
}
inline Lane scmp(Lane a, Lane b, int w, int op) {
    Lane r{};
    for (int i = 0; i < kLanes; ++i) {
        int64_t x = sext_u64(a.v[i], w), y = sext_u64(b.v[i], w);
        bool hit = op == 0 ? (x < y) : op == 1 ? (x <= y) : op == 2 ? (x > y) : (x >= y);
        r.v[i] = hit ? 1ULL : 0ULL;
    }
    return r;
}
inline Lane cond(Lane c, Lane a, Lane b, uint64_t m) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = (c.v[i] ? a.v[i] : b.v[i]) & m; return r; }
inline Lane concat(Lane a, Lane b, int bw, uint64_t bm) {
    Lane r{};
    if (bw >= 64) { for (int i = 0; i < kLanes; ++i) r.v[i] = b.v[i] & bm; }
    else { for (int i = 0; i < kLanes; ++i) r.v[i] = (a.v[i] << bw) | (b.v[i] & bm); }
    return r;
}
inline Lane sel_part_fast(Lane a, int right, uint64_t m) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = (a.v[i] >> right) & m; return r; }
inline Lane sel_part(Lane a, int aw, int left, int right) {
    if (right >= aw) return Lane::zero();
    int n = left - right + 1;
    uint64_t am = mask_w(std::min(aw - right, n));
    Lane r{};
    for (int i = 0; i < kLanes; ++i) r.v[i] = (a.v[i] >> right) & am;
    return r;
}
inline Lane sel_bit(Lane a, int aw, Lane idx) {
    Lane r{};
    for (int i = 0; i < kLanes; ++i) r.v[i] = idx.v[i] < static_cast<uint64_t>(aw) ? ((a.v[i] >> idx.v[i]) & 1ULL) : 0ULL;
    return r;
}
inline Lane sel_idx(Lane a, int aw, Lane b, int width, bool desc) {
    Lane r{};
    for (int i = 0; i < kLanes; ++i) {
        int64_t lo = desc ? (static_cast<int64_t>(b.v[i]) - (width - 1)) : static_cast<int64_t>(b.v[i]);
        int64_t src_lo = std::max<int64_t>(0, lo), src_hi = std::min<int64_t>(aw, lo + width);
        if (src_lo < src_hi) {
            int count = static_cast<int>(src_hi - src_lo), dst_lo = static_cast<int>(src_lo - lo);
            r.v[i] = ((a.v[i] >> src_lo) & mask_w(count)) << dst_lo;
        }
    }
    return r;
}
inline Lane dep_idx(Lane before, int bw, Lane b, int width, bool desc, Lane val) {
    uint64_t wm = mask_w(width); Lane r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t cur = before.v[i];
        int64_t lo = desc ? (static_cast<int64_t>(b.v[i]) - (width - 1)) : static_cast<int64_t>(b.v[i]);
        int64_t dst_lo = std::max<int64_t>(0, lo), dst_hi = std::min<int64_t>(bw, lo + width);
        if (dst_lo < dst_hi) {
            int count = static_cast<int>(dst_hi - dst_lo), src_lo = static_cast<int>(dst_lo - lo), d_lo = static_cast<int>(dst_lo);
            uint64_t cm = mask_w(count), dm = cm << d_lo;
            cur = (cur & ~dm) | ((((val.v[i] & wm) >> src_lo) & cm) << d_lo);
        }
        r.v[i] = cur;
    }
    return r;
}
inline Lane mem_idx(Lane idx, int lower) {
    uint64_t lo = static_cast<uint64_t>(lower); Lane r{};
    for (int i = 0; i < kLanes; ++i) r.v[i] = idx.v[i] < lo ? 0xffffffffffffffffULL : (idx.v[i] - lo);
    return r;
}
inline Lane red(Lane a, uint64_t m, int op) {
    Lane r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t v = a.v[i] & m;
        if (op == 0) r.v[i] = v == m ? 1ULL : 0ULL;
        else if (op == 1) r.v[i] = v == m ? 0ULL : 1ULL;
        else if (op == 2) r.v[i] = v != 0ULL ? 1ULL : 0ULL;
        else if (op == 3) r.v[i] = v != 0ULL ? 0ULL : 1ULL;
        else if (op == 4) r.v[i] = static_cast<uint64_t>(std::popcount(v) & 1);
        else r.v[i] = static_cast<uint64_t>((std::popcount(v) & 1) ^ 1);
    }
    return r;
}
inline Lane countones(Lane a, uint64_t m) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = static_cast<uint64_t>(std::popcount(a.v[i] & m)); return r; }
inline Lane onehot(Lane a, uint64_t m) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = std::popcount(a.v[i] & m) == 1 ? 1ULL : 0ULL; return r; }
inline Lane onehot0(Lane a, uint64_t m) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = std::popcount(a.v[i] & m) <= 1 ? 1ULL : 0ULL; return r; }
inline Lane clog2(Lane a, uint64_t m) {
    Lane r{};
    for (int i = 0; i < kLanes; ++i) { uint64_t x = a.v[i] & m; r.v[i] = x <= 1ULL ? 0ULL : static_cast<uint64_t>(64 - std::countl_zero(x - 1ULL)); }
    return r;
}
} // namespace w2s

namespace w4s {
inline W4xN resize(W4xN a, uint64_t m) { return {w2s::mask(a.a, m), w2s::mask(a.b, m)}; }
inline W4xN two_state(W4xN a, uint64_t m) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) r.a.v[i] = (a.a.v[i] & ~a.b.v[i]) & m;
    return r;
}
inline W4xN sel(Lane m, W4xN t, W4xN f) { return {w2s::sel(m, t.a, f.a), w2s::sel(m, t.b, f.b)}; }
inline Lane yes_mask(W4xN a) { Lane r{}; for (int i = 0; i < kLanes; ++i) r.v[i] = (a.a.v[i] & ~a.b.v[i]) ? 0xffffffffffffffffULL : 0ULL; return r; }
inline Lane edge(W4xN prev, W4xN cur, bool posedge) {
    Lane r{};
    for (int i = 0; i < kLanes; ++i) {
        bool p0 = ((prev.a.v[i] | prev.b.v[i]) & 1ULL) == 0ULL;
        bool p1 = ((prev.a.v[i] & ~prev.b.v[i]) & 1ULL) != 0ULL;
        bool c0 = ((cur.a.v[i] | cur.b.v[i]) & 1ULL) == 0ULL;
        bool c1 = ((cur.a.v[i] & ~cur.b.v[i]) & 1ULL) != 0ULL;
        bool hit = posedge ? ((p0 && !c0) || (!p1 && c1)) : ((p1 && !c1) || (!p0 && c0));
        r.v[i] = hit ? 0xffffffffffffffffULL : 0ULL;
    }
    return r;
}
inline uint32_t diverged_mask(W4xN a, int golden) {
    uint64_t ga = a.a.v[golden], gb = a.b.v[golden]; uint32_t m = 0;
    for (int i = 0; i < kLanes; ++i) if (a.a.v[i] != ga || a.b.v[i] != gb) m |= (1U << i);
    return m;
}
inline W4xN sresize(W4xN a, int from_w, uint64_t from_m, uint64_t to_m) {
    W4xN r{};
    uint64_t sign = (from_w > 0 && from_w < 64) ? (1ULL << (from_w - 1)) : 0ULL;
    uint64_t fill = to_m & ~from_m;
    for (int i = 0; i < kLanes; ++i) {
        uint64_t va = a.a.v[i] & from_m, vb = a.b.v[i] & from_m;
        r.a.v[i] = (sign && (va & sign)) ? ((va | fill) & to_m) : (va & to_m);
        r.b.v[i] = (sign && (vb & sign)) ? ((vb | fill) & to_m) : (vb & to_m);
    }
    return r;
}
inline W4xN bit_not(W4xN a, uint64_t m) { W4xN r{}; for (int i = 0; i < kLanes; ++i) { uint64_t u = a.b.v[i] & m; r.a.v[i] = ((~a.a.v[i]) | u) & m; r.b.v[i] = u; } return r; }
inline W4xN neg(W4xN a, uint64_t m) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t unk = (a.b.v[i] & m) ? m : 0ULL;
        r.a.v[i] = ((0ULL - a.a.v[i]) & m) | unk;
        r.b.v[i] = unk;
    }
    return r;
}
inline W4xN lnot(W4xN a) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t is1 = (a.a.v[i] & ~a.b.v[i]) ? 1ULL : 0ULL;
        uint64_t not1 = is1 ^ 1ULL;
        r.a.v[i] = not1;
        r.b.v[i] = (a.b.v[i] ? 1ULL : 0ULL) & not1;
    }
    return r;
}
inline W4xN bit_and(W4xN a, W4xN b, uint64_t m) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t u = (a.a.v[i] | a.b.v[i]) & (b.a.v[i] | b.b.v[i]) & (a.b.v[i] | b.b.v[i]) & m;
        r.a.v[i] = ((a.a.v[i] & b.a.v[i]) | u) & m;
        r.b.v[i] = u;
    }
    return r;
}
inline W4xN bit_or(W4xN a, W4xN b, uint64_t m) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t one = (a.a.v[i] & ~a.b.v[i]) | (b.a.v[i] & ~b.b.v[i]);
        uint64_t ab = a.b.v[i] | b.b.v[i];
        r.a.v[i] = (one | ab) & m;
        r.b.v[i] = (ab & ~one) & m;
    }
    return r;
}
inline W4xN bit_xor(W4xN a, W4xN b, uint64_t m) { W4xN r{}; for (int i = 0; i < kLanes; ++i) { uint64_t u = (a.b.v[i] | b.b.v[i]) & m; r.a.v[i] = ((a.a.v[i] ^ b.a.v[i]) | u) & m; r.b.v[i] = u; } return r; }
inline W4xN bit_xnor(W4xN a, W4xN b, uint64_t m) { W4xN r{}; for (int i = 0; i < kLanes; ++i) { uint64_t u = (a.b.v[i] | b.b.v[i]) & m; r.a.v[i] = ((~(a.a.v[i] ^ b.a.v[i])) | u) & m; r.b.v[i] = u; } return r; }
inline W4xN land(W4xN a, W4xN b) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t a1 = (a.a.v[i] & ~a.b.v[i]) ? 1ULL : 0ULL;
        uint64_t b1 = (b.a.v[i] & ~b.b.v[i]) ? 1ULL : 0ULL;
        uint64_t a0 = ((a.a.v[i] | a.b.v[i]) == 0ULL) ? 1ULL : 0ULL;
        uint64_t b0 = ((b.a.v[i] | b.b.v[i]) == 0ULL) ? 1ULL : 0ULL;
        uint64_t is0 = a0 | b0;
        uint64_t is1 = a1 & b1;
        r.a.v[i] = is0 ^ 1ULL;
        r.b.v[i] = (is0 | is1) ^ 1ULL;
    }
    return r;
}
inline W4xN lor(W4xN a, W4xN b) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t a1 = (a.a.v[i] & ~a.b.v[i]) ? 1ULL : 0ULL;
        uint64_t b1 = (b.a.v[i] & ~b.b.v[i]) ? 1ULL : 0ULL;
        uint64_t a0 = ((a.a.v[i] | a.b.v[i]) == 0ULL) ? 1ULL : 0ULL;
        uint64_t b0 = ((b.a.v[i] | b.b.v[i]) == 0ULL) ? 1ULL : 0ULL;
        uint64_t is1 = a1 | b1;
        uint64_t is0 = a0 & b0;
        r.a.v[i] = is0 ^ 1ULL;
        r.b.v[i] = (is0 | is1) ^ 1ULL;
    }
    return r;
}
inline W4xN add(W4xN a, W4xN b, uint64_t m) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t unk = ((a.b.v[i] | b.b.v[i]) & m) ? m : 0ULL;
        r.a.v[i] = ((a.a.v[i] + b.a.v[i]) & m) | unk;
        r.b.v[i] = unk;
    }
    return r;
}
inline W4xN sub(W4xN a, W4xN b, uint64_t m) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t unk = ((a.b.v[i] | b.b.v[i]) & m) ? m : 0ULL;
        r.a.v[i] = ((a.a.v[i] - b.a.v[i]) & m) | unk;
        r.b.v[i] = unk;
    }
    return r;
}
inline W4xN product(W4xN a, W4xN b, uint64_t m, int op) {
    W4xN r{};
    if (op == 0) {
        for (int i = 0; i < kLanes; ++i) {
            uint64_t unk = ((a.b.v[i] | b.b.v[i]) & m) ? m : 0ULL;
            r.a.v[i] = ((a.a.v[i] * b.a.v[i]) & m) | unk;
            r.b.v[i] = unk;
        }
        return r;
    }
    for (int i = 0; i < kLanes; ++i) {
        uint64_t x = a.a.v[i] & m, y = b.a.v[i] & m;
        if (((a.b.v[i] | b.b.v[i]) & m) || y == 0ULL) { r.a.v[i] = m; r.b.v[i] = m; }
        else { r.a.v[i] = (op == 1 ? (x / y) : (x % y)) & m; r.b.v[i] = 0ULL; }
    }
    return r;
}
inline W4xN sproduct(W4xN a, W4xN b, int w, int op) {
    uint64_t m = mask_w(w); W4xN r{};
    if (op == 0) {
        for (int i = 0; i < kLanes; ++i) {
            uint64_t unk = ((a.b.v[i] | b.b.v[i]) & m) ? m : 0ULL;
            r.a.v[i] = ((a.a.v[i] * b.a.v[i]) & m) | unk;
            r.b.v[i] = unk;
        }
        return r;
    }
    for (int i = 0; i < kLanes; ++i) {
        int64_t x = sext_u64(a.a.v[i], w), y = sext_u64(b.a.v[i], w);
        if (((a.b.v[i] | b.b.v[i]) & m) || y == 0) { r.a.v[i] = m; r.b.v[i] = m; }
        else if (x == INT64_MIN && y == -1) { r.a.v[i] = op == 1 ? (static_cast<uint64_t>(x) & m) : 0ULL; r.b.v[i] = 0ULL; }
        else { r.a.v[i] = static_cast<uint64_t>(op == 1 ? (x / y) : (x % y)) & m; r.b.v[i] = 0ULL; }
    }
    return r;
}
inline W4xN shl_c(W4xN a, int k, uint64_t m) { return {w2s::shl_c(a.a, k, m), w2s::shl_c(a.b, k, m)}; }
inline W4xN shr_c(W4xN a, int k, uint64_t m) { return {w2s::shr_c(a.a, k, m), w2s::shr_c(a.b, k, m)}; }
inline W4xN shl(W4xN a, W4xN b, uint64_t m) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        if (b.b.v[i]) { r.a.v[i] = m; r.b.v[i] = m; }
        else if (b.a.v[i] >= 64) { r.a.v[i] = 0; r.b.v[i] = 0; }
        else { r.a.v[i] = (a.a.v[i] << b.a.v[i]) & m; r.b.v[i] = (a.b.v[i] << b.a.v[i]) & m; }
    }
    return r;
}
inline W4xN shr(W4xN a, W4xN b, uint64_t m) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        if (b.b.v[i]) { r.a.v[i] = m; r.b.v[i] = m; }
        else if (b.a.v[i] >= 64) { r.a.v[i] = 0; r.b.v[i] = 0; }
        else { r.a.v[i] = ((a.a.v[i] & m) >> b.a.v[i]) & m; r.b.v[i] = ((a.b.v[i] & m) >> b.a.v[i]) & m; }
    }
    return r;
}
inline W4xN ashr(W4xN a, W4xN b, int w, uint64_t m) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        if (b.b.v[i]) { r.a.v[i] = m; r.b.v[i] = m; }
        else {
            int k = b.a.v[i] >= 63 ? 63 : static_cast<int>(b.a.v[i]);
            r.a.v[i] = static_cast<uint64_t>(sext_u64(a.a.v[i], w) >> k) & m;
            r.b.v[i] = static_cast<uint64_t>(sext_u64(a.b.v[i], w) >> k) & m;
        }
    }
    return r;
}
inline W4xN eq(W4xN a, W4xN b, uint64_t m) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t u = (a.b.v[i] | b.b.v[i]) & m;
        uint64_t neq = (((a.a.v[i] ^ b.a.v[i]) & ~u) & m) ? 1ULL : 0ULL;
        uint64_t eq_or_u = neq ^ 1ULL;
        r.a.v[i] = eq_or_u;
        r.b.v[i] = (u ? 1ULL : 0ULL) & eq_or_u;
    }
    return r;
}
inline W4xN case_eq(W4xN a, W4xN b, uint64_t m) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) r.a.v[i] = (((a.a.v[i] ^ b.a.v[i]) & m) == 0 && ((a.b.v[i] ^ b.b.v[i]) & m) == 0) ? 1ULL : 0ULL;
    return r;
}
inline W4xN casez_eq(W4xN a, W4xN b, uint64_t m) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t dc = (((~a.a.v[i]) & a.b.v[i]) | ((~b.a.v[i]) & b.b.v[i])) & m, cm = m & ~dc;
        r.a.v[i] = (((a.a.v[i] ^ b.a.v[i]) & cm) == 0 && ((a.b.v[i] ^ b.b.v[i]) & cm) == 0) ? 1ULL : 0ULL;
    }
    return r;
}
inline W4xN casex_eq(W4xN a, W4xN b, uint64_t m) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) { uint64_t cm = m & ~(a.b.v[i] | b.b.v[i]); r.a.v[i] = (((a.a.v[i] ^ b.a.v[i]) & cm) == 0) ? 1ULL : 0ULL; }
    return r;
}
inline W4xN wild_eq(W4xN a, W4xN b, uint64_t m) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t care = m & ~b.b.v[i], au = a.b.v[i] & care;
        uint64_t neq = (((a.a.v[i] ^ b.a.v[i]) & care) & ~au) ? 1ULL : 0ULL;
        uint64_t eq_or_u = neq ^ 1ULL;
        r.a.v[i] = eq_or_u;
        r.b.v[i] = (au ? 1ULL : 0ULL) & eq_or_u;
    }
    return r;
}
inline W4xN cmp(W4xN a, W4xN b, int op) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t unk = (a.b.v[i] | b.b.v[i]) ? 1ULL : 0ULL;
        bool hit = op == 0 ? (a.a.v[i] < b.a.v[i]) : op == 1 ? (a.a.v[i] <= b.a.v[i]) : op == 2 ? (a.a.v[i] > b.a.v[i]) : (a.a.v[i] >= b.a.v[i]);
        r.a.v[i] = (hit ? 1ULL : 0ULL) | unk;
        r.b.v[i] = unk;
    }
    return r;
}
inline W4xN scmp(W4xN a, W4xN b, int w, int op) {
    uint64_t m = mask_w(w);
    uint64_t flip = w > 0 ? (1ULL << (w - 1)) : 0ULL;
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t unk = (a.b.v[i] | b.b.v[i]) ? 1ULL : 0ULL;
        uint64_t x = (a.a.v[i] & m) ^ flip, y = (b.a.v[i] & m) ^ flip;
        bool hit = op == 0 ? (x < y) : op == 1 ? (x <= y) : op == 2 ? (x > y) : (x >= y);
        r.a.v[i] = (hit ? 1ULL : 0ULL) | unk;
        r.b.v[i] = unk;
    }
    return r;
}
inline W4xN cond(W4xN c, W4xN a, W4xN b, uint64_t m) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t is1 = (c.a.v[i] & ~c.b.v[i]) ? ~0ULL : 0ULL;
        uint64_t is0 = ((c.a.v[i] | c.b.v[i]) == 0ULL) ? ~0ULL : 0ULL;
        uint64_t is_u = ~(is1 | is0);
        uint64_t aa = a.a.v[i] & m, ab = a.b.v[i] & m;
        uint64_t ba = b.a.v[i] & m, bb = b.b.v[i] & m;
        uint64_t diff = ((aa ^ ba) | (ab ^ bb)) & m;
        r.a.v[i] = (is1 & aa) | (is0 & ba) | (is_u & (aa | diff));
        r.b.v[i] = (is1 & ab) | (is0 & bb) | (is_u & (ab | diff));
    }
    return r;
}
inline W4xN concat(W4xN a, W4xN b, int bw, uint64_t bm) { return {w2s::concat(a.a, b.a, bw, bm), w2s::concat(a.b, b.b, bw, bm)}; }
inline W4xN sel_part_fast(W4xN a, int right, uint64_t m) { return {w2s::sel_part_fast(a.a, right, m), w2s::sel_part_fast(a.b, right, m)}; }
inline W4xN sel_part(W4xN a, int aw, int left, int right) {
    int n = left - right + 1; uint64_t m = mask_w(n);
    if (right >= aw) return W4xN::splat(m, m);
    uint64_t am = mask_w(std::min(aw - right, n)), oob = m & ~am;
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) { r.a.v[i] = ((a.a.v[i] >> right) & am) | oob; r.b.v[i] = ((a.b.v[i] >> right) & am) | oob; }
    return r;
}
inline W4xN sel_bit(W4xN a, int aw, W4xN idx) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        if (idx.b.v[i] || idx.a.v[i] >= static_cast<uint64_t>(aw)) { r.a.v[i] = 1; r.b.v[i] = 1; }
        else { r.a.v[i] = (a.a.v[i] >> idx.a.v[i]) & 1ULL; r.b.v[i] = (a.b.v[i] >> idx.a.v[i]) & 1ULL; }
    }
    return r;
}
inline W4xN sel_idx(W4xN a, int aw, W4xN b, int width, bool desc) {
    uint64_t wm = mask_w(width); W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        if (b.b.v[i]) { r.a.v[i] = wm; r.b.v[i] = wm; continue; }
        int64_t lo = desc ? (static_cast<int64_t>(b.a.v[i]) - (width - 1)) : static_cast<int64_t>(b.a.v[i]);
        int64_t src_lo = std::max<int64_t>(0, lo), src_hi = std::min<int64_t>(aw, lo + width);
        if (src_lo >= src_hi) { r.a.v[i] = wm; r.b.v[i] = wm; continue; }
        int count = static_cast<int>(src_hi - src_lo), dst_lo = static_cast<int>(src_lo - lo), s_lo = static_cast<int>(src_lo);
        uint64_t cm = mask_w(count), oob = wm & ~(cm << dst_lo);
        r.a.v[i] = (((a.a.v[i] >> s_lo) & cm) << dst_lo) | oob;
        r.b.v[i] = (((a.b.v[i] >> s_lo) & cm) << dst_lo) | oob;
    }
    return r;
}
inline W4xN dep_idx(W4xN before, int bw, W4xN b, int width, bool desc, W4xN val) {
    uint64_t wm = mask_w(width); W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        r.a.v[i] = before.a.v[i]; r.b.v[i] = before.b.v[i];
        if (b.b.v[i]) continue;
        int64_t lo = desc ? (static_cast<int64_t>(b.a.v[i]) - (width - 1)) : static_cast<int64_t>(b.a.v[i]);
        int64_t dst_lo = std::max<int64_t>(0, lo), dst_hi = std::min<int64_t>(bw, lo + width);
        if (dst_lo >= dst_hi) continue;
        int count = static_cast<int>(dst_hi - dst_lo), src_lo = static_cast<int>(dst_lo - lo), d_lo = static_cast<int>(dst_lo);
        uint64_t cm = mask_w(count), dm = cm << d_lo;
        r.a.v[i] = (r.a.v[i] & ~dm) | ((((val.a.v[i] & wm) >> src_lo) & cm) << d_lo);
        r.b.v[i] = (r.b.v[i] & ~dm) | ((((val.b.v[i] & wm) >> src_lo) & cm) << d_lo);
    }
    return r;
}
inline W4xN mem_idx(W4xN idx, int lower) {
    uint64_t lo = static_cast<uint64_t>(lower); W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        if (idx.b.v[i] || idx.a.v[i] < lo) { r.a.v[i] = 1; r.b.v[i] = 1; } else { r.a.v[i] = idx.a.v[i] - lo; r.b.v[i] = 0; }
    }
    return r;
}
inline W4xN red(W4xN a, uint64_t m, int op) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t u = a.b.v[i] & m, v = a.a.v[i] & ~u & m;
        if (op == 0 || op == 1) {
            if ((~(v | u) & m) != 0ULL) { r.a.v[i] = op == 1 ? 1ULL : 0ULL; r.b.v[i] = 0ULL; }
            else if (u) { r.a.v[i] = 1ULL; r.b.v[i] = 1ULL; }
            else { r.a.v[i] = op == 0 ? 1ULL : 0ULL; r.b.v[i] = 0ULL; }
        } else if (op == 2 || op == 3) {
            if (v) { r.a.v[i] = op == 2 ? 1ULL : 0ULL; r.b.v[i] = 0ULL; }
            else if (u) { r.a.v[i] = 1ULL; r.b.v[i] = 1ULL; }
            else { r.a.v[i] = op == 3 ? 1ULL : 0ULL; r.b.v[i] = 0ULL; }
        } else if (u) { r.a.v[i] = 1ULL; r.b.v[i] = 1ULL; }
        else { r.a.v[i] = (((std::popcount(v) & 1) != 0) ^ (op == 5)) ? 1ULL : 0ULL; r.b.v[i] = 0ULL; }
    }
    return r;
}
inline W4xN countones(W4xN a, uint64_t m) { W4xN r{}; for (int i = 0; i < kLanes; ++i) r.a.v[i] = static_cast<uint64_t>(std::popcount(a.a.v[i] & ~a.b.v[i] & m)); return r; }
inline W4xN onehot(W4xN a, uint64_t m) { W4xN r{}; for (int i = 0; i < kLanes; ++i) r.a.v[i] = std::popcount(a.a.v[i] & ~a.b.v[i] & m) == 1 ? 1ULL : 0ULL; return r; }
inline W4xN onehot0(W4xN a, uint64_t m) { W4xN r{}; for (int i = 0; i < kLanes; ++i) r.a.v[i] = std::popcount(a.a.v[i] & ~a.b.v[i] & m) <= 1 ? 1ULL : 0ULL; return r; }
inline W4xN isunknown(W4xN a, uint64_t m) { W4xN r{}; for (int i = 0; i < kLanes; ++i) r.a.v[i] = (a.b.v[i] & m) ? 1ULL : 0ULL; return r; }
inline W4xN clog2(W4xN a, uint64_t m) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        if (a.b.v[i] & m) r.a.v[i] = 0ULL;
        else { uint64_t x = a.a.v[i] & m; r.a.v[i] = x <= 1ULL ? 0ULL : static_cast<uint64_t>(64 - std::countl_zero(x - 1ULL)); }
    }
    return r;
}
inline W4xN resolve_pair(W4xN a, W4xN b, int nk, uint64_t m) {
    W4xN r{};
    for (int i = 0; i < kLanes; ++i) {
        uint64_t va = a.a.v[i] & ~a.b.v[i] & m, za = ~a.a.v[i] & a.b.v[i] & m, a0 = ~a.a.v[i] & ~a.b.v[i] & m;
        uint64_t vb = b.a.v[i] & ~b.b.v[i] & m, zb = ~b.a.v[i] & b.b.v[i] & m, b0 = ~b.a.v[i] & ~b.b.v[i] & m;
        uint64_t z_out = za & zb, v_out = 0, o0 = 0;
        if (nk == 1) { o0 = (a0 | b0) & m; v_out = (~o0) & ((va & (vb | zb)) | (za & vb)) & m; }
        else if (nk == 2) { v_out = (va | vb) & m; o0 = (~v_out) & ((a0 & (b0 | zb)) | (za & b0)) & m; }
        else { v_out = ((va & (vb | zb)) | (za & vb)) & m; o0 = ((a0 & (b0 | zb)) | (za & b0)) & m; }
        uint64_t x_out = ~(z_out | v_out | o0) & m;
        r.a.v[i] = (v_out | x_out) & m; r.b.v[i] = (x_out | z_out) & m;
    }
    return r;
}
inline W4xN resolve_finalize(W4xN acc, int nk, uint64_t m) {
    if (nk == 3) { W4xN r{}; for (int i = 0; i < kLanes; ++i) { r.a.v[i] = acc.a.v[i] & m; r.b.v[i] = (acc.a.v[i] & acc.b.v[i]) & m; } return r; }
    if (nk == 4) { W4xN r{}; for (int i = 0; i < kLanes; ++i) { r.a.v[i] = (acc.a.v[i] | acc.b.v[i]) & m; r.b.v[i] = (acc.a.v[i] & acc.b.v[i]) & m; } return r; }
    return acc;
}
} // namespace w4s

struct SimdMemLog2 {
    struct Entry { int mem; Lane mask; Lane idx; Lane val; };
    std::vector<Entry> entries;
    SimdMemLog2() { entries.reserve(8); }
    inline size_t size() const { return entries.size(); }
    inline const Entry& at(size_t i) const { return entries[i]; }
    inline void push(int mem_id, size_t depth, Lane active, Lane idx, Lane val) {
        Lane ok{}; uint64_t any = 0;
        for (int i = 0; i < kLanes; ++i) {
            if (active.v[i] && static_cast<size_t>(idx.v[i]) < depth) { ok.v[i] = 0xffffffffffffffffULL; any = 1; }
        }
        if (any) entries.push_back({mem_id, ok, idx, val});
    }
    inline void clear() { entries.clear(); }
};

struct SimdMemLog4 {
    struct Entry { int mem; Lane mask; Lane idx; W4xN val; };
    std::vector<Entry> entries;
    SimdMemLog4() { entries.reserve(8); }
    inline size_t size() const { return entries.size(); }
    inline const Entry& at(size_t i) const { return entries[i]; }
    inline void push(int mem_id, size_t depth, Lane active, W4xN idx, W4xN val) {
        Lane ok{}; uint64_t any = 0;
        for (int i = 0; i < kLanes; ++i) {
            if (active.v[i] && idx.b.v[i] == 0ULL && static_cast<size_t>(idx.a.v[i]) < depth) { ok.v[i] = 0xffffffffffffffffULL; any = 1; }
        }
        if (any) entries.push_back({mem_id, ok, idx.a, val});
    }
    inline void clear() { entries.clear(); }
};

} // namespace velosim_simd
""";
    }
}
