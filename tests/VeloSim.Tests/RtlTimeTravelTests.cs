using System.Diagnostics;
using NUnit.Framework;

namespace VeloSim.Tests;

public sealed class RtlTimeTravelTests
{
    private const string SvaCounterIr = """
        {
          "format": "velosim.rtl.v1",
          "signals": [
            {"name": "clk", "width": 1, "direction": "input"},
            {"name": "rst", "width": 1, "direction": "input"},
            {"name": "step", "width": 8, "direction": "input"},
            {"name": "q", "width": 8, "direction": "output"}
          ],
          "memories": [
            {"name": "hist", "width": 8, "depth": 4}
          ],
          "continuous": [],
          "properties": [
            {
              "name": "p_q_lt_50",
              "kind": "assert",
              "clock": "clk",
              "edge": "posedge",
              "disable_iff": {"kind": "ref", "name": "rst"},
              "antecedent": {"kind": "const", "bits": "1"},
              "consequent": {"kind": "lt", "args": [{"kind": "ref", "name": "q"}, {"kind": "const", "bits": "00110010"}]},
              "delay": 0
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
                "kind": "nba",
                "target": {"kind": "ref", "name": "q"},
                "value": {"kind": "const", "bits": "00000000"}
              },
              "else": {
                "kind": "block",
                "items": [
                  {"kind": "nba", "target": {"kind": "index", "name": "hist", "index": {"kind": "partselect", "value": {"kind": "ref", "name": "q"}, "left": 1, "right": 0}}, "value": {"kind": "ref", "name": "step"}},
                  {"kind": "nba", "target": {"kind": "ref", "name": "q"}, "value": {"kind": "add", "args": [{"kind": "ref", "name": "q"}, {"kind": "ref", "name": "step"}]}}
                ]
              }
            }
          }
        }
        """;

    [TestCase(false)]
    [TestCase(true)]
    public async Task CSharpInRamForkRewindAndSvaFailureVcdDumpWork(bool twoState)
    {
        string root = Path.Combine(Path.GetTempPath(), $"velosim-cs-tt-{(twoState ? "2s" : "4s")}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "dut.rtl.json");
            string sdkDir = Path.Combine(root, "sdk");
            string hostDir = Path.Combine(root, "host");
            await File.WriteAllTextAsync(irPath, SvaCounterIr);

            var args = new List<string> { "rtl-export", irPath, "--top", "tt_dut", "--output-dir", sdkDir, "--target", "csharp" };
            if (twoState) args.Add("--two-state");
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(args.ToArray(), new StringWriter(), err), Is.Zero, err.ToString());

            string modelCs = await File.ReadAllTextAsync(Path.Combine(sdkDir, "Vtt_dut.cs"));
            Assert.That(modelCs, Does.Not.Contain("unsafe "));

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
                    <ProjectReference Include="{Path.Combine(sdkDir, "Vtt_dut.csproj")}" />
                  </ItemGroup>
                </Project>
                """);

            string vcdPath = Path.Combine(root, "rewind_fail.vcd").Replace("\\", "\\\\");
            await File.WriteAllTextAsync(Path.Combine(hostDir, "Program.cs"), $$"""
                using System;
                using System.Diagnostics;
                using System.IO;
                using VeloSim.Generated;

                var dut = new Vtt_dut();
                var tt = new TimeTravelRecorder<Vtt_dut>(8);
                void Tick(ulong rst, ulong step) {
                    dut.rst = rst; dut.step = step;
                    dut.TimeInc(10);
                    dut.clk = 1; dut.Eval();
                    dut.clk = 0; dut.Eval();
                    tt.Record(dut);
                }
                dut.clk = 0; dut.rst = 1; dut.step = 0; dut.Eval();
                Tick(1, 0); // t=10, q=0
                Tick(0, 5); // t=20, q=5
                Tick(0, 5); // t=30, q=10
                if (dut.q != 10UL) throw new Exception("expected q=10");

                // In-RAM Fork() independence: fork two speculative timelines at q=10
                var sw = Stopwatch.StartNew();
                Vtt_dut? lastFork = null;
                for (int i = 0; i < 1000; ++i) lastFork = dut.Fork();
                sw.Stop();
                double usPerFork = sw.Elapsed.TotalMilliseconds * 1000.0 / 1000.0;

                var branchA = dut.Fork();
                var branchB = dut.Fork();
                branchA.rst = 0; branchA.step = 3; branchA.clk = 1; branchA.Eval(); branchA.clk = 0; branchA.Eval();
                branchB.rst = 0; branchB.step = 7; branchB.clk = 1; branchB.Eval(); branchB.clk = 0; branchB.Eval();
                if (branchA.q != 13UL || branchB.q != 17UL || dut.q != 10UL)
                    throw new Exception($"fork isolation broken: A={branchA.q} B={branchB.q} parent={dut.q}");

                // Run until SVA assertion fails (q >= 50)
                if (tt.DumpVcdOnFailure(dut, "{{vcdPath}}") != 0) throw new Exception("unexpected early dump");
                bool svaFailed = false;
                for (int i = 0; i < 10 && !svaFailed; ++i)
                {
                    try { Tick(0, 10); }
                    catch (InvalidOperationException) { svaFailed = true; }
                }
                if (!svaFailed || dut.PropertyFailures[0] == 0) throw new Exception("expected SVA failure");
                int frames = tt.DumpVcdOnFailure(dut, "{{vcdPath}}");
                if (frames < 4) throw new Exception($"expected >= 4 ring frames, got {frames}");
                string vcd = File.ReadAllText("{{vcdPath}}");
                if (!vcd.Contains("$scope module tt_dut") || !vcd.Contains("#30")) throw new Exception("VCD content invalid");

                // Time-travel rewind 3 cycles back and re-explore
                ulong beforeRewind = dut.q;
                if (!tt.Rewind(dut, 3)) throw new Exception("rewind failed");
                if (dut.q >= beforeRewind) throw new Exception($"rewind did not move q backward: {dut.q} vs {beforeRewind}");
                Console.WriteLine($"OK us_per_fork={usPerFork:F3}");
                """);

            var run = await RunCommandAsync("dotnet", ["run", "-c", "Release", "--disable-build-servers"], hostDir);
            Assert.That(run.ExitCode, Is.Zero, run.Output);
            Assert.That(run.Output, Does.Contain("OK"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RustInRamForkRewindAndSvaFailureVcdDumpWork(bool twoState)
    {
        if (!await ToolExistsAsync("cargo")) Assert.Ignore("cargo not installed");
        string root = Path.Combine(Path.GetTempPath(), $"velosim-rs-tt-{(twoState ? "2s" : "4s")}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "dut.rtl.json");
            string sdkDir = Path.Combine(root, "sdk");
            await File.WriteAllTextAsync(irPath, SvaCounterIr);

            var args = new List<string> { "rtl-export", irPath, "--top", "tt_dut", "--output-dir", sdkDir, "--target", "rust" };
            if (twoState) args.Add("--two-state");
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(args.ToArray(), new StringWriter(), err), Is.Zero, err.ToString());

            string vcdPath = Path.Combine(root, "rewind_fail.vcd").Replace("\\", "/");
            Directory.CreateDirectory(Path.Combine(sdkDir, "tests"));
            await File.WriteAllTextAsync(Path.Combine(sdkDir, "tests", "time_travel.rs"), $$"""
                use velosim_tt_dut::{TimeTravelRecorder, Vtt_dut};

                #[test]
                fn test_fork_rewind_and_vcd_on_failure() {
                    let mut dut = Vtt_dut::new();
                    let mut tt = TimeTravelRecorder::<Vtt_dut>::new(8);
                    let tick = |d: &mut Vtt_dut, rec: &mut TimeTravelRecorder<Vtt_dut>, rst: u64, step: u64| {
                        d.set_rst(rst); d.set_step(step);
                        d.time_inc(10);
                        d.set_clk(1); d.eval();
                        d.set_clk(0); d.eval();
                        rec.record(d);
                    };
                    dut.set_clk(0); dut.set_rst(1); dut.set_step(0); dut.eval();
                    tick(&mut dut, &mut tt, 1, 0);
                    tick(&mut dut, &mut tt, 0, 5);
                    tick(&mut dut, &mut tt, 0, 5);
                    assert_eq!(dut.q(), 10);

                    let mut branch_a = dut.fork();
                    let mut branch_b = dut.fork();
                    branch_a.set_rst(0); branch_a.set_step(3); branch_a.set_clk(1); branch_a.eval(); branch_a.set_clk(0); branch_a.eval();
                    branch_b.set_rst(0); branch_b.set_step(7); branch_b.set_clk(1); branch_b.eval(); branch_b.set_clk(0); branch_b.eval();
                    assert_eq!(branch_a.q(), 13);
                    assert_eq!(branch_b.q(), 17);
                    assert_eq!(dut.q(), 10);

                    assert_eq!(tt.dump_vcd_on_failure(&dut, "{{vcdPath}}", "1ns", false, &[]).unwrap(), 0);
                    let mut sva_failed = false;
                    for _ in 0..10 {
                        dut.set_rst(0); dut.set_step(10); dut.time_inc(10);
                        dut.set_clk(1);
                        if dut.try_eval().is_err() { sva_failed = true; break; }
                        dut.set_clk(0); dut.eval();
                        tt.record(&dut);
                    }
                    assert!(sva_failed && dut.property_failures()[0] > 0);
                    let frames = tt.dump_vcd_on_failure(&dut, "{{vcdPath}}", "1ns", false, &[]).unwrap();
                    assert!(frames >= 4);
                    let vcd = std::fs::read_to_string("{{vcdPath}}").unwrap();
                    assert!(vcd.contains("$scope module tt_dut"));
                    assert!(vcd.contains("#30"));

                    let before = dut.q();
                    assert!(tt.rewind(&mut dut, 3));
                    assert!(dut.q() < before);
                }
                """);

            var run = await RunCommandAsync("cargo", ["test", "--release"], sdkDir);
            Assert.That(run.ExitCode, Is.Zero, run.Output);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CppInRamForkRewindAndSvaFailureVcdDumpWork(bool twoState)
    {
        string cxx = await ToolExistsAsync("c++") ? "c++" : "g++";
        string root = Path.Combine(Path.GetTempPath(), $"velosim-cpp-tt-{(twoState ? "2s" : "4s")}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "dut.rtl.json");
            string sdkDir = Path.Combine(root, "sdk");
            await File.WriteAllTextAsync(irPath, SvaCounterIr);

            var args = new List<string> { "rtl-export", irPath, "--top", "tt_dut", "--output-dir", sdkDir, "--target", "cpp" };
            if (twoState) args.Add("--two-state");
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(args.ToArray(), new StringWriter(), err), Is.Zero, err.ToString());

            string vcdPath = Path.Combine(root, "rewind_fail.vcd").Replace("\\", "/");
            string hostCpp = Path.Combine(root, "host.cpp");
            string exePath = Path.Combine(root, "host");
            await File.WriteAllTextAsync(hostCpp, $$"""
                #include "Vtt_dut.h"
                #include <fstream>
                #include <iostream>
                #include <string>

                int main() {
                    Vtt_dut dut;
                    dut.enableTimeTravel(8);
                    auto tick = [&](uint64_t rst, uint64_t step) {
                        dut.rst = rst; dut.step = step;
                        dut.timeInc(10);
                        dut.clk = 1; dut.eval();
                        dut.clk = 0; dut.eval();
                        dut.recordTimeTravel();
                    };
                    dut.clk = 0; dut.rst = 1; dut.step = 0; dut.eval();
                    tick(1, 0);
                    tick(0, 5);
                    tick(0, 5);
                    if (static_cast<uint64_t>(dut.q) != 10ULL) return 1;

                    auto branchA = dut.fork();
                    auto branchB = dut.fork();
                    branchA->rst = 0; branchA->step = 3; branchA->clk = 1; branchA->eval(); branchA->clk = 0; branchA->eval();
                    branchB->rst = 0; branchB->step = 7; branchB->clk = 1; branchB->eval(); branchB->clk = 0; branchB->eval();
                    if (static_cast<uint64_t>(branchA->q) != 13ULL || static_cast<uint64_t>(branchB->q) != 17ULL || static_cast<uint64_t>(dut.q) != 10ULL) return 2;

                    if (dut.dumpTimeTravelOnFailure("{{vcdPath}}") != 0) return 3;
                    bool svaFailed = false;
                    for (int i = 0; i < 10 && !svaFailed; ++i) {
                        try { tick(0, 10); }
                        catch (const std::runtime_error&) { svaFailed = true; }
                    }
                    if (!svaFailed || dut.propertyStats().empty() || dut.propertyStats()[0].failures == 0) return 4;
                    if (dut.dumpTimeTravelOnFailure("{{vcdPath}}") < 4) return 5;
                    std::ifstream vf("{{vcdPath}}");
                    std::string vcd((std::istreambuf_iterator<char>(vf)), std::istreambuf_iterator<char>());
                    if (vcd.find("$scope module tt_dut") == std::string::npos || vcd.find("#30") == std::string::npos) return 6;

                    uint64_t before = static_cast<uint64_t>(dut.q);
                    if (!dut.rewindTimeTravel(3)) return 7;
                    if (static_cast<uint64_t>(dut.q) >= before) return 8;
                    std::cout << "OK\n";
                    return 0;
                }
                """);

            var compile = await RunCommandAsync(cxx, ["-std=c++20", "-O3", "-I" + sdkDir, hostCpp, Path.Combine(sdkDir, "vpi.cpp"), "-ldl", "-o", exePath], root);
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
        try { return (await RunCommandAsync(tool, ["--version"], Environment.CurrentDirectory)).ExitCode == 0; }
        catch { return false; }
    }
}
