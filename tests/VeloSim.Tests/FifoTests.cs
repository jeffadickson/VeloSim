using NUnit.Framework;
using System.Text.Json.Nodes;
namespace VeloSim.Tests;

public class FifoTests
{
    private const string Carry = """
    {"format":"velosim.rtl.v1","signals":[
      {"name":"clk","width":1,"direction":"input"},{"name":"a","width":8,"direction":"input"},{"name":"b","width":8,"direction":"input"},{"name":"c","width":16,"direction":"input"},
      {"name":"qe","width":1,"direction":"output"},{"name":"qc","width":1,"direction":"output"},{"name":"history","width":1,"direction":"output"}],"memories":[],"continuous":[],"clock":"clk",
     "body":{"kind":"block","items":[
       {"kind":"nba","target":{"kind":"ref","name":"history"},"value":{"kind":"ref","name":"qe"}},
       {"kind":"nba","target":{"kind":"ref","name":"qe"},"value":{"kind":"eq","args":[{"kind":"add","args":[{"kind":"ref","name":"a"},{"kind":"ref","name":"b"}]},{"kind":"ref","name":"c"}]}},
       {"kind":"case","value":{"kind":"add","args":[{"kind":"ref","name":"a"},{"kind":"ref","name":"b"}]},"items":[
         {"match":{"kind":"const","bits":"0000000100000000"},"body":{"kind":"nba","target":{"kind":"ref","name":"qc"},"value":{"kind":"const","bits":"1"}}},
         {"match":null,"body":{"kind":"nba","target":{"kind":"ref","name":"qc"},"value":{"kind":"const","bits":"0"}}}]}]}}
    """;
    private static string Root() { for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d is not null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "native/rtl/runtime.h"))) return d.FullName; throw new IOException("Checkout missing."); }
    [Test] public async Task NativeRtlHonorsEqualityCaseContextAndNbaHistory()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Native RTL compilation uses the Linux/macOS Ninja toolchain.");
        var path = Path.Combine(Path.GetTempPath(), "velosim-rtl-carry-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        try
        {
            var design = RtlDesign.Parse(Carry); File.WriteAllText(Path.Combine(path, "design.cpp"), design.EmitCpp()); File.Copy(Path.Combine(Root(), "native/rtl/runtime.h"), Path.Combine(path, "runtime.h"));
            File.WriteAllText(Path.Combine(path, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.20)\nproject(carry LANGUAGES CXX)\nset(CMAKE_CXX_STANDARD 20)\nadd_executable(carry design.cpp)\n");
            File.WriteAllText(Path.Combine(path, "input.txt"), "11111111 00000001 0000000100000000\n00000000 00000000 0000000000000001\n");
            var runner = new ProcessRunner(); var config = await runner.RunAsync("cmake", ["-S", path, "-B", Path.Combine(path, "build"), "-G", "Ninja"], path, TimeSpan.FromMinutes(1)); Assert.That(config.ExitCode, Is.Zero, config.StandardError);
            var build = await runner.RunAsync("cmake", ["--build", Path.Combine(path, "build"), "--parallel", "4"], path, TimeSpan.FromMinutes(1)); Assert.That(build.ExitCode, Is.Zero, build.StandardOutput + build.StandardError);
            var result = await runner.RunAsync(Path.Combine(path, "build", "carry"), [Path.Combine(path, "input.txt"), "4"], path, TimeSpan.FromSeconds(5)); Assert.That(result.ExitCode, Is.Zero, result.StandardError); Assert.That(result.StandardOutput.Replace("\r", ""), Is.EqualTo("VS_RTL 1 1 1 x\nVS_RTL 2 0 0 1\n"));
        }
        finally { Directory.Delete(path, true); }
    }
    [Test] public void RtlRejectsInputWritersFeedbackUnknownKindsAndDuplicateFields()
    {
        var node = JsonNode.Parse(Carry)!; node["body"]!["items"]![0]!["target"]!["name"] = "a"; Assert.Throws<IrValidationException>(() => RtlDesign.Parse(node.ToJsonString()));
        node = JsonNode.Parse(Carry)!; node["continuous"]!.AsArray().Add(JsonNode.Parse("""{"target":{"kind":"ref","name":"qc"},"value":{"kind":"ref","name":"qc"}}""")); Assert.Throws<IrValidationException>(() => RtlDesign.Parse(node.ToJsonString()));
        node = JsonNode.Parse(Carry)!; node["body"]!["kind"] = "wait"; Assert.Throws<IrValidationException>(() => RtlDesign.Parse(node.ToJsonString()));
        Assert.Throws<IrValidationException>(() => RtlDesign.Parse(Carry.Replace("\"memories\":[]", "\"memories\":[],\"memories\":[]")));
        node = JsonNode.Parse(Carry)!; node["signals"]!.AsArray().Add(JsonNode.Parse("""{"name":"floating","width":1,"direction":"internal"}""")); Assert.Throws<IrValidationException>(() => RtlDesign.Parse(node.ToJsonString()));
    }
    [Test] public void RtlRejectsClockDependentContinuousLogic()
    {
        var node = JsonNode.Parse(Carry)!; node["signals"]!.AsArray().Add(JsonNode.Parse("""{"name":"gate","width":1,"direction":"internal"}""")); node["continuous"]!.AsArray().Add(JsonNode.Parse("""{"target":{"kind":"ref","name":"gate"},"value":{"kind":"ref","name":"clk"}}""")); Assert.Throws<IrValidationException>(() => RtlDesign.Parse(node.ToJsonString()));
    }
    [Test] public void QueueOracleUsesPreEdgeFullAndEmptyForSimultaneousRequests()
    {
        FifoDrive D(bool push, bool pop, ulong data) => new(true, push, pop, new(8, data), "unit");
        FifoDrive[] drives = [new(false, false, false, new(8), "reset"), D(true, true, 11), D(true, false, 22), D(true, true, 33), D(true, true, 44), D(false, true, 0), D(false, true, 0)];
        IReadOnlyDictionary<string, string> T(string full, string empty, string data) => new Dictionary<string, string> { ["full"] = full, ["empty"] = empty, ["dout"] = data };
        IReadOnlyDictionary<string, string>[] traces = [T("0", "1", "xxxxxxxx"), T("0", "0", "00001011"), T("1", "0", "00001011"), T("0", "0", "00010110"), T("0", "0", "00101100"), T("0", "1", "00000000"), T("0", "1", "xxxxxxxx")];
        Assert.That(FifoDifferential.CheckQueue(drives, traces, 2, "unit"), Is.Empty); traces[3] = T("1", "0", "00010110"); Assert.That(FifoDifferential.CheckQueue(drives, traces, 2, "unit").Any(d => d.Cycle == 4 && d.Signal == "full"), Is.True);
    }
    [Test] public void FifoStimulusReproduciblyExercisesBoundariesWrapResetAndUnknownData()
    {
        var first = FifoStimulus.Generate(64, 8, 1234, 1000); Assert.That(first, Is.EqualTo(FifoStimulus.Generate(64, 8, 1234, 1000))); Assert.That(first, Is.Not.EqualTo(FifoStimulus.Generate(64, 8, 1235, 1000)));
        Assert.That(first.Select(s => s.Phase), Does.Contain("simultaneous-full").And.Contain("simultaneous-empty").And.Contain("simultaneous-middle").And.Contain("reset-while-nonempty").And.Contain("wrap-push")); Assert.That(first.Any(d => d.Data.XMask != 0), Is.True); Assert.That(first.Any(d => d.Data.ZMask != 0), Is.True);
        Assert.Throws<ArgumentException>(() => FifoStimulus.Generate(8, 3, 1, 0));
        var coverage = FifoStimulus.Coverage(first, 8); foreach (var key in new[] { "push_on_full", "pop_on_empty", "simultaneous_full", "simultaneous_empty", "simultaneous_middle", "write_wrap", "read_wrap", "reset_nonempty", "unknown_data_write", "high_impedance_data_write" }) Assert.That(coverage[key], Is.GreaterThan(0), key);
    }
    [Test] public async Task FifoCliRejectsInvalidDimensionsBeforeSpawning()
    {
        var error = new StringWriter(); Assert.That(await ToolCli.RunAsync(["fifo-differential", "source.sv", "--cycles", "9999999999999999999", "--output-dir", "unused"], new StringWriter(), error), Is.EqualTo(1)); Assert.That(error.ToString(), Is.Not.Empty);
    }
    [Test, Category("RealFifo")]
    public async Task ActualFifoRtlWidthDepthMatrixAndIndependentOracleMutation()
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Set VELOSIM_REAL_FIFO=1 after building the pinned UHDM RTL exporter.");
        var root = Root(); string exporter = Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL") ?? Path.Combine(root, "artifacts/with-uhdm/bin/velosim-uhdm-rtl");
        string destination = Path.GetFullPath(Environment.GetEnvironmentVariable("VELOSIM_FIFO_EVIDENCE") ?? Path.Combine(root, "artifacts", "fifo-matrix-" + Guid.NewGuid().ToString("N")), root); Directory.CreateDirectory(destination);
        string source = File.ReadAllText(Path.Combine(root, "examples/surelog_fifo.sv"));
        foreach (var (width, depth) in new[] { (1, 2), (8, 4), (16, 8), (64, 8) })
        {
            string path = Path.Combine(destination, $"fifo-{width}-{depth}.sv"); File.WriteAllText(path, source.Replace("WIDTH=8, DEPTH=4", $"WIDTH={width}, DEPTH={depth}", StringComparison.Ordinal));
            var result = await new FifoDifferential().RunAsync(path, "surelog_fifo", Path.Combine(destination, $"run-{width}-{depth}"), [1, 4, 8], randomCycles: 1000, exporter: exporter);
            Assert.That(result.Status, Is.EqualTo("pass"), result.ReportPath); Assert.That((result.Width, result.Depth), Is.EqualTo((width, depth)));
        }
        string mutant = Path.Combine(destination, "wrong-read-pointer.sv"); File.WriteAllText(mutant, source.Replace("assign dout = mem[rd_ptr];", "assign dout = mem[wr_ptr];", StringComparison.Ordinal));
        var bad = await new FifoDifferential().RunAsync(mutant, "surelog_fifo", Path.Combine(destination, "mutant"), [1, 4], randomCycles: 20, exporter: exporter); Assert.That(bad.Status, Is.EqualTo("fail"), "The independent queue must detect a wrong design even when both simulators agree.");
        var report = JsonNode.Parse(File.ReadAllText(bad.ReportPath))!; Assert.That(report["errors"]!.AsObject(), Is.Empty); var engines = report["differences"]!.AsArray().Select(d => d!["engine"]!.GetValue<string>()).ToArray(); Assert.That(engines, Does.Contain("icarus").And.Contain("velosim-1")); Assert.That(engines.Any(e => e.EndsWith(":icarus", StringComparison.Ordinal)), Is.False, "Both compiled simulators should agree on the deliberately wrong RTL.");
    }
    [Test, Category("RealFifo")]
    public async Task ActualExporterRejectsUnsupportedInitializationSignednessFillDelaysAndPrimitives()
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned UHDM RTL exporter.");
        var root = Root(); var directory = Path.Combine(root, "artifacts", "fifo-rejections-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); string exporter = Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL") ?? Path.Combine(root, "artifacts/with-uhdm/bin/velosim-uhdm-rtl"); var source = File.ReadAllText(Path.Combine(root, "examples/surelog_fifo.sv"));
        var variants = new Dictionary<string, string>
        {
            ["blocking"] = source.Replace("rd_ptr <= '0;", "rd_ptr = '0;"),
            ["initializer"] = source.Replace("] count;", "] count = 3'd5;"),
            ["signed-memory"] = source.Replace("logic [WIDTH-1:0] mem", "logic signed [WIDTH-1:0] mem"),
            ["signed-expression"] = source.Replace("count <= '0;", "count <= (-8'sd1 < 16'sd0);"),
            ["negative-parameter"] = source.Replace("localparam int PTR_W", "localparam int NEG=-1; localparam int PTR_W").Replace("count <= '0;", "count <= (NEG < 0);"),
            ["two-state-memory"] = source.Replace("logic [WIDTH-1:0] mem", "bit [WIDTH-1:0] mem"),
            ["two-state-input"] = source.Replace("input logic [WIDTH-1:0] din", "input bit [WIDTH-1:0] din"),
            ["pull-net"] = source.Replace("endmodule", "tri0 pulled;\nendmodule"),
            ["fill-one"] = source.Replace("count <= '0;", "count <= '1;"),
            ["delay"] = source.Replace("assign full", "assign #1 full"),
            ["primitive"] = source.Replace("endmodule", "wire ignored; and gate_instance(ignored,push,pop);\nendmodule"),
            ["generate"] = source.Replace("endmodule", "generate if(DEPTH>1) begin:g wire ignored; assign ignored=push; end endgenerate\nendmodule")
        };
        foreach (var (name, text) in variants)
        {
            var path = Path.Combine(directory, name + ".sv"); File.WriteAllText(path, text); var elaboration = await new SurelogFrontend().ElaborateAsync([path], Path.Combine(directory, name), "surelog_fifo", workers: 4);
            var result = await new ProcessRunner().RunAsync(exporter, [elaboration.UhdmPath, Path.Combine(directory, name + ".json"), "surelog_fifo"], root, TimeSpan.FromSeconds(30)); Assert.That(result.ExitCode, Is.Not.Zero, name + " was silently accepted"); Assert.That(result.StandardError, Does.Contain("rejected"), name);
        }
    }
}
