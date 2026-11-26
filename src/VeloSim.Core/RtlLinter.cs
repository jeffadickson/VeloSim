using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VeloSim;

public enum LintSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

public sealed record LintLocation(
    string? File = null,
    int? Line = null,
    int? Column = null,
    string? Signal = null,
    string? Context = null);

public sealed record LintDiagnostic(
    string Code,
    LintSeverity Severity,
    string Message,
    string Source,
    LintLocation? Location = null,
    string? Phase = null,
    IReadOnlyList<string>? GuidelineTags = null);

public sealed record LintRuleInfo(
    string Code,
    bool DefaultEnabled,
    LintSeverity DefaultSeverity,
    string Group,
    string Summary,
    string Hint = "",
    LintPhase Phase = LintPhase.RtlSynthesis,
    IReadOnlyList<string>? Standards = null);

public sealed record LintOptions(
    bool Wall = false,
    bool WarningsAsErrors = false,
    bool NoFatal = false,
    IReadOnlySet<string>? EnabledRules = null,
    IReadOnlySet<string>? DisabledRules = null,
    IReadOnlySet<string>? ErrorRules = null,
    bool LargeDesign = false,
    bool SlangOnly = false,
    IReadOnlyList<string>? IncludeDirectories = null,
    IReadOnlyList<string>? Defines = null,
    LintProfile? Profile = null,
    LintConfig? Config = null,
    LintBaseline? Baseline = null,
    LintRuleParameters? Parameters = null,
    IReadOnlyList<IRtlLintRule>? CustomRules = null,
    bool WarnUnusedWaivers = false,
    DateOnly? AsOfDate = null,
    IReadOnlySet<LintPhase>? Phases = null,
    bool StopOnPhaseError = false,
    SdcConstraints? Sdc = null,
    bool HasExplicitSdc = false,
    LintGroupBy GroupBy = LintGroupBy.None)
{
    public static LintOptions Default { get; } = new();
    public static LintOptions All { get; } = new(Wall: true);

    public LintProfile EffectiveProfile =>
        Profile ?? (Wall ? LintProfile.Wall : Config?.Profile ?? LintProfile.Default);

    public bool EffectiveWall =>
        Wall || LintProfiles.EnablesAllWarnings(EffectiveProfile);

    public bool EffectiveWarningsAsErrors =>
        !NoFatal && (WarningsAsErrors || (Config?.WarningsAsErrors ?? false));

    public bool EffectiveWarnUnusedWaivers =>
        WarnUnusedWaivers || (Config?.WarnUnusedWaivers ?? false);

    public LintRuleParameters EffectiveParameters =>
        Parameters ?? Config?.Parameters ?? LintRuleParameters.Default;

    public IReadOnlySet<LintPhase>? EffectivePhases =>
        Phases ?? Config?.Phases;

    public bool EffectiveStopOnPhaseError =>
        StopOnPhaseError || (Config?.StopOnPhaseError ?? false);
}

public static class LintRuleCatalog
{
    public const string CatalogSchemaVersion = "velosim.lint.rules.v1";

    public static IReadOnlyList<LintRuleInfo> Rules { get; } =
    [
        // Phase 1 (Root Cause): RTL & Synthesis Correctness
        new("LATCH", true, LintSeverity.Error, "COMB",
            "Combinational block leaves destination unassigned on at least one path or reads before definite assignment.",
            "Assign a default value at the top of the combinational block or assign on every if/case branch.",
            LintPhase.RtlSynthesis, ["DO-254", "STARC-2.3.1.2", "RMM-5.5.4", "ISO-26262", "LOWRISC"]),
        new("UNOPTFLAT", true, LintSeverity.Error, "COMB",
            "Combinational feedback cycle across continuous or combinational blocks.",
            "Break the combinational loop with a clocked register or separate unrelated bits.",
            LintPhase.RtlSynthesis, ["DO-254", "STARC-2.1.6.1", "RMM-5.5.6", "ISO-26262", "LOWRISC"]),
        new("MULTIDRIVEN", true, LintSeverity.Error, "DRIVER",
            "Signal or memory is driven by multiple conflicting processes or blocks.",
            "Consolidate writes to a single owning process or use a resolved net.",
            LintPhase.RtlSynthesis, ["DO-254", "STARC-2.1.4.5", "RMM-5.2", "ISO-26262", "LOWRISC"]),
        new("BLKANDNBLK", true, LintSeverity.Error, "DRIVER",
            "Signal mixes blocking (=) and non-blocking (<=) assignments.",
            "Use non-blocking (<=) consistently in sequential blocks and blocking (=) in combinational blocks.",
            LintPhase.RtlSynthesis, ["DO-254", "STARC-2.2.2.1", "RMM-5.2.3", "ISO-26262", "LOWRISC"]),
        new("ASSIGNIN", true, LintSeverity.Error, "DRIVER",
            "Assignment targets an input port.",
            "Remove the assignment to the input port or change the port direction.",
            LintPhase.RtlSynthesis, ["DO-254", "STARC-1.1.2.4", "ISO-26262", "LOWRISC"]),
        new("INFLOOP", true, LintSeverity.Error, "COMB",
            "Untimed or combinational loop has a constant true condition with no break or suspension.",
            "Add a bounded loop condition, a break exit, or an explicit timing/event control.",
            LintPhase.RtlSynthesis, ["DO-254", "STARC-2.3.6.1", "ISO-26262"]),
        new("DIVZERO", true, LintSeverity.Error, "ARITH",
            "Division or modulo by a constant zero.",
            "Guard the divisor against zero or fix the constant divisor.",
            LintPhase.RtlSynthesis, ["DO-254", "ISO-26262"]),
        new("SHIFTOVF", true, LintSeverity.Warning, "ARITH",
            "Constant shift amount equals or exceeds the shifted operand width.",
            "Reduce the shift amount or widen the operand before shifting.",
            LintPhase.RtlSynthesis, ["DO-254", "ISO-26262"]),
        new("UNDRIVEN", true, LintSeverity.Warning, "DRIVER",
            "Non-input signal is never written by any process or assignment.",
            "Drive the signal or remove the unused declaration.",
            LintPhase.RtlSynthesis, ["DO-254", "STARC-2.1.3.1", "ISO-26262", "LOWRISC"]),
        new("WIDTHTRUNC", true, LintSeverity.Warning, "WIDTH",
            "Expression is truncated to a narrower assignment target or case selector.",
            "Use an explicit part-select or width cast to make truncation intentional.",
            LintPhase.RtlSynthesis, ["DO-254", "STARC-2.10.3.1", "RMM-5.2", "ISO-26262", "LOWRISC"]),
        new("BLKSEQ", false, LintSeverity.Warning, "SEQ",
            "Blocking assignment (=) used on module state inside a clocked sequential process.",
            "Use non-blocking assignment (<=) for clocked registers to avoid simulation/synthesis races.",
            LintPhase.RtlSynthesis, ["DO-254", "STARC-2.2.2.3", "RMM-5.2.3", "ISO-26262", "LOWRISC"]),
        new("CASEINCOMPLETE", false, LintSeverity.Warning, "CASE",
            "Case statement has no default branch and does not exhaust all selector values.",
            "Add a default branch or cover all 2^W selector values.",
            LintPhase.RtlSynthesis, ["DO-254", "STARC-2.8.1.3", "RMM-5.5.4", "ISO-26262", "LOWRISC"]),
        new("CASEOVERLAP", true, LintSeverity.Warning, "CASE",
            "Case statement contains duplicate match labels.",
            "Remove or merge duplicate case items.",
            LintPhase.RtlSynthesis, ["DO-254", "STARC-2.8.1.5", "ISO-26262", "LOWRISC"]),
        new("CASEX", false, LintSeverity.Warning, "CASE",
            "Use of casex treats X as a wildcard; prefer casez or inside.",
            "Replace casex with casez (using '?') or SystemVerilog 'inside'.",
            LintPhase.RtlSynthesis, ["DO-254", "STARC-2.8.1.1", "RMM-5.5.4", "ISO-26262", "LOWRISC"]),
        new("CASEWITHX", true, LintSeverity.Warning, "CASE",
            "Case or casez label contains literal 'x' which never matches in 2-state synthesis.",
            "Replace 'x' with '?' in casez or use an explicit known pattern.",
            LintPhase.RtlSynthesis, ["DO-254", "STARC-2.8.1.2", "ISO-26262", "LOWRISC"]),
        new("SELRANGE", true, LintSeverity.Warning, "RANGE",
            "Constant bit select, part select, or memory index is out of declared bounds.",
            "Keep constant bit/part selects and memory indices within declared bounds.",
            LintPhase.RtlSynthesis, ["DO-254", "STARC-2.10.2.3", "ISO-26262"]),
        new("FOURSTATE", false, LintSeverity.Warning, "FOURSTATE",
            "Four-state X/Z equality hazard (== against X/Z) or 4-state expression assigned to 2-state storage.",
            "Use ===/!== or ==?/!=? for X/Z checks, or convert explicitly before writing two-state storage.",
            LintPhase.RtlSynthesis, ["ISO-26262", "STARC-2.10.1.4"]),
        new("RACEWW", true, LintSeverity.Warning, "RACE",
            "Potential multi-process write-write race in timed design.",
            "Synchronize writers or assign each variable from a single process.",
            LintPhase.RtlSynthesis, ["ISO-26262"]),
        new("RACERW", true, LintSeverity.Warning, "RACE",
            "Potential blocking-write / concurrent-read race in timed design.",
            "Use non-blocking assignments or explicit event/clock synchronization.",
            LintPhase.RtlSynthesis, ["ISO-26262"]),
        new("VALIDERROR", true, LintSeverity.Error, "VALID",
            "Structural or semantic IR validation error.",
            "Fix the IR schema or HDL semantic error reported in the message.",
            LintPhase.RtlSynthesis, ["DO-254", "ISO-26262"]),

        // Phase 2: FSM Verification (Aldec ALINT-PRO / Questa / SpyGlass)
        new("FSMUNREACH", true, LintSeverity.Warning, "FSM",
            "FSM state is unreachable from the reset state.",
            "Add a valid transition into the state or remove dead state encodings.",
            LintPhase.Fsm, ["DO-254", "STARC-2.11.3.1", "ISO-26262"]),
        new("FSMDEADLOCK", true, LintSeverity.Warning, "FSM",
            "FSM state has no outgoing transition to any other state (deadlock/trap state).",
            "Add an exit transition or recovery path back to IDLE/reset.",
            LintPhase.Fsm, ["DO-254", "STARC-2.11.3.2", "ISO-26262"]),
        new("FSMNORESET", false, LintSeverity.Warning, "FSM",
            "Extracted FSM state register has no constant reset state assignment.",
            "Initialize the FSM state register to a known reset state.",
            LintPhase.Fsm, ["DO-254", "STARC-2.11.1.1", "RMM-5.5.5", "ISO-26262"]),
        new("FSMRECOVERY", false, LintSeverity.Warning, "FSM",
            "FSM case statement has no default recovery transition to a safe state for illegal/SEU encodings.",
            "Add a default branch assigning the reset/safe state for SEU and illegal-state recovery.",
            LintPhase.Fsm, ["DO-254", "STARC-2.11.2.1", "ISO-26262", "LOWRISC"]),

        // Phase 3: Clock / Reset Tree, CDC / RDC, SDC & DFT Controllability (Aldec ALINT-PRO ALDEC_CDC)
        new("CLKCOMB", true, LintSeverity.Error, "COMB",
            "Clock or event signal read inside combinational or continuous logic.",
            "Keep clock signals out of combinational data paths; use clock-enable logic instead.",
            LintPhase.ClockResetCdc, ["DO-254", "STARC-1.4.1.1", "RMM-5.5.2", "ISO-26262", "LOWRISC"]),
        new("SYNCASYNCNET", false, LintSeverity.Warning, "SEQ",
            "Signal is used both as an asynchronous edge trigger and in synchronous/combinational logic.",
            "Keep asynchronous reset nets strictly on async edge lists and their corresponding reset branches.",
            LintPhase.ClockResetCdc, ["DO-254", "STARC-1.3.1.3", "RMM-5.5.3", "ISO-26262"]),
        new("RESETCONSIST", false, LintSeverity.Warning, "SEQ",
            "Sequential reset branch initializes some registers while leaving others unreset in the same block.",
            "Reset all registers in the block or split unreset datapath flops into a separate always_ff.",
            LintPhase.ClockResetCdc, ["DO-254", "STARC-2.3.4.1", "ISO-26262"]),
        new("CDCUNSYNC", true, LintSeverity.Warning, "CDC",
            "Signal crosses asynchronous clock domains without a recognized 2-FF synchronizer or SDC false-path.",
            "Add a 2-FF synchronizer (1-bit), Gray-coded async FIFO (multi-bit), or explicit SDC false-path.",
            LintPhase.ClockResetCdc, ["DO-254", "STARC-1.4.3.1", "RMM-5.5.2", "ISO-26262", "LOWRISC"]),
        new("RDCUNSYNC", true, LintSeverity.Warning, "CDC",
            "Signal crosses distinct reset domains without synchronization or SDC false-path.",
            "Sequence reset assertion/deassertion or isolate reset domains with a synchronizer.",
            LintPhase.ClockResetCdc, ["DO-254", "ISO-26262", "LOWRISC"]),
        new("DFTCLKCTRL", false, LintSeverity.Warning, "DFT",
            "Internally generated or gated clock is not directly controllable from a primary input (DFT hazard).",
            "Drive clocks from primary inputs / dedicated clock buffers or add a scan-test clock mux.",
            LintPhase.ClockResetCdc, ["DO-254", "STARC-1.4.1.2", "RMM-5.5.2"]),
        new("DFTRSTCTRL", false, LintSeverity.Warning, "DFT",
            "Internally generated asynchronous reset is not directly controllable from a primary input (DFT hazard).",
            "Drive asynchronous resets from primary inputs or add a scan-test reset bypass mux.",
            LintPhase.ClockResetCdc, ["DO-254", "STARC-1.3.1.4", "RMM-5.5.3"]),
        new("SDCUCONSTR", true, LintSeverity.Warning, "SDC",
            "Clock detected in RTL is unconstrained in the provided SDC file.",
            "Add a create_clock or create_generated_clock constraint for this clock.",
            LintPhase.ClockResetCdc, ["DO-254", "RMM-5.5.2"]),
        new("SDCOVERCONSTR", true, LintSeverity.Warning, "SDC",
            "Clock constrained in SDC does not match any signal in RTL.",
            "Remove the stale SDC clock constraint or fix the target port name.",
            LintPhase.ClockResetCdc, ["DO-254"]),

        // Phase 4: Coding Style, Unused, Naming, Budgets & Waivers
        new("UNUSEDSIGNAL", false, LintSeverity.Warning, "UNUSED",
            "Declared input or internal signal is never read.",
            "Remove the signal, connect it, or match it with unused_ignore_patterns / a scoped waiver.",
            LintPhase.SyntaxStyle, ["STARC-2.1.3.2", "LOWRISC"]),
        new("UNUSEDMEM", false, LintSeverity.Warning, "UNUSED",
            "Declared memory is never read.",
            "Remove the memory or read from it.",
            LintPhase.SyntaxStyle, ["STARC-2.1.3.2"]),
        new("WIDTHEXPAND", false, LintSeverity.Warning, "WIDTH",
            "Expression is implicitly widened in an assignment, operator, conditional, or case item.",
            "Match operand/literal bit widths explicitly or configure allow_literal_width_expand.",
            LintPhase.SyntaxStyle, ["STARC-2.10.3.2", "LOWRISC"]),
        new("SIGNCOMPARE", false, LintSeverity.Warning, "SIGN",
            "Signed and unsigned operands are mixed in a comparison, conditional, or assignment.",
            "Cast explicitly with $signed() or $unsigned() so comparison semantics are unambiguous.",
            LintPhase.SyntaxStyle, ["STARC-2.10.3.6", "LOWRISC"]),
        new("IFDEPTH", false, LintSeverity.Warning, "STYLE",
            "Deeply nested if/else-if chain exceeds max_if_depth (synthesizes to a slow priority encoder).",
            "Refactor the priority chain into a case / unique case statement or adjust params.max_if_depth.",
            LintPhase.SyntaxStyle, ["STARC-2.8.4.3", "RMM-5.5.4"]),
        new("SELFASSIGN", true, LintSeverity.Warning, "STYLE",
            "Trivial self-assignment has no effect.",
            "Remove the redundant self-assignment or fix the misspelled RHS signal.",
            LintPhase.SyntaxStyle, ["STARC-2.1.4.4", "LOWRISC"]),
        new("CMPCONST", false, LintSeverity.Warning, "STYLE",
            "Branch condition or comparison is statically constant.",
            "Remove dead branches or widen the compared operand.",
            LintPhase.SyntaxStyle, ["DO-254", "STARC-2.10.1.3", "LOWRISC"]),
        new("NAMING", true, LintSeverity.Warning, "STYLE",
            "Signal or memory name violates configured naming regex in velosim-lint.json.",
            "Rename the identifier to match params.naming or add a scoped waiver.",
            LintPhase.SyntaxStyle, ["RMM-5.1", "LOWRISC"]),
        new("WIDTHBUDGET", true, LintSeverity.Warning, "RANGE",
            "Signal width or memory depth exceeds configured project budget in velosim-lint.json.",
            "Keep widths/depths within params.max_signal_width / max_memory_depth or waive.",
            LintPhase.SyntaxStyle, ["RMM-5.2"]),
        new("UNUSEDWAIVER", true, LintSeverity.Warning, "WAIVER",
            "Configured lint waiver matched zero diagnostics.",
            "Remove stale waivers from velosim-lint.json to keep waiver audits clean.",
            LintPhase.SyntaxStyle, ["DO-254"]),
        new("EXPIREDWAIVER", true, LintSeverity.Warning, "WAIVER",
            "Configured lint waiver has passed its expiration date.",
            "Fix the underlying issue or renew the waiver with an updated expiration date.",
            LintPhase.SyntaxStyle, ["DO-254"]),
        new("VS-SUBSET", true, LintSeverity.Info, "SUBSET",
            "SystemVerilog construct is outside VeloSim's direct IR lowering subset; slang lint still applied.",
            "Use --slang-only to suppress this informational note for non-lowered testbench code.",
            LintPhase.SyntaxStyle, [])
    ];

    private static readonly Dictionary<string, LintRuleInfo> ByCode =
        Rules.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string[]> GroupAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ALL"] = Rules.Select(r => r.Code).ToArray(),
        ["WIDTH"] = ["WIDTHTRUNC", "WIDTHEXPAND"],
        ["UNUSED"] = ["UNUSEDSIGNAL", "UNUSEDMEM"],
        ["CASE"] = ["CASEINCOMPLETE", "CASEOVERLAP", "CASEX", "CASEWITHX"],
        ["RACE"] = ["RACEWW", "RACERW"],
        ["COMB"] = ["LATCH", "UNOPTFLAT", "CLKCOMB", "INFLOOP"],
        ["DRIVER"] = ["MULTIDRIVEN", "BLKANDNBLK", "UNDRIVEN", "ASSIGNIN"],
        ["ARITH"] = ["SHIFTOVF", "DIVZERO"],
        ["SEQ"] = ["BLKSEQ", "SYNCASYNCNET", "RESETCONSIST"],
        ["FSM"] = ["FSMUNREACH", "FSMDEADLOCK", "FSMNORESET", "FSMRECOVERY"],
        ["CDC"] = ["CDCUNSYNC", "RDCUNSYNC"],
        ["DFT"] = ["DFTCLKCTRL", "DFTRSTCTRL"],
        ["SDC"] = ["SDCUCONSTR", "SDCOVERCONSTR"],
        ["STYLE"] = ["SELFASSIGN", "CMPCONST", "IFDEPTH", "NAMING"],
        ["RANGE"] = ["SELRANGE", "WIDTHBUDGET"],
        ["WAIVER"] = ["UNUSEDWAIVER", "EXPIREDWAIVER"]
    };

    public static bool TryGetRule(string code, out LintRuleInfo info) => ByCode.TryGetValue(code, out info!);

    public static IEnumerable<string> ExpandRuleOrGroup(string token)
    {
        string normalized = token.Trim().ToUpperInvariant();
        if (GroupAliases.TryGetValue(normalized, out var expanded))
            return expanded;
        return [normalized];
    }

    public static HashSet<string> ExpandSet(IReadOnlySet<string>? tokens)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (tokens is null) return set;
        foreach (string token in tokens)
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            foreach (string code in ExpandRuleOrGroup(token))
                set.Add(code);
        }
        return set;
    }

    public static string FormatRulesText(IEnumerable<IRtlLintRule>? customRules = null)
    {
        var all = customRules is null ? Rules : Rules.Concat(customRules.Select(r => r.Info));
        var sb = new StringBuilder();
        sb.AppendLine("CODE             PHASE  GROUP      DEFAULT  SEVERITY  SUMMARY");
        sb.AppendLine("---------------  -----  ---------  -------  --------  -------");
        foreach (var r in all)
        {
            string def = r.DefaultEnabled ? "on" : "-Wall";
            string sev = r.DefaultSeverity.ToString().ToLowerInvariant();
            string ph = LintPhases.ToName(r.Phase);
            sb.Append(CultureInfo.InvariantCulture, $"{r.Code,-15}  {ph,-5}  {r.Group,-9}  {def,-7}  {sev,-8}  {r.Summary}");
            sb.AppendLine();
            if (r.Standards is { Count: > 0 })
                sb.Append(CultureInfo.InvariantCulture, $"                                                      Standards: {string.Join(", ", r.Standards)}").AppendLine();
            if (!string.IsNullOrWhiteSpace(r.Hint))
                sb.Append(CultureInfo.InvariantCulture, $"                                                      Hint: {r.Hint}").AppendLine();
        }
        return sb.ToString();
    }

    public static string FormatRulesJson(IEnumerable<IRtlLintRule>? customRules = null)
    {
        var all = customRules is null ? Rules : Rules.Concat(customRules.Select(r => r.Info));
        var contract = new LintRuleCatalogContract(
            CatalogSchemaVersion,
            all.Select(r => new LintRuleInfoContract(
                r.Code,
                r.DefaultEnabled,
                r.DefaultSeverity.ToString().ToLowerInvariant(),
                r.Group,
                LintPhases.ToName(r.Phase),
                r.Standards?.ToArray() ?? [],
                r.Summary,
                r.Hint)).ToArray());
        return JsonSerializer.Serialize(contract, LintJsonContext.Default.LintRuleCatalogContract);
    }
}

public sealed record LintReport(
    string Schema,
    string Status,
    bool Wall,
    bool WarningsAsErrors,
    int ErrorCount,
    int WarningCount,
    int InfoCount,
    IReadOnlyList<LintDiagnostic> Diagnostics,
    string Profile = "default",
    int WaivedCount = 0,
    int BaselinedCount = 0,
    LintQualityMetrics? Quality = null,
    string? StoppedAtPhase = null,
    IReadOnlyList<FsmInfo>? Fsms = null,
    CdcAnalysisResult? Cdc = null)
{
    public const string SarifSchemaUri = "https://json.schemastore.org/sarif-2.1.0.json";
    public const string SarifVersion = "2.1.0";
    public const string FsmDumpSchema = "velosim.lint.fsm.v1";
    public const string CdcDumpSchema = "velosim.lint.cdc.v1";

    public bool Passed => Status == "pass";
    public int ExitCode => Passed ? 0 : 1;
    public LintQualityMetrics EffectiveQuality => Quality ?? LintQualityMetrics.Compute(ErrorCount, WarningCount, WaivedCount, 0);

    public string FormatText(LintGroupBy groupBy = LintGroupBy.None)
    {
        var sb = new StringBuilder();
        if (groupBy == LintGroupBy.None)
        {
            foreach (var d in Diagnostics)
                AppendDiagnosticLine(sb, d);
        }
        else
        {
            var groups = Diagnostics.GroupBy(d => groupBy switch
            {
                LintGroupBy.Rule => d.Code,
                LintGroupBy.Signal => d.Location?.Signal ?? "<design>",
                LintGroupBy.File => d.Location?.File ?? "<in-memory>",
                LintGroupBy.Phase => "phase:" + (d.Phase ?? "rtl"),
                _ => d.Code
            }, StringComparer.OrdinalIgnoreCase);

            foreach (var g in groups)
            {
                int count = g.Count();
                sb.Append(CultureInfo.InvariantCulture, $"=== [{g.Key}] ({count} finding{(count == 1 ? "" : "s")}) ===").AppendLine();
                foreach (var d in g)
                {
                    sb.Append("  ");
                    AppendDiagnosticLine(sb, d);
                }
            }
        }

        if (!string.IsNullOrEmpty(StoppedAtPhase))
            sb.Append(CultureInfo.InvariantCulture, $"note: Phase-Based Linting stopped after phase '{StoppedAtPhase}' due to error(s).").AppendLine();

        var q = EffectiveQuality;
        sb.Append(CultureInfo.InvariantCulture, $"lint {Status}: {ErrorCount} error(s), {WarningCount} warning(s)");
        if (InfoCount > 0) sb.Append(CultureInfo.InvariantCulture, $", {InfoCount} info");
        if (WaivedCount > 0) sb.Append(CultureInfo.InvariantCulture, $", {WaivedCount} waived");
        if (BaselinedCount > 0) sb.Append(CultureInfo.InvariantCulture, $", {BaselinedCount} baselined");
        sb.Append(CultureInfo.InvariantCulture, $" | quality={q.Score:F1}/100 ({q.Grade})");
        sb.AppendLine();
        return sb.ToString();
    }

    private static void AppendDiagnosticLine(StringBuilder sb, LintDiagnostic d)
    {
        string prefix = d.Severity switch
        {
            LintSeverity.Error => "%Error",
            LintSeverity.Warning => "%Warning",
            _ => "%Info"
        };
        sb.Append(prefix).Append('-').Append(d.Code).Append(':');
        if (d.Location is { } loc)
        {
            if (!string.IsNullOrEmpty(loc.File))
            {
                sb.Append(' ').Append(loc.File);
                if (loc.Line is > 0)
                {
                    sb.Append(':').Append(loc.Line.Value.ToString(CultureInfo.InvariantCulture));
                    if (loc.Column is > 0)
                        sb.Append(':').Append(loc.Column.Value.ToString(CultureInfo.InvariantCulture));
                }
                sb.Append(':');
            }
            else if (!string.IsNullOrEmpty(loc.Context) || !string.IsNullOrEmpty(loc.Signal))
            {
                sb.Append(' ');
                if (!string.IsNullOrEmpty(loc.Context)) sb.Append(loc.Context);
                if (!string.IsNullOrEmpty(loc.Signal))
                {
                    if (!string.IsNullOrEmpty(loc.Context)) sb.Append(' ');
                    sb.Append('\'').Append(loc.Signal).Append('\'');
                }
                sb.Append(':');
            }
        }
        sb.Append(' ').Append(d.Message);
        if (d.GuidelineTags is { Count: > 0 })
            sb.Append(" [").Append(string.Join(", ", d.GuidelineTags)).Append(']');
        sb.AppendLine();
    }

    public string ToJson()
    {
        var q = EffectiveQuality;
        var contract = new LintReportContract(
            Schema,
            Status,
            Profile,
            Wall,
            WarningsAsErrors,
            ErrorCount,
            WarningCount,
            InfoCount,
            WaivedCount,
            BaselinedCount,
            new LintQualityContract(q.Score, q.Grade, q.DesignElements),
            StoppedAtPhase,
            Diagnostics.Select(d => new LintDiagnosticContract(
                d.Code,
                d.Severity.ToString().ToLowerInvariant(),
                d.Message,
                d.Source,
                d.Location is null
                    ? null
                    : new LintLocationContract(d.Location.File, d.Location.Line, d.Location.Column, d.Location.Signal, d.Location.Context),
                LintBaseline.ComputeFingerprint(d),
                d.Phase,
                d.GuidelineTags is { Count: > 0 } ? d.GuidelineTags.ToArray() : null)).ToArray(),
            Fsms is { Count: > 0 } ? ToFsmContracts(Fsms) : null,
            Cdc is { Crossings.Count: > 0 } ? ToCdcContracts(Cdc.Crossings) : null);
        return JsonSerializer.Serialize(contract, LintJsonContext.Default.LintReportContract);
    }

    public string FormatFsmDumpJson()
    {
        var dump = new FsmDumpContract(FsmDumpSchema, Fsms is null ? [] : ToFsmContracts(Fsms));
        return JsonSerializer.Serialize(dump, LintJsonContext.Default.FsmDumpContract);
    }

    public string FormatCdcDumpJson()
    {
        var c = Cdc ?? CdcAnalysisResult.Empty;
        var dump = new CdcDumpContract(
            CdcDumpSchema,
            c.ClockDomains.Select(d => new ClockDomainContract(d.ClockSignal, d.IsPrimaryInput, d.IsConstrainedInSdc, d.Signals.ToArray())).ToArray(),
            c.ResetSignals.ToArray(),
            ToCdcContracts(c.Crossings));
        return JsonSerializer.Serialize(dump, LintJsonContext.Default.CdcDumpContract);
    }

    private static FsmSummaryContract[] ToFsmContracts(IReadOnlyList<FsmInfo> fsms) =>
        fsms.Select(f => new FsmSummaryContract(
            f.StateSignal,
            f.NextStateSignal,
            f.Width,
            f.ResetState,
            f.HasDefaultRecovery,
            f.States.ToArray(),
            f.Transitions.Select(t => new FsmTransitionContract(t.FromState, t.ToState)).ToArray(),
            f.UnreachableStates.ToArray(),
            f.DeadlockStates.ToArray(),
            f.Context,
            f.ToMermaid())).ToArray();

    private static CdcCrossingContract[] ToCdcContracts(IReadOnlyList<CdcCrossingInfo> crossings) =>
        crossings.Select(x => new CdcCrossingContract(
            x.Kind, x.SourceSignal, x.SourceDomain, x.DestinationSignal, x.DestinationDomain,
            x.Width, x.Synchronized, x.SynchronizerStages, x.FalsePath, x.Context)).ToArray();

    /// <summary>
    /// Serializes this report as an OASIS SARIF 2.1.0 log suitable for GitHub Code Scanning,
    /// VS Code SARIF Viewer, and Azure DevOps without runtime reflection (NativeAOT-safe).
    /// </summary>
    public string ToSarif(IEnumerable<IRtlLintRule>? customRules = null)
    {
        var customMap = customRules?.ToDictionary(r => r.Info.Code, r => r.Info, StringComparer.OrdinalIgnoreCase);
        var ruleIds = Diagnostics.Select(d => d.Code).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var sarifRules = new List<SarifRuleContract>(ruleIds.Length);
        foreach (string id in ruleIds)
        {
            LintRuleInfo? info = null;
            if (LintRuleCatalog.TryGetRule(id, out var builtIn)) info = builtIn;
            else if (customMap is not null && customMap.TryGetValue(id, out var custom)) info = custom;

            string shortDesc = info?.Summary ?? $"VeloSim lint rule {id}.";
            SarifMessageContract? help = !string.IsNullOrWhiteSpace(info?.Hint) ? new SarifMessageContract(info.Hint) : null;
            string defLevel = ToSarifLevel(info?.DefaultSeverity ?? LintSeverity.Warning);

            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (info?.Standards is { Count: > 0 })
                foreach (string s in info.Standards) tags.Add(s);
            foreach (var d in Diagnostics.Where(x => x.Code.Equals(id, StringComparison.OrdinalIgnoreCase)))
                if (d.GuidelineTags is { Count: > 0 })
                    foreach (string gt in d.GuidelineTags) tags.Add(gt);

            var props = new SarifRulePropertiesContract(
                tags.Count > 0 ? tags.Order(StringComparer.OrdinalIgnoreCase).ToArray() : null,
                info is not null ? LintPhases.ToName(info.Phase) : null);

            sarifRules.Add(new SarifRuleContract(id, new SarifMessageContract(shortDesc), help, new SarifRuleDefaultConfigContract(defLevel), props));
        }

        var sarifResults = new List<SarifResultContract>(Diagnostics.Count);
        foreach (var d in Diagnostics)
        {
            SarifLocationContract[]? locations = null;
            if (d.Location is { } loc)
            {
                SarifPhysicalLocationContract? phys = null;
                if (!string.IsNullOrEmpty(loc.File))
                {
                    var region = loc.Line is > 0 ? new SarifRegionContract(loc.Line.Value, loc.Column is > 0 ? loc.Column : null) : null;
                    phys = new SarifPhysicalLocationContract(new SarifArtifactLocationContract(loc.File.Replace('\\', '/')), region);
                }
                SarifLogicalLocationContract[]? logical = null;
                if (!string.IsNullOrEmpty(loc.Signal) || !string.IsNullOrEmpty(loc.Context))
                {
                    string name = !string.IsNullOrEmpty(loc.Signal) ? loc.Signal : loc.Context!;
                    string? fqn = !string.IsNullOrEmpty(loc.Context) && !string.IsNullOrEmpty(loc.Signal) ? $"{loc.Context}.{loc.Signal}" : name;
                    logical = [new SarifLogicalLocationContract(name, !string.IsNullOrEmpty(loc.Signal) ? "signal" : "scope", fqn)];
                }
                if (phys is not null || logical is not null)
                    locations = [new SarifLocationContract(phys, logical)];
            }

            var fingerprints = new Dictionary<string, string>
            {
                ["velosim/v1"] = LintBaseline.ComputeFingerprint(d)
            };

            sarifResults.Add(new SarifResultContract(
                d.Code,
                ToSarifLevel(d.Severity),
                new SarifMessageContract(d.Message),
                locations,
                fingerprints));
        }

        var log = new SarifLogContract(
            SarifSchemaUri,
            SarifVersion,
            [
                new SarifRunContract(
                    new SarifToolContract(new SarifDriverContract(
                        "VeloSim",
                        "1.0.0",
                        "https://github.com/jeffadickson/VeloSim",
                        sarifRules.ToArray())),
                    sarifResults.ToArray())
            ]);

        return JsonSerializer.Serialize(log, SarifJsonContext.Default.SarifLogContract);
    }

    private static string ToSarifLevel(LintSeverity severity) => severity switch
    {
        LintSeverity.Error => "error",
        LintSeverity.Warning => "warning",
        _ => "note"
    };
}

/// <summary>
/// Pure C# flagship static linter for <c>velosim.rtl.v1</c> IR designs (zero external binary dependencies).
/// Supports Verilator-compatible <c>-Wall</c>/<c>-Werror</c>/<c>-Wno-*</c>, Aldec ALINT-PRO Phase-Based Linting,
/// DO-254/STARC/ISO-26262/lowRISC compliance profiles, FSM extraction & verification, structural CDC/RDC/DFT
/// with SDC constraints & SVA generation, RTL Quality Score, scoped waivers, baseline ratcheting,
/// Tier 2 <see cref="IRtlLintRule"/> plugins (ready for Tier 3 Cameo DSL), and SARIF 2.1.0 output.
/// </summary>
public static class RtlLinter
{
    public const string SchemaVersion = "velosim.lint.v1";

    private sealed record SigInfo(string Name, int Width, string Direction, bool Signed, bool TwoState, string? SourcePath);
    private sealed record MemInfo(string Name, int Width, int Depth, bool Signed, int LowerBound, bool TwoState, bool Container);

    public static LintReport LintDesign(RtlDesign design, LintOptions? options = null, string? sourceFile = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        return LintJson(design.Json, options, sourceFile);
    }

    public static LintReport LintJson(string json, LintOptions? options = null, string? sourceFile = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        var opts = options ?? LintOptions.Default;
        var limits = opts.LargeDesign ? RtlDesignLimits.Large : RtlDesignLimits.Bounded;
        var raw = new List<LintDiagnostic>();

        if (json.Length > limits.MaxIrBytes)
        {
            raw.Add(new("VALIDERROR", LintSeverity.Error, $"RTL IR exceeds size limit ({limits.MaxIrBytes} bytes).", "ir", new LintLocation(File: sourceFile)));
            return Finalize(raw, opts);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            raw.Add(new("VALIDERROR", LintSeverity.Error, "Malformed JSON: " + ex.Message, "ir", new LintLocation(File: sourceFile)));
            return Finalize(raw, opts);
        }

        IReadOnlyList<FsmInfo>? fsms = null;
        CdcAnalysisResult? cdc = null;
        int designElements = 0;

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("format", out var fmtEl) ||
                fmtEl.ValueKind != JsonValueKind.String ||
                fmtEl.GetString() != "velosim.rtl.v1")
            {
                raw.Add(new("VALIDERROR", LintSeverity.Error, "Unsupported or missing RTL IR format (expected 'velosim.rtl.v1').", "ir", new LintLocation(File: sourceFile)));
                return Finalize(raw, opts);
            }

            var ctx = new IrLintContext(root, sourceFile, opts.EffectiveParameters, raw);
            ctx.Run();
            designElements = ctx.DesignElementCount;

            // Phase 2: Automatic FSM Extraction & Verification
            var phases = opts.EffectivePhases;
            if (phases is null || phases.Contains(LintPhase.Fsm))
            {
                var (extractedFsms, fsmDiags) = RtlFsmAnalyzer.Analyze(root, ctx.SignalWidths, sourceFile);
                fsms = extractedFsms;
                raw.AddRange(fsmDiags);
            }

            // Phase 3: Clock/Reset Tree Auto-Detection, CDC/RDC, DFT & SDC Audit
            if (phases is null || phases.Contains(LintPhase.ClockResetCdc))
            {
                var sdc = opts.Sdc;
                bool hasExplicitSdc = opts.HasExplicitSdc || sdc is not null;
                if (sdc is null && !string.IsNullOrWhiteSpace(opts.Config?.SdcFile))
                {
                    sdc = SdcConstraints.LoadFile(opts.Config.SdcFile);
                    hasExplicitSdc = true;
                }
                cdc = RtlCdcAnalyzer.Analyze(root, ctx.SignalMeta, sdc, hasExplicitSdc, sourceFile);
                raw.AddRange(cdc.Diagnostics);
            }

            // Execute Tier 2 C# / Tier 3 Cameo custom IR rules against the populated read-only context
            if (opts.CustomRules is { Count: > 0 })
            {
                var lintCtx = ctx.BuildExtensibilityContext();
                foreach (var rule in opts.CustomRules)
                    rule.Analyze(lintCtx);
            }

            // Run canonical RtlDesign.Parse as a final fail-closed backstop to catch any
            // contract violation not already explained by a more specific structural diagnostic,
            // and to pull in conservative timed race diagnostics (VS-RACE-WW / VS-RACE-RW).
            try
            {
                var parsed = RtlDesign.Parse(json, limits);
                foreach (string td in parsed.TimingDiagnostics)
                {
                    if (td.StartsWith("VS-RACE-WW:", StringComparison.Ordinal))
                        raw.Add(new("RACEWW", LintSeverity.Warning, td["VS-RACE-WW:".Length..].Trim(), "ir", new LintLocation(File: sourceFile, Context: "timed")));
                    else if (td.StartsWith("VS-RACE-RW:", StringComparison.Ordinal))
                        raw.Add(new("RACERW", LintSeverity.Warning, td["VS-RACE-RW:".Length..].Trim(), "ir", new LintLocation(File: sourceFile, Context: "timed")));
                    else
                        raw.Add(new("RACEWW", LintSeverity.Warning, td, "ir", new LintLocation(File: sourceFile, Context: "timed")));
                }
            }
            catch (IrValidationException ex)
            {
                if (!ctx.HasStructuralEquivalent(ex.Message))
                    raw.Add(new("VALIDERROR", LintSeverity.Error, ex.Message, "ir", new LintLocation(File: sourceFile)));
            }
        }

        return Finalize(raw, opts, designElements, fsms, cdc);
    }

    public static LintReport Finalize(
        IEnumerable<LintDiagnostic> rawDiagnostics,
        LintOptions options,
        int designElements = 0,
        IReadOnlyList<FsmInfo>? fsms = null,
        CdcAnalysisResult? cdc = null)
    {
        ArgumentNullException.ThrowIfNull(rawDiagnostics);
        ArgumentNullException.ThrowIfNull(options);

        var profile = options.EffectiveProfile;
        bool wall = options.EffectiveWall;
        bool effectiveWerror = options.EffectiveWarningsAsErrors;
        var activePhases = options.EffectivePhases;
        bool stopOnPhaseError = options.EffectiveStopOnPhaseError;

        var enabled = LintRuleCatalog.ExpandSet(options.EnabledRules);
        var disabled = LintRuleCatalog.ExpandSet(options.DisabledRules);
        var promoted = LintRuleCatalog.ExpandSet(options.ErrorRules);
        var configRules = options.Config?.Rules;
        var configAnnotations = options.Config?.Annotations;
        var customRuleMap = options.CustomRules?.ToDictionary(r => r.Info.Code, r => r.Info, StringComparer.OrdinalIgnoreCase);

        var waivers = options.Config?.Waivers;
        int[]? waiverHits = waivers is { Count: > 0 } ? new int[waivers.Count] : null;
        int waivedCount = 0;
        int baselinedCount = 0;

        var filtered = new List<LintDiagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        LintPhase ResolvePhase(string code, LintRuleInfo? info)
        {
            if (info is not null) return info.Phase;
            return code.StartsWith("FSM", StringComparison.OrdinalIgnoreCase) ? LintPhase.Fsm
                : code.StartsWith("CDC", StringComparison.OrdinalIgnoreCase) || code.StartsWith("RDC", StringComparison.OrdinalIgnoreCase) || code.StartsWith("DFT", StringComparison.OrdinalIgnoreCase) || code.StartsWith("SDC", StringComparison.OrdinalIgnoreCase) ? LintPhase.ClockResetCdc
                : LintPhase.RtlSynthesis;
        }

        IReadOnlyList<string>? ResolveGuidelineTags(string code, LintRuleInfo? info)
        {
            List<string>? tags = null;
            if (configAnnotations is not null && configAnnotations.TryGetValue(code, out string? customTag))
            {
                tags ??= [];
                tags.Add(customTag);
            }
            if (info?.Standards is { Count: > 0 })
            {
                string? profileStdPrefix = profile switch
                {
                    LintProfile.Do254 => "DO-254",
                    LintProfile.Starc => "STARC",
                    LintProfile.Iso26262 => "ISO-26262",
                    LintProfile.LowRisc => "LOWRISC",
                    _ => null
                };
                if (profileStdPrefix is not null)
                {
                    foreach (string s in info.Standards)
                    {
                        if (s.StartsWith(profileStdPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            tags ??= [];
                            if (!tags.Contains(s, StringComparer.OrdinalIgnoreCase))
                                tags.Add(s);
                        }
                    }
                }
            }
            return tags;
        }

        void ProcessOne(LintDiagnostic d, bool allowWaiver)
        {
            string code = d.Code.ToUpperInvariant();
            bool hasRuleInfo = LintRuleCatalog.TryGetRule(code, out var ruleInfo) ||
                               (customRuleMap is not null && customRuleMap.TryGetValue(code, out ruleInfo));

            var phase = ResolvePhase(code, ruleInfo);
            if (activePhases is not null && !activePhases.Contains(phase))
                return;

            // Precedence: CLI (-Wno-*, -Werror-*, -W*) > Config.Rules > Profile > Rule default
            bool cliDisabled = disabled.Contains(code) || disabled.Contains(d.Code);
            bool cliEnabled = enabled.Contains(code) || enabled.Contains(d.Code);
            bool cliPromoted = promoted.Contains(code) || promoted.Contains(d.Code);

            if (cliDisabled && (d.Severity != LintSeverity.Error || hasRuleInfo))
                return;

            LintSeverity severity = d.Severity;
            bool explicitlyConfigured = cliEnabled || cliPromoted;

            if (!explicitlyConfigured && configRules is not null &&
                (configRules.TryGetValue(code, out var cfgAction) || configRules.TryGetValue(d.Code, out cfgAction)))
            {
                if (cfgAction == LintRuleAction.Off) return;
                severity = cfgAction switch
                {
                    LintRuleAction.Info => LintSeverity.Info,
                    LintRuleAction.Error => LintSeverity.Error,
                    _ => LintSeverity.Warning
                };
                explicitlyConfigured = true;
            }

            if (hasRuleInfo && severity == LintSeverity.Warning && !explicitlyConfigured)
            {
                bool active = wall || ruleInfo!.DefaultEnabled || LintProfiles.PromotesToError(profile, code);
                if (!active) return;
            }

            if (severity == LintSeverity.Warning && !options.NoFatal)
            {
                if (cliPromoted || effectiveWerror || (!explicitlyConfigured && LintProfiles.PromotesToError(profile, code)))
                    severity = LintSeverity.Error;
            }

            var tags = d.GuidelineTags ?? ResolveGuidelineTags(code, ruleInfo);
            var normalized = d with
            {
                Severity = severity,
                Phase = d.Phase ?? LintPhases.ToName(phase),
                GuidelineTags = tags
            };

            // Apply scoped waivers before deduplicating into active findings
            if (allowWaiver && waivers is { Count: > 0 } && waiverHits is not null)
            {
                for (int i = 0; i < waivers.Count; i++)
                {
                    if (waivers[i].Matches(normalized))
                    {
                        waiverHits[i]++;
                        waivedCount++;
                        return;
                    }
                }
            }

            // Apply baseline ratchet (suppresses already-baselined findings)
            if (allowWaiver && options.Baseline is not null && options.Baseline.Contains(normalized))
            {
                baselinedCount++;
                return;
            }

            string dedupKey = $"{normalized.Source}|{normalized.Code}|{normalized.Severity}|{normalized.Location?.File}|{normalized.Location?.Line}|{normalized.Location?.Column}|{normalized.Location?.Signal}|{normalized.Location?.Context}|{normalized.Message}";
            if (seen.Add(dedupKey))
                filtered.Add(normalized);
        }

        foreach (var d in rawDiagnostics)
            ProcessOne(d, allowWaiver: true);

        // Emit unused / expired waiver diagnostics if configured
        if (waivers is { Count: > 0 } && waiverHits is not null)
        {
            var today = options.AsOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
            for (int i = 0; i < waivers.Count; i++)
            {
                var w = waivers[i];
                if (w.IsExpired(today))
                {
                    ProcessOne(new LintDiagnostic(
                        "EXPIREDWAIVER",
                        LintSeverity.Warning,
                        $"Waiver '{w.DisplayId}' for rule '{w.Code}' expired on {w.Expires}.",
                        "config",
                        new LintLocation(File: w.File, Signal: w.Signal, Context: "waiver")),
                        allowWaiver: false);
                }
                if (options.EffectiveWarnUnusedWaivers && waiverHits[i] == 0)
                {
                    ProcessOne(new LintDiagnostic(
                        "UNUSEDWAIVER",
                        LintSeverity.Warning,
                        $"Waiver '{w.DisplayId}' for rule '{w.Code}' matched 0 diagnostics.",
                        "config",
                        new LintLocation(File: w.File, Signal: w.Signal, Context: "waiver")),
                        allowWaiver: false);
                }
            }
        }

        // Phase-Based Linting (PBL): stop after the first phase that reports an error
        string? stoppedAtPhase = null;
        if (stopOnPhaseError && filtered.Count > 0)
        {
            var kept = new List<LintDiagnostic>();
            foreach (var p in LintPhases.Ordered)
            {
                string pName = LintPhases.ToName(p);
                var phaseItems = filtered.Where(d => string.Equals(d.Phase ?? "rtl", pName, StringComparison.OrdinalIgnoreCase)).ToList();
                kept.AddRange(phaseItems);
                if (phaseItems.Any(d => d.Severity == LintSeverity.Error))
                {
                    stoppedAtPhase = pName;
                    break;
                }
            }
            filtered = kept;
        }

        int errors = filtered.Count(d => d.Severity == LintSeverity.Error);
        int warnings = filtered.Count(d => d.Severity == LintSeverity.Warning);
        int infos = filtered.Count(d => d.Severity == LintSeverity.Info);
        string status = errors == 0 ? "pass" : "fail";
        var quality = LintQualityMetrics.Compute(errors, warnings, waivedCount, designElements);

        return new LintReport(
            SchemaVersion,
            status,
            wall,
            effectiveWerror,
            errors,
            warnings,
            infos,
            filtered.AsReadOnly(),
            LintProfiles.ToName(profile),
            waivedCount,
            baselinedCount,
            quality,
            stoppedAtPhase,
            fsms,
            cdc);
    }

    private sealed class IrLintContext
    {
        private const int MaxDepth = 128;

        private readonly JsonElement root;
        private readonly string? sourceFile;
        private readonly LintRuleParameters parameters;
        private readonly List<LintDiagnostic> diagnostics;

        private readonly Dictionary<string, SigInfo> signals = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MemInfo> memories = new(StringComparer.Ordinal);
        private readonly List<SigInfo> signalList = [];
        private readonly List<MemInfo> memoryList = [];

        private readonly HashSet<string> allReads = new(StringComparer.Ordinal);
        private readonly HashSet<string> allWrites = new(StringComparer.Ordinal);
        private readonly HashSet<string> blockingWriters = new(StringComparer.Ordinal);
        private readonly HashSet<string> nbaWriters = new(StringComparer.Ordinal);
        private readonly HashSet<string> clockAndEventSignals = new(StringComparer.Ordinal);
        private readonly HashSet<string> asyncEventSignals = new(StringComparer.Ordinal);
        private readonly HashSet<string> nonAsyncReads = new(StringComparer.Ordinal);

        // Fact tables for Tier 2 IRtlLintRule / Tier 3 Cameo DSL extensibility
        private readonly List<RtlLintAssignment> collectedAssignments = [];
        private readonly List<RtlLintStatement> collectedStatements = [];
        private readonly List<RtlLintExpression> collectedExpressions = [];

        private string? clock;
        private bool isTimed;
        private bool sawLatch;
        private bool sawCombLoop;
        private bool sawMultiDriven;
        private bool sawBlkAndNblk;
        private bool sawClkComb;
        private bool sawUndriven;
        private bool sawAssignIn;

        public IrLintContext(JsonElement root, string? sourceFile, LintRuleParameters parameters, List<LintDiagnostic> diagnostics)
        {
            this.root = root;
            this.sourceFile = sourceFile;
            this.parameters = parameters;
            this.diagnostics = diagnostics;
        }

        public int DesignElementCount => signalList.Count + memoryList.Count + collectedStatements.Count;
        public IReadOnlyDictionary<string, int> SignalWidths => signals.ToDictionary(kv => kv.Key, kv => kv.Value.Width, StringComparer.Ordinal);
        public IReadOnlyDictionary<string, (int Width, string Direction)> SignalMeta =>
            signals.ToDictionary(kv => kv.Key, kv => (kv.Value.Width, kv.Value.Direction), StringComparer.Ordinal);

        public bool HasStructuralEquivalent(string validationMessage)
        {
            if (sawLatch && (validationMessage.Contains("every path", StringComparison.OrdinalIgnoreCase) || validationMessage.Contains("definite assignment", StringComparison.OrdinalIgnoreCase)))
                return true;
            if (sawCombLoop && validationMessage.Contains("feedback", StringComparison.OrdinalIgnoreCase))
                return true;
            if (sawMultiDriven && (validationMessage.Contains("Conflicting", StringComparison.OrdinalIgnoreCase) || validationMessage.Contains("Multiple processes cannot write", StringComparison.OrdinalIgnoreCase)))
                return true;
            if (sawBlkAndNblk && validationMessage.Contains("mix blocking and nonblocking", StringComparison.OrdinalIgnoreCase))
                return true;
            if (sawClkComb && validationMessage.Contains("Clock-dependent", StringComparison.OrdinalIgnoreCase))
                return true;
            if (sawUndriven && validationMessage.Contains("Undriven", StringComparison.OrdinalIgnoreCase))
                return true;
            if (sawAssignIn && (validationMessage.Contains("writable declared word", StringComparison.OrdinalIgnoreCase) || validationMessage.Contains("Invalid or conflicting continuous writer", StringComparison.OrdinalIgnoreCase)))
                return true;
            return false;
        }

        public RtlLintContext BuildExtensibilityContext()
        {
            var sigViews = signalList.Select(s => new RtlLintSignal(
                s.Name, s.Width, s.Direction, s.Signed, s.TwoState, s.SourcePath,
                allReads.Contains(s.Name), allWrites.Contains(s.Name), clockAndEventSignals.Contains(s.Name))).ToArray();
            var memViews = memoryList.Select(m => new RtlLintMemory(
                m.Name, m.Width, m.Depth, m.Signed, m.LowerBound, m.TwoState, m.Container,
                allReads.Contains(m.Name), allWrites.Contains(m.Name))).ToArray();

            return new RtlLintContext(
                sourceFile,
                clock,
                isTimed,
                root,
                sigViews,
                memViews,
                collectedAssignments.AsReadOnly(),
                collectedStatements.AsReadOnly(),
                collectedExpressions.AsReadOnly(),
                parameters,
                d => diagnostics.Add(d));
        }

        public void Run()
        {
            ParseDeclarations();
            if (root.TryGetProperty("clock", out var clkEl) && clkEl.ValueKind == JsonValueKind.String)
            {
                clock = clkEl.GetString();
                if (!string.IsNullOrEmpty(clock))
                    clockAndEventSignals.Add(clock);
            }

            isTimed = root.TryGetProperty("timed", out var timedEl) && timedEl.ValueKind == JsonValueKind.Array;

            // 1. Sequential body (cycle-based designs)
            var seqWriters = new HashSet<string>(StringComparer.Ordinal);
            if (!isTimed && root.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.Object)
            {
                CollectEventSignals(bodyEl);
                AnalyzeSequentialBody(bodyEl, seqWriters);
            }

            // 2. Continuous assignments & combinational blocks
            var combSteps = new List<(string Label, HashSet<string> Reads, HashSet<string> Writes)>();
            var combOwners = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("continuous", out var contEl) && contEl.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var assign in contEl.EnumerateArray())
                {
                    string label = $"continuous[{idx++}]";
                    if (assign.ValueKind != JsonValueKind.Object ||
                        !assign.TryGetProperty("target", out var target) ||
                        !assign.TryGetProperty("value", out var value))
                        continue;

                    var stepReads = new HashSet<string>(StringComparer.Ordinal);
                    var stepWrites = new HashSet<string>(StringComparer.Ordinal);
                    AnalyzeExpression(value, label, stepReads, 0);
                    AnalyzeTarget(target, label, stepReads, stepWrites, 0);
                    AnalyzeAssignmentChecks(target, value, isBlocking: true, inClockedProcess: false, label);

                    allReads.UnionWith(stepReads);
                    nonAsyncReads.UnionWith(stepReads);
                    allWrites.UnionWith(stepWrites);
                    RecordCombStep(label, stepReads, stepWrites, seqWriters, combOwners, combSteps);
                }
            }

            if (root.TryGetProperty("combinational", out var combEl) && combEl.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var block in combEl.EnumerateArray())
                {
                    string label = $"combinational[{idx++}]";
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    var stepReads = new HashSet<string>(StringComparer.Ordinal);
                    var stepWrites = new HashSet<string>(StringComparer.Ordinal);
                    AnalyzeStatement(block, inClockedProcess: false, isTimedContext: false, label, stepReads, stepWrites, 0);

                    if (!isTimed && stepWrites.Count > 0)
                    {
                        var assigned = new HashSet<string>(StringComparer.Ordinal);
                        CheckCombinationalLatch(block, stepWrites, assigned, label, 0);
                        foreach (string missing in stepWrites.Where(w => !assigned.Contains(w)).Order(StringComparer.Ordinal))
                        {
                            sawLatch = true;
                            Emit("LATCH", LintSeverity.Error, $"Signal '{missing}' is not assigned on all paths in combinational block (inferred latch).", missing, label);
                        }
                    }

                    allReads.UnionWith(stepReads);
                    nonAsyncReads.UnionWith(stepReads);
                    allWrites.UnionWith(stepWrites);
                    RecordCombStep(label, stepReads, stepWrites, seqWriters, combOwners, combSteps);
                }
            }

            if (!isTimed && combSteps.Count > 0)
                CheckCombinationalCycles(combSteps);

            // 3. Initialization block
            if (root.TryGetProperty("initialization", out var initEl) && initEl.ValueKind == JsonValueKind.Object)
            {
                var initReads = new HashSet<string>(StringComparer.Ordinal);
                var initWrites = new HashSet<string>(StringComparer.Ordinal);
                AnalyzeStatement(initEl, inClockedProcess: false, isTimedContext: false, "initialization", initReads, initWrites, 0);
                allReads.UnionWith(initReads);
                allWrites.UnionWith(initWrites);
            }

            // 4. Timed processes
            if (isTimed)
            {
                int idx = 0;
                foreach (var proc in timedEl.EnumerateArray())
                {
                    string label = $"timed[{idx++}]";
                    if (proc.ValueKind != JsonValueKind.Object) continue;
                    var tReads = new HashSet<string>(StringComparer.Ordinal);
                    var tWrites = new HashSet<string>(StringComparer.Ordinal);
                    AnalyzeStatement(proc, inClockedProcess: false, isTimedContext: true, label, tReads, tWrites, 0);
                    allReads.UnionWith(tReads);
                    allWrites.UnionWith(tWrites);
                }
            }

            // 5. Verification artifacts (properties & covergroups) count as reads
            AnalyzeVerificationReads();

            // 6. Blocking + NBA mix check (BLKANDNBLK)
            foreach (string sig in blockingWriters.Intersect(nbaWriters, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                sawBlkAndNblk = true;
                Emit("BLKANDNBLK", LintSeverity.Error, $"Signal '{sig}' is assigned with both blocking (=) and non-blocking (<=) assignments.", sig, "design");
            }

            // 7. SYNCASYNCNET: async edge trigger (non-clock) also used in synchronous/combinational logic
            foreach (string sig in asyncEventSignals.Intersect(nonAsyncReads, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                if (sig == clock) continue;
                Emit("SYNCASYNCNET", LintSeverity.Warning, $"Signal '{sig}' is used both as an asynchronous edge trigger and in synchronous/combinational logic.", sig, "design");
            }

            // 8. Undriven non-input signals (UNDRIVEN)
            foreach (var sig in signalList)
            {
                if (sig.Direction != "input" && !allWrites.Contains(sig.Name))
                {
                    sawUndriven = true;
                    Emit("UNDRIVEN", LintSeverity.Warning, $"Signal '{sig.Name}' ({sig.Direction}, {sig.Width}-bit) is never driven.", sig.Name, "declaration");
                }
            }

            // 9. Unused inputs/internals & unused memories (UNUSEDSIGNAL / UNUSEDMEM)
            foreach (var sig in signalList)
            {
                if (sig.Direction == "output") continue;
                if (clockAndEventSignals.Contains(sig.Name)) continue;
                if (IsIgnoredUnused(sig.Name)) continue;
                if (!allReads.Contains(sig.Name))
                {
                    string desc = sig.Direction == "input"
                        ? $"Input signal '{sig.Name}' ({sig.Width}-bit) is never read."
                        : allWrites.Contains(sig.Name)
                            ? $"Internal signal '{sig.Name}' ({sig.Width}-bit) is written but never read."
                            : $"Internal signal '{sig.Name}' ({sig.Width}-bit) is never used.";
                    Emit("UNUSEDSIGNAL", LintSeverity.Warning, desc, sig.Name, "declaration");
                }
            }

            foreach (var mem in memoryList)
            {
                if (IsIgnoredUnused(mem.Name)) continue;
                if (!allReads.Contains(mem.Name))
                    Emit("UNUSEDMEM", LintSeverity.Warning, $"Memory '{mem.Name}' ({mem.Width}-bit x {mem.Depth}) is never read.", mem.Name, "declaration");
            }

            // 10. Parameterized NAMING & WIDTHBUDGET rules
            CheckNamingConventions();
            CheckWidthBudgets();
        }

        private bool IsIgnoredUnused(string name)
        {
            if (parameters.UnusedIgnorePatterns is not { Count: > 0 } pats) return false;
            foreach (string pat in pats)
                if (!string.IsNullOrWhiteSpace(pat) && LintGlob.IsMatch(pat, name, ignoreCase: false))
                    return true;
            return false;
        }

        private void CheckNamingConventions()
        {
            var naming = parameters.Naming;
            if (naming is null || !naming.HasAnyPattern) return;

            static bool FailsRegex(string? pattern, string value)
            {
                if (string.IsNullOrWhiteSpace(pattern)) return false;
                return !Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            }

            foreach (var sig in signalList)
            {
                if (sig.Name == clock && FailsRegex(naming.ClockPattern, sig.Name))
                    Emit("NAMING", LintSeverity.Warning, $"Clock signal '{sig.Name}' does not match configured clock_pattern '{naming.ClockPattern}'.", sig.Name, "declaration");

                if (asyncEventSignals.Contains(sig.Name) && sig.Name != clock && FailsRegex(naming.ResetPattern, sig.Name))
                    Emit("NAMING", LintSeverity.Warning, $"Reset/event signal '{sig.Name}' does not match configured reset_pattern '{naming.ResetPattern}'.", sig.Name, "declaration");

                if (FailsRegex(naming.SignalPattern, sig.Name))
                    Emit("NAMING", LintSeverity.Warning, $"Signal '{sig.Name}' does not match configured signal_pattern '{naming.SignalPattern}'.", sig.Name, "declaration");

                string? dirPattern = sig.Direction switch
                {
                    "input" => naming.InputPattern,
                    "output" => naming.OutputPattern,
                    "internal" => naming.InternalPattern,
                    _ => null
                };
                if (FailsRegex(dirPattern, sig.Name))
                    Emit("NAMING", LintSeverity.Warning, $"{sig.Direction} signal '{sig.Name}' does not match configured {sig.Direction}_pattern '{dirPattern}'.", sig.Name, "declaration");
            }

            foreach (var mem in memoryList)
            {
                if (FailsRegex(naming.MemoryPattern, mem.Name))
                    Emit("NAMING", LintSeverity.Warning, $"Memory '{mem.Name}' does not match configured memory_pattern '{naming.MemoryPattern}'.", mem.Name, "declaration");
            }
        }

        private void CheckWidthBudgets()
        {
            if (parameters.MaxSignalWidth is > 0 and int maxW)
            {
                foreach (var sig in signalList)
                    if (sig.Width > maxW)
                        Emit("WIDTHBUDGET", LintSeverity.Warning, $"Signal '{sig.Name}' width ({sig.Width} bits) exceeds configured max_signal_width ({maxW}).", sig.Name, "declaration");
            }
            if (parameters.MaxMemoryDepth is > 0 and int maxD)
            {
                foreach (var mem in memoryList)
                    if (!mem.Container && mem.Depth > maxD)
                        Emit("WIDTHBUDGET", LintSeverity.Warning, $"Memory '{mem.Name}' depth ({mem.Depth}) exceeds configured max_memory_depth ({maxD}).", mem.Name, "declaration");
            }
        }

        private void ParseDeclarations()
        {
            if (root.TryGetProperty("signals", out var sigsEl) && sigsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in sigsEl.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;
                    string? name = TryString(s, "name");
                    int? width = TryInt(s, "width");
                    string? dir = TryString(s, "direction");
                    if (string.IsNullOrEmpty(name) || width is not > 0 || string.IsNullOrEmpty(dir)) continue;
                    bool isSigned = TryBool(s, "signed");
                    bool twoState = TryBool(s, "two_state");
                    string? path = TryString(s, "path");
                    var info = new SigInfo(name, width.Value, dir, isSigned, twoState, path);
                    if (signals.TryAdd(name, info))
                        signalList.Add(info);
                }
            }

            if (root.TryGetProperty("memories", out var memsEl) && memsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in memsEl.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object) continue;
                    string? name = TryString(m, "name");
                    int? width = TryInt(m, "width");
                    int? depth = TryInt(m, "depth");
                    if (string.IsNullOrEmpty(name) || width is not > 0 || depth is not > 0) continue;
                    bool isSigned = TryBool(m, "signed");
                    int lower = TryInt(m, "lower") ?? 0;
                    bool twoState = TryBool(m, "two_state");
                    bool container = TryBool(m, "dynamic") || TryBool(m, "queue") || TryBool(m, "associative");
                    var info = new MemInfo(name, width.Value, depth.Value, isSigned, lower, twoState, container);
                    if (memories.TryAdd(name, info))
                        memoryList.Add(info);
                }
            }
        }

        private void CollectEventSignals(JsonElement bodyEl)
        {
            string? kind = TryString(bodyEl, "kind");
            if (kind == "process" && bodyEl.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Array)
            {
                var evList = new List<string>();
                foreach (var ev in evs.EnumerateArray())
                {
                    string? sig = TryString(ev, "signal");
                    if (!string.IsNullOrEmpty(sig))
                    {
                        clockAndEventSignals.Add(sig);
                        allReads.Add(sig);
                        evList.Add(sig);
                    }
                }
                // Secondary event triggers in a multi-event edge list (e.g. @(posedge clk or posedge rst)) are async triggers.
                if (evList.Count > 1)
                {
                    foreach (string s in evList)
                        if (s != clock) asyncEventSignals.Add(s);
                }
            }
            else if (kind == "parallel" && bodyEl.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                    CollectEventSignals(item);
            }
        }

        private void AnalyzeSequentialBody(JsonElement bodyEl, HashSet<string> seqWriters)
        {
            string? kind = TryString(bodyEl, "kind");
            bool clocked = clock is not null;

            if (kind == "parallel" && bodyEl.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                var ownerByTarget = new Dictionary<string, string>(StringComparer.Ordinal);
                int idx = 0;
                foreach (var proc in items.EnumerateArray())
                {
                    string label = $"process[{idx++}]";
                    var pReads = new HashSet<string>(StringComparer.Ordinal);
                    var pWrites = new HashSet<string>(StringComparer.Ordinal);
                    bool procClocked = clocked || TryString(proc, "kind") == "process";
                    var procEvents = ExtractProcessEventSignals(proc);
                    AnalyzeStatement(proc, procClocked, isTimedContext: false, label, pReads, pWrites, 0);
                    CheckSequentialResetConsistency(proc, label);
                    foreach (string r in pReads)
                        if (!procEvents.Contains(r)) nonAsyncReads.Add(r);
                    foreach (string w in pWrites)
                    {
                        if (ownerByTarget.TryGetValue(w, out string? prior))
                        {
                            sawMultiDriven = true;
                            Emit("MULTIDRIVEN", LintSeverity.Error, $"Target '{w}' is written by multiple sequential processes ({prior} and {label}).", w, label);
                        }
                        else
                        {
                            ownerByTarget[w] = label;
                        }
                    }
                    seqWriters.UnionWith(pWrites);
                    allReads.UnionWith(pReads);
                    allWrites.UnionWith(pWrites);
                }
            }
            else
            {
                var pReads = new HashSet<string>(StringComparer.Ordinal);
                var pWrites = new HashSet<string>(StringComparer.Ordinal);
                bool procClocked = clocked || kind == "process";
                var procEvents = ExtractProcessEventSignals(bodyEl);
                AnalyzeStatement(bodyEl, procClocked, isTimedContext: false, "body", pReads, pWrites, 0);
                CheckSequentialResetConsistency(bodyEl, "body");
                foreach (string r in pReads)
                    if (!procEvents.Contains(r)) nonAsyncReads.Add(r);
                seqWriters.UnionWith(pWrites);
                allReads.UnionWith(pReads);
                allWrites.UnionWith(pWrites);
            }
        }

        private static HashSet<string> ExtractProcessEventSignals(JsonElement proc)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (TryString(proc, "kind") == "process" && proc.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Array)
            {
                foreach (var ev in evs.EnumerateArray())
                    if (TryString(ev, "signal") is { Length: > 0 } s) set.Add(s);
            }
            return set;
        }

        /// <summary>
        /// RESETCONSIST: Detects partial reset in a clocked sequential process where a top-level
        /// <c>if (rst) ... else ...</c> branch resets some word registers to constants while leaving
        /// other word registers written in the <c>else</c> branch unreset.
        /// </summary>
        private void CheckSequentialResetConsistency(JsonElement proc, string context)
        {
            JsonElement inner = proc;
            if (TryString(inner, "kind") == "process" && inner.TryGetProperty("body", out var pb))
                inner = pb;
            // Unwrap single-item block
            while (TryString(inner, "kind") == "block" && inner.TryGetProperty("items", out var items) &&
                   items.ValueKind == JsonValueKind.Array && items.GetArrayLength() == 1)
            {
                inner = items[0];
            }

            if (TryString(inner, "kind") != "if" ||
                !inner.TryGetProperty("then", out var thenEl) ||
                !inner.TryGetProperty("else", out var elseEl))
                return;

            // Only treat as a reset branch when every assignment in 'then' assigns a constant to a signal ref
            var resetSignals = new HashSet<string>(StringComparer.Ordinal);
            bool allConstReset = CollectConstResetWrites(thenEl, resetSignals, 0);
            if (!allConstReset || resetSignals.Count == 0) return;

            var activeSignals = new HashSet<string>(StringComparer.Ordinal);
            CollectSignalRefWrites(elseEl, activeSignals, 0);
            foreach (string missing in activeSignals.Where(s => !resetSignals.Contains(s)).Order(StringComparer.Ordinal))
            {
                Emit("RESETCONSIST", LintSeverity.Warning, $"Sequential signal '{missing}' is updated in active branch but not reset in the reset branch of {context}.", missing, context);
            }
        }

        private static bool CollectConstResetWrites(JsonElement stmt, HashSet<string> resetSignals, int depth)
        {
            if (depth > MaxDepth || stmt.ValueKind != JsonValueKind.Object) return false;
            string? kind = TryString(stmt, "kind");
            if (kind == "block" && stmt.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                    if (!CollectConstResetWrites(item, resetSignals, depth + 1)) return false;
                return true;
            }
            if (kind is "nba" or "blocking")
            {
                if (!stmt.TryGetProperty("value", out var val) || TryString(val, "kind") != "const")
                    return false;
                if (stmt.TryGetProperty("target", out var tgt) && TryString(tgt, "kind") == "ref" && TryString(tgt, "name") is { } n)
                    resetSignals.Add(n);
                return true;
            }
            return false;
        }

        private static void CollectSignalRefWrites(JsonElement stmt, HashSet<string> written, int depth)
        {
            if (depth > MaxDepth || stmt.ValueKind != JsonValueKind.Object) return;
            string? kind = TryString(stmt, "kind");
            switch (kind)
            {
                case "block":
                    if (stmt.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        foreach (var it in items.EnumerateArray()) CollectSignalRefWrites(it, written, depth + 1);
                    break;
                case "nba":
                case "blocking":
                    if (stmt.TryGetProperty("target", out var tgt))
                    {
                        foreach (var leaf in TargetLeaves(tgt))
                        {
                            string? lk = TryString(leaf, "kind");
                            if (lk == "ref" && TryString(leaf, "name") is { } n) written.Add(n);
                            else if (lk is "bitselect" or "partselect" or "indexedselect" && leaf.TryGetProperty("value", out var v) && TryString(v, "name") is { } sn) written.Add(sn);
                        }
                    }
                    break;
                case "if":
                    if (stmt.TryGetProperty("then", out var t)) CollectSignalRefWrites(t, written, depth + 1);
                    if (stmt.TryGetProperty("else", out var e)) CollectSignalRefWrites(e, written, depth + 1);
                    break;
                case "case":
                    if (stmt.TryGetProperty("items", out var ci) && ci.ValueKind == JsonValueKind.Array)
                        foreach (var it in ci.EnumerateArray())
                            if (it.TryGetProperty("body", out var b)) CollectSignalRefWrites(b, written, depth + 1);
                    break;
                case "while":
                case "do":
                case "repeat":
                case "runtime_repeat":
                case "for":
                    if (stmt.TryGetProperty("body", out var lb)) CollectSignalRefWrites(lb, written, depth + 1);
                    break;
            }
        }

        private void RecordCombStep(
            string label,
            HashSet<string> stepReads,
            HashSet<string> stepWrites,
            HashSet<string> seqWriters,
            Dictionary<string, string> combOwners,
            List<(string Label, HashSet<string> Reads, HashSet<string> Writes)> combSteps)
        {
            if (!isTimed)
            {
                foreach (string clk in clockAndEventSignals)
                {
                    if (stepReads.Contains(clk))
                    {
                        sawClkComb = true;
                        Emit("CLKCOMB", LintSeverity.Error, $"Clock/event signal '{clk}' is read inside combinational logic ({label}).", clk, label);
                    }
                }

                foreach (string w in stepWrites)
                {
                    if (seqWriters.Contains(w))
                    {
                        sawMultiDriven = true;
                        Emit("MULTIDRIVEN", LintSeverity.Error, $"Signal '{w}' is driven by both sequential and combinational logic ({label}).", w, label);
                    }
                    else if (combOwners.TryGetValue(w, out string? prior))
                    {
                        sawMultiDriven = true;
                        Emit("MULTIDRIVEN", LintSeverity.Error, $"Signal '{w}' has multiple combinational drivers ({prior} and {label}).", w, label);
                    }
                    else
                    {
                        combOwners[w] = label;
                    }
                }
            }

            combSteps.Add((label, stepReads, stepWrites));
        }

        private void CheckCombinationalCycles(List<(string Label, HashSet<string> Reads, HashSet<string> Writes)> steps)
        {
            var owners = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < steps.Count; i++)
                foreach (string w in steps[i].Writes)
                    owners.TryAdd(w, i);

            var pending = Enumerable.Range(0, steps.Count).ToHashSet();
            while (pending.Count > 0)
            {
                var ready = pending
                    .Where(i => !steps[i].Reads.Any(n => owners.TryGetValue(n, out int owner) && owner != i && pending.Contains(owner)))
                    .ToArray();
                if (ready.Length == 0)
                {
                    var cyclicSignals = pending
                        .SelectMany(i => steps[i].Writes)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)
                        .ToArray();
                    sawCombLoop = true;
                    string list = string.Join(", ", cyclicSignals);
                    Emit("UNOPTFLAT", LintSeverity.Error, $"Combinational feedback loop detected involving: {list}.", cyclicSignals.FirstOrDefault(), "combinational");
                    break;
                }
                foreach (int i in ready) pending.Remove(i);
            }
        }

        private void CheckCombinationalLatch(JsonElement stmt, HashSet<string> owned, HashSet<string> assigned, string context, int depth)
        {
            if (depth > MaxDepth || stmt.ValueKind != JsonValueKind.Object) return;
            string? kind = TryString(stmt, "kind");
            if (kind is null) return;

            void CheckReadBeforeAssign(JsonElement expr)
            {
                var refs = new HashSet<string>(StringComparer.Ordinal);
                CollectReferences(expr, refs, 0);
                foreach (string name in refs.Where(n => owned.Contains(n) && !assigned.Contains(n)).Order(StringComparer.Ordinal))
                {
                    sawLatch = true;
                    Emit("LATCH", LintSeverity.Error, $"Signal '{name}' is read before definite assignment in combinational block (inferred latch).", name, context);
                }
            }

            switch (kind)
            {
                case "block":
                    if (stmt.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        foreach (var item in items.EnumerateArray())
                            CheckCombinationalLatch(item, owned, assigned, context, depth + 1);
                    break;
                case "blocking":
                    if (stmt.TryGetProperty("value", out var val)) CheckReadBeforeAssign(val);
                    if (stmt.TryGetProperty("target", out var tgt))
                    {
                        foreach (var leaf in TargetLeaves(tgt))
                        {
                            if (TryString(leaf, "kind") == "ref" && TryString(leaf, "name") is { } n)
                                assigned.Add(n);
                        }
                    }
                    break;
                case "if":
                    if (stmt.TryGetProperty("condition", out var cond)) CheckReadBeforeAssign(cond);
                    var yes = new HashSet<string>(assigned, StringComparer.Ordinal);
                    var no = new HashSet<string>(assigned, StringComparer.Ordinal);
                    if (stmt.TryGetProperty("then", out var thenEl)) CheckCombinationalLatch(thenEl, owned, yes, context, depth + 1);
                    if (stmt.TryGetProperty("else", out var elseEl)) CheckCombinationalLatch(elseEl, owned, no, context, depth + 1);
                    yes.IntersectWith(no);
                    assigned.UnionWith(yes);
                    break;
                case "case":
                    if (stmt.TryGetProperty("value", out var caseVal)) CheckReadBeforeAssign(caseVal);
                    var paths = new List<HashSet<string>>();
                    bool hasDefault = false;
                    if (stmt.TryGetProperty("items", out var caseItems) && caseItems.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in caseItems.EnumerateArray())
                        {
                            if (IsCaseDefault(item)) hasDefault = true;
                            var path = new HashSet<string>(assigned, StringComparer.Ordinal);
                            if (item.TryGetProperty("body", out var b)) CheckCombinationalLatch(b, owned, path, context, depth + 1);
                            paths.Add(path);
                        }
                    }
                    if (!hasDefault) paths.Add(new HashSet<string>(assigned, StringComparer.Ordinal));
                    if (paths.Count > 0)
                    {
                        var common = paths[0];
                        foreach (var p in paths.Skip(1)) common.IntersectWith(p);
                        assigned.UnionWith(common);
                    }
                    break;
                case "while":
                case "do":
                case "runtime_repeat":
                    if (stmt.TryGetProperty("condition", out var lc)) CheckReadBeforeAssign(lc);
                    if (stmt.TryGetProperty("count", out var rc)) CheckReadBeforeAssign(rc);
                    var loopAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                    if (stmt.TryGetProperty("body", out var lb)) CheckCombinationalLatch(lb, owned, loopAssigned, context, depth + 1);
                    break;
                case "repeat":
                    var repAssigned = new HashSet<string>(assigned, StringComparer.Ordinal);
                    if (stmt.TryGetProperty("body", out var rb)) CheckCombinationalLatch(rb, owned, repAssigned, context, depth + 1);
                    if (TryInt(stmt, "count") is > 0) assigned.UnionWith(repAssigned);
                    break;
            }
        }

        private void AnalyzeStatement(JsonElement stmt, bool inClockedProcess, bool isTimedContext, string context, HashSet<string> reads, HashSet<string> writes, int depth, int ifChainDepth = 0)
        {
            if (depth > MaxDepth || stmt.ValueKind != JsonValueKind.Object) return;
            string? kind = TryString(stmt, "kind");
            if (kind is null) return;

            collectedStatements.Add(new RtlLintStatement(kind, context, depth, inClockedProcess, stmt));

            switch (kind)
            {
                case "block":
                case "parallel":
                case "fork":
                    if (stmt.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                        foreach (var item in items.EnumerateArray())
                            AnalyzeStatement(item, inClockedProcess, isTimedContext, context, reads, writes, depth + 1);
                    break;
                case "process":
                {
                    if (stmt.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var ev in evs.EnumerateArray())
                        {
                            if (TryString(ev, "signal") is { } s) reads.Add(s);
                            if (TryString(ev, "memory") is { } m) reads.Add(m);
                        }
                    }
                    if (stmt.TryGetProperty("body", out var body))
                        AnalyzeStatement(body, inClockedProcess: true, isTimedContext, context, reads, writes, depth + 1);
                    break;
                }
                case "always_star":
                    if (stmt.TryGetProperty("body", out var starBody))
                        AnalyzeStatement(starBody, inClockedProcess: false, isTimedContext: true, context, reads, writes, depth + 1);
                    break;
                case "event":
                    if (stmt.TryGetProperty("events", out var tEvs) && tEvs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var ev in tEvs.EnumerateArray())
                        {
                            if (TryString(ev, "signal") is { } s) { reads.Add(s); clockAndEventSignals.Add(s); }
                            if (TryString(ev, "memory") is { } m) reads.Add(m);
                        }
                    }
                    if (stmt.TryGetProperty("body", out var evBody))
                        AnalyzeStatement(evBody, inClockedProcess, isTimedContext: true, context, reads, writes, depth + 1);
                    break;
                case "delay":
                    if (stmt.TryGetProperty("amount", out var amt)) AnalyzeExpression(amt, context, reads, depth + 1);
                    if (stmt.TryGetProperty("body", out var dBody)) AnalyzeStatement(dBody, inClockedProcess, isTimedContext: true, context, reads, writes, depth + 1);
                    break;
                case "wait":
                    if (stmt.TryGetProperty("condition", out var wCond)) AnalyzeExpression(wCond, context, reads, depth + 1);
                    if (stmt.TryGetProperty("body", out var wBody)) AnalyzeStatement(wBody, inClockedProcess, isTimedContext: true, context, reads, writes, depth + 1);
                    break;
                case "blocking":
                case "blocking_delay":
                case "nba":
                case "nba_delay":
                {
                    bool isBlocking = kind is "blocking" or "blocking_delay";
                    if (stmt.TryGetProperty("amount", out var dAmt)) AnalyzeExpression(dAmt, context, reads, depth + 1);
                    if (stmt.TryGetProperty("value", out var val)) AnalyzeExpression(val, context, reads, depth + 1);
                    if (stmt.TryGetProperty("target", out var tgt))
                    {
                        var tgtWrites = new HashSet<string>(StringComparer.Ordinal);
                        AnalyzeTarget(tgt, context, reads, tgtWrites, depth + 1);
                        writes.UnionWith(tgtWrites);
                        foreach (string w in tgtWrites)
                        {
                            if (isBlocking) blockingWriters.Add(w);
                            else nbaWriters.Add(w);
                        }
                        if (stmt.TryGetProperty("value", out var v2))
                            AnalyzeAssignmentChecks(tgt, v2, isBlocking, inClockedProcess, context);
                    }
                    break;
                }
                case "if":
                {
                    int currentIfDepth = ifChainDepth + 1;
                    if (currentIfDepth == parameters.MaxIfDepth + 1)
                        Emit("IFDEPTH", LintSeverity.Warning, $"Deep if/else-if chain exceeds max_if_depth ({parameters.MaxIfDepth}); consider a 'case' statement.", null, context);

                    if (stmt.TryGetProperty("condition", out var cond))
                    {
                        AnalyzeExpression(cond, context, reads, depth + 1);
                        if (TryString(cond, "kind") == "const")
                            Emit("CMPCONST", LintSeverity.Warning, $"Branch condition in 'if' statement is constant ({TryString(cond, "bits")}).", null, context);
                    }
                    if (stmt.TryGetProperty("then", out var thenEl))
                        AnalyzeStatement(thenEl, inClockedProcess, isTimedContext, context, reads, writes, depth + 1, 0);
                    if (stmt.TryGetProperty("else", out var elseEl))
                    {
                        bool elseIsIf = TryString(elseEl, "kind") == "if";
                        AnalyzeStatement(elseEl, inClockedProcess, isTimedContext, context, reads, writes, depth + 1, elseIsIf ? currentIfDepth : 0);
                    }
                    break;
                }
                case "case":
                    AnalyzeCase(stmt, inClockedProcess, isTimedContext, context, reads, writes, depth + 1);
                    break;
                case "while":
                case "do":
                {
                    bool hasBody = stmt.TryGetProperty("body", out var lb);
                    if (stmt.TryGetProperty("condition", out var lc))
                    {
                        AnalyzeExpression(lc, context, reads, depth + 1);
                        if (TryConstUInt64(lc, out ulong cVal) && cVal != 0 && hasBody && !ContainsLoopExitOrSuspension(lb, 0))
                            Emit("INFLOOP", LintSeverity.Error, $"Loop '{kind}' has constant true condition with no break or timing suspension.", null, context);
                    }
                    if (hasBody) AnalyzeStatement(lb, inClockedProcess, isTimedContext, context, reads, writes, depth + 1);
                    break;
                }
                case "for":
                {
                    bool hasBody = stmt.TryGetProperty("body", out var fb);
                    if (stmt.TryGetProperty("condition", out var fc))
                    {
                        AnalyzeExpression(fc, context, reads, depth + 1);
                        if (TryConstUInt64(fc, out ulong cVal) && cVal != 0 && hasBody && !ContainsLoopExitOrSuspension(fb, 0))
                            Emit("INFLOOP", LintSeverity.Error, "Loop 'for' has constant true condition with no break or timing suspension.", null, context);
                    }
                    if (stmt.TryGetProperty("step", out var fs)) AnalyzeStatement(fs, inClockedProcess, isTimedContext, context, reads, writes, depth + 1);
                    if (hasBody) AnalyzeStatement(fb, inClockedProcess, isTimedContext, context, reads, writes, depth + 1);
                    break;
                }
                case "runtime_repeat":
                    if (stmt.TryGetProperty("count", out var rrc)) AnalyzeExpression(rrc, context, reads, depth + 1);
                    if (stmt.TryGetProperty("body", out var rrb)) AnalyzeStatement(rrb, inClockedProcess, isTimedContext, context, reads, writes, depth + 1);
                    break;
                case "repeat":
                    if (stmt.TryGetProperty("body", out var rb)) AnalyzeStatement(rb, inClockedProcess, isTimedContext, context, reads, writes, depth + 1);
                    break;
                case "assert":
                    if (stmt.TryGetProperty("condition", out var ac)) AnalyzeExpression(ac, context, reads, depth + 1);
                    break;
                case "display":
                case "fatal":
                    if (stmt.TryGetProperty("args", out var dArgs) && dArgs.ValueKind == JsonValueKind.Array)
                        foreach (var a in dArgs.EnumerateArray())
                            AnalyzeExpression(a, context, reads, depth + 1);
                    break;
                case "value_plusargs":
                    if (TryString(stmt, "target") is { } vpTgt) writes.Add(vpTgt);
                    if (TryString(stmt, "matched") is { } vpMatch) writes.Add(vpMatch);
                    break;
                case "force":
                    if (TryString(stmt, "target") is { } fTgt) writes.Add(fTgt);
                    if (stmt.TryGetProperty("value", out var fVal)) AnalyzeExpression(fVal, context, reads, depth + 1);
                    break;
                case "allocate":
                    if (TryString(stmt, "name") is { } allocName) { writes.Add(allocName); blockingWriters.Add(allocName); }
                    if (stmt.TryGetProperty("count", out var allocCnt)) AnalyzeExpression(allocCnt, context, reads, depth + 1);
                    break;
                case "delete":
                    if (TryString(stmt, "name") is { } delName) { writes.Add(delName); blockingWriters.Add(delName); }
                    if (stmt.TryGetProperty("index", out var delIdx)) AnalyzeExpression(delIdx, context, reads, depth + 1);
                    break;
                case "queue_push":
                    if (TryString(stmt, "name") is { } qpName) { writes.Add(qpName); blockingWriters.Add(qpName); }
                    if (stmt.TryGetProperty("value", out var qpVal)) AnalyzeExpression(qpVal, context, reads, depth + 1);
                    break;
                case "queue_pop":
                    if (TryString(stmt, "name") is { } qpopName) { reads.Add(qpopName); writes.Add(qpopName); blockingWriters.Add(qpopName); }
                    if (TryString(stmt, "target") is { } qpopTgt) { writes.Add(qpopTgt); blockingWriters.Add(qpopTgt); }
                    break;
                case "assoc_traverse":
                    if (TryString(stmt, "name") is { } atName) reads.Add(atName);
                    if (TryString(stmt, "key") is { } atKey) { reads.Add(atKey); writes.Add(atKey); blockingWriters.Add(atKey); }
                    if (TryString(stmt, "found") is { } atFound) { writes.Add(atFound); blockingWriters.Add(atFound); }
                    break;
            }
        }

        private static bool ContainsLoopExitOrSuspension(JsonElement stmt, int depth)
        {
            if (depth > MaxDepth || stmt.ValueKind != JsonValueKind.Object) return false;
            string? kind = TryString(stmt, "kind");
            if (kind is "break" or "delay" or "wait" or "event" or "blocking_delay" or "finish" or "fatal")
                return true;
            if (stmt.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                foreach (var it in items.EnumerateArray())
                    if (ContainsLoopExitOrSuspension(it, depth + 1)) return true;
            foreach (string field in new[] { "body", "then", "else" })
                if (stmt.TryGetProperty(field, out var child) && ContainsLoopExitOrSuspension(child, depth + 1))
                    return true;
            return false;
        }

        private void AnalyzeCase(JsonElement stmt, bool inClockedProcess, bool isTimedContext, string context, HashSet<string> reads, HashSet<string> writes, int depth)
        {
            string caseKind = TryString(stmt, "case_kind") ?? "exact";
            if (caseKind == "casex")
                Emit("CASEX", LintSeverity.Warning, "Use of 'casex' treats X bits as wildcards and can mask simulation hazards; prefer 'casez' or 'inside'.", null, context);

            int? selWidth = null;
            if (stmt.TryGetProperty("value", out var selVal))
            {
                AnalyzeExpression(selVal, context, reads, depth + 1);
                selWidth = TryExprWidth(selVal);
            }

            bool hasDefault = false;
            var seenPatterns = new HashSet<string>(StringComparer.Ordinal);
            var coveredExact = new HashSet<ulong>();
            var wildcardPatterns = new List<string>();

            if (stmt.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (IsCaseDefault(item))
                    {
                        hasDefault = true;
                    }
                    else
                    {
                        foreach (var matchEl in CaseItemMatches(item))
                        {
                            AnalyzeExpression(matchEl, context, reads, depth + 1);
                            int? mw = TryExprWidth(matchEl);
                            if (selWidth is > 0 && mw is > 0)
                            {
                                if (mw.Value > selWidth.Value)
                                    Emit("WIDTHTRUNC", LintSeverity.Warning, $"Case label width ({mw.Value} bits) exceeds case selector width ({selWidth.Value} bits).", null, context);
                                else if (mw.Value < selWidth.Value && !(parameters.AllowLiteralWidthExpand && TryString(matchEl, "kind") == "const"))
                                    Emit("WIDTHEXPAND", LintSeverity.Warning, $"Case label width ({mw.Value} bits) is narrower than case selector width ({selWidth.Value} bits).", null, context);
                            }

                            if (TryString(matchEl, "kind") == "const" && TryString(matchEl, "bits") is { } bits)
                            {
                                if (caseKind is "exact" or "casez" && bits.IndexOfAny(['x', 'X']) >= 0)
                                    Emit("CASEWITHX", LintSeverity.Warning, $"Case label '{bits}' contains literal 'x' in '{caseKind}' statement; it will never match in 2-state synthesis.", null, context);

                                string normalized = selWidth is > 0 ? NormalizeBits(bits, selWidth.Value) : bits;
                                if (!seenPatterns.Add(normalized))
                                    Emit("CASEOVERLAP", LintSeverity.Warning, $"Duplicate case label '{bits}' in case statement.", null, context);

                                if (TryParseBinary(normalized, out ulong val))
                                    coveredExact.Add(val);
                                else if (caseKind is "casez" or "casex")
                                    wildcardPatterns.Add(normalized);
                            }
                        }
                    }

                    if (item.TryGetProperty("body", out var body))
                        AnalyzeStatement(body, inClockedProcess, isTimedContext, context, reads, writes, depth + 1);
                }
            }

            if (!hasDefault)
            {
                bool complete = false;
                if (selWidth is >= 1 and <= 16)
                {
                    int totalStates = 1 << selWidth.Value;
                    if (caseKind == "exact")
                    {
                        complete = coveredExact.Count >= totalStates;
                    }
                    else if (selWidth.Value <= 8)
                    {
                        int covered = 0;
                        for (int s = 0; s < totalStates; s++)
                        {
                            if (coveredExact.Contains((ulong)s) || wildcardPatterns.Any(p => WildcardMatches(p, (ulong)s, caseKind == "casex")))
                                covered++;
                        }
                        complete = covered == totalStates;
                    }
                }
                if (!complete)
                    Emit("CASEINCOMPLETE", LintSeverity.Warning, $"Case statement ({selWidth?.ToString(CultureInfo.InvariantCulture) ?? "?"}-bit selector) has no default branch and may not cover all values.", null, context);
            }
        }

        private void AnalyzeTarget(JsonElement target, string context, HashSet<string> reads, HashSet<string> writes, int depth)
        {
            if (depth > MaxDepth || target.ValueKind != JsonValueKind.Object) return;
            foreach (var leaf in TargetLeaves(target))
            {
                string? kind = TryString(leaf, "kind");
                if (kind == "ref")
                {
                    if (TryString(leaf, "name") is { } n)
                    {
                        writes.Add(n);
                        CheckInputTarget(n, context);
                    }
                }
                else if (kind == "index")
                {
                    if (TryString(leaf, "name") is { } mn)
                    {
                        writes.Add(mn);
                        CheckMemoryIndexRange(leaf, mn, context);
                    }
                    foreach (var idxExpr in MemoryIndices(leaf))
                        AnalyzeExpression(idxExpr, context, reads, depth + 1);
                }
                else if (kind is "bitselect" or "partselect" or "indexedselect")
                {
                    if (leaf.TryGetProperty("value", out var inner) && TryString(inner, "kind") == "ref" && TryString(inner, "name") is { } sn)
                    {
                        writes.Add(sn);
                        CheckInputTarget(sn, context);
                        CheckSelectRange(leaf, sn, context);
                    }
                    if (leaf.TryGetProperty("index", out var idx)) AnalyzeExpression(idx, context, reads, depth + 1);
                    if (leaf.TryGetProperty("base", out var b)) AnalyzeExpression(b, context, reads, depth + 1);
                }
            }
        }

        private void CheckInputTarget(string name, string context)
        {
            if (signals.TryGetValue(name, out var sig) && sig.Direction == "input")
            {
                sawAssignIn = true;
                Emit("ASSIGNIN", LintSeverity.Error, $"Assignment targets input port '{name}'.", name, context);
            }
        }

        private void AnalyzeAssignmentChecks(JsonElement target, JsonElement value, bool isBlocking, bool inClockedProcess, string context)
        {
            string targetDesc = DescribeTarget(target);
            string? targetOwner = TryTargetOwner(target);
            int? tw = TryExprWidth(target);
            int? vw = TryExprWidth(value);
            bool? tSigned = TryExprSigned(target);
            bool? vSigned = TryExprSigned(value);

            collectedAssignments.Add(new RtlLintAssignment(
                targetDesc, targetOwner, tw, vw, tSigned, vSigned, isBlocking, inClockedProcess, context, target, value));

            // SELFASSIGN: x = x or x <= x
            if (TryString(target, "kind") == "ref" && TryString(value, "kind") == "ref" &&
                TryString(target, "name") == TryString(value, "name") && !string.IsNullOrEmpty(targetOwner))
            {
                Emit("SELFASSIGN", LintSeverity.Warning, $"Self-assignment to '{targetOwner}' has no effect.", targetOwner, context);
            }

            // BLKSEQ: blocking assignment to module state inside clocked sequential process
            if (isBlocking && inClockedProcess)
            {
                bool isContainerMem = TryString(target, "kind") == "index" && targetOwner is not null &&
                                      memories.TryGetValue(targetOwner, out var mem) && mem.Container;
                if (!isContainerMem && !string.IsNullOrEmpty(targetOwner))
                {
                    Emit("BLKSEQ", LintSeverity.Warning, $"Blocking assignment (=) to '{targetDesc}' inside clocked sequential process; consider non-blocking '<='.", targetOwner, context);
                }
            }

            // Width checks: WIDTHTRUNC / WIDTHEXPAND
            if (tw is > 0 && vw is > 0 && tw.Value != vw.Value)
            {
                if (vw.Value > tw.Value)
                    Emit("WIDTHTRUNC", LintSeverity.Warning, $"Assignment to '{targetDesc}' ({tw.Value} bits) truncates {vw.Value}-bit expression.", targetOwner, context);
                else if (!(parameters.AllowLiteralWidthExpand && TryString(value, "kind") == "const"))
                    Emit("WIDTHEXPAND", LintSeverity.Warning, $"Assignment to '{targetDesc}' ({tw.Value} bits) implicitly expands {vw.Value}-bit expression.", targetOwner, context);
            }

            // Sign mismatch check on simple ref/index assignments (skip explicit casts/conversions and non-negative constants)
            string? valKind = TryString(value, "kind");
            if (valKind is not ("convert" or "castsigned" or "castunsigned"))
            {
                if (tSigned.HasValue && vSigned.HasValue && tSigned.Value != vSigned.Value)
                {
                    bool benignConst = valKind == "const" && !(vSigned.Value && TryString(value, "bits") is { Length: > 0 } b && b[0] == '1');
                    if (!benignConst)
                    {
                        string tLabel = tSigned.Value ? "signed" : "unsigned";
                        string vLabel = vSigned.Value ? "signed" : "unsigned";
                        Emit("SIGNCOMPARE", LintSeverity.Warning, $"Assignment to {tLabel} target '{targetDesc}' from {vLabel} expression without explicit cast.", targetOwner, context);
                    }
                }
            }

            // FOURSTATE: 4-state expression assigned directly to two_state storage
            if (valKind != "convert" && IsTargetTwoState(target) && IsFourStateExpr(value, 0))
            {
                Emit("FOURSTATE", LintSeverity.Warning, $"Four-state expression assigned to two-state target '{targetDesc}'; X/Z bits will be coerced to 0.", targetOwner, context);
            }
        }

        private void AnalyzeExpression(JsonElement expr, string context, HashSet<string> reads, int depth)
        {
            if (depth > MaxDepth || expr.ValueKind != JsonValueKind.Object) return;
            string? kind = TryString(expr, "kind");
            if (kind is null) return;

            collectedExpressions.Add(new RtlLintExpression(kind, context, TryExprWidth(expr), TryExprSigned(expr), expr));

            switch (kind)
            {
                case "ref":
                case "size":
                    if (TryString(expr, "name") is { } rn) reads.Add(rn);
                    return;
                case "past":
                case "rose":
                case "fell":
                case "stable":
                    if (TryString(expr, "signal") is { } ps) reads.Add(ps);
                    return;
                case "const":
                case "urandom":
                case "random":
                case "test_plusargs":
                    return;
                case "index":
                case "exists":
                    if (TryString(expr, "name") is { } mn)
                    {
                        reads.Add(mn);
                        if (kind == "index") CheckMemoryIndexRange(expr, mn, context);
                    }
                    foreach (var idx in MemoryIndices(expr))
                        AnalyzeExpression(idx, context, reads, depth + 1);
                    return;
                case "bitselect":
                case "partselect":
                case "indexedselect":
                    if (expr.TryGetProperty("value", out var selVal))
                    {
                        AnalyzeExpression(selVal, context, reads, depth + 1);
                        CheckSelectRange(expr, TryString(selVal, "name"), context);
                    }
                    if (expr.TryGetProperty("index", out var bIdx)) AnalyzeExpression(bIdx, context, reads, depth + 1);
                    if (expr.TryGetProperty("base", out var sBase)) AnalyzeExpression(sBase, context, reads, depth + 1);
                    return;
                case "convert":
                    if (expr.TryGetProperty("value", out var cv)) AnalyzeExpression(cv, context, reads, depth + 1);
                    return;
                case "urandom_range":
                    if (expr.TryGetProperty("max", out var uMax)) AnalyzeExpression(uMax, context, reads, depth + 1);
                    if (expr.TryGetProperty("min", out var uMin)) AnalyzeExpression(uMin, context, reads, depth + 1);
                    return;
                case "inside":
                    if (expr.TryGetProperty("value", out var inVal)) AnalyzeExpression(inVal, context, reads, depth + 1);
                    if (expr.TryGetProperty("items", out var inItems) && inItems.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var it in inItems.EnumerateArray())
                        {
                            if (it.TryGetProperty("lo", out var lo) && it.TryGetProperty("hi", out var hi))
                            {
                                AnalyzeExpression(lo, context, reads, depth + 1);
                                AnalyzeExpression(hi, context, reads, depth + 1);
                            }
                            else
                            {
                                AnalyzeExpression(it, context, reads, depth + 1);
                            }
                        }
                    }
                    return;
            }

            if (!expr.TryGetProperty("args", out var argsEl) || argsEl.ValueKind != JsonValueKind.Array)
                return;

            var args = argsEl.EnumerateArray().ToArray();
            foreach (var a in args)
                AnalyzeExpression(a, context, reads, depth + 1);

            if (kind == "cond" && args.Length == 3)
            {
                if (TryString(args[0], "kind") == "const")
                    Emit("CMPCONST", LintSeverity.Warning, $"Conditional '?:' selector is constant ({TryString(args[0], "bits")}).", null, context);

                int? w1 = TryExprWidth(args[1]), w2 = TryExprWidth(args[2]);
                if (w1 is > 0 && w2 is > 0 && w1.Value != w2.Value)
                {
                    bool literalExempt = parameters.AllowLiteralWidthExpand &&
                        ((w1.Value < w2.Value && TryString(args[1], "kind") == "const") ||
                         (w2.Value < w1.Value && TryString(args[2], "kind") == "const"));
                    if (!literalExempt)
                        Emit("WIDTHEXPAND", LintSeverity.Warning, $"Conditional '?:' branches have mismatched widths ({w1.Value} vs {w2.Value} bits).", null, context);
                }

                bool? s1 = TryExprSigned(args[1]), s2 = TryExprSigned(args[2]);
                if (s1.HasValue && s2.HasValue && s1.Value != s2.Value)
                    Emit("SIGNCOMPARE", LintSeverity.Warning, "Conditional '?:' branches mix signed and unsigned expressions.", null, context);
            }
            else if (args.Length == 2)
            {
                // SHIFTOVF: shift amount >= shifted value width
                if (kind is "shl" or "shr" or "ashr")
                {
                    int? lw = TryExprWidth(args[0]);
                    if (lw is > 0 && TryConstUInt64(args[1], out ulong shiftAmt) && shiftAmt >= (ulong)lw.Value)
                        Emit("SHIFTOVF", LintSeverity.Warning, $"Constant shift amount ({shiftAmt}) in '{kind}' equals or exceeds {lw.Value}-bit operand width.", null, context);
                }

                // DIVZERO: division or modulo by constant zero
                if (kind is "div" or "mod" && TryConstUInt64(args[1], out ulong divisor) && divisor == 0)
                {
                    Emit("DIVZERO", LintSeverity.Error, $"Operator '{kind}' divides by constant zero.", null, context);
                }

                bool isWidthMatchedOp = kind is "add" or "sub" or "mul" or "div" or "mod"
                    or "bitand" or "bitor" or "bitxor" or "bitxnor"
                    or "eq" or "neq" or "caseeq" or "caseneq" or "wildeq" or "wildneq"
                    or "lt" or "le" or "gt" or "ge";

                if (isWidthMatchedOp)
                {
                    int? w0 = TryExprWidth(args[0]), w1 = TryExprWidth(args[1]);
                    if (w0 is > 0 && w1 is > 0 && w0.Value != w1.Value)
                    {
                        bool literalExempt = parameters.AllowLiteralWidthExpand &&
                            ((w0.Value < w1.Value && TryString(args[0], "kind") == "const") ||
                             (w1.Value < w0.Value && TryString(args[1], "kind") == "const"));
                        if (!literalExempt)
                            Emit("WIDTHEXPAND", LintSeverity.Warning, $"Operator '{kind}' operands have mismatched widths ({w0.Value} vs {w1.Value} bits).", null, context);
                    }
                }

                bool isCompare = kind is "eq" or "neq" or "wildeq" or "wildneq" or "lt" or "le" or "gt" or "ge";
                if (isCompare)
                {
                    bool? s0 = TryExprSigned(args[0]), s1 = TryExprSigned(args[1]);
                    if (s0.HasValue && s1.HasValue && s0.Value != s1.Value)
                        Emit("SIGNCOMPARE", LintSeverity.Warning, $"Comparison '{kind}' mixes signed and unsigned operands; unsigned comparison semantics apply.", null, context);

                    CheckConstantComparison(kind, args[0], args[1], context);
                }

                if (kind is "eq" or "neq")
                {
                    if (IsXzConst(args[0]) || IsXzConst(args[1]))
                        Emit("FOURSTATE", LintSeverity.Warning, $"Logical equality '{kind}' against literal X/Z always produces X; use case equality (===/!==) or wildcard equality (==?/!=?).", null, context);
                }
            }
        }

        private void CheckConstantComparison(string kind, JsonElement lhs, JsonElement rhs, string context)
        {
            if (kind is "lt" or "ge" && TryExprSigned(lhs) == false && TryConstUInt64(rhs, out ulong rZero) && rZero == 0)
            {
                string outcome = kind == "lt" ? "false" : "true";
                Emit("CMPCONST", LintSeverity.Warning, $"Unsigned comparison '{kind} 0' is always {outcome}.", null, context);
                return;
            }

            if (TryString(lhs, "kind") != "const" && TryExprSigned(lhs) == false && TryExprWidth(lhs) is >= 1 and <= 60 and int lw &&
                TryConstUInt64(rhs, out ulong rVal) && rVal >= (1UL << lw))
            {
                Emit("CMPCONST", LintSeverity.Warning, $"Comparison '{kind}' of {lw}-bit unsigned expression against constant {rVal} is statically constant.", null, context);
            }
            else if (TryString(rhs, "kind") != "const" && TryExprSigned(rhs) == false && TryExprWidth(rhs) is >= 1 and <= 60 and int rw &&
                TryConstUInt64(lhs, out ulong lVal) && lVal >= (1UL << rw))
            {
                Emit("CMPCONST", LintSeverity.Warning, $"Comparison '{kind}' of constant {lVal} against {rw}-bit unsigned expression is statically constant.", null, context);
            }
        }

        private void CheckSelectRange(JsonElement sel, string? sigName, string context)
        {
            if (!sel.TryGetProperty("value", out var valEl)) return;
            int? valWidth = TryExprWidth(valEl);
            if (valWidth is not > 0) return;
            string? kind = TryString(sel, "kind");
            if (kind == "bitselect" && sel.TryGetProperty("index", out var idxEl) && TryConstUInt64(idxEl, out ulong bitIdx))
            {
                if (bitIdx >= (ulong)valWidth.Value)
                    Emit("SELRANGE", LintSeverity.Warning, $"Constant bit select [{bitIdx}] is out of range for {valWidth.Value}-bit expression.", sigName, context);
            }
            else if (kind == "partselect")
            {
                int? left = TryInt(sel, "left"), right = TryInt(sel, "right");
                if (left.HasValue && right.HasValue && (right.Value < 0 || left.Value < right.Value || left.Value >= valWidth.Value))
                    Emit("SELRANGE", LintSeverity.Warning, $"Part select [{left.Value}:{right.Value}] is out of range for {valWidth.Value}-bit expression.", sigName, context);
            }
        }

        private void CheckMemoryIndexRange(JsonElement idxNode, string memName, string context)
        {
            if (!memories.TryGetValue(memName, out var mem) || mem.Container) return;
            if (!idxNode.TryGetProperty("index", out var idxEl)) return;
            if (TryConstUInt64(idxEl, out ulong addr))
            {
                ulong hi = (ulong)mem.LowerBound + (ulong)mem.Depth;
                if (addr < (ulong)mem.LowerBound || addr >= hi)
                    Emit("SELRANGE", LintSeverity.Warning, $"Constant index [{addr}] is out of bounds for memory '{memName}' ([{mem.LowerBound}:{hi - 1}]).", memName, context);
            }
        }

        private void AnalyzeVerificationReads()
        {
            if (root.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var p in props.EnumerateArray())
                {
                    string label = $"property[{idx++}]";
                    foreach (string field in new[] { "antecedent", "consequent", "disable_iff" })
                        if (p.TryGetProperty(field, out var ex) && ex.ValueKind == JsonValueKind.Object)
                            AnalyzeExpression(ex, label, allReads, 0);
                }
            }
            if (root.TryGetProperty("covergroups", out var cgs) && cgs.ValueKind == JsonValueKind.Array)
            {
                int idx = 0;
                foreach (var cg in cgs.EnumerateArray())
                {
                    string label = $"covergroup[{idx++}]";
                    if (cg.TryGetProperty("coverpoints", out var cps) && cps.ValueKind == JsonValueKind.Array)
                        foreach (var cp in cps.EnumerateArray())
                            if (cp.TryGetProperty("expr", out var ex) && ex.ValueKind == JsonValueKind.Object)
                                AnalyzeExpression(ex, label, allReads, 0);
                }
            }
        }

        private bool IsTargetTwoState(JsonElement target)
        {
            string? kind = TryString(target, "kind");
            if (kind == "ref" && TryString(target, "name") is { } sn && signals.TryGetValue(sn, out var sig))
                return sig.TwoState;
            if (kind == "index" && TryString(target, "name") is { } mn && memories.TryGetValue(mn, out var mem))
                return mem.TwoState;
            if (kind is "bitselect" or "partselect" or "indexedselect" && target.TryGetProperty("value", out var inner))
                return IsTargetTwoState(inner);
            return false;
        }

        private bool IsFourStateExpr(JsonElement expr, int depth)
        {
            if (depth > MaxDepth || expr.ValueKind != JsonValueKind.Object) return false;
            string? kind = TryString(expr, "kind");
            if (kind is null) return false;
            switch (kind)
            {
                case "const":
                    return IsXzConst(expr);
                case "ref":
                    return TryString(expr, "name") is { } sn && signals.TryGetValue(sn, out var sig) && !sig.TwoState;
                case "past":
                    return TryString(expr, "signal") is { } ps && signals.TryGetValue(ps, out var psig) && !psig.TwoState;
                case "index":
                    return TryString(expr, "name") is { } mn && memories.TryGetValue(mn, out var mem) && !mem.TwoState;
                case "size":
                case "exists":
                case "urandom":
                case "urandom_range":
                case "random":
                case "test_plusargs":
                case "caseeq":
                case "caseneq":
                case "isunknown":
                case "onehot":
                case "onehot0":
                case "countones":
                    return false;
                case "convert":
                case "bitselect":
                case "partselect":
                case "indexedselect":
                    return expr.TryGetProperty("value", out var iv) && IsFourStateExpr(iv, depth + 1);
                default:
                    if (expr.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
                        foreach (var a in args.EnumerateArray())
                            if (IsFourStateExpr(a, depth + 1)) return true;
                    return false;
            }
        }

        private int? TryExprWidth(JsonElement expr, int depth = 0)
        {
            if (depth > MaxDepth || expr.ValueKind != JsonValueKind.Object) return null;
            string? kind = TryString(expr, "kind");
            if (kind is null) return null;
            switch (kind)
            {
                case "size":
                case "exists":
                case "countones":
                case "clog2":
                case "test_plusargs":
                case "urandom":
                case "urandom_range":
                case "random":
                    return 32;
                case "convert":
                case "dpi_call":
                case "indexedselect":
                    return TryInt(expr, "width");
                case "ref":
                    return TryString(expr, "name") is { } sn && signals.TryGetValue(sn, out var sig) ? sig.Width : null;
                case "past":
                    return TryString(expr, "signal") is { } ps && signals.TryGetValue(ps, out var psig) ? psig.Width : null;
                case "index":
                    return TryString(expr, "name") is { } mn && memories.TryGetValue(mn, out var mem) ? mem.Width : null;
                case "const":
                    return TryString(expr, "bits") is { Length: > 0 } b ? b.Length : null;
                case "bitselect":
                case "inside":
                case "rose":
                case "fell":
                case "stable":
                    return 1;
                case "partselect":
                {
                    int? l = TryInt(expr, "left"), r = TryInt(expr, "right");
                    return l.HasValue && r.HasValue && l.Value >= r.Value ? l.Value - r.Value + 1 : null;
                }
                case var k when IsScalarOp(k):
                    return 1;
                default:
                {
                    if (!expr.TryGetProperty("args", out var argsEl) || argsEl.ValueKind != JsonValueKind.Array) return null;
                    var widths = new List<int>();
                    foreach (var a in argsEl.EnumerateArray())
                    {
                        int? aw = TryExprWidth(a, depth + 1);
                        if (aw is null) return null;
                        widths.Add(aw.Value);
                    }
                    if (widths.Count == 0) return null;
                    if (kind is "shl" or "shr" or "ashr") return widths[0];
                    if (kind == "cond") return widths.Count >= 3 ? Math.Max(widths[1], widths[2]) : null;
                    if (kind == "concat") return widths.Sum();
                    return widths.Max();
                }
            }
        }

        private bool? TryExprSigned(JsonElement expr, int depth = 0)
        {
            if (depth > MaxDepth || expr.ValueKind != JsonValueKind.Object) return null;
            string? kind = TryString(expr, "kind");
            if (kind is null) return null;
            switch (kind)
            {
                case "size":
                case "exists":
                case "random":
                case "castsigned":
                    return true;
                case "countones":
                case "clog2":
                case "test_plusargs":
                case "urandom":
                case "urandom_range":
                case "resolve":
                case "inside":
                case "rose":
                case "fell":
                case "stable":
                case "castunsigned":
                case "bitselect":
                case "partselect":
                case "indexedselect":
                case "concat":
                    return false;
                case var k when IsScalarOp(k):
                    return false;
                case "convert":
                case "dpi_call":
                case "const":
                    return TryBool(expr, "signed");
                case "ref":
                    return TryString(expr, "name") is { } sn && signals.TryGetValue(sn, out var sig) ? sig.Signed : null;
                case "past":
                    return TryString(expr, "signal") is { } ps && signals.TryGetValue(ps, out var psig) ? psig.Signed : null;
                case "index":
                    return TryString(expr, "name") is { } mn && memories.TryGetValue(mn, out var mem) ? mem.Signed : null;
                default:
                {
                    if (!expr.TryGetProperty("args", out var argsEl) || argsEl.ValueKind != JsonValueKind.Array) return null;
                    var args = argsEl.EnumerateArray().ToArray();
                    if (args.Length == 0) return null;
                    if (kind is "shl" or "shr" or "ashr") return TryExprSigned(args[0], depth + 1);
                    var slice = kind == "cond" ? args.Skip(1) : args;
                    bool allSigned = true;
                    foreach (var a in slice)
                    {
                        bool? s = TryExprSigned(a, depth + 1);
                        if (s is null) return null;
                        allSigned &= s.Value;
                    }
                    return allSigned;
                }
            }
        }

        private void CollectReferences(JsonElement expr, HashSet<string> refs, int depth)
        {
            if (depth > MaxDepth || expr.ValueKind != JsonValueKind.Object) return;
            string? kind = TryString(expr, "kind");
            if (kind is "ref" or "size") { if (TryString(expr, "name") is { } n) refs.Add(n); }
            else if (kind is "past" or "rose" or "fell" or "stable") { if (TryString(expr, "signal") is { } s) refs.Add(s); }
            else if (kind is "index" or "exists")
            {
                if (TryString(expr, "name") is { } m) refs.Add(m);
                foreach (var i in MemoryIndices(expr)) CollectReferences(i, refs, depth + 1);
            }
            else if (kind is "bitselect" or "partselect" or "indexedselect")
            {
                if (expr.TryGetProperty("value", out var v)) CollectReferences(v, refs, depth + 1);
                if (expr.TryGetProperty("index", out var i)) CollectReferences(i, refs, depth + 1);
                if (expr.TryGetProperty("base", out var b)) CollectReferences(b, refs, depth + 1);
            }
            else if (kind == "convert")
            {
                if (expr.TryGetProperty("value", out var v)) CollectReferences(v, refs, depth + 1);
            }
            else if (expr.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in args.EnumerateArray()) CollectReferences(a, refs, depth + 1);
            }
        }

        private void Emit(string code, LintSeverity severity, string message, string? signal, string? context)
        {
            diagnostics.Add(new LintDiagnostic(code, severity, message, "ir", new LintLocation(sourceFile, null, null, signal, context)));
        }

        private static string DescribeTarget(JsonElement target)
        {
            string? kind = TryString(target, "kind");
            return kind switch
            {
                "ref" => TryString(target, "name") ?? "<ref>",
                "index" => (TryString(target, "name") ?? "<mem>") + "[...]",
                "bitselect" => (target.TryGetProperty("value", out var bv) ? DescribeTarget(bv) : "?") + "[bit]",
                "partselect" => (target.TryGetProperty("value", out var pv) ? DescribeTarget(pv) : "?") + $"[{TryInt(target, "left")}:{TryInt(target, "right")}]",
                "indexedselect" => (target.TryGetProperty("value", out var iv) ? DescribeTarget(iv) : "?") + "[+:...]",
                "concat" => "{" + string.Join(", ", TargetLeaves(target).Select(DescribeTarget)) + "}",
                _ => "<target>"
            };
        }

        private static string? TryTargetOwner(JsonElement target)
        {
            string? kind = TryString(target, "kind");
            if (kind is "ref" or "index") return TryString(target, "name");
            if (kind is "bitselect" or "partselect" or "indexedselect" && target.TryGetProperty("value", out var v))
                return TryString(v, "name");
            return null;
        }

        private static IEnumerable<JsonElement> TargetLeaves(JsonElement target)
        {
            if (target.ValueKind != JsonValueKind.Object) yield break;
            if (TryString(target, "kind") == "concat" && target.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in args.EnumerateArray())
                    foreach (var leaf in TargetLeaves(child))
                        yield return leaf;
            }
            else
            {
                yield return target;
            }
        }

        private static IEnumerable<JsonElement> MemoryIndices(JsonElement node)
        {
            if (node.TryGetProperty("index", out var single)) yield return single;
            if (node.TryGetProperty("indices", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray()) yield return item;
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

        private static bool IsScalarOp(string kind) => kind is "and" or "or" or "not" or "eq" or "neq"
            or "caseeq" or "caseneq" or "wildeq" or "wildneq" or "lt" or "le" or "gt" or "ge"
            or "redand" or "rednand" or "redor" or "rednor" or "redxor" or "redxnor"
            or "onehot" or "onehot0" or "isunknown";

        private static bool IsXzConst(JsonElement expr) =>
            TryString(expr, "kind") == "const" &&
            TryString(expr, "bits") is { } bits &&
            bits.IndexOfAny(['x', 'z', 'X', 'Z', '?']) >= 0;

        private static bool TryConstUInt64(JsonElement expr, out ulong value)
        {
            value = 0;
            if (TryString(expr, "kind") != "const") return false;
            if (TryBool(expr, "signed")) return false;
            string? bits = TryString(expr, "bits");
            return bits is not null && TryParseBinary(bits, out value);
        }

        private static bool TryParseBinary(string bits, out ulong value)
        {
            value = 0;
            if (bits.Length == 0 || bits.Length > 64) return false;
            foreach (char c in bits)
            {
                if (c is not ('0' or '1')) return false;
                value = (value << 1) | (c == '1' ? 1UL : 0UL);
            }
            return true;
        }

        private static string NormalizeBits(string bits, int width)
        {
            if (bits.Length == width) return bits;
            if (bits.Length > width) return bits[^width..];
            return new string('0', width - bits.Length) + bits;
        }

        private static bool WildcardMatches(string pattern, ulong state, bool xIsWild)
        {
            int n = pattern.Length;
            for (int i = 0; i < n; i++)
            {
                char p = char.ToLowerInvariant(pattern[i]);
                if (p is 'z' or '?') continue;
                if (xIsWild && p == 'x') continue;
                int bitPos = n - 1 - i;
                bool stateBit = ((state >> bitPos) & 1UL) != 0;
                if (p == '1' && !stateBit) return false;
                if (p == '0' && stateBit) return false;
                if (p == 'x') return false;
            }
            return true;
        }

        private static string? TryString(JsonElement el, string prop) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int? TryInt(JsonElement el, string prop) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;

        private static bool TryBool(JsonElement el, string prop) =>
            el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();
    }
}
