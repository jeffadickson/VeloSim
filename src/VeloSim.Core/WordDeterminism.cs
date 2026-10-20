namespace VeloSim;

public sealed record WordDeterminismResult(int Workers, int Repetition, bool MatchesOracle, bool MatchesFirstRun);
public static class WordDeterminism
{
    public static IReadOnlyList<WordDeterminismResult> Run(WordFixture fixture, int repetitions, CancellationToken cancellationToken = default)
    {
        WordSystemVerilog.Validate(fixture);
        if (repetitions is < 2 or > 16 || (long)fixture.Drives.Count * fixture.Workers.Sum(w => (long)w) * repetitions > 1_000_000) throw new ArgumentException("Word determinism requires 2..16 repetitions within a million worker-steps.");
        cancellationToken.ThrowIfCancellationRequested(); var oracle = WordHdlTrace.Oracle(fixture, cancellationToken); var results = new List<WordDeterminismResult>();
        foreach (var workers in fixture.Workers)
        {
            IReadOnlyList<WordHdlStep>? first = null;
            for (int repetition = 0; repetition < repetitions; repetition++)
            {
                cancellationToken.ThrowIfCancellationRequested(); var trace = WordHdlTrace.Native(fixture, workers, cancellationToken); first ??= trace;
                results.Add(new(workers, repetition, WordReportReader.TraceEqual(oracle, trace), WordReportReader.TraceEqual(first, trace)));
            }
        }
        return results.AsReadOnly();
    }
}
