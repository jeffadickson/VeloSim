namespace VeloSim;

public sealed record RegressionCase(string Name, IrDesign Design, string Clock, string Observed, int Cycles, IReadOnlyList<TraceSample> Expected, string? ActiveLowReset = null);
public sealed record RegressionResult(string Name, int Workers, TraceMismatch? Mismatch, IReadOnlyList<TraceSample> Actual);
public static class RegressionRunner
{
    public static IReadOnlyList<RegressionResult> Run(RegressionCase test, IReadOnlyList<int> workerCounts)
    {
        if (string.IsNullOrWhiteSpace(test.Name) || test.Cycles < 1 || test.Expected.Count != test.Cycles) throw new ArgumentException("Invalid regression case.");
        if (workerCounts.Count == 0 || workerCounts.Distinct().Count() != workerCounts.Count || workerCounts.Any(w => w is < 1 or > 256)) throw new ArgumentException("Invalid worker matrix.");
        var signals = test.Design.Signals.ToDictionary(s => s.Name);
        if (!signals.TryGetValue(test.Clock, out var clock) || clock.Initial.Width != 1 || !signals.ContainsKey(test.Observed)) throw new ArgumentException("Invalid regression signals.");
        if (test.ActiveLowReset is not null && (!signals.TryGetValue(test.ActiveLowReset, out var reset) || reset.Initial.Width != 1)) throw new ArgumentException("Invalid reset.");
        var results = new List<RegressionResult>();
        foreach (var workers in workerCounts)
        {
            using var sim = new Simulation(test.Design); var actual = new List<TraceSample>();
            if (test.ActiveLowReset is not null) sim.Drive(new Dictionary<string, FourStateWord> { [test.ActiveLowReset] = new(1) }, workers);
            for (int cycle = 0; cycle < test.Cycles; cycle++)
            {
                if (cycle == 1 && test.ActiveLowReset is not null) sim.Drive(new Dictionary<string, FourStateWord> { [test.ActiveLowReset] = new(1, 1) }, workers);
                sim.Drive(new Dictionary<string, FourStateWord> { [test.Clock] = new(1) }, workers);
                sim.Drive(new Dictionary<string, FourStateWord> { [test.Clock] = new(1, 1) }, workers);
                var word = sim.Read(test.Observed); var bits = new char[word.Width];
                for (int bit = 0; bit < word.Width; bit++) { var mask = 1UL << bit; bits[word.Width - bit - 1] = (word.ZMask & mask) != 0 ? 'z' : (word.XMask & mask) != 0 ? 'x' : (word.Value & mask) != 0 ? '1' : '0'; }
                actual.Add(new(cycle, new string(bits)));
            }
            results.Add(new(test.Name, workers, TraceComparison.Compare(test.Expected, actual), actual.AsReadOnly()));
        }
        return results.AsReadOnly();
    }
}
