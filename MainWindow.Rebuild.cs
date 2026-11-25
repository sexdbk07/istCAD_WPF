using System;
using System.Collections.Generic;
using System.Linq;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>Rebuilds objects imported from DWG that the app only displays (INSERT, DIMENSION, leaders...) into editable LINE/POLYLINE/MTEXT objects.</summary>
public partial class MainWindow
{
    private const int RebuildLimit = 20000;

    private static bool CanRebuildProxy(ProxyCadEntity p) =>
        p.Dimension is null && !p.PreviewFilled &&
        (p.ProxyGraphicsPaths is { Count: > 0 } || p.PreviewPaths is { Count: > 0 } || p.ProxyGraphicsTexts is { Count: > 0 } || !string.IsNullOrWhiteSpace(p.PreviewText));

    /// <summary>Plain, editable objects that draw the same picture as the proxy; null when it has nothing to rebuild from.</summary>
    private static List<CadEntity>? RebuildProxy(ProxyCadEntity p)
    {
        if (!CanRebuildProxy(p)) return null;
        var result = new List<CadEntity>();
        CadEntity Style(CadEntity e) => e with
        {
            Layer = p.Layer, ExplicitColor = p.ExplicitColor, DisplayColor = p.DisplayColor, ColorByBlock = p.ColorByBlock,
            LineTypeName = p.LineTypeName, LineTypePattern = p.LineTypePattern, LineWeightMm = p.LineWeightMm, LineTypeScale = p.LineTypeScale,
            TextFontFamily = p.TextFontFamily
        };
        void AddPath(IReadOnlyList<Point2> points, bool closed)
        {
            if (points.Count < 2) return;
            if (points.Count == 2 && !closed) result.Add(Style(new LineEntity(points[0], points[1], Guid.NewGuid())));
            else result.Add(Style(new PolylineEntity(points.Select(pt => new PolylineVertex(pt, 0)).ToArray(), closed, Guid.NewGuid())));
        }
        if (p.ProxyGraphicsPaths is { Count: > 0 })
            foreach (var path in p.ProxyGraphicsPaths) AddPath(path.Points, path.Closed);
        else if (p.PreviewPaths is { Count: > 0 })
            foreach (var path in p.PreviewPaths) AddPath(path, p.PreviewClosed);
        if (p.ProxyGraphicsTexts is { Count: > 0 })
            foreach (var t in p.ProxyGraphicsTexts)
                result.Add(Style(new CadMTextEntity(t.Text, t.Text, t.Position, t.Height, t.RotationRadians, t.Width, t.LineSpacing, t.AttachmentPoint, Guid.NewGuid())));
        else if (!string.IsNullOrWhiteSpace(p.PreviewText) && p.PreviewTextPosition is { } at)
            result.Add(Style(new CadMTextEntity(p.PreviewText, p.PreviewText, at, p.PreviewTextHeight, p.PreviewTextRotation, p.PreviewTextWidth, p.PreviewTextLineSpacing, p.PreviewTextAttachment, Guid.NewGuid())));
        return result.Count == 0 || result.Count > RebuildLimit ? null : result;
    }

    /// <summary>Moves / rotates / scales a block reference (INSERT) as a whole: its drawn picture and its block-reference data.</summary>
    private static ProxyCadEntity TransformProxy(ProxyCadEntity p, Func<Point2, Point2> map, double dRot, double scale)
    {
        IReadOnlyList<Point2> M(IReadOnlyList<Point2> pts) => pts.Select(map).ToArray();
        var corners = new[] { p.BoundsMin, new Point2(p.BoundsMax.X, p.BoundsMin.Y), p.BoundsMax, new Point2(p.BoundsMin.X, p.BoundsMax.Y) }.Select(map).ToArray();
        var data = p.InsertData;
        return p with
        {
            BoundsMin = new Point2(corners.Min(c => c.X), corners.Min(c => c.Y)),
            BoundsMax = new Point2(corners.Max(c => c.X), corners.Max(c => c.Y)),
            ProxyGraphicsPaths = p.ProxyGraphicsPaths?.Select(x => new ProxyPreviewPath(M(x.Points), x.Closed)).ToArray(),
            PreviewPaths = p.PreviewPaths?.Select(M).ToArray(),
            ProxyGraphicsTexts = p.ProxyGraphicsTexts?.Select(t => t with { Position = map(t.Position), RotationRadians = t.RotationRadians + dRot, Height = t.Height * scale, Width = t.Width * scale }).ToArray(),
            PreviewTextPosition = p.PreviewTextPosition is { } tp ? map(tp) : (Point2?)null,
            PreviewTextRotation = p.PreviewTextRotation + dRot, PreviewTextHeight = p.PreviewTextHeight * scale, PreviewTextWidth = p.PreviewTextWidth * scale,
            InsertData = data is null ? null : data with { Position = map(data.Position), Rotation = data.Rotation + dRot, ScaleX = data.ScaleX * scale, ScaleY = data.ScaleY * scale },
            Hatch = p.Hatch is null ? null : p.Hatch with
            {
                Boundaries = p.Hatch.Boundaries.Select(ring => (IReadOnlyList<Point2>)M(ring)).ToArray(),
                AngleDegrees = p.Hatch.AngleDegrees + dRot * 180 / Math.PI,
                Spacing = p.Hatch.Spacing * scale
            },
            Leader = p.Leader is null ? null : p.Leader with
            {
                Points = M(p.Leader.Points),
                TextHeight = p.Leader.TextHeight * scale,
                ArrowSize = p.Leader.ArrowSize * scale
            },
            AttributeDef = p.AttributeDef is null ? null : p.AttributeDef with
            {
                Height = p.AttributeDef.Height * scale,
                RotationDegrees = p.AttributeDef.RotationDegrees + dRot * 180 / Math.PI
            },
            // Center and MajorAxisEndpoint are both ordinary world points, so the same map() already used for every other
            // point gives the right result under MOVE/ROTATE/uniform SCALE/MIRROR - RadiusRatio needs no change since map()
            // scales both axes by the same factor (this app's SCALE/transform pipeline is uniform-scale only, like the rest
            // of RECTANG/HATCH/LEADER above).
            Ellipse = p.Ellipse is null ? null : p.Ellipse with { Center = map(p.Ellipse.Center), MajorAxisEndpoint = map(p.Ellipse.MajorAxisEndpoint) },
            // Knots/Weights/Degree/Closed are invariant under any affine map (NURBS basis functions depend only on the
            // parameter, not on where the control points sit in space) - only FitPoints/ControlPoints need map().
            Spline = p.Spline is null ? null : p.Spline with { FitPoints = M(p.Spline.FitPoints), ControlPoints = M(p.Spline.ControlPoints) },
            Wipeout = p.Wipeout is null ? null : p.Wipeout with { Boundary = M(p.Wipeout.Boundary) }
        };
    }

    /// <summary>Turns selected read-only DWG proxies into editable objects (one undo step). Block references stay whole unless <paramref name="keepInserts"/> is false.</summary>
    private bool RebuildSelectedProxies(string command, bool keepInserts = true)
    {
        // Hatch/Leader/AttributeDef/Ellipse/Spline/Wipeout/Table/Viewport stay whole (like Dimension/kept Inserts): they move/rotate/scale as one object via TransformProxy.
        var proxies = _editor.Drawing.Entities.OfType<ProxyCadEntity>().Where(e => _selectedEntityIds.Contains(e.Id) && e.Dimension is null && e.Hatch is null && e.Leader is null && e.AttributeDef is null && e.Ellipse is null && e.Spline is null && e.Wipeout is null && e.Table is null && e.Viewport is null && !(keepInserts && e.InsertData is not null)).ToArray();
        if (proxies.Length == 0) return true;
        var removals = new List<CadEntity>(); var additions = new List<CadEntity>(); var failed = 0;
        foreach (var proxy in proxies)
        {
            var parts = RebuildProxy(proxy);
            if (parts is null) { failed++; continue; }
            removals.Add(proxy); additions.AddRange(parts);
        }
        if (failed > 0) { AppendCommandHistory($"{command}: {failed} object(s) of type {string.Join("/", proxies.Select(x => x.NativeType).Distinct())} cannot be rebuilt (hatch fills, unsupported or too large); they stay read-only."); return false; }
        _editor.ApplyEntityChanges(removals, additions);
        _selectedEntityIds.Clear(); foreach (var e in additions) _selectedEntityIds.Add(e.Id);
        RefreshPropertiesPalette();
        AppendCommandHistory($"{command}: rebuilt {removals.Count} DWG object(s) into {additions.Count} editable objects.");
        return true;
    }
}
