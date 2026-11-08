using SchematicLab.Core;
using Xunit;

public sealed class CatalogTests
{
    [Fact]
    public void EveryPartHasAUniqueDefinitionAndDrawableTerminals()
    {
        var board = new Board();
        var ids = new HashSet<string>();
        foreach (var kind in Enum.GetValues<Kind>())
        {
            var spec = PartCatalog.Get(kind);
            Assert.NotEmpty(spec.Artwork);
            if (kind != Kind.CoupledInductor) Assert.NotEmpty(spec.Terminals);
            Assert.Equal(spec.Terminals.Count, spec.Terminals.Select(t => t.Name).Distinct().Count());
            Assert.Equal(spec.Terminals.Count, spec.Terminals.Select(t => (t.X,t.Y)).Distinct().Count());
            var part = board.Place(kind, 100, 100);
            Assert.True(ids.Add(part.Id));
            Assert.Equal(spec.Terminals.Count, part.PinCount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PolarizedCapacitorRequiresForwardOrientationInTheNativeRcProfile(bool reversed)
    {
        var session = new LessonSession();
        session.Execute(new LoadRc(session.Version));
        session.Execute(new DeletePart(session.Version, "C1"));
        var placed = session.Execute(new PlacePart(session.Version, Kind.PolarizedCapacitor, 350, 215));
        var id = placed.Part!.Id;
        Assert.Equal(new[] { "Positive", "Negative" }, PartCatalog.Get(Kind.PolarizedCapacitor).Terminals.Select(pin => pin.Name));
        Assert.Equal(CommandCode.Ok, session.Execute(new SetPartValue(session.Version, id, 2e-6)).Code);
        session.Execute(new ConnectPins(session.Version, new(id, reversed ? 1 : 0), new("R1", 1)));
        session.Execute(new ConnectPins(session.Version, new(id, reversed ? 0 : 1), new("G1", 0)));
        var called = false;
        var run = await session.ExecuteTransientAsync(new RunTransient(session.Version, "P1"), (input, _) =>
        {
            called = true;
            Assert.Equal(2e-6, input.Farads);
            return Task.FromResult<IReadOnlyList<TransientSample>>([new(0, 0), new(input.StopSeconds, 4.96)]);
        });
        Assert.Equal(reversed ? CommandCode.UnsupportedCircuit : CommandCode.Ok, run.Code);
        Assert.Equal(!reversed, called);
    }

    [Fact]
    public void ExtendedPaletteRetainsMultiTerminalOrderAndSavesDrawableSymbols()
    {
        Assert.Equal(new[] { "Drain", "Gate", "Source", "Back gate", "Body contact" },
            PartCatalog.Get(Kind.NmosSoi).Terminals.Select(pin => pin.Name));
        Assert.Equal(6, PartCatalog.Get(Kind.CoupledTransmissionLine).Terminals.Count);
        Assert.Empty(PartCatalog.Get(Kind.CoupledInductor).Terminals);
        Assert.Equal("I", PartCatalog.Get(Kind.PwlFileCurrent).Prefix);

        var kinds = Enum.GetValues<Kind>();
        Assert.Equal(90, kinds.Length);
        foreach (var kind in kinds)
        {
            var session = new LessonSession();
            var placed = session.Execute(new PlacePart(session.Version, kind, 500, 320));
            Assert.Equal(CommandCode.Ok, placed.Code);
            var saved = session.Execute(new SaveDocument()).Document!;
            Assert.Equal(CommandCode.Ok, session.Execute(new LoadDocument(session.Version, saved)).Code);
            Assert.Contains(session.Parts, part => part.Kind == kind && part.Id == placed.Part!.Id);
        }
    }

    [Fact]
    public void SemiconductorTerminalsRetainSpiceOrder()
    {
        Assert.Equal(new[] { "Collector", "Base", "Emitter" },
            PartCatalog.Get(Kind.Npn).Terminals.Select(t => t.Name));
        Assert.Equal(new[] { "Drain", "Gate", "Source", "Bulk" },
            PartCatalog.Get(Kind.Pmos).Terminals.Select(t => t.Name));
        Assert.False(PartCatalog.Get(Kind.Nmos).DividerSupported);
    }
}
