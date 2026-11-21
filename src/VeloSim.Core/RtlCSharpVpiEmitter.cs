namespace VeloSim;

public sealed partial class RtlDesign
{
    /// <summary>
    /// Emits the 100% safe managed C# VPI introspection and callback runtime appended to
    /// <c>VeloSimRuntime.cs</c>. Zero <c>unsafe</c> code; zero overhead on <c>V&lt;top&gt;.Eval()</c>.
    /// </summary>
    internal static string EmitCSharpSafeVpiRuntime() => """

public enum VpiDirection
{
    Input = 1,
    Output = 2,
    Internal = 5
}

public readonly record struct VpiSignalInfo(int Id, string Name, int Width, VpiDirection Direction, bool IsSigned, bool TwoState);

public readonly record struct VpiMemoryInfo(int Id, string Name, int Depth, int Width, bool TwoState);

public enum VpiCbReason
{
    ValueChange = 1,
    ReadWriteSynch = 6,
    ReadOnlySynch = 7,
    AfterDelay = 9,
    StartOfSimulation = 11,
    EndOfSimulation = 12
}

public readonly record struct VpiCbHandle(int Id);

public readonly record struct VpiCbEvent(VpiCbReason Reason, int SignalId, ulong Time, W64? Value);

public interface IVpiModel
{
    string ModelName { get; }
    IReadOnlyList<VpiSignalInfo> Signals { get; }
    IReadOnlyList<VpiMemoryInfo> Memories { get; }
    IReadOnlyList<string> Plusargs { get; }
    ulong Time { get; }
    bool GotFinish { get; }
    void TimeInc(ulong delta);
    void Eval();
    void Final();
    void Finish();
    int SignalByName(string name);
    W64 GetSignal(int id);
    void PutSignal(int id, W64 value);
    void ForceSignal(int id, W64 value);
    void ReleaseSignal(int id);
    bool IsForced(int id);
    W64 ReadMemory(int memId, int addr);
    void WriteMemory(int memId, int addr, W64 value);
}

public static class VpiLookup
{
    public static int SignalByName(string modelName, IReadOnlyList<VpiSignalInfo> signals, string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        string n = name;
        if (n.StartsWith("TOP.", StringComparison.Ordinal)) n = n[4..];
        string prefix = modelName + ".";
        if (n.StartsWith(prefix, StringComparison.Ordinal)) n = n[prefix.Length..];
        for (int i = 0; i < signals.Count; ++i)
            if (string.Equals(signals[i].Name, n, StringComparison.Ordinal)) return signals[i].Id;
        return -1;
    }
}

public sealed class VpiContext<TModel> where TModel : IVpiModel
{
    private readonly VpiSession<TModel> _session;
    private readonly bool _readOnly;
    private readonly bool _dispatching;

    internal VpiContext(VpiSession<TModel> session, bool readOnly, bool dispatching)
    {
        _session = session;
        _readOnly = readOnly;
        _dispatching = dispatching;
    }

    public TModel Dut => _session.Dut;
    public IReadOnlyList<VpiSignalInfo> Signals => _session.Dut.Signals;
    public IReadOnlyList<VpiMemoryInfo> Memories => _session.Dut.Memories;
    public ulong Time => _session.Dut.Time;
    public bool GotFinish => _session.Dut.GotFinish;
    public int SignalByName(string name) => _session.Dut.SignalByName(name);
    public W64 GetValue(int id) => _session.Dut.GetSignal(id);

    public void PutValue(int id, W64 value)
    {
        if (_readOnly || _session.IsFinished || _session.Dut.GotFinish)
            throw new InvalidOperationException("Model is finished or in read-only callback.");
        if ((uint)id >= (uint)_session.Dut.Signals.Count)
            throw new ArgumentOutOfRangeException(nameof(id), "Invalid signal id.");
        var sig = _session.Dut.Signals[id];
        if (sig.Direction != VpiDirection.Input)
            throw new InvalidOperationException("Only input ports may be deposited.");
        W64 val = W64Ops.Resize(value, sig.Width);
        if (sig.TwoState) val = W64Ops.TwoState(val);
        if (_session.Dut.IsForced(id)) return;
        if (_dispatching) _session.QueuePending(id, val);
        else _session.Dut.PutSignal(id, val);
    }

    public void ForceValue(int id, W64 value)
    {
        if (_readOnly || _session.IsFinished)
            throw new InvalidOperationException("Model is finished or in read-only callback.");
        _session.Dut.ForceSignal(id, value);
    }

    public void ReleaseValue(int id)
    {
        if (_readOnly || _session.IsFinished)
            throw new InvalidOperationException("Model is finished or in read-only callback.");
        _session.Dut.ReleaseSignal(id);
    }

    public W64 ReadMemory(int memId, int addr) => _session.Dut.ReadMemory(memId, addr);

    public void WriteMemory(int memId, int addr, W64 value)
    {
        if (_readOnly || _session.IsFinished || _session.Dut.GotFinish)
            throw new InvalidOperationException("Model is finished or in read-only callback.");
        _session.Dut.WriteMemory(memId, addr, value);
    }

    public void Finish() => _session.Dut.Finish();
    public VpiCbHandle OnValueChange(int signalId, Action<VpiContext<TModel>, VpiCbEvent> cb) => _session.OnValueChange(signalId, cb);
    public VpiCbHandle OnReadWriteSynch(Action<VpiContext<TModel>, VpiCbEvent> cb) => _session.OnReadWriteSynch(cb);
    public VpiCbHandle OnReadOnlySynch(Action<VpiContext<TModel>, VpiCbEvent> cb) => _session.OnReadOnlySynch(cb);
    public VpiCbHandle OnAfterDelay(ulong delay, Action<VpiContext<TModel>, VpiCbEvent> cb) => _session.OnAfterDelay(delay, cb);
    public VpiCbHandle OnEndOfSimulation(Action<VpiContext<TModel>, VpiCbEvent> cb) => _session.OnEndOfSimulation(cb);
    public bool RemoveCb(VpiCbHandle handle) => _session.RemoveCb(handle);
}

public sealed class VpiSession<TModel> where TModel : IVpiModel
{
    private sealed class CbEntry
    {
        public VpiCbHandle Handle;
        public VpiCbReason Reason;
        public int SignalId;
        public ulong TargetTime;
        public W64 Previous;
        public bool Active;
        public Action<VpiContext<TModel>, VpiCbEvent>? Callback;
    }

    private readonly List<CbEntry> _callbacks = new();
    private readonly List<(int Id, W64 Value)> _pending = new();
    private int _nextHandle = 1;
    private bool _started;
    private bool _finished;
    private bool _evalRequested;

    public TModel Dut { get; }
    internal bool IsFinished => _finished;

    public VpiSession(TModel dut) => Dut = dut;

    public IReadOnlyList<VpiSignalInfo> Signals => Dut.Signals;
    public IReadOnlyList<VpiMemoryInfo> Memories => Dut.Memories;
    public int SignalByName(string name) => Dut.SignalByName(name);
    public ulong Time => Dut.Time;
    public void TimeInc(ulong delta) => Dut.TimeInc(delta);
    public bool GotFinish => Dut.GotFinish;
    public void Finish() => Dut.Finish();
    public W64 GetValue(int id) => Dut.GetSignal(id);
    public void PutValue(int id, W64 value) => Dut.PutSignal(id, value);
    public void ForceValue(int id, W64 value) => Dut.ForceSignal(id, value);
    public void ReleaseValue(int id) => Dut.ReleaseSignal(id);
    public W64 ReadMemory(int memId, int addr) => Dut.ReadMemory(memId, addr);
    public void WriteMemory(int memId, int addr, W64 value) => Dut.WriteMemory(memId, addr, value);

    internal void QueuePending(int id, W64 val)
    {
        for (int i = 0; i < _pending.Count; ++i)
        {
            if (_pending[i].Id == id) { _pending[i] = (id, val); return; }
        }
        _pending.Add((id, val));
    }

    private VpiCbHandle Register(VpiCbReason reason, int signalId, ulong targetTime, W64 previous, Action<VpiContext<TModel>, VpiCbEvent> cb)
    {
        if (_finished || Dut.GotFinish) throw new InvalidOperationException("Finished model.");
        if (reason == VpiCbReason.StartOfSimulation && _started)
            throw new InvalidOperationException("Start callbacks must be registered before first eval.");
        var handle = new VpiCbHandle(_nextHandle++);
        _callbacks.Add(new CbEntry
        {
            Handle = handle,
            Reason = reason,
            SignalId = signalId,
            TargetTime = targetTime,
            Previous = previous,
            Active = true,
            Callback = cb
        });
        if (reason is VpiCbReason.ReadOnlySynch or VpiCbReason.ReadWriteSynch || (reason == VpiCbReason.AfterDelay && targetTime <= Dut.Time))
            _evalRequested = true;
        return handle;
    }

    public VpiCbHandle OnStartOfSimulation(Action<VpiContext<TModel>, VpiCbEvent> cb) =>
        Register(VpiCbReason.StartOfSimulation, -1, Dut.Time, default, cb);

    public VpiCbHandle OnEndOfSimulation(Action<VpiContext<TModel>, VpiCbEvent> cb) =>
        Register(VpiCbReason.EndOfSimulation, -1, Dut.Time, default, cb);

    public VpiCbHandle OnValueChange(int signalId, Action<VpiContext<TModel>, VpiCbEvent> cb) =>
        Register(VpiCbReason.ValueChange, signalId, Dut.Time, Dut.GetSignal(signalId), cb);

    public VpiCbHandle OnReadWriteSynch(Action<VpiContext<TModel>, VpiCbEvent> cb) =>
        Register(VpiCbReason.ReadWriteSynch, -1, Dut.Time, default, cb);

    public VpiCbHandle OnReadOnlySynch(Action<VpiContext<TModel>, VpiCbEvent> cb) =>
        Register(VpiCbReason.ReadOnlySynch, -1, Dut.Time, default, cb);

    public VpiCbHandle OnAfterDelay(ulong delay, Action<VpiContext<TModel>, VpiCbEvent> cb)
    {
        if (delay > ulong.MaxValue - Dut.Time) throw new OverflowException("cbAfterDelay time overflow.");
        return Register(VpiCbReason.AfterDelay, -1, Dut.Time + delay, default, cb);
    }

    public bool RemoveCb(VpiCbHandle handle)
    {
        for (int i = 0; i < _callbacks.Count; ++i)
        {
            if (_callbacks[i].Handle == handle)
            {
                _callbacks[i].Active = false;
                _callbacks[i].Callback = null;
                return true;
            }
        }
        return false;
    }

    private void Invoke(CbEntry entry, VpiCbEvent ev, bool oneShot, bool readOnly, bool dispatching)
    {
        if (!entry.Active || entry.Callback is null) return;
        if (oneShot) entry.Active = false;
        var cb = entry.Callback;
        var ctx = new VpiContext<TModel>(this, readOnly, dispatching);
        cb(ctx, ev);
    }

    public void Eval()
    {
        if (_finished || Dut.GotFinish) return;
        if (_callbacks.Count == 0 && _pending.Count == 0)
        {
            _started = true;
            _evalRequested = false;
            Dut.Eval();
            return;
        }

        if (!_started)
        {
            _started = true;
            var starts = new List<CbEntry>();
            foreach (var c in _callbacks)
                if (c.Active && c.Reason == VpiCbReason.StartOfSimulation) starts.Add(c);
            foreach (var c in starts)
            {
                Invoke(c, new VpiCbEvent(VpiCbReason.StartOfSimulation, -1, Dut.Time, null), oneShot: true, readOnly: false, dispatching: true);
                if (Dut.GotFinish) return;
            }
        }

        for (int round = 0; round < 64; ++round)
        {
            _evalRequested = false;
            ulong now = Dut.Time;
            var due = new List<CbEntry>();
            foreach (var c in _callbacks)
                if (c.Active && c.Reason == VpiCbReason.AfterDelay && c.TargetTime <= now) due.Add(c);
            foreach (var c in due)
            {
                Invoke(c, new VpiCbEvent(VpiCbReason.AfterDelay, -1, Dut.Time, null), oneShot: true, readOnly: false, dispatching: true);
                if (Dut.GotFinish) return;
            }

            if (_pending.Count > 0)
            {
                var drained = _pending.ToArray();
                _pending.Clear();
                foreach (var (id, val) in drained)
                    if (!Dut.IsForced(id)) Dut.PutSignal(id, val);
            }

            Dut.Eval();
            if (Dut.GotFinish) return;

            var changed = new List<(CbEntry Entry, W64 Val)>();
            foreach (var c in _callbacks)
            {
                if (c.Active && c.Reason == VpiCbReason.ValueChange)
                {
                    W64 cur = Dut.GetSignal(c.SignalId);
                    if (!W64Ops.Exact(cur, c.Previous))
                    {
                        c.Previous = cur;
                        changed.Add((c, cur));
                    }
                }
            }
            foreach (var (entry, val) in changed)
            {
                Invoke(entry, new VpiCbEvent(VpiCbReason.ValueChange, entry.SignalId, Dut.Time, val), oneShot: false, readOnly: false, dispatching: true);
                if (Dut.GotFinish) return;
            }

            var rw = new List<CbEntry>();
            foreach (var c in _callbacks)
                if (c.Active && c.Reason == VpiCbReason.ReadWriteSynch) rw.Add(c);
            foreach (var c in rw)
            {
                Invoke(c, new VpiCbEvent(VpiCbReason.ReadWriteSynch, -1, Dut.Time, null), oneShot: true, readOnly: false, dispatching: true);
                if (Dut.GotFinish) return;
            }

            if (_pending.Count == 0 && !_evalRequested) break;
        }

        var ro = new List<CbEntry>();
        foreach (var c in _callbacks)
            if (c.Active && c.Reason == VpiCbReason.ReadOnlySynch) ro.Add(c);
        foreach (var c in ro)
        {
            Invoke(c, new VpiCbEvent(VpiCbReason.ReadOnlySynch, -1, Dut.Time, null), oneShot: true, readOnly: true, dispatching: false);
            if (Dut.GotFinish) return;
        }

        _callbacks.RemoveAll(static c => !c.Active);
    }

    public void Final()
    {
        if (_finished) return;
        _finished = true;
        var ends = new List<CbEntry>();
        foreach (var c in _callbacks)
            if (c.Active && c.Reason == VpiCbReason.EndOfSimulation) ends.Add(c);
        foreach (var c in ends)
            Invoke(c, new VpiCbEvent(VpiCbReason.EndOfSimulation, -1, Dut.Time, null), oneShot: true, readOnly: true, dispatching: false);
        Dut.Final();
        _callbacks.Clear();
    }
}
""";

    internal static string EmitCSharpVpiTrampolineC() => """
/* Auto-generated VeloSim C# IEEE 1800 C-ABI VPI trampoline.
 * Build: gcc -shared -fPIC -O2 -I.. velosim_vpi_trampoline.c -ldl -o libvelosim_vpi_trampoline.so */
#include "vpi_user.h"
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#if defined(_WIN32)
#include <windows.h>
#else
#include <dlfcn.h>
#endif

typedef struct VelosimVpiTable {
    vpiHandle (*handle_by_name)(PLI_BYTE8*, vpiHandle);
    vpiHandle (*handle)(PLI_INT32, vpiHandle);
    vpiHandle (*iterate)(PLI_INT32, vpiHandle);
    vpiHandle (*scan)(vpiHandle);
    PLI_INT32 (*free_object)(vpiHandle);
    PLI_INT32 (*compare_objects)(vpiHandle, vpiHandle);
    PLI_INT32 (*get)(PLI_INT32, vpiHandle);
    PLI_BYTE8* (*get_str)(PLI_INT32, vpiHandle);
    void (*get_value)(vpiHandle, p_vpi_value);
    vpiHandle (*put_value)(vpiHandle, p_vpi_value, p_vpi_time, PLI_INT32);
    void (*get_time)(vpiHandle, p_vpi_time);
    vpiHandle (*register_cb)(p_cb_data);
    PLI_INT32 (*remove_cb)(vpiHandle);
    PLI_INT32 (*chk_error)(p_vpi_error_info);
    PLI_INT32 (*control)(PLI_INT32);
    PLI_INT32 (*get_vlog_info)(p_vpi_vlog_info);
} VelosimVpiTable;

static VelosimVpiTable g_tbl;

VS_VPI_API void velosim_cs_vpi_init(const VelosimVpiTable* tbl, const char* self_path) {
    if (tbl) g_tbl = *tbl;
#if !defined(_WIN32)
    /* Promote this already-loaded trampoline to RTLD_GLOBAL so C VPI plugins resolve vpi_* symbols. */
    if (self_path && self_path[0]) {
        (void)dlopen(self_path, RTLD_NOW | RTLD_GLOBAL | RTLD_NOLOAD);
    }
#else
    (void)self_path;
#endif
}

VS_VPI_API PLI_INT32 velosim_cs_vpi_load_plugin(const char* path, void** out_mod, char* err_buf, PLI_INT32 err_cap) {
    if (!path || !out_mod) return 0;
#if defined(_WIN32)
    HMODULE mod = LoadLibraryA(path);
    if (!mod) {
        if (err_buf && err_cap > 0) snprintf(err_buf, (size_t)err_cap, "cannot load VPI plugin");
        return 0;
    }
    void (**startup)(void) = (void (**)(void))GetProcAddress(mod, "vlog_startup_routines");
#else
    void* mod = dlopen(path, RTLD_NOW | RTLD_LOCAL);
    if (!mod) {
        const char* de = dlerror();
        if (err_buf && err_cap > 0) snprintf(err_buf, (size_t)err_cap, "cannot load VPI plugin: %s", de ? de : "unknown");
        return 0;
    }
    void (**startup)(void) = (void (**)(void))dlsym(mod, "vlog_startup_routines");
#endif
    *out_mod = (void*)mod;
    if (!startup) {
        if (err_buf && err_cap > 0) snprintf(err_buf, (size_t)err_cap, "VPI plugin lacks vlog_startup_routines");
        return 0;
    }
    for (int i = 0; i < 256; ++i) {
        if (!startup[i]) return 1;
        startup[i]();
        s_vpi_error_info ei;
        if (g_tbl.chk_error && g_tbl.chk_error(&ei)) {
            if (err_buf && err_cap > 0) snprintf(err_buf, (size_t)err_cap, "VPI startup: %s", ei.message ? ei.message : "error");
            return 0;
        }
    }
    if (err_buf && err_cap > 0) snprintf(err_buf, (size_t)err_cap, "VPI startup routine budget exceeded");
    return 0;
}

VS_VPI_API vpiHandle vpi_handle_by_name(PLI_BYTE8* name, vpiHandle scope) { return g_tbl.handle_by_name ? g_tbl.handle_by_name(name, scope) : NULL; }
VS_VPI_API vpiHandle vpi_handle(PLI_INT32 rel, vpiHandle obj) { return g_tbl.handle ? g_tbl.handle(rel, obj) : NULL; }
VS_VPI_API vpiHandle vpi_iterate(PLI_INT32 kind, vpiHandle scope) { return g_tbl.iterate ? g_tbl.iterate(kind, scope) : NULL; }
VS_VPI_API vpiHandle vpi_scan(vpiHandle it) { return g_tbl.scan ? g_tbl.scan(it) : NULL; }
VS_VPI_API PLI_INT32 vpi_free_object(vpiHandle obj) { return g_tbl.free_object ? g_tbl.free_object(obj) : 0; }
VS_VPI_API PLI_INT32 vpi_compare_objects(vpiHandle a, vpiHandle b) { return g_tbl.compare_objects ? g_tbl.compare_objects(a, b) : 0; }
VS_VPI_API PLI_INT32 vpi_get(PLI_INT32 prop, vpiHandle obj) { return g_tbl.get ? g_tbl.get(prop, obj) : 0; }
VS_VPI_API PLI_BYTE8* vpi_get_str(PLI_INT32 prop, vpiHandle obj) { return g_tbl.get_str ? g_tbl.get_str(prop, obj) : NULL; }
VS_VPI_API void vpi_get_value(vpiHandle obj, p_vpi_value val) { if (g_tbl.get_value) g_tbl.get_value(obj, val); }
VS_VPI_API vpiHandle vpi_put_value(vpiHandle obj, p_vpi_value val, p_vpi_time tm, PLI_INT32 flags) { return g_tbl.put_value ? g_tbl.put_value(obj, val, tm, flags) : NULL; }
VS_VPI_API void vpi_get_time(vpiHandle obj, p_vpi_time tm) { if (g_tbl.get_time) g_tbl.get_time(obj, tm); }
VS_VPI_API vpiHandle vpi_register_cb(p_cb_data data) { return g_tbl.register_cb ? g_tbl.register_cb(data) : NULL; }
VS_VPI_API PLI_INT32 vpi_remove_cb(vpiHandle obj) { return g_tbl.remove_cb ? g_tbl.remove_cb(obj) : 0; }
VS_VPI_API PLI_INT32 vpi_chk_error(p_vpi_error_info info) { return g_tbl.chk_error ? g_tbl.chk_error(info) : 0; }
VS_VPI_API PLI_INT32 vpi_control(PLI_INT32 op, ...) { return g_tbl.control ? g_tbl.control(op) : 0; }
VS_VPI_API PLI_INT32 vpi_printf(PLI_BYTE8* fmt, ...) { if (!fmt) return 0; va_list ap; va_start(ap, fmt); int n = vprintf(fmt, ap); va_end(ap); return n; }
VS_VPI_API PLI_INT32 vpi_get_vlog_info(p_vpi_vlog_info info) { return g_tbl.get_vlog_info ? g_tbl.get_vlog_info(info) : 0; }
""";

    internal static string EmitCSharpVpiFfiCsproj(string top) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net11.0</TargetFramework>
            <ImplicitUsings>disable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <LangVersion>15.0</LangVersion>
            <AnalysisLevel>11.0</AnalysisLevel>
            <Deterministic>true</Deterministic>
            <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="../V{top}.csproj" />
          </ItemGroup>
        </Project>
        """;

    /// <summary>
    /// Emits the opt-in IEEE 1800 C-ABI VPI FFI host (<c>vpi_ffi/VpiFfiHost.cs</c>) using
    /// .NET 11 <c>[UnmanagedCallersOnly]</c> callbacks and <c>NativeLibrary.Load</c>.
    /// Core model project <c>V&lt;top&gt;.csproj</c> remains 100% safe managed C#.
    /// </summary>
    public static string EmitCSharpVpiFfiHost(string top, string @namespace = "VeloSim.Generated")
    {
        string className = "V" + top;
        return $$"""
// <auto-generated/>
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace {{@namespace}}.VpiFfi;

[StructLayout(LayoutKind.Sequential)]
public struct s_vpi_time
{
    public int type;
    public uint high;
    public uint low;
    public double real;
}

[StructLayout(LayoutKind.Sequential)]
public struct s_vpi_vecval
{
    public uint aval;
    public uint bval;
}

[StructLayout(LayoutKind.Explicit)]
public unsafe struct u_vpi_value
{
    [FieldOffset(0)] public byte* str;
    [FieldOffset(0)] public int scalar;
    [FieldOffset(0)] public int integer;
    [FieldOffset(0)] public double real;
    [FieldOffset(0)] public s_vpi_time* time;
    [FieldOffset(0)] public s_vpi_vecval* vector;
    [FieldOffset(0)] public void* strength;
    [FieldOffset(0)] public byte* misc;
}

[StructLayout(LayoutKind.Sequential)]
public struct s_vpi_value
{
    public int format;
    public u_vpi_value value;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct s_cb_data
{
    public int reason;
    public delegate* unmanaged[Cdecl]<s_cb_data*, int> cb_rtn;
    public nint obj;
    public s_vpi_time* time;
    public s_vpi_value* value;
    public int index;
    public byte* user_data;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct s_vpi_error_info
{
    public int state;
    public int level;
    public byte* message;
    public byte* product;
    public byte* code;
    public byte* file;
    public int line;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct s_vpi_vlog_info
{
    public int argc;
    public byte** argv;
    public byte* product;
    public byte* version;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct VelosimVpiTable
{
    public delegate* unmanaged[Cdecl]<byte*, nint, nint> handle_by_name;
    public delegate* unmanaged[Cdecl]<int, nint, nint> handle;
    public delegate* unmanaged[Cdecl]<int, nint, nint> iterate;
    public delegate* unmanaged[Cdecl]<nint, nint> scan;
    public delegate* unmanaged[Cdecl]<nint, int> free_object;
    public delegate* unmanaged[Cdecl]<nint, nint, int> compare_objects;
    public delegate* unmanaged[Cdecl]<int, nint, int> get;
    public delegate* unmanaged[Cdecl]<int, nint, byte*> get_str;
    public delegate* unmanaged[Cdecl]<nint, s_vpi_value*, void> get_value;
    public delegate* unmanaged[Cdecl]<nint, s_vpi_value*, s_vpi_time*, int, nint> put_value;
    public delegate* unmanaged[Cdecl]<nint, s_vpi_time*, void> get_time;
    public delegate* unmanaged[Cdecl]<s_cb_data*, nint> register_cb;
    public delegate* unmanaged[Cdecl]<nint, int> remove_cb;
    public delegate* unmanaged[Cdecl]<s_vpi_error_info*, int> chk_error;
    public delegate* unmanaged[Cdecl]<int, int> control;
    public delegate* unmanaged[Cdecl]<s_vpi_vlog_info*, int> get_vlog_info;
}

public static class VpiConst
{
    public const int vpiIterator = 27;
    public const int vpiMemory = 29;
    public const int vpiMemoryWord = 30;
    public const int vpiModule = 32;
    public const int vpiPort = 44;
    public const int vpiReg = 48;
    public const int vpiLowConn = 80;
    public const int vpiScope = 84;
    public const int vpiCallback = 107;
    public const int vpiType = 1;
    public const int vpiName = 2;
    public const int vpiFullName = 3;
    public const int vpiSize = 4;
    public const int vpiTopModule = 7;
    public const int vpiDefName = 9;
    public const int vpiScalar = 17;
    public const int vpiVector = 18;
    public const int vpiDirection = 20;
    public const int vpiSigned = 65;
    public const int vpiBinStrVal = 1;
    public const int vpiOctStrVal = 2;
    public const int vpiDecStrVal = 3;
    public const int vpiHexStrVal = 4;
    public const int vpiScalarVal = 5;
    public const int vpiIntVal = 6;
    public const int vpiVectorVal = 9;
    public const int vpiSuppressVal = 13;
    public const int vpiNoDelay = 1;
    public const int vpiForceFlag = 5;
    public const int vpiReleaseFlag = 6;
    public const int vpi0 = 0;
    public const int vpi1 = 1;
    public const int vpiZ = 2;
    public const int vpiX = 3;
    public const int vpiSimTime = 2;
    public const int vpiSuppressTime = 3;
    public const int vpiRun = 3;
    public const int vpiError = 3;
    public const int vpiFinish = 67;
    public const int cbValueChange = 1;
    public const int cbReadWriteSynch = 6;
    public const int cbReadOnlySynch = 7;
    public const int cbAfterDelay = 9;
    public const int cbStartOfSimulation = 11;
    public const int cbEndOfSimulation = 12;
}

public sealed unsafe class VpiFfiHost : IDisposable
{
    private sealed class HandleEntry
    {
        public VpiFfiHost Host = null!;
        public long HostId;
        public int Kind;
        public int Id;
        public int MemIndex;
        public bool Released;
        public bool Active = true;
        public int[]? Items;
        public int Cursor;
        public int ItemKind;
        public int ParentId;
        public s_cb_data Cb;
        public bool HasTime;
        public s_vpi_time CbTime;
        public bool HasValue;
        public s_vpi_value CbValue;
        public ulong TargetTime;
        public W64 Previous;
        public long Order;
    }

    private sealed class TlsState
    {
        public VpiFfiHost? Current;
        public readonly Dictionary<nint, HandleEntry> Handles = new();
        public readonly HashSet<long> LiveHosts = new();
        public long NextHandle = 1;
        public string? Error;
        public nint ErrorPtr;
        public nint ResultStrPtr;
        public s_vpi_vecval* ResultVecPtr;
        public readonly List<nint> VlogArgPtrs = new();
        public byte** VlogArgvPtr;
        public int VlogArgvCap;
    }

    [ThreadStatic] private static TlsState? s_tls;
    private static long s_nextHostId;
    private static readonly nint s_productPtr = Marshal.StringToHGlobalAnsi("VeloSim");
    private static readonly nint s_versionPtr = Marshal.StringToHGlobalAnsi("csharp.vpi.v1");
    private static readonly nint s_codePtr = Marshal.StringToHGlobalAnsi("VS_VPI");

    private static TlsState Tls => s_tls ??= new TlsState();

    private readonly long _hostId;
    private readonly List<(int Id, W64 Val)> _pending = new();
    private readonly List<nint> _plugins = new();
    private nint _trampolineHandle;
    private delegate* unmanaged[Cdecl]<byte*, void**, byte*, int, int> _loadPluginFn;
    private bool _started;
    private bool _finished;
    private bool _evaluating;
    private bool _readOnly;
    private bool _dispatching;
    private bool _hasCallbacks;
    private bool _evalRequested;
    private bool _disposed;

    public {{className}} Dut { get; }
    public ulong Time => Dut.Time;
    public bool GotFinish => Dut.GotFinish;

    public VpiFfiHost(string trampolineLibraryPath)
    {
        Dut = new {{className}}();
        _hostId = System.Threading.Interlocked.Increment(ref s_nextHostId);
        Tls.LiveHosts.Add(_hostId);
        InitTrampoline(trampolineLibraryPath);
        UseVpi();
    }

    private void InitTrampoline(string path)
    {
        _trampolineHandle = NativeLibrary.Load(path);
        var initPtr = (delegate* unmanaged[Cdecl]<VelosimVpiTable*, byte*, void>)NativeLibrary.GetExport(_trampolineHandle, "velosim_cs_vpi_init");
        _loadPluginFn = (delegate* unmanaged[Cdecl]<byte*, void**, byte*, int, int>)NativeLibrary.GetExport(_trampolineHandle, "velosim_cs_vpi_load_plugin");
        VelosimVpiTable tbl = new()
        {
            handle_by_name = &VpiHandleByName,
            handle = &VpiHandleFn,
            iterate = &VpiIterate,
            scan = &VpiScan,
            free_object = &VpiFreeObject,
            compare_objects = &VpiCompareObjects,
            get = &VpiGet,
            get_str = &VpiGetStr,
            get_value = &VpiGetValue,
            put_value = &VpiPutValue,
            get_time = &VpiGetTime,
            register_cb = &VpiRegisterCb,
            remove_cb = &VpiRemoveCb,
            chk_error = &VpiChkError,
            control = &VpiControl,
            get_vlog_info = &VpiGetVlogInfo
        };
        nint pathPtr = Marshal.StringToHGlobalAnsi(path);
        try { initPtr(&tbl, (byte*)pathPtr); }
        finally { Marshal.FreeHGlobal(pathPtr); }
    }

    public void UseVpi() => Tls.Current = this;
    public void TimeInc(ulong delta) => Dut.TimeInc(delta);

    public void LoadVpi(string pluginPath)
    {
        if (_evaluating || _started || GotFinish || _plugins.Count >= 64)
            throw new InvalidOperationException("Load VPI plugins before first eval; maximum 64.");
        var prev = Tls.Current;
        UseVpi();
        try
        {
            nint pathPtr = Marshal.StringToHGlobalAnsi(pluginPath);
            byte* errBuf = stackalloc byte[512];
            void* mod = null;
            try
            {
                Tls.Error = null;
                int ok = _loadPluginFn((byte*)pathPtr, &mod, errBuf, 512);
                if (mod != null) _plugins.Add((nint)mod);
                if (ok == 0)
                {
                    string msg = Marshal.PtrToStringAnsi((nint)errBuf) ?? "VPI plugin load failed";
                    throw new InvalidOperationException(msg);
                }
            }
            finally { Marshal.FreeHGlobal(pathPtr); }
        }
        finally { Tls.Current = prev; }
    }

    private void QueuePending(int id, W64 val)
    {
        for (int i = 0; i < _pending.Count; ++i)
            if (_pending[i].Id == id) { _pending[i] = (id, val); return; }
        _pending.Add((id, val));
    }

    private List<HandleEntry> CallbacksFor(int reason)
    {
        var list = new List<HandleEntry>();
        foreach (var e in Tls.Handles.Values)
            if (!e.Released && e.Active && e.HostId == _hostId && e.Kind == VpiConst.vpiCallback && e.Cb.reason == reason)
                list.Add(e);
        list.Sort(static (a, b) => a.Order.CompareTo(b.Order));
        return list;
    }

    private List<HandleEntry> DueDelayCallbacks(ulong now)
    {
        var list = new List<HandleEntry>();
        foreach (var e in Tls.Handles.Values)
            if (!e.Released && e.Active && e.HostId == _hostId && e.Kind == VpiConst.vpiCallback && e.Cb.reason == VpiConst.cbAfterDelay && e.TargetTime <= now)
                list.Add(e);
        list.Sort(static (a, b) => a.Order.CompareTo(b.Order));
        return list;
    }

    private void InvokeCb(HandleEntry e, W64? valueOverride, bool isSigned, bool oneShot)
    {
        if (e.Released || !e.Active || e.Cb.cb_rtn == null) return;
        if (oneShot) e.Active = false;
        s_cb_data cb = e.Cb;
        s_vpi_time timeCopy = e.CbTime;
        if (e.HasTime)
        {
            ulong now = Dut.Time;
            timeCopy.type = VpiConst.vpiSimTime;
            timeCopy.high = (uint)(now >> 32);
            timeCopy.low = (uint)(now & 0xFFFF_FFFFUL);
            timeCopy.real = 0.0;
            cb.time = &timeCopy;
        }
        s_vpi_value valCopy = e.CbValue;
        s_vpi_vecval* vecCopy = stackalloc s_vpi_vecval[2];
        nint strAlloc = 0;
        try
        {
            if (e.HasValue)
            {
                if (valueOverride is { } w)
                    EncodeW64(w, &valCopy, ref strAlloc, vecCopy, isSigned);
                cb.value = &valCopy;
            }
            cb.cb_rtn(&cb);
        }
        finally
        {
            if (strAlloc != 0) Marshal.FreeHGlobal(strAlloc);
        }
    }

    public void Eval()
    {
        if (_evaluating) throw new InvalidOperationException("Reentrant Eval().");
        if (_finished || GotFinish) return;
        if (!_hasCallbacks && _pending.Count == 0)
        {
            _started = true;
            _evalRequested = false;
            Dut.Eval();
            return;
        }
        var prev = Tls.Current;
        UseVpi();
        _evaluating = true;
        try
        {
            if (!_started)
            {
                _started = true;
                _dispatching = true;
                foreach (var c in CallbacksFor(VpiConst.cbStartOfSimulation))
                {
                    InvokeCb(c, null, false, oneShot: true);
                    if (GotFinish) return;
                }
                _dispatching = false;
            }

            for (int round = 0; round < 64; ++round)
            {
                _evalRequested = false;
                _dispatching = true;
                foreach (var c in DueDelayCallbacks(Dut.Time))
                {
                    InvokeCb(c, null, false, oneShot: true);
                    if (GotFinish) return;
                }
                _dispatching = false;

                if (_pending.Count > 0)
                {
                    var drained = _pending.ToArray();
                    _pending.Clear();
                    foreach (var (id, val) in drained)
                        if (!Dut.IsForced(id)) Dut.PutSignal(id, val);
                }

                Dut.Eval();
                if (GotFinish) return;

                var changed = new List<(HandleEntry Entry, W64 Cur, bool IsSigned)>();
                foreach (var c in CallbacksFor(VpiConst.cbValueChange))
                {
                    if (!Tls.Handles.TryGetValue(c.Cb.obj, out var obj) || obj.Released) continue;
                    W64 cur = Dut.GetSignal(obj.Id);
                    if (!W64Ops.Exact(cur, c.Previous))
                    {
                        c.Previous = cur;
                        changed.Add((c, cur, Dut.Signals[obj.Id].IsSigned));
                    }
                }

                _dispatching = true;
                foreach (var (c, cur, isSigned) in changed)
                {
                    InvokeCb(c, cur, isSigned, oneShot: false);
                    if (GotFinish) return;
                }
                foreach (var c in CallbacksFor(VpiConst.cbReadWriteSynch))
                {
                    InvokeCb(c, null, false, oneShot: true);
                    if (GotFinish) return;
                }
                _dispatching = false;

                if (_pending.Count == 0 && !_evalRequested) break;
            }

            _readOnly = true;
            foreach (var c in CallbacksFor(VpiConst.cbReadOnlySynch))
            {
                InvokeCb(c, null, false, oneShot: true);
                if (GotFinish) return;
            }
        }
        finally
        {
            _readOnly = false;
            _dispatching = false;
            _evaluating = false;
            Tls.Current = prev;
        }
    }

    public void Final()
    {
        if (_finished) return;
        var prev = Tls.Current;
        UseVpi();
        _finished = true;
        _evaluating = true;
        _readOnly = true;
        try
        {
            foreach (var c in CallbacksFor(VpiConst.cbEndOfSimulation))
                InvokeCb(c, null, false, oneShot: true);
            Dut.Final();
        }
        finally
        {
            _readOnly = false;
            _evaluating = false;
            Tls.Current = prev;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Final();
        var tls = Tls;
        if (ReferenceEquals(tls.Current, this)) tls.Current = null;
        tls.LiveHosts.Remove(_hostId);
        var dead = new List<nint>();
        foreach (var kv in tls.Handles) if (kv.Value.HostId == _hostId) dead.Add(kv.Key);
        foreach (var k in dead) tls.Handles.Remove(k);
    }

    private static VpiFfiHost Selected()
    {
        var cur = Tls.Current;
        if (cur is null || !Tls.LiveHosts.Contains(cur._hostId))
            throw new InvalidOperationException("No active VeloSim model; call UseVpi().");
        return cur;
    }

    private static HandleEntry GetHandle(nint h)
    {
        var tls = Tls;
        if (!tls.Handles.TryGetValue(h, out var e) || e.Released || !tls.LiveHosts.Contains(e.HostId))
            throw new InvalidOperationException("Invalid, released or expired VPI handle.");
        return e;
    }

    private static nint MakeHandle(VpiFfiHost host, int kind, int id = 0, int memIndex = 0)
    {
        var tls = Tls;
        if (tls.Handles.Count >= 65536) throw new InvalidOperationException("VPI handle budget exceeded.");
        long keyNum = tls.NextHandle++;
        nint key = (nint)keyNum;
        tls.Handles[key] = new HandleEntry { Host = host, HostId = host._hostId, Kind = kind, Id = id, MemIndex = memIndex, Order = keyNum };
        return key;
    }

    private static byte* SetResultStr(string text)
    {
        var tls = Tls;
        if (tls.ResultStrPtr != 0) Marshal.FreeHGlobal(tls.ResultStrPtr);
        tls.ResultStrPtr = Marshal.StringToHGlobalAnsi(text);
        return (byte*)tls.ResultStrPtr;
    }

    private static void ValidateFormat(int fmt)
    {
        if (fmt is not (VpiConst.vpiBinStrVal or VpiConst.vpiOctStrVal or VpiConst.vpiDecStrVal or VpiConst.vpiHexStrVal or VpiConst.vpiVectorVal or VpiConst.vpiIntVal or VpiConst.vpiScalarVal or VpiConst.vpiSuppressVal))
            throw new InvalidOperationException("Unsupported VPI value format.");
    }

    private static string RadixText(W64 w, int group)
    {
        var sb = new StringBuilder();
        for (int low = ((w.W - 1) / group) * group; low >= 0; low -= group)
        {
            ulong bits = W64Ops.Mask(Math.Min(group, w.W - low));
            ulong x = (w.X >> low) & bits, z = (w.Z >> low) & bits, v = (w.V >> low) & bits;
            sb.Append(x != 0 ? (x == bits ? 'x' : 'X') : z != 0 ? (z == bits ? 'z' : 'Z') : "0123456789abcdef"[(int)v]);
        }
        return sb.ToString();
    }

    private static W64 RadixWord(string input, int width, int group)
    {
        if (string.IsNullOrEmpty(input)) throw new InvalidOperationException("Empty radix string.");
        int limit = (64 + group - 1) / group;
        if (input.Length > limit) throw new InvalidOperationException("Radix string exceeds 64-bit digit budget.");
        ulong v = 0, x = 0, z = 0, bits = W64Ops.Mask(group);
        for (int i = 0; i < input.Length; ++i)
        {
            char c = char.ToLowerInvariant(input[i]);
            if (i == 0) { if (c == 'x') x = ulong.MaxValue; else if (c == 'z') z = ulong.MaxValue; }
            v <<= group; x <<= group; z <<= group;
            if (c == 'x') x |= bits;
            else if (c == 'z') z |= bits;
            else
            {
                int d = c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : -1;
                if (d < 0 || d >= (1 << group)) throw new InvalidOperationException("Invalid radix digit.");
                v |= (uint)d;
            }
        }
        ulong m = W64Ops.Mask(width);
        ulong zc = z & m, xc = x & m & ~zc, vc = v & m & ~(xc | zc);
        return new W64(vc, xc, zc, width);
    }

    private static W64 DecimalWord(string input, int width)
    {
        if (string.IsNullOrEmpty(input) || input.Length > 20) throw new InvalidOperationException("Decimal string requires 1..20 characters.");
        if (input.Length == 1)
        {
            if (input[0] is 'x' or 'X') return W64Ops.Unknown(width);
            if (input[0] is 'z' or 'Z') return new W64(0UL, 0UL, W64Ops.Mask(width), width);
        }
        if (!ulong.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out ulong val))
            throw new InvalidOperationException("Unsigned decimal digit or overflow error.");
        return new W64(val & W64Ops.Mask(width), 0UL, 0UL, width);
    }

    private static void EncodeW64(W64 w, s_vpi_value* value, ref nint strAlloc, s_vpi_vecval* vecBuf, bool isSigned)
    {
        ValidateFormat(value->format);
        switch (value->format)
        {
            case VpiConst.vpiBinStrVal:
                strAlloc = Marshal.StringToHGlobalAnsi(W64Ops.Text(w));
                value->value.str = (byte*)strAlloc;
                break;
            case VpiConst.vpiDecStrVal:
            {
                ulong m = W64Ops.Mask(w.W);
                string s;
                if (w.X != 0) s = w.X == m ? "x" : "X";
                else if (w.Z != 0) s = w.Z == m ? "z" : "Z";
                else if (isSigned && w.W > 0 && ((w.V >> (w.W - 1)) & 1UL) != 0)
                    s = "-" + ((~w.V + 1UL) & m).ToString(CultureInfo.InvariantCulture);
                else
                    s = w.V.ToString(CultureInfo.InvariantCulture);
                strAlloc = Marshal.StringToHGlobalAnsi(s);
                value->value.str = (byte*)strAlloc;
                break;
            }
            case VpiConst.vpiOctStrVal:
                strAlloc = Marshal.StringToHGlobalAnsi(RadixText(w, 3));
                value->value.str = (byte*)strAlloc;
                break;
            case VpiConst.vpiHexStrVal:
                strAlloc = Marshal.StringToHGlobalAnsi(RadixText(w, 4));
                value->value.str = (byte*)strAlloc;
                break;
            case VpiConst.vpiVectorVal:
            {
                ulong a = w.V | w.X, b = w.X | w.Z;
                vecBuf[0] = new s_vpi_vecval { aval = (uint)a, bval = (uint)b };
                vecBuf[1] = new s_vpi_vecval { aval = (uint)(a >> 32), bval = (uint)(b >> 32) };
                value->value.vector = vecBuf;
                break;
            }
            case VpiConst.vpiIntVal:
                if ((w.X | w.Z) != 0) throw new InvalidOperationException("X/Z integer read; request binary or vector format.");
                value->value.integer = unchecked((int)(uint)w.V);
                break;
            case VpiConst.vpiScalarVal:
                if (w.W != 1) throw new InvalidOperationException("Scalar format requires width 1.");
                value->value.scalar = (w.X & 1UL) != 0 ? VpiConst.vpiX : (w.Z & 1UL) != 0 ? VpiConst.vpiZ : (w.V & 1UL) != 0 ? VpiConst.vpi1 : VpiConst.vpi0;
                break;
            case VpiConst.vpiSuppressVal:
                break;
        }
    }

    private static W64 DecodeVpiValue(s_vpi_value* value, int width)
    {
        if (value == null) throw new InvalidOperationException("Null VPI value.");
        switch (value->format)
        {
            case VpiConst.vpiBinStrVal:
            {
                if (value->value.str == null) throw new InvalidOperationException("Null binary string.");
                string s = Marshal.PtrToStringAnsi((nint)value->value.str) ?? "";
                if (s.Length != width) throw new InvalidOperationException("Binary deposit must match declared width.");
                return W64Ops.Parse(s, width);
            }
            case VpiConst.vpiDecStrVal:
            {
                if (value->value.str == null) throw new InvalidOperationException("Null decimal string.");
                return DecimalWord(Marshal.PtrToStringAnsi((nint)value->value.str) ?? "", width);
            }
            case VpiConst.vpiOctStrVal:
            {
                if (value->value.str == null) throw new InvalidOperationException("Null radix string.");
                return RadixWord(Marshal.PtrToStringAnsi((nint)value->value.str) ?? "", width, 3);
            }
            case VpiConst.vpiHexStrVal:
            {
                if (value->value.str == null) throw new InvalidOperationException("Null radix string.");
                return RadixWord(Marshal.PtrToStringAnsi((nint)value->value.str) ?? "", width, 4);
            }
            case VpiConst.vpiIntVal:
            {
                int i = value->value.integer;
                if (width > 32 && i < 0) throw new InvalidOperationException("Negative integer deposit wider than 32 bits; use vector format.");
                return new W64((uint)i & W64Ops.Mask(width), 0UL, 0UL, width);
            }
            case VpiConst.vpiScalarVal:
            {
                int n = value->value.scalar;
                if (width != 1 || n < 0 || n > 3) throw new InvalidOperationException("Invalid scalar deposit.");
                return new W64(n == VpiConst.vpi1 ? 1UL : 0UL, n == VpiConst.vpiX ? 1UL : 0UL, n == VpiConst.vpiZ ? 1UL : 0UL, 1);
            }
            case VpiConst.vpiVectorVal:
            {
                if (value->value.vector == null) throw new InvalidOperationException("Null vector deposit.");
                int words = Math.Clamp((width + 31) / 32, 1, 2);
                ulong a = 0, b = 0;
                for (int i = 0; i < words; ++i)
                {
                    a |= (ulong)value->value.vector[i].aval << (32 * i);
                    b |= (ulong)value->value.vector[i].bval << (32 * i);
                }
                ulong m = W64Ops.Mask(width);
                return new W64(a & ~b & m, a & b & m, ~a & b & m, width);
            }
            default:
                throw new InvalidOperationException("Unsupported VPI value format.");
        }
    }

    private static T Api<T>(Func<T> fn, T failure)
    {
        Tls.Error = null;
        try { return fn(); }
        catch (Exception ex) { Tls.Error = ex.Message; return failure; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint VpiHandleByName(byte* name, nint scope) => Api<nint>(() =>
    {
        if (name == null) throw new InvalidOperationException("Null VPI name.");
        string raw = Marshal.PtrToStringAnsi((nint)name) ?? "";
        VpiFfiHost host;
        if (scope == 0) host = Selected();
        else
        {
            var sh = GetHandle(scope);
            if (sh.Kind != VpiConst.vpiModule) throw new InvalidOperationException("Module scope required.");
            host = sh.Host;
        }
        string top = host.Dut.ModelName;
        if (string.Equals(raw, top, StringComparison.Ordinal) || string.Equals(raw, "TOP." + top, StringComparison.Ordinal))
            return MakeHandle(host, VpiConst.vpiModule);
        string n = raw;
        if (n.StartsWith("TOP.", StringComparison.Ordinal)) n = n[4..];
        string pref = top + ".";
        if (n.StartsWith(pref, StringComparison.Ordinal)) n = n[pref.Length..];
        else if (scope == 0) return 0;
        var sigs = host.Dut.Signals;
        for (int i = 0; i < sigs.Count; ++i)
            if (string.Equals(sigs[i].Name, n, StringComparison.Ordinal))
                return MakeHandle(host, VpiConst.vpiReg, sigs[i].Id);
        return 0;
    }, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint VpiHandleFn(int relation, nint obj) => Api<nint>(() =>
    {
        var h = GetHandle(obj);
        if (relation is VpiConst.vpiScope or VpiConst.vpiModule && h.Kind is VpiConst.vpiReg or VpiConst.vpiPort or VpiConst.vpiMemory or VpiConst.vpiMemoryWord)
            return MakeHandle(h.Host, VpiConst.vpiModule);
        if (relation == VpiConst.vpiScope && h.Kind == VpiConst.vpiModule) return 0;
        if (relation == VpiConst.vpiLowConn && h.Kind == VpiConst.vpiPort)
            return MakeHandle(h.Host, VpiConst.vpiReg, h.Id);
        throw new InvalidOperationException("Unsupported VPI relation.");
    }, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint VpiIterate(int kind, nint scope) => Api<nint>(() =>
    {
        if (scope == 0)
        {
            if (kind != VpiConst.vpiModule) throw new InvalidOperationException("Only top-module iteration allowed without scope.");
            var host = Selected();
            nint key = MakeHandle(host, VpiConst.vpiIterator);
            var it = Tls.Handles[key];
            it.Items = [0];
            it.ItemKind = VpiConst.vpiModule;
            return key;
        }
        var sh = GetHandle(scope);
        var m = sh.Host;
        if (sh.Kind == VpiConst.vpiMemory && kind == VpiConst.vpiMemoryWord)
        {
            int depth = m.Dut.Memories[sh.Id].Depth;
            if (depth == 0) return 0;
            int[] arr = new int[depth];
            for (int i = 0; i < depth; ++i) arr[i] = i;
            nint key = MakeHandle(m, VpiConst.vpiIterator);
            var it = Tls.Handles[key];
            it.Items = arr;
            it.ItemKind = VpiConst.vpiMemoryWord;
            it.ParentId = sh.Id;
            return key;
        }
        if (sh.Kind != VpiConst.vpiModule || kind is not (VpiConst.vpiReg or VpiConst.vpiPort or VpiConst.vpiMemory))
            throw new InvalidOperationException("Unsupported VPI iterator.");
        var items = new List<int>();
        if (kind == VpiConst.vpiMemory)
            for (int i = 0; i < m.Dut.Memories.Count; ++i) items.Add(i);
        else
            foreach (var s in m.Dut.Signals)
                if (kind == VpiConst.vpiReg || s.Direction != VpiDirection.Internal) items.Add(s.Id);
        if (items.Count == 0) return 0;
        nint itKey = MakeHandle(m, VpiConst.vpiIterator);
        var entry = Tls.Handles[itKey];
        entry.Items = items.ToArray();
        entry.ItemKind = kind;
        return itKey;
    }, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint VpiScan(nint iterator) => Api<nint>(() =>
    {
        var h = GetHandle(iterator);
        if (h.Kind != VpiConst.vpiIterator || h.Items is null) throw new InvalidOperationException("Iterator required.");
        if (h.Cursor >= h.Items.Length) { h.Released = true; return 0; }
        int item = h.Items[h.Cursor++];
        return h.ItemKind == VpiConst.vpiMemoryWord
            ? MakeHandle(h.Host, VpiConst.vpiMemoryWord, h.ParentId, item)
            : MakeHandle(h.Host, h.ItemKind, item);
    }, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int VpiFreeObject(nint obj) => Api(() =>
    {
        var h = GetHandle(obj);
        h.Released = true;
        h.Active = false;
        return 1;
    }, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int VpiCompareObjects(nint left, nint right) => Api(() =>
    {
        var a = GetHandle(left);
        var b = GetHandle(right);
        bool eq = a.HostId == b.HostId && a.Kind == b.Kind && a.Kind switch
        {
            VpiConst.vpiReg or VpiConst.vpiPort or VpiConst.vpiModule or VpiConst.vpiMemory => a.Id == b.Id,
            VpiConst.vpiMemoryWord => a.Id == b.Id && a.MemIndex == b.MemIndex,
            _ => left == right
        };
        return eq ? 1 : 0;
    }, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int VpiGet(int property, nint obj) => Api(() =>
    {
        var h = GetHandle(obj);
        if (property == VpiConst.vpiType) return h.Kind;
        if (property == VpiConst.vpiTopModule && h.Kind == VpiConst.vpiModule) return 1;
        if (h.Kind == VpiConst.vpiMemory)
            return property == VpiConst.vpiSize ? h.Host.Dut.Memories[h.Id].Depth : throw new InvalidOperationException("Unsupported VPI property.");
        if (h.Kind == VpiConst.vpiMemoryWord)
            return property == VpiConst.vpiSize ? h.Host.Dut.Memories[h.Id].Width : throw new InvalidOperationException("Unsupported VPI property.");
        if (h.Kind is not (VpiConst.vpiReg or VpiConst.vpiPort)) throw new InvalidOperationException("Signal handle required.");
        var sig = h.Host.Dut.Signals[h.Id];
        return property switch
        {
            VpiConst.vpiScalar => sig.Width == 1 ? 1 : 0,
            VpiConst.vpiVector => sig.Width > 1 ? 1 : 0,
            VpiConst.vpiSigned => sig.IsSigned ? 1 : 0,
            VpiConst.vpiSize => sig.Width,
            VpiConst.vpiDirection => (int)sig.Direction,
            _ => throw new InvalidOperationException("Unsupported VPI property.")
        };
    }, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte* VpiGetStr(int property, nint obj) => (byte*)Api<nint>(() =>
    {
        var h = GetHandle(obj);
        string top = h.Host.Dut.ModelName;
        string text = h.Kind switch
        {
            VpiConst.vpiMemory => property switch
            {
                VpiConst.vpiName => h.Host.Dut.Memories[h.Id].Name,
                VpiConst.vpiFullName => top + "." + h.Host.Dut.Memories[h.Id].Name,
                _ => throw new InvalidOperationException("Unsupported VPI string property.")
            },
            VpiConst.vpiMemoryWord => property switch
            {
                VpiConst.vpiName => h.Host.Dut.Memories[h.Id].Name + "[" + h.MemIndex.ToString(CultureInfo.InvariantCulture) + "]",
                VpiConst.vpiFullName => top + "." + h.Host.Dut.Memories[h.Id].Name + "[" + h.MemIndex.ToString(CultureInfo.InvariantCulture) + "]",
                _ => throw new InvalidOperationException("Unsupported VPI string property.")
            },
            VpiConst.vpiModule => property is VpiConst.vpiName or VpiConst.vpiFullName or VpiConst.vpiDefName
                ? top
                : throw new InvalidOperationException("Unsupported VPI string property."),
            VpiConst.vpiReg or VpiConst.vpiPort => property switch
            {
                VpiConst.vpiName => h.Host.Dut.Signals[h.Id].Name,
                VpiConst.vpiFullName => top + "." + h.Host.Dut.Signals[h.Id].Name,
                _ => throw new InvalidOperationException("Unsupported VPI string property.")
            },
            _ => throw new InvalidOperationException("Named object required.")
        };
        return (nint)SetResultStr(text);
    }, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void VpiGetValue(nint obj, s_vpi_value* value) => Api(() =>
    {
        if (value == null) throw new InvalidOperationException("Null VPI value.");
        var h = GetHandle(obj);
        W64 w;
        bool isSigned;
        if (h.Kind == VpiConst.vpiMemoryWord) { w = h.Host.Dut.ReadMemory(h.Id, h.MemIndex); isSigned = false; }
        else if (h.Kind is VpiConst.vpiReg or VpiConst.vpiPort) { w = h.Host.Dut.GetSignal(h.Id); isSigned = h.Host.Dut.Signals[h.Id].IsSigned; }
        else throw new InvalidOperationException("Signal handle required.");

        var tls = Tls;
        if (tls.ResultVecPtr == null)
            tls.ResultVecPtr = (s_vpi_vecval*)NativeMemory.AllocZeroed(2, (nuint)sizeof(s_vpi_vecval));
        nint strAlloc = 0;
        EncodeW64(w, value, ref strAlloc, tls.ResultVecPtr, isSigned);
        if (strAlloc != 0)
        {
            if (tls.ResultStrPtr != 0) Marshal.FreeHGlobal(tls.ResultStrPtr);
            tls.ResultStrPtr = strAlloc;
        }
        return 1;
    }, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint VpiPutValue(nint obj, s_vpi_value* value, s_vpi_time* time, int flags) => Api<nint>(() =>
    {
        if (time != null && ((time->type != VpiConst.vpiSimTime && time->type != VpiConst.vpiSuppressTime) || time->high != 0 || time->low != 0))
            throw new InvalidOperationException("Only immediate input deposits supported.");
        var h = GetHandle(obj);
        var host = h.Host;
        if (host._finished || host.GotFinish || host._readOnly)
            throw new InvalidOperationException("Model is finished or in read-only callback.");
        if (flags == VpiConst.vpiReleaseFlag)
        {
            if (h.Kind is not (VpiConst.vpiReg or VpiConst.vpiPort)) throw new InvalidOperationException("Signal handle required.");
            host.Dut.ReleaseSignal(h.Id);
            return 0;
        }
        if (flags is not (VpiConst.vpiNoDelay or VpiConst.vpiForceFlag) || value == null)
            throw new InvalidOperationException("Only immediate input deposits supported.");
        if (h.Kind == VpiConst.vpiMemoryWord)
        {
            if (flags != VpiConst.vpiNoDelay) throw new InvalidOperationException("Force/release unsupported on memory words.");
            W64 mw = DecodeVpiValue(value, host.Dut.Memories[h.Id].Width);
            host.Dut.WriteMemory(h.Id, h.MemIndex, mw);
            return 0;
        }
        if (h.Kind is not (VpiConst.vpiReg or VpiConst.vpiPort)) throw new InvalidOperationException("Signal handle required.");
        var sig = host.Dut.Signals[h.Id];
        W64 w = DecodeVpiValue(value, sig.Width);
        if (sig.TwoState) w = W64Ops.TwoState(w);
        if (flags == VpiConst.vpiForceFlag) host.Dut.ForceSignal(h.Id, w);
        else
        {
            if (sig.Direction != VpiDirection.Input) throw new InvalidOperationException("Only input ports may be deposited.");
            if (host.Dut.IsForced(h.Id)) return 0;
            if (host._dispatching) host.QueuePending(h.Id, w);
            else host.Dut.PutSignal(h.Id, w);
        }
        return 0;
    }, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void VpiGetTime(nint obj, s_vpi_time* time) => Api(() =>
    {
        if (time == null) throw new InvalidOperationException("Null VPI time.");
        if (time->type != VpiConst.vpiSimTime) throw new InvalidOperationException("Only vpiSimTime supported.");
        var host = obj == 0 ? Selected() : GetHandle(obj).Host;
        ulong now = host.Dut.Time;
        time->high = (uint)(now >> 32);
        time->low = (uint)(now & 0xFFFF_FFFFUL);
        time->real = 0.0;
        return 1;
    }, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint VpiRegisterCb(s_cb_data* data) => Api<nint>(() =>
    {
        if (data == null || data->cb_rtn == null) throw new InvalidOperationException("Callback routine required.");
        var host = Selected();
        if (host._finished || host.GotFinish) throw new InvalidOperationException("Finished model.");
        int reason = data->reason;
        if (reason is not (VpiConst.cbValueChange or VpiConst.cbReadOnlySynch or VpiConst.cbReadWriteSynch or VpiConst.cbAfterDelay or VpiConst.cbStartOfSimulation or VpiConst.cbEndOfSimulation))
            throw new InvalidOperationException("Unsupported callback reason.");
        ulong now = host.Dut.Time;
        ulong targetTime = now;
        if (reason == VpiConst.cbAfterDelay)
        {
            if (data->time == null || data->time->type != VpiConst.vpiSimTime)
                throw new InvalidOperationException("cbAfterDelay requires vpiSimTime.");
            ulong delay = ((ulong)data->time->high << 32) | data->time->low;
            if (delay > ulong.MaxValue - now) throw new InvalidOperationException("cbAfterDelay time overflow.");
            targetTime = now + delay;
        }
        else if (data->time != null && ((data->time->type != VpiConst.vpiSimTime && data->time->type != VpiConst.vpiSuppressTime) || data->time->high != 0 || data->time->low != 0))
            throw new InvalidOperationException("Only zero-delay callbacks supported.");

        if (data->value != null) ValidateFormat(data->value->format);
        nint objHandle = 0;
        W64 previous = default;
        if (reason == VpiConst.cbValueChange)
        {
            var sh = GetHandle(data->obj);
            if (sh.Kind is not (VpiConst.vpiReg or VpiConst.vpiPort)) throw new InvalidOperationException("Signal handle required.");
            if (sh.HostId != host._hostId) throw new InvalidOperationException("Callback model mismatch.");
            previous = host.Dut.GetSignal(sh.Id);
            if (data->value != null && data->value->format == VpiConst.vpiScalarVal && previous.W != 1)
                throw new InvalidOperationException("Scalar callback format requires width 1.");
            objHandle = MakeHandle(host, VpiConst.vpiReg, sh.Id);
        }
        else if (data->obj != 0) throw new InvalidOperationException("Lifecycle callback object must be null.");

        if (reason == VpiConst.cbStartOfSimulation && host._started)
            throw new InvalidOperationException("Start callbacks must be registered before first eval.");

        nint key = MakeHandle(host, VpiConst.vpiCallback);
        var e = Tls.Handles[key];
        e.Cb = *data;
        e.Cb.obj = objHandle;
        e.Previous = previous;
        e.TargetTime = targetTime;
        if (data->time != null) { e.HasTime = true; e.CbTime = *data->time; }
        if (data->value != null) { e.HasValue = true; e.CbValue = *data->value; }
        host._hasCallbacks = true;
        if (reason is VpiConst.cbReadOnlySynch or VpiConst.cbReadWriteSynch || (reason == VpiConst.cbAfterDelay && targetTime <= now))
            host._evalRequested = true;
        return key;
    }, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int VpiRemoveCb(nint obj) => Api(() =>
    {
        var h = GetHandle(obj);
        if (h.Kind != VpiConst.vpiCallback) throw new InvalidOperationException("Callback handle required.");
        h.Active = false;
        return 1;
    }, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int VpiChkError(s_vpi_error_info* info)
    {
        var tls = Tls;
        if (string.IsNullOrEmpty(tls.Error)) return 0;
        if (info != null)
        {
            if (tls.ErrorPtr != 0) Marshal.FreeHGlobal(tls.ErrorPtr);
            tls.ErrorPtr = Marshal.StringToHGlobalAnsi(tls.Error);
            *info = new s_vpi_error_info
            {
                state = VpiConst.vpiRun,
                level = VpiConst.vpiError,
                message = (byte*)tls.ErrorPtr,
                product = (byte*)s_productPtr,
                code = (byte*)s_codePtr,
                file = null,
                line = 0
            };
        }
        return VpiConst.vpiError;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int VpiControl(int operation) => Api(() =>
    {
        if (operation != VpiConst.vpiFinish) throw new InvalidOperationException("Only vpiFinish supported.");
        Selected().Dut.Finish();
        return 1;
    }, 0);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int VpiGetVlogInfo(s_vpi_vlog_info* info) => Api(() =>
    {
        if (info == null) throw new InvalidOperationException("Null vlog info.");
        var tls = Tls;
        foreach (var p in tls.VlogArgPtrs) Marshal.FreeHGlobal(p);
        tls.VlogArgPtrs.Clear();
        if (tls.Current is { } host && tls.LiveHosts.Contains(host._hostId))
        {
            var pa = host.Dut.Plusargs;
            if (pa.Count > tls.VlogArgvCap)
            {
                if (tls.VlogArgvPtr != null) NativeMemory.Free(tls.VlogArgvPtr);
                tls.VlogArgvPtr = (byte**)NativeMemory.Alloc((nuint)pa.Count, (nuint)sizeof(byte*));
                tls.VlogArgvCap = pa.Count;
            }
            for (int i = 0; i < pa.Count; ++i)
            {
                nint p = Marshal.StringToHGlobalAnsi("+" + pa[i]);
                tls.VlogArgPtrs.Add(p);
                tls.VlogArgvPtr[i] = (byte*)p;
            }
        }
        *info = new s_vpi_vlog_info
        {
            argc = tls.VlogArgPtrs.Count,
            argv = tls.VlogArgPtrs.Count == 0 ? null : tls.VlogArgvPtr,
            product = (byte*)s_productPtr,
            version = (byte*)s_versionPtr
        };
        return 1;
    }, 0);
}
""";
    }
}
