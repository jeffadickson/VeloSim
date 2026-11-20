using System.Diagnostics;
using System.Text.Json;
using NUnit.Framework;

namespace VeloSim.Tests;

public sealed class RtlVerilatorGapsWave2Tests
{
    private const string Wide512Ir = """
        {
          "format": "velosim.rtl.v1",
          "signals": [
            {"name": "clk", "width": 1, "direction": "input"},
            {"name": "fi_mask", "width": 512, "direction": "input"},
            {"name": "acc", "width": 512, "direction": "output"},
            {"name": "hi_byte", "width": 8, "direction": "output"}
          ],
          "memories": [],
          "continuous": [
            {"target": {"kind": "ref", "name": "hi_byte"}, "value": {"kind": "partselect", "value": {"kind": "ref", "name": "acc"}, "left": 511, "right": 504}}
          ],
          "clock": "clk",
          "body": {
            "kind": "process",
            "events": [{"signal": "clk", "edge": "posedge"}],
            "body": {
              "kind": "nba",
              "target": {"kind": "ref", "name": "acc"},
              "value": {"kind": "bitxor", "args": [{"kind": "ref", "name": "fi_mask"}, {"kind": "const", "bits": "10101010"}]}
            }
          }
        }
        """;

    private const string MultiCaseWildcardIr = """
        {
          "format": "velosim.rtl.v1",
          "signals": [
            {"name": "clk", "width": 1, "direction": "input"},
            {"name": "op", "width": 4, "direction": "input"},
            {"name": "cls", "width": 4, "direction": "output"},
            {"name": "zcls", "width": 4, "direction": "output"},
            {"name": "xcls", "width": 4, "direction": "output"},
            {"name": "weq", "width": 1, "direction": "output"},
            {"name": "wneq", "width": 1, "direction": "output"}
          ],
          "memories": [],
          "continuous": [
            {"target": {"kind": "ref", "name": "weq"}, "value": {"kind": "wildeq", "args": [{"kind": "ref", "name": "op"}, {"kind": "const", "bits": "10zz"}]}},
            {"target": {"kind": "ref", "name": "wneq"}, "value": {"kind": "wildneq", "args": [{"kind": "ref", "name": "op"}, {"kind": "const", "bits": "10zz"}]}}
          ],
          "clock": "clk",
          "body": {
            "kind": "process",
            "events": [{"signal": "clk", "edge": "posedge"}],
            "body": {
              "kind": "block",
              "items": [
                {
                  "kind": "case",
                  "value": {"kind": "ref", "name": "op"},
                  "items": [
                    {
                      "matches": [{"kind": "const", "bits": "0001"}, {"kind": "const", "bits": "0010"}, {"kind": "const", "bits": "0011"}],
                      "body": {"kind": "nba", "target": {"kind": "ref", "name": "cls"}, "value": {"kind": "const", "bits": "0001"}}
                    },
                    {
                      "match": {"kind": "const", "bits": "1000"},
                      "body": {"kind": "nba", "target": {"kind": "ref", "name": "cls"}, "value": {"kind": "const", "bits": "0010"}}
                    },
                    {
                      "match": null,
                      "body": {"kind": "nba", "target": {"kind": "ref", "name": "cls"}, "value": {"kind": "const", "bits": "1111"}}
                    }
                  ]
                },
                {
                  "kind": "case",
                  "case_kind": "casez",
                  "value": {"kind": "ref", "name": "op"},
                  "items": [
                    {
                      "matches": [{"kind": "const", "bits": "1zzz"}],
                      "body": {"kind": "nba", "target": {"kind": "ref", "name": "zcls"}, "value": {"kind": "const", "bits": "1010"}}
                    },
                    {
                      "match": null,
                      "body": {"kind": "nba", "target": {"kind": "ref", "name": "zcls"}, "value": {"kind": "const", "bits": "0101"}}
                    }
                  ]
                },
                {
                  "kind": "case",
                  "case_kind": "casex",
                  "value": {"kind": "ref", "name": "op"},
                  "items": [
                    {
                      "match": {"kind": "const", "bits": "01xx"},
                      "body": {"kind": "nba", "target": {"kind": "ref", "name": "xcls"}, "value": {"kind": "const", "bits": "1100"}}
                    },
                    {
                      "match": null,
                      "body": {"kind": "nba", "target": {"kind": "ref", "name": "xcls"}, "value": {"kind": "const", "bits": "0011"}}
                    }
                  ]
                }
              ]
            }
          }
        }
        """;

    private const string DpiCallIr = """
        {
          "format": "velosim.rtl.v1",
          "signals": [
            {"name": "clk", "width": 1, "direction": "input"},
            {"name": "a", "width": 32, "direction": "input"},
            {"name": "b", "width": 32, "direction": "input"},
            {"name": "y", "width": 32, "direction": "output"}
          ],
          "memories": [],
          "continuous": [],
          "clock": "clk",
          "body": {
            "kind": "process",
            "events": [{"signal": "clk", "edge": "posedge"}],
            "body": {
              "kind": "nba",
              "target": {"kind": "ref", "name": "y"},
              "value": {
                "kind": "dpi_call",
                "name": "velosim_dpi_mix",
                "width": 32,
                "args": [{"kind": "ref", "name": "a"}, {"kind": "ref", "name": "b"}]
              }
            }
          }
        }
        """;

    [Test]
    public async Task Wide512BitDesignCompilesRunsWithVcdAndCheckpointUnderLargeLimits()
    {
        // Bounded default must still fail closed on 512-bit signals.
        Assert.Throws<IrValidationException>(() => RtlDesign.Parse(Wide512Ir));

        string root = Path.Combine(Path.GetTempPath(), "velosim-wide512-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "wide512.rtl.json");
            string sdkDir = Path.Combine(root, "sdk");
            string buildDir = Path.Combine(root, "build");
            string vcdPath = Path.Combine(root, "wide.vcd");
            string ckptPath = Path.Combine(root, "wide.vscp");
            string hostPath = Path.Combine(root, "host.cpp");

            await File.WriteAllTextAsync(irPath, Wide512Ir);
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "wide512", "--output-dir", sdkDir, "--large-design"], new StringWriter(), err), Is.Zero, err.ToString());

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sdkDir, "external-model.json")));
            Assert.That(manifest.RootElement.GetProperty("max_word_bits").GetInt32(), Is.EqualTo(512));
            Assert.That(await File.ReadAllTextAsync(Path.Combine(sdkDir, "CMakeLists.txt")), Does.Contain("VSRTL_MAX_WORD_BITS=512"));

            await File.WriteAllTextAsync(hostPath, $$"""
                #include "Vwide512.h"
                #include <iostream>
                #include <string>
                int main() {
                    Vwide512 dut;
                    dut.openVcd("{{vcdPath.Replace("\\", "\\\\")}}", "1ns");
                    std::string mask512(512, '0');
                    mask512[0] = '1'; // bit 511
                    mask512[1] = '1'; // bit 510
                    mask512[7] = '1'; // bit 504 -> top byte 0b11000001 = 0xc1
                    mask512[511] = '1'; // bit 0 -> xor 0xaa -> 0xab
                    dut.fi_mask.bits(mask512);
                    dut.clk = 0; dut.eval(); dut.dumpVcd();
                    dut.timeInc(10); dut.clk = 1; dut.eval(); dut.dumpVcd();
                    if ((uint64_t)dut.hi_byte != 0xc1ULL) { std::cerr << "bad hi_byte: " << (uint64_t)dut.hi_byte << "\n"; return 1; }
                    std::string accBits = dut.acc.bits();
                    if (accBits.size() != 512 || accBits.substr(0, 8) != "11000001" || accBits.substr(504, 8) != "10101011") {
                        std::cerr << "bad acc bits\n"; return 2;
                    }
                    dut.saveCheckpoint("{{ckptPath.Replace("\\", "\\\\")}}");
                    dut.fi_mask.bits(std::string(512, '0'));
                    dut.timeInc(10); dut.clk = 0; dut.eval();
                    dut.timeInc(10); dut.clk = 1; dut.eval();
                    if ((uint64_t)dut.hi_byte != 0ULL) return 3;
                    dut.restoreCheckpoint("{{ckptPath.Replace("\\", "\\\\")}}");
                    if ((uint64_t)dut.hi_byte != 0xc1ULL || dut.time() != 10ULL) return 4;
                    dut.final();
                    std::cout << "WIDE512_OK\n";
                    return 0;
                }
                """);

            Assert.That(await RunAsync("cmake", ["-S", sdkDir, "-B", buildDir, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", $"-DVELOSIM_HOST_SOURCE={hostPath}"], root), Is.EqualTo(0));
            Assert.That(await RunAsync("cmake", ["--build", buildDir, "--parallel", "2"], root), Is.EqualTo(0));
            var (rc, stdout, stderr) = await RunCaptureAsync(Path.Combine(buildDir, "velosim_host"), [], buildDir);
            Assert.That(rc, Is.EqualTo(0), stderr);
            Assert.That(stdout, Does.Contain("WIDE512_OK"));
            string vcd = await File.ReadAllTextAsync(vcdPath);
            Assert.That(vcd, Does.Contain("$var wire 512").And.Contain("#10"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task MultiLabelCaseWildcardsAndCoverageWorkEndToEndInCpp()
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-wave2-cpp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "wild.rtl.json");
            string sdkDir = Path.Combine(root, "sdk");
            string buildDir = Path.Combine(root, "build");
            string covPath = Path.Combine(root, "cov.json");
            string hostPath = Path.Combine(root, "host.cpp");

            await File.WriteAllTextAsync(irPath, MultiCaseWildcardIr);
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "wild", "--output-dir", sdkDir, "--coverage"], new StringWriter(), err), Is.Zero, err.ToString());

            await File.WriteAllTextAsync(hostPath, $$"""
                #include "Vwild.h"
                #include <iostream>
                int main() {
                    Vwild dut;
                    auto step = [&](const char* bits) {
                        dut.op.bits(bits);
                        dut.clk = 0; dut.eval();
                        dut.clk = 1; dut.eval();
                    };
                    step("0010"); // multi-label arm 0, casez default, casex default, weq=0
                    if ((uint64_t)dut.cls != 1 || (uint64_t)dut.zcls != 5 || (uint64_t)dut.xcls != 3 || (uint64_t)dut.weq != 0 || (uint64_t)dut.wneq != 1) return 1;
                    step("1010"); // default cls=15, casez 1zzz=10, casex default=3, weq=1
                    if ((uint64_t)dut.cls != 15 || (uint64_t)dut.zcls != 10 || (uint64_t)dut.xcls != 3 || (uint64_t)dut.weq != 1 || (uint64_t)dut.wneq != 0) return 2;
                    step("01x1"); // default cls=15, casez default=5, casex 01xx=12, weq=0 (MSB 0 != 1)
                    if ((uint64_t)dut.cls != 15 || (uint64_t)dut.zcls != 5 || (uint64_t)dut.xcls != 12 || (uint64_t)dut.weq != 0) return 3;
                    step("1x10"); // weq = 1x10 ==? 10zz => x!
                    if (dut.weq.bits() != "x" || dut.wneq.bits() != "x") return 4;
                    auto pts = dut.coveragePoints();
                    if (pts.size() != 7) { std::cerr << "expected 7 cov pts, got " << pts.size() << "\n"; return 5; }
                    dut.writeCoverageJson("{{covPath.Replace("\\", "\\\\")}}");
                    dut.final();
                    std::cout << "WILD_COV_OK\n";
                    return 0;
                }
                """);

            Assert.That(await RunAsync("cmake", ["-S", sdkDir, "-B", buildDir, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", $"-DVELOSIM_HOST_SOURCE={hostPath}"], root), Is.EqualTo(0));
            Assert.That(await RunAsync("cmake", ["--build", buildDir, "--parallel", "2"], root), Is.EqualTo(0));
            var (rc, stdout, stderr) = await RunCaptureAsync(Path.Combine(buildDir, "velosim_host"), [], buildDir);
            Assert.That(rc, Is.EqualTo(0), stderr);
            Assert.That(stdout, Does.Contain("WILD_COV_OK"));

            using var covDoc = JsonDocument.Parse(await File.ReadAllTextAsync(covPath));
            Assert.That(covDoc.RootElement.GetProperty("schema").GetString(), Is.EqualTo("velosim.dut.coverage.v1"));
            Assert.That(covDoc.RootElement.GetProperty("total").GetInt32(), Is.EqualTo(7));
            Assert.That(covDoc.RootElement.GetProperty("covered").GetInt32(), Is.EqualTo(6)); // arm 1 ("1000") never hit
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task DpiCImportCallAndSvScopeBridgeWorkEndToEnd()
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-dpi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "dpi.rtl.json");
            string sdkDir = Path.Combine(root, "sdk");
            string buildDir = Path.Combine(root, "build");
            string hostPath = Path.Combine(root, "host.cpp");

            await File.WriteAllTextAsync(irPath, DpiCallIr);
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "dpi_top", "--output-dir", sdkDir], new StringWriter(), err), Is.Zero, err.ToString());

            await File.WriteAllTextAsync(hostPath, """
                #include "Vdpi_top.h"
                #include "svdpi.h"
                #include <cstring>
                #include <iostream>
                static int g_scope_ok = 0;
                extern "C" uint64_t velosim_dpi_mix(uint64_t a, uint64_t b) {
                    svScope sc = svGetScope();
                    const char* nm = svGetNameFromScope(sc);
                    if (nm && std::strcmp(nm, "dpi_top") == 0) ++g_scope_ok;
                    svBitVecVal vec = static_cast<svBitVecVal>(a);
                    svPutBitselBit(&vec, 0, sv_1);
                    return (uint64_t(vec) ^ (b << 1)) & 0xffffffffULL;
                }
                int main() {
                    Vdpi_top dut;
                    dut.a = 0x10;
                    dut.b = 0x20;
                    dut.clk = 0; dut.eval();
                    dut.clk = 1; dut.eval();
                    if ((uint64_t)dut.y != (0x11ULL ^ 0x40ULL) || g_scope_ok < 1) {
                        std::cerr << "bad dpi result y=" << (uint64_t)dut.y << " scope_ok=" << g_scope_ok << "\n";
                        return 1;
                    }
                    dut.final();
                    std::cout << "DPI_OK\n";
                    return 0;
                }
                """);

            Assert.That(await RunAsync("cmake", ["-S", sdkDir, "-B", buildDir, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", $"-DVELOSIM_HOST_SOURCE={hostPath}"], root), Is.EqualTo(0));
            Assert.That(await RunAsync("cmake", ["--build", buildDir, "--parallel", "2"], root), Is.EqualTo(0));
            var (rc, stdout, stderr) = await RunCaptureAsync(Path.Combine(buildDir, "velosim_host"), [], buildDir);
            Assert.That(rc, Is.EqualTo(0), stderr);
            Assert.That(stdout, Does.Contain("DPI_OK"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task CSharpAndRustTargetsExecuteMultiCaseWildcardsAndEmitVcd()
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-wave2-csrs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "wild.rtl.json");
            await File.WriteAllTextAsync(irPath, MultiCaseWildcardIr);

            // C# target: compile + run + check VCD
            string csSdk = Path.Combine(root, "cs_sdk");
            string csHost = Path.Combine(root, "cs_host");
            string csVcd = Path.Combine(root, "cs.vcd");
            Directory.CreateDirectory(csHost);
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "wild", "--target", "csharp", "--output-dir", csSdk], new StringWriter(), new StringWriter()), Is.Zero);
            await File.WriteAllTextAsync(Path.Combine(csHost, "Host.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{Path.Combine(csSdk, "Vwild.csproj")}" />
                  </ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(csHost, "Program.cs"), $$"""
                using System;
                using VeloSim.Generated;
                var dut = new Vwild();
                dut.OpenVcd(@"{{csVcd}}");
                dut.Set_op_Bits("0010"); dut.clk = 0; dut.Eval(); dut.DumpVcd();
                dut.TimeInc(5); dut.clk = 1; dut.Eval(); dut.DumpVcd();
                if (dut.cls != 1 || dut.zcls != 5 || dut.xcls != 3 || dut.weq != 0) return 1;
                dut.TimeInc(5); dut.Set_op_Bits("1010"); dut.clk = 0; dut.Eval();
                dut.TimeInc(5); dut.clk = 1; dut.Eval(); dut.DumpVcd();
                if (dut.cls != 15 || dut.zcls != 10 || dut.weq != 1) return 2;
                dut.Final();
                Console.WriteLine("CS_WAVE2_OK");
                return 0;
                """);
            var (csRc, csOut, csErr) = await RunCaptureAsync("dotnet", ["run", "--project", Path.Combine(csHost, "Host.csproj"), "-c", "Release"], csHost);
            Assert.That(csRc, Is.EqualTo(0), csErr);
            Assert.That(csOut, Does.Contain("CS_WAVE2_OK"));
            Assert.That(await File.ReadAllTextAsync(csVcd), Does.Contain("$version VeloSim $end").And.Contain("$dumpvars").And.Contain("#15"));

            // Rust target: compile + run + check VCD
            string rsSdk = Path.Combine(root, "rs_sdk");
            string rsVcd = Path.Combine(root, "rs.vcd");
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "wild", "--target", "rust", "--output-dir", rsSdk], new StringWriter(), new StringWriter()), Is.Zero);
            Directory.CreateDirectory(Path.Combine(rsSdk, "tests"));
            await File.WriteAllTextAsync(Path.Combine(rsSdk, "tests", "wave2.rs"), $$"""
                use velosim_wild::Vwild;
                #[test]
                fn test_wave2_wild_and_vcd() {
                    let mut dut = Vwild::new();
                    dut.open_vcd(r"{{rsVcd}}", "1ns").unwrap();
                    dut.set_op_bits("0010").unwrap();
                    dut.set_clk(0); dut.eval(); dut.dump_vcd().unwrap();
                    dut.time_inc(5); dut.set_clk(1); dut.eval(); dut.dump_vcd().unwrap();
                    assert_eq!(dut.cls(), 1);
                    assert_eq!(dut.zcls(), 5);
                    assert_eq!(dut.xcls(), 3);
                    assert_eq!(dut.weq(), 0);
                    dut.time_inc(5); dut.set_op_bits("1010").unwrap();
                    dut.set_clk(0); dut.eval();
                    dut.time_inc(5); dut.set_clk(1); dut.eval(); dut.dump_vcd().unwrap();
                    assert_eq!(dut.cls(), 15);
                    assert_eq!(dut.zcls(), 10);
                    assert_eq!(dut.weq(), 1);
                    dut.final_model();
                }
                """);
            var (rsRc, rsOut, rsErr) = await RunCaptureAsync("cargo", ["test", "--release"], rsSdk);
            Assert.That(rsRc, Is.EqualTo(0), rsOut + "\n" + rsErr);
            Assert.That(await File.ReadAllTextAsync(rsVcd), Does.Contain("$version VeloSim $end").And.Contain("$dumpvars").And.Contain("#15"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<int> RunAsync(string file, string[] args, string cwd)
    {
        var (rc, _, stderr) = await RunCaptureAsync(file, args, cwd);
        if (rc != 0) TestContext.Progress.WriteLine($"{file} {string.Join(' ', args)} failed:\n{stderr}");
        return rc;
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunCaptureAsync(string file, string[] args, string cwd)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, await outTask, await errTask);
    }
}
