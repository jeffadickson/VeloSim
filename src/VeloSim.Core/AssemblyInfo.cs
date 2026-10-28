using System.Runtime.CompilerServices;

// Parser/serializer contracts are internal product details, but their observable
// argument and wire behavior is exercised directly without launching HDL tools.
[assembly: InternalsVisibleTo("VeloSim.Tests")]
