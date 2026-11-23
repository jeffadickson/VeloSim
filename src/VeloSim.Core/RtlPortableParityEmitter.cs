using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed partial class RtlDesign
{
    private bool HasInitBlock() => Statements(Initialization).Any(st => Kind(st) is not "block");

    private bool HasBlockingOrContainerOps() =>
        Memories.Any(m => m.Dynamic || m.Queue || m.Associative || m.Dimensions is not null) ||
        Statements(Body).Any(e => Kind(e) is "blocking" or "allocate" or "delete" or "queue_push" or "queue_pop" or "assoc_traverse");

    private static bool IsContainerMem(RtlMemory m) => m.Dynamic || m.Queue || m.Associative;

    private string CsTimedDelay(JsonElement st) =>
        st.TryGetProperty("amount", out var amt)
            ? $"HdlSched.DelayTicks({CsExpr64(amt)},{ExpressionWidth(amt)},{(ExpressionSigned(amt) ? "true" : "false")},{st.GetProperty("scale").GetUInt64()}UL)"
            : st.TryGetProperty("literal", out _)
                ? LiteralDelayTicks(st) + "UL"
                : st.TryGetProperty("ticks", out var ticks) ? ticks.GetUInt64() + "UL" : "0UL";

    private string RsTimedDelay(JsonElement st) =>
        st.TryGetProperty("amount", out var amt)
            ? $"hdl_sched::delay_ticks({RsExpr64(amt)},{ExpressionWidth(amt)},{(ExpressionSigned(amt) ? "true" : "false")},{st.GetProperty("scale").GetUInt64()}u64)?"
            : st.TryGetProperty("literal", out _)
                ? LiteralDelayTicks(st) + "u64"
                : st.TryGetProperty("ticks", out var ticks) ? ticks.GetUInt64() + "u64" : "0u64";

    private string CsTimedWatch(string name, string edge = "change")
    {
        string e = edge switch { "posedge" => "HdlEdge.Posedge", "negedge" => "HdlEdge.Negedge", _ => "HdlEdge.Change" };
        return memoryIds.ContainsKey(name)
            ? $"new HdlWatch({MemoryId(name)}, HdlEdge.Change, true)"
            : $"new HdlWatch({SignalId(name)}, {e}, false)";
    }

    private string RsTimedWatch(string name, string edge = "change")
    {
        string e = edge switch { "posedge" => "HdlEdge::Posedge", "negedge" => "HdlEdge::Negedge", _ => "HdlEdge::Change" };
        return memoryIds.ContainsKey(name)
            ? $"HdlWatch {{ signal: {MemoryId(name)}, edge: HdlEdge::Change, memory: true }}"
            : $"HdlWatch {{ signal: {SignalId(name)}, edge: {e}, memory: false }}";
    }

    private int CompileCsTimed(JsonElement statement, int next, List<string> instrs, (int Exit, int Next, int? Repeat)? control = null)
    {
        void Set(int pc, string code) => instrs[pc] = code;
        int Add(string code) { instrs.Add(code); return instrs.Count - 1; }
        string Cont(int pc) => $"frame.Pc = {pc}; return HdlStep.Again;";
        switch (Kind(statement))
        {
            case "block":
                foreach (var child in statement.GetProperty("items").EnumerateArray().Reverse())
                    next = CompileCsTimed(child, next, instrs, control);
                return next;
            case "delay":
            {
                int resumed = CompileCsTimed(statement.GetProperty("body"), next, instrs, control);
                return Add($"frame.Pc = {resumed}; return HdlStep.Delay({CsTimedDelay(statement)});");
            }
            case "event":
            {
                int body = CompileCsTimed(statement.GetProperty("body"), next, instrs, control);
                var evArr = statement.GetProperty("events").EnumerateArray().ToArray();
                string watches = string.Join(", ", evArr.Where(ev => !ev.TryGetProperty("event", out _))
                    .Select(ev => CsTimedWatch(ev.GetProperty(ev.TryGetProperty("memory", out _) ? "memory" : "signal").GetString()!, ev.GetProperty("edge").GetString()!)));
                string named = string.Join(", ", evArr.Where(ev => ev.TryGetProperty("event", out _))
                    .Select(ev => NamedEvents.ToList().IndexOf(ev.GetProperty("event").GetString()!)));
                return Add($"frame.Watch([{watches}], [{named}]); frame.Pc = {body}; return HdlStep.Wait;");
            }
            case "wait":
            {
                int afterWait = CompileCsTimed(statement.GetProperty("body"), next, instrs, control);
                var cond = statement.GetProperty("condition");
                string watches = string.Join(", ", References(cond).Distinct().Select(n => CsTimedWatch(n)));
                return Add($"if ({CsCond64(cond)}) {{ {Cont(afterWait)} }} frame.Watch([{watches}], []); return HdlStep.Wait;");
            }
            case "while":
            case "do":
            {
                int test = Add("");
                int loop = CompileCsTimed(statement.GetProperty("body"), test, instrs, (next, test, null));
                Set(test, $"frame.Pc = {CsCond64(statement.GetProperty("condition"))} ? {loop} : {next}; return HdlStep.Again;");
                return Kind(statement) == "do" ? loop : test;
            }
            case "for":
            {
                int forTest = Add("");
                int forStep = CompileCsTimed(statement.GetProperty("step"), forTest, instrs);
                int forBody = CompileCsTimed(statement.GetProperty("body"), forStep, instrs, (next, forStep, null));
                Set(forTest, $"frame.Pc = {CsCond64(statement.GetProperty("condition"))} ? {forBody} : {next}; return HdlStep.Again;");
                return forTest;
            }
            case "break":
            case "continue":
            {
                if (control is not { } loop) throw new IrValidationException("Missing compiled loop context.");
                string cleanup = Kind(statement) == "break" && loop.Repeat is int slot ? $"frame.Repeats.Remove({slot}); " : "";
                return Add(cleanup + Cont(Kind(statement) == "break" ? loop.Exit : loop.Next));
            }
            case "if":
            {
                int yes = CompileCsTimed(statement.GetProperty("then"), next, instrs, control);
                int no = CompileCsTimed(statement.GetProperty("else"), next, instrs, control);
                return Add($"frame.Pc = {CsCond64(statement.GetProperty("condition"))} ? {yes} : {no}; return HdlStep.Again;");
            }
            case "repeat":
            {
                int exit = next;
                for (int i = 0; i < statement.GetProperty("count").GetInt32(); ++i)
                    next = CompileCsTimed(statement.GetProperty("body"), next, instrs, (exit, next, null));
                return next;
            }
            case "runtime_repeat":
            {
                int entry = Add(""), repTest = Add("");
                int repBody = CompileCsTimed(statement.GetProperty("body"), repTest, instrs, (next, repTest, entry));
                var cnt = statement.GetProperty("count");
                Set(entry, $"frame.Repeats[{entry}] = W4Ops.RepeatCount({CsExpr64(cnt)},{ExpressionWidth(cnt)},{(ExpressionSigned(cnt) ? "true" : "false")}); " + Cont(repTest));
                Set(repTest, $"if (frame.Repeats[{entry}] == 0u) {{ frame.Repeats.Remove({entry}); {Cont(next)} }} frame.Repeats[{entry}]--; " + Cont(repBody));
                return entry;
            }
            case "fork":
            {
                string entries = string.Join(", ", statement.GetProperty("items").EnumerateArray().Select(ch => CompileCsTimed(ch, 0, instrs)));
                string join = statement.GetProperty("join").GetString() switch { "all" => "HdlJoin.All", "any" => "HdlJoin.Any", _ => "HdlJoin.None" };
                return Add($"frame.Pc = {next}; return _Fork(procId, [{entries}], {join});");
            }
            case "wait_fork":
                return Add($"frame.Pc = {next}; return _WaitFork(procId);");
            case "disable_fork":
                return Add($"_DisableFork(procId); " + Cont(next));
            case "trigger":
            {
                int eid = NamedEvents.ToList().IndexOf(Name(statement));
                bool nb = statement.TryGetProperty("nonblocking", out var nbp) && nbp.GetBoolean();
                return Add($"{(nb ? $"_NonblockingTrigger({eid});" : $"_Trigger({eid});")} " + Cont(next));
            }
            case "blocking":
            case "nba":
            case "blocking_delay":
            {
                var target = statement.GetProperty("target");
                int width = ExpressionWidth(target);
                string ticks = CsTimedDelay(statement);
                if (Kind(target) == "concat")
                {
                    int rem = width;
                    var pieces = new List<string>();
                    foreach (var leaf in TargetLeaves(target))
                    {
                        int sz = ExpressionWidth(leaf); rem -= sz; int id = SignalId(Name(leaf));
                        string piece = $"W4Ops.SelPartFast(_assigned, {rem}, {CsMaskHex(sz)})";
                        if (Signals[id].TwoState) piece = $"W4Ops.TwoState({piece}, {CsMaskHex(sz)})";
                        pieces.Add($"new HdlWrite({id}, -1, {piece}, false, 0UL)");
                    }
                    string snap = $"W4 _assigned = W4Ops.Resize({CsExpr64(statement.GetProperty("value"), width)}, {CsMaskHex(width)}); HdlWrite[] _ws = [{string.Join(", ", pieces)}]; ";
                    if (Kind(statement) == "blocking_delay")
                    {
                        int commit = Add("if (frame.SampledWrites.Count == 0) throw new InvalidOperationException(\"missing sampled concatenation\"); foreach (var _w in frame.SampledWrites) _Blocking(_w); frame.SampledWrites.Clear(); " + Cont(next));
                        return Add(snap + $"frame.SampledWrites.Clear(); frame.SampledWrites.AddRange(_ws); frame.Pc = {commit}; return HdlStep.Delay({ticks});");
                    }
                    return Add(snap + (Kind(statement) == "blocking"
                        ? "foreach (var _w in _ws) _Blocking(_w); "
                        : $"ulong _dt = {ticks}; foreach (var _w in _ws) _Nonblocking(_w, _dt); ") + Cont(next));
                }
                bool selected = Kind(target) is "bitselect" or "partselect" or "indexedselect";
                bool two = selected ? Signals[SignalId(Name(target.GetProperty("value")))].TwoState
                    : Kind(target) == "ref" ? Signals[SignalId(Name(target))].TwoState : Memories[MemoryId(Name(target))].TwoState;
                string val = $"W4Ops.Resize({CsExpr64(statement.GetProperty("value"), width)}, {CsMaskHex(width)})";
                if (two) val = $"W4Ops.TwoState({val}, {CsMaskHex(width)})";
                string writeExpr;
                if (selected)
                {
                    int id = SignalId(Name(target.GetProperty("value")));
                    int sigW = Signals[id].Width;
                    string basis = Kind(target) == "bitselect" ? CsExpr64(target.GetProperty("index"))
                        : Kind(target) == "partselect" ? $"new W4({target.GetProperty("right").GetInt32()}UL, 0UL)"
                        : CsExpr64(target.GetProperty("base"));
                    bool down = Kind(target) == "indexedselect" && target.GetProperty("descending").GetBoolean();
                    writeExpr = $"HdlSched.SelectedWrite({id}, {sigW}, {val}, {basis}, {width}, {(down ? "true" : "false")}, {(two ? "true" : "false")})";
                }
                else if (Kind(target) == "ref")
                {
                    writeExpr = $"new HdlWrite({SignalId(Name(target))}, -1, {val}, false, 0UL)";
                }
                else
                {
                    int mid = MemoryId(Name(target));
                    writeExpr = $"new HdlWrite({mid}, HdlSched.SampledIndex({CsExpr64(target.GetProperty("index"))}, {Memories[mid].LowerBound}, {Memories[mid].Depth}), {val}, false, 0UL)";
                }
                if (Kind(statement) == "blocking_delay")
                {
                    int commit = Add("if (frame.Sampled is null) throw new InvalidOperationException(\"missing sampled assignment\"); _Blocking(frame.Sampled.Value); frame.Sampled = null; " + Cont(next));
                    return Add($"frame.Sampled = {writeExpr}; frame.Pc = {commit}; return HdlStep.Delay({ticks});");
                }
                return Add((Kind(statement) == "blocking" ? $"_Blocking({writeExpr}); " : $"_Nonblocking({writeExpr}, {ticks}); ") + Cont(next));
            }
            default:
                throw new IrValidationException("Unsupported timed statement for C#: " + Kind(statement));
        }
    }

    private int CompileRsTimed(JsonElement statement, int next, List<string> instrs, (int Exit, int Next, int? Repeat)? control = null)
    {
        void Set(int pc, string code) => instrs[pc] = code;
        int Add(string code) { instrs.Add(code); return instrs.Count - 1; }
        string Cont(int pc) => $"self.sched.frames[proc_id].pc = {pc}; return Ok(HdlStep::Again);";
        switch (Kind(statement))
        {
            case "block":
                foreach (var child in statement.GetProperty("items").EnumerateArray().Reverse())
                    next = CompileRsTimed(child, next, instrs, control);
                return next;
            case "delay":
            {
                int resumed = CompileRsTimed(statement.GetProperty("body"), next, instrs, control);
                return Add($"let _dt = {RsTimedDelay(statement)}; self.sched.frames[proc_id].pc = {resumed}; return Ok(HdlStep::Delay(_dt));");
            }
            case "event":
            {
                int body = CompileRsTimed(statement.GetProperty("body"), next, instrs, control);
                var evArr = statement.GetProperty("events").EnumerateArray().ToArray();
                string watches = string.Join(", ", evArr.Where(ev => !ev.TryGetProperty("event", out _))
                    .Select(ev => RsTimedWatch(ev.GetProperty(ev.TryGetProperty("memory", out _) ? "memory" : "signal").GetString()!, ev.GetProperty("edge").GetString()!)));
                string named = string.Join(", ", evArr.Where(ev => ev.TryGetProperty("event", out _))
                    .Select(ev => NamedEvents.ToList().IndexOf(ev.GetProperty("event").GetString()!) + "usize"));
                return Add($"self.sched.frames[proc_id].watch(vec![{watches}], vec![{named}]); self.sched.frames[proc_id].pc = {body}; return Ok(HdlStep::Wait);");
            }
            case "wait":
            {
                int afterWait = CompileRsTimed(statement.GetProperty("body"), next, instrs, control);
                var cond = statement.GetProperty("condition");
                string watches = string.Join(", ", References(cond).Distinct().Select(n => RsTimedWatch(n)));
                return Add($"if w4::yes({RsExpr64(cond)}) {{ {Cont(afterWait)} }} self.sched.frames[proc_id].watch(vec![{watches}], Vec::new()); return Ok(HdlStep::Wait);");
            }
            case "while":
            case "do":
            {
                int test = Add("");
                int loop = CompileRsTimed(statement.GetProperty("body"), test, instrs, (next, test, null));
                Set(test, $"self.sched.frames[proc_id].pc = if w4::yes({RsExpr64(statement.GetProperty("condition"))}) {{ {loop} }} else {{ {next} }}; return Ok(HdlStep::Again);");
                return Kind(statement) == "do" ? loop : test;
            }
            case "for":
            {
                int forTest = Add("");
                int forStep = CompileRsTimed(statement.GetProperty("step"), forTest, instrs);
                int forBody = CompileRsTimed(statement.GetProperty("body"), forStep, instrs, (next, forStep, null));
                Set(forTest, $"self.sched.frames[proc_id].pc = if w4::yes({RsExpr64(statement.GetProperty("condition"))}) {{ {forBody} }} else {{ {next} }}; return Ok(HdlStep::Again);");
                return forTest;
            }
            case "break":
            case "continue":
            {
                if (control is not { } loop) throw new IrValidationException("Missing compiled loop context.");
                string cleanup = Kind(statement) == "break" && loop.Repeat is int slot ? $"self.sched.frames[proc_id].repeats.remove(&{slot}); " : "";
                return Add(cleanup + Cont(Kind(statement) == "break" ? loop.Exit : loop.Next));
            }
            case "if":
            {
                int yes = CompileRsTimed(statement.GetProperty("then"), next, instrs, control);
                int no = CompileRsTimed(statement.GetProperty("else"), next, instrs, control);
                return Add($"self.sched.frames[proc_id].pc = if w4::yes({RsExpr64(statement.GetProperty("condition"))}) {{ {yes} }} else {{ {no} }}; return Ok(HdlStep::Again);");
            }
            case "repeat":
            {
                int exit = next;
                for (int i = 0; i < statement.GetProperty("count").GetInt32(); ++i)
                    next = CompileRsTimed(statement.GetProperty("body"), next, instrs, (exit, next, null));
                return next;
            }
            case "runtime_repeat":
            {
                int entry = Add(""), repTest = Add("");
                int repBody = CompileRsTimed(statement.GetProperty("body"), repTest, instrs, (next, repTest, entry));
                var cnt = statement.GetProperty("count");
                Set(entry, $"let _rc = w4::repeat_count({RsExpr64(cnt)},{ExpressionWidth(cnt)},{(ExpressionSigned(cnt) ? "true" : "false")})?; self.sched.frames[proc_id].repeats.insert({entry}, _rc); " + Cont(repTest));
                Set(repTest, $"let _rem = *self.sched.frames[proc_id].repeats.get(&{entry}).unwrap_or(&0); if _rem == 0 {{ self.sched.frames[proc_id].repeats.remove(&{entry}); {Cont(next)} }} self.sched.frames[proc_id].repeats.insert({entry}, _rem - 1); " + Cont(repBody));
                return entry;
            }
            case "fork":
            {
                string entries = string.Join(", ", statement.GetProperty("items").EnumerateArray().Select(ch => CompileRsTimed(ch, 0, instrs) + "usize"));
                string join = statement.GetProperty("join").GetString() switch { "all" => "HdlJoin::All", "any" => "HdlJoin::Any", _ => "HdlJoin::None" };
                return Add($"self.sched.frames[proc_id].pc = {next}; return self.fork_proc(proc_id, &[{entries}], {join});");
            }
            case "wait_fork":
                return Add($"self.sched.frames[proc_id].pc = {next}; return self.wait_fork_proc(proc_id);");
            case "disable_fork":
                return Add($"self.disable_fork_proc(proc_id); " + Cont(next));
            case "trigger":
            {
                int eid = NamedEvents.ToList().IndexOf(Name(statement));
                bool nb = statement.TryGetProperty("nonblocking", out var nbp) && nbp.GetBoolean();
                return Add($"{(nb ? $"self.nonblocking_trigger({eid})?;" : $"self.trigger_event({eid})?;")} " + Cont(next));
            }
            case "blocking":
            case "nba":
            case "blocking_delay":
            {
                var target = statement.GetProperty("target");
                int width = ExpressionWidth(target);
                string ticks = RsTimedDelay(statement);
                if (Kind(target) == "concat")
                {
                    int rem = width;
                    var pieces = new List<string>();
                    foreach (var leaf in TargetLeaves(target))
                    {
                        int sz = ExpressionWidth(leaf); rem -= sz; int id = SignalId(Name(leaf));
                        string piece = $"w4::selpart(_assigned, {width}, {rem + sz - 1}, {rem})";
                        if (Signals[id].TwoState) piece = $"w4::two_state({piece}, {RsMaskHex(sz)})";
                        pieces.Add($"HdlWrite {{ signal: {id}, index: None, value: {piece}, enable: None }}");
                    }
                    string snap = $"let _assigned = w4::resize({RsExpr64(statement.GetProperty("value"), width)}, {RsMaskHex(width)}); let _ws = vec![{string.Join(", ", pieces)}]; ";
                    if (Kind(statement) == "blocking_delay")
                    {
                        int commit = Add("let _ws = std::mem::take(&mut self.sched.frames[proc_id].sampled_writes); if _ws.is_empty() { return Err(\"missing sampled concatenation\"); } for _w in _ws { self.blocking_write(_w)?; } " + Cont(next));
                        return Add(snap + $"let _dt = {ticks}; self.sched.frames[proc_id].sampled_writes = _ws; self.sched.frames[proc_id].pc = {commit}; return Ok(HdlStep::Delay(_dt));");
                    }
                    return Add(snap + (Kind(statement) == "blocking"
                        ? "for _w in _ws { self.blocking_write(_w)?; } "
                        : $"let _dt = {ticks}; for _w in _ws {{ self.nonblocking_write(_w, _dt)?; }} ") + Cont(next));
                }
                bool selected = Kind(target) is "bitselect" or "partselect" or "indexedselect";
                bool two = selected ? Signals[SignalId(Name(target.GetProperty("value")))].TwoState
                    : Kind(target) == "ref" ? Signals[SignalId(Name(target))].TwoState : Memories[MemoryId(Name(target))].TwoState;
                string val = $"w4::resize({RsExpr64(statement.GetProperty("value"), width)}, {RsMaskHex(width)})";
                if (two) val = $"w4::two_state({val}, {RsMaskHex(width)})";
                string writeExpr;
                if (selected)
                {
                    int id = SignalId(Name(target.GetProperty("value")));
                    int sigW = Signals[id].Width;
                    string basis = Kind(target) == "bitselect" ? RsExpr64(target.GetProperty("index"))
                        : Kind(target) == "partselect" ? $"W4::lit({target.GetProperty("right").GetInt32()}u64)"
                        : RsExpr64(target.GetProperty("base"));
                    bool down = Kind(target) == "indexedselect" && target.GetProperty("descending").GetBoolean();
                    writeExpr = $"hdl_sched::selected_write({id}, {sigW}, {val}, {basis}, {width}, {(down ? "true" : "false")}, {(two ? "true" : "false")})?";
                }
                else if (Kind(target) == "ref")
                {
                    writeExpr = $"HdlWrite {{ signal: {SignalId(Name(target))}, index: None, value: {val}, enable: None }}";
                }
                else
                {
                    int mid = MemoryId(Name(target));
                    writeExpr = $"HdlWrite {{ signal: {mid}, index: Some(hdl_sched::sampled_index({RsExpr64(target.GetProperty("index"))}, {Memories[mid].LowerBound}, {Memories[mid].Depth})?), value: {val}, enable: None }}";
                }
                if (Kind(statement) == "blocking_delay")
                {
                    int commit = Add("let _w = self.sched.frames[proc_id].sampled.take().ok_or(\"missing sampled assignment\")?; self.blocking_write(_w)?; " + Cont(next));
                    return Add($"let _w = {writeExpr}; let _dt = {ticks}; self.sched.frames[proc_id].sampled = Some(_w); self.sched.frames[proc_id].pc = {commit}; return Ok(HdlStep::Delay(_dt));");
                }
                return Add(Kind(statement) == "blocking"
                    ? $"let _w = {writeExpr}; self.blocking_write(_w)?; " + Cont(next)
                    : $"let _w = {writeExpr}; let _dt = {ticks}; self.nonblocking_write(_w, _dt)?; " + Cont(next));
            }
            default:
                throw new IrValidationException("Unsupported timed statement for Rust: " + Kind(statement));
        }
    }

    /// <summary>Emits a zero-unsafe C# 15 explicit-time (#delay) model matching native/rtl/hdl_scheduler.h.</summary>
    private string EmitCSharpTimedModel(string top, string @namespace)
    {
        string className = "V" + top;
        var names = CSharpPortNames();
        if (names.Values.Contains(className)) throw new ArgumentException("Port conflicts with generated class name.", nameof(top));
        var instrs = new List<string> { "return HdlStep.Done;" };
        int[] entries = TimedProcesses.Select(p => CompileCsTimed(p, 0, instrs)).ToArray();

        var code = new StringBuilder();
        code.AppendLine("// <auto-generated/>");
        code.AppendLine("#nullable enable");
        code.AppendLine("using System;");
        code.AppendLine("using System.Collections.Generic;");
        code.AppendLine("using System.Runtime.CompilerServices;");
        code.AppendLine($"namespace {@namespace};");
        code.AppendLine();
        code.AppendLine($"public sealed class {className} : IVpiModel, ITimeTravelModel<{className}>");
        code.AppendLine("{");
        code.AppendLine("    private ulong _time;");
        code.AppendLine("    private bool _finished;");
        code.AppendLine("    private int _exitCode;");
        code.AppendLine("    private VcdWriter? _vcd;");
        code.AppendLine("    private PlusargStore _plusargs = new();");
        code.AppendLine("    private SplitMix64Rng _rng = new();");
        code.AppendLine("    private Dictionary<int, W4>? _forced;");
        code.AppendLine("    private bool _hasForces;");
        code.AppendLine("    private HdlSchedState _sched = new();");
        for (int i = 0; i < Signals.Count; ++i) code.AppendLine($"    private W4 _s{i};");
        for (int i = 0; i < Memories.Count; ++i)
        {
            string mm = CsMaskHex(Memories[i].Width);
            code.AppendLine($"    private W4[] _m{i} = new W4[{Memories[i].Depth}];");
            code.AppendLine($"    private W4 _ReadMem{i}(W4 idx) => W4Ops.Read(_m{i}, idx, {mm});");
        }
        code.AppendLine();
        code.AppendLine($"    public {className}()");
        code.AppendLine("    {");
        for (int i = 0; i < Signals.Count; ++i)
            code.AppendLine(Signals[i].TwoState ? $"        _s{i} = default;" : $"        _s{i} = W4Ops.Unknown({CsMaskHex(Signals[i].Width)});");
        for (int i = 0; i < Memories.Count; ++i)
            if (!Memories[i].TwoState) code.AppendLine($"        Array.Fill(_m{i}, W4Ops.Unknown({CsMaskHex(Memories[i].Width)}));");
        if (HasInitBlock())
        {
            code.AppendLine("        _RunInit();");
        }
        foreach (int entry in entries) code.AppendLine($"        _sched.AddProcess({entry});");
        code.AppendLine("    }");
        if (HasInitBlock())
        {
            code.AppendLine("    private void _RunInit()");
            code.AppendLine("    {");
            int initUnique = 0;
            var emptySet = new HashSet<int>();
            foreach (int sid in Enumerable.Range(0, Signals.Count)) code.AppendLine($"        W4 _next{sid} = _s{sid};");
            CsStmt(code, Initialization, ref initUnique, false, false, emptySet);
            code.AppendLine("    }");
        }
        code.AppendLine("    public ulong Time => _time;");
        code.AppendLine("    public void TimeInc(ulong delta) => _time += delta;");
        code.AppendLine("    public bool EventsPending => _sched.EventsPending;");
        code.AppendLine("    public ulong? NextTimeSlot => _sched.NextTimeSlot;");
        code.AppendLine("    public bool GotFinish => _finished;");
        code.AppendLine("    public void Finish() => _finished = true;");
        code.AppendLine("    public int ExitCode => _exitCode;");
        code.AppendLine("    public IReadOnlyList<string> Plusargs => _plusargs.List;");
        code.AppendLine("    public Action<string>? DisplaySink { get; set; }");
        code.AppendLine("    public void Final() { CloseVcd(); }");
        code.AppendLine("    private static readonly (string Name, int Width)[] _signalSchema = [" + string.Join(", ", Signals.Select(s => $"({CppStringLiteral(s.SourcePath ?? s.Name)}, {s.Width})")) + "];");
        code.AppendLine($"    public void OpenVcd(string path, string timescale = \"1ns\", string[]? filterPrefixes = null) {{ CloseVcd(); _vcd = new VcdWriter(path, \"{top}\", _signalSchema, timescale, false, filterPrefixes); }}");
        code.AppendLine($"    public void OpenVcdGz(string path, string timescale = \"1ns\", string[]? filterPrefixes = null) {{ CloseVcd(); _vcd = new VcdWriter(path, \"{top}\", _signalSchema, timescale, true, filterPrefixes); }}");
        code.AppendLine("    public void DumpVcd() { if (_vcd is null) return; _vcd.Dump(_time, [" + string.Join(", ", Signals.Select((s, idx) => $"_s{idx}.ToW64({s.Width})")) + "]); }");
        code.AppendLine("    public void CloseVcd() { _vcd?.Dispose(); _vcd = null; }");
        code.AppendLine($"    public {className} Fork()");
        code.AppendLine("    {");
        code.AppendLine($"        var c = ({className})MemberwiseClone();");
        code.AppendLine("        c._vcd = null; c._plusargs = _plusargs.Clone(); c._sched = _sched.Clone();");
        code.AppendLine("        if (_forced is not null) c._forced = new Dictionary<int, W4>(_forced);");
        for (int i = 0; i < Memories.Count; ++i) code.AppendLine($"        c._m{i} = (W4[])_m{i}.Clone();");
        code.AppendLine("        return c;");
        code.AppendLine("    }");
        code.AppendLine($"    public {className} Snapshot() => Fork();");
        code.AppendLine($"    public void Restore({className} snap)");
        code.AppendLine("    {");
        code.AppendLine("        ArgumentNullException.ThrowIfNull(snap);");
        code.AppendLine("        _time = snap._time; _finished = snap._finished; _exitCode = snap._exitCode; _rng = snap._rng;");
        code.AppendLine("        _plusargs = snap._plusargs.Clone(); _sched = snap._sched.Clone();");
        code.AppendLine("        _forced = snap._forced is null ? null : new Dictionary<int, W4>(snap._forced); _hasForces = snap._hasForces;");
        for (int i = 0; i < Signals.Count; ++i) code.AppendLine($"        _s{i} = snap._s{i};");
        for (int i = 0; i < Memories.Count; ++i) code.AppendLine($"        _m{i} = (W4[])snap._m{i}.Clone();");
        code.AppendLine("    }");
        code.AppendLine($"    string ITimeTravelModel<{className}>.TtTopName => \"{top}\";");
        code.AppendLine($"    (string Name, int Width)[] ITimeTravelModel<{className}>.TtSignalSchema => _signalSchema;");
        code.AppendLine($"    ulong ITimeTravelModel<{className}>.TtTime => _time;");
        code.AppendLine($"    W64[] ITimeTravelModel<{className}>.TtSignalValues() => [" + string.Join(", ", Signals.Select((s, idx) => $"_s{idx}.ToW64({s.Width})")) + "];");
        code.AppendLine($"    bool ITimeTravelModel<{className}>.TtHasFailure() => _exitCode != 0;");
        foreach (var sig in Signals.Where(s => s.Direction != "internal"))
        {
            int id = SignalId(sig.Name); string prop = names[sig.Name]; string sm = CsMaskHex(sig.Width);
            if (sig.Direction == "input")
            {
                code.AppendLine($"    public ulong {prop} {{ get => W4Ops.ToUInt64(_s{id}); set => _s{id} = new W4({(sig.Width >= 64 ? "value" : $"value & {sm}")}, 0UL); }}");
                code.AppendLine($"    public W64 Get_{prop}_Word() => _s{id}.ToW64({sig.Width});");
                code.AppendLine($"    public void Set_{prop}_Word(W64 value) => _s{id} = {(sig.TwoState ? $"W4Ops.TwoState(W4.FromW64(value, {sm}), {sm})" : $"W4.FromW64(value, {sm})")};");
                code.AppendLine($"    public string Get_{prop}_Bits() => W64Ops.Text(_s{id}.ToW64({sig.Width}));");
                code.AppendLine($"    public void Set_{prop}_Bits(string bits) => Set_{prop}_Word(W64Ops.Parse(bits, {sig.Width}));");
            }
            else
            {
                code.AppendLine($"    public ulong {prop} {{ get => W4Ops.ToUInt64(_s{id}); }}");
                code.AppendLine($"    public W64 Get_{prop}_Word() => _s{id}.ToW64({sig.Width});");
                code.AppendLine($"    public string Get_{prop}_Bits() => W64Ops.Text(_s{id}.ToW64({sig.Width}));");
            }
        }
        code.AppendLine("    private W4 _GetSig(int id) => id switch { " + string.Join(" ", Signals.Select((_, i) => $"{i} => _s{i},")) + " _ => throw new ArgumentOutOfRangeException(nameof(id)) };");
        code.AppendLine("    private void _SetSig(int id, W4 v) { switch (id) { " + string.Join(" ", Signals.Select((_, i) => $"case {i}: _s{i} = v; break;")) + " default: throw new ArgumentOutOfRangeException(nameof(id)); } }");
        code.AppendLine("    private void _ApplyWrite(HdlWrite w)");
        code.AppendLine("    {");
        code.AppendLine("        if (w.Index >= 0)");
        code.AppendLine("        {");
        code.AppendLine("            switch (w.Signal)");
        code.AppendLine("            {");
        for (int i = 0; i < Memories.Count; ++i)
            code.AppendLine($"                case {i}: {{ W4 before = _m{i}[w.Index]; W4 after = HdlSched.Merge(before, w); _m{i}[w.Index] = after; _sched.Changed({i}, before, after, true); break; }}");
        code.AppendLine("                default: throw new ArgumentOutOfRangeException(nameof(w));");
        code.AppendLine("            }");
        code.AppendLine("        }");
        code.AppendLine("        else");
        code.AppendLine("        {");
        code.AppendLine("            W4 before = _GetSig(w.Signal); W4 after = HdlSched.Merge(before, w); _SetSig(w.Signal, after); _sched.Changed(w.Signal, before, after, false);");
        code.AppendLine("        }");
        code.AppendLine("    }");
        code.AppendLine("    private void _Blocking(HdlWrite w) => _ApplyWrite(w);");
        code.AppendLine("    private void _Nonblocking(HdlWrite w, ulong ticks) => _sched.EnqueueNba(w, ticks);");
        code.AppendLine("    private void _Trigger(int eid) => _sched.Trigger(eid);");
        code.AppendLine("    private void _NonblockingTrigger(int eid) => _sched.EnqueueNbaTrigger(eid);");
        code.AppendLine("    private HdlStep _Fork(int procId, int[] entries, HdlJoin join) => _sched.Fork(procId, entries, join);");
        code.AppendLine("    private HdlStep _WaitFork(int procId) => _sched.WaitFork(procId);");
        code.AppendLine("    private void _DisableFork(int procId) => _sched.DisableFork(procId);");
        code.AppendLine("    private HdlStep _Execute(int procId, HdlFrame frame)");
        code.AppendLine("    {");
        code.AppendLine("        switch (frame.Pc)");
        code.AppendLine("        {");
        for (int i = 0; i < instrs.Count; ++i) code.AppendLine($"            case {i}: {{ {instrs[i]} }}");
        code.AppendLine("            default: throw new InvalidOperationException(\"invalid timed process PC\");");
        code.AppendLine("        }");
        code.AppendLine("    }");
        code.AppendLine("    public void Eval()");
        code.AppendLine("    {");
        code.AppendLine("        var c = Fork();");
        var inputIds = Signals.Where(s => s.Direction == "input").Select(s => SignalId(s.Name)).ToArray();
        code.AppendLine("        HdlWrite[] drives = [" + string.Join(", ", inputIds.Select(id => $"new HdlWrite({id}, -1, _s{id}, false, 0UL)")) + "];");
        code.AppendLine("        c._sched.DriveAt(_time, drives, c._ApplyWrite, c._Execute);");
        code.AppendLine("        Restore(c);");
        code.AppendLine("    }");
        code.AppendLine($"    public string ModelName => {CppStringLiteral(top)};");
        code.AppendLine("    private static readonly VpiSignalInfo[] s_vpiSignals = [" + string.Join(", ", Signals.Select((s, idx) =>
            $"new VpiSignalInfo({idx}, {CppStringLiteral(s.SourcePath ?? s.Name)}, {s.Width}, {(s.Direction == "input" ? "VpiDirection.Input" : s.Direction == "output" ? "VpiDirection.Output" : "VpiDirection.Internal")}, {(s.Signed ? "true" : "false")}, {(s.TwoState ? "true" : "false")})")) + "];");
        code.AppendLine("    private static readonly VpiMemoryInfo[] s_vpiMemories = [" + string.Join(", ", Memories.Select((m, idx) =>
            $"new VpiMemoryInfo({idx}, {CppStringLiteral(m.Name)}, {m.Depth}, {m.Width}, {(m.TwoState ? "true" : "false")})")) + "];");
        code.AppendLine("    public IReadOnlyList<VpiSignalInfo> Signals => s_vpiSignals;");
        code.AppendLine("    public IReadOnlyList<VpiMemoryInfo> Memories => s_vpiMemories;");
        code.AppendLine("    public int SignalByName(string name) => VpiLookup.SignalByName(ModelName, s_vpiSignals, name);");
        code.AppendLine("    public W64 GetSignal(int id) => id switch { " + string.Join(" ", Signals.Select((s, i) => $"{i} => _s{i}.ToW64({s.Width}),")) + " _ => throw new ArgumentOutOfRangeException(nameof(id)) };");
        code.AppendLine("    public void PutSignal(int id, W64 value) { if (_finished) throw new InvalidOperationException(\"Model is finished.\"); if ((uint)id >= (uint)s_vpiSignals.Length || s_vpiSignals[id].Direction != VpiDirection.Input) throw new InvalidOperationException(\"Only input ports may be deposited.\"); switch (id) { " + string.Join(" ", Signals.Where(s => s.Direction == "input").Select(s => { int id = SignalId(s.Name); string m = CsMaskHex(s.Width); return $"case {id}: _s{id} = {(s.TwoState ? $"new W4(value.V & ~(value.X | value.Z) & {m}, 0UL)" : $"W4.FromW64(in value, {m})")}; break;"; })) + " } }");
        code.AppendLine("    public W64 ReadMemory(int memId, int addr) => memId switch { " + string.Join(" ", Memories.Select((m, i) => $"{i} => _m{i}[addr].ToW64({m.Width}),")) + " _ => throw new ArgumentOutOfRangeException(nameof(memId)) };");
        code.AppendLine("    public void WriteMemory(int memId, int addr, W64 value) { switch (memId) { " + string.Join(" ", Memories.Select((m, i) => $"case {i}: _m{i}[addr] = W4.FromW64(in value, {CsMaskHex(m.Width)}); break;")) + " default: throw new ArgumentOutOfRangeException(nameof(memId)); } }");
        code.AppendLine("    public void ForceSignal(int id, W64 value) { _forced ??= new(); _SetSig(id, W4.FromW64(in value, 0xFFFFFFFFFFFFFFFFUL)); _forced[id] = _GetSig(id); _hasForces = true; }");
        code.AppendLine("    public void ReleaseSignal(int id) { if (_forced is not null) { _forced.Remove(id); _hasForces = _forced.Count > 0; } }");
        code.AppendLine("    public bool IsForced(int id) => _forced is not null && _forced.ContainsKey(id);");
        code.AppendLine("    public int LoadMemoryHex(int memId, string path, int startAddr = 0, int endAddr = int.MaxValue) => _LoadMem(memId, path, true, startAddr, endAddr);");
        code.AppendLine("    public int LoadMemoryBin(int memId, string path, int startAddr = 0, int endAddr = int.MaxValue) => _LoadMem(memId, path, false, startAddr, endAddr);");
        code.AppendLine("    private int _LoadMem(int memId, string path, bool hex, int startAddr, int endAddr) => memId switch { " + string.Join(" ", Memories.Select((m, i) => $"{i} => MemFileIO.LoadW4(_m{i}, {m.Width}, path, hex, startAddr, endAddr, {(m.TwoState ? "true" : "false")}),")) + " _ => throw new ArgumentOutOfRangeException(nameof(memId)) };");
        code.AppendLine("}");
        return code.ToString();
    }

    /// <summary>Emits a #![forbid(unsafe_code)] Rust 2021 explicit-time (#delay) model matching native/rtl/hdl_scheduler.h.</summary>
    private string EmitRustTimedModel(string top)
    {
        string structName = "V" + top;
        var names = RustPortNames();
        if (names.Values.Contains(structName)) throw new ArgumentException("Port conflicts with generated struct name.", nameof(top));
        var instrs = new List<string> { "return Ok(HdlStep::Done);" };
        int[] entries = TimedProcesses.Select(p => CompileRsTimed(p, 0, instrs)).ToArray();

        var code = new StringBuilder();
        code.AppendLine("#![forbid(unsafe_code)]");
        code.AppendLine("#![allow(non_camel_case_types, non_snake_case, unused_variables, unused_mut, unused_parens, unused_imports, unused_assignments, dead_code, clippy::all)]");
        code.AppendLine("pub mod runtime;");
        code.AppendLine("pub use runtime::vpi;");
        code.AppendLine("pub use runtime::{TimeTravelModel, TimeTravelRecorder};");
        code.AppendLine("use runtime::{hdl_sched, w4, w64, HdlEdge, HdlJoin, HdlSchedState, HdlStep, HdlWatch, HdlWrite, PlusargStore, SplitMix64Rng, VcdSlot, W4, W64};");
        code.AppendLine();
        code.AppendLine("#[derive(Clone)]");
        code.AppendLine($"pub struct {structName} {{");
        code.AppendLine("    time: u64, finished: bool, exit_code: i32,");
        code.AppendLine("    vcd: VcdSlot, plusargs: PlusargStore, rng: SplitMix64Rng,");
        code.AppendLine("    forced: Vec<(usize, W4)>, sched: HdlSchedState,");
        for (int i = 0; i < Signals.Count; ++i) code.AppendLine($"    s{i}: W4,");
        for (int i = 0; i < Memories.Count; ++i) code.AppendLine($"    m{i}: Vec<W4>,");
        code.AppendLine("}");
        code.AppendLine();
        code.AppendLine($"impl Default for {structName} {{ fn default() -> Self {{ Self::new() }} }}");
        code.AppendLine();
        code.AppendLine($"impl {structName} {{");
        code.AppendLine("    pub fn new() -> Self {");
        code.AppendLine("        let mut m = Self {");
        code.AppendLine("            time: 0, finished: false, exit_code: 0, vcd: VcdSlot::new(),");
        code.AppendLine("            plusargs: PlusargStore::new(), rng: SplitMix64Rng::new(),");
        code.AppendLine("            forced: Vec::new(), sched: HdlSchedState::new(),");
        for (int i = 0; i < Signals.Count; ++i)
            code.AppendLine(Signals[i].TwoState ? $"            s{i}: W4::lit(0u64)," : $"            s{i}: W4::unknown({RsMaskHex(Signals[i].Width)}),");
        for (int i = 0; i < Memories.Count; ++i)
            code.AppendLine(Memories[i].TwoState ? $"            m{i}: vec![W4::lit(0u64); {Memories[i].Depth}]," : $"            m{i}: vec![W4::unknown({RsMaskHex(Memories[i].Width)}); {Memories[i].Depth}],");
        code.AppendLine("        };");
        if (HasInitBlock()) code.AppendLine("        m.run_init();");
        foreach (int entry in entries) code.AppendLine($"        m.sched.add_process({entry});");
        code.AppendLine("        m");
        code.AppendLine("    }");
        if (HasInitBlock())
        {
            code.AppendLine("    fn run_init(&mut self) {");
            for (int sid = 0; sid < Signals.Count; ++sid) code.AppendLine($"        let mut next{sid} = self.s{sid};");
            int initUnique = 0;
            RsStmt(code, Initialization, ref initUnique, false, false);
            code.AppendLine("    }");
        }
        code.AppendLine("    pub fn time(&self) -> u64 { self.time }");
        code.AppendLine("    pub fn time_inc(&mut self, delta: u64) { self.time = self.time.wrapping_add(delta); }");
        code.AppendLine("    pub fn events_pending(&self) -> bool { self.sched.events_pending() }");
        code.AppendLine("    pub fn next_time_slot(&self) -> Option<u64> { self.sched.next_time_slot() }");
        code.AppendLine("    pub fn final_model(&mut self) { self.close_vcd(); }");
        code.AppendLine("    pub fn finish(&mut self) { self.finished = true; }");
        code.AppendLine("    pub fn got_finish(&self) -> bool { self.finished }");
        code.AppendLine("    pub fn exit_code(&self) -> i32 { self.exit_code }");
        code.AppendLine("    pub fn snapshot(&self) -> Self { self.clone() }");
        code.AppendLine("    pub fn restore(&mut self, snap: &Self) { let vcd = self.vcd.take(); *self = snap.clone(); self.vcd = vcd; }");
        code.AppendLine("    pub fn fork(&self) -> Self { self.clone() }");
        code.AppendLine("    const SIGNAL_SCHEMA: &'static [(&'static str, i32)] = &[" + string.Join(", ", Signals.Select(s => $"({CppStringLiteral(s.SourcePath ?? s.Name)}, {s.Width})")) + "];");
        code.AppendLine($"    pub fn open_vcd(&mut self, path: &str, timescale: &str) -> std::io::Result<()> {{ self.vcd.open(path, \"{top}\", Self::SIGNAL_SCHEMA, timescale, false, &[]) }}");
        code.AppendLine($"    pub fn open_vcd_gz(&mut self, path: &str, timescale: &str) -> std::io::Result<()> {{ self.vcd.open(path, \"{top}\", Self::SIGNAL_SCHEMA, timescale, true, &[]) }}");
        code.AppendLine("    pub fn dump_vcd(&mut self) -> std::io::Result<()> { self.vcd.dump(self.time, &[" + string.Join(", ", Signals.Select((s, idx) => $"self.s{idx}.to_w64({s.Width})")) + "]) }");
        code.AppendLine("    pub fn close_vcd(&mut self) { self.vcd.close(); }");
        foreach (var sig in Signals.Where(s => s.Direction != "internal"))
        {
            int id = SignalId(sig.Name); string port = names[sig.Name]; string m = RsMaskHex(sig.Width);
            if (sig.Direction == "input")
            {
                code.AppendLine($"    pub fn set_{port}(&mut self, value: u64) {{ self.s{id} = W4::lit(value & {m}); }}");
                code.AppendLine($"    pub fn {port}(&self) -> u64 {{ w4::to_u64(self.s{id}).expect(\"integer conversion of X/Z port\") }}");
                code.AppendLine($"    pub fn {port}_w64(&self) -> W64 {{ self.s{id}.to_w64({sig.Width}) }}");
                code.AppendLine($"    pub fn set_{port}_w64(&mut self, value: W64) {{ self.s{id} = {(sig.TwoState ? $"W4::lit(value.v & !(value.x | value.z) & {m})" : $"W4::from_w64(value, {m})")}; }}");
                code.AppendLine($"    pub fn {port}_bits(&self) -> String {{ w64::text(self.s{id}.to_w64({sig.Width})) }}");
            }
            else
            {
                code.AppendLine($"    pub fn {port}(&self) -> u64 {{ w4::to_u64(self.s{id}).expect(\"integer conversion of X/Z port\") }}");
                code.AppendLine($"    pub fn {port}_w64(&self) -> W64 {{ self.s{id}.to_w64({sig.Width}) }}");
                code.AppendLine($"    pub fn {port}_bits(&self) -> String {{ w64::text(self.s{id}.to_w64({sig.Width})) }}");
            }
        }
        code.AppendLine("    fn get_sig(&self, id: usize) -> Result<W4, &'static str> { match id { " + string.Join(" ", Signals.Select((_, i) => $"{i} => Ok(self.s{i}),")) + " _ => Err(\"invalid signal\") } }");
        code.AppendLine("    fn set_sig(&mut self, id: usize, v: W4) -> Result<(), &'static str> { match id { " + string.Join(" ", Signals.Select((_, i) => $"{i} => {{ self.s{i} = v; Ok(()) }}")) + " _ => Err(\"invalid signal\") } }");
        code.AppendLine("    fn apply_write(&mut self, w: HdlWrite) -> Result<(), &'static str> {");
        code.AppendLine("        if let Some(idx) = w.index {");
        code.AppendLine("            match w.signal {");
        for (int i = 0; i < Memories.Count; ++i)
            code.AppendLine($"                {i} => {{ if idx >= self.m{i}.len() {{ return Err(\"mem out of range\"); }} let before = self.m{i}[idx]; let after = hdl_sched::merge(before, &w); self.m{i}[idx] = after; self.sched.changed({i}, before, after, true)?; Ok(()) }}");
        code.AppendLine("                _ => Err(\"invalid memory\"),");
        code.AppendLine("            }");
        code.AppendLine("        } else {");
        code.AppendLine("            let before = self.get_sig(w.signal)?; let after = hdl_sched::merge(before, &w); self.set_sig(w.signal, after)?; self.sched.changed(w.signal, before, after, false)?; Ok(())");
        code.AppendLine("        }");
        code.AppendLine("    }");
        code.AppendLine("    fn blocking_write(&mut self, w: HdlWrite) -> Result<(), &'static str> { self.apply_write(w) }");
        code.AppendLine("    fn nonblocking_write(&mut self, w: HdlWrite, ticks: u64) -> Result<(), &'static str> { self.sched.enqueue_nba(w, ticks) }");
        code.AppendLine("    fn trigger_event(&mut self, eid: usize) -> Result<(), &'static str> { self.sched.trigger(eid) }");
        code.AppendLine("    fn nonblocking_trigger(&mut self, eid: usize) -> Result<(), &'static str> { self.sched.enqueue_nba_trigger(eid) }");
        code.AppendLine("    fn fork_proc(&mut self, proc_id: usize, entries: &[usize], join: HdlJoin) -> Result<HdlStep, &'static str> { self.sched.fork(proc_id, entries, join) }");
        code.AppendLine("    fn wait_fork_proc(&mut self, proc_id: usize) -> Result<HdlStep, &'static str> { self.sched.wait_fork(proc_id) }");
        code.AppendLine("    fn disable_fork_proc(&mut self, proc_id: usize) { self.sched.disable_fork(proc_id); }");
        code.AppendLine("    fn exec_step(&mut self, proc_id: usize) -> Result<HdlStep, &'static str> {");
        code.AppendLine("        let pc = self.sched.frames[proc_id].pc;");
        code.AppendLine("        match pc {");
        for (int i = 0; i < instrs.Count; ++i) code.AppendLine($"            {i} => {{ {instrs[i]} }}");
        code.AppendLine("            _ => Err(\"invalid timed process PC\"),");
        code.AppendLine("        }");
        code.AppendLine("    }");
        code.AppendLine("    fn settle_slot(&mut self) -> Result<(), &'static str> {");
        code.AppendLine("        while !self.sched.active.is_empty() || !self.sched.inactive.is_empty() || !self.sched.nba.is_empty() {");
        code.AppendLine("            if self.sched.active.is_empty() {");
        code.AppendLine("                if !self.sched.inactive.is_empty() { std::mem::swap(&mut self.sched.active, &mut self.sched.inactive); }");
        code.AppendLine("                else {");
        code.AppendLine("                    let batch = std::mem::take(&mut self.sched.nba);");
        code.AppendLine("                    for act in batch { self.sched.consume()?; self.sched.pending -= 1; match act { runtime::HdlNba::Write(w) => self.apply_write(w)?, runtime::HdlNba::Trigger(e) => self.sched.trigger(e)? } }");
        code.AppendLine("                    continue;");
        code.AppendLine("                }");
        code.AppendLine("            }");
        code.AppendLine("            let id = self.sched.active.pop_front().unwrap(); self.sched.pending -= 1;");
        code.AppendLine("            self.sched.frames[id].queued = false;");
        code.AppendLine("            if self.sched.frames[id].finished { continue; }");
        code.AppendLine("            loop {");
        code.AppendLine("                self.sched.consume()?;");
        code.AppendLine("                match self.exec_step(id)? {");
        code.AppendLine("                    HdlStep::Again => continue,");
        code.AppendLine("                    HdlStep::Done => { self.sched.complete(id)?; break; }");
        code.AppendLine("                    HdlStep::Join => { self.sched.finish_join(id)?; break; }");
        code.AppendLine("                    HdlStep::Wait => { self.sched.frames[id].waiting = true; self.sched.release_children(id)?; break; }");
        code.AppendLine("                    HdlStep::Delay(ticks) => { self.sched.schedule_delay(id, ticks)?; break; }");
        code.AppendLine("                }");
        code.AppendLine("            }");
        code.AppendLine("        }");
        code.AppendLine("        Ok(())");
        code.AppendLine("    }");
        code.AppendLine("    pub fn try_eval(&mut self) -> Result<(), &'static str> {");
        code.AppendLine("        let mut c = self.fork();");
        code.AppendLine("        let target = self.time;");
        code.AppendLine("        if target < c.sched.now { return Err(\"HDL time went backward\"); }");
        code.AppendLine("        if !c.sched.started { c.sched.started = true; c.sched.remaining = 20000; c.settle_slot()?; }");
        code.AppendLine("        while let Some((&next_t, _)) = c.sched.future.first_key_value() {");
        code.AppendLine("            if next_t > target { break; }");
        code.AppendLine("            let tick = c.sched.future.remove(&next_t).unwrap();");
        code.AppendLine("            c.sched.now = next_t; c.sched.remaining = 20000;");
        code.AppendLine("            c.sched.active.extend(tick.active); c.sched.nba.extend(tick.nba);");
        code.AppendLine("            c.settle_slot()?;");
        code.AppendLine("        }");
        code.AppendLine("        c.sched.now = target; c.sched.remaining = 20000;");
        var rsInputIds = Signals.Where(s => s.Direction == "input").Select(s => SignalId(s.Name)).ToArray();
        foreach (int id in rsInputIds)
            code.AppendLine($"        c.apply_write(HdlWrite {{ signal: {id}, index: None, value: self.s{id}, enable: None }})?;");
        code.AppendLine("        c.settle_slot()?;");
        code.AppendLine("        self.restore(&c);");
        code.AppendLine("        Ok(())");
        code.AppendLine("    }");
        code.AppendLine("    pub fn eval(&mut self) { self.try_eval().expect(\"timed eval failed\"); }");
        code.AppendLine("    pub fn load_memory_hex(&mut self, mem_id: usize, path: &str, start: usize, end: usize) -> std::io::Result<usize> { self.load_mem(mem_id, path, true, start, end) }");
        code.AppendLine("    pub fn load_memory_bin(&mut self, mem_id: usize, path: &str, start: usize, end: usize) -> std::io::Result<usize> { self.load_mem(mem_id, path, false, start, end) }");
        code.AppendLine("    fn load_mem(&mut self, mem_id: usize, path: &str, hex: bool, start: usize, end: usize) -> std::io::Result<usize> { match mem_id { " + string.Join(" ", Memories.Select((m, i) => $"{i} => runtime::load_mem_file_w4(&mut self.m{i}, {m.Width}, path, hex, start, end, {(m.TwoState ? "true" : "false")}),")) + " _ => Err(std::io::Error::new(std::io::ErrorKind::InvalidInput, \"invalid mem_id\")) } }");
        code.AppendLine("}");
        code.AppendLine($"impl TimeTravelModel for {structName} {{");
        code.AppendLine($"    fn tt_top_name(&self) -> &'static str {{ \"{top}\" }}");
        code.AppendLine("    fn tt_signal_schema(&self) -> &'static [(&'static str, i32)] { Self::SIGNAL_SCHEMA }");
        code.AppendLine("    fn tt_time(&self) -> u64 { self.time }");
        code.AppendLine("    fn tt_signal_values(&self) -> Vec<W64> { vec![" + string.Join(", ", Signals.Select((s, idx) => $"self.s{idx}.to_w64({s.Width})")) + "] }");
        code.AppendLine("    fn tt_has_failure(&self) -> bool { self.exit_code != 0 }");
        code.AppendLine("    fn tt_restore(&mut self, snap: &Self) { self.restore(snap); }");
        code.AppendLine("}");
        return code.ToString();
    }

    internal static string EmitCSharpParityRuntimeHelpers() => """
        public static class ContainerOps
        {
            public const int Capacity = 256;
            private static W4 ElemInit(int w, bool twoState) => twoState ? default : W4Ops.Unknown(w >= 64 ? ulong.MaxValue : (1UL << w) - 1UL);
            private static W4 ElemConv(W4 v, int w, bool twoState) { ulong m = w >= 64 ? ulong.MaxValue : (1UL << w) - 1UL; return twoState ? W4Ops.TwoState(v, m) : W4Ops.Resize(v, m); }
            public static W4 ArrayIndex(W4[] indices, (int Left, int Right)[] dims)
            {
                ulong offset = 0, prod = 1;
                for (int i = 0; i < indices.Length; ++i)
                {
                    int lo = Math.Min(dims[i].Left, dims[i].Right), hi = Math.Max(dims[i].Left, dims[i].Right);
                    ulong sz = (ulong)(hi - lo) + 1UL;
                    prod *= sz;
                    if (prod > Capacity) throw new InvalidOperationException("array dimension product");
                    W4 v = indices[i];
                    if (v.B != 0UL || v.A < (ulong)lo || v.A > (ulong)hi) return new W4(ulong.MaxValue, ulong.MaxValue);
                    offset = offset * sz + (v.A - (ulong)lo);
                }
                return new W4(offset, 0UL);
            }
            public static ulong ArrayIndex2(ulong[] indices, (int Left, int Right)[] dims)
            {
                ulong offset = 0;
                for (int i = 0; i < indices.Length; ++i)
                {
                    int lo = Math.Min(dims[i].Left, dims[i].Right), hi = Math.Max(dims[i].Left, dims[i].Right);
                    ulong sz = (ulong)(hi - lo) + 1UL;
                    ulong v = indices[i];
                    if (v < (ulong)lo || v > (ulong)hi) return ulong.MaxValue;
                    offset = offset * sz + (v - (ulong)lo);
                }
                return offset;
            }
            public static void DynAlloc(List<W4> mem, W4 count, int countW, int elemW, bool twoState, bool preserve, bool isSigned)
            {
                if (count.B != 0UL || (isSigned && (count.A & (1UL << (countW - 1))) != 0UL))
                    throw new ArgumentException("dynamic array count must be known and nonnegative");
                if (count.A > Capacity) throw new InvalidOperationException("dynamic array capacity exceeds 256");
                int n = (int)count.A;
                var init = ElemInit(elemW, twoState);
                var next = new List<W4>(n);
                int copy = preserve ? Math.Min(n, mem.Count) : 0;
                for (int i = 0; i < copy; ++i) next.Add(mem[i]);
                for (int i = copy; i < n; ++i) next.Add(init);
                mem.Clear(); mem.AddRange(next);
            }
            public static void DynAlloc2(List<ulong> mem, ulong count, int countW, bool preserve, bool isSigned)
            {
                if (isSigned && (count & (1UL << (countW - 1))) != 0UL)
                    throw new ArgumentException("dynamic array count must be nonnegative");
                if (count > Capacity) throw new InvalidOperationException("dynamic array capacity exceeds 256");
                int n = (int)count;
                var next = new List<ulong>(n);
                int copy = preserve ? Math.Min(n, mem.Count) : 0;
                for (int i = 0; i < copy; ++i) next.Add(mem[i]);
                for (int i = copy; i < n; ++i) next.Add(0UL);
                mem.Clear(); mem.AddRange(next);
            }
            public static W4 DynRead(List<W4> mem, W4 idx, int elemW, bool twoState) =>
                (idx.B != 0UL || idx.A >= (ulong)mem.Count) ? ElemInit(elemW, twoState) : mem[(int)idx.A];
            public static ulong DynRead2(List<ulong> mem, ulong idx) => idx >= (ulong)mem.Count ? 0UL : mem[(int)idx];
            public static void DynWrite(List<W4> mem, W4 idx, W4 val, int elemW, bool twoState)
            {
                if (idx.B == 0UL && idx.A < (ulong)mem.Count) mem[(int)idx.A] = ElemConv(val, elemW, twoState);
            }
            public static void DynWrite2(List<ulong> mem, ulong idx, ulong val, int elemW)
            {
                if (idx < (ulong)mem.Count) mem[(int)idx] = W2Ops.Resize(val, elemW);
            }
            public static void QueuePush(List<W4> mem, W4 val, int elemW, bool twoState, bool front)
            {
                if (mem.Count >= Capacity) throw new InvalidOperationException("queue capacity exceeds 256");
                var cv = ElemConv(val, elemW, twoState);
                if (front) mem.Insert(0, cv); else mem.Add(cv);
            }
            public static void QueuePush2(List<ulong> mem, ulong val, int elemW, bool front)
            {
                if (mem.Count >= Capacity) throw new InvalidOperationException("queue capacity exceeds 256");
                ulong cv = W2Ops.Resize(val, elemW);
                if (front) mem.Insert(0, cv); else mem.Add(cv);
            }
            public static W4 QueuePop(List<W4> mem, int elemW, bool twoState, bool front)
            {
                if (mem.Count == 0) return ElemInit(elemW, twoState);
                int idx = front ? 0 : mem.Count - 1;
                W4 v = mem[idx]; mem.RemoveAt(idx); return v;
            }
            public static ulong QueuePop2(List<ulong> mem, bool front)
            {
                if (mem.Count == 0) return 0UL;
                int idx = front ? 0 : mem.Count - 1;
                ulong v = mem[idx]; mem.RemoveAt(idx); return v;
            }
            public static void QueueDelete(List<W4> mem, W4 idx) { if (idx.B == 0UL && idx.A < (ulong)mem.Count) mem.RemoveAt((int)idx.A); }
            public static void QueueDelete2(List<ulong> mem, ulong idx) { if (idx < (ulong)mem.Count) mem.RemoveAt((int)idx); }
            public static void QueueWrite(List<W4> mem, W4 idx, W4 val, int elemW, bool twoState)
            {
                if (idx.B != 0UL || idx.A > (ulong)mem.Count) return;
                if (idx.A == (ulong)mem.Count) QueuePush(mem, val, elemW, twoState, false);
                else mem[(int)idx.A] = ElemConv(val, elemW, twoState);
            }
            public static void QueueWrite2(List<ulong> mem, ulong idx, ulong val, int elemW)
            {
                if (idx > (ulong)mem.Count) return;
                if (idx == (ulong)mem.Count) QueuePush2(mem, val, elemW, false);
                else mem[(int)idx] = W2Ops.Resize(val, elemW);
            }
            private static ulong AssocEncode(ulong k, int kw, bool ks)
            {
                ulong km = kw >= 64 ? ulong.MaxValue : (1UL << kw) - 1UL;
                return (k & km) ^ (ks ? (1UL << (kw - 1)) : 0UL);
            }
            public static W4 AssocRead(SortedDictionary<ulong, W4> map, W4 key, int kw, bool ks, int elemW, bool twoState)
            {
                ulong km = kw >= 64 ? ulong.MaxValue : (1UL << kw) - 1UL;
                var rk = W4Ops.Resize(key, km);
                if (rk.B != 0UL) return ElemInit(elemW, twoState);
                return map.TryGetValue(AssocEncode(rk.A, kw, ks), out var v) ? v : ElemInit(elemW, twoState);
            }
            public static ulong AssocRead2(SortedDictionary<ulong, ulong> map, ulong key, int kw, bool ks) =>
                map.TryGetValue(AssocEncode(key, kw, ks), out var v) ? v : 0UL;
            public static W4 AssocExists(SortedDictionary<ulong, W4> map, W4 key, int kw, bool ks)
            {
                ulong km = kw >= 64 ? ulong.MaxValue : (1UL << kw) - 1UL;
                var rk = W4Ops.Resize(key, km);
                return (rk.B == 0UL && map.ContainsKey(AssocEncode(rk.A, kw, ks))) ? new W4(1UL, 0UL) : default;
            }
            public static ulong AssocExists2(SortedDictionary<ulong, ulong> map, ulong key, int kw, bool ks) =>
                map.ContainsKey(AssocEncode(key, kw, ks)) ? 1UL : 0UL;
            public static void AssocWrite(SortedDictionary<ulong, W4> map, W4 key, W4 val, int kw, bool ks, int elemW, bool twoState)
            {
                ulong km = kw >= 64 ? ulong.MaxValue : (1UL << kw) - 1UL;
                var rk = W4Ops.Resize(key, km);
                if (rk.B != 0UL) return;
                ulong ek = AssocEncode(rk.A, kw, ks);
                if (!map.ContainsKey(ek) && map.Count >= Capacity) throw new InvalidOperationException("associative array capacity exceeds 256");
                map[ek] = ElemConv(val, elemW, twoState);
            }
            public static void AssocWrite2(SortedDictionary<ulong, ulong> map, ulong key, ulong val, int kw, bool ks, int elemW)
            {
                ulong ek = AssocEncode(key, kw, ks);
                if (!map.ContainsKey(ek) && map.Count >= Capacity) throw new InvalidOperationException("associative array capacity exceeds 256");
                map[ek] = W2Ops.Resize(val, elemW);
            }
            public static void AssocDelete(SortedDictionary<ulong, W4> map, W4 key, int kw, bool ks)
            {
                ulong km = kw >= 64 ? ulong.MaxValue : (1UL << kw) - 1UL;
                var rk = W4Ops.Resize(key, km);
                if (rk.B == 0UL) map.Remove(AssocEncode(rk.A, kw, ks));
            }
            public static void AssocDelete2(SortedDictionary<ulong, ulong> map, ulong key, int kw, bool ks) =>
                map.Remove(AssocEncode(key, kw, ks));
            public static W4 AssocTraverse(SortedDictionary<ulong, W4> map, ref W4 key, int kw, bool ks, int op)
            {
                if (map.Count == 0) return default;
                ulong found = 0; bool ok = false;
                if (op == 0) { foreach (var k in map.Keys) { found = k; ok = true; break; } }
                else if (op == 1) { foreach (var k in map.Keys) { found = k; ok = true; } }
                else
                {
                    ulong km = kw >= 64 ? ulong.MaxValue : (1UL << kw) - 1UL;
                    var rk = W4Ops.Resize(key, km);
                    if (rk.B != 0UL) return default;
                    ulong ek = AssocEncode(rk.A, kw, ks);
                    if (op == 2) { foreach (var k in map.Keys) if (k > ek) { found = k; ok = true; break; } }
                    else { foreach (var k in map.Keys) { if (k < ek) { found = k; ok = true; } else break; } }
                }
                if (!ok) return default;
                key = new W4(found ^ (ks ? (1UL << (kw - 1)) : 0UL), 0UL);
                return new W4(1UL, 0UL);
            }
            public static ulong AssocTraverse2(SortedDictionary<ulong, ulong> map, ref ulong key, int kw, bool ks, int op)
            {
                if (map.Count == 0) return 0UL;
                ulong found = 0; bool ok = false;
                if (op == 0) { foreach (var k in map.Keys) { found = k; ok = true; break; } }
                else if (op == 1) { foreach (var k in map.Keys) { found = k; ok = true; } }
                else
                {
                    ulong ek = AssocEncode(key, kw, ks);
                    if (op == 2) { foreach (var k in map.Keys) if (k > ek) { found = k; ok = true; break; } }
                    else { foreach (var k in map.Keys) { if (k < ek) { found = k; ok = true; } else break; } }
                }
                if (!ok) return 0UL;
                key = found ^ (ks ? (1UL << (kw - 1)) : 0UL);
                return 1UL;
            }
        }

        public static class MemFileIO
        {
            private static W4 ParseTok(string tok, int width, bool hex, bool twoState)
            {
                ulong a = 0, b = 0;
                int shift = hex ? 4 : 1;
                foreach (char raw in tok)
                {
                    if (raw == '_') continue;
                    char c = char.ToLowerInvariant(raw);
                    a <<= shift; b <<= shift;
                    if (c == 'x') { ulong f = hex ? 0xFUL : 1UL; a |= f; b |= f; }
                    else if (c is 'z' or '?') { b |= hex ? 0xFUL : 1UL; }
                    else if (c is >= '0' and <= '9') { a |= (uint)(c - '0'); }
                    else if (hex && c is >= 'a' and <= 'f') { a |= (uint)(c - 'a' + 10); }
                    else throw new FormatException("Invalid memory file token: " + tok);
                }
                ulong m = width >= 64 ? ulong.MaxValue : (1UL << width) - 1UL;
                return twoState ? new W4((a & ~b) & m, 0UL) : new W4(a & m, b & m);
            }
            public static int LoadW4(Span<W4> mem, int width, string path, bool hex, int startAddr = 0, int endAddr = int.MaxValue, bool twoState = false)
            {
                string content = System.IO.File.ReadAllText(path);
                int limit = Math.Min(endAddr, mem.Length - 1);
                int addr = startAddr, loaded = 0, i = 0, n = content.Length;
                while (true)
                {
                    while (i < n)
                    {
                        if (content[i] <= ' ') { ++i; continue; }
                        if (content[i] == '/' && i + 1 < n && content[i + 1] == '/') { i += 2; while (i < n && content[i] != '\n') ++i; continue; }
                        if (content[i] == '/' && i + 1 < n && content[i + 1] == '*') { i += 2; while (i + 1 < n && !(content[i] == '*' && content[i + 1] == '/')) ++i; i += 2; continue; }
                        break;
                    }
                    if (i >= n) break;
                    int st = i;
                    while (i < n && content[i] > ' ' && !(content[i] == '/' && i + 1 < n && (content[i + 1] == '/' || content[i + 1] == '*'))) ++i;
                    string tok = content[st..i];
                    if (tok.Length == 0) continue;
                    if (tok[0] == '@') { addr = Convert.ToInt32(tok[1..].Replace("_", ""), 16); continue; }
                    if (addr < 0 || addr > limit || addr >= mem.Length) break;
                    mem[addr++] = ParseTok(tok, width, hex, twoState);
                    loaded++;
                }
                return loaded;
            }
            public static int LoadU64(Span<ulong> mem, int width, string path, bool hex, int startAddr = 0, int endAddr = int.MaxValue)
            {
                var tmp = new W4[mem.Length];
                for (int k = 0; k < mem.Length; ++k) tmp[k] = new W4(mem[k], 0UL);
                int loaded = LoadW4(tmp, width, path, hex, startAddr, endAddr, true);
                for (int k = 0; k < mem.Length; ++k) mem[k] = tmp[k].A;
                return loaded;
            }
        }

        public enum HdlEdge { Change, Posedge, Negedge }
        public enum HdlJoin { All, Any, None }
        public readonly record struct HdlWatch(int Signal, HdlEdge Edge, bool Memory);
        public readonly record struct HdlWrite(int Signal, int Index, W4 Value, bool HasEnable, ulong Enable);
        public readonly record struct HdlStep(int Kind, ulong Ticks)
        {
            public static readonly HdlStep Again = new(0, 0);
            public static readonly HdlStep Wait = new(1, 0);
            public static readonly HdlStep Done = new(2, 0);
            public static readonly HdlStep Join = new(3, 0);
            public static HdlStep Delay(ulong t) => new(4, t);
        }
        public sealed class HdlFrame
        {
            public int Pc;
            public bool Finished, Waiting, Queued;
            public List<HdlWatch> Watches = new();
            public List<int> NamedWatches = new();
            public HdlWrite? Sampled;
            public List<HdlWrite> SampledWrites = new();
            public Dictionary<int, uint> Repeats = new();
            public int? Parent;
            public List<int> Children = new(), Deferred = new(), Joining = new();
            public HdlJoin Join = HdlJoin.None;
            public void Watch(HdlWatch[] ws, int[] nws) { Watches.Clear(); Watches.AddRange(ws); NamedWatches.Clear(); NamedWatches.AddRange(nws); }
            public HdlFrame Clone() => new()
            {
                Pc = Pc, Finished = Finished, Waiting = Waiting, Queued = Queued,
                Watches = new(Watches), NamedWatches = new(NamedWatches), Sampled = Sampled,
                SampledWrites = new(SampledWrites), Repeats = new(Repeats), Parent = Parent,
                Children = new(Children), Deferred = new(Deferred), Joining = new(Joining), Join = Join
            };
        }
        public static class HdlSched
        {
            public static ulong DelayTicks(W4 amt, int w, bool isSigned, ulong scale)
            {
                if (scale == 0 || amt.B != 0UL || (isSigned && (amt.A & (1UL << (w - 1))) != 0UL))
                    throw new ArgumentException("HDL delay must be known and nonnegative");
                return checked(amt.A * scale);
            }
            public static int SampledIndex(W4 idx, int lower, int depth)
            {
                if (idx.B != 0UL || idx.A < (ulong)lower || idx.A - (ulong)lower >= (ulong)depth)
                    throw new ArgumentOutOfRangeException(nameof(idx), "HDL sampled memory address");
                return (int)(idx.A - (ulong)lower);
            }
            public static HdlWrite SelectedWrite(int sigId, int sigW, W4 val, W4 basis, int width, bool down, bool twoState)
            {
                if (basis.B != 0UL || basis.A > (ulong)int.MaxValue) throw new ArgumentOutOfRangeException(nameof(basis));
                int lo = down ? (int)basis.A - width + 1 : (int)basis.A;
                ulong sigMask = sigW >= 64 ? ulong.MaxValue : (1UL << sigW) - 1UL;
                ulong a = 0, b = 0, en = 0;
                for (int bit = 0; bit < width; ++bit)
                {
                    int dst = lo + bit;
                    if ((uint)dst >= (uint)sigW) continue;
                    en |= 1UL << dst;
                    if (((val.A >> bit) & 1UL) != 0UL) a |= 1UL << dst;
                    if (!twoState && ((val.B >> bit) & 1UL) != 0UL) b |= 1UL << dst;
                }
                return new HdlWrite(sigId, -1, new W4(a & sigMask, b & sigMask), true, en & sigMask);
            }
            public static W4 Merge(W4 before, HdlWrite w)
            {
                if (!w.HasEnable) return w.Value;
                ulong keep = ~w.Enable;
                return new W4((before.A & keep) | (w.Value.A & w.Enable), (before.B & keep) | (w.Value.B & w.Enable));
            }
        }
        public sealed class HdlSchedState
        {
            public const int Capacity = 20000;
            public readonly record struct NbaEntry(bool IsTrigger, HdlWrite Write, int EventId);
            public sealed class Tick { public Queue<int> Active = new(); public List<NbaEntry> Nba = new(); public Tick Clone() => new() { Active = new(Active), Nba = new(Nba) }; }
            public List<HdlFrame> Frames = new();
            public SortedDictionary<ulong, Tick> Future = new();
            public Queue<int> Active = new(), Inactive = new();
            public List<NbaEntry> Nba = new();
            public ulong Now;
            public int Remaining = Capacity, Pending;
            public bool Started;
            private ulong FirstFutureKey() { foreach (var k in Future.Keys) return k; throw new InvalidOperationException("empty future"); }
            public bool EventsPending => Pending > 0;
            public ulong? NextTimeSlot => Future.Count > 0 ? FirstFutureKey() : null;
            public HdlSchedState Clone()
            {
                var c = new HdlSchedState { Now = Now, Remaining = Remaining, Pending = Pending, Started = Started, Active = new(Active), Inactive = new(Inactive), Nba = new(Nba) };
                foreach (var f in Frames) c.Frames.Add(f.Clone());
                foreach (var kv in Future) c.Future[kv.Key] = kv.Value.Clone();
                return c;
            }
            private void Reserve() { if (Pending >= Capacity) throw new InvalidOperationException("HDL event capacity"); ++Pending; }
            private void Consume() { if (Remaining <= 0) throw new InvalidOperationException("HDL execution budget exceeded"); --Remaining; }
            public void AddProcess(int entryPc)
            {
                int id = Frames.Count;
                Frames.Add(new HdlFrame { Pc = entryPc, Queued = true });
                Reserve(); Active.Enqueue(id);
            }
            private void Activate(int id)
            {
                var f = Frames[id];
                if (f.Finished || f.Queued) return;
                Reserve(); Active.Enqueue(id); f.Queued = true; f.Waiting = false; f.Watches.Clear(); f.NamedWatches.Clear();
            }
            public void Changed(int sig, W4 before, W4 after, bool memory)
            {
                if (before.A == after.A && before.B == after.B) return;
                for (int id = 0; id < Frames.Count; ++id)
                {
                    var f = Frames[id];
                    if (!f.Waiting) continue;
                    foreach (var w in f.Watches)
                    {
                        if (w.Signal != sig || w.Memory != memory) continue;
                        bool hit = w.Edge == HdlEdge.Change || W4Ops.Edge(before, after, w.Edge == HdlEdge.Posedge);
                        if (hit) { Activate(id); break; }
                    }
                }
            }
            public void Trigger(int eid)
            {
                for (int id = 0; id < Frames.Count; ++id)
                    if (Frames[id].Waiting && Frames[id].NamedWatches.Contains(eid)) Activate(id);
            }
            public void EnqueueNba(HdlWrite w, ulong ticks)
            {
                Reserve();
                var e = new NbaEntry(false, w, 0);
                if (ticks == 0UL) Nba.Add(e);
                else { ulong t = checked(Now + ticks); if (!Future.TryGetValue(t, out var tk)) { tk = new(); Future[t] = tk; } tk.Nba.Add(e); }
            }
            public void EnqueueNbaTrigger(int eid) { Reserve(); Nba.Add(new NbaEntry(true, default, eid)); }
            private void ReleaseChildren(int id) { var d = new List<int>(Frames[id].Deferred); Frames[id].Deferred.Clear(); foreach (int c in d) Activate(c); }
            private bool JoinReady(HdlFrame f) => f.Join == HdlJoin.None || (f.Join == HdlJoin.All ? f.Joining.TrueForAll(c => Frames[c].Finished) : (f.Joining.Count == 0 || f.Joining.Exists(c => Frames[c].Finished)));
            private void Complete(int id)
            {
                var f = Frames[id]; f.Finished = true; f.Waiting = false; f.Watches.Clear(); f.NamedWatches.Clear();
                ReleaseChildren(id);
                if (f.Parent is int pid)
                {
                    var p = Frames[pid];
                    if (p.Waiting && p.Join != HdlJoin.None && JoinReady(p)) { p.Join = HdlJoin.None; p.Joining.Clear(); Activate(pid); }
                }
            }
            public HdlStep Fork(int parent, int[] entries, HdlJoin join)
            {
                var kids = new List<int>(entries.Length);
                foreach (int ep in entries) { int cid = Frames.Count; Frames.Add(new HdlFrame { Pc = ep, Parent = parent }); Frames[parent].Children.Add(cid); Frames[parent].Deferred.Add(cid); kids.Add(cid); }
                if (join == HdlJoin.None) return HdlStep.Again;
                Frames[parent].Join = join; Frames[parent].Joining = kids;
                return HdlStep.Join;
            }
            public HdlStep WaitFork(int parent) { Frames[parent].Join = HdlJoin.All; Frames[parent].Joining = new(Frames[parent].Children); return HdlStep.Join; }
            public void DisableFork(int parent) { foreach (int c in Frames[parent].Children) { Frames[c].Finished = true; Frames[c].Waiting = false; Frames[c].Watches.Clear(); } Frames[parent].Children.Clear(); Frames[parent].Deferred.Clear(); }
            private void Settle(Action<HdlWrite> applyWrite, Func<int, HdlFrame, HdlStep> exec)
            {
                while (Active.Count > 0 || Inactive.Count > 0 || Nba.Count > 0)
                {
                    if (Active.Count == 0)
                    {
                        if (Inactive.Count > 0) { (Active, Inactive) = (Inactive, Active); }
                        else
                        {
                            var batch = new List<NbaEntry>(Nba); Nba.Clear();
                            foreach (var act in batch) { Consume(); --Pending; if (act.IsTrigger) Trigger(act.EventId); else applyWrite(act.Write); }
                            continue;
                        }
                    }
                    int id = Active.Dequeue(); --Pending;
                    Frames[id].Queued = false;
                    if (Frames[id].Finished) continue;
                    while (true)
                    {
                        Consume();
                        var step = exec(id, Frames[id]);
                        if (step.Kind == 0) continue;
                        if (step.Kind == 2) { Complete(id); break; }
                        if (step.Kind == 3) { Frames[id].Waiting = true; ReleaseChildren(id); if (JoinReady(Frames[id])) { Frames[id].Join = HdlJoin.None; Activate(id); } break; }
                        if (step.Kind == 1) { Frames[id].Waiting = true; ReleaseChildren(id); break; }
                        ulong tgt = checked(Now + step.Ticks); Reserve(); Frames[id].Queued = true;
                        if (step.Ticks == 0UL) Inactive.Enqueue(id);
                        else { if (!Future.TryGetValue(tgt, out var tk)) { tk = new(); Future[tgt] = tk; } tk.Active.Enqueue(id); }
                        ReleaseChildren(id);
                        break;
                    }
                }
            }
            public void DriveAt(ulong target, HdlWrite[] drives, Action<HdlWrite> applyWrite, Func<int, HdlFrame, HdlStep> exec)
            {
                if (target < Now) throw new ArgumentException("HDL time went backward");
                if (!Started) { Started = true; Remaining = Capacity; Settle(applyWrite, exec); }
                while (Future.Count > 0)
                {
                    ulong nextT = FirstFutureKey();
                    if (nextT > target) break;
                    var tk = Future[nextT]; Future.Remove(nextT);
                    Now = nextT; Remaining = Capacity;
                    while (tk.Active.Count > 0) Active.Enqueue(tk.Active.Dequeue());
                    Nba.AddRange(tk.Nba);
                    Settle(applyWrite, exec);
                }
                Now = target; Remaining = Capacity;
                foreach (var d in drives) applyWrite(d);
                Settle(applyWrite, exec);
            }
        }
        """;

    internal static string EmitRustParityRuntimeHelpers() => """
        pub mod container {
            use super::{w2, w4, W4};
            use std::collections::BTreeMap;
            pub const CAPACITY: usize = 256;
            #[inline] fn elem_init(w: i32, two_state: bool) -> W4 { if two_state { W4::lit(0) } else { W4::unknown(if w >= 64 { u64::MAX } else { (1u64 << w) - 1 }) } }
            #[inline] fn elem_conv(v: W4, w: i32, two_state: bool) -> W4 { let m = if w >= 64 { u64::MAX } else { (1u64 << w) - 1 }; if two_state { w4::two_state(v, m) } else { w4::resize(v, m) } }
            pub fn array_index(indices: &[W4], dims: &[(i32, i32)]) -> W4 {
                let mut offset = 0u64;
                for (idx, &(l, r)) in indices.iter().zip(dims.iter()) {
                    let lo = l.min(r) as u64; let hi = l.max(r) as u64; let sz = hi - lo + 1;
                    if idx.b != 0 || idx.a < lo || idx.a > hi { return W4::new(u64::MAX, u64::MAX); }
                    offset = offset * sz + (idx.a - lo);
                }
                W4::lit(offset)
            }
            pub fn array_index2(indices: &[u64], dims: &[(i32, i32)]) -> u64 {
                let mut offset = 0u64;
                for (&v, &(l, r)) in indices.iter().zip(dims.iter()) {
                    let lo = l.min(r) as u64; let hi = l.max(r) as u64; let sz = hi - lo + 1;
                    if v < lo || v > hi { return u64::MAX; }
                    offset = offset * sz + (v - lo);
                }
                offset
            }
            pub fn dyn_alloc(mem: &mut Vec<W4>, count: W4, count_w: i32, elem_w: i32, two_state: bool, preserve: bool, is_signed: bool) -> Result<(), &'static str> {
                if count.b != 0 || (is_signed && (count.a & (1u64 << (count_w - 1))) != 0) { return Err("dynamic array count must be known and nonnegative"); }
                if count.a as usize > CAPACITY { return Err("dynamic array capacity exceeds 256"); }
                let n = count.a as usize;
                let init = elem_init(elem_w, two_state);
                let mut next = vec![init; n];
                if preserve { let cp = n.min(mem.len()); next[..cp].copy_from_slice(&mem[..cp]); }
                *mem = next; Ok(())
            }
            pub fn dyn_alloc2(mem: &mut Vec<u64>, count: u64, count_w: i32, preserve: bool, is_signed: bool) -> Result<(), &'static str> {
                if is_signed && (count & (1u64 << (count_w - 1))) != 0 { return Err("dynamic array count must be nonnegative"); }
                if count as usize > CAPACITY { return Err("dynamic array capacity exceeds 256"); }
                let n = count as usize;
                let mut next = vec![0u64; n];
                if preserve { let cp = n.min(mem.len()); next[..cp].copy_from_slice(&mem[..cp]); }
                *mem = next; Ok(())
            }
            #[inline] pub fn dyn_read(mem: &[W4], idx: W4, elem_w: i32, two_state: bool) -> W4 { if idx.b != 0 || idx.a as usize >= mem.len() { elem_init(elem_w, two_state) } else { mem[idx.a as usize] } }
            #[inline] pub fn dyn_read2(mem: &[u64], idx: u64) -> u64 { if idx as usize >= mem.len() { 0 } else { mem[idx as usize] } }
            #[inline] pub fn dyn_write(mem: &mut [W4], idx: W4, val: W4, elem_w: i32, two_state: bool) { if idx.b == 0 && (idx.a as usize) < mem.len() { mem[idx.a as usize] = elem_conv(val, elem_w, two_state); } }
            #[inline] pub fn dyn_write2(mem: &mut [u64], idx: u64, val: u64, elem_w: i32) { if (idx as usize) < mem.len() { mem[idx as usize] = w2::resize(val, elem_w); } }
            pub fn queue_push(mem: &mut Vec<W4>, val: W4, elem_w: i32, two_state: bool, front: bool) -> Result<(), &'static str> {
                if mem.len() >= CAPACITY { return Err("queue capacity exceeds 256"); }
                let cv = elem_conv(val, elem_w, two_state);
                if front { mem.insert(0, cv); } else { mem.push(cv); }
                Ok(())
            }
            pub fn queue_push2(mem: &mut Vec<u64>, val: u64, elem_w: i32, front: bool) -> Result<(), &'static str> {
                if mem.len() >= CAPACITY { return Err("queue capacity exceeds 256"); }
                let cv = w2::resize(val, elem_w);
                if front { mem.insert(0, cv); } else { mem.push(cv); }
                Ok(())
            }
            pub fn queue_pop(mem: &mut Vec<W4>, elem_w: i32, two_state: bool, front: bool) -> W4 {
                if mem.is_empty() { return elem_init(elem_w, two_state); }
                if front { mem.remove(0) } else { mem.pop().unwrap() }
            }
            pub fn queue_pop2(mem: &mut Vec<u64>, front: bool) -> u64 {
                if mem.is_empty() { return 0; }
                if front { mem.remove(0) } else { mem.pop().unwrap() }
            }
            pub fn queue_delete(mem: &mut Vec<W4>, idx: W4) { if idx.b == 0 && (idx.a as usize) < mem.len() { mem.remove(idx.a as usize); } }
            pub fn queue_delete2(mem: &mut Vec<u64>, idx: u64) { if (idx as usize) < mem.len() { mem.remove(idx as usize); } }
            pub fn queue_write(mem: &mut Vec<W4>, idx: W4, val: W4, elem_w: i32, two_state: bool) -> Result<(), &'static str> {
                if idx.b != 0 || idx.a as usize > mem.len() { return Ok(()); }
                if idx.a as usize == mem.len() { queue_push(mem, val, elem_w, two_state, false) }
                else { mem[idx.a as usize] = elem_conv(val, elem_w, two_state); Ok(()) }
            }
            pub fn queue_write2(mem: &mut Vec<u64>, idx: u64, val: u64, elem_w: i32) -> Result<(), &'static str> {
                if idx as usize > mem.len() { return Ok(()); }
                if idx as usize == mem.len() { queue_push2(mem, val, elem_w, false) }
                else { mem[idx as usize] = w2::resize(val, elem_w); Ok(()) }
            }
            #[inline] fn encode_key(k: u64, kw: i32, ks: bool) -> u64 { let km = if kw >= 64 { u64::MAX } else { (1u64 << kw) - 1 }; (k & km) ^ if ks { 1u64 << (kw - 1) } else { 0 } }
            pub fn assoc_read(map: &BTreeMap<u64, W4>, key: W4, kw: i32, ks: bool, elem_w: i32, two_state: bool) -> W4 {
                let km = if kw >= 64 { u64::MAX } else { (1u64 << kw) - 1 }; let rk = w4::resize(key, km);
                if rk.b != 0 { return elem_init(elem_w, two_state); }
                map.get(&encode_key(rk.a, kw, ks)).copied().unwrap_or_else(|| elem_init(elem_w, two_state))
            }
            pub fn assoc_read2(map: &BTreeMap<u64, u64>, key: u64, kw: i32, ks: bool) -> u64 { map.get(&encode_key(key, kw, ks)).copied().unwrap_or(0) }
            pub fn assoc_exists(map: &BTreeMap<u64, W4>, key: W4, kw: i32, ks: bool) -> W4 {
                let km = if kw >= 64 { u64::MAX } else { (1u64 << kw) - 1 }; let rk = w4::resize(key, km);
                if rk.b == 0 && map.contains_key(&encode_key(rk.a, kw, ks)) { W4::lit(1) } else { W4::lit(0) }
            }
            pub fn assoc_exists2(map: &BTreeMap<u64, u64>, key: u64, kw: i32, ks: bool) -> u64 { if map.contains_key(&encode_key(key, kw, ks)) { 1 } else { 0 } }
            pub fn assoc_write(map: &mut BTreeMap<u64, W4>, key: W4, val: W4, kw: i32, ks: bool, elem_w: i32, two_state: bool) -> Result<(), &'static str> {
                let km = if kw >= 64 { u64::MAX } else { (1u64 << kw) - 1 }; let rk = w4::resize(key, km);
                if rk.b != 0 { return Ok(()); }
                let ek = encode_key(rk.a, kw, ks);
                if !map.contains_key(&ek) && map.len() >= CAPACITY { return Err("associative array capacity exceeds 256"); }
                map.insert(ek, elem_conv(val, elem_w, two_state)); Ok(())
            }
            pub fn assoc_write2(map: &mut BTreeMap<u64, u64>, key: u64, val: u64, kw: i32, ks: bool, elem_w: i32) -> Result<(), &'static str> {
                let ek = encode_key(key, kw, ks);
                if !map.contains_key(&ek) && map.len() >= CAPACITY { return Err("associative array capacity exceeds 256"); }
                map.insert(ek, w2::resize(val, elem_w)); Ok(())
            }
            pub fn assoc_delete(map: &mut BTreeMap<u64, W4>, key: W4, kw: i32, ks: bool) {
                let km = if kw >= 64 { u64::MAX } else { (1u64 << kw) - 1 }; let rk = w4::resize(key, km);
                if rk.b == 0 { map.remove(&encode_key(rk.a, kw, ks)); }
            }
            pub fn assoc_delete2(map: &mut BTreeMap<u64, u64>, key: u64, kw: i32, ks: bool) { map.remove(&encode_key(key, kw, ks)); }
            pub fn assoc_traverse(map: &BTreeMap<u64, W4>, key: &mut W4, kw: i32, ks: bool, op: i32) -> W4 {
                if map.is_empty() { return W4::lit(0); }
                let found = match op {
                    0 => map.keys().next().copied(),
                    1 => map.keys().next_back().copied(),
                    2 => {
                        let km = if kw >= 64 { u64::MAX } else { (1u64 << kw) - 1 }; let rk = w4::resize(*key, km);
                        if rk.b != 0 { return W4::lit(0); }
                        let ek = encode_key(rk.a, kw, ks);
                        map.range((std::ops::Bound::Excluded(ek), std::ops::Bound::Unbounded)).next().map(|(&k, _)| k)
                    }
                    _ => {
                        let km = if kw >= 64 { u64::MAX } else { (1u64 << kw) - 1 }; let rk = w4::resize(*key, km);
                        if rk.b != 0 { return W4::lit(0); }
                        let ek = encode_key(rk.a, kw, ks);
                        map.range(..ek).next_back().map(|(&k, _)| k)
                    }
                };
                if let Some(fk) = found { *key = W4::lit(fk ^ if ks { 1u64 << (kw - 1) } else { 0 }); W4::lit(1) } else { W4::lit(0) }
            }
            pub fn assoc_traverse2(map: &BTreeMap<u64, u64>, key: &mut u64, kw: i32, ks: bool, op: i32) -> u64 {
                if map.is_empty() { return 0; }
                let found = match op {
                    0 => map.keys().next().copied(),
                    1 => map.keys().next_back().copied(),
                    2 => { let ek = encode_key(*key, kw, ks); map.range((std::ops::Bound::Excluded(ek), std::ops::Bound::Unbounded)).next().map(|(&k, _)| k) }
                    _ => { let ek = encode_key(*key, kw, ks); map.range(..ek).next_back().map(|(&k, _)| k) }
                };
                if let Some(fk) = found { *key = fk ^ if ks { 1u64 << (kw - 1) } else { 0 }; 1 } else { 0 }
            }
        }

        fn parse_mem_tok(tok: &str, width: i32, hex: bool, two_state: bool) -> std::io::Result<W4> {
            let mut a = 0u64; let mut b = 0u64; let sh = if hex { 4 } else { 1 };
            for ch in tok.chars() {
                if ch == '_' { continue; }
                let c = ch.to_ascii_lowercase();
                a = a.wrapping_shl(sh); b = b.wrapping_shl(sh);
                match c {
                    'x' => { let f = if hex { 0xFu64 } else { 1u64 }; a |= f; b |= f; }
                    'z' | '?' => { b |= if hex { 0xFu64 } else { 1u64 }; }
                    '0'..='9' => { a |= (c as u8 - b'0') as u64; }
                    'a'..='f' if hex => { a |= (c as u8 - b'a' + 10) as u64; }
                    _ => return Err(std::io::Error::new(std::io::ErrorKind::InvalidData, "invalid memory token")),
                }
            }
            let m = if width >= 64 { u64::MAX } else { (1u64 << width) - 1 };
            Ok(if two_state { W4::lit((a & !b) & m) } else { W4::new(a & m, b & m) })
        }
        pub fn load_mem_file_w4(mem: &mut [W4], width: i32, path: &str, hex: bool, start: usize, end: usize, two_state: bool) -> std::io::Result<usize> {
            let content = std::fs::read_to_string(path)?;
            let bytes = content.as_bytes();
            let limit = end.min(mem.len().saturating_sub(1));
            let mut addr = start; let mut loaded = 0usize; let mut i = 0usize; let n = bytes.len();
            loop {
                while i < n {
                    if bytes[i] <= b' ' { i += 1; continue; }
                    if bytes[i] == b'/' && i + 1 < n && bytes[i + 1] == b'/' { i += 2; while i < n && bytes[i] != b'\n' { i += 1; } continue; }
                    if bytes[i] == b'/' && i + 1 < n && bytes[i + 1] == b'*' { i += 2; while i + 1 < n && !(bytes[i] == b'*' && bytes[i + 1] == b'/') { i += 1; } i += 2; continue; }
                    break;
                }
                if i >= n { break; }
                let st = i;
                while i < n && bytes[i] > b' ' && !(bytes[i] == b'/' && i + 1 < n && (bytes[i + 1] == b'/' || bytes[i + 1] == b'*')) { i += 1; }
                let tok = &content[st..i];
                if tok.is_empty() { continue; }
                if let Some(rest) = tok.strip_prefix('@') {
                    let clean: String = rest.chars().filter(|&c| c != '_').collect();
                    addr = usize::from_str_radix(&clean, 16).map_err(|_| std::io::Error::new(std::io::ErrorKind::InvalidData, "invalid @address"))?;
                    continue;
                }
                if mem.is_empty() || addr > limit || addr >= mem.len() { break; }
                mem[addr] = parse_mem_tok(tok, width, hex, two_state)?;
                addr += 1; loaded += 1;
            }
            Ok(loaded)
        }
        pub fn load_mem_file_u64(mem: &mut [u64], width: i32, path: &str, hex: bool, start: usize, end: usize) -> std::io::Result<usize> {
            let mut tmp: Vec<W4> = mem.iter().map(|&v| W4::lit(v)).collect();
            let loaded = load_mem_file_w4(&mut tmp, width, path, hex, start, end, true)?;
            for (d, s) in mem.iter_mut().zip(tmp.iter()) { *d = s.a; }
            Ok(loaded)
        }

        #[derive(Clone, Copy, PartialEq, Eq)] pub enum HdlEdge { Change, Posedge, Negedge }
        #[derive(Clone, Copy, PartialEq, Eq)] pub enum HdlJoin { All, Any, None }
        #[derive(Clone, Copy)] pub struct HdlWatch { pub signal: usize, pub edge: HdlEdge, pub memory: bool }
        #[derive(Clone, Copy)] pub struct HdlWrite { pub signal: usize, pub index: Option<usize>, pub value: W4, pub enable: Option<u64> }
        #[derive(Clone, Copy)] pub enum HdlStep { Again, Wait, Done, Join, Delay(u64) }
        #[derive(Clone, Copy)] pub enum HdlNba { Write(HdlWrite), Trigger(usize) }
        #[derive(Clone)]
        pub struct HdlFrame {
            pub pc: usize, pub finished: bool, pub waiting: bool, pub queued: bool,
            pub watches: Vec<HdlWatch>, pub named_watches: Vec<usize>,
            pub sampled: Option<HdlWrite>, pub sampled_writes: Vec<HdlWrite>,
            pub repeats: std::collections::BTreeMap<usize, usize>,
            pub parent: Option<usize>, pub children: Vec<usize>, pub deferred: Vec<usize>, pub joining: Vec<usize>,
            pub join: HdlJoin,
        }
        impl HdlFrame {
            pub fn new(pc: usize, parent: Option<usize>) -> Self {
                Self { pc, finished: false, waiting: false, queued: false, watches: Vec::new(), named_watches: Vec::new(), sampled: None, sampled_writes: Vec::new(), repeats: std::collections::BTreeMap::new(), parent, children: Vec::new(), deferred: Vec::new(), joining: Vec::new(), join: HdlJoin::None }
            }
            pub fn watch(&mut self, ws: Vec<HdlWatch>, nws: Vec<usize>) { self.watches = ws; self.named_watches = nws; }
        }
        #[derive(Clone, Default)]
        pub struct HdlTick { pub active: std::collections::VecDeque<usize>, pub nba: Vec<HdlNba> }
        #[derive(Clone)]
        pub struct HdlSchedState {
            pub frames: Vec<HdlFrame>,
            pub future: std::collections::BTreeMap<u64, HdlTick>,
            pub active: std::collections::VecDeque<usize>,
            pub inactive: std::collections::VecDeque<usize>,
            pub nba: Vec<HdlNba>,
            pub now: u64, pub remaining: usize, pub pending: usize, pub started: bool,
        }
        impl HdlSchedState {
            pub const CAPACITY: usize = 20000;
            pub fn new() -> Self {
                Self { frames: Vec::new(), future: std::collections::BTreeMap::new(), active: std::collections::VecDeque::new(), inactive: std::collections::VecDeque::new(), nba: Vec::new(), now: 0, remaining: Self::CAPACITY, pending: 0, started: false }
            }
            pub fn events_pending(&self) -> bool { self.pending > 0 }
            pub fn next_time_slot(&self) -> Option<u64> { self.future.keys().next().copied() }
            fn reserve(&mut self) -> Result<(), &'static str> { if self.pending >= Self::CAPACITY { return Err("HDL event capacity"); } self.pending += 1; Ok(()) }
            pub fn consume(&mut self) -> Result<(), &'static str> { if self.remaining == 0 { return Err("HDL execution budget exceeded"); } self.remaining -= 1; Ok(()) }
            pub fn add_process(&mut self, entry_pc: usize) {
                let id = self.frames.len();
                let mut f = HdlFrame::new(entry_pc, None); f.queued = true;
                self.frames.push(f); self.pending += 1; self.active.push_back(id);
            }
            fn activate(&mut self, id: usize) -> Result<(), &'static str> {
                if self.frames[id].finished || self.frames[id].queued { return Ok(()); }
                self.reserve()?;
                self.active.push_back(id);
                let f = &mut self.frames[id]; f.queued = true; f.waiting = false; f.watches.clear(); f.named_watches.clear();
                Ok(())
            }
            pub fn changed(&mut self, sig: usize, before: W4, after: W4, memory: bool) -> Result<(), &'static str> {
                if before.a == after.a && before.b == after.b { return Ok(()); }
                let mut to_act = Vec::new();
                for (id, f) in self.frames.iter().enumerate() {
                    if !f.waiting { continue; }
                    if f.watches.iter().any(|w| w.signal == sig && w.memory == memory && (w.edge == HdlEdge::Change || w4::edge(before, after, w.edge == HdlEdge::Posedge))) {
                        to_act.push(id);
                    }
                }
                for id in to_act { self.activate(id)?; }
                Ok(())
            }
            pub fn trigger(&mut self, eid: usize) -> Result<(), &'static str> {
                let mut to_act = Vec::new();
                for (id, f) in self.frames.iter().enumerate() {
                    if f.waiting && f.named_watches.contains(&eid) { to_act.push(id); }
                }
                for id in to_act { self.activate(id)?; }
                Ok(())
            }
            pub fn enqueue_nba(&mut self, w: HdlWrite, ticks: u64) -> Result<(), &'static str> {
                self.reserve()?;
                if ticks == 0 { self.nba.push(HdlNba::Write(w)); }
                else { let t = self.now.checked_add(ticks).ok_or("HDL time overflow")?; self.future.entry(t).or_default().nba.push(HdlNba::Write(w)); }
                Ok(())
            }
            pub fn enqueue_nba_trigger(&mut self, eid: usize) -> Result<(), &'static str> { self.reserve()?; self.nba.push(HdlNba::Trigger(eid)); Ok(()) }
            pub fn release_children(&mut self, id: usize) -> Result<(), &'static str> {
                let d = std::mem::take(&mut self.frames[id].deferred);
                for c in d { self.activate(c)?; }
                Ok(())
            }
            fn join_ready(&self, id: usize) -> bool {
                let f = &self.frames[id];
                match f.join {
                    HdlJoin::None => true,
                    HdlJoin::All => f.joining.iter().all(|&c| self.frames[c].finished),
                    HdlJoin::Any => f.joining.is_empty() || f.joining.iter().any(|&c| self.frames[c].finished),
                }
            }
            pub fn complete(&mut self, id: usize) -> Result<(), &'static str> {
                let parent = self.frames[id].parent;
                { let f = &mut self.frames[id]; f.finished = true; f.waiting = false; f.watches.clear(); f.named_watches.clear(); }
                self.release_children(id)?;
                if let Some(pid) = parent {
                    if self.frames[pid].waiting && self.frames[pid].join != HdlJoin::None && self.join_ready(pid) {
                        self.frames[pid].join = HdlJoin::None; self.frames[pid].joining.clear();
                        self.activate(pid)?;
                    }
                }
                Ok(())
            }
            pub fn finish_join(&mut self, id: usize) -> Result<(), &'static str> {
                self.frames[id].waiting = true;
                self.release_children(id)?;
                if self.join_ready(id) { self.frames[id].join = HdlJoin::None; self.activate(id)?; }
                Ok(())
            }
            pub fn schedule_delay(&mut self, id: usize, ticks: u64) -> Result<(), &'static str> {
                let tgt = self.now.checked_add(ticks).ok_or("HDL time overflow")?;
                self.reserve()?;
                self.frames[id].queued = true;
                if ticks == 0 { self.inactive.push_back(id); }
                else { self.future.entry(tgt).or_default().active.push_back(id); }
                self.release_children(id)?;
                Ok(())
            }
            pub fn fork(&mut self, parent: usize, entries: &[usize], join: HdlJoin) -> Result<HdlStep, &'static str> {
                let mut kids = Vec::with_capacity(entries.len());
                for &ep in entries {
                    let cid = self.frames.len();
                    self.frames.push(HdlFrame::new(ep, Some(parent)));
                    self.frames[parent].children.push(cid);
                    self.frames[parent].deferred.push(cid);
                    kids.push(cid);
                }
                if join == HdlJoin::None { return Ok(HdlStep::Again); }
                self.frames[parent].join = join; self.frames[parent].joining = kids;
                Ok(HdlStep::Join)
            }
            pub fn wait_fork(&mut self, parent: usize) -> Result<HdlStep, &'static str> {
                let kids = self.frames[parent].children.clone();
                self.frames[parent].join = HdlJoin::All; self.frames[parent].joining = kids;
                Ok(HdlStep::Join)
            }
            pub fn disable_fork(&mut self, parent: usize) {
                let kids = std::mem::take(&mut self.frames[parent].children);
                self.frames[parent].deferred.clear();
                for c in kids { let f = &mut self.frames[c]; f.finished = true; f.waiting = false; f.watches.clear(); }
            }
        }
        pub mod hdl_sched {
            use super::{HdlWrite, W4};
            pub fn delay_ticks(amt: W4, w: i32, is_signed: bool, scale: u64) -> Result<u64, &'static str> {
                if scale == 0 || amt.b != 0 || (is_signed && (amt.a & (1u64 << (w - 1))) != 0) { return Err("HDL delay must be known and nonnegative"); }
                amt.a.checked_mul(scale).ok_or("HDL delay overflow")
            }
            pub fn sampled_index(idx: W4, lower: i32, depth: usize) -> Result<usize, &'static str> {
                if idx.b != 0 || idx.a < lower as u64 || (idx.a - lower as u64) as usize >= depth { return Err("HDL sampled memory address"); }
                Ok((idx.a - lower as u64) as usize)
            }
            pub fn selected_write(sig_id: usize, sig_w: i32, val: W4, basis: W4, width: i32, down: bool, two_state: bool) -> Result<HdlWrite, &'static str> {
                if basis.b != 0 || basis.a > i32::MAX as u64 { return Err("HDL selected write basis"); }
                let lo = if down { basis.a as i64 - width as i64 + 1 } else { basis.a as i64 };
                let sm = if sig_w >= 64 { u64::MAX } else { (1u64 << sig_w) - 1 };
                let mut a = 0u64; let mut b = 0u64; let mut en = 0u64;
                for bit in 0..width {
                    let dst = lo + bit as i64;
                    if dst < 0 || dst >= sig_w as i64 { continue; }
                    let sh = dst as u32;
                    en |= 1u64 << sh;
                    if ((val.a >> bit) & 1) != 0 { a |= 1u64 << sh; }
                    if !two_state && ((val.b >> bit) & 1) != 0 { b |= 1u64 << sh; }
                }
                Ok(HdlWrite { signal: sig_id, index: None, value: W4::new(a & sm, b & sm), enable: Some(en & sm) })
            }
            #[inline] pub fn merge(before: W4, w: &HdlWrite) -> W4 {
                match w.enable {
                    None => w.value,
                    Some(en) => { let keep = !en; W4::new((before.a & keep) | (w.value.a & en), (before.b & keep) | (w.value.b & en)) }
                }
            }
        }
        """;
}
