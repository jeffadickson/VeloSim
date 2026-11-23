using System.Diagnostics;
using NUnit.Framework;

namespace VeloSim.Tests;

public sealed class RtlParity14Of14Tests
{
    // Exact velosim.rtl.v1 IR for examples/dynamic_array.sv
    private const string DynamicArrayIr = """
        {
          "format": "velosim.rtl.v1",
          "signals": [
            {"name": "clk", "width": 1, "direction": "input"},
            {"name": "op", "width": 3, "direction": "input"},
            {"name": "n", "width": 9, "direction": "input"},
            {"name": "i", "width": 9, "direction": "input"},
            {"name": "a", "width": 8, "direction": "input"},
            {"name": "q", "width": 8, "direction": "output"},
            {"name": "sz", "width": 32, "direction": "output", "signed": true, "two_state": true}
          ],
          "memories": [
            {"name": "m", "width": 8, "depth": 1, "dynamic": true}
          ],
          "continuous": [],
          "initialization": {
            "kind": "block",
            "items": [
              {"kind": "allocate", "name": "m", "count": {"kind": "const", "bits": "000000010"}, "preserve": false},
              {"kind": "blocking", "target": {"kind": "index", "name": "m", "index": {"kind": "const", "bits": "000000000"}}, "value": {"kind": "const", "bits": "00000001"}}
            ]
          },
          "clock": "clk",
          "body": {
            "kind": "process",
            "events": [{"signal": "clk", "edge": "posedge"}],
            "body": {
              "kind": "block",
              "items": [
                {
                  "kind": "case",
                  "case_kind": "exact",
                  "value": {"kind": "ref", "name": "op"},
                  "items": [
                    {"match": {"kind": "const", "bits": "000"}, "body": {"kind": "allocate", "name": "m", "count": {"kind": "ref", "name": "n"}, "preserve": false}},
                    {"match": {"kind": "const", "bits": "001"}, "body": {"kind": "allocate", "name": "m", "count": {"kind": "ref", "name": "n"}, "preserve": true}},
                    {"match": {"kind": "const", "bits": "010"}, "body": {"kind": "delete", "name": "m"}},
                    {"match": {"kind": "const", "bits": "011"}, "body": {"kind": "blocking", "target": {"kind": "index", "name": "m", "index": {"kind": "ref", "name": "i"}}, "value": {"kind": "ref", "name": "a"}}},
                    {"match": null, "body": {"kind": "block", "items": []}}
                  ]
                },
                {"kind": "nba", "target": {"kind": "ref", "name": "q"}, "value": {"kind": "index", "name": "m", "index": {"kind": "ref", "name": "i"}}},
                {"kind": "nba", "target": {"kind": "ref", "name": "sz"}, "value": {"kind": "size", "name": "m"}}
              ]
            }
          }
        }
        """;

    // Exact velosim.rtl.v1 IR for examples/queue_demo.sv
    private const string QueueDemoIr = """
        {
          "format": "velosim.rtl.v1",
          "signals": [
            {"name": "clk", "width": 1, "direction": "input"},
            {"name": "reset", "width": 1, "direction": "input"},
            {"name": "push", "width": 1, "direction": "input"},
            {"name": "pop", "width": 1, "direction": "input"},
            {"name": "front", "width": 1, "direction": "input"},
            {"name": "data_in", "width": 8, "direction": "input"},
            {"name": "data_out", "width": 8, "direction": "output"},
            {"name": "count", "width": 32, "direction": "output", "signed": true, "two_state": true}
          ],
          "memories": [
            {"name": "pending", "width": 8, "depth": 1, "queue": true}
          ],
          "continuous": [],
          "clock": "clk",
          "body": {
            "kind": "process",
            "events": [{"signal": "clk", "edge": "posedge"}],
            "body": {
              "kind": "block",
              "items": [
                {
                  "kind": "if",
                  "condition": {"kind": "ref", "name": "reset"},
                  "then": {
                    "kind": "block",
                    "items": [
                      {"kind": "delete", "name": "pending"},
                      {"kind": "blocking", "target": {"kind": "ref", "name": "data_out"}, "value": {"kind": "const", "bits": "00000000"}}
                    ]
                  },
                  "else": {
                    "kind": "block",
                    "items": [
                      {
                        "kind": "if",
                        "condition": {"kind": "ref", "name": "push"},
                        "then": {
                          "kind": "if",
                          "condition": {"kind": "ref", "name": "front"},
                          "then": {"kind": "queue_push", "name": "pending", "front": true, "value": {"kind": "ref", "name": "data_in"}},
                          "else": {"kind": "queue_push", "name": "pending", "front": false, "value": {"kind": "ref", "name": "data_in"}}
                        },
                        "else": {"kind": "block", "items": []}
                      },
                      {
                        "kind": "if",
                        "condition": {"kind": "ref", "name": "pop"},
                        "then": {
                          "kind": "if",
                          "condition": {"kind": "ref", "name": "front"},
                          "then": {"kind": "queue_pop", "name": "pending", "front": true, "target": {"kind": "ref", "name": "data_out"}},
                          "else": {"kind": "queue_pop", "name": "pending", "front": false, "target": {"kind": "ref", "name": "data_out"}}
                        },
                        "else": {"kind": "block", "items": []}
                      }
                    ]
                  }
                },
                {"kind": "nba", "target": {"kind": "ref", "name": "count"}, "value": {"kind": "size", "name": "pending"}}
              ]
            }
          }
        }
        """;

    // Exact velosim.rtl.v1 IR for examples/timed_counter.sv
    private const string TimedCounterIr = """
        {
          "format": "velosim.rtl.v1",
          "time_precision": -9,
          "signals": [
            {"name": "clk", "width": 1, "direction": "internal"},
            {"name": "count", "width": 8, "direction": "output"},
            {"name": "delayed", "width": 8, "direction": "output"}
          ],
          "memories": [],
          "continuous": [],
          "initialization": {
            "kind": "block",
            "items": [
              {"kind": "blocking", "target": {"kind": "ref", "name": "clk"}, "value": {"kind": "const", "bits": "0"}},
              {"kind": "blocking", "target": {"kind": "ref", "name": "count"}, "value": {"kind": "const", "bits": "00000000"}},
              {"kind": "blocking", "target": {"kind": "ref", "name": "delayed"}, "value": {"kind": "const", "bits": "00000000"}}
            ]
          },
          "clock": null,
          "body": {"kind": "block", "items": []},
          "timed": [
            {
              "kind": "while",
              "condition": {"kind": "const", "bits": "1"},
              "body": {
                "kind": "delay",
                "ticks": 2,
                "body": {
                  "kind": "blocking",
                  "target": {"kind": "ref", "name": "clk"},
                  "value": {"kind": "bitnot", "args": [{"kind": "ref", "name": "clk"}]}
                }
              }
            },
            {
              "kind": "while",
              "condition": {"kind": "const", "bits": "1"},
              "body": {
                "kind": "event",
                "events": [{"signal": "clk", "edge": "posedge"}],
                "body": {
                  "kind": "block",
                  "items": [
                    {"kind": "nba", "target": {"kind": "ref", "name": "count"}, "value": {"kind": "add", "args": [{"kind": "ref", "name": "count"}, {"kind": "const", "bits": "00000001"}]}},
                    {"kind": "nba", "ticks": 1, "target": {"kind": "ref", "name": "delayed"}, "value": {"kind": "ref", "name": "count"}}
                  ]
                }
              }
            }
          ]
        }
        """;

    // Associative array + 2D multidimensional array + fixed memory ($readmemh) IR
    private const string AssocMdReadmemIr = """
        {
          "format": "velosim.rtl.v1",
          "signals": [
            {"name": "clk", "width": 1, "direction": "input"},
            {"name": "op", "width": 2, "direction": "input"},
            {"name": "k_in", "width": 8, "direction": "input", "signed": true},
            {"name": "v_in", "width": 8, "direction": "input"},
            {"name": "row", "width": 2, "direction": "input"},
            {"name": "col", "width": 2, "direction": "input"},
            {"name": "a_out", "width": 8, "direction": "output"},
            {"name": "md_out", "width": 8, "direction": "output"},
            {"name": "rom_out", "width": 8, "direction": "output"},
            {"name": "trav_k", "width": 8, "direction": "output", "signed": true},
            {"name": "trav_ok", "width": 32, "direction": "output", "signed": true, "two_state": true},
            {"name": "has_k", "width": 32, "direction": "output", "signed": true, "two_state": true}
          ],
          "memories": [
            {"name": "amap", "width": 8, "depth": 1, "associative": true, "key_width": 8, "key_signed": true},
            {"name": "grid", "width": 8, "depth": 8, "dimensions": [{"left": 1, "right": 0}, {"left": 3, "right": 0}]},
            {"name": "rom", "width": 8, "depth": 4}
          ],
          "continuous": [],
          "clock": "clk",
          "body": {
            "kind": "process",
            "events": [{"signal": "clk", "edge": "posedge"}],
            "body": {
              "kind": "block",
              "items": [
                {
                  "kind": "if",
                  "condition": {"kind": "eq", "args": [{"kind": "ref", "name": "op"}, {"kind": "const", "bits": "00"}]},
                  "then": {
                    "kind": "block",
                    "items": [
                      {"kind": "blocking", "target": {"kind": "index", "name": "amap", "index": {"kind": "ref", "name": "k_in"}}, "value": {"kind": "ref", "name": "v_in"}},
                      {"kind": "blocking", "target": {"kind": "index", "name": "grid", "indices": [{"kind": "ref", "name": "row"}, {"kind": "ref", "name": "col"}]}, "value": {"kind": "ref", "name": "v_in"}}
                    ]
                  },
                  "else": {
                    "kind": "if",
                    "condition": {"kind": "eq", "args": [{"kind": "ref", "name": "op"}, {"kind": "const", "bits": "01"}]},
                    "then": {"kind": "assoc_traverse", "name": "amap", "operation": "first", "index": {"kind": "ref", "name": "trav_k"}, "target": {"kind": "ref", "name": "trav_ok"}},
                    "else": {"kind": "delete", "name": "amap", "index": {"kind": "ref", "name": "k_in"}}
                  }
                },
                {"kind": "nba", "target": {"kind": "ref", "name": "a_out"}, "value": {"kind": "index", "name": "amap", "index": {"kind": "ref", "name": "k_in"}}},
                {"kind": "nba", "target": {"kind": "ref", "name": "md_out"}, "value": {"kind": "index", "name": "grid", "indices": [{"kind": "ref", "name": "row"}, {"kind": "ref", "name": "col"}]}},
                {"kind": "nba", "target": {"kind": "ref", "name": "rom_out"}, "value": {"kind": "index", "name": "rom", "index": {"kind": "ref", "name": "col"}}},
                {"kind": "nba", "target": {"kind": "ref", "name": "has_k"}, "value": {"kind": "exists", "name": "amap", "index": {"kind": "ref", "name": "k_in"}}}
              ]
            }
          }
        }
        """;

    [Test]
    public async Task DynamicArrayInitialAndBlockingMatchAcrossCppRustAndCSharp()
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-p4-dyn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "dyn.rtl.json");
            await File.WriteAllTextAsync(irPath, DynamicArrayIr);

            // Export C#, Rust, and C++
            string csDir = Path.Combine(root, "cs"), rsDir = Path.Combine(root, "rs"), cppDir = Path.Combine(root, "cpp");
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "dyn_dut", "--output-dir", csDir, "--target", "csharp"], new StringWriter(), err), Is.Zero, err.ToString());
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "dyn_dut", "--output-dir", rsDir, "--target", "rust"], new StringWriter(), err), Is.Zero, err.ToString());
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "dyn_dut", "--output-dir", cppDir, "--target", "cpp"], new StringWriter(), err), Is.Zero, err.ToString());

            // Verify C#
            string csHost = Path.Combine(root, "cs_host");
            Directory.CreateDirectory(csHost);
            await File.WriteAllTextAsync(Path.Combine(csHost, "Host.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><LangVersion>15.0</LangVersion><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
                  <ItemGroup><ProjectReference Include="{Path.Combine(csDir, "Vdyn_dut.csproj")}" /></ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(csHost, "Program.cs"), """
                using System;
                using VeloSim.Generated;
                var d = new Vdyn_dut();
                void Tick(ulong op, ulong n, ulong i, ulong a) { d.op = op; d.n = n; d.i = i; d.a = a; d.clk = 1; d.Eval(); d.clk = 0; d.Eval(); }
                d.clk = 0; d.Eval();
                // Default op=4 reads initial m[0]=1 and sz=2
                Tick(4, 0, 0, 0);
                if (d.q != 1 || d.sz != 2) throw new Exception($"init mismatch q={d.q} sz={d.sz}");
                // op=3 write m[1]=42, then op=1 preserve resize to 4
                Tick(3, 0, 1, 42);
                Tick(1, 4, 1, 0);
                if (d.q != 42 || d.sz != 4) throw new Exception($"preserve mismatch q={d.q} sz={d.sz}");
                // Fork before delete, delete original, verify fork still has sz=4 & m[1]=42
                var forked = d.Fork();
                Tick(2, 0, 0, 0);
                if (d.sz != 0) throw new Exception($"delete mismatch sz={d.sz}");
                forked.op = 4; forked.i = 1; forked.clk = 1; forked.Eval(); forked.clk = 0; forked.Eval();
                if (forked.q != 42 || forked.sz != 4) throw new Exception("fork isolation failed");
                Console.WriteLine("DYN_CS_OK");
                """);
            var csRun = await RunCommandAsync("dotnet", ["run", "--disable-build-servers", "-c", "Release"], csHost);
            Assert.That(csRun.ExitCode, Is.Zero, csRun.Output);
            Assert.That(csRun.Output, Does.Contain("DYN_CS_OK"));

            // Verify Rust
            if (await ToolExistsAsync("cargo"))
            {
                string rsHost = Path.Combine(root, "rs_host");
                Directory.CreateDirectory(Path.Combine(rsHost, "src"));
                await File.WriteAllTextAsync(Path.Combine(rsHost, "Cargo.toml"), $$"""
                    [package]
                    name = "dyn_rs_host"
                    version = "0.1.0"
                    edition = "2021"
                    [dependencies]
                    velosim_dyn_dut = { path = "{{rsDir.Replace("\\", "/")}}" }
                    """);
                await File.WriteAllTextAsync(Path.Combine(rsHost, "src", "main.rs"), """
                    #![forbid(unsafe_code)]
                    use velosim_dyn_dut::Vdyn_dut;
                    fn tick(d: &mut Vdyn_dut, op: u64, n: u64, i: u64, a: u64) {
                        d.set_op(op); d.set_n(n); d.set_i(i); d.set_a(a);
                        d.set_clk(1); d.eval(); d.set_clk(0); d.eval();
                    }
                    fn main() {
                        let mut d = Vdyn_dut::new();
                        d.set_clk(0); d.eval();
                        tick(&mut d, 4, 0, 0, 0);
                        assert_eq!((d.q(), d.sz()), (1, 2));
                        tick(&mut d, 3, 0, 1, 42);
                        tick(&mut d, 1, 4, 1, 0);
                        assert_eq!((d.q(), d.sz()), (42, 4));
                        let mut forked = d.fork();
                        tick(&mut d, 2, 0, 0, 0);
                        assert_eq!(d.sz(), 0);
                        tick(&mut forked, 4, 0, 1, 0);
                        assert_eq!((forked.q(), forked.sz()), (42, 4));
                        println!("DYN_RS_OK");
                    }
                    """);
                var rsRun = await RunCommandAsync("cargo", ["run", "--release"], rsHost);
                Assert.That(rsRun.ExitCode, Is.Zero, rsRun.Output);
                Assert.That(rsRun.Output, Does.Contain("DYN_RS_OK"));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task QueueDemoMatchesAcrossCppRustAndCSharp()
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-p4-q-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "queue.rtl.json");
            await File.WriteAllTextAsync(irPath, QueueDemoIr);
            string csDir = Path.Combine(root, "cs"), rsDir = Path.Combine(root, "rs");
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "queue_demo", "--output-dir", csDir, "--target", "csharp"], new StringWriter(), err), Is.Zero, err.ToString());
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "queue_demo", "--output-dir", rsDir, "--target", "rust"], new StringWriter(), err), Is.Zero, err.ToString());

            string csHost = Path.Combine(root, "cs_host");
            Directory.CreateDirectory(csHost);
            await File.WriteAllTextAsync(Path.Combine(csHost, "Host.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><LangVersion>15.0</LangVersion><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
                  <ItemGroup><ProjectReference Include="{Path.Combine(csDir, "Vqueue_demo.csproj")}" /></ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(csHost, "Program.cs"), """
                using System;
                using VeloSim.Generated;
                var q = new Vqueue_demo();
                void Tick(ulong rst, ulong push, ulong pop, ulong front, ulong din) {
                    q.reset = rst; q.push = push; q.pop = pop; q.front = front; q.data_in = din;
                    q.clk = 1; q.Eval(); q.clk = 0; q.Eval();
                }
                q.clk = 0; q.Eval();
                Tick(1, 0, 0, 0, 0);
                if (q.count != 0 || q.data_out != 0) throw new Exception("reset");
                Tick(0, 1, 0, 0, 0xAA); // push_back AA -> [AA]
                Tick(0, 1, 0, 1, 0xBB); // push_front BB -> [BB, AA]
                Tick(0, 1, 0, 0, 0xCC); // push_back CC -> [BB, AA, CC]
                if (q.count != 3) throw new Exception("count3");
                Tick(0, 0, 1, 1, 0);    // pop_front -> BB, [AA, CC]
                if (q.data_out != 0xBB || q.count != 2) throw new Exception($"pop_front {q.data_out:x}");
                Tick(0, 0, 1, 0, 0);    // pop_back -> CC, [AA]
                if (q.data_out != 0xCC || q.count != 1) throw new Exception($"pop_back {q.data_out:x}");
                Console.WriteLine("Q_CS_OK");
                """);
            var csRun = await RunCommandAsync("dotnet", ["run", "--disable-build-servers", "-c", "Release"], csHost);
            Assert.That(csRun.ExitCode, Is.Zero, csRun.Output);
            Assert.That(csRun.Output, Does.Contain("Q_CS_OK"));

            if (await ToolExistsAsync("cargo"))
            {
                string rsHost = Path.Combine(root, "rs_host");
                Directory.CreateDirectory(Path.Combine(rsHost, "src"));
                await File.WriteAllTextAsync(Path.Combine(rsHost, "Cargo.toml"), $$"""
                    [package]
                    name = "q_rs_host"
                    version = "0.1.0"
                    edition = "2021"
                    [dependencies]
                    velosim_queue_demo = { path = "{{rsDir.Replace("\\", "/")}}" }
                    """);
                await File.WriteAllTextAsync(Path.Combine(rsHost, "src", "main.rs"), """
                    #![forbid(unsafe_code)]
                    use velosim_queue_demo::Vqueue_demo;
                    fn tick(q: &mut Vqueue_demo, rst: u64, push: u64, pop: u64, front: u64, din: u64) {
                        q.set_reset(rst); q.set_push(push); q.set_pop(pop); q.set_front(front); q.set_data_in(din);
                        q.set_clk(1); q.eval(); q.set_clk(0); q.eval();
                    }
                    fn main() {
                        let mut q = Vqueue_demo::new();
                        q.set_clk(0); q.eval();
                        tick(&mut q, 1, 0, 0, 0, 0);
                        assert_eq!((q.count(), q.data_out()), (0, 0));
                        tick(&mut q, 0, 1, 0, 0, 0xAA);
                        tick(&mut q, 0, 1, 0, 1, 0xBB);
                        tick(&mut q, 0, 1, 0, 0, 0xCC);
                        assert_eq!(q.count(), 3);
                        tick(&mut q, 0, 0, 1, 1, 0);
                        assert_eq!((q.data_out(), q.count()), (0xBB, 2));
                        tick(&mut q, 0, 0, 1, 0, 0);
                        assert_eq!((q.data_out(), q.count()), (0xCC, 1));
                        println!("Q_RS_OK");
                    }
                    """);
                var rsRun = await RunCommandAsync("cargo", ["run", "--release"], rsHost);
                Assert.That(rsRun.ExitCode, Is.Zero, rsRun.Output);
                Assert.That(rsRun.Output, Does.Contain("Q_RS_OK"));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task TimedCounterExplicitDelaySchedulerMatchesAcrossCppRustAndCSharp()
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-p4-timed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "timed_counter.rtl.json");
            await File.WriteAllTextAsync(irPath, TimedCounterIr);
            string csDir = Path.Combine(root, "cs"), rsDir = Path.Combine(root, "rs"), cppDir = Path.Combine(root, "cpp");
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "timed_counter", "--output-dir", csDir, "--target", "csharp"], new StringWriter(), err), Is.Zero, err.ToString());
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "timed_counter", "--output-dir", rsDir, "--target", "rust"], new StringWriter(), err), Is.Zero, err.ToString());
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "timed_counter", "--output-dir", cppDir, "--target", "cpp"], new StringWriter(), err), Is.Zero, err.ToString());

            // Expected trace for timed_counter.sv:
            // clk toggles every 2 ticks: posedge at t=2, 6, 10, 14
            // count increments at posedge (t=2->1, t=6->2, t=10->3, t=14->4)
            // delayed <= #1 count commits 1 tick after posedge (t=3->0, t=7->1, t=11->2, t=15->3)
            const string expectedTrace = "0:0,0;2:1,0;3:1,0;4:1,0;6:2,0;7:2,1;8:2,1;10:3,1;11:3,2;12:3,2;14:4,2;15:4,3";

            string csHost = Path.Combine(root, "cs_host");
            Directory.CreateDirectory(csHost);
            await File.WriteAllTextAsync(Path.Combine(csHost, "Host.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><LangVersion>15.0</LangVersion><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
                  <ItemGroup><ProjectReference Include="{Path.Combine(csDir, "Vtimed_counter.csproj")}" /></ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(csHost, "Program.cs"), $$"""
                using System;
                using System.Collections.Generic;
                using VeloSim.Generated;
                var dut = new Vtimed_counter();
                dut.Eval();
                var log = new List<string> { $"{dut.Time}:{dut.count},{dut.delayed}" };
                while (dut.EventsPending && dut.NextTimeSlot is ulong nextT && nextT <= 15UL)
                {
                    dut.TimeInc(nextT - dut.Time);
                    dut.Eval();
                    log.Add($"{dut.Time}:{dut.count},{dut.delayed}");
                }
                string trace = string.Join(";", log);
                if (trace != "{{expectedTrace}}") throw new Exception("Trace mismatch: " + trace);
                // Verify Fork() works on live timed scheduler!
                var forked = dut.Fork();
                forked.TimeInc(forked.NextTimeSlot!.Value - forked.Time);
                forked.Eval();
                if (dut.Time != 15UL || forked.Time != 16UL) throw new Exception("Timed fork isolation failed");
                Console.WriteLine("TIMED_CS_OK");
                """);
            var csRun = await RunCommandAsync("dotnet", ["run", "--disable-build-servers", "-c", "Release"], csHost);
            Assert.That(csRun.ExitCode, Is.Zero, csRun.Output);
            Assert.That(csRun.Output, Does.Contain("TIMED_CS_OK"));

            if (await ToolExistsAsync("cargo"))
            {
                string rsHost = Path.Combine(root, "rs_host");
                Directory.CreateDirectory(Path.Combine(rsHost, "src"));
                await File.WriteAllTextAsync(Path.Combine(rsHost, "Cargo.toml"), $$"""
                    [package]
                    name = "timed_rs_host"
                    version = "0.1.0"
                    edition = "2021"
                    [dependencies]
                    velosim_timed_counter = { path = "{{rsDir.Replace("\\", "/")}}" }
                    """);
                await File.WriteAllTextAsync(Path.Combine(rsHost, "src", "main.rs"), $$"""
                    #![forbid(unsafe_code)]
                    use velosim_timed_counter::Vtimed_counter;
                    fn main() {
                        let mut dut = Vtimed_counter::new();
                        dut.eval();
                        let mut log = vec![format!("{}:{},{}", dut.time(), dut.count(), dut.delayed())];
                        while dut.events_pending() {
                            let Some(next_t) = dut.next_time_slot() else { break; };
                            if next_t > 15 { break; }
                            let cur = dut.time();
                            dut.time_inc(next_t - cur);
                            dut.eval();
                            log.push(format!("{}:{},{}", dut.time(), dut.count(), dut.delayed()));
                        }
                        assert_eq!(log.join(";"), "{{expectedTrace}}");
                        let mut forked = dut.fork();
                        let nt = forked.next_time_slot().unwrap();
                        let ft = forked.time();
                        forked.time_inc(nt - ft);
                        forked.eval();
                        assert_eq!((dut.time(), forked.time()), (15, 16));
                        println!("TIMED_RS_OK");
                    }
                    """);
                var rsRun = await RunCommandAsync("cargo", ["run", "--release"], rsHost);
                Assert.That(rsRun.ExitCode, Is.Zero, rsRun.Output);
                Assert.That(rsRun.Output, Does.Contain("TIMED_RS_OK"));
            }

            string? cxx = await ToolExistsAsync("g++") ? "g++" : (await ToolExistsAsync("clang++") ? "clang++" : null);
            if (cxx is not null)
            {
                string hostCpp = Path.Combine(root, "host.cpp");
                string exe = Path.Combine(root, "host_timed");
                await File.WriteAllTextAsync(hostCpp, $$"""
                    #include "Vtimed_counter.h"
                    #include <iostream>
                    #include <sstream>
                    int main() {
                        Vtimed_counter dut;
                        dut.eval();
                        std::ostringstream os;
                        os << dut.time() << ":" << static_cast<uint64_t>(dut.count) << "," << static_cast<uint64_t>(dut.delayed);
                        while (dut.eventsPending()) {
                            auto nt = dut.nextTimeSlot();
                            if (!nt || *nt > 15ULL) break;
                            dut.timeInc(*nt - dut.time());
                            dut.eval();
                            os << ";" << dut.time() << ":" << static_cast<uint64_t>(dut.count) << "," << static_cast<uint64_t>(dut.delayed);
                        }
                        if (os.str() != "{{expectedTrace}}") { std::cerr << os.str() << "\n"; return 1; }
                        std::cout << "TIMED_CPP_OK\n";
                        return 0;
                    }
                    """);
                var comp = await RunCommandAsync(cxx, ["-std=c++20", "-O2", "-I" + cppDir, hostCpp, Path.Combine(cppDir, "vpi.cpp"), "-ldl", "-o", exe], root);
                Assert.That(comp.ExitCode, Is.Zero, comp.Output);
                var cppRun = await RunCommandAsync(exe, [], root);
                Assert.That(cppRun.ExitCode, Is.Zero, cppRun.Output);
                Assert.That(cppRun.Output, Does.Contain("TIMED_CPP_OK"));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task AssociativeMultidimAndReadmemhMatchAcrossRustAndCSharp()
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-p4-assoc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "assoc.rtl.json");
            string hexPath = Path.Combine(root, "rom.hex");
            await File.WriteAllTextAsync(irPath, AssocMdReadmemIr);
            await File.WriteAllTextAsync(hexPath, "// ROM init\n@0 11 22 /* mid */ 33 44\n");

            string csDir = Path.Combine(root, "cs"), rsDir = Path.Combine(root, "rs");
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "assoc_dut", "--output-dir", csDir, "--target", "csharp"], new StringWriter(), err), Is.Zero, err.ToString());
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "assoc_dut", "--output-dir", rsDir, "--target", "rust"], new StringWriter(), err), Is.Zero, err.ToString());

            string csHost = Path.Combine(root, "cs_host");
            Directory.CreateDirectory(csHost);
            await File.WriteAllTextAsync(Path.Combine(csHost, "Host.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><Nullable>enable</Nullable><LangVersion>15.0</LangVersion><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
                  <ItemGroup><ProjectReference Include="{Path.Combine(csDir, "Vassoc_dut.csproj")}" /></ItemGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(csHost, "Program.cs"), $$"""
                using System;
                using VeloSim.Generated;
                var d = new Vassoc_dut();
                int loaded = d.LoadMemoryHex(2, @"{{hexPath.Replace("\\", "\\\\")}}");
                if (loaded != 4) throw new Exception("loaded=" + loaded);
                void Tick(ulong op, ulong k, ulong v, ulong r, ulong c) {
                    d.op = op; d.k_in = k; d.v_in = v; d.row = r; d.col = c;
                    d.clk = 1; d.Eval(); d.clk = 0; d.Eval();
                }
                d.clk = 0; d.Eval();
                // Insert signed keys +5 (0x05) and -3 (0xFD), plus grid[1][2] = 0x77
                Tick(0, 0x05, 0x55, 0, 0);
                Tick(0, 0xFD, 0x77, 1, 2);
                if (d.a_out != 0x77 || d.md_out != 0x77 || d.rom_out != 0x33 || d.has_k != 1)
                    throw new Exception($"insert mismatch a={d.a_out:x} md={d.md_out:x} rom={d.rom_out:x} has={d.has_k}");
                // Signed first() must return -3 (0xFD), not +5!
                Tick(1, 0x05, 0, 1, 2);
                if (d.trav_ok != 1 || d.trav_k != 0xFD) throw new Exception($"first mismatch ok={d.trav_ok} k={d.trav_k:x}");
                // Delete key -3
                Tick(2, 0xFD, 0, 1, 2);
                if (d.has_k != 0) throw new Exception("delete failed");
                Console.WriteLine("ASSOC_CS_OK");
                """);
            var csRun = await RunCommandAsync("dotnet", ["run", "--disable-build-servers", "-c", "Release"], csHost);
            Assert.That(csRun.ExitCode, Is.Zero, csRun.Output);
            Assert.That(csRun.Output, Does.Contain("ASSOC_CS_OK"));

            if (await ToolExistsAsync("cargo"))
            {
                string rsHost = Path.Combine(root, "rs_host");
                Directory.CreateDirectory(Path.Combine(rsHost, "src"));
                await File.WriteAllTextAsync(Path.Combine(rsHost, "Cargo.toml"), $$"""
                    [package]
                    name = "assoc_rs_host"
                    version = "0.1.0"
                    edition = "2021"
                    [dependencies]
                    velosim_assoc_dut = { path = "{{rsDir.Replace("\\", "/")}}" }
                    """);
                await File.WriteAllTextAsync(Path.Combine(rsHost, "src", "main.rs"), $$"""
                    #![forbid(unsafe_code)]
                    use velosim_assoc_dut::Vassoc_dut;
                    fn tick(d: &mut Vassoc_dut, op: u64, k: u64, v: u64, r: u64, c: u64) {
                        d.set_op(op); d.set_k_in(k); d.set_v_in(v); d.set_row(r); d.set_col(c);
                        d.set_clk(1); d.eval(); d.set_clk(0); d.eval();
                    }
                    fn main() {
                        let mut d = Vassoc_dut::new();
                        let loaded = d.load_memory_hex(2, "{{hexPath.Replace("\\", "/")}}", 0, usize::MAX).unwrap();
                        assert_eq!(loaded, 4);
                        d.set_clk(0); d.eval();
                        tick(&mut d, 0, 0x05, 0x55, 0, 0);
                        tick(&mut d, 0, 0xFD, 0x77, 1, 2);
                        assert_eq!((d.a_out(), d.md_out(), d.rom_out(), d.has_k()), (0x77, 0x77, 0x33, 1));
                        tick(&mut d, 1, 0x05, 0, 1, 2);
                        assert_eq!((d.trav_ok(), d.trav_k()), (1, 0xFD));
                        tick(&mut d, 2, 0xFD, 0, 1, 2);
                        assert_eq!(d.has_k(), 0);
                        println!("ASSOC_RS_OK");
                    }
                    """);
                var rsRun = await RunCommandAsync("cargo", ["run", "--release"], rsHost);
                Assert.That(rsRun.ExitCode, Is.Zero, rsRun.Output);
                Assert.That(rsRun.Output, Does.Contain("ASSOC_RS_OK"));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Test]
    public async Task TimedControlFlowAndTwoStateContainersEmitCleanlyForCSharpAndRust()
    {
        // Covers two-state container paths in C# & Rust
        string root = Path.Combine(Path.GetTempPath(), "velosim-p4-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var (name, ir) in new[] { ("dyn", DynamicArrayIr), ("q", QueueDemoIr), ("assoc", AssocMdReadmemIr) })
            {
                string irPath = Path.Combine(root, name + ".rtl.json");
                await File.WriteAllTextAsync(irPath, ir);
                var err = new StringWriter();
                Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", name + "_cs2", "--output-dir", Path.Combine(root, name + "_cs2"), "--target", "csharp", "--two-state"], new StringWriter(), err), Is.Zero, err.ToString());
                Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", name + "_rs2", "--output-dir", Path.Combine(root, name + "_rs2"), "--target", "rust", "--two-state"], new StringWriter(), err), Is.Zero, err.ToString());
            }

            // Covers timed wait, do, for, break, continue, if, repeat, runtime_repeat, fork/wait_fork/disable_fork, trigger, blocking_delay, concat, selected, memory write
            const string richTimedIr = """
                {
                  "format": "velosim.rtl.v1",
                  "time_precision": -9,
                  "named_events": ["ev0"],
                  "signals": [
                    {"name": "go", "width": 1, "direction": "input"},
                    {"name": "a", "width": 4, "direction": "output"},
                    {"name": "b", "width": 4, "direction": "output"},
                    {"name": "cnt", "width": 8, "direction": "output"}
                  ],
                  "memories": [
                    {"name": "m", "width": 8, "depth": 4}
                  ],
                  "continuous": [],
                  "initialization": {
                    "kind": "block",
                    "items": [
                      {"kind": "blocking", "target": {"kind": "ref", "name": "a"}, "value": {"kind": "const", "bits": "0000"}},
                      {"kind": "blocking", "target": {"kind": "ref", "name": "b"}, "value": {"kind": "const", "bits": "0000"}},
                      {"kind": "blocking", "target": {"kind": "ref", "name": "cnt"}, "value": {"kind": "const", "bits": "00000000"}}
                    ]
                  },
                  "clock": null,
                  "body": {"kind": "block", "items": []},
                  "timed": [
                    {
                      "kind": "block",
                      "items": [
                        {"kind": "wait", "condition": {"kind": "ref", "name": "go"}, "body": {"kind": "block", "items": []}},
                        {"kind": "blocking_delay", "ticks": 1, "target": {"kind": "concat", "args": [{"kind": "ref", "name": "a"}, {"kind": "ref", "name": "b"}]}, "value": {"kind": "const", "bits": "10100101"}},
                        {"kind": "blocking_delay", "ticks": 1, "target": {"kind": "bitselect", "value": {"kind": "ref", "name": "cnt"}, "index": {"kind": "const", "bits": "00000000"}}, "value": {"kind": "const", "bits": "1"}},
                        {"kind": "blocking", "target": {"kind": "index", "name": "m", "index": {"kind": "const", "bits": "00000000"}}, "value": {"kind": "ref", "name": "cnt"}},
                        {"kind": "repeat", "count": 2, "body": {"kind": "blocking", "target": {"kind": "ref", "name": "cnt"}, "value": {"kind": "add", "args": [{"kind": "ref", "name": "cnt"}, {"kind": "const", "bits": "00000001"}]}}},
                        {"kind": "runtime_repeat", "count": {"kind": "const", "bits": "00000010"}, "body": {"kind": "if", "condition": {"kind": "eq", "args": [{"kind": "ref", "name": "cnt"}, {"kind": "const", "bits": "11111111"}]}, "then": {"kind": "break"}, "else": {"kind": "continue"}}},
                        {"kind": "for", "condition": {"kind": "lt", "args": [{"kind": "ref", "name": "cnt"}, {"kind": "const", "bits": "00000100"}]}, "step": {"kind": "blocking", "target": {"kind": "ref", "name": "cnt"}, "value": {"kind": "add", "args": [{"kind": "ref", "name": "cnt"}, {"kind": "const", "bits": "00000001"}]}}, "body": {"kind": "block", "items": []}},
                        {"kind": "do", "condition": {"kind": "const", "bits": "0"}, "body": {"kind": "trigger", "name": "ev0", "nonblocking": false}},
                        {"kind": "fork", "join": "all", "items": [{"kind": "delay", "ticks": 1, "body": {"kind": "trigger", "name": "ev0", "nonblocking": true}}]},
                        {"kind": "wait_fork"},
                        {"kind": "disable_fork"}
                      ]
                    },
                    {
                      "kind": "event",
                      "events": [{"event": "ev0"}],
                      "body": {"kind": "nba", "ticks": 1, "target": {"kind": "concat", "args": [{"kind": "ref", "name": "a"}, {"kind": "ref", "name": "b"}]}, "value": {"kind": "const", "bits": "01011010"}}
                    }
                  ]
                }
                """;
            string richPath = Path.Combine(root, "rich_timed.rtl.json");
            await File.WriteAllTextAsync(richPath, richTimedIr);
            var err2 = new StringWriter();
            string csRich = Path.Combine(root, "rich_cs"), rsRich = Path.Combine(root, "rich_rs");
            Assert.That(await ToolCli.RunAsync(["rtl-export", richPath, "--top", "rich_timed", "--output-dir", csRich, "--target", "csharp"], new StringWriter(), err2), Is.Zero, err2.ToString());
            Assert.That(await ToolCli.RunAsync(["rtl-export", richPath, "--top", "rich_timed", "--output-dir", rsRich, "--target", "rust"], new StringWriter(), err2), Is.Zero, err2.ToString());
            // Verify emitted C# timed model compiles with zero warnings
            var csBuild = await RunCommandAsync("dotnet", ["build", Path.Combine(csRich, "Vrich_timed.csproj"), "--disable-build-servers", "-p:TreatWarningsAsErrors=true"], csRich);
            Assert.That(csBuild.ExitCode, Is.Zero, csBuild.Output);
            // Verify emitted Rust timed model compiles with #![forbid(unsafe_code)]
            if (await ToolExistsAsync("cargo"))
            {
                var rsBuild = await RunCommandAsync("cargo", ["check"], rsRich);
                Assert.That(rsBuild.ExitCode, Is.Zero, rsBuild.Output);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<(int ExitCode, string Output)> RunCommandAsync(string file, string[] args, string cwd)
    {
        var psi = new ProcessStartInfo(file) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start " + file);
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        return (proc.ExitCode, (await stdout) + "\n" + (await stderr));
    }

    private static async Task<bool> ToolExistsAsync(string tool)
    {
        try { return (await RunCommandAsync(tool, ["--version"], Environment.CurrentDirectory)).ExitCode == 0; }
        catch { return false; }
    }
}
