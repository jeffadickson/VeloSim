using System.Diagnostics;
using System.Text.Json;
using NUnit.Framework;

namespace VeloSim.Tests;

public sealed class RtlCSharpVpiTests
{
    private const string VpiDutIr = """
        {
          "format": "velosim.rtl.v1",
          "signals": [
            {"name": "clk", "width": 1, "direction": "input"},
            {"name": "rst", "width": 1, "direction": "input"},
            {"name": "en", "width": 1, "direction": "input"},
            {"name": "din", "width": 8, "direction": "input", "signed": true},
            {"name": "q", "width": 8, "direction": "output"},
            {"name": "we", "width": 1, "direction": "input"},
            {"name": "addr", "width": 2, "direction": "input"},
            {"name": "rdata", "width": 8, "direction": "output"}
          ],
          "memories": [
            {"name": "ram", "width": 8, "depth": 4}
          ],
          "continuous": [],
          "clock": "clk",
          "body": {
            "kind": "process",
            "events": [{"signal": "clk", "edge": "posedge"}],
            "body": {
              "kind": "if",
              "condition": {"kind": "ref", "name": "rst"},
              "then": {
                "kind": "block",
                "items": [
                  {"kind": "nba", "target": {"kind": "ref", "name": "q"}, "value": {"kind": "const", "bits": "00000000"}},
                  {"kind": "nba", "target": {"kind": "ref", "name": "rdata"}, "value": {"kind": "const", "bits": "00000000"}}
                ]
              },
              "else": {
                "kind": "block",
                "items": [
                  {
                    "kind": "if",
                    "condition": {"kind": "ref", "name": "en"},
                    "then": {"kind": "nba", "target": {"kind": "ref", "name": "q"}, "value": {"kind": "add", "args": [{"kind": "ref", "name": "q"}, {"kind": "const", "bits": "00000001"}]}},
                    "else": {"kind": "block", "items": []}
                  },
                  {
                    "kind": "if",
                    "condition": {"kind": "ref", "name": "we"},
                    "then": {"kind": "nba", "target": {"kind": "index", "name": "ram", "index": {"kind": "ref", "name": "addr"}}, "value": {"kind": "ref", "name": "din"}},
                    "else": {"kind": "block", "items": []}
                  },
                  {"kind": "nba", "target": {"kind": "ref", "name": "rdata"}, "value": {"kind": "index", "name": "ram", "index": {"kind": "ref", "name": "addr"}}}
                ]
              }
            }
          }
        }
        """;

    [TestCase(false)]
    [TestCase(true)]
    public async Task SafeCSharpVpiSessionSupportsIntrospectionCallbacksForceAndMemory(bool twoState)
    {
        string root = Path.Combine(Path.GetTempPath(), "velosim-cs-safe-vpi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "vpi_dut.rtl.json");
            string sdkDir = Path.Combine(root, "sdk");
            string hostDir = Path.Combine(root, "host");
            await File.WriteAllTextAsync(irPath, VpiDutIr);

            string[] cliArgs = twoState
                ? ["rtl-export", irPath, "--top", "vpi_dut", "--output-dir", sdkDir, "--target", "csharp", "--two-state"]
                : ["rtl-export", irPath, "--top", "vpi_dut", "--output-dir", sdkDir, "--target", "csharp"];
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(cliArgs, new StringWriter(), err), Is.Zero, err.ToString());

            // Core model & runtime remain 100% safe managed C# (no unsafe blocks).
            string modelCs = await File.ReadAllTextAsync(Path.Combine(sdkDir, "Vvpi_dut.cs"));
            string runtimeCs = await File.ReadAllTextAsync(Path.Combine(sdkDir, "VeloSimRuntime.cs"));
            Assert.That(modelCs, Does.Not.Contain("unsafe "));
            Assert.That(runtimeCs, Does.Not.Contain("unsafe "));

            Directory.CreateDirectory(hostDir);
            await File.WriteAllTextAsync(Path.Combine(hostDir, "Host.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <LangVersion>15.0</LangVersion>
                    <AnalysisLevel>11.0</AnalysisLevel>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{Path.Combine(sdkDir, "Vvpi_dut.csproj")}" />
                  </ItemGroup>
                </Project>
                """);

            await File.WriteAllTextAsync(Path.Combine(hostDir, "Program.cs"), """
                using System;
                using System.Collections.Generic;
                using VeloSim.Generated;

                var sim = new VpiSession<Vvpi_dut>(new Vvpi_dut());
                int clk = sim.SignalByName("clk");
                int rst = sim.SignalByName("vpi_dut.rst");
                int en = sim.SignalByName("TOP.vpi_dut.en");
                int q = sim.SignalByName("q");
                int din = sim.SignalByName("din");
                if (clk < 0 || rst < 0 || en < 0 || q < 0 || din < 0) throw new Exception("signal lookup failed");
                if (!sim.Signals[din].IsSigned) throw new Exception("din signed mismatch");
                if (sim.Signals[q].Direction != VpiDirection.Output) throw new Exception("q dir mismatch");
                if (sim.Memories.Count != 1 || sim.Memories[0].Name != "ram" || sim.Memories[0].Depth != 4) throw new Exception("mem metadata mismatch");

                // Output deposit must fail closed.
                bool rejectedOut = false;
                try { sim.PutValue(q, new W64(7, 0, 0, 8)); } catch (InvalidOperationException) { rejectedOut = true; }
                if (!rejectedOut) throw new Exception("output put did not reject");

                // Backdoor memory R/W.
                sim.WriteMemory(0, 2, new W64(0x5A, 0, 0, 8));
                if (sim.ReadMemory(0, 2).V != 0x5A) throw new Exception("memory R/W mismatch");

                var log = new List<string>();
                sim.OnStartOfSimulation((ctx, ev) => {
                    log.Add($"start@{ev.Time}");
                    ctx.PutValue(rst, new W64(1, 0, 0, 1));
                    ctx.PutValue(en, new W64(0, 0, 0, 1));
                });
                var vcHandle = sim.OnValueChange(q, (_, ev) => log.Add($"q={ev.Value!.Value.V}@{ev.Time}"));
                sim.OnReadWriteSynch((_, ev) => log.Add($"rw@{ev.Time}"));
                sim.OnReadOnlySynch((ctx, ev) => {
                    bool roRej = false;
                    try { ctx.PutValue(en, new W64(1, 0, 0, 1)); } catch (InvalidOperationException) { roRej = true; }
                    if (!roRej) throw new Exception("ReadOnlySynch allowed put");
                    log.Add($"ro@{ev.Time}");
                });
                sim.OnAfterDelay(10, (_, ev) => log.Add($"delay@{ev.Time}"));
                sim.OnEndOfSimulation((ctx, ev) => {
                    bool endRej = false;
                    try { ctx.PutValue(en, new W64(0, 0, 0, 1)); } catch (InvalidOperationException) { endRej = true; }
                    if (!endRej) throw new Exception("EndOfSimulation allowed put");
                    log.Add($"end@{ev.Time}");
                });

                sim.PutValue(clk, new W64(0, 0, 0, 1)); sim.Eval();
                sim.PutValue(clk, new W64(1, 0, 0, 1)); sim.Eval();
                sim.PutValue(clk, new W64(0, 0, 0, 1)); sim.Eval();
                if (sim.GetValue(q).V != 0) throw new Exception("reset failed");

                sim.PutValue(rst, new W64(0, 0, 0, 1));
                sim.PutValue(en, new W64(1, 0, 0, 1));
                for (int i = 0; i < 3; ++i) {
                    sim.TimeInc(5);
                    sim.PutValue(clk, new W64(1, 0, 0, 1)); sim.Eval();
                    sim.PutValue(clk, new W64(0, 0, 0, 1)); sim.Eval();
                }
                if (sim.GetValue(q).V != 3) throw new Exception("count mismatch: " + sim.GetValue(q).V);

                // Force & release output q.
                sim.ForceValue(q, new W64(0x77, 0, 0, 8));
                sim.PutValue(clk, new W64(1, 0, 0, 1)); sim.Eval();
                if (sim.GetValue(q).V != 0x77) throw new Exception("force failed");
                sim.PutValue(clk, new W64(0, 0, 0, 1)); sim.Eval();
                sim.ReleaseValue(q);

                if (!sim.RemoveCb(vcHandle)) throw new Exception("RemoveCb failed");
                int before = log.Count;
                sim.PutValue(clk, new W64(1, 0, 0, 1)); sim.Eval();
                if (sim.GetValue(q).V != 0x78) throw new Exception("post-release count failed");
                if (log.Count != before) throw new Exception("removed callback still fired");

                sim.Final();
                if (log[0] != "start@0" || !log.Contains("rw@0") || !log.Contains("ro@0") || !log.Contains("delay@10") ||
                    !log.Contains("q=1@5") || !log.Contains("q=2@10") || !log.Contains("q=3@15") || log[^1] != "end@15")
                    throw new Exception("unexpected log: " + string.Join(", ", log));
                Console.WriteLine("CS_SAFE_VPI_OK");
                """);

            var (bCode, bOut, bErr) = await RunAsync("dotnet", ["build", hostDir, "-c", "Release", "--disable-build-servers", "--nologo"], root);
            Assert.That(bCode, Is.Zero, bErr + "\n" + bOut);
            var (code, stdout, stderr) = await RunAsync("dotnet", [Path.Combine(hostDir, "bin", "Release", "net11.0", "Host.dll")], root);
            Assert.That(code, Is.Zero, stderr + "\n" + stdout);
            Assert.That(stdout, Does.Contain("CS_SAFE_VPI_OK"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task CSharpCAbiVpiLoadsRealCompiledCPluginViaUnmanagedCallersOnly()
    {
        if (!ToolExists("gcc")) Assert.Ignore("gcc not installed on PATH.");
        string root = Path.Combine(Path.GetTempPath(), "velosim-cs-cabi-vpi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "vpi_dut.rtl.json");
            string sdkDir = Path.Combine(root, "sdk");
            string hostDir = Path.Combine(root, "host");
            await File.WriteAllTextAsync(irPath, VpiDutIr);

            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "vpi_dut", "--output-dir", sdkDir, "--target", "csharp", "--vpi"], new StringWriter(), err), Is.Zero, err.ToString());

            // Core model remains 100% safe managed C#.
            Assert.That(await File.ReadAllTextAsync(Path.Combine(sdkDir, "Vvpi_dut.cs")), Does.Not.Contain("unsafe "));

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sdkDir, "external-model.json")));
            Assert.That(manifest.RootElement.GetProperty("execution_scope").GetString(), Does.Contain("csharp; four-state; vpi;"));

            // 1. Compile the exported C VPI trampoline shared library.
            string trampSo = Path.Combine(root, "libvelosim_vpi_trampoline.so");
            var (tCode, tOut, tErr) = await RunAsync("gcc", [
                "-shared", "-fPIC", "-O2", $"-I{sdkDir}",
                Path.Combine(sdkDir, "vpi_ffi", "velosim_vpi_trampoline.c"),
                "-ldl", "-o", trampSo
            ], root);
            Assert.That(tCode, Is.Zero, tErr + "\n" + tOut);

            // 2. Compile a real C VPI plugin exporting vlog_startup_routines against sdk/vpi_user.h.
            string pluginC = Path.Combine(root, "plugin.c");
            string pluginSo = Path.Combine(root, "libtest_cs_vpi_plugin.so");
            await File.WriteAllTextAsync(pluginC, """
                #include "vpi_user.h"
                #include <stdio.h>
                #include <string.h>

                static int g_start = 0, g_end = 0, g_delay = 0, g_rw = 0, g_ro = 0;
                static int g_vc = 0, g_last_q = -1, g_ro_rej = 0, g_meta = 0, g_mem = 0, g_pa = 0, g_fmt = 0;

                static PLI_INT32 on_vc(p_cb_data cb) {
                    if (cb && cb->value && cb->value->format == vpiIntVal) { g_vc++; g_last_q = cb->value->value.integer; }
                    return 0;
                }

                static PLI_INT32 on_rw(p_cb_data cb) {
                    (void)cb; g_rw++;
                    vpiHandle dinh = vpi_handle_by_name("vpi_dut.din", NULL);
                    s_vpi_value fv;
                    fv.format = vpiDecStrVal; vpi_get_value(dinh, &fv);
                    int ok = !vpi_chk_error(NULL) && strcmp(fv.value.str, "-2") == 0;
                    fv.format = vpiVectorVal; vpi_get_value(dinh, &fv);
                    ok = ok && !vpi_chk_error(NULL) && fv.value.vector[0].aval == 0xFE && fv.value.vector[0].bval == 0;
                    fv.format = vpiBinStrVal; vpi_get_value(dinh, &fv);
                    ok = ok && !vpi_chk_error(NULL) && strcmp(fv.value.str, "11111110") == 0;
                    g_fmt = ok ? 1 : 0;
                    vpi_free_object(dinh);
                    return 0;
                }

                static PLI_INT32 on_ro(p_cb_data cb) {
                    (void)cb; g_ro++;
                    vpiHandle en = vpi_handle_by_name("vpi_dut.en", NULL);
                    s_vpi_value v; v.format = vpiIntVal; v.value.integer = 0;
                    vpi_put_value(en, &v, NULL, vpiNoDelay);
                    if (vpi_chk_error(NULL)) g_ro_rej = 1;
                    vpi_free_object(en);
                    return 0;
                }

                static PLI_INT32 on_delay(p_cb_data cb) { (void)cb; g_delay++; return 0; }

                static PLI_INT32 on_end(p_cb_data cb) {
                    (void)cb; g_end++;
                    printf("CS_PLUGIN_SUMMARY start=%d end=%d delay=%d rw=%d ro=%d vc=%d last_q=%d ro_rej=%d meta=%d mem=%d pa=%d fmt=%d\n",
                           g_start, g_end, g_delay, g_rw, g_ro, g_vc, g_last_q, g_ro_rej, g_meta, g_mem, g_pa, g_fmt);
                    fflush(stdout);
                    return 0;
                }

                static PLI_INT32 on_start(p_cb_data cb) {
                    (void)cb; g_start++;
                    s_vpi_vlog_info vi; memset(&vi, 0, sizeof(vi));
                    if (vpi_get_vlog_info(&vi) && vi.argc == 2 &&
                        strcmp(vi.argv[0], "+MODE=cs_vpi") == 0 && strcmp(vi.argv[1], "+SEED=99") == 0) g_pa = 1;

                    vpiHandle top_it = vpi_iterate(vpiModule, NULL);
                    vpiHandle mod = vpi_scan(top_it);
                    vpiHandle mod2 = vpi_handle_by_name("TOP.vpi_dut", NULL);
                    vpiHandle qh = vpi_handle_by_name("q", mod);
                    vpiHandle dinh = vpi_handle_by_name("vpi_dut.din", NULL);
                    vpiHandle clkh = vpi_handle_by_name("vpi_dut.clk", NULL);
                    if (mod && mod2 && vpi_compare_objects(mod, mod2) &&
                        vpi_get(vpiTopModule, mod) == 1 &&
                        strcmp(vpi_get_str(vpiName, mod), "vpi_dut") == 0 &&
                        strcmp(vpi_get_str(vpiFullName, qh), "vpi_dut.q") == 0 &&
                        vpi_get(vpiSize, qh) == 8 && vpi_get(vpiVector, qh) == 1 &&
                        vpi_get(vpiScalar, clkh) == 1 && vpi_get(vpiSigned, dinh) == 1 &&
                        vpi_get(vpiDirection, qh) == vpiOutput) g_meta = 1;

                    s_vpi_value fv; fv.format = vpiHexStrVal; fv.value.str = "fe";
                    vpi_put_value(dinh, &fv, NULL, vpiNoDelay);

                    vpiHandle mit = vpi_iterate(vpiMemory, mod);
                    vpiHandle mh = vpi_scan(mit);
                    if (mh && vpi_get(vpiType, mh) == vpiMemory && vpi_get(vpiSize, mh) == 4) {
                        vpiHandle wit = vpi_iterate(vpiMemoryWord, mh);
                        vpiHandle w; int idx = 0;
                        while ((w = vpi_scan(wit)) != NULL) {
                            if (idx == 2) {
                                s_vpi_value mv; mv.format = vpiIntVal; mv.value.integer = 0xA5;
                                vpi_put_value(w, &mv, NULL, vpiNoDelay);
                                mv.value.integer = 0; vpi_get_value(w, &mv);
                                if (!vpi_chk_error(NULL) && mv.value.integer == 0xA5) g_mem = 1;
                            }
                            vpi_free_object(w); idx++;
                        }
                        vpi_free_object(mh);
                    }

                    s_vpi_value vc_val; memset(&vc_val, 0, sizeof(vc_val)); vc_val.format = vpiIntVal;
                    s_cb_data vc_cb; memset(&vc_cb, 0, sizeof(vc_cb));
                    vc_cb.reason = cbValueChange; vc_cb.cb_rtn = on_vc; vc_cb.obj = qh; vc_cb.value = &vc_val;
                    vpi_register_cb(&vc_cb);

                    s_cb_data rw_cb; memset(&rw_cb, 0, sizeof(rw_cb)); rw_cb.reason = cbReadWriteSynch; rw_cb.cb_rtn = on_rw;
                    vpi_register_cb(&rw_cb);

                    s_cb_data ro_cb; memset(&ro_cb, 0, sizeof(ro_cb)); ro_cb.reason = cbReadOnlySynch; ro_cb.cb_rtn = on_ro;
                    vpi_register_cb(&ro_cb);

                    s_vpi_time dt = { vpiSimTime, 0, 10, 0.0 };
                    s_cb_data d_cb; memset(&d_cb, 0, sizeof(d_cb)); d_cb.reason = cbAfterDelay; d_cb.cb_rtn = on_delay; d_cb.time = &dt;
                    vpi_register_cb(&d_cb);

                    vpi_free_object(clkh); vpi_free_object(dinh); vpi_free_object(qh); vpi_free_object(mod2); vpi_free_object(mod);
                    return 0;
                }

                static void register_plugin(void) {
                    s_cb_data scb; memset(&scb, 0, sizeof(scb)); scb.reason = cbStartOfSimulation; scb.cb_rtn = on_start;
                    vpi_register_cb(&scb);
                    s_cb_data ecb; memset(&ecb, 0, sizeof(ecb)); ecb.reason = cbEndOfSimulation; ecb.cb_rtn = on_end;
                    vpi_register_cb(&ecb);
                }

                void (*vlog_startup_routines[])(void) = { register_plugin, NULL };
                """);

            string[] pluginArgs = OperatingSystem.IsMacOS()
                ? ["-shared", "-fPIC", "-O2", "-undefined", "dynamic_lookup", $"-I{sdkDir}", pluginC, "-o", pluginSo]
                : ["-shared", "-fPIC", "-O2", $"-I{sdkDir}", pluginC, "-o", pluginSo];
            var (pCode, pOut, pErr) = await RunAsync("gcc", pluginArgs, root);
            Assert.That(pCode, Is.Zero, pErr + "\n" + pOut);

            // 3. Build & run a .NET 11 host referencing vpi_ffi/Vvpi_dut.VpiFfi.csproj with TreatWarningsAsErrors=true.
            Directory.CreateDirectory(hostDir);
            await File.WriteAllTextAsync(Path.Combine(hostDir, "Host.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <LangVersion>15.0</LangVersion>
                    <AnalysisLevel>11.0</AnalysisLevel>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{Path.Combine(sdkDir, "Vvpi_dut.csproj")}" />
                    <ProjectReference Include="{Path.Combine(sdkDir, "vpi_ffi", "Vvpi_dut.VpiFfi.csproj")}" />
                  </ItemGroup>
                </Project>
                """);

            await File.WriteAllTextAsync(Path.Combine(hostDir, "Program.cs"), $$"""
                using System;
                using VeloSim.Generated.VpiFfi;

                using var host = new VpiFfiHost({{JsonSerializer.Serialize(trampSo)}});
                host.Dut.SetPlusargs(["MODE=cs_vpi", "SEED=99"]);
                host.LoadVpi({{JsonSerializer.Serialize(pluginSo)}});

                host.Dut.rst = 1; host.Dut.en = 0; host.Dut.clk = 0; host.Eval();
                host.Dut.clk = 1; host.Eval();
                host.Dut.clk = 0; host.Eval();
                if (host.Dut.q != 0) throw new Exception("reset failed");

                host.Dut.rst = 0; host.Dut.en = 1;
                for (int i = 0; i < 3; ++i) {
                    host.TimeInc(5);
                    host.Dut.clk = 1; host.Eval();
                    host.Dut.clk = 0; host.Eval();
                }
                if (host.Dut.q != 3) throw new Exception("count failed: " + host.Dut.q);
                host.Final();
                Console.WriteLine("CS_CABI_VPI_OK");
                """);

            var (bCode, bOut, bErr) = await RunAsync("dotnet", ["build", hostDir, "-c", "Release", "--disable-build-servers", "--nologo"], root);
            Assert.That(bCode, Is.Zero, bErr + "\n" + bOut);
            var (runCode, runOut, runErr) = await RunAsync("dotnet", [Path.Combine(hostDir, "bin", "Release", "net11.0", "Host.dll")], root);
            Assert.That(runCode, Is.Zero, runErr + "\n" + runOut);
            Assert.That(runOut, Does.Contain("CS_CABI_VPI_OK"));
            Assert.That(runOut, Does.Contain("CS_PLUGIN_SUMMARY start=1 end=1 delay=1 rw=1 ro=1 vc=4 last_q=3 ro_rej=1 meta=1 mem=1 pa=1 fmt=1"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task CSharpInlineMemoryAndPureInternalSettleFastPath(bool twoState)
    {
        const string fifoIr = """
            {
              "format": "velosim.rtl.v1",
              "clock": "clk",
              "signals": [
                {"name": "clk", "width": 1, "direction": "input"},
                {"name": "rst", "width": 1, "direction": "input"},
                {"name": "we", "width": 1, "direction": "input"},
                {"name": "waddr", "width": 2, "direction": "input"},
                {"name": "din", "width": 8, "direction": "input"},
                {"name": "raddr", "width": 2, "direction": "internal"},
                {"name": "dout", "width": 8, "direction": "output"}
              ],
              "memories": [{"name": "mem", "width": 8, "depth": 4}],
              "continuous": [
                {"target": {"kind": "ref", "name": "dout"}, "value": {"kind": "index", "name": "mem", "index": {"kind": "ref", "name": "raddr"}}}
              ],
              "body": {
                "kind": "process",
                "events": [{"signal": "clk", "edge": "posedge"}],
                "body": {
                  "kind": "if",
                  "condition": {"kind": "ref", "name": "rst"},
                  "then": {"kind": "nba", "target": {"kind": "ref", "name": "raddr"}, "value": {"kind": "const", "bits": "00"}},
                  "else": {
                    "kind": "block",
                    "items": [
                      {
                        "kind": "if",
                        "condition": {"kind": "ref", "name": "we"},
                        "then": {"kind": "nba", "target": {"kind": "index", "name": "mem", "index": {"kind": "ref", "name": "waddr"}}, "value": {"kind": "ref", "name": "din"}},
                        "else": {"kind": "block", "items": []}
                      },
                      {"kind": "nba", "target": {"kind": "ref", "name": "raddr"}, "value": {"kind": "ref", "name": "waddr"}}
                    ]
                  }
                }
              }
            }
            """;

        string root = Path.Combine(Path.GetTempPath(), "velosim-cs-inline-mem-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "inline_mem.rtl.json");
            string sdkDir = Path.Combine(root, "sdk");
            string hostDir = Path.Combine(root, "host");
            string ckptPath = Path.Combine(root, "state.ckpt");
            await File.WriteAllTextAsync(irPath, fifoIr);

            string[] cliArgs = twoState
                ? ["rtl-export", irPath, "--top", "inline_mem", "--output-dir", sdkDir, "--target", "csharp", "--two-state"]
                : ["rtl-export", irPath, "--top", "inline_mem", "--output-dir", sdkDir, "--target", "csharp"];
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(cliArgs, new StringWriter(), err), Is.Zero, err.ToString());

            string modelCs = await File.ReadAllTextAsync(Path.Combine(sdkDir, "Vinline_mem.cs"));
            Assert.That(modelCs, Does.Contain("[InlineArray(4)]"));
            Assert.That(modelCs, Does.Contain("Unsafe.Add(ref _m0[0]"));
            Assert.That(modelCs, Does.Contain("_mw0_idx"));
            Assert.That(modelCs, Does.Contain("_evalSlow"));
            Assert.That(modelCs, Does.Not.Contain("MemLog"));
            Assert.That(modelCs, Does.Not.Contain("unsafe "));

            Directory.CreateDirectory(hostDir);
            await File.WriteAllTextAsync(Path.Combine(hostDir, "Host.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net11.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <LangVersion>15.0</LangVersion>
                    <AnalysisLevel>11.0</AnalysisLevel>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{Path.Combine(sdkDir, "Vinline_mem.csproj")}" />
                  </ItemGroup>
                </Project>
                """);

            await File.WriteAllTextAsync(Path.Combine(hostDir, "Program.cs"), $$"""
                using System;
                using VeloSim.Generated;

                var dut = new Vinline_mem();
                dut.rst = 1; dut.we = 0; dut.waddr = 0; dut.din = 0;
                dut.clk = 0; dut.Eval();
                dut.clk = 1; dut.Eval();
                dut.clk = 0; dut.Eval();

                // Write 0xA5 at addr 2 via single-write stack-local NBA path
                dut.rst = 0; dut.we = 1; dut.waddr = 2; dut.din = 0xA5;
                dut.clk = 1; dut.Eval();
                dut.clk = 0; dut.Eval();
                if (dut.dout != 0xA5) throw new Exception("NBA readback mismatch: " + dut.dout);

                dut.SaveCheckpoint({{JsonSerializer.Serialize(ckptPath)}});

                // Backdoor WriteMemory must invalidate _evalSlow and reflect on next Eval() even without clock edge
                dut.WriteMemory(0, 2, new W64(0x3C, 0, 0, 8));
                dut.Eval();
                if (dut.dout != 0x3C) throw new Exception("WriteMemory settle mismatch: " + dut.dout);

                // ForceSignal / ReleaseSignal must re-settle combinational output
                int doutId = dut.SignalByName("dout");
                dut.ForceSignal(doutId, new W64(0x77, 0, 0, 8));
                dut.Eval();
                if (dut.dout != 0x77) throw new Exception("Force mismatch: " + dut.dout);
                dut.ReleaseSignal(doutId);
                dut.Eval();
                if (dut.dout != 0x3C) throw new Exception("Release settle mismatch: " + dut.dout);

                // RestoreCheckpoint must invalidate _evalSlow and restore 0xA5 on next Eval()
                dut.RestoreCheckpoint({{JsonSerializer.Serialize(ckptPath)}});
                dut.Eval();
                if (dut.dout != 0xA5) throw new Exception("RestoreCheckpoint settle mismatch: " + dut.dout);

                Console.WriteLine("CS_INLINE_MEM_OK");
                """);

            var (bCode, bOut, bErr) = await RunAsync("dotnet", ["build", hostDir, "-c", "Release", "--disable-build-servers", "--nologo"], root);
            Assert.That(bCode, Is.Zero, bErr + "\n" + bOut);
            var (runCode, runOut, runErr) = await RunAsync("dotnet", [Path.Combine(hostDir, "bin", "Release", "net11.0", "Host.dll")], root);
            Assert.That(runCode, Is.Zero, runErr + "\n" + runOut);
            Assert.That(runOut, Does.Contain("CS_INLINE_MEM_OK"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static bool ToolExists(string name)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(name, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p?.WaitForExit(3000);
            return p is { ExitCode: 0 };
        }
        catch { return false; }
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(string exe, string[] args, string cwd)
    {
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await outTask, await errTask);
    }
}
