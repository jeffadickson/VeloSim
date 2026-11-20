namespace VeloSim;

public sealed class IrOracle
{
    private readonly record struct BoundProcess(int Clock, int Destination, int Source, int SourceB, int Reset, int Width, IrEdge Edge, IrOperation Operation);
    private readonly Dictionary<string, int> signalIds;
    private readonly BoundProcess[] processes;
    private FourStateWord[] state;
    private FourStateWord[] precommit;
    private readonly (int Id, FourStateWord Value)[] scheduled;
    public ulong Ticks { get; private set; }
    public IrOracle(IrDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        int count = design.Signals.Count;
        signalIds = new(count, StringComparer.Ordinal);
        state = new FourStateWord[count];
        precommit = new FourStateWord[count];
        for (int i = 0; i < count; i++)
        {
            var s = design.Signals[i];
            signalIds.Add(s.Name, i);
            state[i] = FourStateCodec.Resize(s.Initial, s.Initial.Width);
        }
        processes = new BoundProcess[design.Processes.Count];
        for (int i = 0; i < processes.Length; i++)
        {
            var p = design.Processes[i];
            int dst = signalIds[p.Destination];
            processes[i] = new(signalIds[p.Clock], dst, signalIds[p.Source], signalIds[p.SourceB ?? p.Source], signalIds[p.Reset], state[dst].Width, p.Edge, p.Operation);
        }
        scheduled = new (int Id, FourStateWord Value)[processes.Length];
    }
    public FourStateWord Read(string name) => state[signalIds[name]];
    public int Drive(IReadOnlyDictionary<string, FourStateWord> updates, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Array.Copy(state, precommit, state.Length);
        foreach (var (name, word) in updates)
        {
            if (!signalIds.TryGetValue(name, out int id) || word.Width != state[id].Width) throw new ArgumentException("Invalid oracle drive signal or width.");
            precommit[id] = FourStateCodec.Resize(word, word.Width);
        }
        int scheduledCount = 0;
        for (int i = 0; i < processes.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var p = processes[i];
            if (!ScalarLogic.IsEdge(p.Edge, FourStateCodec.BitAt(state[p.Clock], 0), FourStateCodec.BitAt(precommit[p.Clock], 0))) continue;
            var source = precommit[p.Source];
            var second = precommit[p.SourceB];
            var result = p.Operation switch
            {
                IrOperation.Copy => FourStateCodec.Resize(source, p.Width),
                IrOperation.Increment => FourStateArithmetic.Increment(source, p.Width),
                IrOperation.SvIncrement => FourStateArithmetic.Increment(source, p.Width, true),
                IrOperation.ResetIncrement => FourStateArithmetic.ResetIncrement(source, precommit[p.Reset], p.Width, false, false),
                IrOperation.SvIfResetIncrement => FourStateArithmetic.ResetIncrement(source, precommit[p.Reset], p.Width, true, false),
                IrOperation.SvIfResetIncrementStrict => FourStateArithmetic.ResetIncrement(source, precommit[p.Reset], p.Width, true, true),
                IrOperation.And => FourStateLogic.Evaluate(ScalarOperation.And, FourStateCodec.Resize(source, p.Width), FourStateCodec.Resize(second, p.Width)),
                IrOperation.Or => FourStateLogic.Evaluate(ScalarOperation.Or, FourStateCodec.Resize(source, p.Width), FourStateCodec.Resize(second, p.Width)),
                IrOperation.Xor => FourStateLogic.Evaluate(ScalarOperation.Xor, FourStateCodec.Resize(source, p.Width), FourStateCodec.Resize(second, p.Width)),
                IrOperation.Not => FourStateLogic.Evaluate(ScalarOperation.Not, FourStateCodec.Resize(source, p.Width), FourStateCodec.Resize(source, p.Width)),
                IrOperation.UnsignedAdd or IrOperation.UnsignedSubtract or IrOperation.UnsignedMultiply => UnsignedWordArithmetic.Evaluate(p.Operation, source, second, p.Width),
                _ => throw new IrValidationException("Unsupported oracle operation.")
            };
            scheduled[scheduledCount++] = (p.Destination, result);
        }
        cancellationToken.ThrowIfCancellationRequested();
        for (int i = 0; i < scheduledCount; i++) precommit[scheduled[i].Id] = scheduled[i].Value;
        (state, precommit) = (precommit, state);
        Ticks++;
        return scheduledCount;
    }
}
