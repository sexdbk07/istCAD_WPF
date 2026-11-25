using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>ARC and CIRCLE with the AutoCAD prompts and options, and FILLET / CHAMFER of every corner of a polyline.</summary>
public partial class MainWindow
{
    private double _lastCircleRadius;

    // ---- ARC: start, second, end (default) or [Center]: center, start, end --------------------

    private void ArcCommand()
    {
        EndLineCommand();
        ArcEntity? ThreePoint(Point2 start, Point2 second, Point2 end)
        {
            if (CircleThrough(start, second, end) is not { } circle) return null;
            double Angle(Point2 p) => Math.Atan2(p.Y - circle.Center.Y, p.X - circle.Center.X) * 180 / Math.PI;
            // The arc runs from start through second to end; ARC entities are stored counter-clockwise.
            var counterClockwise = (second.X - start.X) * (end.Y - start.Y) - (second.Y - start.Y) * (end.X - start.X) > 0;
            return new ArcEntity(circle.Center, circle.Radius, counterClockwise ? Angle(start) : Angle(end), counterClockwise ? Angle(end) : Angle(start), Guid.NewGuid(), _editor.Drawing.CurrentLayer);
        }
        ArcEntity? CenterStartEnd(Point2 center, Point2 start, Point2 end)
        {
            var radius = Point2.Distance(center, start);
            if (radius < 1e-9 || Point2.Distance(center, end) < 1e-9) return null;
            return new ArcEntity(center, radius, Math.Atan2(start.Y - center.Y, start.X - center.X) * 180 / Math.PI,
                Math.Atan2(end.Y - center.Y, end.X - center.X) * 180 / Math.PI, Guid.NewGuid(), _editor.Drawing.CurrentLayer);
        }
        List<CadEntity>? Build(Wizard x)
        {
            if (x.Flag)
            {
                if (!x.Has(3) || !x.Has(4)) return null;
                if (!x.Has(5)) return new List<CadEntity> { new LineEntity(x.P(3), x.P(4), Guid.NewGuid(), _editor.Drawing.CurrentLayer) }; // radius rubber band
                return CenterStartEnd(x.P(3), x.P(4), x.P(5)) is { } arc ? new List<CadEntity> { arc } : null;
            }
            if (!x.Has(0) || !x.Has(1)) return null;
            if (!x.Has(2)) return new List<CadEntity> { new LineEntity(x.P(0), x.P(1), Guid.NewGuid(), _editor.Drawing.CurrentLayer) };
            return ThreePoint(x.P(0), x.P(1), x.P(2)) is { } threePoint ? new List<CadEntity> { threePoint } : null;
        }
        StartWizard(new Wizard
        {
            Name = "ARC",
            Steps = new[]
            {
                new WizStep
                {
                    Kind = WizKind.Point, Prompt = "Specify start point of arc or [Center]:",
                    Keyword = (x, text) => { if (text is not ("C" or "CENTER")) return false; x.Flag = true; WizardGoto(3); return true; }
                },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify second point of arc:" },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify end point of arc:", Next = (_, _) => 99 },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify center point of arc:" },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify start point of arc:" },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify end point of arc (drawn counter-clockwise from the start):" }
            },
            Preview = Build,
            Commit = x =>
            {
                var arc = x.Flag ? CenterStartEnd(x.P(3), x.P(4), x.P(5)) : ThreePoint(x.P(0), x.P(1), x.P(2));
                if (arc is null) { AppendCommandHistory(x.Flag ? "ARC: the start and end points must differ from the center." : "ARC: the three points are on one line."); return false; }
                AddNewEntities("ARC", new List<CadEntity> { arc }, "arc created.");
                return true;
            }
        });
    }

    // ---- CIRCLE: center + radius [Diameter], [2P], [3P] ----------------------------------------

    private void CircleCommand()
    {
        EndLineCommand();
        // x.Data: null = center/radius, "D" = center/diameter, "2P", "3P".
        CircleEntity? Make(Point2 center, double radius) =>
            radius > 1e-9 && double.IsFinite(radius) ? new CircleEntity(center, radius, Guid.NewGuid(), _editor.Drawing.CurrentLayer) : null;
        CircleEntity? Build(Wizard x)
        {
            switch (x.Data as string)
            {
                case "2P":
                    if (!x.Has(3) || !x.Has(4)) return null;
                    return Make(new Point2((x.P(3).X + x.P(4).X) / 2, (x.P(3).Y + x.P(4).Y) / 2), Point2.Distance(x.P(3), x.P(4)) / 2);
                case "3P":
                    if (!x.Has(5) || !x.Has(6) || !x.Has(7)) return null;
                    return CircleThrough(x.P(5), x.P(6), x.P(7)) is { } c ? Make(c.Center, c.Radius) : null;
                case "D":
                    return x.Has(0) && x.Has(2) ? Make(x.P(0), x.D(2) / 2) : null;
                case "TTR":
                    if (!x.Has(8) || !x.Has(9) || !x.Has(10) || x.Clicks[8] is not { } pick1 || x.Clicks[9] is not { } pick2) return null;
                    return TangentTangentRadiusCircle(x.E(8), pick1, x.E(9), pick2, x.D(10));
                default:
                    return x.Has(0) && x.Has(1) ? Make(x.P(0), x.D(1)) : null;
            }
        }
        List<CadEntity>? Show(Wizard x)
        {
            if (x.Index == 0) return null; // no circle before its center is picked
            if (Build(x) is { } circle) return new List<CadEntity> { circle };
            // 3P with two points so far: show the chord.
            if ((x.Data as string) == "3P" && x.Has(5) && x.Has(6)) return new List<CadEntity> { new LineEntity(x.P(5), x.P(6), Guid.NewGuid(), _editor.Drawing.CurrentLayer) };
            return null;
        }
        var wizard = new Wizard
        {
            Name = "CIRCLE",
            Steps = new[]
            {
                new WizStep
                {
                    Kind = WizKind.Point, Prompt = "Specify center point for circle or [3P/2P/Ttr (tan tan radius)]:",
                    Keyword = (x, text) =>
                    {
                        if (text == "2P") { x.Data = "2P"; WizardGoto(3); return true; }
                        if (text == "3P") { x.Data = "3P"; WizardGoto(5); return true; }
                        if (text is "T" or "TTR") { x.Data = "TTR"; WizardGoto(8); return true; }
                        return false;
                    }
                },
                new WizStep
                {
                    Kind = WizKind.Dist, Ref = 0, Prompt = "Specify radius of circle or [Diameter]", Default = _lastCircleRadius > 0 ? _lastCircleRadius : null, Next = (_, _) => 99,
                    Keyword = (x, text) => { if (text is not ("D" or "DIAMETER")) return false; x.Data = "D"; WizardGoto(2); return true; }
                },
                new WizStep { Kind = WizKind.Dist, Ref = 0, Prompt = "Specify diameter of circle", Default = _lastCircleRadius > 0 ? _lastCircleRadius * 2 : null, Next = (_, _) => 99 },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify first end point of circle's diameter:" },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify second end point of circle's diameter:", Next = (_, _) => 99 },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify first point on circle:" },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify second point on circle:" },
                new WizStep { Kind = WizKind.Point, Prompt = "Specify third point on circle:", Next = (_, _) => 99 },
                new WizStep { Kind = WizKind.Entity, Prompt = "Specify point on object for first tangent of circle:", Accept = e => e is LineEntity or ArcEntity or CircleEntity or PolylineEntity },
                new WizStep { Kind = WizKind.Entity, Prompt = "Specify point on object for second tangent of circle:", Accept = e => e is LineEntity or ArcEntity or CircleEntity or PolylineEntity },
                new WizStep { Kind = WizKind.Number, Prompt = "Specify radius of circle", Default = _lastCircleRadius > 0 ? _lastCircleRadius : null }
            },
            Preview = Show,
            Commit = x =>
            {
                if (Build(x) is not { } circle)
                {
                    AppendCommandHistory((x.Data as string) switch { "3P" => "CIRCLE: the three points are on one line.", "TTR" => "CIRCLE: no circle of that radius is tangent to both objects.", _ => "CIRCLE: the radius must be positive." });
                    return false;
                }
                _lastCircleRadius = circle.Radius;
                AddNewEntities("CIRCLE", new List<CadEntity> { circle }, $"circle created, radius {circle.Radius.ToString("0.####", CultureInfo.CurrentCulture)}.");
                return true;
            }
        };
        StartWizard(wizard);
        // The remembered radius is this session's last circle (as in AutoCAD), not an older typed value.
        if (_lastCircleRadius > 0) { wizard.Values[1] = _lastCircleRadius; wizard.Values[2] = _lastCircleRadius * 2; wizard.Values[10] = _lastCircleRadius; }
    }

    // ---- FILLET / CHAMFER: every corner of one polyline (AutoCAD's Polyline option) -----------

    /// <summary>Fillets (radius) or chamfers (distance) all corners between straight segments of a polyline; null when nothing changes.</summary>
    private static PolylineEntity? CornerPolyline(PolylineEntity pl, double value, bool fillet, out int corners)
    {
        corners = 0;
        var v = pl.Vertices.ToList(); var n = v.Count;
        if (n < 3 || value <= 1e-12) return null;
        // cut[i] = length taken from both segments that meet at vertex i (0 = corner left alone).
        var cut = new double[n]; var turn = new double[n];
        bool Straight(int index) => Math.Abs(v[index].Bulge) < 1e-12;
        int Prev(int i) => (i - 1 + n) % n; int Next(int i) => (i + 1) % n;
        for (var i = 0; i < n; i++)
        {
            if (!pl.Closed && (i == 0 || i == n - 1)) continue;
            if (!Straight(Prev(i)) || !Straight(i)) continue;
            Point2 a = v[Prev(i)].Point, b = v[i].Point, c = v[Next(i)].Point;
            double ux = b.X - a.X, uy = b.Y - a.Y, wx = c.X - b.X, wy = c.Y - b.Y;
            double lu = Math.Sqrt(ux * ux + uy * uy), lw = Math.Sqrt(wx * wx + wy * wy);
            if (lu < 1e-12 || lw < 1e-12) continue;
            var cross = (ux * wy - uy * wx) / (lu * lw); var dot = (ux * wx + uy * wy) / (lu * lw);
            var phi = Math.Atan2(cross, dot); // signed turn angle: + = left turn
            if (Math.Abs(phi) < 1e-9 || Math.Abs(Math.Abs(phi) - Math.PI) < 1e-9) continue;
            turn[i] = phi;
            cut[i] = fillet ? value * Math.Tan(Math.Abs(phi) / 2) : value;
        }
        // A segment must be long enough for the cuts at both of its ends; drop the corners that do not fit.
        for (var pass = 0; pass < 2; pass++)
            for (var i = 0; i < n; i++)
            {
                if (!pl.Closed && i == n - 1) continue;
                var j = Next(i); var length = Point2.Distance(v[i].Point, v[j].Point);
                if (cut[i] + cut[j] > length + 1e-9) { if (cut[i] >= cut[j]) cut[i] = 0; else cut[j] = 0; }
            }
        var result = new List<PolylineVertex>(n * 2);
        for (var i = 0; i < n; i++)
        {
            if (cut[i] <= 0) { result.Add(v[i]); continue; }
            Point2 a = v[Prev(i)].Point, b = v[i].Point, c = v[Next(i)].Point;
            double lu = Point2.Distance(a, b), lw = Point2.Distance(b, c);
            var entry = new Point2(b.X - (b.X - a.X) / lu * cut[i], b.Y - (b.Y - a.Y) / lu * cut[i]);
            var exit = new Point2(b.X + (c.X - b.X) / lw * cut[i], b.Y + (c.Y - b.Y) / lw * cut[i]);
            // The fillet arc turns by the same angle as the corner: bulge = tan(turn / 4), left turn = counter-clockwise.
            result.Add(new PolylineVertex(entry, fillet ? Math.Tan(turn[i] / 4) : 0));
            result.Add(new PolylineVertex(exit, v[i].Bulge));
            corners++;
        }
        return corners == 0 ? null : pl with { Vertices = result };
    }

    /// <summary>FILLET / CHAMFER picked on a single polyline: applies the current radius / distance to all of its corners.</summary>
    private bool TryCornerPolyline(PolylineEntity pl)
    {
        var name = _cornerFillet ? "FILLET" : "CHAMFER";
        var value = CornerValue;
        if (value <= 1e-12) { AppendCommandHistory($"{name}: set a {CornerLabel.ToLowerInvariant()} greater than 0 first (type {(_cornerFillet ? "R" : "D")}), then pick the polyline."); return false; }
        var changed = CornerPolyline(pl, value, _cornerFillet, out var corners);
        if (changed is null) { AppendCommandHistory($"{name}: no corner of this polyline fits a {CornerLabel.ToLowerInvariant()} of {Fmt(value)}."); return false; }
        _editor.ApplyEntityChanges(new CadEntity[] { pl }, new CadEntity[] { changed });
        _selectedEntityIds.Clear(); _selectedEntityIds.Add(changed.Id);
        _cornerPicks.Clear();
        RefreshPropertiesPalette(); RenderSelectionOverlay(); RenderDrawing(false);
        AppendCommandHistory($"{name}: {corners} corner(s) of the polyline {(_cornerFillet ? "filleted" : "chamfered")} with {CornerLabel.ToLowerInvariant()} {Fmt(value)}.");
        DrawingCanvas.Focus();
        return true;
    }
}
