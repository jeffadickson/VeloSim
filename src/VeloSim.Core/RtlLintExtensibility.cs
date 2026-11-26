using System.Text.Json;

namespace VeloSim;

public sealed record RtlLintSignal(
    string Name,
    int Width,
    string Direction,
    bool Signed,
    bool TwoState,
    string? SourcePath,
    bool IsRead,
    bool IsWritten,
    bool IsClockOrEvent);

public sealed record RtlLintMemory(
    string Name,
    int Width,
    int Depth,
    bool Signed,
    int LowerBound,
    bool TwoState,
    bool IsContainer,
    bool IsRead,
    bool IsWritten);

public sealed record RtlLintAssignment(
    string TargetDescription,
    string? TargetOwner,
    int? TargetWidth,
    int? ValueWidth,
    bool? TargetSigned,
    bool? ValueSigned,
    bool IsBlocking,
    bool InClockedProcess,
    string Context,
    JsonElement TargetNode,
    JsonElement ValueNode);

public sealed record RtlLintStatement(
    string Kind,
    string Context,
    int Depth,
    bool InClockedProcess,
    JsonElement Node);

public sealed record RtlLintExpression(
    string Kind,
    string Context,
    int? Width,
    bool? Signed,
    JsonElement Node);

/// <summary>
/// Read-only semantic view of a <c>velosim.rtl.v1</c> design passed to custom <see cref="IRtlLintRule"/>
/// implementations (Tier 2 C# plugins and Tier 3 Cameo DSL compiled rules).
/// </summary>
public sealed class RtlLintContext
{
    private readonly Action<LintDiagnostic> sink;

    internal RtlLintContext(
        string? sourceFile,
        string? clock,
        bool isTimed,
        JsonElement root,
        IReadOnlyList<RtlLintSignal> signals,
        IReadOnlyList<RtlLintMemory> memories,
        IReadOnlyList<RtlLintAssignment> assignments,
        IReadOnlyList<RtlLintStatement> statements,
        IReadOnlyList<RtlLintExpression> expressions,
        LintRuleParameters parameters,
        Action<LintDiagnostic> sink)
    {
        SourceFile = sourceFile;
        Clock = clock;
        IsTimed = isTimed;
        Root = root;
        Signals = signals;
        Memories = memories;
        Assignments = assignments;
        Statements = statements;
        Expressions = expressions;
        Parameters = parameters;
        this.sink = sink;
    }

    public string? SourceFile { get; }
    public string? Clock { get; }
    public bool IsTimed { get; }
    public JsonElement Root { get; }
    public IReadOnlyList<RtlLintSignal> Signals { get; }
    public IReadOnlyList<RtlLintMemory> Memories { get; }
    public IReadOnlyList<RtlLintAssignment> Assignments { get; }
    public IReadOnlyList<RtlLintStatement> Statements { get; }
    public IReadOnlyList<RtlLintExpression> Expressions { get; }
    public LintRuleParameters Parameters { get; }

    public void Report(
        string code,
        LintSeverity severity,
        string message,
        string? signal = null,
        string? context = null,
        int? line = null,
        int? column = null,
        string source = "custom")
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Diagnostic code is required.", nameof(code));
        sink(new LintDiagnostic(
            code.Trim().ToUpperInvariant(),
            severity,
            message,
            source,
            new LintLocation(SourceFile, line, column, signal, context)));
    }

    public void Report(
        LintRuleInfo rule,
        string message,
        string? signal = null,
        string? context = null,
        int? line = null,
        int? column = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        Report(rule.Code, rule.DefaultSeverity, message, signal, context, line, column);
    }
}

/// <summary>
/// Extensibility contract for custom VeloSim lint rules (Tier 2 C# plugins and Tier 3 Cameo DSL rules).
/// Rules registered via <see cref="LintOptions.CustomRules"/> automatically participate in profiles,
/// <c>velosim-lint.json</c> severity overrides, scoped waivers, baselines, JSON reports, and SARIF 2.1.0 output.
/// </summary>
public interface IRtlLintRule
{
    LintRuleInfo Info { get; }
    void Analyze(RtlLintContext context);
}

public sealed class DelegateRtlLintRule(LintRuleInfo info, Action<RtlLintContext> analyzer) : IRtlLintRule
{
    public LintRuleInfo Info { get; } = info ?? throw new ArgumentNullException(nameof(info));
    private readonly Action<RtlLintContext> analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));

    public void Analyze(RtlLintContext context) => analyzer(context);
}

/// <summary>
/// Fluent registry for composing custom <see cref="IRtlLintRule"/> packs (e.g., team signoff rules
/// or compiled Cameo DSL rule sets).
/// </summary>
public sealed class RtlLintRuleRegistry
{
    private readonly List<IRtlLintRule> rules = [];

    public IReadOnlyList<IRtlLintRule> Rules => rules.AsReadOnly();

    public RtlLintRuleRegistry Add(IRtlLintRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rules.Any(r => r.Info.Code.Equals(rule.Info.Code, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Duplicate custom lint rule code: '{rule.Info.Code}'.", nameof(rule));
        rules.Add(rule);
        return this;
    }

    public RtlLintRuleRegistry Add(
        string code,
        string summary,
        Action<RtlLintContext> analyzer,
        LintSeverity defaultSeverity = LintSeverity.Warning,
        bool defaultEnabled = true,
        string group = "CUSTOM",
        string hint = "")
    {
        var info = new LintRuleInfo(code.Trim().ToUpperInvariant(), defaultEnabled, defaultSeverity, group, summary, hint);
        return Add(new DelegateRtlLintRule(info, analyzer));
    }
}
