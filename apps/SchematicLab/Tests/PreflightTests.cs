using SchematicLab.Core;
using Xunit;

public sealed class PreflightTests
{
    [Fact]
    public void MissingGroundAndDanglingTerminalsHaveStructuredFindings()
    {
        var session = new LessonSession();
        session.Execute(new DeletePart(session.Version, "G1"));
        var revision = session.Version;
        var result = session.Execute(new CheckCircuit());
        Assert.Contains(result.Findings!, f => f.Code == FindingCode.MissingGround && f.Blocking);
        Assert.Contains(result.Findings!, f => f.Code == FindingCode.DanglingTerminal && f.Terminal == new Pin("V1", 1));
        Assert.Contains(result.Findings!, f => f.Code == FindingCode.DanglingTerminal && f.Terminal == new Pin("R2", 1));
        Assert.Equal(revision, session.Version);
    }

    [Fact]
    public void BothBuiltInCircuitsPassPreflight()
    {
        var session = new LessonSession();
        Assert.Empty(session.Execute(new CheckCircuit()).Findings!);
        session.Execute(new LoadRc(session.Version));
        Assert.Empty(session.Execute(new CheckCircuit()).Findings!);
    }
}
