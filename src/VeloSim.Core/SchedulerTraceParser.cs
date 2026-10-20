using System.Globalization;

namespace VeloSim;

public static class SchedulerTraceParser
{
    public static IReadOnlyList<SchedulerStep> Parse(string output, SchedulerFixture fixture)
    {
        bool header = false; var trace = new List<SchedulerStep>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r'); if (!line.StartsWith("VS_SCHED", StringComparison.Ordinal)) continue;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields[0] == "VS_SCHED_HEADER")
            {
                var dimensions = new[] { fixture.SignalCount, fixture.Processes.Count, fixture.Observed.Count, fixture.Drives.Count };
                if (header || trace.Count > 0 || fields.Length != 5 || dimensions.Where((n, i) => fields[i + 1] != n.ToString(CultureInfo.InvariantCulture)).Any()) throw new FormatException("Invalid or duplicate scheduler trace header.");
                header = true; continue;
            }
            if (!header || fields.Length != 5 || fields[0] != "VS_SCHED" || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var step) || step != trace.Count || !ulong.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var tick) || tick != (ulong)step + 1 || !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var active) || active < 0 || active > fixture.Processes.Count || fields[4].Length != fixture.Observed.Count || fields[4].Any(c => c is not ('0' or '1' or 'x' or 'X' or 'z' or 'Z'))) throw new FormatException("Malformed scheduler trace row: " + line);
            trace.Add(new(tick, active, fields[4].ToLowerInvariant()));
        }
        if (!header || trace.Count != fixture.Drives.Count) throw new FormatException("Missing scheduler header or incomplete scheduler steps.");
        return trace.AsReadOnly();
    }
}
