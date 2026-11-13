using SchematicLab.Core;
using Xunit;

public sealed class LessonTests
{
    [Fact]
    public void DividerRespondsToValueChanges()
    {
        var board = Board.Divider();
        Assert.Equal(2.5, board.Output());
        Assert.True(board.SetValue("R2", 3000));
        Assert.Equal(3.75, board.Output());
    }

    [Fact]
    public void DeletingPartRemovesOnlyItsWires()
    {
        var board = Board.Divider();
        Assert.True(board.Remove("R1"));
        Assert.Equal(2, board.Wires.Count);
        Assert.Null(board.Output());
    }

    [Fact]
    public void CoordinatesAloneDoNotConnectParts()
    {
        var board = new Board();
        board.Place(Kind.Supply, 60, 60);
        board.Place(Kind.Resistor, 60, 60);
        Assert.False(board.Connected(new("V1", 0), new("R1", 0)));
    }

    [Fact]
    public void InvalidEditsPreserveDocument()
    {
        var board = Board.Divider();
        Assert.False(board.Connect(new("R1", 2), new("R2", 1)));
        Assert.False(board.Connect(new("R1", 0), new("R1", 0)));
        Assert.False(board.Connect(new("R1", 0), new("V1", 0)));
        Assert.False(board.SetValue("R1", double.NaN));
        Assert.False(board.SetValue("R1", 0));
        Assert.False(board.SetValue("R1", -1));
        Assert.Equal(5, board.Wires.Count);
        Assert.Equal(2.5, board.Output());
    }

    [Fact]
    public void SeededDividerValuesMatchIndependentEquation()
    {
        var random = new Random(1108);
        var board = Board.Divider();
        for (var i = 0; i < 256; i++)
        {
            var upper = random.Next(1, 100_000);
            var lower = random.Next(1, 100_000);
            var supply = random.Next(1, 100);
            Assert.True(board.SetValue("R1", upper));
            Assert.True(board.SetValue("R2", lower));
            Assert.True(board.SetValue("V1", supply));
            Assert.Equal(supply / (1.0 + (double)upper / lower), board.Output()!.Value, 12);
        }
    }
}
