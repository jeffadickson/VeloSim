using System.Text;
using System.Text.Json;

namespace VeloSim;

public static class WordSeededConformance
{
    public static async Task<string> RunAsync(string outputDirectory, uint seed, int steps, IReadOnlyList<int> workers, int repetitions = 2, IProcessRunner? runner = null, string? compiler = null, string? runtime = null, CancellationToken cancellationToken = default)
    {
        var dimensions = new[] { (1, 1), (8, 8), (16, 16), (64, 64), (64, 8) };
        var fixtures = dimensions.Select(d => WordSeededStimulus.Generate(seed, steps, d.Item1, d.Item2, workers, cancellationToken)).ToArray();
        if (repetitions is < 2 or > 16 || (long)steps * workers.Sum(w => (long)w) * repetitions * fixtures.Length > 1_000_000) throw new ArgumentException("Seeded conformance exceeds repetition/worker-step budget.");
        var directory = WordEvidenceLimits.FreshOutput(outputDirectory); cancellationToken.ThrowIfCancellationRequested(); Directory.CreateDirectory(directory);
        var results = new List<(string Name, WordDifferentialReport Report, IReadOnlyList<WordDeterminismResult> Determinism)>();
        foreach (var (dimension, fixture) in dimensions.Zip(fixtures))
        {
            cancellationToken.ThrowIfCancellationRequested(); var name = "seeded-" + dimension.Item1 + "-" + dimension.Item2; var report = await new WordDifferential(runner).RunAsync(fixture, Path.Combine(directory, name), compiler, runtime, cancellationToken).ConfigureAwait(false);
            var deterministic = report.Status == "pass" ? WordDeterminism.Run(fixture, repetitions, cancellationToken) : [];
            results.Add((name, report, deterministic));
        }
        string status = results.Any(r => r.Report.Status == "fail" || r.Determinism.Any(d => !d.MatchesOracle || !d.MatchesFirstRun)) ? "fail" : results.Any(r => r.Report.Status != "pass") ? "incomplete" : "pass";
        using var stream = new MemoryStream(); using (var w = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            w.WriteStartObject(); w.WriteString("schema", "velosim.word.seeded.conformance.v1"); w.WriteString("status", status); w.WriteString("generator", "xorshift32_known_and_four_state_phases_v1"); w.WriteNumber("seed", seed); w.WriteNumber("steps", steps); w.WriteNumber("repetitions", repetitions); w.WriteStartArray("cases");
            foreach (var result in results)
            {
                w.WriteStartObject(); w.WriteString("name", result.Name); w.WriteString("status", result.Report.Status); w.WriteString("report_path", Path.Combine(directory, result.Name, "word-differential-report.json")); w.WriteStartArray("determinism");
                foreach (var check in result.Determinism) { w.WriteStartObject(); w.WriteNumber("workers", check.Workers); w.WriteNumber("repetition", check.Repetition); w.WriteBoolean("matches_oracle", check.MatchesOracle); w.WriteBoolean("matches_first_run", check.MatchesFirstRun); w.WriteEndObject(); }
                w.WriteEndArray(); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "word-seeded-conformance-report.json"), Encoding.UTF8.GetString(stream.ToArray()) + "\n", cancellationToken).ConfigureAwait(false); return status;
    }
}
