using NUnit.Framework;
using System.Text.Json;

namespace VeloSim.Tests;

public class WordFixtureTests
{
    private static IrDesign Design() => new([new("clk", new(1)), new("rst_n", new(1)), new("src", new(8)), new("a", new(8)), new("b", new(8))], [new("clk", "a", "src", "rst_n", IrEdge.Posedge, IrOperation.SvIfResetIncrementStrict), new("clk", "b", "a", "rst_n", IrEdge.Posedge, IrOperation.Copy)]);
    private static WordFixture Fixture(IEnumerable<WordCheckpoint>? checkpoints = null) => new(Design(), [new Dictionary<string, FourStateWord> { ["clk"] = new(1), ["rst_n"] = new(1, 1), ["src"] = new(8, 4) }, new Dictionary<string, FourStateWord> { ["clk"] = new(1, 1) }], [1, 4], checkpoints);
    [Test] public void StrictWordFixtureRoundTripsAndRejectsDuplicateSignalsAndWrongWidths()
    {
        var fixture = Fixture(); var json = fixture.ToJson(); var parsed = WordFixture.Parse(json);
        Assert.That(parsed.ToJson(), Is.EqualTo(json)); Assert.That(parsed.Design.Signals.Count, Is.EqualTo(5));
        Assert.Throws<IrValidationException>(() => WordFixture.Parse(json.Replace("\"src\": \"00000100\"", "\"src\":\"00000100\",\"src\":\"00000101\"")));
        Assert.Throws<IrValidationException>(() => WordFixture.Parse(json.Replace("00000100", "100")));
        var drive = new Dictionary<string, FourStateWord> { ["clk"] = new(1) }; var immutable = new WordFixture(Design(), [drive], [1]); drive["clk"] = new(1, 1); Assert.That(immutable.Drives[0]["clk"].Value, Is.Zero);
    }
    [Test] public void CheckpointsValidateExplicitNativeSignalAndActivationExpectations()
    {
        var correct = Fixture([new(1, new Dictionary<string, FourStateWord> { ["a"] = new(8, 5), ["b"] = new(8) }, 2)]);
        Assert.That(WordFixtureRunner.Run(correct).All(r => r.Failure is null), Is.True);
        var wrong = Fixture([new(1, new Dictionary<string, FourStateWord> { ["a"] = new(8, 6) }, 2)]);
        var results = WordFixtureRunner.Run(wrong); Assert.That(results[0].Failure!.Kind, Is.EqualTo("checkpoint_signal_mismatch")); Assert.That(results[0].Failure!.Expected, Is.EqualTo("00000110"));
        using var report = JsonDocument.Parse(WordFixtureRunner.ReportJson(results)); Assert.That(report.RootElement.GetProperty("status").GetString(), Is.EqualTo("fail"));
        Assert.Throws<IrValidationException>(() => Fixture([new(99, new Dictionary<string, FourStateWord> { ["a"] = new(8) }, null)]));
    }
    [Test] public async Task WordCheckCliWritesStructuredReportAndKeepsExistingArtifacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "velosim-word-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "fixture.json"); var report = Path.Combine(directory, "report.json"); File.WriteAllText(path, Fixture().ToJson());
            Assert.That(await ToolCli.RunAsync(["word-check", path, "--report", report], new StringWriter(), new StringWriter()), Is.Zero);
            using var json = JsonDocument.Parse(File.ReadAllText(report)); Assert.That(json.RootElement.GetProperty("results").GetArrayLength(), Is.EqualTo(2));
            Assert.That(await ToolCli.RunAsync(["word-check", path, "--report", report], new StringWriter(), new StringWriter()), Is.EqualTo(1));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public async Task SeededWordGenerationIsDeterministicAndRunsThroughCliConformance()
    {
        var first = IrStimulus.Generate(Design(), 100, 1234, true, [1, 4]); var again = IrStimulus.Generate(Design(), 100, 1234, true, [1, 4]);
        Assert.That(first.ToJson(), Is.EqualTo(again.ToJson())); Assert.That(first.Drives.SelectMany(d => d.Values).Any(w => !w.IsKnown), Is.True); Assert.That(WordFixtureRunner.Run(first).All(r => r.Failure is null), Is.True);
        var directory = Path.Combine(Path.GetTempPath(), "velosim-generate-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var design = Path.Combine(directory, "design.json"); var fixture = Path.Combine(directory, "fixture.json"); File.WriteAllText(design, Design().ToJson());
            Assert.That(await ToolCli.RunAsync(["word-generate", design, "--output", fixture, "--steps", "20", "--seed", "1234", "--workers", "1,4", "--four-state"], new StringWriter(), new StringWriter()), Is.Zero);
            Assert.That(WordFixture.Parse(File.ReadAllText(fixture)).Drives.Count, Is.EqualTo(20));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Test] public void WorkBudgetAndAllDrivesAreValidatedBeforeOpeningNativeSimulation()
    {
        var fixture = Fixture(); var tiny = new ConformanceLimits(MaxSignalChecks: 1);
        Assert.Throws<ArgumentException>(() => IrConformance.Run(fixture.Design, fixture.Drives, fixture.Workers, limits: tiny));
        Assert.Throws<ArgumentException>(() => IrConformance.Run(fixture.Design, [fixture.Drives[0], new Dictionary<string, FourStateWord> { ["unknown"] = new(1) }], [1]));
        Assert.Throws<ArgumentException>(() => IrStimulus.Generate(Design(), 1_000_000, 1, true));
        Assert.That(ConformanceLimits.Default.Validate(fixture.Design, fixture.Drives, fixture.Workers), Is.EqualTo(20));
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); Assert.Throws<OperationCanceledException>(() => IrConformance.Run(fixture.Design, fixture.Drives, fixture.Workers, cancel.Token));
    }
}
