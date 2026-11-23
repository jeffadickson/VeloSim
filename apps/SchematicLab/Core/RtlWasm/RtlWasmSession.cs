using VeloSim.Generated;

namespace SchematicLab.Core.RtlWasm;

/// <summary>One recorded digital waveform sample captured in-browser from the VeloSim C# RTL model.</summary>
public readonly record struct RtlWaveformPoint(
    int Cycle,
    uint Pc,
    uint Inst,
    uint A0,
    uint A1,
    bool Halted);

/// <summary>
/// Client-side WebAssembly RTL simulation session backed by VeloSim's zero-unsafe C# 15
/// compiled RV32I RISC-V core (<see cref="Vrv32i_core"/>). Runs 100% in-browser with zero server round-trips.
/// </summary>
public sealed class RtlWasmSession
{
    private const int MaxWaveformPoints = 64;
    private const int RingCapacity = 128;

    private readonly List<RtlWaveformPoint> _wave = new(MaxWaveformPoints);
    private Vrv32i_core _dut = new();
    private TimeTravelRecorder<Vrv32i_core> _recorder = new(RingCapacity);
    private Vrv32i_core? _forked;

    public uint Seed { get; private set; } = 0x13572468u;
    public int Iterations { get; private set; } = 10;
    public int Cycles { get; private set; }
    public uint Pc => (uint)_dut.pc;
    public uint Inst => (uint)_dut.inst;
    public uint A0 => (uint)_dut.x10_a0;
    public uint A1 => (uint)_dut.x11_a1;
    public uint CycleCount => (uint)_dut.cycle_count;
    public bool Halted => _dut.halted != 0UL;
    public int RecordedDepth => _recorder.Count;
    public bool HasFork => _forked is not null;
    public uint ForkedA0 => _forked is null ? 0u : (uint)_forked.x10_a0;
    public uint ForkedPc => _forked is null ? 0u : (uint)_forked.pc;
    public IReadOnlyList<RtlWaveformPoint> Waveform => _wave;

    public RtlWasmSession()
    {
        Reset(Seed, Iterations);
    }

    public static uint[] BuildRv32iProgram(int iters)
    {
        static uint R(uint f7, uint rs2, uint rs1, uint f3, uint rd, uint op = 0x33) =>
            ((f7 & 0x7Fu) << 25) | ((rs2 & 0x1Fu) << 20) | ((rs1 & 0x1Fu) << 15) | ((f3 & 7u) << 12) | ((rd & 0x1Fu) << 7) | (op & 0x7Fu);
        static uint I(int imm, uint rs1, uint f3, uint rd, uint op = 0x13) =>
            (((uint)imm & 0xFFFu) << 20) | ((rs1 & 0x1Fu) << 15) | ((f3 & 7u) << 12) | ((rd & 0x1Fu) << 7) | (op & 0x7Fu);
        static uint S(int imm, uint rs2, uint rs1, uint f3, uint op = 0x23) =>
            ((((uint)imm >> 5) & 0x7Fu) << 25) | ((rs2 & 0x1Fu) << 20) | ((rs1 & 0x1Fu) << 15) | ((f3 & 7u) << 12) | (((uint)imm & 0x1Fu) << 7) | (op & 0x7Fu);
        static uint B(int imm, uint rs2, uint rs1, uint f3, uint op = 0x63) =>
            ((((uint)imm >> 12) & 1u) << 31) | ((((uint)imm >> 5) & 0x3Fu) << 25) | ((rs2 & 0x1Fu) << 20) | ((rs1 & 0x1Fu) << 15) | ((f3 & 7u) << 12) | ((((uint)imm >> 1) & 0xFu) << 8) | ((((uint)imm >> 11) & 1u) << 7) | (op & 0x7Fu);
        static uint U(uint imm20, uint rd, uint op = 0x37) =>
            ((imm20 & 0xFFFFFu) << 12) | ((rd & 0x1Fu) << 7) | (op & 0x7Fu);

        int clamped = Math.Clamp(iters, 1, 2000);
        uint hi = (uint)((clamped + 0x800) >> 12);
        int lo = clamped - (int)(hi << 12);
        return
        [
            I(-16, 0, 0x2, 1, 0x03),          // lw   x1, -16(x0)  (MMIO host_arg)
            U(hi, 2, 0x37),                    // lui  x2, hi
            I(lo, 2, 0x0, 2, 0x13),            // addi x2, x2, lo
            I(0, 0, 0x0, 3, 0x13),             // addi x3, x0, 0
            I(1, 0, 0x0, 4, 0x13),             // addi x4, x0, 1
            I(1, 0, 0x0, 5, 0x13),             // addi x5, x0, 1
            I(0, 1, 0x0, 10, 0x13),            // addi x10, x1, 0
            R(0x00, 5, 4, 0x0, 6),             // add  x6, x4, x5
            I(0, 5, 0x0, 4, 0x13),             // addi x4, x5, 0
            I(0, 6, 0x0, 5, 0x13),             // addi x5, x6, 0
            I(15, 3, 0x7, 7, 0x13),            // andi x7, x3, 15
            I(2, 7, 0x1, 7, 0x13),             // slli x7, x7, 2
            S(0, 6, 7, 0x2),                   // sw   x6, 0(x7)
            I(0, 7, 0x2, 8, 0x03),             // lw   x8, 0(x7)
            R(0x00, 8, 10, 0x4, 9),            // xor  x9, x10, x8
            I(3, 10, 0x5, 12, 0x13),           // srli x12, x10, 3
            R(0x00, 12, 9, 0x0, 10),           // add  x10, x9, x12
            I(1, 8, 0x1, 13, 0x13),            // slli x13, x8, 1
            R(0x00, 13, 10, 0x0, 10),          // add  x10, x10, x13
            I(1, 3, 0x0, 3, 0x13),             // addi x3, x3, 1
            B(-13 * 4, 2, 3, 0x4),             // blt  x3, x2, -52
            I(0, 3, 0x0, 11, 0x13),            // addi x11, x3, 0
            0x00100073u                        // ebreak
        ];
    }

    public static uint ExpectedDigest(uint seed, int iters)
    {
        int clamped = Math.Clamp(iters, 1, 2000);
        uint fibA = 1, fibB = 1, a0 = seed;
        for (int i = 0; i < clamped; ++i)
        {
            uint x6 = fibA + fibB;
            fibA = fibB;
            fibB = x6;
            uint x9 = a0 ^ x6;
            uint x12 = a0 >> 3;
            a0 = x9 + x12 + (x6 << 1);
        }
        return a0;
    }

    public void Reset(uint seed, int iterations)
    {
        Seed = seed;
        Iterations = Math.Clamp(iterations, 1, 2000);
        Cycles = 0;
        _forked = null;
        _wave.Clear();
        _dut = new Vrv32i_core();
        _recorder = new TimeTravelRecorder<Vrv32i_core>(RingCapacity);

        uint[] prog = BuildRv32iProgram(Iterations);
        _dut.clk = 0;
        _dut.rst_n = 0;
        _dut.imem_we = 0;
        _dut.host_arg = Seed;
        _dut.Eval();
        for (uint i = 0; i < (uint)prog.Length; ++i)
        {
            _dut.imem_we = 1;
            _dut.imem_waddr = i;
            _dut.imem_wdata = prog[i];
            _dut.clk = 1; _dut.Eval();
            _dut.clk = 0; _dut.Eval();
        }
        _dut.imem_we = 0;
        _dut.rst_n = 1;
        _dut.Eval();
        RecordPoint();
    }

    public void Step()
    {
        if (Halted) return;
        _dut.TimeInc(10);
        _dut.clk = 1; _dut.Eval();
        _dut.clk = 0; _dut.Eval();
        Cycles++;
        RecordPoint();
        if (_forked is not null && _forked.halted == 0UL)
        {
            _forked.TimeInc(10);
            _forked.clk = 1; _forked.Eval();
            _forked.clk = 0; _forked.Eval();
        }
    }

    public int RunCycles(int count)
    {
        int ran = 0;
        for (int i = 0; i < count && !Halted; ++i)
        {
            Step();
            ran++;
        }
        return ran;
    }

    public int RunToHalt(int maxCycles = 50000) => RunCycles(maxCycles);

    /// <summary>Creates an independent sub-microsecond in-RAM fork of the live RV32I core.</summary>
    public void ForkWithPerturbedSeed(uint delta = 1u)
    {
        _forked = _dut.Fork();
        _forked.host_arg = Seed + delta;
        _forked.Eval();
    }

    /// <summary>Rewinds the live RV32I core back into the recorded time-travel ring buffer.</summary>
    public bool Rewind(int stepsBack)
    {
        if (!_recorder.Rewind(_dut, stepsBack)) return false;
        Cycles = (int)_dut.cycle_count;
        RecordPoint();
        return true;
    }

    private void RecordPoint()
    {
        _recorder.Record(_dut);
        if (_wave.Count >= MaxWaveformPoints) _wave.RemoveAt(0);
        _wave.Add(new RtlWaveformPoint(Cycles, Pc, Inst, A0, A1, Halted));
    }
}
