using SchematicLab.Core.RtlWasm;
using Xunit;

namespace SchematicLab.Tests;

public sealed class RtlWasmSessionTests
{
    [Fact]
    public void Rv32iWasmCoreComputesExactDigestAndSupportsForkAndRewind()
    {
        var session = new RtlWasmSession();
        session.Reset(0x13572468u, 25);
        Assert.False(session.Halted);
        Assert.Equal(0u, session.Pc);

        // Step a few cycles, fork an independent timeline, then run to halt
        session.RunCycles(12);
        uint midPc = session.Pc;
        int midCycles = session.Cycles;
        session.ForkWithPerturbedSeed(1u);
        Assert.True(session.HasFork);

        int ran = session.RunToHalt();
        Assert.True(ran > 0);
        Assert.True(session.Halted);
        Assert.Equal(25u, session.A1);
        Assert.Equal(RtlWasmSession.ExpectedDigest(0x13572468u, 25), session.A0);
        Assert.True(session.Waveform.Count > 10);

        // Rewind 10 cycles into the recorded ring buffer and verify live state rolled back
        Assert.True(session.Rewind(10));
        Assert.False(session.Halted);
        Assert.True(session.Cycles < midCycles + ran);
        Assert.True(midPc > 0u);
    }
}
