using System.Text.Json;
using NUnit.Framework;

namespace VeloSim.Tests;

public sealed class RtlTwoStateTests
{
    private const string EventCounterIr = """
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

    private const string CombAddIr = """
        {
          "format": "velosim.rtl.v1",
          "signals": [
            {"name": "a", "width": 8, "direction": "input"},
            {"name": "b", "width": 8, "direction": "input"},
            {"name": "y", "width": 8, "direction": "output"}
          ],
          "memories": [],
          "continuous": [
            {"target": {"kind": "ref", "name": "y"}, "value": {"kind": "add", "args": [{"kind": "ref", "name": "a"}, {"kind": "ref", "name": "b"}]}}
          ],
          "clock": null,
          "body": {"kind": "block", "items": []}
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
          "continuous": [
            {"target": {"kind": "ref", "name": "y"}, "value": {"kind": "ref", "name": "a"}}
          ],
          "clock": null,
          "body": {"kind": "block", "items": []}
        }
        """;

    [Test]
    public void DefaultModeUsesFourStateAndTwoStateIsOptIn()
    {
        var design = RtlDesign.Parse(EventCounterIr);
        string fourState = design.EmitModelHeader("counter");
        string twoState = design.EmitModelHeader("counter", RtlStateMode.TwoState);

        Assert.That(fourState, Does.Contain("vsrtl::w64::"));
        Assert.That(fourState, Does.Not.Contain("vsrtl::w2::"));
        Assert.That(twoState, Does.Contain("vsrtl::w2::"));
        Assert.That(twoState, Does.Not.Contain("vsrtl::w64::"));
    }

    [Test]
    public void CombinationalTwoStateEmitsW2Header()
    {
        var design = RtlDesign.Parse(CombAddIr);
        string twoState = design.EmitModelHeader("comb_add", RtlStateMode.TwoState);
        Assert.That(twoState, Does.Contain("vsrtl::w2::arith"));
        Assert.That(twoState, Does.Contain("inline void eval()"));
    }

    [Test]
    public void TwoStateRejectsWideDesignFailClosed()
    {
        var wide = RtlDesign.Parse(Wide128Ir);
        Assert.Throws<IrValidationException>(() => wide.EmitModelHeader("wide128", RtlStateMode.TwoState));
    }

    [Test]
    public async Task CliRtlExportSupportsTwoStateFlagAndRecordsManifest()
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-twostate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "counter.rtl.json");
            string sdkDir = Path.Combine(root, "sdk");
            await File.WriteAllTextAsync(irPath, EventCounterIr);

            var output = new StringWriter();
            var error = new StringWriter();
            int exit = await ToolCli.RunAsync(["rtl-export", irPath, "--top", "counter", "--output-dir", sdkDir, "--two-state"], output, error);
            Assert.That(exit, Is.Zero, error.ToString());

            string header = await File.ReadAllTextAsync(Path.Combine(sdkDir, "Vcounter.h"));
            Assert.That(header, Does.Contain("vsrtl::w2::"));

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sdkDir, "external-model.json")));
            Assert.That(manifest.RootElement.GetProperty("execution_scope").GetString(), Does.StartWith("two-state;"));
            foreach (var port in manifest.RootElement.GetProperty("ports").EnumerateArray())
                Assert.That(port.GetProperty("two_state").GetBoolean(), Is.True);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
