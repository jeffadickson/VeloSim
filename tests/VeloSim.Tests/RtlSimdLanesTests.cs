using System.Diagnostics;
using System.Text.Json;
using NUnit.Framework;

namespace VeloSim.Tests;

public sealed class RtlSimdLanesTests
{
    private const string SimdFifoAluIr = """
        {
          "format": "velosim.rtl.v1",
          "signals": [
            {"name": "clk", "width": 1, "direction": "input"},
            {"name": "rst", "width": 1, "direction": "input"},
            {"name": "we", "width": 1, "direction": "input"},
            {"name": "op", "width": 2, "direction": "input"},
            {"name": "addr", "width": 3, "direction": "input"},
            {"name": "din", "width": 16, "direction": "input"},
            {"name": "acc", "width": 16, "direction": "output"},
            {"name": "rdata", "width": 16, "direction": "output"},
            {"name": "mix", "width": 16, "direction": "output"}
          ],
          "memories": [
            {"name": "ram", "width": 16, "depth": 8}
          ],
          "continuous": [
            {
              "target": {"kind": "ref", "name": "mix"},
              "value": {
                "kind": "bitxor",
                "args": [
                  {"kind": "ref", "name": "acc"},
                  {"kind": "add", "args": [{"kind": "ref", "name": "rdata"}, {"kind": "const", "bits": "0000000000000101"}]}
                ]
              }
            }
          ],
          "clock": "clk",
          "body": {
            "kind": "process",
            "events": [{"signal": "clk", "edge": "posedge"}],
            "body": {
              "kind": "if",
              "condition": {"kind": "ref", "name": "rst"},
              "then": {
                "kind": "block",
                "items": [
                  {"kind": "nba", "target": {"kind": "ref", "name": "acc"}, "value": {"kind": "const", "bits": "0000000000000000"}},
                  {"kind": "nba", "target": {"kind": "ref", "name": "rdata"}, "value": {"kind": "const", "bits": "0000000000000000"}}
                ]
              },
              "else": {
                "kind": "block",
                "items": [
                  {
                    "kind": "case",
                    "case_kind": "exact",
                    "value": {"kind": "ref", "name": "op"},
                    "items": [
                      {"match": {"kind": "const", "bits": "00"}, "body": {"kind": "nba", "target": {"kind": "ref", "name": "acc"}, "value": {"kind": "add", "args": [{"kind": "ref", "name": "acc"}, {"kind": "ref", "name": "din"}]}}},
                      {"match": {"kind": "const", "bits": "01"}, "body": {"kind": "nba", "target": {"kind": "ref", "name": "acc"}, "value": {"kind": "sub", "args": [{"kind": "ref", "name": "acc"}, {"kind": "ref", "name": "din"}]}}},
                      {"match": {"kind": "const", "bits": "10"}, "body": {"kind": "nba", "target": {"kind": "ref", "name": "acc"}, "value": {"kind": "bitxor", "args": [{"kind": "ref", "name": "acc"}, {"kind": "shl", "args": [{"kind": "ref", "name": "din"}, {"kind": "const", "bits": "0001"}]}]}}},
                      {"match": null, "body": {"kind": "nba", "target": {"kind": "ref", "name": "acc"}, "value": {"kind": "shr", "args": [{"kind": "ref", "name": "acc"}, {"kind": "const", "bits": "0001"}]}}}
                    ]
                  },
                  {
                    "kind": "if",
                    "condition": {"kind": "ref", "name": "we"},
                    "then": {"kind": "nba", "target": {"kind": "index", "name": "ram", "index": {"kind": "ref", "name": "addr"}}, "value": {"kind": "ref", "name": "din"}},
                    "else": {"kind": "block", "items": []}
                  },
                  {"kind": "nba", "target": {"kind": "ref", "name": "rdata"}, "value": {"kind": "index", "name": "ram", "index": {"kind": "ref", "name": "addr"}}}
                ]
              }
            }
          }
        }
        """;

    private const string RichOperatorsIr = """
        {
          "format": "velosim.rtl.v1",
          "signals": [
            {"name": "clk", "width": 1, "direction": "input"},
            {"name": "a", "width": 16, "direction": "input", "signed": true},
            {"name": "b", "width": 16, "direction": "input", "signed": true},
            {"name": "sh", "width": 4, "direction": "input"},
            {"name": "sel", "width": 2, "direction": "input"},
            {"name": "out1", "width": 16, "direction": "output"},
            {"name": "out2", "width": 16, "direction": "output"},
            {"name": "out3", "width": 16, "direction": "output"},
            {"name": "hi", "width": 8, "direction": "output"},
            {"name": "lo", "width": 8, "direction": "output", "two_state": true}
          ],
          "memories": [
            {"name": "m0", "width": 16, "depth": 4, "lower": 2}
          ],
          "continuous": [
            {
              "target": {"kind": "ref", "name": "out1"},
              "value": {
                "kind": "add",
                "args": [
                  {"kind": "mul", "args": [{"kind": "ref", "name": "a"}, {"kind": "ref", "name": "b"}]},
                  {"kind": "div", "args": [{"kind": "castunsigned", "args": [{"kind": "ref", "name": "a"}]}, {"kind": "bitor", "args": [{"kind": "castunsigned", "args": [{"kind": "ref", "name": "b"}]}, {"kind": "const", "bits": "0000000000000001"}]}]}
                ]
              }
            },
            {
              "target": {"kind": "ref", "name": "out2"},
              "value": {
                "kind": "cond",
                "args": [
                  {"kind": "and", "args": [
                    {"kind": "inside", "value": {"kind": "ref", "name": "sel"}, "items": [{"lo": {"kind": "const", "bits": "00"}, "hi": {"kind": "const", "bits": "10"}}, {"kind": "const", "bits": "1z"}]},
                    {"kind": "or", "args": [{"kind": "lt", "args": [{"kind": "ref", "name": "a"}, {"kind": "ref", "name": "b"}]}, {"kind": "ge", "args": [{"kind": "ref", "name": "a"}, {"kind": "ref", "name": "b"}]}]}
                  ]},
                  {"kind": "bitselect", "value": {"kind": "ashr", "args": [{"kind": "ref", "name": "a"}, {"kind": "ref", "name": "sh"}]}, "index": {"kind": "ref", "name": "sh"}},
                  {"kind": "indexedselect", "value": {"kind": "bitxnor", "args": [{"kind": "bitnot", "args": [{"kind": "ref", "name": "a"}]}, {"kind": "neg", "args": [{"kind": "ref", "name": "b"}]}]}, "base": {"kind": "ref", "name": "sh"}, "width": 4, "descending": false}
                ]
              }
            },
            {
              "target": {"kind": "ref", "name": "out3"},
              "value": {
                "kind": "resolve",
                "net": "wire",
                "args": [
                  {"kind": "concat", "args": [{"kind": "partselect", "value": {"kind": "ref", "name": "a"}, "left": 15, "right": 8}, {"kind": "partselect", "value": {"kind": "ref", "name": "b"}, "left": 7, "right": 0}]},
                  {"kind": "add", "args": [
                    {"kind": "countones", "args": [{"kind": "ref", "name": "a"}]},
                    {"kind": "add", "args": [
                      {"kind": "clog2", "args": [{"kind": "ref", "name": "b"}]},
                      {"kind": "add", "args": [
                        {"kind": "onehot", "args": [{"kind": "ref", "name": "a"}]},
                        {"kind": "add", "args": [
                          {"kind": "onehot0", "args": [{"kind": "ref", "name": "b"}]},
                          {"kind": "add", "args": [
                            {"kind": "isunknown", "args": [{"kind": "ref", "name": "a"}]},
                            {"kind": "add", "args": [
                              {"kind": "redand", "args": [{"kind": "ref", "name": "a"}]},
                              {"kind": "add", "args": [
                                {"kind": "rednand", "args": [{"kind": "ref", "name": "a"}]},
                                {"kind": "add", "args": [
                                  {"kind": "redor", "args": [{"kind": "ref", "name": "a"}]},
                                  {"kind": "add", "args": [
                                    {"kind": "rednor", "args": [{"kind": "ref", "name": "a"}]},
                                    {"kind": "add", "args": [
                                      {"kind": "redxor", "args": [{"kind": "ref", "name": "a"}]},
                                      {"kind": "redxnor", "args": [{"kind": "ref", "name": "a"}]}
                                    ]}
                                  ]}
                                ]}
                              ]}
                            ]}
                          ]}
                        ]}
                      ]}
                    ]}
                  ]}
                ]
              }
            }
          ],
          "clock": "clk",
          "body": {
            "kind": "process",
            "events": [{"signal": "clk", "edge": "posedge"}],
            "body": {
              "kind": "block",
              "items": [
                {"kind": "nba", "target": {"kind": "concat", "args": [{"kind": "ref", "name": "hi"}, {"kind": "ref", "name": "lo"}]}, "value": {"kind": "mod", "args": [{"kind": "ref", "name": "a"}, {"kind": "bitor", "args": [{"kind": "ref", "name": "b"}, {"kind": "const", "bits": "0000000000000001"}]}]}},
                {"kind": "nba", "target": {"kind": "bitselect", "value": {"kind": "ref", "name": "hi"}, "index": {"kind": "const", "bits": "001"}}, "value": {"kind": "caseeq", "args": [{"kind": "ref", "name": "a"}, {"kind": "ref", "name": "b"}]}},
                {"kind": "nba", "target": {"kind": "partselect", "value": {"kind": "ref", "name": "hi"}, "left": 5, "right": 2}, "value": {"kind": "wildeq", "args": [{"kind": "ref", "name": "a"}, {"kind": "ref", "name": "b"}]}},
                {"kind": "nba", "target": {"kind": "indexedselect", "value": {"kind": "ref", "name": "lo"}, "base": {"kind": "ref", "name": "sel"}, "width": 2, "descending": false}, "value": {"kind": "eq", "args": [{"kind": "ref", "name": "a"}, {"kind": "ref", "name": "b"}]}},
                {
                  "kind": "case",
                  "case_kind": "casez",
                  "value": {"kind": "ref", "name": "sel"},
                  "items": [
                    {"matches": [{"kind": "const", "bits": "0z"}, {"kind": "const", "bits": "10"}], "body": {"kind": "nba", "target": {"kind": "index", "name": "m0", "index": {"kind": "add", "args": [{"kind": "ref", "name": "sel"}, {"kind": "const", "bits": "10"}]}}, "value": {"kind": "ref", "name": "out1"}}},
                    {"match": null, "body": {"kind": "repeat", "count": 2, "body": {"kind": "nba", "target": {"kind": "ref", "name": "lo"}, "value": {"kind": "not", "args": [{"kind": "neq", "args": [{"kind": "ref", "name": "a"}, {"kind": "ref", "name": "b"}]}]}}}}
                  ]
                },
                {
                  "kind": "case",
                  "case_kind": "casex",
                  "value": {"kind": "ref", "name": "sel"},
                  "items": [
                    {"match": {"kind": "const", "bits": "1x"}, "body": {"kind": "nba", "target": {"kind": "ref", "name": "hi"}, "value": {"kind": "index", "name": "m0", "index": {"kind": "const", "bits": "010"}}}}
                  ]
                }
              ]
            }
          }
        }
        """;

    [Test]
    public async Task CliRejectsInvalidLanesAndIncompatibleFlagsFailClosed()
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-simd-neg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "dut.rtl.json");
            await File.WriteAllTextAsync(irPath, SimdFifoAluIr);

            foreach (string[] badArgs in new string[][]
            {
                ["rtl-export", irPath, "--top", "dut", "--output-dir", Path.Combine(root, "b1"), "--lanes", "3"],
                ["rtl-export", irPath, "--top", "dut", "--output-dir", Path.Combine(root, "b2"), "--lanes", "4", "--threads", "2"],
                ["rtl-export", irPath, "--top", "dut", "--output-dir", Path.Combine(root, "b3"), "--lanes", "4", "--coverage"],
                ["rtl-export", irPath, "--top", "dut", "--output-dir", Path.Combine(root, "b4"), "--lanes", "4", "--vpi"],
            })
            {
                var err = new StringWriter();
                int rc = await ToolCli.RunAsync(badArgs, new StringWriter(), err);
                Assert.That(rc, Is.Not.Zero, $"Expected rejection for: {string.Join(' ', badArgs)}");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestCase(4, false)]
    [TestCase(4, true)]
    [TestCase(8, false)]
    [TestCase(8, true)]
    public void RichOperatorsEmitCleanlyAcrossCSharpRustAndCpp(int lanes, bool twoState)
    {
        var design = RtlDesign.Parse(RichOperatorsIr);
        var mode = twoState ? RtlStateMode.TwoState : RtlStateMode.FourState;
        Assert.That(design.CanUseSimdLanes(mode), Is.True);

        string cs = design.EmitCSharpSimdModel("rich_ops", lanes, mode);
        string csRt = RtlDesign.EmitCSharpSimdRuntime(lanes);
        string rs = design.EmitRustSimdModel("rich_ops", lanes, mode);
        string rsRt = RtlDesign.EmitRustSimdRuntime(lanes);
        string cpp = design.EmitCppSimdModelHeader("rich_ops", lanes, mode);
        string cppRt = RtlDesign.EmitCppSimdRuntimeHeader(lanes);

        Assert.That(cs, Does.Not.Contain("unsafe "));
        Assert.That(csRt, Does.Not.Contain("unsafe "));
        Assert.That(cs, Does.Contain($"public const int Lanes = {lanes};"));
        Assert.That(rs, Does.Contain("#![forbid(unsafe_code)]"));
        Assert.That(rsRt, Does.Contain("#![forbid(unsafe_code)]"));
        Assert.That(rs, Does.Contain($"pub const LANES: usize = {lanes};"));
        Assert.That(cpp, Does.Contain($"static constexpr int kLanes = {lanes};"));
        Assert.That(cppRt, Does.Contain($"inline constexpr int kLanes = {lanes};"));
    }

    [TestCase(4, false)]
    [TestCase(4, true)]
    [TestCase(8, false)]
    [TestCase(8, true)]
    public async Task CSharpSimdMatchesScalarBitForBitWithDivergentSeedsAndFaultInjection(int lanes, bool twoState)
    {
        string root = Path.Combine(Path.GetTempPath(), $"velosim-cs-simd-{lanes}-{(twoState ? "2s" : "4s")}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "dut.rtl.json");
            string sdkDir = Path.Combine(root, "sdk");
            string hostDir = Path.Combine(root, "host");
            await File.WriteAllTextAsync(irPath, SimdFifoAluIr);

            var args = new List<string> { "rtl-export", irPath, "--top", "simd_dut", "--output-dir", sdkDir, "--target", "csharp", "--lanes", lanes.ToString() };
            if (twoState) args.Add("--two-state");
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(args.ToArray(), new StringWriter(), err), Is.Zero, err.ToString());

            string modelCs = await File.ReadAllTextAsync(Path.Combine(sdkDir, "Vsimd_dut.cs"));
            string runtimeCs = await File.ReadAllTextAsync(Path.Combine(sdkDir, "VeloSimSimdRuntime.cs"));
            Assert.That(modelCs, Does.Not.Contain("unsafe "));
            Assert.That(runtimeCs, Does.Not.Contain("unsafe "));

            Directory.CreateDirectory(hostDir);
            await File.WriteAllTextAsync(Path.Combine(hostDir, "Host.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <LangVersion>15.0</LangVersion>
                    <AnalysisLevel>11.0</AnalysisLevel>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{Path.Combine(sdkDir, "Vsimd_dut.csproj")}" />
                  </ItemGroup>
                </Project>
                """);

            await File.WriteAllTextAsync(Path.Combine(hostDir, "Program.cs"), $$"""
                using System;
                using VeloSim.Generated;

                const int L = {{lanes}};
                var dut = new Vsimd_dut();
                if (Vsimd_dut.Lanes != L) throw new Exception("lane count mismatch");

                // Scalar golden reference for each lane
                ushort[] gAcc = new ushort[L];
                ushort[] gRdata = new ushort[L];
                ushort[,] gRam = new ushort[L, 8];
                uint[] seeds = new uint[L];
                for (int l = 0; l < L; ++l) seeds[l] = (uint)(0x12345678U + l * 0x9E3779B9U);

                // Reset all lanes first
                dut.Set_rst_All(1);
                dut.Set_clk_All(0); dut.Eval();
                dut.Set_clk_All(1); dut.Eval();
                dut.Set_clk_All(0); dut.Eval();
                if (dut.DivergedMask_acc(0) != 0U) throw new Exception("post-reset lanes diverged");

                // Phase 1: Identical inputs -> zero divergence; inject fault on lane 2 -> bit 2 set!
                dut.Set_rst_All(0);
                dut.Set_we_All(1);
                dut.Set_op_All(0);
                dut.Set_addr_All(3);
                dut.Set_din_All(0x0011);
                dut.Set_din(2, 0x0022); // fault injected on lane 2
                dut.Set_clk_All(1); dut.Eval();
                dut.Set_clk_All(0); dut.Eval();
                if (dut.DivergedMask_acc(0) != (1U << 2))
                    throw new Exception($"expected fault mask 4, got {dut.DivergedMask_acc(0)}");

                // Phase 2: Initialize all 8 RAM addresses to 0 (known 4-state), re-reset, and run 500 divergent cycles
                dut.Set_rst_All(0); dut.Set_we_All(1); dut.Set_op_All(0); dut.Set_din_All(0);
                for (ulong a = 0; a < 8; ++a) { dut.Set_addr_All(a); dut.Set_clk_All(1); dut.Eval(); dut.Set_clk_All(0); dut.Eval(); }
                dut.Set_rst_All(1); dut.Set_we_All(0);
                dut.Set_clk_All(1); dut.Eval();
                dut.Set_clk_All(0); dut.Eval();

                for (int cyc = 0; cyc < 500; ++cyc)
                {
                    for (int l = 0; l < L; ++l)
                    {
                        seeds[l] = seeds[l] * 1664525U + 1013904223U;
                        uint r = seeds[l];
                        ulong rst = ((cyc + l) % 97 == 0) ? 1UL : 0UL;
                        ulong we = (r >> 3) & 1UL;
                        ulong op = (r >> 4) & 3UL;
                        ulong addr = (r >> 6) & 7UL;
                        ulong din = (r >> 9) & 0xFFFFUL;

                        dut.Set_rst(l, rst);
                        dut.Set_we(l, we);
                        dut.Set_op(l, op);
                        dut.Set_addr(l, addr);
                        dut.Set_din(l, din);

                        // Scalar golden step (NBA semantics: read old ram before write)
                        if (rst != 0)
                        {
                            gAcc[l] = 0;
                            gRdata[l] = 0;
                        }
                        else
                        {
                            ushort oldAcc = gAcc[l];
                            ushort nextAcc = op switch
                            {
                                0 => (ushort)(oldAcc + din),
                                1 => (ushort)(oldAcc - din),
                                2 => (ushort)(oldAcc ^ (ushort)(din << 1)),
                                _ => (ushort)(oldAcc >> 1)
                            };
                            ushort nextRdata = gRam[l, addr];
                            if (we != 0) gRam[l, addr] = (ushort)din;
                            gAcc[l] = nextAcc;
                            gRdata[l] = nextRdata;
                        }
                    }

                    dut.Set_clk_All(1); dut.Eval();
                    dut.Set_clk_All(0); dut.Eval();

                    for (int l = 0; l < L; ++l)
                    {
                        ushort expectedMix = (ushort)(gAcc[l] ^ (ushort)(gRdata[l] + 5));
                        if (dut.Get_acc(l) != gAcc[l]) throw new Exception($"cyc={cyc} lane={l} acc mismatch: {dut.Get_acc(l)} != {gAcc[l]}");
                        if (dut.Get_rdata(l) != gRdata[l]) throw new Exception($"cyc={cyc} lane={l} rdata mismatch: {dut.Get_rdata(l)} != {gRdata[l]}");
                        if (dut.Get_mix(l) != expectedMix) throw new Exception($"cyc={cyc} lane={l} mix mismatch: {dut.Get_mix(l)} != {expectedMix}");
                    }
                }
                Console.WriteLine("OK");
                """);

            var run = await RunCommandAsync("dotnet", ["run", "-c", "Release", "--disable-build-servers"], hostDir);
            Assert.That(run.ExitCode, Is.Zero, run.Output);
            Assert.That(run.Output, Does.Contain("OK"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestCase(4, false)]
    [TestCase(4, true)]
    [TestCase(8, false)]
    [TestCase(8, true)]
    public async Task RustSimdMatchesScalarBitForBitWithForbidUnsafe(int lanes, bool twoState)
    {
        if (!await ToolExistsAsync("cargo")) Assert.Ignore("cargo not installed");
        string root = Path.Combine(Path.GetTempPath(), $"velosim-rs-simd-{lanes}-{(twoState ? "2s" : "4s")}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "dut.rtl.json");
            string sdkDir = Path.Combine(root, "sdk");
            await File.WriteAllTextAsync(irPath, SimdFifoAluIr);

            var args = new List<string> { "rtl-export", irPath, "--top", "simd_dut", "--output-dir", sdkDir, "--target", "rust", "--lanes", lanes.ToString() };
            if (twoState) args.Add("--two-state");
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(args.ToArray(), new StringWriter(), err), Is.Zero, err.ToString());

            string setFn = twoState ? "set_" : "set_";
            string setSuf = twoState ? "" : "_u64";
            string setAllSuf = twoState ? "_all" : "_all_u64";
            string getSuf = twoState ? "" : "_u64";

            Directory.CreateDirectory(Path.Combine(sdkDir, "tests"));
            await File.WriteAllTextAsync(Path.Combine(sdkDir, "tests", "simd_verify.rs"), $$"""
                use velosim_simd_dut::Vsimd_dut;

                #[test]
                fn verify_lockstep_lanes() {
                    const L: usize = {{lanes}};
                    assert_eq!(Vsimd_dut::LANES, L);
                    let mut dut = Vsimd_dut::new();
                    let mut g_acc = [0u16; L];
                    let mut g_rdata = [0u16; L];
                    let mut g_ram = [[0u16; 8]; L];
                    let mut seeds = [0u32; L];
                    for l in 0..L { seeds[l] = 0x12345678u32.wrapping_add((l as u32).wrapping_mul(0x9E3779B9u32)); }

                    dut.{{setFn}}rst{{setAllSuf}}(1);
                    dut.{{setFn}}clk{{setAllSuf}}(0); dut.eval();
                    dut.{{setFn}}clk{{setAllSuf}}(1); dut.eval();
                    dut.{{setFn}}clk{{setAllSuf}}(0); dut.eval();
                    assert_eq!(dut.diverged_mask_acc(0), 0);

                    // Fault injection on lane 3
                    dut.{{setFn}}rst{{setAllSuf}}(0);
                    dut.{{setFn}}we{{setAllSuf}}(1);
                    dut.{{setFn}}op{{setAllSuf}}(0);
                    dut.{{setFn}}addr{{setAllSuf}}(2);
                    dut.{{setFn}}din{{setAllSuf}}(0x0042);
                    dut.{{setFn}}din{{setSuf}}(3, 0x0099);
                    dut.{{setFn}}clk{{setAllSuf}}(1); dut.eval();
                    dut.{{setFn}}clk{{setAllSuf}}(0); dut.eval();
                    assert_eq!(dut.diverged_mask_acc(0), 1u32 << 3);

                    // Initialize all 8 RAM addresses to 0 (known 4-state), re-reset, and run 500 divergent cycles
                    dut.{{setFn}}rst{{setAllSuf}}(0); dut.{{setFn}}we{{setAllSuf}}(1); dut.{{setFn}}op{{setAllSuf}}(0); dut.{{setFn}}din{{setAllSuf}}(0);
                    for a in 0..8u64 { dut.{{setFn}}addr{{setAllSuf}}(a); dut.{{setFn}}clk{{setAllSuf}}(1); dut.eval(); dut.{{setFn}}clk{{setAllSuf}}(0); dut.eval(); }
                    dut.{{setFn}}rst{{setAllSuf}}(1); dut.{{setFn}}we{{setAllSuf}}(0);
                    dut.{{setFn}}clk{{setAllSuf}}(1); dut.eval();
                    dut.{{setFn}}clk{{setAllSuf}}(0); dut.eval();

                    for cyc in 0..500usize {
                        for l in 0..L {
                            seeds[l] = seeds[l].wrapping_mul(1664525).wrapping_add(1013904223);
                            let r = seeds[l] as u64;
                            let rst = if (cyc + l) % 97 == 0 { 1u64 } else { 0u64 };
                            let we = (r >> 3) & 1;
                            let op = (r >> 4) & 3;
                            let addr = ((r >> 6) & 7) as usize;
                            let din = (r >> 9) & 0xFFFF;

                            dut.{{setFn}}rst{{setSuf}}(l, rst);
                            dut.{{setFn}}we{{setSuf}}(l, we);
                            dut.{{setFn}}op{{setSuf}}(l, op);
                            dut.{{setFn}}addr{{setSuf}}(l, addr as u64);
                            dut.{{setFn}}din{{setSuf}}(l, din);

                            if rst != 0 {
                                g_acc[l] = 0; g_rdata[l] = 0;
                            } else {
                                let old = g_acc[l];
                                let next_acc = match op {
                                    0 => old.wrapping_add(din as u16),
                                    1 => old.wrapping_sub(din as u16),
                                    2 => old ^ ((din as u16) << 1),
                                    _ => old >> 1,
                                };
                                let next_rd = g_ram[l][addr];
                                if we != 0 { g_ram[l][addr] = din as u16; }
                                g_acc[l] = next_acc;
                                g_rdata[l] = next_rd;
                            }
                        }
                        dut.{{setFn}}clk{{setAllSuf}}(1); dut.eval();
                        dut.{{setFn}}clk{{setAllSuf}}(0); dut.eval();
                        for l in 0..L {
                            let exp_mix = g_acc[l] ^ g_rdata[l].wrapping_add(5);
                            assert_eq!(dut.get_acc{{getSuf}}(l), g_acc[l] as u64);
                            assert_eq!(dut.get_rdata{{getSuf}}(l), g_rdata[l] as u64);
                            assert_eq!(dut.get_mix{{getSuf}}(l), exp_mix as u64);
                        }
                    }
                }
                """);

            var run = await RunCommandAsync("cargo", ["test", "--release"], sdkDir);
            Assert.That(run.ExitCode, Is.Zero, run.Output);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestCase(4, false)]
    [TestCase(4, true)]
    [TestCase(8, false)]
    [TestCase(8, true)]
    public async Task CppSimdMatchesScalarBitForBitWithDivergentSeeds(int lanes, bool twoState)
    {
        string cxx = await ToolExistsAsync("c++") ? "c++" : "g++";
        string root = Path.Combine(Path.GetTempPath(), $"velosim-cpp-simd-{lanes}-{(twoState ? "2s" : "4s")}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "dut.rtl.json");
            string sdkDir = Path.Combine(root, "sdk");
            await File.WriteAllTextAsync(irPath, SimdFifoAluIr);

            var args = new List<string> { "rtl-export", irPath, "--top", "simd_dut", "--output-dir", sdkDir, "--target", "cpp", "--lanes", lanes.ToString() };
            if (twoState) args.Add("--two-state");
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(args.ToArray(), new StringWriter(), err), Is.Zero, err.ToString());

            string setSuf = twoState ? "" : "_u64";
            string setAllSuf = twoState ? "_all" : "_all_u64";
            string getSuf = twoState ? "" : "_u64";

            string hostCpp = Path.Combine(root, "host.cpp");
            string exePath = Path.Combine(root, "host");
            await File.WriteAllTextAsync(hostCpp, $$"""
                #include "Vsimd_dut.h"
                #include <cstdint>
                #include <iostream>

                int main() {
                    constexpr int L = {{lanes}};
                    static_assert(Vsimd_dut::kLanes == L);
                    Vsimd_dut dut;
                    uint16_t g_acc[L]{}, g_rdata[L]{}, g_ram[L][8]{};
                    uint32_t seeds[L]{};
                    for (int l = 0; l < L; ++l) seeds[l] = 0x12345678U + static_cast<uint32_t>(l) * 0x9E3779B9U;

                    dut.set_rst{{setAllSuf}}(1);
                    dut.set_clk{{setAllSuf}}(0); dut.eval();
                    dut.set_clk{{setAllSuf}}(1); dut.eval();
                    dut.set_clk{{setAllSuf}}(0); dut.eval();
                    if (dut.diverged_mask_acc(0) != 0U) return 1;

                    // Fault injection on lane 1
                    dut.set_rst{{setAllSuf}}(0);
                    dut.set_we{{setAllSuf}}(1);
                    dut.set_op{{setAllSuf}}(0);
                    dut.set_addr{{setAllSuf}}(1);
                    dut.set_din{{setAllSuf}}(0x0010);
                    dut.set_din{{setSuf}}(1, 0x0033);
                    dut.set_clk{{setAllSuf}}(1); dut.eval();
                    dut.set_clk{{setAllSuf}}(0); dut.eval();
                    if (dut.diverged_mask_acc(0) != (1U << 1)) return 2;

                    // Initialize all 8 RAM addresses to 0 (known 4-state), re-reset, and run 500 divergent cycles
                    dut.set_rst{{setAllSuf}}(0); dut.set_we{{setAllSuf}}(1); dut.set_op{{setAllSuf}}(0); dut.set_din{{setAllSuf}}(0);
                    for (uint64_t a = 0; a < 8; ++a) { dut.set_addr{{setAllSuf}}(a); dut.set_clk{{setAllSuf}}(1); dut.eval(); dut.set_clk{{setAllSuf}}(0); dut.eval(); }
                    dut.set_rst{{setAllSuf}}(1); dut.set_we{{setAllSuf}}(0);
                    dut.set_clk{{setAllSuf}}(1); dut.eval();
                    dut.set_clk{{setAllSuf}}(0); dut.eval();

                    for (int cyc = 0; cyc < 500; ++cyc) {
                        for (int l = 0; l < L; ++l) {
                            seeds[l] = seeds[l] * 1664525U + 1013904223U;
                            uint64_t r = seeds[l];
                            uint64_t rst = ((cyc + l) % 97 == 0) ? 1ULL : 0ULL;
                            uint64_t we = (r >> 3) & 1ULL;
                            uint64_t op = (r >> 4) & 3ULL;
                            size_t addr = static_cast<size_t>((r >> 6) & 7ULL);
                            uint64_t din = (r >> 9) & 0xFFFFULL;

                            dut.set_rst{{setSuf}}(l, rst);
                            dut.set_we{{setSuf}}(l, we);
                            dut.set_op{{setSuf}}(l, op);
                            dut.set_addr{{setSuf}}(l, addr);
                            dut.set_din{{setSuf}}(l, din);

                            if (rst) { g_acc[l] = 0; g_rdata[l] = 0; }
                            else {
                                uint16_t old = g_acc[l];
                                uint16_t next_acc = op == 0 ? static_cast<uint16_t>(old + din)
                                    : op == 1 ? static_cast<uint16_t>(old - din)
                                    : op == 2 ? static_cast<uint16_t>(old ^ static_cast<uint16_t>(din << 1))
                                    : static_cast<uint16_t>(old >> 1);
                                uint16_t next_rd = g_ram[l][addr];
                                if (we) g_ram[l][addr] = static_cast<uint16_t>(din);
                                g_acc[l] = next_acc;
                                g_rdata[l] = next_rd;
                            }
                        }
                        dut.set_clk{{setAllSuf}}(1); dut.eval();
                        dut.set_clk{{setAllSuf}}(0); dut.eval();
                        for (int l = 0; l < L; ++l) {
                            uint16_t exp_mix = static_cast<uint16_t>(g_acc[l] ^ static_cast<uint16_t>(g_rdata[l] + 5));
                            if (dut.get_acc{{getSuf}}(l) != g_acc[l]) return 3;
                            if (dut.get_rdata{{getSuf}}(l) != g_rdata[l]) return 4;
                            if (dut.get_mix{{getSuf}}(l) != exp_mix) return 5;
                        }
                    }
                    std::cout << "OK\n";
                    return 0;
                }
                """);

            var compile = await RunCommandAsync(cxx, ["-std=c++20", "-O3", "-I" + sdkDir, hostCpp, "-o", exePath], root);
            Assert.That(compile.ExitCode, Is.Zero, compile.Output);
            var run = await RunCommandAsync(exePath, [], root);
            Assert.That(run.ExitCode, Is.Zero, run.Output);
            Assert.That(run.Output, Does.Contain("OK"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
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
        try
        {
            var r = await RunCommandAsync(tool, ["--version"], Environment.CurrentDirectory);
            return r.ExitCode == 0;
        }
        catch { return false; }
    }
}
