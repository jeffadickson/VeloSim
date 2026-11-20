using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace VeloSim;

public sealed record RtlSignal(string Name, int Width, string Direction, string? SourcePath = null, bool Signed = false, bool TwoState = false);
public sealed record RtlMemory(string Name, int Width, int Depth, bool Signed = false, int LowerBound = 0);
public sealed class RtlDesign
{
    public IReadOnlyList<RtlSignal> Signals { get; }
    public IReadOnlyList<RtlMemory> Memories { get; }
    public IReadOnlyList<JsonElement> Continuous { get; }
    public JsonElement Body { get; }
    public string Clock { get; }
    public string Json { get; }
    public bool EventDriven => Kind(Body) == "process" || Kind(Body) == "parallel" && Body.GetProperty("items").EnumerateArray().Any(p => Kind(p) == "process");
    private IEnumerable<string> EventSignals => (Kind(Body) == "parallel" ? Body.GetProperty("items").EnumerateArray().ToArray() : [Body]).Where(p => Kind(p) == "process").SelectMany(p => p.GetProperty("events").EnumerateArray()).Select(e => e.GetProperty("signal").GetString()!).Distinct();
    private readonly Dictionary<string, int> signalIds;
    private readonly Dictionary<string, int> memoryIds;
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
    public static bool Identifier(string name) => Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant);
    private RtlDesign(string json)
    {
        if (json.Length > 4 * 1024 * 1024) throw new IrValidationException("RTL IR too large.");
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        JsonShape.Fields(root, ["format", "signals", "memories", "continuous", "clock", "body"]);
        if (root.GetProperty("format").GetString() != "velosim.rtl.v1") throw new IrValidationException("Unsupported RTL IR.");
        Signals = root.GetProperty("signals").EnumerateArray().Select(e => { JsonShape.Fields(e, ["name", "width", "direction"], ["path", "signed", "two_state"]); return new RtlSignal(Name(e), e.GetProperty("width").GetInt32(), e.GetProperty("direction").GetString()!, e.TryGetProperty("path", out var path) ? path.GetString() : null, e.TryGetProperty("signed",out var sign)&&sign.GetBoolean(),e.TryGetProperty("two_state",out var two)&&two.GetBoolean()); }).ToArray();
        Memories = root.GetProperty("memories").EnumerateArray().Select(e => { JsonShape.Fields(e, ["name", "width", "depth"], ["signed", "lower"]); return new RtlMemory(Name(e), e.GetProperty("width").GetInt32(), e.GetProperty("depth").GetInt32(),e.TryGetProperty("signed",out var sign)&&sign.GetBoolean(),e.TryGetProperty("lower",out var lower)?lower.GetInt32():0); }).ToArray();
        if (Signals.Count is < 1 or > 256 || Memories.Count > 16 || Signals.Any(s => !Identifier(s.Name) || s.Width is < 1 or > 128 || s.Direction is not ("input" or "output" or "internal")) || Memories.Any(m => !Identifier(m.Name) || m.Width is < 1 or > 128 || m.Depth is < 1 or > 256 || m.LowerBound<0 || (long)m.LowerBound+m.Depth-1>int.MaxValue) || Signals.Select(s => s.Name).Concat(Memories.Select(m => m.Name)).Distinct().Count() != Signals.Count + Memories.Count) throw new IrValidationException("Invalid RTL declarations.");
        signalIds = Signals.Select((s, i) => (s.Name, i)).ToDictionary(s => s.Name, s => s.i, StringComparer.Ordinal); memoryIds = Memories.Select((m, i) => (m.Name, i)).ToDictionary(m => m.Name, m => m.i, StringComparer.Ordinal);
        if (Signals.Any(s => s.SourcePath is not null && (s.Direction != "internal" || s.SourcePath.Length > 1024 || !Regex.IsMatch(s.SourcePath, "^[A-Za-z0-9_.$\\[\\]:]+$", RegexOptions.CultureInvariant))) || Signals.Select(s => s.SourcePath ?? s.Name).Distinct(StringComparer.Ordinal).Count() != Signals.Count) throw new IrValidationException("Invalid or duplicate source signal paths.");
        Clock = root.GetProperty("clock").GetString()!; var clock = Signals[SignalId(Clock)];
        if (clock.Width != 1 || clock.Direction != "input") throw new IrValidationException("Clock must be a scalar input.");
        Body = root.GetProperty("body").Clone(); var sequential = new HashSet<string>(); ValidateStatement(Body, sequential, 0);
        sequentialSignalIds = sequential.Where(signalIds.ContainsKey).Select(SignalId).Order().ToArray();
        hasMemoryWrites = sequential.Any(memoryIds.ContainsKey);
        _ = StatementCost(Body);
        if (EventDriven && Kind(Body) == "parallel" && Body.GetProperty("items").EnumerateArray().Any(p => Kind(p) != "process")) throw new IrValidationException("Explicit and implicit event processes cannot be mixed.");
        var continuous = root.GetProperty("continuous").EnumerateArray().Select(e => e.Clone()).ToList(); if (continuous.Count > 256) throw new IrValidationException("Too many continuous assignments.");
        var pending = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var assignment in continuous)
        {
            JsonShape.Fields(assignment, ["target", "value"]); var target = assignment.GetProperty("target"); ValidateExpression(target, 0); ValidateExpression(assignment.GetProperty("value"), 0);
            if (References(assignment.GetProperty("value")).Intersect(EventSignals.Append(Clock)).Any()) throw new IrValidationException("Clock-dependent continuous logic is outside the race-free input-event subset.");
            if (Kind(target) != "ref" || Signals[SignalId(Name(target))].Direction == "input" || sequential.Contains(Name(target)) || !pending.TryAdd(Name(target), assignment)) throw new IrValidationException("Invalid or conflicting continuous writer.");
        }
        // Emit a combinational DAG in dependency order. Feedback and oscillation are rejected.
        var sorted = new List<JsonElement>();
        while (pending.Count > 0)
        {
            var ready = pending.Where(p => !References(p.Value.GetProperty("value")).Any(pending.ContainsKey)).Select(p => p.Key).ToArray();
            if (ready.Length == 0) throw new IrValidationException("Combinational feedback is unsupported.");
            foreach (var key in ready) { sorted.Add(pending[key]); pending.Remove(key); }
        }
        var combWriters = sorted.Select(a => Name(a.GetProperty("target"))).ToHashSet(StringComparer.Ordinal);
        if (Signals.Any(s => s.Direction != "input" && !sequential.Contains(s.Name) && !combWriters.Contains(s.Name))) throw new IrValidationException("Undriven non-input signals are unsupported; net/variable initialization must not be guessed.");
        Continuous = sorted.AsReadOnly(); ValidateBlockingIsolation(); Json = json;
    }
    public static RtlDesign Parse(string json)
    {
        try { return new(json); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException or ArgumentException && e is not IrValidationException) { throw new IrValidationException("Malformed RTL IR: " + e.Message); }
    }
    private void Budget(int depth) { if (depth > 64 || ++nodes > 20000) throw new IrValidationException("RTL node/depth budget exceeded."); }
    private void ValidateIndex(JsonElement e,int depth){ValidateExpression(e,depth);if(ExpressionSigned(e)&&!(Kind(e)=="const"&&e.GetProperty("bits").GetString()![0]=='0'))throw new IrValidationException("Signed indices require an explicit unsigned cast.");}
    private int ValidateExpression(JsonElement e, int depth)
    {
        Budget(depth); string kind = Kind(e);
        if (kind == "ref") { JsonShape.Fields(e, ["kind", "name"]); return Signals[SignalId(Name(e))].Width; }
        if (kind == "const") { JsonShape.Fields(e, ["kind", "bits"], ["signed"]); if(e.TryGetProperty("signed",out var sign))_=sign.GetBoolean(); var bits=e.GetProperty("bits").GetString();if(string.IsNullOrEmpty(bits)||bits.Length>128||bits.Any(c=>c is not ('0' or '1' or 'x' or 'z')))throw new IrValidationException("Invalid RTL constant.");return bits.Length; }
        if (kind == "index") { JsonShape.Fields(e, ["kind", "name", "index"]); ValidateIndex(e.GetProperty("index"), depth + 1); return Memories[MemoryId(Name(e))].Width; }
        if (kind == "bitselect") { JsonShape.Fields(e, ["kind", "value", "index"]); ValidateExpression(e.GetProperty("value"), depth + 1); ValidateIndex(e.GetProperty("index"), depth + 1); return 1; }
        if (kind == "partselect") { JsonShape.Fields(e, ["kind", "value", "left", "right"]); ValidateExpression(e.GetProperty("value"), depth + 1); int left = e.GetProperty("left").GetInt32(), right = e.GetProperty("right").GetInt32(); if (right < 0 || left < right || (long)left - right >= 128) throw new IrValidationException("Only nonnegative descending constant part selects of 1..128 bits supported."); return left - right + 1; }
        if (kind == "indexedselect") { JsonShape.Fields(e, ["kind", "value", "base", "width", "descending"]); ValidateExpression(e.GetProperty("value"), depth + 1); ValidateIndex(e.GetProperty("base"), depth + 1); int width = e.GetProperty("width").GetInt32(); _ = e.GetProperty("descending").GetBoolean(); if (width is < 1 or > 128) throw new IrValidationException("Indexed select width must be 1..128."); return width; }
        JsonShape.Fields(e, ["kind", "args"]); var args = e.GetProperty("args").EnumerateArray().ToArray();
        if (Arity(kind) == 0 || args.Length != Arity(kind)) throw new IrValidationException("Unsupported RTL expression.");
        var widths = args.Select(a => ValidateExpression(a, depth + 1)).ToArray(); var result = Scalar(kind) ? 1 : kind is "shl" or "shr" or "ashr" ? widths[0] : kind == "cond" ? Math.Max(widths[1], widths[2]) : kind == "concat" ? widths.Sum() : widths.Max(); if (result > 128) throw new IrValidationException("Expression exceeds 128 bits."); return result;
    }
    private IEnumerable<string> References(JsonElement e)
    {
        if (Kind(e) == "ref") yield return Name(e);
        else if (Kind(e) == "index") { foreach (var n in References(e.GetProperty("index"))) yield return n; }
        else if (Kind(e) is "bitselect" or "partselect" or "indexedselect") { foreach (var n in References(e.GetProperty("value"))) yield return n; if (e.TryGetProperty("index", out var index)) foreach (var n in References(index)) yield return n; if (e.TryGetProperty("base", out var basis)) foreach (var n in References(basis)) yield return n; }
        else if (e.TryGetProperty("args", out var args)) foreach (var a in args.EnumerateArray()) foreach (var n in References(a)) yield return n;
    }
    private void ValidateStatement(JsonElement e, HashSet<string> writers, int depth)
    {
        Budget(depth);
        switch (Kind(e))
        {
            case "repeat":
                JsonShape.Fields(e, ["kind", "count", "body"]); int count=e.GetProperty("count").GetInt32();
                if(count is <0 or >256)throw new IrValidationException("Repeat count must be 0..256.");
                ValidateStatement(e.GetProperty("body"),writers,depth+1);break;
            case "process":
                if (depth > 1 || depth == 1 && Kind(Body) != "parallel") throw new IrValidationException("Event processes must be at the root or in a root parallel group.");
                JsonShape.Fields(e, ["kind", "events", "body"]); var events = e.GetProperty("events").EnumerateArray().ToArray();
                if (events.Length is < 1 or > 16) throw new IrValidationException("Process events must be 1..16.");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var trigger in events) { JsonShape.Fields(trigger, ["signal", "edge"]); string signal = trigger.GetProperty("signal").GetString()!, edge = trigger.GetProperty("edge").GetString()!; var declaration = Signals[SignalId(signal)]; if (declaration.Width != 1 || declaration.Direction != "input" || edge is not ("posedge" or "negedge") || !seen.Add(signal + ":" + edge)) throw new IrValidationException("Events require unique scalar input edges."); }
                ValidateStatement(e.GetProperty("body"), writers, depth + 1); break;
            case "parallel":
                if (depth != 0) throw new IrValidationException("Parallel processes must be at the root.");
                JsonShape.Fields(e, ["kind", "items"]); var processes = e.GetProperty("items").EnumerateArray().ToArray();
                if (processes.Length is < 1 or > 256) throw new IrValidationException("Parallel process count must be 1..256.");
                foreach (var process in processes)
                {
                    var owned = new HashSet<string>(StringComparer.Ordinal); ValidateStatement(process, owned, depth + 1);
                    if (owned.Any(writers.Contains)) throw new IrValidationException("Multiple processes cannot write the same word or memory.");
                    writers.UnionWith(owned);
                }
                break;
            case "block": JsonShape.Fields(e, ["kind", "items"]); foreach (var item in e.GetProperty("items").EnumerateArray()) ValidateStatement(item, writers, depth + 1); break;
            case "blocking": case "nba":
                JsonShape.Fields(e, ["kind", "target", "value"]); var target = e.GetProperty("target"); ValidateExpression(target, depth + 1); ValidateExpression(e.GetProperty("value"), depth + 1);
                if(Kind(e)=="blocking"&&Kind(target)!="ref")throw new IrValidationException("Blocking assignments require whole signal words.");
                if (Kind(target) == "index") { writers.Add(Name(target)); break; }
                var parents = new HashSet<string>(StringComparer.Ordinal);
                foreach (var leaf in TargetLeaves(target))
                {
                    var word = Kind(leaf) is "bitselect" or "partselect" or "indexedselect" ? leaf.GetProperty("value") : leaf;
                    if (Kind(leaf) is not ("ref" or "bitselect" or "partselect" or "indexedselect") || Kind(word) != "ref" || Name(word) == Clock || Signals[SignalId(Name(word))].Direction == "input") throw new IrValidationException("Selected sequential targets require a writable declared word.");
                    if (!parents.Add(Name(word))) throw new IrValidationException("Concatenation targets require distinct destination words.");
                    writers.Add(Name(word));
                }
                break;
            case "if": JsonShape.Fields(e, ["kind", "condition", "then", "else"]); ValidateExpression(e.GetProperty("condition"), depth + 1); ValidateStatement(e.GetProperty("then"), writers, depth + 1); ValidateStatement(e.GetProperty("else"), writers, depth + 1); break;
            case "case":
                JsonShape.Fields(e, ["kind", "value", "items"]); ValidateExpression(e.GetProperty("value"), depth + 1); int defaults = 0;
                foreach (var item in e.GetProperty("items").EnumerateArray()) { JsonShape.Fields(item, ["match", "body"]); var match = item.GetProperty("match"); if (match.ValueKind == JsonValueKind.Null) defaults++; else { if (Kind(match) != "const") throw new IrValidationException("Only constant case labels supported."); ValidateExpression(match, depth + 1); } ValidateStatement(item.GetProperty("body"), writers, depth + 1); }
                if (defaults > 1) throw new IrValidationException("Duplicate default case."); break;
            default: throw new IrValidationException("Unsupported RTL statement.");
        }
    }
    private static IEnumerable<JsonElement> Statements(JsonElement e)
    {
        yield return e;
        IEnumerable<JsonElement> children=Kind(e) switch {
            "block" or "parallel"=>e.GetProperty("items").EnumerateArray().ToArray(),
            "process" or "repeat"=>[e.GetProperty("body")],"if"=>[e.GetProperty("then"),e.GetProperty("else")],
            "case"=>e.GetProperty("items").EnumerateArray().Select(i=>i.GetProperty("body")),_=>[]};
        foreach(var child in children)foreach(var statement in Statements(child))yield return statement;
    }
    private void ValidateBlockingIsolation()
    {
        var processes=Kind(Body)=="parallel"?Body.GetProperty("items").EnumerateArray().ToArray():[Body];
        var reads=processes.Select(p=>Statements(p).SelectMany(e=>new[]{"value","target","condition"}.Where(name=>e.TryGetProperty(name,out _)).SelectMany(name=>References(e.GetProperty(name)))).ToHashSet(StringComparer.Ordinal)).ToArray();
        for(int i=0;i<processes.Length;++i){
            var statements=Statements(processes[i]).ToArray();var blocking=statements.Where(e=>Kind(e)=="blocking").Select(e=>Name(e.GetProperty("target"))).ToHashSet(StringComparer.Ordinal);
            var nonblocking=statements.Where(e=>Kind(e)=="nba").SelectMany(e=>TargetLeaves(e.GetProperty("target"))).Select(t=>Kind(t) is "bitselect" or "partselect" or "indexedselect"?Name(t.GetProperty("value")):Name(t)).ToHashSet(StringComparer.Ordinal);
            if(blocking.Overlaps(nonblocking))throw new IrValidationException("A word cannot mix blocking and nonblocking assignments.");
            if(Enumerable.Range(0,processes.Length).Any(j=>j!=i&&blocking.Overlaps(reads[j]))||Continuous.Any(a=>References(a.GetProperty("value")).Any(blocking.Contains)))throw new IrValidationException("Blocking destinations must be isolated from other processes and continuous logic.");
        }
    }
    private static long StatementCost(JsonElement e)
    {
        long cost=Kind(e) switch {
            "parallel" or "block" => 1+e.GetProperty("items").EnumerateArray().Sum(StatementCost),
            "process" => 1+StatementCost(e.GetProperty("body")),
            "repeat" => 1+Math.Max(1,e.GetProperty("count").GetInt32())*StatementCost(e.GetProperty("body")),
            "if" => 1+StatementCost(e.GetProperty("then"))+StatementCost(e.GetProperty("else")),
            "case" => 1+e.GetProperty("items").EnumerateArray().Sum(item=>StatementCost(item.GetProperty("body"))), _=>1};
        return cost<=20000?cost:throw new IrValidationException("Expanded statement execution budget exceeded.");
    }
    private static IEnumerable<JsonElement> TargetLeaves(JsonElement target)
    {
        if (Kind(target) == "concat") foreach (var child in target.GetProperty("args").EnumerateArray()) foreach (var leaf in TargetLeaves(child)) yield return leaf;
        else yield return target;
    }
    private int ExpressionWidth(JsonElement e) => Kind(e) switch
    {
        "ref" => Signals[SignalId(Name(e))].Width, "index" => Memories[MemoryId(Name(e))].Width, "const" => e.GetProperty("bits").GetString()!.Length,
        "bitselect" => 1, "partselect" => e.GetProperty("left").GetInt32() - e.GetProperty("right").GetInt32() + 1, "indexedselect" => e.GetProperty("width").GetInt32(),
        var kind when Scalar(kind) => 1, "shl" or "shr" or "ashr" => ExpressionWidth(e.GetProperty("args")[0]), "cond" => e.GetProperty("args").EnumerateArray().Skip(1).Max(ExpressionWidth), "concat" => e.GetProperty("args").EnumerateArray().Sum(ExpressionWidth), _ => e.GetProperty("args").EnumerateArray().Max(ExpressionWidth)
    };
    private bool ExpressionSigned(JsonElement e) => Kind(e) switch
    {
        "ref"=>Signals[SignalId(Name(e))].Signed,"index"=>Memories[MemoryId(Name(e))].Signed,
        "const"=>e.TryGetProperty("signed",out var sign)&&sign.GetBoolean(),"castsigned"=>true,
        "castunsigned" or "bitselect" or "partselect" or "indexedselect" or "concat"=>false,
        var k when Scalar(k)=>false,"shl" or "shr" or "ashr"=>ExpressionSigned(e.GetProperty("args")[0]),
        "cond"=>e.GetProperty("args").EnumerateArray().Skip(1).All(ExpressionSigned),
        _=>e.GetProperty("args").EnumerateArray().All(ExpressionSigned)
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
    private bool IsExactWidth(JsonElement e, int targetWidth) => Kind(e) switch
    {
        "ref" => Signals[SignalId(Name(e))].Width == targetWidth,
        "const" => e.GetProperty("bits").GetString()!.Length == targetWidth,
        "index" => Memories[MemoryId(Name(e))].Width == targetWidth,
        "bitselect" => targetWidth == 1,
        "partselect" => e.GetProperty("left").GetInt32() - e.GetProperty("right").GetInt32() + 1 == targetWidth,
        "indexedselect" => e.GetProperty("width").GetInt32() == targetWidth,
        var kind when Scalar(kind) => targetWidth == 1,
        "add" or "sub" or "bitnot" or "neg" or "pos" or "mul" or "div" or "mod" or "cond" or "shl" or "shr" or "ashr" or "bitand" or "bitor" or "bitxor" or "bitxnor" => ExpressionWidth(e) <= targetWidth,
        "concat" => ExpressionWidth(e) == targetWidth,
        _ => false
    };
    private string Expression(JsonElement e, int context = 0, bool? forceSigned = null, HashSet<int>? blockingScope = null)
    {
        string kind = Kind(e); bool signed = forceSigned ?? ExpressionSigned(e);
        string Extend(string value) => signed && context > ExpressionWidth(e) ? $"signed_resize({value},{context})" : value;
        if (kind == "ref") { int id = SignalId(Name(e)); return Extend(blockingScope is not null && blockingScope.Contains(id) ? $"next[{id}]" : $"s[{id}]"); }
        if (kind == "const") return Extend(ConstantLiteral(e.GetProperty("bits").GetString()!));
        if (kind == "index") { int id = MemoryId(Name(e)); return Extend($"read(mem[{id}],{MemoryIndex(e.GetProperty("index"), id, blockingScope)},{Memories[id].Width})"); }
        if (kind == "bitselect") return $"select_bit({Expression(e.GetProperty("value"), blockingScope: blockingScope)},{Expression(e.GetProperty("index"), blockingScope: blockingScope)})";
        if (kind == "partselect") return $"select_part({Expression(e.GetProperty("value"), blockingScope: blockingScope)},{e.GetProperty("left").GetInt32()},{e.GetProperty("right").GetInt32()})";
        if (kind == "indexedselect") return $"select_indexed({Expression(e.GetProperty("value"), blockingScope: blockingScope)},{Expression(e.GetProperty("base"), blockingScope: blockingScope)},{e.GetProperty("width").GetInt32()},{(e.GetProperty("descending").GetBoolean() ? "true" : "false")})";
        var args = e.GetProperty("args").EnumerateArray().ToArray(); int width = Math.Max(context, ExpressionWidth(e));
        return kind switch
        {
            "castsigned" or "castunsigned" => Extend(Expression(args[0], blockingScope: blockingScope)),
            "add" or "sub" => $"arithmetic({Expression(args[0], width, signed, blockingScope)},{Expression(args[1], width, signed, blockingScope)},{width},{(kind == "sub" ? "true" : "false")})",
            "bitnot" => $"bit_not({Expression(args[0], width, signed, blockingScope)},{width})",
            "neg" => $"arithmetic(W{{0,0,0,{width}}},{Expression(args[0], width, signed, blockingScope)},{width},true)",
            "pos" => $"resize({Expression(args[0], width, signed, blockingScope)},{width})",
            "mul" or "div" or "mod" => $"{(signed ? "signed_product" : "product")}({Expression(args[0], width, signed, blockingScope)},{Expression(args[1], width, signed, blockingScope)},{width},{(kind == "mul" ? 0 : kind == "div" ? 1 : 2)})",
            "cond" => $"conditional({Expression(args[0], blockingScope: blockingScope)},{Expression(args[1], width, signed, blockingScope)},{Expression(args[2], width, signed, blockingScope)},{width})",
            "ashr" when signed => $"signed_shift({Expression(args[0], width, true, blockingScope)},{Expression(args[1], blockingScope: blockingScope)},{width})",
            "shl" or "shr" or "ashr" => $"shift({Expression(args[0], width, signed, blockingScope)},{Expression(args[1], blockingScope: blockingScope)},{width},{(kind == "shl" ? "false" : "true")})",
            "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor" => $"reduce({Expression(args[0], blockingScope: blockingScope)},{(kind == "redand" ? 0 : kind == "rednand" ? 1 : kind == "redor" ? 2 : kind == "rednor" ? 3 : kind == "redxor" ? 4 : 5)})",
            "bitxnor" => $"bit_not(bit_binary({Expression(args[0], width, signed, blockingScope)},{Expression(args[1], width, signed, blockingScope)},{width},2),{width})",
            "bitand" or "bitor" or "bitxor" => $"bit_binary({Expression(args[0], width, signed, blockingScope)},{Expression(args[1], width, signed, blockingScope)},{width},{(kind == "bitand" ? 0 : kind == "bitor" ? 1 : 2)})",
            "not" => $"logical_not({Expression(args[0], blockingScope: blockingScope)})",
            "and" => $"logical_and({Expression(args[0], blockingScope: blockingScope)},{Expression(args[1], blockingScope: blockingScope)})",
            "or" => $"logical_or({Expression(args[0], blockingScope: blockingScope)},{Expression(args[1], blockingScope: blockingScope)})",
            "eq" or "neq" => (kind == "neq" ? "logical_not(" : "") + $"equal({Expression(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned), blockingScope)},{Expression(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned), blockingScope)})" + (kind == "neq" ? ")" : ""),
            "caseeq" or "caseneq" => $"W{{uint64_t({(kind == "caseneq" ? "!" : "")}exact({Expression(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned), blockingScope)},{Expression(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned), blockingScope)})),0,0,1}}",
            "lt" or "le" or "gt" or "ge" => $"{(args.All(ExpressionSigned) ? "signed_compare" : "compare")}({Expression(args[0], args.Max(ExpressionWidth), args.All(ExpressionSigned), blockingScope)},{Expression(args[1], args.Max(ExpressionWidth), args.All(ExpressionSigned), blockingScope)},{(kind == "lt" ? 0 : kind == "le" ? 1 : kind == "gt" ? 2 : 3)})",
            "concat" => $"concat({Expression(args[0], blockingScope: blockingScope)},{Expression(args[1], blockingScope: blockingScope)})",
            _ => throw new IrValidationException("Unsupported expression.")
        };
    }
    private string MemoryIndex(JsonElement index, int id, HashSet<int>? blockingScope = null) => Memories[id].LowerBound == 0 ? Expression(index, blockingScope: blockingScope) : $"memory_index({Expression(index, blockingScope: blockingScope)},{Memories[id].LowerBound})";
    private void WordWrite(StringBuilder code, JsonElement target, string value, HashSet<int>? blockingScope = null, bool exactWidth = false)
    {
        if (Kind(target) == "ref") { int id = SignalId(Name(target)); code.AppendLine($"next[{id}]={StoreWord(value, Signals[id], exactWidth)};"); return; }
        int selectedId = SignalId(Name(target.GetProperty("value"))), width = ExpressionWidth(target);
        string basis = Kind(target) == "bitselect" ? Expression(target.GetProperty("index"), blockingScope: blockingScope) : Kind(target) == "indexedselect" ? Expression(target.GetProperty("base"), blockingScope: blockingScope) : $"W{{{target.GetProperty("right").GetInt32()},0,0,64}}";
        bool descending = Kind(target) == "indexedselect" && target.GetProperty("descending").GetBoolean();
        string deposited = $"deposit_indexed(next[{selectedId}],{basis},{width},{(descending ? "true" : "false")},{value})"; code.AppendLine($"next[{selectedId}]={(Signals[selectedId].TwoState ? "two_state(" + deposited + ")" : deposited)};");
    }
    private static string StoreWord(string value, RtlSignal signal, bool exactWidth = false)
    {
        string sized = exactWidth ? value : $"resize({value},{signal.Width})";
        return signal.TwoState ? $"two_state({sized})" : sized;
    }
    private void Statement(StringBuilder code, JsonElement e, ref int unique, HashSet<int>? blockingScope = null)
    {
        switch (Kind(e))
        {
            case "blocking":
                int blockingId = SignalId(Name(e.GetProperty("target")));
                var bval = e.GetProperty("value");
                code.AppendLine($"next[{blockingId}]={StoreWord(Expression(bval, Signals[blockingId].Width, blockingScope: blockingScope), Signals[blockingId], IsExactWidth(bval, Signals[blockingId].Width))};");
                break;
            case "repeat":
                string loop = "repeat" + unique++;
                code.AppendLine($"for(int {loop}=0;{loop}<{e.GetProperty("count").GetInt32()};++{loop}){{");
                Statement(code, e.GetProperty("body"), ref unique, blockingScope);
                code.AppendLine("}");
                break;
            case "process":
                code.AppendLine("if(" + string.Join("||", e.GetProperty("events").EnumerateArray().Select(trigger => { int id = SignalId(trigger.GetProperty("signal").GetString()!); return $"edge_event(previous_events[{id}],s[{id}],{(trigger.GetProperty("edge").GetString() == "posedge" ? "true" : "false")})"; })) + "){");
                ProcessStatement(code, e.GetProperty("body"), ref unique);
                code.AppendLine("}");
                break;
            case "parallel":
                foreach (var item in e.GetProperty("items").EnumerateArray()) ProcessStatement(code, item, ref unique);
                break;
            case "block":
                foreach (var item in e.GetProperty("items").EnumerateArray()) Statement(code, item, ref unique, blockingScope);
                break;
            case "nba":
                var target = e.GetProperty("target");
                var rhsExpr = e.GetProperty("value");
                if (Kind(target) == "concat")
                {
                    int total = ExpressionWidth(target), remaining = total; string rhs = "nba" + unique++;
                    string rhsCode = Expression(rhsExpr, total, blockingScope: blockingScope);
                    code.AppendLine(IsExactWidth(rhsExpr, total) ? $"{{auto {rhs}={rhsCode};" : $"{{auto {rhs}=resize({rhsCode},{total});");
                    foreach (var leaf in TargetLeaves(target)) { int width = ExpressionWidth(leaf); remaining -= width; WordWrite(code, leaf, $"select_part({rhs},{remaining + width - 1},{remaining})", blockingScope, exactWidth: true); }
                    code.AppendLine("}");
                }
                else if (Kind(target) is "ref" or "bitselect" or "partselect" or "indexedselect")
                {
                    int targetWidth = ExpressionWidth(target);
                    WordWrite(code, target, Expression(rhsExpr, targetWidth, blockingScope: blockingScope), blockingScope, exactWidth: Kind(target) == "ref" && IsExactWidth(rhsExpr, targetWidth));
                }
                else
                {
                    int id = MemoryId(Name(target));
                    code.AppendLine($"mem_log.push({id},mem[{id}].size(),{MemoryIndex(target.GetProperty("index"), id, blockingScope)},{Expression(rhsExpr, Memories[id].Width, blockingScope: blockingScope)},{Memories[id].Width});");
                }
                break;
            case "if":
                code.AppendLine($"if(yes({Expression(e.GetProperty("condition"), blockingScope: blockingScope)})){{");
                Statement(code, e.GetProperty("then"), ref unique, blockingScope);
                code.AppendLine("}else{");
                Statement(code, e.GetProperty("else"), ref unique, blockingScope);
                code.AppendLine("}");
                break;
            case "case":
                var label = "case" + unique++;
                var items = e.GetProperty("items").EnumerateArray().ToArray();
                var nonDefaults = items.Where(i => i.GetProperty("match").ValueKind != JsonValueKind.Null).ToArray();
                int caseWidth = Math.Max(ExpressionWidth(e.GetProperty("value")), nonDefaults.Select(i => ExpressionWidth(i.GetProperty("match"))).DefaultIfEmpty(1).Max());
                bool caseSigned = ExpressionSigned(e.GetProperty("value")) && nonDefaults.All(i => ExpressionSigned(i.GetProperty("match")));
                code.AppendLine($"{{auto {label}={Expression(e.GetProperty("value"), caseWidth, caseSigned, blockingScope)};");
                var defaultItem = items.FirstOrDefault(i => i.GetProperty("match").ValueKind == JsonValueKind.Null);
                bool canSwitch = caseWidth <= 64 && nonDefaults.Length > 0 && nonDefaults.All(i =>
                {
                    string b = i.GetProperty("match").GetProperty("bits").GetString()!;
                    return !b.Contains('x') && !b.Contains('z');
                });
                if (canSwitch)
                {
                    var seenCases = new HashSet<ulong>();
                    if (defaultItem.ValueKind != JsonValueKind.Undefined)
                    {
                        code.AppendLine($"bool {label}_d=({label}.x|{label}.z)!=0;if(!{label}_d){{switch(uint64_t({label}.v)){{");
                        foreach (var item in nonDefaults)
                        {
                            ulong v = ConstantUnsignedValue(item.GetProperty("match").GetProperty("bits").GetString()!, caseWidth, caseSigned);
                            if (!seenCases.Add(v)) continue;
                            code.AppendLine($"case 0x{v:x}ULL:{{");
                            Statement(code, item.GetProperty("body"), ref unique, blockingScope);
                            code.AppendLine("break;}");
                        }
                        code.AppendLine($"default:{label}_d=true;break;}}}}if({label}_d){{");
                        Statement(code, defaultItem.GetProperty("body"), ref unique, blockingScope);
                        code.AppendLine("}");
                    }
                    else
                    {
                        code.AppendLine($"if(!({label}.x|{label}.z)){{switch(uint64_t({label}.v)){{");
                        foreach (var item in nonDefaults)
                        {
                            ulong v = ConstantUnsignedValue(item.GetProperty("match").GetProperty("bits").GetString()!, caseWidth, caseSigned);
                            if (!seenCases.Add(v)) continue;
                            code.AppendLine($"case 0x{v:x}ULL:{{");
                            Statement(code, item.GetProperty("body"), ref unique, blockingScope);
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
                        code.AppendLine($"{(first ? "if" : "else if")}(exact({label},{Expression(item.GetProperty("match"), caseWidth, caseSigned, blockingScope)})){{");
                        Statement(code, item.GetProperty("body"), ref unique, blockingScope);
                        code.AppendLine("}");
                        first = false;
                    }
                    if (defaultItem.ValueKind != JsonValueKind.Undefined)
                    {
                        code.AppendLine(first ? "{" : "else{");
                        Statement(code, defaultItem.GetProperty("body"), ref unique, blockingScope);
                        code.AppendLine("}");
                    }
                }
                code.AppendLine("}");
                break;
        }
    }
    private void ProcessStatement(StringBuilder code, JsonElement body, ref int unique)
    {
        if (Kind(body) is "process" or "parallel") { Statement(code, body, ref unique); return; }
        HashSet<int>? blockingScope = null;
        foreach (var st in Statements(body))
            if (Kind(st) == "blocking") (blockingScope ??= []).Add(SignalId(Name(st.GetProperty("target"))));
        Statement(code, body, ref unique, blockingScope);
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
        if (Continuous.Count > 0) code.AppendLine("settle();");
        var processes = Kind(Body) == "parallel" ? Body.GetProperty("items").EnumerateArray().ToArray() : [Body];
        var triggers = new string[processes.Length];
        for (int i = 0; i < processes.Length; i++)
        {
            triggers[i] = "ev" + i;
            code.AppendLine($"const bool {triggers[i]}=" + string.Join("||", processes[i].GetProperty("events").EnumerateArray().Select(trigger => { int id = SignalId(trigger.GetProperty("signal").GetString()!); return $"edge_event(previous_events[{id}],s[{id}],{(trigger.GetProperty("edge").GetString() == "posedge" ? "true" : "false")})"; })) + ";");
        }
        code.AppendLine("if(" + string.Join("||", triggers) + "){");
        StageSequential(code);
        int unique = 0;
        for (int i = 0; i < processes.Length; i++)
        {
            code.AppendLine($"if({triggers[i]}){{");
            ProcessStatement(code, processes[i].GetProperty("body"), ref unique);
            code.AppendLine("}");
        }
        CommitSequential(code);
        if (Continuous.Count > 0) code.AppendLine("settle();");
        code.AppendLine("}");
        foreach (string signal in EventSignals) { int id = SignalId(signal); code.AppendLine($"previous_events[{id}]=s[{id}];"); }
    }
    public IReadOnlyDictionary<string, string> ExternalPortNames()
    {
        var reserved = ("alignas alignof and and_eq asm atomic_cancel atomic_commit atomic_noexcept auto bitand bitor bool break case catch char char8_t char16_t char32_t class compl concept const consteval constexpr constinit const_cast continue co_await co_return co_yield decltype default delete do double dynamic_cast else enum explicit export extern false float for friend goto if inline int long mutable namespace new noexcept not not_eq nullptr operator or or_eq private protected public reflexpr register reinterpret_cast requires return short signed sizeof static static_assert static_cast struct switch synchronized template this thread_local throw true try typedef typeid typename union unsigned using virtual void volatile wchar_t while xor xor_eq eval final evaluate settle previous_clock s mem next mem_log MemWrite MemLog time timeInc name signals get put lifetime checkThread useVpi loadVpi gotFinish finish evalNeeded requestEval started state std vsrtl simd Model Port W Signal word mask mask_unchecked mask64_unchecked lit resize unknown select_bit select_part select_indexed deposit_indexed parse parse_span text append_text truth yes logical_not logical_and logical_or bit_not bit_binary exact equal compare shift reduce product conditional arithmetic concat read write Bits signed_resize signed_compare signed_shift signed_product negative previous_events edge_event two_state memory_index").Split(' ').ToHashSet(StringComparer.Ordinal);
        var used = Signals.Select(s => s.Name).ToHashSet(StringComparer.Ordinal); var result = new Dictionary<string, string>(StringComparer.Ordinal);
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
        if (Continuous.Count > 0)
        {
            code.AppendLine("inline void settle(){");
            foreach (var assignment in Continuous)
            {
                int id = SignalId(Name(assignment.GetProperty("target")));
                var rhs = assignment.GetProperty("value");
                code.AppendLine($"s[{id}]={StoreWord(Expression(rhs, Signals[id].Width), Signals[id], IsExactWidth(rhs, Signals[id].Width))};");
            }
            code.AppendLine("}");
        }
        code.AppendLine("void evaluate() override {");
        if (EventDriven) EventEvaluation(code);
        else
        {
            code.AppendLine($"auto clock=s[{SignalId(Clock)}];if(clock.x|clock.z)throw std::runtime_error(\"external clock must be known0/1\");");
            if (Continuous.Count > 0) code.AppendLine("settle();");
            code.AppendLine("if(!previous_clock&&clock.v){");
            StageSequential(code);
            int unique = 0; ProcessStatement(code, Body, ref unique);
            CommitSequential(code);
            if (Continuous.Count > 0) code.AppendLine("settle();");
            code.AppendLine("}\nprevious_clock=clock.v!=0;");
        }
        code.AppendLine("}\npublic:");
        foreach (var signal in Signals.Where(s => s.Direction != "internal")) code.AppendLine($"vsrtl::Port {names[signal.Name]};");
        code.Append($"{className}():vsrtl::Model(\"{top}\",{{");
        code.AppendJoin(',', Signals.Select(s => $"{{\"{s.SourcePath ?? s.Name}\",{s.Width},{(s.Direction == "input" ? "vpiInput" : s.Direction == "output" ? "vpiOutput" : "vpiNoDirection")},{(s.Signed ? "true" : "false")},{(s.TwoState ? "true" : "false")}}}"));
        code.Append($"}},{SignalId(Clock)})");
        foreach (var signal in Signals.Where(s => s.Direction != "internal")) code.Append($",{names[signal.Name]}(*this,{SignalId(signal.Name)})");
        code.AppendLine("{");
        if (Memories.Count > 0)
        {
            code.Append("mem={"); code.AppendJoin(',', Memories.Select(m => $"std::vector<W>({m.Depth},unknown({m.Width}))")); code.AppendLine("};");
        }
        if (EventDriven)
        {
            code.AppendLine($"for(int i=0;i<{Signals.Count};++i)previous_events[i]=unknown(1);");
            code.AppendLine($"previous_events[{SignalId(Clock)}]=word(0,0,0,1);");
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
        if (Continuous.Count > 0)
        {
            code.AppendLine("auto settle=[&](){");
            foreach (var assignment in Continuous)
            {
                int id = SignalId(Name(assignment.GetProperty("target")));
                var rhs = assignment.GetProperty("value");
                code.AppendLine($"s[{id}]={StoreWord(Expression(rhs, Signals[id].Width), Signals[id], IsExactWidth(rhs, Signals[id].Width))};");
            }
            code.AppendLine("};");
        }
        if (EventDriven)
        {
            code.AppendLine($"W previous_events[{Signals.Count}];for(int i=0;i<{Signals.Count};++i)previous_events[i]=unknown(1);previous_events[{SignalId(Clock)}]=lit(0ULL,1);");
            foreach (var signal in Signals.Where(s => s.TwoState && s.Width == 1)) code.AppendLine($"previous_events[{SignalId(signal.Name)}]=lit(0ULL,1);");
            code.AppendLine("auto evaluate=[&](){"); EventEvaluation(code); code.AppendLine("};");
        }
        code.AppendLine("std::ifstream input(argv[1]);if(!input)throw std::runtime_error(\"stimulus missing\");std::string line,out_buf;out_buf.reserve(256);int cycle=0;");
        code.AppendLine("auto next_tok=[](const std::string& str,size_t& pos,const char*& ptr,size_t& len)->bool{const size_t n=str.size();while(pos<n){unsigned char c=str[pos];if(c!=' '&&c!='\\t'&&c!='\\r'&&c!='\\v'&&c!='\\f')break;++pos;}if(pos>=n)return false;const size_t start=pos;while(pos<n){unsigned char c=str[pos];if(c==' '||c=='\\t'||c=='\\r'||c=='\\v'||c=='\\f')break;++pos;}ptr=str.data()+start;len=pos-start;return true;};");
        code.AppendLine("while(std::getline(input,line)){if(++cycle>100000)throw std::runtime_error(\"cycle budget\");size_t pos=0;const char* tok=nullptr;size_t tok_len=0;");
        foreach (var signal in Signals.Where(s => s.Direction == "input" && s.Name != Clock))
            code.AppendLine($"if(!next_tok(line,pos,tok,tok_len)||tok_len!={signal.Width})throw std::runtime_error(\"invalid input width\");s[{SignalId(signal.Name)}]={(signal.TwoState ? "two_state(parse_span(tok,tok_len))" : "parse_span(tok,tok_len)")};");
        if (EventDriven)
            code.AppendLine($"if(next_tok(line,pos,tok,tok_len))throw std::runtime_error(\"extra input\");s[{SignalId(Clock)}]=lit(0ULL,1);evaluate();s[{SignalId(Clock)}]=lit(1ULL,1);evaluate();");
        else
        {
            code.AppendLine($"if(next_tok(line,pos,tok,tok_len))throw std::runtime_error(\"extra input\");s[{SignalId(Clock)}]=lit(0ULL,1);");
            if (Continuous.Count > 0) code.AppendLine("settle();");
            code.AppendLine($"s[{SignalId(Clock)}]=lit(1ULL,1);");
            StageSequential(code);
            int unique = 0; ProcessStatement(code, Body, ref unique);
            CommitSequential(code);
            if (Continuous.Count > 0) code.AppendLine("settle();");
        }
        code.AppendLine("out_buf.clear();out_buf+=\"VS_RTL \";out_buf+=std::to_string(cycle);");
        foreach (var signal in Signals.Where(s => s.Direction == "output"))
            code.AppendLine($"out_buf+=' ';append_text(out_buf,s[{SignalId(signal.Name)}]);");
        return code.AppendLine("out_buf+='\\n';std::cout.write(out_buf.data(),static_cast<std::streamsize>(out_buf.size()));}return 0;}catch(const std::exception&e){std::cerr<<e.what()<<'\\n';return 1;}}\n").ToString();
    }
}
