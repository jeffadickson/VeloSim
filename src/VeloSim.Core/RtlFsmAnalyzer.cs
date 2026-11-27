using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed record FsmTransition(string FromState, string ToState);

public sealed record FsmInfo(
    string StateSignal,
    string? NextStateSignal,
    int Width,
    string? ResetState,
    bool HasDefaultRecovery,
    IReadOnlyList<string> States,
    IReadOnlyList<FsmTransition> Transitions,
    IReadOnlyList<string> UnreachableStates,
    IReadOnlyList<string> DeadlockStates,
    string Context)
{
    public string ToMermaid()
    {
        var sb = new StringBuilder();
        sb.AppendLine("stateDiagram-v2");
        if (!string.IsNullOrEmpty(ResetState))
            sb.Append("    [*] --> S_").AppendLine(ResetState);
        foreach (var t in Transitions)
            sb.Append("    S_").Append(t.FromState).Append(" --> S_").AppendLine(t.ToState);
        return sb.ToString();
    }
}

/// <summary>
/// Pure C# Finite State Machine (FSM) extractor and static verifier (inspired by Aldec ALINT-PRO,
/// Questa Lint, and SpyGlass): detects 1-process and 2-process FSMs and checks unreachable states,
/// deadlock/trap states, missing reset states, and missing default recovery clauses.
/// </summary>
public static class RtlFsmAnalyzer
{
    private const int MaxDepth = 64;

    public static (IReadOnlyList<FsmInfo> Fsms, IReadOnlyList<LintDiagnostic> Diagnostics) Analyze(
        JsonElement root,
        IReadOnlyDictionary<string, int> signalWidths,
        string? sourceFile)
    {
        var fsms = new List<FsmInfo>();
        var diagnostics = new List<LintDiagnostic>();

        if (root.ValueKind != JsonValueKind.Object)
            return (fsms, diagnostics);

        // 1. Collect sequential state candidates:
        //    - reset values: signal -> const bits (from top-level if(rst) then-branch)
        //    - next-state links: stateSig -> nextStateSig (from stateSig <= nextStateSig)
        //    - 1-process FSM cases inside sequential processes
        var resetStates = new Dictionary<string, string>(StringComparer.Ordinal);
        var nextStateOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var seqCases = new List<(JsonElement CaseNode, string Context)>();

        if (root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var (proc, ctx) in EnumerateSequentialProcesses(bodyEl))
            {
                ExtractSequentialInfo(proc, resetStates, nextStateOf, seqCases, ctx);
            }
        }

        // Map nextStateSig -> stateSig for 2-process FSM detection
        var stateOfNext = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (st, nxt) in nextStateOf)
            stateOfNext.TryAdd(nxt, st);

        var discoveredStateSignals = new HashSet<string>(StringComparer.Ordinal);

        // 2. Check 2-process FSMs in combinational blocks (case(state) writing next_state = const)
        if (root.TryGetProperty("combinational", out var combEl) && combEl.ValueKind == JsonValueKind.Array)
        {
            int idx = 0;
            foreach (var block in combEl.EnumerateArray())
            {
                string ctx = $"combinational[{idx++}]";
                var cases = new List<JsonElement>();
                CollectCases(block, cases, 0);
                foreach (var c in cases)
                {
                    if (!c.TryGetProperty("value", out var sel) || TryString(sel, "kind") != "ref") continue;
                    string? stateSig = TryString(sel, "name");
                    if (string.IsNullOrEmpty(stateSig) || !nextStateOf.TryGetValue(stateSig, out string? nextSig)) continue;
                    if (!signalWidths.TryGetValue(stateSig, out int width)) continue;

                    if (TryExtractFsm(c, stateSig, nextSig, width, resetStates.GetValueOrDefault(stateSig), ctx, out var fsm))
                    {
                        if (discoveredStateSignals.Add(stateSig))
                        {
                            fsms.Add(fsm);
                            EmitFsmDiagnostics(fsm, sourceFile, diagnostics);
                        }
                    }
                }
            }
        }

        // 3. Check 1-process FSMs inside sequential processes (case(state) writing state <= const)
        foreach (var (c, ctx) in seqCases)
        {
            if (!c.TryGetProperty("value", out var sel) || TryString(sel, "kind") != "ref") continue;
            string? stateSig = TryString(sel, "name");
            if (string.IsNullOrEmpty(stateSig) || discoveredStateSignals.Contains(stateSig)) continue;
            if (!signalWidths.TryGetValue(stateSig, out int width)) continue;

            if (TryExtractFsm(c, stateSig, null, width, resetStates.GetValueOrDefault(stateSig), ctx, out var fsm))
            {
                if (discoveredStateSignals.Add(stateSig))
                {
                    fsms.Add(fsm);
                    EmitFsmDiagnostics(fsm, sourceFile, diagnostics);
                }
            }
        }

        return (fsms.AsReadOnly(), diagnostics.AsReadOnly());
    }

    private static IEnumerable<(JsonElement Proc, string Context)> EnumerateSequentialProcesses(JsonElement bodyEl)
    {
        if (TryString(bodyEl, "kind") == "parallel" && bodyEl.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var it in items.EnumerateArray())
                yield return (it, $"process[{i++}]");
        }
        else
        {
            yield return (bodyEl, "body");
        }
    }

    private static void ExtractSequentialInfo(
        JsonElement proc,
        Dictionary<string, string> resetStates,
        Dictionary<string, string> nextStateOf,
        List<(JsonElement CaseNode, string Context)> seqCases,
        string context)
    {
        JsonElement inner = proc;
        if (TryString(inner, "kind") == "process" && inner.TryGetProperty("body", out var pb))
            inner = pb;

        while (TryString(inner, "kind") == "block" && inner.TryGetProperty("items", out var items) &&
               items.ValueKind == JsonValueKind.Array && items.GetArrayLength() == 1)
            inner = items[0];

        if (TryString(inner, "kind") == "if" && inner.TryGetProperty("then", out var thenEl) && inner.TryGetProperty("else", out var elseEl))
        {
            CollectConstAssignments(thenEl, resetStates, 0);
            CollectRefAssignments(elseEl, nextStateOf, 0);
        }
        else
        {
            CollectRefAssignments(inner, nextStateOf, 0);
        }

        var cases = new List<JsonElement>();
        CollectCases(inner, cases, 0);
        foreach (var c in cases) seqCases.Add((c, context));
    }

    private static bool TryExtractFsm(
        JsonElement caseNode,
        string stateSig,
        string? nextStateSig,
        int width,
        string? rawResetState,
        string context,
        out FsmInfo fsm)
    {
        fsm = null!;
        string writeTarget = nextStateSig ?? stateSig;
        if (!caseNode.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return false;

        var declaredStates = new List<string>();
        var stateSet = new HashSet<string>(StringComparer.Ordinal);
        var transitions = new List<FsmTransition>();
        var transSet = new HashSet<string>(StringComparer.Ordinal);
        bool hasDefaultRecovery = false;
        int constTransitionCount = 0;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("body", out var body)) continue;
            var assignedConsts = new List<string>();
            CollectTargetConstValues(body, writeTarget, width, assignedConsts, 0);

            if (IsCaseDefault(item))
            {
                if (assignedConsts.Count > 0)
                    hasDefaultRecovery = true;
                continue;
            }

            var fromLabels = new List<string>();
            foreach (var m in CaseItemMatches(item))
            {
                if (TryString(m, "kind") == "const" && TryString(m, "bits") is { Length: > 0 } b && IsBinary(b))
                {
                    string norm = NormalizeBits(b, width);
                    fromLabels.Add(norm);
                    if (stateSet.Add(norm)) declaredStates.Add(norm);
                }
            }

            foreach (string from in fromLabels)
            {
                foreach (string to in assignedConsts)
                {
                    constTransitionCount++;
                    string key = from + "->" + to;
                    if (transSet.Add(key))
                        transitions.Add(new FsmTransition(from, to));
                }
            }
        }

        // Require at least 2 declared constant states and at least 1 constant state transition
        // to avoid misclassifying ordinary decoders/ALU muxes as FSMs.
        if (declaredStates.Count < 2 || constTransitionCount == 0)
            return false;

        string? resetState = rawResetState is not null && IsBinary(rawResetState) ? NormalizeBits(rawResetState, width) : null;
        if (resetState is not null && stateSet.Add(resetState))
            declaredStates.Insert(0, resetState);

        // Build O(T) adjacency map and escape set so BFS and deadlock checks are O(S + T)
        var outgoing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var hasEscape = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in transitions)
        {
            if (!outgoing.TryGetValue(t.FromState, out var list))
            {
                list = [];
                outgoing[t.FromState] = list;
            }
            list.Add(t.ToState);
            if (t.ToState != t.FromState)
                hasEscape.Add(t.FromState);
        }

        // Compute unreachable states via BFS from ResetState
        var unreachable = new List<string>();
        HashSet<string> reachable;
        if (resetState is not null)
        {
            reachable = new HashSet<string>(StringComparer.Ordinal) { resetState };
            var queue = new Queue<string>();
            queue.Enqueue(resetState);
            while (queue.Count > 0)
            {
                string cur = queue.Dequeue();
                if (outgoing.TryGetValue(cur, out var nexts))
                {
                    foreach (string nxt in nexts)
                        if (reachable.Add(nxt))
                            queue.Enqueue(nxt);
                }
            }
            foreach (string st in declaredStates)
                if (!reachable.Contains(st))
                    unreachable.Add(st);
        }
        else
        {
            reachable = new HashSet<string>(declaredStates, StringComparer.Ordinal);
        }

        // Compute deadlock/trap states (reachable states with no outgoing transition to a distinct state)
        var deadlocks = new List<string>();
        foreach (string st in declaredStates)
        {
            if (reachable.Contains(st) && !hasEscape.Contains(st))
                deadlocks.Add(st);
        }

        fsm = new FsmInfo(
            stateSig,
            nextStateSig,
            width,
            resetState,
            hasDefaultRecovery,
            declaredStates.AsReadOnly(),
            transitions.AsReadOnly(),
            unreachable.AsReadOnly(),
            deadlocks.AsReadOnly(),
            context);
        return true;
    }

    private static void EmitFsmDiagnostics(FsmInfo fsm, string? sourceFile, List<LintDiagnostic> diagnostics)
    {
        var loc = new LintLocation(sourceFile, null, null, fsm.StateSignal, fsm.Context);

        if (fsm.ResetState is null)
            diagnostics.Add(new LintDiagnostic("FSMNORESET", LintSeverity.Warning, $"FSM state register '{fsm.StateSignal}' has no constant reset state assignment.", "fsm", loc));

        foreach (string u in fsm.UnreachableStates)
            diagnostics.Add(new LintDiagnostic("FSMUNREACH", LintSeverity.Warning, $"FSM '{fsm.StateSignal}' state '{u}' is unreachable from reset state '{fsm.ResetState}'.", "fsm", loc));

        foreach (string d in fsm.DeadlockStates)
            diagnostics.Add(new LintDiagnostic("FSMDEADLOCK", LintSeverity.Warning, $"FSM '{fsm.StateSignal}' state '{d}' has no outgoing transition to another state (deadlock/trap state).", "fsm", loc));

        if (!fsm.HasDefaultRecovery)
            diagnostics.Add(new LintDiagnostic("FSMRECOVERY", LintSeverity.Warning, $"FSM '{fsm.StateSignal}' has no default recovery transition to a known state for illegal/SEU encodings.", "fsm", loc));
    }

    private static void CollectConstAssignments(JsonElement stmt, Dictionary<string, string> map, int depth)
    {
        if (depth > MaxDepth || stmt.ValueKind != JsonValueKind.Object) return;
        string? kind = TryString(stmt, "kind");
        if (kind == "block" && stmt.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in items.EnumerateArray()) CollectConstAssignments(it, map, depth + 1);
        }
        else if (kind is "nba" or "blocking" &&
                 stmt.TryGetProperty("target", out var tgt) && TryString(tgt, "kind") == "ref" &&
                 TryString(tgt, "name") is { } name &&
                 stmt.TryGetProperty("value", out var val) && TryString(val, "kind") == "const" &&
                 TryString(val, "bits") is { } bits)
        {
            map[name] = bits;
        }
    }

    private static void CollectRefAssignments(JsonElement stmt, Dictionary<string, string> map, int depth)
    {
        if (depth > MaxDepth || stmt.ValueKind != JsonValueKind.Object) return;
        string? kind = TryString(stmt, "kind");
        if (kind == "block" && stmt.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in items.EnumerateArray()) CollectRefAssignments(it, map, depth + 1);
        }
        else if (kind is "nba" or "blocking" &&
                 stmt.TryGetProperty("target", out var tgt) && TryString(tgt, "kind") == "ref" &&
                 TryString(tgt, "name") is { } dst &&
                 stmt.TryGetProperty("value", out var val) && TryString(val, "kind") == "ref" &&
                 TryString(val, "name") is { } src && dst != src)
        {
            map[dst] = src;
        }
    }

    private static void CollectCases(JsonElement stmt, List<JsonElement> cases, int depth)
    {
        if (depth > MaxDepth || stmt.ValueKind != JsonValueKind.Object) return;
        string? kind = TryString(stmt, "kind");
        if (kind == "case")
            cases.Add(stmt);

        if (stmt.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in items.EnumerateArray())
            {
                if (kind == "case" && it.TryGetProperty("body", out var cb)) CollectCases(cb, cases, depth + 1);
                else CollectCases(it, cases, depth + 1);
            }
        }
        foreach (string f in new[] { "body", "then", "else" })
            if (stmt.TryGetProperty(f, out var ch)) CollectCases(ch, cases, depth + 1);
    }

    private static void CollectTargetConstValues(JsonElement stmt, string targetName, int width, List<string> values, int depth)
    {
        if (depth > MaxDepth || stmt.ValueKind != JsonValueKind.Object) return;
        string? kind = TryString(stmt, "kind");
        switch (kind)
        {
            case "block":
                if (stmt.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    foreach (var it in items.EnumerateArray()) CollectTargetConstValues(it, targetName, width, values, depth + 1);
                break;
            case "nba":
            case "blocking":
                if (stmt.TryGetProperty("target", out var tgt) && TryString(tgt, "kind") == "ref" &&
                    TryString(tgt, "name") == targetName && stmt.TryGetProperty("value", out var val))
                {
                    CollectExprConstValues(val, width, values, 0);
                }
                break;
            case "if":
                if (stmt.TryGetProperty("then", out var t)) CollectTargetConstValues(t, targetName, width, values, depth + 1);
                if (stmt.TryGetProperty("else", out var e)) CollectTargetConstValues(e, targetName, width, values, depth + 1);
                break;
            case "case":
                if (stmt.TryGetProperty("items", out var ci) && ci.ValueKind == JsonValueKind.Array)
                    foreach (var it in ci.EnumerateArray())
                        if (it.TryGetProperty("body", out var b)) CollectTargetConstValues(b, targetName, width, values, depth + 1);
                break;
        }
    }

    private static void CollectExprConstValues(JsonElement expr, int width, List<string> values, int depth)
    {
        if (depth > MaxDepth || expr.ValueKind != JsonValueKind.Object) return;
        string? kind = TryString(expr, "kind");
        if (kind == "const" && TryString(expr, "bits") is { Length: > 0 } b && IsBinary(b))
        {
            values.Add(NormalizeBits(b, width));
        }
        else if (kind == "cond" && expr.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array && args.GetArrayLength() == 3)
        {
            CollectExprConstValues(args[1], width, values, depth + 1);
            CollectExprConstValues(args[2], width, values, depth + 1);
        }
    }

    private static bool IsCaseDefault(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("match", out var m) && m.ValueKind == JsonValueKind.Null;

    private static IEnumerable<JsonElement> CaseItemMatches(JsonElement item)
    {
        if (item.TryGetProperty("matches", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in arr.EnumerateArray()) yield return m;
            yield break;
        }
        if (item.TryGetProperty("match", out var single) && single.ValueKind != JsonValueKind.Null)
            yield return single;
    }

    private static bool IsBinary(string bits) => bits.Length > 0 && bits.All(c => c is '0' or '1');

    private static string NormalizeBits(string bits, int width) =>
        bits.Length == width ? bits : bits.Length > width ? bits[^width..] : new string('0', width - bits.Length) + bits;

    private static string? TryString(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
