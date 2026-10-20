using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace VeloSim;

public enum ScalarValue : byte { Zero, One, X, Z }
public enum ScalarOperation : byte { Copy, And, Or, Xor, Not }
public sealed record SchedulerProcess(int Clock, int Destination, int A, int B, IrEdge Edge, ScalarOperation Operation);
internal sealed class SchedulerHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SchedulerHandle() : base(true) { }
    protected override bool ReleaseHandle() { NativeMethods.SchedulerFree(handle); return true; }
}
internal static partial class NativeMethods
{
    [LibraryImport("velosim_scheduler", EntryPoint = "vs_sched_new")] internal static partial SchedulerHandle SchedulerNew(uint signalCount);
    [LibraryImport("velosim_scheduler", EntryPoint = "vs_sched_free")] internal static partial void SchedulerFree(nint handle);
    [LibraryImport("velosim_scheduler", EntryPoint = "vs_sched_get")] internal static partial int SchedulerGet(SchedulerHandle handle, uint id);
    [LibraryImport("velosim_scheduler", EntryPoint = "vs_sched_ticks")] internal static partial ulong SchedulerTicks(SchedulerHandle handle);
    [LibraryImport("velosim_scheduler", EntryPoint = "vs_sched_add")] internal static partial int SchedulerAdd(SchedulerHandle handle, uint clock, uint dst, uint a, uint b, byte edge, byte operation);
    [LibraryImport("velosim_scheduler", EntryPoint = "vs_sched_drive")] internal static unsafe partial int SchedulerDrive(SchedulerHandle handle, uint* ids, byte* values, uint count, uint workers);
}
public sealed class ScalarScheduler : IDisposable
{
    private readonly SchedulerHandle handle;
    private readonly object gate = new();
    private readonly HashSet<int> writers = [];
    public int SignalCount { get; }
    public ScalarScheduler(int signalCount)
    {
        if (signalCount is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(signalCount));
        SignalCount = signalCount; handle = NativeMethods.SchedulerNew((uint)signalCount);
        if (handle.IsInvalid) { handle.Dispose(); throw new InvalidOperationException("Native scheduler allocation failed."); }
    }
    public static string NativeLibraryPath
    {
        get
        {
            if (NativeMethods.LoadedSchedulerPath is null) { using var probe = new ScalarScheduler(1); }
            return NativeMethods.LoadedSchedulerPath ?? throw new InvalidOperationException("Native scheduler path unavailable; set VELOSIM_NATIVE_DIR.");
        }
    }
    private void ValidateSignal(int signal) { if (signal < 0 || signal >= SignalCount) throw new ArgumentOutOfRangeException(nameof(signal)); }
    public ScalarValue Read(int signal)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this); ValidateSignal(signal);
            var value = NativeMethods.SchedulerGet(handle, (uint)signal);
            if (value is < 0 or > 3) throw new InvalidOperationException("Native scheduler read failed.");
            return (ScalarValue)value;
        }
    }
    public ulong Ticks { get { lock (gate) { ObjectDisposedException.ThrowIf(handle.IsClosed, this); return NativeMethods.SchedulerTicks(handle); } } }
    public void AddProcess(SchedulerProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            foreach (var id in new[] { process.Clock, process.Destination, process.A, process.B }) ValidateSignal(id);
            if (!Enum.IsDefined(process.Edge) || !Enum.IsDefined(process.Operation)) throw new ArgumentException("Invalid scheduler edge or operation.");
            if (writers.Contains(process.Destination)) throw new ArgumentException("Multiple scheduler writers.");
            var result = NativeMethods.SchedulerAdd(handle, (uint)process.Clock, (uint)process.Destination, (uint)process.A, (uint)process.B, (byte)process.Edge, (byte)process.Operation);
            if (result != 0) throw new InvalidOperationException("Native scheduler process registration failed: " + result);
            writers.Add(process.Destination);
        }
    }
    public unsafe int Drive(IReadOnlyDictionary<int, ScalarValue> updates, int workers = 1)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (workers is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(workers));
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            var ids = new uint[updates.Count]; var values = new byte[updates.Count]; int index = 0;
            foreach (var (id, value) in updates)
            {
                ValidateSignal(id); if (!Enum.IsDefined(value)) throw new ArgumentException("Invalid scalar value.");
                ids[index] = (uint)id; values[index++] = (byte)value;
            }
            fixed (uint* ip = ids) fixed (byte* vp = values)
            {
                var result = NativeMethods.SchedulerDrive(handle, ip, vp, (uint)index, (uint)workers);
                if (result < 0) throw new InvalidOperationException("Native scheduler drive failed: " + result);
                return result;
            }
        }
    }
    public void Dispose() { lock (gate) handle.Dispose(); }
}
