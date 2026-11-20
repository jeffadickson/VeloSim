using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
namespace VeloSim.Tests;

public sealed class RtlVerilatorGapsWave3Tests
{
    private sealed record ProcessOutput(int ExitCode, string StandardOutput, string StandardError);

    private static async Task<ProcessOutput> Run(string file, params string[] args) =>
        await RunIn(Environment.CurrentDirectory, file, args);

    private static async Task<ProcessOutput> RunIn(string cwd, string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var result = new ProcessOutput(proc.ExitCode, await outTask, await errTask);
        Assert.That(result.ExitCode, Is.EqualTo(0), $"{file} {string.Join(' ', args)}\n{result.StandardOutput}\n{result.StandardError}");
        return result;
    }

    private static string DecompressGzipText(string gzPath)
    {
        using var fs = File.OpenRead(gzPath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var sr = new StreamReader(gz, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    [Test]
    public async Task CompressedVcdGzMatchesPlainVcdAndRespectsTraceSelection()
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-wave3-vcdgz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string ir = """
            {
              "format":"velosim.rtl.v1",
              "clock":"clk",
              "signals":[
                {"name":"clk","width":1,"direction":"input"},
                {"name":"din","width":16,"direction":"input"},
                {"name":"sub_acc","width":16,"direction":"internal","path":"sub.acc"},
                {"name":"other_aux","width":8,"direction":"internal","path":"other.aux"},
                {"name":"acc","width":16,"direction":"output"},
                {"name":"aux","width":8,"direction":"output"}
              ],
              "memories":[],
              "continuous":[
                {"target":{"kind":"ref","name":"acc"},"value":{"kind":"ref","name":"sub_acc"}},
                {"target":{"kind":"ref","name":"aux"},"value":{"kind":"ref","name":"other_aux"}}
              ],
              "body":{
                "kind":"process",
                "events":[{"signal":"clk","edge":"posedge"}],
                "body":{
                  "kind":"block",
                  "items":[
                    {"kind":"nba","target":{"kind":"ref","name":"sub_acc"},"value":{"kind":"add","args":[{"kind":"ref","name":"sub_acc"},{"kind":"ref","name":"din"}]}},
                    {"kind":"nba","target":{"kind":"ref","name":"other_aux"},"value":{"kind":"partselect","value":{"kind":"ref","name":"din"},"left":7,"right":0}}
                  ]
                }
              }
            }
            """;
            string irPath = Path.Combine(root, "vcdgz.rtl.json");
            File.WriteAllText(irPath, ir);
            string sdk = Path.Combine(root, "sdk");
            await RtlExternalSdk.ExportAsync(irPath, "vcdgz", sdk);

            string plainVcd = Path.Combine(root, "full.vcd");
            string gzVcd = Path.Combine(root, "full.vcd.gz");
            string filteredGz = Path.Combine(root, "sub_only.vcd.gz");
            string hostPath = Path.Combine(root, "host.cpp");
            File.WriteAllText(hostPath, $$"""
                #include "Vvcdgz.h"
                #include <iostream>
                #include <stdexcept>
                int main(){
                 Vvcdgz m;
                 // Unmatched selector must fail closed.
                 bool rejected=false;
                 try{ m.openVcdGz("{{filteredGz.Replace("\\", "/")}}", "1ns", {"nonexistent"}); }
                 catch(const std::invalid_argument&){ rejected=true; }
                 if(!rejected) return 10;

                 // Run 1: uncompressed VCD.
                 m.openVcd("{{plainVcd.Replace("\\", "/")}}", "1ns");
                 m.din=0; m.clk=0; m.eval(); m.dumpVcd();
                 for(int i=1;i<=120;++i){
                  m.timeInc(5); m.din=uint64_t((i*37)&0xFFFF); m.clk=1; m.eval(); m.dumpVcd();
                  m.timeInc(5); m.clk=0; m.eval(); m.dumpVcd();
                 }
                 m.closeVcd();

                 // Run 2: identical stimulus -> gzip-compressed VCD.
                 Vvcdgz m2;
                 m2.openVcdGz("{{gzVcd.Replace("\\", "/")}}", "1ns");
                 m2.din=0; m2.clk=0; m2.eval(); m2.dumpVcd();
                 for(int i=1;i<=120;++i){
                  m2.timeInc(5); m2.din=uint64_t((i*37)&0xFFFF); m2.clk=1; m2.eval(); m2.dumpVcd();
                  m2.timeInc(5); m2.clk=0; m2.eval(); m2.dumpVcd();
                 }
                 m2.closeVcd();

                 // Run 3: filtered hierarchical selection ("sub").
                 Vvcdgz m3;
                 m3.openVcdGz("{{filteredGz.Replace("\\", "/")}}", "10ps", {"sub"});
                 m3.din=0x1234; m3.clk=0; m3.eval(); m3.dumpVcd();
                 m3.timeInc(10); m3.clk=1; m3.eval(); m3.dumpVcd();
                 m3.final();
                 std::cout<<"VCDGZ_OK\n";
                 return 0;
                }
                """);
            string build = Path.Combine(root, "build");
            await Run("cmake", "-S", sdk, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DVELOSIM_HOST_SOURCE=" + hostPath);
            await Run("cmake", "--build", build, "--parallel", "2");
            var run = await Run(Path.Combine(build, "velosim_host"));
            Assert.That(run.StandardOutput, Does.Contain("VCDGZ_OK"));

            string plainText = File.ReadAllText(plainVcd);
            string decompressedText = DecompressGzipText(gzVcd);
            Assert.That(decompressedText, Is.EqualTo(plainText));
            Assert.That(new FileInfo(gzVcd).Length, Is.LessThan(new FileInfo(plainVcd).Length),
                "Compressed .vcd.gz must be strictly smaller than uncompressed .vcd on a 120-cycle trace.");

            string filteredText = DecompressGzipText(filteredGz);
            Assert.That(filteredText, Does.Contain("$timescale 10ps $end"));
            Assert.That(filteredText, Does.Contain("$scope module sub $end"));
            Assert.That(filteredText, Does.Contain("acc [15:0]"));
            Assert.That(filteredText, Does.Not.Contain("other"));
            Assert.That(filteredText, Does.Not.Contain("aux"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task BitQueriesPlusargsAndSeededRngMatchAcrossCppCSharpAndRust()
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-wave3-xtarget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string ir = """
            {
              "format":"velosim.rtl.v1",
              "clock":"clk",
              "signals":[
                {"name":"clk","width":1,"direction":"input"},
                {"name":"din","width":16,"direction":"input"},
                {"name":"pop","width":32,"direction":"output"},
                {"name":"oh","width":1,"direction":"output"},
                {"name":"oh0","width":1,"direction":"output"},
                {"name":"unk","width":1,"direction":"output"},
                {"name":"lg","width":32,"direction":"output"},
                {"name":"has_flag","width":32,"direction":"output"},
                {"name":"vp_val","width":32,"direction":"output"},
                {"name":"vp_ok","width":32,"direction":"output"},
                {"name":"rnd","width":32,"direction":"output"},
                {"name":"rrng","width":32,"direction":"output"}
              ],
              "memories":[],
              "continuous":[
                {"target":{"kind":"ref","name":"pop"},"value":{"kind":"countones","args":[{"kind":"ref","name":"din"}]}},
                {"target":{"kind":"ref","name":"oh"},"value":{"kind":"onehot","args":[{"kind":"ref","name":"din"}]}},
                {"target":{"kind":"ref","name":"oh0"},"value":{"kind":"onehot0","args":[{"kind":"ref","name":"din"}]}},
                {"target":{"kind":"ref","name":"unk"},"value":{"kind":"isunknown","args":[{"kind":"ref","name":"din"}]}},
                {"target":{"kind":"ref","name":"lg"},"value":{"kind":"clog2","args":[{"kind":"ref","name":"din"}]}},
                {"target":{"kind":"ref","name":"has_flag"},"value":{"kind":"test_plusargs","prefix":"ENABLE_FAST"}}
              ],
              "body":{
                "kind":"process",
                "events":[{"signal":"clk","edge":"posedge"}],
                "body":{
                  "kind":"block",
                  "items":[
                    {"kind":"value_plusargs","format":"SEED=%h","target":"vp_val","matched":"vp_ok"},
                    {"kind":"nba","target":{"kind":"ref","name":"rnd"},"value":{"kind":"urandom"}},
                    {"kind":"nba","target":{"kind":"ref","name":"rrng"},"value":{"kind":"urandom_range","max":{"kind":"const","bits":"00000000000000000000000001100100"},"min":{"kind":"const","bits":"00000000000000000000000000001010"}}}
                  ]
                }
              }
            }
            """;
            string irPath = Path.Combine(root, "qpr.rtl.json");
            File.WriteAllText(irPath, ir);

            // 1. C++ 4-state (W64)
            string cpp4Sdk = Path.Combine(root, "cpp4");
            await RtlExternalSdk.ExportAsync(irPath, "qpr", cpp4Sdk, stateMode: RtlStateMode.FourState, target: RtlTargetLanguage.Cpp);
            string cpp2Sdk = Path.Combine(root, "cpp2");
            await RtlExternalSdk.ExportAsync(irPath, "qpr", cpp2Sdk, stateMode: RtlStateMode.TwoState, target: RtlTargetLanguage.Cpp);

            string cppHost = Path.Combine(root, "host.cpp");
            File.WriteAllText(cppHost, """
                #include "Vqpr.h"
                #include <iostream>
                int main(){
                 Vqpr m;
                 m.setPlusargs({"+ENABLE_FAST", "+SEED=2a"});
                 m.setRandomSeed(0x123456789ABCDEF0ULL);
                 const uint64_t inputs[] = {0u, 1u, 0x0010u, 0x0105u, 0x0100u};
                 m.clk=0; m.din=0; m.eval();
                 for(uint64_t x : inputs){
                  m.din=x; m.clk=1; m.eval();
                  std::cout<<uint64_t(m.pop)<<","<<uint64_t(m.oh)<<","<<uint64_t(m.oh0)<<","<<uint64_t(m.unk)<<","
                           <<uint64_t(m.lg)<<","<<uint64_t(m.has_flag)<<","<<uint64_t(m.vp_val)<<","<<uint64_t(m.vp_ok)<<","
                           <<uint64_t(m.rnd)<<","<<uint64_t(m.rrng)<<"\n";
                  m.clk=0; m.eval();
                 }
                 m.final();
                 return 0;
                }
                """);
            string b4 = Path.Combine(root, "b4");
            await Run("cmake", "-S", cpp4Sdk, "-B", b4, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DVELOSIM_HOST_SOURCE=" + cppHost);
            await Run("cmake", "--build", b4, "--parallel", "2");
            string outCpp4 = (await Run(Path.Combine(b4, "velosim_host"))).StandardOutput.Replace("\r\n", "\n").Trim();

            string b2 = Path.Combine(root, "b2");
            await Run("cmake", "-S", cpp2Sdk, "-B", b2, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DVELOSIM_HOST_SOURCE=" + cppHost);
            await Run("cmake", "--build", b2, "--parallel", "2");
            string outCpp2 = (await Run(Path.Combine(b2, "velosim_host"))).StandardOutput.Replace("\r\n", "\n").Trim();

            // 2. C# 4-state
            string csSdk = Path.Combine(root, "cs");
            await RtlExternalSdk.ExportAsync(irPath, "qpr", csSdk, stateMode: RtlStateMode.FourState, target: RtlTargetLanguage.CSharp);
            string csApp = Path.Combine(root, "csapp");
            Directory.CreateDirectory(csApp);
            File.WriteAllText(Path.Combine(csApp, "App.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
                  <ItemGroup><ProjectReference Include="{Path.Combine(csSdk, "Vqpr.csproj")}" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(csApp, "Program.cs"), """
                using VeloSim.Generated;
                var m = new Vqpr();
                m.SetPlusargs(["+ENABLE_FAST", "+SEED=2a"]);
                m.SetRandomSeed(0x123456789ABCDEF0UL);
                ulong[] inputs = [0UL, 1UL, 0x0010UL, 0x0105UL, 0x0100UL];
                m.clk = 0; m.din = 0; m.Eval();
                foreach (ulong x in inputs)
                {
                    m.din = x; m.clk = 1; m.Eval();
                    Console.WriteLine($"{m.pop},{m.oh},{m.oh0},{m.unk},{m.lg},{m.has_flag},{m.vp_val},{m.vp_ok},{m.rnd},{m.rrng}");
                    m.clk = 0; m.Eval();
                }
                m.Final();
                """);
            await RunIn(csApp, "dotnet", "build", "-c", "Release", "--nologo", "-warnaserror");
            string outCs = (await RunIn(csApp, "dotnet", "run", "--no-build", "-c", "Release")).StandardOutput.Replace("\r\n", "\n").Trim();

            // 3. Safe Rust 4-state
            string rsSdk = Path.Combine(root, "rs");
            await RtlExternalSdk.ExportAsync(irPath, "qpr", rsSdk, stateMode: RtlStateMode.FourState, target: RtlTargetLanguage.Rust);
            string rsTests = Path.Combine(rsSdk, "tests");
            Directory.CreateDirectory(rsTests);
            File.WriteAllText(Path.Combine(rsTests, "parity.rs"), $$"""
                use velosim_qpr::Vqpr;
                #[test]
                fn check_qpr() {
                    let mut m = Vqpr::new();
                    m.set_plusargs(["+ENABLE_FAST", "+SEED=2a"]);
                    m.set_random_seed(0x123456789ABCDEF0u64);
                    let inputs = [0u64, 1, 0x0010, 0x0105, 0x0100];
                    m.set_clk(0); m.set_din(0); m.eval();
                    let mut lines = Vec::new();
                    for x in inputs {
                        m.set_din(x); m.set_clk(1); m.eval();
                        lines.push(format!("{},{},{},{},{},{},{},{},{},{}",
                            m.pop(), m.oh(), m.oh0(), m.unk(), m.lg(), m.has_flag(), m.vp_val(), m.vp_ok(), m.rnd(), m.rrng()));
                        m.set_clk(0); m.eval();
                    }
                    m.final_model();
                    let expected = {{RtlDesign.CppStringLiteral(outCpp4)}};
                    assert_eq!(lines.join("\n"), expected);
                }
                """);
            await RunIn(rsSdk, "cargo", "test", "--release", "--quiet");

            Assert.That(outCpp2, Is.EqualTo(outCpp4));
            Assert.That(outCs, Is.EqualTo(outCpp4));
            var rows = outCpp4.Split('\n');
            Assert.That(rows.Length, Is.EqualTo(5));
            // Verify concrete expected bit-query & plusarg results on row 0 (din=0), row 2 (din=0x10), row 3 (din=0x105), row 4 (din=0x100):
            Assert.That(rows[0], Does.StartWith("0,0,1,0,0,1,42,1,"));
            Assert.That(rows[2], Does.StartWith("1,1,1,0,4,1,42,1,"));
            Assert.That(rows[3], Does.StartWith("3,0,0,0,9,1,42,1,"));
            Assert.That(rows[4], Does.StartWith("1,1,1,0,8,1,42,1,"));
            // Verify urandom_range output is within [10, 100] on every cycle.
            foreach (string row in rows)
            {
                ulong rrng = ulong.Parse(row.Split(',')[^1]);
                Assert.That(rrng, Is.InRange(10UL, 100UL));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task ForceReleaseAndToggleCoverageMergeWorkEndToEnd()
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-wave3-covmerge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string ir = """
            {
              "format":"velosim.rtl.v1",
              "clock":"clk",
              "signals":[
                {"name":"clk","width":1,"direction":"input"},
                {"name":"cmd","width":2,"direction":"input"},
                {"name":"din","width":8,"direction":"input"},
                {"name":"q","width":8,"direction":"output"}
              ],
              "memories":[],
              "continuous":[],
              "body":{
                "kind":"process",
                "events":[{"signal":"clk","edge":"posedge"}],
                "body":{
                  "kind":"case",
                  "value":{"kind":"ref","name":"cmd"},
                  "items":[
                    {"match":{"kind":"const","bits":"00"},"body":{"kind":"nba","target":{"kind":"ref","name":"q"},"value":{"kind":"ref","name":"din"}}},
                    {"match":{"kind":"const","bits":"01"},"body":{"kind":"force","target":"q","value":{"kind":"const","bits":"10100101"}}},
                    {"match":{"kind":"const","bits":"10"},"body":{"kind":"release","target":"q"}},
                    {"match":null,"body":{"kind":"nba","target":{"kind":"ref","name":"q"},"value":{"kind":"bitnot","args":[{"kind":"ref","name":"din"}]}}}
                  ]
                }
              }
            }
            """;
            string irPath = Path.Combine(root, "covm.rtl.json");
            File.WriteAllText(irPath, ir);
            string sdk = Path.Combine(root, "sdk");
            await RtlExternalSdk.ExportAsync(irPath, "covm", sdk, enableCoverage: true);

            string covA = Path.Combine(root, "cov_a.json");
            string covB = Path.Combine(root, "cov_b.json");
            string hostPath = Path.Combine(root, "host.cpp");
            File.WriteAllText(hostPath, $$"""
                #include "Vcovm.h"
                #include <iostream>
                int main(){
                 // Run A: toggles lower nibble (0x0F -> 0x00) and exercises arm 0, force (arm 1), release (arm 2).
                 {
                  Vcovm m;
                  m.clk=0; m.cmd=0; m.din=0x00; m.eval();
                  m.clk=1; m.eval(); m.clk=0; m.eval(); // q=0x00
                  m.din=0x0F; m.clk=1; m.eval(); if(uint64_t(m.q)!=0x0F) return 1;
                  m.clk=0; m.eval();
                  // IR force q = 0xA5
                  m.cmd=1; m.clk=1; m.eval(); if(!m.isForced(3) || uint64_t(m.q)!=0xA5) return 2;
                  m.clk=0; m.eval();
                  // Even while cmd=0 drives din=0x00, forced q stays 0xA5.
                  m.cmd=0; m.din=0x00; m.clk=1; m.eval(); if(uint64_t(m.q)!=0xA5) return 3;
                  m.clk=0; m.eval();
                  // IR release q, then normal drive restores din=0x00.
                  m.cmd=2; m.clk=1; m.eval(); if(m.isForced(3)) return 4;
                  m.clk=0; m.eval();
                  m.cmd=0; m.din=0x00; m.clk=1; m.eval(); if(uint64_t(m.q)!=0x00) return 5;
                  m.writeCoverageJson("{{covA.Replace("\\", "/")}}");
                  m.final();
                 }
                 // Run B: toggles upper nibble (0x00 -> 0xF0 -> 0x00) and exercises default arm (cmd=3).
                 {
                  Vcovm m;
                  m.clk=0; m.cmd=0; m.din=0x00; m.eval();
                  m.clk=1; m.eval(); m.clk=0; m.eval(); // q=0x00
                  m.cmd=3; m.din=0x0F; m.clk=1; m.eval(); if(uint64_t(m.q)!=0xF0) return 6;
                  m.clk=0; m.eval();
                  m.cmd=0; m.din=0x00; m.clk=1; m.eval(); if(uint64_t(m.q)!=0x00) return 7;
                  m.writeCoverageJson("{{covB.Replace("\\", "/")}}");
                  m.final();
                 }
                 std::cout<<"COVMERGE_OK\n";
                 return 0;
                }
                """);
            string build = Path.Combine(root, "build");
            await Run("cmake", "-S", sdk, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DVELOSIM_HOST_SOURCE=" + hostPath);
            await Run("cmake", "--build", build, "--parallel", "2");
            var run = await Run(Path.Combine(build, "velosim_host"));
            Assert.That(run.StandardOutput, Does.Contain("COVMERGE_OK"));

            string mergedPath = Path.Combine(root, "merged.json");
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            int rc = await ToolCli.RunAsync(["coverage-merge", mergedPath, covA, covB], stdout, stderr);
            Assert.That(rc, Is.EqualTo(0), stderr.ToString());

            using var doc = JsonDocument.Parse(File.ReadAllText(mergedPath));
            var r = doc.RootElement;
            Assert.That(r.GetProperty("schema").GetString(), Is.EqualTo("velosim.dut.coverage.v1"));
            Assert.That(r.GetProperty("model").GetString(), Is.EqualTo("covm"));
            Assert.That(r.GetProperty("merged_runs").GetInt32(), Is.EqualTo(2));
            Assert.That(r.GetProperty("total").GetInt32(), Is.EqualTo(4));
            Assert.That(r.GetProperty("covered").GetInt32(), Is.EqualTo(4));

            var qToggle = r.GetProperty("toggles").EnumerateArray().Single(t => t.GetProperty("signal").GetString() == "q");
            Assert.That(qToggle.GetProperty("width").GetInt32(), Is.EqualTo(8));
            Assert.That(qToggle.GetProperty("rise_hex").GetString(), Is.EqualTo("ff"));
            Assert.That(qToggle.GetProperty("fall_hex").GetString(), Is.EqualTo("ff"));
            Assert.That(qToggle.GetProperty("rise_hits").GetInt32(), Is.EqualTo(8));
            Assert.That(qToggle.GetProperty("fall_hits").GetInt32(), Is.EqualTo(8));

            // Mismatched model name must fail closed.
            string badCov = Path.Combine(root, "other.json");
            File.WriteAllText(badCov, File.ReadAllText(covA).Replace("\"covm\"", "\"other_model\""));
            Assert.Throws<InvalidOperationException>(() => RtlCoverageMerge.MergeFiles(Path.Combine(root, "fail.json"), [covA, badCov]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task CrossTargetCSharpAndRustCheckpointsAndCSharpGzipVcdWorkEndToEnd()
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-wave3-ckpt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string ir = """
            {
              "format":"velosim.rtl.v1",
              "clock":"clk",
              "signals":[
                {"name":"clk","width":1,"direction":"input"},
                {"name":"we","width":1,"direction":"input"},
                {"name":"addr","width":3,"direction":"input"},
                {"name":"wdata","width":16,"direction":"input"},
                {"name":"rdata","width":16,"direction":"output"},
                {"name":"cnt","width":16,"direction":"output"}
              ],
              "memories":[{"name":"ram","width":16,"depth":8,"lower":0}],
              "continuous":[
                {"target":{"kind":"ref","name":"rdata"},"value":{"kind":"index","name":"ram","index":{"kind":"ref","name":"addr"}}}
              ],
              "body":{
                "kind":"process",
                "events":[{"signal":"clk","edge":"posedge"}],
                "body":{
                  "kind":"block",
                  "items":[
                    {"kind":"nba","target":{"kind":"ref","name":"cnt"},"value":{"kind":"add","args":[{"kind":"ref","name":"cnt"},{"kind":"const","bits":"0000000000000001"}]}},
                    {"kind":"if","condition":{"kind":"ref","name":"we"},"then":{"kind":"nba","target":{"kind":"index","name":"ram","index":{"kind":"ref","name":"addr"}},"value":{"kind":"ref","name":"wdata"}},"else":{"kind":"block","items":[]}}
                  ]
                }
              }
            }
            """;
            string irPath = Path.Combine(root, "ck.rtl.json");
            File.WriteAllText(irPath, ir);

            // C# checkpoint + gzip VCD
            string csSdk = Path.Combine(root, "cs");
            await RtlExternalSdk.ExportAsync(irPath, "ck", csSdk, stateMode: RtlStateMode.TwoState, target: RtlTargetLanguage.CSharp);
            string csApp = Path.Combine(root, "csapp");
            Directory.CreateDirectory(csApp);
            string csGz = Path.Combine(root, "cs.vcd.gz");
            string csCkpt = Path.Combine(root, "cs.ckpt");
            File.WriteAllText(Path.Combine(csApp, "App.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
                  <ItemGroup><ProjectReference Include="{Path.Combine(csSdk, "Vck.csproj")}" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(csApp, "Program.cs"), $$"""
                using VeloSim.Generated;
                var m = new Vck();
                m.OpenVcdGz({{RtlDesign.CppStringLiteral(csGz)}}, "1ns");
                m.clk = 0; m.we = 1; m.addr = 2; m.wdata = 0xBEEF; m.Eval(); m.DumpVcd();
                m.TimeInc(5); m.clk = 1; m.Eval(); m.DumpVcd();
                m.TimeInc(5); m.clk = 0; m.we = 0; m.Eval(); m.DumpVcd();
                m.SaveCheckpoint({{RtlDesign.CppStringLiteral(csCkpt)}});
                // Advance 3 more cycles to record golden post-checkpoint state.
                for (int i = 0; i < 3; ++i) { m.TimeInc(5); m.clk = 1; m.Eval(); m.TimeInc(5); m.clk = 0; m.Eval(); }
                ulong goldenCnt = m.cnt, goldenRdata = m.rdata, goldenTime = m.Time;
                // Corrupt checkpoint copy must fail closed without mutating live state.
                string corrupt = {{RtlDesign.CppStringLiteral(csCkpt + ".bad")}};
                byte[] bytes = File.ReadAllBytes({{RtlDesign.CppStringLiteral(csCkpt)}});
                bytes[^1] ^= 0xFF;
                File.WriteAllBytes(corrupt, bytes);
                bool rejected = false;
                try { m.RestoreCheckpoint(corrupt); } catch (InvalidDataException) { rejected = true; }
                if (!rejected || m.cnt != goldenCnt) throw new Exception("corrupt checkpoint not rejected atomically");
                // Restore valid checkpoint and replay 3 cycles.
                m.RestoreCheckpoint({{RtlDesign.CppStringLiteral(csCkpt)}});
                for (int i = 0; i < 3; ++i) { m.TimeInc(5); m.clk = 1; m.Eval(); m.TimeInc(5); m.clk = 0; m.Eval(); }
                if (m.cnt != goldenCnt || m.rdata != goldenRdata || m.Time != goldenTime || m.rdata != 0xBEEF)
                    throw new Exception($"replay mismatch: cnt={m.cnt}/{goldenCnt} rdata={m.rdata:x}/{goldenRdata:x}");
                m.Final();
                Console.WriteLine("CS_CKPT_VCDGZ_OK");
                """);
            var csRun = await RunIn(csApp, "dotnet", "run", "-c", "Release", "--nologo");
            Assert.That(csRun.StandardOutput, Does.Contain("CS_CKPT_VCDGZ_OK"));
            string csDecompressed = DecompressGzipText(csGz);
            Assert.That(csDecompressed, Does.Contain("$version VeloSim $end"));
            Assert.That(csDecompressed, Does.Contain("$dumpvars"));

            // Safe Rust checkpoint + atomic corruption rejection
            string rsSdk = Path.Combine(root, "rs");
            await RtlExternalSdk.ExportAsync(irPath, "ck", rsSdk, stateMode: RtlStateMode.FourState, target: RtlTargetLanguage.Rust);
            string rsTests = Path.Combine(rsSdk, "tests");
            Directory.CreateDirectory(rsTests);
            string rsCkpt = Path.Combine(root, "rs.ckpt");
            File.WriteAllText(Path.Combine(rsTests, "ckpt.rs"), $$"""
                use velosim_ck::Vck;
                #[test]
                fn check_rust_checkpoint() {
                    let mut m = Vck::new();
                    m.set_clk(0); m.set_we(1); m.set_addr(3); m.set_wdata(0xCAFE); m.eval();
                    m.time_inc(5); m.set_clk(1); m.eval();
                    m.time_inc(5); m.set_clk(0); m.set_we(0); m.eval();
                    let ckpt = {{RtlDesign.CppStringLiteral(rsCkpt)}};
                    m.save_checkpoint(ckpt).unwrap();
                    for _ in 0..3 { m.time_inc(5); m.set_clk(1); m.eval(); m.time_inc(5); m.set_clk(0); m.eval(); }
                    let (g_rdata, g_time) = (m.rdata(), m.time());
                    let mut bad = std::fs::read(ckpt).unwrap();
                    let last = bad.len() - 1;
                    bad[last] ^= 0xFF;
                    let bad_path = {{RtlDesign.CppStringLiteral(rsCkpt + ".bad")}};
                    std::fs::write(bad_path, bad).unwrap();
                    assert!(m.restore_checkpoint(bad_path).is_err());
                    assert_eq!(m.time(), g_time);
                    m.restore_checkpoint(ckpt).unwrap();
                    for _ in 0..3 { m.time_inc(5); m.set_clk(1); m.eval(); m.time_inc(5); m.set_clk(0); m.eval(); }
                    assert_eq!(m.rdata(), g_rdata);
                    assert_eq!(m.rdata(), 0xCAFE);
                    assert_eq!(m.time(), g_time);
                }
                """);
            await RunIn(rsSdk, "cargo", "test", "--release", "--quiet");
        }
        finally { Directory.Delete(root, true); }
    }
}
