namespace VeloSim;

public sealed partial class RtlDesign
{
    /// <summary>
    /// Emits the 100% safe Rust VPI introspection and callback runtime (<c>pub mod vpi</c>)
    /// embedded in <c>src/runtime.rs</c>. Preserves <c>#![forbid(unsafe_code)]</c> with zero external crates.
    /// </summary>
    internal static string EmitRustSafeVpiRuntime() => """

pub mod vpi {
    use super::{w64, W64};

    #[derive(Copy, Clone, Debug, PartialEq, Eq)]
    #[repr(i32)]
    pub enum VpiDirection {
        Input = 1,
        Output = 2,
        Internal = 5,
    }

    #[derive(Copy, Clone, Debug, PartialEq, Eq)]
    pub struct VpiSignalInfo {
        pub id: usize,
        pub name: &'static str,
        pub width: i32,
        pub direction: VpiDirection,
        pub is_signed: bool,
        pub two_state: bool,
    }

    #[derive(Copy, Clone, Debug, PartialEq, Eq)]
    pub struct VpiMemoryInfo {
        pub id: usize,
        pub name: &'static str,
        pub depth: usize,
        pub width: i32,
        pub two_state: bool,
    }

    #[derive(Copy, Clone, Debug, PartialEq, Eq)]
    #[repr(i32)]
    pub enum VpiCbReason {
        ValueChange = 1,
        ReadWriteSynch = 6,
        ReadOnlySynch = 7,
        AfterDelay = 9,
        StartOfSimulation = 11,
        EndOfSimulation = 12,
    }

    #[derive(Copy, Clone, Debug, PartialEq, Eq, Hash)]
    pub struct VpiCbHandle(pub usize);

    #[derive(Clone, Debug, PartialEq, Eq)]
    pub struct VpiCbEvent {
        pub reason: VpiCbReason,
        pub signal_id: Option<usize>,
        pub time: u64,
        pub value: Option<W64>,
    }

    pub trait VpiModel {
        fn model_name(&self) -> &'static str;
        fn signals(&self) -> &'static [VpiSignalInfo];
        fn memories(&self) -> &'static [VpiMemoryInfo];

        fn signal_by_name(&self, name: &str) -> Option<usize> {
            let mut n = name;
            if let Some(rest) = n.strip_prefix("TOP.") {
                n = rest;
            }
            let top = self.model_name();
            if let Some(rest) = n.strip_prefix(top) {
                if let Some(local) = rest.strip_prefix('.') {
                    n = local;
                }
            }
            self.signals().iter().find(|s| s.name == n).map(|s| s.id)
        }

        fn get_signal(&self, id: usize) -> Result<W64, &'static str>;
        fn put_signal(&mut self, id: usize, value: W64) -> Result<(), &'static str>;
        fn force_signal_vpi(&mut self, id: usize, value: W64) -> Result<(), &'static str>;
        fn release_signal_vpi(&mut self, id: usize) -> Result<(), &'static str>;
        fn is_forced_vpi(&self, id: usize) -> bool;
        fn read_memory(&self, mem_id: usize, addr: usize) -> Result<W64, &'static str>;
        fn write_memory(&mut self, mem_id: usize, addr: usize, value: W64) -> Result<(), &'static str>;
        fn time_vpi(&self) -> u64;
        fn time_inc_vpi(&mut self, delta: u64);
        fn eval_vpi(&mut self) -> Result<(), &'static str>;
        fn final_vpi(&mut self);
        fn finish_vpi(&mut self);
        fn got_finish_vpi(&self) -> bool;
        fn plusargs_vpi(&self) -> &[String];
    }

    type SafeCb<M> = Box<dyn FnMut(&mut VpiContext<'_, M>, &VpiCbEvent)>;

    struct CbEntry<M: VpiModel> {
        handle: VpiCbHandle,
        reason: VpiCbReason,
        signal_id: Option<usize>,
        target_time: u64,
        previous: W64,
        active: bool,
        callback: Option<SafeCb<M>>,
    }

    pub struct VpiContext<'a, M: VpiModel> {
        session: &'a mut VpiSession<M>,
        read_only: bool,
        dispatching: bool,
    }

    impl<'a, M: VpiModel> VpiContext<'a, M> {
        pub fn dut(&self) -> &M { &self.session.dut }
        pub fn signals(&self) -> &'static [VpiSignalInfo] { self.session.dut.signals() }
        pub fn memories(&self) -> &'static [VpiMemoryInfo] { self.session.dut.memories() }
        pub fn signal_by_name(&self, name: &str) -> Option<usize> { self.session.dut.signal_by_name(name) }
        pub fn time(&self) -> u64 { self.session.dut.time_vpi() }
        pub fn got_finish(&self) -> bool { self.session.dut.got_finish_vpi() }
        pub fn get_value(&self, id: usize) -> Result<W64, &'static str> { self.session.dut.get_signal(id) }

        pub fn put_value(&mut self, id: usize, value: W64) -> Result<(), &'static str> {
            if self.read_only || self.session.finished || self.session.dut.got_finish_vpi() {
                return Err("model is finished or in read-only callback");
            }
            let sig = self.session.dut.signals().get(id).ok_or("invalid signal id")?;
            if sig.direction != VpiDirection::Input {
                return Err("only input ports may be deposited");
            }
            let mut val = w64::resize(value, sig.width);
            if sig.two_state {
                val = w64::two_state(val);
            }
            if self.session.dut.is_forced_vpi(id) {
                return Ok(());
            }
            if self.dispatching {
                self.session.queue_pending(id, val);
                Ok(())
            } else {
                self.session.dut.put_signal(id, val)
            }
        }

        pub fn force_value(&mut self, id: usize, value: W64) -> Result<(), &'static str> {
            if self.read_only || self.session.finished {
                return Err("model is finished or in read-only callback");
            }
            self.session.dut.force_signal_vpi(id, value)
        }

        pub fn release_value(&mut self, id: usize) -> Result<(), &'static str> {
            if self.read_only || self.session.finished {
                return Err("model is finished or in read-only callback");
            }
            self.session.dut.release_signal_vpi(id)
        }

        pub fn read_memory(&self, mem_id: usize, addr: usize) -> Result<W64, &'static str> {
            self.session.dut.read_memory(mem_id, addr)
        }

        pub fn write_memory(&mut self, mem_id: usize, addr: usize, value: W64) -> Result<(), &'static str> {
            if self.read_only || self.session.finished || self.session.dut.got_finish_vpi() {
                return Err("model is finished or in read-only callback");
            }
            self.session.dut.write_memory(mem_id, addr, value)
        }

        pub fn finish(&mut self) {
            self.session.dut.finish_vpi();
        }

        pub fn on_value_change<F>(&mut self, signal_id: usize, cb: F) -> Result<VpiCbHandle, &'static str>
        where
            F: FnMut(&mut VpiContext<'_, M>, &VpiCbEvent) + 'static,
        {
            self.session.on_value_change(signal_id, cb)
        }

        pub fn on_read_write_synch<F>(&mut self, cb: F) -> Result<VpiCbHandle, &'static str>
        where
            F: FnMut(&mut VpiContext<'_, M>, &VpiCbEvent) + 'static,
        {
            self.session.on_read_write_synch(cb)
        }

        pub fn on_read_only_synch<F>(&mut self, cb: F) -> Result<VpiCbHandle, &'static str>
        where
            F: FnMut(&mut VpiContext<'_, M>, &VpiCbEvent) + 'static,
        {
            self.session.on_read_only_synch(cb)
        }

        pub fn on_after_delay<F>(&mut self, delay: u64, cb: F) -> Result<VpiCbHandle, &'static str>
        where
            F: FnMut(&mut VpiContext<'_, M>, &VpiCbEvent) + 'static,
        {
            self.session.on_after_delay(delay, cb)
        }

        pub fn on_end_of_simulation<F>(&mut self, cb: F) -> Result<VpiCbHandle, &'static str>
        where
            F: FnMut(&mut VpiContext<'_, M>, &VpiCbEvent) + 'static,
        {
            self.session.on_end_of_simulation(cb)
        }

        pub fn remove_cb(&mut self, handle: VpiCbHandle) -> bool {
            self.session.remove_cb(handle)
        }
    }

    pub struct VpiSession<M: VpiModel> {
        dut: M,
        started: bool,
        finished: bool,
        next_handle: usize,
        callbacks: Vec<CbEntry<M>>,
        pending: Vec<(usize, W64)>,
        eval_requested: bool,
    }

    impl<M: VpiModel> VpiSession<M> {
        pub fn new(dut: M) -> Self {
            Self {
                dut,
                started: false,
                finished: false,
                next_handle: 1,
                callbacks: Vec::new(),
                pending: Vec::new(),
                eval_requested: false,
            }
        }

        pub fn into_inner(self) -> M { self.dut }
        pub fn dut(&self) -> &M { &self.dut }
        pub fn dut_mut(&mut self) -> &mut M { &mut self.dut }
        pub fn signals(&self) -> &'static [VpiSignalInfo] { self.dut.signals() }
        pub fn memories(&self) -> &'static [VpiMemoryInfo] { self.dut.memories() }
        pub fn signal_by_name(&self, name: &str) -> Option<usize> { self.dut.signal_by_name(name) }
        pub fn time(&self) -> u64 { self.dut.time_vpi() }
        pub fn time_inc(&mut self, delta: u64) { self.dut.time_inc_vpi(delta); }
        pub fn got_finish(&self) -> bool { self.dut.got_finish_vpi() }
        pub fn finish(&mut self) { self.dut.finish_vpi(); }
        pub fn get_value(&self, id: usize) -> Result<W64, &'static str> { self.dut.get_signal(id) }
        pub fn put_value(&mut self, id: usize, value: W64) -> Result<(), &'static str> { self.dut.put_signal(id, value) }
        pub fn force_value(&mut self, id: usize, value: W64) -> Result<(), &'static str> { self.dut.force_signal_vpi(id, value) }
        pub fn release_value(&mut self, id: usize) -> Result<(), &'static str> { self.dut.release_signal_vpi(id) }
        pub fn read_memory(&self, mem_id: usize, addr: usize) -> Result<W64, &'static str> { self.dut.read_memory(mem_id, addr) }
        pub fn write_memory(&mut self, mem_id: usize, addr: usize, value: W64) -> Result<(), &'static str> { self.dut.write_memory(mem_id, addr, value) }

        fn queue_pending(&mut self, id: usize, val: W64) {
            if let Some(entry) = self.pending.iter_mut().find(|(sid, _)| *sid == id) {
                entry.1 = val;
            } else {
                self.pending.push((id, val));
            }
        }

        fn register_inner<F>(
            &mut self,
            reason: VpiCbReason,
            signal_id: Option<usize>,
            target_time: u64,
            previous: W64,
            cb: F,
        ) -> Result<VpiCbHandle, &'static str>
        where
            F: FnMut(&mut VpiContext<'_, M>, &VpiCbEvent) + 'static,
        {
            if self.finished || self.dut.got_finish_vpi() {
                return Err("finished model");
            }
            if reason == VpiCbReason::StartOfSimulation && self.started {
                return Err("start callbacks must be registered before first eval");
            }
            let handle = VpiCbHandle(self.next_handle);
            self.next_handle += 1;
            self.callbacks.push(CbEntry {
                handle,
                reason,
                signal_id,
                target_time,
                previous,
                active: true,
                callback: Some(Box::new(cb)),
            });
            if matches!(reason, VpiCbReason::ReadOnlySynch | VpiCbReason::ReadWriteSynch)
                || (reason == VpiCbReason::AfterDelay && target_time <= self.dut.time_vpi())
            {
                self.eval_requested = true;
            }
            Ok(handle)
        }

        pub fn on_start_of_simulation<F>(&mut self, cb: F) -> Result<VpiCbHandle, &'static str>
        where
            F: FnMut(&mut VpiContext<'_, M>, &VpiCbEvent) + 'static,
        {
            self.register_inner(VpiCbReason::StartOfSimulation, None, self.dut.time_vpi(), W64::lit(0, 1), cb)
        }

        pub fn on_end_of_simulation<F>(&mut self, cb: F) -> Result<VpiCbHandle, &'static str>
        where
            F: FnMut(&mut VpiContext<'_, M>, &VpiCbEvent) + 'static,
        {
            self.register_inner(VpiCbReason::EndOfSimulation, None, self.dut.time_vpi(), W64::lit(0, 1), cb)
        }

        pub fn on_value_change<F>(&mut self, signal_id: usize, cb: F) -> Result<VpiCbHandle, &'static str>
        where
            F: FnMut(&mut VpiContext<'_, M>, &VpiCbEvent) + 'static,
        {
            let prev = self.dut.get_signal(signal_id)?;
            self.register_inner(VpiCbReason::ValueChange, Some(signal_id), self.dut.time_vpi(), prev, cb)
        }

        pub fn on_read_write_synch<F>(&mut self, cb: F) -> Result<VpiCbHandle, &'static str>
        where
            F: FnMut(&mut VpiContext<'_, M>, &VpiCbEvent) + 'static,
        {
            self.register_inner(VpiCbReason::ReadWriteSynch, None, self.dut.time_vpi(), W64::lit(0, 1), cb)
        }

        pub fn on_read_only_synch<F>(&mut self, cb: F) -> Result<VpiCbHandle, &'static str>
        where
            F: FnMut(&mut VpiContext<'_, M>, &VpiCbEvent) + 'static,
        {
            self.register_inner(VpiCbReason::ReadOnlySynch, None, self.dut.time_vpi(), W64::lit(0, 1), cb)
        }

        pub fn on_after_delay<F>(&mut self, delay: u64, cb: F) -> Result<VpiCbHandle, &'static str>
        where
            F: FnMut(&mut VpiContext<'_, M>, &VpiCbEvent) + 'static,
        {
            let target = self.dut.time_vpi().checked_add(delay).ok_or("cbAfterDelay time overflow")?;
            self.register_inner(VpiCbReason::AfterDelay, None, target, W64::lit(0, 1), cb)
        }

        pub fn remove_cb(&mut self, handle: VpiCbHandle) -> bool {
            if let Some(entry) = self.callbacks.iter_mut().find(|c| c.handle == handle) {
                entry.active = false;
                entry.callback = None;
                true
            } else {
                false
            }
        }

        fn invoke_handle(&mut self, handle: VpiCbHandle, event: VpiCbEvent, one_shot: bool, read_only: bool, dispatching: bool) {
            let idx = match self.callbacks.iter().position(|c| c.handle == handle && c.active) {
                Some(i) => i,
                None => return,
            };
            if one_shot {
                self.callbacks[idx].active = false;
            }
            let Some(mut cb) = self.callbacks[idx].callback.take() else { return; };
            {
                let mut ctx = VpiContext { session: self, read_only, dispatching };
                cb(&mut ctx, &event);
            }
            if !one_shot {
                if let Some(entry) = self.callbacks.iter_mut().find(|c| c.handle == handle && c.active) {
                    entry.callback = Some(cb);
                }
            }
        }

        pub fn try_eval(&mut self) -> Result<(), &'static str> {
            if self.finished || self.dut.got_finish_vpi() {
                return Ok(());
            }
            if self.callbacks.is_empty() && self.pending.is_empty() {
                self.started = true;
                self.eval_requested = false;
                return self.dut.eval_vpi();
            }

            if !self.started {
                self.started = true;
                let starts: Vec<VpiCbHandle> = self.callbacks.iter()
                    .filter(|c| c.active && c.reason == VpiCbReason::StartOfSimulation)
                    .map(|c| c.handle)
                    .collect();
                for h in starts {
                    let ev = VpiCbEvent { reason: VpiCbReason::StartOfSimulation, signal_id: None, time: self.dut.time_vpi(), value: None };
                    self.invoke_handle(h, ev, true, false, true);
                    if self.dut.got_finish_vpi() { return Ok(()); }
                }
            }

            for _round in 0..64 {
                self.eval_requested = false;
                let now = self.dut.time_vpi();
                let due: Vec<VpiCbHandle> = self.callbacks.iter()
                    .filter(|c| c.active && c.reason == VpiCbReason::AfterDelay && c.target_time <= now)
                    .map(|c| c.handle)
                    .collect();
                for h in due {
                    let ev = VpiCbEvent { reason: VpiCbReason::AfterDelay, signal_id: None, time: self.dut.time_vpi(), value: None };
                    self.invoke_handle(h, ev, true, false, true);
                    if self.dut.got_finish_vpi() { return Ok(()); }
                }

                let drained = std::mem::take(&mut self.pending);
                for (id, val) in drained {
                    if !self.dut.is_forced_vpi(id) {
                        let _ = self.dut.put_signal(id, val);
                    }
                }

                self.dut.eval_vpi()?;
                if self.dut.got_finish_vpi() { return Ok(()); }

                let mut changed: Vec<(VpiCbHandle, usize, W64)> = Vec::new();
                for c in self.callbacks.iter_mut() {
                    if c.active && c.reason == VpiCbReason::ValueChange {
                        if let Some(sid) = c.signal_id {
                            if let Ok(cur) = self.dut.get_signal(sid) {
                                if !w64::exact(cur, c.previous) {
                                    c.previous = cur;
                                    changed.push((c.handle, sid, cur));
                                }
                            }
                        }
                    }
                }
                for (h, sid, val) in changed {
                    let ev = VpiCbEvent { reason: VpiCbReason::ValueChange, signal_id: Some(sid), time: self.dut.time_vpi(), value: Some(val) };
                    self.invoke_handle(h, ev, false, false, true);
                    if self.dut.got_finish_vpi() { return Ok(()); }
                }

                let rw: Vec<VpiCbHandle> = self.callbacks.iter()
                    .filter(|c| c.active && c.reason == VpiCbReason::ReadWriteSynch)
                    .map(|c| c.handle)
                    .collect();
                for h in rw {
                    let ev = VpiCbEvent { reason: VpiCbReason::ReadWriteSynch, signal_id: None, time: self.dut.time_vpi(), value: None };
                    self.invoke_handle(h, ev, true, false, true);
                    if self.dut.got_finish_vpi() { return Ok(()); }
                }

                if self.pending.is_empty() && !self.eval_requested {
                    break;
                }
            }

            let ro: Vec<VpiCbHandle> = self.callbacks.iter()
                .filter(|c| c.active && c.reason == VpiCbReason::ReadOnlySynch)
                .map(|c| c.handle)
                .collect();
            for h in ro {
                let ev = VpiCbEvent { reason: VpiCbReason::ReadOnlySynch, signal_id: None, time: self.dut.time_vpi(), value: None };
                self.invoke_handle(h, ev, true, true, false);
                if self.dut.got_finish_vpi() { return Ok(()); }
            }

            self.callbacks.retain(|c| c.active);
            Ok(())
        }

        pub fn eval(&mut self) {
            self.try_eval().expect("VeloSim VPI evaluation failed");
        }

        pub fn final_model(&mut self) {
            if self.finished {
                return;
            }
            self.finished = true;
            let ends: Vec<VpiCbHandle> = self.callbacks.iter()
                .filter(|c| c.active && c.reason == VpiCbReason::EndOfSimulation)
                .map(|c| c.handle)
                .collect();
            for h in ends {
                let ev = VpiCbEvent { reason: VpiCbReason::EndOfSimulation, signal_id: None, time: self.dut.time_vpi(), value: None };
                self.invoke_handle(h, ev, true, true, false);
            }
            self.dut.final_vpi();
            self.callbacks.clear();
        }
    }
}
""";

    internal static string EmitRustVpiFfiCargoToml(string crateName) => $$"""
        [package]
        name = "{{crateName}}_vpi"
        version = "0.1.0"
        edition = "2021"

        [lib]
        crate-type = ["rlib", "cdylib", "staticlib"]

        [dependencies]
        {{crateName}} = { path = ".." }
        """;

    internal static string EmitRustVpiFfiBuildRs() => """
        fn main() {
            // Export #[no_mangle] vpi_* symbols from Linux binaries, tests, and cdylibs
            // so dynamically loaded C/C++ VPI plugins (vlog_startup_routines) resolve them.
            if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("linux") {
                println!("cargo:rustc-link-arg=-Wl,--export-dynamic");
            }
        }
        """;

    /// <summary>
    /// Emits the opt-in IEEE 1800 C-ABI VPI FFI companion crate (<c>vpi_ffi/src/lib.rs</c>)
    /// wrapping the <c>#![forbid(unsafe_code)]</c> Rust model crate.
    /// Zero external crates (<c>std</c> only); ABI-compatible with <c>vpi_user.h</c> and cocotb/PLI plugins.
    /// </summary>
    public static string EmitRustVpiFfi(string top)
    {
        string crateName = "velosim_" + top.ToLowerInvariant();
        string structName = "V" + top;
        return $$"""
// Auto-generated VeloSim IEEE 1800 C-ABI VPI bridge for {{structName}}.
// Core model crate `{{crateName}}` remains strictly `#![forbid(unsafe_code)]`.
#![allow(non_camel_case_types, non_upper_case_globals, non_snake_case, dead_code, clippy::all)]

use std::cell::{Cell, RefCell};
use std::collections::{HashMap, HashSet};
use std::ffi::{c_char, c_void, CStr, CString};
use std::ptr;
use std::sync::atomic::{AtomicU64, Ordering};

pub use {{crateName}}::runtime::{w64, W64};
pub use {{crateName}}::runtime::vpi::{VpiDirection, VpiMemoryInfo, VpiModel, VpiSignalInfo};
pub use {{crateName}}::{{structName}};

pub type PLI_INT32 = i32;
pub type PLI_UINT32 = u32;
pub type PLI_BYTE8 = c_char;
pub type vpiHandle = *mut PLI_UINT32;

#[repr(C)]
#[derive(Copy, Clone, Debug, Default)]
pub struct s_vpi_time {
    pub type_: PLI_INT32,
    pub high: PLI_UINT32,
    pub low: PLI_UINT32,
    pub real: f64,
}
pub type p_vpi_time = *mut s_vpi_time;

#[repr(C)]
#[derive(Copy, Clone, Debug, Default)]
pub struct s_vpi_vecval {
    pub aval: PLI_UINT32,
    pub bval: PLI_UINT32,
}
pub type p_vpi_vecval = *mut s_vpi_vecval;

#[repr(C)]
#[derive(Copy, Clone)]
pub union u_vpi_value {
    pub str: *mut PLI_BYTE8,
    pub scalar: PLI_INT32,
    pub integer: PLI_INT32,
    pub real: f64,
    pub time: p_vpi_time,
    pub vector: p_vpi_vecval,
    pub strength: *mut c_void,
    pub misc: *mut PLI_BYTE8,
}

#[repr(C)]
#[derive(Copy, Clone)]
pub struct s_vpi_value {
    pub format: PLI_INT32,
    pub value: u_vpi_value,
}
pub type p_vpi_value = *mut s_vpi_value;

#[repr(C)]
#[derive(Copy, Clone)]
pub struct s_cb_data {
    pub reason: PLI_INT32,
    pub cb_rtn: Option<unsafe extern "C" fn(*mut s_cb_data) -> PLI_INT32>,
    pub obj: vpiHandle,
    pub time: p_vpi_time,
    pub value: p_vpi_value,
    pub index: PLI_INT32,
    pub user_data: *mut PLI_BYTE8,
}
pub type p_cb_data = *mut s_cb_data;

#[repr(C)]
#[derive(Copy, Clone)]
pub struct s_vpi_error_info {
    pub state: PLI_INT32,
    pub level: PLI_INT32,
    pub message: *mut PLI_BYTE8,
    pub product: *mut PLI_BYTE8,
    pub code: *mut PLI_BYTE8,
    pub file: *mut PLI_BYTE8,
    pub line: PLI_INT32,
}
pub type p_vpi_error_info = *mut s_vpi_error_info;

#[repr(C)]
#[derive(Copy, Clone)]
pub struct s_vpi_vlog_info {
    pub argc: PLI_INT32,
    pub argv: *mut *mut PLI_BYTE8,
    pub product: *mut PLI_BYTE8,
    pub version: *mut PLI_BYTE8,
}
pub type p_vpi_vlog_info = *mut s_vpi_vlog_info;

pub const vpiIterator: PLI_INT32 = 27;
pub const vpiMemory: PLI_INT32 = 29;
pub const vpiMemoryWord: PLI_INT32 = 30;
pub const vpiModule: PLI_INT32 = 32;
pub const vpiPort: PLI_INT32 = 44;
pub const vpiReg: PLI_INT32 = 48;
pub const vpiLowConn: PLI_INT32 = 80;
pub const vpiScope: PLI_INT32 = 84;
pub const vpiCallback: PLI_INT32 = 107;
pub const vpiType: PLI_INT32 = 1;
pub const vpiName: PLI_INT32 = 2;
pub const vpiFullName: PLI_INT32 = 3;
pub const vpiSize: PLI_INT32 = 4;
pub const vpiTopModule: PLI_INT32 = 7;
pub const vpiDefName: PLI_INT32 = 9;
pub const vpiScalar: PLI_INT32 = 17;
pub const vpiVector: PLI_INT32 = 18;
pub const vpiDirection: PLI_INT32 = 20;
pub const vpiSigned: PLI_INT32 = 65;
pub const vpiInput: PLI_INT32 = 1;
pub const vpiOutput: PLI_INT32 = 2;
pub const vpiNoDirection: PLI_INT32 = 5;
pub const vpiBinStrVal: PLI_INT32 = 1;
pub const vpiOctStrVal: PLI_INT32 = 2;
pub const vpiDecStrVal: PLI_INT32 = 3;
pub const vpiHexStrVal: PLI_INT32 = 4;
pub const vpiScalarVal: PLI_INT32 = 5;
pub const vpiIntVal: PLI_INT32 = 6;
pub const vpiVectorVal: PLI_INT32 = 9;
pub const vpiSuppressVal: PLI_INT32 = 13;
pub const vpiNoDelay: PLI_INT32 = 1;
pub const vpiInertialDelay: PLI_INT32 = 2;
pub const vpiForceFlag: PLI_INT32 = 5;
pub const vpiReleaseFlag: PLI_INT32 = 6;
pub const vpi0: PLI_INT32 = 0;
pub const vpi1: PLI_INT32 = 1;
pub const vpiZ: PLI_INT32 = 2;
pub const vpiX: PLI_INT32 = 3;
pub const vpiSimTime: PLI_INT32 = 2;
pub const vpiSuppressTime: PLI_INT32 = 3;
pub const vpiRun: PLI_INT32 = 3;
pub const vpiError: PLI_INT32 = 3;
pub const vpiFinish: PLI_INT32 = 67;
pub const cbValueChange: PLI_INT32 = 1;
pub const cbReadWriteSynch: PLI_INT32 = 6;
pub const cbReadOnlySynch: PLI_INT32 = 7;
pub const cbAfterDelay: PLI_INT32 = 9;
pub const cbStartOfSimulation: PLI_INT32 = 11;
pub const cbEndOfSimulation: PLI_INT32 = 12;

#[cfg(unix)]
mod dynload {
    use std::ffi::{c_char, c_int, c_void, CStr, CString};
    const RTLD_NOW: c_int = 2;
    #[cfg(target_os = "macos")]
    const RTLD_LOCAL: c_int = 4;
    #[cfg(not(target_os = "macos"))]
    const RTLD_LOCAL: c_int = 0;
    extern "C" {
        fn dlopen(filename: *const c_char, flags: c_int) -> *mut c_void;
        fn dlsym(handle: *mut c_void, symbol: *const c_char) -> *mut c_void;
        fn dlerror() -> *const c_char;
    }
    pub unsafe fn open_library(path: &str) -> Result<*mut c_void, String> {
        let cpath = CString::new(path).map_err(|_| "NUL byte in plugin path".to_string())?;
        let h = dlopen(cpath.as_ptr(), RTLD_NOW | RTLD_LOCAL);
        if h.is_null() {
            let err = dlerror();
            let msg = if err.is_null() { "unknown dlopen error".to_string() } else { CStr::from_ptr(err).to_string_lossy().into_owned() };
            return Err(format!("cannot load VPI plugin: {}", msg));
        }
        Ok(h)
    }
    pub unsafe fn find_symbol(handle: *mut c_void, sym: &str) -> *mut c_void {
        let Ok(csym) = CString::new(sym) else { return std::ptr::null_mut(); };
        dlsym(handle, csym.as_ptr())
    }
}

#[cfg(windows)]
mod dynload {
    use std::ffi::{c_char, c_void, CString};
    #[link(name = "kernel32")]
    extern "system" {
        fn LoadLibraryA(lpLibFileName: *const c_char) -> *mut c_void;
        fn GetProcAddress(hModule: *mut c_void, lpProcName: *const c_char) -> *mut c_void;
    }
    pub unsafe fn open_library(path: &str) -> Result<*mut c_void, String> {
        let cpath = CString::new(path).map_err(|_| "NUL byte in plugin path".to_string())?;
        let h = LoadLibraryA(cpath.as_ptr());
        if h.is_null() { return Err("cannot load VPI plugin".to_string()); }
        Ok(h)
    }
    pub unsafe fn find_symbol(handle: *mut c_void, sym: &str) -> *mut c_void {
        let Ok(csym) = CString::new(sym) else { return std::ptr::null_mut(); };
        GetProcAddress(handle, csym.as_ptr())
    }
}

static NEXT_HOST_ID: AtomicU64 = AtomicU64::new(1);

struct HandleEntry {
    host: *mut VpiHost,
    host_id: u64,
    kind: PLI_INT32,
    id: usize,
    mem_index: usize,
    released: bool,
    active: bool,
    items: Vec<usize>,
    cursor: usize,
    item_kind: PLI_INT32,
    parent_id: usize,
    cb: s_cb_data,
    has_time: bool,
    cb_time: s_vpi_time,
    has_value: bool,
    cb_value: s_vpi_value,
    target_time: u64,
    previous: W64,
    order: usize,
}

struct VpiTls {
    handles: HashMap<usize, HandleEntry>,
    next_handle: usize,
    live_hosts: HashSet<u64>,
    error: Vec<u8>,
    result_string: Vec<u8>,
    result_vector: [s_vpi_vecval; 2],
    vlog_arg_strings: Vec<CString>,
    vlog_argv: Vec<*mut PLI_BYTE8>,
}

impl VpiTls {
    fn new() -> Self {
        Self {
            handles: HashMap::new(),
            next_handle: 1,
            live_hosts: HashSet::new(),
            error: Vec::new(),
            result_string: Vec::new(),
            result_vector: [s_vpi_vecval::default(); 2],
            vlog_arg_strings: Vec::new(),
            vlog_argv: Vec::new(),
        }
    }

    fn set_error(&mut self, msg: &str) {
        self.error.clear();
        self.error.extend_from_slice(msg.as_bytes());
        self.error.push(0);
    }

    fn clear_error(&mut self) {
        self.error.clear();
    }

    fn set_result_str(&mut self, s: &str) -> *mut PLI_BYTE8 {
        self.result_string.clear();
        self.result_string.extend_from_slice(s.as_bytes());
        self.result_string.push(0);
        self.result_string.as_mut_ptr() as *mut PLI_BYTE8
    }

    fn make_handle(&mut self, host: *mut VpiHost, host_id: u64, kind: PLI_INT32, id: usize, mem_index: usize) -> Result<vpiHandle, &'static str> {
        if self.handles.len() >= 65536 {
            return Err("VPI handle budget exceeded");
        }
        let key = self.next_handle;
        self.next_handle += 1;
        let zero_val = s_vpi_value { format: 0, value: u_vpi_value { integer: 0 } };
        let zero_cb = s_cb_data {
            reason: 0, cb_rtn: None, obj: ptr::null_mut(), time: ptr::null_mut(),
            value: ptr::null_mut(), index: 0, user_data: ptr::null_mut(),
        };
        self.handles.insert(key, HandleEntry {
            host, host_id, kind, id, mem_index, released: false, active: true,
            items: Vec::new(), cursor: 0, item_kind: 0, parent_id: 0,
            cb: zero_cb, has_time: false, cb_time: s_vpi_time::default(),
            has_value: false, cb_value: zero_val, target_time: 0,
            previous: W64::lit(0, 1), order: key,
        });
        Ok(key as vpiHandle)
    }

    fn get_handle(&self, h: vpiHandle) -> Result<&HandleEntry, &'static str> {
        let key = h as usize;
        let entry = self.handles.get(&key).ok_or("invalid, released or expired VPI handle")?;
        if entry.released || !self.live_hosts.contains(&entry.host_id) {
            return Err("invalid, released or expired VPI handle");
        }
        Ok(entry)
    }

    fn get_handle_mut(&mut self, h: vpiHandle) -> Result<&mut HandleEntry, &'static str> {
        let key = h as usize;
        let live = &self.live_hosts;
        let entry = self.handles.get_mut(&key).ok_or("invalid, released or expired VPI handle")?;
        if entry.released || !live.contains(&entry.host_id) {
            return Err("invalid, released or expired VPI handle");
        }
        Ok(entry)
    }
}

thread_local! {
    static CURRENT_HOST: Cell<(*mut VpiHost, u64)> = const { Cell::new((ptr::null_mut(), 0)) };
    static VPI_TLS: RefCell<VpiTls> = RefCell::new(VpiTls::new());
}

static PRODUCT_STR: &[u8] = b"VeloSim\0";
static VERSION_STR: &[u8] = b"rust.vpi.v1\0";
static CODE_STR: &[u8] = b"VS_VPI\0";

pub struct VpiHost {
    dut: {{structName}},
    host_id: u64,
    started: bool,
    finished: bool,
    evaluating: bool,
    read_only: bool,
    dispatching: bool,
    has_callbacks: bool,
    eval_requested: bool,
    pending: Vec<(usize, W64)>,
    plugins: Vec<*mut c_void>,
}

impl Drop for VpiHost {
    fn drop(&mut self) {
        let id = self.host_id;
        CURRENT_HOST.with(|c| {
            if c.get().1 == id {
                c.set((ptr::null_mut(), 0));
            }
        });
        VPI_TLS.with(|tls| {
            let mut t = tls.borrow_mut();
            t.live_hosts.remove(&id);
            t.handles.retain(|_, e| e.host_id != id);
        });
    }
}

struct Activation {
    prev: (*mut VpiHost, u64),
}

impl Activation {
    fn new(host: &mut VpiHost) -> Self {
        let prev = CURRENT_HOST.with(|c| c.get());
        host.use_vpi();
        Self { prev }
    }
}

impl Drop for Activation {
    fn drop(&mut self) {
        CURRENT_HOST.with(|c| c.set(self.prev));
    }
}

impl VpiHost {
    pub fn new() -> Box<Self> {
        let host_id = NEXT_HOST_ID.fetch_add(1, Ordering::Relaxed);
        VPI_TLS.with(|tls| tls.borrow_mut().live_hosts.insert(host_id));
        Box::new(Self {
            dut: {{structName}}::new(),
            host_id,
            started: false,
            finished: false,
            evaluating: false,
            read_only: false,
            dispatching: false,
            has_callbacks: false,
            eval_requested: false,
            pending: Vec::new(),
            plugins: Vec::new(),
        })
    }

    pub fn dut(&self) -> &{{structName}} { &self.dut }
    pub fn dut_mut(&mut self) -> &mut {{structName}} { &mut self.dut }
    pub fn time(&self) -> u64 { self.dut.time() }
    pub fn time_inc(&mut self, delta: u64) { self.dut.time_inc(delta); }
    pub fn got_finish(&self) -> bool { self.dut.got_finish() }

    pub fn use_vpi(&mut self) {
        let ptr: *mut VpiHost = self;
        let id = self.host_id;
        CURRENT_HOST.with(|c| c.set((ptr, id)));
    }

    fn queue_pending(&mut self, id: usize, value: W64) {
        if let Some(entry) = self.pending.iter_mut().find(|(sid, _)| *sid == id) {
            entry.1 = value;
        } else {
            self.pending.push((id, value));
        }
    }

    pub fn load_vpi(&mut self, path: &str) -> Result<(), String> {
        if self.evaluating || self.started || self.got_finish() || self.plugins.len() >= 64 {
            return Err("load VPI plugins before first eval; maximum 64".to_string());
        }
        let _active = Activation::new(self);
        unsafe {
            let module = dynload::open_library(path)?;
            self.plugins.push(module);
            let sym = dynload::find_symbol(module, "vlog_startup_routines");
            if sym.is_null() {
                return Err("VPI plugin lacks vlog_startup_routines".to_string());
            }
            let table = sym as *const Option<unsafe extern "C" fn()>;
            for i in 0..256usize {
                let func = *table.add(i);
                let Some(f) = func else { return Ok(()); };
                VPI_TLS.with(|tls| tls.borrow_mut().clear_error());
                f();
                let err = VPI_TLS.with(|tls| {
                    let t = tls.borrow();
                    if t.error.is_empty() { None } else { Some(CStr::from_bytes_with_nul(&t.error).map(|c| c.to_string_lossy().into_owned()).unwrap_or_default()) }
                });
                if let Some(msg) = err {
                    return Err(format!("VPI startup: {}", msg));
                }
            }
            Err("VPI startup routine budget exceeded".to_string())
        }
    }

    fn callbacks_for_host(&self, reason: PLI_INT32) -> Vec<usize> {
        let host_id = self.host_id;
        VPI_TLS.with(|tls| {
            let t = tls.borrow();
            let mut list: Vec<(usize, usize)> = t.handles.iter()
                .filter(|(_, e)| !e.released && e.active && e.host_id == host_id && e.kind == vpiCallback && e.cb.reason == reason)
                .map(|(&k, e)| (e.order, k))
                .collect();
            list.sort_by_key(|&(ord, _)| ord);
            list.into_iter().map(|(_, k)| k).collect()
        })
    }

    fn due_delay_callbacks(&self, now: u64) -> Vec<usize> {
        let host_id = self.host_id;
        VPI_TLS.with(|tls| {
            let t = tls.borrow();
            let mut list: Vec<(usize, usize)> = t.handles.iter()
                .filter(|(_, e)| !e.released && e.active && e.host_id == host_id && e.kind == vpiCallback && e.cb.reason == cbAfterDelay && e.target_time <= now)
                .map(|(&k, e)| (e.order, k))
                .collect();
            list.sort_by_key(|&(ord, _)| ord);
            list.into_iter().map(|(_, k)| k).collect()
        })
    }

    fn invoke_cb(&mut self, key: usize, value_override: Option<(W64, bool)>, one_shot: bool) {
        let now = self.dut.time();
        let prepared = VPI_TLS.with(|tls| {
            let mut t = tls.borrow_mut();
            let Some(e) = t.handles.get_mut(&key) else { return None; };
            if e.released || !e.active { return None; }
            if one_shot { e.active = false; }
            let cb_rtn = e.cb.cb_rtn?;
            let cb = e.cb;
            let mut time_copy = e.cb_time;
            let has_time = e.has_time;
            if has_time {
                time_copy.type_ = vpiSimTime;
                time_copy.high = (now >> 32) as PLI_UINT32;
                time_copy.low = (now & 0xFFFF_FFFF) as PLI_UINT32;
                time_copy.real = 0.0;
            }
            let has_val = e.has_value;
            let val_copy = e.cb_value;
            Some((cb_rtn, cb, has_time, time_copy, has_val, val_copy))
        });

        let Some((cb_rtn, mut cb, has_time, mut time_copy, has_val, mut val_copy)) = prepared else { return; };
        let mut str_buf: Vec<u8> = Vec::new();
        let mut vec_buf: [s_vpi_vecval; 2] = [s_vpi_vecval::default(); 2];
        if has_time {
            cb.time = &mut time_copy;
        }
        if has_val {
            if let Some((w, is_signed)) = value_override {
                if encode_w64(w, &mut val_copy, &mut str_buf, &mut vec_buf, is_signed).is_err() {
                    return;
                }
            }
            cb.value = &mut val_copy;
        }
        unsafe {
            let _ = cb_rtn(&mut cb);
        }
    }

    pub fn try_eval(&mut self) -> Result<(), String> {
        if self.evaluating {
            return Err("reentrant eval()".to_string());
        }
        if self.finished || self.dut.got_finish() {
            return Ok(());
        }
        if !self.has_callbacks && self.pending.is_empty() {
            self.started = true;
            self.eval_requested = false;
            return self.dut.try_eval().map_err(|e| e.to_string());
        }

        let _active = Activation::new(self);
        self.evaluating = true;

        let res = (|| -> Result<(), String> {
            if !self.started {
                self.started = true;
                self.dispatching = true;
                for key in self.callbacks_for_host(cbStartOfSimulation) {
                    self.invoke_cb(key, None, true);
                    if self.dut.got_finish() { self.dispatching = false; return Ok(()); }
                }
                self.dispatching = false;
            }

            for _round in 0..64 {
                self.eval_requested = false;
                self.dispatching = true;
                let now = self.dut.time();
                for key in self.due_delay_callbacks(now) {
                    self.invoke_cb(key, None, true);
                    if self.dut.got_finish() { self.dispatching = false; return Ok(()); }
                }
                self.dispatching = false;

                let drained = std::mem::take(&mut self.pending);
                for (id, val) in drained {
                    if !self.dut.is_forced_vpi(id) {
                        let _ = self.dut.put_signal(id, val);
                    }
                }

                self.dut.try_eval().map_err(|e| e.to_string())?;
                if self.dut.got_finish() { return Ok(()); }

                let mut changed: Vec<(usize, W64, bool)> = Vec::new();
                let vc_keys = self.callbacks_for_host(cbValueChange);
                for key in vc_keys {
                    let info = VPI_TLS.with(|tls| {
                        let t = tls.borrow();
                        let e = t.handles.get(&key)?;
                        if e.released || !e.active { return None; }
                        let obj = t.get_handle(e.cb.obj).ok()?;
                        Some((obj.id, e.previous))
                    });
                    if let Some((sig_id, prev)) = info {
                        if let Ok(cur) = self.dut.get_signal(sig_id) {
                            if !w64::exact(cur, prev) {
                                VPI_TLS.with(|tls| {
                                    if let Some(e) = tls.borrow_mut().handles.get_mut(&key) {
                                        e.previous = cur;
                                    }
                                });
                                let is_signed = self.dut.signals().get(sig_id).map(|s| s.is_signed).unwrap_or(false);
                                changed.push((key, cur, is_signed));
                            }
                        }
                    }
                }

                self.dispatching = true;
                for (key, cur, is_signed) in changed {
                    self.invoke_cb(key, Some((cur, is_signed)), false);
                    if self.dut.got_finish() { self.dispatching = false; return Ok(()); }
                }
                for key in self.callbacks_for_host(cbReadWriteSynch) {
                    self.invoke_cb(key, None, true);
                    if self.dut.got_finish() { self.dispatching = false; return Ok(()); }
                }
                self.dispatching = false;

                if self.pending.is_empty() && !self.eval_requested {
                    break;
                }
            }

            self.read_only = true;
            for key in self.callbacks_for_host(cbReadOnlySynch) {
                self.invoke_cb(key, None, true);
                if self.dut.got_finish() { self.read_only = false; return Ok(()); }
            }
            self.read_only = false;
            Ok(())
        })();

        self.evaluating = false;
        self.dispatching = false;
        self.read_only = false;
        res
    }

    pub fn eval(&mut self) {
        self.try_eval().expect("VeloSim VPI evaluation failed");
    }

    pub fn final_model(&mut self) {
        if self.finished {
            return;
        }
        let _active = Activation::new(self);
        self.finished = true;
        self.evaluating = true;
        self.read_only = true;
        for key in self.callbacks_for_host(cbEndOfSimulation) {
            self.invoke_cb(key, None, true);
        }
        self.dut.final_model();
        self.evaluating = false;
        self.read_only = false;
    }
}

fn selected_host() -> Result<(*mut VpiHost, u64), &'static str> {
    let (ptr, id) = CURRENT_HOST.with(|c| c.get());
    if ptr.is_null() {
        return Err("no active VeloSim model; call use_vpi()");
    }
    let alive = VPI_TLS.with(|tls| tls.borrow().live_hosts.contains(&id));
    if !alive {
        return Err("no active VeloSim model; call use_vpi()");
    }
    Ok((ptr, id))
}

fn validate_format(fmt: PLI_INT32) -> Result<(), &'static str> {
    match fmt {
        vpiBinStrVal | vpiOctStrVal | vpiDecStrVal | vpiHexStrVal | vpiVectorVal | vpiIntVal | vpiScalarVal | vpiSuppressVal => Ok(()),
        _ => Err("unsupported VPI value format"),
    }
}

fn radix_text(w: W64, group: i32) -> String {
    let mut out = String::new();
    let mut low = ((w.w - 1) / group) * group;
    loop {
        let bits = W64::mask(group.min(w.w - low));
        let x = (w.x >> (low as u32)) & bits;
        let z = (w.z >> (low as u32)) & bits;
        let v = (w.v >> (low as u32)) & bits;
        let ch = if x != 0 {
            if x == bits { 'x' } else { 'X' }
        } else if z != 0 {
            if z == bits { 'z' } else { 'Z' }
        } else {
            b"0123456789abcdef"[v as usize] as char
        };
        out.push(ch);
        if low == 0 { break; }
        low -= group;
    }
    out
}

fn radix_word(input: &str, width: i32, group: i32) -> Result<W64, &'static str> {
    if input.is_empty() { return Err("empty radix string"); }
    let limit = ((64 + group - 1) / group) as usize;
    if input.len() > limit { return Err("radix string exceeds 64-bit digit budget"); }
    let (mut v, mut x, mut z) = (0u64, 0u64, 0u64);
    let bits = W64::mask(group);
    for (i, raw) in input.bytes().enumerate() {
        let c = raw.to_ascii_lowercase();
        if i == 0 {
            if c == b'x' { x = u64::MAX; } else if c == b'z' { z = u64::MAX; }
        }
        v = v.wrapping_shl(group as u32);
        x = x.wrapping_shl(group as u32);
        z = z.wrapping_shl(group as u32);
        if c == b'x' {
            x |= bits;
        } else if c == b'z' {
            z |= bits;
        } else {
            let digit = match c {
                b'0'..=b'9' => (c - b'0') as u64,
                b'a'..=b'f' => (c - b'a' + 10) as u64,
                _ => return Err("invalid radix digit"),
            };
            if digit >= (1u64 << (group as u32)) {
                return Err("invalid radix digit");
            }
            v |= digit;
        }
    }
    Ok(W64::lit4(v, x, z, width))
}

fn decimal_word(input: &str, width: i32) -> Result<W64, &'static str> {
    if input.is_empty() || input.len() > 20 { return Err("decimal string requires 1..20 characters"); }
    if input.len() == 1 {
        match input.as_bytes()[0] {
            b'x' | b'X' => return Ok(W64::unknown(width)),
            b'z' | b'Z' => return Ok(W64::lit4(0, 0, u64::MAX, width)),
            _ => {}
        }
    }
    let val = input.parse::<u64>().map_err(|_| "unsigned decimal digit or overflow error")?;
    Ok(W64::lit(val, width))
}

fn encode_w64(
    w: W64,
    value: &mut s_vpi_value,
    str_buf: &mut Vec<u8>,
    vec_buf: &mut [s_vpi_vecval; 2],
    is_signed: bool,
) -> Result<(), &'static str> {
    validate_format(value.format)?;
    match value.format {
        vpiBinStrVal => {
            let s = w64::text(w);
            str_buf.clear();
            str_buf.extend_from_slice(s.as_bytes());
            str_buf.push(0);
            value.value.str = str_buf.as_mut_ptr() as *mut PLI_BYTE8;
        }
        vpiDecStrVal => {
            let m = W64::mask(w.w);
            let s = if w.x != 0 {
                if w.x == m { "x".to_string() } else { "X".to_string() }
            } else if w.z != 0 {
                if w.z == m { "z".to_string() } else { "Z".to_string() }
            } else if is_signed && w.w > 0 && ((w.v >> ((w.w - 1) as u32)) & 1) != 0 {
                let mag = (!w.v).wrapping_add(1) & m;
                format!("-{}", mag)
            } else {
                w.v.to_string()
            };
            str_buf.clear();
            str_buf.extend_from_slice(s.as_bytes());
            str_buf.push(0);
            value.value.str = str_buf.as_mut_ptr() as *mut PLI_BYTE8;
        }
        vpiOctStrVal => {
            let s = radix_text(w, 3);
            str_buf.clear();
            str_buf.extend_from_slice(s.as_bytes());
            str_buf.push(0);
            value.value.str = str_buf.as_mut_ptr() as *mut PLI_BYTE8;
        }
        vpiHexStrVal => {
            let s = radix_text(w, 4);
            str_buf.clear();
            str_buf.extend_from_slice(s.as_bytes());
            str_buf.push(0);
            value.value.str = str_buf.as_mut_ptr() as *mut PLI_BYTE8;
        }
        vpiVectorVal => {
            let a = w.v | w.x;
            let b = w.x | w.z;
            vec_buf[0] = s_vpi_vecval { aval: a as PLI_UINT32, bval: b as PLI_UINT32 };
            vec_buf[1] = s_vpi_vecval { aval: (a >> 32) as PLI_UINT32, bval: (b >> 32) as PLI_UINT32 };
            value.value.vector = vec_buf.as_mut_ptr();
        }
        vpiIntVal => {
            if (w.x | w.z) != 0 {
                return Err("X/Z integer read; request binary or vector format");
            }
            value.value.integer = (w.v as u32) as PLI_INT32;
        }
        vpiScalarVal => {
            if w.w != 1 {
                return Err("scalar format requires width 1");
            }
            value.value.scalar = if (w.x & 1) != 0 { vpiX } else if (w.z & 1) != 0 { vpiZ } else if (w.v & 1) != 0 { vpi1 } else { vpi0 };
        }
        vpiSuppressVal => {}
        _ => return Err("unsupported VPI value format"),
    }
    Ok(())
}

unsafe fn decode_vpi_value(value: p_vpi_value, width: i32) -> Result<W64, &'static str> {
    if value.is_null() {
        return Err("null VPI value");
    }
    let val = &*value;
    match val.format {
        vpiBinStrVal => {
            let sp = val.value.str;
            if sp.is_null() { return Err("null binary string"); }
            let s = CStr::from_ptr(sp).to_str().map_err(|_| "invalid UTF-8 in binary string")?;
            if s.len() != width as usize { return Err("binary deposit must match declared width"); }
            w64::parse(s, width)
        }
        vpiDecStrVal => {
            let sp = val.value.str;
            if sp.is_null() { return Err("null decimal string"); }
            let s = CStr::from_ptr(sp).to_str().map_err(|_| "invalid UTF-8 in decimal string")?;
            decimal_word(s, width)
        }
        vpiOctStrVal => {
            let sp = val.value.str;
            if sp.is_null() { return Err("null radix string"); }
            let s = CStr::from_ptr(sp).to_str().map_err(|_| "invalid UTF-8 in octal string")?;
            radix_word(s, width, 3)
        }
        vpiHexStrVal => {
            let sp = val.value.str;
            if sp.is_null() { return Err("null radix string"); }
            let s = CStr::from_ptr(sp).to_str().map_err(|_| "invalid UTF-8 in hex string")?;
            radix_word(s, width, 4)
        }
        vpiIntVal => {
            let i = val.value.integer;
            if width > 32 && i < 0 {
                return Err("negative integer deposit wider than 32 bits; use vector format");
            }
            Ok(W64::lit((i as u32) as u64, width))
        }
        vpiScalarVal => {
            let n = val.value.scalar;
            if width != 1 || !(0..=3).contains(&n) {
                return Err("invalid scalar deposit");
            }
            Ok(W64::lit4(if n == vpi1 { 1 } else { 0 }, if n == vpiX { 1 } else { 0 }, if n == vpiZ { 1 } else { 0 }, 1))
        }
        vpiVectorVal => {
            let vp = val.value.vector;
            if vp.is_null() { return Err("null vector deposit"); }
            let words = ((width + 31) / 32).clamp(1, 2) as usize;
            let (mut a, mut b) = (0u64, 0u64);
            for i in 0..words {
                let w = *vp.add(i);
                a |= (w.aval as u64) << (32 * i);
                b |= (w.bval as u64) << (32 * i);
            }
            Ok(W64::lit4(a & !b, a & b, !a & b, width))
        }
        _ => Err("unsupported VPI value format"),
    }
}

fn vpi_api<T, F: FnOnce() -> Result<T, &'static str>>(f: F, failure: T) -> T {
    VPI_TLS.with(|tls| tls.borrow_mut().clear_error());
    match f() {
        Ok(v) => v,
        Err(msg) => {
            VPI_TLS.with(|tls| tls.borrow_mut().set_error(msg));
            failure
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn vpi_handle_by_name(name: *mut PLI_BYTE8, scope: vpiHandle) -> vpiHandle {
    vpi_api(|| {
        if name.is_null() { return Err("null VPI name"); }
        let raw = CStr::from_ptr(name).to_str().map_err(|_| "invalid UTF-8 VPI name")?;
        let (host_ptr, host_id) = if scope.is_null() {
            selected_host()?
        } else {
            VPI_TLS.with(|tls| {
                let t = tls.borrow();
                let h = t.get_handle(scope)?;
                if h.kind != vpiModule { return Err("module scope required"); }
                Ok((h.host, h.host_id))
            })?
        };
        let host = &*host_ptr;
        let top = host.dut.model_name();
        let top_pref = format!("TOP.{}", top);
        if raw == top || raw == top_pref {
            return VPI_TLS.with(|tls| tls.borrow_mut().make_handle(host_ptr, host_id, vpiModule, 0, 0));
        }
        let mut n = raw;
        if let Some(rest) = n.strip_prefix("TOP.") { n = rest; }
        let dot_prefix = format!("{}.", top);
        if let Some(rest) = n.strip_prefix(&dot_prefix) {
            n = rest;
        } else if scope.is_null() {
            return Ok(ptr::null_mut());
        }
        for sig in host.dut.signals() {
            if sig.name == n {
                return VPI_TLS.with(|tls| tls.borrow_mut().make_handle(host_ptr, host_id, vpiReg, sig.id, 0));
            }
        }
        Ok(ptr::null_mut())
    }, ptr::null_mut())
}

#[no_mangle]
pub unsafe extern "C" fn vpi_handle(relation: PLI_INT32, object: vpiHandle) -> vpiHandle {
    vpi_api(|| {
        VPI_TLS.with(|tls| {
            let mut t = tls.borrow_mut();
            let (host, host_id, kind, id) = {
                let h = t.get_handle(object)?;
                (h.host, h.host_id, h.kind, h.id)
            };
            if (relation == vpiScope || relation == vpiModule)
                && matches!(kind, vpiReg | vpiPort | vpiMemory | vpiMemoryWord)
            {
                return t.make_handle(host, host_id, vpiModule, 0, 0);
            }
            if relation == vpiScope && kind == vpiModule {
                return Ok(ptr::null_mut());
            }
            if relation == vpiLowConn && kind == vpiPort {
                return t.make_handle(host, host_id, vpiReg, id, 0);
            }
            Err("unsupported VPI relation")
        })
    }, ptr::null_mut())
}

#[no_mangle]
pub unsafe extern "C" fn vpi_iterate(kind: PLI_INT32, scope: vpiHandle) -> vpiHandle {
    vpi_api(|| {
        if scope.is_null() {
            if kind != vpiModule {
                return Err("only top-module iteration allowed without scope");
            }
            let (host_ptr, host_id) = selected_host()?;
            return VPI_TLS.with(|tls| {
                let mut t = tls.borrow_mut();
                let key = t.make_handle(host_ptr, host_id, vpiIterator, 0, 0)?;
                let e = t.get_handle_mut(key)?;
                e.items = vec![0];
                e.item_kind = vpiModule;
                Ok(key)
            });
        }
        let (host_ptr, host_id, h_kind, h_id) = VPI_TLS.with(|tls| {
            let t = tls.borrow();
            let h = t.get_handle(scope)?;
            Ok::<_, &'static str>((h.host, h.host_id, h.kind, h.id))
        })?;
        let host = &*host_ptr;
        if h_kind == vpiMemory && kind == vpiMemoryWord {
            let depth = host.dut.memories().get(h_id).map(|m| m.depth).unwrap_or(0);
            if depth == 0 { return Ok(ptr::null_mut()); }
            return VPI_TLS.with(|tls| {
                let mut t = tls.borrow_mut();
                let key = t.make_handle(host_ptr, host_id, vpiIterator, 0, 0)?;
                let e = t.get_handle_mut(key)?;
                e.items = (0..depth).collect();
                e.item_kind = vpiMemoryWord;
                e.parent_id = h_id;
                Ok(key)
            });
        }
        if h_kind != vpiModule || !matches!(kind, vpiReg | vpiPort | vpiMemory) {
            return Err("unsupported VPI iterator");
        }
        let items: Vec<usize> = if kind == vpiMemory {
            (0..host.dut.memories().len()).collect()
        } else {
            host.dut.signals().iter()
                .filter(|s| kind == vpiReg || s.direction != VpiDirection::Internal)
                .map(|s| s.id)
                .collect()
        };
        if items.is_empty() { return Ok(ptr::null_mut()); }
        VPI_TLS.with(|tls| {
            let mut t = tls.borrow_mut();
            let key = t.make_handle(host_ptr, host_id, vpiIterator, 0, 0)?;
            let e = t.get_handle_mut(key)?;
            e.items = items;
            e.item_kind = kind;
            Ok(key)
        })
    }, ptr::null_mut())
}

#[no_mangle]
pub unsafe extern "C" fn vpi_scan(iterator: vpiHandle) -> vpiHandle {
    vpi_api(|| {
        VPI_TLS.with(|tls| {
            let mut t = tls.borrow_mut();
            let next = {
                let h = t.get_handle_mut(iterator)?;
                if h.kind != vpiIterator { return Err("iterator required"); }
                if h.cursor >= h.items.len() {
                    h.released = true;
                    return Ok(ptr::null_mut());
                }
                let item = h.items[h.cursor];
                h.cursor += 1;
                (h.host, h.host_id, h.item_kind, h.parent_id, item)
            };
            if next.2 == vpiMemoryWord {
                t.make_handle(next.0, next.1, vpiMemoryWord, next.3, next.4)
            } else {
                t.make_handle(next.0, next.1, next.2, next.4, 0)
            }
        })
    }, ptr::null_mut())
}

#[no_mangle]
pub unsafe extern "C" fn vpi_free_object(object: vpiHandle) -> PLI_INT32 {
    vpi_api(|| {
        VPI_TLS.with(|tls| {
            let mut t = tls.borrow_mut();
            let h = t.get_handle_mut(object)?;
            h.released = true;
            h.active = false;
            Ok(1)
        })
    }, 0)
}

#[no_mangle]
pub unsafe extern "C" fn vpi_compare_objects(left: vpiHandle, right: vpiHandle) -> PLI_INT32 {
    vpi_api(|| {
        VPI_TLS.with(|tls| {
            let t = tls.borrow();
            let a = t.get_handle(left)?;
            let b = t.get_handle(right)?;
            let eq = a.host_id == b.host_id && a.kind == b.kind && match a.kind {
                vpiReg | vpiPort | vpiModule | vpiMemory => a.id == b.id,
                vpiMemoryWord => a.id == b.id && a.mem_index == b.mem_index,
                _ => left == right,
            };
            Ok(if eq { 1 } else { 0 })
        })
    }, 0)
}

#[no_mangle]
pub unsafe extern "C" fn vpi_get(property: PLI_INT32, object: vpiHandle) -> PLI_INT32 {
    vpi_api(|| {
        let (host_ptr, kind, id) = VPI_TLS.with(|tls| {
            let t = tls.borrow();
            let h = t.get_handle(object)?;
            Ok::<_, &'static str>((h.host, h.kind, h.id))
        })?;
        if property == vpiType { return Ok(kind); }
        if property == vpiTopModule && kind == vpiModule { return Ok(1); }
        let host = &*host_ptr;
        if kind == vpiMemory {
            if property == vpiSize {
                return Ok(host.dut.memories().get(id).map(|m| m.depth as PLI_INT32).unwrap_or(0));
            }
            return Err("unsupported VPI property");
        }
        if kind == vpiMemoryWord {
            if property == vpiSize {
                return Ok(host.dut.memories().get(id).map(|m| m.width).unwrap_or(0));
            }
            return Err("unsupported VPI property");
        }
        if kind != vpiReg && kind != vpiPort {
            return Err("signal handle required");
        }
        let sig = host.dut.signals().get(id).ok_or("invalid signal id")?;
        match property {
            vpiScalar => Ok(if sig.width == 1 { 1 } else { 0 }),
            vpiVector => Ok(if sig.width > 1 { 1 } else { 0 }),
            vpiSigned => Ok(if sig.is_signed { 1 } else { 0 }),
            vpiSize => Ok(sig.width),
            vpiDirection => Ok(sig.direction as PLI_INT32),
            _ => Err("unsupported VPI property"),
        }
    }, 0)
}

#[no_mangle]
pub unsafe extern "C" fn vpi_get_str(property: PLI_INT32, object: vpiHandle) -> *mut PLI_BYTE8 {
    vpi_api(|| {
        let (host_ptr, kind, id, mem_index) = VPI_TLS.with(|tls| {
            let t = tls.borrow();
            let h = t.get_handle(object)?;
            Ok::<_, &'static str>((h.host, h.kind, h.id, h.mem_index))
        })?;
        let host = &*host_ptr;
        let top = host.dut.model_name();
        let text = match kind {
            vpiMemory => {
                let local = host.dut.memories().get(id).map(|m| m.name.to_string()).unwrap_or_else(|| format!("mem_{}", id));
                match property {
                    vpiName => local,
                    vpiFullName => format!("{}.{}", top, local),
                    _ => return Err("unsupported VPI string property"),
                }
            }
            vpiMemoryWord => {
                let base = host.dut.memories().get(id).map(|m| m.name.to_string()).unwrap_or_else(|| format!("mem_{}", id));
                let local = format!("{}[{}]", base, mem_index);
                match property {
                    vpiName => local,
                    vpiFullName => format!("{}.{}", top, local),
                    _ => return Err("unsupported VPI string property"),
                }
            }
            vpiModule => match property {
                vpiName | vpiFullName | vpiDefName => top.to_string(),
                _ => return Err("unsupported VPI string property"),
            },
            vpiReg | vpiPort => {
                let sig = host.dut.signals().get(id).ok_or("invalid signal id")?;
                match property {
                    vpiName => sig.name.to_string(),
                    vpiFullName => format!("{}.{}", top, sig.name),
                    _ => return Err("unsupported VPI string property"),
                }
            }
            _ => return Err("named object required"),
        };
        Ok(VPI_TLS.with(|tls| tls.borrow_mut().set_result_str(&text)))
    }, ptr::null_mut())
}

#[no_mangle]
pub unsafe extern "C" fn vpi_get_value(object: vpiHandle, value: p_vpi_value) {
    let _ = vpi_api(|| {
        if value.is_null() { return Err("null VPI value"); }
        let (host_ptr, kind, id, mem_index) = VPI_TLS.with(|tls| {
            let t = tls.borrow();
            let h = t.get_handle(object)?;
            Ok::<_, &'static str>((h.host, h.kind, h.id, h.mem_index))
        })?;
        let host = &*host_ptr;
        let (w, is_signed) = if kind == vpiMemoryWord {
            (host.dut.read_memory(id, mem_index)?, false)
        } else if kind == vpiReg || kind == vpiPort {
            let sig = host.dut.signals().get(id).ok_or("invalid signal id")?;
            (host.dut.get_signal(id)?, sig.is_signed)
        } else {
            return Err("signal handle required");
        };
        VPI_TLS.with(|tls| {
            let mut t = tls.borrow_mut();
            let VpiTls { ref mut result_string, ref mut result_vector, .. } = *t;
            encode_w64(w, &mut *value, result_string, result_vector, is_signed)
        })?;
        Ok(1)
    }, 0);
}

#[no_mangle]
pub unsafe extern "C" fn vpi_put_value(object: vpiHandle, value: p_vpi_value, time: p_vpi_time, flags: PLI_INT32) -> vpiHandle {
    vpi_api(|| {
        if !time.is_null() {
            let tm = &*time;
            if (tm.type_ != vpiSimTime && tm.type_ != vpiSuppressTime) || tm.high != 0 || tm.low != 0 {
                return Err("only immediate input deposits supported");
            }
        }
        let (host_ptr, kind, id, mem_index) = VPI_TLS.with(|tls| {
            let t = tls.borrow();
            let h = t.get_handle(object)?;
            Ok::<_, &'static str>((h.host, h.kind, h.id, h.mem_index))
        })?;
        let host = &mut *host_ptr;
        if host.finished || host.dut.got_finish() || host.read_only {
            return Err("model is finished or in read-only callback");
        }
        if flags == vpiReleaseFlag {
            if kind != vpiReg && kind != vpiPort { return Err("signal handle required"); }
            host.dut.release_signal_vpi(id)?;
            return Ok(ptr::null_mut());
        }
        if flags != vpiNoDelay && flags != vpiForceFlag {
            return Err("only immediate input deposits supported");
        }
        if value.is_null() {
            return Err("only immediate input deposits supported");
        }
        if kind == vpiMemoryWord {
            if flags != vpiNoDelay { return Err("force/release unsupported on memory words"); }
            let width = host.dut.memories().get(id).ok_or("invalid memory id")?.width;
            let w = decode_vpi_value(value, width)?;
            host.dut.write_memory(id, mem_index, w)?;
            return Ok(ptr::null_mut());
        }
        if kind != vpiReg && kind != vpiPort {
            return Err("signal handle required");
        }
        let sig = host.dut.signals().get(id).ok_or("invalid signal id")?;
        let mut w = decode_vpi_value(value, sig.width)?;
        if sig.two_state { w = w64::two_state(w); }
        if flags == vpiForceFlag {
            host.dut.force_signal_vpi(id, w)?;
        } else {
            if sig.direction != VpiDirection::Input {
                return Err("only input ports may be deposited");
            }
            if host.dut.is_forced_vpi(id) {
                return Ok(ptr::null_mut());
            }
            if host.dispatching {
                host.queue_pending(id, w);
            } else {
                host.dut.put_signal(id, w)?;
            }
        }
        Ok(ptr::null_mut())
    }, ptr::null_mut())
}

#[no_mangle]
pub unsafe extern "C" fn vpi_get_time(object: vpiHandle, time: p_vpi_time) {
    let _ = vpi_api(|| {
        if time.is_null() { return Err("null VPI time"); }
        let tm = &mut *time;
        if tm.type_ != vpiSimTime { return Err("only vpiSimTime supported"); }
        let host_ptr = if object.is_null() {
            selected_host()?.0
        } else {
            VPI_TLS.with(|tls| Ok::<_, &'static str>(tls.borrow().get_handle(object)?.host))?
        };
        let now = (&*host_ptr).dut.time();
        tm.high = (now >> 32) as PLI_UINT32;
        tm.low = (now & 0xFFFF_FFFF) as PLI_UINT32;
        tm.real = 0.0;
        Ok(1)
    }, 0);
}

#[no_mangle]
pub unsafe extern "C" fn vpi_register_cb(data: p_cb_data) -> vpiHandle {
    vpi_api(|| {
        if data.is_null() { return Err("callback routine required"); }
        let d = &*data;
        if d.cb_rtn.is_none() { return Err("callback routine required"); }
        let (host_ptr, host_id) = selected_host()?;
        let host = &mut *host_ptr;
        if host.finished || host.dut.got_finish() { return Err("finished model"); }
        if !matches!(d.reason, cbValueChange | cbReadOnlySynch | cbReadWriteSynch | cbAfterDelay | cbStartOfSimulation | cbEndOfSimulation) {
            return Err("unsupported callback reason");
        }
        let now = host.dut.time();
        let target_time = if d.reason == cbAfterDelay {
            if d.time.is_null() || (*d.time).type_ != vpiSimTime {
                return Err("cbAfterDelay requires vpiSimTime");
            }
            let delay = (((*d.time).high as u64) << 32) | ((*d.time).low as u64);
            now.checked_add(delay).ok_or("cbAfterDelay time overflow")?
        } else {
            if !d.time.is_null() {
                let tm = &*d.time;
                if (tm.type_ != vpiSimTime && tm.type_ != vpiSuppressTime) || tm.high != 0 || tm.low != 0 {
                    return Err("only zero-delay callbacks supported");
                }
            }
            now
        };
        if !d.value.is_null() {
            validate_format((*d.value).format)?;
        }
        let (obj_handle, previous) = if d.reason == cbValueChange {
            let (sig_host_id, sig_id) = VPI_TLS.with(|tls| {
                let t = tls.borrow();
                let h = t.get_handle(d.obj)?;
                if h.kind != vpiReg && h.kind != vpiPort { return Err("signal handle required"); }
                Ok::<_, &'static str>((h.host_id, h.id))
            })?;
            if sig_host_id != host_id { return Err("callback model mismatch"); }
            let prev = host.dut.get_signal(sig_id)?;
            if !d.value.is_null() && (*d.value).format == vpiScalarVal && prev.w != 1 {
                return Err("scalar callback format requires width 1");
            }
            let obj = VPI_TLS.with(|tls| tls.borrow_mut().make_handle(host_ptr, host_id, vpiReg, sig_id, 0))?;
            (obj, prev)
        } else {
            if !d.obj.is_null() { return Err("lifecycle callback object must be null"); }
            (ptr::null_mut(), W64::lit(0, 1))
        };
        if d.reason == cbStartOfSimulation && host.started {
            return Err("start callbacks must be registered before first eval");
        }
        let key = VPI_TLS.with(|tls| {
            let mut t = tls.borrow_mut();
            let handle = t.make_handle(host_ptr, host_id, vpiCallback, 0, 0)?;
            let e = t.get_handle_mut(handle)?;
            e.cb = *d;
            e.cb.obj = obj_handle;
            e.previous = previous;
            e.target_time = target_time;
            if !d.time.is_null() {
                e.has_time = true;
                e.cb_time = *d.time;
            }
            if !d.value.is_null() {
                e.has_value = true;
                e.cb_value = *d.value;
            }
            Ok::<_, &'static str>(handle)
        })?;
        host.has_callbacks = true;
        if matches!(d.reason, cbReadOnlySynch | cbReadWriteSynch) || (d.reason == cbAfterDelay && target_time <= now) {
            host.eval_requested = true;
        }
        Ok(key)
    }, ptr::null_mut())
}

#[no_mangle]
pub unsafe extern "C" fn vpi_remove_cb(object: vpiHandle) -> PLI_INT32 {
    vpi_api(|| {
        VPI_TLS.with(|tls| {
            let mut t = tls.borrow_mut();
            let h = t.get_handle_mut(object)?;
            if h.kind != vpiCallback { return Err("callback handle required"); }
            h.active = false;
            Ok(1)
        })
    }, 0)
}

#[no_mangle]
pub unsafe extern "C" fn vpi_chk_error(info: p_vpi_error_info) -> PLI_INT32 {
    VPI_TLS.with(|tls| {
        let mut t = tls.borrow_mut();
        if t.error.is_empty() { return 0; }
        if !info.is_null() {
            *info = s_vpi_error_info {
                state: vpiRun,
                level: vpiError,
                message: t.error.as_mut_ptr() as *mut PLI_BYTE8,
                product: PRODUCT_STR.as_ptr() as *mut PLI_BYTE8,
                code: CODE_STR.as_ptr() as *mut PLI_BYTE8,
                file: ptr::null_mut(),
                line: 0,
            };
        }
        vpiError
    })
}

#[no_mangle]
pub unsafe extern "C" fn vpi_control(operation: PLI_INT32) -> PLI_INT32 {
    vpi_api(|| {
        if operation != vpiFinish { return Err("only vpiFinish supported"); }
        let (host_ptr, _) = selected_host()?;
        (&mut *host_ptr).dut.finish();
        Ok(1)
    }, 0)
}

#[no_mangle]
pub unsafe extern "C" fn vpi_printf(format: *mut PLI_BYTE8) -> PLI_INT32 {
    if format.is_null() { return 0; }
    let s = CStr::from_ptr(format).to_string_lossy();
    print!("{}", s);
    s.len() as PLI_INT32
}

#[no_mangle]
pub unsafe extern "C" fn vpi_get_vlog_info(info: p_vpi_vlog_info) -> PLI_INT32 {
    vpi_api(|| {
        if info.is_null() { return Err("null vlog info"); }
        let plusargs: Vec<String> = match selected_host() {
            Ok((host_ptr, _)) => (&*host_ptr).dut.plusargs_vpi().to_vec(),
            Err(_) => Vec::new(),
        };
        VPI_TLS.with(|tls| {
            let mut t = tls.borrow_mut();
            let VpiTls { ref mut vlog_arg_strings, ref mut vlog_argv, .. } = *t;
            vlog_arg_strings.clear();
            vlog_argv.clear();
            for a in plusargs {
                if let Ok(cs) = CString::new(format!("+{}", a)) {
                    vlog_arg_strings.push(cs);
                }
            }
            for cs in vlog_arg_strings.iter() {
                vlog_argv.push(cs.as_ptr() as *mut PLI_BYTE8);
            }
            *info = s_vpi_vlog_info {
                argc: vlog_argv.len() as PLI_INT32,
                argv: if vlog_argv.is_empty() { ptr::null_mut() } else { vlog_argv.as_mut_ptr() },
                product: PRODUCT_STR.as_ptr() as *mut PLI_BYTE8,
                version: VERSION_STR.as_ptr() as *mut PLI_BYTE8,
            };
        });
        Ok(1)
    }, 0)
}

// C-ABI host driver functions for driving this Rust model from C/C++/Python/cocotb via cdylib.

#[no_mangle]
pub unsafe extern "C" fn velosim_vpi_create() -> *mut VpiHost {
    let mut host = VpiHost::new();
    host.use_vpi();
    Box::into_raw(host)
}

#[no_mangle]
pub unsafe extern "C" fn velosim_vpi_use(host: *mut VpiHost) {
    if !host.is_null() { (&mut *host).use_vpi(); }
}

#[no_mangle]
pub unsafe extern "C" fn velosim_vpi_load_plugin(host: *mut VpiHost, path: *const c_char) -> PLI_INT32 {
    if host.is_null() || path.is_null() { return 0; }
    let Ok(p) = CStr::from_ptr(path).to_str() else { return 0; };
    match (&mut *host).load_vpi(p) {
        Ok(()) => 1,
        Err(e) => {
            VPI_TLS.with(|tls| tls.borrow_mut().set_error(&e));
            0
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn velosim_vpi_add_plusarg(host: *mut VpiHost, arg: *const c_char) {
    if host.is_null() || arg.is_null() { return; }
    if let Ok(s) = CStr::from_ptr(arg).to_str() {
        (&mut *host).dut.add_plusarg(s);
    }
}

#[no_mangle]
pub unsafe extern "C" fn velosim_vpi_eval(host: *mut VpiHost) -> PLI_INT32 {
    if host.is_null() { return 0; }
    match (&mut *host).try_eval() {
        Ok(()) => 1,
        Err(e) => {
            VPI_TLS.with(|tls| tls.borrow_mut().set_error(&e));
            0
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn velosim_vpi_time_inc(host: *mut VpiHost, delta: u64) {
    if !host.is_null() { (&mut *host).time_inc(delta); }
}

#[no_mangle]
pub unsafe extern "C" fn velosim_vpi_final(host: *mut VpiHost) {
    if !host.is_null() { (&mut *host).final_model(); }
}

#[no_mangle]
pub unsafe extern "C" fn velosim_vpi_got_finish(host: *const VpiHost) -> PLI_INT32 {
    if host.is_null() { 0 } else if (&*host).got_finish() { 1 } else { 0 }
}

#[no_mangle]
pub unsafe extern "C" fn velosim_vpi_destroy(host: *mut VpiHost) {
    if !host.is_null() {
        drop(Box::from_raw(host));
    }
}
""";
    }
}
