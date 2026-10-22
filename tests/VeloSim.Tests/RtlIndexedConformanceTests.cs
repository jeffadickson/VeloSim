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
            host.AppendLine("top.clk=0;top.eval();top.timeInc(1);top.clk=1;top.eval();top.eval();std::cout<<\"VS_RTL \"<<++cycle;"); foreach (var s in outputs) host.AppendLine($"std::cout<<' '<<top.{names[s.Name]}.bits();"); host.AppendLine("std::cout<<'\\n';}top.final();Vdut probe;");
            foreach (var s in inputs) host.AppendLine($"probe.{names[s.Name]}.bits(\"{new string(s.Name == "i" ? '0' : '1', s.Width)}\");");
            host.AppendLine("probe.rst_n=0;probe.enable=1;probe.clk=0;probe.eval();probe.clk=1;probe.eval();auto before=probe.q.bits();auto previousSnapshot=probe.snapshot.bits();probe.rst_n=1;probe.i=2147483648ULL;probe.clk=0;probe.eval();probe.clk=1;bool rejected=false;try{probe.eval();}catch(const std::out_of_range&){rejected=true;}if(!rejected||probe.q.bits()!=before||probe.snapshot.bits()!=previousSnapshot)return 5;probe.i=0;probe.eval();if(probe.evalNeeded())return 6;probe.final();return 0;}"); File.WriteAllText(Path.Combine(path, "sdk/host.cpp"), host.ToString());
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
    [Test, Category("RealFifo")]
    public async Task ActualBitFixedWritesCycles51To60()
    {
        const string source = "module dut(input logic clk,rst_n,mode,input logic[63:0]a,i,output logic[63:0]q,snapshot,output logic[6:0]idx);always_ff @(posedge clk) begin if(!rst_n) begin q<='0;snapshot<='0;idx<='0;end else begin idx<=idx+7'b1;if(mode) begin q<=a;q[i]<=~a[0];q[7:4]<=a[3:0];q[idx]<=a[2];end else begin q[i]<=~a[0];q[7:4]<=a[3:0];q<=a;end snapshot<=q;end end endmodule";
        var vectors = ReadVectors(); for (int row = 0; row < vectors.Count; ++row) { vectors[row]["rst_n"] = row == 0 ? "0" : "1"; vectors[row]["mode"] = row % 2 == 1 ? "1" : "0"; }
        await Qualify("bit-fixed-writes", source, [new("clk",1,"input"),new("rst_n",1,"input"),new("mode",1,"input"),new("a",64,"input"),new("i",64,"input"),new("q",64,"output"),new("snapshot",64,"output"),new("idx",7,"output")], vectors, 51);
    }    [Test, Category("RealFifo")]
    public async Task ActualIndexedWritesCycles71To80()
    {
        const string source = "module dut(input logic clk,input logic[63:0]a,i,output logic[63:0]qplus,qminus,qwide,history);always_ff @(posedge clk) begin qplus<=a;qplus[i +: 4]<=~a[3:0];qminus<=a;qminus[i -: 4]<=a[3:0];qwide<=a;qwide[i -: 64]<=~a;history<=qwide;end endmodule";
        await Qualify("indexed-writes", source, [new("clk",1,"input"),new("a",64,"input"),new("i",64,"input"),new("qplus",64,"output"),new("qminus",64,"output"),new("qwide",64,"output"),new("history",64,"output")], ReadVectors(), 71);
    }

    [TestCase(91,1,4), TestCase(92,7,2), TestCase(93,8,4), TestCase(94,16,8), TestCase(95,31,16), TestCase(96,32,31), TestCase(97,33,32), TestCase(98,63,33), TestCase(99,64,63), TestCase(100,64,64), Category("RealFifo")]
    public async Task MixedWidthSelectedNbaMatrix(int cycle, int width, int slice)
    {
        string source = $"module dut(input logic clk,rst_n,enable,input logic[63:0]i,input logic[{width-1}:0]select_indexed,deposit_indexed,output logic[{width-1}:0]q,snapshot,output logic[{slice-1}:0]plus,minus);always_ff @(posedge clk) begin if(!rst_n) begin q<='0;snapshot<='0;plus<='0;minus<='0;end else begin snapshot<=q;if(enable) begin q<=select_indexed;q[i]<=deposit_indexed[0];q[3:0]<=deposit_indexed[3:0];q[i +: {slice}]<=deposit_indexed;q[i -: {slice}]<=(select_indexed << 3);end plus<=select_indexed[i +: {slice}];minus<=select_indexed[i -: {slice}];end end endmodule";
        var vectors = new List<Dictionary<string,string>>(); uint seed = 0x812afU + (uint)width;
        string RandomBits(int n, bool fourState) { var bits = new char[n]; for (int bit = 0; bit < n; ++bit) { seed ^= seed << 13; seed ^= seed >> 17; seed ^= seed << 5; bits[bit] = "01xz"[(int)(seed % (fourState ? 4U : 2U))]; } return new(bits); }
        for (int row = 0; row < 128; ++row)
        {
            string basis = row % 17 == 7 ? new('x',64) : row % 17 == 8 ? new('z',64) : Bits(row % 17 == 9 ? 2147483647UL : (ulong)(row % (width + 3)),64);
            vectors.Add(new() { ["rst_n"] = row % 31 == 0 ? "0" : "1", ["enable"] = row % 5 == 0 ? "0" : "1", ["i"] = basis, ["select_indexed"] = RandomBits(width,row>=64), ["deposit_indexed"] = RandomBits(width,row>=64) });
        }
        await Qualify($"matrix-{width}-{slice}", source, [new("clk",1,"input"),new("rst_n",1,"input"),new("enable",1,"input"),new("i",64,"input"),new("select_indexed",width,"input"),new("deposit_indexed",width,"input"),new("q",width,"output"),new("snapshot",width,"output"),new("plus",slice,"output"),new("minus",slice,"output")], vectors, sdk:true);
        TestContext.Progress.WriteLine($"RTL_CYCLE {cycle} PASS mixed width {width}, slice {slice}, 128 cycles, standalone and SDK");
    }}
