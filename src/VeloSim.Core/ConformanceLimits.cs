namespace VeloSim;

public sealed record ConformanceLimits(int MaxSignals = 100_000, int MaxProcesses = 100_000, int MaxSteps = 1_000_000, long MaxSignalChecks = 100_000_000, long MaxProcessEvaluations = 100_000_000)
{
    public const int MaxFixtureCharacters = 16 * 1024 * 1024;
    public static ConformanceLimits Default { get; } = new();
    public long Validate(IrDesign design, IReadOnlyList<IReadOnlyDictionary<string, FourStateWord>> drives, IReadOnlyList<int> workers, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (MaxSignals < 1 || MaxProcesses < 0 || MaxSteps < 1 || MaxSignalChecks < 1 || MaxProcessEvaluations < 1) throw new ArgumentException("Invalid conformance resource limits.");
        if (design.Signals.Count > MaxSignals || design.Processes.Count > MaxProcesses || drives.Count is < 1 || drives.Count > MaxSteps || workers.Count is < 1 or > 256 || workers.Distinct().Count() != workers.Count || workers.Any(w => w is < 1 or > 256)) throw new ArgumentException("Conformance dimensions exceed allowed limits.");
        long work = checked((long)design.Signals.Count * drives.Count * workers.Count);
        if (work > MaxSignalChecks) throw new ArgumentException("Conformance signal-check budget exceeded.");
        if (checked((long)design.Processes.Count * drives.Count * workers.Count) > MaxProcessEvaluations) throw new ArgumentException("Conformance process-evaluation budget exceeded.");
        var names = design.Signals.ToDictionary(s => s.Name, s => s.Initial.Width, StringComparer.Ordinal);
        foreach (var drive in drives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (drive.Any(s => !names.TryGetValue(s.Key, out var width) || s.Value.Width != width)) throw new ArgumentException("Invalid conformance drive signal or width.");
        }
        return work;
    }
}
