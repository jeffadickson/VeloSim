using NUnit.Framework;
namespace VeloSim.Tests;
public class NativeAbiTests
{
    [Test] public void NativeTicksCountOnlySuccessfulAtomicDrivesAndRespectDisposal()
    {
        var design = new IrDesign([new("clk", new FourStateWord(1))], []);
        var simulation = new Simulation(design);
        Assert.That(simulation.Ticks, Is.Zero);
        simulation.Drive(new Dictionary<string, FourStateWord>());
        Assert.That(simulation.Ticks, Is.EqualTo(1));
        Assert.Throws<ArgumentException>(() => simulation.Drive(new Dictionary<string, FourStateWord> { ["clk"] = new(2) }));
        Assert.That(simulation.Ticks, Is.EqualTo(1));
        simulation.Dispose(); Assert.Throws<ObjectDisposedException>(() => _ = simulation.Ticks);
    }
}
