using SchematicLab.Core;
using Xunit;

public sealed class HierarchyTests
{
    [Fact]
    public async Task CellReplacesSeriesResistorAndSurvivesReload()
    {
        var session = new LessonSession();
        var oldVersion = session.Version;
        Assert.Equal(CommandCode.Ok, session.Execute(new LoadHierarchyDemo(oldVersion)).Code);
        Assert.Equal(CommandCode.StaleRevision, session.Execute(new LoadHierarchyDemo(oldVersion)).Code);
        var child = session.Execute(new ReadChildSheet("X1")).Child!;
        var current = session.Version;
        Assert.Equal(CommandCode.InvalidInput, session.Execute(new AttachChildSheet(current, "X1",
            child with { RightPort = child.LeftPort })).Code);
        Assert.Equal(current, session.Version);
        Assert.Equal(CommandCode.Ok, session.Execute(new AttachChildSheet(current, "X1", child)).Code);
        var saved = session.Execute(new SaveDocument()).Document!;
        var restored = new LessonSession();
        Assert.Equal(CommandCode.Ok, restored.Execute(new LoadDocument(restored.Version, saved)).Code);
        var result = await restored.ExecuteBridgeAsync(new RunBridge(restored.Version,
            restored.BridgeDraft.Parse(BridgeAnalysis.Dc)!), (job, _) =>
        {
            Assert.Equal(2, job.Elements.Count(e => e.Kind == Kind.Resistor));
            Assert.DoesNotContain(job.Elements, e => e.Kind is Kind.CellInstance or Kind.InterfacePort);
            Assert.Contains(job.Elements, e => e.Id == "X1_X1_R1" && e.Value == 1000);
            return Task.FromResult<IReadOnlyList<BridgeSample>>([new(0, 3.75)]);
        });
        Assert.Equal(CommandCode.Ok, result.Code);
    }
}
