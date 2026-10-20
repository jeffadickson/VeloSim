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
    static NativeMethods() => NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? search)
    {
        if (name != "velosim_ir") return 0;
        var directory = Environment.GetEnvironmentVariable("VELOSIM_NATIVE_DIR");
        if (string.IsNullOrEmpty(directory)) return 0;
        var filename = OperatingSystem.IsWindows() ? "velosim_ir.dll" : OperatingSystem.IsMacOS() ? "libvelosim_ir.dylib" : "libvelosim_ir.so";
        return NativeLibrary.Load(Path.Combine(Path.GetFullPath(directory), filename));
    }
    [LibraryImport("velosim_ir", EntryPoint = "vs_ir_new")] internal static partial EngineHandle New();
    [LibraryImport("velosim_ir", EntryPoint = "vs_ir_free")] internal static partial void Free(nint handle);
    [LibraryImport("velosim_ir", EntryPoint = "vs_ir_signal")] internal static partial int Signal(EngineHandle handle, uint width, ulong value, ulong x, ulong z);
    [LibraryImport("velosim_ir", EntryPoint = "vs_ir_process")] internal static partial int Process(EngineHandle handle, uint clock, uint dst, uint source, uint reset, byte edge, byte op);
    [LibraryImport("velosim_ir", EntryPoint = "vs_ir_read")] internal static partial int Read(EngineHandle handle, uint id, out ulong value, out ulong x, out ulong z);
    [LibraryImport("velosim_ir", EntryPoint = "vs_ir_drive")] internal static unsafe partial int Drive(EngineHandle handle, uint* ids, ulong* values, ulong* xs, ulong* zs, uint count, uint workers);
}

public sealed class Simulation : IDisposable
{
    private readonly EngineHandle handle;
    private readonly Dictionary<string, (uint Id, int Width)> signals = new(StringComparer.Ordinal);
    private readonly object gate = new();
    public IrDesign Design { get; }
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
