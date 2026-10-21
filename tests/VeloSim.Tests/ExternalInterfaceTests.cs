using NUnit.Framework;
using System.Text;
using System.Text.Json;
namespace VeloSim.Tests;

public class ExternalInterfaceTests
{
    private static string Root() { for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d is not null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "native/rtl/external_model.h"))) return d.FullName; throw new IOException("Checkout missing."); }
    [Test]
    public void GeneratedModelProtectsCppIdentifiersAndExportsAllPorts()
    {
        var names = new[] { "clk", "eval", "std", "vsrtl", "parse", "_private", "a__b", "port_eval" };
        string json = JsonSerializer.Serialize(new { format = "velosim.rtl.v1", signals = names.Select(n => new { name = n, width = 1, direction = "input" }), memories = Array.Empty<object>(), continuous = Array.Empty<object>(), clock = "clk", body = new { kind = "block", items = Array.Empty<object>() } });
        var design = RtlDesign.Parse(json); var map = design.ExternalPortNames();
        Assert.That(map.Values.Distinct().Count(), Is.EqualTo(names.Length));
        foreach (string name in names.Skip(1).SkipLast(1)) Assert.That(map[name], Is.Not.EqualTo(name));
        Assert.That(map.Values.All(n => !n.Contains("__", StringComparison.Ordinal)), Is.True);
        string header = design.EmitModelHeader("reserved"); Assert.That(header, Does.Contain("class Vreserved final").And.Contain("void evaluate() override"));
        Assert.Throws<ArgumentException>(() => design.EmitModelHeader("../bad"));
        Assert.Throws<ArgumentException>(() => design.EmitModelHeader("a__b"));
    }
    [Test]
    public async Task RtlExportCliRequiresExactOptionsAndRefusesToOverwrite()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.That(await ToolCli.RunAsync(["rtl-export", "missing"], output, error), Is.Not.Zero);
        string directory = Path.Combine(Path.GetTempPath(), "velosim-external-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "design.json");
        File.WriteAllText(source, """{"format":"velosim.rtl.v1","signals":[{"name":"clk","width":1,"direction":"input"}],"memories":[],"continuous":[],"clock":"clk","body":{"kind":"block","items":[]}}""");
        Assert.ThrowsAsync<ArgumentException>(async () => await RtlExternalSdk.ExportAsync(source, "top", directory));
    }
    [Test, Category("RealFifo")]
    public async Task SameSourceExternalModelAndIndependentHeaderPluginMatchIcarus()
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned frontend and Icarus.");
        string root = Root(), path = Path.Combine(root, "artifacts", "external-interface-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        string source = Path.Combine(root, "examples/external_counter.sv"), sdk = Path.Combine(path, "sdk");
        var sourceBefore = ArtifactMetadata.SourceClosure([source]); var runner = new RecordingProcessRunner(new ProcessRunner());
        var tools = new Dictionary<string, FileFingerprint>(StringComparer.Ordinal);
        string Tool(string executable) { string resolved = ArtifactMetadata.ResolveExecutable(executable, path) ?? throw new IOException("Missing tool: " + executable); tools.TryAdd(resolved, ArtifactMetadata.Fingerprint(resolved)); return resolved; }
        async Task<ProcessResult> Run(string executable, params string[] args) { var result = await runner.RunAsync(Tool(executable), args, path, TimeSpan.FromMinutes(3)); Assert.That(result.ExitCode, Is.Zero, result.StandardOutput + result.StandardError); return result; }
        string surelog = Tool(Environment.GetEnvironmentVariable("VELOSIM_SURELOG") ?? "surelog");
        var elaboration = await new SurelogFrontend(runner).ElaborateAsync([source], Path.Combine(path, "surelog"), "external_counter", executable: surelog, workers: 4);
        await Run(Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL") ?? Path.Combine(root, "artifacts/with-uhdm/bin/velosim-uhdm-rtl"), elaboration.UhdmPath, Path.Combine(path, "design.json"), "external_counter");
        var design = RtlDesign.Parse(File.ReadAllText(Path.Combine(path, "design.json")));
        var expected = new[] { new RtlSignal("clk", 1, "input"), new("data", 64, "input"), new("enable", 1, "input"), new("rst_n", 1, "input"), new("q", 8, "output"), new("sampled", 64, "output"), new("mirrored", 64, "output") };
        Assert.That(design.Signals.OrderBy(s => s.Name), Is.EqualTo(expected.OrderBy(s => s.Name)));
        await RtlExternalSdk.ExportAsync(Path.Combine(path, "design.json"), "external_counter", sdk);
        string reservedJson = JsonSerializer.Serialize(new { format = "velosim.rtl.v1", signals = new[] { "clk", "eval", "std", "vsrtl", "parse", "_private", "a__b", "port_eval" }.Select(n => new { name = n, width = 1, direction = "input" }), memories = Array.Empty<object>(), continuous = Array.Empty<object>(), clock = "clk", body = new { kind = "block", items = Array.Empty<object>() } });
        var reserved = RtlDesign.Parse(reservedJson); File.WriteAllText(Path.Combine(sdk, "Vreserved.h"), reserved.EmitModelHeader("reserved"));
        File.WriteAllText(Path.Combine(sdk, "host.cpp"), "#include \"Vreserved.h\"\nstatic void check_reserved(){Vreserved m;m.clk=0;m." + reserved.ExternalPortNames()["eval"] + "=1;m.eval();}\n" + File.ReadAllText(Path.Combine(root, "examples/external_main.cpp")).Replace("Vexternal_counter top;", "check_reserved();Vexternal_counter top;", StringComparison.Ordinal));
        File.Copy(Path.Combine(root, "examples/external_plugin.c"), Path.Combine(path, "plugin.c"));
        string independentHeader = "/usr/include/iverilog/vpi_user.h";
        Assert.That(File.Exists(independentHeader), Is.True, "Use Icarus's standard header to independently verify VPI ABI.");
        var compileInputs = Directory.GetFiles(sdk).Concat(new[] { Path.Combine(path, "plugin.c"), independentHeader }).Select(ArtifactMetadata.Fingerprint).ToArray();
        await Run("cmake", "-S", sdk, "-B", Path.Combine(path, "build"), "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DVELOSIM_HOST_SOURCE=" + Path.Combine(sdk, "host.cpp"));
        string Cache(string name) => File.ReadLines(Path.Combine(path, "build/CMakeCache.txt")).Single(l => l.StartsWith(name + ":FILEPATH=", StringComparison.Ordinal)).Split('=', 2)[1];
        var compilerVersion = await Run(Cache("CMAKE_CXX_COMPILER"), "--version"); var ninjaVersion = await Run(Cache("CMAKE_MAKE_PROGRAM"), "--version");
        await Run("cmake", "--build", Path.Combine(path, "build"), "--parallel", "4");
        await Run("cc", "-std=c11", "-shared", "-fPIC", "-I/usr/include/iverilog", Path.Combine(path, "plugin.c"), "-o", Path.Combine(path, "plugin.so"));
        var inputs = design.Signals.Where(s => s.Direction == "input" && s.Name != "clk").ToArray(); var outputs = design.Signals.Where(s => s.Direction == "output").ToArray();
        var vectors = new List<Dictionary<string, string>>();
        uint seed = 0xc81ab;
        for (int row = 0; row < 140; row++) { var data = new char[64]; for (int bit = 0; bit < 64; bit++) { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; data[bit] = "01xz"[(int)(seed % (row < 70 ? 2u : 4u))]; } vectors.Add(new() { ["data"] = new(data), ["enable"] = row % 3 == 0 ? "0" : "1", ["rst_n"] = row % 31 == 0 ? "0" : "1" }); }
        File.WriteAllLines(Path.Combine(path, "stimulus.txt"), vectors.Select(v => string.Join(' ', inputs.Select(s => v[s.Name]))));
        var tb = new StringBuilder("module tb; logic clk=0; logic rst_n,enable; logic[63:0]data; wire[7:0]q; wire[63:0]sampled,mirrored; external_counter dut(.*); initial begin\n");
        for (int row = 0; row < vectors.Count; row++) { tb.Append("clk=0;"); foreach (var input in inputs) tb.Append($"{input.Name}={input.Width}'b{vectors[row][input.Name]};"); tb.AppendLine($"#1;clk=1;#1;$display(\"VS_RTL {row + 1}{string.Concat(outputs.Select(_ => " %b"))}\",{string.Join(',', outputs.Select(s => s.Name))});"); }
        File.WriteAllText(Path.Combine(path, "tb.sv"), tb.AppendLine("$finish;end endmodule").ToString());
        var buildInputs = Directory.GetFiles(sdk).Concat(new[] { Path.Combine(path, "plugin.c"), independentHeader, Path.Combine(path, "tb.sv"), Path.Combine(path, "stimulus.txt"), elaboration.UhdmPath }).Select(ArtifactMetadata.Fingerprint).ToArray();
        await Run("iverilog", "-g2012", "-s", "tb", "-o", Path.Combine(path, "reference.vvp"), source, Path.Combine(path, "tb.sv"));
        var reference = await Run("vvp", Path.Combine(path, "reference.vvp")); var native = await Run(Path.Combine(path, "build/velosim_host"), Path.Combine(path, "plugin.so"), Path.Combine(path, "stimulus.txt"));
        Assert.That(native.StandardOutput, Does.Contain("VPI_PLUGIN_PASS"));
        _ = FifoDifferential.ParseTrace(reference.StandardOutput, design, vectors.Count); _ = FifoDifferential.ParseTrace(native.StandardOutput, design, vectors.Count);
        string Trace(string text) => string.Join('\n', text.Split('\n').Where(l => l.StartsWith("VS_RTL ", StringComparison.Ordinal))) + "\n";
        File.WriteAllText(Path.Combine(path, "reference.txt"), reference.StandardOutput); File.WriteAllText(Path.Combine(path, "native.txt"), native.StandardOutput);
        Assert.That(Trace(native.StandardOutput), Is.EqualTo(Trace(reference.StandardOutput)));
        Assert.That(runner.Commands.Single(c => c.Executable == surelog).Arguments, Does.Contain(source));
        Assert.That(runner.Commands.Single(c => Path.GetFileName(c.Executable) == "iverilog").Arguments, Does.Contain(source));
        Assert.That(ArtifactMetadata.SourceClosure([source]), Is.EqualTo(sourceBefore));
        foreach (var fingerprint in tools.Values.Concat(buildInputs).Concat(compileInputs)) Assert.That(ArtifactMetadata.Fingerprint(fingerprint.Path), Is.EqualTo(fingerprint));
        var artifacts = new[] { "plugin.so", "build/velosim_host", "build/libvelosim_vpi.so", "reference.txt", "native.txt", "reference.vvp" }.Select(p => ArtifactMetadata.Fingerprint(Path.Combine(path, p))).ToArray();
        File.WriteAllText(Path.Combine(path, "report.json"), JsonSerializer.Serialize(new { schema = "velosim.external.conformance.v1", status = "pass", cycles = vectors.Count, sourceBefore, compileInputs, buildInputs, artifacts, compiler_version = compilerVersion.StandardOutput, ninja_version = ninjaVersion.StandardOutput, tools = tools.Values, commands = runner.Commands, plugin_header = independentHeader, execution_scope = "known-clock single posedge process, explicit eval, serial" }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
