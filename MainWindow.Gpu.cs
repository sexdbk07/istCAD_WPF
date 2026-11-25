using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace iStCAD_1;

/// <summary>
/// Real GPU path: a private, hidden OpenGL context owned by one worker thread drives Skia's GPU backend (Ganesh).
/// Dense segment batches are drawn on the GPU and read back once into the frame's pixel buffer.
/// Any failure (no driver, no GL, timeout) permanently disables the GPU and the frame falls back to CPU Skia, then Bresenham.
/// Set environment variable ISTCAD_NOGPU=1 to disable it.
/// </summary>
public partial class MainWindow
{
    private const int GpuMinimumSegments = 3000; // below this the CPU path is faster than a GPU round trip
    private const int GpuAntialiasLimit = 80_000;   // above this, anti-aliased hairlines cost far more than they add: draw plain 1-px lines
    private static long s_gpuPrepTicks, s_gpuDrawTicks, s_gpuReadTicks;
    private static string GpuMs(long ticks) => (ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("0.0");

    private static volatile int s_gpuState; // 0 = not started, 1 = ready, -1 = unavailable
    private static volatile string s_gpuMessage = "not used yet";
    private static long s_lastGpuTicks;
    private static readonly BlockingCollection<GpuJob> s_gpuJobs = new(8);
    private static Thread? s_gpuThread;
    private static readonly object s_gpuStartLock = new();

    private sealed class GpuJob
    {
        public SoftSegmentBatch Batch = null!;
        public IntPtr Pixels;
        public int Width, Height;
        public readonly object Gate = new();
        public bool Abandoned, Done, Success;
        public readonly ManualResetEventSlim Signal = new(false);
    }

    private static string GpuStatusText() => s_gpuState switch
    {
        1 => $"GPU ({s_gpuMessage}, last {GpuMs(s_lastGpuTicks)} ms = prepare {GpuMs(s_gpuPrepTicks)} + draw {GpuMs(s_gpuDrawTicks)} + read-back {GpuMs(s_gpuReadTicks)})",
        -1 => $"GPU off ({s_gpuMessage})",
        _ => "GPU idle",
    };

    private static bool TryGpuDraw(SoftSegmentBatch batch, byte[] pixels, int width, int height)
    {
        if (batch.Count < GpuMinimumSegments || s_gpuState < 0 || width < 16 || height < 16) return false;
        if (Environment.GetEnvironmentVariable("ISTCAD_NOGPU") == "1") { s_gpuState = -1; s_gpuMessage = "disabled by ISTCAD_NOGPU"; return false; }
        EnsureGpuThread();
        if (s_gpuState < 0) return false;
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var job = new GpuJob { Batch = batch, Pixels = handle.AddrOfPinnedObject(), Width = width, Height = height };
            if (!s_gpuJobs.TryAdd(job)) return false;
            if (!job.Signal.Wait(2500))
            {
                lock (job.Gate)
                {
                    if (!job.Done) { job.Abandoned = true; batch.NoPool = true; s_gpuState = -1; s_gpuMessage = "timeout, disabled"; return false; }
                }
            }
            return job.Success;
        }
        finally { handle.Free(); }
    }

    private static void EnsureGpuThread()
    {
        if (s_gpuThread is not null) return;
        if (Environment.GetEnvironmentVariable("ISTCAD_NOGPU") == "1") { s_gpuState = -1; s_gpuMessage = "disabled by ISTCAD_NOGPU"; return; }
        lock (s_gpuStartLock)
        {
            if (s_gpuThread is not null) return;
            var ready = new ManualResetEventSlim(false);
            s_gpuThread = new Thread(() => GpuThreadMain(ready)) { IsBackground = true, Name = "iStCAD GPU", Priority = ThreadPriority.BelowNormal };
            s_gpuThread.SetApartmentState(ApartmentState.MTA);
            s_gpuThread.Start();
            ready.Wait(6000);
            if (s_gpuState == 0) { s_gpuState = -1; s_gpuMessage = "init timeout"; }
        }
    }

    private static void GpuThreadMain(ManualResetEventSlim ready)
    {
        IntPtr hwnd = IntPtr.Zero, dc = IntPtr.Zero, rc = IntPtr.Zero;
        GRContext? context = null;
        SKSurface? surface = null;
        int surfaceWidth = 0, surfaceHeight = 0;
        try
        {
            hwnd = CreateWindowExW(0, "STATIC", "iStCAD GPU", unchecked((int)0x80000000), 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("no helper window");
            dc = GetDC(hwnd);
            var pfd = new PixelFormatDescriptor
            {
                nSize = (ushort)Marshal.SizeOf<PixelFormatDescriptor>(), nVersion = 1, dwFlags = 0x04 | 0x20 | 0x01, // window | OpenGL | double buffer
                iPixelType = 0, cColorBits = 32, cAlphaBits = 8, cDepthBits = 24, cStencilBits = 8,
            };
            var format = ChoosePixelFormat(dc, ref pfd);
            if (format == 0 || !SetPixelFormat(dc, format, ref pfd)) throw new InvalidOperationException("no pixel format");
            rc = wglCreateContext(dc);
            if (rc == IntPtr.Zero || !wglMakeCurrent(dc, rc)) throw new InvalidOperationException("no OpenGL context");
            using var gl = GRGlInterface.Create() ?? throw new InvalidOperationException("no GL interface");
            context = GRContext.CreateGl(gl) ?? throw new InvalidOperationException("Skia GPU context failed");
            s_gpuMessage = "Skia OpenGL";
            s_gpuState = 1;
            try { GpuWarmUp(context); } catch { } // compile the shaders now, not inside the first dense frame
            ready.Set();

            foreach (var job in s_gpuJobs.GetConsumingEnumerable())
            {
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    if (surface is null || surfaceWidth != job.Width || surfaceHeight != job.Height)
                    {
                        surface?.Dispose();
                        surface = SKSurface.Create(context, false, new SKImageInfo(job.Width, job.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
                        surfaceWidth = job.Width; surfaceHeight = job.Height;
                    }
                    if (surface is null) throw new InvalidOperationException("GPU surface failed");
                    var info = new SKImageInfo(job.Width, job.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    var canvas = surface.Canvas;
                    canvas.Clear(SKColors.Transparent);
                    var prepared = System.Diagnostics.Stopwatch.GetTimestamp();
                    s_gpuPrepTicks = System.Diagnostics.Stopwatch.GetTimestamp() - prepared;
                    var drawStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    using (var background = SKImage.FromPixelCopy(info, job.Pixels, job.Width * 4)) // pixels already plotted on the CPU (tiny dots)
                        canvas.DrawImage(background, 0, 0);
                    using var paint = new SKPaint { IsAntialias = job.Batch.Count < GpuAntialiasLimit, Style = SKPaintStyle.Stroke, StrokeWidth = 0, StrokeCap = SKStrokeCap.Butt };
                    foreach (var (key, run) in job.Batch.Runs)
                    {
                        paint.Color = new SKColor((byte)(key >> 16), (byte)(key >> 8), (byte)key);
                        foreach (var points in SoftSegmentBatch.ExactChunks(run)) canvas.DrawPoints(SKPointMode.Lines, points, paint);
                    }
                    canvas.Flush();
                    s_gpuDrawTicks = System.Diagnostics.Stopwatch.GetTimestamp() - drawStart;
                    var readStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    lock (job.Gate)
                    {
                        if (!job.Abandoned)
                        {
                            job.Success = surface.ReadPixels(info, job.Pixels, job.Width * 4, 0, 0); // one GPU -> CPU copy
                            job.Done = true;
                            s_gpuReadTicks = System.Diagnostics.Stopwatch.GetTimestamp() - readStart;
                        }
                    }
                }
                catch (Exception ex)
                {
                    s_gpuState = -1; s_gpuMessage = "error: " + ex.GetType().Name;
                    lock (job.Gate) job.Done = true;
                }
                finally
                {
                    s_lastGpuTicks = System.Diagnostics.Stopwatch.GetTimestamp() - started;
                    job.Signal.Set();
                }
                if (s_gpuState < 0) break;
            }
        }
        catch (Exception ex)
        {
            s_gpuState = -1;
            s_gpuMessage = ex.Message;
        }
        finally
        {
            ready.Set();
            try { surface?.Dispose(); context?.Dispose(); } catch { }
            try { if (rc != IntPtr.Zero) { wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); wglDeleteContext(rc); } } catch { }
            try { if (dc != IntPtr.Zero && hwnd != IntPtr.Zero) ReleaseDC(hwnd, dc); if (hwnd != IntPtr.Zero) DestroyWindow(hwnd); } catch { }
            if (s_gpuState >= 0) s_gpuState = -1;
            while (s_gpuJobs.TryTake(out var left)) { left.Success = false; left.Done = true; left.Signal.Set(); }
        }
    }

    /// <summary>Draws a tiny frame with every paint/shader variant the real frames use, so their compilation does not land on the first dense frame.</summary>
    private static void GpuWarmUp(GRContext context)
    {
        var rgba = new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(context, false, rgba);
        if (surface is null) return;
        var canvas = surface.Canvas;
        var pixels = new byte[64 * 64 * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            canvas.Clear(SKColors.Transparent);
            using (var image = SKImage.FromPixelCopy(new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul), handle.AddrOfPinnedObject(), 64 * 4)) canvas.DrawImage(image, 0, 0);
            var line = new[] { new SKPoint(2, 2), new SKPoint(60, 40), new SKPoint(5, 50), new SKPoint(55, 9) };
            foreach (var antialias in new[] { true, false })
            {
                using var paint = new SKPaint { IsAntialias = antialias, Style = SKPaintStyle.Stroke, StrokeWidth = 0, Color = SKColors.White };
                canvas.DrawPoints(SKPointMode.Lines, line, paint);
            }
            canvas.Flush();
            surface.ReadPixels(rgba, handle.AddrOfPinnedObject(), 64 * 4, 0, 0);
        }
        finally { handle.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PixelFormatDescriptor
    {
        public ushort nSize, nVersion;
        public uint dwFlags;
        public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift, cAlphaBits, cAlphaShift;
        public byte cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits, cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("gdi32.dll")] private static extern int ChoosePixelFormat(IntPtr dc, ref PixelFormatDescriptor descriptor);
    [DllImport("gdi32.dll")] private static extern bool SetPixelFormat(IntPtr dc, int format, ref PixelFormatDescriptor descriptor);
    [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr dc);
    [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(IntPtr dc, IntPtr context);
    [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(IntPtr context);
}
