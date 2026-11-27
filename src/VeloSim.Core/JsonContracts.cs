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

internal sealed record LintLocationContract(string? File, int? Line, int? Column, string? Signal, string? Context);
internal sealed record LintDiagnosticContract(string Code, string Severity, string Message, string Source, LintLocationContract? Location, string? Fingerprint = null, string? Phase = null, string[]? GuidelineTags = null);
internal sealed record LintQualityContract(double Score, string Grade, int DesignElements);
internal sealed record FsmTransitionContract(string From, string To);
internal sealed record FsmSummaryContract(string StateSignal, string? NextStateSignal, int Width, string? ResetState, bool HasDefaultRecovery, string[] States, FsmTransitionContract[] Transitions, string[] UnreachableStates, string[] DeadlockStates, string Context, string Mermaid);
internal sealed record FsmDumpContract(string Schema, FsmSummaryContract[] Fsms);
internal sealed record ClockDomainContract(string ClockSignal, bool IsPrimaryInput, bool IsConstrainedInSdc, string[] Signals);
internal sealed record CdcCrossingContract(string Kind, string SourceSignal, string SourceDomain, string DestinationSignal, string DestinationDomain, int Width, bool Synchronized, int SynchronizerStages, bool FalsePath, string Context);
internal sealed record CdcDumpContract(string Schema, ClockDomainContract[] ClockDomains, string[] ResetSignals, CdcCrossingContract[] Crossings);

internal sealed record LintReportContract(string Schema, string Status, string Profile, bool Wall, bool WarningsAsErrors, int ErrorCount, int WarningCount, int InfoCount, int WaivedCount, int BaselinedCount, LintQualityContract Quality, string? StoppedAtPhase, LintDiagnosticContract[] Diagnostics, FsmSummaryContract[]? Fsms = null, CdcCrossingContract[]? CdcCrossings = null);

internal sealed record LintNamingConfigContract(string? SignalPattern, string? InputPattern, string? OutputPattern, string? InternalPattern, string? MemoryPattern, string? ClockPattern, string? ResetPattern);
internal sealed record LintRuleParamsContract(int? MaxIfDepth, string[]? UnusedIgnorePatterns, bool? AllowLiteralWidthExpand, int? MaxSignalWidth, int? MaxMemoryDepth, LintNamingConfigContract? Naming);
internal sealed record LintWaiverContract(string? Id, string Code, string? File, string? Signal, string? Context, string? Message, string? Reason, string? Owner, string? Expires);
internal sealed record LintConfigContract(string? Schema, string? Profile, bool? WarningsAsErrors, bool? WarnUnusedWaivers, string? SlangWaiverFile, string? SdcFile, string[]? Phases, bool? StopOnPhaseError, Dictionary<string, string>? Rules, Dictionary<string, string>? Annotations, LintRuleParamsContract? Params, LintWaiverContract[]? Waivers, string[]? RuleScripts = null, long? CameoMaxSteps = null, Dictionary<string, string>? CameoParams = null);

internal sealed record LintBaselineEntryContract(string Fingerprint, string Code, string Source, string? File, string? Signal, string? Context, string Message);
internal sealed record LintBaselineContract(string Schema, int Count, LintBaselineEntryContract[] Entries);

internal sealed record LintRuleInfoContract(string Code, bool DefaultEnabled, string DefaultSeverity, string Group, string Phase, string[] Standards, string Summary, string Hint);
internal sealed record LintRuleCatalogContract(string Schema, LintRuleInfoContract[] Rules);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(LintReportContract))]
[JsonSerializable(typeof(LintConfigContract))]
[JsonSerializable(typeof(LintBaselineContract))]
[JsonSerializable(typeof(LintRuleCatalogContract))]
[JsonSerializable(typeof(FsmDumpContract))]
[JsonSerializable(typeof(CdcDumpContract))]
internal partial class LintJsonContext : JsonSerializerContext { }

internal sealed record SarifMessageContract(string Text);
internal sealed record SarifRuleDefaultConfigContract(string Level);
internal sealed record SarifRulePropertiesContract(string[]? Tags, string? Phase);
internal sealed record SarifRuleContract(string Id, SarifMessageContract ShortDescription, SarifMessageContract? Help, SarifRuleDefaultConfigContract DefaultConfiguration, SarifRulePropertiesContract? Properties = null);
internal sealed record SarifDriverContract(string Name, string Version, string InformationUri, SarifRuleContract[] Rules);
internal sealed record SarifToolContract(SarifDriverContract Driver);
internal sealed record SarifArtifactLocationContract(string Uri);
internal sealed record SarifRegionContract(int StartLine, int? StartColumn);
internal sealed record SarifPhysicalLocationContract(SarifArtifactLocationContract ArtifactLocation, SarifRegionContract? Region);
internal sealed record SarifLogicalLocationContract(string Name, string? Kind, string? FullyQualifiedName);
internal sealed record SarifLocationContract(SarifPhysicalLocationContract? PhysicalLocation, SarifLogicalLocationContract[]? LogicalLocations);
internal sealed record SarifResultContract(string RuleId, string Level, SarifMessageContract Message, SarifLocationContract[]? Locations, Dictionary<string, string>? PartialFingerprints);
internal sealed record SarifRunContract(SarifToolContract Tool, SarifResultContract[] Results);
internal sealed record SarifLogContract(
    [property: JsonPropertyName("$schema")] string Schema,
    string Version,
    SarifRunContract[] Runs);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SarifLogContract))]
internal partial class SarifJsonContext : JsonSerializerContext { }
