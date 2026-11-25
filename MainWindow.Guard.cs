using System;
using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace iStCAD_1;

/// <summary>Protection for heavy drawings: RAM check before opening, post-open memory cleanup, and a UI-stall watchdog that records what was busy.</summary>
public partial class MainWindow
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint dwLength; public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    private static bool TryGetPhysicalMemory(out ulong total, out ulong available)
    {
        total = available = 0;
        try
        {
            var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref status)) return false;
            total = status.ullTotalPhys; available = status.ullAvailPhys;
            return total > 0;
        }
        catch { return false; }
    }

    /// <summary>Writes a command-line warning (no popup) before opening a file whose estimated RAM need is high. Always lets the open proceed.</summary>
    private bool ConfirmOpenMemory(string path)
    {
        try
        {
            var size = new FileInfo(path).Length;
            var isDwg = System.IO.Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase);
            // Measured: a 12 MB DWG needed ~600 MB (about 50-70x); text DXF is lighter per byte.
            var need = (ulong)(size * (isDwg ? 70.0 : 14.0));
            if (!TryGetPhysicalMemory(out var total, out var available)) return true;
            if (need <= available * 0.6) return true;
            AppendCommandHistory($"Memory warning: \"{System.IO.Path.GetFileName(path)}\" is {size / 1048576.0:0.#} MB, estimated to need about {need / 1073741824.0:0.0} GB of RAM; {available / 1073741824.0:0.0} GB of {total / 1073741824.0:0.0} GB is free. Opening may be slow - close other applications if the PC becomes unresponsive.");
            return true;
        }
        catch { return true; }
    }

    /// <summary>Opening allocates ~0.5 GB of long-lived objects; full blocking gen2 collections while that happens only copy the same objects
    /// again (several seconds of GC pause in logs). LowLatency suppresses gen2 for the duration of the open, then normal low-latency mode returns.</summary>
    private static async Task<T> RunWithLowLatencyGc<T>(Func<Task<T>> work, string? path = null)
    {
        var noGcRegion = false;
        try { GCSettings.LatencyMode = GCLatencyMode.LowLatency; } catch { }
        try
        {
            // Logs showed 5,000+ gen0 and 4,800 gen1 collections (14 s of pause) because nearly every gen0 survivor was part of the drawing being built.
            // A no-GC region sized from the file lets the whole read allocate without any collection; if it is exceeded the runtime just resumes normal GC.
            var budget = OpenGcBudget(path);
            if (budget > 0) noGcRegion = GC.TryStartNoGCRegion(budget);
        }
        catch { noGcRegion = false; }
        try { return await work(); }
        finally
        {
            try { if (noGcRegion && GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) GC.EndNoGCRegion(); } catch { }
            try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; } catch { }
        }
    }

    private static long OpenGcBudget(string? path)
    {
        if (string.IsNullOrEmpty(path)) return 0;
        try
        {
            var size = new FileInfo(path).Length;
            var isDwg = System.IO.Path.GetExtension(path).Equals(".dwg", StringComparison.OrdinalIgnoreCase);
            var need = size * (isDwg ? 70.0 : 14.0);
            if (!TryGetPhysicalMemory(out var total, out var available)) return 0;
            var cap = Math.Min(Math.Min(total * 0.30, available * 0.40), 3.0 * 1073741824.0);
            var budget = Math.Min(need, cap);
            return budget < 128 * 1048576.0 ? 0 : (long)budget;
        }
        catch { return 0; }
    }

    /// <summary>Reading a DWG leaves a lot of short-lived garbage; compact it once after the first frame so the working set shrinks.</summary>
    private static void ScheduleMemoryCleanup()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000).ConfigureAwait(false);
            try
            {
                // Non-blocking (background) gen2: a forced blocking, compacting collection of a ~1 GB heap froze every thread, UI included, for seconds.
                GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: false);
            }
            catch { }
        });
    }

    // ---- UI-stall watchdog ----------------------------------------------------

    /// <summary>Runs background viewport work below normal priority so the UI thread and the rest of the PC stay responsive during heavy frames.</summary>
    private static Task<T> RunLowPriority<T>(Func<T> work) => Task.Run(() =>
    {
        var thread = Thread.CurrentThread; var previous = thread.Priority;
        try { thread.Priority = ThreadPriority.BelowNormal; return work(); }
        finally { try { thread.Priority = previous; } catch { } }
    });

    private System.Threading.Timer? _uiWatchdog;
    private long _uiPingSentAt;       // Stopwatch timestamp of the outstanding ping, 0 when none
    private string? _uiStallContext;
    private int _uiStallCount; private double _uiWorstStallMs;

    private void StartUiWatchdog()
    {
        if (_uiWatchdog is not null) return;
        if (TryGetPhysicalMemory(out var totalPhysical, out _)) iStCAD_1.Rendering.CadGeometryCache.ConfigureForMemory(totalPhysical);
        // Avoid long blocking gen2 collections while the user is interacting (needs concurrent GC, which the project enables).
        try { GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency; } catch { }
        _ = Task.Run(EnsureGpuThread); // create the GPU context now, off the UI thread, so the first dense frame does not pay for driver start-up
        _uiWatchdog = new System.Threading.Timer(_ =>
        {
            try
            {
                var sent = Interlocked.Read(ref _uiPingSentAt);
                if (sent != 0)
                {
                    // The UI thread has not answered the previous ping: it is busy right now. Capture what the app was doing.
                    if (Stopwatch.GetElapsedTime(sent).TotalMilliseconds > 1500 && _uiStallContext is null)
                        _uiStallContext = BuildStallContext();
                    return;
                }
                Interlocked.Exchange(ref _uiPingSentAt, Stopwatch.GetTimestamp());
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                {
                    var started = Interlocked.Exchange(ref _uiPingSentAt, 0);
                    if (started == 0) return;
                    var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    var context = _uiStallContext; _uiStallContext = null;
                    if (ms < 1500) return;
                    _uiStallCount++; _uiWorstStallMs = Math.Max(_uiWorstStallMs, ms);
                    if (_uiStallCount <= 25)
                        AppendCommandHistory($"UI stall: the interface did not respond for {ms:0} ms. {context ?? "Context not captured (stall ended quickly)."}");
                }));
            }
            catch { }
        }, null, 1000, 500);
    }

    private string BuildStallContext()
    {
        string Safe(Func<string> read) { try { return read(); } catch { return "?"; } }
        return "Busy: " +
            $"open={(Safe(() => _openDrawingCancellation is not null ? "yes" : "no"))}, geometryWorker={_geometryBuildInProgress}, indexBuild={_indexBuildInProgress}, " +
            $"selected={Safe(() => _selectedEntityIds.Count.ToString("N0"))}, lastCommand=\"{_lastCommand}\", " +
            $"managedHeap={GC.GetTotalMemory(false) / 1048576} MB, gen2 GCs={GC.CollectionCount(2)}, workingSet={Environment.WorkingSet / 1048576} MB, " +
            $"pause={GC.GetTotalPauseDuration().TotalMilliseconds:0} ms total GC pause.";
    }
}
