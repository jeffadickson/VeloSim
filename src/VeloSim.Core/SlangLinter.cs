using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VeloSim;

/// <summary>
/// SystemVerilog source linter backed by <c>MikePopoloski/slang</c> (<c>--lint-only</c> / <c>-Weverything</c>),
/// optionally followed by VeloSim IR lowering and <see cref="RtlLinter"/> analysis.
/// </summary>
public sealed partial class SlangLinter(IProcessRunner? runner = null)
{
    private readonly IProcessRunner runner = runner ?? new ProcessRunner();

    private static readonly Dictionary<string, string[]> VerilatorToSlangWarnings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WIDTHTRUNC"] = ["width-trunc"],
        ["WIDTHEXPAND"] = ["width-expand"],
        ["WIDTH"] = ["width-trunc", "width-expand"],
        ["UNUSEDSIGNAL"] = ["unused-variable", "unused-net", "unused-port"],
        ["UNUSEDPARAM"] = ["unused-parameter"],
        ["UNUSED"] = ["unused"],
        ["UNDRIVEN"] = ["unassigned-variable", "undriven-port"],
        ["LATCH"] = ["inferred-latch"],
        ["CASEINCOMPLETE"] = ["case-incomplete"],
        ["CASEOVERLAP"] = ["case-duplicate"],
        ["IMPLICIT"] = ["implicit-net"],
        ["SIGNCOMPARE"] = ["sign-conversion"],
        ["CMPCONST"] = ["constant-comparison"],
        ["SELRANGE"] = ["range-oob"],
        ["MULTIDRIVEN"] = ["multiple-cont-assigns"],
        ["BLKSEQ"] = ["blocking-in-ff"],
        ["CASE"] = ["case-incomplete", "case-duplicate"]
    };

    private static readonly Dictionary<string, string> SlangToVerilatorCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["width-trunc"] = "WIDTHTRUNC",
        ["width-expand"] = "WIDTHEXPAND",
        ["unused-variable"] = "UNUSEDSIGNAL",
        ["unused-net"] = "UNUSEDSIGNAL",
        ["unused-port"] = "UNUSEDSIGNAL",
        ["unused-but-set-variable"] = "UNUSEDSIGNAL",
        ["unused-parameter"] = "UNUSEDPARAM",
        ["unassigned-variable"] = "UNDRIVEN",
        ["undriven-port"] = "UNDRIVEN",
        ["inferred-latch"] = "LATCH",
        ["case-incomplete"] = "CASEINCOMPLETE",
        ["case-duplicate"] = "CASEOVERLAP",
        ["implicit-net"] = "IMPLICIT",
        ["sign-conversion"] = "SIGNCOMPARE",
        ["range-oob"] = "SELRANGE",
        ["multiple-cont-assigns"] = "MULTIDRIVEN"
    };

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiEscapeRegex();

    // Matches POSIX or Windows paths followed by :line:col: severity: message [optional [-Wflag]]
    [GeneratedRegex(@"^(?<file>(?:[A-Za-z]:)?[^:\r\n]+):(?<line>\d+):(?<col>\d+):\s*(?<sev>fatal error|error|warning|note):\s*(?<msg>.+?)(?:\s+\[-W(?<flag>[^\]]+)\])?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlangDiagLineRegex();

    public static IReadOnlyList<string> BuildSlangArguments(
        IReadOnlyList<string> sourceFiles,
        LintOptions options,
        string? top = null,
        string? astJsonPath = null)
    {
        ArgumentNullException.ThrowIfNull(sourceFiles);
        ArgumentNullException.ThrowIfNull(options);
        if (sourceFiles.Count == 0 || sourceFiles.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one SystemVerilog source file is required.", nameof(sourceFiles));

        var args = new List<string> { "--std", "1800-2023" };

        if (astJsonPath is not null)
        {
            args.Add("--ast-json");
            args.Add(astJsonPath);
        }
        else
        {
            args.Add("--lint-only");
        }

        if (!string.IsNullOrWhiteSpace(top))
        {
            args.Add("--top");
            args.Add(top);
        }

        if (options.EffectiveWall)
            args.Add("-Weverything");

        if (options.EffectiveWarningsAsErrors)
            args.Add("-Werror");

        if (options.Config?.Rules is { Count: > 0 } cfgRules)
        {
            foreach (var (rule, action) in cfgRules)
            {
                foreach (string slangFlag in MapToSlangFlags(rule))
                {
                    switch (action)
                    {
                        case LintRuleAction.Off: args.Add("-Wno-" + slangFlag); break;
                        case LintRuleAction.Error when !options.NoFatal: args.Add("-Werror=" + slangFlag); break;
                        case LintRuleAction.Warning: args.Add("-W" + slangFlag); break;
                    }
                }
            }
        }

        if (options.EnabledRules is not null)
        {
            foreach (string rule in options.EnabledRules)
                foreach (string slangFlag in MapToSlangFlags(rule))
                    args.Add("-W" + slangFlag);
        }

        if (options.DisabledRules is not null)
        {
            foreach (string rule in options.DisabledRules)
                foreach (string slangFlag in MapToSlangFlags(rule))
                    args.Add("-Wno-" + slangFlag);
        }

        if (options.ErrorRules is not null && !options.NoFatal)
        {
            foreach (string rule in options.ErrorRules)
                foreach (string slangFlag in MapToSlangFlags(rule))
                    args.Add("-Werror=" + slangFlag);
        }

        if (!string.IsNullOrWhiteSpace(options.Config?.SlangWaiverFile))
        {
            args.Add("--waiver-file");
            args.Add(Path.GetFullPath(options.Config.SlangWaiverFile));
            if (options.EffectiveWarnUnusedWaivers)
                args.Add("--warn-unused-waivers");
        }

        if (options.IncludeDirectories is not null)
        {
            foreach (string dir in options.IncludeDirectories)
            {
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    args.Add("-I");
                    args.Add(Path.GetFullPath(dir));
                }
            }
        }

        if (options.Defines is not null)
        {
            foreach (string def in options.Defines)
            {
                if (!string.IsNullOrWhiteSpace(def))
                {
                    args.Add("-D");
                    args.Add(def);
                }
            }
        }

        foreach (string src in sourceFiles)
            args.Add(Path.GetFullPath(src));

        return args;
    }

    public async Task<LintReport> LintSystemVerilogAsync(
        IReadOnlyList<string> sourceFiles,
        LintOptions? options = null,
        string? top = null,
        string? slangExecutable = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? LintOptions.Default;
        if (sourceFiles.Count == 0 || sourceFiles.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one SystemVerilog source file is required.", nameof(sourceFiles));

        string slang = SlangAstLowerer.ResolveSlangExecutable(slangExecutable);
        bool tryIrLower = !opts.SlangOnly && !string.IsNullOrWhiteSpace(top);
        string workDir = Path.Combine(Path.GetTempPath(), "velosim-lint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            string? astJsonPath = tryIrLower ? Path.Combine(workDir, "ast.json") : null;
            var args = BuildSlangArguments(sourceFiles, opts, top, astJsonPath);

            var result = await runner.RunAsync(
                slang,
                args,
                workDir,
                timeout ?? TimeSpan.FromMinutes(2),
                cancellationToken).ConfigureAwait(false);

            var combinedOutput = string.IsNullOrEmpty(result.StandardError)
                ? result.StandardOutput
                : string.IsNullOrEmpty(result.StandardOutput)
                    ? result.StandardError
                    : result.StandardError + "\n" + result.StandardOutput;

            var diagnostics = new List<LintDiagnostic>(ParseSlangOutput(combinedOutput));

            if (result.ExitCode != 0 && diagnostics.All(d => d.Severity != LintSeverity.Error))
            {
                string detail = StripAnsi(combinedOutput).Trim();
                if (detail.Length == 0) detail = $"slang exited with code {result.ExitCode}.";
                diagnostics.Add(new LintDiagnostic("SLANGERROR", LintSeverity.Error, detail, "slang", new LintLocation(File: sourceFiles[0])));
            }

            int designElements = 0;
            IReadOnlyList<FsmInfo>? fsms = null;
            CdcAnalysisResult? cdc = null;

            // If elaboration succeeded and emitted AST JSON, also run VeloSim's pure C# IR linter
            // (deferring waivers/baseline/phase-stop to a single Finalize pass over the merged slang + IR findings).
            if (tryIrLower && result.ExitCode == 0 && astJsonPath is not null && File.Exists(astJsonPath))
            {
                string astJson = await File.ReadAllTextAsync(astJsonPath, cancellationToken).ConfigureAwait(false);
                try
                {
                    string irJson = SlangAstLowerer.LowerAstJson(astJson, top);
                    var innerOpts = opts with
                    {
                        Config = opts.Config is null ? null : opts.Config with { Waivers = null, WarnUnusedWaivers = false, StopOnPhaseError = false },
                        Baseline = null,
                        WarnUnusedWaivers = false,
                        StopOnPhaseError = false
                    };
                    var irReport = RtlLinter.LintJson(irJson, innerOpts, sourceFiles[0]);
                    diagnostics.AddRange(irReport.Diagnostics);
                    designElements = irReport.EffectiveQuality.DesignElements;
                    fsms = irReport.Fsms;
                    cdc = irReport.Cdc;
                }
                catch (Exception ex) when (ex is NotSupportedException or IrValidationException or InvalidOperationException or ArgumentException)
                {
                    diagnostics.Add(new LintDiagnostic(
                        "VS-SUBSET",
                        LintSeverity.Info,
                        "Skipped VeloSim IR lint pass (design uses constructs outside direct lowering subset): " + ex.Message,
                        "ir",
                        new LintLocation(File: sourceFiles[0])));
                }
            }

            return RtlLinter.Finalize(diagnostics, opts, designElements, fsms, cdc);
        }
        finally
        {
            if (Directory.Exists(workDir))
                Directory.Delete(workDir, recursive: true);
        }
    }

    public static IReadOnlyList<LintDiagnostic> ParseSlangOutput(string output)
    {
        var diagnostics = new List<LintDiagnostic>();
        if (string.IsNullOrWhiteSpace(output)) return diagnostics;

        string clean = StripAnsi(output);
        using var reader = new StringReader(clean);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var m = SlangDiagLineRegex().Match(line);
            if (!m.Success) continue;

            string file = m.Groups["file"].Value.Trim();
            int lineNo = int.Parse(m.Groups["line"].Value, CultureInfo.InvariantCulture);
            int colNo = int.Parse(m.Groups["col"].Value, CultureInfo.InvariantCulture);
            string sevText = m.Groups["sev"].Value;
            string message = m.Groups["msg"].Value.Trim();
            string flag = m.Groups["flag"].Success ? m.Groups["flag"].Value.Trim() : "";

            var severity = sevText switch
            {
                "error" or "fatal error" => LintSeverity.Error,
                "warning" => LintSeverity.Warning,
                _ => LintSeverity.Info
            };

            string code = NormalizeSlangCode(flag, severity);
            diagnostics.Add(new LintDiagnostic(code, severity, message, "slang", new LintLocation(file, lineNo, colNo)));
        }

        return diagnostics;
    }

    public static IReadOnlyList<LintDiagnostic> ParseSlangDiagnosticsJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var diagnostics = new List<LintDiagnostic>();
        using var doc = JsonDocument.Parse(json);
        JsonElement items;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            items = doc.RootElement;
        else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                 doc.RootElement.TryGetProperty("diagnostics", out var arr) &&
                 arr.ValueKind == JsonValueKind.Array)
            items = arr;
        else
            return diagnostics;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string sevText = item.TryGetProperty("severity", out var sEl) && sEl.ValueKind == JsonValueKind.String
                ? sEl.GetString()!.ToLowerInvariant()
                : "warning";
            var severity = sevText switch
            {
                "error" or "fatal" => LintSeverity.Error,
                "warning" => LintSeverity.Warning,
                _ => LintSeverity.Info
            };
            string message = item.TryGetProperty("message", out var mEl) && mEl.ValueKind == JsonValueKind.String
                ? mEl.GetString()!
                : "";
            string optionName = item.TryGetProperty("optionName", out var oEl) && oEl.ValueKind == JsonValueKind.String
                ? oEl.GetString()!
                : item.TryGetProperty("code", out var cEl) && cEl.ValueKind == JsonValueKind.String
                    ? cEl.GetString()!
                    : "";

            string? file = null;
            int? lineNo = null;
            int? colNo = null;
            if (item.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object)
            {
                if (loc.TryGetProperty("file", out var fEl) && fEl.ValueKind == JsonValueKind.String) file = fEl.GetString();
                if (loc.TryGetProperty("line", out var lEl) && lEl.TryGetInt32(out int l)) lineNo = l;
                if (loc.TryGetProperty("column", out var colEl) && colEl.TryGetInt32(out int c)) colNo = c;
            }
            else
            {
                if (item.TryGetProperty("file", out var fEl) && fEl.ValueKind == JsonValueKind.String) file = fEl.GetString();
                if (item.TryGetProperty("line", out var lEl) && lEl.TryGetInt32(out int l)) lineNo = l;
                if (item.TryGetProperty("column", out var colEl) && colEl.TryGetInt32(out int c)) colNo = c;
            }

            string code = NormalizeSlangCode(optionName, severity);
            diagnostics.Add(new LintDiagnostic(code, severity, message, "slang",
                file is not null || lineNo.HasValue ? new LintLocation(file, lineNo, colNo) : null));
        }

        return diagnostics;
    }

    private static IEnumerable<string> MapToSlangFlags(string rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) yield break;
        string trimmed = rule.Trim();
        if (VerilatorToSlangWarnings.TryGetValue(trimmed, out var mapped))
        {
            foreach (string s in mapped) yield return s;
            yield break;
        }

        // Only forward non-VeloSim-only rules to slang (slang rejects unknown -W names).
        if (LintRuleCatalog.TryGetRule(trimmed, out _))
            yield break;

        yield return trimmed;
    }

    private static string NormalizeSlangCode(string flag, LintSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(flag))
            return severity switch
            {
                LintSeverity.Error => "SLANGERROR",
                LintSeverity.Warning => "SLANGWARN",
                _ => "NOTE"
            };

        if (SlangToVerilatorCode.TryGetValue(flag, out string? mapped))
            return mapped;

        return flag.Replace('-', '_').ToUpperInvariant();
    }

    private static string StripAnsi(string text) => AnsiEscapeRegex().Replace(text, string.Empty);
}
