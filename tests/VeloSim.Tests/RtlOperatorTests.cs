using NUnit.Framework;
using System.Text;
using System.Text.Json;
namespace VeloSim.Tests;

public class RtlOperatorTests
{
    public sealed record Op(string Kind, string Expression, bool Scalar = false);
    public static readonly Op[] Operators =
    [
        new("or", "a || b", true), new("neq", "a != b", true),
        new("caseeq", "a === b", true), new("caseneq", "a !== b", true),
        new("lt", "a < b", true), new("le", "a <= b", true),
        new("gt", "a > b", true), new("ge", "a >= b", true),
        new("bitxnor", "a ~^ b"), new("neg", "-a"),
        new("shl", "a << b"), new("shr", "a >> b"), new("ashr", "a >>> b"),
        new("redand", "&a", true), new("rednand", "~&a", true),
        new("redor", "|a", true), new("rednor", "~|a", true),
        new("redxor", "^a", true), new("redxnor", "~^a", true), new("pos", "+a"),
        new("mul", "a * b"), new("div", "a / b"), new("mod", "a % b"), new("cond", "sel ? a : b")
    ];
    [Test]
    public void AllExpandedOperatorsRejectMalformedArity()
    {
        foreach (var op in Operators) foreach (int count in new[] { 0, 4 })
        {
            var value = new { kind = op.Kind, args = Enumerable.Repeat(new { kind = "const", bits = "1" }, count) };
            string json = JsonSerializer.Serialize(new { format = "velosim.rtl.v1", signals = new[] { new RtlSignal("clk", 1, "input"), new RtlSignal("q", 8, "output") }.Select(s => new { name = s.Name, width = s.Width, direction = s.Direction }), memories = Array.Empty<object>(), continuous = Array.Empty<object>(), clock = "clk", body = new { kind = "nba", target = new { kind = "ref", name = "q" }, value } });
            Assert.Throws<IrValidationException>(() => RtlDesign.Parse(json), op.Kind + ":" + count);
        }
    }
    private static string Root()
    {
        for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "native/rtl/runtime.h"))) return d.FullName;
        throw new IOException("Checkout missing.");
    }
    [Test, Category("RealFifo"), TestCase(8, 16)]
    public async Task SameSourceOperatorMatrixMatchesIcarus(int aWidth, int bWidth)
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned UHDM RTL exporter.");
        string root = Root(), path = Path.Combine(root, "artifacts", $"rtl-operators-{aWidth}-{bWidth}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); int resultWidth = Math.Min(64, Math.Max(aWidth, bWidth) + 1);
        var ports = new List<string> { "input logic clk", $"input logic [{aWidth - 1}:0] a", $"input logic [{bWidth - 1}:0] b", "input logic [1:0] sel" };
        ports.AddRange(Operators.Select(o => $"output logic [{(o.Scalar ? 1 : resultWidth) - 1}:0] q_{o.Kind}"));
        ports.Add($"output logic [{resultWidth - 1}:0] q_nested");
        if (aWidth < 64) ports.Add($"output logic [{aWidth}:0] q_shiftconcat");
        string source = Path.Combine(path, "operators.sv");
        File.WriteAllText(source, "module operators(" + string.Join(',', ports) + ");\nalways_ff @(posedge clk) begin\n" + string.Join('\n', Operators.Select(o => $"q_{o.Kind} <= {o.Expression};")) + "\nq_nested <= ~(a + 1'b1) ^ b;\n" + (aWidth < 64 ? "q_shiftconcat <= {(a << b),1'b0};\n" : "") + "end\nendmodule\n");
        var sources = ArtifactMetadata.SourceClosure([source]);
        var runner = new RecordingProcessRunner(new ProcessRunner());
        async Task<ProcessResult> Run(string executable, params string[] args)
        {
            var result = await runner.RunAsync(executable, args, path, TimeSpan.FromMinutes(2));
            Assert.That(result.ExitCode, Is.Zero, result.StandardOutput + result.StandardError); return result;
        }
        var elaboration = await new SurelogFrontend(runner).ElaborateAsync([source], Path.Combine(path, "surelog"), "operators", workers: 4);
        string exporter = Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL") ?? Path.Combine(root, "artifacts/with-uhdm/bin/velosim-uhdm-rtl");
        await Run(exporter, elaboration.UhdmPath, Path.Combine(path, "design.json"), "operators");
        var design = RtlDesign.Parse(File.ReadAllText(Path.Combine(path, "design.json")));
        var expectedSignals = new List<RtlSignal> { new("clk", 1, "input"), new("a", aWidth, "input"), new("b", bWidth, "input"), new("sel", 2, "input") };
        expectedSignals.AddRange(Operators.Select(o => new RtlSignal("q_" + o.Kind, o.Scalar ? 1 : resultWidth, "output")));
        expectedSignals.Add(new("q_nested", resultWidth, "output"));
        if (aWidth < 64) expectedSignals.Add(new("q_shiftconcat", aWidth + 1, "output"));
        Assert.That(design.Signals.OrderBy(s => s.Name), Is.EqualTo(expectedSignals.OrderBy(s => s.Name)), "Exported ports must match the independent source specification.");
        Assert.That(design.Body.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("target").GetProperty("name").GetString()), Is.EquivalentTo(expectedSignals.Where(s => s.Direction == "output").Select(s => s.Name)), "Every requested operator output must have its NBA writer.");
        File.WriteAllText(Path.Combine(path, "design.cpp"), design.EmitCpp());
        File.Copy(Path.Combine(root, "native/rtl/runtime.h"), Path.Combine(path, "runtime.h"));
        File.WriteAllText(Path.Combine(path, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.20)\nproject(operators LANGUAGES CXX)\nset(CMAKE_CXX_STANDARD 20)\nadd_executable(operators design.cpp)\n");
        var inputs = design.Signals.Where(s => s.Direction == "input" && s.Name != design.Clock).ToArray();
        var outputs = design.Signals.Where(s => s.Direction == "output").ToArray();
        var vectors = new List<Dictionary<string, string>>();
        foreach (char a in "01xz") foreach (char b in "01xz") foreach (char select in "01xz")
            vectors.Add(new() { ["a"] = new(a, aWidth), ["b"] = new(b, bWidth), ["sel"] = "0" + select });
        static string Bits(ulong v, int width) => new(Enumerable.Range(0, width).Reverse().Select(i => ((v >> i) & 1) == 0 ? '0' : '1').ToArray());
        foreach (ulong n in new ulong[] { 0, 1, 2, (ulong)aWidth - 1, (ulong)aWidth, 63, 64, 65, ulong.MaxValue })
            vectors.Add(new() { ["a"] = new('1', aWidth), ["b"] = Bits(n, bWidth), ["sel"] = "01" });
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
        string stimulus = Path.Combine(path, "stimulus.txt");
        File.WriteAllLines(stimulus, vectors.Select(v => string.Join(' ', inputs.Select(s => v[s.Name]))));
        var tb = new StringBuilder("module tb; logic clk=0;\n");
        foreach (var signal in design.Signals.Where(s => s.Name != "clk")) tb.AppendLine($"{(signal.Direction == "input" ? "logic" : "wire")} [{signal.Width - 1}:0] {signal.Name};");
        tb.AppendLine("operators dut(.*); initial begin");
        for (int i = 0; i < vectors.Count; i++)
        {
            tb.Append("clk=0; "); foreach (var input in inputs) tb.Append($"{input.Name}={input.Width}'b{vectors[i][input.Name]}; ");
            tb.Append($"#1; clk=1; #1; $display(\"VS_RTL {i + 1}{string.Concat(outputs.Select(_ => " %b"))}\",{string.Join(',', outputs.Select(s => s.Name))});\n");
        }
        File.WriteAllText(Path.Combine(path, "tb.sv"), tb.Append("$finish; end endmodule\n").ToString());
        await Run("cmake", "-S", path, "-B", Path.Combine(path, "build"), "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release");
        await Run("cmake", "--build", Path.Combine(path, "build"), "--parallel", "4");
        await Run("iverilog", "-g2012", "-s", "tb", "-o", Path.Combine(path, "reference.vvp"), source, Path.Combine(path, "tb.sv"));
        var reference = await Run("vvp", Path.Combine(path, "reference.vvp"));
        string[] rows = reference.StandardOutput.Split('\n').Where(l => l.StartsWith("VS_RTL ", StringComparison.Ordinal)).ToArray();
        Assert.That(rows, Has.Length.EqualTo(vectors.Count)); string expected = string.Join('\n', rows) + "\n";
        File.WriteAllText(Path.Combine(path, "expected.txt"), expected);
        foreach (string workers in new[] { "1", "4", "8" })
        {
            var result = await Run(Path.Combine(path, "build/operators"), stimulus, workers);
            File.WriteAllText(Path.Combine(path, "native-" + workers + ".txt"), result.StandardOutput);
            Assert.That(result.StandardOutput, Is.EqualTo(expected), path);
        }
        Assert.That(ArtifactMetadata.SourceClosure([source]), Is.EqualTo(sources), "The same design file must remain unchanged for both compilers.");
        File.WriteAllText(Path.Combine(path, "report.json"), JsonSerializer.Serialize(new { status = "pass", source_mode = "same-file", sources, aWidth, bWidth, resultWidth, cycles = vectors.Count, seed = 0x91ab32, workers = new[] { 1, 4, 8 }, execution_scope = "single synchronous process; serial", operators = Operators, commands = runner.Commands }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
