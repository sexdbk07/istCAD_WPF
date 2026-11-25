using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>Interactive pick tools (TRIM, EXTEND, ID) in the AutoCAD style: click objects repeatedly until Esc.</summary>
public partial class MainWindow
{
    private string? _pickTool;

    private void BeginPickTool(string tool, string prompt)
    {
        CancelPickTool(silent: true);
        _pickTool = tool;
        if (DynamicPromptText is not null) DynamicPromptText.Text = prompt;
        AppendCommandHistory(prompt);
        UpdateDynamicInputMode();
    }

    private void CancelPickTool(bool silent = false)
    {
        if (_pickTool is null) return;
        if (_pickTool == "ZOOMW") CancelZoomWindowVisuals();
        _pickTool = null;
        _wiz = null;
        _dimPoints.Clear(); _dimEntityA = _dimEntityB = null;
        _dimChainKind = null;
        _leaderArrowPoint = null;
        _fenceStart = null;
        _pendingAttDef = null;
        _pendingBlockName = null;
        UpdateOsnapMarker(null);
        if (DynamicPromptText is not null) DynamicPromptText.Text = string.Empty;
        UpdateDynamicInputMode(); RenderPreview();
        if (!silent) AppendCommandHistory("Command ended.");
    }

    /// <summary>Returns true when the click was consumed by a pick tool.</summary>
    private bool HandlePickToolClick(Point screenPoint)
    {
        if (_pickTool is null) return false;
        var world = ScreenToWorld(screenPoint);
        switch (_pickTool)
        {
            case "ZOOMW":
                HandleZoomWindowClick(screenPoint);
                break;
            case "ID":
                var idPoint = PickWorldPoint(screenPoint);
                AppendCommandHistory($"X = {idPoint.X.ToString("0.####", CultureInfo.CurrentCulture)}  Y = {idPoint.Y.ToString("0.####", CultureInfo.CurrentCulture)}  Z = 0");
                break;
            case "WIZ":
                WizardClick(screenPoint);
                break;
            case "DIMLINEAR": case "DIMALIGNED": case "DIMRADIUS": case "DIMDIAMETER": case "DIMANGULAR":
            case "DIMCONTINUE": case "DIMBASELINE":
                HandleDimensionClick(screenPoint);
                break;
            case "TEXTEDIT":
                var hitText = FindEntityAt(screenPoint, recordSample: false);
                if (hitText is CadTextEntity or CadMTextEntity) { EditTextEntity(hitText); AppendCommandHistory("TEXTEDIT: Select an annotation object (Esc to end):"); }
                else AppendCommandHistory("TEXTEDIT: select a TEXT or MTEXT object.");
                break;
            case "QLEADER": case "MLEADER":
                HandleLeaderClick(screenPoint);
                break;
            case "ATTDEF":
                PlaceAttributeDefinition(world);
                break;
            case "BLOCKBASE":
                BlockBasePointPicked(world);
                break;
            case "TRIM":
            case "EXTEND":
                var hit = FindEntityAt(screenPoint, recordSample: false);
                if (hit is null) { HandleTrimFenceClick(world, (_pickTool == "TRIM") != ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)); break; } // empty space: fence
                _fenceStart = null;
                if (hit is not (LineEntity or ArcEntity or CircleEntity or PolylineEntity)) { AppendCommandHistory($"{_pickTool}: only LINE, ARC, CIRCLE and POLYLINE objects can be trimmed or extended."); break; }
                // As in AutoCAD, holding Shift while picking switches between trimming and extending.
                var shiftHeld = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
                if ((_pickTool == "TRIM") != shiftHeld) TrimEntity(hit, world); else ExtendEntity(hit, world);
                break;
        }
        return true;
    }

    // ---- intersection helpers -------------------------------------------------

    private static IEnumerable<(Point2 A, Point2 B)> EdgeSegments(CadEntity e)
    {
        switch (e)
        {
            case LineEntity l: yield return (l.Start, l.End); break;
            case PolylineEntity p:
                var v = p.Vertices;
                for (var i = 0; i + 1 < v.Count; i++) yield return (v[i].Point, v[i + 1].Point);
                if (p.Closed && v.Count > 2) yield return (v[^1].Point, v[0].Point);
                break;
        }
    }

    /// <summary>Parameters t on the infinite line A + t(B-A) where it meets the edge entity (edge extents respected).</summary>
    private static List<double> LineParameters(Point2 a, Point2 b, CadEntity edge)
    {
        var result = new List<double>();
        var dx = b.X - a.X; var dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 < 1e-18) return result;
        foreach (var (c, d) in EdgeSegments(edge))
        {
            var ex = d.X - c.X; var ey = d.Y - c.Y;
            var den = dx * ey - dy * ex;
            if (Math.Abs(den) < 1e-12 * Math.Sqrt(len2) * Math.Sqrt(ex * ex + ey * ey + 1e-30)) continue;
            var t = ((c.X - a.X) * ey - (c.Y - a.Y) * ex) / den;
            var u = ((c.X - a.X) * dy - (c.Y - a.Y) * dx) / den;
            if (u >= -1e-9 && u <= 1 + 1e-9) result.Add(t);
        }
        Point2 center; double radius; double? a0 = null, a1 = null;
        if (edge is CircleEntity ce) { center = ce.Center; radius = Math.Abs(ce.Radius); }
        else if (edge is ArcEntity ar) { center = ar.Center; radius = Math.Abs(ar.Radius); a0 = ar.StartAngleDegrees; a1 = ar.EndAngleDegrees; }
        else return result;
        var fx = a.X - center.X; var fy = a.Y - center.Y;
        var qa = len2; var qb = 2 * (fx * dx + fy * dy); var qc = fx * fx + fy * fy - radius * radius;
        var disc = qb * qb - 4 * qa * qc;
        if (disc < 0) return result;
        var root = Math.Sqrt(disc);
        foreach (var t in disc < 1e-12 ? new[] { -qb / (2 * qa) } : new[] { (-qb - root) / (2 * qa), (-qb + root) / (2 * qa) })
        {
            if (a0 is { } s && a1 is { } en)
            {
                var ang = Math.Atan2(a.Y + t * dy - center.Y, a.X + t * dx - center.X) * 180 / Math.PI;
                var span = ((en - s) % 360 + 360) % 360; if (span < 1e-9) span = 360;
                var rel = ((ang - s) % 360 + 360) % 360;
                if (rel > span + 1e-7) continue;
            }
            result.Add(t);
        }
        return result;
    }

    private List<double> AllLineParameters(LineEntity target)
    {
        var all = new List<double>();
        foreach (var other in _editor.Drawing.Entities)
        {
            if (other.Id == target.Id || _editor.Drawing.HiddenEntityIds.Contains(other.Id)) continue;
            all.AddRange(LineParameters(target.Start, target.End, other));
        }
        return all;
    }

    private void TrimLine(LineEntity line, Point2 click)
    {
        var dx = line.End.X - line.Start.X; var dy = line.End.Y - line.Start.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 < 1e-18) return;
        var tc = ((click.X - line.Start.X) * dx + (click.Y - line.Start.Y) * dy) / len2;
        var cuts = AllLineParameters(line).Where(t => t > 1e-7 && t < 1 - 1e-7).OrderBy(t => t).ToList();
        if (cuts.Count == 0) { AppendCommandHistory("TRIM: No cutting edge crosses this object."); return; }
        double? lo = null, hi = null;
        foreach (var t in cuts) { if (t <= tc) lo = t; else { hi = t; break; } }
        Point2 At(double t) => new(line.Start.X + dx * t, line.Start.Y + dy * t);
        var pieces = new List<CadEntity>();
        if (lo is { } l) pieces.Add(line with { End = At(l), Id = Guid.NewGuid(), NativeHandle = null });
        if (hi is { } h) pieces.Add(line with { Start = At(h), Id = Guid.NewGuid(), NativeHandle = null });
        _editor.ApplyEntityChanges([line], pieces);
        _selectedEntityIds.Clear();
        AppendCommandHistory(pieces.Count == 0 ? "TRIM: Object removed." : "TRIM: Done. Click next object or Esc.");
    }

    private void ExtendLine(LineEntity line, Point2 click)
    {
        var dx = line.End.X - line.Start.X; var dy = line.End.Y - line.Start.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 < 1e-18) return;
        var tc = ((click.X - line.Start.X) * dx + (click.Y - line.Start.Y) * dy) / len2;
        var fromEnd = tc >= 0.5;
        var hits = AllLineParameters(line);
        double? best = fromEnd
            ? hits.Where(t => t > 1 + 1e-7).DefaultIfEmpty(double.NaN).Min()
            : hits.Where(t => t < -1e-7).DefaultIfEmpty(double.NaN).Max();
        if (best is null || double.IsNaN(best.Value)) { AppendCommandHistory("EXTEND: No boundary in that direction."); return; }
        var p = new Point2(line.Start.X + dx * best.Value, line.Start.Y + dy * best.Value);
        var changed = fromEnd ? line with { End = p, Id = Guid.NewGuid(), NativeHandle = null } : line with { Start = p, Id = Guid.NewGuid(), NativeHandle = null };
        _editor.ApplyEntityChanges([line], [changed]);
        _selectedEntityIds.Clear();
        AppendCommandHistory("EXTEND: Done. Click next object or Esc.");
    }

    // ---- LIST ----------------------------------------------------------------

    private void ListSelectedEntities()
    {
        var items = SelectedGeometry();
        if (items.Length == 0) { AppendCommandHistory("LIST: Select objects first."); return; }
        string N(double v) => v.ToString("0.####", CultureInfo.CurrentCulture);
        foreach (var e in items.Take(20))
        {
            var detail = e switch
            {
                LineEntity l => $"LINE  from ({N(l.Start.X)}, {N(l.Start.Y)}) to ({N(l.End.X)}, {N(l.End.Y)})  length {N(Point2.Distance(l.Start, l.End))}",
                CircleEntity c => $"CIRCLE  center ({N(c.Center.X)}, {N(c.Center.Y)})  radius {N(c.Radius)}  circumference {N(2 * Math.PI * c.Radius)}  area {N(Math.PI * c.Radius * c.Radius)}",
                ArcEntity a => $"ARC  center ({N(a.Center.X)}, {N(a.Center.Y)})  radius {N(a.Radius)}  angles {N(a.StartAngleDegrees)}..{N(a.EndAngleDegrees)}",
                ProxyCadEntity { Ellipse: { } el } => $"ELLIPSE  center ({N(el.Center.X)}, {N(el.Center.Y)})  major axis endpoint ({N(el.MajorAxisEndpoint.X)}, {N(el.MajorAxisEndpoint.Y)})  ratio {N(el.RadiusRatio)}",
                ProxyCadEntity { Spline: { } sp } => $"SPLINE  degree {sp.Degree}  {sp.FitPoints.Count} fit points  {sp.ControlPoints.Count} control points  {(sp.Closed ? "closed" : "open")}",
                ProxyCadEntity { Wipeout: { } wo } => $"WIPEOUT  {wo.Boundary.Count} boundary points",
                ProxyCadEntity { Table: { } td } => $"TABLE  {td.Columns}x{td.Rows}  at ({N(td.Position.X)}, {N(td.Position.Y)})",
                ProxyCadEntity { Viewport: { } vd } => $"VIEWPORT  center ({N(vd.Center.X)}, {N(vd.Center.Y)})  {N(vd.Width)} x {N(vd.Height)}  view height {N(vd.ViewHeight)}",
                ProxyCadEntity { Leader: { IsMultiLeader: true } mld } => $"MULTILEADER  arrow ({N(mld.Points[0].X)}, {N(mld.Points[0].Y)})  text \"{mld.Text}\"",
                ProxyCadEntity { Leader: { } ld } => $"LEADER  arrow ({N(ld.Points[0].X)}, {N(ld.Points[0].Y)})  text \"{ld.Text}\"",
                PolylineEntity p => $"POLYLINE  {p.Vertices.Count} vertices  {(p.Closed ? "closed" : "open")}",
                CadTextEntity t => $"TEXT  \"{t.Value}\"  at ({N(t.Position.X)}, {N(t.Position.Y)})  height {N(t.Height)}",
                CadMTextEntity t => $"MTEXT  at ({N(t.Position.X)}, {N(t.Position.Y)})  height {N(t.Height)}",
                _ => e.GetType().Name
            };
            AppendCommandHistory($"{detail}  layer {e.Layer}  handle {e.NativeHandle ?? "-"}");
        }
        if (items.Length > 20) AppendCommandHistory($"... and {items.Length - 20} more objects.");
    }
}
