using System.Diagnostics;
using System.Text.Json;
using NUnit.Framework;

namespace VeloSim.Tests;

public sealed class RtlRustVpiTests
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
    public async Task SafeRustVpiSessionSupportsIntrospectionCallbacksForceAndMemory(bool twoState)
    {
        if (!ToolExists("cargo")) Assert.Ignore("cargo not installed on PATH.");
        string root = Path.Combine(Path.GetTempPath(), "velosim-rs-safe-vpi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "vpi_dut.rtl.json");
            string sdkDir = Path.Combine(root, "sdk");
            await File.WriteAllTextAsync(irPath, VpiDutIr);

            string[] cliArgs = twoState
                ? ["rtl-export", irPath, "--top", "vpi_dut", "--output-dir", sdkDir, "--target", "rust", "--two-state"]
                : ["rtl-export", irPath, "--top", "vpi_dut", "--output-dir", sdkDir, "--target", "rust"];
            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(cliArgs, new StringWriter(), err), Is.Zero, err.ToString());

            string libRs = await File.ReadAllTextAsync(Path.Combine(sdkDir, "src", "lib.rs"));
            string runtimeRs = await File.ReadAllTextAsync(Path.Combine(sdkDir, "src", "runtime.rs"));
            Assert.That(libRs, Does.Contain("#![forbid(unsafe_code)]"));
            Assert.That(runtimeRs, Does.Not.Contain("unsafe "));

            Directory.CreateDirectory(Path.Combine(sdkDir, "tests"));
            await File.WriteAllTextAsync(Path.Combine(sdkDir, "tests", "safe_vpi.rs"), """
                use std::cell::RefCell;
                use std::rc::Rc;
                use velosim_vpi_dut::runtime::W64;
                use velosim_vpi_dut::vpi::{VpiDirection, VpiSession};
                use velosim_vpi_dut::Vvpi_dut;

                #[test]
                fn safe_vpi_full_lifecycle() {
                    let mut sim = VpiSession::new(Vvpi_dut::new());

                    // Lookup via short, module-qualified, and TOP-qualified names.
                    let clk = sim.signal_by_name("clk").unwrap();
                    let rst = sim.signal_by_name("vpi_dut.rst").unwrap();
                    let en = sim.signal_by_name("TOP.vpi_dut.en").unwrap();
                    let q = sim.signal_by_name("q").unwrap();
                    let din = sim.signal_by_name("din").unwrap();
                    assert_eq!(sim.signals()[din].is_signed, true);
                    assert_eq!(sim.signals()[q].direction, VpiDirection::Output);
                    assert_eq!(sim.memories().len(), 1);
                    assert_eq!(sim.memories()[0].name, "ram");
                    assert_eq!(sim.memories()[0].depth, 4);

                    // Output port deposit must fail closed.
                    assert!(sim.put_value(q, W64::lit(7, 8)).is_err());

                    // Memory backdoor read/write.
                    sim.write_memory(0, 2, W64::lit(0x5A, 8)).unwrap();
                    assert_eq!(sim.read_memory(0, 2).unwrap().v, 0x5A);

                    let log: Rc<RefCell<Vec<String>>> = Rc::new(RefCell::new(Vec::new()));
                    let log_s = log.clone();
                    sim.on_start_of_simulation(move |ctx, ev| {
                        log_s.borrow_mut().push(format!("start@{}", ev.time));
                        ctx.put_value(rst, W64::lit(1, 1)).unwrap();
                        ctx.put_value(en, W64::lit(0, 1)).unwrap();
                    }).unwrap();

                    let log_vc = log.clone();
                    let vc_handle = sim.on_value_change(q, move |_ctx, ev| {
                        let v = ev.value.unwrap().v;
                        log_vc.borrow_mut().push(format!("q={}@{}", v, ev.time));
                    }).unwrap();

                    let log_rw = log.clone();
                    sim.on_read_write_synch(move |_ctx, ev| {
                        log_rw.borrow_mut().push(format!("rw@{}", ev.time));
                    }).unwrap();

                    let log_ro = log.clone();
                    sim.on_read_only_synch(move |ctx, ev| {
                        // ReadOnlySynch must reject deposits.
                        assert!(ctx.put_value(en, W64::lit(1, 1)).is_err());
                        log_ro.borrow_mut().push(format!("ro@{}", ev.time));
                    }).unwrap();

                    let log_delay = log.clone();
                    sim.on_after_delay(10, move |_ctx, ev| {
                        log_delay.borrow_mut().push(format!("delay@{}", ev.time));
                    }).unwrap();

                    let log_end = log.clone();
                    sim.on_end_of_simulation(move |ctx, ev| {
                        assert!(ctx.put_value(en, W64::lit(0, 1)).is_err());
                        log_end.borrow_mut().push(format!("end@{}", ev.time));
                    }).unwrap();

                    // Cycle 0 (reset)
                    sim.put_value(clk, W64::lit(0, 1)).unwrap();
                    sim.eval();
                    sim.put_value(clk, W64::lit(1, 1)).unwrap();
                    sim.eval();
                    sim.put_value(clk, W64::lit(0, 1)).unwrap();
                    sim.eval();
                    assert_eq!(sim.get_value(q).unwrap().v, 0);

                    // Enable counting for 3 cycles
                    sim.put_value(rst, W64::lit(0, 1)).unwrap();
                    sim.put_value(en, W64::lit(1, 1)).unwrap();
                    for _ in 0..3 {
                        sim.time_inc(5);
                        sim.put_value(clk, W64::lit(1, 1)).unwrap();
                        sim.eval();
                        sim.put_value(clk, W64::lit(0, 1)).unwrap();
                        sim.eval();
                    }
                    assert_eq!(sim.get_value(q).unwrap().v, 3);

                    // Force and release output q
                    sim.force_value(q, W64::lit(0x77, 8)).unwrap();
                    sim.put_value(clk, W64::lit(1, 1)).unwrap();
                    sim.eval();
                    assert_eq!(sim.get_value(q).unwrap().v, 0x77);
                    sim.put_value(clk, W64::lit(0, 1)).unwrap();
                    sim.eval();
                    sim.release_value(q).unwrap();

                    // Remove value change callback and verify no more q events
                    assert!(sim.remove_cb(vc_handle));
                    let before_len = log.borrow().len();
                    sim.put_value(clk, W64::lit(1, 1)).unwrap();
                    sim.eval();
                    assert_eq!(sim.get_value(q).unwrap().v, 0x78);
                    assert_eq!(log.borrow().len(), before_len);

                    sim.final_model();

                    let events = log.borrow();
                    assert_eq!(events[0], "start@0");
                    assert!(events.iter().any(|e| e == "rw@0"));
                    assert!(events.iter().any(|e| e == "ro@0"));
                    assert!(events.iter().any(|e| e == "delay@10"));
                    assert!(events.iter().any(|e| e == "q=1@5"));
                    assert!(events.iter().any(|e| e == "q=2@10"));
                    assert!(events.iter().any(|e| e == "q=3@15"));
                    assert_eq!(events.last().unwrap(), "end@15");
                }
                """);

            var (code, stdout, stderr) = await RunAsync("cargo", ["test", "--release"], sdkDir);
            Assert.That(code, Is.Zero, stderr + "\n" + stdout);
            Assert.That(stdout, Does.Contain("safe_vpi_full_lifecycle ... ok"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Test]
    public async Task RustCAbiVpiLoadsRealCPluginAndRunsStandaloneCHostAgainstCdylib()
    {
        if (!ToolExists("cargo") || !ToolExists("gcc")) Assert.Ignore("cargo or gcc not installed on PATH.");
        string root = Path.Combine(Path.GetTempPath(), "velosim-rs-cabi-vpi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string irPath = Path.Combine(root, "vpi_dut.rtl.json");
            string sdkDir = Path.Combine(root, "sdk");
            await File.WriteAllTextAsync(irPath, VpiDutIr);

            var err = new StringWriter();
            Assert.That(await ToolCli.RunAsync(["rtl-export", irPath, "--top", "vpi_dut", "--output-dir", sdkDir, "--target", "rust", "--vpi"], new StringWriter(), err), Is.Zero, err.ToString());

            // Core crate MUST still enforce #![forbid(unsafe_code)]!
            string coreLib = await File.ReadAllTextAsync(Path.Combine(sdkDir, "src", "lib.rs"));
            Assert.That(coreLib, Does.Contain("#![forbid(unsafe_code)]"));

            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sdkDir, "external-model.json")));
            Assert.That(manifest.RootElement.GetProperty("execution_scope").GetString(), Does.Contain("rust; four-state; vpi;"));

            // 1. Compile a real C VPI plugin exporting vlog_startup_routines against sdk/vpi_user.h.
            string pluginC = Path.Combine(root, "plugin.c");
            string pluginSo = Path.Combine(root, "libtest_vpi_plugin.so");
            await File.WriteAllTextAsync(pluginC, """
                #include "vpi_user.h"
                #include <stdio.h>
                #include <string.h>

                static int g_start = 0;
                static int g_end = 0;
                static int g_delay = 0;
                static int g_rw = 0;
                static int g_ro = 0;
                static int g_vc_hits = 0;
                static int g_last_q = -1;
                static int g_ro_rejected_put = 0;
                static int g_meta_ok = 0;
                static int g_mem_ok = 0;
                static int g_plusargs_ok = 0;
                static int g_formats_ok = 0;

                static PLI_INT32 on_vc(p_cb_data cb) {
                    if (cb && cb->value && cb->value->format == vpiIntVal) {
                        g_vc_hits++;
                        g_last_q = cb->value->value.integer;
                    }
                    return 0;
                }

                static PLI_INT32 on_rw(p_cb_data cb) {
                    (void)cb;
                    g_rw++;
                    vpiHandle dinh = vpi_handle_by_name("vpi_dut.din", NULL);
                    s_vpi_value fv;
                    fv.format = vpiDecStrVal;
                    vpi_get_value(dinh, &fv);
                    int ok_fmt = !vpi_chk_error(NULL) && strcmp(fv.value.str, "-2") == 0;
                    fv.format = vpiVectorVal;
                    vpi_get_value(dinh, &fv);
                    ok_fmt = ok_fmt && !vpi_chk_error(NULL) && fv.value.vector[0].aval == 0xFE && fv.value.vector[0].bval == 0;
                    fv.format = vpiBinStrVal;
                    vpi_get_value(dinh, &fv);
                    ok_fmt = ok_fmt && !vpi_chk_error(NULL) && strcmp(fv.value.str, "11111110") == 0;
                    g_formats_ok = ok_fmt ? 1 : 0;
                    vpi_free_object(dinh);
                    return 0;
                }

                static PLI_INT32 on_ro(p_cb_data cb) {
                    (void)cb;
                    g_ro++;
                    vpiHandle en = vpi_handle_by_name("vpi_dut.en", NULL);
                    s_vpi_value v;
                    v.format = vpiIntVal;
                    v.value.integer = 0;
                    vpi_put_value(en, &v, NULL, vpiNoDelay);
                    if (vpi_chk_error(NULL)) g_ro_rejected_put = 1;
                    vpi_free_object(en);
                    return 0;
                }

                static PLI_INT32 on_delay(p_cb_data cb) {
                    (void)cb;
                    g_delay++;
                    return 0;
                }

                static PLI_INT32 on_end(p_cb_data cb) {
                    (void)cb;
                    g_end++;
                    printf("PLUGIN_SUMMARY start=%d end=%d delay=%d rw=%d ro=%d vc=%d last_q=%d ro_rej=%d meta=%d mem=%d pa=%d fmt=%d\n",
                           g_start, g_end, g_delay, g_rw, g_ro, g_vc_hits, g_last_q, g_ro_rejected_put, g_meta_ok, g_mem_ok, g_plusargs_ok, g_formats_ok);
                    fflush(stdout);
                    return 0;
                }

                static PLI_INT32 on_start(p_cb_data cb) {
                    (void)cb;
                    g_start++;

                    // Check vlog_info plusargs
                    s_vpi_vlog_info vi;
                    memset(&vi, 0, sizeof(vi));
                    if (vpi_get_vlog_info(&vi) && vi.argc == 2 &&
                        strcmp(vi.argv[0], "+MODE=rust_vpi") == 0 &&
                        strcmp(vi.argv[1], "+SEED=77") == 0) {
                        g_plusargs_ok = 1;
                    }

                    // Check module, port, reg metadata and handle identity
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
                        vpi_get(vpiSize, qh) == 8 &&
                        vpi_get(vpiVector, qh) == 1 &&
                        vpi_get(vpiScalar, clkh) == 1 &&
                        vpi_get(vpiSigned, dinh) == 1 &&
                        vpi_get(vpiDirection, qh) == vpiOutput) {
                        g_meta_ok = 1;
                    }

                    // Deposit hex value during StartOfSimulation (queued into pending, verified in on_rw)
                    s_vpi_value fv;
                    fv.format = vpiHexStrVal;
                    fv.value.str = "fe";
                    vpi_put_value(dinh, &fv, NULL, vpiNoDelay);

                    // Check memory & memory word iteration + read/write
                    vpiHandle mit = vpi_iterate(vpiMemory, mod);
                    vpiHandle mh = vpi_scan(mit);
                    if (mh && vpi_get(vpiType, mh) == vpiMemory && vpi_get(vpiSize, mh) == 4) {
                        vpiHandle wit = vpi_iterate(vpiMemoryWord, mh);
                        vpiHandle w;
                        int idx = 0;
                        while ((w = vpi_scan(wit)) != NULL) {
                            if (idx == 2) {
                                s_vpi_value mv;
                                mv.format = vpiIntVal;
                                mv.value.integer = 0xA5;
                                vpi_put_value(w, &mv, NULL, vpiNoDelay);
                                mv.value.integer = 0;
                                vpi_get_value(w, &mv);
                                if (!vpi_chk_error(NULL) && mv.value.integer == 0xA5) g_mem_ok = 1;
                            }
                            vpi_free_object(w);
                            idx++;
                        }
                        vpi_free_object(mh);
                    }

                    // Register ValueChange on q, ReadWriteSynch, ReadOnlySynch, and AfterDelay(10)
                    s_vpi_value vc_val;
                    memset(&vc_val, 0, sizeof(vc_val));
                    vc_val.format = vpiIntVal;
                    s_cb_data vc_cb;
                    memset(&vc_cb, 0, sizeof(vc_cb));
                    vc_cb.reason = cbValueChange;
                    vc_cb.cb_rtn = on_vc;
                    vc_cb.obj = qh;
                    vc_cb.value = &vc_val;
                    vpi_register_cb(&vc_cb);

                    s_cb_data rw_cb;
                    memset(&rw_cb, 0, sizeof(rw_cb));
                    rw_cb.reason = cbReadWriteSynch;
                    rw_cb.cb_rtn = on_rw;
                    vpi_register_cb(&rw_cb);

                    s_cb_data ro_cb;
                    memset(&ro_cb, 0, sizeof(ro_cb));
                    ro_cb.reason = cbReadOnlySynch;
                    ro_cb.cb_rtn = on_ro;
                    vpi_register_cb(&ro_cb);

                    s_vpi_time dt = { vpiSimTime, 0, 10, 0.0 };
                    s_cb_data d_cb;
                    memset(&d_cb, 0, sizeof(d_cb));
                    d_cb.reason = cbAfterDelay;
                    d_cb.cb_rtn = on_delay;
                    d_cb.time = &dt;
                    vpi_register_cb(&d_cb);

                    vpi_free_object(clkh);
                    vpi_free_object(dinh);
                    vpi_free_object(qh);
                    vpi_free_object(mod2);
                    vpi_free_object(mod);
                    return 0;
                }

                static void register_plugin(void) {
                    s_cb_data scb;
                    memset(&scb, 0, sizeof(scb));
                    scb.reason = cbStartOfSimulation;
                    scb.cb_rtn = on_start;
                    vpi_register_cb(&scb);

                    s_cb_data ecb;
                    memset(&ecb, 0, sizeof(ecb));
                    ecb.reason = cbEndOfSimulation;
                    ecb.cb_rtn = on_end;
                    vpi_register_cb(&ecb);
                }

                void (*vlog_startup_routines[])(void) = { register_plugin, NULL };
                """);

            string[] pluginArgs = OperatingSystem.IsMacOS()
                ? ["-shared", "-fPIC", "-O2", "-undefined", "dynamic_lookup", $"-I{sdkDir}", pluginC, "-o", pluginSo]
                : ["-shared", "-fPIC", "-O2", $"-I{sdkDir}", pluginC, "-o", pluginSo];
            var (gccCode, gccOut, gccErr) = await RunAsync("gcc", pluginArgs, root);
            Assert.That(gccCode, Is.Zero, gccErr + "\n" + gccOut);

            // 2. Build the Rust workspace (both core crate and vpi_ffi cdylib/rlib).
            var (buildCode, buildOut, buildErr) = await RunAsync("cargo", ["build", "--release", "--workspace"], sdkDir);
            Assert.That(buildCode, Is.Zero, buildErr + "\n" + buildOut);

            string cdylibDir = Path.Combine(sdkDir, "target", "release");
            string cdylibExt = OperatingSystem.IsMacOS() ? ".dylib" : ".so";
            string cdylibPath = Path.Combine(cdylibDir, "libvelosim_vpi_dut_vpi" + cdylibExt);
            Assert.That(File.Exists(cdylibPath), Is.True, "Expected Rust VPI cdylib at " + cdylibPath);

            // 3. Test loading the C plugin directly from a Rust test inside vpi_ffi!
            Directory.CreateDirectory(Path.Combine(sdkDir, "vpi_ffi", "tests"));
            await File.WriteAllTextAsync(Path.Combine(sdkDir, "vpi_ffi", "tests", "plugin_load.rs"), $$"""
                use velosim_vpi_dut_vpi::{vpi_chk_error, vpi_control, vpi_handle_by_name, vpi_put_value, s_vpi_value, u_vpi_value, vpiFinish, vpiForceFlag, vpiIntVal, vpiReleaseFlag, VpiHost};
                use std::ffi::CString;
                use std::ptr;

                #[test]
                fn loads_c_vpi_plugin_and_runs_callbacks() {
                    let mut host = VpiHost::new();
                    host.dut_mut().set_plusargs(["MODE=rust_vpi", "SEED=77"]);
                    host.load_vpi({{JsonSerializer.Serialize(pluginSo)}}).expect("load_vpi failed");

                    host.dut_mut().set_rst(1);
                    host.dut_mut().set_en(0);
                    host.dut_mut().set_clk(0);
                    host.eval();
                    host.dut_mut().set_clk(1);
                    host.eval();
                    host.dut_mut().set_clk(0);
                    host.eval();
                    assert_eq!(host.dut().q(), 0);

                    host.dut_mut().set_rst(0);
                    host.dut_mut().set_en(1);
                    for _ in 0..3 {
                        host.time_inc(5);
                        host.dut_mut().set_clk(1);
                        host.eval();
                        host.dut_mut().set_clk(0);
                        host.eval();
                    }
                    assert_eq!(host.dut().q(), 3);

                    // Exercise VPI force / release and vpi_control(vpiFinish) through C ABI
                    unsafe {
                        host.use_vpi();
                        let cname = CString::new("vpi_dut.q").unwrap();
                        let qh = vpi_handle_by_name(cname.as_ptr() as *mut _, ptr::null_mut());
                        assert!(!qh.is_null());
                        let mut fv = s_vpi_value { format: vpiIntVal, value: u_vpi_value { integer: 99 } };
                        vpi_put_value(qh, &mut fv, ptr::null_mut(), vpiForceFlag);
                        assert_eq!(vpi_chk_error(ptr::null_mut()), 0);
                        assert_eq!(host.dut().q(), 99);
                        vpi_put_value(qh, ptr::null_mut(), ptr::null_mut(), vpiReleaseFlag);
                        assert_eq!(vpi_chk_error(ptr::null_mut()), 0);
                        assert_eq!(vpi_control(vpiFinish), 1);
                        assert!(host.got_finish());
                    }

                    host.final_model();
                }
                """);

            var (rsTestCode, rsTestOut, rsTestErr) = await RunAsync("cargo", ["test", "--release", "-p", "velosim_vpi_dut_vpi", "--", "--nocapture"], sdkDir);
            Assert.That(rsTestCode, Is.Zero, rsTestErr + "\n" + rsTestOut);
            Assert.That(rsTestOut, Does.Contain("loads_c_vpi_plugin_and_runs_callbacks ... ok"));
            Assert.That(rsTestOut, Does.Contain("PLUGIN_SUMMARY start=1 end=1 delay=1 rw=1 ro=1 vc=4 last_q=3 ro_rej=1 meta=1 mem=1 pa=1 fmt=1"));

            // 4. Also compile & run a standalone C host executable linked directly against libvelosim_vpi_dut_vpi.so!
            string cHostSrc = Path.Combine(root, "c_host.c");
            string cHostBin = Path.Combine(root, "c_host");
            await File.WriteAllTextAsync(cHostSrc, $$"""
                #include "vpi_user.h"
                #include <stdint.h>
                #include <stdio.h>
                #include <stdlib.h>

                extern void* velosim_vpi_create(void);
                extern void velosim_vpi_add_plusarg(void* host, const char* arg);
                extern PLI_INT32 velosim_vpi_load_plugin(void* host, const char* path);
                extern PLI_INT32 velosim_vpi_eval(void* host);
                extern void velosim_vpi_time_inc(void* host, uint64_t delta);
                extern void velosim_vpi_final(void* host);
                extern void velosim_vpi_destroy(void* host);

                static void put_i(vpiHandle h, int v) {
                    s_vpi_value val;
                    val.format = vpiIntVal;
                    val.value.integer = v;
                    vpi_put_value(h, &val, NULL, vpiNoDelay);
                }

                static int get_i(vpiHandle h) {
                    s_vpi_value val;
                    val.format = vpiIntVal;
                    vpi_get_value(h, &val);
                    return val.value.integer;
                }

                int main(void) {
                    void* host = velosim_vpi_create();
                    velosim_vpi_add_plusarg(host, "MODE=rust_vpi");
                    velosim_vpi_add_plusarg(host, "SEED=77");
                    if (!velosim_vpi_load_plugin(host, {{JsonSerializer.Serialize(pluginSo)}})) {
                        fprintf(stderr, "load_plugin failed\n");
                        return 1;
                    }
                    vpiHandle clk = vpi_handle_by_name("vpi_dut.clk", NULL);
                    vpiHandle rst = vpi_handle_by_name("vpi_dut.rst", NULL);
                    vpiHandle en  = vpi_handle_by_name("vpi_dut.en", NULL);
                    vpiHandle q   = vpi_handle_by_name("vpi_dut.q", NULL);

                    put_i(rst, 1); put_i(en, 0); put_i(clk, 0); velosim_vpi_eval(host);
                    put_i(clk, 1); velosim_vpi_eval(host);
                    put_i(clk, 0); velosim_vpi_eval(host);
                    put_i(rst, 0); put_i(en, 1);
                    for (int i = 0; i < 4; ++i) {
                        velosim_vpi_time_inc(host, 5);
                        put_i(clk, 1); velosim_vpi_eval(host);
                        put_i(clk, 0); velosim_vpi_eval(host);
                    }
                    int qv = get_i(q);
                    velosim_vpi_final(host);
                    velosim_vpi_destroy(host);
                    printf("CHOST_Q=%d\n", qv);
                    return qv == 4 ? 0 : 2;
                }
                """);

            var (chBuildCode, chBuildOut, chBuildErr) = await RunAsync("gcc", [
                "-O2", $"-I{sdkDir}", cHostSrc, $"-L{cdylibDir}", "-lvelosim_vpi_dut_vpi", $"-Wl,-rpath,{cdylibDir}", "-o", cHostBin
            ], root);
            Assert.That(chBuildCode, Is.Zero, chBuildErr + "\n" + chBuildOut);

            var (chRunCode, chRunOut, chRunErr) = await RunAsync(cHostBin, [], root);
            Assert.That(chRunCode, Is.Zero, chRunErr + "\n" + chRunOut);
            Assert.That(chRunOut, Does.Contain("CHOST_Q=4"));
            Assert.That(chRunOut, Does.Contain("PLUGIN_SUMMARY start=1 end=1 delay=1 rw=1 ro=1 vc=5 last_q=4 ro_rej=1 meta=1 mem=1 pa=1 fmt=1"));
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
