using SchematicLab.Core;
using Xunit;

public sealed class SweepTests
{
    [Fact]
    public void DcSweepReportsProbeVoltageAndRefusesAnOpenCircuit()
    {
        var board = Board.Divider();
        Assert.True(board.SetValue("R2", 3000));
        var sweep = board.Sweep();
        Assert.Equal(21, sweep.Count);
        Assert.Equal((0d, 0d), sweep[0]);
        Assert.Equal((2.5, 1.875), sweep[10]);
        Assert.Equal((5d, 3.75), sweep[^1]);
        board.Remove("G1");
        Assert.Empty(board.Sweep());
    }
}
