using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>AutoCAD-style view commands: VIEW (named views), ZOOM options, ZOOMIN/ZOOMOUT, ZW window by two clicks.</summary>
public partial class MainWindow
{
    private Point? _zoomWindowFirst;

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    /// <summary>Handles view-related commands; returns true when the command text was consumed.</summary>
    private bool TryHandleViewCommand(string normalized)
    {
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;
        switch (tokens[0])
        {
            case "VIEW": case "-VIEW": case "V" when tokens.Length > 1:
                NamedViewCommand(tokens.Skip(1).ToArray());
                return true;
            case "ALIGN": case "AL": case "QSELECT": case "QSEL": case "FILTER": case "FI": case "LIST": case "SPLINE": case "SPL": case "XLINE": case "XL": case "DIVIDE": case "DIV": case "MEASURE":
                return TryHandleGuidedCommands(tokens);
            case "OVERKILL": case "CLEANUP": OverkillCommand(tokens.Skip(1).ToArray()); return true;
            case "POLYGON": case "POL": case "RPOLYGON": case "RPOL": case "ELLIPSE": case "EL": case "DONUT": case "DO": case "LENGTHEN": case "LEN": case "SELECTSIMILAR": case "SSIM":
            case "PEDIT": case "PE": case "REVERSE": case "COPYCLIP": case "COPYBASE": case "CUTCLIP": case "PASTECLIP": case "PASTE":
                return TryHandleMoreAutoCad(tokens);
            case "PICKDEBUG": _pickDebug = !_pickDebug; AppendCommandHistory("PICKDEBUG " + (_pickDebug ? "ON: a click that selects nothing explains why (hidden / locked layer)." : "OFF.")); return true;
            case "DWGCHECK": case "FOREIGNDWG": DwgCheckHelp(); return true;
            case "VNFIX": case "FIXVIET": case "VIETFIX": VnFixCommand(tokens.Skip(1).ToArray()); return true;
            case "RENDERMODE": case "RM": RenderModeCommand(tokens.Skip(1).ToArray()); return true;
            case "NATIVESTATUS": case "NATIVE": NativeStatusCommand(); return true;
            case "ZOOMIN": case "ZI": ZoomByFactor(2); return true;
            case "ZOOMOUT": case "ZOUT": ZoomByFactor(0.5); return true;
            case "ZW": case "ZWINDOW": BeginZoomWindow(); return true;
            case "ZA": case "ZALL": ZoomAbsoluteExtents(); return true;
            case "ZO": case "ZOBJECT": ZoomToSelection(); return true;
            case "ZC": case "ZCENTER": ZoomOption(new[] { "C" }.Concat(tokens.Skip(1)).ToArray()); return true;
            case "ZS": case "ZSCALE": ZoomOption(new[] { "S" }.Concat(tokens.Skip(1)).ToArray()); return true;
            case "ZOOM" when tokens.Length > 1:
                ZoomOption(tokens.Skip(1).ToArray());
                return true;
        }
        return false;
    }

    private void ZoomOption(string[] args)
    {
        var option = args[0];
        switch (option)
        {
            case "E": case "EXTENTS": ZoomExtents_Click(this, new RoutedEventArgs()); return;
            case "A": case "ALL": ZoomAbsoluteExtents(); return;
            case "P": case "PREVIOUS": ZoomPrevious(); return;
            case "W": case "WINDOW": BeginZoomWindow(); return;
            case "O": case "OBJECT": ZoomToSelection(); return;
            case "IN": ZoomByFactor(2); return;
            case "OUT": ZoomByFactor(0.5); return;
            case "C": case "CENTER":
            {
                // ZOOM C x,y [height]  (height = world units visible vertically)
                if (args.Length < 2) { AppendCommandHistory("ZOOM Center: use  ZOOM C x,y [height]"); return; }
                var xy = args[1].Split(',');
                if (xy.Length != 2 || !TryParseNumber(xy[0], out var cx) || !TryParseNumber(xy[1], out var cy)) { AppendCommandHistory("ZOOM Center: invalid point. Use x,y."); return; }
                var scale = _viewScale;
                if (args.Length > 2)
                {
                    if (!TryParseNumber(args[2], out var h) || h <= 0) { AppendCommandHistory("ZOOM Center: invalid height."); return; }
                    scale = Math.Clamp(DrawingCanvas.ActualHeight / h, MinViewportScale, MaxViewportScale);
                }
                ApplyView(new Point2(cx, cy), scale, "Zoomed to center.");
                return;
            }
            case "S": case "SCALE":
                if (args.Length < 2) { AppendCommandHistory("ZOOM Scale: use  ZOOM S <n>  (relative to extents) or  <n>X  (relative to current view)."); return; }
                ZoomOption(new[] { args[1] });
                return;
        }
        // Numeric: "2" = relative to full extents, "2X" = relative to the current view.
        var text = option.EndsWith("XP", StringComparison.Ordinal) ? option[..^2] : option.EndsWith('X') ? option[..^1] : option;
        if (TryParseNumber(text, out var factor) && factor > 0)
        {
            var relativeToCurrent = option.EndsWith('X') || option.EndsWith("XP", StringComparison.Ordinal);
            var target = (relativeToCurrent ? _viewScale : _fitScale) * factor;
            ApplyView(_viewCenter, Math.Clamp(target, MinViewportScale, MaxViewportScale), $"Zoom scale {option}.");
            return;
        }
        AppendCommandHistory("ZOOM options: E(xtents), A(ll), P(revious), W(indow), O(bject), C(enter) x,y [h], S(cale) n / nX, IN, OUT.");
    }

    private void ApplyView(Point2 center, double scale, string message)
    {
        if (!double.IsFinite(center.X) || !double.IsFinite(center.Y) || !double.IsFinite(scale) || scale <= 0) return;
        _wheelDebounceTimer.Stop(); _pendingWheelZoom = false; ResetWheelZoomPreview();
        SavePreviousView();
        _viewCenter = center; _viewScale = Math.Clamp(scale, MinViewportScale, MaxViewportScale);
        UpdateZoomControls();
        RenderDrawing(false);
        AppendCommandHistory(message);
    }

    /// <summary>ZOOM All: the true extents of every object, including far-away strays that zoom extents ignores.</summary>
    private void ZoomAbsoluteExtents()
    {
        SavePreviousView(); FitView(absolute: true); RenderDrawing(false);
        AppendCommandHistory("Zoomed to absolute drawing extents.");
    }

    private void ZoomByFactor(double factor) =>
        ApplyView(_viewCenter, _viewScale * factor, factor > 1 ? "Zoomed in." : "Zoomed out.");

    private void ZoomToWorldRect(double minX, double minY, double maxX, double maxY, string message)
    {
        var width = DrawingCanvas.ActualWidth; var height = DrawingCanvas.ActualHeight;
        if (width < 20 || height < 20) return;
        var spanX = Math.Max(maxX - minX, 1e-9); var spanY = Math.Max(maxY - minY, 1e-9);
        var scale = Math.Min(width / spanX, height / spanY) * 0.96; // small margin like AutoCAD's zoom object
        ApplyView(new Point2((minX + maxX) / 2, (minY + maxY) / 2), scale, message);
    }

    private void ZoomToSelection()
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        var found = 0;
        foreach (var id in _selectedEntityIds)
        {
            if (!_spatialIndex.TryGetBounds(id, out var b)) continue;
            minX = Math.Min(minX, Math.Min(b.MinX, b.MaxX)); maxX = Math.Max(maxX, Math.Max(b.MinX, b.MaxX));
            minY = Math.Min(minY, Math.Min(b.MinY, b.MaxY)); maxY = Math.Max(maxY, Math.Max(b.MinY, b.MaxY));
            found++;
        }
        if (found == 0) { AppendCommandHistory("ZOOM Object: select objects first."); return; }
        ZoomToWorldRect(minX, minY, maxX, maxY, $"Zoomed to {found:N0} selected object(s).");
    }

    // ---- ZOOM Window by two clicks ---------------------------------------------

    private void BeginZoomWindow()
    {
        BeginPickTool("ZOOMW", "ZOOM Window: Specify first corner:");
        _zoomWindowFirst = null;
    }

    private void HandleZoomWindowClick(Point screenPoint)
    {
        if (_zoomWindowFirst is not { } first)
        {
            _zoomWindowFirst = screenPoint;
            SelectionDragOverlay.Width = SelectionDragOverlay.Height = 0;
            SelectionDragOverlay.Visibility = Visibility.Visible;
            UpdateSelectionBox(screenPoint, screenPoint);
            AppendCommandHistory("ZOOM Window: Specify opposite corner:");
            return;
        }
        _zoomWindowFirst = null;
        SelectionDragOverlay.Visibility = Visibility.Collapsed;
        _pickTool = null;
        if (Math.Abs(screenPoint.X - first.X) < 3 || Math.Abs(screenPoint.Y - first.Y) < 3) { AppendCommandHistory("ZOOM Window: window too small."); return; }
        var a = ScreenToWorld(first); var b = ScreenToWorld(screenPoint);
        ZoomToWorldRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), "Zoomed to window.");
    }

    private void CancelZoomWindowVisuals()
    {
        if (_zoomWindowFirst is null) return;
        _zoomWindowFirst = null;
        SelectionDragOverlay.Visibility = Visibility.Collapsed;
    }

    // ---- Named views ----------------------------------------------------------

    private void NamedViewCommand(string[] args)
    {
        var session = _activeSession;
        if (session is null) return;
        var views = session.NamedViews;
        if (args.Length == 0 || args[0] is "L" or "LIST" or "?")
        {
            AppendCommandHistory(views.Count == 0
                ? "VIEW: no named views. Use  VIEW S <name>  to save the current view."
                : "VIEW: " + string.Join(", ", views.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase)) + "   (VIEW R <name> restore, S save, D delete)");
            return;
        }
        string Name(int from) => string.Join(' ', args.Skip(from));
        switch (args[0])
        {
            case "S": case "SAVE":
            {
                var name = Name(1);
                if (string.IsNullOrWhiteSpace(name)) { AppendCommandHistory("VIEW Save: give a name, e.g.  VIEW S PLAN1"); return; }
                views[name] = (_viewCenter, _viewScale);
                AppendCommandHistory($"VIEW: saved \"{name}\" (center {_viewCenter.X:0.###}, {_viewCenter.Y:0.###}; scale {_viewScale:0.#####}). Named views live for this session.");
                return;
            }
            case "D": case "DELETE":
            {
                var name = Name(1);
                AppendCommandHistory(views.Remove(name) ? $"VIEW: deleted \"{name}\"." : $"VIEW: \"{name}\" not found.");
                return;
            }
            case "R": case "RESTORE": case "SET":
                RestoreNamedView(views, Name(1));
                return;
            default:
                RestoreNamedView(views, Name(0));
                return;
        }
    }

    private void RestoreNamedView(System.Collections.Generic.Dictionary<string, (Point2 Center, double Scale)> views, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !views.TryGetValue(name, out var view))
        {
            AppendCommandHistory($"VIEW: \"{name}\" not found. Use VIEW to list named views.");
            return;
        }
        ApplyView(view.Center, view.Scale, $"VIEW: restored \"{name}\".");
    }

    /// <summary>Liang–Barsky clip of a segment to a rectangle; false when entirely outside.</summary>
    private static bool ClipSegmentToRect(Point2 a, Point2 b, double minX, double minY, double maxX, double maxY, out Point2 ca, out Point2 cb)
    {
        ca = a; cb = b;
        if (!double.IsFinite(a.X) || !double.IsFinite(a.Y) || !double.IsFinite(b.X) || !double.IsFinite(b.Y)) return false;
        // Trivial accept (the common case) and reject.
        var aInside = a.X >= minX && a.X <= maxX && a.Y >= minY && a.Y <= maxY;
        var bInside = b.X >= minX && b.X <= maxX && b.Y >= minY && b.Y <= maxY;
        if (aInside && bInside) return true;
        if ((a.X < minX && b.X < minX) || (a.X > maxX && b.X > maxX) || (a.Y < minY && b.Y < minY) || (a.Y > maxY && b.Y > maxY)) return false;
        double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
        bool Clip(double p, double q)
        {
            if (p == 0) return q >= 0;
            var r = q / p;
            if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
            return true;
        }
        if (!Clip(-dx, a.X - minX) || !Clip(dx, maxX - a.X) || !Clip(-dy, a.Y - minY) || !Clip(dy, maxY - a.Y)) return false;
        ca = new Point2(a.X + t0 * dx, a.Y + t0 * dy);
        cb = new Point2(a.X + t1 * dx, a.Y + t1 * dy);
        return true;
    }

    /// <summary>1-px Bresenham line into a Pbgra32 (opaque) buffer; endpoints are already clipped to the view, pixels are bounds-checked.</summary>
    private static void DrawPixelLine(byte[] buffer, int width, int height, int x0, int y0, int x1, int y1, byte blue, byte green, byte red)
    {
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;
        var guard = dx - dy + 2; // never loop more than the pixel length of the line
        while (guard-- > 0)
        {
            if ((uint)x0 < (uint)width && (uint)y0 < (uint)height)
            {
                var offset = (y0 * width + x0) * 4;
                buffer[offset] = blue; buffer[offset + 1] = green; buffer[offset + 2] = red; buffer[offset + 3] = 255;
            }
            if (x0 == x1 && y0 == y1) break;
            var e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
}
