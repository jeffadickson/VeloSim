using SchematicLab.Core;
using Xunit;

public sealed class WiringDocumentTests
{
    [Fact]
    public void CoincidentBendsJoinOnlyWhenExplicitlyJunctioned()
    {
        var session = new LessonSession();
        var wires = session.Wires;
        var crossing = new SheetPoint(250, 145);
        Assert.Equal(CommandCode.Ok, session.Execute(new SetWireRoute(session.Version, wires[0], [crossing])).Code);
        Assert.Equal(CommandCode.Ok, session.Execute(new SetWireRoute(session.Version, wires[1], [crossing])).Code);
        Assert.Equal(2.5, session.Probe);
        Assert.Equal(CommandCode.Ok, session.Execute(new JoinRoutes(session.Version, wires[0], wires[1], crossing)).Code);
        Assert.Null(session.Probe);
        Assert.Equal(CommandCode.InvalidInput, session.Execute(new SetWireRoute(session.Version, wires[0], [])).Code);
        Assert.Equal(CommandCode.Ok, session.Execute(new UndoEdit(session.Version)).Code);
        Assert.Equal(2.5, session.Probe);
    }

    [Fact]
    public async Task NamedConnectionsAndRoutesRoundTripAtomically()
    {
        var session = new LessonSession();
        session.Execute(new LoadRc(session.Version));
        var output = new Pin("R1", 1);
        var probe = new Pin("P1", 0);
        var probeWire = session.Wires.Single(wire => wire.B == probe);
        Assert.Equal(CommandCode.Ok, session.Execute(new SetNetLabel(session.Version, output, "output")).Code);
        Assert.Equal(CommandCode.Ok, session.Execute(new SetNetLabel(session.Version, probe, "OUTPUT")).Code);
        Assert.Equal(CommandCode.Ok, session.Execute(new DeleteWire(session.Version, probeWire)).Code);
        var first = session.Wires[0];
        Assert.Equal(CommandCode.Ok, session.Execute(new SetWireRoute(session.Version, first, [new(280, 40)])).Code);
        var saved = session.Execute(new SaveDocument()).Document!;
        session.Execute(new ResetLesson(session.Version));
        Assert.Equal(CommandCode.Ok, session.Execute(new LoadDocument(session.Version, saved)).Code);
        Assert.Equal(saved, session.Execute(new SaveDocument()).Document);
        Assert.Equal(CommandCode.InvalidInput, session.Execute(new LoadDocument(session.Version, "{\"schemaVersion\":99}")).Code);
        Assert.Equal(saved, session.Execute(new SaveDocument()).Document);
        var run = await session.ExecuteTransientAsync(new RunTransient(session.Version, "P1"), (input, _) =>
            Task.FromResult<IReadOnlyList<TransientSample>>([new(0, 0), new(input.StopSeconds, 4.96)]));
        Assert.Equal(CommandCode.Ok, run.Code);
    }

    [Fact]
    public void TruncatedAndInvalidDocumentsPreserveTheRevisionAndUndo()
    {
        var session = new LessonSession();
        session.Execute(new SetNetLabel(session.Version, new Pin("V1", 0), "input"));
        var version = session.Version;
        var saved = session.Execute(new SaveDocument()).Document!;
        var random = new Random(110821);
        for (var trial = 0; trial < 128; trial++)
        {
            var truncated = saved[..random.Next(saved.Length)];
            Assert.Equal(CommandCode.InvalidInput, session.Execute(new LoadDocument(version, truncated)).Code);
        }

        var invalid = saved.Replace("\"part\":\"V1\",\"index\":0", "\"part\":\"V1\",\"index\":99", StringComparison.Ordinal);
        Assert.NotEqual(saved, invalid);
        Assert.Equal(CommandCode.InvalidInput, session.Execute(new LoadDocument(version, invalid)).Code);
        Assert.Equal(version, session.Version);
        Assert.Equal(saved, session.Execute(new SaveDocument()).Document);
        Assert.Equal(CommandCode.Ok, session.Execute(new UndoEdit(version)).Code);
        Assert.Empty(session.Labels);
    }

}