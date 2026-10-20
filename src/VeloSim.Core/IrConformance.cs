namespace VeloSim;

public sealed record IrConformanceMismatch(int Step, string Signal, FourStateWord? Expected, FourStateWord? Actual, int ExpectedActive, int ActualActive);
public sealed record IrConformanceResult(int Workers, int Steps, IrConformanceMismatch? Mismatch);
public static class IrConformance
{
    public static IReadOnlyList<IrConformanceResult> Run(IrDesign design, IReadOnlyList<IReadOnlyDictionary<string, FourStateWord>> drives, IReadOnlyList<int> workers, CancellationToken cancellationToken = default, ConformanceLimits? limits = null)
    {
        (limits ?? ConformanceLimits.Default).Validate(design, drives, workers, cancellationToken);
        var results = new List<IrConformanceResult>();
        foreach (var count in workers)
        {
            cancellationToken.ThrowIfCancellationRequested(); var oracle = new IrOracle(design); using var native = new Simulation(design); IrConformanceMismatch? mismatch = null; int completed = 0;
            for (int step = 0; step < drives.Count; step++)
            {
                cancellationToken.ThrowIfCancellationRequested(); var expectedActive = oracle.Drive(drives[step], cancellationToken); var active = native.Drive(drives[step], count); completed++;
                if (active != expectedActive) mismatch = new(step, "", null, null, expectedActive, active);
                if (native.Ticks != oracle.Ticks) mismatch ??= new(step, "$ticks", new FourStateWord(64, oracle.Ticks), new FourStateWord(64, native.Ticks), expectedActive, active);
                foreach (var signal in design.Signals)
                {
                    var expected = oracle.Read(signal.Name); var actual = native.Read(signal.Name);
                    if (actual != expected) { mismatch ??= new(step, signal.Name, expected, actual, expectedActive, active); break; }
                }
                if (mismatch is not null) break;
            }
            results.Add(new(count, completed, mismatch));
        }
        return results.AsReadOnly();
    }
}
