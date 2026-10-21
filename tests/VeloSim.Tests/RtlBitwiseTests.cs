using NUnit.Framework;
namespace VeloSim.Tests;

public class RtlBitwiseTests
{
    [Test]
    public void BitwiseIrRejectsWrongOperandCounts()
    {
        foreach (string kind in new[] { "bitand", "bitor", "bitxor", "bitnot" })
        {
            string json = """
            {"format":"velosim.rtl.v1","signals":[{"name":"clk","width":1,"direction":"input"},{"name":"q","width":8,"direction":"output"}],"memories":[],"continuous":[],"clock":"clk","body":{"kind":"nba","target":{"kind":"ref","name":"q"},"value":{"kind":"KIND","args":[]}}}
            """.Replace("KIND", kind, StringComparison.Ordinal);
            Assert.Throws<IrValidationException>(() => RtlDesign.Parse(json), kind);
        }
    }
    [Test, Category("RealFifo")]
    public async Task ActualBitwiseRtlMatchesIcarusForFourStatesWidthsAndArithmeticContext()
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned UHDM RTL exporter.");
        DirectoryInfo? root = new(TestContext.CurrentContext.TestDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "native/rtl/runtime.h"))) root = root.Parent;
        Assert.That(root, Is.Not.Null);
        string checkout = root!.FullName, path = Path.Combine(checkout, "artifacts", "rtl-bitwise-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        string source = Path.Combine(checkout, "examples/surelog_bitwise.sv");
        var elaboration = await new SurelogFrontend().ElaborateAsync([source], Path.Combine(path, "surelog"), "surelog_bitwise", workers: 4);
        var runner = new ProcessRunner();
        async Task<ProcessResult> Run(string executable, string[] args)
        {
            var result = await runner.RunAsync(executable, args, path, TimeSpan.FromMinutes(2));
            Assert.That(result.ExitCode, Is.Zero, result.StandardOutput + result.StandardError); return result;
        }
        await Run(Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL") ?? Path.Combine(checkout, "artifacts/with-uhdm/bin/velosim-uhdm-rtl"), [elaboration.UhdmPath, Path.Combine(path, "design.json"), "surelog_bitwise"]);
        var design = RtlDesign.Parse(File.ReadAllText(Path.Combine(path, "design.json")));
        File.WriteAllText(Path.Combine(path, "design.cpp"), design.EmitCpp());
        File.Copy(Path.Combine(checkout, "native/rtl/runtime.h"), Path.Combine(path, "runtime.h"));
        File.WriteAllText(Path.Combine(path, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.20)\nproject(bitwise LANGUAGES CXX)\nset(CMAKE_CXX_STANDARD 20)\nadd_executable(bitwise design.cpp)\n");
        var inputs = design.Signals.Where(s => s.Direction == "input" && s.Name != design.Clock).ToArray();
        var outputs = design.Signals.Where(s => s.Direction == "output").ToArray();
        var vectors = new List<Dictionary<string, string>>();
        foreach (char a in "01xz") foreach (char b in "01xz")
            vectors.Add(new() { ["a"] = new(a, 8), ["b"] = new(b, 16), ["wide"] = new(a, 64) });
        // Fixed PRNG for stable evidence across runtimes; known and four-state phases.
        uint seed = 0x91ab32;
        for (int row = 0; row < 128; row++)
        {
            var vector = new Dictionary<string, string>();
            foreach (var input in inputs)
            {
                var bits = new char[input.Width];
                for (int i = 0; i < bits.Length; i++) { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; bits[i] = "01xz"[(int)(seed % (row < 64 ? 2u : 4u))]; }
                vector[input.Name] = new(bits);
            }
            vectors.Add(vector);
        }
        File.WriteAllLines(Path.Combine(path, "stimulus.txt"), vectors.Select(v => string.Join(' ', inputs.Select(s => v[s.Name]))));
        var tb = new System.Text.StringBuilder("module tb; logic clk=0; logic [7:0] a; logic [15:0] b; logic [63:0] wide; wire [15:0] q_and,q_or,q_xor,q_not; wire [16:0] q_nested; wire [63:0] q_wide; surelog_bitwise dut(.*); initial begin\n");
        for (int i = 0; i < vectors.Count; i++)
        {
            tb.Append("clk=0; "); foreach (var input in inputs) tb.Append($"{input.Name}={input.Width}'b{vectors[i][input.Name]}; ");
            tb.Append($"#1; clk=1; #1; $display(\"VS_RTL {i + 1}{string.Concat(outputs.Select(_ => " %b"))}\",{string.Join(',', outputs.Select(s => s.Name))});\n");
        }
        File.WriteAllText(Path.Combine(path, "tb.sv"), tb.Append("$finish; end endmodule\n").ToString());
        await Run("cmake", ["-S", path, "-B", Path.Combine(path, "build"), "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release"]);
        await Run("cmake", ["--build", Path.Combine(path, "build"), "--parallel", "4"]);
        await Run("iverilog", ["-g2012", "-s", "tb", "-o", Path.Combine(path, "reference.vvp"), source, Path.Combine(path, "tb.sv")]);
        var reference = await Run("vvp", [Path.Combine(path, "reference.vvp")]);
        string expected = string.Join('\n', reference.StandardOutput.Split('\n').Where(l => l.StartsWith("VS_RTL ", StringComparison.Ordinal))) + "\n";
        File.WriteAllText(Path.Combine(path, "expected.txt"), expected);
        foreach (string workers in new[] { "1", "4", "8" })
        {
            var result = await Run(Path.Combine(path, "build/bitwise"), [Path.Combine(path, "stimulus.txt"), workers]);
            File.WriteAllText(Path.Combine(path, "native-" + workers + ".txt"), result.StandardOutput);
            Assert.That(result.StandardOutput, Is.EqualTo(expected), path);
        }
    }
}
