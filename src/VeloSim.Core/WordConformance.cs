using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed record WordConformanceCase(string Name, WordFixture Fixture);
public sealed record WordConformanceResult(string Name, string Status, string ReportPath);
public static class WordConformance
{
    public static IReadOnlyList<WordConformanceCase> Cases(IReadOnlyList<int> workers)
    {
        if (workers.Count is < 1 or > 256 || workers.Distinct().Count() != workers.Count || workers.Any(w => w is < 1 or > 256)) throw new ArgumentException("Invalid strict word conformance workers.");
        var cases = new List<WordConformanceCase>();
        foreach (var operation in new[] { IrOperation.Copy, IrOperation.SvIncrement, IrOperation.SvIfResetIncrementStrict, IrOperation.And, IrOperation.Or, IrOperation.Xor, IrOperation.Not, IrOperation.UnsignedAdd, IrOperation.UnsignedSubtract, IrOperation.UnsignedMultiply })
        foreach (var dimension in new[] { (A: 1, B: 1, D: 1), (A: 8, B: 8, D: 8), (A: 16, B: 16, D: 16), (A: 64, B: 64, D: 64), (A: 64, B: 1, D: 8) })
        {
            var design = new IrDesign([new("clk", new(1)), new("rst", new(1, 1)), new("a", new(dimension.A)), new("b", new(dimension.B)), new("q", new(dimension.D)), new("history", new(dimension.D))], [new("clk", "q", "a", "rst", IrEdge.Posedge, operation, "b"), new("clk", "history", "q", "rst", IrEdge.Posedge, IrOperation.Copy)]);
            cases.Add(new(IrDesign.OperationNames[(int)operation] + "-" + dimension.A + "-" + dimension.B + "-" + dimension.D, IrStimulus.GenerateBoundaries(design, workers)));
        }
        var resetDesign = new IrDesign([new("clk", new(1)), new("rst", new(1, 1)), new("a", new(64)), new("q", new(8)), new("history", new(8))], [new("clk", "q", "a", "rst", IrEdge.Posedge, IrOperation.SvIfResetIncrementStrict), new("clk", "history", "q", "rst", IrEdge.Posedge, IrOperation.Copy)]);
        var resetDrives = new List<IReadOnlyDictionary<string, FourStateWord>>();
        foreach (var reset in new[] { new FourStateWord(1), new(1, 1), new(1, 0, 1), new(1, 0, 0, 1) }) foreach (var source in new[] { new FourStateWord(64), new(64, 5), new(64, 1, 1UL << 63), new(64, 0, 0, 1UL << 63) })
        { resetDrives.Add(new Dictionary<string, FourStateWord> { ["clk"] = new(1), ["rst"] = reset, ["a"] = source }); resetDrives.Add(new Dictionary<string, FourStateWord> { ["clk"] = new(1, 1) }); }
        cases.Add(new("procedural-reset-zero-one-x-z", new(resetDesign, resetDrives, workers)));
        var mixedDesign = new IrDesign([new("clk_a", new(1)), new("clk_b", new(1, 1)), new("rst", new(1, 1)), new("a", new(8)), new("b", new(4)), new("q", new(8)), new("history", new(8))], [new("clk_a", "q", "a", "rst", IrEdge.Posedge, IrOperation.UnsignedAdd, "b"), new("clk_b", "history", "q", "rst", IrEdge.Negedge, IrOperation.Copy)]);
        var random = IrStimulus.Generate(mixedDesign, 500, 0xC0C07B, true, workers).Drives;
        var mixed = new List<IReadOnlyDictionary<string, FourStateWord>> { new Dictionary<string, FourStateWord> { ["clk_a"] = new(1), ["clk_b"] = new(1, 1), ["a"] = new(8, 3), ["b"] = new(4, 2) }, new Dictionary<string, FourStateWord> { ["clk_a"] = new(1, 1), ["clk_b"] = new(1) } }; mixed.AddRange(random);
        cases.Add(new("seeded-multiclock-nba", new(mixedDesign, mixed, workers, [new(1, new Dictionary<string, FourStateWord> { ["q"] = new(8, 5), ["history"] = new(8) }, 2)])));
        return cases.AsReadOnly();
    }
    public static async Task<IReadOnlyList<WordConformanceResult>> RunAsync(string outputDirectory, IReadOnlyList<int> workers, IProcessRunner? runner = null, string? compiler = null, string? runtime = null, CancellationToken cancellationToken = default)
    {
        var tests = Cases(workers); cancellationToken.ThrowIfCancellationRequested(); var directory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("Word conformance output must be empty."); Directory.CreateDirectory(directory);
        var results = new List<WordConformanceResult>();
        foreach (var test in tests) { cancellationToken.ThrowIfCancellationRequested(); var output = Path.Combine(directory, test.Name); var report = await new WordDifferential(runner).RunAsync(test.Fixture, output, compiler, runtime, cancellationToken).ConfigureAwait(false); results.Add(new(test.Name, report.Status, Path.Combine(output, "word-differential-report.json"))); }
        using var stream = new MemoryStream(); using (var w = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            w.WriteStartObject(); w.WriteString("schema", "velosim.word.hdl.conformance.v1"); w.WriteString("status", results.Any(r => r.Status == "fail") ? "fail" : results.Any(r => r.Status != "pass") ? "incomplete" : "pass"); w.WriteStartArray("cases"); foreach (var result in results) { w.WriteStartObject(); w.WriteString("name", result.Name); w.WriteString("status", result.Status); w.WriteString("report_path", result.ReportPath); w.WriteEndObject(); } w.WriteEndArray(); w.WriteEndObject();
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "word-conformance-report.json"), Encoding.UTF8.GetString(stream.ToArray()) + "\n", cancellationToken).ConfigureAwait(false); return results.AsReadOnly();
    }
}
