namespace VeloSim;

public sealed class IrOracle
{
    private readonly IrDesign design;
    private Dictionary<string, FourStateWord> state;
    public ulong Ticks { get; private set; }
    public IrOracle(IrDesign design)
    {
        this.design = design; state = design.Signals.ToDictionary(s => s.Name, s => FourStateCodec.Resize(s.Initial, s.Initial.Width), StringComparer.Ordinal);
    }
    public FourStateWord Read(string name) => state[name];
    public int Drive(IReadOnlyDictionary<string, FourStateWord> updates)
    {
        var precommit = new Dictionary<string, FourStateWord>(state, StringComparer.Ordinal);
        foreach (var (name, word) in updates)
        {
            if (!state.TryGetValue(name, out var previous) || word.Width != previous.Width) throw new ArgumentException("Invalid oracle drive signal or width.");
            precommit[name] = FourStateCodec.Resize(word, word.Width);
        }
        var scheduled = new List<(string Name, FourStateWord Value)>();
        foreach (var p in design.Processes)
        {
            if (!ScalarLogic.IsEdge(p.Edge, FourStateCodec.BitAt(state[p.Clock], 0), FourStateCodec.BitAt(precommit[p.Clock], 0))) continue;
            var source = precommit[p.Source]; var width = precommit[p.Destination].Width;
            var result = p.Operation switch
            {
                IrOperation.Copy => FourStateCodec.Resize(source, width),
                IrOperation.Increment => FourStateArithmetic.Increment(source, width),
                IrOperation.SvIncrement => FourStateArithmetic.Increment(source, width, true),
                IrOperation.ResetIncrement => FourStateArithmetic.ResetIncrement(source, precommit[p.Reset], width, false, false),
                IrOperation.SvIfResetIncrement => FourStateArithmetic.ResetIncrement(source, precommit[p.Reset], width, true, false),
                IrOperation.SvIfResetIncrementStrict => FourStateArithmetic.ResetIncrement(source, precommit[p.Reset], width, true, true),
                _ => throw new IrValidationException("Unsupported oracle operation.")
            };
            scheduled.Add((p.Destination, result));
        }
        foreach (var result in scheduled) precommit[result.Name] = result.Value;
        state = precommit; Ticks++; return scheduled.Count;
    }
}
