using System;
using System.Collections.Generic;
using System.Linq;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>
/// Curve kernel for the edit commands (TRIM, EXTEND, BREAK, JOIN, EXPLODE, LENGTHEN, OFFSET): every LINE, ARC, CIRCLE and POLYLINE
/// (with bulge arcs) is a path of line/arc primitives, so the commands work the same on all of them, as in AutoCAD.
/// </summary>
public partial class MainWindow
{
    /// <summary>A straight segment A→B or a circular arc from A to B (centre C, radius R, start angle A0, signed sweep in radians: + = counter-clockwise).</summary>
    private readonly struct Prim
    {
        public readonly bool IsArc;
        public readonly Point2 A, B, C;
        public readonly double R, A0, Sweep;

        private Prim(bool isArc, Point2 a, Point2 b, Point2 c, double r, double a0, double sweep)
        { IsArc = isArc; A = a; B = b; C = c; R = r; A0 = a0; Sweep = sweep; }

        public static Prim Line(Point2 a, Point2 b) => new(false, a, b, default, 0, 0, 0);

        public static Prim Arc(Point2 center, double radius, double startAngle, double sweep) => new(true,
            new Point2(center.X + radius * Math.Cos(startAngle), center.Y + radius * Math.Sin(startAngle)),
            new Point2(center.X + radius * Math.Cos(startAngle + sweep), center.Y + radius * Math.Sin(startAngle + sweep)),
            center, radius, startAngle, sweep);

        /// <summary>Polyline segment: bulge = tan(sweep / 4), positive = counter-clockwise.</summary>
        public static Prim FromBulge(Point2 a, Point2 b, double bulge)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y; var chord = Math.Sqrt(dx * dx + dy * dy);
            if (Math.Abs(bulge) < 1e-12 || !double.IsFinite(bulge) || chord < 1e-12) return Line(a, b);
            var offset = (1 - bulge * bulge) / (4 * bulge) * chord;
            var c = new Point2((a.X + b.X) / 2 - dy / chord * offset, (a.Y + b.Y) / 2 + dx / chord * offset);
            var r = Point2.Distance(a, c);
            // Keep the exact end points (A/B recomputed from angles would drift by rounding).
            return new Prim(true, a, b, c, r, Math.Atan2(a.Y - c.Y, a.X - c.X), 4 * Math.Atan(bulge));
        }

        public double Bulge => IsArc ? Math.Tan(Sweep / 4) : 0;
        public double Length => IsArc ? R * Math.Abs(Sweep) : Point2.Distance(A, B);

        public Point2 At(double t)
        {
            if (!IsArc) return new Point2(A.X + (B.X - A.X) * t, A.Y + (B.Y - A.Y) * t);
            var angle = A0 + Sweep * t;
            return new Point2(C.X + R * Math.Cos(angle), C.Y + R * Math.Sin(angle));
        }

        /// <summary>Number of t units in one full turn of an arc (t = 1 is the arc's own end).</summary>
        public double Turn => IsArc && Math.Abs(Sweep) > 1e-12 ? 2 * Math.PI / Math.Abs(Sweep) : double.PositiveInfinity;

        /// <summary>Parameter of a point on the carrier. Lines: projection (any real number). Arcs: forward fraction in [0, Turn).</summary>
        public double Param(Point2 p)
        {
            if (!IsArc)
            {
                double dx = B.X - A.X, dy = B.Y - A.Y, len2 = dx * dx + dy * dy;
                return len2 < 1e-24 ? 0 : ((p.X - A.X) * dx + (p.Y - A.Y) * dy) / len2;
            }
            var delta = Math.Atan2(p.Y - C.Y, p.X - C.X) - A0;
            if (Sweep < 0) delta = -delta;
            delta %= 2 * Math.PI; if (delta < 0) delta += 2 * Math.PI;
            return delta / Math.Abs(Sweep);
        }

        /// <summary>The part between two parameters (t0 &lt; t1; values outside 0..1 extend the primitive along its carrier).</summary>
        public Prim Sub(double t0, double t1)
        {
            if (!IsArc) return Line(At(t0), At(t1));
            var a = t0 == 0 ? A : At(t0); var b = t1 == 1 ? B : At(t1);
            return new Prim(true, a, b, C, R, A0 + Sweep * t0, Sweep * (t1 - t0));
        }

        public Prim Reversed() => IsArc ? new Prim(true, B, A, C, R, A0 + Sweep, -Sweep) : Line(B, A);

        /// <summary>Closest point of the primitive (within its extents) to p, as a parameter in 0..1.</summary>
        public double Nearest(Point2 p)
        {
            if (!IsArc) return Math.Clamp(Param(p), 0, 1);
            var t = Param(p);
            if (t <= 1) return t;
            return Point2.Distance(p, B) <= Point2.Distance(p, A) ? 1 : 0;
        }
    }

    private sealed class CurvePath
    {
        public List<Prim> Prims = new();
        public bool Closed;
        public double Length => Prims.Sum(p => p.Length);
        public Point2 At(double u)
        {
            var i = Math.Clamp((int)Math.Floor(u), 0, Prims.Count - 1);
            return Prims[i].At(u - i);
        }
    }

    private static double ArcSweepDegrees(ArcEntity a)
    {
        var sweep = ((a.EndAngleDegrees - a.StartAngleDegrees) % 360 + 360) % 360;
        return sweep < 1e-9 ? 360 : sweep;
    }

    /// <summary>LINE, ARC, CIRCLE or POLYLINE as a path of primitives; null for anything else.</summary>
    private static CurvePath? PathOf(CadEntity e)
    {
        var path = new CurvePath();
        switch (e)
        {
            case LineEntity l when Point2.Distance(l.Start, l.End) > 1e-12:
                path.Prims.Add(Prim.Line(l.Start, l.End)); break;
            case CircleEntity c when Math.Abs(c.Radius) > 1e-12:
                path.Prims.Add(Prim.Arc(c.Center, Math.Abs(c.Radius), 0, 2 * Math.PI)); path.Closed = true; break;
            case ArcEntity a when Math.Abs(a.Radius) > 1e-12:
            {
                var sweep = ArcSweepDegrees(a);
                path.Prims.Add(Prim.Arc(a.Center, Math.Abs(a.Radius), a.StartAngleDegrees * Math.PI / 180, sweep * Math.PI / 180));
                path.Closed = sweep >= 360 - 1e-9;
                break;
            }
            case PolylineEntity pl when pl.Vertices.Count >= 2:
            {
                var v = pl.Vertices; var count = pl.Closed ? v.Count : v.Count - 1;
                for (var i = 0; i < count; i++)
                {
                    var a = v[i]; var b = v[(i + 1) % v.Count];
                    if (Point2.Distance(a.Point, b.Point) < 1e-12) continue;
                    path.Prims.Add(Prim.FromBulge(a.Point, b.Point, a.Bulge));
                }
                path.Closed = pl.Closed && path.Prims.Count > 1;
                break;
            }
            default: return null;
        }
        return path.Prims.Count == 0 ? null : path;
    }

    /// <summary>Intersections of the carriers (infinite line / full circle) of two primitives.</summary>
    private static void IntersectCarriers(in Prim p, in Prim q, List<Point2> hits)
    {
        if (!p.IsArc && !q.IsArc)
        {
            double d1x = p.B.X - p.A.X, d1y = p.B.Y - p.A.Y, d2x = q.B.X - q.A.X, d2y = q.B.Y - q.A.Y;
            var den = d1x * d2y - d1y * d2x;
            var scale = Math.Sqrt((d1x * d1x + d1y * d1y) * (d2x * d2x + d2y * d2y));
            if (scale < 1e-24 || Math.Abs(den) < 1e-12 * scale) return;
            var t = ((q.A.X - p.A.X) * d2y - (q.A.Y - p.A.Y) * d2x) / den;
            hits.Add(new Point2(p.A.X + d1x * t, p.A.Y + d1y * t));
            return;
        }
        if (p.IsArc && q.IsArc)
        {
            double dx = q.C.X - p.C.X, dy = q.C.Y - p.C.Y; var d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-12 || d > p.R + q.R + 1e-9 * (p.R + q.R) || d < Math.Abs(p.R - q.R) - 1e-9 * (p.R + q.R)) return;
            var a = (d * d + p.R * p.R - q.R * q.R) / (2 * d);
            var h2 = p.R * p.R - a * a; var h = h2 > 0 ? Math.Sqrt(h2) : 0;
            double mx = p.C.X + dx / d * a, my = p.C.Y + dy / d * a;
            hits.Add(new Point2(mx - dy / d * h, my + dx / d * h));
            if (h > 1e-9 * (p.R + q.R)) hits.Add(new Point2(mx + dy / d * h, my - dx / d * h));
            return;
        }
        var line = p.IsArc ? q : p; var arc = p.IsArc ? p : q;
        double lx = line.B.X - line.A.X, ly = line.B.Y - line.A.Y, len2 = lx * lx + ly * ly;
        if (len2 < 1e-24) return;
        double fx = line.A.X - arc.C.X, fy = line.A.Y - arc.C.Y;
        var qb = 2 * (fx * lx + fy * ly); var qc = fx * fx + fy * fy - arc.R * arc.R;
        var disc = qb * qb - 4 * len2 * qc;
        if (disc < -1e-9 * len2 * arc.R * arc.R) return;
        var root = disc > 0 ? Math.Sqrt(disc) : 0;
        var t1 = (-qb - root) / (2 * len2); var t2 = (-qb + root) / (2 * len2);
        hits.Add(new Point2(line.A.X + lx * t1, line.A.Y + ly * t1));
        if (root > 1e-9 * Math.Sqrt(len2) * arc.R) hits.Add(new Point2(line.A.X + lx * t2, line.A.Y + ly * t2));
    }

    private static double CurveTolerance(Point2 p, double length) => 1e-8 * (1 + Math.Abs(p.X) + Math.Abs(p.Y) + length);

    /// <summary>True when a point of the carrier lies within the primitive's own extents.</summary>
    private static bool WithinPrim(in Prim q, Point2 p)
    {
        var t = q.Param(p);
        if (q.IsArc && t > 1) t = t > (1 + q.Turn) / 2 ? 0 : 1; // past the end or before the start: test the nearer end
        return Point2.Distance(p, q.At(Math.Clamp(t, 0, 1))) <= CurveTolerance(p, q.Length);
    }

    /// <summary>All visible LINE/ARC/CIRCLE/POLYLINE primitives except those of one entity: the cutting edges / boundaries.</summary>
    private List<Prim> EdgePrims(Guid exceptId)
    {
        var edges = new List<Prim>();
        var d = _editor.Drawing;
        foreach (var other in d.Entities)
        {
            if (other.Id == exceptId || d.HiddenEntityIds.Contains(other.Id)) continue;
            if (other is not (LineEntity or ArcEntity or CircleEntity or PolylineEntity)) continue;
            if (PathOf(other) is { } path) edges.AddRange(path.Prims);
        }
        return edges;
    }

    /// <summary>Sorted, de-duplicated path parameters (prim index + t) where the path crosses the edges.</summary>
    private static List<double> CutParameters(CurvePath path, List<Prim> edges)
    {
        var cuts = new List<double>(); var hits = new List<Point2>();
        var n = path.Prims.Count;
        for (var i = 0; i < n; i++)
        {
            var prim = path.Prims[i];
            foreach (var edge in edges)
            {
                hits.Clear(); IntersectCarriers(prim, edge, hits);
                foreach (var hit in hits)
                {
                    if (!WithinPrim(edge, hit) || !WithinPrim(prim, hit)) continue;
                    cuts.Add(i + Math.Clamp(prim.IsArc && prim.Param(hit) > 1 ? (prim.Param(hit) > (1 + prim.Turn) / 2 ? 0 : 1) : prim.Param(hit), 0, 1));
                }
            }
        }
        cuts.Sort();
        var unique = new List<double>();
        foreach (var c in cuts) if (unique.Count == 0 || c - unique[^1] > 1e-7) unique.Add(c);
        if (path.Closed && unique.Count > 1 && unique[0] + n - unique[^1] < 1e-7) unique.RemoveAt(unique.Count - 1);
        return unique;
    }

    /// <summary>Path parameter of the point of the path closest to p.</summary>
    private static double NearestParameter(CurvePath path, Point2 p)
    {
        var best = 0.0; var bestDistance = double.MaxValue;
        for (var i = 0; i < path.Prims.Count; i++)
        {
            var t = path.Prims[i].Nearest(p);
            var distance = Point2.Distance(p, path.Prims[i].At(t));
            if (distance < bestDistance) { bestDistance = distance; best = i + t; }
        }
        return best;
    }

    /// <summary>Primitives of the path between two parameters (u0 &lt; u1).</summary>
    private static List<Prim> SubPath(CurvePath path, double u0, double u1)
    {
        var result = new List<Prim>();
        for (var i = Math.Max(0, (int)Math.Floor(u0)); i < path.Prims.Count && i < u1; i++)
        {
            var t0 = Math.Max(u0, i) - i; var t1 = Math.Min(u1, i + 1) - i;
            if (t1 - t0 < 1e-9) continue;
            result.Add(t0 <= 0 && t1 >= 1 ? path.Prims[i] : path.Prims[i].Sub(t0, t1));
        }
        return result;
    }

    private static CadEntity WithLookOf(CadEntity target, CadEntity source) => target with
    {
        ExplicitColor = source.ExplicitColor, DisplayColor = source.DisplayColor, ColorByBlock = source.ColorByBlock,
        LineTypePattern = source.LineTypePattern, LineTypeName = source.LineTypeName, LineWeightMm = source.LineWeightMm, LineTypeScale = source.LineTypeScale
    };

    private static ArcEntity ArcFromPrim(in Prim p, string layer)
    {
        // ARC entities always run counter-clockwise from start to end.
        var start = p.Sweep >= 0 ? p.A0 : p.A0 + p.Sweep;
        var startDegrees = (start * 180 / Math.PI % 360 + 360) % 360;
        return new ArcEntity(p.C, p.R, startDegrees, startDegrees + Math.Abs(p.Sweep) * 180 / Math.PI, Guid.NewGuid(), layer);
    }

    /// <summary>
    /// Builds the entity for an open run of primitives cut from <paramref name="source"/>: a LINE stays a LINE, an ARC/CIRCLE piece is an ARC,
    /// a POLYLINE piece is a POLYLINE (bulges kept). Layer and look follow the source.
    /// </summary>
    private static CadEntity? EntityFromPrims(List<Prim> prims, CadEntity source)
    {
        if (prims.Count == 0) return null;
        switch (source)
        {
            case LineEntity line:
                return line with { Start = prims[0].A, End = prims[^1].B, Id = Guid.NewGuid(), NativeHandle = null };
            case ArcEntity or CircleEntity:
            {
                var first = prims[0]; var sweep = prims.Sum(p => p.Sweep);
                if (Math.Abs(sweep) < 1e-12) return null;
                var merged = Prim.Arc(first.C, first.R, first.A0, sweep);
                return WithLookOf(ArcFromPrim(merged, source.Layer), source);
            }
            case PolylineEntity pl:
            {
                var vertices = new List<PolylineVertex>(prims.Count + 1);
                foreach (var p in prims) vertices.Add(new PolylineVertex(p.A, p.Bulge));
                vertices.Add(new PolylineVertex(prims[^1].B));
                return pl with { Vertices = vertices, Closed = false, Id = Guid.NewGuid(), NativeHandle = null };
            }
        }
        return null;
    }

    /// <summary>LINE and ARC entities for each primitive (EXPLODE of a polyline).</summary>
    private static List<CadEntity> EntitiesFromPrims(IEnumerable<Prim> prims, CadEntity source)
    {
        var result = new List<CadEntity>();
        foreach (var p in prims)
        {
            CadEntity piece = p.IsArc ? ArcFromPrim(p, source.Layer) : new LineEntity(p.A, p.B, Guid.NewGuid(), source.Layer);
            result.Add(WithLookOf(piece, source));
        }
        return result;
    }

    // ---- TRIM / EXTEND ------------------------------------------------------------------

    /// <summary>TRIM: removes the part of the object under the click, between the nearest crossings on either side (all objects are cutting edges).</summary>
    private void TrimEntity(CadEntity target, Point2 click)
    {
        if (PathOf(target) is not { } path) { AppendCommandHistory("TRIM: this object cannot be trimmed (LINE, ARC, CIRCLE, POLYLINE only)."); return; }
        var cuts = CutParameters(path, EdgePrims(target.Id));
        var n = path.Prims.Count; var uc = NearestParameter(path, click);
        var pieces = new List<CadEntity>();
        if (path.Closed)
        {
            if (cuts.Count < 2) { AppendCommandHistory("TRIM: a closed object needs at least two crossings to be trimmed."); return; }
            var hi = cuts.FirstOrDefault(c => c > uc + 1e-9, cuts[0]);
            var lo = cuts.LastOrDefault(c => c < uc - 1e-9, cuts[^1]);
            // Keep the part that runs forward from hi round to lo.
            var kept = hi < lo ? SubPath(path, hi, lo) : SubPath(path, hi, n).Concat(SubPath(path, 0, lo)).ToList();
            if (EntityFromPrims(kept, target) is { } piece) pieces.Add(piece);
        }
        else
        {
            var inner = cuts.Where(c => c > 1e-7 && c < n - 1e-7).ToList();
            double? lo = null, hi = null;
            foreach (var c in inner) { if (c <= uc) lo = c; else { hi = c; break; } }
            if (lo is null && hi is null) { AppendCommandHistory("TRIM: no cutting edge crosses this object."); return; }
            if (lo is { } l && EntityFromPrims(SubPath(path, 0, l), target) is { } head) pieces.Add(head);
            if (hi is { } h && EntityFromPrims(SubPath(path, h, n), target) is { } tail) pieces.Add(tail);
        }
        _editor.ApplyEntityChanges(new[] { target }, pieces);
        _selectedEntityIds.Clear();
        RenderDrawing(false);
        AppendCommandHistory(pieces.Count == 0 ? "TRIM: object removed." : "TRIM: done. Click the next object (Shift+click extends), Esc to end.");
    }

    /// <summary>EXTEND: lengthens the end nearest the click along its own line / circle up to the next boundary object.</summary>
    private void ExtendEntity(CadEntity target, Point2 click)
    {
        if (PathOf(target) is not { } path || path.Closed) { AppendCommandHistory("EXTEND: only open LINE, ARC and POLYLINE objects can be extended."); return; }
        var n = path.Prims.Count; var atEnd = NearestParameter(path, click) >= n / 2.0;
        var prim = atEnd ? path.Prims[^1] : path.Prims[0];
        var edges = EdgePrims(target.Id); var hits = new List<Point2>();
        double? best = null;
        foreach (var edge in edges)
        {
            hits.Clear(); IntersectCarriers(prim, edge, hits);
            foreach (var hit in hits)
            {
                if (!WithinPrim(edge, hit)) continue;
                var t = prim.Param(hit);
                if (atEnd)
                {
                    // Forward past the end; for an arc anything up to the start again (a full turn) counts.
                    if (t > 1 + 1e-7 && (!prim.IsArc || t < prim.Turn - 1e-7) && (best is null || t < best)) best = t;
                }
                else
                {
                    var back = prim.IsArc ? t - prim.Turn : t; // arcs: measure backwards from the start
                    if (prim.IsArc && t <= 1 + 1e-7) continue;
                    if (back < -1e-7 && (best is null || back > best)) best = back;
                }
            }
        }
        if (best is not { } target_t) { AppendCommandHistory("EXTEND: no boundary in that direction."); return; }
        var prims = new List<Prim>(path.Prims);
        if (atEnd) prims[^1] = prim.Sub(0, target_t); else prims[0] = prim.Sub(target_t, 1);
        if (EntityFromPrims(prims, target) is not { } changed) return;
        _editor.ApplyEntityChanges(new[] { target }, new[] { changed });
        _selectedEntityIds.Clear();
        RenderDrawing(false);
        AppendCommandHistory("EXTEND: done. Click the next object (Shift+click trims), Esc to end.");
    }

    // ---- BREAK --------------------------------------------------------------------------

    /// <summary>Removes the part between two points of an object (same point = split in two). Closed objects lose the part from the first to the second point.</summary>
    private bool BreakEntity(CadEntity target, Point2 first, Point2 second)
    {
        if (PathOf(target) is not { } path) { AppendCommandHistory("BREAK: select a LINE, ARC, CIRCLE or POLYLINE."); return false; }
        var n = path.Prims.Count;
        double u1 = NearestParameter(path, first), u2 = NearestParameter(path, second);
        var pieces = new List<CadEntity>();
        void Add(List<Prim> prims) { if (EntityFromPrims(prims, target) is { } e) pieces.Add(e); }
        if (path.Closed)
        {
            if (Math.Abs(u1 - u2) < 1e-9)
            {
                if (target is CircleEntity) { AppendCommandHistory("BREAK: a circle needs two different points."); return false; }
                Add(SubPath(path, u1, n).Concat(SubPath(path, 0, u1)).ToList()); // open the closed polyline at that point
            }
            else Add(u2 < u1 ? SubPath(path, u2, u1) : SubPath(path, u2, n).Concat(SubPath(path, 0, u1)).ToList());
        }
        else
        {
            var a = Math.Min(u1, u2); var b = Math.Max(u1, u2);
            if (a > 1e-9) Add(SubPath(path, 0, a));
            if (b < n - 1e-9) Add(SubPath(path, b, n));
            if (pieces.Count == 1 && b - a < 1e-9) { AppendCommandHistory("BREAK: the point is at the end of the object; nothing to break."); return false; }
        }
        _editor.ApplyEntityChanges(new[] { target }, pieces);
        _selectedEntityIds.Clear(); foreach (var p in pieces) _selectedEntityIds.Add(p.Id);
        RefreshPropertiesPalette(); RenderDrawing(false);
        AppendCommandHistory(Math.Abs(u1 - u2) < 1e-9 ? "BREAK: object broken at the point." : "BREAK: part removed.");
        return true;
    }

    // ---- JOIN ---------------------------------------------------------------------------

    /// <summary>
    /// JOIN as in AutoCAD: collinear lines become one LINE (gaps allowed); objects that touch end to end (lines, arcs, open polylines)
    /// become one POLYLINE per chain. Objects that touch nothing are left alone and reported.
    /// </summary>
    private void JoinSelectedCurves()
    {
        var sources = SelectedGeometry().Where(e => e is LineEntity or ArcEntity || e is PolylineEntity { Closed: false }).ToList();
        if (sources.Count < 2) { AppendCommandHistory("JOIN: select two or more lines, arcs or open polylines."); return; }
        var removed = new List<CadEntity>(); var created = new List<CadEntity>();

        // 1) All lines collinear: one line from end to end.
        if (sources.All(e => e is LineEntity))
        {
            var lines = sources.Cast<LineEntity>().ToList(); var first = lines[0];
            double dx = first.End.X - first.Start.X, dy = first.End.Y - first.Start.Y; var len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 1e-12)
            {
                double ux = dx / len, uy = dy / len; var tolerance = 1e-6 * (1 + len);
                double Off(Point2 p) => Math.Abs((p.X - first.Start.X) * uy - (p.Y - first.Start.Y) * ux);
                double Along(Point2 p) => (p.X - first.Start.X) * ux + (p.Y - first.Start.Y) * uy;
                if (lines.All(l => Off(l.Start) <= tolerance && Off(l.End) <= tolerance))
                {
                    var min = lines.Min(l => Math.Min(Along(l.Start), Along(l.End))); var max = lines.Max(l => Math.Max(Along(l.Start), Along(l.End)));
                    var joined = first with
                    {
                        Start = new Point2(first.Start.X + ux * min, first.Start.Y + uy * min), End = new Point2(first.Start.X + ux * max, first.Start.Y + uy * max),
                        Id = Guid.NewGuid(), NativeHandle = null
                    };
                    ApplySelectedReplacement("JOIN", lines.Cast<CadEntity>().ToArray(), new CadEntity[] { joined });
                    RenderDrawing(false);
                    return;
                }
            }
        }

        // 2) End-to-end chains.
        var pool = sources.Select(e => (Entity: e, Prims: PathOf(e)?.Prims)).Where(x => x.Prims is { Count: > 0 }).Select(x => (x.Entity, Prims: x.Prims!)).ToList();
        var leftovers = 0;
        while (pool.Count > 0)
        {
            var seed = pool[0]; pool.RemoveAt(0);
            var chain = new List<Prim>(seed.Prims); var members = new List<CadEntity> { seed.Entity };
            var grew = true;
            while (grew)
            {
                grew = false;
                for (var i = 0; i < pool.Count; i++)
                {
                    var candidate = pool[i].Prims; var tolerance = 1e-6 * (1 + Math.Abs(chain[^1].B.X) + Math.Abs(chain[^1].B.Y));
                    Point2 head = chain[0].A, tail = chain[^1].B, cStart = candidate[0].A, cEnd = candidate[^1].B;
                    List<Prim> Flipped() { var r = candidate.Select(p => p.Reversed()).ToList(); r.Reverse(); return r; }
                    if (Point2.Distance(tail, cStart) <= tolerance) chain.AddRange(candidate);
                    else if (Point2.Distance(tail, cEnd) <= tolerance) chain.AddRange(Flipped());
                    else if (Point2.Distance(head, cEnd) <= tolerance) chain.InsertRange(0, candidate);
                    else if (Point2.Distance(head, cStart) <= tolerance) chain.InsertRange(0, Flipped());
                    else continue;
                    members.Add(pool[i].Entity); pool.RemoveAt(i); grew = true; break;
                }
            }
            if (members.Count < 2) { leftovers++; continue; }
            var closeTolerance = 1e-6 * (1 + Math.Abs(chain[0].A.X) + Math.Abs(chain[0].A.Y));
            var closed = chain.Count > 1 && Point2.Distance(chain[0].A, chain[^1].B) <= closeTolerance;
            var vertices = chain.Select(p => new PolylineVertex(p.A, p.Bulge)).ToList();
            if (!closed) vertices.Add(new PolylineVertex(chain[^1].B));
            var source = members[0];
            CadEntity poly = source is PolylineEntity sp
                ? sp with { Vertices = vertices, Closed = closed, Id = Guid.NewGuid(), NativeHandle = null }
                : WithLookOf(new PolylineEntity(vertices, closed, Guid.NewGuid(), source.Layer), source);
            removed.AddRange(members); created.Add(poly);
        }
        if (created.Count == 0) { AppendCommandHistory("JOIN: the selected objects do not touch end to end (and the lines are not collinear)."); return; }
        ApplySelectedReplacement("JOIN", removed.ToArray(), created);
        RenderDrawing(false);
        if (leftovers > 0) AppendCommandHistory($"JOIN: {leftovers} object(s) touch nothing and were left unchanged.");
    }

    // ---- OFFSET of polylines with arcs ----------------------------------------------------

    /// <summary>Offsets a path to the left of its direction by d (negative = right). Corners are trimmed/extended to meet; gaps are closed with an arc about the original vertex.</summary>
    private static List<Prim>? OffsetPrims(CurvePath path, double d)
    {
        var source = path.Prims; var n = source.Count;
        var offset = new Prim?[n];
        for (var i = 0; i < n; i++)
        {
            var p = source[i];
            if (!p.IsArc)
            {
                var len = p.Length; if (len < 1e-12) continue;
                double ox = -(p.B.Y - p.A.Y) / len * d, oy = (p.B.X - p.A.X) / len * d;
                offset[i] = Prim.Line(new Point2(p.A.X + ox, p.A.Y + oy), new Point2(p.B.X + ox, p.B.Y + oy));
            }
            else
            {
                var r = p.Sweep > 0 ? p.R - d : p.R + d; // left of a counter-clockwise arc is towards its centre
                if (r > 1e-9) offset[i] = Prim.Arc(p.C, r, p.A0, p.Sweep);
            }
        }
        var result = new List<Prim>(); var hits = new List<Point2>();
        var kept = Enumerable.Range(0, n).Where(i => offset[i] is not null).ToList();
        if (kept.Count == 0) return null;
        var prims = kept.Select(i => offset[i]!.Value).ToList();
        var corners = kept.Select(i => source[i].B).ToList(); // original vertex after each kept primitive
        var joins = path.Closed ? prims.Count : prims.Count - 1;
        var bridges = new Prim?[prims.Count];
        for (var k = 0; k < joins; k++)
        {
            var i = k; var j = (k + 1) % prims.Count;
            var a = prims[i]; var b = prims[j];
            if (Point2.Distance(a.B, b.A) <= CurveTolerance(a.B, 0)) continue;
            hits.Clear(); IntersectCarriers(a, b, hits);
            var mid = new Point2((a.B.X + b.A.X) / 2, (a.B.Y + b.A.Y) / 2);
            Point2? best = null; var bestDistance = double.MaxValue;
            foreach (var hit in hits)
            {
                var ta = a.Param(hit); var tb = b.Param(hit);
                // The end of a may move back or forth, but a must not vanish or wrap round; same for the start of b.
                var okA = a.IsArc ? ta > 1e-9 && ta < 1 + (a.Turn - 1) / 2 : ta > 1e-9;
                var okB = b.IsArc ? tb < 1 - 1e-9 || tb > 1 + (b.Turn - 1) / 2 : tb < 1 - 1e-9;
                if (!okA || !okB) continue;
                var distance = Point2.Distance(hit, mid);
                if (distance < bestDistance) { bestDistance = distance; best = hit; }
            }
            if (best is { } corner && bestDistance <= Math.Abs(d) * 10 + Point2.Distance(a.B, b.A))
            {
                var ta = a.Param(corner); var tb = b.Param(corner);
                prims[i] = a.Sub(0, ta);
                prims[j] = b.Sub(b.IsArc && tb > 1 ? tb - b.Turn : tb, 1);
            }
            else
            {
                // No usable crossing (outside of a convex corner next to an arc): round the gap about the original vertex.
                var v = corners[i];
                var start = Math.Atan2(a.B.Y - v.Y, a.B.X - v.X); var end = Math.Atan2(b.A.Y - v.Y, b.A.X - v.X);
                var sweep = (end - start) % (2 * Math.PI);
                if (sweep > Math.PI) sweep -= 2 * Math.PI; else if (sweep < -Math.PI) sweep += 2 * Math.PI;
                var radius = Point2.Distance(a.B, v);
                if (radius > 1e-9 && Math.Abs(sweep) > 1e-9) bridges[i] = Prim.Arc(v, radius, start, sweep);
                else bridges[i] = Prim.Line(a.B, b.A);
            }
        }
        for (var k = 0; k < prims.Count; k++)
        {
            if (prims[k].Length > 1e-12) result.Add(prims[k]);
            if (bridges[k] is { } bridge) result.Add(bridge);
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>Offset of a polyline that has arc segments, to the side of the pick point.</summary>
    private static PolylineEntity? OffsetCurvedPolyline(PolylineEntity pl, Point2 side, double distance)
    {
        if (PathOf(pl) is not { } path) return null;
        var u = NearestParameter(path, side); var index = Math.Clamp((int)Math.Floor(u), 0, path.Prims.Count - 1);
        var prim = path.Prims[index];
        bool left;
        if (prim.IsArc) left = (Point2.Distance(side, prim.C) < prim.R) == (prim.Sweep > 0);
        else left = (prim.B.X - prim.A.X) * (side.Y - prim.A.Y) - (prim.B.Y - prim.A.Y) * (side.X - prim.A.X) >= 0;
        var prims = OffsetPrims(path, left ? distance : -distance);
        if (prims is null) return null;
        var vertices = prims.Select(p => new PolylineVertex(p.A, p.Bulge)).ToList();
        if (!pl.Closed) vertices.Add(new PolylineVertex(prims[^1].B));
        return pl with { Vertices = vertices, Id = Guid.NewGuid(), NativeHandle = null };
    }

    /// <summary>Shortest distance from a point to a LINE/ARC/CIRCLE/POLYLINE (OFFSET Through).</summary>
    private static double? DistanceToCurve(CadEntity e, Point2 p)
    {
        if (PathOf(e) is not { } path) return null;
        return Point2.Distance(p, path.At(NearestParameter(path, p)));
    }

    /// <summary>Circle through three points; null when they are collinear.</summary>
    private static (Point2 Center, double Radius)? CircleThrough(Point2 a, Point2 b, Point2 c)
    {
        var d = 2 * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        var scale = Point2.Distance(a, b) * Point2.Distance(b, c) + Point2.Distance(a, c) * Point2.Distance(a, c);
        if (Math.Abs(d) < 1e-12 * Math.Max(1e-12, scale)) return null;
        double a2 = a.X * a.X + a.Y * a.Y, b2 = b.X * b.X + b.Y * b.Y, c2 = c.X * c.X + c.Y * c.Y;
        var center = new Point2((a2 * (b.Y - c.Y) + b2 * (c.Y - a.Y) + c2 * (a.Y - b.Y)) / d, (a2 * (c.X - b.X) + b2 * (a.X - c.X) + c2 * (b.X - a.X)) / d);
        return (center, Point2.Distance(center, a));
    }
}
