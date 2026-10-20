using NUnit.Framework;
using System.Text.Json.Nodes;

namespace VeloSim.Tests;

public class IrV2Tests
{
    private static IrDesign Design(IrOperation operation, int a = 8, int b = 8, int destination = 8) => new([new("clk", new(1)), new("rst", new(1, 1)), new("a", new(a)), new("b", new(b)), new("q", new(destination)), new("history", new(destination))], [new("clk", "q", "a", "rst", IrEdge.Posedge, operation, "b"), new("clk", "history", "q", "rst", IrEdge.Posedge, IrOperation.Copy)]);
    private static FourStateWord Native(IrOperation operation, FourStateWord a, FourStateWord b, int destination)
    {
        using var native = new Simulation(Design(operation, a.Width, b.Width, destination)); native.Drive(new Dictionary<string, FourStateWord> { ["a"] = a, ["b"] = b }); Assert.That(native.Drive(new Dictionary<string, FourStateWord> { ["clk"] = new(1, 1) }, 4), Is.EqualTo(2)); return native.Read("q");
    }
    [Test] public void TypedSecondOperandRequiresKnownSignalAndKeepsLegacyDesignsV1()
    {
        var design = Design(IrOperation.And); Assert.That(design.Format, Is.EqualTo("velosim.ir.v2")); Assert.That(design.Processes[0].SourceB, Is.EqualTo("b"));
        Assert.Throws<IrValidationException>(() => new IrDesign(design.Signals, [design.Processes[0] with { SourceB = null }]));
        Assert.Throws<IrValidationException>(() => new IrDesign(design.Signals, [design.Processes[0] with { SourceB = "missing" }]));
        Assert.That(new IrDesign(design.Signals, [design.Processes[1]]).Format, Is.EqualTo("velosim.ir.v1"));
    }
    [Test] public void StrictIrV2RoundTripsEverySecondSourceAndRejectsV1WordOpsAndMissingFields()
    {
        var json = Design(IrOperation.UnsignedAdd).ToJson(); Assert.That(IrDesign.Parse(json).ToJson(), Is.EqualTo(json));
        var node = JsonNode.Parse(json)!; node["processes"]![0]!.AsObject().Remove("source_b"); Assert.Throws<IrValidationException>(() => IrDesign.Parse(node.ToJsonString()));
        Assert.Throws<IrValidationException>(() => IrDesign.Parse(json.Replace("velosim.ir.v2", "velosim.ir.v1")));
        node = JsonNode.Parse(json)!; node["processes"]![1]!["source_b"] = null; Assert.Throws<IrValidationException>(() => IrDesign.Parse(node.ToJsonString()));
    }
    [Test] public void ManagedV2BindingUsesSecondSourceAndPreservesNbaPrecommitAndTickCounts()
    {
        using var native = new Simulation(Design(IrOperation.Or)); native.Drive(new Dictionary<string, FourStateWord> { ["a"] = new(8, 1), ["b"] = new(8, 2), ["q"] = new(8, 7) });
        Assert.That(native.Drive(new Dictionary<string, FourStateWord> { ["clk"] = new(1, 1) }, 8), Is.EqualTo(2)); Assert.That(native.Read("q").Value, Is.EqualTo(3)); Assert.That(native.Read("history").Value, Is.EqualTo(7)); Assert.That(native.Ticks, Is.EqualTo(2));
    }
    [Test] public void WordBitwiseOperationsResizeOperandsAndRetainKnownDominatingBits()
    {
        var left = FourStateCodec.ParseBinary("000000000000000000000000000000000000000000000000000000000000xz10"); var right = new FourStateWord(1, 1);
        foreach (var pair in new[] { (IrOperation.And, ScalarOperation.And), (IrOperation.Or, ScalarOperation.Or), (IrOperation.Xor, ScalarOperation.Xor), (IrOperation.Not, ScalarOperation.Not) })
        {
            var expected = FourStateLogic.Evaluate(pair.Item2, FourStateCodec.Resize(left, 8), pair.Item1 == IrOperation.Not ? FourStateCodec.Resize(left, 8) : FourStateCodec.Resize(right, 8)); Assert.That(Native(pair.Item1, left, right, 8), Is.EqualTo(expected));
        }
    }
    [Test] public void StrictUnsignedAdditionWrapsAndPropagatesUnknownBitsBeforeTruncation()
    {
        Assert.That(Native(IrOperation.UnsignedAdd, new(64, ulong.MaxValue), new(1, 1), 64), Is.EqualTo(new FourStateWord(64)));
        var highUnknown = new FourStateWord(64, 1, 1UL << 63); Assert.That(Native(IrOperation.UnsignedAdd, highUnknown, new(1, 1), 8), Is.EqualTo(new FourStateWord(8, 0, 255)));
        Assert.That(UnsignedWordArithmetic.Evaluate(IrOperation.UnsignedAdd, new(8, 255), new(8, 2), 8).Value, Is.EqualTo(1));
    }
    [Test] public void StrictUnsignedSubtractionUsesDestinationModuloForUnderflow()
    {
        Assert.That(Native(IrOperation.UnsignedSubtract, new(1), new(8, 1), 8).Value, Is.EqualTo(255));
        Assert.That(Native(IrOperation.UnsignedSubtract, new(64), new(64, 1), 64).Value, Is.EqualTo(ulong.MaxValue));
        Assert.That(Native(IrOperation.UnsignedSubtract, new(8, 7), new(8, 0, 0, 1), 8).XMask, Is.EqualTo(255));
    }
    [Test] public void StrictUnsignedMultiplicationUsesWideIndependentOracleAndUnknownRules()
    {
        Assert.That(Native(IrOperation.UnsignedMultiply, new(64, ulong.MaxValue), new(64, ulong.MaxValue), 64).Value, Is.EqualTo(1));
        Assert.That(Native(IrOperation.UnsignedMultiply, new(16, 300), new(8, 3), 8).Value, Is.EqualTo(132));
        Assert.That(Native(IrOperation.UnsignedMultiply, new(64, 1, 1UL << 63), new(1), 8).XMask, Is.EqualTo(255));
    }
    [Test] public async Task WordFixtureV2RoundTripsAndRunsThroughCompiledConformanceCli()
    {
        var fixture = IrStimulus.Generate(Design(IrOperation.Xor), 50, 42, true, [1, 4]); var json = fixture.ToJson(); Assert.That(json, Does.Contain("velosim.word.fixture.v2")); Assert.That(WordFixture.Parse(json).ToJson(), Is.EqualTo(json));
        Assert.Throws<IrValidationException>(() => WordFixture.Parse(json.Replace("velosim.word.fixture.v2", "velosim.word.fixture.v1")));
        var file = Path.GetTempFileName();
        try { File.WriteAllText(file, json); Assert.That(await ToolCli.RunAsync(["word-check", file], new StringWriter(), new StringWriter()), Is.Zero); }
        finally { File.Delete(file); }
    }
    [Test] public async Task BoundaryGeneratorAndCliCoverOriginalOperandHighUnknownsAndBothClockTransitions()
    {
        var design = Design(IrOperation.UnsignedAdd, 64, 1, 8); var fixture = IrStimulus.GenerateBoundaries(design, [1, 4]); Assert.That(fixture.Drives.Count, Is.EqualTo(147)); Assert.That(fixture.Drives.Any(d => d.TryGetValue("a", out var value) && value.XMask == 1UL << 63), Is.True); Assert.That(WordFixtureRunner.Run(fixture).All(r => r.Failure is null), Is.True);
        var directory = Path.Combine(Path.GetTempPath(), "velosim-v2-boundaries-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var ir = Path.Combine(directory, "design.json"); var path = Path.Combine(directory, "fixture.json"); File.WriteAllText(ir, design.ToJson()); Assert.That(await ToolCli.RunAsync(["word-boundaries", ir, "--output", path, "--workers", "1,4"], new StringWriter(), new StringWriter()), Is.Zero); Assert.That(WordFixture.Parse(File.ReadAllText(path)).Drives.Count, Is.EqualTo(147));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public void CompiledWordOperationSuiteRunsAllSevenOperationsAndWidthResizeCases()
    {
        var directory = Path.Combine(Path.GetTempPath(), "velosim-v2-suite-" + Guid.NewGuid().ToString("N"));
        try
        {
            var results = IrOperationConformance.Run(directory, [1, 4]); Assert.That(results.Count, Is.EqualTo(35)); Assert.That(results.All(r => r.Passed && File.Exists(r.FixturePath)), Is.True); Assert.That(File.Exists(Path.Combine(directory, "word-operations-report.json")), Is.True);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
