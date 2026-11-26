using System.Globalization;
using System.Text.RegularExpressions;

namespace VeloSim;

public sealed record SdcClock(string Name, string TargetSignal, double? Period, bool Generated = false, string? SourceSignal = null);
public sealed record SdcFalsePath(string? From, string? To);
public sealed record SdcClockGroup(string Kind, IReadOnlyList<IReadOnlyList<string>> Groups);

/// <summary>
/// Pure C# subset parser for Synopsys Design Constraints (SDC) files used by CDC/RDC static analysis:
/// <c>create_clock</c>, <c>create_generated_clock</c>, <c>set_clock_groups</c>, <c>set_false_path</c>, and <c>set_reset</c>.
/// </summary>
public sealed partial class SdcConstraints
{
    public IReadOnlyList<SdcClock> Clocks { get; }
    public IReadOnlyList<SdcClockGroup> ClockGroups { get; }
    public IReadOnlyList<SdcFalsePath> FalsePaths { get; }
    public IReadOnlySet<string> DeclaredResets { get; }

    public SdcConstraints(
        IReadOnlyList<SdcClock> clocks,
        IReadOnlyList<SdcClockGroup> clockGroups,
        IReadOnlyList<SdcFalsePath> falsePaths,
        IReadOnlySet<string> declaredResets)
    {
        Clocks = clocks;
        ClockGroups = clockGroups;
        FalsePaths = falsePaths;
        DeclaredResets = declaredResets;
    }

    public static SdcConstraints Empty { get; } = new([], [], [], new HashSet<string>(StringComparer.Ordinal));

    [GeneratedRegex(@"\[(?:get_ports|get_pins|get_nets|get_clocks|get_registers)\s+([^\]]+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketQueryRegex();

    public static SdcConstraints LoadFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("SDC constraint file not found: " + path, path);
        return Parse(File.ReadAllText(path));
    }

    public static SdcConstraints Parse(string sdcText)
    {
        ArgumentNullException.ThrowIfNull(sdcText);

        var clocks = new List<SdcClock>();
        var groups = new List<SdcClockGroup>();
        var falsePaths = new List<SdcFalsePath>();
        var resets = new HashSet<string>(StringComparer.Ordinal);

        // Join backslash-continued lines and strip '#' comments
        var logicalLines = SplitLogicalLines(sdcText);
        foreach (string rawLine in logicalLines)
        {
            var tokens = TokenizeCommand(rawLine);
            if (tokens.Count == 0) continue;

            string cmd = tokens[0];
            if (cmd == "create_clock")
                ParseCreateClock(tokens, generated: false, clocks);
            else if (cmd == "create_generated_clock")
                ParseCreateClock(tokens, generated: true, clocks);
            else if (cmd == "set_clock_groups")
                ParseClockGroups(tokens, groups);
            else if (cmd == "set_false_path")
                ParseFalsePath(tokens, falsePaths);
            else if (cmd == "set_reset")
            {
                for (int i = 1; i < tokens.Count; i++)
                {
                    if (tokens[i].StartsWith('-')) { i++; continue; }
                    foreach (string s in ExtractTargets(tokens[i]))
                        resets.Add(s);
                }
            }
        }

        return new SdcConstraints(clocks.AsReadOnly(), groups.AsReadOnly(), falsePaths.AsReadOnly(), resets);
    }

    public bool HasClockTarget(string signal) =>
        Clocks.Any(c => c.TargetSignal.Equals(signal, StringComparison.Ordinal) || c.Name.Equals(signal, StringComparison.Ordinal));

    public string ResolveClockName(string signal) =>
        Clocks.FirstOrDefault(c => c.TargetSignal.Equals(signal, StringComparison.Ordinal))?.Name ?? signal;

    public bool AreClocksSynchronous(string clkA, string clkB)
    {
        if (clkA.Equals(clkB, StringComparison.Ordinal)) return true;

        // If clkB is a generated clock derived from clkA (or vice versa), they are synchronous unless separated by set_clock_groups
        string nameA = ResolveClockName(clkA);
        string nameB = ResolveClockName(clkB);

        bool explicitlyAsync = false;
        foreach (var cg in ClockGroups)
        {
            int gA = -1, gB = -1;
            for (int i = 0; i < cg.Groups.Count; i++)
            {
                if (cg.Groups[i].Any(x => x.Equals(clkA, StringComparison.Ordinal) || x.Equals(nameA, StringComparison.Ordinal))) gA = i;
                if (cg.Groups[i].Any(x => x.Equals(clkB, StringComparison.Ordinal) || x.Equals(nameB, StringComparison.Ordinal))) gB = i;
            }
            if (gA >= 0 && gB >= 0)
            {
                if (gA == gB) return true;
                explicitlyAsync = true;
            }
        }

        if (explicitlyAsync) return false;

        // Generated clock derived from same root source is synchronous
        string rootA = FindClockRoot(clkA);
        string rootB = FindClockRoot(clkB);
        return rootA.Equals(rootB, StringComparison.Ordinal);
    }

    public bool IsFalsePath(string srcSignal, string srcClock, string dstSignal, string dstClock)
    {
        string srcClkName = ResolveClockName(srcClock);
        string dstClkName = ResolveClockName(dstClock);

        foreach (var fp in FalsePaths)
        {
            bool fromOk = string.IsNullOrEmpty(fp.From) ||
                          LintGlob.IsMatch(fp.From, srcSignal) ||
                          LintGlob.IsMatch(fp.From, srcClock) ||
                          LintGlob.IsMatch(fp.From, srcClkName);
            bool toOk = string.IsNullOrEmpty(fp.To) ||
                        LintGlob.IsMatch(fp.To, dstSignal) ||
                        LintGlob.IsMatch(fp.To, dstClock) ||
                        LintGlob.IsMatch(fp.To, dstClkName);
            if (fromOk && toOk) return true;
        }
        return false;
    }

    private string FindClockRoot(string sig)
    {
        string cur = sig;
        for (int depth = 0; depth < 16; depth++)
        {
            var gen = Clocks.FirstOrDefault(c => c.Generated && c.TargetSignal.Equals(cur, StringComparison.Ordinal) && !string.IsNullOrEmpty(c.SourceSignal));
            if (gen?.SourceSignal is null) break;
            cur = gen.SourceSignal;
        }
        return cur;
    }

    private static void ParseCreateClock(List<string> tokens, bool generated, List<SdcClock> clocks)
    {
        string? name = null, source = null;
        double? period = null;
        var targets = new List<string>();

        for (int i = 1; i < tokens.Count; i++)
        {
            string t = tokens[i];
            if (t == "-name" && i + 1 < tokens.Count) { name = tokens[++i].Trim('{', '}', '"'); }
            else if (t == "-period" && i + 1 < tokens.Count)
            {
                if (double.TryParse(tokens[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double p))
                    period = p;
            }
            else if (t == "-source" && i + 1 < tokens.Count)
            {
                source = ExtractTargets(tokens[++i]).FirstOrDefault();
            }
            else if (t is "-divide_by" or "-multiply_by" or "-waveform" or "-edges" && i + 1 < tokens.Count)
            {
                i++;
            }
            else if (t.StartsWith('-')) { continue; }
            else
            {
                targets.AddRange(ExtractTargets(t));
            }
        }

        if (targets.Count == 0 && !string.IsNullOrEmpty(name))
            targets.Add(name);

        foreach (string target in targets)
            clocks.Add(new SdcClock(name ?? target, target, period, generated, source));
    }

    private static void ParseClockGroups(List<string> tokens, List<SdcClockGroup> groups)
    {
        string kind = "asynchronous";
        var groupLists = new List<IReadOnlyList<string>>();
        for (int i = 1; i < tokens.Count; i++)
        {
            string t = tokens[i];
            if (t is "-asynchronous" or "-logically_exclusive" or "-physically_exclusive")
                kind = t[1..];
            else if (t == "-group" && i + 1 < tokens.Count)
            {
                var items = ExtractTargets(tokens[++i]);
                if (items.Count > 0) groupLists.Add(items);
            }
        }
        if (groupLists.Count >= 2)
            groups.Add(new SdcClockGroup(kind, groupLists));
    }

    private static void ParseFalsePath(List<string> tokens, List<SdcFalsePath> falsePaths)
    {
        var froms = new List<string>();
        var tos = new List<string>();
        for (int i = 1; i < tokens.Count; i++)
        {
            string t = tokens[i];
            if (t == "-from" && i + 1 < tokens.Count) froms.AddRange(ExtractTargets(tokens[++i]));
            else if (t == "-to" && i + 1 < tokens.Count) tos.AddRange(ExtractTargets(tokens[++i]));
        }
        if (froms.Count == 0 && tos.Count == 0) return;
        if (froms.Count == 0)
        {
            foreach (string to in tos) falsePaths.Add(new SdcFalsePath(null, to));
        }
        else if (tos.Count == 0)
        {
            foreach (string fr in froms) falsePaths.Add(new SdcFalsePath(fr, null));
        }
        else
        {
            foreach (string fr in froms)
                foreach (string to in tos)
                    falsePaths.Add(new SdcFalsePath(fr, to));
        }
    }

    private static List<string> ExtractTargets(string token)
    {
        string cleaned = token.Trim();
        if (cleaned.StartsWith('{') && cleaned.EndsWith('}'))
            cleaned = cleaned[1..^1].Trim();

        cleaned = BracketQueryRegex().Replace(cleaned, "$1");
        var list = new List<string>();
        foreach (string part in cleaned.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            string p = part.Trim('{', '}', '"', '[', ']');
            if (p.Length > 0 && !p.StartsWith('-'))
                list.Add(p);
        }
        return list;
    }

    private static List<string> SplitLogicalLines(string text)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed.EndsWith('\\'))
            {
                current.Append(trimmed[..^1]).Append(' ');
            }
            else
            {
                current.Append(trimmed);
                foreach (string stmt in current.ToString().Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string s = stmt.Trim();
                    if (s.Length > 0) result.Add(s);
                }
                current.Clear();
            }
        }
        if (current.Length > 0)
        {
            string s = current.ToString().Trim();
            if (s.Length > 0) result.Add(s);
        }
        return result;
    }

    private static List<string> TokenizeCommand(string line)
    {
        var tokens = new List<string>();
        var cur = new System.Text.StringBuilder();
        int braceDepth = 0, bracketDepth = 0;
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"' && braceDepth == 0 && bracketDepth == 0)
            {
                inQuotes = !inQuotes;
                cur.Append(c);
            }
            else if (!inQuotes && c == '{') { braceDepth++; cur.Append(c); }
            else if (!inQuotes && c == '}' && braceDepth > 0) { braceDepth--; cur.Append(c); }
            else if (!inQuotes && c == '[') { bracketDepth++; cur.Append(c); }
            else if (!inQuotes && c == ']' && bracketDepth > 0) { bracketDepth--; cur.Append(c); }
            else if (!inQuotes && braceDepth == 0 && bracketDepth == 0 && char.IsWhiteSpace(c))
            {
                if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); }
            }
            else
            {
                cur.Append(c);
            }
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        return tokens;
    }
}
