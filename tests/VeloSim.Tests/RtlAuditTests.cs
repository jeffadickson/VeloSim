using NUnit.Framework;

namespace VeloSim.Tests;

/// <summary>Executable constructs retained by Surelog must not disappear in lowering.</summary>
[NonParallelizable]
public class RtlAuditTests
{
    [TestCase("assert property (@(posedge clk) a);", "concurrent assertion")]
    [TestCase("assume property (@(posedge clk) (a==1'b1));", "concurrent assumption")]
    [TestCase("cover property (@(posedge clk) (a==1'b1));", "concurrent cover")]
    [TestCase("initial assert(a);", "immediate assertion")]
    [TestCase("final q=1;", "final process")]
    [TestCase("clocking cb @(posedge clk);input a;endclocking", "clocking block")]
    public async Task UnsupportedExecutableItemsHaveSourceLocatedRejection(string item, string marker)
    {
        var result = await Export("module dut(input logic clk,a,output logic q);assign q=a;" + item + " endmodule");
        Assert.That(result.ExitCode, Is.EqualTo(1), result.StandardOutput + result.StandardError);
        Assert.That(result.StandardError, Does.Contain(marker).And.Contain("design.sv:1:"));
    }

    [Test]
    public async Task InactiveGenerateAssertionsDoNotRejectTheExecutableDesign()
    {
        var result = await Export("module dut(input logic clk,a,output logic q);assign q=a;generate if(0)begin:g assert property (@(posedge clk) a);end endgenerate endmodule");
        Assert.That(result.ExitCode, Is.Zero, result.StandardOutput + result.StandardError);
    }

    private static async Task<ProcessResult> Export(string source)
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned Surelog/UHDM.");
        string root = Environment.GetEnvironmentVariable("VELOSIM_RTL_EVIDENCE_ROOT") ?? Path.GetTempPath();
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        string path = Path.Combine(root, "audit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        string file = Path.Combine(path, "design.sv"); await File.WriteAllTextAsync(file, source);
        var runner = new RecordingProcessRunner(new ProcessRunner());
        var elaboration = await new SurelogFrontend(runner).ElaborateAsync([file], Path.Combine(path, "surelog"), "dut", workers: 2);
        string exporter = Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL") ?? throw new IOException("VELOSIM_UHDM_RTL required.");
        var result = await runner.RunAsync(exporter, [elaboration.UhdmPath, Path.Combine(path, "design.json"), "dut"], path, TimeSpan.FromMinutes(2));
        // Keep exact original HDL and commands/diagnostics for replay. This test
        // qualifies rejection/discovery, not runtime assertion implementation.
        await File.WriteAllTextAsync(Path.Combine(path, "lowering.stdout.txt"), result.StandardOutput);
        await File.WriteAllTextAsync(Path.Combine(path, "lowering.stderr.txt"), result.StandardError);
        return result;
    }
}
