using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace VeloSim;

public sealed record RtlSignal(string Name, int Width, string Direction, string? SourcePath = null, bool Signed = false, bool TwoState = false);
public sealed record RtlMemory(string Name, int Width, int Depth, bool Signed = false, int LowerBound = 0);

internal closed class RtlExpr(int width, bool signed)
{
    public int Width { get; } = width;
    public bool Signed { get; } = signed;
}
internal sealed class RtlRefExpr(int signalId, int width, bool signed) : RtlExpr(width, signed)
{
    public int SignalId { get; } = signalId;
}
internal sealed class RtlConstExpr(string bits, int width, bool signed) : RtlExpr(width, signed)
{
    public string Bits { get; } = bits;
}
internal sealed class RtlMemIndexExpr(int memoryId, RtlExpr index, int width, bool signed) : RtlExpr(width, signed)
{
    public int MemoryId { get; } = memoryId;
    public RtlExpr Index { get; } = index;
}
internal sealed class RtlBitSelectExpr(RtlExpr value, RtlExpr index) : RtlExpr(1, false)
{
    public RtlExpr Value { get; } = value;
    public RtlExpr Index { get; } = index;
}
internal sealed class RtlPartSelectExpr(RtlExpr value, int left, int right) : RtlExpr(left - right + 1, false)
{
    public RtlExpr Value { get; } = value;
    public int Left { get; } = left;
    public int Right { get; } = right;
}
internal sealed class RtlIndexedSelectExpr(RtlExpr value, RtlExpr basis, int width, bool descending) : RtlExpr(width, false)
{
    public RtlExpr Value { get; } = value;
    public RtlExpr Base { get; } = basis;
    public bool Descending { get; } = descending;
}
internal sealed class RtlUnaryExpr(string op, RtlExpr arg, int width, bool signed) : RtlExpr(width, signed)
{
    public string Op { get; } = op;
    public RtlExpr Arg { get; } = arg;
}
internal sealed class RtlBinaryExpr(string op, RtlExpr left, RtlExpr right, int width, bool signed) : RtlExpr(width, signed)
{
    public string Op { get; } = op;
    public RtlExpr Left { get; } = left;
    public RtlExpr Right { get; } = right;
}
internal sealed class RtlCondExpr(RtlExpr condition, RtlExpr whenTrue, RtlExpr whenFalse, int width, bool signed) : RtlExpr(width, signed)
{
    public RtlExpr Condition { get; } = condition;
    public RtlExpr WhenTrue { get; } = whenTrue;
    public RtlExpr WhenFalse { get; } = whenFalse;
}

internal closed record class RtlStmt;
internal sealed record class RtlBlockStmt(RtlStmt[] Items) : RtlStmt;
internal sealed record class RtlParallelStmt(RtlStmt[] Items) : RtlStmt;
internal sealed record class RtlProcessStmt((int SignalId, bool Posedge)[] Events, RtlStmt Body) : RtlStmt;
internal sealed record class RtlRepeatStmt(int Count, RtlStmt Body) : RtlStmt;
internal sealed record class RtlBlockingStmt(int TargetId, RtlExpr Value) : RtlStmt;
internal sealed record class RtlNbaSignalStmt(RtlExpr Target, RtlExpr Value) : RtlStmt;
internal sealed record class RtlNbaMemStmt(int MemoryId, RtlExpr Index, RtlExpr Value) : RtlStmt;
internal sealed record class RtlIfStmt(RtlExpr Condition, RtlStmt Then, RtlStmt Else) : RtlStmt;
internal sealed record class RtlCaseStmt(RtlExpr Value, (RtlConstExpr? Match, RtlStmt Body)[] Items) : RtlStmt;

public sealed partial class RtlDesign
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
    [GeneratedRegex("^[A-Za-z0-9_.$\\[\\]:]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SourcePathRegex();

    public IReadOnlyList<RtlSignal> Signals { get; }
    public IReadOnlyList<RtlMemory> Memories { get; }
    public IReadOnlyList<JsonElement> Continuous { get; }
    public JsonElement Body { get; }
    public string Clock { get; }
    public string Json { get; }
    public bool EventDriven => bodyAst is RtlProcessStmt || (bodyAst is RtlParallelStmt p && p.Items.Any(i => i is RtlProcessStmt));

    private readonly Dictionary<string, int> signalIds;
    private readonly Dictionary<string, int> memoryIds;
    private readonly int clockId;
    private readonly RtlStmt bodyAst;
    private readonly (int TargetId, RtlExpr Value)[] continuousAst;
    private readonly int[] eventSignalIds;
    private readonly int[] sequentialSignalIds;
    private readonly bool hasMemoryWrites;
    private int nodes;

    private static int Arity(string kind) => kind switch
    {
        "not" or "bitnot" or "neg" or "pos" or "castsigned" or "castunsigned" or "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor" => 1,
        "add" or "sub" or "and" or "or" or "eq" or "neq" or "caseeq" or "caseneq" or "lt" or "le" or "gt" or "ge" or "concat" or "bitand" or "bitor" or "bitxor" or "bitxnor" or "shl" or "shr" or "ashr" or "mul" or "div" or "mod" => 2,
        "cond" => 3,
        _ => 0
    };
    private static bool Scalar(string kind) => kind is "and" or "or" or "not" or "eq" or "neq" or "caseeq" or "caseneq" or "lt" or "le" or "gt" or "ge" or "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor";
    private static string Kind(JsonElement e) => e.GetProperty("kind").GetString()!;
    private static string Name(JsonElement e) => e.GetProperty("name").GetString()!;
    public int SignalId(string name) => signalIds.TryGetValue(name, out int id) ? id : throw new IrValidationException("Unknown RTL signal: " + name);
    private int MemoryId(string name) => memoryIds.TryGetValue(name, out int id) ? id : throw new IrValidationException("Unknown RTL memory: " + name);
    public static bool Identifier(string name) => IdentifierRegex().IsMatch(name);

    private RtlDesign(string json)
    {
        if (json.Length > 4 * 1024 * 1024) throw new IrValidationException("RTL IR too large.");
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        JsonShape.Fields(root, ["format", "signals", "memories", "continuous", "clock", "body"]);
        if (root.GetProperty("format").GetString() != "velosim.rtl.v1") throw new IrValidationException("Unsupported RTL IR.");
        Signals = root.GetProperty("signals").EnumerateArray().Select(e => { JsonShape.Fields(e, ["name", "width", "direction"], ["path", "signed", "two_state"]); return new RtlSignal(Name(e), e.GetProperty("width").GetInt32(), e.GetProperty("direction").GetString()!, e.TryGetProperty("path", out var path) ? path.GetString() : null, e.TryGetProperty("signed", out var sign) && sign.GetBoolean(), e.TryGetProperty("two_state", out var two) && two.GetBoolean()); }).ToArray();
        Memories = root.GetProperty("memories").EnumerateArray().Select(e => { JsonShape.Fields(e, ["name", "width", "depth"], ["signed", "lower"]); return new RtlMemory(Name(e), e.GetProperty("width").GetInt32(), e.GetProperty("depth").GetInt32(), e.TryGetProperty("signed", out var sign) && sign.GetBoolean(), e.TryGetProperty("lower", out var lower) ? lower.GetInt32() : 0); }).ToArray();
        if (Signals.Count is < 1 or > 256 || Memories.Count > 16 || Signals.Any(s => !Identifier(s.Name) || s.Width is < 1 or > 128 || s.Direction is not ("input" or "output" or "internal")) || Memories.Any(m => !Identifier(m.Name) || m.Width is < 1 or > 128 || m.Depth is < 1 or > 256 || m.LowerBound < 0 || (long)m.LowerBound + m.Depth - 1 > int.MaxValue) || Signals.Select(s => s.Name).Concat(Memories.Select(m => m.Name)).Distinct().Count() != Signals.Count + Memories.Count) throw new IrValidationException("Invalid RTL declarations.");
        signalIds = Signals.Select((s, i) => (s.Name, i)).ToDictionary(s => s.Name, s => s.i, StringComparer.Ordinal);
        memoryIds = Memories.Select((m, i) => (m.Name, i)).ToDictionary(m => m.Name, m => m.i, StringComparer.Ordinal);
        if (Signals.Any(s => s.SourcePath is not null && (s.Direction != "internal" || s.SourcePath.Length > 1024 || !SourcePathRegex().IsMatch(s.SourcePath))) || Signals.Select(s => s.SourcePath ?? s.Name).Distinct(StringComparer.Ordinal).Count() != Signals.Count) throw new IrValidationException("Invalid or duplicate source signal paths.");
        Clock = root.GetProperty("clock").GetString()!; clockId = SignalId(Clock); var clock = Signals[clockId];
        if (clock.Width != 1 || clock.Direction != "input") throw new IrValidationException("Clock must be a scalar input.");
        Body = root.GetProperty("body").Clone();
        var seqSigWriters = new HashSet<int>();
        var seqMemWriters = new HashSet<int>();
        bodyAst = ParseStatement(Body, seqSigWriters, seqMemWriters, 0, Kind(Body) == "parallel");
        sequentialSignalIds = seqSigWriters.Order().ToArray();
        hasMemoryWrites = seqMemWriters.Count > 0;
        _ = StatementCost(bodyAst);
        if (EventDriven && bodyAst is RtlParallelStmt parallel && parallel.Items.Any(p => p is not RtlProcessStmt)) throw new IrValidationException("Explicit and implicit event processes cannot be mixed.");
        eventSignalIds = (bodyAst is RtlParallelStmt par ? par.Items : [bodyAst]).OfType<RtlProcessStmt>().SelectMany(p => p.Events).Select(e => e.SignalId).Distinct().ToArray();
        HashSet<int> forbiddenCombInputs = [clockId, .. eventSignalIds];
        var continuous = root.GetProperty("continuous").EnumerateArray().Select(e => e.Clone()).ToList();
        if (continuous.Count > 256) throw new IrValidationException("Too many continuous assignments.");
        var pending = new Dictionary<int, (JsonElement Raw, RtlExpr Value)>(continuous.Count);
        foreach (var assignment in continuous)
        {
            JsonShape.Fields(assignment, ["target", "value"]);
            var targetExpr = ParseExpression(assignment.GetProperty("target"), 0);
            var valueExpr = ParseExpression(assignment.GetProperty("value"), 0);
            if (References(valueExpr).Any(forbiddenCombInputs.Contains)) throw new IrValidationException("Clock-dependent continuous logic is outside the race-free input-event subset.");
            if (targetExpr is not RtlRefExpr refTarget || Signals[refTarget.SignalId].Direction == "input" || seqSigWriters.Contains(refTarget.SignalId) || !pending.TryAdd(refTarget.SignalId, (assignment, valueExpr))) throw new IrValidationException("Invalid or conflicting continuous writer.");
        }
        // Emit a combinational DAG in dependency order. Feedback and oscillation are rejected.
        List<JsonElement> sortedRaw = [with(capacity: pending.Count)];
        List<(int TargetId, RtlExpr Value)> sortedAst = [with(capacity: pending.Count)];
        while (pending.Count > 0)
        {
            var ready = pending.Where(p => !References(p.Value.Value).Any(pending.ContainsKey)).Select(p => p.Key).ToArray();
            if (ready.Length == 0) throw new IrValidationException("Combinational feedback is unsupported.");
            foreach (int key in ready)
            {
                var item = pending[key];
                sortedRaw.Add(item.Raw);
                sortedAst.Add((key, item.Value));
                pending.Remove(key);
            }
        }
        var combWriters = sortedAst.Select(a => a.TargetId).ToHashSet();
        for (int i = 0; i < Signals.Count; i++)
            if (Signals[i].Direction != "input" && !seqSigWriters.Contains(i) && !combWriters.Contains(i))
                throw new IrValidationException("Undriven non-input signals are unsupported; net/variable initialization must not be guessed.");
        Continuous = sortedRaw.AsReadOnly();
        continuousAst = sortedAst.ToArray();
        ValidateBlockingIsolation();
        Json = json;
    }

    public static RtlDesign Parse(string json)
    {
        try { return new(json); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException or ArgumentException && e is not IrValidationException) { throw new IrValidationException("Malformed RTL IR: " + e.Message); }
    }

    private void Budget(int depth) { if (depth > 64 || ++nodes > 20000) throw new IrValidationException("RTL node/depth budget exceeded."); }

    private RtlExpr ParseIndex(JsonElement e, int depth)
    {
        var expr = ParseExpression(e, depth);
        if (expr.Signed && expr is not RtlConstExpr { Bits: ['0', ..] })
            throw new IrValidationException("Signed indices require an explicit unsigned cast.");
        return expr;
    }

    private RtlExpr ParseExpression(JsonElement e, int depth)
    {
        Budget(depth); string kind = Kind(e);
        if (kind == "ref") { JsonShape.Fields(e, ["kind", "name"]); int id = SignalId(Name(e)); var sig = Signals[id]; return new RtlRefExpr(id, sig.Width, sig.Signed); }
        if (kind == "const")
        {
            JsonShape.Fields(e, ["kind", "bits"], ["signed"]);
            bool signed = e.TryGetProperty("signed", out var sign) && sign.GetBoolean();
            var bits = e.GetProperty("bits").GetString();
            if (string.IsNullOrEmpty(bits) || bits.Length > 128 || bits.Any(c => c is not ('0' or '1' or 'x' or 'z'))) throw new IrValidationException("Invalid RTL constant.");
            return new RtlConstExpr(bits, bits.Length, signed);
        }
        if (kind == "index") { JsonShape.Fields(e, ["kind", "name", "index"]); var idx = ParseIndex(e.GetProperty("index"), depth + 1); int id = MemoryId(Name(e)); var mem = Memories[id]; return new RtlMemIndexExpr(id, idx, mem.Width, mem.Signed); }
        if (kind == "bitselect") { JsonShape.Fields(e, ["kind", "value", "index"]); return new RtlBitSelectExpr(ParseExpression(e.GetProperty("value"), depth + 1), ParseIndex(e.GetProperty("index"), depth + 1)); }
        if (kind == "partselect")
        {
            JsonShape.Fields(e, ["kind", "value", "left", "right"]);
            var val = ParseExpression(e.GetProperty("value"), depth + 1);
            int left = e.GetProperty("left").GetInt32(), right = e.GetProperty("right").GetInt32();
            if (right < 0 || left < right || (long)left - right >= 128) throw new IrValidationException("Only nonnegative descending constant part selects of 1..128 bits supported.");
            return new RtlPartSelectExpr(val, left, right);
        }
        if (kind == "indexedselect")
        {
            JsonShape.Fields(e, ["kind", "value", "base", "width", "descending"]);
            var val = ParseExpression(e.GetProperty("value"), depth + 1);
            var basis = ParseIndex(e.GetProperty("base"), depth + 1);
            int width = e.GetProperty("width").GetInt32();
            bool descending = e.GetProperty("descending").GetBoolean();
            if (width is < 1 or > 128) throw new IrValidationException("Indexed select width must be 1..128.");
            return new RtlIndexedSelectExpr(val, basis, width, descending);
        }
        JsonShape.Fields(e, ["kind", "args"]);
        var rawArgs = e.GetProperty("args").EnumerateArray().ToArray();
        int arity = Arity(kind);
        if (arity == 0 || rawArgs.Length != arity) throw new IrValidationException("Unsupported RTL expression.");
        var args = rawArgs.Select(a => ParseExpression(a, depth + 1)).ToArray();
        int resultWidth = Scalar(kind) ? 1 : kind is "shl" or "shr" or "ashr" ? args[0].Width : kind == "cond" ? Math.Max(args[1].Width, args[2].Width) : kind == "concat" ? args[0].Width + args[1].Width : args.Max(a => a.Width);
        if (resultWidth > 128) throw new IrValidationException("Expression exceeds 128 bits.");
        bool resultSigned = kind switch
        {
            "castsigned" => true,
            "castunsigned" or "concat" => false,
            var k when Scalar(k) => false,
            "shl" or "shr" or "ashr" => args[0].Signed,
            "cond" => args[1].Signed && args[2].Signed,
            _ => args.All(a => a.Signed)
        };
        return arity switch
        {
            1 => new RtlUnaryExpr(kind, args[0], resultWidth, resultSigned),
            2 => new RtlBinaryExpr(kind, args[0], args[1], resultWidth, resultSigned),
            _ => new RtlCondExpr(args[0], args[1], args[2], resultWidth, resultSigned)
        };
    }

    private RtlStmt ParseStatement(JsonElement e, HashSet<int> sigWriters, HashSet<int> memWriters, int depth, bool rootIsParallel)
    {
        Budget(depth);
        string kind = Kind(e);
        switch (kind)
        {
            case "repeat":
                JsonShape.Fields(e, ["kind", "count", "body"]);
                int count = e.GetProperty("count").GetInt32();
                if (count is < 0 or > 256) throw new IrValidationException("Repeat count must be 0..256.");
                return new RtlRepeatStmt(count, ParseStatement(e.GetProperty("body"), sigWriters, memWriters, depth + 1, rootIsParallel));
            case "process":
                if (depth > 1 || (depth == 1 && !rootIsParallel)) throw new IrValidationException("Event processes must be at the root or in a root parallel group.");
                JsonShape.Fields(e, ["kind", "events", "body"]);
                var rawEvents = e.GetProperty("events").EnumerateArray().ToArray();
                if (rawEvents.Length is < 1 or > 16) throw new IrValidationException("Process events must be 1..16.");
                HashSet<string> seen = [with(StringComparer.Ordinal)];
                var events = new (int SignalId, bool Posedge)[rawEvents.Length];
                for (int i = 0; i < rawEvents.Length; i++)
                {
                    var trigger = rawEvents[i];
                    JsonShape.Fields(trigger, ["signal", "edge"]);
                    string signal = trigger.GetProperty("signal").GetString()!, edge = trigger.GetProperty("edge").GetString()!;
                    int sigId = SignalId(signal);
                    var declaration = Signals[sigId];
                    if (declaration.Width != 1 || declaration.Direction != "input" || edge is not ("posedge" or "negedge") || !seen.Add(signal + ":" + edge)) throw new IrValidationException("Events require unique scalar input edges.");
                    events[i] = (sigId, edge == "posedge");
                }
                return new RtlProcessStmt(events, ParseStatement(e.GetProperty("body"), sigWriters, memWriters, depth + 1, rootIsParallel));
            case "parallel":
                if (depth != 0) throw new IrValidationException("Parallel processes must be at the root.");
                JsonShape.Fields(e, ["kind", "items"]);
                var rawProcs = e.GetProperty("items").EnumerateArray().ToArray();
                if (rawProcs.Length is < 1 or > 256) throw new IrValidationException("Parallel process count must be 1..256.");
                var parsedProcs = new RtlStmt[rawProcs.Length];
                for (int i = 0; i < rawProcs.Length; i++)
                {
                    var ownedSigs = new HashSet<int>();
                    var ownedMems = new HashSet<int>();
                    parsedProcs[i] = ParseStatement(rawProcs[i], ownedSigs, ownedMems, depth + 1, rootIsParallel);
                    if (ownedSigs.Overlaps(sigWriters) || ownedMems.Overlaps(memWriters)) throw new IrValidationException("Multiple processes cannot write the same word or memory.");
                    sigWriters.UnionWith(ownedSigs);
                    memWriters.UnionWith(ownedMems);
                }
                return new RtlParallelStmt(parsedProcs);
            case "block":
                JsonShape.Fields(e, ["kind", "items"]);
                return new RtlBlockStmt(e.GetProperty("items").EnumerateArray().Select(item => ParseStatement(item, sigWriters, memWriters, depth + 1, rootIsParallel)).ToArray());
            case "blocking":
            case "nba":
                JsonShape.Fields(e, ["kind", "target", "value"]);
                var target = ParseExpression(e.GetProperty("target"), depth + 1);
                var value = ParseExpression(e.GetProperty("value"), depth + 1);
                if (kind == "blocking" && target is not RtlRefExpr) throw new IrValidationException("Blocking assignments require whole signal words.");
                if (target is RtlMemIndexExpr memTarget)
                {
                    memWriters.Add(memTarget.MemoryId);
                    return new RtlNbaMemStmt(memTarget.MemoryId, memTarget.Index, value);
                }
                var parents = new HashSet<int>();
                foreach (var leaf in TargetLeaves(target))
                {
                    RtlExpr word = leaf switch
                    {
                        RtlBitSelectExpr bs => bs.Value,
                        RtlPartSelectExpr ps => ps.Value,
                        RtlIndexedSelectExpr ix => ix.Value,
                        _ => leaf
                    };
                    if (leaf is not (RtlRefExpr or RtlBitSelectExpr or RtlPartSelectExpr or RtlIndexedSelectExpr)
                        || word is not RtlRefExpr refWord
                        || refWord.SignalId == clockId
                        || Signals[refWord.SignalId].Direction == "input")
                        throw new IrValidationException("Selected sequential targets require a writable declared word.");
                    if (!parents.Add(refWord.SignalId)) throw new IrValidationException("Concatenation targets require distinct destination words.");
                    sigWriters.Add(refWord.SignalId);
                }
                return kind == "blocking" ? new RtlBlockingStmt(((RtlRefExpr)target).SignalId, value) : new RtlNbaSignalStmt(target, value);
            case "if":
                JsonShape.Fields(e, ["kind", "condition", "then", "else"]);
                return new RtlIfStmt(
                    ParseExpression(e.GetProperty("condition"), depth + 1),
                    ParseStatement(e.GetProperty("then"), sigWriters, memWriters, depth + 1, rootIsParallel),
                    ParseStatement(e.GetProperty("else"), sigWriters, memWriters, depth + 1, rootIsParallel));
            case "case":
                JsonShape.Fields(e, ["kind", "value", "items"]);
                var caseVal = ParseExpression(e.GetProperty("value"), depth + 1);
                var rawItems = e.GetProperty("items").EnumerateArray().ToArray();
                var parsedItems = new (RtlConstExpr? Match, RtlStmt Body)[rawItems.Length];
                int defaults = 0;
                for (int i = 0; i < rawItems.Length; i++)
                {
                    var item = rawItems[i];
                    JsonShape.Fields(item, ["match", "body"]);
                    var matchEl = item.GetProperty("match");
                    RtlConstExpr? matchExpr = null;
                    if (matchEl.ValueKind == JsonValueKind.Null) defaults++;
                    else
                    {
                        if (Kind(matchEl) != "const") throw new IrValidationException("Only constant case labels supported.");
                        matchExpr = (RtlConstExpr)ParseExpression(matchEl, depth + 1);
                    }
                    parsedItems[i] = (matchExpr, ParseStatement(item.GetProperty("body"), sigWriters, memWriters, depth + 1, rootIsParallel));
                }
                if (defaults > 1) throw new IrValidationException("Duplicate default case.");
                return new RtlCaseStmt(caseVal, parsedItems);
            default:
                throw new IrValidationException("Unsupported RTL statement.");
        }
    }

    private static IEnumerable<int> References(RtlExpr e)
    {
        switch (e)
        {
            case RtlRefExpr r: yield return r.SignalId; break;
            case RtlConstExpr: break;
            case RtlMemIndexExpr m: foreach (int id in References(m.Index)) yield return id; break;
            case RtlBitSelectExpr bs: foreach (int id in References(bs.Value)) yield return id; foreach (int id in References(bs.Index)) yield return id; break;
            case RtlPartSelectExpr ps: foreach (int id in References(ps.Value)) yield return id; break;
            case RtlIndexedSelectExpr ix: foreach (int id in References(ix.Value)) yield return id; foreach (int id in References(ix.Base)) yield return id; break;
            case RtlUnaryExpr u: foreach (int id in References(u.Arg)) yield return id; break;
            case RtlBinaryExpr b: foreach (int id in References(b.Left)) yield return id; foreach (int id in References(b.Right)) yield return id; break;
            case RtlCondExpr c: foreach (int id in References(c.Condition)) yield return id; foreach (int id in References(c.WhenTrue)) yield return id; foreach (int id in References(c.WhenFalse)) yield return id; break;
        }
    }

    private static IEnumerable<RtlStmt> Statements(RtlStmt s)
    {
        yield return s;
        IEnumerable<RtlStmt> children = s switch
        {
            RtlBlockStmt b => b.Items,
            RtlParallelStmt p => p.Items,
            RtlProcessStmt pr => [pr.Body],
            RtlRepeatStmt r => [r.Body],
            RtlIfStmt f => [f.Then, f.Else],
            RtlCaseStmt c => c.Items.Select(i => i.Body),
            RtlBlockingStmt or RtlNbaSignalStmt or RtlNbaMemStmt => []
        };
        foreach (var child in children)
            foreach (var stmt in Statements(child))
                yield return stmt;
    }

    private static IEnumerable<int> StatementReads(RtlStmt s)
    {
        IEnumerable<RtlExpr> exprs = s switch
        {
            RtlBlockingStmt b => [b.Value],
            RtlNbaSignalStmt n => [n.Target, n.Value],
            RtlNbaMemStmt m => [m.Index, m.Value],
            RtlIfStmt f => [f.Condition],
            RtlCaseStmt c => [c.Value],
            RtlBlockStmt or RtlParallelStmt or RtlProcessStmt or RtlRepeatStmt => []
        };
        foreach (var expr in exprs)
            foreach (int id in References(expr))
                yield return id;
    }

    private void ValidateBlockingIsolation()
    {
        var processes = bodyAst is RtlParallelStmt par ? par.Items : [bodyAst];
        var reads = processes.Select(p => Statements(p).SelectMany(StatementReads).ToHashSet()).ToArray();
        for (int i = 0; i < processes.Length; ++i)
        {
            var stmts = Statements(processes[i]).ToArray();
            var blocking = stmts.OfType<RtlBlockingStmt>().Select(b => b.TargetId).ToHashSet();
            if (blocking.Count == 0) continue;
            var nonblocking = stmts.OfType<RtlNbaSignalStmt>().SelectMany(n => TargetLeaves(n.Target)).Select(TargetSignalId).ToHashSet();
            if (blocking.Overlaps(nonblocking)) throw new IrValidationException("A word cannot mix blocking and nonblocking assignments.");
            if (Enumerable.Range(0, processes.Length).Any(j => j != i && blocking.Overlaps(reads[j])) || continuousAst.Any(a => References(a.Value).Any(blocking.Contains)))
                throw new IrValidationException("Blocking destinations must be isolated from other processes and continuous logic.");
        }
    }

    private static long StatementCost(RtlStmt s)
    {
        long cost = s switch
        {
            RtlParallelStmt p => 1 + p.Items.Sum(StatementCost),
            RtlBlockStmt b => 1 + b.Items.Sum(StatementCost),
            RtlProcessStmt pr => 1 + StatementCost(pr.Body),
            RtlRepeatStmt r => 1 + Math.Max(1, r.Count) * StatementCost(r.Body),
            RtlIfStmt f => 1 + StatementCost(f.Then) + StatementCost(f.Else),
            RtlCaseStmt c => 1 + c.Items.Sum(i => StatementCost(i.Body)),
            RtlBlockingStmt or RtlNbaSignalStmt or RtlNbaMemStmt => 1
        };
        return cost <= 20000 ? cost : throw new IrValidationException("Expanded statement execution budget exceeded.");
    }

    private static IEnumerable<RtlExpr> TargetLeaves(RtlExpr target)
    {
        if (target is RtlBinaryExpr { Op: "concat", Left: var left, Right: var right })
        {
            foreach (var leaf in TargetLeaves(left)) yield return leaf;
            foreach (var leaf in TargetLeaves(right)) yield return leaf;
        }
        else yield return target;
    }

    private static int TargetSignalId(RtlExpr leaf) => leaf switch
    {
        RtlRefExpr r => r.SignalId,
        RtlBitSelectExpr bs => ((RtlRefExpr)bs.Value).SignalId,
        RtlPartSelectExpr ps => ((RtlRefExpr)ps.Value).SignalId,
        RtlIndexedSelectExpr ix => ((RtlRefExpr)ix.Value).SignalId,
        _ => throw new IrValidationException("Selected sequential targets require a writable declared word.")
    };

    private static string ConstantLiteral(string bits)
    {
        ulong vLo = 0, vHi = 0, xLo = 0, xHi = 0, zLo = 0, zHi = 0;
        int w = bits.Length;
        for (int i = 0; i < w; i++)
        {
            int bit = w - 1 - i;
            ulong bitMask = 1UL << (bit & 63);
            char c = bits[i];
            if (bit < 64)
            {
                if (c == '1') vLo |= bitMask;
                else if (c == 'x') xLo |= bitMask;
                else if (c == 'z') zLo |= bitMask;
            }
            else
            {
                if (c == '1') vHi |= bitMask;
                else if (c == 'x') xHi |= bitMask;
                else if (c == 'z') zHi |= bitMask;
            }
        }
        if (vHi == 0 && xLo == 0 && xHi == 0 && zLo == 0 && zHi == 0)
            return $"lit(0x{vLo:x}ULL,{w})";
        return $"lit(0x{vLo:x}ULL,0x{vHi:x}ULL,0x{xLo:x}ULL,0x{xHi:x}ULL,0x{zLo:x}ULL,0x{zHi:x}ULL,{w})";
    }

    private static ulong ConstantUnsignedValue(string bits, int targetWidth, bool signed)
    {
        ulong value = 0;
        foreach (char c in bits) value = (value << 1) | (c == '1' ? 1UL : 0UL);
        if (signed && targetWidth > bits.Length && bits[0] == '1')
        {
            ulong targetMask = targetWidth == 64 ? ulong.MaxValue : (1UL << targetWidth) - 1;
            ulong origMask = bits.Length == 64 ? ulong.MaxValue : (1UL << bits.Length) - 1;
            value |= targetMask ^ origMask;
        }
        return value;
    }

    private static bool IsExactWidth(RtlExpr e, int targetWidth) => e switch
    {
        RtlRefExpr r => r.Width == targetWidth,
        RtlConstExpr c => c.Width == targetWidth,
        RtlMemIndexExpr m => m.Width == targetWidth,
        RtlBitSelectExpr => targetWidth == 1,
        RtlPartSelectExpr p => p.Width == targetWidth,
        RtlIndexedSelectExpr ix => ix.Width == targetWidth,
        RtlUnaryExpr u => Scalar(u.Op) ? targetWidth == 1 : u.Op is not ("castsigned" or "castunsigned") && u.Width <= targetWidth,
        RtlBinaryExpr b => Scalar(b.Op) ? targetWidth == 1 : b.Op == "concat" ? b.Width == targetWidth : b.Width <= targetWidth,
        RtlCondExpr c => c.Width <= targetWidth
    };

    private string EmitExpr(RtlExpr e, int context = 0, bool? forceSigned = null, HashSet<int>? blockingScope = null)
    {
        bool signed = forceSigned ?? e.Signed;
        string Extend(string value) => signed && context > e.Width ? $"signed_resize({value},{context})" : value;
        int width = Math.Max(context, e.Width);
        return e switch
        {
            RtlRefExpr r => Extend(blockingScope is not null && blockingScope.Contains(r.SignalId) ? $"next[{r.SignalId}]" : $"s[{r.SignalId}]"),
            RtlConstExpr c => Extend(ConstantLiteral(c.Bits)),
            RtlMemIndexExpr m => Extend($"read(mem[{m.MemoryId}],{MemoryIndex(m.Index, m.MemoryId, blockingScope)},{m.Width})"),
            RtlBitSelectExpr bs => $"select_bit({EmitExpr(bs.Value, blockingScope: blockingScope)},{EmitExpr(bs.Index, blockingScope: blockingScope)})",
            RtlPartSelectExpr ps => $"select_part({EmitExpr(ps.Value, blockingScope: blockingScope)},{ps.Left},{ps.Right})",
            RtlIndexedSelectExpr ix => $"select_indexed({EmitExpr(ix.Value, blockingScope: blockingScope)},{EmitExpr(ix.Base, blockingScope: blockingScope)},{ix.Width},{(ix.Descending ? "true" : "false")})",
            RtlUnaryExpr u => u.Op switch
            {
                "castsigned" or "castunsigned" => Extend(EmitExpr(u.Arg, blockingScope: blockingScope)),
                "bitnot" => $"bit_not({EmitExpr(u.Arg, width, signed, blockingScope)},{width})",
                "neg" => $"arithmetic(W{{0,0,0,{width}}},{EmitExpr(u.Arg, width, signed, blockingScope)},{width},true)",
                "pos" => $"resize({EmitExpr(u.Arg, width, signed, blockingScope)},{width})",
                "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor" => $"reduce({EmitExpr(u.Arg, blockingScope: blockingScope)},{(u.Op == "redand" ? 0 : u.Op == "rednand" ? 1 : u.Op == "redor" ? 2 : u.Op == "rednor" ? 3 : u.Op == "redxor" ? 4 : 5)})",
                "not" => $"logical_not({EmitExpr(u.Arg, blockingScope: blockingScope)})",
                _ => throw new IrValidationException("Unsupported expression.")
            },
            RtlBinaryExpr b => b.Op switch
            {
                "add" or "sub" => $"arithmetic({EmitExpr(b.Left, width, signed, blockingScope)},{EmitExpr(b.Right, width, signed, blockingScope)},{width},{(b.Op == "sub" ? "true" : "false")})",
                "mul" or "div" or "mod" => $"{(signed ? "signed_product" : "product")}({EmitExpr(b.Left, width, signed, blockingScope)},{EmitExpr(b.Right, width, signed, blockingScope)},{width},{(b.Op == "mul" ? 0 : b.Op == "div" ? 1 : 2)})",
                "ashr" when signed => $"signed_shift({EmitExpr(b.Left, width, true, blockingScope)},{EmitExpr(b.Right, blockingScope: blockingScope)},{width})",
                "shl" or "shr" or "ashr" => $"shift({EmitExpr(b.Left, width, signed, blockingScope)},{EmitExpr(b.Right, blockingScope: blockingScope)},{width},{(b.Op == "shl" ? "false" : "true")})",
                "bitxnor" => $"bit_not(bit_binary({EmitExpr(b.Left, width, signed, blockingScope)},{EmitExpr(b.Right, width, signed, blockingScope)},{width},2),{width})",
                "bitand" or "bitor" or "bitxor" => $"bit_binary({EmitExpr(b.Left, width, signed, blockingScope)},{EmitExpr(b.Right, width, signed, blockingScope)},{width},{(b.Op == "bitand" ? 0 : b.Op == "bitor" ? 1 : 2)})",
                "and" => $"logical_and({EmitExpr(b.Left, blockingScope: blockingScope)},{EmitExpr(b.Right, blockingScope: blockingScope)})",
                "or" => $"logical_or({EmitExpr(b.Left, blockingScope: blockingScope)},{EmitExpr(b.Right, blockingScope: blockingScope)})",
                "eq" or "neq" => (b.Op == "neq" ? "logical_not(" : "") + $"equal({EmitExpr(b.Left, Math.Max(b.Left.Width, b.Right.Width), b.Left.Signed && b.Right.Signed, blockingScope)},{EmitExpr(b.Right, Math.Max(b.Left.Width, b.Right.Width), b.Left.Signed && b.Right.Signed, blockingScope)})" + (b.Op == "neq" ? ")" : ""),
                "caseeq" or "caseneq" => $"W{{uint64_t({(b.Op == "caseneq" ? "!" : "")}exact({EmitExpr(b.Left, Math.Max(b.Left.Width, b.Right.Width), b.Left.Signed && b.Right.Signed, blockingScope)},{EmitExpr(b.Right, Math.Max(b.Left.Width, b.Right.Width), b.Left.Signed && b.Right.Signed, blockingScope)})),0,0,1}}",
                "lt" or "le" or "gt" or "ge" => $"{(b.Left.Signed && b.Right.Signed ? "signed_compare" : "compare")}({EmitExpr(b.Left, Math.Max(b.Left.Width, b.Right.Width), b.Left.Signed && b.Right.Signed, blockingScope)},{EmitExpr(b.Right, Math.Max(b.Left.Width, b.Right.Width), b.Left.Signed && b.Right.Signed, blockingScope)},{(b.Op == "lt" ? 0 : b.Op == "le" ? 1 : b.Op == "gt" ? 2 : 3)})",
                "concat" => $"concat({EmitExpr(b.Left, blockingScope: blockingScope)},{EmitExpr(b.Right, blockingScope: blockingScope)})",
                _ => throw new IrValidationException("Unsupported expression.")
            },
            RtlCondExpr c => $"conditional({EmitExpr(c.Condition, blockingScope: blockingScope)},{EmitExpr(c.WhenTrue, width, signed, blockingScope)},{EmitExpr(c.WhenFalse, width, signed, blockingScope)},{width})"
        };
    }

    private string MemoryIndex(RtlExpr index, int id, HashSet<int>? blockingScope = null) => Memories[id].LowerBound == 0 ? EmitExpr(index, blockingScope: blockingScope) : $"memory_index({EmitExpr(index, blockingScope: blockingScope)},{Memories[id].LowerBound})";

    private void WordWrite(StringBuilder code, RtlExpr target, string value, HashSet<int>? blockingScope = null, bool exactWidth = false)
    {
        if (target is RtlRefExpr r) { code.AppendLine($"next[{r.SignalId}]={StoreWord(value, Signals[r.SignalId], exactWidth)};"); return; }
        int selectedId = TargetSignalId(target), width = target.Width;
        string basis = target switch
        {
            RtlBitSelectExpr bs => EmitExpr(bs.Index, blockingScope: blockingScope),
            RtlIndexedSelectExpr ix => EmitExpr(ix.Base, blockingScope: blockingScope),
            RtlPartSelectExpr ps => $"W{{{ps.Right},0,0,64}}",
            _ => throw new IrValidationException("Unsupported selected write target.")
        };
        bool descending = target is RtlIndexedSelectExpr { Descending: true };
        string deposited = $"deposit_indexed(next[{selectedId}],{basis},{width},{(descending ? "true" : "false")},{value})";
        code.AppendLine($"next[{selectedId}]={(Signals[selectedId].TwoState ? "two_state(" + deposited + ")" : deposited)};");
    }

    private static string StoreWord(string value, RtlSignal signal, bool exactWidth = false)
    {
        string sized = exactWidth ? value : $"resize({value},{signal.Width})";
        return signal.TwoState ? $"two_state({sized})" : sized;
    }

    private void EmitStmt(StringBuilder code, RtlStmt stmt, ref int unique, HashSet<int>? blockingScope = null)
    {
        switch (stmt)
        {
            case RtlBlockingStmt b:
                code.AppendLine($"next[{b.TargetId}]={StoreWord(EmitExpr(b.Value, Signals[b.TargetId].Width, blockingScope: blockingScope), Signals[b.TargetId], IsExactWidth(b.Value, Signals[b.TargetId].Width))};");
                break;
            case RtlRepeatStmt r:
                string loop = "repeat" + unique++;
                code.AppendLine($"for(int {loop}=0;{loop}<{r.Count};++{loop}){{");
                EmitStmt(code, r.Body, ref unique, blockingScope);
                code.AppendLine("}");
                break;
            case RtlProcessStmt pr:
                code.AppendLine("if(" + string.Join("||", pr.Events.Select(ev => $"edge_event(previous_events[{ev.SignalId}],s[{ev.SignalId}],{(ev.Posedge ? "true" : "false")})")) + "){");
                ProcessStatement(code, pr.Body, ref unique);
                code.AppendLine("}");
                break;
            case RtlParallelStmt par:
                foreach (var item in par.Items) ProcessStatement(code, item, ref unique);
                break;
            case RtlBlockStmt blk:
                foreach (var item in blk.Items) EmitStmt(code, item, ref unique, blockingScope);
                break;
            case RtlNbaSignalStmt nba:
                if (nba.Target is RtlBinaryExpr { Op: "concat" })
                {
                    int total = nba.Target.Width, remaining = total; string rhs = "nba" + unique++;
                    string rhsCode = EmitExpr(nba.Value, total, blockingScope: blockingScope);
                    code.AppendLine(IsExactWidth(nba.Value, total) ? $"{{auto {rhs}={rhsCode};" : $"{{auto {rhs}=resize({rhsCode},{total});");
                    foreach (var leaf in TargetLeaves(nba.Target)) { int width = leaf.Width; remaining -= width; WordWrite(code, leaf, $"select_part({rhs},{remaining + width - 1},{remaining})", blockingScope, exactWidth: true); }
                    code.AppendLine("}");
                }
                else
                {
                    int targetWidth = nba.Target.Width;
                    WordWrite(code, nba.Target, EmitExpr(nba.Value, targetWidth, blockingScope: blockingScope), blockingScope, exactWidth: nba.Target is RtlRefExpr && IsExactWidth(nba.Value, targetWidth));
                }
                break;
            case RtlNbaMemStmt m:
                code.AppendLine($"mem_log.push({m.MemoryId},mem[{m.MemoryId}].size(),{MemoryIndex(m.Index, m.MemoryId, blockingScope)},{EmitExpr(m.Value, Memories[m.MemoryId].Width, blockingScope: blockingScope)},{Memories[m.MemoryId].Width});");
                break;
            case RtlIfStmt f:
                code.AppendLine($"if(yes({EmitExpr(f.Condition, blockingScope: blockingScope)})){{");
                EmitStmt(code, f.Then, ref unique, blockingScope);
                code.AppendLine("}else{");
                EmitStmt(code, f.Else, ref unique, blockingScope);
                code.AppendLine("}");
                break;
            case RtlCaseStmt c:
                var label = "case" + unique++;
                var nonDefaults = c.Items.Where(i => i.Match is not null).ToArray();
                int caseWidth = Math.Max(c.Value.Width, nonDefaults.Select(i => i.Match!.Width).DefaultIfEmpty(1).Max());
                bool caseSigned = c.Value.Signed && nonDefaults.All(i => i.Match!.Signed);
                code.AppendLine($"{{auto {label}={EmitExpr(c.Value, caseWidth, caseSigned, blockingScope)};");
                var defaultIdx = Array.FindIndex(c.Items, i => i.Match is null);
                bool canSwitch = caseWidth <= 64 && nonDefaults.Length > 0 && nonDefaults.All(i => !i.Match!.Bits.Contains('x') && !i.Match!.Bits.Contains('z'));
                if (canSwitch)
                {
                    var seenCases = new HashSet<ulong>();
                    if (defaultIdx >= 0)
                    {
                        code.AppendLine($"bool {label}_d=({label}.x|{label}.z)!=0;if(!{label}_d){{switch(uint64_t({label}.v)){{");
                        foreach (var item in nonDefaults)
                        {
                            ulong v = ConstantUnsignedValue(item.Match!.Bits, caseWidth, caseSigned);
                            if (!seenCases.Add(v)) continue;
                            code.AppendLine($"case 0x{v:x}ULL:{{");
                            EmitStmt(code, item.Body, ref unique, blockingScope);
                            code.AppendLine("break;}");
                        }
                        code.AppendLine($"default:{label}_d=true;break;}}}}if({label}_d){{");
                        EmitStmt(code, c.Items[defaultIdx].Body, ref unique, blockingScope);
                        code.AppendLine("}");
                    }
                    else
                    {
                        code.AppendLine($"if(!({label}.x|{label}.z)){{switch(uint64_t({label}.v)){{");
                        foreach (var item in nonDefaults)
                        {
                            ulong v = ConstantUnsignedValue(item.Match!.Bits, caseWidth, caseSigned);
                            if (!seenCases.Add(v)) continue;
                            code.AppendLine($"case 0x{v:x}ULL:{{");
                            EmitStmt(code, item.Body, ref unique, blockingScope);
                            code.AppendLine("break;}");
                        }
                        code.AppendLine("}}");
                    }
                }
                else
                {
                    bool first = true;
                    foreach (var item in nonDefaults)
                    {
                        code.AppendLine($"{(first ? "if" : "else if")}(exact({label},{EmitExpr(item.Match!, caseWidth, caseSigned, blockingScope)})){{");
                        EmitStmt(code, item.Body, ref unique, blockingScope);
                        code.AppendLine("}");
                        first = false;
                    }
                    if (defaultIdx >= 0)
                    {
                        code.AppendLine(first ? "{" : "else{");
                        EmitStmt(code, c.Items[defaultIdx].Body, ref unique, blockingScope);
                        code.AppendLine("}");
                    }
                }
                code.AppendLine("}");
                break;
        }
    }

    private void ProcessStatement(StringBuilder code, RtlStmt body, ref int unique)
    {
        if (body is RtlProcessStmt or RtlParallelStmt) { EmitStmt(code, body, ref unique); return; }
        HashSet<int>? blockingScope = null;
        foreach (var st in Statements(body).OfType<RtlBlockingStmt>())
            (blockingScope ??= []).Add(st.TargetId);
        EmitStmt(code, body, ref unique, blockingScope);
    }

    private void StageSequential(StringBuilder code)
    {
        foreach (int id in sequentialSignalIds) code.Append($"next[{id}]=s[{id}];");
        if (sequentialSignalIds.Length > 0) code.AppendLine();
        if (hasMemoryWrites) code.AppendLine("mem_log.count=0;if(!mem_log.overflow.empty())mem_log.overflow.clear();");
    }

    private void CommitSequential(StringBuilder code)
    {
        foreach (int id in sequentialSignalIds) code.Append($"s[{id}]=next[{id}];");
        if (sequentialSignalIds.Length > 0) code.AppendLine();
        if (hasMemoryWrites) code.AppendLine("mem_log.commit(mem);");
    }

    private void EventEvaluation(StringBuilder code)
    {
        if (continuousAst.Length > 0) code.AppendLine("settle();");
        var processes = (bodyAst is RtlParallelStmt par ? par.Items : [bodyAst]).Cast<RtlProcessStmt>().ToArray();
        var triggers = new string[processes.Length];
        for (int i = 0; i < processes.Length; i++)
        {
            triggers[i] = "ev" + i;
            code.AppendLine($"const bool {triggers[i]}=" + string.Join("||", processes[i].Events.Select(ev => $"edge_event(previous_events[{ev.SignalId}],s[{ev.SignalId}],{(ev.Posedge ? "true" : "false")})")) + ";");
        }
        code.AppendLine("if(" + string.Join("||", triggers) + "){");
        StageSequential(code);
        int unique = 0;
        for (int i = 0; i < processes.Length; i++)
        {
            code.AppendLine($"if({triggers[i]}){{");
            ProcessStatement(code, processes[i].Body, ref unique);
            code.AppendLine("}");
        }
        CommitSequential(code);
        if (continuousAst.Length > 0) code.AppendLine("settle();");
        code.AppendLine("}");
        foreach (int id in eventSignalIds) code.AppendLine($"previous_events[{id}]=s[{id}];");
    }

    public IReadOnlyDictionary<string, string> ExternalPortNames()
    {
        var reserved = ("alignas alignof and and_eq asm atomic_cancel atomic_commit atomic_noexcept auto bitand bitor bool break case catch char char8_t char16_t char32_t class compl concept const consteval constexpr constinit const_cast continue co_await co_return co_yield decltype default delete do double dynamic_cast else enum explicit export extern false float for friend goto if inline int long mutable namespace new noexcept not not_eq nullptr operator or or_eq private protected public reflexpr register reinterpret_cast requires return short signed sizeof static static_assert static_cast struct switch synchronized template this thread_local throw true try typedef typeid typename union unsigned using virtual void volatile wchar_t while xor xor_eq eval final evaluate settle previous_clock s mem next mem_log MemWrite MemLog time timeInc name signals get put lifetime checkThread useVpi loadVpi gotFinish finish evalNeeded requestEval started state std vsrtl simd Model Port W Signal word mask mask_unchecked mask64_unchecked lit resize unknown select_bit select_part select_indexed deposit_indexed parse parse_span text append_text truth yes logical_not logical_and logical_or bit_not bit_binary exact equal compare shift reduce product conditional arithmetic concat read write Bits signed_resize signed_compare signed_shift signed_product negative previous_events edge_event two_state memory_index").Split(' ').ToHashSet(StringComparer.Ordinal);
        var used = Signals.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(Signals.Count, StringComparer.Ordinal);
        foreach (var signal in Signals.Where(s => s.Direction != "internal"))
        {
            string field = signal.Name;
            if (reserved.Contains(field) || field.StartsWith('_') || field.Contains("__", StringComparison.Ordinal)) { field = "port_" + field.TrimStart('_'); while (field.Contains("__", StringComparison.Ordinal)) field = field.Replace("__", "_", StringComparison.Ordinal); while (used.Contains(field) || reserved.Contains(field)) field = "port_" + field; }
            used.Add(field); result.Add(signal.Name, field);
        }
        return result;
    }

    public string EmitModelHeader(string top)
    {
        if (!Identifier(top) || ("V" + top).Contains("__", StringComparison.Ordinal)) throw new ArgumentException("Invalid external model top name.", nameof(top));
        var names = ExternalPortNames(); string className = "V" + top;
        // Avoid a legal HDL port hiding the generated C++ constructor's class name.
        if (names.Values.Contains(className)) throw new ArgumentException("Port conflicts with generated class name.", nameof(top));
        var code = new StringBuilder("#pragma once\n#include \"external_model.h\"\nusing namespace vsrtl;\n");
        code.AppendLine($"class {className} final : public vsrtl::Model {{");
        if (!EventDriven) code.AppendLine("bool previous_clock=false;");
        else code.AppendLine($"W previous_events[{Signals.Count}];");
        if (sequentialSignalIds.Length > 0) code.AppendLine($"W next[{Signals.Count}];");
        if (hasMemoryWrites) code.AppendLine("MemLog<16> mem_log;");
        if (continuousAst.Length > 0)
        {
            code.AppendLine("inline void settle(){");
            foreach (var (id, rhs) in continuousAst)
                code.AppendLine($"s[{id}]={StoreWord(EmitExpr(rhs, Signals[id].Width), Signals[id], IsExactWidth(rhs, Signals[id].Width))};");
            code.AppendLine("}");
        }
        code.AppendLine("void evaluate() override {");
        if (EventDriven) EventEvaluation(code);
        else
        {
            code.AppendLine($"auto clock=s[{clockId}];if(clock.x|clock.z)throw std::runtime_error(\"external clock must be known0/1\");");
            if (continuousAst.Length > 0) code.AppendLine("settle();");
            code.AppendLine("if(!previous_clock&&clock.v){");
            StageSequential(code);
            int unique = 0; ProcessStatement(code, bodyAst, ref unique);
            CommitSequential(code);
            if (continuousAst.Length > 0) code.AppendLine("settle();");
            code.AppendLine("}\nprevious_clock=clock.v!=0;");
        }
        code.AppendLine("}\npublic:");
        foreach (var signal in Signals.Where(s => s.Direction != "internal")) code.AppendLine($"vsrtl::Port {names[signal.Name]};");
        code.Append($"{className}():vsrtl::Model(\"{top}\",{{");
        code.AppendJoin(',', Signals.Select(s => $"{{\"{s.SourcePath ?? s.Name}\",{s.Width},{(s.Direction == "input" ? "vpiInput" : s.Direction == "output" ? "vpiOutput" : "vpiNoDirection")},{(s.Signed ? "true" : "false")},{(s.TwoState ? "true" : "false")}}}"));
        code.Append($"}},{clockId})");
        foreach (var signal in Signals.Where(s => s.Direction != "internal")) code.Append($",{names[signal.Name]}(*this,{SignalId(signal.Name)})");
        code.AppendLine("{");
        if (Memories.Count > 0)
        {
            code.Append("mem={"); code.AppendJoin(',', Memories.Select(m => $"std::vector<W>({m.Depth},unknown({m.Width}))")); code.AppendLine("};");
        }
        if (EventDriven)
        {
            code.AppendLine($"for(int i=0;i<{Signals.Count};++i)previous_events[i]=unknown(1);");
            code.AppendLine($"previous_events[{clockId}]=word(0,0,0,1);");
            foreach (var signal in Signals.Where(s => s.TwoState && s.Width == 1)) code.AppendLine($"previous_events[{SignalId(signal.Name)}]=word(0,0,0,1);");
        }
        return code.AppendLine("}\n};").ToString();
    }

    public string EmitCpp()
    {
        var code = new StringBuilder("#include \"runtime.h\"\n#include <iostream>\n#include <fstream>\nusing namespace vsrtl;\nint main(int argc,char**argv){try{std::ios::sync_with_stdio(false);std::cin.tie(nullptr);if(argc!=3)throw std::runtime_error(\"stimulus and workers required\");int workers=std::stoi(argv[2]);if(workers<1||workers>256)throw std::runtime_error(\"workers out of range\");\nW s[");
        code.Append(Signals.Count).AppendLine("]={");
        code.AppendJoin(',', Signals.Select(s => s.TwoState ? $"lit(0ULL,{s.Width})" : $"unknown({s.Width})"));
        code.AppendLine("};");
        if (sequentialSignalIds.Length > 0) code.AppendLine($"W next[{Signals.Count}];");
        if (Memories.Count > 0)
        {
            code.Append("std::vector<std::vector<W>>mem{"); code.AppendJoin(',', Memories.Select(m => $"std::vector<W>({m.Depth},unknown({m.Width}))")); code.AppendLine("};");
        }
        if (hasMemoryWrites) code.AppendLine("MemLog<16> mem_log;");
        if (continuousAst.Length > 0)
        {
            code.AppendLine("auto settle=[&](){");
            foreach (var (id, rhs) in continuousAst)
                code.AppendLine($"s[{id}]={StoreWord(EmitExpr(rhs, Signals[id].Width), Signals[id], IsExactWidth(rhs, Signals[id].Width))};");
            code.AppendLine("};");
        }
        if (EventDriven)
        {
            code.AppendLine($"W previous_events[{Signals.Count}];for(int i=0;i<{Signals.Count};++i)previous_events[i]=unknown(1);previous_events[{clockId}]=lit(0ULL,1);");
            foreach (var signal in Signals.Where(s => s.TwoState && s.Width == 1)) code.AppendLine($"previous_events[{SignalId(signal.Name)}]=lit(0ULL,1);");
            code.AppendLine("auto evaluate=[&](){"); EventEvaluation(code); code.AppendLine("};");
        }
        code.AppendLine("std::ifstream input(argv[1]);if(!input)throw std::runtime_error(\"stimulus missing\");std::string line,out_buf;out_buf.reserve(256);int cycle=0;");
        code.AppendLine("auto next_tok=[](const std::string& str,size_t& pos,const char*& ptr,size_t& len)->bool{const size_t n=str.size();while(pos<n){unsigned char c=str[pos];if(c!=' '&&c!='\\t'&&c!='\\r'&&c!='\\v'&&c!='\\f')break;++pos;}if(pos>=n)return false;const size_t start=pos;while(pos<n){unsigned char c=str[pos];if(c==' '||c=='\\t'||c=='\\r'||c=='\\v'||c=='\\f')break;++pos;}ptr=str.data()+start;len=pos-start;return true;};");
        code.AppendLine("while(std::getline(input,line)){if(++cycle>100000)throw std::runtime_error(\"cycle budget\");size_t pos=0;const char* tok=nullptr;size_t tok_len=0;");
        foreach (var signal in Signals.Where(s => s.Direction == "input" && s.Name != Clock))
            code.AppendLine($"if(!next_tok(line,pos,tok,tok_len)||tok_len!={signal.Width})throw std::runtime_error(\"invalid input width\");s[{SignalId(signal.Name)}]={(signal.TwoState ? "two_state(parse_span(tok,tok_len))" : "parse_span(tok,tok_len)")};");
        if (EventDriven)
            code.AppendLine($"if(next_tok(line,pos,tok,tok_len))throw std::runtime_error(\"extra input\");s[{clockId}]=lit(0ULL,1);evaluate();s[{clockId}]=lit(1ULL,1);evaluate();");
        else
        {
            code.AppendLine($"if(next_tok(line,pos,tok,tok_len))throw std::runtime_error(\"extra input\");s[{clockId}]=lit(0ULL,1);");
            if (continuousAst.Length > 0) code.AppendLine("settle();");
            code.AppendLine($"s[{clockId}]=lit(1ULL,1);");
            StageSequential(code);
            int unique = 0; ProcessStatement(code, bodyAst, ref unique);
            CommitSequential(code);
            if (continuousAst.Length > 0) code.AppendLine("settle();");
        }
        code.AppendLine("out_buf.clear();out_buf+=\"VS_RTL \";out_buf+=std::to_string(cycle);");
        foreach (var signal in Signals.Where(s => s.Direction == "output"))
            code.AppendLine($"out_buf+=' ';append_text(out_buf,s[{SignalId(signal.Name)}]);");
        return code.AppendLine("out_buf+='\\n';std::cout.write(out_buf.data(),static_cast<std::streamsize>(out_buf.size()));}return 0;}catch(const std::exception&e){std::cerr<<e.what()<<'\\n';return 1;}}\n").ToString();
    }
}
