using SchematicLab.Core;
using Xunit;

public sealed class BridgeSettingsTests
{
    [Fact]
    public void NamedModelSurvivesReloadAndBindsToAnotherCompatibleInstance()
    {
        var session = new LessonSession();
        session.Execute(new LoadBjtDemo(session.Version));
        var model = new NamedModel("SmallSignal", BridgeField.Bjts, "1e-15 75 1 .025 .025");
        Assert.Equal(CommandCode.Ok, session.Execute(new DefineNamedModel(model)).Code);
        Assert.Equal(CommandCode.InvalidInput, session.Execute(new ApplyNamedModel("SmallSignal", "R1")).Code);
        var saved = session.Execute(new SaveDocument()).Document!;
        var imported = new LessonSession();
        Assert.Equal(CommandCode.Ok, imported.Execute(new LoadDocument(imported.Version, saved)).Code);
        Assert.Equal(model, Assert.Single(imported.Execute(new ReadNamedModels()).Models!));
        Assert.Equal(CommandCode.Ok, imported.Execute(new ApplyNamedModel("smallsignal", "Q1")).Code);
        Assert.Equal(75, Assert.Single(imported.BridgeDraft.Parse(BridgeAnalysis.Dc)!.Bjts!).ForwardBeta);
        Assert.Equal(CommandCode.InvalidInput,
            imported.Execute(new DefineNamedModel(model with { Parameters = "NaN 75 1 .025 .025" })).Code);
    }

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
