using System;
using System.Runtime.InteropServices;

namespace iStCAD_1.Native;

/// <summary>
/// Raw P/Invoke declarations for the optional Rust "heavy file" native engine
/// (see native/istcad_native/README.md for the full design). This DLL is not
/// required for the app to run: iStCAD_1-WPF.csproj's "BuildNativeEngine"
/// MSBuild target tries to build it automatically (and skips cleanly if Rust
/// isn't installed), and every call site in this file is wrapped so a
/// missing/mismatched/failed-to-load DLL degrades to "not available" instead
/// of crashing the app.
///
/// Struct/field layout here MUST exactly mirror the `#[repr(C)]` types in
/// native/istcad_native/src/lib.rs. This has not been validated against a
/// real build of the DLL (no Windows/.NET toolchain in the sandbox that wrote
/// this) - run NATIVESTATUS after building the native engine to check it.
/// </summary>
internal static class NativeEngineInterop
{
    private const string LibraryName = "istcad_native";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int istcad_native_abi_version();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr istcad_scene_new();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void istcad_scene_free(IntPtr scene);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int istcad_scene_build(
        IntPtr scene,
        double[] pointsX, double[] pointsY, UIntPtr pointsLen,
        NativeEntity[] entities, UIntPtr entitiesLen);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int istcad_scene_query(
        IntPtr scene, double minX, double minY, double maxX, double maxY,
        int[] outIds, UIntPtr outCapacity);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern byte istcad_scene_extents(
        IntPtr scene, out double minX, out double minY, out double maxX, out double maxY);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int istcad_scene_render(
        IntPtr scene, double centerX, double centerY, double scale, int pixelWidth, int pixelHeight,
        double viewMinX, double viewMinY, double viewMaxX, double viewMaxY,
        byte[] pixels, UIntPtr pixelsLen, uint backgroundBgra, byte flipY);
}

/// <summary>Mirrors Rust's `NativeEntity` (`#[repr(C)]`) field-for-field. Do not reorder.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct NativeEntity
{
    public int Id;
    public uint PointOffset;
    public uint PointCount;
    public byte Closed;
    public uint ColorBgr;
    public double MinX;
    public double MinY;
    public double MaxX;
    public double MaxY;
}

/// <summary>
/// Safe managed wrapper around <see cref="NativeEngineInterop"/>. Every public
/// member catches the loader exceptions a missing/incompatible DLL raises
/// (<see cref="DllNotFoundException"/>, <see cref="EntryPointNotFoundException"/>,
/// <see cref="BadImageFormatException"/>) so callers never need their own
/// try/catch around it; they just check <see cref="IsAvailable"/> or act on a
/// `false`/`null` return.
/// </summary>
internal sealed class NativeHeavyFileEngine : IDisposable
{
    private const int ExpectedAbiVersion = 1;

    private static readonly Lazy<(bool Available, int AbiVersion, string Status)> s_probe = new(ProbeAvailability);

    private IntPtr _scene;
    private bool _disposed;

    /// <summary>True once a scene handle has been allocated; does not by itself mean the DLL is loadable (see <see cref="IsAvailable"/>).</summary>
    public bool HasScene => _scene != IntPtr.Zero;

    /// <summary>Whether the native engine DLL is present, loadable, and reports a compatible ABI version.</summary>
    public static bool IsAvailable => s_probe.Value.Available;

    /// <summary>Human-readable one-line status for the NATIVESTATUS command and startup diagnostics.</summary>
    public static string StatusText => s_probe.Value.Status;

    private static (bool, int, string) ProbeAvailability()
    {
        try
        {
            var abi = NativeEngineInterop.istcad_native_abi_version();
            if (abi != ExpectedAbiVersion)
                return (false, abi, $"istcad_native.dll loaded but reports ABI version {abi}, expected {ExpectedAbiVersion} (rebuild native/istcad_native and the app together).");
            return (true, abi, $"istcad_native.dll loaded (ABI v{abi}).");
        }
        catch (DllNotFoundException)
        {
            return (false, 0, "istcad_native.dll not found next to the app. Build it: install Rust (https://rustup.rs), then rebuild the solution in Visual Studio (native/istcad_native builds automatically when cargo is on PATH).");
        }
        catch (EntryPointNotFoundException ex)
        {
            return (false, 0, $"istcad_native.dll found but missing an expected export ({ex.Message}). It is likely an outdated build - rebuild native/istcad_native.");
        }
        catch (BadImageFormatException)
        {
            return (false, 0, "istcad_native.dll found but could not be loaded (architecture mismatch - the app and the DLL must both be x64).");
        }
        catch (Exception ex)
        {
            return (false, 0, $"istcad_native.dll failed to load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Allocates a scene handle. No-op (and leaves <see cref="HasScene"/> false) if the native engine is unavailable.</summary>
    public void EnsureScene()
    {
        if (_disposed || _scene != IntPtr.Zero || !IsAvailable) return;
        try { _scene = NativeEngineInterop.istcad_scene_new(); }
        catch { _scene = IntPtr.Zero; }
    }

    /// <summary>
    /// Rebuilds the native spatial index + geometry from flat, already-tessellated point data.
    /// `pointsX`/`pointsY` are the shared flat coordinate arrays; each entity in `entities`
    /// references a contiguous range of them via PointOffset/PointCount. Returns false if the
    /// native engine is unavailable or the build call reported an error.
    /// </summary>
    public bool TryBuild(double[] pointsX, double[] pointsY, NativeEntity[] entities)
    {
        EnsureScene();
        if (!HasScene) return false;
        try
        {
            var rc = NativeEngineInterop.istcad_scene_build(
                _scene, pointsX, pointsY, (UIntPtr)pointsX.Length, entities, (UIntPtr)entities.Length);
            return rc == 0;
        }
        catch { return false; }
    }

    /// <summary>Returns the caller ids of every entity whose bounding box intersects the given world-space rectangle, or null on any failure.</summary>
    public int[]? Query(double minX, double minY, double maxX, double maxY, int capacityHint = 4096)
    {
        if (!HasScene) return null;
        try
        {
            var buffer = new int[Math.Max(capacityHint, 16)];
            var count = NativeEngineInterop.istcad_scene_query(_scene, minX, minY, maxX, maxY, buffer, (UIntPtr)buffer.Length);
            if (count < 0)
            {
                // Negative result = -(required count); retry once with an exact-size buffer.
                buffer = new int[-count];
                count = NativeEngineInterop.istcad_scene_query(_scene, minX, minY, maxX, maxY, buffer, (UIntPtr)buffer.Length);
                if (count < 0) return null;
            }
            if (count == buffer.Length) return buffer;
            var result = new int[count];
            Array.Copy(buffer, result, count);
            return result;
        }
        catch { return null; }
    }

    /// <summary>Overall bounding box of the current scene, or null if empty/unavailable.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY)? TryGetExtents()
    {
        if (!HasScene) return null;
        try
        {
            var has = NativeEngineInterop.istcad_scene_extents(_scene, out var minX, out var minY, out var maxX, out var maxY);
            return has != 0 ? (minX, minY, maxX, maxY) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Renders one frame directly into a caller-owned BGRA8888 buffer (e.g. a WriteableBitmap's
    /// back buffer). Returns the number of segments drawn, or -1 if the native engine is
    /// unavailable or the call failed - callers should fall back to the existing managed
    /// rasterizer in that case.
    /// </summary>
    public int Render(double centerX, double centerY, double scale, int pixelWidth, int pixelHeight,
        double viewMinX, double viewMinY, double viewMaxX, double viewMaxY,
        byte[] pixels, uint backgroundBgra, bool flipY)
    {
        if (!HasScene) return -1;
        try
        {
            return NativeEngineInterop.istcad_scene_render(
                _scene, centerX, centerY, scale, pixelWidth, pixelHeight,
                viewMinX, viewMinY, viewMaxX, viewMaxY,
                pixels, (UIntPtr)pixels.Length, backgroundBgra, (byte)(flipY ? 1 : 0));
        }
        catch { return -1; }
    }

    /// <summary>
    /// Allocates a tiny scene, builds a single triangle, queries it, reads back extents, and
    /// renders one frame - used by the NATIVESTATUS command to prove the full round trip works
    /// end to end, not just that the DLL loaded.
    /// </summary>
    public static string RunSelfTest()
    {
        if (!IsAvailable) return StatusText;
        using var engine = new NativeHeavyFileEngine();
        engine.EnsureScene();
        if (!engine.HasScene) return "istcad_scene_new() failed even though the DLL loaded.";

        var xs = new[] { 0.0, 10.0, 0.0 };
        var ys = new[] { 0.0, 0.0, 10.0 };
        var entities = new[]
        {
            new NativeEntity { Id = 7, PointOffset = 0, PointCount = 3, Closed = 1, ColorBgr = 0x0000FF00, MinX = 0, MinY = 0, MaxX = 10, MaxY = 10 },
        };
        if (!engine.TryBuild(xs, ys, entities)) return "istcad_scene_build() reported an error.";

        var extents = engine.TryGetExtents();
        if (extents is null) return "istcad_scene_extents() returned empty after a successful build.";

        var hits = engine.Query(-1, -1, 20, 20);
        if (hits is null || Array.IndexOf(hits, 7) < 0) return "istcad_scene_query() did not find the test entity.";

        var pixels = new byte[64 * 64 * 4];
        var drawn = engine.Render(5, 5, 4.0, 64, 64, -5, -5, 15, 15, pixels, 0xFF000000, true);
        if (drawn <= 0) return "istcad_scene_render() drew zero segments for the test triangle.";

        return $"Self-test OK (ABI v{s_probe.Value.AbiVersion}): extents=({extents.Value.MinX:0.#},{extents.Value.MinY:0.#})-({extents.Value.MaxX:0.#},{extents.Value.MaxY:0.#}), " +
               $"query found {hits.Length} entit{(hits.Length == 1 ? "y" : "ies")}, render drew {drawn} segment{(drawn == 1 ? "" : "s")}.";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_scene != IntPtr.Zero)
        {
            try { NativeEngineInterop.istcad_scene_free(_scene); } catch { /* best effort */ }
            _scene = IntPtr.Zero;
        }
    }
}
