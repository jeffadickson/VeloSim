namespace VeloSim;

public sealed record SchedulerMismatch(int Step, SchedulerStep Expected, SchedulerStep Actual);
public sealed record SchedulerWorkerResult(int Workers, IReadOnlyList<SchedulerStep> Trace, SchedulerMismatch? Mismatch);
public static class SchedulerRegression
{
    public static IReadOnlyList<SchedulerStep> Oracle(SchedulerFixture fixture, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = Enumerable.Repeat(ScalarValue.X, fixture.SignalCount).ToArray(); var trace = new List<SchedulerStep>(); ulong tick = 0;
        foreach (var drive in fixture.Drives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = state; var precommit = (ScalarValue[])state.Clone();
            foreach (var (signal, value) in drive) precommit[signal] = value;
            var scheduled = new List<(int Destination, ScalarValue Value)>();
            foreach (var p in fixture.Processes)
                if (ScalarLogic.IsEdge(p.Edge, before[p.Clock], precommit[p.Clock])) scheduled.Add((p.Destination, ScalarLogic.Evaluate(p.Operation, precommit[p.A], precommit[p.B])));
            state = (ScalarValue[])precommit.Clone(); foreach (var update in scheduled) state[update.Destination] = update.Value;
            trace.Add(new(++tick, scheduled.Count, new string(fixture.Observed.Select(id => ScalarLogic.Format(state[id])).ToArray())));
        }
        return trace.AsReadOnly();
    }
    public static IReadOnlyList<SchedulerWorkerResult> Run(SchedulerFixture fixture, IReadOnlyList<int> workers, CancellationToken cancellationToken = default)
    {
        if (workers.Count == 0 || workers.Distinct().Count() != workers.Count || workers.Any(w => w is < 1 or > 256)) throw new ArgumentException("Invalid scheduler worker matrix.");
        cancellationToken.ThrowIfCancellationRequested(); var expected = Oracle(fixture, cancellationToken); var results = new List<SchedulerWorkerResult>();
        foreach (var count in workers)
        {
            var actual = fixture.Run(count, cancellationToken); SchedulerMismatch? mismatch = null;
            for (int i = 0; i < expected.Count; i++) if (expected[i] != actual[i]) { mismatch = new(i, expected[i], actual[i]); break; }
            results.Add(new(count, actual, mismatch));
        }
        return results.AsReadOnly();
    }
}
