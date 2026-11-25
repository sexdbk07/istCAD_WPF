using System.Globalization;
using System.Text;
using iStCAD_1.Core.Geometry;
using iStCAD_1.Core.Model;

namespace iStCAD_1;

/// <summary>OVERKILL (remove redundant/invalid objects). A single undoable edit. VNFIX lives in MainWindow.Vnfix.cs.</summary>
public partial class MainWindow
{
    // ============================ OVERKILL ============================

    private static bool FinitePoint(Point2 p) => double.IsFinite(p.X) && double.IsFinite(p.Y);

    private static string DuplicateStyle(CadEntity e) =>
        e.Layer + "|" + (e.ExplicitColor is { } c ? $"{c.R},{c.G},{c.B}" : "-") + "|" +
        (e.LineTypePattern is { Count: > 0 } pattern ? string.Join(",", pattern.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) : "-");

    private static string? DuplicateKey(CadEntity e, double tol)
    {
        long Q(double v) => (long)Math.Round(v / tol);
        long A(double v) => (long)Math.Round(v * 1e6);
        switch (e)
        {
            case LineEntity l:
            {
                var a = (Q(l.Start.X), Q(l.Start.Y)); var b = (Q(l.End.X), Q(l.End.Y));
                if (a.CompareTo(b) > 0) (a, b) = (b, a);
                return $"L|{DuplicateStyle(e)}|{a}|{b}";
            }
            case CircleEntity c: return $"C|{DuplicateStyle(e)}|{Q(c.Center.X)}|{Q(c.Center.Y)}|{Q(c.Radius)}";
            case ArcEntity a: return $"A|{DuplicateStyle(e)}|{Q(a.Center.X)}|{Q(a.Center.Y)}|{Q(a.Radius)}|{A(a.StartAngleDegrees)}|{A(a.EndAngleDegrees)}";
            case PolylineEntity p:
            {
                var forward = string.Join(";", p.Vertices.Select(v => $"{Q(v.Point.X)},{Q(v.Point.Y)},{Math.Round(v.Bulge, 6)}"));
                var key = forward;
                if (p.Vertices.All(v => v.Bulge == 0))
                {
                    var reverse = string.Join(";", p.Vertices.Reverse().Select(v => $"{Q(v.Point.X)},{Q(v.Point.Y)},0"));
                    if (string.CompareOrdinal(reverse, forward) < 0) key = reverse;
                }
                return $"P|{DuplicateStyle(e)}|{p.Closed}|{key}";
            }
            case CadTextEntity t: return $"T|{DuplicateStyle(e)}|{t.Value}|{Q(t.Position.X)}|{Q(t.Position.Y)}|{Math.Round(t.Height, 6)}|{Math.Round(t.RotationRadians, 6)}";
            case CadMTextEntity m: return $"M|{DuplicateStyle(e)}|{m.Value}|{Q(m.Position.X)}|{Q(m.Position.Y)}|{Math.Round(m.Height, 6)}|{Math.Round(m.RotationRadians, 6)}";
        }
        return null;
    }

    private void OverkillCommand(string[] args)
    {
        var dryRun = args.Any(a => a is "CHECK" or "SCAN");
        var all = _editor.Drawing.Entities;
        if (all.Count == 0) { AppendCommandHistory("OVERKILL: the drawing is empty."); return; }
        var scope = _selectedEntityIds.Count > 0 ? all.Where(e => _selectedEntityIds.Contains(e.Id)).ToList() : all.ToList();
        var diagonal = 0.0;
        if (_spatialIndex.TryGetExtents(out var extents))
            diagonal = Math.Sqrt(Math.Pow(extents.MaxX - extents.MinX, 2) + Math.Pow(extents.MaxY - extents.MinY, 2));
        var tol = Math.Clamp(diagonal * 1e-9, 1e-6, 1e-3);

        // id -> entity as it will be after cleanup (null = removed)
        var final = new Dictionary<Guid, CadEntity?>(scope.Count);
        int invalid = 0, duplicates = 0, merged = 0, vertexFixes = 0;
        foreach (var e in scope) final[e.Id] = e;

        // 1) Invalid / degenerate objects, and consecutive duplicate polyline vertices.
        foreach (var e in scope)
        {
            var bad = false;
            switch (e)
            {
                case LineEntity l: bad = !FinitePoint(l.Start) || !FinitePoint(l.End) || Math.Abs(l.Start.X - l.End.X) + Math.Abs(l.Start.Y - l.End.Y) <= tol; break;
                case CircleEntity c: bad = !FinitePoint(c.Center) || !double.IsFinite(c.Radius) || c.Radius <= tol; break;
                case ArcEntity a:
                    bad = !FinitePoint(a.Center) || !double.IsFinite(a.Radius) || a.Radius <= tol ||
                          !double.IsFinite(a.StartAngleDegrees) || !double.IsFinite(a.EndAngleDegrees); break;
                case PolylineEntity p:
                {
                    if (p.Vertices.Count < 2 || p.Vertices.Any(v => !FinitePoint(v.Point) || !double.IsFinite(v.Bulge))) { bad = true; break; }
                    var kept = new List<PolylineVertex>(p.Vertices.Count) { p.Vertices[0] };
                    for (var i = 1; i < p.Vertices.Count; i++)
                    {
                        var last = kept[^1].Point; var cur = p.Vertices[i];
                        if (Math.Abs(cur.Point.X - last.X) + Math.Abs(cur.Point.Y - last.Y) <= tol) continue;
                        kept.Add(cur);
                    }
                    if (p.Closed && kept.Count > 1 && Math.Abs(kept[0].Point.X - kept[^1].Point.X) + Math.Abs(kept[0].Point.Y - kept[^1].Point.Y) <= tol) kept.RemoveAt(kept.Count - 1);
                    if (kept.Count < 2) { bad = true; break; }
                    if (kept.Count < p.Vertices.Count) { final[e.Id] = p with { Vertices = kept }; vertexFixes++; }
                    break;
                }
                case CadTextEntity t: bad = string.IsNullOrWhiteSpace(t.Value) || !FinitePoint(t.Position) || !double.IsFinite(t.Height); break;
                case CadMTextEntity m: bad = string.IsNullOrWhiteSpace(m.Value) || !FinitePoint(m.Position) || !double.IsFinite(m.Height); break;
                case ProxyCadEntity proxy: bad = proxy.HasBounds && (!FinitePoint(proxy.BoundsMin) || !FinitePoint(proxy.BoundsMax)); break;
            }
            if (bad) { final[e.Id] = null; invalid++; }
        }

        // 2) Exact duplicates (same type, style and geometry within tolerance).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in scope)
        {
            if (final[e.Id] is not { } current) continue;
            var key = DuplicateKey(current, tol);
            if (key is null) continue;
            if (!seen.Add(key)) { final[e.Id] = null; duplicates++; }
        }

        // 3) Collinear lines that overlap or touch end-to-end merge into one line.
        var lineGroups = new Dictionary<string, List<(double T0, double T1, LineEntity Line)>>(StringComparer.Ordinal);
        foreach (var e in scope)
        {
            if (final[e.Id] is not LineEntity l) continue;
            double dx = l.End.X - l.Start.X, dy = l.End.Y - l.Start.Y, length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= tol) continue;
            double ux = dx / length, uy = dy / length;
            if (ux < 0 || (ux == 0 && uy < 0)) { ux = -ux; uy = -uy; }
            var angleKey = Math.Round(Math.Atan2(uy, ux) / 1e-9);
            var offsetKey = Math.Round((ux * l.Start.Y - uy * l.Start.X) / tol);
            var t0 = ux * l.Start.X + uy * l.Start.Y; var t1 = ux * l.End.X + uy * l.End.Y;
            var groupKey = $"{DuplicateStyle(l)}|{angleKey}|{offsetKey}";
            if (!lineGroups.TryGetValue(groupKey, out var list)) lineGroups[groupKey] = list = new();
            list.Add((Math.Min(t0, t1), Math.Max(t0, t1), l));
        }
        foreach (var group in lineGroups.Values)
        {
            if (group.Count < 2) continue;
            group.Sort((x, y) => x.T0.CompareTo(y.T0));
            var members = new List<LineEntity> { group[0].Line };
            double start = group[0].T0, end = group[0].T1;
            void Flush()
            {
                if (members.Count > 1)
                {
                    var first = members[0];
                    double dx = first.End.X - first.Start.X, dy = first.End.Y - first.Start.Y, length = Math.Sqrt(dx * dx + dy * dy);
                    double ux = dx / length, uy = dy / length;
                    if (ux < 0 || (ux == 0 && uy < 0)) { ux = -ux; uy = -uy; }
                    var sT = ux * first.Start.X + uy * first.Start.Y;
                    double fx = first.Start.X - sT * ux, fy = first.Start.Y - sT * uy; // foot of the perpendicular from the origin
                    final[first.Id] = first with { Start = new Point2(fx + ux * start, fy + uy * start), End = new Point2(fx + ux * end, fy + uy * end) };
                    for (var i = 1; i < members.Count; i++) final[members[i].Id] = null;
                    merged += members.Count - 1;
                }
            }
            for (var i = 1; i < group.Count; i++)
            {
                if (group[i].T0 <= end + tol) { end = Math.Max(end, group[i].T1); members.Add(group[i].Line); }
                else { Flush(); members = new List<LineEntity> { group[i].Line }; start = group[i].T0; end = group[i].T1; }
            }
            Flush();
        }

        var remove = new List<CadEntity>(); var add = new List<CadEntity>();
        foreach (var original in scope)
        {
            var result = final[original.Id];
            if (result is null) remove.Add(original);
            else if (!ReferenceEquals(result, original)) { remove.Add(original); add.Add(result); }
        }

        // 4) Far-away stray objects are reported and selected, never deleted automatically (they may be intentional).
        var strays = new List<Guid>();
        if (_spatialIndex.TryGetRobustExtents(out var robust, out var outlierCount) && outlierCount > 0)
            foreach (var e in all)
                if (!(final.TryGetValue(e.Id, out var f) && f is null) && _spatialIndex.TryGetBounds(e.Id, out var b) &&
                    (b.MaxX < robust.MinX || b.MinX > robust.MaxX || b.MaxY < robust.MinY || b.MinY > robust.MaxY)) strays.Add(e.Id);

        var summary = $"{invalid:N0} invalid/empty, {duplicates:N0} duplicate, {merged:N0} overlapping line(s) merged, {vertexFixes:N0} polyline(s) with repeated vertices fixed";
        if (remove.Count == 0 && add.Count == 0) AppendCommandHistory($"OVERKILL: nothing to clean ({scope.Count:N0} object(s) checked, tolerance {tol:0.#########}).");
        else if (dryRun) AppendCommandHistory($"OVERKILL (check only): would clean {summary}. Run OVERKILL to apply.");
        else
        {
            _editor.ApplyEntityChanges(remove, add);
            _selectedEntityIds.Clear();
            AppendCommandHistory($"OVERKILL: cleaned {summary}; {remove.Count - add.Count:N0} object(s) removed in total ({scope.Count:N0} checked). Undo with U.");
        }
        if (strays.Count > 0)
        {
            _selectedEntityIds.Clear(); foreach (var id in strays) _selectedEntityIds.Add(id);
            RefreshPropertiesPalette(); RenderSelectionOverlay();
            AppendCommandHistory($"OVERKILL: {strays.Count:N0} object(s) lie far outside the main drawing and are now selected. Press Delete to erase them, or Esc to keep them.");
        }
    }
}
