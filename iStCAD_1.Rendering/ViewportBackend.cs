using System.Diagnostics;
using System.Windows.Controls;

namespace iStCAD_1.Rendering;

/// <summary>Render backend boundary. The existing WPF UI and document own all interaction state.</summary>
public interface IViewportBackend
{
    string Id { get; }
    bool IsAvailable { get; }
    string Description { get; }
    void Attach(CadViewportControl viewport);
    void Detach();
}

/// <summary>The verified integration target for v1.0.42: retains original visuals and input handlers.</summary>
public sealed class NativeWpfViewportBackend : IViewportBackend
{
    public string Id => "native-wpf";
    public bool IsAvailable => true;
    public string Description => "iSt-CAD v1.0.42 WPF renderer, R-tree and geometry LRU cache";
    public void Attach(CadViewportControl viewport) { ArgumentNullException.ThrowIfNull(viewport); }
    public void Detach() { }
}

/// <summary>Experimental native-WPF lightweight composition path. This is NOT the upstream Tida.CAD code.
/// It is an integration test backend using only iSt-CAD models and frozen WPF drawing primitives.
/// No new document model, selection logic or undo stack is created.</summary>
public sealed class LightweightWpfViewportBackend : IViewportBackend
{
    public string Id => "lightweight-wpf";
    public bool IsAvailable => true;
    public string Description => "Experimental lightweight WPF composition, inspired by DrawingVisual principles; no Tida.CAD source copied";
    public void Attach(CadViewportControl viewport) { ArgumentNullException.ThrowIfNull(viewport); }
    public void Detach() { }
}

/// <summary>Optional external source adapters are deliberately not registered until tested independently.</summary>
public static class ViewportBackendRegistry
{
    public static IReadOnlyList<string> Candidates { get; } = Array.AsReadOnly(new[] { "native-wpf", "lightweight-wpf", "procad-adapter", "tida-wpf-adapter" });
    public static IViewportBackend Create(string? id = null) => id switch
    {
        null or "native-wpf" => new NativeWpfViewportBackend(),
        "lightweight-wpf" => new LightweightWpfViewportBackend(),
        "procad-adapter" => throw new NotSupportedException("ProCad renderer is Avalonia/Skia based and requires a validated WPF-compatible adapter; no third-party code has been merged."),
        "tida-wpf-adapter" => throw new NotSupportedException("Tida.CAD WPF renderer needs license and geometry-coordinate validation before activation."),
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Unknown viewport renderer.")
    };
}

/// <summary>Lightweight frame timing counters. Sampling does not force a redraw.</summary>
public sealed class ViewportPerformanceCounter
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _sync = new();
    private long _frames, _elapsedTicks;
    public IDisposable MeasureFrame() => new FrameScope(this);
    public void RecordCompletedFrame(TimeSpan elapsed) { if (elapsed.Ticks >= 0) Record((long)(elapsed.TotalSeconds * Stopwatch.Frequency)); }
    public (long Frames, double AverageMs, double ElapsedSeconds) Snapshot()
    {
        lock (_sync) return (_frames, _frames == 0 ? 0 : (double)_elapsedTicks * 1000 / Stopwatch.Frequency / _frames, _clock.Elapsed.TotalSeconds);
    }
    private void Record(long ticks) { lock (_sync) { _frames++; _elapsedTicks += ticks; } }
    private sealed class FrameScope : IDisposable
    {
        private readonly ViewportPerformanceCounter _owner;
        private readonly long _start = Stopwatch.GetTimestamp();
        private bool _finished;
        public FrameScope(ViewportPerformanceCounter owner) => _owner = owner;
        public void Dispose() { if (_finished) return; _finished = true; _owner.Record(Stopwatch.GetTimestamp() - _start); }
    }
}
