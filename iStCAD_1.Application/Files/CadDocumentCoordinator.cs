using System.Diagnostics;
using iStCAD_1.Application.Spatial;
using iStCAD_1.Core.Model;

namespace iStCAD_1.Application.Files;

public sealed record PreparedCadDrawing(CadDrawing Drawing, CadSpatialIndex SpatialIndex, TimeSpan IndexDuration);

/// <summary>Runs CAD decoding and spatial-index preparation away from the UI thread.</summary>
public sealed class CadDocumentCoordinator(ICadFileService files)
{
    private readonly SemaphoreSlim _openGate = new(1, 1);

    public Task<PreparedCadDrawing> OpenAsync(string path, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _openGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var drawing = files.Open(path, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            var index = new CadSpatialIndex(drawing.Entities, cancellationToken, drawing.HiddenEntityIds, buildSnapCandidates: false);
            timer.Stop();
            return new PreparedCadDrawing(drawing, index, timer.Elapsed);
        }
        finally { _openGate.Release(); }
    }, cancellationToken);
}
