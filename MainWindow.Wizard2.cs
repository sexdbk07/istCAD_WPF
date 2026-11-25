using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>More guided commands: ALIGN, QSELECT, LIST, SPLINE, XLINE, DIVIDE, MEASURE.</summary>
public partial class MainWindow
{
    private bool TryHandleGuidedCommands(string[] tokens)
    {
        switch (tokens[0])
        {
            case "ALIGN": case "AL": AlignCommand(); return true;
            case "QSELECT": case "QSEL": QSelectCommand(); return true;
            case "FILTER": case "FI": FilterCommand(); return true;
            case "LIST": ListCommand(); return true;
            case "SPLINE": case "SPL": SplineCommand(); return true;
            case "XLINE": case "XL": XLineCommand(); return true;
            case "DIVIDE": case "DIV": DivideMeasureCommand(true); return true;
            case "MEASURE": DivideMeasureCommand(false); return true;
            case "TABLE": case "TB": TableCommand(); return true;
            case "VIEWPORT": case "MVIEW": case "VPORTS": ViewportCommand(); return true;
        }
        return false;
    }

    // ---- ALIGN ------------------------------------------------------------------------

    private void AlignCommand()
    {
        RequireSelection("ALIGN", () =>
        {
            if (!RebuildSelectedProxies("ALIGN")) return;
            var originals = SelectedGeometry();
            if (originals.Length == 0) { AppendCommandHistory("ALIGN: select objects first."); return; }
            List<CadEntity> Moved(Wizard x)
            {
                if (!x.Has(0) || !x.Has(1)) return new List<CadEntity>();
                Point2 s1 = x.P(0), d1 = x.P(1);
                var angle = 0.0;
                if (x.Has(2) && x.Has(3))
                {
                    Point2 s2 = x.P(2), d2 = x.P(3);
                    if (Point2.Distance(s1, s2) > 1e-9 && Point2.Distance(d1, d2) > 1e-9)
                        angle = Math.Atan2(d2.Y - d1.Y, d2.X - d1.X) - Math.Atan2(s2.Y - s1.Y, s2.X - s1.X);
                }
                var offset = new Point2(d1.X - s1.X, d1.Y - s1.Y);
                var result = new List<CadEntity>();
                foreach (var e in originals)
                {
                    var rotated = Math.Abs(angle) > 1e-12 ? RotateEntity(e, s1, angle) : e;
                    var moved = TranslateEntity(rotated, offset, false);
                    if (!ReferenceEquals(moved, e)) result.Add(moved);
                }
                return result;
            }
            StartWizard(new Wizard
            {
                Name = "ALIGN",
                Steps = new[]
                {
                    new WizStep { Kind = WizKind.Point, Prompt = "Specify first source point:" },
                    new WizStep { Kind = WizKind.Point, Prompt = "Specify first destination point:" },
                    new WizStep { Kind = WizKind.Point, Prompt = "Specify second source point (Enter = move only):", Optional = true },
                    new WizStep { Kind = WizKind.Point, Prompt = "Specify second destination point:" }
                },
                Preview = x => originals.Length > 400 || x.Index < 1 ? null : Moved(x),
                Commit = x =>
                {
                    var after = Moved(x);
                    if (after.Count == 0) { AppendCommandHistory("ALIGN: nothing to move."); return false; }
                    var before = originals.Where(o => after.Any(a => a.Id == o.Id)).ToArray();
                    ApplySelectedReplacement("ALIGN", before, after);
                    return true;
                }
            });
        });
    }

    // ---- QSELECT ----------------------------------------------------------------------

    private static bool WildcardMatch(string text, string pattern) =>
        string.IsNullOrWhiteSpace(pattern) || pattern.Trim() == "*" || Regex.IsMatch(text, "^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);

    private void QSelectCommand()
    {
        StartWizard(new Wizard
        {
            Name = "QSELECT",
            Steps = new[]
            {
                new WizStep { Kind = WizKind.Text, Default = "*", Prompt = "Object type (Line, Circle, Arc, Polyline, Text, MText, Dimension, Block... or *)" },
                new WizStep { Kind = WizKind.Text, Default = "*", Prompt = "Layer name (wildcards * ? allowed)" },
                new WizStep { Kind = WizKind.Text, Default = "*", Prompt = "Color (ByLayer, ByBlock, R,G,B or *)" }
            },
            Commit = x =>
            {
                var d = _editor.Drawing;
                string ColorOf(CadEntity e) => e.ColorByBlock ? "ByBlock" : e.ExplicitColor is { } c ? $"{c.R},{c.G},{c.B}" : "ByLayer";
                var matches = d.Entities.Where(e => !d.HiddenEntityIds.Contains(e.Id)
                    && WildcardMatch(GetEntityTypeName(e), x.S(0)) && WildcardMatch(e.Layer, x.S(1)) && WildcardMatch(ColorOf(e), x.S(2))).ToList();
                _selectedEntityIds.Clear(); foreach (var e in matches) _selectedEntityIds.Add(e.Id);
                RefreshPropertiesPalette(); RenderSelectionOverlay();
                AppendCommandHistory($"QSELECT: {matches.Count:N0} object(s) selected.");
                return true;
            }
        });
    }

    // ---- LIST -------------------------------------------------------------------------

    private void ListCommand()
    {
        string Describe(CadEntity e)
        {
            var color = e.ColorByBlock ? "ByBlock" : e.ExplicitColor is { } c ? $"{c.R},{c.G},{c.B}" : "ByLayer";
            var line = $"{GetEntityTypeName(e)}  Layer: {e.Layer}  Color: {color}  Linetype: {e.LineTypeName ?? "ByLayer"}" + (e.NativeHandle is { } h ? $"  Handle: {h}" : "");
            string F(double v) => v.ToString("0.####", CultureInfo.CurrentCulture);
            switch (e)
            {
                case LineEntity l:
                    return $"{line}\n   from ({F(l.Start.X)}, {F(l.Start.Y)}) to ({F(l.End.X)}, {F(l.End.Y)})  length {F(Point2.Distance(l.Start, l.End))}  angle {F(Math.Atan2(l.End.Y - l.Start.Y, l.End.X - l.Start.X) * 180 / Math.PI)}°";
                case CircleEntity ci:
                    return $"{line}\n   center ({F(ci.Center.X)}, {F(ci.Center.Y)})  radius {F(ci.Radius)}  circumference {F(Math.Tau * ci.Radius)}  area {F(Math.PI * ci.Radius * ci.Radius)}";
                case ArcEntity a:
                    return $"{line}\n   center ({F(a.Center.X)}, {F(a.Center.Y)})  radius {F(a.Radius)}  start {F(a.StartAngleDegrees)}°  end {F(a.EndAngleDegrees)}°";
                case PolylineEntity pl:
                {
                    var pts = PathPoints(pl); double len = 0; for (var i = 1; i < pts.Count; i++) len += Point2.Distance(pts[i - 1], pts[i]);
                    var area = pl.Closed ? Math.Abs(pts.Select((p, i) => p.X * pts[(i + 1) % pts.Count].Y - pts[(i + 1) % pts.Count].X * p.Y).Sum()) / 2 : 0;
                    return $"{line}\n   {pl.Vertices.Count} vertices  {(pl.Closed ? "closed" : "open")}  length {F(len)}" + (pl.Closed ? $"  area {F(area)}" : "");
                }
                case CadTextEntity t: return $"{line}\n   \"{t.Value}\" at ({F(t.Position.X)}, {F(t.Position.Y)})  height {F(t.Height)}  font {t.TextFontFamily}";
                case CadMTextEntity t: return $"{line}\n   \"{t.Value}\" at ({F(t.Position.X)}, {F(t.Position.Y)})  height {F(t.Height)}  font {t.TextFontFamily}";
                case ProxyCadEntity { Dimension: { } dim }: return $"{line}\n   {dim.Kind.ToLowerInvariant()} dimension  measurement {DimensionMeasureText(dim)}  style {dim.Style.Name}";
                case ProxyCadEntity { InsertData: { } ins }: return $"{line}\n   block \"{ins.BlockName}\" at ({F(ins.Position.X)}, {F(ins.Position.Y)})  rotation {F(ins.Rotation * 180 / Math.PI)}°  scale {F(ins.ScaleX)}, {F(ins.ScaleY)}";
                case ProxyCadEntity p: return $"{line}\n   native {p.NativeType}";
            }
            return line;
        }
        var preselected = SelectedGeometry();
        if (preselected.Length > 0)
        {
            foreach (var e in preselected.Take(30)) AppendCommandHistory(Describe(e));
            if (preselected.Length > 30) AppendCommandHistory($"... and {preselected.Length - 30} more.");
            return;
        }
        StartWizard(new Wizard
        {
            Name = "LIST",
            Steps = new[] { new WizStep { Kind = WizKind.Entity, Prompt = "Click an object (Enter or Esc to end):" } },
            RepeatFrom = 0,
            Commit = x => { AppendCommandHistory(Describe(x.E(0))); return true; }
        });
    }

    // ---- shared helper: an entity as a list of points -------------------------------------

    private static List<Point2> PathPoints(CadEntity e)
    {
        switch (e)
        {
            case LineEntity l: return new List<Point2> { l.Start, l.End };
            case CircleEntity c: return Enumerable.Range(0, 361).Select(i => new Point2(c.Center.X + c.Radius * Math.Cos(Math.Tau * i / 360), c.Center.Y + c.Radius * Math.Sin(Math.Tau * i / 360))).ToList();
            case ArcEntity a:
            {
                double a0 = a.StartAngleDegrees * Math.PI / 180, a1 = a.EndAngleDegrees * Math.PI / 180;
                while (a1 <= a0) a1 += Math.Tau;
                var steps = Math.Max(8, (int)((a1 - a0) / Math.Tau * 360));
                return Enumerable.Range(0, steps + 1).Select(i => new Point2(a.Center.X + a.Radius * Math.Cos(a0 + (a1 - a0) * i / steps), a.Center.Y + a.Radius * Math.Sin(a0 + (a1 - a0) * i / steps))).ToList();
            }
            case PolylineEntity pl:
            {
                var pts = PolylineTessellator.Tessellate(pl).ToList();
                if (pl.Closed && pts.Count > 1 && Point2.Distance(pts[0], pts[^1]) > 1e-12) pts.Add(pts[0]);
                return pts;
            }
        }
        return new List<Point2>();
    }

    // ---- SPLINE (true native cubic NURBS curve through the picked fit points) --------------------

    /// <summary>Builds the real SPLINE parametric data (control points/knots/weights) from fit points, using
    /// ACadSharp's OWN native Spline.UpdateFromFitPoints() - the exact same math a real AutoCAD-compatible SPLINE
    /// entity uses - instead of a hand-rolled curve, so the live preview and the saved file always agree and the
    /// saved object is a true native SPLINE (editable with AutoCAD's own fit-point/control-point grips), not a
    /// tessellated polyline approximation like this command used before.</summary>
    private static CadSplineData? BuildSplineData(IReadOnlyList<Point2> fitPoints, bool closed = false)
    {
        if (fitPoints.Count < 2) return null;
        var native = new ACadSharp.Entities.Spline { Degree = 3, IsClosed = closed && fitPoints.Count >= 3 };
        foreach (var p in fitPoints) native.FitPoints.Add(new CSMath.XYZ(p.X, p.Y, 0));
        if (!native.UpdateFromFitPoints()) return null;
        return new CadSplineData(fitPoints.ToArray(),
            native.ControlPoints.Select(c => new Point2(c.X, c.Y)).ToArray(),
            native.Knots.ToArray(),
            (native.Weights.Count == native.ControlPoints.Count ? native.Weights : Enumerable.Repeat(1.0, native.ControlPoints.Count)).ToArray(),
            native.Degree, native.IsClosed);
    }

    /// <summary>Samples a CadSplineData into world points for display/hit-testing (ProxyGraphicsPaths) by rebuilding
    /// the same ACadSharp native Spline from the stored control points/knots/weights and calling its own
    /// PolygonalVertexes - so the preview/selection outline is pixel-exact to what gets saved, same pattern as
    /// HATCH/LEADER/ELLIPSE (SampleEllipse) already use for their own parametric data.</summary>
    private static Point2[] SampleSpline(CadSplineData s, int segments = 160)
    {
        var native = new ACadSharp.Entities.Spline { Degree = s.Degree, IsClosed = s.Closed };
        foreach (var c in s.ControlPoints) native.ControlPoints.Add(new CSMath.XYZ(c.X, c.Y, 0));
        foreach (var k in s.Knots) native.Knots.Add(k);
        foreach (var w in s.Weights) native.Weights.Add(w);
        return native.TryPolygonalVertexes(segments, out var verts) && verts.Count >= 2
            ? verts.Select(v => new Point2(v.X, v.Y)).ToArray()
            : s.FitPoints.ToArray();
    }

    private ProxyCadEntity? BuildSplineProxy(IReadOnlyList<Point2> fitPoints)
    {
        var data = BuildSplineData(fitPoints);
        if (data is null) return null;
        var pts = SampleSpline(data);
        double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y), maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
        return (ProxyCadEntity)_editor.StyleNew(new ProxyCadEntity("SPLINE", new Point2(minX, minY), new Point2(maxX, maxY), true, Guid.NewGuid(), _editor.Drawing.CurrentLayer,
            ProxyGraphicsPaths: new[] { new ProxyPreviewPath(pts, false) }, Spline: data));
    }

    private void SplineCommand()
    {
        List<CadEntity>? Build(Wizard x)
        {
            var pts = new List<Point2>((List<Point2>)x.Data!);
            if (x.Has(0) && x.Index == 0 && (pts.Count == 0 || Point2.Distance(pts[^1], x.P(0)) > 1e-10)) pts.Add(x.P(0));
            if (pts.Count < 2) return null;
            var proxy = BuildSplineProxy(pts);
            return proxy is null ? null : new List<CadEntity> { proxy };
        }
        StartWizard(new Wizard
        {
            Name = "SPLINE",
            Data = new List<Point2>(),
            Steps = new[] { new WizStep { Kind = WizKind.Point, Prompt = "Specify the next fit point (Enter or Esc to finish):" } },
            RepeatFrom = 0, EndOnEsc = true,
            Preview = Build,
            Commit = x => { ((List<Point2>)x.Data!).Add(x.P(0)); return true; },
            OnEnd = x =>
            {
                var pts = (List<Point2>)x.Data!;
                if (pts.Count < 2) { AppendCommandHistory("SPLINE: at least two points are required."); return; }
                var proxy = BuildSplineProxy(pts);
                if (proxy is null) { AppendCommandHistory("SPLINE: could not fit a curve through these points."); return; }
                AddNewEntities("SPLINE", new List<CadEntity> { proxy }, "spline created (real native SPLINE, not a polyline approximation).");
            }
        });
    }

    // ---- XLINE --------------------------------------------------------------------------

    private void XLineCommand()
    {
        const double Half = 100_000;
        LineEntity Infinite(Point2 a, double ux, double uy) =>
            new(new Point2(a.X - ux * Half, a.Y - uy * Half), new Point2(a.X + ux * Half, a.Y + uy * Half), Guid.NewGuid(), _editor.Drawing.CurrentLayer);
        List<CadEntity>? Build(Wizard x)
        {
            switch (x.Data as string)
            {
                case "H":
                    if (!x.Has(2)) return null;
                    return new List<CadEntity> { Infinite(x.P(2), 1, 0) };
                case "V":
                    if (!x.Has(2)) return null;
                    return new List<CadEntity> { Infinite(x.P(2), 0, 1) };
                case "A":
                {
                    if (!x.Has(3) || !x.Has(4)) return null;
                    var rad = x.D(3) * Math.PI / 180;
                    return new List<CadEntity> { Infinite(x.P(4), Math.Cos(rad), Math.Sin(rad)) };
                }
                case "B":
                {
                    if (!x.Has(5) || !x.Has(6) || !x.Has(7)) return null;
                    Point2 vtx = x.P(5), leg1 = x.P(6), leg2 = x.P(7);
                    var a1 = Math.Atan2(leg1.Y - vtx.Y, leg1.X - vtx.X);
                    var a2 = Math.Atan2(leg2.Y - vtx.Y, leg2.X - vtx.X);
                    var diff = a2 - a1; while (diff <= -Math.PI) diff += 2 * Math.PI; while (diff > Math.PI) diff -= 2 * Math.PI;
                    var bisector = a1 + diff / 2;
                    return new List<CadEntity> { Infinite(vtx, Math.Cos(bisector), Math.Sin(bisector)) };
                }
                case "O":
                {
                    if (!x.Has(8) || !x.Has(9) || !x.Has(10) || x.E(9) is not LineEntity src) return null;
                    var dist = x.D(8); if (dist <= 0) return null;
                    var len = Point2.Distance(src.Start, src.End); if (len < 1e-9) return null;
                    double Cross(Point2 a, Point2 b, Point2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
                    var sign = Cross(src.Start, src.End, x.P(10)) >= 0 ? 1 : -1;
                    double ux = -(src.End.Y - src.Start.Y) / len * dist * sign, uy = (src.End.X - src.Start.X) / len * dist * sign;
                    var mid = new Point2((src.Start.X + src.End.X) / 2 + ux, (src.Start.Y + src.End.Y) / 2 + uy);
                    return new List<CadEntity> { Infinite(mid, (src.End.X - src.Start.X) / len, (src.End.Y - src.Start.Y) / len) };
                }
                default:
                {
                    if (!x.Has(0) || !x.Has(1)) return null;
                    Point2 a = x.P(0), b = x.P(1); var len = Point2.Distance(a, b); if (len < 1e-9) return null;
                    return new List<CadEntity> { Infinite(a, (b.X - a.X) / len, (b.Y - a.Y) / len) };
                }
            }
        }
        StartWizard(new Wizard
        {
            Name = "XLINE",
            Steps = new[]
            {
                new WizStep
                {
                    Kind = WizKind.Point, Prompt = "Specify a point or [Hor/Ver/Ang/Bisect/Offset]:",
                    Keyword = (x, text) =>
                    {
                        switch (text)
                        {
                            case "H" or "HOR": x.Data = "H"; WizardGoto(2); return true;
                            case "V" or "VER": x.Data = "V"; WizardGoto(2); return true;
                            case "A" or "ANG": x.Data = "A"; WizardGoto(3); return true;
                            case "B" or "BISECT": x.Data = "B"; WizardGoto(5); return true;
                            case "O" or "OFFSET": x.Data = "O"; WizardGoto(8); return true;
                            default: return false;
                        }
                    }
                },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify a through point (Enter or Esc to end):", Optional = true, Next = (_, _) => 99 },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify through point (Enter or Esc to end):", Optional = true, Next = (_, _) => 99 },
                new WizStep { Kind = WizKind.Angle, Prompt = "Enter angle of xline", Default = 0.0 },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify through point (Enter or Esc to end):", Optional = true, Next = (_, _) => 99 },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify angle vertex point:" },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify start point of angle:" },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify end point of angle (Enter or Esc to end):", Optional = true, Next = (_, _) => 99 },
                new WizStep { Kind = WizKind.Number, Prompt = "Specify offset distance", Default = 100.0 },
                new WizStep { Kind = WizKind.Entity, Prompt = "Select a line to offset:", Accept = e => e is LineEntity },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify side (Enter or Esc to end):", Optional = true, Next = (_, _) => 99 }
            },
            RepeatFrom = 0,
            Preview = Build,
            Commit = x =>
            {
                var items = Build(x); x.Data = null;
                if (items is null) { AppendCommandHistory("XLINE: specify the required points first."); return false; }
                AddNewEntities("XLINE", items, "construction line created (a very long LINE)."); return true;
            }
        });
    }

    // ---- DIVIDE / MEASURE (tick marks across the object) -----------------------------------------

    private void DivideMeasureCommand(bool divide)
    {
        var name = divide ? "DIVIDE" : "MEASURE";
        StartWizard(new Wizard
        {
            Name = name,
            Steps = new[]
            {
                new WizStep { Kind = WizKind.Entity, Prompt = "Click the object to " + (divide ? "divide:" : "measure:"), Accept = e => e is LineEntity or CircleEntity or ArcEntity or PolylineEntity },
                new WizStep { Kind = WizKind.Number, Default = divide ? 5.0 : 100.0, Prompt = divide ? "Enter the number of segments" : "Specify the segment length" }
            },
            Commit = x =>
            {
                var path = PathPoints(x.E(0));
                if (path.Count < 2) return false;
                var lengths = new List<double> { 0 };
                for (var i = 1; i < path.Count; i++) lengths.Add(lengths[^1] + Point2.Distance(path[i - 1], path[i]));
                var total = lengths[^1]; if (total < 1e-9) return false;
                var count = divide ? (int)Math.Round(x.D(1)) : (int)Math.Floor(total / Math.Max(x.D(1), 1e-9));
                var step = divide ? total / count : x.D(1);
                if (divide ? count < 2 || count > 2000 : count < 1 || count > 2000) { AppendCommandHistory($"{name}: invalid value (2..2000 segments)."); return false; }
                var tick = Math.Min(step, total) * 0.15;
                var items = new List<CadEntity>();
                for (var k = 1; divide ? k < count : k <= count; k++)
                {
                    var d = k * step; if (d > total + 1e-9) break;
                    var seg = Math.Max(1, lengths.FindIndex(len => len >= d - 1e-12));
                    var a = path[seg - 1]; var b = path[seg]; var segLen = lengths[seg] - lengths[seg - 1]; if (segLen < 1e-12) continue;
                    var t = (d - lengths[seg - 1]) / segLen;
                    var at = new Point2(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
                    double nx = -(b.Y - a.Y) / segLen * tick / 2, ny = (b.X - a.X) / segLen * tick / 2;
                    items.Add(new LineEntity(new Point2(at.X - nx, at.Y - ny), new Point2(at.X + nx, at.Y + ny), Guid.NewGuid(), _editor.Drawing.CurrentLayer));
                }
                if (items.Count == 0) { AppendCommandHistory($"{name}: nothing to place."); return false; }
                AddNewEntities(name, items, $"{items.Count} tick mark(s) placed (short LINEs, no POINT objects).");
                return true;
            }
        });
    }
}
