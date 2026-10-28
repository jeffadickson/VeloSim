using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace VeloSim;

public sealed record RtlSignal(string Name, int Width, string Direction, string? SourcePath = null, bool Signed = false, bool TwoState = false);
/// <summary>A fixed unpacked dimension preserving declared bounds; storage offsets ascend from the smaller bound.</summary>
public sealed record RtlDimension(int Left,int Right);
/// <summary>Word storage with optional fixed shape, dynamic/queue lifetime or typed sparse associative keys. Dynamic depth1 metadata denotes initially empty storage; two-state cells default to zero.</summary>
public sealed record RtlMemory(string Name, int Width, int Depth, bool Signed = false, int LowerBound = 0, IReadOnlyList<RtlDimension>? Dimensions = null, bool TwoState = false, bool Dynamic = false, bool Queue = false, bool Associative = false, int KeyWidth = 0, bool KeySigned = false);
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
    public bool EventDriven => Kind(Body) == "process" || Kind(Body) == "parallel" && Body.GetProperty("items").EnumerateArray().Any(p => Kind(p) == "process");
    private IEnumerable<string> EventSignals => (Kind(Body) == "parallel" ? Body.GetProperty("items").EnumerateArray().ToArray() : [Body]).Where(p => Kind(p) == "process").SelectMany(p => p.GetProperty("events").EnumerateArray()).Select(e => e.GetProperty("signal").GetString()!).Distinct();
    private readonly Dictionary<string, int> signalIds;
    private readonly Dictionary<string, int> memoryIds;
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
        JsonShape.Fields(root, ["format", "signals", "memories", "continuous", "clock", "body"], ["combinational", "initialization", "timed", "time_precision", "named_events"]);
        if (root.GetProperty("format").GetString() != "velosim.rtl.v1") throw new IrValidationException("Unsupported RTL IR.");
        Signals = root.GetProperty("signals").EnumerateArray().Select(e => { JsonShape.Fields(e, ["name", "width", "direction"], ["path", "signed", "two_state"]); return new RtlSignal(Name(e), e.GetProperty("width").GetInt32(), e.GetProperty("direction").GetString()!, e.TryGetProperty("path", out var path) ? path.GetString() : null, e.TryGetProperty("signed",out var sign)&&sign.GetBoolean(),e.TryGetProperty("two_state",out var two)&&two.GetBoolean()); }).ToArray();
        Memories = root.GetProperty("memories").EnumerateArray().Select(ParseMemory).ToArray();
        if (Signals.Count is < 1 or > 256 || Memories.Count > 16 || Signals.Any(s => !Identifier(s.Name) || s.Width is < 1 or > 256 || s.Direction is not ("input" or "output" or "internal")) || Memories.Any(m => !Identifier(m.Name) || m.Width is < 1 or > 256 || m.Depth is < 1 or > 256 || m.LowerBound<0 || (long)m.LowerBound+m.Depth-1>int.MaxValue) || Signals.Select(s => s.Name).Concat(Memories.Select(m => m.Name)).Distinct().Count() != Signals.Count + Memories.Count) throw new IrValidationException("Invalid RTL declarations.");
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
        var continuous = root.GetProperty("continuous").EnumerateArray().Select(e => e.Clone()).ToList(); if (continuous.Count > 256) throw new IrValidationException("Too many continuous assignments.");
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
    public static RtlDesign Parse(string json)
    {
        try { return new(json); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException or FormatException or ArgumentException && e is not IrValidationException) { throw new IrValidationException("Malformed RTL IR: " + e.Message); }
    }
    private void Budget(int depth) { if (depth > 64 || ++nodes > 20000) throw new IrValidationException("RTL node/depth budget exceeded."); }
    private void ValidateIndex(JsonElement e,int depth){ValidateExpression(e,depth);if(ExpressionSigned(e)&&!(Kind(e)=="const"&&e.GetProperty("bits").GetString()![0]=='0'))throw new IrValidationException("Signed indices require an explicit unsigned cast.");}
    private int ValidateExpression(JsonElement e, int depth)
    {
        Budget(depth); string kind = Kind(e);
        if (kind == "convert") return ValidateConversion(e,depth);
        if(kind=="exists"){JsonShape.Fields(e,["kind","name","index"]);RequireAssociative(Name(e));ValidateExpression(e.GetProperty("index"),depth+1);return 32;}
        if (kind == "size") { JsonShape.Fields(e,["kind","name"]); RequireContainer(Name(e)); return 32; }
        if (kind == "ref") { JsonShape.Fields(e, ["kind", "name"]); return Signals[SignalId(Name(e))].Width; }
        if (kind == "const") { JsonShape.Fields(e, ["kind", "bits"], ["signed"]); if(e.TryGetProperty("signed",out var sign))_=sign.GetBoolean(); var bits=e.GetProperty("bits").GetString();if(string.IsNullOrEmpty(bits)||bits.Length>256||bits.Any(c=>c is not ('0' or '1' or 'x' or 'z')))throw new IrValidationException("Invalid RTL constant.");return bits.Length; }
        if (kind == "index") return ValidateMemoryAccess(e,depth);
        if (kind == "bitselect") { JsonShape.Fields(e, ["kind", "value", "index"]); ValidateExpression(e.GetProperty("value"), depth + 1); ValidateIndex(e.GetProperty("index"), depth + 1); return 1; }
        if (kind == "partselect") { JsonShape.Fields(e, ["kind", "value", "left", "right"]); ValidateExpression(e.GetProperty("value"), depth + 1); int left = e.GetProperty("left").GetInt32(), right = e.GetProperty("right").GetInt32(); if (right < 0 || left < right || (long)left - right >= 256) throw new IrValidationException("Only nonnegative descending constant part selects of 1..256 bits supported."); return left - right + 1; }
        if (kind == "indexedselect") { JsonShape.Fields(e, ["kind", "value", "base", "width", "descending"]); ValidateExpression(e.GetProperty("value"), depth + 1); ValidateIndex(e.GetProperty("base"), depth + 1); int width = e.GetProperty("width").GetInt32(); _ = e.GetProperty("descending").GetBoolean(); if (width is < 1 or > 256) throw new IrValidationException("Indexed select width must be 1..256."); return width; }
        JsonShape.Fields(e, ["kind", "args"]); var args = e.GetProperty("args").EnumerateArray().ToArray();
        if (Arity(kind) == 0 || args.Length != Arity(kind)) throw new IrValidationException("Unsupported RTL expression.");
        var widths = args.Select(a => ValidateExpression(a, depth + 1)).ToArray(); var result = Scalar(kind) ? 1 : kind is "shl" or "shr" or "ashr" ? widths[0] : kind == "cond" ? Math.Max(widths[1], widths[2]) : kind == "concat" ? widths.Sum() : widths.Max(); if (result > 256) throw new IrValidationException("Expression exceeds 256 bits."); return result;
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
    private static long StatementCost(JsonElement e)
    {
        long cost=Kind(e) switch {
            "parallel" or "block" => 1+e.GetProperty("items").EnumerateArray().Sum(StatementCost),
            "process" => 1+StatementCost(e.GetProperty("body")),
            "repeat" => 1+Math.Max(1,e.GetProperty("count").GetInt32())*StatementCost(e.GetProperty("body")),
            "while" or "do" or "runtime_repeat" => 1+StatementCost(e.GetProperty("body")),
            "if" => 1+StatementCost(e.GetProperty("then"))+StatementCost(e.GetProperty("else")),
            "case" => 1+e.GetProperty("items").EnumerateArray().Sum(item=>StatementCost(item.GetProperty("body"))), _=>1};
        return cost<=20000?cost:throw new IrValidationException("Expanded statement execution budget exceeded.");
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
        "size" or "exists"=>32,"convert"=>e.GetProperty("width").GetInt32(), "ref" => Signals[SignalId(Name(e))].Width, "index" => Memories[MemoryId(Name(e))].Width, "const" => e.GetProperty("bits").GetString()!.Length,
        "bitselect" => 1, "partselect" => e.GetProperty("left").GetInt32() - e.GetProperty("right").GetInt32() + 1, "indexedselect" => e.GetProperty("width").GetInt32(),
        var kind when Scalar(kind) => 1, "shl" or "shr" or "ashr" => ExpressionWidth(e.GetProperty("args")[0]), "cond" => e.GetProperty("args").EnumerateArray().Skip(1).Max(ExpressionWidth), "concat" => e.GetProperty("args").EnumerateArray().Sum(ExpressionWidth), _ => e.GetProperty("args").EnumerateArray().Max(ExpressionWidth)
    };
    private bool ExpressionSigned(JsonElement e) => Kind(e) switch
    {
        "size" or "exists"=>true,"convert"=>e.GetProperty("signed").GetBoolean(), "ref"=>Signals[SignalId(Name(e))].Signed,"index"=>Memories[MemoryId(Name(e))].Signed,
        "const"=>e.TryGetProperty("signed",out var sign)&&sign.GetBoolean(),"castsigned"=>true,
        "castunsigned" or "bitselect" or "partselect" or "indexedselect" or "concat"=>false,
        var k when Scalar(k)=>false,"shl" or "shr" or "ashr"=>ExpressionSigned(e.GetProperty("args")[0]),
        "cond"=>e.GetProperty("args").EnumerateArray().Skip(1).All(ExpressionSigned),
        _=>e.GetProperty("args").EnumerateArray().All(ExpressionSigned)
    };
    private string Expression(JsonElement e, int context = 0, bool? forceSigned = null)
    {
        string kind = Kind(e);bool signed=forceSigned??ExpressionSigned(e);
        string Extend(string value)=>signed&&context>ExpressionWidth(e)?$"signed_resize({value},{context})":value;
        if (kind == "convert") return Extend(Conversion(e));
        if (kind == "ref") return Extend($"s[{SignalId(Name(e))}]");
        if(kind=="size"){int id=MemoryId(Name(e));return Extend($"word({(Memories[id].Associative?"assoc":"mem")}[{id}].size(),0,0,32)");}
        if (kind == "const") return Extend("parse(\"" + e.GetProperty("bits").GetString() + "\")");
        if(kind=="exists"){int id=MemoryId(Name(e));return Extend($"assoc_exists(assoc[{id}],{Expression(e.GetProperty("index"),Memories[id].KeyWidth)},{Memories[id].KeyWidth},{(Memories[id].KeySigned?"true":"false")})");}
        if (kind == "index") { int id = MemoryId(Name(e));var memory=Memories[id];var read=memory.Associative?$"assoc_read(assoc[{id}],{MemoryIndex(e,id)},{memory.KeyWidth},{(memory.KeySigned?"true":"false")},{memory.Width},{(memory.TwoState?"true":"false")})":(memory.Dynamic||memory.Queue)?$"dynamic_read(mem[{id}],{MemoryIndex(e,id)},{memory.Width},{(memory.TwoState?"true":"false")})":$"read(mem[{id}],{MemoryIndex(e,id)},{memory.Width})";return Extend(read); }
        if (kind == "bitselect") return $"select_bit({Expression(e.GetProperty("value"))},{Expression(e.GetProperty("index"))})";
        if (kind == "partselect") return $"select_part({Expression(e.GetProperty("value"))},{e.GetProperty("left").GetInt32()},{e.GetProperty("right").GetInt32()})";
        if (kind == "indexedselect") return $"select_indexed({Expression(e.GetProperty("value"))},{Expression(e.GetProperty("base"))},{e.GetProperty("width").GetInt32()},{(e.GetProperty("descending").GetBoolean() ? "true" : "false")})";
        var args = e.GetProperty("args").EnumerateArray().ToArray(); int width = Math.Max(context, ExpressionWidth(e));
        return kind switch
        {
            "castsigned" or "castunsigned"=>Extend(Expression(args[0])),
            "add" or "sub" => $"arithmetic({Expression(args[0], width,signed)},{Expression(args[1], width,signed)},{width},{(kind == "sub" ? "true" : "false")})",
            "bitnot" => $"bit_not({Expression(args[0], width,signed)},{width})",
            "neg" => $"arithmetic(W{{0,0,0,{width}}},{Expression(args[0], width,signed)},{width},true)",
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
            "caseeq" or "caseneq" => $"W{{uint64_t({(kind == "caseneq" ? "!" : "")}exact({Expression(args[0], args.Max(ExpressionWidth),args.All(ExpressionSigned))},{Expression(args[1], args.Max(ExpressionWidth),args.All(ExpressionSigned))})),0,0,1}}",
            "lt" or "le" or "gt" or "ge" => $"{(args.All(ExpressionSigned)?"signed_compare":"compare")}({Expression(args[0], args.Max(ExpressionWidth),args.All(ExpressionSigned))},{Expression(args[1], args.Max(ExpressionWidth),args.All(ExpressionSigned))},{(kind == "lt" ? 0 : kind == "le" ? 1 : kind == "gt" ? 2 : 3)})",
            "concat" => $"concat({Expression(args[0])},{Expression(args[1])})", _ => throw new IrValidationException("Unsupported expression.")
        };
    }
    private void WordWrite(StringBuilder code, JsonElement target, string value)
    {
        if (Kind(target) == "ref") { int id = SignalId(Name(target)); code.AppendLine($"next[{id}]={StoreWord(value,Signals[id])};"); return; }
        int selectedId = SignalId(Name(target.GetProperty("value"))), width = ExpressionWidth(target);
        string basis = Kind(target) == "bitselect" ? Expression(target.GetProperty("index")) : Kind(target) == "indexedselect" ? Expression(target.GetProperty("base")) : $"W{{{target.GetProperty("right").GetInt32()},0,0,64}}";
        bool descending = Kind(target) == "indexedselect" && target.GetProperty("descending").GetBoolean();
        string deposited=$"deposit_indexed(next[{selectedId}],{basis},{width},{(descending ? "true" : "false")},{value})";code.AppendLine($"next[{selectedId}]={(Signals[selectedId].TwoState?"two_state("+deposited+")":deposited)};");
    }
    private static string StoreWord(string value,RtlSignal signal)=>signal.TwoState?$"two_state(resize({value},{signal.Width}))":$"resize({value},{signal.Width})";
    private void Statement(StringBuilder code, JsonElement e, ref int unique)
    {
        code.AppendLine("execution_step(execution_budget);");
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
                code.AppendLine("execution_step(execution_budget);");Statement(code,e.GetProperty("body"),ref unique);
                code.AppendLine(Kind(e)=="do"?$"}}while(yes({Expression(e.GetProperty("condition"))}));":"}");break;
            case "runtime_repeat":
                string runtimeLoop="runtime_repeat"+unique++;code.AppendLine($"for(unsigned {runtimeLoop}=repeat_count({Expression(e.GetProperty("count"))},{(ExpressionSigned(e.GetProperty("count"))?"true":"false")});{runtimeLoop}>0;--{runtimeLoop}){{");
                code.AppendLine("execution_step(execution_budget);");Statement(code,e.GetProperty("body"),ref unique);code.AppendLine("}");break;
            case "repeat":
                string loop="repeat"+unique++;code.AppendLine($"for(int {loop}=0;{loop}<{e.GetProperty("count").GetInt32()};++{loop}){{");Statement(code,e.GetProperty("body"),ref unique);code.AppendLine("}");break;
            case "process":
                code.AppendLine("if(" + string.Join("||", e.GetProperty("events").EnumerateArray().Select(trigger => { int id = SignalId(trigger.GetProperty("signal").GetString()!); return $"edge_event(previous_events[{id}],s[{id}],{(trigger.GetProperty("edge").GetString() == "posedge" ? "true" : "false")})"; })) + "){");
                ProcessStatement(code, e.GetProperty("body"), ref unique); code.AppendLine("}"); break;
            case "parallel": foreach(var item in e.GetProperty("items").EnumerateArray())ProcessStatement(code,item,ref unique);break;
            case "block": foreach (var item in e.GetProperty("items").EnumerateArray()) Statement(code, item, ref unique); break;
            case "blocking": case "nba":
                var target = e.GetProperty("target");
                if (Kind(target) == "concat")
                {
                    int total = ExpressionWidth(target), remaining = total; string rhs = "nba" + unique++;
                    code.AppendLine($"{{auto {rhs}=resize({Expression(e.GetProperty("value"), total)},{total});");
                    foreach (var leaf in TargetLeaves(target)) { int width = ExpressionWidth(leaf); remaining -= width; WordWrite(code, leaf, $"select_part({rhs},{remaining + width - 1},{remaining})"); }
                    code.AppendLine("}");
                }
                else if (Kind(target) is "ref" or "bitselect" or "partselect" or "indexedselect") WordWrite(code, target, Expression(e.GetProperty("value"), ExpressionWidth(target)));
                else { int id = MemoryId(Name(target));var value=Expression(e.GetProperty("value"),Memories[id].Width);if(Memories[id].TwoState)value=$"two_state({value})";code.AppendLine(Memories[id].Associative?$"assoc_write(nextAssoc[{id}],{MemoryIndex(target,id)},{value},{Memories[id].KeyWidth},{(Memories[id].KeySigned?"true":"false")},{Memories[id].Width},{(Memories[id].TwoState?"true":"false")});":Memories[id].Queue?$"queue_write(nextMem[{id}],{MemoryIndex(target,id)},{value},{Memories[id].Width},{(Memories[id].TwoState?"true":"false")});":$"write(nextMem[{id}],{MemoryIndex(target,id)},{value},{Memories[id].Width});"); }
                // Publish local blocking results only after all RHS and target indices have been sampled.
                if (Kind(e)=="blocking") foreach (var leaf in TargetLeaves(target)) {
                    if (Kind(leaf)=="index") { int id=MemoryId(Name(leaf));code.AppendLine(Memories[id].Associative?$"assoc[{id}]=nextAssoc[{id}];":$"mem[{id}]=nextMem[{id}];"); }
                    else { int id=SignalId(TargetOwner(leaf));code.AppendLine($"s[{id}]=next[{id}];"); }
                }
                break;
            case "if": code.AppendLine($"if(yes({Expression(e.GetProperty("condition"))})){{"); Statement(code, e.GetProperty("then"), ref unique); code.AppendLine("}else{"); Statement(code, e.GetProperty("else"), ref unique); code.AppendLine("}"); break;
            case "case":
                var label = "case" + unique++; var items = e.GetProperty("items").EnumerateArray().ToArray(); int caseWidth = Math.Max(ExpressionWidth(e.GetProperty("value")), items.Where(i => i.GetProperty("match").ValueKind != JsonValueKind.Null).Select(i => ExpressionWidth(i.GetProperty("match"))).DefaultIfEmpty(1).Max()); bool caseSigned=ExpressionSigned(e.GetProperty("value"))&&items.Where(i=>i.GetProperty("match").ValueKind!=JsonValueKind.Null).All(i=>ExpressionSigned(i.GetProperty("match"))); code.AppendLine($"{{auto {label}={Expression(e.GetProperty("value"), caseWidth,caseSigned)};"); bool first = true;
                foreach (var item in items.Where(i => i.GetProperty("match").ValueKind != JsonValueKind.Null)) { code.AppendLine($"{(first ? "if" : "else if")}(exact({label},{Expression(item.GetProperty("match"),caseWidth,caseSigned)})){{"); Statement(code, item.GetProperty("body"), ref unique); code.AppendLine("}"); first = false; }
                var defaultItem = items.FirstOrDefault(i => i.GetProperty("match").ValueKind == JsonValueKind.Null); if (defaultItem.ValueKind != JsonValueKind.Undefined) { code.AppendLine(first ? "{" : "else{"); Statement(code, defaultItem.GetProperty("body"), ref unique); code.AppendLine("}"); } code.AppendLine("}"); break;
        }
    }
    private void ProcessStatement(StringBuilder code,JsonElement body,ref int unique)
    {
        if(Kind(body) is "process" or "parallel"||!Statements(body).Any(e=>Kind(e) is "blocking" or "allocate" or "delete" or "queue_push" or "queue_pop" or "assoc_traverse")){Statement(code,body,ref unique);return;}
        var process=new StringBuilder();Statement(process,body,ref unique);
        // Rewrite generated numeric storage accesses, never HDL source or identifiers.
        code.AppendLine("{auto local=s;auto localMem=mem;auto localAssoc=assoc;");code.Append(process.ToString().Replace("s[","local[",StringComparison.Ordinal).Replace("mem[","localMem[",StringComparison.Ordinal).Replace("assoc[","localAssoc[",StringComparison.Ordinal));code.AppendLine("}");
    }
    private void EventEvaluation(StringBuilder code)
    {
        code.AppendLine("settle();auto next=s;auto nextMem=mem;auto nextAssoc=assoc;"); int unique=0; ProcessStatement(code,Body,ref unique);
        code.AppendLine("s=std::move(next);mem=std::move(nextMem);assoc=std::move(nextAssoc);settle();");
        foreach(string signal in EventSignals){int id=SignalId(signal);code.AppendLine($"previous_events[{id}]=s[{id}];");}
    }
    public IReadOnlyDictionary<string, string> ExternalPortNames()
    {
        var reserved = ("alignas alignof and and_eq asm atomic_cancel atomic_commit atomic_noexcept auto bitand bitor bool break case catch char char8_t char16_t char32_t class compl concept const consteval constexpr constinit const_cast continue co_await co_return co_yield decltype default delete do double dynamic_cast else enum explicit export extern false float for friend goto if inline int long mutable namespace new noexcept not not_eq nullptr operator or or_eq private protected public reflexpr register reinterpret_cast requires return short signed sizeof static static_assert static_cast struct switch synchronized template this thread_local throw true try typedef typeid typename union unsigned using virtual void volatile wchar_t while xor xor_eq eval final evaluate settle previous_clock s mem time timeInc name signals get put lifetime checkThread useVpi loadVpi gotFinish finish evalNeeded requestEval started state std vsrtl Model Port W Signal word mask resize unknown select_bit select_part select_indexed deposit_indexed parse text truth yes logical_not logical_and logical_or bit_not bit_binary exact equal compare shift reduce product conditional arithmetic concat read write Bits signed_resize signed_compare signed_shift signed_product negative previous_events edge_event two_state memory_index execution_budget execution_step repeat_count array_index dynamic_allocate dynamic_read queue_push queue_pop queue_delete queue_write assoc_read assoc_write assoc_delete assoc_exists assoc_traverse assoc_find assoc_key assoc localAssoc nextAssoc AssociativeStorage").Split(' ').ToHashSet(StringComparer.Ordinal);
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
    public string EmitModelHeader(string top)
    {
        if (!Identifier(top) || ("V" + top).Contains("__", StringComparison.Ordinal)) throw new ArgumentException("Invalid external model top name.", nameof(top));
        if (Timed) return EmitTimedModelHeader(top);
        var names = ExternalPortNames(); string className = "V" + top;
        // Avoid a legal HDL port hiding the generated C++ constructor's class name.
        if (names.Values.Contains(className)) throw new ArgumentException("Port conflicts with generated class name.", nameof(top));
        var code = new StringBuilder("#pragma once\n#include \"external_model.h\"\nusing namespace vsrtl;\n");
        code.AppendLine($"class {className} final : public vsrtl::Model {{\nbool previous_clock=false;\nsize_t execution_budget=20000;\nstd::vector<W> previous_events;std::vector<AssociativeStorage>assoc;\nvoid settle(){{");
        EmitSettle(code);
        code.AppendLine("}\nvoid evaluate() override { execution_budget=20000;auto saved_s=s;auto saved_mem=mem;auto saved_assoc=assoc;auto saved_events=previous_events;auto saved_clock=previous_clock;try{");
        if(CombinationalOnly)code.AppendLine("settle();");
        else if(EventDriven)EventEvaluation(code);
        else {code.AppendLine($"auto clock=s[{SignalId(Clock!)}];if(clock.x||clock.z)throw std::runtime_error(\"external clock must be known0/1\");settle();\nif(!previous_clock&&clock.v){{auto next=s;auto nextMem=mem;auto nextAssoc=assoc;");int unique=0;ProcessStatement(code,Body,ref unique);code.AppendLine("s=std::move(next);mem=std::move(nextMem);assoc=std::move(nextAssoc);settle();}\nprevious_clock=clock.v!=0;");}
        code.AppendLine("}catch(...){s=std::move(saved_s);mem=std::move(saved_mem);assoc=std::move(saved_assoc);previous_events=std::move(saved_events);previous_clock=saved_clock;throw;}}\npublic:");
        foreach (var signal in Signals.Where(s => s.Direction != "internal")) code.AppendLine($"vsrtl::Port {names[signal.Name]};");
        code.Append($"{className}():vsrtl::Model(\"{top}\",{{");
        code.AppendJoin(',', Signals.Select(s => $"{{\"{s.SourcePath ?? s.Name}\",{s.Width},{(s.Direction == "input" ? "vpiInput" : s.Direction == "output" ? "vpiOutput" : "vpiNoDirection")},{(s.Signed?"true":"false")},{(s.TwoState?"true":"false")}}}"));
        code.Append($"}},{(CombinationalOnly ? -1 : SignalId(Clock!))})");
        foreach (var signal in Signals.Where(s => s.Direction != "internal")) code.Append($",{names[signal.Name]}(*this,{SignalId(signal.Name)})");
        code.AppendLine($"{{assoc.resize({Memories.Count});mem={{"); code.AppendJoin(',', Memories.Select(m => $"std::vector<W>({((m.Dynamic||m.Queue||m.Associative)?0:m.Depth)},{(m.TwoState?$"word(0,0,0,{m.Width})":$"unknown({m.Width})")})"));
        code.AppendLine("};");EmitInitialization(code);code.AppendLine($"previous_events.assign({Signals.Count},unknown(1));");
        if(!CombinationalOnly)code.AppendLine($"previous_events[{SignalId(Clock!)}]=word(0,0,0,1);");
        foreach(var signal in Signals.Where(s=>s.TwoState&&s.Width==1))code.AppendLine($"previous_events[{SignalId(signal.Name)}]=word(0,0,0,1);");
        return code.AppendLine("}\n};").ToString();
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
