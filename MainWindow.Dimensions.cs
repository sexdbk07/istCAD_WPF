﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using iStCAD_1.Rendering;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>
/// AutoCAD-style dimensioning commands (DIMLINEAR, DIMALIGNED, DIMRADIUS, DIMDIAMETER, DIMANGULAR). Dimensions are created
/// from the current Dim Style (text height, arrow size, extension offset/extend, gap, scale, decimals) as ordinary
/// LINE / POLYLINE / ARC / MTEXT objects on the current layer, so they render, snap and save to DWG without special support.
/// </summary>
public partial class MainWindow
{
    private readonly List<Point2> _dimPoints = new();
    private CadEntity? _dimEntityA;
    private CadEntity? _dimEntityB;

    private CadDimensionStyleDefinition? _dimStyleOverride;

    // ---- DIMCONTINUE / DIMBASELINE chain state --------------------------------------
    private Guid? _lastLinearDimId;
    private string? _dimChainKind;      // "LINEAR" or "ALIGNED" - same as the base dimension being continued
    private Point2 _dimChainOrigin;     // first extension-line origin for the NEXT dimension in the chain
    private Point2 _dimChainDir;        // measuring direction (unit vector) shared by the whole chain
    private Point2 _dimChainN;          // perpendicular to _dimChainDir (unit vector)
    private double _dimChainOffset;     // Dot(dimension-line point, _dimChainN) - constant for DIMCONTINUE, stepped out for DIMBASELINE
    private bool _dimChainIsBaseline;
    private bool _dimChainActive => _dimChainKind is not null;

    /// <summary>Point lying on the chain's dimension line (same perpendicular offset), directly above/beside <paramref name="near"/> - so the
    /// new dimension's line stays on that line regardless of where the user actually clicked along the measuring direction.</summary>
    private Point2 ChainLocationNear(Point2 near)
    {
        double Dot(Point2 u, Point2 v) => u.X * v.X + u.Y * v.Y;
        var d = _dimChainOffset - Dot(near, _dimChainN);
        return new Point2(near.X + _dimChainN.X * d, near.Y + _dimChainN.Y * d);
    }

    private CadDimensionStyleDefinition CurrentDimStyle()
    {
        if (_dimStyleOverride is not null) return _dimStyleOverride;
        var d = _editor.Drawing;
        return d.DimensionStyles.TryGetValue(d.CurrentDimensionStyle, out var style) ? style : new CadDimensionStyleDefinition("ISO-25", 2.5, 2.5, 1);
    }

    private void BeginDimensionTool(string tool)
    {
        _dimPoints.Clear(); _dimEntityA = _dimEntityB = null;
        var prompt = tool switch
        {
            "DIMLINEAR" => "DIMLINEAR: Specify first extension line origin:",
            "DIMALIGNED" => "DIMALIGNED: Specify first extension line origin:",
            "DIMRADIUS" => "DIMRADIUS: Select arc or circle:",
            "DIMDIAMETER" => "DIMDIAMETER: Select arc or circle:",
            _ => "DIMANGULAR: Select first line:"
        };
        BeginPickTool(tool, prompt);
    }

    private Point2 PickWorldPoint(Point screen)
    {
        var world = ConstrainPointer(ScreenToWorld(screen));
        return _activeOsnap is { } snap ? snap.Point : world;
    }

    /// <summary>DIMCONTINUE/DIMBASELINE: use the last dimension created in this session if it is still a LINEAR/ALIGNED
    /// dimension, otherwise ask the user to click one (AutoCAD's own default behaviour, simplified: no separate "Select" keyword).</summary>
    private void BeginContinueOrBaseline(string tool)
    {
        _dimChainKind = null;
        if (_lastLinearDimId is { } id && _spatialIndex.TryGetEntity(id, out var last) &&
            last is ProxyCadEntity { Dimension: { } lastData } && lastData.Kind is "LINEAR" or "ALIGNED")
        {
            StartDimensionChain(tool, lastData);
            return;
        }
        BeginPickTool(tool, $"{tool}: Select a linear or aligned dimension to continue from:");
    }

    private void StartDimensionChain(string tool, CadDimensionData baseData)
    {
        var p = baseData.Points;
        Point2 a = p[0], b = p[1], loc = p[2];
        Point2 dir;
        if (baseData.Kind == "ALIGNED")
        {
            var len = Point2.Distance(a, b);
            dir = len < 1e-9 ? new Point2(1, 0) : new Point2((b.X - a.X) / len, (b.Y - a.Y) / len);
        }
        else
        {
            var mid = new Point2((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            dir = Math.Abs(loc.Y - mid.Y) >= Math.Abs(loc.X - mid.X) ? new Point2(1, 0) : new Point2(0, 1);
        }
        double Dot(Point2 u, Point2 v) => u.X * v.X + u.Y * v.Y;
        _dimChainDir = dir; _dimChainN = new Point2(-dir.Y, dir.X);
        _dimChainOffset = Dot(loc, _dimChainN);
        _dimChainIsBaseline = tool == "DIMBASELINE";
        _dimChainKind = baseData.Kind;
        _dimChainOrigin = _dimChainIsBaseline ? a : b;
        if (_dimChainIsBaseline)
        {
            // Step the dimension line further out, on the same side it was already on, so dimensions stack instead of overlapping.
            var style = baseData.Style; var k = style.Scale <= 0 ? 1 : style.Scale;
            var spacing = (style.TextHeight * 2.5 + style.ArrowSize) * k;
            var side = _dimChainOffset - Dot(_dimChainOrigin, _dimChainN) >= 0 ? 1 : -1;
            _dimChainOffset += side * spacing;
        }
        BeginPickTool(tool, $"{tool}: Specify next extension line origin (Esc to end):");
    }

    /// <summary>Creates the next dimension in an active DIMCONTINUE/DIMBASELINE chain ending at <paramref name="next"/>, then
    /// advances the chain (DIMCONTINUE moves its origin on; DIMBASELINE steps the dimension line further out).</summary>
    private void CommitChainDimension(Point2 next)
    {
        var loc = ChainLocationNear(next);
        CommitDimensionData(_dimChainKind!, new[] { _dimChainOrigin, next, loc }, 0);
        if (_dimChainIsBaseline)
        {
            var style = CurrentDimStyle(); var k = style.Scale <= 0 ? 1 : style.Scale;
            var spacing = (style.TextHeight * 2.5 + style.ArrowSize) * k;
            double Dot(Point2 u, Point2 v) => u.X * v.X + u.Y * v.Y;
            var side = _dimChainOffset - Dot(_dimChainOrigin, _dimChainN) >= 0 ? 1 : -1;
            _dimChainOffset += side * spacing;
        }
        else _dimChainOrigin = next;
        AppendCommandHistory($"{_pickTool}: Specify next extension line origin (Esc to end):");
    }

    private void HandleDimensionClick(Point screen)
    {
        var tool = _pickTool!;
        switch (tool)
        {
            case "DIMLINEAR":
            case "DIMALIGNED":
                var p = PickWorldPoint(screen);
                _dimPoints.Add(p);
                if (_dimPoints.Count == 1) AppendCommandHistory($"{tool}: Specify second extension line origin:");
                else if (_dimPoints.Count == 2) AppendCommandHistory($"{tool}: Specify dimension line location:");
                else
                {
                    var (a, b, loc) = (_dimPoints[0], _dimPoints[1], _dimPoints[2]);
                    Point2 dir;
                    if (tool == "DIMALIGNED")
                    {
                        var len = Point2.Distance(a, b);
                        if (len < 1e-9) { AppendCommandHistory("DIMALIGNED: Extension origins coincide."); _dimPoints.Clear(); return; }
                        dir = new Point2((b.X - a.X) / len, (b.Y - a.Y) / len);
                    }
                    else
                    {
                        var mid = new Point2((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                        dir = Math.Abs(loc.Y - mid.Y) >= Math.Abs(loc.X - mid.X) ? new Point2(1, 0) : new Point2(0, 1);
                    }
                    CommitDimensionData(tool == "DIMALIGNED" ? "ALIGNED" : "LINEAR", new[] { a, b, loc }, 0);
                    _dimPoints.Clear();
                    AppendCommandHistory($"{tool}: Specify first extension line origin (Esc to end):");
                }
                break;
            case "DIMRADIUS":
            case "DIMDIAMETER":
                if (_dimEntityA is null)
                {
                    var hit = FindEntityAt(screen, recordSample: false);
                    if (hit is not (CircleEntity or ArcEntity)) { AppendCommandHistory($"{tool}: Select an arc or circle."); return; }
                    _dimEntityA = hit;
                    AppendCommandHistory($"{tool}: Specify dimension line location:");
                }
                else
                {
                    var (c, r) = _dimEntityA switch { CircleEntity ci => (ci.Center, Math.Abs(ci.Radius)), ArcEntity ar => (ar.Center, Math.Abs(ar.Radius)), _ => (default(Point2), 0d) };
                    CommitDimensionData(tool == "DIMDIAMETER" ? "DIAMETER" : "RADIUS", new[] { c, PickWorldPoint(screen) }, r);
                    _dimEntityA = null;
                    AppendCommandHistory($"{tool}: Select arc or circle (Esc to end):");
                }
                break;
            case "DIMCONTINUE":
            case "DIMBASELINE":
                if (!_dimChainActive)
                {
                    var hit = FindEntityAt(screen, recordSample: false);
                    if (hit is not ProxyCadEntity { Dimension: { } picked } || picked.Kind is not ("LINEAR" or "ALIGNED"))
                    { AppendCommandHistory($"{tool}: select a linear or aligned dimension."); return; }
                    StartDimensionChain(tool, picked);
                }
                else CommitChainDimension(PickWorldPoint(screen));
                break;
            case "DIMANGULAR":
                if (_dimEntityA is null || _dimEntityB is null)
                {
                    var hit = FindEntityAt(screen, recordSample: false);
                    if (hit is not LineEntity) { AppendCommandHistory("DIMANGULAR: Select a LINE."); return; }
                    if (_dimEntityA is null) { _dimEntityA = hit; AppendCommandHistory("DIMANGULAR: Select second line:"); }
                    else if (hit.Id == _dimEntityA.Id) AppendCommandHistory("DIMANGULAR: Select a different line.");
                    else { _dimEntityB = hit; AppendCommandHistory("DIMANGULAR: Specify dimension arc line location:"); }
                }
                else
                {
                    var (la, lb) = ((LineEntity)_dimEntityA, (LineEntity)_dimEntityB);
                    var arcPoint = PickWorldPoint(screen);
                    if (BuildAngularDimension(la, lb, arcPoint) is null) AppendCommandHistory("DIMANGULAR: Lines are parallel.");
                    else CommitDimensionData("ANGULAR", new[] { la.Start, la.End, lb.Start, lb.End, arcPoint }, 0);
                    _dimEntityA = _dimEntityB = null;
                    AppendCommandHistory("DIMANGULAR: Select first line (Esc to end):");
                }
                break;
        }
    }

    /// <summary>Provisional dimension for the cursor position (same construction as the final click), or null while nothing can be built yet.</summary>
    private List<CadEntity>? BuildDimensionPreview(Point2 cursor)
    {
        try
        {
            switch (_pickTool)
            {
                case "WIZ":
                    return WizardPreview(cursor);
                case "DIMLINEAR" or "DIMALIGNED" when _dimPoints.Count == 2:
                {
                    var (a, b) = (_dimPoints[0], _dimPoints[1]);
                    Point2 dir;
                    if (_pickTool == "DIMALIGNED")
                    {
                        var len = Point2.Distance(a, b); if (len < 1e-9) return null;
                        dir = new Point2((b.X - a.X) / len, (b.Y - a.Y) / len);
                    }
                    else
                    {
                        var mid = new Point2((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                        dir = Math.Abs(cursor.Y - mid.Y) >= Math.Abs(cursor.X - mid.X) ? new Point2(1, 0) : new Point2(0, 1);
                    }
                    return BuildLinearDimension(a, b, dir, cursor);
                }
                case "DIMCONTINUE" or "DIMBASELINE" when _dimChainActive:
                    return BuildLinearDimension(_dimChainOrigin, cursor, _dimChainDir, ChainLocationNear(cursor));
                case "DIMRADIUS" or "DIMDIAMETER" when _dimEntityA is not null:
                {
                    var (c, r) = _dimEntityA switch { CircleEntity ci => (ci.Center, Math.Abs(ci.Radius)), ArcEntity ar => (ar.Center, Math.Abs(ar.Radius)), _ => (default(Point2), 0d) };
                    return r > 0 ? BuildRadialDimension(c, r, cursor, _pickTool == "DIMDIAMETER") : null;
                }
                case "DIMANGULAR" when _dimEntityA is LineEntity la && _dimEntityB is LineEntity lb:
                    return BuildAngularDimension(la, lb, cursor);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Rubber band and provisional result of the active pick tool, drawn at the snapped cursor.</summary>
    private void DrawPickToolPreview(DrawingContext context, Point2 pointer, Brush stroke)
    {
        if (_pickTool == "WIZ" && _wiz is { } wiz && wiz.Index < wiz.Steps.Length && wiz.Steps[wiz.Index].Ref >= 0 && wiz.Has(wiz.Steps[wiz.Index].Ref))
            DrawLine(context, wiz.P(wiz.Steps[wiz.Index].Ref), pointer, stroke, 1.1, dashed: true);
        if (_pickTool is "TRIM" or "EXTEND" && _fenceStart is { } fenceStart) DrawLine(context, fenceStart, pointer, stroke, 1.1, dashed: true);
        if (_pickTool is "DIMLINEAR" or "DIMALIGNED" && _dimPoints.Count == 1)
        {
            DrawLine(context, _dimPoints[0], pointer, stroke, 1.1, dashed: true);
            var mid = WorldToScreen(new Point2((_dimPoints[0].X + pointer.X) / 2, (_dimPoints[0].Y + pointer.Y) / 2));
            context.DrawText(new FormattedText(Point2.Distance(_dimPoints[0], pointer).ToString("0.###", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, stroke, 1), new Point(mid.X + 6, mid.Y - 18));
            return;
        }
        if (_pickTool is "QLEADER" or "MLEADER" && _leaderArrowPoint is { } leaderStart)
        {
            DrawLine(context, leaderStart, pointer, stroke, 1.1, dashed: true);
            return;
        }
        var parts = BuildDimensionPreview(pointer);
        if (parts is null || parts.Count == 0) return;
        var scale = _viewScale;
        var matrix = new Matrix(scale, 0, 0, -scale, DrawingCanvas.ActualWidth / 2 - _viewCenter.X * scale, DrawingCanvas.ActualHeight / 2 + _viewCenter.Y * scale);
        var pen = CreatePen(stroke, 1.15 / Math.Max(scale, 1e-9), dashed: false);
        context.PushTransform(new MatrixTransform(matrix));
        foreach (var part in parts)
        {
            if (part is CadMTextEntity) continue;
            var geometry = CadGeometryBuilder.BuildEntityGeometry(part, 0, System.Threading.CancellationToken.None);
            if (geometry is not null && !geometry.Bounds.IsEmpty) context.DrawGeometry(part is PolylineEntity { Closed: true } ? stroke : null, pen, geometry);
        }
        context.Pop();
        foreach (var text in parts.OfType<CadMTextEntity>())
        {
            var at = WorldToScreen(text.Position);
            var formatted = new FormattedText(text.Value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), Math.Clamp(text.Height * scale, 9, 60), stroke, 1);
            context.PushTransform(new RotateTransform(-text.RotationRadians * 180 / Math.PI, at.X, at.Y));
            context.DrawText(formatted, new Point(at.X - formatted.Width / 2, at.Y - formatted.Height));
            context.Pop();
        }
    }

    /// <summary>Builds the drawing parts of a dimension from its parametric data (uses the data's own style, not the current one).</summary>
    private List<CadEntity>? BuildDimensionParts(CadDimensionData d)
    {
        _dimStyleOverride = d.Style; _dimTextOverride = d.TextOverride;
        try
        {
            var p = d.Points;
            switch (d.Kind)
            {
                case "LINEAR" or "ALIGNED":
                {
                    Point2 a = p[0], b = p[1], loc = p[2], dir;
                    if (d.Kind == "ALIGNED")
                    {
                        var len = Point2.Distance(a, b); if (len < 1e-9) return null;
                        dir = new Point2((b.X - a.X) / len, (b.Y - a.Y) / len);
                    }
                    else
                    {
                        var mid = new Point2((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                        dir = Math.Abs(loc.Y - mid.Y) >= Math.Abs(loc.X - mid.X) ? new Point2(1, 0) : new Point2(0, 1);
                    }
                    return BuildLinearDimension(a, b, dir, loc);
                }
                case "RADIUS" or "DIAMETER":
                    return d.Radius > 0 ? BuildRadialDimension(p[0], d.Radius, p[1], d.Kind == "DIAMETER") : null;
                case "ANGULAR":
                    return BuildAngularDimension(new LineEntity(p[0], p[1], Guid.NewGuid()), new LineEntity(p[2], p[3], Guid.NewGuid()), p[4]);
            }
        }
        catch { }
        finally { _dimStyleOverride = null; _dimTextOverride = null; }
        return null;
    }

    /// <summary>Composes ONE selectable object (a proxy carrying its parametric data) out of the dimension's drawing parts.</summary>
    private ProxyCadEntity? ComposeDimension(CadDimensionData data, Guid id, string layer)
    {
        var parts = BuildDimensionParts(data);
        if (parts is null || parts.Count == 0) return null;
        var paths = new List<ProxyPreviewPath>();
        var texts = new List<ProxyPreviewText>();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Grow(Point2 pt) { minX = Math.Min(minX, pt.X); minY = Math.Min(minY, pt.Y); maxX = Math.Max(maxX, pt.X); maxY = Math.Max(maxY, pt.Y); }
        foreach (var part in parts)
        {
            switch (part)
            {
                case LineEntity l: paths.Add(new ProxyPreviewPath(new[] { l.Start, l.End })); Grow(l.Start); Grow(l.End); break;
                case PolylineEntity pl:
                {
                    var pts = pl.Vertices.Select(v => v.Point).ToArray();
                    paths.Add(new ProxyPreviewPath(pts, pl.Closed)); foreach (var pt in pts) Grow(pt); break;
                }
                case ArcEntity arc:
                {
                    var a0 = arc.StartAngleDegrees * Math.PI / 180; var a1 = arc.EndAngleDegrees * Math.PI / 180;
                    while (a1 <= a0) a1 += Math.Tau;
                    var steps = Math.Clamp((int)((a1 - a0) / Math.Tau * 96) + 2, 4, 96);
                    var pts = new Point2[steps + 1];
                    for (var i = 0; i <= steps; i++) { var t = a0 + (a1 - a0) * i / steps; pts[i] = new Point2(arc.Center.X + Math.Cos(t) * arc.Radius, arc.Center.Y + Math.Sin(t) * arc.Radius); Grow(pts[i]); }
                    paths.Add(new ProxyPreviewPath(pts)); break;
                }
                case CadMTextEntity t:
                {
                    texts.Add(new ProxyPreviewText(t.Value, t.Position, t.Height, t.RotationRadians, t.Width, t.LineSpacing, t.AttachmentPoint));
                    var half = Math.Max(t.Value.Length, 1) * t.Height * 0.35;
                    Grow(new Point2(t.Position.X - half, t.Position.Y - t.Height)); Grow(new Point2(t.Position.X + half, t.Position.Y + t.Height)); break;
                }
            }
        }
        if (minX > maxX) return null;
        return new ProxyCadEntity("DIMENSION", new Point2(minX, minY), new Point2(maxX, maxY), true, id, layer,
            ProxyGraphicsPaths: paths, ProxyGraphicsTexts: texts, Dimension: data);
    }

    /// <summary>Same dimension object (same id, layer, color, ...) with new parametric data and regenerated graphics.</summary>
    private CadEntity RecomposeDimension(ProxyCadEntity proxy, CadDimensionData data)
    {
        var c = ComposeDimension(data, proxy.Id, proxy.Layer);
        return c is null ? proxy : proxy with { Dimension = data, BoundsMin = c.BoundsMin, BoundsMax = c.BoundsMax, ProxyGraphicsPaths = c.ProxyGraphicsPaths, ProxyGraphicsTexts = c.ProxyGraphicsTexts };
    }

    /// <summary>Measured value text of a dimension (ignores any text override).</summary>
    private string DimensionMeasureText(CadDimensionData data) =>
        BuildDimensionParts(data with { TextOverride = null })?.OfType<CadMTextEntity>().FirstOrDefault()?.Value ?? "";

    private void CommitDimensionData(string kind, IReadOnlyList<Point2> points, double radius)
    {
        var data = new CadDimensionData(kind, points.ToArray(), radius, CurrentDimStyle());
        var composed = ComposeDimension(data, Guid.NewGuid(), _editor.Drawing.CurrentLayer);
        if (composed is null) { AppendCommandHistory("Dimension: cannot build."); return; }
        var dim = (ProxyCadEntity)_editor.StyleNew(composed);
        _editor.ApplyEntityChanges(Array.Empty<CadEntity>(), new CadEntity[] { dim });
        _selectedEntityIds.Clear();
        _selectedEntityIds.Add(dim.Id);
        if (kind is "LINEAR" or "ALIGNED") _lastLinearDimId = dim.Id;
        RefreshPropertiesPalette();
    }

    private void CommitDimension(List<CadEntity> parts, string label)
    {
        if (parts.Count == 0) return;
        var layer = _editor.Drawing.CurrentLayer;
        var styled = parts.Select(e => (CadEntity)(e with { Layer = layer })).ToList();
        _editor.ApplyEntityChanges(Array.Empty<CadEntity>(), styled);
        _selectedEntityIds.Clear();
        foreach (var e in styled) _selectedEntityIds.Add(e.Id);
        RefreshPropertiesPalette();
    }

    // ---- geometry -----------------------------------------------------------------

    private string? _dimTextOverride;

    private string FormatMeasure(double value, CadDimensionStyleDefinition style)
    {
        var text = value.ToString("0." + new string('#', Math.Clamp(style.Decimals, 0, 8)), CultureInfo.InvariantCulture);
        if (text == "-0") text = "0";
        if (_dimTextOverride is { Length: > 0 } custom) return custom.Contains("<>") ? custom.Replace("<>", text) : custom;
        return text;
    }

    private static LineEntity DimLine(Point2 a, Point2 b) => new(a, b, Guid.NewGuid());

    private static PolylineEntity DimPoly(IEnumerable<Point2> points, bool closed) =>
        new(points.Select(pt => new PolylineVertex(pt, 0)).ToArray(), closed, Guid.NewGuid());

    private static CadMTextEntity DimText(string value, Point2 position, double height, double rotation, int attachment) =>
        new(value, value, position, height, rotation, 0, 1, attachment, Guid.NewGuid());

    /// <summary>Closed arrowhead (width = size/3) plus its axis, so it reads as a filled arrow at drawing scale.</summary>
    private static void AddArrow(List<CadEntity> parts, Point2 tip, Point2 inward, double size)
    {
        var half = size / 6;
        var nx = -inward.Y; var ny = inward.X;
        var b = new Point2(tip.X + inward.X * size, tip.Y + inward.Y * size);
        parts.Add(DimPoly(new[] { tip, new Point2(b.X + nx * half, b.Y + ny * half), new Point2(b.X - nx * half, b.Y - ny * half) }, true));
        parts.Add(DimLine(tip, b));
    }

    private static double UprightAngle(double angle)
    {
        var a = angle;
        while (a > Math.PI / 2 + 1e-9) a -= Math.PI;
        while (a <= -Math.PI / 2 + 1e-9) a += Math.PI;
        return a;
    }

    private List<CadEntity> BuildLinearDimension(Point2 p1, Point2 p2, Point2 dir, Point2 loc)
    {
        var style = CurrentDimStyle();
        var k = style.Scale <= 0 ? 1 : style.Scale;
        double ts = style.TextHeight * k, arrow = style.ArrowSize * k, off = style.ExtensionOffset * k, ext = style.ExtensionExtend * k, gap = style.TextGap * k;
        var n = new Point2(-dir.Y, dir.X);
        double Dot(Point2 u, Point2 v) => u.X * v.X + u.Y * v.Y;
        var parts = new List<CadEntity>();
        Point2 Foot(Point2 pt, out double sign)
        {
            var h = Dot(new Point2(loc.X - pt.X, loc.Y - pt.Y), n);
            sign = h >= 0 ? 1 : -1;
            parts.Add(DimLine(new Point2(pt.X + n.X * sign * off, pt.Y + n.Y * sign * off),
                new Point2(pt.X + n.X * (h + sign * ext), pt.Y + n.Y * (h + sign * ext))));
            return new Point2(pt.X + n.X * h, pt.Y + n.Y * h);
        }
        var d1 = Foot(p1, out _); var d2 = Foot(p2, out _);
        var length = Point2.Distance(d1, d2);
        var measure = Math.Abs(Dot(new Point2(p2.X - p1.X, p2.Y - p1.Y), dir));
        var along = length < 1e-12 ? dir : new Point2((d2.X - d1.X) / length, (d2.Y - d1.Y) / length);
        var inside = length >= arrow * 2.2;
        if (inside)
        {
            parts.Add(DimLine(d1, d2));
            AddArrow(parts, d1, along, arrow);
            AddArrow(parts, d2, new Point2(-along.X, -along.Y), arrow);
        }
        else
        {
            // Arrows outside the extension lines, short dimension line between them.
            parts.Add(DimLine(new Point2(d1.X - along.X * arrow * 1.5, d1.Y - along.Y * arrow * 1.5), new Point2(d2.X + along.X * arrow * 1.5, d2.Y + along.Y * arrow * 1.5)));
            AddArrow(parts, d1, new Point2(-along.X, -along.Y), arrow);
            AddArrow(parts, d2, along, arrow);
        }
        var angle = UprightAngle(Math.Atan2(along.Y, along.X));
        var up = new Point2(-Math.Sin(angle), Math.Cos(angle));
        var mid = new Point2((d1.X + d2.X) / 2 + up.X * gap, (d1.Y + d2.Y) / 2 + up.Y * gap);
        parts.Add(DimText(FormatMeasure(measure, style), mid, ts, angle, 8));
        return parts;
    }

    private List<CadEntity> BuildRadialDimension(Point2 c, double r, Point2 p, bool diameter)
    {
        var style = CurrentDimStyle();
        var k = style.Scale <= 0 ? 1 : style.Scale;
        double ts = style.TextHeight * k, arrow = style.ArrowSize * k, gap = style.TextGap * k;
        var parts = new List<CadEntity>();
        var dx = p.X - c.X; var dy = p.Y - c.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        var u = dist < 1e-12 ? new Point2(1, 0) : new Point2(dx / dist, dy / dist);
        // Center mark.
        var mark = Math.Max(arrow / 2, 1e-9);
        parts.Add(DimLine(new Point2(c.X - mark, c.Y), new Point2(c.X + mark, c.Y)));
        parts.Add(DimLine(new Point2(c.X, c.Y - mark), new Point2(c.X, c.Y + mark)));
        var tip = new Point2(c.X + u.X * r, c.Y + u.Y * r);
        var outside = dist > r;
        var label = (diameter ? "Ø" : "R") + FormatMeasure(diameter ? r * 2 : r, style);
        Point2 lineEnd;
        if (diameter)
        {
            var opposite = new Point2(c.X - u.X * r, c.Y - u.Y * r);
            lineEnd = outside ? p : tip;
            parts.Add(DimLine(opposite, lineEnd));
            AddArrow(parts, opposite, u, arrow);
            AddArrow(parts, tip, new Point2(-u.X, -u.Y), arrow);
        }
        else
        {
            lineEnd = outside ? p : tip;
            parts.Add(DimLine(c, lineEnd));
            AddArrow(parts, tip, new Point2(-u.X, -u.Y), arrow);
        }
        // Text sits at the free end, horizontal, on the outer side (AutoCAD default).
        var right = u.X >= 0;
        var textPos = new Point2(lineEnd.X + (right ? gap : -gap), lineEnd.Y);
        if (!outside) textPos = new Point2((tip.X + c.X) / 2, (tip.Y + c.Y) / 2 + gap);
        parts.Add(DimText(label, textPos, ts, 0, outside ? (right ? 4 : 6) : 8));
        return parts;
    }

    private List<CadEntity>? BuildAngularDimension(LineEntity l1, LineEntity l2, Point2 p)
    {
        var style = CurrentDimStyle();
        var k = style.Scale <= 0 ? 1 : style.Scale;
        double ts = style.TextHeight * k, arrow = style.ArrowSize * k, off = style.ExtensionOffset * k, ext = style.ExtensionExtend * k, gap = style.TextGap * k;
        double x1 = l1.End.X - l1.Start.X, y1 = l1.End.Y - l1.Start.Y, x2 = l2.End.X - l2.Start.X, y2 = l2.End.Y - l2.Start.Y;
        var den = x1 * y2 - y1 * x2;
        if (Math.Abs(den) < 1e-12) return null;
        var t = ((l2.Start.X - l1.Start.X) * y2 - (l2.Start.Y - l1.Start.Y) * x2) / den;
        var vertex = new Point2(l1.Start.X + x1 * t, l1.Start.Y + y1 * t);
        var toP = new Point2(p.X - vertex.X, p.Y - vertex.Y);
        var radius = Math.Sqrt(toP.X * toP.X + toP.Y * toP.Y);
        if (radius < 1e-9) return null;
        Point2 Direction(double dx, double dy)
        {
            var len = Math.Sqrt(dx * dx + dy * dy);
            var d = new Point2(dx / len, dy / len);
            return d.X * toP.X + d.Y * toP.Y >= 0 ? d : new Point2(-d.X, -d.Y);
        }
        var d1 = Direction(x1, y1); var d2 = Direction(x2, y2);
        double a1 = Math.Atan2(d1.Y, d1.X), a2 = Math.Atan2(d2.Y, d2.X), ap = Math.Atan2(toP.Y, toP.X);
        static double Norm(double a) { a %= Math.Tau; return a < 0 ? a + Math.Tau : a; }
        double start, end;
        if (Norm(ap - a1) <= Norm(a2 - a1) + 1e-9) { start = a1; end = a2; } else { start = a2; end = a1; }
        var sweep = Norm(end - start);
        var parts = new List<CadEntity>
        {
            new ArcEntity(vertex, radius, start * 180 / Math.PI, end * 180 / Math.PI, Guid.NewGuid())
        };
        foreach (var (line, dir) in new[] { (l1, d1), (l2, d2) })
        {
            var far = Math.Max(Point2.Distance(vertex, line.Start) * Math.Sign(((line.Start.X - vertex.X) * dir.X + (line.Start.Y - vertex.Y) * dir.Y)),
                               Point2.Distance(vertex, line.End) * Math.Sign(((line.End.X - vertex.X) * dir.X + (line.End.Y - vertex.Y) * dir.Y)));
            var from = Math.Max(far, 0) + off;
            if (from < radius + ext)
                parts.Add(DimLine(new Point2(vertex.X + dir.X * from, vertex.Y + dir.Y * from), new Point2(vertex.X + dir.X * (radius + ext), vertex.Y + dir.Y * (radius + ext))));
        }
        Point2 OnArc(double a) => new(vertex.X + Math.Cos(a) * radius, vertex.Y + Math.Sin(a) * radius);
        AddArrow(parts, OnArc(start), new Point2(-Math.Sin(start), Math.Cos(start)), arrow);
        AddArrow(parts, OnArc(end), new Point2(Math.Sin(end), -Math.Cos(end)), arrow);
        var midAngle = start + sweep / 2;
        var textPos = new Point2(vertex.X + Math.Cos(midAngle) * (radius + gap), vertex.Y + Math.Sin(midAngle) * (radius + gap));
        parts.Add(DimText(FormatMeasure(sweep * 180 / Math.PI, style) + "°", textPos, ts, UprightAngle(midAngle - Math.PI / 2), 8));
        return parts;
    }
}
