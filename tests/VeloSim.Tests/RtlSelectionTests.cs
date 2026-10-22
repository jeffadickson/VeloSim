using NUnit.Framework;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace VeloSim.Tests;

public class RtlSelectionTests
{
    private static string Root() { for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d is not null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "native/rtl/runtime.h"))) return d.FullName; throw new IOException("Checkout missing."); }
    private static string Fixture(object value, object? target = null) => JsonSerializer.Serialize(new { format = "velosim.rtl.v1", signals = new[] { new { name = "clk", width = 1, direction = "input" }, new { name = "a", width = 64, direction = "input" }, new { name = "i", width = 7, direction = "input" }, new { name = "q", width = 8, direction = "output" } }, memories = Array.Empty<object>(), continuous = Array.Empty<object>(), clock = "clk", body = new { kind = "nba", target = target ?? new { kind = "ref", name = "q" }, value } });
    [Test] public void SelectionIrValidatesFieldsBoundsAndSelectedTargets()
    {
        var a = new { kind = "ref", name = "a" }; var i = new { kind = "ref", name = "i" };
        Assert.That(RtlDesign.Parse(Fixture(new { kind = "bitselect", value = a, index = i })).EmitCpp(), Does.Contain("select_bit(s[1],s[2])"));
        Assert.That(RtlDesign.Parse(Fixture(new { kind = "partselect", value = a, left = 70, right = 63 })).EmitCpp(), Does.Contain("select_part(s[1],70,63)"));
        foreach (var bounds in new[] { (L: 0, R: 1), (L: 128, R: 0), (L: -1, R: -2), (L: int.MaxValue, R: 0) })
            Assert.Throws<IrValidationException>(() => RtlDesign.Parse(Fixture(new { kind = "partselect", value = a, left = bounds.L, right = bounds.R })));
        Assert.That(RtlDesign.Parse(Fixture(a, new { kind = "bitselect", value = new { kind = "ref", name = "q" }, index = i })).EmitCpp(), Does.Contain("deposit_indexed(next[3],s[2],1,false,s[1])"));
        var json = JsonNode.Parse(Fixture(new { kind = "bitselect", value = a, index = i }))!; json["body"]!["value"]!["extra"] = 1;
        Assert.Throws<IrValidationException>(() => RtlDesign.Parse(json.ToJsonString()));
        Assert.Throws<IrValidationException>(() => RtlDesign.Parse(Fixture(new { kind = "bitselect", value = a })));
        Assert.Throws<IrValidationException>(() => RtlDesign.Parse(Fixture(new { kind = "partselect", value = a, left = 7 })));
        Assert.Throws<IrValidationException>(() => RtlDesign.Parse(Fixture(new { kind = "bitselect", value = new { kind = "ref", name = "missing" }, index = i })));
        JsonNode deep = JsonSerializer.SerializeToNode(a)!;
        for (int depth = 0; depth < 65; depth++) deep = new JsonObject { ["kind"] = "partselect", ["value"] = deep, ["left"] = 0, ["right"] = 0 };
        var deepFixture = JsonNode.Parse(Fixture(a))!; deepFixture["body"]!["value"] = deep;
        Assert.Throws<IrValidationException>(() => RtlDesign.Parse(deepFixture.ToJsonString(new JsonSerializerOptions { MaxDepth = 256 })));
    }
    [Test] public void SelectionDependenciesParticipateInCombinationalCycleAndClockChecks()
    {
        var json = JsonNode.Parse(Fixture(new { kind = "ref", name = "a" }))!;
        json["body"] = new JsonObject { ["kind"] = "block", ["items"] = new JsonArray() };
        json["continuous"] = JsonNode.Parse("""[{"target":{"kind":"ref","name":"q"},"value":{"kind":"bitselect","value":{"kind":"ref","name":"a"},"index":{"kind":"ref","name":"clk"}}}]""");
        Assert.Throws<IrValidationException>(() => RtlDesign.Parse(json.ToJsonString()));
        json["continuous"]![0]!["value"]!["index"] = JsonNode.Parse("""{"kind":"partselect","value":{"kind":"ref","name":"q"},"left":0,"right":0}""");
        Assert.Throws<IrValidationException>(() => RtlDesign.Parse(json.ToJsonString()));
    }
    [Test, Category("RealFifo")]
    public async Task SameSourcePackedSelectionsConcatAndReplicationMatchIcarus()
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned UHDM RTL exporter.");
        string root = Root(), path = Path.Combine(root, "artifacts", "rtl-selections-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        string source = Path.Combine(path, "selections.sv");
        string original = File.ReadAllText(Path.Combine(root, "examples/surelog_selections.sv"));
        // Preserve the documented example while also exercising partial/fully out-of-range reads,
        // top-bit masks, selection self-determination, and NBA history.
        string extended = original.Replace("output logic selected,", "output logic selected, history, top_bit, selected_less,\n    output logic [7:0] partial_oob, whole_oob, nested_bit_concat,\n    output logic [15:0] selected_shift, selected_sum,\n    output logic [63:0] identity, concat64, nested_repeat,\n    output logic [63:0] repeated_bit,", StringComparison.Ordinal)
            .Replace("selected <= data[index];", "selected <= data[index]; history <= selected; top_bit <= data[63];\n    partial_oob <= data[70:63]; whole_oob <= data[80:73];\n    selected_shift <= data[7:0] << 4'd8; selected_sum <= data[7:0] + 8'b1;\n    selected_less <= data[7:0] < data[15:8]; nested_repeat <= {2{{4{data[7:0]}}}};\n    nested_bit_concat <= {data[7:1],{1{data[0]}}}; identity <= {1{data[63:0]}};\n    repeated_bit <= {64{data[0]}}; concat64 <= {" + string.Join(',', Enumerable.Range(0, 64).Reverse().Select(i => $"data[{i}]")) + "};", StringComparison.Ordinal);
        extended = extended.Replace("output logic [7:0] partial_oob", "output logic [1:0] repeated_add, repeated_and,\n    output logic [7:0] partial_oob", StringComparison.Ordinal).Replace("selected <= data[index];", "selected <= data[index]; repeated_add <= {2{data[0] + 1'b0}}; repeated_and <= {2{data[0] & 1'b1}};", StringComparison.Ordinal);
        File.WriteAllText(source, extended); var sources = ArtifactMetadata.SourceClosure([source]);
        var runner = new RecordingProcessRunner(new ProcessRunner()); var tools = new Dictionary<string, FileFingerprint>(StringComparer.Ordinal);
        async Task<ProcessResult> Run(string exe, params string[] args)
        {
            exe = ArtifactMetadata.ResolveExecutable(exe, path) ?? throw new IOException("Missing tool: " + exe); var hash = ArtifactMetadata.Fingerprint(exe);
            if (tools.TryGetValue(exe, out var previous)) Assert.That(hash, Is.EqualTo(previous)); else tools.Add(exe, hash);
            var result = await runner.RunAsync(exe, args, path, TimeSpan.FromMinutes(2)); Assert.That(result.ExitCode, Is.Zero, result.StandardOutput + result.StandardError); return result;
        }
        string surelog = ArtifactMetadata.ResolveExecutable(Environment.GetEnvironmentVariable("VELOSIM_SURELOG") ?? "surelog", path) ?? throw new IOException("Surelog missing."); tools.Add(surelog, ArtifactMetadata.Fingerprint(surelog));
        var elaboration = await new SurelogFrontend(runner).ElaborateAsync([source], Path.Combine(path, "surelog"), "surelog_selections", executable: surelog, workers: 4);
        var uhdmFingerprint = ArtifactMetadata.Fingerprint(elaboration.UhdmPath);
        await Run(Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL") ?? Path.Combine(root, "artifacts/with-uhdm/bin/velosim-uhdm-rtl"), elaboration.UhdmPath, Path.Combine(path, "design.json"), "surelog_selections");
        var design = RtlDesign.Parse(File.ReadAllText(Path.Combine(path, "design.json")));
        var expectedSignals = new[] { new RtlSignal("clk", 1, "input"), new("data", 64, "input"), new("index", 7, "input"), new("selected", 1, "output"), new("history", 1, "output"), new("top_bit", 1, "output"), new("selected_less", 1, "output"), new("partial_oob", 8, "output"), new("whole_oob", 8, "output"), new("selected_shift", 16, "output"), new("selected_sum", 16, "output"), new("identity", 64, "output"), new("concat64", 64, "output"), new("nested_repeat", 64, "output"), new("repeated_bit", 64, "output"), new("low_byte", 8, "output"), new("high_byte", 8, "output"), new("reordered", 16, "output"), new("repeated", 32, "output") };
        expectedSignals = [.. expectedSignals, new("nested_bit_concat", 8, "output"), new("repeated_add", 2, "output"), new("repeated_and", 2, "output")];
        Assert.That(design.Signals.OrderBy(s => s.Name), Is.EqualTo(expectedSignals.OrderBy(s => s.Name)), "Source interface must not depend on exporter omissions.");
        Assert.That(design.Body.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("target").GetProperty("name").GetString()), Is.EquivalentTo(expectedSignals.Where(s => s.Direction == "output").Select(s => s.Name)), "Each independently specified output requires its writer.");
        File.WriteAllText(Path.Combine(path, "design.cpp"), design.EmitCpp()); File.Copy(Path.Combine(root, "native/rtl/runtime.h"), Path.Combine(path, "runtime.h"));
        File.WriteAllText(Path.Combine(path, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.20)\nproject(selections LANGUAGES CXX)\nset(CMAKE_CXX_STANDARD 20)\nadd_executable(selections design.cpp)\n");
        var inputs = design.Signals.Where(s => s.Direction == "input" && s.Name != design.Clock).ToArray(); var outputs = design.Signals.Where(s => s.Direction == "output").ToArray();
        var vectors = new List<Dictionary<string, string>>();
        static string Bits(ulong value, int width) => new(Enumerable.Range(0, width).Reverse().Select(i => (value & (1UL << i)) == 0 ? '0' : '1').ToArray());
        foreach (char digit in "01xz") foreach (int index in new[] { 0, 1, 7, 8, 31, 32, 63, 64, 65, 127 }) vectors.Add(new() { ["data"] = new(digit, 64), ["index"] = Bits((ulong)index, 7) });
        foreach (string index in new[] { "xxxxxxx", "zzzzzzz", "000000x", "000000z" }) vectors.Add(new() { ["data"] = Bits(0xfedcba9876543210, 64), ["index"] = index });
        uint seed = 0x59a24b;
        for (int row = 0; row < 128; row++) { var values = new Dictionary<string, string>(); foreach (var input in inputs) { var bits = new char[input.Width]; for (int i = 0; i < bits.Length; i++) { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; bits[i] = "01xz"[(int)(seed % (row < 64 ? 2u : 4u))]; } values.Add(input.Name, new(bits)); } vectors.Add(values); }
        File.WriteAllLines(Path.Combine(path, "stimulus.txt"), vectors.Select(v => string.Join(' ', inputs.Select(s => v[s.Name]))));
        var tb = new StringBuilder("module tb; logic clk=0;\n"); foreach (var signal in design.Signals.Where(s => s.Name != "clk")) tb.AppendLine($"{(signal.Direction == "input" ? "logic" : "wire")} [{signal.Width - 1}:0] {signal.Name};"); tb.AppendLine("surelog_selections dut(.*); initial begin");
        for (int row = 0; row < vectors.Count; row++) { tb.Append("clk=0;"); foreach (var input in inputs) tb.Append($"{input.Name}={input.Width}'b{vectors[row][input.Name]};"); tb.AppendLine($"#1;clk=1;#1;$display(\"VS_RTL {row + 1}{string.Concat(outputs.Select(_ => " %b"))}\",{string.Join(',', outputs.Select(s => s.Name))});"); }
        File.WriteAllText(Path.Combine(path, "tb.sv"), tb.AppendLine("$finish;end endmodule").ToString());
        var buildInputs = new[] { source, "design.json", "design.cpp", "runtime.h", "CMakeLists.txt", "stimulus.txt", "tb.sv" }.Select(p => ArtifactMetadata.Fingerprint(Path.GetFullPath(p, path))).ToArray();
        await Run("cmake", "-S", path, "-B", Path.Combine(path, "build"), "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release");
        string Cache(string name) => File.ReadLines(Path.Combine(path, "build/CMakeCache.txt")).Single(l => l.StartsWith(name + ":FILEPATH=", StringComparison.Ordinal)).Split('=', 2)[1];
        var compilerVersion = await Run(Cache("CMAKE_CXX_COMPILER"), "--version"); var ninjaVersion = await Run(Cache("CMAKE_MAKE_PROGRAM"), "--version");
        await Run("cmake", "--build", Path.Combine(path, "build"), "--parallel", "4");
        await Run("iverilog", "-g2012", "-s", "tb", "-o", Path.Combine(path, "reference.vvp"), source, Path.Combine(path, "tb.sv"));
        var reference = await Run("vvp", Path.Combine(path, "reference.vvp")); _ = FifoDifferential.ParseTrace(reference.StandardOutput, design, vectors.Count); var rows = reference.StandardOutput.Split('\n').Where(l => l.StartsWith("VS_RTL ", StringComparison.Ordinal)).ToArray(); Assert.That(rows.Length, Is.EqualTo(vectors.Count)); string expected = string.Join('\n', rows) + "\n"; File.WriteAllText(Path.Combine(path, "expected.txt"), expected);
        foreach (string workers in new[] { "1", "4", "8" }) { var result = await Run(Path.Combine(path, "build/selections"), Path.Combine(path, "stimulus.txt"), workers); _ = FifoDifferential.ParseTrace(result.StandardOutput, design, vectors.Count); File.WriteAllText(Path.Combine(path, "native-" + workers + ".txt"), result.StandardOutput); Assert.That(result.StandardOutput, Is.EqualTo(expected), path); }
        Assert.That(ArtifactMetadata.SourceClosure([source]), Is.EqualTo(sources)); foreach (var tool in tools.Values.Concat(buildInputs).Append(uhdmFingerprint)) Assert.That(ArtifactMetadata.Fingerprint(tool.Path), Is.EqualTo(tool));
        var artifacts = new[] { "reference.vvp", "expected.txt", "native-1.txt", "native-4.txt", "native-8.txt", "build/selections", "build/CMakeCache.txt" }.Select(p => ArtifactMetadata.Fingerprint(Path.Combine(path, p))).ToArray();
        File.WriteAllText(Path.Combine(path, "report.json"), JsonSerializer.Serialize(new { schema = "velosim.rtl.selections.v1", status = "pass", source_mode = "same_file_packed_selections", cycles = vectors.Count, workers = new[] { 1, 4, 8 }, build_jobs = 4, execution_scope = "single_process_serial", sources, buildInputs, uhdmFingerprint, artifacts, compiler_version = compilerVersion.StandardOutput, ninja_version = ninjaVersion.StandardOutput, tools = tools.Values, commands = runner.Commands }, new JsonSerializerOptions { WriteIndented = true }));
    }
    [Test, Category("RealFifo")]
    public async Task NoncanonicalRangesNegativeSelectsAndSelectedWritersFailClosed()
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned UHDM RTL exporter.");
        string root = Root(), path = Path.Combine(root, "artifacts", "rtl-selections-rejections-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); var runner = new ProcessRunner();
        var variants = new Dictionary<string, string> { ["ascending"] = "input logic [0:7] a, output logic q); always_ff @(posedge clk) q<=a[0];", ["nonzero"] = "input logic [15:8] a, output logic q); always_ff @(posedge clk) q<=a[8];", ["negative"] = "input logic [7:0] a, output logic q); always_ff @(posedge clk) q<=a[-1];", ["lhs-memory"] = "input logic [7:0] a, output logic [7:0] q); logic[7:0]m[0:1]; always_ff @(posedge clk) begin m[0][0]<=a[0];q<=m[0];end", ["lhs-fixed-memory"] = "input logic [7:0] a, output logic [7:0] q);logic[7:0]m[0:1];always_ff @(posedge clk) begin m[0][3:0]<=a[3:0];q<=m[0];end", ["lhs-indexed-memory"] = "input logic [7:0] a, input logic [2:0] i, output logic [7:0] q);logic[7:0]m[0:1];always_ff @(posedge clk) begin m[0][i +: 2]<=a[1:0];q<=m[0];end", ["indexed-overwide"] = "input logic [7:0] a, input logic [2:0] i, output logic [63:0] q); always_ff @(posedge clk) q<=a[i +: 129];", ["indexed-negative"] = "input logic [7:0] a, output logic [1:0] q); always_ff @(posedge clk) q<=a[-1 -: 2];", ["zero-repeat"] = "input logic [7:0] a, output logic [7:0] q); always_ff @(posedge clk) q<={a,{0{a}}};", ["overwide-concat"] = "input logic [63:0] a, output logic [63:0] q); always_ff @(posedge clk) q<={a,a,1'b0};", ["overwide-repeat"] = "input logic [63:0] a, output logic [63:0] q); always_ff @(posedge clk) q<={129{a[0]}};" };
        variants.Add("negative-part", "input logic [7:0] a, output logic [1:0] q); always_ff @(posedge clk) q<=a[2'sb11:2'sb10];");
        variants.Add("negative-repeat", "input logic a, output logic [2:0] q); always_ff @(posedge clk) q<={2'sb11{a}};");
        variants.Add("wrapped-repeat", "input logic a, output logic [3:0] q); always_ff @(posedge clk) q<={{(2'b11 + 2'b01){a}},a};");
        static string ExpandedPayload(int depth) => depth == 0 ? "(a[0] & a[1])" : "(" + ExpandedPayload(depth - 1) + " ^ " + ExpandedPayload(depth - 1) + ")";
        variants.Add("expanded-nodes", "input logic [7:0] a, output logic [63:0] q); always_ff @(posedge clk) q<={64{" + ExpandedPayload(7) + "}};");
        foreach (var (name, body) in variants) { string file = Path.Combine(path, name + ".sv"); File.WriteAllText(file, "module bad(input logic clk," + body + " endmodule\n"); var elaboration = await new SurelogFrontend().ElaborateAsync([file], Path.Combine(path, name), "bad", workers: 4); var result = await runner.RunAsync(Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL") ?? Path.Combine(root, "artifacts/with-uhdm/bin/velosim-uhdm-rtl"), [elaboration.UhdmPath, Path.Combine(path, name + ".json"), "bad"], path, TimeSpan.FromSeconds(30)); if (name.StartsWith("lhs", StringComparison.Ordinal)) { if (result.ExitCode == 0) Assert.Throws<IrValidationException>(() => RtlDesign.Parse(File.ReadAllText(Path.Combine(path, name + ".json"))), name); } else Assert.That(result.ExitCode, Is.Not.Zero, name); if (name == "expanded-nodes") Assert.That(result.StandardError, Does.Contain("budget").Or.Contain("4MiB"), "Expanded replication must hit a normalized resource guard."); }
    }
}
