using System.Diagnostics;
using System.Text.Json;
using NUnit.Framework;

namespace VeloSim.Tests;

public sealed class RtlMultiTargetTests
{
    private const string CounterIr = """
        {
          "format": "velosim.rtl.v1",
          "signals": [
            {"name": "clk", "width": 1, "direction": "input"},
            {"name": "rst", "width": 1, "direction": "input"},
            {"name": "en", "width": 1, "direction": "input"},
            {"name": "q", "width": 8, "direction": "output"}
          ],
          "memories": [],
          "continuous": [],
          "clock": "clk",
          "body": {
            "kind": "process",
            "events": [{"signal": "clk", "edge": "posedge"}],
            "body": {
              "kind": "if",
              "condition": {"kind": "ref", "name": "rst"},
              "then": {"kind": "nba", "target": {"kind": "ref", "name": "q"}, "value": {"kind": "const", "bits": "00000000"}},
              "else": {
                "kind": "if",
                "condition": {"kind": "ref", "name": "en"},
                "then": {"kind": "nba", "target": {"kind": "ref", "name": "q"}, "value": {"kind": "add", "args": [{"kind": "ref", "name": "q"}, {"kind": "const", "bits": "00000001"}]}},
                "else": {"kind": "block", "items": []}
              }
            }
          }
        }
        """;

    private const string Wide128Ir = """
        {
          "format": "velosim.rtl.v1",
          "signals": [
            {"name": "a", "width": 128, "direction": "input"},
            {"name": "y", "width": 128, "direction": "output"}
          ],
          "memories": [],
          "continuous": [{"target": {"kind": "ref", "name": "y"}, "value": {"kind": "ref", "name": "a"}}],
          "clock": null,
          "body": {"kind": "block", "items": []}
        }
        """;

    [Test]
    public void CSharpAndRustTargetsRejectWideDesignFailClosed()
    {
        var wide = RtlDesign.Parse(Wide128Ir);
        Assert.Throws<IrValidationException>(() => wide.EmitCSharpModel("wide128", RtlStateMode.FourState));
        Assert.Throws<IrValidationException>(() => wide.EmitCSharpModel("wide128", RtlStateMode.TwoState));
        Assert.Throws<IrValidationException>(() => wide.EmitRustModel("wide128", RtlStateMode.FourState));
        Assert.Throws<IrValidationException>(() => wide.EmitRustModel("wide128", RtlStateMode.TwoState));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CSharpTargetExportsCompilesAndSimulatesCounter(bool twoState)
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-cs-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "counter.rtl.json");
            string sdkDir = Path.Combine(root, "sdk");
            string hostDir = Path.Combine(root, "host");
            await File.WriteAllTextAsync(irPath, CounterIr);

            string[] cliArgs = twoState
                ? ["rtl-export", irPath, "--top", "counter", "--output-dir", sdkDir, "--target", "csharp", "--two-state"]
                : ["rtl-export", irPath, "--top", "counter", "--output-dir", sdkDir, "--target", "csharp"];
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(cliArgs, new StringWriter(), err), Is.Zero, err.ToString());

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sdkDir, "external-model.json")));
            Assert.That(manifest.RootElement.GetProperty("execution_scope").GetString(), Does.StartWith(twoState ? "csharp; two-state;" : "csharp; four-state;"));

            Directory.CreateDirectory(hostDir);
            await File.WriteAllTextAsync(Path.Combine(hostDir, "Host.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>
                  <ItemGroup><ProjectReference Include="{Path.Combine(sdkDir, "Vcounter.csproj")}" /></ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(hostDir, "Program.cs"), """
                using System;
                using VeloSim.Generated;
                var dut = new Vcounter();
                dut.rst = 1; dut.en = 0; dut.clk = 0; dut.Eval(); dut.clk = 1; dut.Eval(); dut.clk = 0; dut.Eval();
                if (dut.q != 0) throw new Exception("reset failed: " + dut.q);
                dut.rst = 0; dut.en = 1;
                for (ulong i = 1; i <= 10; ++i) {
                    dut.clk = 1; dut.Eval(); dut.clk = 0; dut.Eval();
                    if (dut.q != i) throw new Exception($"cycle {i} got {dut.q}");
                }
                dut.Final();
                Console.WriteLine("CS_OK=" + dut.q);
                """);

            var (code, stdout, stderr) = await RunAsync("dotnet", ["run", "--project", hostDir, "-c", "Release"], root);
            Assert.That(code, Is.Zero, stderr + "\n" + stdout);
            Assert.That(stdout.Trim(), Does.Contain("CS_OK=10"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RustTargetExportsCompilesAndSimulatesCounter(bool twoState)
    {
        if (!ToolExists("cargo")) Assert.Ignore("cargo not installed on PATH.");
        string root = Path.Combine(Path.GetTempPath(), "velosim-rs-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "counter.rtl.json");
            string sdkDir = Path.Combine(root, "sdk");
            await File.WriteAllTextAsync(irPath, CounterIr);

            string[] cliArgs = twoState
                ? ["rtl-export", irPath, "--top", "counter", "--output-dir", sdkDir, "--target", "rust", "--two-state"]
                : ["rtl-export", irPath, "--top", "counter", "--output-dir", sdkDir, "--target", "rust"];
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(cliArgs, new StringWriter(), err), Is.Zero, err.ToString());

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sdkDir, "external-model.json")));
            Assert.That(manifest.RootElement.GetProperty("execution_scope").GetString(), Does.StartWith(twoState ? "rust; two-state;" : "rust; four-state;"));

            Directory.CreateDirectory(Path.Combine(sdkDir, "tests"));
            await File.WriteAllTextAsync(Path.Combine(sdkDir, "tests", "counter_sim.rs"), """
                use velosim_counter::Vcounter;

                #[test]
                fn counter_runs() {
                    let mut dut = Vcounter::new();
                    dut.set_rst(1); dut.set_en(0); dut.set_clk(0); dut.eval();
                    dut.set_clk(1); dut.eval();
                    dut.set_clk(0); dut.eval();
                    assert_eq!(dut.q(), 0);
                    dut.set_rst(0); dut.set_en(1);
                    for i in 1..=10u64 {
                        dut.set_clk(1); dut.eval();
                        dut.set_clk(0); dut.eval();
                        assert_eq!(dut.q(), i);
                    }
                    dut.final_model();
                }
                """);

            var (code, stdout, stderr) = await RunAsync("cargo", ["test", "--release"], sdkDir);
            Assert.That(code, Is.Zero, stderr + "\n" + stdout);
            Assert.That(stdout, Does.Contain("counter_runs ... ok"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static bool ToolExists(string name)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(name, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p?.WaitForExit(3000);
            return p is { ExitCode: 0 };
        }
        catch { return false; }
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
