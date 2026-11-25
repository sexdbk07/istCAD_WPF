using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using iStCAD_1.Application.Spatial;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>
/// AutoCAD-style grip editing: press a grip of a selected object and drag (or click it, move, click again) to stretch it.
/// Line ends / polyline vertices / arc ends stretch, midpoints and centers move, circle quadrants and arc midpoints change the radius.
/// </summary>
public partial class MainWindow
{
    private enum GripKind { LineStart, LineEnd, LineMid, Vertex, SegmentMid, Center, Radius, ArcStart, ArcEnd, ArcMid, Position }

    private readonly record struct Grip(Guid EntityId, GripKind Kind, int Index, Point2 Point);

    private bool _gripActive;          // a grip is being edited (dragging or "hot" after a click)
    private bool _gripSticky;          // click-move-click mode
    private bool _gripMoved;
    private Grip _grip;
    private CadEntity? _gripEntity;
    private Point _gripPressScreen;
    private Point2 _gripCurrent;
    private Point2? _gripAnchor;

    private static int NormalizeIndex(int i, int n) => ((i % n) + n) % n;

    private static double NormalizeDegrees(double d) { d %= 360; return d < 0 ? d + 360 : d; }

    private static IEnumerable<Grip> EnumerateGrips(CadEntity entity)
    {
        switch (entity)
        {
            case LineEntity line:
                yield return new Grip(line.Id, GripKind.LineStart, 0, line.Start);
                yield return new Grip(line.Id, GripKind.LineEnd, 1, line.End);
                yield return new Grip(line.Id, GripKind.LineMid, 2, new Point2((line.Start.X + line.End.X) / 2, (line.Start.Y + line.End.Y) / 2));
                break;
            case PolylineEntity polyline:
            {
                var v = polyline.Vertices;
                for (var i = 0; i < v.Count; i++) yield return new Grip(polyline.Id, GripKind.Vertex, i, v[i].Point);
                var segments = polyline.Closed ? v.Count : v.Count - 1;
                for (var i = 0; i < segments; i++)
                {
                    if (Math.Abs(v[i].Bulge) > 1e-9) continue; // arc segment: the vertex grips are enough
                    var a = v[i].Point; var b = v[(i + 1) % v.Count].Point;
                    yield return new Grip(polyline.Id, GripKind.SegmentMid, i, new Point2((a.X + b.X) / 2, (a.Y + b.Y) / 2));
                }
                break;
            }
            case CircleEntity circle:
                yield return new Grip(circle.Id, GripKind.Center, 0, circle.Center);
                yield return new Grip(circle.Id, GripKind.Radius, 0, new Point2(circle.Center.X + circle.Radius, circle.Center.Y));
                yield return new Grip(circle.Id, GripKind.Radius, 1, new Point2(circle.Center.X, circle.Center.Y + circle.Radius));
                yield return new Grip(circle.Id, GripKind.Radius, 2, new Point2(circle.Center.X - circle.Radius, circle.Center.Y));
                yield return new Grip(circle.Id, GripKind.Radius, 3, new Point2(circle.Center.X, circle.Center.Y - circle.Radius));
                break;
            case ArcEntity arc:
            {
                var s = arc.StartAngleDegrees * Math.PI / 180; var e = arc.EndAngleDegrees * Math.PI / 180;
                var sweep = NormalizeDegrees(arc.EndAngleDegrees - arc.StartAngleDegrees); if (sweep < 1e-9) sweep = 360;
                var m = (arc.StartAngleDegrees + sweep / 2) * Math.PI / 180;
                yield return new Grip(arc.Id, GripKind.Center, 0, arc.Center);
                yield return new Grip(arc.Id, GripKind.ArcStart, 0, new Point2(arc.Center.X + arc.Radius * Math.Cos(s), arc.Center.Y + arc.Radius * Math.Sin(s)));
                yield return new Grip(arc.Id, GripKind.ArcEnd, 1, new Point2(arc.Center.X + arc.Radius * Math.Cos(e), arc.Center.Y + arc.Radius * Math.Sin(e)));
                yield return new Grip(arc.Id, GripKind.ArcMid, 2, new Point2(arc.Center.X + arc.Radius * Math.Cos(m), arc.Center.Y + arc.Radius * Math.Sin(m)));
                break;
            }
            case CadTextEntity text: yield return new Grip(text.Id, GripKind.Position, 0, text.Position); break;
            case CadMTextEntity mtext: yield return new Grip(mtext.Id, GripKind.Position, 0, mtext.Position); break;
        }
    }

    /// <summary>Returns the entity edited so that grip <paramref name="g"/> sits at <paramref name="p"/>; null when unsupported.</summary>
    private static CadEntity? ApplyGrip(CadEntity entity, Grip g, Point2 p)
    {
        var delta = p - g.Point;
        switch (entity)
        {
            case LineEntity line:
                return g.Kind switch
                {
                    GripKind.LineStart => line with { Start = p },
                    GripKind.LineEnd => line with { End = p },
                    GripKind.LineMid => line with { Start = line.Start + delta, End = line.End + delta },
                    _ => null
                };
            case PolylineEntity polyline:
            {
                var vertices = polyline.Vertices.ToArray();
                if (g.Index < 0 || g.Index >= vertices.Length) return null;
                if (g.Kind == GripKind.Vertex) vertices[g.Index] = vertices[g.Index] with { Point = p };
                else if (g.Kind == GripKind.SegmentMid)
                {
                    var j = (g.Index + 1) % vertices.Length;
                    vertices[g.Index] = vertices[g.Index] with { Point = vertices[g.Index].Point + delta };
                    vertices[j] = vertices[j] with { Point = vertices[j].Point + delta };
                }
                else return null;
                return polyline with { Vertices = vertices };
            }
            case CircleEntity circle:
                if (g.Kind == GripKind.Center) return circle with { Center = circle.Center + delta };
                if (g.Kind == GripKind.Radius)
                {
                    var r = Point2.Distance(circle.Center, p);
                    return r > 1e-9 ? circle with { Radius = r } : null;
                }
                return null;
            case ArcEntity arc:
                switch (g.Kind)
                {
                    case GripKind.Center: return arc with { Center = arc.Center + delta };
                    case GripKind.ArcStart: return arc with { StartAngleDegrees = NormalizeDegrees(Math.Atan2(p.Y - arc.Center.Y, p.X - arc.Center.X) * 180 / Math.PI) };
                    case GripKind.ArcEnd: return arc with { EndAngleDegrees = NormalizeDegrees(Math.Atan2(p.Y - arc.Center.Y, p.X - arc.Center.X) * 180 / Math.PI) };
                    case GripKind.ArcMid:
                    {
                        var r = Point2.Distance(arc.Center, p);
                        return r > 1e-9 ? arc with { Radius = r } : null;
                    }
                }
                return null;
            case CadTextEntity text when g.Kind == GripKind.Position: return text with { Position = text.Position + delta };
            case CadMTextEntity mtext when g.Kind == GripKind.Position: return mtext with { Position = mtext.Position + delta };
        }
        return null;
    }

    /// <summary>The fixed point the rubber band / ortho is measured from while a grip is dragged.</summary>
    private static Point2? GripAnchorFor(CadEntity entity, Grip g)
    {
        switch (entity)
        {
            case LineEntity line:
                return g.Kind == GripKind.LineStart ? line.End : g.Kind == GripKind.LineEnd ? line.Start : g.Point;
            case PolylineEntity polyline when g.Kind == GripKind.Vertex && polyline.Vertices.Count > 1:
            {
                var n = polyline.Vertices.Count;
                if (polyline.Closed || g.Index > 0) return polyline.Vertices[NormalizeIndex(g.Index - 1, n)].Point;
                return polyline.Vertices[1].Point;
            }
            case CircleEntity circle when g.Kind == GripKind.Radius: return circle.Center;
            case ArcEntity arc when g.Kind is GripKind.ArcStart or GripKind.ArcEnd or GripKind.ArcMid: return arc.Center;
            default: return g.Point;
        }
    }

    private bool TryFindGripAt(Point screen, out Grip grip, out CadEntity? entity)
    {
        grip = default; entity = null;
        if (_selectedEntityIds.Count == 0 || _selectedEntityIds.Count > 2048) return false;
        var tolerance = Math.Max(_gripSize / 2 + 3, 7);
        var best = double.MaxValue;
        foreach (var id in _selectedEntityIds)
        {
            if (!_spatialIndex.TryGetEntity(id, out var candidate) || IsLayerLocked(candidate.Layer)) continue;
            foreach (var g in EnumerateGrips(candidate))
            {
                var s = WorldToScreen(g.Point);
                var d = Math.Max(Math.Abs(s.X - screen.X), Math.Abs(s.Y - screen.Y));
                if (d <= tolerance && d < best) { best = d; grip = g; entity = candidate; }
            }
        }
        return entity is not null;
    }

    /// <summary>Left-button press: starts (or finishes, in hot mode) a grip edit. Returns true when the event was consumed.</summary>
    private bool TryHandleGripPress(MouseButtonEventArgs e)
    {
        if (_gripActive && _gripSticky)
        {
            UpdateGripPointer();
            CommitGrip();
            return true;
        }
        if (_pendingSelectionAction != SelectionAction.None || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || e.ClickCount >= 2) return false;
        if (!TryFindGripAt(_lastPointer, out var grip, out var entity) || entity is null) return false;
        _gripActive = true; _gripSticky = false; _gripMoved = false;
        _grip = grip; _gripEntity = entity; _gripPressScreen = _lastPointer;
        _gripCurrent = grip.Point;
        _gripAnchor = GripAnchorFor(entity, grip);
        if (!_spatialIndex.SnapCandidatesReady && _activeSession is not null) QueueSnapCandidateBuild(_activeSession);
        DrawingCanvas.CaptureMouse();
        RenderGripPreview();
        return true;
    }

    private void UpdateGripPointer()
    {
        var p = ScreenToWorld(_lastPointer);
        OsnapResult? snapped = null;
        if (StatusOsnapToggle.IsChecked == true && _osnapModes.Count > 0 && _spatialIndex.SnapCandidatesReady)
        {
            var tolerance = 10 / Math.Max(_viewScale, 1e-9);
            if (_spatialIndex.TryFindSnap(p, tolerance, _osnapModes, out var snap) && Point2.Distance(snap.Point, _grip.Point) > 1e-9) snapped = snap;
        }
        UpdateOsnapMarker(snapped);
        if (snapped is { } s) p = s.Point;
        else if (_gripAnchor is Point2 a && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            var dx = p.X - a.X; var dy = p.Y - a.Y;
            if (StatusOrthoToggle.IsChecked == true)
                p = Math.Abs(dx) >= Math.Abs(dy) ? new Point2(p.X, a.Y) : new Point2(a.X, p.Y);
            else if (StatusPolarToggle.IsChecked == true && (_grip.Kind is GripKind.LineStart or GripKind.LineEnd or GripKind.Vertex))
            {
                var dist = Math.Sqrt(dx * dx + dy * dy);
                const double step = Math.PI / 12.0;
                var ang = Math.Round(Math.Atan2(dy, dx) / step) * step;
                p = new Point2(a.X + dist * Math.Cos(ang), a.Y + dist * Math.Sin(ang));
            }
        }
        _gripCurrent = p;
    }

    private void GripMouseMove()
    {
        if (!_gripMoved && Math.Abs(_lastPointer.X - _gripPressScreen.X) < 4 && Math.Abs(_lastPointer.Y - _gripPressScreen.Y) < 4 && !_gripSticky) return;
        _gripMoved = true;
        UpdateGripPointer();
        RenderGripPreview();
    }

    /// <summary>Left-button release: a real drag commits, a plain click arms hot-grip mode (move, then click to place).</summary>
    private bool TryHandleGripRelease()
    {
        if (!_gripActive || _gripSticky) return false;
        DrawingCanvas.ReleaseMouseCapture();
        if (_gripMoved) { UpdateGripPointer(); CommitGrip(); }
        else
        {
            _gripSticky = true; _gripMoved = true;
            AppendCommandHistory("** STRETCH ** Specify stretch point (Esc to cancel):");
            RenderGripPreview();
        }
        return true;
    }

    private void CommitGrip()
    {
        var original = _gripEntity; var grip = _grip; var target = _gripCurrent;
        EndGrip();
        if (original is null) return;
        try
        {
            var updated = ApplyGrip(original, grip, target);
            if (updated is null || updated.Equals(original)) { RenderGripPreview(); return; }
            _editor.ApplyEntityChanges([original], [updated]);
            _selectedEntityIds.Clear(); _selectedEntityIds.Add(updated.Id);
            _lastSelectionOverlayKey = null;
            RefreshPropertiesPalette();
            AppendCommandHistory($"Stretched {GetEntityTypeName(updated)} to {target.X:0.###}, {target.Y:0.###}.");
            RenderDrawing(false);
        }
        catch (Exception ex)
        {
            AppendCommandHistory($"Grip edit failed: {ex.Message}");
        }
        DrawingCanvas.Focus();
    }

    private void EndGrip()
    {
        _gripActive = false; _gripSticky = false; _gripMoved = false; _gripEntity = null; _gripAnchor = null;
        if (DrawingCanvas.IsMouseCaptured) DrawingCanvas.ReleaseMouseCapture();
        UpdateOsnapMarker(null);
        RenderGripPreview();
    }

    private void CancelGrip()
    {
        if (!_gripActive) return;
        EndGrip();
        AppendCommandHistory("*Cancel*");
    }

    private void RenderGripPreview()
    {
        if (DrawingPreviewImage is null || DrawingCanvas is null) return;
        if (!_gripActive || _gripEntity is null)
        {
            if (!IsDynamicCommandActive) DrawingPreviewImage.Source = null;
            return;
        }
        DrawingPreviewImage.Width = DrawingCanvas.ActualWidth; DrawingPreviewImage.Height = DrawingCanvas.ActualHeight;
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, DrawingCanvas.ActualWidth, DrawingCanvas.ActualHeight));
            var ghost = new SolidColorBrush(Color.FromRgb(120, 210, 255)); ghost.Freeze();
            var warm = new SolidColorBrush(Color.FromRgb(255, 90, 80)); warm.Freeze();
            var edited = _gripMoved ? ApplyGrip(_gripEntity, _grip, _gripCurrent) : _gripEntity;
            switch (edited)
            {
                case LineEntity line: DrawLine(context, line.Start, line.End, ghost, 1.3, dashed: true); break;
                case CircleEntity circle: DrawCircle(context, circle.Center, circle.Radius, ghost, 1.3, dashed: true); break;
                case ArcEntity arc: DrawArc(context, arc, ghost, 1.3, dashed: true); break;
                case PolylineEntity polyline: DrawPolyline(context, polyline, ghost, 1.3, dashed: true); break;
                case CadTextEntity or CadMTextEntity:
                {
                    var at = WorldToScreen(_gripCurrent);
                    context.DrawLine(new Pen(ghost, 1), new Point(at.X - 8, at.Y), new Point(at.X + 8, at.Y));
                    context.DrawLine(new Pen(ghost, 1), new Point(at.X, at.Y - 8), new Point(at.X, at.Y + 8));
                    break;
                }
            }
            if (_gripMoved && _gripAnchor is Point2 anchor && Point2.Distance(anchor, _gripCurrent) > 1e-9 && _grip.Kind is not (GripKind.LineMid or GripKind.SegmentMid or GripKind.Center or GripKind.Position))
                DrawLine(context, anchor, _gripCurrent, warm, 1, dashed: true);
            var half = Math.Max(_gripSize, 7) / 2;
            var hot = WorldToScreen(_gripMoved ? _gripCurrent : _grip.Point);
            context.DrawRectangle(warm, null, new Rect(hot.X - half, hot.Y - half, half * 2, half * 2));
            var readout = BuildGripReadout(edited);
            if (readout is not null)
            {
                var text = new FormattedText(readout, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, warm, 1);
                context.DrawText(text, new Point(hot.X + 12, hot.Y + 10));
            }
        }
        group.Freeze();
        var image = new DrawingImage(group); image.Freeze();
        DrawingPreviewImage.Source = image;
    }

    private string? BuildGripReadout(CadEntity? edited)
    {
        if (!_gripMoved) return null;
        switch (edited)
        {
            case LineEntity line when _grip.Kind is GripKind.LineStart or GripKind.LineEnd:
            {
                var len = Point2.Distance(line.Start, line.End);
                var ang = NormalizeDegrees(Math.Atan2(line.End.Y - line.Start.Y, line.End.X - line.Start.X) * 180 / Math.PI);
                return $"{len:0.###} < {ang:0.##}°";
            }
            case CircleEntity circle when _grip.Kind == GripKind.Radius: return $"R {circle.Radius:0.###}";
            case ArcEntity arc when _grip.Kind == GripKind.ArcMid: return $"R {arc.Radius:0.###}";
            case ArcEntity arc when _grip.Kind is GripKind.ArcStart or GripKind.ArcEnd:
                return $"{NormalizeDegrees(arc.EndAngleDegrees - arc.StartAngleDegrees):0.##}°";
            case PolylineEntity polyline when _grip.Kind == GripKind.Vertex && _gripAnchor is Point2 a:
                return $"{Point2.Distance(a, _gripCurrent):0.###}";
            default:
            {
                var d = _gripCurrent - _grip.Point;
                return $"Δ {d.X:0.###}, {d.Y:0.###}";
            }
        }
    }
}
