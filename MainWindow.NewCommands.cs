using System.Globalization;
using System.Windows;
using System.Windows.Input;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;
using Microsoft.Win32;

namespace iStCAD_1;

/// <summary>STRETCH, HATCH (pattern lines) and INSERT (a drawing file) - catalog commands that had no implementation.</summary>
public partial class MainWindow
{
    // ============================ STRETCH ============================
    private bool _stretchActive;
    private (double MinX, double MinY, double MaxX, double MaxY)? _lastCrossingWindow;
    private (double MinX, double MinY, double MaxX, double MaxY) _stretchWindow;

    private void BeginStretch()
    {
        _stretchActive = false; _lastCrossingWindow = null;
        _selectedEntityIds.Clear();
        _pendingSelectionAction = SelectionAction.Stretch;
        AppendCommandHistory("STRETCH: select objects with a CROSSING window (drag right-to-left) - vertices inside the window move, the rest stay. Then press Enter.");
        DrawingCanvas.Focus(); RefreshPropertiesPalette(); RenderSelectionOverlay();
    }

    private void StartStretchPointStage()
    {
        if (_lastCrossingWindow is not { } window) { _pendingSelectionAction = SelectionAction.Stretch; AppendCommandHistory("STRETCH: sweep a crossing window (drag right-to-left) over the part to stretch, then press Enter."); return; }
        _stretchWindow = window; _transformCopy = false;
        StartMovePointStage();
        _stretchActive = _transformActive;
    }

    private static CadEntity? StretchEntity(CadEntity entity, (double MinX, double MinY, double MaxX, double MaxY) w, Point2 offset)
    {
        bool In(Point2 p) => p.X >= w.MinX && p.X <= w.MaxX && p.Y >= w.MinY && p.Y <= w.MaxY;
        switch (entity)
        {
            case LineEntity l:
            {
                var si = In(l.Start); var ei = In(l.End);
                return !si && !ei ? null : l with { Start = si ? l.Start + offset : l.Start, End = ei ? l.End + offset : l.End };
            }
            case PolylineEntity pl:
            {
                var moved = false;
                var vertices = pl.Vertices.Select(v => { if (!In(v.Point)) return v; moved = true; return v with { Point = v.Point + offset }; }).ToArray();
                return moved ? pl with { Vertices = vertices } : null;
            }
            case ArcEntity a: return In(a.Center) ? a with { Center = a.Center + offset } : null;
            case CircleEntity c: return In(c.Center) ? c with { Center = c.Center + offset } : null;
            case CadTextEntity t: return In(t.Position) ? t with { Position = t.Position + offset } : null;
            case CadMTextEntity m: return In(m.Position) ? m with { Position = m.Position + offset } : null;
            default: return null;
        }
    }

    private void CompleteStretch(Point2 offset)
    {
        var selected = _transformEntities;
        _stretchActive = false; _transformActive = false; _transformBasePoint = null; _transformEntities = Array.Empty<CadEntity>();
        var remove = new List<CadEntity>(); var add = new List<CadEntity>();
        foreach (var e in selected)
            if (StretchEntity(e, _stretchWindow, offset) is { } stretched) { remove.Add(e); add.Add(stretched); }
        if (remove.Count == 0) AppendCommandHistory("STRETCH: no vertex lay inside the crossing window; nothing changed.");
        else { _editor.ApplyEntityChanges(remove, add); AppendCommandHistory($"STRETCH: {remove.Count:N0} object(s) stretched."); }
        UpdateDynamicInputMode(); RefreshPropertiesPalette(); RenderSelectionOverlay();
        DrawingCanvas.Focus(); RenderDrawing(false);
    }

    // ============================ HATCH (pattern lines) ============================
    private void BeginHatch()
    {
        var boundaries = SelectedHatchBoundaries();
        if (boundaries.Length > 0) { HatchSelected(); return; }
        _selectedEntityIds.Clear();
        _pendingSelectionAction = SelectionAction.Hatch;
        AppendCommandHistory("HATCH: click a closed boundary (closed polyline, rectangle or circle); window several to make islands, then Enter.");
        DrawingCanvas.Focus(); RefreshPropertiesPalette(); RenderSelectionOverlay();
    }

    private CadEntity[] SelectedHatchBoundaries() => _selectedEntityIds
        .Select(id => _spatialIndex.TryGetEntity(id, out var e) ? e : null).OfType<CadEntity>()
        .Where(e => e is CircleEntity || (e is PolylineEntity { Closed: true })).ToArray();

    /// <summary>A sensible default line spacing for the boundary's own size, rounded to 1/2/5 x a power of ten - so a small
    /// sketch and a large, real-world-scale drawing both get a reasonable number of pattern lines (not thousands) by default.</summary>
    private static double SuggestedHatchSpacing(CadEntity[] boundaries)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Add(Point2 p) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
        foreach (var boundary in boundaries)
        {
            if (boundary is CircleEntity circle) { Add(new Point2(circle.Center.X - circle.Radius, circle.Center.Y - circle.Radius)); Add(new Point2(circle.Center.X + circle.Radius, circle.Center.Y + circle.Radius)); }
            else if (boundary is PolylineEntity polyline) foreach (var v in polyline.Vertices) Add(v.Point);
        }
        if (minX > maxX) return 10;
        var diagonal = Point2.Distance(new Point2(minX, minY), new Point2(maxX, maxY));
        if (diagonal < 1e-6) return 10;
        var raw = diagonal / 40.0; // aim for roughly 30-50 pattern lines across the boundary: visible, not excessive.
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var n = raw / magnitude;
        var nice = n < 1.5 ? 1 : n < 3.5 ? 2 : n < 7.5 ? 5 : 10;
        return nice * magnitude;
    }

    /// <summary>
    /// Builds the hatch as ONE object (a HATCH proxy carrying its boundary + angle/spacing, with the actual pattern
    /// lines pre-computed for display): selecting, moving, copying or erasing it is one O(1) operation instead of
    /// touching thousands of separate LINE entities, which is both easier to use and much lighter on the drawing.
    /// EXPLODE still turns it into its separate pattern lines when that is genuinely what's needed.
    /// </summary>
    private void HatchApply(CadEntity[] boundaries, double angleDegrees, double spacing)
    {
        if (boundaries.Length == 0 || spacing <= 0) { AppendCommandHistory("HATCH: spacing must be greater than 0."); return; }

        var rings = new List<List<Point2>>();
        foreach (var boundary in boundaries)
        {
            if (boundary is CircleEntity circle)
                rings.Add(Enumerable.Range(0, 96).Select(i => new Point2(circle.Center.X + circle.Radius * Math.Cos(i * Math.Tau / 96), circle.Center.Y + circle.Radius * Math.Sin(i * Math.Tau / 96))).ToList());
            else if (boundary is PolylineEntity polyline)
            {
                var ring = PolylineTessellator.Tessellate(polyline).ToList();
                if (ring.Count > 2 && Point2.Distance(ring[0], ring[^1]) < 1e-12) ring.RemoveAt(ring.Count - 1);
                if (ring.Count >= 3) rings.Add(ring);
            }
        }
        var angle = angleDegrees * Math.PI / 180;
        var ux = Math.Cos(angle); var uy = Math.Sin(angle); var nx = -uy; var ny = ux;
        var all = rings.SelectMany(r => r).ToArray();
        if (all.Length < 3) { AppendCommandHistory("HATCH: the boundary has no usable area."); return; }
        var dMin = all.Min(q => q.X * nx + q.Y * ny); var dMax = all.Max(q => q.X * nx + q.Y * ny);
        var first = (long)Math.Ceiling(dMin / spacing); var last = (long)Math.Floor(dMax / spacing);
        if (last - first > 20_000)
        {
            AppendCommandHistory($"HATCH: spacing {spacing:0.###} is too fine for this boundary ({last - first:N0} scan lines) - try a larger spacing, e.g. {spacing * (last - first) / 2000.0:0.##} or more.");
            return;
        }

        var boundsMin = new Point2(all.Min(q => q.X), all.Min(q => q.Y));
        var boundsMax = new Point2(all.Max(q => q.X), all.Max(q => q.Y));
        var layer = boundaries[0].Layer; var template = boundaries[0];
        var paths = new List<ProxyPreviewPath>(); var crossings = new List<double>();
        for (var k = first; k <= last; k++)
        {
            var d = k * spacing; crossings.Clear();
            foreach (var ring in rings)
                for (var i = 0; i < ring.Count; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                    var da = a.X * nx + a.Y * ny; var db = b.X * nx + b.Y * ny;
                    if (!((da <= d && db > d) || (db <= d && da > d))) continue;
                    var f = (d - da) / (db - da);
                    crossings.Add((a.X + (b.X - a.X) * f) * ux + (a.Y + (b.Y - a.Y) * f) * uy);
                }
            crossings.Sort();
            for (var i = 0; i + 1 < crossings.Count; i += 2)
            {
                if (crossings[i + 1] - crossings[i] < 1e-9) continue;
                paths.Add(new ProxyPreviewPath(new[]
                {
                    new Point2(d * nx + crossings[i] * ux, d * ny + crossings[i] * uy),
                    new Point2(d * nx + crossings[i + 1] * ux, d * ny + crossings[i + 1] * uy)
                }, false));
            }
            if (paths.Count > 100_000) { AppendCommandHistory("HATCH: too many pattern lines; use a larger spacing."); return; }
        }
        if (paths.Count == 0) { AppendCommandHistory("HATCH: no hatch lines fall inside the boundary."); return; }

        var hatch = new ProxyCadEntity("HATCH", boundsMin, boundsMax, true, Guid.NewGuid(), layer,
            ProxyGraphicsPaths: paths, Hatch: new CadHatchData(rings.Select(r => (IReadOnlyList<Point2>)r.ToArray()).ToArray(), angleDegrees, spacing));
        var styled = _editor.StyleNew(hatch);
        if (template.ExplicitColor is not null) styled = styled with { ExplicitColor = template.ExplicitColor, DisplayColor = template.DisplayColor };
        _editor.ApplyEntityChanges(Array.Empty<CadEntity>(), new[] { styled });
        _selectedEntityIds.Clear(); _selectedEntityIds.Add(styled.Id);
        RefreshPropertiesPalette(); RenderSelectionOverlay(); RenderDrawing(false);
        AppendCommandHistory($"HATCH: created as ONE object ({paths.Count:N0} pattern lines inside) at {angleDegrees:0.##}°, spacing {spacing:0.###}. Move/copy/erase it like any single object; EXPLODE turns it into separate lines if you ever need that.");
    }

    // ============================ INSERT (a drawing file) ============================
    private void BeginInsertFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "INSERT - choose a drawing to insert", Filter = "CAD drawings (*.dwg;*.dxf)|*.dwg;*.dxf|All files (*.*)|*.*",
            InitialDirectory = GetLastInput("insert.folder") is { Length: > 0 } folder && System.IO.Directory.Exists(folder) ? folder : string.Empty
        };
        if (dialog.ShowDialog(this) != true) return;
        SetLastInput("insert.folder", System.IO.Path.GetDirectoryName(dialog.FileName) ?? string.Empty);
        CadDrawing source;
        try { Mouse.OverrideCursor = Cursors.Wait; source = _cadFiles.Open(dialog.FileName, CancellationToken.None); }
        catch (Exception ex) { AppendCommandHistory("INSERT failed: " + ex.Message); return; }
        finally { Mouse.OverrideCursor = null; }
        var supported = source.Entities.Where(e => e is LineEntity or CircleEntity or ArcEntity or PolylineEntity or CadTextEntity or CadMTextEntity).ToArray();
        var skipped = source.Entities.Count - supported.Length;
        if (supported.Length == 0) { AppendCommandHistory($"INSERT: \"{System.IO.Path.GetFileName(dialog.FileName)}\" has no insertable objects ({skipped:N0} unsupported: blocks, hatches, dimensions...)."); return; }
        foreach (var layer in supported.Select(e => e.Layer).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!_editor.Drawing.LayerColors.ContainsKey(layer))
                _cadFiles.SetLayerColor(_editor.Drawing, layer, source.LayerColors.TryGetValue(layer, out var color) ? color : new CadColor(255, 255, 255));
        _selectedEntityIds.Clear();
        _stretchActive = false; _transformCopy = true; _transformEntities = supported;
        _transformActive = true; _transformBasePoint = new Point2(0, 0); // the file's origin lands on the picked point
        DynamicPromptText.Text = "Specify insertion point:";
        DynamicValueTextBox.Clear();
        UpdateDynamicInputMode(); PositionDynamicInput(_lastPointer); FocusDynamicInput();
        AppendCommandHistory($"INSERT: {supported.Length:N0} object(s) from \"{System.IO.Path.GetFileName(dialog.FileName)}\"{(skipped > 0 ? $" ({skipped:N0} unsupported skipped)" : "")}. Specify insertion point:");
    }
}
