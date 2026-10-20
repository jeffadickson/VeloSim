using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VeloSim;

internal sealed class EngineHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public EngineHandle() : base(true) { }
    protected override bool ReleaseHandle() { NativeMethods.Free(handle); return true; }
}

internal static partial class NativeMethods
{
    internal static string? LoadedLibraryPath { get; private set; }
    internal static string? LoadedSchedulerPath { get; private set; }
    private static readonly object libraryGate = new();
    private static readonly Dictionary<string, nint> loadedModules = new(StringComparer.Ordinal);
    static NativeMethods() => NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? search)
    {
        if (name is not ("velosim_ir" or "velosim_scheduler")) return 0;
        lock (libraryGate)
        {
            if (loadedModules.TryGetValue(name, out var cached)) return cached;
            var directory = Environment.GetEnvironmentVariable("VELOSIM_NATIVE_DIR");
            var filename = OperatingSystem.IsWindows() ? name + ".dll" : OperatingSystem.IsMacOS() ? "lib" + name + ".dylib" : "lib" + name + ".so";
            if (string.IsNullOrEmpty(directory))
            {
                directory = AppContext.BaseDirectory;
                if (!File.Exists(Path.Combine(directory, filename)))
                {
                    var systemModule = CheckAbi(NativeLibrary.Load(filename), name);
                    loadedModules.Add(name, systemModule);
                    return systemModule; // The system loader did not provide an absolute metadata path.
                }
            }
            var path = Path.Combine(Path.GetFullPath(directory), filename);
            var library = CheckAbi(NativeLibrary.Load(path), name);
            if (name == "velosim_ir") LoadedLibraryPath = path; else LoadedSchedulerPath = path;
            loadedModules.Add(name, library);
            return library;
        }
    }
    private static unsafe nint CheckAbi(nint module, string name)
    {
        try
        {
            var symbol = name == "velosim_ir" ? "vs_ir_abi_version" : "vs_sched_abi_version";
            if (!NativeLibrary.TryGetExport(module, symbol, out var address)) throw new EntryPointNotFoundException($"{name} lacks {symbol}; rebuild the native libraries.");
            var version = ((delegate* unmanaged[Cdecl]<uint>)address)();
            if (version != 1) throw new BadImageFormatException($"{name} ABI version {version} is unsupported; expected1. Rebuild the native libraries.");
            return module;
        }
        catch { NativeLibrary.Free(module); throw; }
    }
    [LibraryImport("velosim_ir", EntryPoint = "vs_ir_new")] internal static partial EngineHandle New();
    [LibraryImport("velosim_ir", EntryPoint = "vs_ir_free")] internal static partial void Free(nint handle);
    [LibraryImport("velosim_ir", EntryPoint = "vs_ir_signal")] internal static partial int Signal(EngineHandle handle, uint width, ulong value, ulong x, ulong z);
    [LibraryImport("velosim_ir", EntryPoint = "vs_ir_process")] internal static partial int Process(EngineHandle handle, uint clock, uint dst, uint source, uint reset, byte edge, byte op);
    [LibraryImport("velosim_ir", EntryPoint = "vs_ir_read")] internal static partial int Read(EngineHandle handle, uint id, out ulong value, out ulong x, out ulong z);
    [LibraryImport("velosim_ir", EntryPoint = "vs_ir_drive")] internal static unsafe partial int Drive(EngineHandle handle, uint* ids, ulong* values, ulong* xs, ulong* zs, uint count, uint workers);
    [LibraryImport("velosim_ir", EntryPoint = "vs_ir_ticks")] internal static partial ulong Ticks(EngineHandle handle);
}

public sealed class Simulation : IDisposable
{
    public static string NativeLibraryPath
    {
        get
        {
            if (NativeMethods.LoadedLibraryPath is null) { using var probe = NativeMethods.New(); if (probe.IsInvalid) throw new InvalidOperationException("Native allocation failed."); }
            return NativeMethods.LoadedLibraryPath ?? throw new InvalidOperationException("Native library path unavailable; set VELOSIM_NATIVE_DIR before the first simulation for reproducible artifact metadata.");
        }
    }
    private readonly EngineHandle handle;
    private readonly Dictionary<string, (uint Id, int Width)> signals = new(StringComparer.Ordinal);
    private readonly object gate = new();
    public IrDesign Design { get; }
    public ulong Ticks { get { lock (gate) { ObjectDisposedException.ThrowIf(handle.IsClosed, this); return NativeMethods.Ticks(handle); } } }
    public Simulation(IrDesign design)
    {
        Design = design ?? throw new ArgumentNullException(nameof(design));
        handle = NativeMethods.New();
        if (handle.IsInvalid) { handle.Dispose(); throw new InvalidOperationException("Native engine allocation failed."); }
        try
        {
            foreach (var s in design.Signals)
            {
                var w = s.Initial; var id = NativeMethods.Signal(handle, (uint)w.Width, w.Value, w.XMask, w.ZMask);
                if (id < 0) throw new InvalidOperationException("Native signal registration failed.");
                signals.Add(s.Name, ((uint)id, w.Width));
            }
            foreach (var p in design.Processes)
                if (NativeMethods.Process(handle, signals[p.Clock].Id, signals[p.Destination].Id, signals[p.Source].Id, signals[p.Reset].Id, (byte)p.Edge, (byte)p.Operation) != 0)
                    throw new InvalidOperationException("Native process registration failed.");
        }
        catch { handle.Dispose(); throw; }
    }
    public FourStateWord Read(string name)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            var s = signals[name]; var width = NativeMethods.Read(handle, s.Id, out var value, out var x, out var z);
            if (width != s.Width) throw new InvalidOperationException("Native read failed.");
            return new(width, value, x, z);
        }
    }
    public unsafe int Drive(IReadOnlyDictionary<string, FourStateWord> updates, int workers = 1)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (workers is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(workers));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            var ids = new uint[updates.Count]; var vals = new ulong[updates.Count]; var xs = new ulong[updates.Count]; var zs = new ulong[updates.Count];
            int i = 0;
            foreach (var (name, value) in updates)
            {
                var signal = signals[name];
                if (value.Width != signal.Width) throw new ArgumentException("Drive width mismatch: " + name);
                ids[i] = signal.Id; vals[i] = value.Value; xs[i] = value.XMask; zs[i] = value.ZMask; i++;
            }
            fixed (uint* ip = ids) fixed (ulong* vp = vals, xp = xs, zp = zs)
            {
                var result = NativeMethods.Drive(handle, ip, vp, xp, zp, (uint)i, (uint)workers);
                if (result < 0) throw new InvalidOperationException("Native drive failed: " + result);
                return result;
            }
        }
    }
    public void Dispose() { lock (gate) handle.Dispose(); }
}
