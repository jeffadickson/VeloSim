namespace VeloSim;

public static class IrStimulus
{
    public static WordFixture GenerateBoundaries(IrDesign design, IReadOnlyList<int>? workers = null)
    {
        if (design.Signals.Count > 4096 || design.Processes.Count is < 1 or > 4096) throw new ArgumentException("Boundary generation requires 1..4096 processes and at most 4096 signals.");
        var clocks = design.Processes.Select(p => p.Clock).ToHashSet(StringComparer.Ordinal);
        if (design.Processes.Any(p => clocks.Contains(p.Destination))) throw new ArgumentException("Boundary generation does not drive computed clocks.");
        var resets = design.Processes.Where(p => p.Operation is IrOperation.ResetIncrement or IrOperation.SvIfResetIncrement or IrOperation.SvIfResetIncrementStrict).Select(p => p.Reset).ToHashSet(StringComparer.Ordinal);
        var widths = design.Signals.ToDictionary(s => s.Name, s => s.Initial.Width, StringComparer.Ordinal);
        FourStateWord Value(int width, int index)
        {
            var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1; var high = 1UL << (width - 1);
            return index switch { 0 => new(width), 1 => new(width, 1), 2 => new(width, mask), 3 => new(width, high), 4 => new(width, 0, mask), 5 => new(width, 0, 0, mask), _ => new(width, width == 1 ? 0UL : 1UL, high) };
        }
        var drives = new List<IReadOnlyDictionary<string, FourStateWord>>();
        for (int left = 0; left < 7; left++) for (int right = 0; right < 7; right++)
        {
            var setup = new Dictionary<string, FourStateWord>(StringComparer.Ordinal);
            foreach (var process in design.Processes)
            {
                if (!clocks.Contains(process.Source) && !resets.Contains(process.Source)) setup[process.Source] = Value(widths[process.Source], left);
                if (process.SourceB is { } second && !clocks.Contains(second) && !resets.Contains(second)) setup[second] = Value(widths[second], right);
            }
            foreach (var reset in resets) setup[reset] = new(1, 1); foreach (var clock in clocks) setup[clock] = new(1);
            drives.Add(setup); drives.Add(clocks.ToDictionary(clock => clock, _ => new FourStateWord(1, 1), StringComparer.Ordinal)); drives.Add(clocks.ToDictionary(clock => clock, _ => new FourStateWord(1), StringComparer.Ordinal));
        }
        return new(design, drives, workers ?? [1]);
    }
    public static WordFixture Generate(IrDesign design, int steps, uint seed, bool fourState, IReadOnlyList<int>? workers = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var counts = (workers ?? [1]).ToArray();
        if (counts.Length is < 1 or > 256 || counts.Distinct().Count() != counts.Length || counts.Any(w => w is < 1 or > 256)) throw new ArgumentException("Invalid generated worker matrix.");
        if (steps is < 1 or > 1_000_000 || (long)steps * design.Signals.Count > 1_000_000) throw new ArgumentException("Generated stimulus exceeds step or signal storage budget.");
        var destinations = design.Processes.Select(p => p.Destination).ToHashSet(StringComparer.Ordinal);
        if (design.Processes.Any(p => destinations.Contains(p.Clock))) throw new ArgumentException("Stimulus generation requires externally driven clocks.");
        var inputs = design.Signals.Where(s => !destinations.Contains(s.Name)).ToArray();
        if (inputs.Length == 0) throw new ArgumentException("Stimulus generation requires input signals.");
        uint state = seed == 0 ? 0x6D2B79F5u : seed;
        uint Next() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; }
        var drives = new List<IReadOnlyDictionary<string, FourStateWord>>();
        for (int step = 0; step < steps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var drive = new Dictionary<string, FourStateWord>(StringComparer.Ordinal);
            foreach (var signal in inputs)
            {
                ulong value = 0, x = 0, z = 0;
                for (int bit = 0; bit < signal.Initial.Width; bit++)
                    switch (Next() % (fourState ? 4u : 2u)) { case 1: value |= 1UL << bit; break; case 2: x |= 1UL << bit; break; case 3: z |= 1UL << bit; break; }
                drive.Add(signal.Name, new(signal.Initial.Width, value, x, z));
            }
            drives.Add(drive);
        }
        return new(design, drives, counts);
    }
}
