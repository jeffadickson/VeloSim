using System.Text.Json.Serialization;

namespace VeloSim;

// Closed report contracts use generated serializers: NativeAOT must not depend
// on reflection discovering anonymous objects or runtime-derived DTO types.
internal sealed record CliErrorContract(string Schema, string Command, int ExitCode, string Category, string Message);
internal sealed record ExternalPortContract(string Name, int Width, bool Signed, bool TwoState, string Direction, string Member);
internal sealed record ExternalInputContract(string Path, string Sha256);
internal sealed record ExternalManifestContract(string Schema, int WordAbi, int MaxWordBits, string Top, string ModelClass, string ExecutionScope, ExternalPortContract[] Ports, ExternalInputContract[] Inputs);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(CliErrorContract))]
internal partial class CliJsonContext : JsonSerializerContext { }

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(ExternalManifestContract))]
internal partial class ManifestJsonContext : JsonSerializerContext { }
