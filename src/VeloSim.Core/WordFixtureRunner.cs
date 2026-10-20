using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed record WordFixtureFailure(int Step, string Signal, string? Expected, string? Actual, string Kind);
public sealed record WordFixtureResult(int Workers, int CompletedSteps, WordFixtureFailure? Failure);
public static class WordFixtureRunner
{
    public static IReadOnlyList<WordFixtureResult> Run(WordFixture fixture, bool compareOracle = true, CancellationToken cancellationToken = default)
    {
        ConformanceLimits.Default.Validate(fixture.Design, fixture.Drives, fixture.Workers, cancellationToken);
        var checkpoints = fixture.Checkpoints.ToDictionary(c => c.Step); var results = new List<WordFixtureResult>();
        foreach (var workers in fixture.Workers)
        {
            cancellationToken.ThrowIfCancellationRequested(); using var native = new Simulation(fixture.Design); var oracle = compareOracle ? new IrOracle(fixture.Design) : null;
            WordFixtureFailure? failure = null; int completed = 0;
            for (int step = 0; step < fixture.Drives.Count; step++)
            {
                cancellationToken.ThrowIfCancellationRequested(); var active = native.Drive(fixture.Drives[step], workers); completed++;
                if (oracle is not null)
                {
                    var expected = oracle.Drive(fixture.Drives[step]);
                    if (active != expected) failure = new(step, "", expected.ToString(System.Globalization.CultureInfo.InvariantCulture), active.ToString(System.Globalization.CultureInfo.InvariantCulture), "oracle_activation_mismatch");
                    foreach (var signal in fixture.Design.Signals)
                    {
                        var actual = native.Read(signal.Name); var wanted = oracle.Read(signal.Name);
                        if (actual != wanted) { failure ??= new(step, signal.Name, FourStateCodec.FormatBinary(wanted), FourStateCodec.FormatBinary(actual), "oracle_signal_mismatch"); break; }
                    }
                }
                if (failure is null && checkpoints.TryGetValue(step, out var checkpoint))
                {
                    if (checkpoint.ActivatedProcesses is { } expected && active != expected) failure = new(step, "", expected.ToString(System.Globalization.CultureInfo.InvariantCulture), active.ToString(System.Globalization.CultureInfo.InvariantCulture), "checkpoint_activation_mismatch");
                    foreach (var (signal, wanted) in checkpoint.Signals)
                    {
                        var actual = native.Read(signal);
                        if (FourStateCodec.Resize(wanted, wanted.Width) != actual) { failure ??= new(step, signal, FourStateCodec.FormatBinary(wanted), FourStateCodec.FormatBinary(actual), "checkpoint_signal_mismatch"); break; }
                    }
                }
                if (failure is not null) break;
            }
            results.Add(new(workers, completed, failure));
        }
        return results.AsReadOnly();
    }
    public static string ReportJson(IReadOnlyList<WordFixtureResult> results)
    {
        using var stream = new MemoryStream(); using (var w = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            w.WriteStartObject(); w.WriteString("schema", "velosim.word.report.v1"); w.WriteString("status", results.Count > 0 && results.All(r => r.Failure is null) ? "pass" : "fail");
            w.WriteStartArray("results"); foreach (var result in results)
            {
                w.WriteStartObject(); w.WriteNumber("workers", result.Workers); w.WriteNumber("completed_steps", result.CompletedSteps);
                if (result.Failure is { } failure) { w.WriteStartObject("failure"); w.WriteNumber("step", failure.Step); w.WriteString("signal", failure.Signal); w.WriteString("expected", failure.Expected); w.WriteString("actual", failure.Actual); w.WriteString("kind", failure.Kind); w.WriteEndObject(); } else w.WriteNull("failure"); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
