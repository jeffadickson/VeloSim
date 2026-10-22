using NUnit.Framework;
using System.Text;
using System.Text.Json;
namespace VeloSim.Tests;

public class RtlIndexedConformanceTests
{
    private static string Root() { for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d is not null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "native/rtl/runtime.h"))) return d.FullName; throw new IOException("Checkout missing."); }
    private static string Bits(ulong n, int width) => string.Concat(Enumerable.Range(0, width).Reverse().Select(i => ((n >> i) & 1) == 0 ? '0' : '1'));
    private static List<Dictionary<string, string>> ReadVectors() => new[] { "0", "1", "60", "63", "64", "70", "2147483647", "x", "z", "7" }.Select((basis, row) => new Dictionary<string, string> { ["a"] = row == 9 ? string.Concat(Enumerable.Range(0, 64).Select(i => "01xz"[i % 4])) : Bits(0x8000000180000001UL, 64), ["i"] = basis == "max" ? new('1', 64) : basis is "x" or "z" ? new(basis[0], 64) : Bits(ulong.Parse(basis), 64) }).ToList();
    private static async Task Qualify(string tag, string sourceText, RtlSignal[] expected, List<Dictionary<string, string>> vectors, int firstRowCycle = 0, bool sdk = false)
    {
        if (Environment.GetEnvironmentVariable("VELOSIM_REAL_FIFO") != "1") Assert.Ignore("Requires pinned Surelog/UHDM and Icarus.");
        string root = Root(), path = Path.Combine(root, "artifacts", "rtl-indexed-" + tag + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
        string source = Path.Combine(path, "design.sv"); File.WriteAllText(source, sourceText); var sources = ArtifactMetadata.SourceClosure([source]);
        var runner = new RecordingProcessRunner(new ProcessRunner()); var tools = new Dictionary<string, FileFingerprint>(StringComparer.Ordinal);
        string Tool(string executable) { string resolved = ArtifactMetadata.ResolveExecutable(executable, path) ?? throw new IOException("Missing tool " + executable); tools.TryAdd(resolved, ArtifactMetadata.Fingerprint(resolved)); return resolved; }
        async Task<ProcessResult> Run(string executable, params string[] args) { var result = await runner.RunAsync(Tool(executable), args, path, TimeSpan.FromMinutes(3)); Assert.That(result.ExitCode, Is.Zero, result.StandardOutput + result.StandardError); return result; }
        string surelog = Tool(Environment.GetEnvironmentVariable("VELOSIM_SURELOG") ?? "surelog"), iverilog = Tool(Environment.GetEnvironmentVariable("VELOSIM_INDEXED_IVERILOG") ?? "iverilog"), vvp = Tool(Environment.GetEnvironmentVariable("VELOSIM_INDEXED_VVP") ?? "vvp");
        var referenceVersion = await Run(iverilog, "-V");
        var elaboration = await new SurelogFrontend(runner).ElaborateAsync([source], Path.Combine(path, "surelog"), "dut", executable: surelog, workers: 4);
        await Run(Environment.GetEnvironmentVariable("VELOSIM_UHDM_RTL") ?? Path.Combine(root, "artifacts/with-uhdm/bin/velosim-uhdm-rtl"), elaboration.UhdmPath, Path.Combine(path, "design.json"), "dut");
        var design = RtlDesign.Parse(File.ReadAllText(Path.Combine(path, "design.json"))); Assert.That(design.Signals.OrderBy(s => s.Name), Is.EqualTo(expected.OrderBy(s => s.Name)), "Independently specified complete source interface");
        var inputs = design.Signals.Where(s => s.Direction == "input" && s.Name != "clk").ToArray(); var outputs = design.Signals.Where(s => s.Direction == "output").ToArray();
        File.WriteAllText(Path.Combine(path, "design.cpp"), design.EmitCpp()); File.Copy(Path.Combine(root, "native/rtl/runtime.h"), Path.Combine(path, "runtime.h"));
        File.WriteAllText(Path.Combine(path, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.20)\nproject(indexed LANGUAGES CXX)\nset(CMAKE_CXX_STANDARD 20)\nadd_executable(indexed design.cpp)\n");
        File.WriteAllLines(Path.Combine(path, "stimulus.txt"), vectors.Select(v => string.Join(' ', inputs.Select(s => v[s.Name]))));
        var tb = new StringBuilder("module tb; logic clk=0;\n"); foreach (var s in design.Signals.Where(s => s.Name != "clk")) tb.AppendLine($"{(s.Direction == "input" ? "logic" : "wire")} [{s.Width - 1}:0] {s.Name};"); tb.AppendLine("dut dut_instance(.*); initial begin");
        for (int row = 0; row < vectors.Count; row++) { tb.Append("clk=0;"); foreach (var input in inputs) tb.Append($"{input.Name}={input.Width}'b{vectors[row][input.Name]};"); tb.AppendLine($"#1;clk=1;#1;$display(\"VS_RTL {row + 1}{string.Concat(outputs.Select(_ => " %b"))}\",{string.Join(',', outputs.Select(s => s.Name))});"); }
        File.WriteAllText(Path.Combine(path, "tb.sv"), tb.AppendLine("$finish;end endmodule").ToString());
        if (sdk)
        {
            await RtlExternalSdk.ExportAsync(Path.Combine(path, "design.json"), "dut", Path.Combine(path, "sdk")); var names = design.ExternalPortNames();
            var host = new StringBuilder("#include \"Vdut.h\"\n#include <fstream>\n#include <iostream>\n#include <sstream>\nint main(int argc,char**argv){if(argc!=2)return 2;Vdut top;std::ifstream input(argv[1]);std::string line,bits;int cycle=0;while(std::getline(input,line)){std::istringstream row(line);\n");
            foreach (var s in inputs) host.AppendLine($"if(!(row>>bits))return 3;top.{names[s.Name]}.bits(bits);");
            host.AppendLine("top.clk=0;top.eval();top.timeInc(1);top.clk=1;top.eval();top.eval();std::cout<<\"VS_RTL \"<<++cycle;"); foreach (var s in outputs) host.AppendLine($"std::cout<<' '<<top.{names[s.Name]}.bits();"); host.AppendLine("std::cout<<'\\n';}top.final();return 0;}"); File.WriteAllText(Path.Combine(path, "sdk/host.cpp"), host.ToString());
        }
        var compileInputs = Directory.GetFiles(path).Concat(sdk ? Directory.GetFiles(Path.Combine(path, "sdk")) : []).Append(elaboration.UhdmPath).Select(ArtifactMetadata.Fingerprint).ToArray();
        await Run("cmake", "-S", path, "-B", Path.Combine(path, "build"), "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release");
        string Cache(string name) => File.ReadLines(Path.Combine(path, "build/CMakeCache.txt")).Single(l => l.StartsWith(name + ":FILEPATH=", StringComparison.Ordinal)).Split('=', 2)[1];
        var compilerVersion = await Run(Cache("CMAKE_CXX_COMPILER"), "--version"); var ninjaVersion = await Run(Cache("CMAKE_MAKE_PROGRAM"), "--version");
        await Run("cmake", "--build", Path.Combine(path, "build"), "--parallel", "4"); await Run(iverilog, "-g2012", "-s", "tb", "-o", Path.Combine(path, "reference.vvp"), source, Path.Combine(path, "tb.sv"));
        var reference = await Run(vvp, Path.Combine(path, "reference.vvp")); _ = FifoDifferential.ParseTrace(reference.StandardOutput, design, vectors.Count);
        string Trace(string value) => string.Join('\n', value.Split('\n').Where(l => l.StartsWith("VS_RTL ", StringComparison.Ordinal))) + "\n";
        string expectedTrace = Trace(reference.StandardOutput); File.WriteAllText(Path.Combine(path, "reference.txt"), expectedTrace);
        foreach (string workers in new[] { "1", "4", "8" }) { var native = await Run(Path.Combine(path, "build/indexed"), Path.Combine(path, "stimulus.txt"), workers); _ = FifoDifferential.ParseTrace(native.StandardOutput, design, vectors.Count); Assert.That(native.StandardOutput, Is.EqualTo(expectedTrace), path); File.WriteAllText(Path.Combine(path, "native-" + workers + ".txt"), native.StandardOutput); }
        if (sdk) { await Run("cmake", "-S", Path.Combine(path, "sdk"), "-B", Path.Combine(path, "sdk-build"), "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DVELOSIM_HOST_SOURCE=" + Path.Combine(path, "sdk/host.cpp")); await Run("cmake", "--build", Path.Combine(path, "sdk-build"), "--parallel", "4"); var host = await Run(Path.Combine(path, "sdk-build/velosim_host"), Path.Combine(path, "stimulus.txt")); _ = FifoDifferential.ParseTrace(host.StandardOutput, design, vectors.Count); Assert.That(host.StandardOutput, Is.EqualTo(expectedTrace)); File.WriteAllText(Path.Combine(path, "sdk.txt"), host.StandardOutput); }
        Assert.That(ArtifactMetadata.SourceClosure([source]), Is.EqualTo(sources)); foreach (var input in compileInputs.Concat(tools.Values)) Assert.That(ArtifactMetadata.Fingerprint(input.Path), Is.EqualTo(input));
        var commands = runner.Commands; Assert.That(commands.Single(c => c.Executable == surelog).Arguments, Does.Contain(source)); Assert.That(commands.Single(c => c.Executable == iverilog && c.Arguments.Contains("-g2012")).Arguments, Does.Contain(source));
        var artifacts = new[] { "design.json", "build/indexed", "reference.vvp", "reference.txt", "native-1.txt", "native-4.txt", "native-8.txt" }.Concat(sdk ? ["sdk-build/velosim_host", "sdk-build/libvelosim_vpi.so", "sdk.txt"] : []).Select(p => ArtifactMetadata.Fingerprint(Path.Combine(path, p))).ToArray();
        File.WriteAllText(Path.Combine(path, "report.json"), JsonSerializer.Serialize(new { schema = "velosim.rtl.indexed.v1", status = "pass", tag, cycles = vectors.Count, firstRowCycle, sdk, workers = new[] { 1, 4, 8 }, execution_scope = "single_process_serial", build_jobs = 4, sources, compileInputs, tools = tools.Values, artifacts, reference_version = referenceVersion.StandardOutput, compiler_version = compilerVersion.StandardOutput, ninja_version = ninjaVersion.StandardOutput, commands }, new JsonSerializerOptions { WriteIndented = true }));
        if (firstRowCycle > 0) for (int row = 0; row < vectors.Count; ++row) TestContext.Progress.WriteLine($"RTL_CYCLE {firstRowCycle + row} PASS {tag} source vector {row}");
    }
    [Test, Category("RealFifo")]
    public async Task ActualIndexedReadsCycles21To30()
    {
        const string source = "module dut(input logic clk,input logic[63:0]a,i,output logic[3:0]plus,minus,output logic[63:0]wide);always_ff @(posedge clk) begin plus<=a[i +: 4];minus<=a[i -: 4];wide<=a[i -: 64];end endmodule";
        await Qualify("reads", source, [new("clk",1,"input"),new("a",64,"input"),new("i",64,"input"),new("plus",4,"output"),new("minus",4,"output"),new("wide",64,"output")], ReadVectors(), 21);
    }
}
