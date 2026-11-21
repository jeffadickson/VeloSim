using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
namespace VeloSim.Tests;

public sealed class RtlVerilatorGapsWave4Tests
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
    public async Task ResolvedNetsInsideRandomAndFatalMatchAcrossCppCSharpAndRust()
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-wave4-res-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string ir = """
            {
              "format":"velosim.rtl.v1",
              "clock":"clk",
              "signals":[
                {"name":"clk","width":1,"direction":"input"},
                {"name":"drv_a","width":4,"direction":"input"},
                {"name":"drv_b","width":4,"direction":"input"},
                {"name":"sel","width":4,"direction":"input"},
                {"name":"trig_fatal","width":1,"direction":"input"},
                {"name":"w_wire","width":4,"direction":"output"},
                {"name":"w_wand","width":4,"direction":"output"},
                {"name":"w_wor","width":4,"direction":"output"},
                {"name":"w_tri0","width":4,"direction":"output"},
                {"name":"w_tri1","width":4,"direction":"output"},
                {"name":"in_hit","width":1,"direction":"output"},
                {"name":"rnd_out","width":32,"direction":"output"},
                {"name":"cnt","width":8,"direction":"output"}
              ],
              "memories":[],
              "continuous":[
                {"target":{"kind":"ref","name":"w_wire"},"value":{"kind":"resolve","net":"wire","args":[{"kind":"ref","name":"drv_a"},{"kind":"ref","name":"drv_b"}]}},
                {"target":{"kind":"ref","name":"w_wand"},"value":{"kind":"resolve","net":"wand","args":[{"kind":"ref","name":"drv_a"},{"kind":"ref","name":"drv_b"}]}},
                {"target":{"kind":"ref","name":"w_wor"},"value":{"kind":"resolve","net":"wor","args":[{"kind":"ref","name":"drv_a"},{"kind":"ref","name":"drv_b"}]}},
                {"target":{"kind":"ref","name":"w_tri0"},"value":{"kind":"resolve","net":"tri0","args":[{"kind":"ref","name":"drv_a"},{"kind":"ref","name":"drv_b"}]}},
                {"target":{"kind":"ref","name":"w_tri1"},"value":{"kind":"resolve","net":"tri1","args":[{"kind":"ref","name":"drv_a"},{"kind":"ref","name":"drv_b"}]}},
                {"target":{"kind":"ref","name":"in_hit"},"value":{
                  "kind":"inside",
                  "value":{"kind":"ref","name":"sel"},
                  "items":[
                    {"kind":"const","bits":"0001"},
                    {"lo":{"kind":"const","bits":"0100"},"hi":{"kind":"const","bits":"0110"}},
                    {"kind":"const","bits":"10xx"}
                  ]
                }}
              ],
              "body":{
                "kind":"process",
                "events":[{"signal":"clk","edge":"posedge"}],
                "body":{
                  "kind":"block",
                  "items":[
                    {"kind":"if","condition":{"kind":"ref","name":"trig_fatal"},
                     "then":{"kind":"fatal","code":42,"format":"boom"},
                     "else":{"kind":"block","items":[
                       {"kind":"nba","target":{"kind":"ref","name":"rnd_out"},"value":{"kind":"random"}},
                       {"kind":"nba","target":{"kind":"ref","name":"cnt"},"value":{"kind":"add","args":[{"kind":"ref","name":"cnt"},{"kind":"const","bits":"00000001"}]}}
                     ]}}
                  ]
                }
              }
            }
            """;
            string irPath = Path.Combine(root, "res.rtl.json");
            File.WriteAllText(irPath, ir);

            // 1. C++ 4-state (W64)
            string cpp4Sdk = Path.Combine(root, "cpp4");
            await RtlExternalSdk.ExportAsync(irPath, "res", cpp4Sdk, stateMode: RtlStateMode.FourState, target: RtlTargetLanguage.Cpp);
            string cppHost = Path.Combine(root, "host.cpp");
            File.WriteAllText(cppHost, """
                #include "Vres.h"
                #include <iostream>
                #include <stdexcept>
                int main(){
                 Vres m;
                 m.setRandomSeed(0x0102030405060708ULL);
                 m.clk=0; m.trig_fatal=0; m.drv_a.bits("01xz"); m.drv_b.bits("10zz"); m.sel=1; m.eval();
                 // Initialize cnt to 0 via one clock with known inputs? cnt starts unknown in 4-state, so check bits directly.
                 const char* da[] = {"01xz","0011","zzzz","1010"};
                 const char* db[] = {"10zz","0101","zzzz","zzzz"};
                 const char* sv[] = {"0001","0101","0011","1011"};
                 for(int i=0;i<4;++i){
                  m.drv_a.bits(da[i]); m.drv_b.bits(db[i]); m.sel.bits(sv[i]);
                  m.clk=1; m.eval();
                  std::cout<<m.w_wire.bits()<<","<<m.w_wand.bits()<<","<<m.w_wor.bits()<<","
                           <<m.w_tri0.bits()<<","<<m.w_tri1.bits()<<","<<m.in_hit.bits()<<","
                           <<uint64_t(m.rnd_out)<<"\n";
                  m.clk=0; m.eval();
                 }
                 // Trigger $fatal(42, "boom") and verify exitCode + gotFinish + rollback.
                 uint64_t beforeRnd = uint64_t(m.rnd_out);
                 m.trig_fatal=1; m.clk=1;
                 bool caught=false;
                 try{ m.eval(); } catch(const std::runtime_error&){ caught=true; }
                 if(!caught || !m.gotFinish() || m.exitCode()!=42 || uint64_t(m.rnd_out)!=beforeRnd) return 20;
                 std::cout<<"FATAL_OK="<<m.exitCode()<<"\n";
                 m.final();
                 return 0;
                }
                """);
            string b4 = Path.Combine(root, "b4");
            await Run("cmake", "-S", cpp4Sdk, "-B", b4, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DVELOSIM_HOST_SOURCE=" + cppHost);
            await Run("cmake", "--build", b4, "--parallel", "2");
            string outCpp4 = (await Run(Path.Combine(b4, "velosim_host"))).StandardOutput.Replace("\r\n", "\n").Trim();

            // 2. C# 4-state (-warnaserror)
            string csSdk = Path.Combine(root, "cs");
            await RtlExternalSdk.ExportAsync(irPath, "res", csSdk, stateMode: RtlStateMode.FourState, target: RtlTargetLanguage.CSharp);
            string csApp = Path.Combine(root, "csapp");
            Directory.CreateDirectory(csApp);
            File.WriteAllText(Path.Combine(csApp, "App.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
                  <ItemGroup><ProjectReference Include="{Path.Combine(csSdk, "Vres.csproj")}" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(csApp, "Program.cs"), """
                using VeloSim.Generated;
                var m = new Vres();
                m.SetRandomSeed(0x0102030405060708UL);
                m.clk = 0; m.trig_fatal = 0; m.Set_drv_a_Bits("01xz"); m.Set_drv_b_Bits("10zz"); m.sel = 1; m.Eval();
                string[] da = ["01xz", "0011", "zzzz", "1010"];
                string[] db = ["10zz", "0101", "zzzz", "zzzz"];
                string[] sv = ["0001", "0101", "0011", "1011"];
                for (int i = 0; i < 4; ++i)
                {
                    m.Set_drv_a_Bits(da[i]); m.Set_drv_b_Bits(db[i]); m.Set_sel_Bits(sv[i]);
                    m.clk = 1; m.Eval();
                    Console.WriteLine($"{m.Get_w_wire_Bits()},{m.Get_w_wand_Bits()},{m.Get_w_wor_Bits()},{m.Get_w_tri0_Bits()},{m.Get_w_tri1_Bits()},{m.Get_in_hit_Bits()},{m.rnd_out}");
                    m.clk = 0; m.Eval();
                }
                ulong beforeRnd = m.rnd_out;
                m.trig_fatal = 1; m.clk = 1;
                bool caught = false;
                try { m.Eval(); } catch (InvalidOperationException) { caught = true; }
                if (!caught || !m.GotFinish || m.ExitCode != 42 || m.rnd_out != beforeRnd) Environment.Exit(20);
                Console.WriteLine($"FATAL_OK={m.ExitCode}");
                m.Final();
                """);
            await RunIn(csApp, "dotnet", "build", "-c", "Release", "--nologo", "-warnaserror");
            string outCs = (await RunIn(csApp, "dotnet", "run", "--no-build", "-c", "Release")).StandardOutput.Replace("\r\n", "\n").Trim();

            // 3. Safe Rust 4-state (#![forbid(unsafe_code)])
            string rsSdk = Path.Combine(root, "rs");
            await RtlExternalSdk.ExportAsync(irPath, "res", rsSdk, stateMode: RtlStateMode.FourState, target: RtlTargetLanguage.Rust);
            string rsTests = Path.Combine(rsSdk, "tests");
            Directory.CreateDirectory(rsTests);
            File.WriteAllText(Path.Combine(rsTests, "wave4_res.rs"), $$"""
                use velosim_res::Vres;
                #[test]
                fn check_resolved_inside_random_fatal() {
                    let mut m = Vres::new();
                    m.set_random_seed(0x0102030405060708u64);
                    m.set_clk(0); m.set_trig_fatal(0);
                    m.set_drv_a_bits("01xz").unwrap(); m.set_drv_b_bits("10zz").unwrap(); m.set_sel(1);
                    m.eval();
                    let da = ["01xz","0011","zzzz","1010"];
                    let db = ["10zz","0101","zzzz","zzzz"];
                    let sv = ["0001","0101","0011","1011"];
                    let mut lines = Vec::new();
                    for i in 0..4 {
                        m.set_drv_a_bits(da[i]).unwrap();
                        m.set_drv_b_bits(db[i]).unwrap();
                        m.set_sel_bits(sv[i]).unwrap();
                        m.set_clk(1); m.eval();
                        lines.push(format!("{},{},{},{},{},{},{}",
                            m.w_wire_bits(), m.w_wand_bits(), m.w_wor_bits(),
                            m.w_tri0_bits(), m.w_tri1_bits(), m.in_hit_bits(), m.rnd_out()));
                        m.set_clk(0); m.eval();
                    }
                    let before_rnd = m.rnd_out();
                    m.set_trig_fatal(1); m.set_clk(1);
                    let err = m.try_eval();
                    assert!(err.is_err());
                    assert!(m.got_finish());
                    assert_eq!(m.exit_code(), 42);
                    assert_eq!(m.rnd_out(), before_rnd);
                    lines.push(format!("FATAL_OK={}", m.exit_code()));
                    m.final_model();
                    let expected = {{RtlDesign.CppStringLiteral(outCpp4)}};
                    assert_eq!(lines.join("\n"), expected);
                }
                """);
            await RunIn(rsSdk, "cargo", "test", "--release", "--quiet");

            Assert.That(outCs, Is.EqualTo(outCpp4));
            var rows = outCpp4.Split('\n');
            Assert.That(rows.Length, Is.EqualTo(5));
            // Row 0: a=01xz, b=10zz -> wire=xxxz, wand=00xz, wor=11xz, tri0=xxx0, tri1=xxx1, inside(0001)=1
            Assert.That(rows[0], Does.StartWith("xxxz,00xz,11xz,xxx0,xxx1,1,"));
            // Row 1: a=0011, b=0101 -> wire=0xx1, wand=0001, wor=0111, tri0=0xx1, tri1=0xx1, inside(0101 in 4..6)=1
            Assert.That(rows[1], Does.StartWith("0xx1,0001,0111,0xx1,0xx1,1,"));
            // Row 2: a=zzzz, b=zzzz -> wire=zzzz, wand=zzzz, wor=zzzz, tri0=0000, tri1=1111, inside(0011)=0
            Assert.That(rows[2], Does.StartWith("zzzz,zzzz,zzzz,0000,1111,0,"));
            // Row 3: a=1010, b=zzzz -> wire=1010, wand=1010, wor=1010, tri0=1010, tri1=1010, inside(1011 ==? 10xx)=1
            Assert.That(rows[3], Does.StartWith("1010,1010,1010,1010,1010,1,"));
            Assert.That(rows[4], Is.EqualTo("FATAL_OK=42"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task ConcurrentSvaPropertiesAndPastFunctionsWorkAcrossCppCSharpAndRust()
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-wave4-sva-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string ir = """
            {
              "format":"velosim.rtl.v1",
              "clock":"clk",
              "signals":[
                {"name":"clk","width":1,"direction":"input"},
                {"name":"rst","width":1,"direction":"input"},
                {"name":"req","width":1,"direction":"input"},
                {"name":"ack","width":1,"direction":"input"},
                {"name":"gnt","width":1,"direction":"input"},
                {"name":"done","width":1,"direction":"input"},
                {"name":"data","width":8,"direction":"input"},
                {"name":"mode","width":2,"direction":"input"},
                {"name":"q","width":8,"direction":"output"}
              ],
              "memories":[],
              "continuous":[],
              "properties":[
                {"name":"p_imm","kind":"assert","clock":"clk","edge":"posedge","disable_iff":{"kind":"ref","name":"rst"},
                 "antecedent":{"kind":"ref","name":"req"},"delay":0,"consequent":{"kind":"ref","name":"ack"},"message":"req requires ack"},
                {"name":"p_next","kind":"assert","clock":"clk","edge":"posedge","disable_iff":{"kind":"ref","name":"rst"},
                 "antecedent":{"kind":"ref","name":"req"},"delay":1,"consequent":{"kind":"ref","name":"gnt"},"message":"req requires next-cycle gnt"},
                {"name":"p_cov2","kind":"cover","clock":"clk","edge":"posedge","disable_iff":{"kind":"ref","name":"rst"},
                 "antecedent":{"kind":"ref","name":"req"},"delay":2,"consequent":{"kind":"ref","name":"done"}},
                {"name":"p_past","kind":"cover","clock":"clk","edge":"posedge","disable_iff":{"kind":"ref","name":"rst"},
                 "antecedent":{"kind":"rose","signal":"req"},"delay":0,
                 "consequent":{"kind":"and","args":[
                   {"kind":"eq","args":[{"kind":"past","signal":"data","ticks":1},{"kind":"const","bits":"00001010"}]},
                   {"kind":"stable","signal":"mode"}
                 ]}}
              ],
              "body":{
                "kind":"process",
                "events":[{"signal":"clk","edge":"posedge"}],
                "body":{"kind":"nba","target":{"kind":"ref","name":"q"},"value":{"kind":"ref","name":"data"}}
              }
            }
            """;
            string irPath = Path.Combine(root, "sva.rtl.json");
            File.WriteAllText(irPath, ir);

            // 1. C++ 4-state
            string cppSdk = Path.Combine(root, "cpp");
            await RtlExternalSdk.ExportAsync(irPath, "sva", cppSdk, stateMode: RtlStateMode.FourState, target: RtlTargetLanguage.Cpp);
            string cppHost = Path.Combine(root, "host.cpp");
            File.WriteAllText(cppHost, """
                #include "Vsva.h"
                #include <iostream>
                #include <stdexcept>
                int main(){
                 Vsva m;
                 auto tick=[&](int rst,int req,int ack,int gnt,int done,int data,int mode){
                  m.rst=rst; m.req=req; m.ack=ack; m.gnt=gnt; m.done=done; m.data=data; m.mode=mode;
                  m.clk=1; m.eval(); m.clk=0; m.eval();
                 };
                 m.clk=0; m.rst=1; m.req=0; m.ack=0; m.gnt=0; m.done=0; m.data=0; m.mode=1; m.eval();
                 // Cycle 1: in reset (disabled)
                 tick(1, 1, 0, 0, 0, 10, 1);
                 // Cycle 2: out of reset, req=0 (vacuous for p_imm/p_next), data=10, mode=1
                 tick(0, 0, 0, 0, 0, 10, 1);
                 // Cycle 3: req rises 0->1, ack=1 -> p_imm passes, p_past sees $rose(req) && $past(data)==10 && $stable(mode) -> passes!
                 tick(0, 1, 1, 0, 0, 20, 1);
                 // Cycle 4: req=0, gnt=1 -> p_next passes (from cycle 3 req)
                 tick(0, 0, 0, 1, 0, 30, 1);
                 // Cycle 5: req=0, done=1 -> p_cov2 passes (delay 2 from cycle 3 req)
                 tick(0, 0, 0, 0, 1, 40, 1);
                 // Cycle 6: intentional failure on p_imm (req=1, ack=0) -> must throw and rollback q!
                 uint64_t qBefore = uint64_t(m.q);
                 bool failed=false;
                 try{ tick(0, 1, 0, 0, 0, 99, 1); } catch(const std::runtime_error&){ failed=true; }
                 if(!failed || uint64_t(m.q)!=qBefore) return 30;
                 for(const auto& st : m.propertyStats())
                  std::cout<<st.name<<":"<<st.passes<<"/"<<st.failures<<"/"<<st.vacuous<<"\n";
                 m.final();
                 return 0;
                }
                """);
            string bCpp = Path.Combine(root, "bcpp");
            await Run("cmake", "-S", cppSdk, "-B", bCpp, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DVELOSIM_HOST_SOURCE=" + cppHost);
            await Run("cmake", "--build", bCpp, "--parallel", "2");
            string outCpp = (await Run(Path.Combine(bCpp, "velosim_host"))).StandardOutput.Replace("\r\n", "\n").Trim();

            // 2. C# 4-state (-warnaserror)
            string csSdk = Path.Combine(root, "cs");
            await RtlExternalSdk.ExportAsync(irPath, "sva", csSdk, stateMode: RtlStateMode.FourState, target: RtlTargetLanguage.CSharp);
            string csApp = Path.Combine(root, "csapp");
            Directory.CreateDirectory(csApp);
            File.WriteAllText(Path.Combine(csApp, "App.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
                  <ItemGroup><ProjectReference Include="{Path.Combine(csSdk, "Vsva.csproj")}" /></ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(csApp, "Program.cs"), """
                using VeloSim.Generated;
                var m = new Vsva();
                void Tick(ulong rst, ulong req, ulong ack, ulong gnt, ulong done, ulong data, ulong mode)
                {
                    m.rst = rst; m.req = req; m.ack = ack; m.gnt = gnt; m.done = done; m.data = data; m.mode = mode;
                    m.clk = 1; m.Eval(); m.clk = 0; m.Eval();
                }
                m.clk = 0; m.rst = 1; m.req = 0; m.ack = 0; m.gnt = 0; m.done = 0; m.data = 0; m.mode = 1; m.Eval();
                Tick(1, 1, 0, 0, 0, 10, 1);
                Tick(0, 0, 0, 0, 0, 10, 1);
                Tick(0, 1, 1, 0, 0, 20, 1);
                Tick(0, 0, 0, 1, 0, 30, 1);
                Tick(0, 0, 0, 0, 1, 40, 1);
                ulong qBefore = m.q;
                bool failed = false;
                try { Tick(0, 1, 0, 0, 0, 99, 1); } catch (InvalidOperationException) { failed = true; }
                if (!failed || m.q != qBefore) Environment.Exit(30);
                string[] names = ["p_imm", "p_next", "p_cov2", "p_past"];
                for (int i = 0; i < names.Length; ++i)
                    Console.WriteLine($"{names[i]}:{m.PropertyPasses[i]}/{m.PropertyFailures[i]}/{m.PropertyVacuous[i]}");
                m.Final();
                """);
            await RunIn(csApp, "dotnet", "build", "-c", "Release", "--nologo", "-warnaserror");
            string outCs = (await RunIn(csApp, "dotnet", "run", "--no-build", "-c", "Release")).StandardOutput.Replace("\r\n", "\n").Trim();

            // 3. Safe Rust 4-state
            string rsSdk = Path.Combine(root, "rs");
            await RtlExternalSdk.ExportAsync(irPath, "sva", rsSdk, stateMode: RtlStateMode.FourState, target: RtlTargetLanguage.Rust);
            string rsTests = Path.Combine(rsSdk, "tests");
            Directory.CreateDirectory(rsTests);
            File.WriteAllText(Path.Combine(rsTests, "wave4_sva.rs"), $$"""
                use velosim_sva::Vsva;
                #[test]
                fn check_sva_properties() {
                    let mut m = Vsva::new();
                    let tick = |m: &mut Vsva, rst: u64, req: u64, ack: u64, gnt: u64, done: u64, data: u64, mode: u64| -> Result<(), &'static str> {
                        m.set_rst(rst); m.set_req(req); m.set_ack(ack); m.set_gnt(gnt); m.set_done(done); m.set_data(data); m.set_mode(mode);
                        m.set_clk(1); m.try_eval()?; m.set_clk(0); m.try_eval()?;
                        Ok(())
                    };
                    m.set_clk(0); m.set_rst(1); m.set_req(0); m.set_ack(0); m.set_gnt(0); m.set_done(0); m.set_data(0); m.set_mode(1); m.eval();
                    tick(&mut m, 1, 1, 0, 0, 0, 10, 1).unwrap();
                    tick(&mut m, 0, 0, 0, 0, 0, 10, 1).unwrap();
                    tick(&mut m, 0, 1, 1, 0, 0, 20, 1).unwrap();
                    tick(&mut m, 0, 0, 0, 1, 0, 30, 1).unwrap();
                    tick(&mut m, 0, 0, 0, 0, 1, 40, 1).unwrap();
                    let q_before = m.q();
                    assert!(tick(&mut m, 0, 1, 0, 0, 0, 99, 1).is_err());
                    assert_eq!(m.q(), q_before);
                    let names = ["p_imm", "p_next", "p_cov2", "p_past"];
                    let mut lines = Vec::new();
                    for i in 0..names.len() {
                        lines.push(format!("{}:{}/{}/{}", names[i], m.property_passes()[i], m.property_failures()[i], m.property_vacuous()[i]));
                    }
                    m.final_model();
                    let expected = {{RtlDesign.CppStringLiteral(outCpp)}};
                    assert_eq!(lines.join("\n"), expected);
                }
                """);
            await RunIn(rsSdk, "cargo", "test", "--release", "--quiet");

            Assert.That(outCs, Is.EqualTo(outCpp));
            Assert.That(outCpp, Is.EqualTo("p_imm:1/1/3\np_next:1/0/3\np_cov2:1/0/0\np_past:1/0/0"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task FunctionalCovergroupsCrossCoverageAndMergeWorkEndToEnd()
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-wave4-cg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string ir = """
            {
              "format":"velosim.rtl.v1",
              "clock":"clk",
              "signals":[
                {"name":"clk","width":1,"direction":"input"},
                {"name":"valid","width":1,"direction":"input"},
                {"name":"op","width":3,"direction":"input"},
                {"name":"sz","width":1,"direction":"input"},
                {"name":"seen","width":8,"direction":"output"}
              ],
              "memories":[],
              "continuous":[],
              "covergroups":[
                {
                  "name":"cg_bus",
                  "coverpoints":[
                    {
                      "name":"cp_op",
                      "value":{"kind":"ref","name":"op"},
                      "bins":[
                        {"name":"read","value":0},
                        {"name":"write","value":1},
                        {"name":"burst","lo":2,"hi":5},
                        {"name":"nop","value":7,"ignore":true},
                        {"name":"bad","value":6,"illegal":true},
                        {"name":"other","default":true}
                      ]
                    },
                    {
                      "name":"cp_sz",
                      "value":{"kind":"ref","name":"sz"},
                      "bins":[
                        {"name":"byte","value":0},
                        {"name":"word","value":1}
                      ]
                    }
                  ],
                  "crosses":[
                    {"name":"cx_op_sz","a":"cp_op","b":"cp_sz"}
                  ]
                }
              ],
              "properties":[
                {"name":"p_valid_not_bad","kind":"assert","clock":"clk","edge":"posedge",
                 "antecedent":{"kind":"ref","name":"valid"},"delay":0,
                 "consequent":{"kind":"neq","args":[{"kind":"ref","name":"op"},{"kind":"const","bits":"110"}]}}
              ],
              "body":{
                "kind":"process",
                "events":[{"signal":"clk","edge":"posedge"}],
                "body":{
                  "kind":"if","condition":{"kind":"ref","name":"valid"},
                  "then":{
                    "kind":"block",
                    "items":[
                      {"kind":"sample_covergroup","name":"cg_bus"},
                      {"kind":"nba","target":{"kind":"ref","name":"seen"},"value":{"kind":"add","args":[{"kind":"ref","name":"seen"},{"kind":"const","bits":"00000001"}]}}
                    ]
                  },
                  "else":{"kind":"block","items":[]}
                }
              }
            }
            """;
            string irPath = Path.Combine(root, "cg.rtl.json");
            File.WriteAllText(irPath, ir);
            string sdk = Path.Combine(root, "sdk");
            await RtlExternalSdk.ExportAsync(irPath, "cg", sdk, enableCoverage: true);

            string cov1 = Path.Combine(root, "cov1.json");
            string cov2 = Path.Combine(root, "cov2.json");
            string hostPath = Path.Combine(root, "host.cpp");
            File.WriteAllText(hostPath, $$"""
                #include "Vcg.h"
                #include <iostream>
                #include <stdexcept>
                int main(){
                 // Run 1: op=0/sz=0 (read x byte), op=3/sz=1 (burst x word), op=7/sz=0 (ignore nop)
                 Vcg m1;
                 auto tick=[](Vcg& m, int v, int op, int sz){ m.valid=v; m.op=op; m.sz=sz; m.clk=1; m.eval(); m.clk=0; m.eval(); };
                 m1.clk=0; m1.valid=0; m1.op=0; m1.sz=0; m1.eval();
                 tick(m1, 1, 0, 0);
                 tick(m1, 1, 3, 1);
                 tick(m1, 1, 7, 0);
                 tick(m1, 0, 0, 0);
                 m1.writeCoverageJson("{{cov1.Replace("\\", "/")}}");
                 m1.final();

                 // Run 2: op=1/sz=1 (write x word), op=4/sz=1 (burst x word), then illegal op=6 must fail closed!
                 Vcg m2;
                 m2.clk=0; m2.valid=0; m2.op=0; m2.sz=0; m2.eval();
                 tick(m2, 1, 1, 1);
                 tick(m2, 1, 4, 1);
                 m2.writeCoverageJson("{{cov2.Replace("\\", "/")}}");
                 bool rejected=false;
                 try{ tick(m2, 1, 6, 0); } catch(const std::runtime_error&){ rejected=true; }
                 if(!rejected) return 40;
                 m2.final();
                 std::cout<<"CG_OK\n";
                 return 0;
                }
                """);
            string build = Path.Combine(root, "build");
            await Run("cmake", "-S", sdk, "-B", build, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DVELOSIM_HOST_SOURCE=" + hostPath);
            await Run("cmake", "--build", build, "--parallel", "2");
            var run = await Run(Path.Combine(build, "velosim_host"));
            Assert.That(run.StandardOutput, Does.Contain("CG_OK"));

            string mergedPath = Path.Combine(root, "merged.json");
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            int cliExit = await ToolCli.RunAsync(["coverage-merge", mergedPath, cov1, cov2], stdout, stderr);
            Assert.That(cliExit, Is.EqualTo(0), stderr.ToString());

            using var doc = JsonDocument.Parse(File.ReadAllText(mergedPath));
            var r = doc.RootElement;
            Assert.That(r.GetProperty("schema").GetString(), Is.EqualTo("velosim.dut.coverage.v1"));
            var cgs = r.GetProperty("covergroups").EnumerateArray().ToArray();
            Assert.That(cgs.Length, Is.EqualTo(1));
            Assert.That(cgs[0].GetProperty("name").GetString(), Is.EqualTo("cg_bus"));
            Assert.That(cgs[0].GetProperty("samples").GetUInt64(), Is.EqualTo(5UL));

            var cps = cgs[0].GetProperty("coverpoints").EnumerateArray().ToArray();
            var cpOpBins = cps[0].GetProperty("bins").EnumerateArray().ToDictionary(b => b.GetProperty("name").GetString()!, b => b.GetProperty("hits").GetUInt64());
            Assert.That(cpOpBins["read"], Is.EqualTo(1UL));
            Assert.That(cpOpBins["write"], Is.EqualTo(1UL));
            Assert.That(cpOpBins["burst"], Is.EqualTo(2UL));
            Assert.That(cpOpBins["nop"], Is.EqualTo(0UL)); // ignore_bins are ignored per IEEE 1800 §19.5.4
            Assert.That(cpOpBins["bad"], Is.EqualTo(0UL));

            var cxBins = cgs[0].GetProperty("crosses")[0].GetProperty("bins").EnumerateArray()
                .ToDictionary(b => $"{b.GetProperty("a").GetString()},{b.GetProperty("b").GetString()}", b => b.GetProperty("hits").GetUInt64());
            Assert.That(cxBins["read,byte"], Is.EqualTo(1UL));
            Assert.That(cxBins["write,word"], Is.EqualTo(1UL));
            Assert.That(cxBins["burst,word"], Is.EqualTo(2UL));
            Assert.That(cxBins["nop,byte"], Is.EqualTo(0UL));

            var props = r.GetProperty("properties").EnumerateArray().ToArray();
            Assert.That(props.Length, Is.EqualTo(1));
            Assert.That(props[0].GetProperty("passes").GetUInt64(), Is.EqualTo(5UL));
            Assert.That(props[0].GetProperty("failures").GetUInt64(), Is.EqualTo(0UL));
            Assert.That(props[0].GetProperty("vacuous").GetUInt64(), Is.EqualTo(1UL));

            // Fail-closed merge check on covergroup mismatch
            string badCov = Path.Combine(root, "bad_cov.json");
            File.WriteAllText(badCov, File.ReadAllText(cov2).Replace("\"cg_bus\"", "\"cg_other\""));
            Assert.Throws<InvalidOperationException>(() => RtlCoverageMerge.MergeFiles(Path.Combine(root, "fail.json"), [cov1, badCov]));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public async Task ParallelDagSettleMatchesSingleThreadAndRustGzipVcdWorksEndToEnd()
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-wave4-par-rsgz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Part A: Multi-level wide combinational DAG (16 parallel lanes x 2 levels + sequential checksum).
            var sigs = new List<string>
            {
                """{"name":"clk","width":1,"direction":"input"}""",
                """{"name":"seed","width":32,"direction":"input"}""",
                """{"name":"acc","width":32,"direction":"output"}"""
            };
            var cont = new List<string>();
            for (int i = 0; i < 16; ++i)
            {
                sigs.Add($$"""{"name":"l0_{{i}}","width":32,"direction":"internal"}""");
                sigs.Add($$"""{"name":"l1_{{i}}","width":32,"direction":"internal"}""");
                string k = Convert.ToString(0x101 + i * 17, 2).PadLeft(32, '0');
                cont.Add($$$"""{"target":{"kind":"ref","name":"l0_{{{i}}}"},"value":{"kind":"bitxor","args":[{"kind":"add","args":[{"kind":"ref","name":"seed"},{"kind":"const","bits":"{{{k}}}"}]},{"kind":"ref","name":"acc"}]}}""");
                cont.Add($$$"""{"target":{"kind":"ref","name":"l1_{{{i}}}"},"value":{"kind":"add","args":[{"kind":"ref","name":"l0_{{{i}}}"},{"kind":"shl","args":[{"kind":"ref","name":"l0_{{{i}}}"},{"kind":"const","bits":"00010"}]}]}}""");
            }
            string sumExpr = """{"kind":"ref","name":"l1_0"}""";
            for (int i = 1; i < 16; ++i)
                sumExpr = $$$"""{"kind":"bitxor","args":[{{{sumExpr}}},{"kind":"ref","name":"l1_{{{i}}}"}]}""";

            string dagIr = $$"""
            {
              "format":"velosim.rtl.v1",
              "clock":"clk",
              "signals":[{{string.Join(",", sigs)}}],
              "memories":[],
              "continuous":[{{string.Join(",", cont)}}],
              "body":{
                "kind":"process",
                "events":[{"signal":"clk","edge":"posedge"}],
                "body":{"kind":"nba","target":{"kind":"ref","name":"acc"},"value":{{sumExpr}}}
              }
            }
            """;
            string dagIrPath = Path.Combine(root, "dag.rtl.json");
            File.WriteAllText(dagIrPath, dagIr);

            // Reject --threads > 1 on C# and Rust fail-closed.
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await RtlExternalSdk.ExportAsync(dagIrPath, "dag", Path.Combine(root, "bad_cs"), target: RtlTargetLanguage.CSharp, threads: 4));
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await RtlExternalSdk.ExportAsync(dagIrPath, "dag", Path.Combine(root, "bad_rs"), target: RtlTargetLanguage.Rust, threads: 4));

            string sdk1 = Path.Combine(root, "sdk1");
            string sdk4 = Path.Combine(root, "sdk4");
            string sdk4Two = Path.Combine(root, "sdk4_2s");
            await RtlExternalSdk.ExportAsync(dagIrPath, "dag", sdk1, stateMode: RtlStateMode.FourState, threads: 1);
            await RtlExternalSdk.ExportAsync(dagIrPath, "dag", sdk4, stateMode: RtlStateMode.FourState, threads: 4);
            await RtlExternalSdk.ExportAsync(dagIrPath, "dag", sdk4Two, stateMode: RtlStateMode.TwoState, threads: 4);

            Assert.That(File.Exists(Path.Combine(sdk1, "thread_pool.h")), Is.False, "Default --threads 1 SDK must not copy thread_pool.h.");
            Assert.That(File.Exists(Path.Combine(sdk4, "thread_pool.h")), Is.True, "--threads 4 SDK must include thread_pool.h.");
            Assert.That(File.ReadAllText(Path.Combine(sdk4, "external-model.json")), Does.Contain("thread_pool.h"));

            string dagHost = Path.Combine(root, "dag_host.cpp");
            File.WriteAllText(dagHost, """
                #include "Vdag.h"
                #include <iostream>
                int main(){
                 Vdag m;
                 m.clk=0; m.seed=0; m.eval();
                 m.forceSignal(2, vsrtl::word(0, 0, 0, 32));
                 m.eval();
                 m.releaseSignal(2);
                 uint64_t hash = 0;
                 for(uint64_t i=1;i<=500;++i){
                  m.seed = (i * 2654435761ULL) & 0xFFFFFFFFULL;
                  m.clk=1; m.eval();
                  hash = (hash * 1315423911ULL) ^ uint64_t(m.acc);
                  m.clk=0; m.eval();
                 }
                 std::cout<<hash<<"\n";
                 m.final();
                 return 0;
                }
                """);
            string b1 = Path.Combine(root, "b1");
            string b4 = Path.Combine(root, "b4");
            string b4t = Path.Combine(root, "b4t");
            await Run("cmake", "-S", sdk1, "-B", b1, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DVELOSIM_HOST_SOURCE=" + dagHost);
            await Run("cmake", "--build", b1, "--parallel", "2");
            string h1 = (await Run(Path.Combine(b1, "velosim_host"))).StandardOutput.Trim();

            await Run("cmake", "-S", sdk4, "-B", b4, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DVELOSIM_HOST_SOURCE=" + dagHost);
            await Run("cmake", "--build", b4, "--parallel", "2");
            string h4 = (await Run(Path.Combine(b4, "velosim_host"))).StandardOutput.Trim();

            await Run("cmake", "-S", sdk4Two, "-B", b4t, "-G", "Ninja", "-DCMAKE_BUILD_TYPE=Release", "-DVELOSIM_HOST_SOURCE=" + dagHost);
            await Run("cmake", "--build", b4t, "--parallel", "2");
            string h4t = (await Run(Path.Combine(b4t, "velosim_host"))).StandardOutput.Trim();

            Assert.That(h4, Is.EqualTo(h1), "--threads 4 W64 settle() must match single-threaded bit-for-bit.");
            Assert.That(h4t, Is.EqualTo(h1), "--threads 4 W2 settle() must match single-threaded bit-for-bit.");

            // Part B: Safe Rust zero-dependency RFC 1952 .vcd.gz + fail-closed filter_prefixes!
            string rsIr = """
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
            string rsIrPath = Path.Combine(root, "rsvgz.rtl.json");
            File.WriteAllText(rsIrPath, rsIr);
            string rsSdk = Path.Combine(root, "rs_vcdgz");
            await RtlExternalSdk.ExportAsync(rsIrPath, "rsvgz", rsSdk, target: RtlTargetLanguage.Rust);

            string plainVcd = Path.Combine(root, "rs_full.vcd");
            string gzVcd = Path.Combine(root, "rs_full.vcd.gz");
            string filteredGz = Path.Combine(root, "rs_sub.vcd.gz");
            string rsTests = Path.Combine(rsSdk, "tests");
            Directory.CreateDirectory(rsTests);
            File.WriteAllText(Path.Combine(rsTests, "vcd_gz.rs"), $$"""
                use velosim_rsvgz::Vrsvgz;
                #[test]
                fn check_rust_vcd_gz_and_filter() {
                    let mut m = Vrsvgz::new();
                    assert!(m.open_vcd_gz_filtered({{RtlDesign.CppStringLiteral(filteredGz)}}, "1ns", &["nonexistent"]).is_err());

                    m.open_vcd({{RtlDesign.CppStringLiteral(plainVcd)}}, "1ns").unwrap();
                    m.set_din(0); m.set_clk(0); m.eval(); m.dump_vcd().unwrap();
                    for i in 1u64..=120 {
                        m.time_inc(5); m.set_din((i * 37) & 0xFFFF); m.set_clk(1); m.eval(); m.dump_vcd().unwrap();
                        m.time_inc(5); m.set_clk(0); m.eval(); m.dump_vcd().unwrap();
                    }
                    m.close_vcd();

                    let mut m2 = Vrsvgz::new();
                    m2.open_vcd_gz({{RtlDesign.CppStringLiteral(gzVcd)}}, "1ns").unwrap();
                    m2.set_din(0); m2.set_clk(0); m2.eval(); m2.dump_vcd().unwrap();
                    for i in 1u64..=120 {
                        m2.time_inc(5); m2.set_din((i * 37) & 0xFFFF); m2.set_clk(1); m2.eval(); m2.dump_vcd().unwrap();
                        m2.time_inc(5); m2.set_clk(0); m2.eval(); m2.dump_vcd().unwrap();
                    }
                    m2.close_vcd();

                    let mut m3 = Vrsvgz::new();
                    m3.open_vcd_gz_filtered({{RtlDesign.CppStringLiteral(filteredGz)}}, "10ps", &["sub"]).unwrap();
                    m3.set_din(0x1234); m3.set_clk(0); m3.eval(); m3.dump_vcd().unwrap();
                    m3.time_inc(10); m3.set_clk(1); m3.eval(); m3.dump_vcd().unwrap();
                    m3.final_model();
                }
                """);
            await RunIn(rsSdk, "cargo", "test", "--release", "--quiet");

            string plainText = File.ReadAllText(plainVcd);
            string decompressed = DecompressGzipText(gzVcd);
            Assert.That(decompressed, Is.EqualTo(plainText), "Rust RFC 1952 .vcd.gz must decompress byte-for-byte identically to plain VCD.");
            Assert.That(new FileInfo(gzVcd).Length, Is.LessThan(new FileInfo(plainVcd).Length),
                "Rust .vcd.gz must be strictly smaller than uncompressed .vcd.");

            string filteredText = DecompressGzipText(filteredGz);
            Assert.That(filteredText, Does.Contain("$timescale 10ps $end"));
            Assert.That(filteredText, Does.Contain("sub_acc [15:0]"));
            Assert.That(filteredText, Does.Not.Contain("other_aux"));
            Assert.That(filteredText, Does.Not.Contain("din"));
        }
        finally { Directory.Delete(root, true); }
    }
}
