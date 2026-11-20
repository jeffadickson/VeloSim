using SchematicLab.Core;
using Xunit;

public sealed class RcTests
{
    [Fact]
    public async Task RcCommandResolvesTheWiredProbeAndRefusesInvalidTopology()
    {
        var session = new LessonSession();
        var selected = session.Execute(new LoadRc(session.Version));
        RcInput? input = null;
        Task<IReadOnlyList<TransientSample>> Solve(RcInput request, CancellationToken cancellation)
        {
            input = request;
            return Task.FromResult<IReadOnlyList<TransientSample>>(
                [new(0, 0), new(request.StopSeconds, 4.96)]);
        }
        var run = await session.ExecuteTransientAsync(new RunTransient(selected.Version, "P1"), Solve);
        Assert.Equal(CommandCode.Ok, run.Code);
        Assert.Equal(new RcInput(1000, 1e-6, 5), input);
        Assert.Equal("P1", run.Transient!.ProbeId);
        session.Execute(new DeletePart(session.Version, "C1"));
        input = null;
        var refused = await session.ExecuteTransientAsync(new RunTransient(session.Version, "P1"), Solve);
        Assert.Equal(CommandCode.UnsupportedCircuit, refused.Code);
        Assert.Null(input);
        Assert.Equal(run.Transient, session.Execute(new ReadRun(run.Transient.Id)).Transient);
    }

    [Fact]
    public async Task RunRetainsItsOwnRevisionWhenTheCircuitChangesDuringSolve()
    {
        var session = new LessonSession();
        var original = session.Execute(new LoadRc(session.Version)).Version;
        var completion = new TaskCompletionSource<IReadOnlyList<TransientSample>>();
        RcInput? captured = null;
        var pending = session.ExecuteTransientAsync(new RunTransient(original, "P1"), (input, _) =>
        {
            captured = input;
            return completion.Task;
        });

        var edit = session.Execute(new SetPartValue(original, "C1", 2e-6));
        Assert.Equal(CommandCode.Ok, edit.Code);
        Assert.Equal(1e-6, captured!.Farads);
        completion.SetResult([new(0, 0), new(captured.StopSeconds, 4.96)]);
        var run = (await pending).Transient!;
        Assert.Equal(original, run.Version);
        Assert.NotEqual(session.Version, run.Version);
        Assert.Equal(run, session.Execute(new ReadRun(run.Id)).Transient);
    }
}
