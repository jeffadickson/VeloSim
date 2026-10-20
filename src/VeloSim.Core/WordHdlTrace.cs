using System.Globalization;

namespace VeloSim;

public sealed record WordHdlStep(ulong Tick, int ActivatedProcesses, IReadOnlyList<string> Values);
public static class WordHdlTrace
{
    public static IReadOnlyList<WordHdlStep> Parse(string output, WordFixture fixture)
    {
        var rows = output.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith("VS_WORD", StringComparison.Ordinal)).Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToArray(); int next = 0;
        string[] Row() => next < rows.Length ? rows[next++] : throw new FormatException("Incomplete word HDL trace.");
        string N(int n) => n.ToString(CultureInfo.InvariantCulture);
        if (!Row().SequenceEqual(new[] { "VS_WORD_HEADER", N(fixture.Design.Signals.Count), N(fixture.Design.Processes.Count), N(fixture.Drives.Count) })) throw new FormatException("Word HDL header dimensions differ from fixture.");
        for (int signal = 0; signal < fixture.Design.Signals.Count; signal++) if (!Row().SequenceEqual(new[] { "VS_WORD_SIGNAL", N(signal), N(fixture.Design.Signals[signal].Initial.Width) })) throw new FormatException("Word HDL signal order or width mismatch.");
        var trace = new List<WordHdlStep>();
        for (int step = 0; step < fixture.Drives.Count; step++)
        {
            var row = Row();
            if (row.Length != 4 || row[0] != "VS_WORD_STEP" || row[1] != N(step) || row[2] != N(step + 1) || !int.TryParse(row[3], NumberStyles.None, CultureInfo.InvariantCulture, out var active) || active < 0 || active > fixture.Design.Processes.Count) throw new FormatException("Malformed word HDL step/tick/activation row.");
            var values = new List<string>();
            for (int signal = 0; signal < fixture.Design.Signals.Count; signal++)
            {
                var value = Row();
                if (value.Length != 4 || value[0] != "VS_WORD_VALUE" || value[1] != N(step) || value[2] != N(signal) || value[3].Length != fixture.Design.Signals[signal].Initial.Width || value[3].Any(c => c is not ('0' or '1' or 'x' or 'X' or 'z' or 'Z'))) throw new FormatException("Malformed word HDL signal row.");
                values.Add(value[3].ToLowerInvariant());
            }
            trace.Add(new((ulong)step + 1, active, values.AsReadOnly()));
        }
        if (next != rows.Length) throw new FormatException("Unexpected extra word HDL trace rows."); return trace.AsReadOnly();
    }
    public static IReadOnlyList<WordHdlStep> Oracle(WordFixture fixture, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var oracle = new IrOracle(fixture.Design); var trace = new List<WordHdlStep>();
        foreach (var drive in fixture.Drives) { cancellationToken.ThrowIfCancellationRequested(); var active = oracle.Drive(drive, cancellationToken); trace.Add(new(oracle.Ticks, active, fixture.Design.Signals.Select(s => FourStateCodec.FormatBinary(oracle.Read(s.Name))).ToArray())); } return trace.AsReadOnly();
    }
    public static IReadOnlyList<WordHdlStep> Native(WordFixture fixture, int workers, CancellationToken cancellationToken = default)
    {
        if (workers is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(workers));
        cancellationToken.ThrowIfCancellationRequested(); using var native = new Simulation(fixture.Design); var trace = new List<WordHdlStep>();
        foreach (var drive in fixture.Drives) { cancellationToken.ThrowIfCancellationRequested(); var active = native.Drive(drive, workers); trace.Add(new(native.Ticks, active, fixture.Design.Signals.Select(s => FourStateCodec.FormatBinary(native.Read(s.Name))).ToArray())); } return trace.AsReadOnly();
    }
}
