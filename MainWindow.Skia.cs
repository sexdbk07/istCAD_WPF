using System.Runtime.InteropServices;
using SkiaSharp;

namespace iStCAD_1;

/// <summary>Anti-aliased line rasterization through SkiaSharp (native, SIMD) for dense/draft frames; falls back to the CPU Bresenham plot if Skia cannot load.</summary>
public partial class MainWindow
{
    /// <summary>
    /// Line segments (pixel coordinates) grouped by color. Points live in recycled 8192-point chunks (64 KB: never the large-object heap), so a frame with
    /// 800,000 segments no longer allocates ~30 MB of lists and arrays (the cause of thousands of GCs). Sub-pixel segments are written straight into the
    /// frame's pixel buffer instead of being sent to Skia/GPU.
    /// </summary>
    private sealed class SoftSegmentBatch
    {
        public const int ChunkPoints = 8192;
        private static readonly System.Collections.Concurrent.ConcurrentBag<SKPoint[]> s_chunkPool = new();
        private static int s_pooledChunks;

        public sealed class ColorRun { public readonly List<SKPoint[]> Chunks = new(); public int LastCount; }
        public readonly Dictionary<int, ColorRun> Runs = new();
        public int Count { get; private set; }
        public int DotCount { get; private set; }
        public bool NoPool; // set when a GPU job may still be reading the chunks (timeout): let the GC have them instead of recycling

        private byte[]? _pixels; private int _width, _height;
        public void Attach(byte[] pixels, int width, int height) { _pixels = pixels; _width = width; _height = height; }

        public void Add(float x0, float y0, float x1, float y1, byte blue, byte green, byte red)
        {
            if (Math.Abs(x1 - x0) < 1f && Math.Abs(y1 - y0) < 1f && _pixels is not null)
            {
                var px = (int)Math.Floor((x0 + x1) * 0.5f); var py = (int)Math.Floor((y0 + y1) * 0.5f);
                if (px >= 0 && py >= 0 && px < _width && py < _height)
                {
                    var offset = (py * _width + px) * 4;
                    _pixels[offset] = blue; _pixels[offset + 1] = green; _pixels[offset + 2] = red; _pixels[offset + 3] = 255;
                }
                DotCount++;
                return;
            }
            var key = (red << 16) | (green << 8) | blue;
            if (!Runs.TryGetValue(key, out var run)) Runs[key] = run = new ColorRun();
            if (run.Chunks.Count == 0 || run.LastCount == ChunkPoints)
            {
                SKPoint[]? chunk = null;
                if (s_chunkPool.TryTake(out var recycled)) { chunk = recycled; Interlocked.Decrement(ref s_pooledChunks); }
                run.Chunks.Add(chunk ?? new SKPoint[ChunkPoints]); run.LastCount = 0;
            }
            var points = run.Chunks[^1];
            points[run.LastCount++] = new SKPoint(x0, y0); points[run.LastCount++] = new SKPoint(x1, y1);
            Count++;
        }

        /// <summary>Visits every chunk of a color as an exact-length array (only the last chunk of a color is copied).</summary>
        public static IEnumerable<SKPoint[]> ExactChunks(ColorRun run)
        {
            for (var i = 0; i < run.Chunks.Count; i++)
            {
                var chunk = run.Chunks[i];
                var used = i == run.Chunks.Count - 1 ? run.LastCount : ChunkPoints;
                if (used == chunk.Length) yield return chunk;
                else if (used > 0) yield return chunk[..used];
            }
        }

        public void Release()
        {
            if (!NoPool)
                foreach (var run in Runs.Values)
                    foreach (var chunk in run.Chunks)
                        if (Volatile.Read(ref s_pooledChunks) < 256) { s_chunkPool.Add(chunk); Interlocked.Increment(ref s_pooledChunks); } // keep at most 16 MB
            Runs.Clear(); Count = 0;
        }
    }

    private static volatile bool s_skiaUnavailable;
    private static volatile int s_lastSkiaSegments;

    private static void FlushSoftSegments(SoftSegmentBatch batch, byte[] pixels, int width, int height)
    {
        try
        {
            if (batch.Count == 0) return;
            s_lastSkiaSegments = batch.Count + batch.DotCount;
            if (TryGpuDraw(batch, pixels, width, height)) return; // dense batch: GPU (OpenGL via Skia) when available
            if (TrySkiaDraw(batch, pixels, width, height)) return;
            foreach (var (key, run) in batch.Runs) // Skia unavailable: aliased Bresenham fallback
                foreach (var points in SoftSegmentBatch.ExactChunks(run))
                    for (var i = 0; i + 1 < points.Length; i += 2)
                        DrawPixelLine(pixels, width, height, (int)Math.Round(points[i].X), (int)Math.Round(points[i].Y), (int)Math.Round(points[i + 1].X), (int)Math.Round(points[i + 1].Y),
                            (byte)key, (byte)(key >> 8), (byte)(key >> 16));
        }
        finally { batch.Release(); }
    }

    private static bool TrySkiaDraw(SoftSegmentBatch batch, byte[] pixels, int width, int height)
    {
        if (s_skiaUnavailable) return false;
        GCHandle handle = default;
        try
        {
            handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul); // same layout as WPF Pbgra32
            using (var surface = SKSurface.Create(info, handle.AddrOfPinnedObject(), width * 4))
            {
                if (surface is null) return false;
                var canvas = surface.Canvas;
                using var paint = new SKPaint { IsAntialias = batch.Count < 200_000, Style = SKPaintStyle.Stroke, StrokeWidth = 0, StrokeCap = SKStrokeCap.Butt };
                foreach (var (key, run) in batch.Runs)
                {
                    paint.Color = new SKColor((byte)(key >> 16), (byte)(key >> 8), (byte)key);
                    foreach (var points in SoftSegmentBatch.ExactChunks(run)) canvas.DrawPoints(SKPointMode.Lines, points, paint);
                }
                canvas.Flush();
            }
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException or EntryPointNotFoundException)
        {
            s_skiaUnavailable = true; // native library missing: stop trying
            return false;
        }
        catch { return false; }
        finally { if (handle.IsAllocated) handle.Free(); }
    }
}
