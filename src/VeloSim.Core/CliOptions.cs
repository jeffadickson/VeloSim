using System.CommandLine;
using System.Globalization;

namespace VeloSim;

// All option tokenization/help is delegated to System.CommandLine. Domain
// validation remains with each workflow. No response-file expansion or POSIX
// bundling is enabled: arguments are explicit, reproducible process inputs.
internal sealed class CliOptions
{
    private sealed record Spec(string Values, string Flags = "", bool Sources = false, bool Unique = false);
    private static readonly IReadOnlyDictionary<string, Spec> Specs = new Dictionary<string, Spec>(StringComparer.Ordinal)
    {
        ["rtl-export"] = new("top output-dir"),
        ["fifo-differential"] = new("top output-dir exporter surelog build-jobs cycles seed workers", Unique: true),
        ["word-seeded-conformance"] = new("output-dir iverilog vvp seed steps repetitions workers", Unique: true),
        ["word-inspect"] = new(""), ["word-replay"] = new("output-dir"), ["word-reduce"] = new("output-dir"),
        ["word-differential"] = new("output-dir iverilog vvp"), ["word-conformance"] = new("output-dir iverilog vvp workers"),
        ["word-boundaries"] = new("output workers"), ["word-operation-conformance"] = new("output-dir workers"),
        ["scheduler-inspect"] = new(""), ["scheduler-replay"] = new("output-dir"), ["scheduler-reduce"] = new("output-dir"),
        ["scheduler-differential"] = new("output-dir iverilog vvp workers"), ["scheduler-conformance"] = new("output-dir iverilog vvp workers"),
        ["scheduler-generate"] = new("output steps seed"), ["matrix-inspect"] = new(""),
        ["elaboration-matrix"] = new("config root output-dir surelog workers"),
        ["word-check"] = new("report"), ["word-generate"] = new("output steps seed workers", "four-state"),
        ["scheduler"] = new("workers"), ["scheduler-check"] = new("workers"),
        ["regression"] = new("output-dir surelog normalizer"),
        ["suite"] = new("output-dir surelog normalizer include exclude shard-index shard-count"),
        ["inspect-report"] = new(""), ["replay"] = new("output-dir"),
        ["differential"] = new("top output-dir surelog normalizer seed cycles threads build-jobs engines", Sources: true),
        ["validate-ir"] = new(""), ["lower"] = new("output"),
        ["compile"] = new("top output-dir surelog normalizer threads", Sources: true),
        ["simulate"] = new("cycles threads", "release-reset"),
        ["benchmark"] = new("processes cycles width workers repeat warmup report")
    };
    private readonly Dictionary<string, Option<string[]>> values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Option<bool>> flags = new(StringComparer.Ordinal);
    private readonly System.CommandLine.ParseResult parsed;
    internal string[] Sources { get; }

    private CliOptions(string command, IReadOnlyList<string> tokens)
    {
        var spec = Specs[command];
        var root = new RootCommand(); root.Options.Clear();
        foreach (string name in spec.Values.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            // Collect one value per occurrence; legacy workflows either reject
            // duplicate occurrences or use the final value. A scalar option's
            // default ExactlyOne arity would reject both kinds of duplicates.
            var option = new Option<string[]>("--" + name) { Arity = ArgumentArity.OneOrMore, AllowMultipleArgumentsPerToken = false };
            values.Add("--" + name, option); root.Options.Add(option);
        }
        foreach (string name in spec.Flags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var option = new Option<bool>("--" + name) { Arity = ArgumentArity.Zero };
            flags.Add("--" + name, option); root.Options.Add(option);
        }
        var sources = new Argument<string[]>("sources") { Arity = ArgumentArity.ZeroOrMore };
        if (spec.Sources) root.Arguments.Add(sources);
        parsed = root.Parse(tokens, new ParserConfiguration { EnablePosixBundling = false, ResponseFileTokenReplacer = null });
        // Only workflows that historically rejected duplicate options do so.
        // Other scalar options retain System.CommandLine's last-value behavior.
        if (spec.Unique && values.Values.Any(o => parsed.GetResult(o)?.IdentifierTokenCount > 1))
            throw new ArgumentException("Incomplete or duplicate " + command + " option.");
        if (parsed.Errors.Count != 0) throw new ArgumentException("Invalid " + command + " options: " + string.Join("; ", parsed.Errors.Select(e => e.Message)));
        foreach (var option in values.Values)
            if (parsed.GetResult(option) is { } result && result.Tokens.Count != result.IdentifierTokenCount)
                throw new ArgumentException("Incomplete " + command + " option: " + option.Name);
        Sources = spec.Sources ? parsed.GetValue(sources) ?? [] : [];
    }
    internal static CliOptions Parse(string[] args, int firstOption) => new(args[0], args[firstOption..]);
    internal static CliOptions Benchmark(string[] args) => new("benchmark", args);
    internal string? String(string name) => parsed.GetValue(values[name])?.LastOrDefault();
    internal bool Flag(string name) => parsed.GetValue(flags[name]);
    internal int Int(string name, int fallback) => String(name) is { } text ? int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException("Invalid integer for " + name) : fallback;
    internal uint UInt(string name, uint fallback) => String(name) is { } text ? uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException("Invalid unsigned integer for " + name) : fallback;
    internal int[] Workers(int[] fallback) => String("--workers") is { } text ? text.Split(',').Select(n => int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : throw new ArgumentException("Invalid worker count.")).ToArray() : fallback;

    internal static bool TryHelp(string[] args, TextWriter output)
    {
        if (args.Length == 0 || args[^1] is not ("--help" or "-h")) return false;
        // Help actions can suppress parser errors. Validate the workflow before
        // invoking one, so an unknown command cannot masquerade as successful help.
        if (args.Length > 1 && (!Specs.ContainsKey(args[0]) || args[0] == "benchmark")) return false;
        var root = new RootCommand("VeloSim compiled HDL simulation and verification workflows");
        foreach (var pair in Specs.Where(p => p.Key != "benchmark"))
        {
            var command = new Command(pair.Key);
            foreach (string name in pair.Value.Values.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                command.Options.Add(new Option<string?>("--" + name));
            foreach (string name in pair.Value.Flags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                command.Options.Add(new Option<bool>("--" + name));
            root.Subcommands.Add(command);
        }
        var help = root.Parse(args.Length == 1 ? ["--help"] : [args[0], "--help"], new ParserConfiguration { ResponseFileTokenReplacer = null });
        if (help.Errors.Count != 0) return false;
        help.Invoke(new InvocationConfiguration { Output = output, Error = TextWriter.Null, EnableDefaultExceptionHandler = false });
        return true;
    }
}
