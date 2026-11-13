using SchematicLab.Core;
using Xunit;

public sealed class SessionPersistenceTests
{
    [Fact]
    public void VersionOneSheetsLoadWithAnEmptySession()
    {
        var session = new LessonSession();
        var old = System.Text.Json.Nodes.JsonNode.Parse(session.Execute(new SaveDocument()).Document!)!.AsObject();
        old["schemaVersion"] = 1;
        old.Remove("session");
        session.Execute(new RunSweep(session.Version));
        session.Execute(new HighlightNet(new Pin("P1", 0)));
        Assert.Equal(CommandCode.Ok, session.Execute(new LoadDocument(session.Version, old.ToJsonString())).Code);
        Assert.Equal(2.5, session.Probe);
        Assert.Empty(session.History.Sweeps);
        Assert.Null(session.View.Highlight);
    }

    [Fact]
    public void NetHighlightFollowsJunctionsAndIgnoresCoincidentBends()
    {
        var session = new LessonSession();
        var first = session.Wires[0];
        var second = session.Wires[1];
        var bend = new SheetPoint(250, 145);
        session.Execute(new SetWireRoute(session.Version, first, [bend]));
        session.Execute(new SetWireRoute(session.Version, second, [bend]));
        var separate = session.Execute(new HighlightNet(first.A)).Net!;
        Assert.DoesNotContain(second, separate.Wires);
        session.Execute(new JoinRoutes(session.Version, first, second, bend));
        Assert.Contains(second, session.HighlightedNet!.Wires);
        Assert.Single(session.HighlightedNet.Junctions);
        Assert.Equal(CommandCode.NotFound, session.Execute(new HighlightNet(new Pin("missing", 0))).Code);
        Assert.Equal(first.A, session.View.Highlight);
        session.Execute(new HighlightNet(null));
        Assert.Null(session.HighlightedNet);
    }

    [Fact]
    public async Task ReloadRestoresNetViewAndHistoryWithOriginalRunProvenance()
    {
        var source = new LessonSession();
        var sweep = source.Execute(new RunSweep(source.Version)).Run!;
        source.Execute(new LoadRc(source.Version));
        var probe = new Pin("P1", 0);
        source.Execute(new SetNetLabel(source.Version, probe, "out"));
        source.Execute(new SetNetLabel(source.Version, new Pin("R1", 1), "OUT"));
        source.Execute(new DeleteWire(source.Version, source.Wires.Single(wire => wire.B == probe)));
        source.Execute(new SelectPart("P1"));
        source.Execute(new ToggleCatalog());
        source.Execute(new SetCatalogFilter("resistor"));
        var highlighted = source.Execute(new HighlightNet(probe));
        Assert.Equal(source.Version, highlighted.Version);
        Assert.Equal(new[] { new Pin("C1", 0), probe, new Pin("R1", 1) },
            highlighted.Net!.Terminals.OrderBy(pin => pin.Part));
        var run = (await source.ExecuteTransientAsync(new RunTransient(source.Version, "P1"), (input, _) =>
            Task.FromResult<IReadOnlyList<TransientSample>>([new(0, 0), new(input.StopSeconds, 4.96)]))).Transient!;

        var target = new LessonSession();
        var saved = source.Execute(new SaveDocument()).Document!;
        Assert.Equal(CommandCode.Ok, target.Execute(new LoadDocument(target.Version, saved)).Code);
        var restored = target.Execute(new ReadRun(run.Id)).Transient!;
        Assert.Equal(run.Version, restored.Version);
        Assert.Equal(run.Samples, restored.Samples);
        Assert.Equal(sweep.Samples, target.Execute(new ReadRun(sweep.Id)).Run!.Samples);
        Assert.True(target.IsCurrent(restored.Id, restored.Version));
        Assert.False(target.IsCurrent(sweep.Id, sweep.Version));
        Assert.Equal(probe, target.View.Highlight);
        Assert.Equal("P1", target.View.Selected);
        Assert.True(target.View.CatalogOpen);
        Assert.Equal("resistor", target.View.Filter);
        Assert.Equal(saved, target.Execute(new SaveDocument()).Document);

        var invalid = saved.Replace("\"probeId\":\"P1\"", "\"probeId\":null", StringComparison.Ordinal);
        Assert.NotEqual(saved, invalid);
        Assert.Equal(CommandCode.InvalidInput, target.Execute(new LoadDocument(target.Version, invalid)).Code);
        Assert.Equal(saved, target.Execute(new SaveDocument()).Document);
        Assert.Equal(CommandCode.Ok, target.Execute(new UndoEdit(target.Version)).Code);
        Assert.Null(target.View.Highlight);
        Assert.Equal(CommandCode.NotFound, target.Execute(new ReadRun(run.Id)).Code);
        Assert.Equal(CommandCode.Ok, target.Execute(new RedoEdit(target.Version)).Code);
        Assert.True(target.IsCurrent(restored.Id, restored.Version));
        target.Execute(new SetPartValue(target.Version, "R1", 2000));
        Assert.False(target.IsCurrent(restored.Id, restored.Version));
    }
}
