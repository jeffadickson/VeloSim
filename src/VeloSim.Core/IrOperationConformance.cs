using System.Text;
using System.Text.Json;

namespace VeloSim;

public sealed record IrOperationCaseResult(string Name, IrOperation Operation, int DestinationWidth, IReadOnlyList<IrConformanceResult> Results, string FixturePath)
{
    public bool Passed => Results.Count > 0 && Results.All(r => r.Mismatch is null);
}
public static class IrOperationConformance
{
    public static IReadOnlyList<IrOperationCaseResult> Run(string outputDirectory, IReadOnlyList<int> workers, CancellationToken cancellationToken = default)
    {
        if (workers.Count is < 1 or > 256 || workers.Distinct().Count() != workers.Count || workers.Any(w => w is < 1 or > 256)) throw new ArgumentException("Invalid word operation worker matrix.");
        cancellationToken.ThrowIfCancellationRequested(); var output = Path.GetFullPath(outputDirectory); if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Word operation conformance output must be empty."); Directory.CreateDirectory(output);
        var cases = new List<IrOperationCaseResult>();
        foreach (var operation in new[] { IrOperation.And, IrOperation.Or, IrOperation.Xor, IrOperation.Not, IrOperation.UnsignedAdd, IrOperation.UnsignedSubtract, IrOperation.UnsignedMultiply })
        foreach (var dimensions in new[] { (A: 1, B: 1, Dst: 1), (A: 8, B: 8, Dst: 8), (A: 16, B: 16, Dst: 16), (A: 64, B: 64, Dst: 64), (A: 64, B: 1, Dst: 8) })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = IrDesign.OperationNames[(int)operation] + "-" + dimensions.A + "-" + dimensions.B + "-" + dimensions.Dst; var directory = Path.Combine(output, name); Directory.CreateDirectory(directory);
            var design = new IrDesign([new("clk", new(1)), new("rst", new(1, 1)), new("a", new(dimensions.A)), new("b", new(dimensions.B)), new("q", new(dimensions.Dst)), new("history", new(dimensions.Dst))], [new("clk", "q", "a", "rst", IrEdge.Posedge, operation, "b"), new("clk", "history", "q", "rst", IrEdge.Posedge, IrOperation.Copy)]);
            var fixture = IrStimulus.GenerateBoundaries(design, workers); var fixturePath = Path.Combine(directory, "fixture.json"); File.WriteAllText(fixturePath, fixture.ToJson());
            var results = IrConformance.Run(design, fixture.Drives, workers, cancellationToken); cases.Add(new(name, operation, dimensions.Dst, results, fixturePath));
        }
        using var stream = new MemoryStream(); using (var w = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            w.WriteStartObject(); w.WriteString("schema", "velosim.word.operations.conformance.v1"); w.WriteNumber("native_ir_abi", 2); w.WriteString("status", cases.All(c => c.Passed) ? "pass" : "fail"); w.WriteStartArray("cases");
            foreach (var test in cases)
            {
                w.WriteStartObject(); w.WriteString("name", test.Name); w.WriteString("operation", IrDesign.OperationNames[(int)test.Operation]); w.WriteNumber("destination_width", test.DestinationWidth); w.WriteString("status", test.Passed ? "pass" : "fail"); w.WriteString("fixture_path", test.FixturePath); w.WriteStartArray("workers");
                foreach (var result in test.Results) { w.WriteStartObject(); w.WriteNumber("count", result.Workers); w.WriteNumber("steps", result.Steps); if (result.Mismatch is { } mismatch) { w.WriteNumber("first_mismatch_step", mismatch.Step); w.WriteString("signal", mismatch.Signal); w.WriteString("expected", mismatch.Expected is { } expected ? FourStateCodec.FormatBinary(expected) : null); w.WriteString("actual", mismatch.Actual is { } actual ? FourStateCodec.FormatBinary(actual) : null); } w.WriteEndObject(); }
                w.WriteEndArray(); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        }
        File.WriteAllText(Path.Combine(output, "word-operations-report.json"), Encoding.UTF8.GetString(stream.ToArray()) + "\n"); return cases.AsReadOnly();
    }
}
