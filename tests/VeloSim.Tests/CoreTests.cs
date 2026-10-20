using NUnit.Framework;

namespace VeloSim.Tests;

public class CoreTests
{
    private const string Ir = """{"format":"velosim.ir.v1","signals":[{"name":"clk","width":1},{"name":"count","width":64,"value":18446744073709551615}],"processes":[{"clock":"clk","destination":"count","source":"count","reset":"clk","edge":"posedge","operation":"increment"}]}""";
    private const string Normalized = """{"format":"velosim.uhdm.normalized.v1","signals":[{"name":"clk","width":1},{"name":"rst_n","width":1},{"name":"count","width":8}],"processes":[{"kind":"always_ff","event":{"edge":"posedge","signal":"clk"},"body":{"kind":"if","condition":{"kind":"not","operand":{"kind":"ref","name":"rst_n"}},"then":{"kind":"nba","target":"count","value":{"kind":"const","value":0}},"else":{"kind":"nba","target":"count","value":{"kind":"add","lhs":{"kind":"ref","name":"count"},"rhs":{"kind":"const","value":1}}}}}]}""";

    [Test] public void FourStateWordsValidateMasksAndFullWidth()
    {
        Assert.That(new FourStateWord(64, ulong.MaxValue).Mask, Is.EqualTo(ulong.MaxValue));
        Assert.That(new FourStateWord(8, 0, 1).IsKnown, Is.False);
        Assert.Throws<ArgumentOutOfRangeException>(() => new FourStateWord(0));
        Assert.Throws<ArgumentException>(() => new FourStateWord(8, 256));
        Assert.Throws<ArgumentException>(() => new FourStateWord(8, 0, 1, 1));
    }
    [Test] public void SignalParsingRejectsInvalidSchemaAndDuplicateKeys()
    {
        Assert.That(IrDesign.Parse(Ir).Signals[1].Initial.Value, Is.EqualTo(ulong.MaxValue));
        foreach (var bad in new[] { Ir.Replace("\"width\":64", "\"width\":true"), Ir.Replace("\"width\":64", "\"width\":64,\"width\":1"), Ir.Replace("18446744073709551615", "-1"), Ir.Replace("18446744073709551615", "18446744073709551616"), Ir.Replace("\"width\":64", "\"width\":64,\"extra\":1") })
            Assert.Throws<IrValidationException>(() => IrDesign.Parse(bad));
    }
    [Test] public void ProcessValidationRejectsUnknownReferencesAndMultipleWriters()
    {
        Assert.Throws<IrValidationException>(() => IrDesign.Parse(Ir.Replace("\"source\":\"count\"", "\"source\":\"missing\"")));
        var d = IrDesign.Parse(Ir);
        Assert.Throws<IrValidationException>(() => new IrDesign(d.Signals, [d.Processes[0], d.Processes[0]]));
        Assert.Throws<IrValidationException>(() => IrDesign.Parse(Ir.Replace("posedge", "anyedge")));
    }
    [Test] public void WriterRoundTripsUnsignedValuesMasksAndEscapedNames()
    {
        var design = new IrDesign([new("quote\"\n", new(64, ulong.MaxValue, 1, 2))], []);
        var roundtrip = IrDesign.Parse(design.ToJson());
        Assert.That(roundtrip.Signals, Is.EqualTo(design.Signals));
        Assert.That(roundtrip.ToJson(), Is.EqualTo(design.ToJson()));
    }
    [Test] public void NormalizedLowererPreservesUnknownInitialStateAndRejectsUnsupportedTrees()
    {
        var design = NormalizedUhdm.Lower(Normalized);
        Assert.That(design.Signals[2].Initial.XMask, Is.EqualTo(255));
        Assert.That(design.Processes[0].Operation, Is.EqualTo(IrOperation.SvIfResetIncrementStrict));
        foreach (var bad in new[] { Normalized.Replace("always_ff", "always_comb"), Normalized.Replace("\"value\":1", "\"value\":2"), Normalized.Replace("\"kind\":\"nba\"", "\"kind\":\"blocking\""), Normalized.Replace("\"kind\":\"add\"", "\"kind\":\"add\",\"ignored\":true") })
            Assert.Throws<IrValidationException>(() => NormalizedUhdm.Lower(bad));
    }
}
