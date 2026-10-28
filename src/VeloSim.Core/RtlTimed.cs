using System.Text;
using System.Text.Json;
using System.Globalization;

namespace VeloSim;

public sealed partial class RtlDesign
{
    /// <summary>True for explicit-time resumable processes, requiring an SDK host.</summary>
    public bool Timed => TimedProcesses.Count != 0;
    /// <summary>Decimal seconds exponent for one host tick, when provided by elaboration.</summary>
    public int? TimePrecisionExponent { get; private set; }
    private IReadOnlyList<JsonElement> TimedProcesses { get; set; } = [];
    private IReadOnlyList<string> NamedEvents { get; set; } = [];
    /// <summary>Conservative timed-process hazards, not proof that a race occurs.
    /// Paths and event-time exclusion are intentionally not inferred.</summary>
    public IReadOnlyList<string> TimingDiagnostics { get; private set; } = [];

    // Unrolling bounded repeats can still multiply a large legal expression.
    // Charge both new instructions and rewritten loop tests incrementally,
    // before retaining their text, rather than after allocating the full model.
    private sealed class TimedInstructions
    {
        private readonly List<string> code=[];
        private long characters;
        public int Count=>code.Count;
        private void Charge(int delta)
        {
            if(characters+delta>16*1024*1024)throw new IrValidationException("Timed generated-code budget exceeds 16 MiB.");
            characters+=delta;
        }
        public string this[int index]{get=>code[index];set{Charge(value.Length-code[index].Length);code[index]=value;}}
        public void Add(string instruction)
        {
            if(code.Count>=20000)throw new IrValidationException("Timed instruction budget exceeded.");
            Charge(instruction.Length);code.Add(instruction);
        }
    }

    private void ValidateTimed(JsonElement root, JsonElement processes)
    {
        if(root.TryGetProperty("named_events",out var named))
        {
            NamedEvents=named.EnumerateArray().Select(n=>n.GetString()??"").ToArray();
            if(NamedEvents.Count>256 || NamedEvents.Any(n=>!Identifier(n)) || NamedEvents.Distinct(StringComparer.Ordinal).Count()!=NamedEvents.Count || NamedEvents.Any(n=>signalIds.ContainsKey(n)||memoryIds.ContainsKey(n)))
                throw new IrValidationException("Invalid named event declarations.");
        }
        if (Clock is not null || Kind(Body) != "block" || Body.GetProperty("items").GetArrayLength() != 0)
            throw new IrValidationException("Explicit-time IR cannot mix cycle bodies.");
        if (root.TryGetProperty("time_precision", out var precision))
        {
            TimePrecisionExponent = precision.GetInt32();
            if (TimePrecisionExponent is < -15 or > 0) throw new IrValidationException("Time precision exponent must be -15..0.");
        }
        if (processes.ValueKind != JsonValueKind.Array || processes.GetArrayLength() > 256)
            throw new IrValidationException("Timed process count must be 0..256 before combinational processes.");
        if (Memories.Any(m => m.Dynamic || m.Queue || m.Associative || m.Dimensions?.Count > 1))
            throw new IrValidationException("Timed slice currently requires words and one-dimensional fixed memories.");
        // Register implicit wait-first processes before initial blocks deposit
        // values. Unlike always_comb, an empty @* sensitivity never executes.
        var expanded = processes.EnumerateArray().Where(p=>Kind(p)=="always_star")
            .Select(p=>{JsonShape.Fields(TimedAnalysis(p),["kind","body"]);return TimedImplicit(p.GetProperty("body"));}).ToList();
        expanded.AddRange(processes.EnumerateArray().Where(p=>Kind(p)!="always_star").Select(p=>p.Clone()));
        foreach (var assignment in root.GetProperty("continuous").EnumerateArray())
        {
            JsonShape.Fields(assignment, ["target", "value"]);
            var body = Element("{\"kind\":\"blocking\",\"target\":" + assignment.GetProperty("target").GetRawText() + ",\"value\":" + assignment.GetProperty("value").GetRawText() + "}");
            expanded.Add(TimedCombinational(body, false));
        }
        if (root.TryGetProperty("combinational", out var blocks))
            foreach (var block in blocks.EnumerateArray()) expanded.Add(TimedCombinational(block, true));
        if (expanded.Count is < 1 or > 256) throw new IrValidationException("Expanded timed process count must be 1..256.");
        TimedProcesses = expanded.AsReadOnly();
        var writers = ValidateInitialization([]);
        foreach (var process in TimedProcesses) ValidateTimedStatement(TimedAnalysis(process), writers, 0);
        DiagnoseTimedProcesses();
        // An uninitialized Verilog variable is X (or zero for two-state types),
        // even if no process ever writes it. Timed storage follows that rule.
    }

    private void DiagnoseTimedProcesses()
    {
        var owners=new List<(HashSet<string> Writes,HashSet<string> Blocking,HashSet<string> Reads)>();
        bool truncated=false;
        void Process(JsonElement root)
        {
            if(owners.Count==256){truncated=true;return;}
            var writes=new HashSet<string>(StringComparer.Ordinal);var blocking=new HashSet<string>(StringComparer.Ordinal);var reads=new HashSet<string>(StringComparer.Ordinal);
            owners.Add((writes,blocking,reads));Visit(root);
            void Visit(JsonElement node)
            {
                string kind=Kind(node);
                if(kind=="fork"){foreach(var child in node.GetProperty("items").EnumerateArray())Process(child);return;}
                if(kind is "blocking" or "blocking_delay" or "nba")
                    foreach(var leaf in TargetLeaves(node.GetProperty("target"))){string owner=TargetOwner(leaf);writes.Add(owner);if(kind!="nba")blocking.Add(owner);}
                // StatementReferences only visits direct operands; recurse
                // explicitly across suspension/loop boundaries below.
                foreach(string name in TimedStatementReads(node))reads.Add(name);
                if(kind=="event")foreach(var sensitivity in node.GetProperty("events").EnumerateArray())
                    if(sensitivity.TryGetProperty("signal",out var signal))reads.Add(signal.GetString()!);
                    else if(sensitivity.TryGetProperty("memory",out var memory))reads.Add(memory.GetString()!);
                foreach(string property in new[]{"body","then","else","step"})if(node.TryGetProperty(property,out var child))Visit(child);
                if(kind=="block")foreach(var child in node.GetProperty("items").EnumerateArray())Visit(child);
            }
        }
        foreach(var process in TimedProcesses)Process(process);
        var diagnostics=new List<string>();
        foreach(string name in owners.SelectMany(o=>o.Writes).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            if(diagnostics.Count>=1024){truncated=true;break;}
            int[] writers=Enumerable.Range(0,owners.Count).Where(i=>owners[i].Writes.Contains(name)).ToArray();
            if(writers.Length>1)diagnostics.Add($"VS-RACE-WW: {name}: potential multiple-process write conflict (processes {string.Join(',',writers)}); scheduling/path exclusion is not proven.");
            foreach(int writer in writers.Where(i=>owners[i].Blocking.Contains(name)))
            {
                if(diagnostics.Count>=1024){truncated=true;break;}
                int[] readers=Enumerable.Range(0,owners.Count).Where(i=>i!=writer&&owners[i].Reads.Contains(name)).ToArray();
                if(readers.Length>0)diagnostics.Add($"VS-RACE-RW: {name}: blocking writer process {writer}, reader processes {string.Join(',',readers)}; scheduling/path exclusion is not proven.");
            }
        }
        if(truncated)diagnostics.Add("VS-RACE-LIMIT: analysis truncated at 256 process fragments or 1024 hazards; remaining interactions are unmeasured.");
        TimingDiagnostics=diagnostics.AsReadOnly();
    }

    // A destination is not a read, but calculating its selected address is.
    // Preserve explicit RHS reads of a variable that is also assigned.
    private IEnumerable<string> TimedStatementReads(JsonElement statement)
    {
        foreach(string field in new[]{"value","condition","count","amount"})
            if(statement.TryGetProperty(field,out var operand)&&operand.ValueKind==JsonValueKind.Object)
                foreach(string name in References(operand))yield return name;
        if(statement.TryGetProperty("target",out var target))foreach(var leaf in TargetLeaves(target))
            foreach(string field in new[]{"index","base"})if(leaf.TryGetProperty(field,out var operand))
                foreach(string name in References(operand))yield return name;
    }

    private JsonElement TimedCombinational(JsonElement body, bool excludeWritten)
    {
        var statements = Statements(body).ToArray();
        var reads = statements.SelectMany(StatementReferences).Distinct().ToHashSet(StringComparer.Ordinal);
        var writes = new HashSet<string>(StringComparer.Ordinal); ValidateStatement(TimedAnalysis(body), writes, 0);
        if (statements.Any(s => Kind(s) is "nba" or "process" or "parallel")) throw new IrValidationException("Timed combinational processes require blocking statements.");
        if (excludeWritten) reads.ExceptWith(writes);
        if (reads.Any(n => !signalIds.ContainsKey(n) && !memoryIds.ContainsKey(n))) throw new IrValidationException("Unknown timed sensitivity storage.");
        if (reads.Count == 0) return body.Clone();
        string events = string.Join(',', reads.Order(StringComparer.Ordinal).Select(n => "{\""+(memoryIds.ContainsKey(n)?"memory":"signal")+"\":\"" + n + "\",\"edge\":\"change\"}"));
        return Element("{\"kind\":\"while\",\"condition\":{\"kind\":\"const\",\"bits\":\"1\"},\"body\":{\"kind\":\"block\",\"items\":[" + body.GetRawText() + ",{\"kind\":\"event\",\"events\":[" + events + "],\"body\":{\"kind\":\"block\",\"items\":[]}}]}}");
    }

    private JsonElement TimedImplicit(JsonElement body)
    {
        // Even an empty inferred sensitivity must not hide unsupported code.
        ValidateTimedStatement(TimedAnalysis(body),new HashSet<string>(StringComparer.Ordinal),0);
        IEnumerable<string> Read(JsonElement node)
        {
            foreach(var name in TimedStatementReads(node))yield return name;
            foreach(string field in new[]{"body","then","else","step"})if(node.TryGetProperty(field,out var child))foreach(var name in Read(child))yield return name;
            if(Kind(node) is "block" or "fork")foreach(var child in node.GetProperty("items").EnumerateArray())foreach(var name in Read(child))yield return name;
        }
        var reads=Read(body).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if(reads.Any(n=>!signalIds.ContainsKey(n)&&!memoryIds.ContainsKey(n)))throw new IrValidationException("Unknown implicit sensitivity storage.");
        if(reads.Length==0)return Element("{\"kind\":\"wait\",\"condition\":{\"kind\":\"const\",\"bits\":\"0\"},\"body\":{\"kind\":\"block\",\"items\":[]}}");
        string events=string.Join(',',reads.Select(n=>"{\""+(memoryIds.ContainsKey(n)?"memory":"signal")+"\":\""+n+"\",\"edge\":\"change\"}"));
        return Element("{\"kind\":\"while\",\"condition\":{\"kind\":\"const\",\"bits\":\"1\"},\"body\":{\"kind\":\"event\",\"events\":["+events+"],\"body\":"+body.GetRawText()+"}}");
    }

    // Keep original source annotations in executable IR, but remove validated
    // metadata from semantic analysis so expression/statement shapes stay closed.
    // Origins are diagnostic data only and never authorize filesystem access.
    private static JsonElement TimedAnalysis(JsonElement value)
    {
        using var bytes=new MemoryStream();
        using(var writer=new Utf8JsonWriter(bytes))Copy(value,writer);
        return Element(Encoding.UTF8.GetString(bytes.ToArray()));
        static void Copy(JsonElement node,Utf8JsonWriter writer)
        {
            if(node.ValueKind==JsonValueKind.Object)
            {
                writer.WriteStartObject();
                var names=new HashSet<string>(StringComparer.Ordinal);
                foreach(var property in node.EnumerateObject())
                {
                    if(!names.Add(property.Name))throw new IrValidationException("Duplicate timed IR property: "+property.Name);
                    if(property.Name=="source")
                    {
                        var source=property.Value;JsonShape.Fields(source,["file","line"],["column"]);
                        string? file=source.GetProperty("file").GetString();
                        if(string.IsNullOrEmpty(file)||file.Length>4096||file.Any(char.IsControl)||source.GetProperty("line").GetInt32()<1||source.TryGetProperty("column",out var column)&&column.GetInt32()<1)
                            throw new IrValidationException("Invalid HDL source location.");
                    }
                    else{writer.WritePropertyName(property.Name);Copy(property.Value,writer);}
                }
                writer.WriteEndObject();
            }
            else if(node.ValueKind==JsonValueKind.Array){writer.WriteStartArray();foreach(var child in node.EnumerateArray())Copy(child,writer);writer.WriteEndArray();}
            else node.WriteTo(writer);
        }
    }

    private void ValidateTimedStatement(JsonElement statement, HashSet<string> writers, int depth, bool inLoop=false)
    {
        Budget(depth);
        switch (Kind(statement))
        {
            case "block":
                JsonShape.Fields(statement, ["kind", "items"]);
                foreach (var child in statement.GetProperty("items").EnumerateArray()) ValidateTimedStatement(child, writers, depth + 1,inLoop);
                break;
            case "delay":
                JsonShape.Fields(statement, ["kind", "body"], ["ticks", "amount", "scale", "literal", "unit", "precision"]);
                ValidateTimedDelay(statement, depth);
                ValidateTimedStatement(statement.GetProperty("body"), writers, depth + 1,inLoop);
                break;
            case "event":
                JsonShape.Fields(statement, ["kind", "events", "body"]);
                ValidateTimedEvents(statement.GetProperty("events"));
                ValidateTimedStatement(statement.GetProperty("body"), writers, depth + 1,inLoop);
                break;
            case "wait":
                JsonShape.Fields(statement, ["kind", "condition", "body"]);
                var condition = statement.GetProperty("condition"); ValidateExpression(condition, depth + 1);
                if (References(condition).Any(n => !signalIds.ContainsKey(n) && !memoryIds.ContainsKey(n)))
                    throw new IrValidationException("Unknown timed wait storage.");
                ValidateTimedStatement(statement.GetProperty("body"), writers, depth + 1,inLoop);
                break;
            case "while": case "do":
                JsonShape.Fields(statement, ["kind", "condition", "body"]);
                ValidateExpression(statement.GetProperty("condition"), depth + 1);
                ValidateTimedStatement(statement.GetProperty("body"), writers, depth + 1,true);
                break;
            case "for":
                JsonShape.Fields(statement,["kind","condition","step","body"]);
                ValidateExpression(statement.GetProperty("condition"),depth+1);
                ValidateTimedStatement(statement.GetProperty("step"),writers,depth+1);
                ValidateTimedStatement(statement.GetProperty("body"),writers,depth+1,true);
                break;
            case "break": case "continue":
                JsonShape.Fields(statement,["kind"]);
                if(!inLoop)throw new IrValidationException("Loop transfer requires an enclosing loop in this process.");
                break;
            case "if":
                JsonShape.Fields(statement, ["kind", "condition", "then", "else"]);
                ValidateExpression(statement.GetProperty("condition"), depth + 1);
                ValidateTimedStatement(statement.GetProperty("then"), writers, depth + 1,inLoop);
                ValidateTimedStatement(statement.GetProperty("else"), writers, depth + 1,inLoop);
                break;
            case "repeat":
                JsonShape.Fields(statement, ["kind", "count", "body"]);
                if (statement.GetProperty("count").GetInt32() is < 0 or > 256) throw new IrValidationException("Timed repeat count must be 0..256.");
                ValidateTimedStatement(statement.GetProperty("body"), writers, depth + 1,true);
                break;
            case "runtime_repeat":
                JsonShape.Fields(statement,["kind","count","body"]);
                ValidateExpression(statement.GetProperty("count"),depth+1);
                ValidateTimedStatement(statement.GetProperty("body"),writers,depth+1,true);
                break;
            case "fork":
                JsonShape.Fields(statement, ["kind", "join", "items"]);
                if (statement.GetProperty("join").GetString() is not ("all" or "any" or "none")) throw new IrValidationException("Unsupported fork join mode.");
                foreach (var child in statement.GetProperty("items").EnumerateArray()) ValidateTimedStatement(child, writers, depth + 1);
                break;
            case "wait_fork": case "disable_fork": JsonShape.Fields(statement, ["kind"]); break;
            case "trigger":
                JsonShape.Fields(statement,["kind","name"],["nonblocking"]);
                if(statement.TryGetProperty("nonblocking",out var nonblocking))_=nonblocking.GetBoolean();
                if(!NamedEvents.Contains(Name(statement),StringComparer.Ordinal))throw new IrValidationException("Unknown named event.");
                break;
            case "blocking": case "nba": case "blocking_delay":
                JsonShape.Fields(statement, ["kind", "target", "value"], ["ticks", "amount", "scale", "literal", "unit", "precision"]);
                if (statement.TryGetProperty("ticks", out _) || statement.TryGetProperty("amount", out _) || statement.TryGetProperty("literal", out _))
                {
                    ValidateTimedDelay(statement, depth);
                    if (Kind(statement) == "blocking") throw new IrValidationException("Blocking timing requires explicit sampled blocking_delay.");
                }
                else if (Kind(statement) == "blocking_delay" || statement.TryGetProperty("scale",out _) || statement.TryGetProperty("unit",out _) || statement.TryGetProperty("precision",out _))
                    throw new IrValidationException("Delay metadata requires an explicit delay operand.");
                var normalized = Element("{\"kind\":\"" + (Kind(statement) == "nba" ? "nba" : "blocking") + "\",\"target\":" + statement.GetProperty("target").GetRawText() + ",\"value\":" + statement.GetProperty("value").GetRawText() + "}");
                ValidateStatement(normalized, writers, depth);
                var target = statement.GetProperty("target");
                if(Kind(target)=="concat")
                {
                    var leaves=TargetLeaves(target).ToArray();
                    if(leaves.Any(leaf=>Kind(leaf)!="ref")||leaves.Select(Name).Distinct(StringComparer.Ordinal).Count()!=leaves.Length)
                        throw new IrValidationException("Timed concatenation requires distinct whole signal destinations.");
                }
                else if (Kind(target) is not ("ref" or "index" or "bitselect" or "partselect" or "indexedselect"))
                    throw new IrValidationException("Unsupported timed assignment destination.");
                break;
            default: throw new IrValidationException("Unsupported timed statement: " + Kind(statement));
        }
    }

    // Delay expressions are sampled at their instruction, using declared
    // signedness. Scale is an elaborated integer time-unit conversion factor.
    private void ValidateTimedDelay(JsonElement statement, int depth)
    {
        bool constant = statement.TryGetProperty("ticks", out var ticks);
        bool runtime = statement.TryGetProperty("amount", out var amount);
        bool literal = statement.TryGetProperty("literal", out _);
        if ((constant?1:0)+(runtime?1:0)+(literal?1:0)!=1) throw new IrValidationException("Delay requires exactly one of ticks, amount or literal.");
        if(literal)
        {
            if(statement.TryGetProperty("scale",out _))throw new IrValidationException("Literal delay scale is derived from its time metadata.");
            _=LiteralDelayTicks(statement);return;
        }
        if(statement.TryGetProperty("unit",out _)||statement.TryGetProperty("precision",out _))throw new IrValidationException("Time-unit fields require a literal delay.");
        if (constant)
        {
            _ = ticks.GetUInt64();
            if (statement.TryGetProperty("scale", out _)) throw new IrValidationException("Constant ticks are already scaled.");
        }
        else
        {
            ValidateExpression(amount, depth + 1);
            if (!statement.TryGetProperty("scale", out var scale) || scale.GetUInt64() == 0)
                throw new IrValidationException("Runtime delay requires a positive scale.");
        }
    }

    // Decimal arithmetic preserves decimal half-way cases that binary floating
    // point could round differently. Quantize to the module's precision first,
    // then convert to global ticks; rounding directly to global ticks is wrong.
    private ulong LiteralDelayTicks(JsonElement statement)
    {
        int unit=statement.GetProperty("unit").GetInt32(), precision=statement.GetProperty("precision").GetInt32();
        if(!TimePrecisionExponent.HasValue)throw new IrValidationException("Literal delay requires global time precision.");
        int global=TimePrecisionExponent.GetValueOrDefault();
        if(global < -15 || precision < global || unit < precision || unit > 0)
            throw new IrValidationException("Invalid literal delay time scale.");
        string? text=statement.GetProperty("literal").GetString();
        // Restrict serialized digits before parsing: Decimal.TryParse can round
        // longer mantissas, which could move a value across a time-precision tie.
        decimal value=0;
        if(text is null || text.Length>128 || text.Count(char.IsAsciiDigit)>28 || !decimal.TryParse(text,NumberStyles.AllowDecimalPoint|NumberStyles.AllowExponent,CultureInfo.InvariantCulture,out value)||value<0)
            throw new IrValidationException("Invalid decimal delay literal.");
        decimal Power(int exponent){decimal factor=1;for(int i=0;i<exponent;++i)factor*=10;return factor;}
        decimal rounded=decimal.Round(checked(value*Power(unit-precision)),0,MidpointRounding.AwayFromZero);
        return checked((ulong)(rounded*Power(precision-global)));
    }

    private string TimedDelay(JsonElement statement) => statement.TryGetProperty("amount", out var amount)
        ? $"HdlScheduler::delayTicks({Expression(amount)},{(ExpressionSigned(amount) ? "true" : "false")},{statement.GetProperty("scale").GetUInt64()}ULL)"
        : statement.TryGetProperty("literal",out _) ? LiteralDelayTicks(statement)+"ULL"
        : statement.TryGetProperty("ticks", out var ticks) ? ticks.GetUInt64() + "ULL" : "0ULL";

    private void ValidateTimedEvents(JsonElement events)
    {
        if (events.ValueKind != JsonValueKind.Array || events.GetArrayLength() is < 1 or > 256)
            throw new IrValidationException("Timed sensitivity count must be 1..256.");
        foreach (var trigger in events.EnumerateArray())
        {
            if(trigger.TryGetProperty("event",out var name))
            {
                JsonShape.Fields(trigger,["event"]);
                if(!NamedEvents.Contains(name.GetString(),StringComparer.Ordinal))throw new IrValidationException("Unknown sensitivity event.");
                continue;
            }
            if(trigger.TryGetProperty("memory",out var memory))
            {
                JsonShape.Fields(trigger,["memory","edge"]);
                _=MemoryId(memory.GetString()!);
                if(trigger.GetProperty("edge").GetString()!="change")throw new IrValidationException("Memory sensitivity requires change.");
                continue;
            }
            JsonShape.Fields(trigger, ["signal", "edge"]);
            int id = SignalId(trigger.GetProperty("signal").GetString()!);
            string? edge = trigger.GetProperty("edge").GetString();
            if (edge is not ("change" or "posedge" or "negedge") || edge != "change" && Signals[id].Width != 1)
                throw new IrValidationException("Timed edges require scalars; change sensitivity accepts words.");
        }
    }

    private string TimedWatch(string name,string edge="change") => memoryIds.ContainsKey(name)
        ? "{"+MemoryId(name)+",HdlScheduler::Edge::change,true}"
        : "{"+SignalId(name)+",HdlScheduler::Edge::"+edge+"}";
    private string TimedWatches(JsonElement events) => "{" + string.Join(',', events.EnumerateArray().Where(e=>!e.TryGetProperty("event",out _)).Select(e =>
        TimedWatch(e.GetProperty(e.TryGetProperty("memory",out _)?"memory":"signal").GetString()!,e.GetProperty("edge").GetString()!))) + "}";
    private string TimedNamedWatches(JsonElement events)=>"{"+string.Join(',',events.EnumerateArray().Where(e=>e.TryGetProperty("event",out _)).Select(e=>NamedEvents.ToList().IndexOf(e.GetProperty("event").GetString()!)))+"}";

    // Each instruction returns after a suspension and resumes by numeric PC.
    // Expressions run against the kernel's current state, never a stale copy.
    private int CompileTimed(JsonElement statement, int next, TimedInstructions instructions,(int Exit,int Next,int? Repeat)? control=null)
    {
        string origin="";
        if(statement.TryGetProperty("source",out var location))
        {
            string prefix=location.GetProperty("file").GetString()+":"+location.GetProperty("line").GetInt32().ToString(CultureInfo.InvariantCulture)+(location.TryGetProperty("column",out var column)?":"+column.GetInt32().ToString(CultureInfo.InvariantCulture):"")+": ";
            origin="frame.source=\""+string.Concat(Encoding.UTF8.GetBytes(prefix).Select(b=>"\\x"+b.ToString("x2",CultureInfo.InvariantCulture)))+"\";";
        }
        void Set(int pc,string instruction)=>instructions[pc]=origin+instruction;
        int Add(string instruction) { instructions.Add(origin+instruction); return instructions.Count - 1; }
        string Continue(int pc) => $"frame.pc={pc};return HdlScheduler::Step::again();";
        switch (Kind(statement))
        {
            case "block":
                foreach (var child in statement.GetProperty("items").EnumerateArray().Reverse()) next = CompileTimed(child, next, instructions,control);
                return next;
            case "delay":
                int resumed = CompileTimed(statement.GetProperty("body"), next, instructions,control);
                return Add($"frame.pc={resumed};return HdlScheduler::Step::delay({TimedDelay(statement)});");
            case "event":
                int body = CompileTimed(statement.GetProperty("body"), next, instructions,control);
                return Add($"engine.watch(frame,{TimedWatches(statement.GetProperty("events"))},{TimedNamedWatches(statement.GetProperty("events"))});frame.pc={body};return HdlScheduler::Step::wait();");
            case "wait":
                int afterWait = CompileTimed(statement.GetProperty("body"), next, instructions,control);
                var condition = statement.GetProperty("condition");
                string watches = "{" + string.Join(',', References(condition).Distinct().Select(n => TimedWatch(n))) + "}";
                // A constant false wait suspends forever. A constant true wait
                // continues immediately; neither needs a synthetic signal.
                return Add($"if(yes({Expression(condition)})){{{Continue(afterWait)}}}engine.watch(frame,{watches});return HdlScheduler::Step::wait();");
            case "while": case "do":
                int test = Add("");
                int loop = CompileTimed(statement.GetProperty("body"), test, instructions,(next,test,null));
                Set(test,$"frame.pc=yes({Expression(statement.GetProperty("condition"))})?{loop}:{next};return HdlScheduler::Step::again();");
                return Kind(statement) == "do" ? loop : test;
            case "for":
                int forTest=Add("");
                int forStep=CompileTimed(statement.GetProperty("step"),forTest,instructions);
                int forBody=CompileTimed(statement.GetProperty("body"),forStep,instructions,(next,forStep,null));
                Set(forTest,$"frame.pc=yes({Expression(statement.GetProperty("condition"))})?{forBody}:{next};return HdlScheduler::Step::again();");
                return forTest;
            case "break": case "continue":
                if(control is not {} targetLoop)throw new IrValidationException("Missing compiled loop context.");
                string cleanup=Kind(statement)=="break"&&targetLoop.Repeat is int slot?$"frame.repeats.erase({slot});":"";
                return Add(cleanup+Continue(Kind(statement)=="break"?targetLoop.Exit:targetLoop.Next));
            case "if":
                int yes = CompileTimed(statement.GetProperty("then"), next, instructions,control), no = CompileTimed(statement.GetProperty("else"), next, instructions,control);
                return Add($"frame.pc=yes({Expression(statement.GetProperty("condition"))})?{yes}:{no};return HdlScheduler::Step::again();");
            case "repeat":
                int repeatExit=next;
                for (int i = 0; i < statement.GetProperty("count").GetInt32(); ++i) next = CompileTimed(statement.GetProperty("body"), next, instructions,(repeatExit,next,null));
                return next;
            case "runtime_repeat":
                int entry=Add(""), repeatTest=Add("");
                int repeatedBody=CompileTimed(statement.GetProperty("body"),repeatTest,instructions,(next,repeatTest,entry));
                var count=statement.GetProperty("count");
                Set(entry,$"frame.repeats[{entry}]=repeat_count({Expression(count)},{(ExpressionSigned(count)?"true":"false")});"+Continue(repeatTest));
                Set(repeatTest,$"if(frame.repeats.at({entry})==0){{frame.repeats.erase({entry});{Continue(next)}}}--frame.repeats.at({entry});"+Continue(repeatedBody));
                return entry;
            case "fork":
                string entries = string.Join(',', statement.GetProperty("items").EnumerateArray().Select(child => CompileTimed(child, 0, instructions)));
                return Add($"frame.pc={next};return engine.fork(process,{{{entries}}},HdlScheduler::Join::{statement.GetProperty("join").GetString()});");
            case "wait_fork": return Add($"frame.pc={next};return engine.waitFork(process);");
            case "disable_fork": return Add("engine.disableFork(process);" + Continue(next));
            case "trigger": return Add($"engine.{(statement.TryGetProperty("nonblocking",out var nb)&&nb.GetBoolean()?"nonblockingTrigger":"trigger")}({NamedEvents.ToList().IndexOf(Name(statement))});"+Continue(next));
            case "blocking": case "nba": case "blocking_delay":
                var target = statement.GetProperty("target"); int width = ExpressionWidth(target);
                if(Kind(target)=="concat")
                {
                    int remaining=width;var pieces=new List<string>();
                    foreach(var leaf in TargetLeaves(target))
                    {
                        int size=ExpressionWidth(leaf);remaining-=size;int id=SignalId(Name(leaf));
                        string piece=$"select_part(assigned,{remaining+size-1},{remaining})";
                        if(Signals[id].TwoState)piece="two_state("+piece+")";
                        pieces.Add($"HdlScheduler::Write{{{{{id},std::nullopt}},{piece}}}");
                    }
                    // One RHS and all destinations are sampled before any
                    // copy-out, including inout actuals aliased with inputs.
                    string snapshot=$"auto assigned=resize({Expression(statement.GetProperty("value"),width)},{width});std::vector<HdlScheduler::Write>writes{{{string.Join(',',pieces)}}};";
                    string sampledTicks=TimedDelay(statement);
                    if(Kind(statement)=="blocking_delay")
                    {
                        int commit=Add("if(frame.sampledWrites.empty())throw std::runtime_error(\"missing sampled concatenation\");for(const auto& piece:frame.sampledWrites)engine.blocking(piece);frame.sampledWrites.clear();"+Continue(next));
                        return Add(snapshot+$"frame.sampledWrites=std::move(writes);frame.pc={commit};return HdlScheduler::Step::delay({sampledTicks});");
                    }
                    return Add(snapshot+(Kind(statement)=="blocking"?"for(const auto& piece:writes)engine.blocking(piece);":$"const auto ticks={sampledTicks};for(const auto& piece:writes)engine.nonblocking(piece,ticks);")+Continue(next));
                }
                bool selected=Kind(target) is "bitselect" or "partselect" or "indexedselect";
                bool two = selected ? Signals[SignalId(Name(target.GetProperty("value")))].TwoState : Kind(target) == "ref" ? Signals[SignalId(Name(target))].TwoState : Memories[MemoryId(Name(target))].TwoState;
                // Context can widen intermediate expressions; assignment still
                // truncates/extends to the declared destination word exactly.
                string value = $"resize({Expression(statement.GetProperty("value"), width)},{width})";
                if (two) value = "two_state(" + value + ")";
                string write;
                if(selected)
                {
                    int id=SignalId(Name(target.GetProperty("value")));
                    string basis=Kind(target)=="bitselect"?Expression(target.GetProperty("index")):
                        Kind(target)=="partselect"?$"word({target.GetProperty("right").GetInt32()},0,0,32)":Expression(target.GetProperty("base"));
                    bool down=Kind(target)=="indexedselect"&&target.GetProperty("descending").GetBoolean();
                    write=$"engine.selected({id},{value},{basis},{width},{(down?"true":"false")},{(two?"true":"false")})";
                }
                else
                {
                    string destination=Kind(target)=="ref"?$"{{{SignalId(Name(target))},std::nullopt}}":
                        $"{{{MemoryId(Name(target))},HdlScheduler::sampledIndex({Expression(target.GetProperty("index"))},{Memories[MemoryId(Name(target))].LowerBound},{Memories[MemoryId(Name(target))].Depth})}}";
                    write=$"HdlScheduler::Write{{{destination},{value}}}";
                }
                string ticks = TimedDelay(statement);
                if (Kind(statement) == "blocking_delay")
                {
                    int commit = Add("if(!frame.sampled)throw std::runtime_error(\"missing sampled assignment\");engine.blocking(*frame.sampled);frame.sampled.reset();" + Continue(next));
                    return Add($"frame.sampled={write};frame.pc={commit};return HdlScheduler::Step::delay({ticks});");
                }
                return Add(Kind(statement) == "blocking" ? $"engine.blocking({write});" + Continue(next) : $"engine.nonblocking({write},{ticks});" + Continue(next));
            default: throw new IrValidationException("Unsupported timed compilation.");
        }
    }

    private string EmitTimedModelHeader(string top)
    {
        var names = ExternalPortNames(); string className = "V" + top;
        if (names.Values.Contains(className)) throw new ArgumentException("Port conflicts with generated class name.", nameof(top));
        var instructions = new TimedInstructions();instructions.Add("return HdlScheduler::Step::done();");
        int[] entries = TimedProcesses.Select(p => CompileTimed(p, 0, instructions)).ToArray();
        var code = new StringBuilder("#pragma once\n#include \"external_model.h\"\n#include \"hdl_scheduler.h\"\nusing namespace vsrtl;\n");
        code.AppendLine($"class {className} final:public vsrtl::Model{{\nstd::unique_ptr<HdlScheduler> scheduler;std::vector<AssociativeStorage>assoc;size_t execution_budget=20000;");
        code.AppendLine("static HdlScheduler::Step execute(size_t process,HdlScheduler::Frame& frame,HdlScheduler& engine){const auto& s=engine.signals();const auto& mem=engine.memories();switch(frame.pc){");
        for (int i = 0; i < instructions.Count; ++i) code.AppendLine($"case {i}:{{{instructions[i]}}}");
        code.AppendLine("default:throw std::runtime_error(\"invalid timed process PC\");}}\nvoid evaluate()override{std::vector<HdlScheduler::Write>drives;");
        foreach (var input in Signals.Where(s => s.Direction == "input")) code.AppendLine($"drives.push_back({{{{{SignalId(input.Name)},std::nullopt}},s[{SignalId(input.Name)}]}});");
        code.AppendLine("auto candidate=*scheduler;candidate.driveAt(time(),drives,execute);auto nextS=candidate.signals();auto nextMem=candidate.memories();*scheduler=std::move(candidate);s=std::move(nextS);mem=std::move(nextMem);}\npublic:\nbool eventsPending()const{checkThread();return scheduler->eventsPending();}\nstd::optional<uint64_t> nextTimeSlot()const{checkThread();return scheduler->nextTimeSlot();}");
        foreach (var signal in Signals.Where(s => s.Direction != "internal")) code.AppendLine($"vsrtl::Port {names[signal.Name]};");
        code.Append($"{className}():vsrtl::Model(\"{top}\",{{");
        code.AppendJoin(',', Signals.Select(s => $"{{\"{s.SourcePath ?? s.Name}\",{s.Width},{(s.Direction == "input" ? "vpiInput" : s.Direction == "output" ? "vpiOutput" : "vpiNoDirection")},{(s.Signed ? "true" : "false")},{(s.TwoState ? "true" : "false")}}}"));
        code.Append("},-1)");
        foreach (var signal in Signals.Where(s => s.Direction != "internal")) code.Append($",{names[signal.Name]}(*this,{SignalId(signal.Name)})");
        code.AppendLine($"{{assoc.resize({Memories.Count});mem={{");
        code.AppendJoin(',', Memories.Select(m => $"std::vector<W>({m.Depth},{(m.TwoState ? $"word(0,0,0,{m.Width})" : $"unknown({m.Width})")})"));
        code.AppendLine("};"); EmitInitialization(code);
        code.AppendLine($"scheduler=std::make_unique<HdlScheduler>(s,mem,{NamedEvents.Count});");
        foreach (int entry in entries) code.AppendLine($"scheduler->addProcess({entry});");
        return code.AppendLine("}\n};").ToString();
    }
}
