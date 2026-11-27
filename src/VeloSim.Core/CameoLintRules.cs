using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cameo.Runtime;
using Cameo.Runtime.Stdlib;
using Cameo.Runtime.Syntax;
using Cameo.Runtime.Vm;

namespace VeloSim;

/// <summary>
/// Source-language adapter enabling Cameo (<c>.cameo</c>) syntax over <c>Cameo.Runtime</c>
/// without pulling in JIT or reflection-heavy assemblies (100% NativeAOT-compatible).
/// </summary>
internal sealed class VeloSimCameoLanguage : ISourceLanguage
{
    public static VeloSimCameoLanguage Instance { get; } = new();
    private VeloSimCameoLanguage() { }
    public string Name => "Cameo";
    public string SourceExtension => ".cameo";
    public bool UsesTaggedResults => true;
    public Chunk Parse(string source, string fileName) => Parser.ParseCameo(source, fileName);
}

public sealed record CameoLintScriptError(string File, int Line, int Column, string Message)
{
    public override string ToString() => Line > 0
        ? $"{File}:{Line}:{Column}: {Message}"
        : $"{File}: {Message}";
}

public sealed class CameoLintScriptException : InvalidOperationException
{
    public IReadOnlyList<CameoLintScriptError> Errors { get; }

    public CameoLintScriptException(string message, IReadOnlyList<CameoLintScriptError>? errors = null)
        : base(message)
    {
        Errors = errors ?? [new CameoLintScriptError("<cameo>", 0, 0, message)];
    }
}

/// <summary>
/// Tier 3 Cameo DSL custom lint rule pack: statically type-checks and executes <c>.cameo</c>
/// rule scripts inside a strictly hermetic, deterministic, step-budgeted Cameo VM with
/// deeply frozen RTL IR / FSM / CDC context tables and hardware helper functions
/// (zero reflection, 100% NativeAOT-safe).
/// </summary>
public sealed class CameoLintRulePack
{
    public const long DefaultMaxSteps = 500_000;

    /// <summary>
    /// Static type declaration prelude (<c>velosim_lint.d.tl</c>) automatically loaded into every
    /// VeloSim Cameo lint VM and exportable via <c>VeloSim.Tools lint --emit-cameo-prelude</c>
    /// for VS Code / editor IDE static checking.
    /// </summary>
    public const string PreludeDeclaration = """
        -- VeloSim Tier 3 Cameo Lint Rule Prelude (velosim_lint.d.tl)
        -- Auto-loaded by VeloSim.Tools lint; drop into your workspace for Cameo VS Code type-checking.

        global record SignalInfo
            name: string
            width: integer
            direction: string
            is_signed: boolean
            is_two_state: boolean
            is_read: boolean
            is_written: boolean
            is_clock: boolean
        end

        global record MemoryInfo
            name: string
            width: integer
            depth: integer
            is_signed: boolean
            lower_bound: integer
            is_two_state: boolean
            is_read: boolean
            is_written: boolean
        end

        global record AssignmentInfo
            target: string
            target_owner: string
            target_width: integer
            value_width: integer
            is_blocking: boolean
            in_sequential: boolean
            context: string
        end

        global record StatementInfo
            kind: string
            context: string
            depth: integer
            in_sequential: boolean
        end

        global record ExpressionInfo
            kind: string
            context: string
            width: integer
            is_signed: boolean
        end

        global record FsmTransitionInfo
            from_state: string
            to_state: string
        end

        global record FsmInfo
            state_signal: string
            next_state_signal: string
            width: integer
            reset_state: string
            has_default_recovery: boolean
            states: List<string>
            unreachable_states: List<string>
            deadlock_states: List<string>
            transitions: List<FsmTransitionInfo>
            context: string
        end

        global record ClockDomainInfo
            clock_signal: string
            is_primary_input: boolean
            is_constrained_in_sdc: boolean
            signals: List<string>
        end

        global record CdcCrossingInfo
            kind: string
            source_signal: string
            source_domain: string
            destination_signal: string
            destination_domain: string
            width: integer
            synchronized: boolean
            synchronizer_stages: integer
            false_path: boolean
            context: string
        end

        global record RtlHelpers
            glob: function(string, string, ? boolean): boolean
            matches: function(string, string): boolean
            is_pow2: function(integer): boolean
            clog2: function(integer): integer
        end

        global record LintRuleMeta
            code: string
            summary: string
            hint: string
            phase: string
            group: string
            severity: string
            default_enabled: boolean
            standards: List<string>
        end

        global record LintContext
            source_file: string
            clock: string
            is_timed: boolean
            signals: List<SignalInfo>
            inputs: List<SignalInfo>
            outputs: List<SignalInfo>
            memories: List<MemoryInfo>
            assignments: List<AssignmentInfo>
            statements: List<StatementInfo>
            expressions: List<ExpressionInfo>
            fsms: List<FsmInfo>
            clock_domains: List<ClockDomainInfo>
            reset_signals: List<string>
            cdc_crossings: List<CdcCrossingInfo>
            signal_by_name: Dict<string, SignalInfo>
            memory_by_name: Dict<string, MemoryInfo>
            report: function(string, string, string, ? string, ? string)
            error: function(string, string, ? string, ? string)
            warn: function(string, string, ? string, ? string)
            info: function(string, string, ? string, ? string)
            param_int: function(string, integer): integer
            param_str: function(string, string): string
            param_bool: function(string, boolean): boolean
        end

        global rtl: RtlHelpers
        global rule: function(LintRuleMeta, function(LintContext))
        """;

    public static string DefaultRuleTemplate(string ruleCode = "CUSTOM_EXAMPLE") => $$"""
        -- VeloSim Tier 3 Cameo Custom Lint Rule Pack
        -- Validate: VeloSim.Tools lint --check-rules --rules <this-file.cameo>
        -- Run:      VeloSim.Tools lint <design.rtl.json|design.sv> --rules <this-file.cameo>

        rule({
            code = "{{ruleCode}}",
            phase = "rtl",
            group = "CAMEO",
            severity = "warning",
            default_enabled = true,
            standards = ["TEAM-RTL-001"],
            summary = "Flag non-power-of-two memories or wide internal buses beyond configured budget.",
            hint = "Adjust memory depth or configure cameo_params.max_bus_width in velosim-lint.json."
        }, (ctx: LintContext) => {
            let maxWidth = ctx.param_int("max_bus_width", 256)
            for s in ctx.signals {
                if s.width > maxWidth and not rtl.glob("dbg_*", s.name) {
                    ctx.warn(
                        "{{ruleCode}}",
                        $"Signal '{s.name}' width {s.width} exceeds configured max_bus_width ({maxWidth}).",
                        s.name,
                        "decl"
                    )
                }
            }
            for m in ctx.memories {
                if not rtl.is_pow2(m.depth) {
                    ctx.error(
                        "{{ruleCode}}",
                        $"Memory '{m.name}' depth {m.depth} is not a power of two (address bits={rtl.clog2(m.depth)}).",
                        m.name,
                        "memory"
                    )
                }
            }
        })
        """;

    public string SourceName { get; }
    public string Source { get; }
    public long MaxSteps { get; }
    public IReadOnlyList<IRtlLintRule> Rules { get; }

    private CameoLintRulePack(string sourceName, string source, long maxSteps, IReadOnlyList<LintRuleInfo> ruleInfos)
    {
        SourceName = sourceName;
        Source = source;
        MaxSteps = maxSteps;
        Rules = ruleInfos.Select(info => (IRtlLintRule)new CameoLintRule(this, info)).ToList().AsReadOnly();
    }

    public static IReadOnlyList<CameoLintScriptError> CheckFile(string path)
    {
        if (!File.Exists(path))
            return [new CameoLintScriptError(path, 0, 0, "Cameo lint rule script not found: " + path)];
        return CheckSource(File.ReadAllText(path), path);
    }

    public static IReadOnlyList<CameoLintScriptError> CheckSource(string source, string fileName = "rules.cameo")
    {
        ArgumentNullException.ThrowIfNull(source);
        using var engine = CreateHermeticEngine(DefaultMaxSteps);
        var diags = engine.Check(source, fileName);
        if (diags.Count == 0) return [];
        return diags.Select(d => new CameoLintScriptError(
            string.IsNullOrEmpty(d.File) ? fileName : d.File,
            d.Line,
            d.Col,
            LuaText.Decode(d.Message))).ToList().AsReadOnly();
    }

    public static CameoLintRulePack LoadFile(string path, long maxSteps = DefaultMaxSteps)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Cameo lint rule script not found: " + path, path);
        return LoadSource(File.ReadAllText(path), path, maxSteps);
    }

    public static CameoLintRulePack LoadSource(string source, string fileName = "rules.cameo", long maxSteps = DefaultMaxSteps)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maxSteps <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSteps), "cameo_max_steps must be positive.");

        var registered = new List<LintRuleInfo>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var engine = CreateHermeticEngine(maxSteps);
        BindRuleRegistrar(engine.State, (info, _) =>
        {
            if (!seenCodes.Add(info.Code))
                throw new LuaException($"Duplicate Cameo lint rule code '{info.Code}'.");
            registered.Add(info);
        });

        RunChecked(engine, source, fileName);

        if (registered.Count == 0)
            throw new CameoLintScriptException($"Cameo rule script '{fileName}' registered zero rules (call rule({{...}}, (ctx) => {{...}})).");

        return new CameoLintRulePack(fileName, source, maxSteps, registered);
    }

    internal void ExecuteBatch(
        RtlLintContext context,
        IReadOnlySet<string>? targetCodes,
        IReadOnlyDictionary<string, string>? cameoParams = null)
    {
        var handlers = new List<(LintRuleInfo Info, Value Handler)>();

        // LoadSource already statically type-checked Source against PreludeDeclaration;
        // skip redundant prelude parsing and re-type-checking at execution time for 2x-3x faster runs.
        using var engine = CreateHermeticEngine(MaxSteps, typeCheck: false);
        BindRuleRegistrar(engine.State, (info, handler) => handlers.Add((info, handler)));

        RunChecked(engine, Source, SourceName);

        Table ctxTable = BuildFrozenContextTable(engine.State, context, cameoParams);
        Value ctxVal = Value.FromTable(ctxTable);

        foreach (var (info, handler) in handlers)
        {
            if (targetCodes is not null && !targetCodes.Contains(info.Code))
                continue;

            RunResult callResult = engine.TryCall(handler, ctxVal);
            switch (callResult)
            {
                case RunCompleted:
                    break;
                case RuntimeFailed rf:
                    throw new CameoLintScriptException($"Cameo rule '{info.Code}' in '{SourceName}' failed at runtime: {rf.Message}");
                case RunAborted ra:
                    throw new CameoLintScriptException($"Cameo rule '{info.Code}' in '{SourceName}' aborted ({ra.Reason}; max_steps={MaxSteps.ToString(CultureInfo.InvariantCulture)}).");
                case RunExited re:
                    throw new CameoLintScriptException($"Cameo rule '{info.Code}' in '{SourceName}' exited unexpectedly with code {re.Code}.");
                case CompileFailed cf:
                    throw BuildCompileException(SourceName, cf.Diagnostics);
            }
        }
    }

    private static ScriptEngine CreateHermeticEngine(long maxSteps, bool typeCheck = true)
    {
        var options = new CameoRuntimeOptions
        {
            TypeCheck = typeCheck,
            Libraries = CameoLibraries.Hermetic,
            AllowDebug = false,
            AllowNetwork = false,
            AllowProcesses = false,
            AllowDatabase = false,
            FilePolicy = FilePolicy.DenyAll,
            MaxSteps = maxSteps
        };
        var engine = new ScriptEngine(options, VeloSimCameoLanguage.Instance);
        if (typeCheck)
            engine.DeclareGlobal(PreludeDeclaration, "<velosim-lint-prelude>");
        BindRtlHelpers(engine.State);
        return engine;
    }

    private static void BindRtlHelpers(LuaState state)
    {
        var rtlTable = new Table(0, 4);

        rtlTable.SetStr("glob", Value.FromFunction((ref CallContext cc) =>
        {
            string pattern = cc.CheckText(0);
            string text = cc.CheckText(1);
            bool ignoreCase = !cc.IsNoneOrNil(2) && cc.ToBoolean(2);
            return cc.Return(Value.FromBool(LintGlob.IsMatch(pattern, text, ignoreCase)));
        }, "rtl.glob"));

        rtlTable.SetStr("matches", Value.FromFunction((ref CallContext cc) =>
        {
            string pattern = cc.CheckText(0);
            string text = cc.CheckText(1);
            try
            {
                bool matched = Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                return cc.Return(Value.FromBool(matched));
            }
            catch (ArgumentException ex)
            {
                throw new LuaException("Invalid regex in rtl.matches(): " + ex.Message);
            }
        }, "rtl.matches"));

        rtlTable.SetStr("is_pow2", Value.FromFunction((ref CallContext cc) =>
        {
            long n = cc.CheckInt(0);
            return cc.Return(Value.FromBool(n > 0 && (n & (n - 1)) == 0));
        }, "rtl.is_pow2"));

        rtlTable.SetStr("clog2", Value.FromFunction((ref CallContext cc) =>
        {
            long n = cc.CheckInt(0);
            if (n <= 1) return cc.Return(Value.FromInt(0));
            ulong v = (ulong)(n - 1);
            int bits = 0;
            while (v > 0) { bits++; v >>= 1; }
            return cc.Return(Value.FromInt(bits));
        }, "rtl.clog2"));

        CollectionsLib.FreezeValue(Value.FromTable(rtlTable), new HashSet<object>(ReferenceEqualityComparer.Instance));
        state.Globals.SetStr("rtl", Value.FromTable(rtlTable));
    }

    private static void RunChecked(ScriptEngine engine, string source, string fileName)
    {
        RunResult result = engine.TryRunString(source, fileName);
        switch (result)
        {
            case RunCompleted:
                return;
            case CompileFailed cf:
                throw BuildCompileException(fileName, cf.Diagnostics);
            case RuntimeFailed rf:
                throw new CameoLintScriptException($"Cameo rule script '{fileName}' failed during registration: {rf.Message}");
            case RunAborted ra:
                throw new CameoLintScriptException($"Cameo rule script '{fileName}' aborted during registration ({ra.Reason}).");
            case RunExited re:
                throw new CameoLintScriptException($"Cameo rule script '{fileName}' exited during registration with code {re.Code}.");
        }
    }

    private static CameoLintScriptException BuildCompileException(string fileName, IReadOnlyList<Diagnostic> diags)
    {
        var errors = diags.Select(d => new CameoLintScriptError(
            string.IsNullOrEmpty(d.File) ? fileName : d.File,
            d.Line,
            d.Col,
            LuaText.Decode(d.Message))).ToList().AsReadOnly();
        string joined = string.Join(Environment.NewLine, errors.Select(e => e.ToString()));
        return new CameoLintScriptException($"Cameo lint script compile/type error(s) in '{fileName}':{Environment.NewLine}{joined}", errors);
    }

    private static void BindRuleRegistrar(LuaState state, Action<LintRuleInfo, Value> onRegister)
    {
        state.Globals.SetStr("rule", Value.FromFunction((ref CallContext cc) =>
        {
            Table meta = cc.CheckTable(0);
            Value handler = cc.CheckFunction(1);

            string code = ReadRequiredString(meta, "code").Trim().ToUpperInvariant();
            string summary = ReadOptionalString(meta, "summary") ?? $"Custom Cameo rule '{code}'.";
            string hint = ReadOptionalString(meta, "hint") ?? "";
            string group = (ReadOptionalString(meta, "group") ?? "CAMEO").Trim().ToUpperInvariant();
            string sevText = (ReadOptionalString(meta, "severity") ?? "warning").Trim().ToLowerInvariant();
            LintSeverity severity = sevText switch
            {
                "error" => LintSeverity.Error,
                "warning" or "warn" => LintSeverity.Warning,
                "info" or "note" => LintSeverity.Info,
                _ => throw new LuaException($"Unknown rule severity '{sevText}' (expected: error, warning, info).")
            };

            string? phaseText = ReadOptionalString(meta, "phase");
            LintPhase phase = string.IsNullOrWhiteSpace(phaseText)
                ? LintPhase.RtlSynthesis
                : LintPhases.Parse(phaseText);

            Value defVal = meta.GetStr("default_enabled");
            bool defaultEnabled = defVal.IsNil || defVal.IsTruthy;

            var standards = ReadStringList(state, meta.GetStr("standards"));
            var info = new LintRuleInfo(code, defaultEnabled, severity, group, summary, hint, phase, standards);
            onRegister(info, handler);
            return 0;
        }, "rule"));
    }

    private static string ReadRequiredString(Table table, string key)
    {
        string? val = ReadOptionalString(table, key);
        if (string.IsNullOrWhiteSpace(val))
            throw new LuaException($"rule() metadata requires non-empty string field '{key}'.");
        return val;
    }

    private static string? ReadOptionalString(Table table, string key)
    {
        Value v = table.GetStr(key);
        if (v.IsNil) return null;
        if (v.Kind == ValueKind.String || v.IsUtf16) return v.ToText();
        throw new LuaException($"rule() metadata field '{key}' must be a string.");
    }

    private static IReadOnlyList<string> ReadStringList(LuaState state, Value val)
    {
        if (val.IsNil) return [];
        if (val.O is LuaList)
        {
            Value listGlobal = state.Globals.GetStr("List");
            if (listGlobal.O is Table lt)
            {
                Value toTableFn = lt.GetStr("toTable");
                if (toTableFn.Kind == ValueKind.Function)
                {
                    Value[] res = state.Call(toTableFn, val);
                    if (res.Length > 0) val = res[0];
                }
            }
        }
        var list = new List<string>();
        if (val.Kind == ValueKind.Table && val.O is Table tbl)
        {
            int len = tbl.Length;
            for (int i = 1; i <= len; i++)
            {
                Value item = tbl.GetInt(i);
                if (!item.IsNil) list.Add(item.ToText());
            }
        }
        return list.AsReadOnly();
    }

    private static Table BuildFrozenContextTable(
        LuaState state,
        RtlLintContext context,
        IReadOnlyDictionary<string, string>? cameoParams)
    {
        Value listFromFn = ((Table)state.Globals.GetStr("List").O!).GetStr("from");
        Value dictFromFn = ((Table)state.Globals.GetStr("Dict").O!).GetStr("from");

        Value ToList(Table arr) => state.Call(listFromFn, Value.FromTable(arr))[0];
        Value ToDict(Table map) => state.Call(dictFromFn, Value.FromTable(map))[0];

        Value ToStringList(IEnumerable<string> items)
        {
            var t = new Table();
            int i = 1;
            foreach (string s in items) t.SetInt(i++, Value.FromText(s));
            return ToList(t);
        }

        // Signals list, inputs, outputs, and lookup dictionary
        var sigArr = new Table(context.Signals.Count, 0);
        var inArr = new Table();
        var outArr = new Table();
        var sigMap = new Table(0, context.Signals.Count);
        int inIdx = 1, outIdx = 1;
        for (int i = 0; i < context.Signals.Count; i++)
        {
            var s = context.Signals[i];
            var st = new Table(0, 8);
            st.SetStr("name", Value.FromText(s.Name));
            st.SetStr("width", Value.FromInt(s.Width));
            st.SetStr("direction", Value.FromText(s.Direction));
            st.SetStr("is_signed", Value.FromBool(s.Signed));
            st.SetStr("is_two_state", Value.FromBool(s.TwoState));
            st.SetStr("is_read", Value.FromBool(s.IsRead));
            st.SetStr("is_written", Value.FromBool(s.IsWritten));
            st.SetStr("is_clock", Value.FromBool(s.IsClockOrEvent));
            Value sv = Value.FromTable(st);
            sigArr.SetInt(i + 1, sv);
            sigMap.SetStr(s.Name, sv);
            if (s.Direction == "input") inArr.SetInt(inIdx++, sv);
            else if (s.Direction == "output") outArr.SetInt(outIdx++, sv);
        }

        // Memories list + lookup dictionary
        var memArr = new Table(context.Memories.Count, 0);
        var memMap = new Table(0, context.Memories.Count);
        for (int i = 0; i < context.Memories.Count; i++)
        {
            var m = context.Memories[i];
            var mt = new Table(0, 8);
            mt.SetStr("name", Value.FromText(m.Name));
            mt.SetStr("width", Value.FromInt(m.Width));
            mt.SetStr("depth", Value.FromInt(m.Depth));
            mt.SetStr("is_signed", Value.FromBool(m.Signed));
            mt.SetStr("lower_bound", Value.FromInt(m.LowerBound));
            mt.SetStr("is_two_state", Value.FromBool(m.TwoState));
            mt.SetStr("is_read", Value.FromBool(m.IsRead));
            mt.SetStr("is_written", Value.FromBool(m.IsWritten));
            Value mv = Value.FromTable(mt);
            memArr.SetInt(i + 1, mv);
            memMap.SetStr(m.Name, mv);
        }

        // Assignments
        var asgArr = new Table(context.Assignments.Count, 0);
        for (int i = 0; i < context.Assignments.Count; i++)
        {
            var a = context.Assignments[i];
            var at = new Table(0, 7);
            at.SetStr("target", Value.FromText(a.TargetDescription));
            at.SetStr("target_owner", Value.FromText(a.TargetOwner ?? ""));
            at.SetStr("target_width", Value.FromInt(a.TargetWidth ?? 0));
            at.SetStr("value_width", Value.FromInt(a.ValueWidth ?? 0));
            at.SetStr("is_blocking", Value.FromBool(a.IsBlocking));
            at.SetStr("in_sequential", Value.FromBool(a.InClockedProcess));
            at.SetStr("context", Value.FromText(a.Context));
            asgArr.SetInt(i + 1, Value.FromTable(at));
        }

        // Statements
        var stmtArr = new Table(context.Statements.Count, 0);
        for (int i = 0; i < context.Statements.Count; i++)
        {
            var s = context.Statements[i];
            var st = new Table(0, 4);
            st.SetStr("kind", Value.FromText(s.Kind));
            st.SetStr("context", Value.FromText(s.Context));
            st.SetStr("depth", Value.FromInt(s.Depth));
            st.SetStr("in_sequential", Value.FromBool(s.InClockedProcess));
            stmtArr.SetInt(i + 1, Value.FromTable(st));
        }

        // Expressions
        var exprArr = new Table(context.Expressions.Count, 0);
        for (int i = 0; i < context.Expressions.Count; i++)
        {
            var e = context.Expressions[i];
            var et = new Table(0, 4);
            et.SetStr("kind", Value.FromText(e.Kind));
            et.SetStr("context", Value.FromText(e.Context));
            et.SetStr("width", Value.FromInt(e.Width ?? 0));
            et.SetStr("is_signed", Value.FromBool(e.Signed ?? false));
            exprArr.SetInt(i + 1, Value.FromTable(et));
        }

        // Extracted FSMs
        var fsmArr = new Table(context.Fsms.Count, 0);
        for (int i = 0; i < context.Fsms.Count; i++)
        {
            var f = context.Fsms[i];
            var ft = new Table(0, 10);
            ft.SetStr("state_signal", Value.FromText(f.StateSignal));
            ft.SetStr("next_state_signal", Value.FromText(f.NextStateSignal ?? ""));
            ft.SetStr("width", Value.FromInt(f.Width));
            ft.SetStr("reset_state", Value.FromText(f.ResetState ?? ""));
            ft.SetStr("has_default_recovery", Value.FromBool(f.HasDefaultRecovery));
            ft.SetStr("states", ToStringList(f.States));
            ft.SetStr("unreachable_states", ToStringList(f.UnreachableStates));
            ft.SetStr("deadlock_states", ToStringList(f.DeadlockStates));
            var trArr = new Table(f.Transitions.Count, 0);
            for (int t = 0; t < f.Transitions.Count; t++)
            {
                var tr = new Table(0, 2);
                tr.SetStr("from_state", Value.FromText(f.Transitions[t].FromState));
                tr.SetStr("to_state", Value.FromText(f.Transitions[t].ToState));
                trArr.SetInt(t + 1, Value.FromTable(tr));
            }
            ft.SetStr("transitions", ToList(trArr));
            ft.SetStr("context", Value.FromText(f.Context));
            fsmArr.SetInt(i + 1, Value.FromTable(ft));
        }

        // Clock domains & CDC crossings
        var domArr = new Table(context.Cdc.ClockDomains.Count, 0);
        for (int i = 0; i < context.Cdc.ClockDomains.Count; i++)
        {
            var d = context.Cdc.ClockDomains[i];
            var dt = new Table(0, 4);
            dt.SetStr("clock_signal", Value.FromText(d.ClockSignal));
            dt.SetStr("is_primary_input", Value.FromBool(d.IsPrimaryInput));
            dt.SetStr("is_constrained_in_sdc", Value.FromBool(d.IsConstrainedInSdc));
            dt.SetStr("signals", ToStringList(d.Signals));
            domArr.SetInt(i + 1, Value.FromTable(dt));
        }

        var cdcArr = new Table(context.Cdc.Crossings.Count, 0);
        for (int i = 0; i < context.Cdc.Crossings.Count; i++)
        {
            var c = context.Cdc.Crossings[i];
            var ct = new Table(0, 10);
            ct.SetStr("kind", Value.FromText(c.Kind));
            ct.SetStr("source_signal", Value.FromText(c.SourceSignal));
            ct.SetStr("source_domain", Value.FromText(c.SourceDomain));
            ct.SetStr("destination_signal", Value.FromText(c.DestinationSignal));
            ct.SetStr("destination_domain", Value.FromText(c.DestinationDomain));
            ct.SetStr("width", Value.FromInt(c.Width));
            ct.SetStr("synchronized", Value.FromBool(c.Synchronized));
            ct.SetStr("synchronizer_stages", Value.FromInt(c.SynchronizerStages));
            ct.SetStr("false_path", Value.FromBool(c.FalsePath));
            ct.SetStr("context", Value.FromText(c.Context));
            cdcArr.SetInt(i + 1, Value.FromTable(ct));
        }

        var ctxTable = new Table(0, 24);
        ctxTable.SetStr("source_file", Value.FromText(context.SourceFile ?? ""));
        ctxTable.SetStr("clock", Value.FromText(context.Clock ?? ""));
        ctxTable.SetStr("is_timed", Value.FromBool(context.IsTimed));
        ctxTable.SetStr("signals", ToList(sigArr));
        ctxTable.SetStr("inputs", ToList(inArr));
        ctxTable.SetStr("outputs", ToList(outArr));
        ctxTable.SetStr("memories", ToList(memArr));
        ctxTable.SetStr("assignments", ToList(asgArr));
        ctxTable.SetStr("statements", ToList(stmtArr));
        ctxTable.SetStr("expressions", ToList(exprArr));
        ctxTable.SetStr("fsms", ToList(fsmArr));
        ctxTable.SetStr("clock_domains", ToList(domArr));
        ctxTable.SetStr("reset_signals", ToStringList(context.Cdc.ResetSignals));
        ctxTable.SetStr("cdc_crossings", ToList(cdcArr));
        ctxTable.SetStr("signal_by_name", ToDict(sigMap));
        ctxTable.SetStr("memory_by_name", ToDict(memMap));

        // Bind reporting methods: report, error, warn, info
        ctxTable.SetStr("report", Value.FromFunction((ref CallContext cc) =>
        {
            string code = cc.CheckText(0);
            string sevText = cc.CheckText(1).Trim().ToLowerInvariant();
            string message = cc.CheckText(2);
            string? signal = cc.IsNoneOrNil(3) ? null : cc.CheckText(3);
            string? ctxName = cc.IsNoneOrNil(4) ? null : cc.CheckText(4);

            var sev = sevText switch
            {
                "error" => LintSeverity.Error,
                "warning" or "warn" => LintSeverity.Warning,
                "info" or "note" => LintSeverity.Info,
                _ => throw new LuaException($"Unknown severity '{sevText}' in ctx.report() (expected: error, warning, info).")
            };

            context.Report(code, sev, message, signal, ctxName, source: "cameo");
            return 0;
        }, "ctx.report"));

        void BindSeverityReporter(string methodName, LintSeverity severity)
        {
            ctxTable.SetStr(methodName, Value.FromFunction((ref CallContext cc) =>
            {
                string code = cc.CheckText(0);
                string message = cc.CheckText(1);
                string? signal = cc.IsNoneOrNil(2) ? null : cc.CheckText(2);
                string? ctxName = cc.IsNoneOrNil(3) ? null : cc.CheckText(3);
                context.Report(code, severity, message, signal, ctxName, source: "cameo");
                return 0;
            }, "ctx." + methodName));
        }
        BindSeverityReporter("error", LintSeverity.Error);
        BindSeverityReporter("warn", LintSeverity.Warning);
        BindSeverityReporter("info", LintSeverity.Info);

        // Parameterized Cameo rules (backed by velosim-lint.json cameo_params)
        ctxTable.SetStr("param_int", Value.FromFunction((ref CallContext cc) =>
        {
            string key = cc.CheckText(0);
            long fallback = cc.CheckInt(1);
            if (cameoParams is not null && cameoParams.TryGetValue(key, out string? rawVal) &&
                long.TryParse(rawVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                return cc.Return(Value.FromInt(parsed));
            return cc.Return(Value.FromInt(fallback));
        }, "ctx.param_int"));

        ctxTable.SetStr("param_str", Value.FromFunction((ref CallContext cc) =>
        {
            string key = cc.CheckText(0);
            string fallback = cc.CheckText(1);
            if (cameoParams is not null && cameoParams.TryGetValue(key, out string? rawVal))
                return cc.ReturnText(rawVal);
            return cc.ReturnText(fallback);
        }, "ctx.param_str"));

        ctxTable.SetStr("param_bool", Value.FromFunction((ref CallContext cc) =>
        {
            string key = cc.CheckText(0);
            bool fallback = cc.ToBoolean(1);
            if (cameoParams is not null && cameoParams.TryGetValue(key, out string? rawVal) &&
                bool.TryParse(rawVal, out bool parsed))
                return cc.Return(Value.FromBool(parsed));
            return cc.Return(Value.FromBool(fallback));
        }, "ctx.param_bool"));

        // Deep-freeze the entire IR snapshot so Cameo rules cannot mutate shared host tables.
        CollectionsLib.FreezeValue(Value.FromTable(ctxTable), new HashSet<object>(ReferenceEqualityComparer.Instance));
        return ctxTable;
    }

    internal sealed class CameoLintRule(CameoLintRulePack pack, LintRuleInfo info) : IRtlLintRule
    {
        public CameoLintRulePack Pack { get; } = pack;
        public LintRuleInfo Info { get; } = info;

        public void Analyze(RtlLintContext context) =>
            Pack.ExecuteBatch(context, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Info.Code });
    }
}
