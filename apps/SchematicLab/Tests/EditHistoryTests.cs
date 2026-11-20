using SchematicLab.Core;
using Xunit;

public sealed class EditHistoryTests
{
    [Fact]
    public void GeometryEditsPreserveProbeAndHistoryUsesFreshRevisions()
    {
        var session = new LessonSession();
        var original = session.Version;
        var wires = session.Wires.ToArray();
        Assert.Equal(CommandCode.Ok, session.Execute(new MovePart(original, "R1", 200, 75)).Code);
        Assert.Equal(CommandCode.Ok, session.Execute(new RotatePart(session.Version, "R1")).Code);
        Assert.Equal(CommandCode.Ok, session.Execute(new MirrorPart(session.Version, "R1")).Code);
        Assert.Equal(wires, session.Wires.ToArray());
        Assert.Equal(2.5, session.Probe);
        var part = session.Parts.Single(p => p.Id == "R1");
        Assert.Equal((200, 75, 1, true), (part.X, part.Y, part.QuarterTurns, part.Mirrored));
        Assert.Equal((232, 75), part.PinPosition(0));

        Assert.Equal(CommandCode.Ok, session.Execute(new UndoEdit(session.Version)).Code);
        Assert.False(session.Parts.Single(p => p.Id == "R1").Mirrored);
        Assert.Equal(CommandCode.StaleRevision, session.Execute(new RedoEdit(original)).Code);
        Assert.Equal(CommandCode.Ok, session.Execute(new RedoEdit(session.Version)).Code);
        Assert.True(session.Parts.Single(p => p.Id == "R1").Mirrored);
        Assert.Equal(original.Revision + 5, session.Version.Revision);
    }

    [Fact]
    public void DuplicateCopiesValueAndGeometryWithoutWiresAndBranchesHistory()
    {
        var session = new LessonSession();
        session.Execute(new SetPartValue(session.Version, "R2", 3000));
        var duplicate = session.Execute(new DuplicatePart(session.Version, "R2", 200, 215)).Part!;
        Assert.Equal("R3", duplicate.Id);
        Assert.Equal(3000, duplicate.Value);
        Assert.DoesNotContain(session.Wires, w => w.A.Part == duplicate.Id || w.B.Part == duplicate.Id);
        session.Execute(new UndoEdit(session.Version));
        Assert.Equal(3.75, session.Probe);
        Assert.Equal(CommandCode.InvalidInput, session.Execute(new MovePart(session.Version, "R2", 350, 75)).Code);
        Assert.True(session.CanRedo);
        session.Execute(new SetPartValue(session.Version, "R2", 2000));
        Assert.False(session.CanRedo);
    }
}
