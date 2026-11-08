using SchematicLab.Core;
using Xunit;

public sealed class SoiBridgeTests
{
    [Fact]
    public async Task FdProfileRequiresDcAndAnUnconnectedBodyContact()
    {
        var session = new LessonSession();
        session.Execute(new LoadSoiDemo(session.Version));
        var options = BridgeDemo.SoiOptions();
        var dc = await session.ExecuteBridgeAsync(new RunBridge(session.Version, options), (job, _) =>
        {
            var soi = Assert.Single(job.Elements, element => element.Kind == Kind.NmosSoi);
            Assert.Equal(0, soi.N);
            Assert.NotEqual(0, soi.Bulk);
            Assert.Equal(job.ProbeNode, job.Nodes[soi.P]);
            return Task.FromResult<IReadOnlyList<BridgeSample>>([new(0, .8)]);
        });
        Assert.Equal(CommandCode.Ok, dc.Code);
        var ac = await session.ExecuteBridgeAsync(new RunBridge(session.Version, options with
        {
            Analysis = BridgeAnalysis.Ac, FrequenciesHz = [1000]
        }), Never);
        Assert.Equal(CommandCode.UnsupportedCircuit, ac.Code);
        session.Execute(new SetNetLabel(session.Version, new Pin("M1", 4), "0"));
        var connected = await session.ExecuteBridgeAsync(new RunBridge(session.Version, options), Never);
        Assert.Equal(CommandCode.UnsupportedCircuit, connected.Code);
    }

    private static Task<IReadOnlyList<BridgeSample>> Never(CircuitJob job, CancellationToken token) =>
        throw new InvalidOperationException("Unsupported SOI request reached the solver.");
}
