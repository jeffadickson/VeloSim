using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace VeloSim.Tests;

public sealed class RtlVerilatorGapsWave1Tests
{
    private const string SysTaskRamIr = """
        {
          "format": "velosim.rtl.v1",
          "signals": [
            {"name": "clk", "width": 1, "direction": "input"},
            {"name": "we", "width": 1, "direction": "input"},
            {"name": "do_fail", "width": 1, "direction": "input"},
            {"name": "do_stop", "width": 1, "direction": "input"},
            {"name": "addr", "width": 4, "direction": "input"},
            {"name": "din", "width": 16, "direction": "input"},
            {"name": "dout", "width": 16, "direction": "output"}
          ],
          "memories": [
            {"name": "ram", "width": 16, "depth": 16}
          ],
          "continuous": [
            {"target": {"kind": "ref", "name": "dout"}, "value": {"kind": "index", "name": "ram", "index": {"kind": "ref", "name": "addr"}}}
          ],
          "clock": "clk",
          "body": {
            "kind": "process",
            "events": [{"signal": "clk", "edge": "posedge"}],
            "body": {
              "kind": "block",
              "items": [
                {"kind": "assert", "condition": {"kind": "not", "args": [{"kind": "ref", "name": "do_fail"}]}, "message": "do_fail tripped"},
                {
                  "kind": "if",
                  "condition": {"kind": "ref", "name": "we"},
                  "then": {
                    "kind": "block",
                    "items": [
                      {"kind": "nba", "target": {"kind": "index", "name": "ram", "index": {"kind": "ref", "name": "addr"}}, "value": {"kind": "ref", "name": "din"}},
                      {"kind": "display", "format": "WR addr=%0x val=%0x", "args": [{"kind": "ref", "name": "addr"}, {"kind": "ref", "name": "din"}]}
                    ]
                  },
                  "else": {"kind": "block", "items": []}
                },
                {
                  "kind": "if",
                  "condition": {"kind": "ref", "name": "do_stop"},
                  "then": {"kind": "finish"},
                  "else": {"kind": "block", "items": []}
                }
              ]
            }
          }
        }
        """;

    [Test]
    public void LargeDesignLimitsUnlockSocScaleWhileBoundedStaysFailClosed()
    {
        var sigs = new JsonArray { new JsonObject { ["name"] = "clk", ["width"] = 1, ["direction"] = "input" } };
        var cont = new JsonArray();
        for (int i = 0; i < 300; ++i)
        {
            sigs.Add(new JsonObject { ["name"] = $"in_{i}", ["width"] = 8, ["direction"] = "input" });
            sigs.Add(new JsonObject { ["name"] = $"out_{i}", ["width"] = 8, ["direction"] = "output" });
            cont.Add(new JsonObject
            {
                ["target"] = new JsonObject { ["kind"] = "ref", ["name"] = $"out_{i}" },
                ["value"] = new JsonObject { ["kind"] = "ref", ["name"] = $"in_{i}" }
            });
        }
        var ir = new JsonObject
        {
            ["format"] = "velosim.rtl.v1",
            ["signals"] = sigs,
            ["memories"] = new JsonArray { new JsonObject { ["name"] = "rom", ["width"] = 32, ["depth"] = 4096 } },
            ["continuous"] = cont,
            ["clock"] = null,
            ["body"] = new JsonObject { ["kind"] = "block", ["items"] = new JsonArray() }
        };
        string json = ir.ToJsonString();

        Assert.Throws<IrValidationException>(() => RtlDesign.Parse(json));
        var large = RtlDesign.Parse(json, RtlDesignLimits.Large);
        Assert.That(large.Signals.Count, Is.EqualTo(601));
        Assert.That(large.Memories[0].Depth, Is.EqualTo(4096));
        Assert.That(large.EmitModelHeader("soc_rom", RtlStateMode.TwoState), Does.Contain("uint64_t _vs_m0[4096]{};"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CppSdkEndToEndVcdCheckpointReadmemDisplayAssertFinish(bool twoState)
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-gaps-cpp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "sysram.rtl.json");
            string sdkDir = Path.Combine(root, "sdk");
            string buildDir = Path.Combine(root, "build");
            string hexPath = Path.Combine(root, "init.hex");
            string vcdPath = Path.Combine(root, "trace.vcd");
            string ckptPath = Path.Combine(root, "state.vscp");
            string hostPath = Path.Combine(root, "host.cpp");

            await File.WriteAllTextAsync(irPath, SysTaskRamIr);
            await File.WriteAllTextAsync(hexPath, "// firmware image\n@2\n1234 5678\n");

            string[] cliArgs = twoState
                ? ["rtl-export", irPath, "--top", "sysram", "--output-dir", sdkDir, "--two-state"]
                : ["rtl-export", irPath, "--top", "sysram", "--output-dir", sdkDir];
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(cliArgs, new StringWriter(), err), Is.Zero, err.ToString());

            await File.WriteAllTextAsync(hostPath, $$"""
                #include "Vsysram.h"
                #include <iostream>
                #include <fstream>
                #include <stdexcept>
                #include <string>

                static void req(bool ok, const char* msg) { if (!ok) throw std::runtime_error(msg); }

                int main() {
                    Vsysram dut;
                    std::string captured;
                    dut.setDisplaySink([&](const std::string& s) { captured += s; });
                    req(dut.loadMemoryHex(0, "{{hexPath.Replace("\\", "\\\\")}}") == 2, "loadMemoryHex count");

                    dut.openVcd("{{vcdPath.Replace("\\", "\\\\")}}", "1ns");
                    dut.clk = 0; dut.we = 0; dut.do_fail = 0; dut.do_stop = 0; dut.addr = 2; dut.din = 0;
                    dut.eval(); dut.dumpVcd();
                    req(uint64_t(dut.dout) == 0x1234, "hex addr 2 mismatch");

                    dut.timeInc(5); dut.addr = 3; dut.eval(); dut.dumpVcd();
                    req(uint64_t(dut.dout) == 0x5678, "hex addr 3 mismatch");

                    // Write addr 5 = 0xCAFE -> triggers $display
                    dut.timeInc(5); dut.addr = 5; dut.din = 0xCAFE; dut.we = 1;
                    dut.clk = 1; dut.eval(); dut.dumpVcd();
                    dut.timeInc(5); dut.clk = 0; dut.we = 0; dut.eval(); dut.dumpVcd();
                    req(uint64_t(dut.dout) == 0xCAFE, "write addr 5 mismatch");
                    req(captured == "WR addr=5 val=cafe\n", "display mismatch");

                    // Save checkpoint, overwrite addr 5, restore checkpoint and verify addr 5 is 0xCAFE again
                    dut.saveCheckpoint("{{ckptPath.Replace("\\", "\\\\")}}");
                    dut.timeInc(5); dut.addr = 5; dut.din = 0x1111; dut.we = 1;
                    dut.clk = 1; dut.eval();
                    dut.timeInc(5); dut.clk = 0; dut.we = 0; dut.eval();
                    req(uint64_t(dut.dout) == 0x1111, "overwrite mismatch");

                    dut.restoreCheckpoint("{{ckptPath.Replace("\\", "\\\\")}}");
                    dut.eval();
                    req(uint64_t(dut.dout) == 0xCAFE, "restore mismatch");

                    // Trigger assertion -> must throw and roll back state cleanly
                    bool threw = false;
                    dut.do_fail = 1; dut.we = 1; dut.addr = 5; dut.din = 0x9999; dut.clk = 1;
                    try { dut.eval(); } catch (const std::runtime_error& e) {
                        threw = std::string(e.what()).find("do_fail tripped") != std::string::npos;
                    }
                    req(threw, "assert did not throw");
                    dut.do_fail = 0; dut.we = 0; dut.clk = 0; dut.eval();
                    req(uint64_t(dut.dout) == 0xCAFE, "assert rollback failed");

                    // Trigger $finish
                    dut.do_stop = 1; dut.clk = 1; dut.eval();
                    req(dut.gotFinish(), "finish did not set gotFinish");

                    dut.final();
                    std::cout << "GAPS_CPP_OK\n";
                    return 0;
                }
                """);

            var (cfgCode, cfgOut, cfgErr) = await RunAsync("cmake", ["-S", sdkDir, "-B", buildDir, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", $"-DVELOSIM_HOST_SOURCE={hostPath}"], root);
            Assert.That(cfgCode, Is.Zero, cfgErr + "\n" + cfgOut);
            var (bldCode, bldOut, bldErr) = await RunAsync("cmake", ["--build", buildDir, "--parallel", "2"], root);
            Assert.That(bldCode, Is.Zero, bldErr + "\n" + bldOut);
            var (runCode, runOut, runErr) = await RunAsync(Path.Combine(buildDir, "velosim_host"), [], root);
            Assert.That(runCode, Is.Zero, runErr + "\n" + runOut);
            Assert.That(runOut, Does.Contain("GAPS_CPP_OK"));

            string vcd = await File.ReadAllTextAsync(vcdPath);
            Assert.That(vcd, Does.Contain("$scope module sysram $end"));
            Assert.That(vcd, Does.Contain("$dumpvars"));
            Assert.That(vcd, Does.Contain("#10"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(string exe, string[] args, string cwd)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await outTask, await errTask);
    }
}
