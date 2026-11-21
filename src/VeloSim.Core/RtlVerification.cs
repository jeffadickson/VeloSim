using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed record RtlValueRangeBin(ulong Lo, ulong Hi);
public sealed record RtlDefaultBin;
public sealed record RtlIgnoreBin(ulong Lo, ulong Hi);
public sealed record RtlIllegalBin(ulong Lo, ulong Hi);

/// <summary>Finite mutually exclusive coverpoint bin semantics; C# 15 checks consumer switches for exhaustiveness.</summary>
public union RtlCoverBinSpec(RtlValueRangeBin, RtlDefaultBin, RtlIgnoreBin, RtlIllegalBin);

public sealed record RtlCoverBin(string Name, RtlCoverBinSpec Spec)
{
    public ulong Lo => Spec switch
    {
        RtlValueRangeBin r => r.Lo,
        RtlIgnoreBin ig => ig.Lo,
        RtlIllegalBin il => il.Lo,
        RtlDefaultBin => 0UL
    };

    public ulong Hi => Spec switch
    {
        RtlValueRangeBin r => r.Hi,
        RtlIgnoreBin ig => ig.Hi,
        RtlIllegalBin il => il.Hi,
        RtlDefaultBin => 0UL
    };

    public bool IsDefault => Spec is RtlDefaultBin;
    public bool IsIgnore => Spec is RtlIgnoreBin;
    public bool IsIllegal => Spec is RtlIllegalBin;
}

public sealed record RtlCoverpoint(string Name, JsonElement Value, IReadOnlyList<RtlCoverBin> Bins);
public sealed record RtlCoverCross(string Name, int CpA, int CpB);
public sealed record RtlCovergroup(string Name, IReadOnlyList<RtlCoverpoint> Points, IReadOnlyList<RtlCoverCross> Crosses);
public sealed record RtlProperty(
    string Name,
    string Kind,
    string Clock,
    string Edge,
    JsonElement? DisableIff,
    JsonElement? Antecedent,
    int Delay,
    JsonElement Consequent,
    string? Message);

public sealed partial class RtlDesign
{
    public IReadOnlyList<RtlCovergroup> Covergroups { get; private set; } = [];
    public IReadOnlyList<RtlProperty> Properties { get; private set; } = [];
    internal Dictionary<int, int> PastDepths { get; } = [];
    private readonly Dictionary<string, int> covergroupIds = [with(StringComparer.Ordinal)];

    internal int CovergroupId(string name) =>
        covergroupIds.TryGetValue(name, out int id) ? id : throw new IrValidationException("Unknown RTL covergroup: " + name);

    internal static int ParseNetKind(string net) => net switch
    {
        "wire" or "tri" => 0,
        "wand" or "triand" => 1,
        "wor" or "trior" => 2,
        "tri0" => 3,
        "tri1" => 4,
        _ => throw new IrValidationException("Unsupported resolved net kind: " + net)
    };

    internal void RecordPastRequirement(int signalId, int ticks)
    {
        if (ticks < 1 || ticks > 16) throw new IrValidationException("$past ticks must be 1..16.");
        if (!PastDepths.TryGetValue(signalId, out int cur) || ticks > cur)
            PastDepths[signalId] = ticks;
    }

    private void ValidateCovergroups(JsonElement root)
    {
        if (!root.TryGetProperty("covergroups", out var arr)) return;
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() > 64)
            throw new IrValidationException("Covergroup count must be 0..64.");
        var list = new List<RtlCovergroup>();
        foreach (var cgEl in arr.EnumerateArray())
        {
            JsonShape.Fields(cgEl, ["name", "coverpoints"], ["crosses"]);
            string cgName = cgEl.GetProperty("name").GetString()!;
            if (!Identifier(cgName) || !covergroupIds.TryAdd(cgName, list.Count))
                throw new IrValidationException("Invalid or duplicate covergroup name: " + cgName);
            var cpsEl = cgEl.GetProperty("coverpoints");
            if (cpsEl.ValueKind != JsonValueKind.Array || cpsEl.GetArrayLength() is < 1 or > 32)
                throw new IrValidationException("Covergroup requires 1..32 coverpoints.");
            Dictionary<string, int> cpNames = [with(StringComparer.Ordinal)];
            var points = new List<RtlCoverpoint>();
            foreach (var cpEl in cpsEl.EnumerateArray())
            {
                JsonShape.Fields(cpEl, ["name", "value", "bins"]);
                string cpName = cpEl.GetProperty("name").GetString()!;
                if (!Identifier(cpName) || !cpNames.TryAdd(cpName, points.Count))
                    throw new IrValidationException("Invalid or duplicate coverpoint name: " + cpName);
                var valEl = cpEl.GetProperty("value").Clone();
                int vw = ValidateExpression(valEl, 1);
                if (vw > 64) throw new IrValidationException("Coverpoint expression width must be 1..64.");
                var binsEl = cpEl.GetProperty("bins");
                if (binsEl.ValueKind != JsonValueKind.Array || binsEl.GetArrayLength() is < 1 or > 128)
                    throw new IrValidationException("Coverpoint requires 1..128 bins.");
                HashSet<string> binNames = [with(StringComparer.Ordinal)];
                var bins = new List<RtlCoverBin>();
                foreach (var bEl in binsEl.EnumerateArray())
                {
                    JsonShape.Fields(bEl, ["name"], ["lo", "hi", "value", "default", "ignore", "illegal"]);
                    string bName = bEl.GetProperty("name").GetString()!;
                    if (!Identifier(bName) || !binNames.Add(bName))
                        throw new IrValidationException("Invalid or duplicate coverpoint bin name: " + bName);
                    bool isDef = bEl.TryGetProperty("default", out var dEl) && dEl.GetBoolean();
                    bool isIgn = bEl.TryGetProperty("ignore", out var iEl) && iEl.GetBoolean();
                    bool isIll = bEl.TryGetProperty("illegal", out var ilEl) && ilEl.GetBoolean();
                    if (isDef && (isIgn || isIll))
                        throw new IrValidationException("Default coverpoint bin cannot also be ignore or illegal.");
                    if (isIgn && isIll)
                        throw new IrValidationException("Coverpoint bin cannot be both ignore and illegal.");
                    RtlCoverBinSpec spec;
                    if (isDef)
                    {
                        spec = new RtlDefaultBin();
                    }
                    else
                    {
                        ulong lo, hi;
                        if (bEl.TryGetProperty("value", out var singleVal))
                        {
                            lo = hi = singleVal.GetUInt64();
                        }
                        else
                        {
                            lo = bEl.GetProperty("lo").GetUInt64();
                            hi = bEl.GetProperty("hi").GetUInt64();
                            if (lo > hi) throw new IrValidationException("Coverpoint bin range requires lo <= hi.");
                        }
                        spec = isIgn ? new RtlIgnoreBin(lo, hi)
                            : isIll ? new RtlIllegalBin(lo, hi)
                            : new RtlValueRangeBin(lo, hi);
                    }
                    bins.Add(new RtlCoverBin(bName, spec));
                }
                points.Add(new RtlCoverpoint(cpName, valEl, bins));
            }
            var crosses = new List<RtlCoverCross>();
            if (cgEl.TryGetProperty("crosses", out var crArr))
            {
                if (crArr.ValueKind != JsonValueKind.Array || crArr.GetArrayLength() > 32)
                    throw new IrValidationException("Covergroup supports 0..32 crosses.");
                HashSet<string> crNames = [with(StringComparer.Ordinal)];
                foreach (var crEl in crArr.EnumerateArray())
                {
                    JsonShape.Fields(crEl, ["name", "a", "b"]);
                    string crName = crEl.GetProperty("name").GetString()!;
                    if (!Identifier(crName) || !crNames.Add(crName))
                        throw new IrValidationException("Invalid or duplicate cross name: " + crName);
                    string aName = crEl.GetProperty("a").GetString()!;
                    string bName = crEl.GetProperty("b").GetString()!;
                    if (!cpNames.TryGetValue(aName, out int aIdx) || !cpNames.TryGetValue(bName, out int bIdx) || aIdx == bIdx)
                        throw new IrValidationException("Cross must reference two distinct coverpoints in the same covergroup.");
                    crosses.Add(new RtlCoverCross(crName, aIdx, bIdx));
                }
            }
            list.Add(new RtlCovergroup(cgName, points, crosses));
        }
        Covergroups = list;
    }

    private void ValidateProperties(JsonElement root)
    {
        if (!root.TryGetProperty("properties", out var arr)) return;
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() > 128)
            throw new IrValidationException("Property count must be 0..128.");
        HashSet<string> names = [with(StringComparer.Ordinal)];
        var list = new List<RtlProperty>();
        foreach (var pEl in arr.EnumerateArray())
        {
            JsonShape.Fields(pEl, ["name", "kind", "clock", "consequent"], ["edge", "disable_iff", "antecedent", "delay", "message"]);
            string pName = pEl.GetProperty("name").GetString()!;
            if (!Identifier(pName) || !names.Add(pName))
                throw new IrValidationException("Invalid or duplicate property name: " + pName);
            string kind = pEl.GetProperty("kind").GetString()!;
            if (kind is not ("assert" or "cover" or "assume"))
                throw new IrValidationException("Property kind must be assert, cover, or assume.");
            string clkName = pEl.GetProperty("clock").GetString()!;
            var clkSig = Signals[SignalId(clkName)];
            if (clkSig.Width != 1 || clkSig.Direction != "input")
                throw new IrValidationException("Property clock must be a scalar input.");
            string edge = pEl.TryGetProperty("edge", out var eEl) ? eEl.GetString()! : "posedge";
            if (edge is not ("posedge" or "negedge"))
                throw new IrValidationException("Property edge must be posedge or negedge.");
            int delay = pEl.TryGetProperty("delay", out var dEl) ? dEl.GetInt32() : 0;
            if (delay is < 0 or > 64)
                throw new IrValidationException("Property implication delay must be 0..64 cycles.");
            JsonElement? disableIff = null;
            if (pEl.TryGetProperty("disable_iff", out var disEl) && disEl.ValueKind != JsonValueKind.Null)
            {
                ValidateExpression(disEl, 1);
                disableIff = disEl.Clone();
            }
            JsonElement? antecedent = null;
            if (pEl.TryGetProperty("antecedent", out var antEl) && antEl.ValueKind != JsonValueKind.Null)
            {
                ValidateExpression(antEl, 1);
                antecedent = antEl.Clone();
            }
            var consEl = pEl.GetProperty("consequent").Clone();
            ValidateExpression(consEl, 1);
            string? msg = null;
            if (pEl.TryGetProperty("message", out var msgEl) && msgEl.ValueKind != JsonValueKind.Null)
            {
                msg = msgEl.GetString();
                if (msg is null || msg.Length > 256) throw new IrValidationException("Invalid property message.");
            }
            list.Add(new RtlProperty(pName, kind, clkName, edge, disableIff, antecedent, delay, consEl, msg));
        }
        Properties = list;
    }

    internal IReadOnlyList<int> PropertyClockSignalIds()
    {
        var ids = new HashSet<int>();
        foreach (var p in Properties) ids.Add(SignalId(p.Clock));
        if (PastDepths.Count > 0 && Clock is not null) ids.Add(SignalId(Clock));
        return ids.Order().ToArray();
    }

    internal void EmitCppCovergroupRegistration(StringBuilder code)
    {
        foreach (var cg in Covergroups)
        {
            code.AppendLine("{vsrtl::CovergroupSpec _cg;");
            code.AppendLine($"_cg.name={CppStringLiteral(cg.Name)};");
            foreach (var cp in cg.Points)
            {
                code.Append("{vsrtl::CoverpointSpec _cp;");
                code.Append($"_cp.name={CppStringLiteral(cp.Name)};");
                foreach (var b in cp.Bins)
                {
                    (ulong lo, ulong hi, string isDef, string isIgn, string isIll) = b.Spec switch
                    {
                        RtlValueRangeBin r => (r.Lo, r.Hi, "false", "false", "false"),
                        RtlDefaultBin => (0UL, 0UL, "true", "false", "false"),
                        RtlIgnoreBin ig => (ig.Lo, ig.Hi, "false", "true", "false"),
                        RtlIllegalBin il => (il.Lo, il.Hi, "false", "false", "true")
                    };
                    code.Append($"_cp.bins.push_back({{{CppStringLiteral(b.Name)},{lo}ULL,{hi}ULL,{isDef},{isIgn},{isIll},0ULL}});");
                }
                code.AppendLine("_cg.points.push_back(std::move(_cp));}");
            }
            foreach (var cr in cg.Crosses)
                code.AppendLine($"_cg.crosses.push_back({{{CppStringLiteral(cr.Name)},{cr.CpA}u,{cr.CpB}u,{{}}}});");
            code.AppendLine("registerCovergroup(std::move(_cg));}");
        }
    }

    internal void EmitCppPropertyMembers(StringBuilder code, bool narrow64, bool twoState)
    {
        if (Properties.Count == 0 && PastDepths.Count == 0) return;
        for (int i = 0; i < Properties.Count; ++i)
        {
            if (Properties[i].Delay > 0)
                code.AppendLine($"bool _vs_prop_pipe_{i}[{Properties[i].Delay}]{{}};");
        }
        string wt = twoState ? "uint64_t" : narrow64 ? "w64::W64" : "W";
        foreach (var (sid, depth) in PastDepths.OrderBy(kv => kv.Key))
            code.AppendLine($"{wt} _vs_past_{sid}[{depth}];");
        foreach (int cid in PropertyClockSignalIds())
            code.AppendLine($"{wt} _vs_pclk_{cid};");
    }

    internal void EmitCppPropertyInit(StringBuilder code, bool narrow64, bool twoState)
    {
        foreach (var p in Properties)
            code.AppendLine($"registerProperty({CppStringLiteral(p.Name)},{CppStringLiteral(p.Kind)});");
        foreach (var (sid, depth) in PastDepths.OrderBy(kv => kv.Key))
        {
            var sig = Signals[sid];
            string initVal = twoState ? "0ULL"
                : narrow64 ? (sig.TwoState ? $"vsrtl::w64::lit(0ULL,{sig.Width})" : $"vsrtl::w64::unknown({sig.Width})")
                : (sig.TwoState ? $"word(0,0,0,{sig.Width})" : $"unknown({sig.Width})");
            code.AppendLine($"for(int _k=0;_k<{depth};++_k)_vs_past_{sid}[_k]={initVal};");
        }
        foreach (int cid in PropertyClockSignalIds())
        {
            string zero = twoState ? "0ULL" : narrow64 ? "w64::lit(0ULL,1)" : "lit(0ULL,1)";
            code.AppendLine($"_vs_pclk_{cid}={zero};");
        }
    }

    internal void EmitCppPropertyEvaluation(
        StringBuilder code,
        Func<JsonElement, string> emitExpr,
        Func<string, string> emitYes,
        Func<int, string> emitSig,
        Func<int, string, string> emitEdge,
        bool narrow64,
        bool twoState)
    {
        if (Properties.Count == 0 && PastDepths.Count == 0) return;
        for (int i = 0; i < Properties.Count; ++i)
        {
            var p = Properties[i];
            int cid = SignalId(p.Clock);
            code.AppendLine($"if({emitEdge(cid, p.Edge)}){{");
            if (p.DisableIff is { } dis)
            {
                code.AppendLine($"if({emitYes(emitExpr(dis))}){{");
                if (p.Delay > 0)
                    code.AppendLine($"for(int _k=0;_k<{p.Delay};++_k)_vs_prop_pipe_{i}[_k]=false;");
                code.AppendLine("}else{");
            }
            string antExpr = p.Antecedent is { } ant ? emitYes(emitExpr(ant)) : "true";
            string consExpr = emitYes(emitExpr(p.Consequent));
            string failMsg = CppStringLiteral(string.IsNullOrEmpty(p.Message) ? $"SVA property '{p.Name}' failed" : $"SVA property '{p.Name}' failed: {p.Message}");
            if (p.Delay == 0)
            {
                code.AppendLine($"if({antExpr}){{if({consExpr}){{recordPropertyPass({i});}}else{{recordPropertyFail({i});{(p.Kind == "assert" ? $"throw std::runtime_error({failMsg});" : "")}}}}}");
                if (p.Kind != "cover") code.AppendLine($"else{{recordPropertyVacuous({i});}}");
            }
            else
            {
                code.AppendLine($"if(_vs_prop_pipe_{i}[{p.Delay - 1}]){{if({consExpr}){{recordPropertyPass({i});}}else{{recordPropertyFail({i});{(p.Kind == "assert" ? $"throw std::runtime_error({failMsg});" : "")}}}}}");
                for (int k = p.Delay - 1; k > 0; --k)
                    code.AppendLine($"_vs_prop_pipe_{i}[{k}]=_vs_prop_pipe_{i}[{k - 1}];");
                code.AppendLine($"bool _vs_ant_{i}={antExpr};_vs_prop_pipe_{i}[0]=_vs_ant_{i};");
                if (p.Kind != "cover") code.AppendLine($"if(!_vs_ant_{i})recordPropertyVacuous({i});");
            }
            if (p.DisableIff is not null) code.AppendLine("}");
            code.AppendLine("}");
        }
        // Shift $past ring buffers on primary clock (or first property clock) edge AFTER sampling.
        if (PastDepths.Count > 0)
        {
            int pastCid = Clock is not null ? SignalId(Clock) : SignalId(Properties[0].Clock);
            string pastEdge = Properties.FirstOrDefault(p => SignalId(p.Clock) == pastCid)?.Edge ?? "posedge";
            code.AppendLine($"if({emitEdge(pastCid, pastEdge)}){{");
            foreach (var (sid, depth) in PastDepths.OrderBy(kv => kv.Key))
            {
                for (int k = depth - 1; k > 0; --k)
                    code.AppendLine($"_vs_past_{sid}[{k}]=_vs_past_{sid}[{k - 1}];");
                code.AppendLine($"_vs_past_{sid}[0]={emitSig(sid)};");
            }
            code.AppendLine("}");
        }
        foreach (int cid in PropertyClockSignalIds())
            code.AppendLine($"_vs_pclk_{cid}={emitSig(cid)};");
    }

    internal string EmitCppSampleCovergroupStmt(JsonElement e, Func<JsonElement, string> emitExpr, bool narrow64, bool twoState)
    {
        int cgId = CovergroupId(e.GetProperty("name").GetString()!);
        var cg = Covergroups[cgId];
        var sb = new StringBuilder();
        sb.Append("{std::vector<uint64_t> _cgv;std::vector<bool> _cgk;");
        foreach (var cp in cg.Points)
        {
            string ex = emitExpr(cp.Value);
            if (twoState)
                sb.Append($"_cgv.push_back({ex});_cgk.push_back(true);");
            else if (narrow64)
                sb.Append($"{{auto _v={ex};_cgv.push_back(_v.v);_cgk.push_back(((_v.x|_v.z)&mask64_unchecked(_v.w))==0);}}");
            else
                sb.Append($"{{auto _v={ex};_cgv.push_back(_v.v.limb[0]);_cgk.push_back(!has_xz(_v));}}");
        }
        sb.Append($"sampleCovergroup({cgId},_cgv,_cgk);}}");
        return sb.ToString();
    }
}
