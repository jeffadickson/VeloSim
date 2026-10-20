using NUnit.Framework;

namespace VeloSim.Tests;

public class WordOracleTests
{
    [Test] public void BitwiseWordOraclePreservesFourStateDominanceAndCopiesHighImpedance()
    {
        var a = FourStateCodec.ParseBinary("01xz"); var b = FourStateCodec.ParseBinary("10z1");
        Assert.That(FourStateCodec.FormatBinary(FourStateLogic.Evaluate(ScalarOperation.And, a, b)), Is.EqualTo("00xx"));
        Assert.That(FourStateCodec.FormatBinary(FourStateLogic.Evaluate(ScalarOperation.Or, a, b)), Is.EqualTo("11x1"));
        Assert.That(FourStateLogic.Evaluate(ScalarOperation.Copy, a, b), Is.EqualTo(a));
        Assert.Throws<ArgumentException>(() => FourStateLogic.Evaluate(ScalarOperation.And, a, new(8)));
    }
    [Test] public void ArithmeticOracleMatchesConcretePossibilitiesAndStrictResetSemantics()
    {
        for (int encoded = 0; encoded < 64; encoded++)
        {
            int code = encoded; var digits = new char[3];
            for (int i = 0; i < digits.Length; i++) { digits[i] = "01xz"[code % 4]; code /= 4; }
            var word = FourStateCodec.ParseBinary(new string(digits));
            var candidates = Enumerable.Range(0, 8).Where(n => (((ulong)n ^ word.Value) & ~(word.XMask | word.ZMask) & 7) == 0).Select(n => (n + 1) & 7).ToArray();
            ulong ones = 7, zeros = 7; foreach (var candidate in candidates) { ones &= (ulong)candidate; zeros &= (ulong)~candidate & 7; }
            Assert.That(FourStateArithmetic.Increment(word, 3), Is.EqualTo(new FourStateWord(3, ones, 7 & ~(ones | zeros))));
        }
        Assert.That(FourStateArithmetic.Increment(new(64, ulong.MaxValue), 64), Is.EqualTo(new FourStateWord(64)));
        Assert.That(FourStateCodec.FormatBinary(FourStateArithmetic.Increment(FourStateCodec.ParseBinary("0x01"), 4, true)), Is.EqualTo("xxxx"));
        Assert.That(FourStateCodec.FormatBinary(FourStateArithmetic.ResetIncrement(new(4, 1), new(1, 0, 1), 4, false, false)), Is.EqualTo("00x0"));
        Assert.That(FourStateArithmetic.ResetIncrement(new(4, 1), new(1, 0, 1), 4, true, false).Value, Is.EqualTo(2));
    }
    [Test] public void BinaryCodecRoundTripsWidth64AndCanonicalizesResize()
    {
        var bits = new string('1', 62) + "xz"; Assert.That(FourStateCodec.FormatBinary(FourStateCodec.ParseBinary(bits)), Is.EqualTo(bits));
        Assert.That(FourStateCodec.FormatBinary(FourStateCodec.Resize(FourStateCodec.ParseBinary("1xz0"), 6)), Is.EqualTo("001xz0"));
        Assert.That(FourStateCodec.FormatBinary(FourStateCodec.Resize(FourStateCodec.ParseBinary("1xz0"), 2)), Is.EqualTo("z0"));
        Assert.That(FourStateCodec.Resize(new(4, 15, 1, 2), 4).Value, Is.EqualTo(12));
        Assert.Throws<ArgumentException>(() => FourStateCodec.ParseBinary(new string('1', 65)));
    }
    private static IrDesign Pipeline(int width, IrOperation first = IrOperation.Copy) => new([new("clk", new(1)), new("reset", new(1, 1)), new("src", new(width)), new("a", new(width)), new("b", new(width))], [new("clk", "a", "src", "reset", IrEdge.Posedge, first), new("clk", "b", "a", "reset", IrEdge.Posedge, IrOperation.Copy)]);
    [Test] public void IndependentIrOracleEvaluatesAllProcessesBeforeNbaCommit()
    {
        var oracle = new IrOracle(Pipeline(8));
        oracle.Drive(new Dictionary<string, FourStateWord> { ["src"] = new(8, 7) });
        Assert.That(oracle.Drive(new Dictionary<string, FourStateWord> { ["clk"] = new(1, 1) }), Is.EqualTo(2));
        Assert.That(oracle.Read("a").Value, Is.EqualTo(7)); Assert.That(oracle.Read("b").Value, Is.Zero);
        oracle.Drive(new Dictionary<string, FourStateWord> { ["clk"] = new(1), ["src"] = new(8, 9) });
        oracle.Drive(new Dictionary<string, FourStateWord> { ["clk"] = new(1, 0, 0, 1) });
        Assert.That(oracle.Read("a").Value, Is.EqualTo(9)); Assert.That(oracle.Read("b").Value, Is.EqualTo(7));
    }
    [Test] public void NativeWordConformanceCoversExhaustiveIncrementAndFourStateWorkerDeterminism()
    {
        var drives = new List<IReadOnlyDictionary<string, FourStateWord>>();
        for (int value = 0; value < 256; value++)
        {
            drives.Add(new Dictionary<string, FourStateWord> { ["clk"] = new(1), ["src"] = new(8, (ulong)value) }); drives.Add(new Dictionary<string, FourStateWord> { ["clk"] = new(1, 1) });
        }
        var binary = IrConformance.Run(Pipeline(8, IrOperation.Increment), drives, [1, 4, 8]); Assert.That(binary.All(r => r.Mismatch is null && r.Steps == 512), Is.True);
        foreach (var operation in new[] { IrOperation.ResetIncrement, IrOperation.SvIfResetIncrement, IrOperation.SvIncrement, IrOperation.SvIfResetIncrementStrict })
        {
            var unknownDrives = new List<IReadOnlyDictionary<string, FourStateWord>>();
            foreach (var bits in new[] { "00000000", "11111111", "0000x101", "z0000001" }) foreach (var reset in new[] { new FourStateWord(1), new(1, 1), new(1, 0, 1), new(1, 0, 0, 1) })
            {
                unknownDrives.Add(new Dictionary<string, FourStateWord> { ["clk"] = new(1), ["src"] = FourStateCodec.ParseBinary(bits), ["reset"] = reset }); unknownDrives.Add(new Dictionary<string, FourStateWord> { ["clk"] = new(1, 1) });
            }
            var results = IrConformance.Run(Pipeline(8, operation), unknownDrives, [1, 4]); Assert.That(results.All(r => r.Mismatch is null), Is.True, operation.ToString());
        }
        Assert.That(IrConformance.Run(Pipeline(64, IrOperation.Increment), [new Dictionary<string, FourStateWord> { ["src"] = new(64, ulong.MaxValue), ["clk"] = new(1, 1) }], [1, 4]).All(r => r.Mismatch is null), Is.True);
    }
}
