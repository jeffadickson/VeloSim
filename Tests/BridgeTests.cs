using SchematicLab.Core;
using Xunit;

public sealed class BridgeTests
{
    [Fact]
    public async Task GeneralCommandCapturesWiredCircuitAndRejectsInvalidOptionsBeforeSolving()
    {
        var session = new LessonSession();
        var original = session.Version;
        Assert.Equal(CommandCode.Ok, session.Execute(new LoadBridgeDemo(original)).Code);
        var options = BridgeDemo.Options(BridgeAnalysis.Ac);
        var stale = await session.ExecuteBridgeAsync(new RunBridge(original, options), Never);
        Assert.Equal(CommandCode.StaleRevision, stale.Code);
        var bad = await session.ExecuteBridgeAsync(new RunBridge(session.Version, options with { FrequenciesHz = [-1] }), Never);
        Assert.Equal(CommandCode.InvalidInput, bad.Code);
        var run = await session.ExecuteBridgeAsync(new RunBridge(session.Version, options), (job, _) =>
        {
            Assert.Equal("0", job.Nodes[0]);
            Assert.Contains(job.Elements, e => e.Kind == Kind.Inductor);
            Assert.Contains(job.Elements, e => e.Kind == Kind.Ccvs && e.ControlId == "V1");
            Assert.Contains(job.Elements, e => e.Kind == Kind.Cccs && e.ControlId == "V1");
            Assert.NotEqual("0", job.ProbeNode);
            return Task.FromResult<IReadOnlyList<BridgeSample>>([new(100, 2, -1), new(1000, 0.5, -0.25)]);
        });
        Assert.Equal(CommandCode.Ok, run.Code);
        Assert.Equal(-1, run.Bridge!.Samples[0].Imaginary);
        Assert.Equal(session.Version, run.Bridge.Version);
    }

    [Fact]
    public async Task DraftAndSourceBindingsRejectMalformedInputsWithoutAdvancingRevision()
    {
        var session = new LessonSession();
        session.Execute(new LoadBridgeDemo(session.Version));
        var version = session.Version;
        var random = new Random(110825);
        for (var i = 0; i < 64; i++)
        {
            session.Execute(new SetBridgeDraft(BridgeField.Phasors, $"V1 invalid{random.Next()} 0"));
            Assert.Null(session.BridgeDraft.Parse(BridgeAnalysis.Ac));
        }
        var options = BridgeDemo.Options(BridgeAnalysis.Ac);
        var result = await session.ExecuteBridgeAsync(new RunBridge(version,
            options with { Controls = [new("F1", "I1")] }), Never);
        Assert.Equal(CommandCode.InvalidInput, result.Code);
        result = await session.ExecuteBridgeAsync(new RunBridge(version,
            options with { Controls = [] }), Never);
        Assert.Equal(CommandCode.UnsupportedCircuit, result.Code);
        result = await session.ExecuteBridgeAsync(new RunBridge(version, options), (_, _) =>
            Task.FromResult<IReadOnlyList<BridgeSample>>([new(100, 2)]));
        Assert.Equal(CommandCode.SolverFailed, result.Code);
        Assert.Equal(version, session.Version);
    }

    [Fact]
    public async Task Mos1RequiresAnExplicitBindingAndKeepsDrainGateSourceBulkOrder()
    {
        var session = new LessonSession();
        session.Execute(new LoadModelDemo(session.Version));
        var options = BridgeDemo.ModelOptions(BridgeAnalysis.Dc, "P2");
        var unbound = await session.ExecuteBridgeAsync(new RunBridge(session.Version, options with { Mos1 = [] }), Never);
        Assert.Equal(CommandCode.UnsupportedCircuit, unbound.Code);
        var wrong = await session.ExecuteBridgeAsync(new RunBridge(session.Version,
            options with { Mos1 = [options.Mos1![0] with { Id = "D1" }] }), Never);
        Assert.Equal(CommandCode.InvalidInput, wrong.Code);
        var result = await session.ExecuteBridgeAsync(new RunBridge(session.Version, options), (job, _) =>
        {
            var mos = Assert.Single(job.Elements, e => e.Id == "M1");
            Assert.Equal(job.ProbeNode, job.Nodes[mos.P]);
            Assert.Equal(0, mos.N);
            Assert.Equal(0, mos.Bulk);
            Assert.NotEqual(mos.Gate, mos.P);
            Assert.NotEqual(0, mos.Gate);
            return Task.FromResult<IReadOnlyList<BridgeSample>>([new(0, 2)]);
        });
        Assert.Equal(CommandCode.Ok, result.Code);
    }

    [Fact]
    public async Task ChargedDiodeBindingRejectsInvalidDepletionAndRetainsTransportCharge()
    {
        var session = new LessonSession();
        session.Execute(new LoadModelDemo(session.Version));
        var options = BridgeDemo.ModelOptions(BridgeAnalysis.Dc, "P1");
        var diode = options.Diodes![0] with { JunctionFarads = 2e-9, TransitSeconds = 1e-8 };
        var invalid = await session.ExecuteBridgeAsync(new RunBridge(session.Version,
            options with { Diodes = [diode with { Grading = 1 }] }), Never);
        Assert.Equal(CommandCode.InvalidInput, invalid.Code);
        var result = await session.ExecuteBridgeAsync(new RunBridge(session.Version,
            options with { Diodes = [diode] }), (job, _) =>
        {
            Assert.Equal(diode, Assert.Single(job.Options.Diodes!));
            return Task.FromResult<IReadOnlyList<BridgeSample>>([new(0, .6)]);
        });
        Assert.Equal(CommandCode.Ok, result.Code);
    }

    [Fact]
    public async Task Mos1OverlapAndBodyChargeValidateBeforeSolving()
    {
        var session = new LessonSession();
        session.Execute(new LoadModelDemo(session.Version));
        var options = BridgeDemo.ModelOptions(BridgeAnalysis.Dc, "P2");
        var model = options.Mos1![0] with { GateDrainFarads = 2e-9, DrainBodyFarads = 1e-9 };
        var invalid = await session.ExecuteBridgeAsync(new RunBridge(session.Version,
            options with { Mos1 = [model with { DrainBodyFarads = -1 }] }), Never);
        Assert.Equal(CommandCode.InvalidInput, invalid.Code);
        var result = await session.ExecuteBridgeAsync(new RunBridge(session.Version,
            options with { Mos1 = [model] }), (job, _) =>
        {
            Assert.Equal(model, Assert.Single(job.Options.Mos1!));
            return Task.FromResult<IReadOnlyList<BridgeSample>>([new(0, 2)]);
        });
        Assert.Equal(CommandCode.Ok, result.Code);
    }

    [Fact]
    public async Task BjtRequiresExplicitModelAndPreservesCollectorBaseEmitterOrder()
    {
        var session = new LessonSession();
        session.Execute(new LoadBjtDemo(session.Version));
        var options = BridgeDemo.BjtOptions(BridgeAnalysis.Dc);
        Assert.Equal(CommandCode.UnsupportedCircuit, (await session.ExecuteBridgeAsync(
            new RunBridge(session.Version, options with { Bjts = [] }), Never)).Code);
        var result = await session.ExecuteBridgeAsync(new RunBridge(session.Version, options), (job, _) =>
        {
            var bjt = Assert.Single(job.Elements, element => element.Kind == Kind.Npn);
            Assert.Equal(job.ProbeNode, job.Nodes[bjt.P]);
            Assert.Equal(0, bjt.N);
            Assert.NotEqual(0, bjt.Base);
            return Task.FromResult<IReadOnlyList<BridgeSample>>([new(0, 2)]);
        });
        Assert.Equal(CommandCode.Ok, result.Code);
    }

    private static Task<IReadOnlyList<BridgeSample>> Never(CircuitJob job, CancellationToken token) =>
        throw new InvalidOperationException("Invalid input reached the solver.");
}
