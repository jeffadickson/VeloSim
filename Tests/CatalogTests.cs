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
            Assert.NotEmpty(spec.Terminals);
            Assert.Equal(spec.Terminals.Count, spec.Terminals.Select(t => t.Name).Distinct().Count());
            Assert.Equal(spec.Terminals.Count, spec.Terminals.Select(t => (t.X,t.Y)).Distinct().Count());
            var part = board.Place(kind, 100, 100);
            Assert.True(ids.Add(part.Id));
            Assert.Equal(spec.Terminals.Count, part.PinCount);
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
