using System.Diagnostics;
using System.IO.Compression;
using NUnit.Framework;

namespace VeloSim.Tests;

public sealed class SlangFrontendRv32iTests
{
    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "VeloSim.slnx")))
            dir = Path.GetDirectoryName(dir) ?? throw new DirectoryNotFoundException("Could not locate VeloSim.slnx");
        return dir;
    }

    private static string LoadRv32iAstJson()
    {
        string gzPath = Path.Combine(RepoRoot(), "tests", "VeloSim.Tests", "Fixtures", "rv32i_core.slang.ast.json.gz");
        using var fs = File.OpenRead(gzPath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(gz);
        return sr.ReadToEnd();
    }

    private static uint[] BuildRv32iProgram(int iters)
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

        uint hi = (uint)((iters + 0x800) >> 12);
        int lo = iters - (int)(hi << 12);
        return
        [
            I(-16, 0, 0x2, 1, 0x03),          // lw   x1, -16(x0)  (MMIO 0xFFFF_FFF0 host_arg)
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

    private static uint ExpectedRv32iDigest(uint seed, int iters)
    {
        uint fibA = 1, fibB = 1, a0 = seed;
        for (int i = 0; i < iters; ++i)
        {
            uint x6 = fibA + fibB;
            fibA = fibB;
            fibB = x6;
            uint x8 = x6;
            uint x9 = a0 ^ x8;
            uint x12 = a0 >> 3;
            a0 = x9 + x12;
            uint x13 = x8 << 1;
            a0 += x13;
        }
        return a0;
    }

    [Test]
    public async Task SlangLowerCliAndAstLowererProduceValidRv32iIr()
    {
        string astJson = LoadRv32iAstJson();
        string irJson = SlangAstLowerer.LowerAstJson(astJson, "rv32i_core");
        var design = RtlDesign.Parse(irJson);

        Assert.That(design.Clock, Is.EqualTo("clk"));
        Assert.That(design.Memories.Select(m => m.Name), Is.EquivalentTo(new[] { "imem", "dmem", "regs" }));
        Assert.That(design.Signals.Any(s => s.Name == "x10_a0" && s.Direction == "output" && s.Width == 32), Is.True);
        Assert.That(design.Signals.Any(s => s.Name == "halted" && s.Direction == "output" && s.Width == 1), Is.True);

        // Exercise CLI slang-lower --ast-json
        string root = Path.Combine(Path.GetTempPath(), "velosim-slang-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string astPath = Path.Combine(root, "rv32i.ast.json");
            string outPath = Path.Combine(root, "out", "rv32i.rtl.json");
            await File.WriteAllTextAsync(astPath, astJson);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int rc = await ToolCli.RunAsync(["slang-lower", "--ast-json", astPath, "--top", "rv32i_core", "--output", outPath], stdout, stderr);
            Assert.That(rc, Is.Zero, stderr.ToString());
            Assert.That(File.Exists(outPath), Is.True);
            _ = RtlDesign.Parse(await File.ReadAllTextAsync(outPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        // If slang binary is available locally, verify live SystemVerilog -> slang -> IR parity.
        string? localSlang = FindSlangBinary();
        if (localSlang is not null)
        {
            string svPath = Path.Combine(RepoRoot(), "examples", "rv32i_core.sv");
            string liveIr = await SlangAstLowerer.LowerSystemVerilogAsync([svPath], "rv32i_core", localSlang);
            Assert.That(liveIr, Is.EqualTo(irJson));
        }
    }

    [Test]
    public async Task Rv32iCoreExecutesRealMachineCodeIdenticallyAcrossCSharpRustCppAndSimd()
    {
        const int Iters = 250;
        const uint Seed0 = 0x13572468u;
        uint expectedA0 = ExpectedRv32iDigest(Seed0, Iters);
        uint[] prog = BuildRv32iProgram(Iters);
        string progInitCsv = string.Join(", ", prog.Select(w => $"0x{w:x8}u"));
        string progInitRust = string.Join(", ", prog.Select(w => $"0x{w:x8}u32"));

        string irJson = SlangAstLowerer.LowerAstJson(LoadRv32iAstJson(), "rv32i_core");
        string root = Path.Combine(Path.GetTempPath(), "velosim-rv32i-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "rv32i_core.rtl.json");
            await File.WriteAllTextAsync(irPath, irJson);

            // 1. C# 2-state + 8-lane SIMD 2-state
            string csDir = Path.Combine(root, "cs2s");
            string csSimdDir = Path.Combine(root, "cs8l");
            string csHost = Path.Combine(root, "cs_host");
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "rv32i_core", "--output-dir", csDir, "--target", "csharp", "--two-state"], new StringWriter(), err), Is.Zero, err.ToString());
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "rv32i_core_x8", "--output-dir", csSimdDir, "--target", "csharp", "--two-state", "--lanes", "8"], new StringWriter(), err), Is.Zero, err.ToString());

            Directory.CreateDirectory(csHost);
            await File.WriteAllTextAsync(Path.Combine(csHost, "Host.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <LangVersion>15.0</LangVersion>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{Path.Combine(csDir, "Vrv32i_core.csproj")}" />
                    <ProjectReference Include="{Path.Combine(csSimdDir, "Vrv32i_core_x8.csproj")}" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(csHost, "Program.cs"), $$"""
                using System;
                using VeloSim.Generated;

                uint[] prog = [{{progInitCsv}}];

                // Scalar C# 2s
                var dut = new Vrv32i_core();
                dut.clk = 0; dut.rst_n = 0; dut.imem_we = 0; dut.host_arg = 0x{{Seed0:x8}}UL; dut.Eval();
                for (uint i = 0; i < (uint)prog.Length; ++i)
                {
                    dut.imem_we = 1; dut.imem_waddr = i; dut.imem_wdata = prog[i];
                    dut.clk = 1; dut.Eval(); dut.clk = 0; dut.Eval();
                }
                dut.imem_we = 0; dut.rst_n = 1; dut.Eval();
                int cycles = 0;
                while (dut.halted == 0 && cycles < 20000)
                {
                    dut.clk = 1; dut.Eval(); dut.clk = 0; dut.Eval();
                    cycles++;
                }
                if (dut.halted != 1 || dut.x10_a0 != 0x{{expectedA0:x8}}UL || dut.x11_a1 != {{Iters}}UL)
                    throw new Exception($"Scalar mismatch: halted={dut.halted} a0=0x{dut.x10_a0:x8} a1={dut.x11_a1}");

                // 8-lane SIMD C# 2s (8 independent RV32I cores with distinct host_arg seeds)
                var simd = new Vrv32i_core_x8();
                simd.Set_clk_All(0); simd.Set_rst_n_All(0); simd.Set_imem_we_All(0);
                for (int l = 0; l < 8; ++l) simd.Set_host_arg(l, 0x{{Seed0:x8}}UL + (ulong)l);
                simd.Eval();
                for (uint i = 0; i < (uint)prog.Length; ++i)
                {
                    simd.Set_imem_we_All(1); simd.Set_imem_waddr_All(i); simd.Set_imem_wdata_All(prog[i]);
                    simd.Set_clk_All(1); simd.Eval(); simd.Set_clk_All(0); simd.Eval();
                }
                simd.Set_imem_we_All(0); simd.Set_rst_n_All(1); simd.Eval();
                for (int c = 0; c < cycles; ++c)
                {
                    simd.Set_clk_All(1); simd.Eval(); simd.Set_clk_All(0); simd.Eval();
                }
                if (simd.Get_x10_a0(0) != 0x{{expectedA0:x8}}UL || simd.DivergedMask_x10_a0() != 0xFEu)
                    throw new Exception($"SIMD mismatch: lane0=0x{simd.Get_x10_a0(0):x8} mask=0x{simd.DivergedMask_x10_a0():x2}");
                Console.WriteLine($"CS_OK cycles={cycles}");
                """);
            var csRun = await RunCommandAsync("dotnet", ["run", "--disable-build-servers", "-c", "Release"], csHost);
            Assert.That(csRun.ExitCode, Is.Zero, csRun.Output);
            Assert.That(csRun.Output, Does.Contain("CS_OK"));

            // 2. Rust 2-state (when cargo is available)
            if (await ToolExistsAsync("cargo"))
            {
                string rsDir = Path.Combine(root, "rs2s");
                string rsHost = Path.Combine(root, "rs_host");
                Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "rv32i_core", "--output-dir", rsDir, "--target", "rust", "--two-state"], new StringWriter(), err), Is.Zero, err.ToString());
                Directory.CreateDirectory(Path.Combine(rsHost, "src"));
                await File.WriteAllTextAsync(Path.Combine(rsHost, "Cargo.toml"), $$"""
                    [package]
                    name = "rv32i_rs_host"
                    version = "0.1.0"
                    edition = "2021"

                    [dependencies]
                    velosim_rv32i_core = { path = "{{rsDir.Replace("\\", "/")}}" }
                    """);
                await File.WriteAllTextAsync(Path.Combine(rsHost, "src", "main.rs"), $$"""
                    #![forbid(unsafe_code)]
                    use velosim_rv32i_core::Vrv32i_core;

                    fn main() {
                        let prog: [u32; {{prog.Length}}] = [{{progInitRust}}];
                        let mut dut = Vrv32i_core::new();
                        dut.set_clk(0); dut.set_rst_n(0); dut.set_imem_we(0); dut.set_host_arg(0x{{Seed0:x8}}); dut.eval();
                        for (i, &w) in prog.iter().enumerate() {
                            dut.set_imem_we(1); dut.set_imem_waddr(i as u64); dut.set_imem_wdata(w as u64);
                            dut.set_clk(1); dut.eval(); dut.set_clk(0); dut.eval();
                        }
                        dut.set_imem_we(0); dut.set_rst_n(1); dut.eval();
                        let mut cycles = 0u32;
                        while dut.halted() == 0 && cycles < 20000 {
                            dut.set_clk(1); dut.eval(); dut.set_clk(0); dut.eval();
                            cycles += 1;
                        }
                        assert_eq!(dut.halted(), 1);
                        assert_eq!(dut.x10_a0(), 0x{{expectedA0:x8}});
                        assert_eq!(dut.x11_a1(), {{Iters}});
                        println!("RS_OK cycles={}", cycles);
                    }
                    """);
                var rsRun = await RunCommandAsync("cargo", ["run", "--release"], rsHost);
                Assert.That(rsRun.ExitCode, Is.Zero, rsRun.Output);
                Assert.That(rsRun.Output, Does.Contain("RS_OK"));
            }

            // 3. C++20 2-state (when g++ or clang++ is available)
            string? cxx = await ToolExistsAsync("g++") ? "g++" : (await ToolExistsAsync("clang++") ? "clang++" : null);
            if (cxx is not null)
            {
                string cppDir = Path.Combine(root, "cpp2s");
                Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "rv32i_core", "--output-dir", cppDir, "--target", "cpp", "--two-state"], new StringWriter(), err), Is.Zero, err.ToString());
                string hostCpp = Path.Combine(root, "host.cpp");
                string exe = Path.Combine(root, "host_cpp");
                await File.WriteAllTextAsync(hostCpp, $$$"""
                    #include "Vrv32i_core.h"
                    #include <cstdint>
                    #include <iostream>
                    int main() {
                        const uint32_t prog[] = { {{{progInitCsv}}} };
                        Vrv32i_core dut;
                        dut.clk = 0; dut.rst_n = 0; dut.imem_we = 0; dut.host_arg = 0x{{{Seed0:x8}}}ULL; dut.eval();
                        for (size_t i = 0; i < sizeof(prog)/sizeof(prog[0]); ++i) {
                            dut.imem_we = 1; dut.imem_waddr = i; dut.imem_wdata = prog[i];
                            dut.clk = 1; dut.eval(); dut.clk = 0; dut.eval();
                        }
                        dut.imem_we = 0; dut.rst_n = 1; dut.eval();
                        int cycles = 0;
                        while (static_cast<uint64_t>(dut.halted) == 0 && cycles < 20000) {
                            dut.clk = 1; dut.eval(); dut.clk = 0; dut.eval();
                            ++cycles;
                        }
                        if (static_cast<uint64_t>(dut.halted) != 1 || static_cast<uint64_t>(dut.x10_a0) != 0x{{{expectedA0:x8}}}ULL || static_cast<uint64_t>(dut.x11_a1) != {{{Iters}}}ULL) return 1;
                        std::cout << "CPP_OK cycles=" << cycles << "\n";
                        return 0;
                    }
                    """);
                var comp = await RunCommandAsync(cxx, ["-std=c++20", "-O2", "-I" + cppDir, hostCpp, Path.Combine(cppDir, "vpi.cpp"), "-ldl", "-o", exe], root);
                Assert.That(comp.ExitCode, Is.Zero, comp.Output);
                var cppRun = await RunCommandAsync(exe, [], root);
                Assert.That(cppRun.ExitCode, Is.Zero, cppRun.Output);
                Assert.That(cppRun.Output, Does.Contain("CPP_OK"));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Test]
    public void SlangAstLowererCoversOperatorsCasesTypesConstantsAndErrors()
    {
        // Synthetic slang AST JSON covering: packed/signed/2-state types, non-zero lower bound memory,
        // always_comb, always @(*) ImplicitEvent, always_ff with EventList (posedge+negedge),
        // case / casez / casex + multi-match + default, conditional without else, empty/block/list,
        // all unary ops, all binary ops, ternary, concat, replication, bitselect, simple/indexed range selects,
        // system calls ($signed/$unsigned/$countones/$onehot/$onehot0/$isunknown/$clog2),
        // conversions (const + signedness cast), and all constant radices ('0/'1/'x/'z/'?, 'b/'o/'d/'h/'sd, negative decimal).
        const string richAst = """
            {
              "design": {
                "members": [
                  {
                    "kind": "Instance",
                    "name": "rich_top",
                    "body": {
                      "name": "rich_top",
                      "members": [
                        {"kind": "Port", "name": "clk", "direction": "In"},
                        {"kind": "Port", "name": "rst_n", "direction": "In"},
                        {"kind": "Port", "name": "a", "direction": "In"},
                        {"kind": "Port", "name": "b", "direction": "In"},
                        {"kind": "Port", "name": "sel", "direction": "In"},
                        {"kind": "Port", "name": "w64", "direction": "In"},
                        {"kind": "Port", "name": "y", "direction": "Out"},
                        {"kind": "Port", "name": "q", "direction": "Out"},
                        {"kind": "Variable", "name": "clk", "type": "logic"},
                        {"kind": "Variable", "name": "rst_n", "type": "bit"},
                        {"kind": "Variable", "name": "a", "type": "byte"},
                        {"kind": "Variable", "name": "b", "type": "shortint"},
                        {"kind": "Variable", "name": "sel", "type": "int"},
                        {"kind": "Variable", "name": "w64", "type": "longint"},
                        {"kind": "Variable", "name": "s8", "type": "logic signed[7:0]"},
                        {"kind": "Net", "name": "u8", "type": "reg unsigned[7:0]"},
                        {"kind": "Variable", "name": "y", "type": "integer"},
                        {"kind": "Variable", "name": "q", "type": "logic[7:0]"},
                        {"kind": "Variable", "name": "mem", "type": "logic signed[7:0]$[4:11]"},
                        {
                          "kind": "ContinuousAssign",
                          "assignment": {
                            "left": {"kind": "NamedValue", "symbol": "1 u8", "type": "logic[7:0]"},
                            "right": {
                              "kind": "BinaryOp", "op": "Add", "type": "logic[7:0]",
                              "left": {
                                "kind": "UnaryOp", "op": "Plus", "type": "logic[7:0]",
                                "operand": {"kind": "NamedValue", "symbol": "2 a", "type": "byte"}
                              },
                              "right": {"kind": "IntegerLiteral", "type": "logic[7:0]", "constant": "8'h0f"}
                            }
                          }
                        },
                        {
                          "kind": "ProceduralBlock",
                          "procedureKind": "AlwaysComb",
                          "body": {
                            "kind": "Block",
                            "body": {
                              "kind": "List",
                              "list": [
                                {"kind": "Empty"},
                                {
                                  "kind": "ExpressionStatement",
                                  "expr": {
                                    "kind": "Assignment", "isNonBlocking": false,
                                    "left": {"kind": "NamedValue", "symbol": "3 y", "type": "integer"},
                                    "right": {
                                      "kind": "ConditionalOp", "type": "integer",
                                      "conditions": [{"expr": {"kind": "Call", "subroutine": "$onehot0", "type": "bit", "arguments": [{"kind": "NamedValue", "symbol": "1 u8", "type": "logic[7:0]"}]}}],
                                      "left": {"kind": "Conversion", "type": "integer", "constant": "-5", "operand": {"kind": "IntegerLiteral", "type": "int", "value": "5"}},
                                      "right": {"kind": "Call", "subroutine": "$clog2", "type": "int", "arguments": [{"kind": "Call", "subroutine": "$countones", "type": "int", "arguments": [{"kind": "NamedValue", "symbol": "2 a", "type": "byte"}]}]}
                                    }
                                  }
                                }
                              ]
                            }
                          }
                        },
                        {
                          "kind": "ProceduralBlock",
                          "procedureKind": "Always",
                          "body": {
                            "kind": "Timed",
                            "timing": {"kind": "ImplicitEvent"},
                            "stmt": {
                              "kind": "ExpressionStatement",
                              "expr": {
                                "kind": "Assignment", "isNonBlocking": false,
                                "left": {"kind": "NamedValue", "symbol": "4 s8", "type": "logic signed[7:0]"},
                                "right": {
                                  "kind": "Conversion", "type": "logic signed[7:0]",
                                  "operand": {"kind": "NamedValue", "symbol": "1 u8", "type": "logic[7:0]"}
                                }
                              }
                            }
                          }
                        },
                        {
                          "kind": "ProceduralBlock",
                          "procedureKind": "AlwaysFF",
                          "body": {
                            "kind": "Timed",
                            "timing": {
                              "kind": "EventList",
                              "events": [
                                {"kind": "SignalEvent", "edge": "PosEdge", "expr": {"kind": "NamedValue", "symbol": "0 clk", "type": "logic"}},
                                {"kind": "SignalEvent", "edge": "NegEdge", "expr": {"kind": "NamedValue", "symbol": "0 rst_n", "type": "bit"}}
                              ]
                            },
                            "stmt": {
                              "kind": "Block",
                              "body": {
                                "kind": "List",
                                "list": [
                                  {"kind": "Empty"},
                                  {
                                    "kind": "Conditional",
                                    "conditions": [{"expr": {"kind": "UnaryOp", "op": "LogicalNot", "type": "bit", "operand": {"kind": "NamedValue", "symbol": "0 rst_n", "type": "bit"}}}],
                                    "ifTrue": {
                                      "kind": "ExpressionStatement",
                                      "expr": {
                                        "kind": "Assignment", "isNonBlocking": true,
                                        "left": {"kind": "NamedValue", "symbol": "5 q", "type": "logic[7:0]"},
                                        "right": {"kind": "UnbasedUnsizedIntegerLiteral", "type": "logic[7:0]", "value": "'0"}
                                      }
                                    }
                                  },
                                  {
                                    "kind": "Case",
                                    "condition": "WildcardZ",
                                    "expr": {"kind": "RangeSelect", "selectionKind": "IndexedUp", "type": "logic[1:0]", "value": {"kind": "NamedValue", "symbol": "1 u8", "type": "logic[7:0]"}, "left": {"kind": "IntegerLiteral", "type": "int", "constant": "32'd0"}, "right": {"kind": "IntegerLiteral", "type": "int", "constant": "2"}},
                                    "items": [
                                      {
                                        "expressions": [
                                          {"kind": "IntegerLiteral", "type": "logic[1:0]", "constant": "2'b0?"},
                                          {"kind": "IntegerLiteral", "type": "logic[1:0]", "constant": "2'o1"}
                                        ],
                                        "stmt": {
                                          "kind": "ExpressionStatement",
                                          "expr": {
                                            "kind": "Assignment", "isNonBlocking": true,
                                            "left": {"kind": "ElementSelect", "type": "logic signed[7:0]", "value": {"kind": "NamedValue", "symbol": "6 mem", "type": "logic signed[7:0]$[4:11]"}, "selector": {"kind": "IntegerLiteral", "type": "int", "constant": "32'sd4"}},
                                            "right": {"kind": "Replication", "type": "logic[7:0]", "count": {"kind": "IntegerLiteral", "type": "int", "constant": "2"}, "concat": {"kind": "RangeSelect", "selectionKind": "IndexedDown", "type": "logic[3:0]", "value": {"kind": "NamedValue", "symbol": "1 u8", "type": "logic[7:0]"}, "left": {"kind": "IntegerLiteral", "type": "int", "constant": "7"}, "right": {"kind": "IntegerLiteral", "type": "int", "constant": "4"}}}
                                          }
                                        }
                                      }
                                    ],
                                    "defaultCase": {
                                      "kind": "Case",
                                      "condition": "WildcardXOrZ",
                                      "expr": {"kind": "ElementSelect", "type": "logic", "value": {"kind": "NamedValue", "symbol": "1 u8", "type": "logic[7:0]"}, "selector": {"kind": "IntegerLiteral", "type": "int", "constant": "0"}},
                                      "items": [
                                        {
                                          "expressions": [{"kind": "IntegerLiteral", "type": "logic", "constant": "1'bx"}],
                                          "stmt": {
                                            "kind": "ExpressionStatement",
                                            "expr": {
                                              "kind": "Assignment", "isNonBlocking": true,
                                              "left": {"kind": "NamedValue", "symbol": "5 q", "type": "logic[7:0]"},
                                              "right": {
                                                "kind": "Concatenation", "type": "logic[7:0]",
                                                "operands": [
                                                  {"kind": "UnaryOp", "op": "BitwiseAnd", "type": "logic", "operand": {"kind": "NamedValue", "symbol": "1 u8", "type": "logic[7:0]"}},
                                                  {"kind": "UnaryOp", "op": "BitwiseNand", "type": "logic", "operand": {"kind": "NamedValue", "symbol": "1 u8", "type": "logic[7:0]"}},
                                                  {"kind": "UnaryOp", "op": "BitwiseOr", "type": "logic", "operand": {"kind": "NamedValue", "symbol": "1 u8", "type": "logic[7:0]"}},
                                                  {"kind": "UnaryOp", "op": "BitwiseNor", "type": "logic", "operand": {"kind": "NamedValue", "symbol": "1 u8", "type": "logic[7:0]"}},
                                                  {"kind": "UnaryOp", "op": "BitwiseXor", "type": "logic", "operand": {"kind": "NamedValue", "symbol": "1 u8", "type": "logic[7:0]"}},
                                                  {"kind": "UnaryOp", "op": "BitwiseXnor", "type": "logic", "operand": {"kind": "NamedValue", "symbol": "1 u8", "type": "logic[7:0]"}},
                                                  {"kind": "Call", "subroutine": "$onehot", "type": "bit", "arguments": [{"kind": "NamedValue", "symbol": "1 u8", "type": "logic[7:0]"}]},
                                                  {"kind": "Call", "subroutine": "$isunknown", "type": "bit", "arguments": [{"kind": "NamedValue", "symbol": "1 u8", "type": "logic[7:0]"}]}
                                                ]
                                              }
                                            }
                                          }
                                        }
                                      ]
                                    }
                                  }
                                ]
                              }
                            }
                          }
                        }
                      ]
                    }
                  }
                ]
              }
            }
            """;

        string irJson = SlangAstLowerer.LowerAstJson(richAst, "rich_top");
        var design = RtlDesign.Parse(irJson);
        Assert.That(design.Memories.Single().LowerBound, Is.EqualTo(4));
        Assert.That(design.Memories.Single().Depth, Is.EqualTo(8));
        Assert.That(design.Memories.Single().Signed, Is.True);
        Assert.That(design.Signals.First(s => s.Name == "w64").Width, Is.EqualTo(64));
        Assert.That(design.Signals.First(s => s.Name == "b").Width, Is.EqualTo(16));

        // Cover error paths fail-closed
        Assert.Throws<ArgumentException>(() => SlangAstLowerer.LowerAstJson("{}"));
        Assert.Throws<ArgumentException>(() => SlangAstLowerer.LowerAstJson("""{"design":{"members":[]}}""", "missing"));
        Assert.ThrowsAsync<ArgumentException>(async () => await SlangAstLowerer.LowerSystemVerilogAsync([], "top"));
        Assert.ThrowsAsync<ArgumentException>(async () => await SlangAstLowerer.LowerSystemVerilogAsync(["a.sv"], ""));
        Assert.Throws<NotSupportedException>(() => SlangAstLowerer.LowerAstJson("""
            {"design":{"members":[{"kind":"Instance","name":"t","body":{"name":"t","members":[{"kind":"Port","name":"p","direction":"InOut"}]}}]}}
            """, "t"));
    }

    private static string? FindSlangBinary()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "slang", "slang");
        if (File.Exists(local)) return local;
        string? env = Environment.GetEnvironmentVariable("VELOSIM_SLANG");
        return !string.IsNullOrWhiteSpace(env) && File.Exists(env) ? env : null;
    }

    private static async Task<(int ExitCode, string Output)> RunCommandAsync(string file, string[] args, string cwd)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start " + file);
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, (await stdout) + "\n" + (await stderr));
    }

    private static async Task<bool> ToolExistsAsync(string tool)
    {
        try { return (await RunCommandAsync(tool, ["--version"], Environment.CurrentDirectory)).ExitCode == 0; }
        catch { return false; }
    }
}
