using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1.Rendering;

/// <summary>
/// Plot geometry computed straight from the CAD model (no WPF Geometry objects): polylines in world units for strokes and fills.
/// Independent of WPF flattening/bounds so what is plotted never depends on the viewport's geometry pipeline.
/// </summary>
internal sealed class PlotGeometry
{
    public readonly record struct Figure(double[] Points, bool Closed);

    public List<Figure> Strokes { get; } = new();
    public List<Figure> Fills { get; } = new();
    public double MinX = double.PositiveInfinity, MinY = double.PositiveInfinity, MaxX = double.NegativeInfinity, MaxY = double.NegativeInfinity;
    public bool HasBounds => MinX <= MaxX && MinY <= MaxY;
    public bool IsEmpty => Strokes.Count == 0 && Fills.Count == 0;

    private void Include(double x, double y)
    {
        if (x < MinX) MinX = x; if (x > MaxX) MaxX = x;
        if (y < MinY) MinY = y; if (y > MaxY) MaxY = y;
    }

    private void AddFigure(List<Figure> target, List<double> points, bool closed)
    {
        if (points.Count < 4) return;
        for (var i = 0; i + 1 < points.Count; i += 2) Include(points[i], points[i + 1]);
        target.Add(new Figure(points.ToArray(), closed));
    }

    /// <summary>Builds the plot geometry of LINE/CIRCLE/ARC/POLYLINE and of a proxy's stroked paths and solid hatch fills. TEXT/MTEXT return null (handled as text).
    /// <paramref name="tolerance"/> is the allowed chord error in world units; &lt;= 0 picks a relative one (used for extents).</summary>
    public static PlotGeometry? Build(CadEntity entity, double tolerance)
    {
        var g = new PlotGeometry();
        try
        {
            switch (entity)
            {
                case LineEntity line when Finite(line.Start) && Finite(line.End):
                    g.AddFigure(g.Strokes, new List<double> { line.Start.X, line.Start.Y, line.End.X, line.End.Y }, false); break;
                case CircleEntity circle when double.IsFinite(circle.Radius) && Math.Abs(circle.Radius) > 1e-12 && Finite(circle.Center):
                {
                    var r = Math.Abs(circle.Radius); var pts = new List<double>();
                    var n = Segments(r, 2 * Math.PI, tolerance, 16);
                    for (var i = 0; i < n; i++) { var a = 2 * Math.PI * i / n; pts.Add(circle.Center.X + r * Math.Cos(a)); pts.Add(circle.Center.Y + r * Math.Sin(a)); }
                    g.AddFigure(g.Strokes, pts, true); break;
                }
                case ArcEntity arc when double.IsFinite(arc.Radius) && Math.Abs(arc.Radius) > 1e-12 && Finite(arc.Center) &&
                    double.IsFinite(arc.StartAngleDegrees) && double.IsFinite(arc.EndAngleDegrees):
                {
                    var sweep = (arc.EndAngleDegrees - arc.StartAngleDegrees) % 360;
                    if (sweep < 0) sweep += 360;
                    if (sweep <= 1e-9) break;
                    var pts = new List<double>();
                    AddArcPoints(pts, arc.Center.X, arc.Center.Y, Math.Abs(arc.Radius), arc.StartAngleDegrees * Math.PI / 180, sweep * Math.PI / 180, tolerance);
                    g.AddFigure(g.Strokes, pts, false); break;
                }
                case PolylineEntity poly when poly.Vertices.Count >= 2:
                    g.AddPolyline(poly, tolerance); break;
                case ProxyCadEntity proxy:
                    g.AddProxy(proxy); break;
                default:
                    return null;
            }
        }
        catch { return null; }
        return g.IsEmpty ? null : g;
    }

    private static bool Finite(Point2 p) => double.IsFinite(p.X) && double.IsFinite(p.Y);

    private static int Segments(double radius, double sweep, double tolerance, int minimum)
    {
        var tol = tolerance > 0 ? tolerance : radius * 0.002;
        var step = radius > tol ? 2 * Math.Acos(Math.Clamp(1 - tol / radius, -1, 1)) : Math.PI / 2;
        step = Math.Clamp(step, 0.002, Math.PI / 6);
        return Math.Clamp((int)Math.Ceiling(Math.Abs(sweep) / step), minimum, 8192);
    }

    /// <summary>Appends arc points from the start angle sweeping by <paramref name="sweep"/> (radians, signed), start and end included.</summary>
    private static void AddArcPoints(List<double> pts, double cx, double cy, double r, double start, double sweep, double tolerance)
    {
        var n = Segments(r, sweep, tolerance, 2);
        for (var i = 0; i <= n; i++)
        {
            var a = start + sweep * i / n;
            pts.Add(cx + r * Math.Cos(a)); pts.Add(cy + r * Math.Sin(a));
        }
    }

    private void AddPolyline(PolylineEntity poly, double tolerance)
    {
        var v = poly.Vertices; var pts = new List<double>(v.Count * 2 + 8);
        var segments = poly.Closed ? v.Count : v.Count - 1;
        if (!Finite(v[0].Point)) return;
        pts.Add(v[0].Point.X); pts.Add(v[0].Point.Y);
        for (var i = 0; i < segments; i++)
        {
            var a = v[i]; var b = v[(i + 1) % v.Count];
            if (!Finite(a.Point) || !Finite(b.Point)) continue;
            var bulge = a.Bulge; var dx = b.Point.X - a.Point.X; var dy = b.Point.Y - a.Point.Y; var chord = Math.Sqrt(dx * dx + dy * dy);
            if (Math.Abs(bulge) > 1e-12 && double.IsFinite(bulge) && chord > 1e-12)
            {
                // Bulge = tan(sweep/4); positive = counter-clockwise. Centre lies on the chord's left normal at (1-b^2)/(4b)*chord.
                var offset = (1 - bulge * bulge) / (4 * bulge) * chord;
                var cx = (a.Point.X + b.Point.X) / 2 + (-dy / chord) * offset;
                var cy = (a.Point.Y + b.Point.Y) / 2 + (dx / chord) * offset;
                var r = Math.Sqrt((a.Point.X - cx) * (a.Point.X - cx) + (a.Point.Y - cy) * (a.Point.Y - cy));
                var start = Math.Atan2(a.Point.Y - cy, a.Point.X - cx);
                var sweep = 4 * Math.Atan(bulge);
                var arc = new List<double>();
                AddArcPoints(arc, cx, cy, r, start, sweep, tolerance);
                for (var k = 2; k + 1 < arc.Count; k += 2) { pts.Add(arc[k]); pts.Add(arc[k + 1]); } // first point is already the previous end
            }
            else if (i < segments - 1 || !poly.Closed) { pts.Add(b.Point.X); pts.Add(b.Point.Y); }
        }
        // A closed polyline's last segment returns to vertex 0: the figure is marked closed instead of repeating the point.
        if (poly.Closed && pts.Count > 4 && Math.Abs(pts[^2] - pts[0]) < 1e-7 * (1 + Math.Abs(pts[0])) && Math.Abs(pts[^1] - pts[1]) < 1e-7 * (1 + Math.Abs(pts[1]))) { pts.RemoveAt(pts.Count - 1); pts.RemoveAt(pts.Count - 1); }
        AddFigure(Strokes, pts, poly.Closed);
    }

    private void AddProxy(ProxyCadEntity proxy)
    {
        var any = false;
        if (proxy.PreviewPaths is { Count: > 0 })
            foreach (var path in proxy.PreviewPaths)
            {
                var pts = PointsOf(path); if (pts.Count < 4) continue;
                if (proxy.PreviewFilled && proxy.PreviewClosed) AddFigure(Fills, pts, true); else AddFigure(Strokes, pts, proxy.PreviewClosed);
                any = true;
            }
        if (proxy.ProxyGraphicsPaths is { Count: > 0 })
            foreach (var path in proxy.ProxyGraphicsPaths)
            {
                var pts = PointsOf(path.Points); if (pts.Count < 4) continue;
                AddFigure(Strokes, pts, path.Closed); any = true;
            }
        var hasText = !string.IsNullOrWhiteSpace(proxy.PreviewText) || proxy.ProxyGraphicsTexts is { Count: > 0 };
        if (any || hasText || !proxy.HasBounds) return;
        // Bounds-only proxy (no preview graphics): mark its extents, like the viewport does.
        double minX = Math.Min(proxy.BoundsMin.X, proxy.BoundsMax.X), maxX = Math.Max(proxy.BoundsMin.X, proxy.BoundsMax.X);
        double minY = Math.Min(proxy.BoundsMin.Y, proxy.BoundsMax.Y), maxY = Math.Max(proxy.BoundsMin.Y, proxy.BoundsMax.Y);
        if (!double.IsFinite(minX + maxX + minY + maxY)) return;
        if (maxX - minX < 1e-9 && maxY - minY < 1e-9) { AddFigure(Strokes, new List<double> { minX - 1, minY, minX + 1, minY }, false); AddFigure(Strokes, new List<double> { minX, minY - 1, minX, minY + 1 }, false); }
        else if (maxX - minX < 1e-9 || maxY - minY < 1e-9) AddFigure(Strokes, new List<double> { minX, minY, maxX, maxY }, false);
        else AddFigure(Strokes, new List<double> { minX, minY, maxX, minY, maxX, maxY, minX, maxY }, true);
    }

    private static List<double> PointsOf(IReadOnlyList<Point2> path)
    {
        var pts = new List<double>(path.Count * 2);
        foreach (var p in path) if (Finite(p)) { pts.Add(p.X); pts.Add(p.Y); }
        return pts;
    }
}
