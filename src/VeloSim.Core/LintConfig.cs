using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VeloSim;

public enum LintProfile
{
    Default = 0,
    Wall = 1,
    Signoff = 2,
    FourState = 3,
    Do254 = 4,
    Starc = 5,
    Iso26262 = 6,
    LowRisc = 7
}

/// <summary>
/// Phase-Based Linting (PBL) execution order (inspired by Aldec ALINT-PRO):
/// fix root-cause RTL structural issues before downstream FSM, CDC/RDC, and coding-style checks.
/// </summary>
public enum LintPhase
{
    RtlSynthesis = 0,
    Fsm = 1,
    ClockResetCdc = 2,
    SyntaxStyle = 3
}

public enum LintGroupBy
{
    None = 0,
    Rule = 1,
    Signal = 2,
    File = 3,
    Phase = 4
}

public static class LintPhases
{
    public static IReadOnlyList<LintPhase> Ordered { get; } =
        [LintPhase.RtlSynthesis, LintPhase.Fsm, LintPhase.ClockResetCdc, LintPhase.SyntaxStyle];

    public static LintPhase Parse(string text) => text.Trim().ToLowerInvariant() switch
    {
        "rtl" or "synthesis" or "rtl-synthesis" or "structural" => LintPhase.RtlSynthesis,
        "fsm" => LintPhase.Fsm,
        "cdc" or "rdc" or "clock" or "clock-reset-cdc" or "dft" => LintPhase.ClockResetCdc,
        "style" or "syntax" or "syntax-style" or "waiver" => LintPhase.SyntaxStyle,
        _ => throw new ArgumentException($"Unknown lint phase '{text}' (expected: rtl, fsm, cdc, style, all).")
    };

    public static IReadOnlySet<LintPhase> ParseList(string text)
    {
        var set = new HashSet<LintPhase>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = part.Trim();
            if (trimmed.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var p in Ordered) set.Add(p);
                continue;
            }
            set.Add(Parse(trimmed));
        }
        if (set.Count == 0)
            throw new ArgumentException("At least one lint phase is required (rtl, fsm, cdc, style, all).");
        return set;
    }

    public static string ToName(LintPhase phase) => phase switch
    {
        LintPhase.RtlSynthesis => "rtl",
        LintPhase.Fsm => "fsm",
        LintPhase.ClockResetCdc => "cdc",
        _ => "style"
    };
}

public sealed record LintQualityMetrics(double Score, string Grade, int DesignElements)
{
    public static LintQualityMetrics Compute(int errors, int warnings, int waived, int designElements)
    {
        int scale = Math.Max(10, designElements);
        double errPenalty = errors * 15.0;
        double warnPenalty = (warnings / (double)scale) * 25.0 + warnings * 1.5;
        double waiverPenalty = waived * 0.5;
        double raw = Math.Max(0.0, 100.0 - errPenalty - warnPenalty - waiverPenalty);
        double score = Math.Round(raw, 1, MidpointRounding.AwayFromZero);
        string grade = score switch
        {
            >= 98.0 when errors == 0 => "A+",
            >= 90.0 when errors == 0 => "A",
            >= 80.0 => "B",
            >= 70.0 => "C",
            >= 60.0 => "D",
            _ => "F"
        };
        return new LintQualityMetrics(score, grade, designElements);
    }
}

public enum LintRuleAction
{
    Off = 0,
    Info = 1,
    Warning = 2,
    Error = 3
}

public sealed record LintNamingConfig(
    string? SignalPattern = null,
    string? InputPattern = null,
    string? OutputPattern = null,
    string? InternalPattern = null,
    string? MemoryPattern = null,
    string? ClockPattern = null,
    string? ResetPattern = null)
{
    public bool HasAnyPattern =>
        !string.IsNullOrWhiteSpace(SignalPattern) ||
        !string.IsNullOrWhiteSpace(InputPattern) ||
        !string.IsNullOrWhiteSpace(OutputPattern) ||
        !string.IsNullOrWhiteSpace(InternalPattern) ||
        !string.IsNullOrWhiteSpace(MemoryPattern) ||
        !string.IsNullOrWhiteSpace(ClockPattern) ||
        !string.IsNullOrWhiteSpace(ResetPattern);

    public void Validate()
    {
        ValidatePattern(SignalPattern, "naming.signal_pattern");
        ValidatePattern(InputPattern, "naming.input_pattern");
        ValidatePattern(OutputPattern, "naming.output_pattern");
        ValidatePattern(InternalPattern, "naming.internal_pattern");
        ValidatePattern(MemoryPattern, "naming.memory_pattern");
        ValidatePattern(ClockPattern, "naming.clock_pattern");
        ValidatePattern(ResetPattern, "naming.reset_pattern");
    }

    private static void ValidatePattern(string? pattern, string field)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return;
        try { _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
        catch (ArgumentException ex) { throw new ArgumentException($"Invalid regex in {field}: {ex.Message}", ex); }
    }
}

public sealed record LintRuleParameters(
    int MaxIfDepth = 8,
    IReadOnlyList<string>? UnusedIgnorePatterns = null,
    bool AllowLiteralWidthExpand = false,
    int? MaxSignalWidth = null,
    int? MaxMemoryDepth = null,
    LintNamingConfig? Naming = null)
{
    public static LintRuleParameters Default { get; } = new();

    public void Validate()
    {
        if (MaxIfDepth is < 1 or > 256)
            throw new ArgumentException("params.max_if_depth must be between 1 and 256.");
        if (MaxSignalWidth is <= 0 or > 4096)
            throw new ArgumentException("params.max_signal_width must be between 1 and 4096.");
        if (MaxMemoryDepth is <= 0 or > 16_777_216)
            throw new ArgumentException("params.max_memory_depth must be between 1 and 16777216.");
        Naming?.Validate();
    }
}

public sealed record LintWaiver(
    string? Id,
    string Code,
    string? File = null,
    string? Signal = null,
    string? Context = null,
    string? Message = null,
    string? Reason = null,
    string? Owner = null,
    string? Expires = null)
{
    public string DisplayId => !string.IsNullOrWhiteSpace(Id) ? Id : $"{Code}({Signal ?? File ?? "*"})";

    public bool Matches(LintDiagnostic diagnostic)
    {
        if (!LintGlob.IsMatch(Code, diagnostic.Code, ignoreCase: true))
            return false;

        if (!string.IsNullOrWhiteSpace(File))
        {
            string diagFile = (diagnostic.Location?.File ?? "").Replace('\\', '/');
            string patFile = File.Replace('\\', '/');
            if (!LintGlob.IsMatch(patFile, diagFile, ignoreCase: OperatingSystem.IsWindows()) &&
                !LintGlob.IsMatch(patFile, Path.GetFileName(diagFile), ignoreCase: OperatingSystem.IsWindows()))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(Signal))
        {
            string diagSig = diagnostic.Location?.Signal ?? "";
            if (diagSig.Length == 0 || !LintGlob.IsMatch(Signal, diagSig, ignoreCase: false))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(Context))
        {
            string diagCtx = diagnostic.Location?.Context ?? "";
            if (diagCtx.Length == 0 || !LintGlob.IsMatch(Context, diagCtx, ignoreCase: true))
                return false;
        }

        if (!string.IsNullOrWhiteSpace(Message))
        {
            if (!LintGlob.IsMatch(Message, diagnostic.Message, ignoreCase: true))
                return false;
        }

        return true;
    }

    public bool IsExpired(DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(Expires)) return false;
        return DateOnly.TryParseExact(Expires, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exp) && exp < today;
    }
}

public static class LintProfiles
{
    private static readonly HashSet<string> SignoffErrorRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "LATCH", "UNOPTFLAT", "MULTIDRIVEN", "BLKANDNBLK", "CLKCOMB", "ASSIGNIN",
        "DIVZERO", "INFLOOP", "WIDTHTRUNC", "BLKSEQ", "CASEX", "CASEWITHX",
        "CASEINCOMPLETE", "SYNCASYNCNET", "RESETCONSIST", "CDCUNSYNC", "RDCUNSYNC"
    };

    private static readonly HashSet<string> FourStateErrorRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "FOURSTATE", "CASEX", "CASEWITHX", "RACEWW", "RACERW", "UNDRIVEN", "DIVZERO"
    };

    // Aldec ALINT-PRO DO-254 / NASA Class B-D aerospace safety compliance pack
    private static readonly HashSet<string> Do254ErrorRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "LATCH", "UNOPTFLAT", "MULTIDRIVEN", "BLKANDNBLK", "CLKCOMB", "ASSIGNIN",
        "INFLOOP", "DIVZERO", "SHIFTOVF", "UNDRIVEN", "WIDTHTRUNC", "BLKSEQ",
        "CASEINCOMPLETE", "CASEOVERLAP", "CASEX", "CASEWITHX", "SELRANGE",
        "SYNCASYNCNET", "RESETCONSIST", "FSMUNREACH", "FSMDEADLOCK", "FSMNORESET",
        "FSMRECOVERY", "CDCUNSYNC", "RDCUNSYNC", "DFTCLKCTRL", "DFTRSTCTRL"
    };

    // Aldec ALINT-PRO / Questa STARC ASIC design guideline compliance pack
    private static readonly HashSet<string> StarcErrorRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "LATCH", "UNOPTFLAT", "MULTIDRIVEN", "BLKANDNBLK", "CLKCOMB", "ASSIGNIN",
        "INFLOOP", "DIVZERO", "WIDTHTRUNC", "BLKSEQ", "CASEINCOMPLETE", "CASEOVERLAP",
        "CASEX", "CASEWITHX", "SYNCASYNCNET", "RESETCONSIST", "FSMUNREACH",
        "FSMDEADLOCK", "FSMRECOVERY", "CDCUNSYNC", "DFTCLKCTRL"
    };

    // Questa / Ascent ISO-26262 automotive functional-safety compliance pack
    private static readonly HashSet<string> Iso26262ErrorRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "LATCH", "UNOPTFLAT", "MULTIDRIVEN", "BLKANDNBLK", "CLKCOMB", "ASSIGNIN",
        "INFLOOP", "DIVZERO", "SHIFTOVF", "UNDRIVEN", "WIDTHTRUNC", "BLKSEQ",
        "CASEINCOMPLETE", "CASEOVERLAP", "CASEX", "CASEWITHX", "SELRANGE",
        "FOURSTATE", "RACEWW", "RACERW", "SYNCASYNCNET", "RESETCONSIST",
        "FSMUNREACH", "FSMDEADLOCK", "FSMNORESET", "FSMRECOVERY", "CDCUNSYNC", "RDCUNSYNC"
    };

    // Real Intent Ascent Lint / OpenTitan lowRISC Verilog Coding Style compliance pack
    private static readonly HashSet<string> LowRiscErrorRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "LATCH", "UNOPTFLAT", "MULTIDRIVEN", "BLKANDNBLK", "CLKCOMB", "ASSIGNIN",
        "INFLOOP", "DIVZERO", "WIDTHTRUNC", "BLKSEQ", "CASEINCOMPLETE", "CASEOVERLAP",
        "CASEX", "CASEWITHX", "SIGNCOMPARE", "FSMRECOVERY", "CDCUNSYNC", "RDCUNSYNC"
    };

    public static LintProfile Parse(string text) => text.Trim().ToLowerInvariant() switch
    {
        "default" => LintProfile.Default,
        "wall" or "all" => LintProfile.Wall,
        "signoff" or "synthesis" or "rtl-signoff" => LintProfile.Signoff,
        "fourstate" or "four-state" or "4state" => LintProfile.FourState,
        "do254" or "do-254" or "avionic" => LintProfile.Do254,
        "starc" or "starc-rtl" => LintProfile.Starc,
        "iso26262" or "iso-26262" or "automotive" or "asil" => LintProfile.Iso26262,
        "lowrisc" or "low-risc" or "opentitan" => LintProfile.LowRisc,
        _ => throw new ArgumentException($"Unknown lint profile '{text}' (expected: default, wall, signoff, fourstate, do254, starc, iso26262, lowrisc).")
    };

    public static string ToName(LintProfile profile) => profile switch
    {
        LintProfile.Wall => "wall",
        LintProfile.Signoff => "signoff",
        LintProfile.FourState => "fourstate",
        LintProfile.Do254 => "do254",
        LintProfile.Starc => "starc",
        LintProfile.Iso26262 => "iso26262",
        LintProfile.LowRisc => "lowrisc",
        _ => "default"
    };

    public static bool EnablesAllWarnings(LintProfile profile) => profile != LintProfile.Default;

    public static bool PromotesToError(LintProfile profile, string code) => profile switch
    {
        LintProfile.Signoff => SignoffErrorRules.Contains(code),
        LintProfile.FourState => FourStateErrorRules.Contains(code),
        LintProfile.Do254 => Do254ErrorRules.Contains(code),
        LintProfile.Starc => StarcErrorRules.Contains(code),
        LintProfile.Iso26262 => Iso26262ErrorRules.Contains(code),
        LintProfile.LowRisc => LowRiscErrorRules.Contains(code),
        _ => false
    };
}

/// <summary>
/// Project-level lint configuration (<c>velosim-lint.json</c>, schema <c>velosim.lint.config.v1</c>):
/// named compliance profiles, Phase-Based Linting, SDC constraints, per-rule severities and
/// guideline annotations, parameterized rule settings, and scoped audit waivers.
/// </summary>
public sealed record LintConfig(
    LintProfile Profile = LintProfile.Default,
    bool WarningsAsErrors = false,
    bool WarnUnusedWaivers = false,
    string? SlangWaiverFile = null,
    string? SdcFile = null,
    IReadOnlySet<LintPhase>? Phases = null,
    bool StopOnPhaseError = false,
    IReadOnlyDictionary<string, LintRuleAction>? Rules = null,
    IReadOnlyDictionary<string, string>? Annotations = null,
    LintRuleParameters? Parameters = null,
    IReadOnlyList<LintWaiver>? Waivers = null,
    IReadOnlyList<string>? RuleScripts = null,
    long? CameoMaxSteps = null,
    IReadOnlyDictionary<string, string>? CameoParams = null)
{
    public const string SchemaVersion = "velosim.lint.config.v1";
    public const string DefaultFileName = "velosim-lint.json";

    public static LintConfig Empty { get; } = new();

    public static LintConfig Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        LintConfigContract contract;
        try
        {
            contract = JsonSerializer.Deserialize(json, LintJsonContext.Default.LintConfigContract)
                ?? throw new ArgumentException("Lint config JSON cannot be null.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Malformed lint config JSON: " + ex.Message, ex);
        }

        if (contract.Schema is not null && contract.Schema != SchemaVersion)
            throw new ArgumentException($"Unsupported lint config schema '{contract.Schema}' (expected '{SchemaVersion}').");

        var profile = contract.Profile is not null ? LintProfiles.Parse(contract.Profile) : LintProfile.Default;

        HashSet<LintPhase>? phases = null;
        if (contract.Phases is { Length: > 0 })
        {
            phases = [];
            foreach (string p in contract.Phases)
            {
                if (p.Equals("all", StringComparison.OrdinalIgnoreCase))
                    foreach (var ap in LintPhases.Ordered) phases.Add(ap);
                else
                    phases.Add(LintPhases.Parse(p));
            }
        }

        Dictionary<string, LintRuleAction>? ruleMap = null;
        if (contract.Rules is { Count: > 0 })
        {
            ruleMap = new Dictionary<string, LintRuleAction>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, val) in contract.Rules)
            {
                if (string.IsNullOrWhiteSpace(key))
                    throw new ArgumentException("Lint config rule key cannot be empty.");
                var action = ParseAction(val);
                foreach (string expanded in LintRuleCatalog.ExpandRuleOrGroup(key))
                    ruleMap[expanded] = action;
            }
        }

        Dictionary<string, string>? annotations = null;
        if (contract.Annotations is { Count: > 0 })
        {
            annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, val) in contract.Annotations)
            {
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(val))
                    annotations[key.Trim().ToUpperInvariant()] = val.Trim();
            }
        }

        LintRuleParameters? parameters = null;
        if (contract.Params is { } pr)
        {
            LintNamingConfig? naming = pr.Naming is { } n
                ? new LintNamingConfig(n.SignalPattern, n.InputPattern, n.OutputPattern, n.InternalPattern, n.MemoryPattern, n.ClockPattern, n.ResetPattern)
                : null;
            parameters = new LintRuleParameters(
                MaxIfDepth: pr.MaxIfDepth ?? LintRuleParameters.Default.MaxIfDepth,
                UnusedIgnorePatterns: pr.UnusedIgnorePatterns,
                AllowLiteralWidthExpand: pr.AllowLiteralWidthExpand ?? false,
                MaxSignalWidth: pr.MaxSignalWidth,
                MaxMemoryDepth: pr.MaxMemoryDepth,
                Naming: naming);
            parameters.Validate();
        }

        List<LintWaiver>? waivers = null;
        if (contract.Waivers is { Length: > 0 })
        {
            waivers = new List<LintWaiver>(contract.Waivers.Length);
            foreach (var w in contract.Waivers)
            {
                if (string.IsNullOrWhiteSpace(w.Code))
                    throw new ArgumentException("Each lint waiver requires a non-empty 'code' field.");
                if (!string.IsNullOrWhiteSpace(w.Expires) &&
                    !DateOnly.TryParseExact(w.Expires, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw new ArgumentException($"Waiver '{w.Id ?? w.Code}' has invalid 'expires' date '{w.Expires}' (expected YYYY-MM-DD).");
                waivers.Add(new LintWaiver(w.Id, w.Code.Trim(), w.File, w.Signal, w.Context, w.Message, w.Reason, w.Owner, w.Expires));
            }
        }

        if (contract.CameoMaxSteps is <= 0)
            throw new ArgumentException("cameo_max_steps must be positive.");

        IReadOnlyList<string>? ruleScripts = contract.RuleScripts is { Length: > 0 }
            ? contract.RuleScripts.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList().AsReadOnly()
            : null;

        Dictionary<string, string>? cameoParams = null;
        if (contract.CameoParams is { Count: > 0 })
        {
            cameoParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in contract.CameoParams)
                if (!string.IsNullOrWhiteSpace(k)) cameoParams[k.Trim()] = v ?? "";
        }

        return new LintConfig(
            Profile: profile,
            WarningsAsErrors: contract.WarningsAsErrors ?? false,
            WarnUnusedWaivers: contract.WarnUnusedWaivers ?? false,
            SlangWaiverFile: contract.SlangWaiverFile,
            SdcFile: contract.SdcFile,
            Phases: phases,
            StopOnPhaseError: contract.StopOnPhaseError ?? false,
            Rules: ruleMap,
            Annotations: annotations,
            Parameters: parameters,
            Waivers: waivers?.AsReadOnly(),
            RuleScripts: ruleScripts,
            CameoMaxSteps: contract.CameoMaxSteps,
            CameoParams: cameoParams);
    }

    public static LintConfig LoadFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Lint configuration file not found: " + path, path);
        var cfg = Parse(File.ReadAllText(path));
        string? baseDir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(baseDir))
        {
            string Resolve(string p) => Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(baseDir, p));
            cfg = cfg with
            {
                SdcFile = cfg.SdcFile is { Length: > 0 } ? Resolve(cfg.SdcFile) : null,
                SlangWaiverFile = cfg.SlangWaiverFile is { Length: > 0 } ? Resolve(cfg.SlangWaiverFile) : null,
                RuleScripts = cfg.RuleScripts is { Count: > 0 } ? cfg.RuleScripts.Select(Resolve).ToList().AsReadOnly() : null
            };
        }
        return cfg;
    }

    public static string? Discover(string? targetFile, string? workingDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(targetFile))
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(targetFile));
            if (!string.IsNullOrEmpty(dir))
            {
                string candidate = Path.Combine(dir, DefaultFileName);
                if (File.Exists(candidate)) return candidate;
            }
        }
        string cwd = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory;
        string cwdCandidate = Path.Combine(cwd, DefaultFileName);
        return File.Exists(cwdCandidate) ? cwdCandidate : null;
    }

    public static string DefaultTemplateJson()
    {
        var template = new LintConfigContract(
            Schema: SchemaVersion,
            Profile: "wall",
            WarningsAsErrors: false,
            WarnUnusedWaivers: true,
            SlangWaiverFile: null,
            SdcFile: null,
            Phases: null,
            StopOnPhaseError: false,
            Rules: new Dictionary<string, string>
            {
                ["WIDTHEXPAND"] = "warning",
                ["BLKSEQ"] = "error",
                ["CASEX"] = "error"
            },
            Annotations: new Dictionary<string, string>
            {
                ["LATCH"] = "DO-254-COMB-01",
                ["CDCUNSYNC"] = "DO-254-CDC-01"
            },
            Params: new LintRuleParamsContract(
                MaxIfDepth: 8,
                UnusedIgnorePatterns: ["_*", "unused_*", "dbg_*"],
                AllowLiteralWidthExpand: false,
                MaxSignalWidth: null,
                MaxMemoryDepth: null,
                Naming: new LintNamingConfigContract(
                    SignalPattern: null,
                    InputPattern: null,
                    OutputPattern: null,
                    InternalPattern: null,
                    MemoryPattern: null,
                    ClockPattern: null,
                    ResetPattern: null)),
            Waivers:
            [
                new LintWaiverContract(
                    Id: "W-EXAMPLE-001",
                    Code: "UNUSEDSIGNAL",
                    File: "vendor/**",
                    Signal: "dbg_*",
                    Context: null,
                    Message: null,
                    Reason: "Example scoped waiver for vendor debug signals",
                    Owner: "rtl-team",
                    Expires: null)
            ]);
        return JsonSerializer.Serialize(template, LintJsonContext.Default.LintConfigContract);
    }

    private static LintRuleAction ParseAction(string text) => text.Trim().ToLowerInvariant() switch
    {
        "off" or "disable" or "disabled" or "ignore" or "none" => LintRuleAction.Off,
        "info" or "note" => LintRuleAction.Info,
        "warn" or "warning" => LintRuleAction.Warning,
        "err" or "error" or "fatal" => LintRuleAction.Error,
        _ => throw new ArgumentException($"Unknown lint rule severity '{text}' (expected: off, info, warning, error).")
    };
}

/// <summary>
/// Deterministic baseline file (<c>velosim.lint.baseline.v1</c>) enabling incremental adoption
/// of strict <c>-Wall -Werror</c> policies on existing RTL codebases without hiding newly introduced defects.
/// </summary>
public sealed record LintBaseline(IReadOnlySet<string> Fingerprints, IReadOnlyList<LintBaselineEntry> Entries)
{
    public const string SchemaVersion = "velosim.lint.baseline.v1";

    public bool Contains(LintDiagnostic diagnostic) => Fingerprints.Contains(ComputeFingerprint(diagnostic));

    public static string ComputeFingerprint(LintDiagnostic diagnostic)
    {
        string file = (diagnostic.Location?.File ?? "").Replace('\\', '/');
        if (file.Length > 0) file = Path.GetFileName(file);
        string payload = $"{diagnostic.Code.ToUpperInvariant()}|{diagnostic.Source}|{file}|{diagnostic.Location?.Signal ?? ""}|{diagnostic.Location?.Context ?? ""}|{diagnostic.Message}";
        // Deterministic 64-bit FNV-1a hash
        ulong hash = 14695981039346656037UL;
        foreach (byte b in Encoding.UTF8.GetBytes(payload))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    public static LintBaseline FromReport(LintReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var entries = new List<LintBaselineEntry>();
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in report.Diagnostics)
        {
            string fp = ComputeFingerprint(d);
            if (set.Add(fp))
            {
                string? file = d.Location?.File is { Length: > 0 } f ? Path.GetFileName(f.Replace('\\', '/')) : null;
                entries.Add(new LintBaselineEntry(fp, d.Code, d.Source, file, d.Location?.Signal, d.Location?.Context, d.Message));
            }
        }
        return new LintBaseline(set, entries.AsReadOnly());
    }

    public static LintBaseline Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        LintBaselineContract contract;
        try
        {
            contract = JsonSerializer.Deserialize(json, LintJsonContext.Default.LintBaselineContract)
                ?? throw new ArgumentException("Lint baseline JSON cannot be null.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Malformed lint baseline JSON: " + ex.Message, ex);
        }

        if (contract.Schema != SchemaVersion)
            throw new ArgumentException($"Unsupported lint baseline schema '{contract.Schema}' (expected '{SchemaVersion}').");

        var set = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<LintBaselineEntry>();
        foreach (var e in contract.Entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(e.Fingerprint)) continue;
            set.Add(e.Fingerprint);
            entries.Add(new LintBaselineEntry(e.Fingerprint, e.Code, e.Source, e.File, e.Signal, e.Context, e.Message));
        }
        return new LintBaseline(set, entries.AsReadOnly());
    }

    public static LintBaseline LoadFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Lint baseline file not found: " + path, path);
        return Parse(File.ReadAllText(path));
    }

    public string ToJson()
    {
        var contract = new LintBaselineContract(
            SchemaVersion,
            Entries.Count,
            Entries.Select(e => new LintBaselineEntryContract(e.Fingerprint, e.Code, e.Source, e.File, e.Signal, e.Context, e.Message)).ToArray());
        return JsonSerializer.Serialize(contract, LintJsonContext.Default.LintBaselineContract);
    }
}

public sealed record LintBaselineEntry(string Fingerprint, string Code, string Source, string? File, string? Signal, string? Context, string Message);

/// <summary>
/// Linear-time, ReDoS-safe wildcard/glob matcher supporting <c>*</c>, <c>?</c>, and <c>**</c>.
/// </summary>
public static class LintGlob
{
    public static bool IsMatch(string pattern, string text, bool ignoreCase = false)
    {
        if (string.IsNullOrEmpty(pattern)) return string.IsNullOrEmpty(text);
        if (pattern == "*" || pattern == "**") return true;

        // Collapse consecutive '*' (including '**') since path separators are matched across '**'.
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        int p = 0, t = 0, starP = -1, starT = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                bool doubleStar = p + 1 < pattern.Length && pattern[p + 1] == '*';
                while (p < pattern.Length && pattern[p] == '*') p++;
                // Remember star position (double-star crosses '/', single-star also matches freely unless needed)
                starP = doubleStar ? -(p) : p;
                starT = t;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || CharEquals(pattern[p], text[t], cmp)))
            {
                p++;
                t++;
            }
            else if (starP != -1)
            {
                bool allowSlash = starP < 0;
                int resumeP = Math.Abs(starP);
                if (!allowSlash && text[starT] == '/')
                    return false;
                starT++;
                t = starT;
                p = resumeP;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static bool CharEquals(char a, char b, StringComparison cmp) =>
        cmp == StringComparison.OrdinalIgnoreCase
            ? char.ToUpperInvariant(a) == char.ToUpperInvariant(b)
            : a == b;
}
