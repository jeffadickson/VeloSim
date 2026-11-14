using SchematicLab.Core;
using Xunit;

public sealed class InteractionPolicyTests
{
    [Fact]
    public async Task UnrelatedMalformedDraftDoesNotChangeLockedControlBindings()
    {
        var session = new LessonSession(new(true, false), rcExample: true);
        session.Execute(new SetBridgeDraft(BridgeField.Diodes, "bad"));
        var result = await session.ExecuteBridgeAsync(new(session.Version, new(BridgeAnalysis.Dc, "P1")),
            (_, _) => Task.FromResult<IReadOnlyList<BridgeSample>>([new(0, 5)]));
        Assert.Equal(CommandCode.Ok, result.Code);
    }

    [Fact]
    public async Task LockedAnalysisCannotRebindCurrentControlledSources()
    {
        var session = new LessonSession();
        session.Execute(new LoadBridgeDemo(session.Version));
        Assert.Equal(CommandCode.Ok, session.Execute(new PlacePart(session.Version, Kind.Supply, 600, 330)).Code);
        session.Execute(new ConnectPins(session.Version, new("V2", 0), new("V1", 0)));
        session.Execute(new ConnectPins(session.Version, new("V2", 1), new("V1", 1)));
        session.Execute(new SetLayoutLocked(session.Version, true));
        var options = BridgeDemo.Options(BridgeAnalysis.Dc) with { Controls = [new("F1", "V2"), new("H1", "V1")] };
        var called = false;
        var result = await session.ExecuteBridgeAsync(new(session.Version, options), (_, _) =>
        {
            called = true;
            return Task.FromResult<IReadOnlyList<BridgeSample>>([new(0, 1)]);
        });
        Assert.Equal(CommandCode.LayoutLocked, result.Code);
        Assert.False(called);
        var unchanged = await session.ExecuteBridgeAsync(new(session.Version, BridgeDemo.Options(BridgeAnalysis.Dc)), (_, _) =>
            Task.FromResult<IReadOnlyList<BridgeSample>>([new(0, 1)]));
        Assert.Equal(CommandCode.Ok, unchanged.Code);
    }

    [Fact]
    public void MissingDeserializedPolicyNeverCreatesAnEditableSession() =>
        Assert.Throws<ArgumentNullException>(() => new LessonDeployment(null!, true).CreateSession());

    [FsCheck.Xunit.Property(MaxTest = 128, Replay = "111413,2026111401")]
    public bool ArbitraryMoveCoordinatesCannotChangeAnAuthorLockedCircuit(int x, int y)
    {
        var session = new LessonSession(new(true, false));
        var before = session.Execute(new SaveDocument()).Document;
        var version = session.Version;
        return session.Execute(new MovePart(version, "R1", x, y)).Code == CommandCode.LayoutLocked &&
            session.Version == version && session.Execute(new SaveDocument()).Document == before;
    }

    [Theory]
    [InlineData("bad", "rc")]
    [InlineData("author-locked", "bad")]
    public void InvalidDeploymentNeverFallsBackToAnEditableSession(string mode, string example) =>
        Assert.Throws<ArgumentException>(() => LessonDeployment.Parse(mode, example));

    [Fact]
    public void AuthorRcBootstrapRetainsPolicyAcrossSaveAndRefusedReload()
    {
        var deployment = LessonDeployment.Parse("author-locked", "rc");
        var session = new LessonSession(deployment.Policy, deployment.RcExample);
        Assert.Contains(session.Parts, part => part.Kind == Kind.Capacitor);
        var saved = session.Execute(new SaveDocument()).Document!;
        Assert.Equal(CommandCode.LayoutLocked, session.Execute(new LoadDocument(session.Version, saved)).Code);
        Assert.Equal(new InteractionState(true, false), session.Execute(new ReadInteractionPolicy()).Interaction);
        Assert.Equal(CommandCode.LayoutLocked, session.Execute(new ApplyAnalysisFields(
            new Dictionary<BridgeField, string> { [BridgeField.Controls] = "F1 V1" })).Code);
        Assert.Equal(CommandCode.Ok, session.Execute(new SetBridgeDraft(BridgeField.Step, ".00001")).Code);
    }

    [Fact]
    public void AuthorLockedLessonAllowsValuesAndRunsButRefusesStructuralCommandsAtomically()
    {
        var session = new LessonSession(new LessonInteractionPolicy(true, false));
        var version = session.Version;
        LessonCommand[] edits = [new MovePart(version, "R1", 100, 100),
            new DeletePart(version, "R1"), new ResetLesson(version), new ArmPlacement(Kind.Resistor),
            new LoadDocument(version, "{}"), new SetBridgeDraft(BridgeField.Controls, "F1 V1")];
        foreach (var edit in edits)
        {
            Assert.Equal(CommandCode.LayoutLocked, session.Execute(edit).Code);
            Assert.Equal(version, session.Version);
        }
        Assert.Equal(CommandCode.LayoutLocked, session.Execute(new SetLayoutLocked(version, false)).Code);
        Assert.Equal(CommandCode.Ok, session.Execute(new SetPartValue(version, "R2", 3000)).Code);
        Assert.Equal(3.75, session.Execute(new RunSweep(session.Version)).Run!.Samples[^1].Probe);
        Assert.Equal(CommandCode.StaleRevision, session.Execute(new SetPartValue(version, "R2", 4000)).Code);
    }

    [Fact]
    public void LockPreservesHistoryButOnlyParameterHistoryCanBeTraversedWhileLocked()
    {
        var session = new LessonSession();
        Assert.Equal(CommandCode.Ok, session.Execute(new MovePart(session.Version, "R1", 200, 50)).Code);
        Assert.Equal(CommandCode.Ok, session.Execute(new SetPartValue(session.Version, "R2", 3000)).Code);
        Assert.Equal(CommandCode.Ok, session.Execute(new SetLayoutLocked(session.Version, true)).Code);
        Assert.Equal(CommandCode.Ok, session.Execute(new UndoEdit(session.Version)).Code);
        Assert.Equal(1000, session.Parts.Single(p => p.Id == "R2").Value);
        Assert.Equal(CommandCode.LayoutLocked, session.Execute(new UndoEdit(session.Version)).Code);
        Assert.Equal(CommandCode.Ok, session.Execute(new RedoEdit(session.Version)).Code);
        Assert.Equal(CommandCode.Ok, session.Execute(new SetLayoutLocked(session.Version, false)).Code);
        Assert.Equal(CommandCode.Ok, session.Execute(new UndoEdit(session.Version)).Code);
        Assert.Equal(CommandCode.Ok, session.Execute(new UndoEdit(session.Version)).Code);
    }
}
