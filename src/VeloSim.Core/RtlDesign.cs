using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace VeloSim;

public sealed record RtlSignal(string Name, int Width, string Direction);
public sealed record RtlMemory(string Name, int Width, int Depth);
public sealed class RtlDesign
{
    public IReadOnlyList<RtlSignal> Signals { get; }
    public IReadOnlyList<RtlMemory> Memories { get; }
    public IReadOnlyList<JsonElement> Continuous { get; }
    public JsonElement Body { get; }
    public string Clock { get; }
    public string Json { get; }
    private readonly Dictionary<string, int> signalIds;
    private readonly Dictionary<string, int> memoryIds;
    private int nodes;
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
        Signals = root.GetProperty("signals").EnumerateArray().Select(e => { JsonShape.Fields(e, ["name", "width", "direction"]); return new RtlSignal(Name(e), e.GetProperty("width").GetInt32(), e.GetProperty("direction").GetString()!); }).ToArray();
        Memories = root.GetProperty("memories").EnumerateArray().Select(e => { JsonShape.Fields(e, ["name", "width", "depth"]); return new RtlMemory(Name(e), e.GetProperty("width").GetInt32(), e.GetProperty("depth").GetInt32()); }).ToArray();
        if (Signals.Count is < 1 or > 256 || Memories.Count > 16 || Signals.Any(s => !Identifier(s.Name) || s.Width is < 1 or > 64 || s.Direction is not ("input" or "output" or "internal")) || Memories.Any(m => !Identifier(m.Name) || m.Width is < 1 or > 64 || m.Depth is < 2 or > 256) || Signals.Select(s => s.Name).Concat(Memories.Select(m => m.Name)).Distinct().Count() != Signals.Count + Memories.Count) throw new IrValidationException("Invalid RTL declarations.");
        signalIds = Signals.Select((s, i) => (s.Name, i)).ToDictionary(s => s.Name, s => s.i, StringComparer.Ordinal); memoryIds = Memories.Select((m, i) => (m.Name, i)).ToDictionary(m => m.Name, m => m.i, StringComparer.Ordinal);
        Clock = root.GetProperty("clock").GetString()!; var clock = Signals[SignalId(Clock)];
        if (clock.Width != 1 || clock.Direction != "input") throw new IrValidationException("Clock must be a scalar input.");
        Body = root.GetProperty("body").Clone(); var sequential = new HashSet<string>(); ValidateStatement(Body, sequential, 0);
        var continuous = root.GetProperty("continuous").EnumerateArray().Select(e => e.Clone()).ToList(); if (continuous.Count > 256) throw new IrValidationException("Too many continuous assignments.");
        var pending = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var assignment in continuous)
        {
            JsonShape.Fields(assignment, ["target", "value"]); var target = assignment.GetProperty("target"); ValidateExpression(target, 0); ValidateExpression(assignment.GetProperty("value"), 0);
            if (References(assignment.GetProperty("value")).Contains(Clock)) throw new IrValidationException("Clock-dependent continuous logic is outside the race-free single-clock subset.");
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
        Continuous = sorted.AsReadOnly(); Json = json;
    }
    public static RtlDesign Parse(string json)
    {
        try { return new(json); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException or ArgumentException && e is not IrValidationException) { throw new IrValidationException("Malformed RTL IR: " + e.Message); }
    }
    private void Budget(int depth) { if (depth > 64 || ++nodes > 20000) throw new IrValidationException("RTL node/depth budget exceeded."); }
    private int ValidateExpression(JsonElement e, int depth)
    {
        Budget(depth); string kind = Kind(e);
        if (kind == "ref") { JsonShape.Fields(e, ["kind", "name"]); return Signals[SignalId(Name(e))].Width; }
        if (kind == "const") { JsonShape.Fields(e, ["kind", "bits"]); return FourStateCodec.ParseBinary(e.GetProperty("bits").GetString()!).Width; }
        if (kind == "index") { JsonShape.Fields(e, ["kind", "name", "index"]); ValidateExpression(e.GetProperty("index"), depth + 1); return Memories[MemoryId(Name(e))].Width; }
        JsonShape.Fields(e, ["kind", "args"]); var args = e.GetProperty("args").EnumerateArray().ToArray();
        if (kind is not ("add" or "sub" or "and" or "not" or "eq" or "concat") || args.Length != (kind == "not" ? 1 : 2)) throw new IrValidationException("Unsupported RTL expression.");
        var widths = args.Select(a => ValidateExpression(a, depth + 1)).ToArray(); var result = kind is "and" or "not" or "eq" ? 1 : kind == "concat" ? widths.Sum() : widths.Max(); if (result > 64) throw new IrValidationException("Expression exceeds 64 bits."); return result;
    }
    private IEnumerable<string> References(JsonElement e)
    {
        if (Kind(e) == "ref") yield return Name(e);
        else if (Kind(e) == "index") { foreach (var n in References(e.GetProperty("index"))) yield return n; }
        else if (e.TryGetProperty("args", out var args)) foreach (var a in args.EnumerateArray()) foreach (var n in References(a)) yield return n;
    }
    private void ValidateStatement(JsonElement e, HashSet<string> writers, int depth)
    {
        Budget(depth);
        switch (Kind(e))
        {
            case "block": JsonShape.Fields(e, ["kind", "items"]); foreach (var item in e.GetProperty("items").EnumerateArray()) ValidateStatement(item, writers, depth + 1); break;
            case "nba":
                JsonShape.Fields(e, ["kind", "target", "value"]); var target = e.GetProperty("target"); ValidateExpression(target, depth + 1); ValidateExpression(e.GetProperty("value"), depth + 1);
                if (Kind(target) is not ("ref" or "index") || Kind(target) == "ref" && (Name(target) == Clock || Signals[SignalId(Name(target))].Direction == "input")) throw new IrValidationException("Invalid sequential assignment target."); writers.Add(Name(target)); break;
            case "if": JsonShape.Fields(e, ["kind", "condition", "then", "else"]); ValidateExpression(e.GetProperty("condition"), depth + 1); ValidateStatement(e.GetProperty("then"), writers, depth + 1); ValidateStatement(e.GetProperty("else"), writers, depth + 1); break;
            case "case":
                JsonShape.Fields(e, ["kind", "value", "items"]); ValidateExpression(e.GetProperty("value"), depth + 1); int defaults = 0;
                foreach (var item in e.GetProperty("items").EnumerateArray()) { JsonShape.Fields(item, ["match", "body"]); var match = item.GetProperty("match"); if (match.ValueKind == JsonValueKind.Null) defaults++; else { if (Kind(match) != "const") throw new IrValidationException("Only constant case labels supported."); ValidateExpression(match, depth + 1); } ValidateStatement(item.GetProperty("body"), writers, depth + 1); }
                if (defaults > 1) throw new IrValidationException("Duplicate default case."); break;
            default: throw new IrValidationException("Unsupported RTL statement.");
        }
    }
    private int ExpressionWidth(JsonElement e) => Kind(e) switch
    {
        "ref" => Signals[SignalId(Name(e))].Width, "index" => Memories[MemoryId(Name(e))].Width, "const" => e.GetProperty("bits").GetString()!.Length,
        "and" or "eq" or "not" => 1, "concat" => e.GetProperty("args").EnumerateArray().Sum(ExpressionWidth), _ => e.GetProperty("args").EnumerateArray().Max(ExpressionWidth)
    };
    private string Expression(JsonElement e, int context = 0)
    {
        string kind = Kind(e);
        if (kind == "ref") return $"s[{SignalId(Name(e))}]";
        if (kind == "const") return "parse(\"" + e.GetProperty("bits").GetString() + "\")";
        if (kind == "index") { int id = MemoryId(Name(e)); return $"read(mem[{id}],{Expression(e.GetProperty("index"))},{Memories[id].Width})"; }
        var args = e.GetProperty("args").EnumerateArray().ToArray(); int width = Math.Max(context, ExpressionWidth(e));
        return kind switch
        {
            "add" or "sub" => $"arithmetic({Expression(args[0], width)},{Expression(args[1], width)},{width},{(kind == "sub" ? "true" : "false")})",
            "not" => $"logical_not({Expression(args[0])})", "and" => $"logical_and({Expression(args[0])},{Expression(args[1])})",
            "eq" => $"equal({Expression(args[0], args.Max(ExpressionWidth))},{Expression(args[1], args.Max(ExpressionWidth))})", "concat" => $"concat({Expression(args[0])},{Expression(args[1])})", _ => throw new IrValidationException("Unsupported expression.")
        };
    }
    private void Statement(StringBuilder code, JsonElement e, ref int unique)
    {
        switch (Kind(e))
        {
            case "block": foreach (var item in e.GetProperty("items").EnumerateArray()) Statement(code, item, ref unique); break;
            case "nba":
                var target = e.GetProperty("target");
                if (Kind(target) == "ref") { int id = SignalId(Name(target)); code.AppendLine($"next[{id}]=resize({Expression(e.GetProperty("value"), Signals[id].Width)},{Signals[id].Width});"); }
                else { int id = MemoryId(Name(target)); code.AppendLine($"write(nextMem[{id}],{Expression(target.GetProperty("index"))},{Expression(e.GetProperty("value"), Memories[id].Width)},{Memories[id].Width});"); } break;
            case "if": code.AppendLine($"if(yes({Expression(e.GetProperty("condition"))})){{"); Statement(code, e.GetProperty("then"), ref unique); code.AppendLine("}else{"); Statement(code, e.GetProperty("else"), ref unique); code.AppendLine("}"); break;
            case "case":
                var label = "case" + unique++; var items = e.GetProperty("items").EnumerateArray().ToArray(); int caseWidth = Math.Max(ExpressionWidth(e.GetProperty("value")), items.Where(i => i.GetProperty("match").ValueKind != JsonValueKind.Null).Select(i => ExpressionWidth(i.GetProperty("match"))).DefaultIfEmpty(1).Max()); code.AppendLine($"{{auto {label}={Expression(e.GetProperty("value"), caseWidth)};"); bool first = true;
                foreach (var item in items.Where(i => i.GetProperty("match").ValueKind != JsonValueKind.Null)) { code.AppendLine($"{(first ? "if" : "else if")}(exact({label},{Expression(item.GetProperty("match"))})){{"); Statement(code, item.GetProperty("body"), ref unique); code.AppendLine("}"); first = false; }
                var defaultItem = items.FirstOrDefault(i => i.GetProperty("match").ValueKind == JsonValueKind.Null); if (defaultItem.ValueKind != JsonValueKind.Undefined) { code.AppendLine(first ? "{" : "else{"); Statement(code, defaultItem.GetProperty("body"), ref unique); code.AppendLine("}"); } code.AppendLine("}"); break;
        }
    }
    public string EmitCpp()
    {
        var code = new StringBuilder("#include \"runtime.h\"\n#include <iostream>\n#include <fstream>\n#include <sstream>\nusing namespace vsrtl;\nint main(int argc,char**argv){try{if(argc!=3)throw std::runtime_error(\"stimulus and workers required\");int workers=std::stoi(argv[2]);if(workers<1||workers>256)throw std::runtime_error(\"workers out of range\");\nstd::vector<W>s{\n");
        code.AppendJoin(',', Signals.Select(s => $"unknown({s.Width})")); code.AppendLine("};std::vector<std::vector<W>>mem{"); code.AppendJoin(',', Memories.Select(m => $"std::vector<W>({m.Depth},unknown({m.Width}))")); code.AppendLine("};auto settle=[&](){");
        foreach (var assignment in Continuous) { int id = SignalId(Name(assignment.GetProperty("target"))); code.AppendLine($"s[{id}]=resize({Expression(assignment.GetProperty("value"), Signals[id].Width)},{Signals[id].Width});"); } code.AppendLine("};\nstd::ifstream input(argv[1]);if(!input)throw std::runtime_error(\"stimulus missing\");std::string line;int cycle=0;while(std::getline(input,line)){if(++cycle>100000)throw std::runtime_error(\"cycle budget\");std::istringstream row(line);std::string value;");
        foreach (var signal in Signals.Where(s => s.Direction == "input" && s.Name != Clock)) code.AppendLine($"if(!(row>>value)||value.size()!={signal.Width})throw std::runtime_error(\"invalid input width\");s[{SignalId(signal.Name)}]=parse(value);");
        code.AppendLine($"if(row>>value)throw std::runtime_error(\"extra input\");s[{SignalId(Clock)}]=W{{0,0,0,1}};settle();s[{SignalId(Clock)}]=W{{1,0,0,1}};auto next=s;auto nextMem=mem;"); int unique = 0; Statement(code, Body, ref unique); code.AppendLine("s=std::move(next);mem=std::move(nextMem);settle();std::cout<<\"VS_RTL \"<<cycle;");
        foreach (var signal in Signals.Where(s => s.Direction == "output")) code.AppendLine($"std::cout<<' '<<text(s[{SignalId(signal.Name)}]);");
        return code.AppendLine("std::cout<<'\\n';}return 0;}catch(const std::exception&e){std::cerr<<e.what()<<'\\n';return 1;}}\n").ToString();
    }
}
