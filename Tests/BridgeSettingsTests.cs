using SchematicLab.Core;
using Xunit;

public sealed class BridgeSettingsTests
{
    [Fact]
    public void ReloadRestoresModelAndSourceDraftsAndUndoRestoresPreviousSettings()
    {
        var source = new LessonSession();
        source.Execute(new LoadBjtDemo(source.Version));
        source.Execute(new SetBridgeDraft(BridgeField.Pwl, "V2 0 .55 1e-5 .68"));
        source.Execute(new SetBridgeDraft(BridgeField.Pulses, ""));
        var text = source.Execute(new SaveDocument()).Document!;

        var target = new LessonSession();
        var original = target.BridgeDraft[BridgeField.Pwl];
        Assert.Equal(CommandCode.Ok, target.Execute(new LoadDocument(target.Version, text)).Code);
        var options = target.BridgeDraft.Parse(BridgeAnalysis.Transient)!;
        Assert.Equal(3e-10, Assert.Single(options.Bjts!).BaseEmitter!.Farads);
        Assert.Equal(new WavePoint(1e-5, .68), Assert.Single(options.Pwl!).Points[1]);
        Assert.Equal(CommandCode.Ok, target.Execute(new UndoEdit(target.Version)).Code);
        Assert.Equal(original, target.BridgeDraft[BridgeField.Pwl]);
    }
}
