using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed partial class RtlDesign
{
    /// <summary>
    /// Returns true when the design can be lowered to lockstep N-lane SIMD execution
    /// (4 or 8 independent instances in parallel per thread across C#, Rust, and C++).
    /// </summary>
    internal bool CanUseSimdLanes(RtlStateMode stateMode)
    {
        if (!CanUsePortable64(stateMode)) return false;
        if (StatementsHaveRuntimeLoops(SettledSteps.Append(Body))) return false;
        if (Properties.Count > 0 || PastDepths.Count > 0) return false;
        foreach (var st in SettledSteps.Append(Body).SelectMany(Statements))
        {
            string k = Kind(st);
            if (k is "force" or "release" or "assert" or "finish" or "fatal" or "display" or "value_plusargs" or "while" or "do" or "runtime_repeat")
                return false;
        }
        return !SettledSteps.Append(Body).Any(SimdHasUnsupportedExpr);
    }

    private static bool SimdHasUnsupportedExpr(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("kind", out _)) return false;
        string k = Kind(e);
        if (k is "urandom" or "urandom_range" or "random" or "test_plusargs" or "past" or "rose" or "fell" or "stable")
            return true;
        if (e.TryGetProperty("value", out var v) && SimdHasUnsupportedExpr(v)) return true;
        if (e.TryGetProperty("condition", out var c) && SimdHasUnsupportedExpr(c)) return true;
        if (e.TryGetProperty("then", out var t) && SimdHasUnsupportedExpr(t)) return true;
        if (e.TryGetProperty("else", out var el) && SimdHasUnsupportedExpr(el)) return true;
        if (e.TryGetProperty("body", out var b) && SimdHasUnsupportedExpr(b)) return true;
        if (e.TryGetProperty("index", out var ix) && SimdHasUnsupportedExpr(ix)) return true;
        if (e.TryGetProperty("base", out var bs) && SimdHasUnsupportedExpr(bs)) return true;
        if (e.TryGetProperty("args", out var args))
            foreach (var a in args.EnumerateArray()) if (SimdHasUnsupportedExpr(a)) return true;
        if (e.TryGetProperty("items", out var items))
            foreach (var it in items.EnumerateArray())
            {
                if (it.TryGetProperty("lo", out var lo) && SimdHasUnsupportedExpr(lo)) return true;
                if (it.TryGetProperty("hi", out var hi) && SimdHasUnsupportedExpr(hi)) return true;
                if (it.TryGetProperty("body", out var ib) && SimdHasUnsupportedExpr(ib)) return true;
                if (it.TryGetProperty("match", out var im) && SimdHasUnsupportedExpr(im)) return true;
                if (it.TryGetProperty("matches", out var ims))
                    foreach (var m in ims.EnumerateArray()) if (SimdHasUnsupportedExpr(m)) return true;
                if (!it.TryGetProperty("lo", out _) && !it.TryGetProperty("body", out _) && SimdHasUnsupportedExpr(it)) return true;
            }
        return false;
    }

    private static void ValidateSimdLanes(int lanes)
    {
        if (lanes is not (4 or 8))
            throw new ArgumentOutOfRangeException(nameof(lanes), "SIMD lane count must be 4 or 8.");
    }

    // =========================================================================
    // C# 15 / .NET 11 Lockstep SIMD Emitter (Vector256<ulong> / Vector512<ulong>, 0 unsafe)
    // =========================================================================

    private string CsSimdVecType(int lanes) => lanes == 8 ? "Vector512<ulong>" : "Vector256<ulong>";
    private string CsSimdVecStatic(int lanes) => lanes == 8 ? "Vector512" : "Vector256";
    private string CsSimdWordType(bool twoState, int lanes) => twoState ? CsSimdVecType(lanes) : $"W4x{lanes}";
    private string CsSimdOps(bool twoState, int lanes) => twoState ? $"W2x{lanes}Ops" : $"W4x{lanes}Ops";

    private string CsSimdConst4(string bits, int lanes)
    {
        ulong a = 0, b = 0;
        foreach (char c in bits)
        {
            a <<= 1; b <<= 1;
            if (c == '1') a |= 1;
            else if (c == 'x') { a |= 1; b |= 1; }
            else if (c == 'z') b |= 1;
        }
        string vs = CsSimdVecStatic(lanes);
        if (a == 0 && b == 0) return $"default(W4x{lanes})";
        return b == 0
            ? $"new W4x{lanes}({vs}.Create(0x{a:x}UL),{vs}<ulong>.Zero)"
            : $"new W4x{lanes}({vs}.Create(0x{a:x}UL),{vs}.Create(0x{b:x}UL))";
    }

    private string CsSimdConst2(string bits, int lanes)
    {
        ulong v = 0;
        foreach (char c in bits) { v <<= 1; if (c == '1') v |= 1; }
        string vs = CsSimdVecStatic(lanes);
        return v == 0 ? $"{vs}<ulong>.Zero" : $"{vs}.Create(0x{v:x}UL)";
    }

    private string CsSimdStore(string expr, RtlSignal sig, bool twoState, int lanes, int knownWidth = -1)
    {
        if (twoState)
        {
            if (sig.Width >= 64 || knownWidth == sig.Width) return expr;
            return $"(({expr})&{CsSimdVecStatic(lanes)}.Create({CsMaskHex(sig.Width)}))";
        }
        string m = CsMaskHex(sig.Width);
        if (sig.TwoState) return $"W4x{lanes}Ops.TwoState({expr},{m})";
        return knownWidth == sig.Width ? expr : $"W4x{lanes}Ops.Resize({expr},{m})";
    }

    private string CsSimdMemIdx(JsonElement access, int id, bool twoState, int lanes)
    {
        string idx = twoState ? CsSimdExpr2(access.GetProperty("index"), lanes) : CsSimdExpr4(access.GetProperty("index"), lanes);
        if (Memories[id].LowerBound == 0) return idx;
        return $"{CsSimdOps(twoState, lanes)}.MemIdx({idx},{Memories[id].LowerBound})";
    }

    private string CsSimdExpr4(JsonElement e, int lanes, int context = 0, bool? forceSigned = null)
    {
        string ops = $"W4x{lanes}Ops";
        string vs = CsSimdVecStatic(lanes);
        string kind = Kind(e);
        int ew = ExpressionWidth(e);
        bool signed = forceSigned ?? ExpressionSigned(e);
        string Extend(string val) => signed && context > ew ? $"{ops}.SResize({val},{ew},{CsMaskHex(ew)},{CsMaskHex(context)})" : val;
        if (kind == "ref") return Extend($"_s{SignalId(Name(e))}");
        if (kind == "const") return Extend(CsSimdConst4(e.GetProperty("bits").GetString()!, lanes));
        if (kind == "index")
        {
            int id = MemoryId(Name(e));
            return Extend($"_ReadMem{id}({CsSimdMemIdx(e, id, false, lanes)})");
        }
        if (kind == "bitselect")
        {
            var valEl = e.GetProperty("value");
            var idxEl = e.GetProperty("index");
            int aw = ExpressionWidth(valEl);
            if (TryConstShift(idxEl, out int bi) && bi < aw)
                return bi == 0 ? $"{ops}.Resize({CsSimdExpr4(valEl, lanes)},0x1UL)" : $"{ops}.SelPartFast({CsSimdExpr4(valEl, lanes)},{bi},0x1UL)";
            return $"{ops}.SelBit({CsSimdExpr4(valEl, lanes)},{aw},{CsSimdExpr4(idxEl, lanes)})";
        }
        if (kind == "partselect")
        {
            var valEl = e.GetProperty("value");
            int aw = ExpressionWidth(valEl), left = e.GetProperty("left").GetInt32(), right = e.GetProperty("right").GetInt32();
            if (right >= 0 && left < aw)
            {
                string m = CsMaskHex(left - right + 1);
                return right == 0 ? $"{ops}.Resize({CsSimdExpr4(valEl, lanes)},{m})" : $"{ops}.SelPartFast({CsSimdExpr4(valEl, lanes)},{right},{m})";
            }
            return $"{ops}.SelPart({CsSimdExpr4(valEl, lanes)},{aw},{left},{right})";
        }
        if (kind == "indexedselect")
        {
            var valEl = e.GetProperty("value");
            return $"{ops}.SelIdx({CsSimdExpr4(valEl, lanes)},{ExpressionWidth(valEl)},{CsSimdExpr4(e.GetProperty("base"), lanes)},{e.GetProperty("width").GetInt32()},{(e.GetProperty("descending").GetBoolean() ? "true" : "false")})";
        }
        if (kind == "resolve")
        {
            int nk = ParseNetKind(e.GetProperty("net").GetString()!);
            int rw = Math.Max(context, ew);
            var drvEls = e.GetProperty("args").EnumerateArray().ToArray();
            string acc = $"{ops}.Resize({CsSimdExpr4(drvEls[0], lanes, rw)},{CsMaskHex(rw)})";
            for (int i = 1; i < drvEls.Length; ++i)
                acc = $"{ops}.ResolvePair({acc},{CsSimdExpr4(drvEls[i], lanes, rw)},{nk},{CsMaskHex(rw)})";
            return Extend($"{ops}.ResolveFinalize({acc},{nk},{CsMaskHex(rw)})");
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
                    string loC = cs ? $"{ops}.SCmp({CsSimdExpr4(valEl, lanes, cw, true)},{CsSimdExpr4(loEl, lanes, cw, true)},{cw},3)" : $"{ops}.Cmp({CsSimdExpr4(valEl, lanes, cw, false)},{CsSimdExpr4(loEl, lanes, cw, false)},3)";
                    string hiC = cs ? $"{ops}.SCmp({CsSimdExpr4(valEl, lanes, cw, true)},{CsSimdExpr4(hiEl, lanes, cw, true)},{cw},1)" : $"{ops}.Cmp({CsSimdExpr4(valEl, lanes, cw, false)},{CsSimdExpr4(hiEl, lanes, cw, false)},1)";
                    parts.Add($"{ops}.LAnd({loC},{hiC})");
                }
                else
                {
                    int cw = Math.Max(vw, ExpressionWidth(it));
                    bool cs = vSigned && ExpressionSigned(it);
                    parts.Add($"{ops}.WildEq({CsSimdExpr4(valEl, lanes, cw, cs)},{CsSimdExpr4(it, lanes, cw, cs)},{CsMaskHex(cw)})");
                }
            }
            string acc = parts[0];
            for (int i = 1; i < parts.Count; ++i) acc = $"{ops}.LOr({acc},{parts[i]})";
            return acc;
        }
        var args = e.GetProperty("args").EnumerateArray().ToArray();
        int width = Math.Max(context, ew);
        string wm = CsMaskHex(width);
        return kind switch
        {
            "castsigned" or "castunsigned" => Extend(CsSimdExpr4(args[0], lanes)),
            "countones" => Extend($"{ops}.CountOnes({CsSimdExpr4(args[0], lanes)},{CsMaskHex(ExpressionWidth(args[0]))})"),
            "onehot" => $"{ops}.OneHot({CsSimdExpr4(args[0], lanes)},{CsMaskHex(ExpressionWidth(args[0]))})",
            "onehot0" => $"{ops}.OneHot0({CsSimdExpr4(args[0], lanes)},{CsMaskHex(ExpressionWidth(args[0]))})",
            "isunknown" => $"{ops}.IsUnknown({CsSimdExpr4(args[0], lanes)},{CsMaskHex(ExpressionWidth(args[0]))})",
            "clog2" => Extend($"{ops}.CLog2({CsSimdExpr4(args[0], lanes)},{CsMaskHex(ExpressionWidth(args[0]))})"),
            "add" => $"{ops}.Arith({CsSimdExpr4(args[0], lanes, width, signed)},{CsSimdExpr4(args[1], lanes, width, signed)},{wm},false)",
            "sub" => $"{ops}.Arith({CsSimdExpr4(args[0], lanes, width, signed)},{CsSimdExpr4(args[1], lanes, width, signed)},{wm},true)",
            "mul" => signed ? $"{ops}.SProduct({CsSimdExpr4(args[0], lanes, width, true)},{CsSimdExpr4(args[1], lanes, width, true)},{width},{wm},0)" : $"{ops}.Product({CsSimdExpr4(args[0], lanes, width, false)},{CsSimdExpr4(args[1], lanes, width, false)},{wm},0)",
            "div" => signed ? $"{ops}.SProduct({CsSimdExpr4(args[0], lanes, width, true)},{CsSimdExpr4(args[1], lanes, width, true)},{width},{wm},1)" : $"{ops}.Product({CsSimdExpr4(args[0], lanes, width, false)},{CsSimdExpr4(args[1], lanes, width, false)},{wm},1)",
            "mod" => signed ? $"{ops}.SProduct({CsSimdExpr4(args[0], lanes, width, true)},{CsSimdExpr4(args[1], lanes, width, true)},{width},{wm},2)" : $"{ops}.Product({CsSimdExpr4(args[0], lanes, width, false)},{CsSimdExpr4(args[1], lanes, width, false)},{wm},2)",
            "neg" => $"{ops}.Arith(default,{CsSimdExpr4(args[0], lanes, width, signed)},{wm},true)",
            "pos" => $"{ops}.Resize({CsSimdExpr4(args[0], lanes, width, signed)},{wm})",
            "bitand" => $"{ops}.BBin({CsSimdExpr4(args[0], lanes, width, signed)},{CsSimdExpr4(args[1], lanes, width, signed)},{wm},0)",
            "bitor" => $"{ops}.BBin({CsSimdExpr4(args[0], lanes, width, signed)},{CsSimdExpr4(args[1], lanes, width, signed)},{wm},1)",
            "bitxor" => $"{ops}.BBin({CsSimdExpr4(args[0], lanes, width, signed)},{CsSimdExpr4(args[1], lanes, width, signed)},{wm},2)",
            "bitxnor" => $"{ops}.BNot({ops}.BBin({CsSimdExpr4(args[0], lanes, width, signed)},{CsSimdExpr4(args[1], lanes, width, signed)},{wm},2),{wm})",
            "bitnot" => $"{ops}.BNot({CsSimdExpr4(args[0], lanes, width, signed)},{wm})",
            "shl" => TryConstShift(args[1], out int sl) ? $"{ops}.ShlConst({CsSimdExpr4(args[0], lanes, width, signed)},{sl},{wm})" : $"{ops}.Shift({CsSimdExpr4(args[0], lanes, width, signed)},{CsSimdExpr4(args[1], lanes)},{wm},false)",
            "shr" => TryConstShift(args[1], out int sr) ? $"{ops}.ShrConst({CsSimdExpr4(args[0], lanes, width, signed)},{sr},{wm})" : $"{ops}.Shift({CsSimdExpr4(args[0], lanes, width, signed)},{CsSimdExpr4(args[1], lanes)},{wm},true)",
            "ashr" => signed ? $"{ops}.SShift({CsSimdExpr4(args[0], lanes, width, true)},{CsSimdExpr4(args[1], lanes)},{width},{wm})" : $"{ops}.Shift({CsSimdExpr4(args[0], lanes, width, false)},{CsSimdExpr4(args[1], lanes)},{wm},true)",
            "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor" => $"{ops}.Red({CsSimdExpr4(args[0], lanes)},{CsMaskHex(ExpressionWidth(args[0]))},{(kind == "redand" ? 0 : kind == "rednand" ? 1 : kind == "redor" ? 2 : kind == "rednor" ? 3 : kind == "redxor" ? 4 : 5)})",
            "not" => $"{ops}.LNot({CsSimdExpr4(args[0], lanes)})",
            "and" => $"{ops}.LAnd({CsSimdExpr4(args[0], lanes)},{CsSimdExpr4(args[1], lanes)})",
            "or" => $"{ops}.LOr({CsSimdExpr4(args[0], lanes)},{CsSimdExpr4(args[1], lanes)})",
            "eq" or "neq" or "caseeq" or "caseneq" or "wildeq" or "wildneq" or "lt" or "le" or "gt" or "ge" => CsSimdRel4(kind, args[0], args[1], lanes),
            "cond" => $"{ops}.Cond({CsSimdExpr4(args[0], lanes)},{CsSimdExpr4(args[1], lanes, width, signed)},{CsSimdExpr4(args[2], lanes, width, signed)},{wm})",
            "concat" => $"{ops}.Concat({CsSimdExpr4(args[0], lanes)},{CsSimdExpr4(args[1], lanes)},{ExpressionWidth(args[1])},{CsMaskHex(ExpressionWidth(args[1]))})",
            _ => $"new W4x{lanes}({vs}<ulong>.Zero,{vs}<ulong>.Zero)"
        };
    }

    private string CsSimdRel4(string kind, JsonElement a, JsonElement b, int lanes)
    {
        string ops = $"W4x{lanes}Ops";
        int cw = Math.Max(ExpressionWidth(a), ExpressionWidth(b));
        bool cs = ExpressionSigned(a) && ExpressionSigned(b);
        string sa = CsSimdExpr4(a, lanes, cw, cs), sb = CsSimdExpr4(b, lanes, cw, cs);
        return kind switch
        {
            "eq" => $"{ops}.Eq({sa},{sb})",
            "neq" => $"{ops}.LNot({ops}.Eq({sa},{sb}))",
            "caseeq" => $"{ops}.FromMask({ops}.ExactMask({sa},{sb}))",
            "caseneq" => $"{ops}.FromMask(~{ops}.ExactMask({sa},{sb}))",
            "wildeq" => $"{ops}.WildEq({sa},{sb},{CsMaskHex(cw)})",
            "wildneq" => $"{ops}.LNot({ops}.WildEq({sa},{sb},{CsMaskHex(cw)}))",
            "lt" => cs ? $"{ops}.SCmp({sa},{sb},{cw},0)" : $"{ops}.Cmp({sa},{sb},0)",
            "le" => cs ? $"{ops}.SCmp({sa},{sb},{cw},1)" : $"{ops}.Cmp({sa},{sb},1)",
            "gt" => cs ? $"{ops}.SCmp({sa},{sb},{cw},2)" : $"{ops}.Cmp({sa},{sb},2)",
            _ => cs ? $"{ops}.SCmp({sa},{sb},{cw},3)" : $"{ops}.Cmp({sa},{sb},3)"
        };
    }

    private string CsSimdCondMask4(JsonElement e, int lanes)
    {
        string ops = $"W4x{lanes}Ops";
        string k = Kind(e);
        if (k == "not") return $"{ops}.IsZeroMask({CsSimdExpr4(e.GetProperty("args")[0], lanes)})";
        if (k is "and" or "or")
        {
            var args = e.GetProperty("args");
            return $"({CsSimdCondMask4(args[0], lanes)}{(k == "and" ? "&" : "|")}{CsSimdCondMask4(args[1], lanes)})";
        }
        if (k is "eq" or "neq" or "caseeq" or "caseneq")
        {
            var args = e.GetProperty("args");
            int cw = Math.Max(ExpressionWidth(args[0]), ExpressionWidth(args[1]));
            bool cs = ExpressionSigned(args[0]) && ExpressionSigned(args[1]);
            string sa = CsSimdExpr4(args[0], lanes, cw, cs), sb = CsSimdExpr4(args[1], lanes, cw, cs);
            return k switch
            {
                "eq" => $"{ops}.EqYesMask({sa},{sb})",
                "neq" => $"{ops}.NeqYesMask({sa},{sb})",
                "caseeq" => $"{ops}.ExactMask({sa},{sb})",
                _ => $"(~{ops}.ExactMask({sa},{sb}))"
            };
        }
        return $"{ops}.YesMask({CsSimdExpr4(e, lanes)})";
    }

    private string CsSimdExpr2(JsonElement e, int lanes, int context = 0, bool? forceSigned = null)
    {
        string ops = $"W2x{lanes}Ops";
        string vs = CsSimdVecStatic(lanes);
        string kind = Kind(e);
        int ew = ExpressionWidth(e);
        bool signed = forceSigned ?? ExpressionSigned(e);
        string Extend(string val) => signed && context > ew ? $"{ops}.SResize({val},{ew},{context})" : val;
        if (kind == "ref") return Extend($"_s{SignalId(Name(e))}");
        if (kind == "const") return Extend(CsSimdConst2(e.GetProperty("bits").GetString()!, lanes));
        if (kind == "index")
        {
            int id = MemoryId(Name(e));
            return Extend($"_ReadMem{id}({CsSimdMemIdx(e, id, true, lanes)})");
        }
        if (kind == "bitselect")
        {
            var valEl = e.GetProperty("value");
            var idxEl = e.GetProperty("index");
            int aw = ExpressionWidth(valEl);
            if (TryConstShift(idxEl, out int bi) && bi < aw)
                return bi == 0 ? $"(({CsSimdExpr2(valEl, lanes)})&{vs}.Create(1UL))" : $"({vs}.ShiftRightLogical({CsSimdExpr2(valEl, lanes)},{bi})&{vs}.Create(1UL))";
            return $"{ops}.SelBit({CsSimdExpr2(valEl, lanes)},{aw},{CsSimdExpr2(idxEl, lanes)})";
        }
        if (kind == "partselect")
        {
            var valEl = e.GetProperty("value");
            int aw = ExpressionWidth(valEl), left = e.GetProperty("left").GetInt32(), right = e.GetProperty("right").GetInt32();
            if (right >= 0 && left < aw)
            {
                string mv = $"{vs}.Create({CsMaskHex(left - right + 1)})";
                return right == 0 ? $"(({CsSimdExpr2(valEl, lanes)})&{mv})" : $"({vs}.ShiftRightLogical({CsSimdExpr2(valEl, lanes)},{right})&{mv})";
            }
            return $"{ops}.SelPart({CsSimdExpr2(valEl, lanes)},{aw},{left},{right})";
        }
        if (kind == "indexedselect")
        {
            var valEl = e.GetProperty("value");
            return $"{ops}.SelIdx({CsSimdExpr2(valEl, lanes)},{ExpressionWidth(valEl)},{CsSimdExpr2(e.GetProperty("base"), lanes)},{e.GetProperty("width").GetInt32()},{(e.GetProperty("descending").GetBoolean() ? "true" : "false")})";
        }
        if (kind == "resolve")
        {
            int nk = ParseNetKind(e.GetProperty("net").GetString()!);
            int rw = Math.Max(context, ew);
            var drvEls = e.GetProperty("args").EnumerateArray().ToArray();
            string acc = $"{ops}.Resize({CsSimdExpr2(drvEls[0], lanes, rw)},{rw})";
            for (int i = 1; i < drvEls.Length; ++i)
                acc = $"{ops}.ResolvePair({acc},{CsSimdExpr2(drvEls[i], lanes, rw)},{nk},{rw})";
            return Extend(acc);
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
                    string loC = cs ? $"{ops}.SCmp({CsSimdExpr2(valEl, lanes, cw, true)},{CsSimdExpr2(loEl, lanes, cw, true)},{cw},3)" : $"{ops}.Cmp({CsSimdExpr2(valEl, lanes, cw, false)},{CsSimdExpr2(loEl, lanes, cw, false)},3)";
                    string hiC = cs ? $"{ops}.SCmp({CsSimdExpr2(valEl, lanes, cw, true)},{CsSimdExpr2(hiEl, lanes, cw, true)},{cw},1)" : $"{ops}.Cmp({CsSimdExpr2(valEl, lanes, cw, false)},{CsSimdExpr2(hiEl, lanes, cw, false)},1)";
                    parts.Add($"({loC}&{hiC})");
                }
                else
                {
                    int cw = Math.Max(vw, ExpressionWidth(it));
                    bool cs = vSigned && ExpressionSigned(it);
                    parts.Add($"({vs}.Equals({CsSimdExpr2(valEl, lanes, cw, cs)},{CsSimdExpr2(it, lanes, cw, cs)})&{vs}.Create(1UL))");
                }
            }
            string acc = parts[0];
            for (int i = 1; i < parts.Count; ++i) acc = $"({acc}|{parts[i]})";
            return acc;
        }
        var args = e.GetProperty("args").EnumerateArray().ToArray();
        int width = Math.Max(context, ew);
        string mv2 = $"{vs}.Create({CsMaskHex(width)})";
        string MaskWrap(string inner) => width >= 64 ? $"({inner})" : $"(({inner})&{mv2})";
        return kind switch
        {
            "castsigned" or "castunsigned" => Extend(CsSimdExpr2(args[0], lanes)),
            "countones" => Extend($"{ops}.CountOnes({CsSimdExpr2(args[0], lanes)},{ExpressionWidth(args[0])})"),
            "onehot" => $"{ops}.OneHot({CsSimdExpr2(args[0], lanes)},{ExpressionWidth(args[0])})",
            "onehot0" => $"{ops}.OneHot0({CsSimdExpr2(args[0], lanes)},{ExpressionWidth(args[0])})",
            "isunknown" => $"{vs}<ulong>.Zero",
            "clog2" => Extend($"{ops}.CLog2({CsSimdExpr2(args[0], lanes)},{ExpressionWidth(args[0])})"),
            "add" => MaskWrap($"{CsSimdExpr2(args[0], lanes, width, signed)}+{CsSimdExpr2(args[1], lanes, width, signed)}"),
            "sub" => MaskWrap($"{CsSimdExpr2(args[0], lanes, width, signed)}-{CsSimdExpr2(args[1], lanes, width, signed)}"),
            "mul" => signed ? $"{ops}.SProduct({CsSimdExpr2(args[0], lanes, width, true)},{CsSimdExpr2(args[1], lanes, width, true)},{width},0)" : MaskWrap($"{CsSimdExpr2(args[0], lanes, width, false)}*{CsSimdExpr2(args[1], lanes, width, false)}"),
            "div" => signed ? $"{ops}.SProduct({CsSimdExpr2(args[0], lanes, width, true)},{CsSimdExpr2(args[1], lanes, width, true)},{width},1)" : $"{ops}.Product({CsSimdExpr2(args[0], lanes, width, false)},{CsSimdExpr2(args[1], lanes, width, false)},{width},1)",
            "mod" => signed ? $"{ops}.SProduct({CsSimdExpr2(args[0], lanes, width, true)},{CsSimdExpr2(args[1], lanes, width, true)},{width},2)" : $"{ops}.Product({CsSimdExpr2(args[0], lanes, width, false)},{CsSimdExpr2(args[1], lanes, width, false)},{width},2)",
            "neg" => MaskWrap($"{vs}<ulong>.Zero-{CsSimdExpr2(args[0], lanes, width, signed)}"),
            "pos" => MaskWrap(CsSimdExpr2(args[0], lanes, width, signed)),
            "bitand" => MaskWrap($"{CsSimdExpr2(args[0], lanes, width, signed)}&{CsSimdExpr2(args[1], lanes, width, signed)}"),
            "bitor" => MaskWrap($"{CsSimdExpr2(args[0], lanes, width, signed)}|{CsSimdExpr2(args[1], lanes, width, signed)}"),
            "bitxor" => MaskWrap($"{CsSimdExpr2(args[0], lanes, width, signed)}^{CsSimdExpr2(args[1], lanes, width, signed)}"),
            "bitxnor" => MaskWrap($"~({CsSimdExpr2(args[0], lanes, width, signed)}^{CsSimdExpr2(args[1], lanes, width, signed)})"),
            "bitnot" => MaskWrap($"~({CsSimdExpr2(args[0], lanes, width, signed)})"),
            "shl" => TryConstShift(args[1], out int sl) ? (sl >= 64 ? $"{vs}<ulong>.Zero" : MaskWrap($"{vs}.ShiftLeft({CsSimdExpr2(args[0], lanes, width, signed)},{sl})")) : $"{ops}.Shift({CsSimdExpr2(args[0], lanes, width, signed)},{CsSimdExpr2(args[1], lanes)},{width},false)",
            "shr" => TryConstShift(args[1], out int sr) ? (sr >= 64 ? $"{vs}<ulong>.Zero" : $"{vs}.ShiftRightLogical({MaskWrap(CsSimdExpr2(args[0], lanes, width, signed))},{sr})") : $"{ops}.Shift({CsSimdExpr2(args[0], lanes, width, signed)},{CsSimdExpr2(args[1], lanes)},{width},true)",
            "ashr" => signed ? $"{ops}.SShift({CsSimdExpr2(args[0], lanes, width, true)},{CsSimdExpr2(args[1], lanes)},{width})" : $"{ops}.Shift({CsSimdExpr2(args[0], lanes, width, false)},{CsSimdExpr2(args[1], lanes)},{width},true)",
            "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor" => $"{ops}.Red({CsSimdExpr2(args[0], lanes)},{ExpressionWidth(args[0])},{(kind == "redand" ? 0 : kind == "rednand" ? 1 : kind == "redor" ? 2 : kind == "rednor" ? 3 : kind == "redxor" ? 4 : 5)})",
            "not" => $"({vs}.Equals({CsSimdExpr2(args[0], lanes)},{vs}<ulong>.Zero)&{vs}.Create(1UL))",
            "and" => $"(~{vs}.Equals({CsSimdExpr2(args[0], lanes)},{vs}<ulong>.Zero)&~{vs}.Equals({CsSimdExpr2(args[1], lanes)},{vs}<ulong>.Zero)&{vs}.Create(1UL))",
            "or" => $"(~{vs}.Equals({CsSimdExpr2(args[0], lanes)}|{CsSimdExpr2(args[1], lanes)},{vs}<ulong>.Zero)&{vs}.Create(1UL))",
            "eq" or "neq" or "caseeq" or "caseneq" or "wildeq" or "wildneq" or "lt" or "le" or "gt" or "ge" => CsSimdRel2(kind, args[0], args[1], lanes),
            "cond" => MaskWrap($"{vs}.ConditionalSelect(~{vs}.Equals({CsSimdExpr2(args[0], lanes)},{vs}<ulong>.Zero),{CsSimdExpr2(args[1], lanes, width, signed)},{CsSimdExpr2(args[2], lanes, width, signed)})"),
            "concat" => ExpressionWidth(args[1]) >= 64
                ? CsSimdExpr2(args[1], lanes)
                : $"({vs}.ShiftLeft({CsSimdExpr2(args[0], lanes)},{ExpressionWidth(args[1])})|({CsSimdExpr2(args[1], lanes)}&{vs}.Create({CsMaskHex(ExpressionWidth(args[1]))})))",
            _ => $"{vs}<ulong>.Zero"
        };
    }

    private string CsSimdRel2(string kind, JsonElement a, JsonElement b, int lanes)
    {
        string ops = $"W2x{lanes}Ops";
        string vs = CsSimdVecStatic(lanes);
        int cw = Math.Max(ExpressionWidth(a), ExpressionWidth(b));
        bool cs = ExpressionSigned(a) && ExpressionSigned(b);
        string sa = CsSimdExpr2(a, lanes, cw, cs), sb = CsSimdExpr2(b, lanes, cw, cs);
        return kind switch
        {
            "eq" or "caseeq" or "wildeq" => $"({vs}.Equals({sa},{sb})&{vs}.Create(1UL))",
            "neq" or "caseneq" or "wildneq" => $"(~{vs}.Equals({sa},{sb})&{vs}.Create(1UL))",
            "lt" => cs ? $"{ops}.SCmp({sa},{sb},{cw},0)" : $"({vs}.LessThan({sa},{sb})&{vs}.Create(1UL))",
            "le" => cs ? $"{ops}.SCmp({sa},{sb},{cw},1)" : $"({vs}.LessThanOrEqual({sa},{sb})&{vs}.Create(1UL))",
            "gt" => cs ? $"{ops}.SCmp({sa},{sb},{cw},2)" : $"({vs}.GreaterThan({sa},{sb})&{vs}.Create(1UL))",
            _ => cs ? $"{ops}.SCmp({sa},{sb},{cw},3)" : $"({vs}.GreaterThanOrEqual({sa},{sb})&{vs}.Create(1UL))"
        };
    }

    private string CsSimdCondMask2(JsonElement e, int lanes)
    {
        string vs = CsSimdVecStatic(lanes);
        string k = Kind(e);
        if (k == "not") return $"{vs}.Equals({CsSimdExpr2(e.GetProperty("args")[0], lanes)},{vs}<ulong>.Zero)";
        if (k is "and" or "or")
        {
            var args = e.GetProperty("args");
            return $"({CsSimdCondMask2(args[0], lanes)}{(k == "and" ? "&" : "|")}{CsSimdCondMask2(args[1], lanes)})";
        }
        if (k is "eq" or "neq" or "caseeq" or "caseneq")
        {
            var args = e.GetProperty("args");
            int cw = Math.Max(ExpressionWidth(args[0]), ExpressionWidth(args[1]));
            bool cs = ExpressionSigned(args[0]) && ExpressionSigned(args[1]);
            string eq = $"{vs}.Equals({CsSimdExpr2(args[0], lanes, cw, cs)},{CsSimdExpr2(args[1], lanes, cw, cs)})";
            return k is "eq" or "caseeq" ? eq : $"(~{eq})";
        }
        if (k is "lt" or "le" or "gt" or "ge")
        {
            var args = e.GetProperty("args");
            if (!(ExpressionSigned(args[0]) && ExpressionSigned(args[1])))
            {
                int cw = Math.Max(ExpressionWidth(args[0]), ExpressionWidth(args[1]));
                string sa = CsSimdExpr2(args[0], lanes, cw, false), sb = CsSimdExpr2(args[1], lanes, cw, false);
                return k switch
                {
                    "lt" => $"{vs}.LessThan({sa},{sb})",
                    "le" => $"{vs}.LessThanOrEqual({sa},{sb})",
                    "gt" => $"{vs}.GreaterThan({sa},{sb})",
                    _ => $"{vs}.GreaterThanOrEqual({sa},{sb})"
                };
            }
        }
        return $"(~{vs}.Equals({CsSimdExpr2(e, lanes)},{vs}<ulong>.Zero))";
    }

    private void CsSimdWordWrite(StringBuilder code, JsonElement target, string value, string activeMask, bool twoState, int lanes, int knownWidth = -1)
    {
        int sid = SignalId(TargetOwner(target));
        string vs = CsSimdVecStatic(lanes);
        string ops = CsSimdOps(twoState, lanes);
        string Select(string newVal) => twoState
            ? $"{vs}.ConditionalSelect({activeMask},{newVal},_next{sid})"
            : $"W4x{lanes}Ops.Select({activeMask},{newVal},_next{sid})";

        if (Kind(target) == "ref")
        {
            string stored = CsSimdStore(value, Signals[sid], twoState, lanes, knownWidth);
            code.AppendLine($"            _next{sid}={Select(stored)};");
            return;
        }
        int width = Kind(target) == "bitselect" ? 1 : Kind(target) == "partselect" ? target.GetProperty("left").GetInt32() - target.GetProperty("right").GetInt32() + 1 : target.GetProperty("width").GetInt32();
        bool desc = Kind(target) == "indexedselect" && target.GetProperty("descending").GetBoolean();
        string basis = twoState
            ? (Kind(target) == "bitselect" ? CsSimdExpr2(target.GetProperty("index"), lanes)
                : Kind(target) == "indexedselect" ? CsSimdExpr2(target.GetProperty("base"), lanes)
                : $"{vs}.Create({target.GetProperty("right").GetInt32()}UL)")
            : (Kind(target) == "bitselect" ? CsSimdExpr4(target.GetProperty("index"), lanes)
                : Kind(target) == "indexedselect" ? CsSimdExpr4(target.GetProperty("base"), lanes)
                : $"new W4x{lanes}({vs}.Create({target.GetProperty("right").GetInt32()}UL),{vs}<ulong>.Zero)");
        string dep = $"{ops}.DepIdx(_next{sid},{Signals[sid].Width},{basis},{width},{(desc ? "true" : "false")},{value})";
        if (!twoState && Signals[sid].TwoState) dep = $"W4x{lanes}Ops.TwoState({dep},{CsMaskHex(Signals[sid].Width)})";
        code.AppendLine($"            _next{sid}={Select(dep)};");
    }

    private void CsSimdStmt(StringBuilder code, JsonElement e, string activeMask, ref int unique, bool twoState, int lanes, HashSet<int> singleWriteMems)
    {
        string vs = CsSimdVecStatic(lanes);
        string vt = CsSimdVecType(lanes);
        string ops = CsSimdOps(twoState, lanes);
        switch (Kind(e))
        {
            case "repeat":
            {
                string lp = "rep" + unique++;
                code.AppendLine($"            for(int {lp}=0;{lp}<{e.GetProperty("count").GetInt32()};++{lp}){{");
                CsSimdStmt(code, e.GetProperty("body"), activeMask, ref unique, twoState, lanes, singleWriteMems);
                code.AppendLine("            }");
                break;
            }
            case "block":
                foreach (var item in e.GetProperty("items").EnumerateArray())
                    CsSimdStmt(code, item, activeMask, ref unique, twoState, lanes, singleWriteMems);
                break;
            case "nba":
            {
                var target = e.GetProperty("target");
                if (Kind(target) == "concat")
                {
                    int total = ExpressionWidth(target), remaining = total;
                    string rhs = "nba" + unique++;
                    code.AppendLine(twoState
                        ? $"            {{{vt} {rhs}={ops}.Resize({CsSimdExpr2(e.GetProperty("value"), lanes, total)},{total});"
                        : $"            {{W4x{lanes} {rhs}=W4x{lanes}Ops.Resize({CsSimdExpr4(e.GetProperty("value"), lanes, total)},{CsMaskHex(total)});");
                    foreach (var leaf in TargetLeaves(target))
                    {
                        int w = ExpressionWidth(leaf);
                        remaining -= w;
                        string part = twoState
                            ? $"{ops}.SelPart({rhs},{total},{remaining + w - 1},{remaining})"
                            : $"W4x{lanes}Ops.SelPartFast({rhs},{remaining},{CsMaskHex(w)})";
                        CsSimdWordWrite(code, leaf, part, activeMask, twoState, lanes, w);
                    }
                    code.AppendLine("            }");
                }
                else if (Kind(target) is "ref" or "bitselect" or "partselect" or "indexedselect")
                {
                    int tw = ExpressionWidth(target);
                    var valEl = e.GetProperty("value");
                    int vw = ExpressionWidth(valEl);
                    bool exactWidth = vw == tw && !(ExpressionSigned(valEl) && tw > vw);
                    string expr = twoState ? CsSimdExpr2(valEl, lanes, tw) : CsSimdExpr4(valEl, lanes, tw);
                    CsSimdWordWrite(code, target, expr, activeMask, twoState, lanes, exactWidth ? tw : -1);
                }
                else
                {
                    int id = MemoryId(Name(target));
                    int mw = Memories[id].Width;
                    string mm = CsMaskHex(mw);
                    if (singleWriteMems.Contains(id))
                    {
                        if (twoState)
                        {
                            string v = CsSimdExpr2(e.GetProperty("value"), lanes, mw);
                            string vm = mw >= 64 ? v : $"(({v})&{vs}.Create({mm}))";
                            code.AppendLine($"            {{var _mi={CsSimdMemIdx(target, id, true, lanes)};var _ok={activeMask}&{vs}.LessThan(_mi,{vs}.Create({Memories[id].Depth}UL));_mw{id}_mask|=_ok;_mw{id}_idx={vs}.ConditionalSelect(_ok,_mi,_mw{id}_idx);_mw{id}_val={vs}.ConditionalSelect(_ok,{vm},_mw{id}_val);}}");
                        }
                        else
                        {
                            string v = CsSimdExpr4(e.GetProperty("value"), lanes, mw);
                            v = Memories[id].TwoState ? $"W4x{lanes}Ops.TwoState({v},{mm})" : $"W4x{lanes}Ops.Resize({v},{mm})";
                            code.AppendLine($"            {{var _mi={CsSimdMemIdx(target, id, false, lanes)};var _ok={activeMask}&{vs}.Equals(_mi.B,{vs}<ulong>.Zero)&{vs}.LessThan(_mi.A,{vs}.Create({Memories[id].Depth}UL));_mw{id}_mask|=_ok;_mw{id}_idx={vs}.ConditionalSelect(_ok,_mi.A,_mw{id}_idx);_mw{id}_val=W4x{lanes}Ops.Select(_ok,{v},_mw{id}_val);}}");
                        }
                    }
                    else
                    {
                        if (twoState)
                        {
                            string v = CsSimdExpr2(e.GetProperty("value"), lanes, mw);
                            string vm = mw >= 64 ? v : $"(({v})&{vs}.Create({mm}))";
                            code.AppendLine($"            _mlog.Push({id},{Memories[id].Depth},{activeMask},{CsSimdMemIdx(target, id, true, lanes)},{vm});");
                        }
                        else
                        {
                            string v = CsSimdExpr4(e.GetProperty("value"), lanes, mw);
                            v = Memories[id].TwoState ? $"W4x{lanes}Ops.TwoState({v},{mm})" : $"W4x{lanes}Ops.Resize({v},{mm})";
                            code.AppendLine($"            _mlog.Push({id},{Memories[id].Depth},{activeMask},{CsSimdMemIdx(target, id, false, lanes)},{v});");
                        }
                    }
                }
                break;
            }
            case "if":
            {
                string cm = "cm" + unique++;
                string tm = "tm" + unique++;
                string em = "em" + unique++;
                string condMask = twoState ? CsSimdCondMask2(e.GetProperty("condition"), lanes) : CsSimdCondMask4(e.GetProperty("condition"), lanes);
                code.AppendLine($"            {{{vt} {cm}={condMask};{vt} {tm}={activeMask}&{cm};{vt} {em}={activeMask}&~{cm};");
                var thenEl = e.GetProperty("then");
                var elseEl = e.GetProperty("else");
                bool hasThen = !(Kind(thenEl) == "block" && thenEl.GetProperty("items").GetArrayLength() == 0);
                bool hasElse = !(Kind(elseEl) == "block" && elseEl.GetProperty("items").GetArrayLength() == 0);
                if (hasThen) CsSimdStmt(code, thenEl, tm, ref unique, twoState, lanes, singleWriteMems);
                if (hasElse) CsSimdStmt(code, elseEl, em, ref unique, twoState, lanes, singleWriteMems);
                code.AppendLine("            }");
                break;
            }
            case "case":
            {
                string lbl = "cs" + unique++;
                string rem = "rem" + unique++;
                var items = e.GetProperty("items").EnumerateArray().ToArray();
                var allMatches = items.SelectMany(CaseItemMatches).ToArray();
                int cw = Math.Max(ExpressionWidth(e.GetProperty("value")), allMatches.Select(ExpressionWidth).DefaultIfEmpty(1).Max());
                string cwm = CsMaskHex(cw);
                string ck = CaseKind(e);
                bool csSigned = ExpressionSigned(e.GetProperty("value")) && allMatches.All(ExpressionSigned);
                code.AppendLine(twoState
                    ? $"            {{{vt} {lbl}={CsSimdExpr2(e.GetProperty("value"), lanes, cw, csSigned)};{vt} {rem}={activeMask};"
                    : $"            {{W4x{lanes} {lbl}={CsSimdExpr4(e.GetProperty("value"), lanes, cw, csSigned)};{vt} {rem}={activeMask};");
                foreach (var item in items.Where(i => !IsCaseDefault(i)))
                {
                    var ms = CaseItemMatches(item);
                    string hit = "hit" + unique++;
                    string cmp = twoState
                        ? string.Join("|", ms.Select(m => $"{vs}.Equals({lbl},{CsSimdExpr2(m, lanes, cw, csSigned)})"))
                        : string.Join("|", ms.Select(m => ck switch
                        {
                            "casez" => $"W4x{lanes}Ops.CasezMatchMask({lbl},{CsSimdExpr4(m, lanes, cw, csSigned)},{cwm})",
                            "casex" => $"W4x{lanes}Ops.CasexMatchMask({lbl},{CsSimdExpr4(m, lanes, cw, csSigned)},{cwm})",
                            _ => $"W4x{lanes}Ops.ExactMask({lbl},{CsSimdExpr4(m, lanes, cw, csSigned)})"
                        }));
                    code.AppendLine($"            {vt} {hit}={rem}&({cmp});{rem}&=~{hit};");
                    CsSimdStmt(code, item.GetProperty("body"), hit, ref unique, twoState, lanes, singleWriteMems);
                }
                var defItem = items.FirstOrDefault(IsCaseDefault);
                if (defItem.ValueKind != JsonValueKind.Undefined)
                    CsSimdStmt(code, defItem.GetProperty("body"), rem, ref unique, twoState, lanes, singleWriteMems);
                code.AppendLine("            }");
                break;
            }
        }
    }

    /// <summary>Emits a 100% safe lockstep N-lane SIMD C# model using Vector256&lt;ulong&gt; (4 lanes) or Vector512&lt;ulong&gt; (8 lanes).</summary>
    public string EmitCSharpSimdModel(string top, int lanes, RtlStateMode stateMode = RtlStateMode.FourState, string @namespace = "VeloSim.Generated")
    {
        ValidateSimdLanes(lanes);
        if (!Identifier(top) || ("V" + top).Contains("__", StringComparison.Ordinal)) throw new ArgumentException("Invalid external model top name.", nameof(top));
        if (!CanUseSimdLanes(stateMode))
            throw new IrValidationException("SIMD --lanes export currently supports non-timed <=64-bit event-driven and combinational designs without runtime loops, forces, SVA, or dynamic containers.");
        bool twoState = stateMode == RtlStateMode.TwoState;
        string className = "V" + top;
        string vt = CsSimdVecType(lanes);
        string vs = CsSimdVecStatic(lanes);
        string wt = CsSimdWordType(twoState, lanes);
        var names = CSharpPortNames();
        var procs = CombinationalOnly ? Array.Empty<JsonElement>() : (Kind(Body) == "parallel" ? Body.GetProperty("items").EnumerateArray().ToArray() : [Body]);
        var eventIds = CombinationalOnly ? Array.Empty<int>() : EventSignals.Select(SignalId).ToArray();
        int[] seqIds = CombinationalOnly ? [] : Statements(Body).Where(e => Kind(e) == "nba" && Kind(e.GetProperty("target")) != "index")
            .SelectMany(e => TargetLeaves(e.GetProperty("target"))).Select(t => SignalId(TargetOwner(t)))
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
        bool combPureInternal = !CombinationalOnly && SettledSteps.Count > 0 && SettledSteps.All(s => CsExprIsPureInternal(s.GetProperty("value")));

        var code = new StringBuilder();
        code.AppendLine("// <auto-generated/>");
        code.AppendLine("#nullable enable");
        code.AppendLine("using System;");
        code.AppendLine("using System.Numerics;");
        code.AppendLine("using System.Runtime.CompilerServices;");
        code.AppendLine("using System.Runtime.Intrinsics;");
        code.AppendLine($"namespace {@namespace};");
        code.AppendLine();
        code.AppendLine($"/// <summary>100% safe lockstep {lanes}-lane SIMD model ({vt}, {(twoState ? "2-state" : "4-state W4")}).</summary>");
        code.AppendLine($"public sealed class {className}");
        code.AppendLine("{");
        code.AppendLine($"    public const int Lanes = {lanes};");
        code.AppendLine("    private ulong _time;");
        if (combPureInternal) code.AppendLine("    private bool _combDirty = true;");
        for (int i = 0; i < Signals.Count; ++i)
            code.AppendLine($"    private {wt} _s{i};");
        foreach (int id in eventIds)
            code.AppendLine($"    private {wt} _prev{id};");

        for (int i = 0; i < Memories.Count; ++i)
        {
            int depth = Memories[i].Depth;
            string mm = CsMaskHex(Memories[i].Width);
            string elemType = twoState ? "ulong" : "W4";
            // Lane-major flat storage [lane * depth + addr] for fast per-lane gather/scatter without cross-lane false sharing.
            int totalCells = depth * lanes;
            if (totalCells <= 4096)
            {
                code.AppendLine($"    [InlineArray({totalCells})] private struct _MemBuf{i} {{ private {elemType} _e0; }}");
                code.AppendLine($"    private _MemBuf{i} _m{i};");
            }
            else
            {
                code.AppendLine($"    private readonly {elemType}[] _m{i} = new {elemType}[{totalCells}];");
            }
            string baseRef = totalCells <= 4096 ? $"ref _m{i}[0]" : $"ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(_m{i})";
            if (twoState)
            {
                code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] private {vt} _ReadMem{i}({vt} idx)");
                code.AppendLine("    {");
                code.AppendLine($"        ref ulong b = {baseRef};");
                for (int l = 0; l < lanes; ++l)
                    code.AppendLine($"        ulong a{l} = idx.GetElement({l}); ulong v{l} = a{l} < {depth}UL ? Unsafe.Add(ref b, (nint)({l * depth} + (int)a{l})) : 0UL;");
                code.AppendLine($"        return {vs}.Create({string.Join(", ", Enumerable.Range(0, lanes).Select(l => $"v{l}"))});");
                code.AppendLine("    }");
                code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] private void _WriteMem{i}({vt} mask, {vt} idx, {vt} val)");
                code.AppendLine("    {");
                code.AppendLine($"        if (mask == {vs}<ulong>.Zero) return;");
                code.AppendLine($"        ref ulong b = {baseRef};");
                for (int l = 0; l < lanes; ++l)
                    code.AppendLine($"        if (mask.GetElement({l}) != 0UL) {{ ulong a = idx.GetElement({l}); if (a < {depth}UL) Unsafe.Add(ref b, (nint)({l * depth} + (int)a)) = val.GetElement({l}); }}");
                code.AppendLine("    }");
            }
            else
            {
                code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] private W4x{lanes} _ReadMem{i}(W4x{lanes} idx)");
                code.AppendLine("    {");
                code.AppendLine($"        ref W4 b = {baseRef};");
                for (int l = 0; l < lanes; ++l)
                    code.AppendLine($"        ulong a{l} = idx.A.GetElement({l}), b{l} = idx.B.GetElement({l}); W4 w{l} = (b{l} == 0UL && a{l} < {depth}UL) ? Unsafe.Add(ref b, (nint)({l * depth} + (int)a{l})) : new W4({mm}, {mm});");
                code.AppendLine($"        return new W4x{lanes}({vs}.Create({string.Join(", ", Enumerable.Range(0, lanes).Select(l => $"w{l}.A"))}), {vs}.Create({string.Join(", ", Enumerable.Range(0, lanes).Select(l => $"w{l}.B"))}));");
                code.AppendLine("    }");
                code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] private void _WriteMem{i}({vt} mask, {vt} idx, W4x{lanes} val)");
                code.AppendLine("    {");
                code.AppendLine($"        if (mask == {vs}<ulong>.Zero) return;");
                code.AppendLine($"        ref W4 b = {baseRef};");
                for (int l = 0; l < lanes; ++l)
                    code.AppendLine($"        if (mask.GetElement({l}) != 0UL) {{ ulong a = idx.GetElement({l}); if (a < {depth}UL) Unsafe.Add(ref b, (nint)({l * depth} + (int)a)) = new W4(val.A.GetElement({l}), val.B.GetElement({l})); }}");
                code.AppendLine("    }");
            }
        }
        if (needsMemLog)
        {
            code.AppendLine($"    private readonly SimdMemLog{(twoState ? "2" : "4")}x{lanes} _mlog = new();");
            code.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)] private void _CommitMemLog()");
            code.AppendLine("    {");
            code.AppendLine("        for (int i = 0; i < _mlog.Count; ++i)");
            code.AppendLine("        {");
            code.AppendLine("            switch (_mlog.MemAt(i))");
            code.AppendLine("            {");
            for (int i = 0; i < Memories.Count; ++i)
                code.AppendLine($"                case {i}: _WriteMem{i}(_mlog.MaskAt(i), _mlog.IdxAt(i), _mlog.ValAt(i)); break;");
            code.AppendLine("            }");
            code.AppendLine("        }");
            code.AppendLine("        _mlog.Clear();");
            code.AppendLine("    }");
        }
        code.AppendLine();
        code.AppendLine($"    public {className}()");
        code.AppendLine("    {");
        for (int i = 0; i < Signals.Count; ++i)
        {
            var sig = Signals[i];
            if (twoState) code.AppendLine($"        _s{i} = {vs}<ulong>.Zero;");
            else if (sig.Name == Clock || sig.TwoState) code.AppendLine($"        _s{i} = default;");
            else code.AppendLine($"        _s{i} = W4x{lanes}Ops.Unknown({CsMaskHex(sig.Width)});");
        }
        foreach (int id in eventIds)
        {
            var sig = Signals[id];
            if (twoState) code.AppendLine($"        _prev{id} = {vs}<ulong>.Zero;");
            else if (sig.Name == Clock || sig.TwoState) code.AppendLine($"        _prev{id} = default;");
            else code.AppendLine($"        _prev{id} = W4x{lanes}Ops.Unknown(0x1UL);");
        }
        for (int i = 0; i < Memories.Count; ++i)
        {
            var mem = Memories[i];
            if (!twoState && !mem.TwoState)
                code.AppendLine($"        for (int j = 0; j < {mem.Depth * lanes}; ++j) _m{i}[j] = new W4({CsMaskHex(mem.Width)}, {CsMaskHex(mem.Width)});");
        }
        code.AppendLine("    }");
        code.AppendLine();
        code.AppendLine("    public ulong Time => _time;");
        code.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)] public void TimeInc(ulong delta) => _time += delta;");
        code.AppendLine("    public void Final() { }");

        // Per-lane, broadcast, vector, and fault-injection divergence port APIs.
        foreach (var sig in Signals.Where(s => s.Direction != "internal"))
        {
            int id = SignalId(sig.Name);
            string prop = names[sig.Name];
            string sm = CsMaskHex(sig.Width);
            if (sig.Direction == "input")
            {
                if (twoState)
                {
                    code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] public ulong Get_{prop}(int lane) => _s{id}.GetElement(lane);");
                    code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] public void Set_{prop}(int lane, ulong value) => _s{id} = _s{id}.WithElement(lane, {(sig.Width >= 64 ? "value" : $"value & {sm}")});");
                    code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] public void Set_{prop}_All(ulong value) => _s{id} = {vs}.Create({(sig.Width >= 64 ? "value" : $"value & {sm}")});");
                    code.AppendLine($"    public {vt} {prop}_Vec {{ [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _s{id}; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => _s{id} = {(sig.Width >= 64 ? "value" : $"value & {vs}.Create({sm})")}; }}");
                }
                else
                {
                    code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] public ulong Get_{prop}(int lane) => W4x{lanes}Ops.LaneToUInt64(_s{id}, lane);");
                    code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] public void Set_{prop}(int lane, ulong value) => _s{id} = new W4x{lanes}(_s{id}.A.WithElement(lane, {(sig.Width >= 64 ? "value" : $"value & {sm}")}), _s{id}.B.WithElement(lane, 0UL));");
                    code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] public void Set_{prop}_All(ulong value) => _s{id} = new W4x{lanes}({vs}.Create({(sig.Width >= 64 ? "value" : $"value & {sm}")}), {vs}<ulong>.Zero);");
                    code.AppendLine($"    public {vt} {prop}_Vec {{ [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _s{id}.A; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => _s{id} = new W4x{lanes}({(sig.Width >= 64 ? "value" : $"value & {vs}.Create({sm})")}, {vs}<ulong>.Zero); }}");
                }
            }
            else
            {
                if (twoState)
                {
                    code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] public ulong Get_{prop}(int lane) => _s{id}.GetElement(lane);");
                    code.AppendLine($"    public {vt} {prop}_Vec {{ [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _s{id}; }}");
                    code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] public uint DivergedMask_{prop}(int goldenLane = 0) => (uint)(~{vs}.Equals(_s{id}, {vs}.Create(_s{id}.GetElement(goldenLane)))).ExtractMostSignificantBits();");
                }
                else
                {
                    code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] public ulong Get_{prop}(int lane) => W4x{lanes}Ops.LaneToUInt64(_s{id}, lane);");
                    code.AppendLine($"    public {vt} {prop}_Vec {{ [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _s{id}.A; }}");
                    code.AppendLine($"    public W4x{lanes} {prop}_W4Vec {{ [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _s{id}; }}");
                    code.AppendLine($"    [MethodImpl(MethodImplOptions.AggressiveInlining)] public uint DivergedMask_{prop}(int goldenLane = 0) => (uint)(~({vs}.Equals(_s{id}.A, {vs}.Create(_s{id}.A.GetElement(goldenLane))) & {vs}.Equals(_s{id}.B, {vs}.Create(_s{id}.B.GetElement(goldenLane))))).ExtractMostSignificantBits();");
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
            string expr = twoState ? CsSimdExpr2(rhs, lanes, Signals[id].Width) : CsSimdExpr4(rhs, lanes, Signals[id].Width);
            code.AppendLine($"        _s{id} = {CsSimdStore(expr, Signals[id], twoState, lanes, exactWidth ? Signals[id].Width : -1)};");
        }
        code.AppendLine("    }");
        code.AppendLine();

        // Render sequential edge body once so we can adaptively outline if large.
        string seqBody = "";
        if (!CombinationalOnly)
        {
            var sb = new StringBuilder();
            foreach (int id in seqIds) sb.AppendLine($"        {wt} _next{id} = _s{id};");
            foreach (int id in singleWriteMems.Order())
            {
                sb.AppendLine($"        {vt} _mw{id}_mask = {vs}<ulong>.Zero;");
                sb.AppendLine($"        {vt} _mw{id}_idx = {vs}<ulong>.Zero;");
                sb.AppendLine($"        {wt} _mw{id}_val = default;");
            }
            int unique = 0;
            for (int p = 0; p < procs.Length; ++p)
                CsSimdStmt(sb, procs[p].GetProperty("body"), $"ev{p}", ref unique, twoState, lanes, singleWriteMems);
            foreach (int id in seqIds) sb.AppendLine($"        _s{id} = _next{id};");
            foreach (int id in singleWriteMems.Order())
                sb.AppendLine($"        _WriteMem{id}(_mw{id}_mask, _mw{id}_idx, _mw{id}_val);");
            if (needsMemLog) sb.AppendLine("        _CommitMemLog();");
            sb.AppendLine("        Settle();");
            seqBody = sb.ToString();
        }
        bool outlineEdge = !CombinationalOnly && seqBody.Length > (twoState ? 2500 : 1200);
        if (outlineEdge)
        {
            string evParams = string.Join(", ", Enumerable.Range(0, procs.Length).Select(p => $"{vt} ev{p}"));
            code.AppendLine($"    private void EvalEdge({evParams})");
            code.AppendLine("    {");
            code.Append(seqBody);
            code.AppendLine("    }");
            code.AppendLine();
        }

        code.AppendLine("    [MethodImpl(MethodImplOptions.AggressiveInlining)]");
        code.AppendLine("    public void Eval()");
        code.AppendLine("    {");
        if (combPureInternal)
            code.AppendLine("        if (_combDirty) { Settle(); _combDirty = false; }");
        else
            code.AppendLine("        Settle();");
        if (!CombinationalOnly)
        {
            for (int p = 0; p < procs.Length; ++p)
            {
                string cond = string.Join(" | ", procs[p].GetProperty("events").EnumerateArray().Select(tr =>
                {
                    int id = SignalId(tr.GetProperty("signal").GetString()!);
                    bool pos = tr.GetProperty("edge").GetString() == "posedge";
                    return twoState
                        ? (pos ? $"({vs}.Equals(_prev{id}&{vs}.Create(1UL),{vs}<ulong>.Zero)&{vs}.Equals(_s{id}&{vs}.Create(1UL),{vs}.Create(1UL)))"
                               : $"({vs}.Equals(_prev{id}&{vs}.Create(1UL),{vs}.Create(1UL))&{vs}.Equals(_s{id}&{vs}.Create(1UL),{vs}<ulong>.Zero))")
                        : $"W4x{lanes}Ops.EdgeMask(_prev{id},_s{id},{(pos ? "true" : "false")})";
                }));
                code.AppendLine($"        {vt} ev{p} = {cond};");
            }
            string anyEv = string.Join(" | ", Enumerable.Range(0, procs.Length).Select(p => $"ev{p}"));
            if (outlineEdge)
            {
                string evArgs = string.Join(", ", Enumerable.Range(0, procs.Length).Select(p => $"ev{p}"));
                code.AppendLine($"        if (({anyEv}) != {vs}<ulong>.Zero) EvalEdge({evArgs});");
            }
            else
            {
                code.AppendLine($"        if (({anyEv}) != {vs}<ulong>.Zero)");
                code.AppendLine("        {");
                code.Append(seqBody);
                code.AppendLine("        }");
            }
            foreach (int id in eventIds) code.AppendLine($"        _prev{id} = _s{id};");
        }
        code.AppendLine("    }");
        code.AppendLine("}");
        return code.ToString();
    }

    /// <summary>Emits the 100% safe C# lockstep SIMD runtime (Vector256/Vector512) for 4 or 8 lanes.</summary>
    internal static string EmitCSharpSimdRuntime(int lanes, string @namespace = "VeloSim.Generated")
    {
        ValidateSimdLanes(lanes);
        string vt = lanes == 8 ? "Vector512<ulong>" : "Vector256<ulong>";
        string vs = lanes == 8 ? "Vector512" : "Vector256";
        string sLanes = lanes.ToString();
        string laneCreate(string expr) => $"{vs}.Create({string.Join(", ", Enumerable.Range(0, lanes).Select(l => expr.Replace("$L", l.ToString())))})";
        string laneArgs(string expr) => string.Join(", ", Enumerable.Range(0, lanes).Select(l => expr.Replace("$L", l.ToString())));
        var sb = new StringBuilder();
        sb.Append($$"""
// <auto-generated/>
#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace {{@namespace}};

public readonly struct W4
{
    public readonly ulong A;
    public readonly ulong B;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public W4(ulong a, ulong b) { A = a; B = b; }
}

public readonly struct W4x{{sLanes}}
{
    public readonly {{vt}} A;
    public readonly {{vt}} B;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public W4x{{sLanes}}({{vt}} a, {{vt}} b) { A = a; B = b; }
}

public static class W2x{{sLanes}}Ops
{
    private static readonly {{vt}} One = {{vs}}.Create(1UL);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Mask(int w) => w >= 64 ? ulong.MaxValue : (w <= 0 ? 0UL : ((1UL << w) - 1UL));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long SExt(ulong v, int w) { if (w >= 64) return unchecked((long)v); int sh = 64 - w; return unchecked((long)(v << sh)) >> sh; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} Resize({{vt}} a, int w) => w >= 64 ? a : (a & {{vs}}.Create(Mask(w)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} SResize({{vt}} a, int aw, int tw)
    {
        ulong tm = Mask(tw);
        if (tw <= aw) return a & {{vs}}.Create(tm);
        ulong am = Mask(aw);
        ulong fill = tm & ~am;
        {{vt}} signBit = {{vs}}.ShiftRightLogical(a, aw - 1) & One;
        {{vt}} signMask = {{vs}}.Equals(signBit, One);
        return (a & {{vs}}.Create(am)) | (signMask & {{vs}}.Create(fill));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} MemIdx({{vt}} idx, int lower)
    {
        {{vt}} lo = {{vs}}.Create((ulong)lower);
        {{vt}} ok = {{vs}}.GreaterThanOrEqual(idx, lo);
        return {{vs}}.ConditionalSelect(ok, idx - lo, {{vs}}.Create(ulong.MaxValue));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} SCmp({{vt}} a, {{vt}} b, int w, int op)
    {
        static ulong OneLane(ulong x, ulong y, int w, int op)
        {
            long sx = SExt(x, w), sy = SExt(y, w);
            bool r = op == 0 ? sx < sy : op == 1 ? sx <= sy : op == 2 ? sx > sy : sx >= sy;
            return r ? 1UL : 0UL;
        }
        return {{laneCreate("OneLane(a.GetElement($L),b.GetElement($L),w,op)")}};
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} Cmp({{vt}} a, {{vt}} b, int op) =>
        (op == 0 ? {{vs}}.LessThan(a, b) : op == 1 ? {{vs}}.LessThanOrEqual(a, b) : op == 2 ? {{vs}}.GreaterThan(a, b) : {{vs}}.GreaterThanOrEqual(a, b)) & One;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} Product({{vt}} a, {{vt}} b, int w, int op)
    {
        ulong m = Mask(w);
        static ulong OneLane(ulong x, ulong y, ulong m, int op)
        {
            x &= m; y &= m;
            if (op == 0) return unchecked(x * y) & m;
            if (y == 0UL) return 0UL;
            return (op == 1 ? (x / y) : (x % y)) & m;
        }
        return {{laneCreate("OneLane(a.GetElement($L),b.GetElement($L),m,op)")}};
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} SProduct({{vt}} a, {{vt}} b, int w, int op)
    {
        ulong m = Mask(w);
        static ulong OneLane(ulong x, ulong y, int w, ulong m, int op)
        {
            long sx = SExt(x, w), sy = SExt(y, w);
            if (op == 0) return unchecked((ulong)sx * (ulong)sy) & m;
            if (sy == 0L) return 0UL;
            if (sx == long.MinValue && sy == -1L) return op == 1 ? ((ulong)sx & m) : 0UL;
            return (ulong)(op == 1 ? (sx / sy) : (sx % sy)) & m;
        }
        return {{laneCreate("OneLane(a.GetElement($L),b.GetElement($L),w,m,op)")}};
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} Shift({{vt}} a, {{vt}} b, int w, bool right)
    {
        ulong m = Mask(w);
        static ulong OneLane(ulong x, ulong y, ulong m, bool right) => y >= 64UL ? 0UL : (right ? ((x & m) >> (int)y) : ((x << (int)y) & m));
        return {{laneCreate("OneLane(a.GetElement($L),b.GetElement($L),m,right)")}};
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} SShift({{vt}} a, {{vt}} b, int w)
    {
        ulong m = Mask(w);
        static ulong OneLane(ulong x, ulong y, int w, ulong m)
        {
            long sx = SExt(x, w);
            if (y >= 64UL) return sx < 0L ? m : 0UL;
            return (ulong)(sx >> (int)y) & m;
        }
        return {{laneCreate("OneLane(a.GetElement($L),b.GetElement($L),w,m)")}};
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} Red({{vt}} a, int w, int op)
    {
        ulong m = Mask(w);
        static ulong OneLane(ulong x, ulong m, int op)
        {
            x &= m;
            if (op == 0) return x == m ? 1UL : 0UL;
            if (op == 1) return x == m ? 0UL : 1UL;
            if (op == 2) return x != 0UL ? 1UL : 0UL;
            if (op == 3) return x != 0UL ? 0UL : 1UL;
            bool odd = (BitOperations.PopCount(x) & 1) != 0;
            return (odd ^ (op == 5)) ? 1UL : 0UL;
        }
        return {{laneCreate("OneLane(a.GetElement($L),m,op)")}};
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} SelBit({{vt}} a, int aw, {{vt}} idx)
    {
        static ulong OneLane(ulong x, int aw, ulong i) => i < (ulong)aw ? ((x >> (int)i) & 1UL) : 0UL;
        return {{laneCreate("OneLane(a.GetElement($L),aw,idx.GetElement($L))")}};
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} SelPart({{vt}} a, int aw, int left, int right) =>
        right >= aw ? {{vs}}<ulong>.Zero : ({{vs}}.ShiftRightLogical(a & {{vs}}.Create(Mask(aw)), right) & {{vs}}.Create(Mask(left - right + 1)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} SelIdx({{vt}} a, int aw, {{vt}} b, int width, bool desc)
    {
        static ulong OneLane(ulong x, int aw, ulong b, int width, bool desc)
        {
            long lo = desc ? (long)b - (width - 1) : (long)b;
            long srcLo = lo < 0 ? 0 : lo;
            long srcHi = (lo + width) < aw ? (lo + width) : aw;
            if (srcLo >= srcHi) return 0UL;
            int count = (int)(srcHi - srcLo), dstLo = (int)(srcLo - lo);
            return (((x & Mask(aw)) >> (int)srcLo) & Mask(count)) << dstLo;
        }
        return {{laneCreate("OneLane(a.GetElement($L),aw,b.GetElement($L),width,desc)")}};
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} DepIdx({{vt}} before, int tw, {{vt}} b, int width, bool desc, {{vt}} val)
    {
        static ulong OneLane(ulong bef, int tw, ulong b, int width, bool desc, ulong v)
        {
            v &= Mask(width);
            long lo = desc ? (long)b - (width - 1) : (long)b;
            long dstLo = lo < 0 ? 0 : lo;
            long dstHi = (lo + width) < tw ? (lo + width) : tw;
            if (dstLo >= dstHi) return bef;
            int count = (int)(dstHi - dstLo), srcLo = (int)(dstLo - lo), dLo = (int)dstLo;
            ulong cm = Mask(count), dm = cm << dLo;
            return (bef & ~dm) | (((v >> srcLo) & cm) << dLo);
        }
        return {{laneCreate("OneLane(before.GetElement($L),tw,b.GetElement($L),width,desc,val.GetElement($L))")}};
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} CountOnes({{vt}} a, int w) { ulong m = Mask(w); return {{laneCreate("(ulong)BitOperations.PopCount(a.GetElement($L)&m)")}}; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} OneHot({{vt}} a, int w) { ulong m = Mask(w); return {{laneCreate("(BitOperations.PopCount(a.GetElement($L)&m)==1?1UL:0UL)")}}; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} OneHot0({{vt}} a, int w) { ulong m = Mask(w); return {{laneCreate("(BitOperations.PopCount(a.GetElement($L)&m)<=1?1UL:0UL)")}}; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} CLog2({{vt}} a, int w) { ulong m = Mask(w); static ulong L(ulong x) => x <= 1UL ? 0UL : (ulong)(64 - BitOperations.LeadingZeroCount(x - 1UL)); return {{laneCreate("L(a.GetElement($L)&m)")}}; }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} ResolvePair({{vt}} a, {{vt}} b, int nk, int w) => (nk == 1 ? (a & b) : (a | b)) & {{vs}}.Create(Mask(w));
}

public static class W4x{{sLanes}}Ops
{
    private static readonly {{vt}} One = {{vs}}.Create(1UL);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} Unknown(ulong m) { var mv = {{vs}}.Create(m); return new W4x{{sLanes}}(mv, mv); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong LaneToUInt64(W4x{{sLanes}} a, int lane)
    {
        if (a.B.GetElement(lane) != 0UL) throw new InvalidOperationException("integer conversion of X/Z port");
        return a.A.GetElement(lane);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} Select({{vt}} mask, W4x{{sLanes}} t, W4x{{sLanes}} f) =>
        new W4x{{sLanes}}({{vs}}.ConditionalSelect(mask, t.A, f.A), {{vs}}.ConditionalSelect(mask, t.B, f.B));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} FromMask({{vt}} mask) => new W4x{{sLanes}}(mask & One, {{vs}}<ulong>.Zero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} Resize(W4x{{sLanes}} a, ulong m) { var mv = {{vs}}.Create(m); return new W4x{{sLanes}}(a.A & mv, a.B & mv); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} TwoState(W4x{{sLanes}} a, ulong m) => new W4x{{sLanes}}(a.A & ~a.B & {{vs}}.Create(m), {{vs}}<ulong>.Zero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} SResize(W4x{{sLanes}} a, int aw, ulong am, ulong wm)
    {
        var wmv = {{vs}}.Create(wm);
        if (wm <= am) return new W4x{{sLanes}}(a.A & wmv, a.B & wmv);
        var fill = {{vs}}.Create(wm & ~am);
        int top = aw - 1;
        var sa = {{vs}}.Equals({{vs}}.ShiftRightLogical(a.A, top) & One, One) & fill;
        var sb = {{vs}}.Equals({{vs}}.ShiftRightLogical(a.B, top) & One, One) & fill;
        return new W4x{{sLanes}}(a.A | sa, a.B | sb);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} EdgeMask(W4x{{sLanes}} prev, W4x{{sLanes}} curr, bool pos)
    {
        {{vt}} bit = pos
            ? ((((~prev.A & ~prev.B) & (curr.A | curr.B)) | (prev.B & curr.A & ~curr.B)) & One)
            : ((((prev.A & ~prev.B) & (~curr.A | curr.B)) | (prev.B & ~curr.A & ~curr.B)) & One);
        return {{vs}}.Equals(bit, One);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} YesMask(W4x{{sLanes}} a) => ~{{vs}}.Equals(a.A & ~a.B, {{vs}}<ulong>.Zero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} IsZeroMask(W4x{{sLanes}} a) => {{vs}}.Equals(a.A | a.B, {{vs}}<ulong>.Zero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} ExactMask(W4x{{sLanes}} a, W4x{{sLanes}} b) => {{vs}}.Equals(a.A, b.A) & {{vs}}.Equals(a.B, b.B);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} EqYesMask(W4x{{sLanes}} a, W4x{{sLanes}} b) => {{vs}}.Equals(a.B | b.B, {{vs}}<ulong>.Zero) & {{vs}}.Equals(a.A, b.A);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} NeqYesMask(W4x{{sLanes}} a, W4x{{sLanes}} b) => ~{{vs}}.Equals((a.A ^ b.A) & ~(a.B | b.B), {{vs}}<ulong>.Zero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} LNot(W4x{{sLanes}} a)
    {
        {{vt}} nt = {{vs}}.Equals(a.A & ~a.B, {{vs}}<ulong>.Zero) & One;
        {{vt}} unk = ~{{vs}}.Equals(a.B, {{vs}}<ulong>.Zero) & nt;
        return new W4x{{sLanes}}(nt, unk);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} LAnd(W4x{{sLanes}} a, W4x{{sLanes}} b)
    {
        {{vt}} nz = ~{{vs}}.Equals(a.A | a.B, {{vs}}<ulong>.Zero) & ~{{vs}}.Equals(b.A | b.B, {{vs}}<ulong>.Zero) & One;
        {{vt}} bothTrue = ~{{vs}}.Equals(a.A & ~a.B, {{vs}}<ulong>.Zero) & ~{{vs}}.Equals(b.A & ~b.B, {{vs}}<ulong>.Zero);
        return new W4x{{sLanes}}(nz, ~bothTrue & nz);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} LOr(W4x{{sLanes}} a, W4x{{sLanes}} b)
    {
        {{vt}} nz = ~{{vs}}.Equals(a.A | a.B | b.A | b.B, {{vs}}<ulong>.Zero) & One;
        {{vt}} anyTrue = ~{{vs}}.Equals((a.A & ~a.B) | (b.A & ~b.B), {{vs}}<ulong>.Zero);
        return new W4x{{sLanes}}(nz, ~anyTrue & nz);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} BNot(W4x{{sLanes}} a, ulong m)
    {
        var mv = {{vs}}.Create(m);
        var ob = a.B & mv;
        return new W4x{{sLanes}}(((~a.A) | ob) & mv, ob);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} BBin(W4x{{sLanes}} a, W4x{{sLanes}} b, ulong m, int op)
    {
        var mv = {{vs}}.Create(m);
        if (op == 2)
        {
            var ob = (a.B | b.B) & mv;
            return new W4x{{sLanes}}(((a.A ^ b.A) | ob) & mv, ob);
        }
        if (op == 0)
        {
            var o0 = ((~a.A & ~a.B) | (~b.A & ~b.B)) & mv;
            var o1 = (a.A & ~a.B) & (b.A & ~b.B) & mv;
            var ox = ~(o0 | o1) & mv;
            return new W4x{{sLanes}}((o1 | ox) & mv, ox);
        }
        {
            var o1 = ((a.A & ~a.B) | (b.A & ~b.B)) & mv;
            var o0 = (~a.A & ~a.B) & (~b.A & ~b.B) & mv;
            var ox = ~(o0 | o1) & mv;
            return new W4x{{sLanes}}((o1 | ox) & mv, ox);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} Eq(W4x{{sLanes}} a, W4x{{sLanes}} b)
    {
        {{vt}} u = a.B | b.B;
        {{vt}} eq = {{vs}}.Equals((a.A ^ b.A) & ~u, {{vs}}<ulong>.Zero) & One;
        {{vt}} unk = ~{{vs}}.Equals(u, {{vs}}<ulong>.Zero) & eq;
        return new W4x{{sLanes}}(eq, unk);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} WildEq(W4x{{sLanes}} a, W4x{{sLanes}} b, ulong m)
    {
        var mv = {{vs}}.Create(m);
        var care = ~b.B & mv;
        var au = a.B & care;
        var known = care & ~au;
        var eq = {{vs}}.Equals((a.A ^ b.A) & known, {{vs}}<ulong>.Zero) & One;
        var unk = ~{{vs}}.Equals(au, {{vs}}<ulong>.Zero) & eq;
        return new W4x{{sLanes}}(eq, unk);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} CasezMatchMask(W4x{{sLanes}} a, W4x{{sLanes}} b, ulong m)
    {
        var care = ~((~a.A & a.B) | (~b.A & b.B)) & {{vs}}.Create(m);
        return {{vs}}.Equals(((a.A ^ b.A) | (a.B ^ b.B)) & care, {{vs}}<ulong>.Zero);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static {{vt}} CasexMatchMask(W4x{{sLanes}} a, W4x{{sLanes}} b, ulong m)
    {
        var care = ~(a.B | b.B) & {{vs}}.Create(m);
        return {{vs}}.Equals((a.A ^ b.A) & care, {{vs}}<ulong>.Zero);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} Cmp(W4x{{sLanes}} a, W4x{{sLanes}} b, int op)
    {
        {{vt}} unk = ~{{vs}}.Equals(a.B | b.B, {{vs}}<ulong>.Zero);
        {{vt}} r = op == 0 ? {{vs}}.LessThan(a.A, b.A) : op == 1 ? {{vs}}.LessThanOrEqual(a.A, b.A) : op == 2 ? {{vs}}.GreaterThan(a.A, b.A) : {{vs}}.GreaterThanOrEqual(a.A, b.A);
        return new W4x{{sLanes}}((r | unk) & One, unk & One);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} SCmp(W4x{{sLanes}} a, W4x{{sLanes}} b, int w, int op)
    {
        {{vt}} unk = ~{{vs}}.Equals(a.B | b.B, {{vs}}<ulong>.Zero);
        {{vt}} r = W2x{{sLanes}}Ops.SCmp(a.A, b.A, w, op);
        return new W4x{{sLanes}}(r | (unk & One), unk & One);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} Arith(W4x{{sLanes}} a, W4x{{sLanes}} b, ulong m, bool sub)
    {
        var mv = {{vs}}.Create(m);
        var unk = ~{{vs}}.Equals(a.B | b.B, {{vs}}<ulong>.Zero);
        var res = (sub ? (a.A - b.A) : (a.A + b.A)) & mv;
        return new W4x{{sLanes}}({{vs}}.ConditionalSelect(unk, mv, res), unk & mv);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} ShlConst(W4x{{sLanes}} a, int sh, ulong m)
    {
        if (sh >= 64) return default;
        var mv = {{vs}}.Create(m);
        return new W4x{{sLanes}}({{vs}}.ShiftLeft(a.A, sh) & mv, {{vs}}.ShiftLeft(a.B, sh) & mv);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} ShrConst(W4x{{sLanes}} a, int sh, ulong m)
    {
        if (sh >= 64) return default;
        var mv = {{vs}}.Create(m);
        return new W4x{{sLanes}}({{vs}}.ShiftRightLogical(a.A & mv, sh), {{vs}}.ShiftRightLogical(a.B & mv, sh));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} Shift(W4x{{sLanes}} a, W4x{{sLanes}} b, ulong m, bool right)
    {
        static W4 L(ulong aa, ulong ab, ulong ba, ulong bb, ulong m, bool right)
        {
            if (bb != 0UL) return new W4(m, m);
            if (ba >= 64UL) return default;
            int sh = (int)ba;
            return right ? new W4((aa & m) >> sh, (ab & m) >> sh) : new W4((aa << sh) & m, (ab << sh) & m);
        }
        return FromLanes({{laneArgs("L(a.A.GetElement($L),a.B.GetElement($L),b.A.GetElement($L),b.B.GetElement($L),m,right)")}});
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} SShift(W4x{{sLanes}} a, W4x{{sLanes}} b, int w, ulong m)
    {
        static W4 L(ulong aa, ulong ab, ulong ba, ulong bb, int w, ulong m)
        {
            if (bb != 0UL) return new W4(m, m);
            int top = w - 1;
            ulong sa = (aa >> top) & 1UL, sb = (ab >> top) & 1UL;
            if (ba >= 64UL) return new W4(sa != 0 ? m : 0UL, sb != 0 ? m : 0UL);
            int sh = (int)ba;
            ulong fill = sh == 0 ? 0UL : (m & ~(m >> sh));
            return new W4((((aa & m) >> sh) | (sa != 0 ? fill : 0UL)) & m, (((ab & m) >> sh) | (sb != 0 ? fill : 0UL)) & m);
        }
        return FromLanes({{laneArgs("L(a.A.GetElement($L),a.B.GetElement($L),b.A.GetElement($L),b.B.GetElement($L),w,m)")}});
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} Product(W4x{{sLanes}} a, W4x{{sLanes}} b, ulong m, int op)
    {
        var mv = {{vs}}.Create(m);
        var unk = ~{{vs}}.Equals((a.B | b.B) & mv, {{vs}}<ulong>.Zero);
        if (op == 0)
        {
            var prod = ((a.A & mv) * (b.A & mv)) & mv;
            return new W4x{{sLanes}}({{vs}}.ConditionalSelect(unk, mv, prod), unk & mv);
        }
        var divZero = {{vs}}.Equals(b.A & mv, {{vs}}<ulong>.Zero);
        var bad = unk | divZero;
        static ulong L(ulong x, ulong y, ulong m, int op) { x &= m; y &= m; if (y == 0UL) return m; return (op == 1 ? (x / y) : (x % y)) & m; }
        var r = {{laneCreate("L(a.A.GetElement($L),b.A.GetElement($L),m,op)")}};
        return new W4x{{sLanes}}({{vs}}.ConditionalSelect(bad, mv, r), bad & mv);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} SProduct(W4x{{sLanes}} a, W4x{{sLanes}} b, int w, ulong m, int op)
    {
        var mv = {{vs}}.Create(m);
        var unk = ~{{vs}}.Equals((a.B | b.B) & mv, {{vs}}<ulong>.Zero);
        var divZero = op == 0 ? {{vs}}<ulong>.Zero : {{vs}}.Equals(b.A & mv, {{vs}}<ulong>.Zero);
        var bad = unk | divZero;
        var r = W2x{{sLanes}}Ops.SProduct(a.A, b.A, w, op);
        return new W4x{{sLanes}}({{vs}}.ConditionalSelect(bad, mv, r), bad & mv);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} Cond(W4x{{sLanes}} c, W4x{{sLanes}} a, W4x{{sLanes}} b, ulong m)
    {
        var mv = {{vs}}.Create(m);
        var t1 = YesMask(c);
        var t0 = IsZeroMask(c);
        var diff = ((a.A ^ b.A) | (a.B ^ b.B)) & mv;
        var outA = {{vs}}.ConditionalSelect(t1, a.A & mv, {{vs}}.ConditionalSelect(t0, b.A & mv, (a.A & mv) | diff));
        var outB = {{vs}}.ConditionalSelect(t1, a.B & mv, {{vs}}.ConditionalSelect(t0, b.B & mv, (a.B & mv) | diff));
        return new W4x{{sLanes}}(outA, outB);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} Concat(W4x{{sLanes}} a, W4x{{sLanes}} b, int bw, ulong bm)
    {
        var bmv = {{vs}}.Create(bm);
        if (bw >= 64) return new W4x{{sLanes}}(b.A & bmv, b.B & bmv);
        return new W4x{{sLanes}}({{vs}}.ShiftLeft(a.A, bw) | (b.A & bmv), {{vs}}.ShiftLeft(a.B, bw) | (b.B & bmv));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} SelPartFast(W4x{{sLanes}} a, int right, ulong m)
    {
        var mv = {{vs}}.Create(m);
        return new W4x{{sLanes}}({{vs}}.ShiftRightLogical(a.A, right) & mv, {{vs}}.ShiftRightLogical(a.B, right) & mv);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} SelPart(W4x{{sLanes}} a, int aw, int left, int right)
    {
        int n = left - right + 1;
        ulong m = W2x{{sLanes}}Ops.Mask(n);
        if (right >= aw) return Unknown(m);
        int avail = aw - right;
        ulong am = W2x{{sLanes}}Ops.Mask(avail < n ? avail : n);
        var amv = {{vs}}.Create(am);
        var oob = {{vs}}.Create(m & ~am);
        return new W4x{{sLanes}}(({{vs}}.ShiftRightLogical(a.A, right) & amv) | oob, ({{vs}}.ShiftRightLogical(a.B, right) & amv) | oob);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} SelBit(W4x{{sLanes}} a, int aw, W4x{{sLanes}} idx)
    {
        static W4 L(ulong aa, ulong ab, int aw, ulong ia, ulong ib) => (ib != 0UL || ia >= (ulong)aw) ? new W4(1UL, 1UL) : new W4((aa >> (int)ia) & 1UL, (ab >> (int)ia) & 1UL);
        return FromLanes({{laneArgs("L(a.A.GetElement($L),a.B.GetElement($L),aw,idx.A.GetElement($L),idx.B.GetElement($L))")}});
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} SelIdx(W4x{{sLanes}} a, int aw, W4x{{sLanes}} b, int width, bool desc)
    {
        ulong wm = W2x{{sLanes}}Ops.Mask(width);
        static W4 L(ulong aa, ulong ab, int aw, ulong ba, ulong bb, int width, bool desc, ulong wm)
        {
            if (bb != 0UL) return new W4(wm, wm);
            long lo = desc ? (long)ba - (width - 1) : (long)ba;
            long srcLo = lo < 0 ? 0 : lo;
            long srcHi = (lo + width) < aw ? (lo + width) : aw;
            if (srcLo >= srcHi) return new W4(wm, wm);
            int count = (int)(srcHi - srcLo), dstLo = (int)(srcLo - lo), sLo = (int)srcLo;
            ulong cm = W2x{{sLanes}}Ops.Mask(count), oob = wm & ~(cm << dstLo);
            return new W4((((aa >> sLo) & cm) << dstLo) | oob, (((ab >> sLo) & cm) << dstLo) | oob);
        }
        return FromLanes({{laneArgs("L(a.A.GetElement($L),a.B.GetElement($L),aw,b.A.GetElement($L),b.B.GetElement($L),width,desc,wm)")}});
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} DepIdx(W4x{{sLanes}} before, int bw, W4x{{sLanes}} b, int width, bool desc, W4x{{sLanes}} val)
    {
        ulong wm = W2x{{sLanes}}Ops.Mask(width);
        static W4 L(ulong ba0, ulong bb0, int bw, ulong ia, ulong ib, int width, bool desc, ulong va, ulong vb, ulong wm)
        {
            if (ib != 0UL) return new W4(ba0, bb0);
            va &= wm; vb &= wm;
            long lo = desc ? (long)ia - (width - 1) : (long)ia;
            long dstLo = lo < 0 ? 0 : lo;
            long dstHi = (lo + width) < bw ? (lo + width) : bw;
            if (dstLo >= dstHi) return new W4(ba0, bb0);
            int count = (int)(dstHi - dstLo), srcLo = (int)(dstLo - lo), dLo = (int)dstLo;
            ulong cm = W2x{{sLanes}}Ops.Mask(count), dm = cm << dLo;
            return new W4((ba0 & ~dm) | (((va >> srcLo) & cm) << dLo), (bb0 & ~dm) | (((vb >> srcLo) & cm) << dLo));
        }
        return FromLanes({{laneArgs("L(before.A.GetElement($L),before.B.GetElement($L),bw,b.A.GetElement($L),b.B.GetElement($L),width,desc,val.A.GetElement($L),val.B.GetElement($L),wm)")}});
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} MemIdx(W4x{{sLanes}} idx, int lower)
    {
        var lo = {{vs}}.Create((ulong)lower);
        var bad = ~{{vs}}.Equals(idx.B, {{vs}}<ulong>.Zero) | {{vs}}.LessThan(idx.A, lo);
        return new W4x{{sLanes}}({{vs}}.ConditionalSelect(bad, One, idx.A - lo), bad & One);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} Red(W4x{{sLanes}} a, ulong m, int op)
    {
        static W4 L(ulong aa, ulong ab, ulong m, int op)
        {
            ulong u = ab & m, v = aa & ~u & m;
            if (op == 0 || op == 1) { if ((~(v | u) & m) != 0UL) return new W4(op == 1 ? 1UL : 0UL, 0UL); return u != 0UL ? new W4(1UL, 1UL) : new W4(op == 0 ? 1UL : 0UL, 0UL); }
            if (op == 2 || op == 3) { if (v != 0UL) return new W4(op == 2 ? 1UL : 0UL, 0UL); return u != 0UL ? new W4(1UL, 1UL) : new W4(op == 3 ? 1UL : 0UL, 0UL); }
            if (u != 0UL) return new W4(1UL, 1UL);
            return new W4((((BitOperations.PopCount(v) & 1) != 0) ^ (op == 5)) ? 1UL : 0UL, 0UL);
        }
        return FromLanes({{laneArgs("L(a.A.GetElement($L),a.B.GetElement($L),m,op)")}});
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} CountOnes(W4x{{sLanes}} a, ulong m) { var o = a.A & ~a.B & {{vs}}.Create(m); return new W4x{{sLanes}}({{laneCreate("(ulong)BitOperations.PopCount(o.GetElement($L))")}}, {{vs}}<ulong>.Zero); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} OneHot(W4x{{sLanes}} a, ulong m) { var o = a.A & ~a.B & {{vs}}.Create(m); return new W4x{{sLanes}}({{laneCreate("(BitOperations.PopCount(o.GetElement($L))==1?1UL:0UL)")}}, {{vs}}<ulong>.Zero); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} OneHot0(W4x{{sLanes}} a, ulong m) { var o = a.A & ~a.B & {{vs}}.Create(m); return new W4x{{sLanes}}({{laneCreate("(BitOperations.PopCount(o.GetElement($L))<=1?1UL:0UL)")}}, {{vs}}<ulong>.Zero); }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} IsUnknown(W4x{{sLanes}} a, ulong m) => new W4x{{sLanes}}(~{{vs}}.Equals(a.B & {{vs}}.Create(m), {{vs}}<ulong>.Zero) & One, {{vs}}<ulong>.Zero);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} CLog2(W4x{{sLanes}} a, ulong m)
    {
        static ulong L(ulong aa, ulong ab, ulong m) { if ((ab & m) != 0UL) return 0UL; ulong x = aa & m; return x <= 1UL ? 0UL : (ulong)(64 - BitOperations.LeadingZeroCount(x - 1UL)); }
        return new W4x{{sLanes}}({{laneCreate("L(a.A.GetElement($L),a.B.GetElement($L),m)")}}, {{vs}}<ulong>.Zero);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} ResolvePair(W4x{{sLanes}} a, W4x{{sLanes}} b, int nk, ulong m)
    {
        var mv = {{vs}}.Create(m);
        var va = a.A & ~a.B & mv; var za = ~a.A & a.B & mv; var a0 = ~a.A & ~a.B & mv;
        var vb = b.A & ~b.B & mv; var zb = ~b.A & b.B & mv; var b0 = ~b.A & ~b.B & mv;
        var zOut = za & zb;
        {{vt}} vOut, o0;
        if (nk == 1) { o0 = (a0 | b0) & mv; vOut = (~o0) & ((va & (vb | zb)) | (za & vb)) & mv; }
        else if (nk == 2) { vOut = (va | vb) & mv; o0 = (~vOut) & ((a0 & (b0 | zb)) | (za & b0)) & mv; }
        else { vOut = ((va & (vb | zb)) | (za & vb)) & mv; o0 = ((a0 & (b0 | zb)) | (za & b0)) & mv; }
        var xOut = ~(zOut | vOut | o0) & mv;
        return new W4x{{sLanes}}((vOut | xOut) & mv, (xOut | zOut) & mv);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static W4x{{sLanes}} ResolveFinalize(W4x{{sLanes}} acc, int nk, ulong m)
    {
        var mv = {{vs}}.Create(m);
        if (nk == 3) return new W4x{{sLanes}}(acc.A & mv, (acc.A & acc.B) & mv);
        if (nk == 4) return new W4x{{sLanes}}((acc.A | acc.B) & mv, (acc.A & acc.B) & mv);
        return acc;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static W4x{{sLanes}} FromLanes({{string.Join(", ", Enumerable.Range(0, lanes).Select(l => $"W4 w{l}"))}}) =>
        new W4x{{sLanes}}({{vs}}.Create({{string.Join(", ", Enumerable.Range(0, lanes).Select(l => $"w{l}.A"))}}), {{vs}}.Create({{string.Join(", ", Enumerable.Range(0, lanes).Select(l => $"w{l}.B"))}}));
}

public sealed class SimdMemLog2x{{sLanes}}
{
    private readonly List<(int mem, {{vt}} mask, {{vt}} idx, {{vt}} val)> _entries = new(8);
    public int Count => _entries.Count;
    public int MemAt(int i) => _entries[i].mem;
    public {{vt}} MaskAt(int i) => _entries[i].mask;
    public {{vt}} IdxAt(int i) => _entries[i].idx;
    public {{vt}} ValAt(int i) => _entries[i].val;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(int memId, int depth, {{vt}} activeMask, {{vt}} idx, {{vt}} val)
    {
        var ok = activeMask & {{vs}}.LessThan(idx, {{vs}}.Create((ulong)depth));
        if (ok != {{vs}}<ulong>.Zero) _entries.Add((memId, ok, idx, val));
    }
    public void Clear() => _entries.Clear();
}

public sealed class SimdMemLog4x{{sLanes}}
{
    private readonly List<(int mem, {{vt}} mask, {{vt}} idx, W4x{{sLanes}} val)> _entries = new(8);
    public int Count => _entries.Count;
    public int MemAt(int i) => _entries[i].mem;
    public {{vt}} MaskAt(int i) => _entries[i].mask;
    public {{vt}} IdxAt(int i) => _entries[i].idx;
    public W4x{{sLanes}} ValAt(int i) => _entries[i].val;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(int memId, int depth, {{vt}} activeMask, W4x{{sLanes}} idx, W4x{{sLanes}} val)
    {
        var ok = activeMask & {{vs}}.Equals(idx.B, {{vs}}<ulong>.Zero) & {{vs}}.LessThan(idx.A, {{vs}}.Create((ulong)depth));
        if (ok != {{vs}}<ulong>.Zero) _entries.Add((memId, ok, idx.A, val));
    }
    public void Clear() => _entries.Clear();
}
""");
        return sb.ToString();
    }
}
