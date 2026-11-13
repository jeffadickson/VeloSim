using SchematicLab.Core;
using Xunit;

public sealed class CommandTests
{
    [Fact]
    public void StaleEditLeavesDocumentAndRunIntact()
    {
        var session = new LessonSession();
        var initial = session.Version;
        var changed = session.Execute(new SetPartValue(initial, "R2", 3000));
        Assert.Equal(CommandCode.Ok, changed.Code);
        var run = session.Execute(new RunSweep(changed.Version)).Run!;
        Assert.Equal(21, run.Samples.Count);
        Assert.Equal(3.75, run.Samples[^1].Probe);
        Assert.Equal(CommandCode.StaleRevision,
            session.Execute(new SetPartValue(initial, "R2", 9000)).Code);
        Assert.Equal(3000, session.Parts.Single(p => p.Id == "R2").Value);
        Assert.Equal(run, session.Execute(new ReadRun(run.Id)).Run);
    }

    [Fact]
    public async Task ConcurrentEditsToOneRevisionOnlyCommitOnce()
    {
        var session = new LessonSession();
        var version = session.Version;
        var edits = await Task.WhenAll(
            Task.Run(() => session.Execute(new SetPartValue(version, "R2", 2000))),
            Task.Run(() => session.Execute(new SetPartValue(version, "R2", 3000))));
        Assert.Single(edits, result => result.Code == CommandCode.Ok);
        Assert.Single(edits, result => result.Code == CommandCode.StaleRevision);
        Assert.Equal(version.Revision + 1, session.Version.Revision);
    }

    [Fact]
    public void InvalidCancelledAndWrongDocumentCommandsAreAtomic()
    {
        var session = new LessonSession();
        var version = session.Version;
        Assert.Equal(CommandCode.InvalidInput, session.Execute(new SetPartValue(version, "R2", double.NaN)).Code);
        Assert.Equal(CommandCode.InvalidInput, session.Execute(new ConnectPins(version, new("R1", 99), new("R2", 0))).Code);
        Assert.Equal(CommandCode.WrongDocument, session.Execute(new ResetLesson(new LessonSession().Version)).Code);
        Assert.Equal(CommandCode.Cancelled, session.Execute(new ResetLesson(version), new CancellationToken(true)).Code);
        Assert.Equal(version, session.Version);
        Assert.Equal(2.5, session.Probe);
    }
}
