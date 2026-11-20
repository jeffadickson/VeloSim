using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace VeloSim;

public sealed record RtlSignal(string Name, int Width, string Direction, string? SourcePath = null, bool Signed = false, bool TwoState = false);
/// <summary>A fixed unpacked dimension preserving declared bounds; storage offsets ascend from the smaller bound.</summary>
public sealed record RtlDimension(int Left,int Right);
/// <summary>Word storage with optional fixed shape, dynamic/queue lifetime or typed sparse associative keys. Dynamic depth1 metadata denotes initially empty storage; two-state cells default to zero.</summary>
public sealed record RtlMemory(string Name, int Width, int Depth, bool Signed = false, int LowerBound = 0, IReadOnlyList<RtlDimension>? Dimensions = null, bool TwoState = false, bool Dynamic = false, bool Queue = false, bool Associative = false, int KeyWidth = 0, bool KeySigned = false);
/// <summary>Configurable resource limits for RTL IR validation. Bounded preserves the strict default contract; Large unlocks SoC/CPU scale.</summary>
public sealed record RtlDesignLimits(int MaxIrBytes, int MaxSignals, int MaxMemories, int MaxMemoryDepth, int MaxContinuous, int MaxNodes, int MaxDepth, int MaxWordWidth = 256)
{
    public static RtlDesignLimits Bounded { get; } = new(4 * 1024 * 1024, 256, 16, 256, 256, 20000, 64, 256);
    public static RtlDesignLimits Large { get; } = new(64 * 1024 * 1024, 65536, 1024, 1_048_576, 65536, 1_000_000, 128, 512);
}

/// <summary>Validated bounded RTL IR with acyclic settling and process-local sequential blocking storage.</summary>
public sealed partial class RtlDesign
{
    public IReadOnlyList<RtlSignal> Signals { get; }
    public IReadOnlyList<RtlMemory> Memories { get; }
    public IReadOnlyList<JsonElement> Continuous { get; }
    public JsonElement Body { get; }
    /// <summary>Untimed startup statements, executed once before inputs are driven or edges sampled.</summary>
    public JsonElement Initialization { get; }
    /// <summary>Absent for a combinational-only design; no synthetic event input is created.</summary>
    public string? Clock { get; }
    public bool CombinationalOnly => Clock is null;
    public string Json { get; }
    public RtlDesignLimits Limits { get; }
    public int MaxWordBits => maxObservedWordWidth > 256 ? 512 : 256;
    public bool EventDriven => Kind(Body) == "process" || Kind(Body) == "parallel" && Body.GetProperty("items").EnumerateArray().Any(p => Kind(p) == "process");
    private IEnumerable<string> EventSignals => (Kind(Body) == "parallel" ? Body.GetProperty("items").EnumerateArray().ToArray() : [Body]).Where(p => Kind(p) == "process").SelectMany(p => p.GetProperty("events").EnumerateArray()).Select(e => e.GetProperty("signal").GetString()!).Distinct();
    private readonly Dictionary<string, int> signalIds;
    private readonly Dictionary<string, int> memoryIds;
    private int nodes;
    private int maxObservedWordWidth;
    private static int Arity(string kind) => kind switch
    {
        "not" or "bitnot" or "neg" or "pos" or "castsigned" or "castunsigned" or "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor" => 1,
        "add" or "sub" or "and" or "or" or "eq" or "neq" or "caseeq" or "caseneq" or "wildeq" or "wildneq" or "lt" or "le" or "gt" or "ge" or "concat" or "bitand" or "bitor" or "bitxor" or "bitxnor" or "shl" or "shr" or "ashr" or "mul" or "div" or "mod" => 2,
        "cond" => 3,
        _ => 0
    };
    private static bool Scalar(string kind) => kind is "and" or "or" or "not" or "eq" or "neq" or "caseeq" or "caseneq" or "wildeq" or "wildneq" or "lt" or "le" or "gt" or "ge" or "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor";
    private static string Kind(JsonElement e) => e.GetProperty("kind").GetString()!;
    private static string Name(JsonElement e) => e.GetProperty("name").GetString()!;
    private static string CaseKind(JsonElement e) => e.TryGetProperty("case_kind", out var ck) ? ck.GetString()! : "exact";
    private static bool IsCaseDefault(JsonElement item) => item.TryGetProperty("match", out var m) && m.ValueKind == JsonValueKind.Null;
    private static JsonElement[] CaseItemMatches(JsonElement item)
    {
        if (item.TryGetProperty("matches", out var arr)) return arr.EnumerateArray().ToArray();
        if (item.TryGetProperty("match", out var m) && m.ValueKind != JsonValueKind.Null) return [m];
        return [];
    }
    public int SignalId(string name) => signalIds.TryGetValue(name, out int id) ? id : throw new IrValidationException("Unknown RTL signal: " + name);
    private int MemoryId(string name) => memoryIds.TryGetValue(name, out int id) ? id : throw new IrValidationException("Unknown RTL memory: " + name);
    public static bool Identifier(string name) => Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant);
    private RtlDesign(string json, RtlDesignLimits limits)
    {
        Limits = limits;
        if (json.Length > limits.MaxIrBytes) throw new IrValidationException("RTL IR too large.");
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        JsonShape.Fields(root, ["format", "signals", "memories", "continuous", "clock", "body"], ["combinational", "initialization", "timed", "time_precision", "named_events"]);
        if (root.GetProperty("format").GetString() != "velosim.rtl.v1") throw new IrValidationException("Unsupported RTL IR.");
        Signals = root.GetProperty("signals").EnumerateArray().Select(e => { JsonShape.Fields(e, ["name", "width", "direction"], ["path", "signed", "two_state"]); return new RtlSignal(Name(e), e.GetProperty("width").GetInt32(), e.GetProperty("direction").GetString()!, e.TryGetProperty("path", out var path) ? path.GetString() : null, e.TryGetProperty("signed",out var sign)&&sign.GetBoolean(),e.TryGetProperty("two_state",out var two)&&two.GetBoolean()); }).ToArray();
        Memories = root.GetProperty("memories").EnumerateArray().Select(ParseMemory).ToArray();
        if (Signals.Count < 1 || Signals.Count > limits.MaxSignals || Memories.Count > limits.MaxMemories || Signals.Any(s => !Identifier(s.Name) || s.Width < 1 || s.Width > limits.MaxWordWidth || s.Direction is not ("input" or "output" or "internal")) || Memories.Any(m => !Identifier(m.Name) || m.Width < 1 || m.Width > limits.MaxWordWidth || m.Depth < 1 || m.Depth > limits.MaxMemoryDepth || m.LowerBound<0 || (long)m.LowerBound+m.Depth-1>int.MaxValue) || Signals.Select(s => s.Name).Concat(Memories.Select(m => m.Name)).Distinct().Count() != Signals.Count + Memories.Count) throw new IrValidationException("Invalid RTL declarations.");
        maxObservedWordWidth = Signals.Select(s => s.Width).Concat(Memories.Select(m => m.Width)).DefaultIfEmpty(1).Max();
        signalIds = Signals.Select((s, i) => (s.Name, i)).ToDictionary(s => s.Name, s => s.i, StringComparer.Ordinal); memoryIds = Memories.Select((m, i) => (m.Name, i)).ToDictionary(m => m.Name, m => m.i, StringComparer.Ordinal);
        foreach(var memory in Memories)ValidateMemoryShape(memory);
        if (Signals.Any(s => s.SourcePath is not null && (s.Direction != "internal" || s.SourcePath.Length > 1024 || !Regex.IsMatch(s.SourcePath, "^[A-Za-z0-9_.$\\[\\]:]+$", RegexOptions.CultureInvariant))) || Signals.Select(s => s.SourcePath ?? s.Name).Distinct(StringComparer.Ordinal).Count() != Signals.Count) throw new IrValidationException("Invalid or duplicate source signal paths.");
        Clock = root.GetProperty("clock").GetString();
        if (Clock is not null && (Signals[SignalId(Clock)].Width != 1 || Signals[SignalId(Clock)].Direction != "input")) throw new IrValidationException("Clock must be a scalar input.");
        Body = root.GetProperty("body").Clone(); var sequential = new HashSet<string>();
        if (root.TryGetProperty("timed", out var timed))
        {
            Continuous = [];
            Initialization = root.TryGetProperty("initialization", out var startup) ? startup.Clone() : Element("{\"kind\":\"block\",\"items\":[]}");
            ValidateTimed(root, timed);
            Json = json;
            return;
        }
        if (root.TryGetProperty("time_precision", out _)) throw new IrValidationException("Time precision requires explicit-time processes.");
        if (root.TryGetProperty("named_events", out _)) throw new IrValidationException("Named events require explicit-time processes.");
        if (CombinationalOnly && (Kind(Body) != "block" || Body.GetProperty("items").GetArrayLength() != 0)) throw new IrValidationException("Clockless designs require an empty sequential body.");
        ValidateStatement(Body, sequential, 0); _ = StatementCost(Body);
        if (EventDriven && Kind(Body) == "parallel" && Body.GetProperty("items").EnumerateArray().Any(p => Kind(p) != "process")) throw new IrValidationException("Explicit and implicit event processes cannot be mixed.");
        var continuous = root.GetProperty("continuous").EnumerateArray().Select(e => e.Clone()).ToList(); if (continuous.Count > limits.MaxContinuous) throw new IrValidationException("Too many continuous assignments.");
        var pending = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var assignment in continuous)
        {
            JsonShape.Fields(assignment, ["target", "value"]); var target = assignment.GetProperty("target"); ValidateExpression(target, 0); ValidateExpression(assignment.GetProperty("value"), 0);
            if (References(assignment.GetProperty("value")).Intersect(EventSignals.Append(Clock)).Any()) throw new IrValidationException("Clock-dependent continuous logic is outside the race-free input-event subset.");
            if (Kind(target) != "ref" || Signals[SignalId(Name(target))].Direction == "input" || sequential.Contains(Name(target)) || !pending.TryAdd(Name(target), assignment)) throw new IrValidationException("Invalid or conflicting continuous writer.");
        }
        var sorted = SortCombinational(root, sequential, pending);
        var combWriters = SettledSteps.SelectMany(step => Statements(step).Where(e => Kind(e)=="blocking").SelectMany(e => TargetLeaves(e.GetProperty("target"))).Select(TargetOwner)).ToHashSet(StringComparer.Ordinal);
        Initialization = root.TryGetProperty("initialization",out var initialization) ? initialization.Clone() : Element("{\"kind\":\"block\",\"items\":[]}");
        var initialWriters=ValidateInitialization(combWriters);
        if (Signals.Any(s => s.Direction != "input" && !sequential.Contains(s.Name) && !combWriters.Contains(s.Name) && !initialWriters.Contains(s.Name))) throw new IrValidationException("Undriven non-input signals are unsupported; net/variable initialization must not be guessed.");
        Continuous = sorted.AsReadOnly(); ValidateBlockingIsolation(); Json = json;
    }
    /// <summary>Parses the bounded IR and rejects unsupported, malformed or conflicting execution contracts.</summary>
    public static RtlDesign Parse(string json, RtlDesignLimits? limits = null)
    {
        try { return new(json, limits ?? RtlDesignLimits.Bounded); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException or FormatException or ArgumentException && e is not IrValidationException) { throw new IrValidationException("Malformed RTL IR: " + e.Message); }
    }
    private void Budget(int depth) { if (depth > Limits.MaxDepth || ++nodes > Limits.MaxNodes) throw new IrValidationException("RTL node/depth budget exceeded."); }
    private void ValidateIndex(JsonElement e,int depth){ValidateExpression(e,depth);if(ExpressionSigned(e)&&!(Kind(e)=="const"&&e.GetProperty("bits").GetString()![0]=='0'))throw new IrValidationException("Signed indices require an explicit unsigned cast.");}
    private int RecordWidth(int w) { if (w > maxObservedWordWidth) maxObservedWordWidth = w; return w; }
    private int ValidateExpression(JsonElement e, int depth)
    {
        Budget(depth); string kind = Kind(e);
        if (kind == "convert") return RecordWidth(ValidateConversion(e,depth));
        if(kind=="exists"){JsonShape.Fields(e,["kind","name","index"]);RequireAssociative(Name(e));ValidateExpression(e.GetProperty("index"),depth+1);return RecordWidth(32);}
        if (kind == "size") { JsonShape.Fields(e,["kind","name"]); RequireContainer(Name(e)); return RecordWidth(32); }
        if (kind == "ref") { JsonShape.Fields(e, ["kind", "name"]); return RecordWidth(Signals[SignalId(Name(e))].Width); }
        if (kind == "const") { JsonShape.Fields(e, ["kind", "bits"], ["signed"]); if(e.TryGetProperty("signed",out var sign))_=sign.GetBoolean(); var bits=e.GetProperty("bits").GetString();if(string.IsNullOrEmpty(bits)||bits.Length>Limits.MaxWordWidth||bits.Any(c=>c is not ('0' or '1' or 'x' or 'z')))throw new IrValidationException("Invalid RTL constant.");return RecordWidth(bits.Length); }
        if (kind == "index") return RecordWidth(ValidateMemoryAccess(e,depth));
        if (kind == "bitselect") { JsonShape.Fields(e, ["kind", "value", "index"]); ValidateExpression(e.GetProperty("value"), depth + 1); ValidateIndex(e.GetProperty("index"), depth + 1); return 1; }
        if (kind == "partselect") { JsonShape.Fields(e, ["kind", "value", "left", "right"]); ValidateExpression(e.GetProperty("value"), depth + 1); int left = e.GetProperty("left").GetInt32(), right = e.GetProperty("right").GetInt32(); if (right < 0 || left < right || (long)left - right >= Limits.MaxWordWidth) throw new IrValidationException($"Only nonnegative descending constant part selects of 1..{Limits.MaxWordWidth} bits supported."); return RecordWidth(left - right + 1); }
        if (kind == "indexedselect") { JsonShape.Fields(e, ["kind", "value", "base", "width", "descending"]); ValidateExpression(e.GetProperty("value"), depth + 1); ValidateIndex(e.GetProperty("base"), depth + 1); int width = e.GetProperty("width").GetInt32(); _ = e.GetProperty("descending").GetBoolean(); if (width < 1 || width > Limits.MaxWordWidth) throw new IrValidationException($"Indexed select width must be 1..{Limits.MaxWordWidth}."); return RecordWidth(width); }
        if (kind == "dpi_call")
        {
            JsonShape.Fields(e, ["kind", "name", "width", "args"], ["signed"]);
            if (!Identifier(Name(e))) throw new IrValidationException("Invalid DPI-C function name.");
            int retWidth = e.GetProperty("width").GetInt32();
            if (retWidth is < 1 or > 64) throw new IrValidationException("DPI-C return width must be 1..64.");
            if (e.TryGetProperty("signed", out var dpiSign)) _ = dpiSign.GetBoolean();
            var dpiArgs = e.GetProperty("args").EnumerateArray().ToArray();
            if (dpiArgs.Length > 16) throw new IrValidationException("DPI-C calls support at most 16 arguments.");
            foreach (var da in dpiArgs)
            {
                int aw = ValidateExpression(da, depth + 1);
                if (aw > 64) throw new IrValidationException("DPI-C arguments must be 1..64 bits.");
            }
            return RecordWidth(retWidth);
        }
        JsonShape.Fields(e, ["kind", "args"]); var args = e.GetProperty("args").EnumerateArray().ToArray();
        if (Arity(kind) == 0 || args.Length != Arity(kind)) throw new IrValidationException("Unsupported RTL expression.");
        var widths = args.Select(a => ValidateExpression(a, depth + 1)).ToArray(); var result = Scalar(kind) ? 1 : kind is "shl" or "shr" or "ashr" ? widths[0] : kind == "cond" ? Math.Max(widths[1], widths[2]) : kind == "concat" ? widths.Sum() : widths.Max(); if (result > Limits.MaxWordWidth) throw new IrValidationException($"Expression exceeds {Limits.MaxWordWidth} bits."); return RecordWidth(result);
    }
    private IEnumerable<string> References(JsonElement e)
    {
        if (Kind(e) is "ref" or "size") yield return Name(e);
        // A memory access reads storage as well as address expressions; isolation needs both identities.
        else if (Kind(e) is "index" or "exists") { yield return Name(e); foreach(var index in MemoryIndices(e))foreach (var n in References(index)) yield return n; }
        else if (Kind(e) is "bitselect" or "partselect" or "indexedselect") { foreach (var n in References(e.GetProperty("value"))) yield return n; if (e.TryGetProperty("index", out var index)) foreach (var n in References(index)) yield return n; if (e.TryGetProperty("base", out var basis)) foreach (var n in References(basis)) yield return n; }
        else if (Kind(e)=="convert") { foreach(var n in References(e.GetProperty("value")))yield return n; }
        else if (e.TryGetProperty("args", out var args)) foreach (var a in args.EnumerateArray()) foreach (var n in References(a)) yield return n;
    }
    private void ValidateStatement(JsonElement e, HashSet<string> writers, int depth)
    {
        Budget(depth);
        switch (Kind(e))
        {
            case "assoc_traverse": ValidateAssociativeTraversal(e,writers,depth);break;
            case "queue_push": case "queue_pop": ValidateQueueOperation(e,writers,depth);break;
            case "allocate":
                JsonShape.Fields(e,["kind","name","count","preserve"]);RequireDynamic(Name(e));ValidateExpression(e.GetProperty("count"),depth+1);_=e.GetProperty("preserve").GetBoolean();writers.Add(Name(e));break;
            case "delete":
                JsonShape.Fields(e,["kind","name"],["index"]);RequireContainer(Name(e));if(e.TryGetProperty("index",out var deleteIndex)){if(Memories[MemoryId(Name(e))].Associative)ValidateExpression(deleteIndex,depth+1);else{RequireQueue(Name(e));ValidateIndex(deleteIndex,depth+1);}}writers.Add(Name(e));break;
            case "while": case "do":
                JsonShape.Fields(e,["kind","condition","body"]);ValidateExpression(e.GetProperty("condition"),depth+1);ValidateStatement(e.GetProperty("body"),writers,depth+1);break;
            case "runtime_repeat":
                JsonShape.Fields(e,["kind","count","body"]);ValidateExpression(e.GetProperty("count"),depth+1);ValidateStatement(e.GetProperty("body"),writers,depth+1);break;
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
                if (processes.Length < 1 || processes.Length > Limits.MaxSignals) throw new IrValidationException($"Parallel process count must be 1..{Limits.MaxSignals}.");
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
                if (Kind(target) == "index") { if(Kind(e)=="nba"&&(Memories[MemoryId(Name(target))].Dynamic||Memories[MemoryId(Name(target))].Queue||Memories[MemoryId(Name(target))].Associative))throw new IrValidationException("Dynamic/queue/associative element writes require blocking assignments.");writers.Add(Name(target)); break; }
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
                JsonShape.Fields(e, ["kind", "value", "items"], ["case_kind"]);
                string ck = CaseKind(e);
                if (ck is not ("exact" or "casez" or "casex")) throw new IrValidationException("Unsupported case_kind: " + ck);
                ValidateExpression(e.GetProperty("value"), depth + 1); int defaults = 0;
                foreach (var item in e.GetProperty("items").EnumerateArray())
                {
                    JsonShape.Fields(item, ["body"], ["match", "matches"]);
                    bool hasMatch = item.TryGetProperty("match", out var match);
                    bool hasMatches = item.TryGetProperty("matches", out var matches);
                    if (hasMatch == hasMatches) throw new IrValidationException("Case item must specify either match or matches.");
                    if (hasMatch)
                    {
                        if (match.ValueKind == JsonValueKind.Null) defaults++;
                        else { if (Kind(match) != "const") throw new IrValidationException("Only constant case labels supported."); ValidateExpression(match, depth + 1); }
                    }
                    else
                    {
                        if (matches.ValueKind != JsonValueKind.Array || matches.GetArrayLength() is < 1 or > 256)
                            throw new IrValidationException("Case matches must contain 1..256 constant labels.");
                        foreach (var m in matches.EnumerateArray())
                        {
                            if (Kind(m) != "const") throw new IrValidationException("Only constant case labels supported.");
                            ValidateExpression(m, depth + 1);
                        }
                    }
                    ValidateStatement(item.GetProperty("body"), writers, depth + 1);
                }
                if (defaults > 1) throw new IrValidationException("Duplicate default case."); break;
            case "assert":
                JsonShape.Fields(e, ["kind", "condition"], ["message"]);
                ValidateExpression(e.GetProperty("condition"), depth + 1);
                if (e.TryGetProperty("message", out var msgEl) && (msgEl.ValueKind != JsonValueKind.String || msgEl.GetString()!.Length > 256))
                    throw new IrValidationException("Invalid assertion message.");
                break;
            case "finish":
                JsonShape.Fields(e, ["kind"], ["code"]);
                if (e.TryGetProperty("code", out var codeEl)) _ = codeEl.GetInt32();
                break;
            case "display":
                JsonShape.Fields(e, ["kind", "format"], ["args", "newline"]);
                if (e.GetProperty("format").GetString()!.Length > 1024) throw new IrValidationException("Display format exceeds 1024 characters.");
                if (e.TryGetProperty("newline", out var nlEl)) _ = nlEl.GetBoolean();
                if (e.TryGetProperty("args", out var dispArgs))
                {
                    if (dispArgs.GetArrayLength() > 32) throw new IrValidationException("Display supports at most 32 arguments.");
                    foreach (var da in dispArgs.EnumerateArray()) ValidateExpression(da, depth + 1);
                }
                break;
            default: throw new IrValidationException("Unsupported RTL statement.");
        }
    }
    private static IEnumerable<JsonElement> Statements(JsonElement e)
    {
        yield return e;
        IEnumerable<JsonElement> children=Kind(e) switch {
            "block" or "parallel"=>e.GetProperty("items").EnumerateArray().ToArray(),
            "process" or "repeat" or "runtime_repeat" or "while" or "do"=>[e.GetProperty("body")],"if"=>[e.GetProperty("then"),e.GetProperty("else")],
            "case"=>e.GetProperty("items").EnumerateArray().Select(i=>i.GetProperty("body")),_=>[]};
        foreach(var child in children)foreach(var statement in Statements(child))yield return statement;
    }
    private void ValidateBlockingIsolation()
    {
        var processes=Kind(Body)=="parallel"?Body.GetProperty("items").EnumerateArray().ToArray():[Body];
        var reads=processes.Select(p=>Statements(p).SelectMany(StatementReferences).ToHashSet(StringComparer.Ordinal)).ToArray();
        for(int i=0;i<processes.Length;++i){
            var statements=Statements(processes[i]).ToArray();var blocking=statements.Where(e=>Kind(e)=="blocking").SelectMany(e=>TargetLeaves(e.GetProperty("target"))).Select(TargetOwner).Concat(statements.Where(e=>Kind(e) is "allocate" or "delete").Select(Name)).Concat(QueueDestinations(statements)).Concat(AssociativeDestinations(statements)).ToHashSet(StringComparer.Ordinal);
            var nonblocking=statements.Where(e=>Kind(e)=="nba").SelectMany(e=>TargetLeaves(e.GetProperty("target"))).Select(TargetOwner).ToHashSet(StringComparer.Ordinal);
            if(blocking.Overlaps(nonblocking))throw new IrValidationException("A word cannot mix blocking and nonblocking assignments.");
            if(Enumerable.Range(0,processes.Length).Any(j=>j!=i&&blocking.Overlaps(reads[j]))||SettledSteps.SelectMany(Statements).Any(a=>new[]{"value","condition"}.Where(n=>a.TryGetProperty(n,out _)).Any(n=>References(a.GetProperty(n)).Any(blocking.Contains))))throw new IrValidationException("Blocking destinations must be isolated from other processes and continuous logic.");
        }
    }
    private long StatementCost(JsonElement e)
    {
        long cost=Kind(e) switch {
            "parallel" or "block" => 1+e.GetProperty("items").EnumerateArray().Sum(StatementCost),
            "process" => 1+StatementCost(e.GetProperty("body")),
            "repeat" => 1+Math.Max(1,e.GetProperty("count").GetInt32())*StatementCost(e.GetProperty("body")),
            "while" or "do" or "runtime_repeat" => 1+StatementCost(e.GetProperty("body")),
            "if" => 1+StatementCost(e.GetProperty("then"))+StatementCost(e.GetProperty("else")),
            "case" => 1+e.GetProperty("items").EnumerateArray().Sum(item=>StatementCost(item.GetProperty("body"))), _=>1};
        return cost<=Limits.MaxNodes?cost:throw new IrValidationException("Expanded statement execution budget exceeded.");
    }
    private static IEnumerable<JsonElement> TargetLeaves(JsonElement target)
    {
        if (Kind(target) == "concat") foreach (var child in target.GetProperty("args").EnumerateArray()) foreach (var leaf in TargetLeaves(child)) yield return leaf;
        else yield return target;
    }
    // Ownership and mixed-assignment checks operate on whole parent storage, even for disjoint selections.
    private static string TargetOwner(JsonElement target) => Kind(target) is "bitselect" or "partselect" or "indexedselect" ? Name(target.GetProperty("value")) : Name(target);
    private int ExpressionWidth(JsonElement e) => Kind(e) switch
    {
        "size" or "exists"=>32,"convert" or "dpi_call"=>e.GetProperty("width").GetInt32(), "ref" => Signals[SignalId(Name(e))].Width, "index" => Memories[MemoryId(Name(e))].Width, "const" => e.GetProperty("bits").GetString()!.Length,
        "bitselect" => 1, "partselect" => e.GetProperty("left").GetInt32() - e.GetProperty("right").GetInt32() + 1, "indexedselect" => e.GetProperty("width").GetInt32(),
        var kind when Scalar(kind) => 1, "shl" or "shr" or "ashr" => ExpressionWidth(e.GetProperty("args")[0]), "cond" => e.GetProperty("args").EnumerateArray().Skip(1).Max(ExpressionWidth), "concat" => e.GetProperty("args").EnumerateArray().Sum(ExpressionWidth), _ => e.GetProperty("args").EnumerateArray().Max(ExpressionWidth)
    };
    private bool ExpressionSigned(JsonElement e) => Kind(e) switch
    {
        "size" or "exists"=>true,"convert"=>e.GetProperty("signed").GetBoolean(), "dpi_call"=>e.TryGetProperty("signed",out var ds)&&ds.GetBoolean(), "ref"=>Signals[SignalId(Name(e))].Signed,"index"=>Memories[MemoryId(Name(e))].Signed,
        "const"=>e.TryGetProperty("signed",out var sign)&&sign.GetBoolean(),"castsigned"=>true,
        "castunsigned" or "bitselect" or "partselect" or "indexedselect" or "concat"=>false,
        var k when Scalar(k)=>false,"shl" or "shr" or "ashr"=>ExpressionSigned(e.GetProperty("args")[0]),
        "cond"=>e.GetProperty("args").EnumerateArray().Skip(1).All(ExpressionSigned),
        _=>e.GetProperty("args").EnumerateArray().All(ExpressionSigned)
    };
    internal static string CppStringLiteral(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c > 0x7e) sb.Append($"\\x{(int)c:x2}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
    private static string EmitConst(string bits)
    {
        int w = bits.Length;
        bool hasXz = bits.IndexOfAny(['x', 'z']) >= 0;
        if (!hasXz && w <= 64)
        {
            ulong v = 0;
            foreach (char c in bits) { v <<= 1; if (c == '1') v |= 1; }
            return $"lit(0x{v:x}ULL,{w})";
        }
        if (w <= 128)
        {
            ulong vlo = 0, vhi = 0, xlo = 0, xhi = 0, zlo = 0, zhi = 0;
            for (int i = 0; i < w; ++i)
            {
                int pos = w - 1 - i;
                char c = bits[i];
                if (pos < 64)
                {
                    ulong bit = 1UL << pos;
                    if (c == '1') vlo |= bit; else if (c == 'x') xlo |= bit; else if (c == 'z') zlo |= bit;
                }
                else
                {
                    ulong bit = 1UL << (pos - 64);
                    if (c == '1') vhi |= bit; else if (c == 'x') xhi |= bit; else if (c == 'z') zhi |= bit;
                }
            }
            return $"lit(0x{vlo:x}ULL,0x{vhi:x}ULL,0x{xlo:x}ULL,0x{xhi:x}ULL,0x{zlo:x}ULL,0x{zhi:x}ULL,{w})";
        }
        if (!hasXz && w <= 256)
        {
            ulong[] l = new ulong[4];
            for (int i = 0; i < w; ++i)
            {
                if (bits[i] != '1') continue;
                int pos = w - 1 - i;
                l[pos >> 6] |= 1UL << (pos & 63);
            }
            return $"lit256(0x{l[0]:x}ULL,0x{l[1]:x}ULL,0x{l[2]:x}ULL,0x{l[3]:x}ULL,{w})";
        }
        if (!hasXz && w <= 512)
        {
            ulong[] l = new ulong[8];
            for (int i = 0; i < w; ++i)
            {
                if (bits[i] != '1') continue;
                int pos = w - 1 - i;
                l[pos >> 6] |= 1UL << (pos & 63);
            }
            return $"lit512(0x{l[0]:x}ULL,0x{l[1]:x}ULL,0x{l[2]:x}ULL,0x{l[3]:x}ULL,0x{l[4]:x}ULL,0x{l[5]:x}ULL,0x{l[6]:x}ULL,0x{l[7]:x}ULL,{w})";
        }
        return "parse(\"" + bits + "\")";
    }
    private IReadOnlyList<(string Name, int ArgCount)> CollectDpiCalls()
    {
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        void WalkExpr(JsonElement e)
        {
            string k = Kind(e);
            if (k == "dpi_call")
            {
                int argc = e.GetProperty("args").GetArrayLength();
                string fn = Name(e);
                if (calls.TryGetValue(fn, out int prev) && prev != argc)
                    throw new IrValidationException($"DPI-C function '{fn}' called with conflicting argument counts ({prev} vs {argc}).");
                calls[fn] = argc;
            }
            if (e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object) WalkExpr(v);
            if (e.TryGetProperty("index", out var idx) && idx.ValueKind == JsonValueKind.Object) WalkExpr(idx);
            if (e.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.Object) WalkExpr(b);
            if (e.TryGetProperty("indices", out var idxs) && idxs.ValueKind == JsonValueKind.Array)
                foreach (var ix in idxs.EnumerateArray()) WalkExpr(ix);
            if (e.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var arg in a.EnumerateArray()) WalkExpr(arg);
        }
        foreach (var root in SettledSteps.Append(Body).Append(Initialization))
        {
            foreach (var st in Statements(root))
            {
                if (st.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object) WalkExpr(v);
                if (st.TryGetProperty("condition", out var c) && c.ValueKind == JsonValueKind.Object) WalkExpr(c);
                if (st.TryGetProperty("count", out var cnt) && cnt.ValueKind == JsonValueKind.Object) WalkExpr(cnt);
                if (st.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.Object) WalkExpr(t);
                if (Kind(st) == "display" && st.TryGetProperty("args", out var da))
                    foreach (var arg in da.EnumerateArray()) WalkExpr(arg);
            }
        }
        return calls.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)).ToArray();
    }
    private static void EmitDpiPrototypes(StringBuilder code, IReadOnlyList<(string Name, int ArgCount)> dpiCalls)
    {
        if (dpiCalls.Count == 0) return;
        code.AppendLine("extern \"C\" {");
        foreach (var (name, argc) in dpiCalls)
        {
            string parms = argc == 0 ? "void" : string.Join(", ", Enumerable.Repeat("uint64_t", argc));
            code.AppendLine($"uint64_t {name}({parms});");
        }
        code.AppendLine("}");
    }
    private static int RegisterCoveragePoint(List<(string Id, string Kind)>? coverage, string id, string kind)
    {
        if (coverage is null) return -1;
        int idx = coverage.Count;
        coverage.Add((id, kind));
        return idx;
    }
    private static void EmitCoveragePointsOverride(StringBuilder code, List<(string Id, string Kind)>? coverage)
    {
        if (coverage is null || coverage.Count == 0) return;
        code.AppendLine("std::vector<vsrtl::CoveragePoint> coveragePoints() const override {");
        code.Append("return {");
        code.AppendJoin(",", coverage.Select((pt, idx) => $"{{{CppStringLiteral(pt.Id)},{CppStringLiteral(pt.Kind)},_vs_cov[{idx}]}}"));
        code.AppendLine("};\n}");
    }
    private string Expression(JsonElement e, int context = 0, bool? forceSigned = null)
    {
        string kind = Kind(e);bool signed=forceSigned??ExpressionSigned(e);
        string Extend(string value)=>signed&&context>ExpressionWidth(e)?$"signed_resize({value},{context})":value;
        if (kind == "convert") return Extend(Conversion(e));
        if (kind == "ref") return Extend($"s[{SignalId(Name(e))}]");
        if(kind=="size"){int id=MemoryId(Name(e));return Extend($"word({(Memories[id].Associative?"assoc":"mem")}[{id}].size(),0,0,32)");}
        if (kind == "const") return Extend(EmitConst(e.GetProperty("bits").GetString()!));
        if(kind=="exists"){int id=MemoryId(Name(e));return Extend($"assoc_exists(assoc[{id}],{Expression(e.GetProperty("index"),Memories[id].KeyWidth)},{Memories[id].KeyWidth},{(Memories[id].KeySigned?"true":"false")})");}
        if (kind == "index") { int id = MemoryId(Name(e));var memory=Memories[id];var read=memory.Associative?$"assoc_read(assoc[{id}],{MemoryIndex(e,id)},{memory.KeyWidth},{(memory.KeySigned?"true":"false")},{memory.Width},{(memory.TwoState?"true":"false")})":(memory.Dynamic||memory.Queue)?$"dynamic_read(mem[{id}],{MemoryIndex(e,id)},{memory.Width},{(memory.TwoState?"true":"false")})":$"read(mem[{id}],{MemoryIndex(e,id)},{memory.Width})";return Extend(read); }
        if (kind == "bitselect") return $"select_bit({Expression(e.GetProperty("value"))},{Expression(e.GetProperty("index"))})";
        if (kind == "partselect") return $"select_part({Expression(e.GetProperty("value"))},{e.GetProperty("left").GetInt32()},{e.GetProperty("right").GetInt32()})";
        if (kind == "indexedselect") return $"select_indexed({Expression(e.GetProperty("value"))},{Expression(e.GetProperty("base"))},{e.GetProperty("width").GetInt32()},{(e.GetProperty("descending").GetBoolean() ? "true" : "false")})";
        if (kind == "dpi_call")
        {
            string callArgs = string.Join(",", e.GetProperty("args").EnumerateArray().Select(a => $"uint64_t({Expression(a)}.v.limb[0])"));
            return Extend($"word(::{Name(e)}({callArgs}),0,0,{e.GetProperty("width").GetInt32()})");
        }
        var args = e.GetProperty("args").EnumerateArray().ToArray(); int width = Math.Max(context, ExpressionWidth(e));
        return kind switch
        {
            "castsigned" or "castunsigned"=>Extend(Expression(args[0])),
            "add" or "sub" => $"arithmetic({Expression(args[0], width,signed)},{Expression(args[1], width,signed)},{width},{(kind == "sub" ? "true" : "false")})",
            "bitnot" => $"bit_not({Expression(args[0], width,signed)},{width})",
            "neg" => $"arithmetic(lit(0ULL,{width}),{Expression(args[0], width,signed)},{width},true)",
            "pos" => $"resize({Expression(args[0], width,signed)},{width})",
            "mul" or "div" or "mod" => $"{(signed?"signed_product":"product")}({Expression(args[0], width,signed)},{Expression(args[1], width,signed)},{width},{(kind == "mul" ? 0 : kind == "div" ? 1 : 2)})",
            "cond" => $"conditional({Expression(args[0])},{Expression(args[1], width,signed)},{Expression(args[2], width,signed)},{width})",
            "ashr" when signed=>$"signed_shift({Expression(args[0],width,true)},{Expression(args[1])},{width})",
            "shl" or "shr" or "ashr" => $"shift({Expression(args[0], width,signed)},{Expression(args[1])},{width},{(kind == "shl" ? "false" : "true")})",
            "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor" => $"reduce({Expression(args[0])},{(kind == "redand" ? 0 : kind == "rednand" ? 1 : kind == "redor" ? 2 : kind == "rednor" ? 3 : kind == "redxor" ? 4 : 5)})",
            "bitxnor" => $"bit_not(bit_binary({Expression(args[0], width,signed)},{Expression(args[1], width,signed)},{width},2),{width})",
            "bitand" or "bitor" or "bitxor" => $"bit_binary({Expression(args[0], width,signed)},{Expression(args[1], width,signed)},{width},{(kind == "bitand" ? 0 : kind == "bitor" ? 1 : 2)})",
            "not" => $"logical_not({Expression(args[0])})", "and" => $"logical_and({Expression(args[0])},{Expression(args[1])})",
            "or" => $"logical_or({Expression(args[0])},{Expression(args[1])})",
            "eq" or "neq" => (kind == "neq" ? "logical_not(" : "") + $"equal({Expression(args[0], args.Max(ExpressionWidth),args.All(ExpressionSigned))},{Expression(args[1], args.Max(ExpressionWidth),args.All(ExpressionSigned))})" + (kind == "neq" ? ")" : ""),
            "wildeq" or "wildneq" => $"{(kind == "wildneq" ? "wildneq" : "wildeq")}({Expression(args[0], args.Max(ExpressionWidth),args.All(ExpressionSigned))},{Expression(args[1], args.Max(ExpressionWidth),args.All(ExpressionSigned))})",
            "caseeq" or "caseneq" => $"W{{uint64_t({(kind == "caseneq" ? "!" : "")}exact({Expression(args[0], args.Max(ExpressionWidth),args.All(ExpressionSigned))},{Expression(args[1], args.Max(ExpressionWidth),args.All(ExpressionSigned))})),0,0,1}}",
            "lt" or "le" or "gt" or "ge" => $"{(args.All(ExpressionSigned)?"signed_compare":"compare")}({Expression(args[0], args.Max(ExpressionWidth),args.All(ExpressionSigned))},{Expression(args[1], args.Max(ExpressionWidth),args.All(ExpressionSigned))},{(kind == "lt" ? 0 : kind == "le" ? 1 : kind == "gt" ? 2 : 3)})",
            "concat" => $"concat({Expression(args[0])},{Expression(args[1])})", _ => throw new IrValidationException("Unsupported expression.")
        };
    }
    private void WordWrite(StringBuilder code, JsonElement target, string value, int exprWidth = -1)
    {
        if (Kind(target) == "ref") { int id = SignalId(Name(target)); code.AppendLine($"next[{id}]={StoreWord(value,Signals[id],exprWidth)};"); return; }
        int selectedId = SignalId(Name(target.GetProperty("value"))), width = ExpressionWidth(target);
        string basis = Kind(target) == "bitselect" ? Expression(target.GetProperty("index")) : Kind(target) == "indexedselect" ? Expression(target.GetProperty("base")) : $"W{{{target.GetProperty("right").GetInt32()},0,0,64}}";
        bool descending = Kind(target) == "indexedselect" && target.GetProperty("descending").GetBoolean();
        string deposited=$"deposit_indexed(next[{selectedId}],{basis},{width},{(descending ? "true" : "false")},{value})";code.AppendLine($"next[{selectedId}]={(Signals[selectedId].TwoState?"two_state("+deposited+")":deposited)};");
    }
    private static string StoreWord(string value,RtlSignal signal,int exprWidth=-1)=>signal.TwoState?$"two_state(resize({value},{signal.Width}))":(exprWidth==signal.Width?value:$"resize({value},{signal.Width})");
    private bool ExpressionCanThrow(JsonElement e)
    {
        string kind = Kind(e);
        if (kind == "indexedselect") return true;
        if (kind is "bitselect" or "partselect") return ExpressionCanThrow(e.GetProperty("value")) || (e.TryGetProperty("index", out var idx) && ExpressionCanThrow(idx));
        if (kind is "index" or "exists") return MemoryIndices(e).Any(ExpressionCanThrow);
        if (kind == "convert") return ExpressionCanThrow(e.GetProperty("value"));
        if (e.TryGetProperty("args", out var args)) return args.EnumerateArray().Any(ExpressionCanThrow);
        return false;
    }
    private bool StatementsHaveRuntimeLoops(IEnumerable<JsonElement> roots) =>
        roots.SelectMany(Statements).Any(e => Kind(e) is "while" or "do" or "runtime_repeat");
    private bool CanEvaluateThrow()
    {
        var allStmts = SettledSteps.Append(Body).SelectMany(Statements).ToArray();
        if (allStmts.Any(e => Kind(e) is "while" or "do" or "runtime_repeat" or "allocate" or "queue_push" or "queue_pop" or "assoc_traverse" or "assert")) return true;
        if (Memories.Any(m => m.Associative || m.Queue || m.Dynamic)) return true;
        foreach (var st in allStmts)
        {
            if (st.TryGetProperty("target", out var t) && TargetLeaves(t).Any(leaf => Kind(leaf) == "indexedselect" || ExpressionCanThrow(leaf))) return true;
            if (st.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object && ExpressionCanThrow(v)) return true;
            if (st.TryGetProperty("condition", out var c) && c.ValueKind == JsonValueKind.Object && ExpressionCanThrow(c)) return true;
        }
        return !CombinationalOnly && !EventDriven;
    }
    private void Statement(StringBuilder code, JsonElement e, ref int unique, bool trackBudget = true, bool useMemLog = false, List<(string Id, string Kind)>? coverage = null)
    {
        if (trackBudget) code.AppendLine("execution_step(execution_budget);");
        switch (Kind(e))
        {
            case "assoc_traverse": EmitAssociativeTraversal(code,e,ref unique);break;
            case "queue_push": case "queue_pop": EmitQueueOperation(code,e,ref unique);break;
            case "allocate": case "delete":
                int dynamicId=MemoryId(Name(e));var dynamicMemory=Memories[dynamicId];
                if(Kind(e)=="delete"){if(e.TryGetProperty("index",out var eraseIndex))code.AppendLine(dynamicMemory.Associative?$"assoc_delete(nextAssoc[{dynamicId}],{Expression(eraseIndex,dynamicMemory.KeyWidth)},{dynamicMemory.KeyWidth},{(dynamicMemory.KeySigned?"true":"false")});":$"queue_delete(nextMem[{dynamicId}],{Expression(eraseIndex)});");else code.AppendLine($"{(dynamicMemory.Associative?"nextAssoc":"nextMem")}[{dynamicId}].clear();");}
                else code.AppendLine($"dynamic_allocate(nextMem[{dynamicId}],{Expression(e.GetProperty("count"))},{dynamicMemory.Width},{(dynamicMemory.TwoState?"true":"false")},{(e.GetProperty("preserve").GetBoolean()?"true":"false")},{(ExpressionSigned(e.GetProperty("count"))?"true":"false")});");
                code.AppendLine(dynamicMemory.Associative?$"assoc[{dynamicId}]=nextAssoc[{dynamicId}];":$"mem[{dynamicId}]=nextMem[{dynamicId}];");break;
            case "while": case "do":
                if(Kind(e)=="do")code.AppendLine("do{");else code.AppendLine($"while(yes({Expression(e.GetProperty("condition"))})){{");
                code.AppendLine("execution_step(execution_budget);");Statement(code,e.GetProperty("body"),ref unique,trackBudget,useMemLog,coverage);
                code.AppendLine(Kind(e)=="do"?$"}}while(yes({Expression(e.GetProperty("condition"))}));":"}");break;
            case "runtime_repeat":
                string runtimeLoop="runtime_repeat"+unique++;code.AppendLine($"for(unsigned {runtimeLoop}=repeat_count({Expression(e.GetProperty("count"))},{(ExpressionSigned(e.GetProperty("count"))?"true":"false")});{runtimeLoop}>0;--{runtimeLoop}){{");
                code.AppendLine("execution_step(execution_budget);");Statement(code,e.GetProperty("body"),ref unique,trackBudget,useMemLog,coverage);code.AppendLine("}");break;
            case "repeat":
                string loop="repeat"+unique++;code.AppendLine($"for(int {loop}=0;{loop}<{e.GetProperty("count").GetInt32()};++{loop}){{");Statement(code,e.GetProperty("body"),ref unique,trackBudget,useMemLog,coverage);code.AppendLine("}");break;
            case "process":
                code.AppendLine("if(" + string.Join("||", e.GetProperty("events").EnumerateArray().Select(trigger => { int id = SignalId(trigger.GetProperty("signal").GetString()!); return $"edge_event(previous_events[{id}],s[{id}],{(trigger.GetProperty("edge").GetString() == "posedge" ? "true" : "false")})"; })) + "){");
                ProcessStatement(code, e.GetProperty("body"), ref unique, trackBudget, useMemLog, coverage); code.AppendLine("}"); break;
            case "parallel": foreach(var item in e.GetProperty("items").EnumerateArray())ProcessStatement(code,item,ref unique,trackBudget,useMemLog,coverage);break;
            case "block": foreach (var item in e.GetProperty("items").EnumerateArray()) Statement(code, item, ref unique, trackBudget, useMemLog, coverage); break;
            case "blocking": case "nba":
                var target = e.GetProperty("target");
                if (Kind(target) == "concat")
                {
                    int total = ExpressionWidth(target), remaining = total; string rhs = "nba" + unique++;
                    code.AppendLine($"{{auto {rhs}=resize({Expression(e.GetProperty("value"), total)},{total});");
                    foreach (var leaf in TargetLeaves(target)) { int width = ExpressionWidth(leaf); remaining -= width; WordWrite(code, leaf, $"select_part({rhs},{remaining + width - 1},{remaining})", width); }
                    code.AppendLine("}");
                }
                else if (Kind(target) is "ref" or "bitselect" or "partselect" or "indexedselect")
                {
                    int tw = ExpressionWidth(target);
                    var valEl = e.GetProperty("value");
                    int vw = ExpressionWidth(valEl);
                    bool exactWidth = vw == tw && !(ExpressionSigned(valEl) && tw > vw);
                    WordWrite(code, target, Expression(valEl, tw), exactWidth ? tw : -1);
                }
                else
                {
                    int id = MemoryId(Name(target)); var value = Expression(e.GetProperty("value"), Memories[id].Width);
                    if (Memories[id].TwoState) value = $"two_state({value})";
                    if (useMemLog && !Memories[id].Associative && !Memories[id].Queue && !Memories[id].Dynamic)
                        code.AppendLine($"_vs_mlog.push({id},mem[{id}].size(),{MemoryIndex(target,id)},{value},{Memories[id].Width});");
                    else
                        code.AppendLine(Memories[id].Associative?$"assoc_write(nextAssoc[{id}],{MemoryIndex(target,id)},{value},{Memories[id].KeyWidth},{(Memories[id].KeySigned?"true":"false")},{Memories[id].Width},{(Memories[id].TwoState?"true":"false")});":Memories[id].Queue?$"queue_write(nextMem[{id}],{MemoryIndex(target,id)},{value},{Memories[id].Width},{(Memories[id].TwoState?"true":"false")});":$"write(nextMem[{id}],{MemoryIndex(target,id)},{value},{Memories[id].Width});");
                }
                // Publish local blocking results only after all RHS and target indices have been sampled.
                if (Kind(e)=="blocking") foreach (var leaf in TargetLeaves(target)) {
                    if (Kind(leaf)=="index") { int id=MemoryId(Name(leaf));code.AppendLine(Memories[id].Associative?$"assoc[{id}]=nextAssoc[{id}];":$"mem[{id}]=nextMem[{id}];"); }
                    else { int id=SignalId(TargetOwner(leaf));code.AppendLine($"s[{id}]=next[{id}];"); }
                }
                break;
            case "if":
            {
                int ifId = unique++;
                int thenCov = RegisterCoveragePoint(coverage, $"if.{ifId}.then", "branch");
                int elseCov = RegisterCoveragePoint(coverage, $"if.{ifId}.else", "branch");
                code.AppendLine($"if(yes({Expression(e.GetProperty("condition"))})){{");
                if (thenCov >= 0) code.AppendLine($"++_vs_cov[{thenCov}];");
                Statement(code, e.GetProperty("then"), ref unique, trackBudget, useMemLog, coverage);
                code.AppendLine("}else{");
                if (elseCov >= 0) code.AppendLine($"++_vs_cov[{elseCov}];");
                Statement(code, e.GetProperty("else"), ref unique, trackBudget, useMemLog, coverage);
                code.AppendLine("}");
                break;
            }
            case "assert":
                code.AppendLine($"vsrtl::check_assert({Expression(e.GetProperty("condition"))},{CppStringLiteral(e.TryGetProperty("message", out var am) ? am.GetString()! : "")});");
                break;
            case "finish":
                code.AppendLine("finish();");
                break;
            case "display":
            {
                string fmt = CppStringLiteral(e.GetProperty("format").GetString()!);
                bool nl = !e.TryGetProperty("newline", out var nlProp) || nlProp.GetBoolean();
                string argList = e.TryGetProperty("args", out var dArgs)
                    ? string.Join(",", dArgs.EnumerateArray().Select(a => Expression(a)))
                    : "";
                code.AppendLine($"emitDisplay(vsrtl::format_display({fmt},{{{argList}}},{(nl ? "true" : "false")}));");
                break;
            }
            case "case":
            {
                int caseId = unique++;
                var label = "case" + caseId;
                var items = e.GetProperty("items").EnumerateArray().ToArray();
                string matcher = CaseKind(e) switch { "casez" => "casez_match", "casex" => "casex_match", _ => "exact" };
                var allMatches = items.SelectMany(CaseItemMatches).ToArray();
                int caseWidth = Math.Max(ExpressionWidth(e.GetProperty("value")), allMatches.Select(ExpressionWidth).DefaultIfEmpty(1).Max());
                bool caseSigned = ExpressionSigned(e.GetProperty("value")) && allMatches.All(ExpressionSigned);
                code.AppendLine($"{{auto {label}={Expression(e.GetProperty("value"), caseWidth, caseSigned)};");
                bool first = true;
                int armIdx = 0;
                foreach (var item in items.Where(i => !IsCaseDefault(i)))
                {
                    var ms = CaseItemMatches(item);
                    string cond = string.Join("||", ms.Select(m => $"{matcher}({label},{Expression(m, caseWidth, caseSigned)})"));
                    int armCov = RegisterCoveragePoint(coverage, $"case.{caseId}.arm.{armIdx++}", "case_arm");
                    code.AppendLine($"{(first ? "if" : "else if")}({cond}){{");
                    if (armCov >= 0) code.AppendLine($"++_vs_cov[{armCov}];");
                    Statement(code, item.GetProperty("body"), ref unique, trackBudget, useMemLog, coverage);
                    code.AppendLine("}");
                    first = false;
                }
                var defaultItem = items.FirstOrDefault(IsCaseDefault);
                if (defaultItem.ValueKind != JsonValueKind.Undefined)
                {
                    int defCov = RegisterCoveragePoint(coverage, $"case.{caseId}.default", "case_default");
                    code.AppendLine(first ? "{" : "else{");
                    if (defCov >= 0) code.AppendLine($"++_vs_cov[{defCov}];");
                    Statement(code, defaultItem.GetProperty("body"), ref unique, trackBudget, useMemLog, coverage);
                    code.AppendLine("}");
                }
                code.AppendLine("}");
                break;
            }
        }
    }
    private void ProcessStatement(StringBuilder code,JsonElement body,ref int unique,bool trackBudget=true,bool useMemLog=false,List<(string Id, string Kind)>? coverage=null)
    {
        if(Kind(body) is "process" or "parallel"||!Statements(body).Any(e=>Kind(e) is "blocking" or "allocate" or "delete" or "queue_push" or "queue_pop" or "assoc_traverse")){Statement(code,body,ref unique,trackBudget,useMemLog,coverage);return;}
        var process=new StringBuilder();Statement(process,body,ref unique,trackBudget,useMemLog,coverage);
        // Rewrite generated numeric storage accesses, never HDL source or identifiers.
        code.AppendLine("{auto local=s;auto localMem=mem;auto localAssoc=assoc;");code.Append(process.ToString().Replace("s[","local[",StringComparison.Ordinal).Replace("mem[","localMem[",StringComparison.Ordinal).Replace("assoc[","localAssoc[",StringComparison.Ordinal));code.AppendLine("}");
    }
    private void EventEvaluation(StringBuilder code, List<(string Id, string Kind)>? coverage = null)
    {
        code.AppendLine("settle();auto next=s;auto nextMem=mem;auto nextAssoc=assoc;"); int unique=0; ProcessStatement(code,Body,ref unique,coverage:coverage);
        code.AppendLine("s=std::move(next);mem=std::move(nextMem);assoc=std::move(nextAssoc);settle();");
        foreach(string signal in EventSignals){int id=SignalId(signal);code.AppendLine($"previous_events[{id}]=s[{id}];");}
    }
    private void EmitFastEventEvaluation(StringBuilder code, int[] seqIds, bool hasFixedMemWrites, bool trackBudget, List<(string Id, string Kind)>? coverage = null)
    {
        var procs = Kind(Body) == "parallel" ? Body.GetProperty("items").EnumerateArray().ToArray() : [Body];
        code.AppendLine("settle();");
        for (int p = 0; p < procs.Length; ++p)
        {
            string cond = string.Join("||", procs[p].GetProperty("events").EnumerateArray().Select(trigger =>
            {
                int id = SignalId(trigger.GetProperty("signal").GetString()!);
                return $"edge_event(previous_events[{id}],s[{id}],{(trigger.GetProperty("edge").GetString() == "posedge" ? "true" : "false")})";
            }));
            code.AppendLine($"const bool _vs_ev{p}={cond};");
        }
        string anyEv = string.Join("||", Enumerable.Range(0, procs.Length).Select(p => $"_vs_ev{p}"));
        code.AppendLine($"if({anyEv}){{");
        code.AppendLine("auto& next=_vs_next;");
        if (seqIds.Length > 0)
            code.AppendLine(string.Concat(seqIds.Select(id => $"next[{id}]=s[{id}];")));
        int unique = 0;
        for (int p = 0; p < procs.Length; ++p)
        {
            code.AppendLine($"if(_vs_ev{p}){{");
            Statement(code, procs[p].GetProperty("body"), ref unique, trackBudget, hasFixedMemWrites, coverage);
            code.AppendLine("}");
        }
        if (seqIds.Length > 0)
            code.AppendLine(string.Concat(seqIds.Select(id => $"s[{id}]=next[{id}];")));
        if (hasFixedMemWrites)
            code.AppendLine("_vs_mlog.commit(mem);");
        code.AppendLine("settle();\n}");
        foreach (string signal in EventSignals) { int id = SignalId(signal); code.AppendLine($"previous_events[{id}]=s[{id}];"); }
    }
    public IReadOnlyDictionary<string, string> ExternalPortNames()
    {
        var reserved = ("alignas alignof and and_eq asm atomic_cancel atomic_commit atomic_noexcept auto bitand bitor bool break case catch char char8_t char16_t char32_t class compl concept const consteval constexpr constinit const_cast continue co_await co_return co_yield decltype default delete do double dynamic_cast else enum explicit export extern false float for friend goto if inline int long mutable namespace new noexcept not not_eq nullptr operator or or_eq private protected public reflexpr register reinterpret_cast requires return short signed sizeof static static_assert static_cast struct switch synchronized template this thread_local throw true try typedef typeid typename union unsigned using virtual void volatile wchar_t while xor xor_eq eval final evaluate settle previous_clock s mem time timeInc name signals get put lifetime checkThread useVpi loadVpi gotFinish finish evalNeeded requestEval started state std vsrtl Model Port W Signal word mask resize unknown select_bit select_part select_indexed deposit_indexed parse text truth yes logical_not logical_and logical_or bit_not bit_binary exact equal compare shift reduce product conditional arithmetic concat read write Bits signed_resize signed_compare signed_shift signed_product negative previous_events edge_event two_state memory_index execution_budget execution_step repeat_count array_index dynamic_allocate dynamic_read queue_push queue_pop queue_delete queue_write assoc_read assoc_write assoc_delete assoc_exists assoc_traverse assoc_find assoc_key assoc localAssoc nextAssoc AssociativeStorage lit lit256 lit512 MemLog wildeq wildneq casez_match casex_match coveragePoints writeCoverageJson CoveragePoint").Split(' ').ToHashSet(StringComparer.Ordinal);
        reserved.UnionWith("scheduler eventsPending nextTimeSlot execute engine frame candidate nextS nextMem drives HdlScheduler".Split(' '));
        var used = Signals.Select(s => s.Name).ToHashSet(StringComparer.Ordinal); var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var signal in Signals.Where(s => s.Direction != "internal"))
        {
            string field = signal.Name;
            if (reserved.Contains(field) || field.StartsWith('_') || field.Contains("__", StringComparison.Ordinal)) { field = "port_" + field.TrimStart('_'); while (field.Contains("__", StringComparison.Ordinal)) field = field.Replace("__", "_", StringComparison.Ordinal); while (used.Contains(field) || reserved.Contains(field)) field = "port_" + field; }
            used.Add(field); result.Add(signal.Name, field);
        }
        return result;
    }
    public string EmitModelHeader(string top, RtlStateMode stateMode = RtlStateMode.FourState, bool enableCoverage = false)
    {
        if (!Identifier(top) || ("V" + top).Contains("__", StringComparison.Ordinal)) throw new ArgumentException("Invalid external model top name.", nameof(top));
        if (Timed)
        {
            if (stateMode == RtlStateMode.TwoState)
                throw new IrValidationException("Two-state mode currently supports non-timed <=64-bit event-driven and combinational designs without dynamic, queue, associative, or multidimensional arrays.");
            return EmitTimedModelHeader(top);
        }
        var names = ExternalPortNames(); string className = "V" + top;
        // Avoid a legal HDL port hiding the generated C++ constructor's class name.
        if (names.Values.Contains(className)) throw new ArgumentException("Port conflicts with generated class name.", nameof(top));
        var dpiCalls = CollectDpiCalls();
        bool hasRuntimeLoops = StatementsHaveRuntimeLoops(SettledSteps.Append(Body));
        bool canThrow = CanEvaluateThrow();
        bool hasBlocking = Statements(Body).Any(e => Kind(e) is "blocking" or "allocate" or "delete" or "queue_push" or "queue_pop" or "assoc_traverse");
        bool hasContainers = Memories.Any(m => m.Associative || m.Queue || m.Dynamic);
        bool useFastEvent = EventDriven && !hasBlocking && !hasContainers;
        int[] seqIds = useFastEvent
            ? Statements(Body).Where(e => Kind(e) == "nba" && Kind(e.GetProperty("target")) != "index")
                .SelectMany(e => TargetLeaves(e.GetProperty("target"))).Select(t => SignalId(TargetOwner(t))).Distinct().Order().ToArray()
            : [];
        bool hasFixedMemWrites = useFastEvent && Statements(Body).Any(e => Kind(e) == "nba" && Kind(e.GetProperty("target")) == "index");
        if (stateMode == RtlStateMode.TwoState)
        {
            if (!CanUseTwoState64(useFastEvent))
                throw new IrValidationException("Two-state mode currently supports non-timed <=64-bit event-driven and combinational designs without dynamic, queue, associative, or multidimensional arrays.");
            return EmitTwoState64ModelHeader(top, names, seqIds, hasFixedMemWrites, hasRuntimeLoops, canThrow, dpiCalls, enableCoverage);
        }
        if (CanUseNarrow64(useFastEvent))
            return EmitNarrow64ModelHeader(top, names, seqIds, hasFixedMemWrites, hasRuntimeLoops, canThrow, dpiCalls, enableCoverage);

        bool hasInit = Statements(Initialization).Any(st => Kind(st) is not "block");
        bool needsAssoc = !useFastEvent || hasInit || Memories.Any(m => m.Associative);
        var coverage = enableCoverage ? new List<(string Id, string Kind)>() : null;

        var bodyCode = new StringBuilder();
        bodyCode.AppendLine("inline void settle(){");
        EmitSettle(bodyCode, coverage);
        bodyCode.AppendLine("}\nvoid evaluate() override {");
        if (dpiCalls.Count > 0) bodyCode.AppendLine("struct _VsDpiScope{svScope prev;explicit _VsDpiScope(svScope s):prev(svSetScope(s)){}~_VsDpiScope(){svSetScope(prev);}} _vs_dpi_scope(static_cast<svScope>(this));");
        if (hasRuntimeLoops || !useFastEvent || SettledSteps.Any(st => !(Kind(st) == "blocking" && Kind(st.GetProperty("target")) == "ref"))) bodyCode.AppendLine("execution_budget=20000;");
        if (canThrow)
        {
            bodyCode.AppendLine($"auto saved_s=s;auto saved_mem=mem;{(needsAssoc?"auto saved_assoc=assoc;":"")}W saved_events[{Signals.Count}];for(int i=0;i<{Signals.Count};++i)saved_events[i]=previous_events[i];{(!CombinationalOnly&&!EventDriven?"auto saved_clock=previous_clock;":"")}try{{");
        }
        if (CombinationalOnly) bodyCode.AppendLine("settle();");
        else if (useFastEvent) EmitFastEventEvaluation(bodyCode, seqIds, hasFixedMemWrites, hasRuntimeLoops, coverage);
        else if (EventDriven) EventEvaluation(bodyCode, coverage);
        else
        {
            bodyCode.AppendLine($"auto clock=s[{SignalId(Clock!)}];if(has_xz(clock))throw std::runtime_error(\"external clock must be known0/1\");settle();\nif(!previous_clock&&clock.v){{auto next=s;auto nextMem=mem;auto nextAssoc=assoc;");
            int unique = 0; ProcessStatement(bodyCode, Body, ref unique, coverage: coverage);
            bodyCode.AppendLine("s=std::move(next);mem=std::move(nextMem);assoc=std::move(nextAssoc);settle();}\nprevious_clock=clock.v!=0;");
        }
        if (canThrow)
        {
            bodyCode.AppendLine($"\n}}catch(...){{s=std::move(saved_s);mem=std::move(saved_mem);{(needsAssoc?"assoc=std::move(saved_assoc);":"")}for(int i=0;i<{Signals.Count};++i)previous_events[i]=saved_events[i];{(!CombinationalOnly&&!EventDriven?"previous_clock=saved_clock;":"")}{(hasFixedMemWrites?"_vs_mlog.count=0;_vs_mlog.overflow.clear();":"")}throw;}}");
        }
        bodyCode.AppendLine("}\npublic:");
        EmitCoveragePointsOverride(bodyCode, coverage);
        foreach (var signal in Signals.Where(s => s.Direction != "internal")) bodyCode.AppendLine($"vsrtl::Port {names[signal.Name]};");
        bodyCode.Append($"{className}():vsrtl::Model(\"{top}\",{{");
        bodyCode.AppendJoin(',', Signals.Select(s => $"{{\"{s.SourcePath ?? s.Name}\",{s.Width},{(s.Direction == "input" ? "vpiInput" : s.Direction == "output" ? "vpiOutput" : "vpiNoDirection")},{(s.Signed?"true":"false")},{(s.TwoState?"true":"false")}}}"));
        bodyCode.Append($"}},{(CombinationalOnly ? -1 : SignalId(Clock!))})");
        foreach (var signal in Signals.Where(s => s.Direction != "internal")) bodyCode.Append($",{names[signal.Name]}(*this,{SignalId(signal.Name)})");
        bodyCode.AppendLine($"{{{(needsAssoc?$"assoc.resize({Memories.Count});":"")}mem={{"); bodyCode.AppendJoin(',', Memories.Select(m => $"std::vector<W>({((m.Dynamic||m.Queue||m.Associative)?0:m.Depth)},{(m.TwoState?$"word(0,0,0,{m.Width})":$"unknown({m.Width})")})"));
        bodyCode.AppendLine("};");
        if (hasInit)
            EmitInitialization(bodyCode);
        bodyCode.AppendLine($"for(int i=0;i<{Signals.Count};++i)previous_events[i]=unknown(1);");
        if(!CombinationalOnly)bodyCode.AppendLine($"previous_events[{SignalId(Clock!)}]=lit(0ULL,1);");
        foreach(var signal in Signals.Where(s=>s.TwoState&&s.Width==1))bodyCode.AppendLine($"previous_events[{SignalId(signal.Name)}]=lit(0ULL,1);");
        bodyCode.AppendLine("}\n};");

        var code = new StringBuilder("#pragma once\n#include \"external_model.h\"\nusing namespace vsrtl;\n");
        EmitDpiPrototypes(code, dpiCalls);
        code.AppendLine($"class {className} final : public vsrtl::Model {{");
        if (!CombinationalOnly && !EventDriven) code.AppendLine("bool previous_clock=false;");
        code.AppendLine("size_t execution_budget=20000;");
        code.AppendLine($"W previous_events[{Signals.Count}];");
        if (useFastEvent) code.AppendLine($"W _vs_next[{Signals.Count}];");
        if (hasFixedMemWrites) code.AppendLine("MemLog<16> _vs_mlog;");
        if (needsAssoc) code.AppendLine("std::vector<AssociativeStorage>assoc;");
        if (coverage is { Count: > 0 }) code.AppendLine($"uint64_t _vs_cov[{coverage.Count}]{{}};");
        code.Append(bodyCode);
        return code.ToString();
    }
    public string EmitCpp()
    {
        if (Timed) throw new IrValidationException("Timed processes currently require the external SDK explicit-time host; cycle-row stimulus is not a time specification.");
        var code = new StringBuilder("#include \"runtime.h\"\n#include <iostream>\n#include <fstream>\n#include <sstream>\nusing namespace vsrtl;\nint main(int argc,char**argv){try{if(argc!=3)throw std::runtime_error(\"stimulus and workers required\");int workers=std::stoi(argv[2]);if(workers<1||workers>256)throw std::runtime_error(\"workers out of range\");\nstd::vector<W>s{\n");
        code.AppendJoin(',', Signals.Select(s => s.TwoState?$"word(0,0,0,{s.Width})":$"unknown({s.Width})")); code.AppendLine("};std::vector<std::vector<W>>mem{"); code.AppendJoin(',', Memories.Select(m => $"std::vector<W>({((m.Dynamic||m.Queue||m.Associative)?0:m.Depth)},{(m.TwoState?$"word(0,0,0,{m.Width})":$"unknown({m.Width})")})")); code.AppendLine($"}};std::vector<AssociativeStorage>assoc({Memories.Count});size_t execution_budget=20000;");EmitInitialization(code);code.AppendLine("auto settle=[&](){");
        EmitSettle(code); code.AppendLine("};");
        if(EventDriven){code.AppendLine($"std::vector<W> previous_events({Signals.Count},unknown(1));previous_events[{SignalId(Clock!)}]=word(0,0,0,1);");foreach(var signal in Signals.Where(s=>s.TwoState&&s.Width==1))code.AppendLine($"previous_events[{SignalId(signal.Name)}]=word(0,0,0,1);");code.AppendLine("auto evaluate=[&](){execution_budget=20000;");EventEvaluation(code);code.AppendLine("};");}
        code.AppendLine("std::ifstream input(argv[1]);if(!input)throw std::runtime_error(\"stimulus missing\");std::string line;int cycle=0;while(std::getline(input,line)){execution_budget=20000;if(++cycle>100000)throw std::runtime_error(\"cycle budget\");std::istringstream row(line);std::string value;");
        foreach (var signal in Signals.Where(s => s.Direction == "input" && s.Name != Clock)) code.AppendLine($"if(!(row>>value)||value.size()!={signal.Width})throw std::runtime_error(\"invalid input width\");s[{SignalId(signal.Name)}]={(signal.TwoState?"two_state(parse(value))":"parse(value)")};");
        if(CombinationalOnly)code.AppendLine("if(row>>value)throw std::runtime_error(\"extra input\");settle();std::cout<<\"VS_RTL \"<<cycle;");
        else if(EventDriven)code.AppendLine($"if(row>>value)throw std::runtime_error(\"extra input\");s[{SignalId(Clock!)}]=W{{0,0,0,1}};evaluate();s[{SignalId(Clock!)}]=W{{1,0,0,1}};evaluate();std::cout<<\"VS_RTL \"<<cycle;");
        else{code.AppendLine($"if(row>>value)throw std::runtime_error(\"extra input\");s[{SignalId(Clock!)}]=W{{0,0,0,1}};settle();s[{SignalId(Clock!)}]=W{{1,0,0,1}};auto next=s;auto nextMem=mem;auto nextAssoc=assoc;");int unique=0;ProcessStatement(code,Body,ref unique);code.AppendLine("s=std::move(next);mem=std::move(nextMem);assoc=std::move(nextAssoc);settle();std::cout<<\"VS_RTL \"<<cycle;");}
        foreach (var signal in Signals.Where(s => s.Direction == "output")) code.AppendLine($"std::cout<<' '<<text(s[{SignalId(signal.Name)}]);");
        return code.AppendLine("std::cout<<'\\n';}return 0;}catch(const std::exception&e){std::cerr<<e.what()<<'\\n';return 1;}}\n").ToString();
    }
}
