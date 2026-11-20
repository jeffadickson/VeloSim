using SchematicLab.Core;
using Xunit;
using System.Text.Json;
using SchematicLab.Solver;

public sealed class BackendSelectionTests
{
    [Fact]
    public async Task ExplicitOverrideIsRetainedWithoutChangingTheSessionDefault()
    {
        var session = new LessonSession(new(true, false), rcExample: true);
        session.Execute(new SetSolverBackend(session.Version, SolverBackend.Faer));
        var result = await session.ExecuteBridgeAsync(new(session.Version, new(BridgeAnalysis.Dc, "P1", Backend: SolverBackend.Lu)), (job, _) =>
        {
            Assert.Equal(SolverBackend.Lu, job.Options.Backend);
            return Task.FromResult<IReadOnlyList<BridgeSample>>([new(0, 0)]);
        });
        Assert.Equal(SolverBackend.Lu, result.Bridge!.Backend);
        Assert.Equal(SolverBackend.Faer, session.SelectedBackend);
        var refused = await session.ExecuteBridgeAsync(new(session.Version, new(BridgeAnalysis.Dc, "P1", Backend: (SolverBackend)99)),
            (_, _) => throw new InvalidOperationException("Undefined adapter must not execute."));
        Assert.Equal(CommandCode.InvalidInput, refused.Code);
    }

    [Fact]
    public async Task QrAcRefusesBeforeSolverInvocationAndDiscoveryExplainsTheLimit()
    {
        var session = new LessonSession(rcExample: true);
        session.Execute(new SetSolverBackend(session.Version, SolverBackend.Qr));
        var discovery = session.Execute(new ReadSolverBackends());
        Assert.False(discovery.BackendCapabilities!.Single(provider => provider.Backend == SolverBackend.Qr).SupportsAc);
        var result = await session.ExecuteBridgeAsync(new(session.Version, new(BridgeAnalysis.Ac, "P1", [100])),
            (_, _) => throw new InvalidOperationException("Unsupported provider must not run."));
        Assert.Equal(CommandCode.UnsupportedCircuit, result.Code);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("faer ")]
    [InlineData("LU")]
    public void InvalidBackendImportIsAtomic(string backend)
    {
        var session = new LessonSession();
        var before = session.Execute(new SaveDocument()).Document;
        var result = session.Execute(new ApplyAnalysisFields(new Dictionary<BridgeField, string>
        { [BridgeField.Probe] = "changed", [BridgeField.Backend] = backend }));
        Assert.Equal(CommandCode.InvalidInput, result.Code);
        Assert.Equal(before, session.Execute(new SaveDocument()).Document);
    }

    [Fact]
    public async Task SavedCurrentFaerRcWaveformRestoresWithoutRelabelingTheProvider()
    {
        var session = new LessonSession(rcExample: true);
        session.Execute(new SetSolverBackend(session.Version, SolverBackend.Faer));
        await session.ExecuteTransientAsync(new(session.Version, "P1"), (input, _) =>
            Task.FromResult<IReadOnlyList<TransientSample>>([new(0, 0), new(input.StopSeconds, 5)]));
        var saved = session.Execute(new SaveDocument()).Document!;
        var loaded = new LessonSession();
        Assert.Equal(CommandCode.Ok, loaded.Execute(new LoadDocument(loaded.Version, saved)).Code);
        Assert.Equal(SolverBackend.Faer, loaded.History.Transients.Single().Input.Backend);
        Assert.Contains(loaded.History.Transients.Single().Id, loaded.History.Current);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"backend\":{\"id\":\"nalgebra_dense_lu_reference\"}}")]
    [InlineData("{\"backend\":null}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"unexpected\"")]
    public void MissingOrSilentlyDifferentProviderIsRejected(string response)
    {
        using var json = JsonDocument.Parse(response);
        Assert.Throws<RcSolverException>(() => NativeRcSolver.CheckBackend(json.RootElement, SolverBackend.Faer));
    }

    [FsCheck.Xunit.Property(MaxTest = 128, Replay = "111414,2026111403")]
    public bool ArbitraryBackendNamesCannotCommitUndefinedProviders(string? name)
    {
        var session = new LessonSession();
        var before = session.Version;
        var result = session.Execute(new SetBridgeDraft(BridgeField.Backend, name!));
        return SolverBackendNames.TryParse(name, out var backend)
            ? result.Code == CommandCode.Ok && session.SelectedBackend == backend
            : result.Code == CommandCode.InvalidInput && session.Version == before && session.SelectedBackend == SolverBackend.Lu;
    }

    [Fact]
    public async Task LockedRcAndGeneralAnalysisUseTheSelectedProviderAndRejectStaleSelection()
    {
        var session = new LessonSession(new(true, false), rcExample: true);
        var old = session.Version;
        Assert.Equal(CommandCode.Ok, session.Execute(new SetSolverBackend(old, SolverBackend.Faer)).Code);
        Assert.Equal(CommandCode.StaleRevision, session.Execute(new SetSolverBackend(old, SolverBackend.Qr)).Code);
        var rc = await session.ExecuteTransientAsync(new(session.Version, "P1"), (input, _) =>
        {
            Assert.Equal(SolverBackend.Faer, input.Backend);
            return Task.FromResult<IReadOnlyList<TransientSample>>([new(0, 0), new(input.StopSeconds, 5)]);
        });
        Assert.Equal(CommandCode.Ok, rc.Code);
        var dc = await session.ExecuteBridgeAsync(new(session.Version, new(BridgeAnalysis.Dc, "P1")), (job, _) =>
        {
            Assert.Equal(SolverBackend.Faer, job.Options.Backend);
            return Task.FromResult<IReadOnlyList<BridgeSample>>([new(0, 5)]);
        });
        Assert.Equal(SolverBackend.Faer, dc.Bridge!.Backend);
    }

    [Fact]
    public void ProviderSelectionSurvivesHistoryAndSavedRcRuns()
    {
        var session = new LessonSession();
        session.Execute(new LoadRc(session.Version));
        session.Execute(new SetSolverBackend(session.Version, SolverBackend.Qr));
        var saved = session.Execute(new SaveDocument()).Document!;
        var restored = new LessonSession();
        Assert.Equal(CommandCode.Ok, restored.Execute(new LoadDocument(restored.Version, saved)).Code);
        Assert.Equal(SolverBackend.Qr, restored.SelectedBackend);
        session.Execute(new SetLayoutLocked(session.Version, true));
        Assert.Equal(CommandCode.Ok, session.Execute(new UndoEdit(session.Version)).Code);
        Assert.Equal(SolverBackend.Lu, session.SelectedBackend);
        Assert.Equal(CommandCode.Ok, session.Execute(new RedoEdit(session.Version)).Code);
        Assert.Equal(SolverBackend.Qr, session.SelectedBackend);
        var before = session.Version;
        Assert.Equal(CommandCode.InvalidInput, session.Execute(new SetSolverBackend(before, (SolverBackend)99)).Code);
        Assert.Equal(before, session.Version);
    }
}
