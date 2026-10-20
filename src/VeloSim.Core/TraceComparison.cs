using System.Globalization;

namespace VeloSim;

public sealed record TraceSample(long Cycle, string Bits);
public sealed record TraceMismatch(int Index, TraceSample? Expected, TraceSample? Actual);
public static class TraceComparison
{
    // Stable reference interchange: one '<cycle> <binary four-state bits>' row per sample.
    public static IReadOnlyList<TraceSample> Parse(string text, int width)
    {
        if (width is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(width));
        var samples = new List<TraceSample>(); long previous = -1;
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2 || !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var cycle) || cycle <= previous || fields[1].Length != width || fields[1].Any(c => c is not ('0' or '1' or 'x' or 'X' or 'z' or 'Z')))
                throw new FormatException("Invalid or nonmonotonic trace row: " + line);
            samples.Add(new(cycle, fields[1].ToLowerInvariant())); previous = cycle;
        }
        if (samples.Count == 0) throw new FormatException("Empty trace.");
        return samples.AsReadOnly();
    }
    public static TraceMismatch? Compare(IReadOnlyList<TraceSample> expected, IReadOnlyList<TraceSample> actual)
    {
        for (int i = 0; i < Math.Max(expected.Count, actual.Count); i++)
        {
            var e = i < expected.Count ? expected[i] : null; var a = i < actual.Count ? actual[i] : null;
            if (e != a) return new(i, e, a);
        }
        return null;
    }
}
