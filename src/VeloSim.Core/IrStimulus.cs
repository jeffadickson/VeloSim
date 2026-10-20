namespace VeloSim;

public static class IrStimulus
{
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
